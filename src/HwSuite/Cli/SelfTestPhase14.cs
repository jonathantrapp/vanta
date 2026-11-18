using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;
using HwSuite.Ui;

namespace HwSuite.Cli;

/// <summary>
/// 1.14 checks: the lighting edit session (Revert / Keep), colours applied from an image, the image palette, and the
/// remembered window state. Pure functions and in-memory configurations; image files are written only to the isolated
/// test folder (or %TEMP%) and deleted. Nothing here opens a device.
/// </summary>
public static partial class SelfTest
{
    static void Phase14Tests()
    {
        Guarded("1.14 revert", RevertTests);
        Guarded("1.14 colours from image", ApplyColourTests);
        Guarded("1.14 palette", PaletteTests);
        Guarded("1.14 palette files", PaletteFileTests);
        Guarded("1.14 window state", WindowStateTests);
        Guarded("1.14.1 fan stalls", FanSignalTests);
        Guarded("1.14.2 fan swap settings", FanSwapTests);
    }

    /// <summary>1.14.2: the settings change for the six-fan swap (Core\FanSwap.cs), on an in-memory copy of the owner's layout.</summary>
    static void FanSwapTests()
    {
        var cfg = new AppConfig { FanControlEnabled = true };
        ChannelConfig Header(string id, string label, ChannelMode mode, double? min, double? start) => new()
        {
            Id = id, Label = label, Kind = FanKind.CaseFan, Mode = mode, Curve = FanPresets.CurveFor("Balanced", FanKind.CaseFan),
            VerifiedMinPercent = min, MinEvidence = min.HasValue ? "owner watched it at " + min + "%" : null,
            MeasuredStartPercent = start, StartEvidence = start.HasValue ? "phase10 start test" : null,
            TachSeen = true, TachMapped = true, TachEvidence = "Phase 9 fan-step"
        };
        var aio = new ChannelConfig { Id = ChannelDefaults.CpuFanChannelId, Label = ChannelDefaults.AioLabel, Kind = FanKind.Aio, Mode = ChannelMode.Curve, Curve = FanPresets.CurveFor("Balanced", FanKind.Aio), TachSeen = true, TachMapped = true };
        cfg.Channels.Add(aio);
        cfg.Channels.Add(Header(FanSwap.Rear, "Rear exhaust", ChannelMode.Curve, 50, 40));
        cfg.Channels.Add(Header(FanSwap.Front, "Front intake—SYS_FAN2 (upper)", ChannelMode.Curve, 35, 25));
        cfg.Channels.Add(Header(FanSwap.Spare, "Front intake—SYS_FAN3 (lower)", ChannelMode.Curve, 50, null));
        cfg.Channels.Add(new ChannelConfig { Id = "gpu:0", Kind = FanKind.GpuFan, Mode = ChannelMode.Curve, Curve = FanPresets.CurveFor("Balanced", FanKind.GpuFan) });
        cfg.Rgb.DLedCount = 27;
        string aioBefore = ConfigStore.ToJson(aio), gpuBefore = ConfigStore.ToJson(cfg.Channels[4]), rgbBefore = ConfigStore.ToJson(cfg.Rgb);
        var curves = cfg.Channels.ToDictionary(c => c.Id, c => ConfigStore.ToJson(c.Curve));

        var changes = FanSwap.Prepare(cfg);
        var rear = cfg.Channels[1]; var front = cfg.Channels[2]; var spare = cfg.Channels[3];
        Check("fan swap: fan control goes off", !cfg.FanControlEnabled && changes.Any(c => c.StartsWith("fan control")));
        Check("fan swap: the old minimums and restart measurements are cleared on SYS_FAN1-3 and listed",
            new[] { rear, front, spare }.All(c => c.VerifiedMinPercent == null && c.MinEvidence == null && c.MeasuredStartPercent == null && c.StartEvidence == null && !c.TachSeen)
            && changes.Count(c => c.Contains("needs re-testing")) == 5 && changes.Any(c => c.Contains("35%")), string.Join(" | ", changes));
        Check("fan swap: the header-to-speed-signal mapping and every curve are kept",
            new[] { rear, front, spare }.All(c => c.TachMapped && c.TachEvidence == "Phase 9 fan-step") && cfg.Channels.All(c => ConfigStore.ToJson(c.Curve) == curves[c.Id]));
        Check("fan swap: SYS_FAN3 goes to firmware, SYS_FAN1/2 keep their curve mode, labels name the new fans",
            spare.Mode == ChannelMode.Auto && rear.Mode == ChannelMode.Curve && front.Mode == ChannelMode.Curve
            && front.Label.Contains("Thermalright") && rear.Label.Contains("SYS_FAN1") && spare.Label.Contains("spare"));
        Check("fan swap: the AIO, GPU and lighting settings are untouched",
            ConfigStore.ToJson(aio) == aioBefore && ConfigStore.ToJson(cfg.Channels[4]) == gpuBefore && ConfigStore.ToJson(cfg.Rgb) == rgbBefore);
        var fake = new FakeChannel(FanSwap.Front, FanKind.CaseFan);
        Check("fan swap: VANTA will not drive the new case fans until a minimum is confirmed again",
            !FanSafety.Assess(rear, fake).Controllable && !FanSafety.Assess(front, fake).Controllable && FanSafety.Assess(aio, new FakeChannel(aio.Id, FanKind.Aio)).Controllable);
        var again = ConfigStore.Parse(ConfigStore.ToJson(cfg))!;
        Check("fan swap: saved and read back it is already prepared (a second run changes nothing)", FanSwap.Prepare(again).Count == 0 && FanSwap.Prepare(cfg).Count == 0);
        Check("fan swap: backup names sort by time", string.CompareOrdinal(FanSwap.BackupName(new DateTime(2026, 10, 21, 9, 5, 0)), FanSwap.BackupName(new DateTime(2026, 10, 21, 10, 0, 0))) < 0);
        var empty = new AppConfig { FanControlEnabled = false };
        Check("fan swap: a settings file without these headers only reports them", FanSwap.Prepare(empty).All(c => c.Contains("not in the settings")) && empty.Channels.Count == 0);
    }

    /// <summary>
    /// 1.14.1: a fan that sticks below some duty and runs again at 100% (SYS_FAN2, 2026-10-18) must not be surged to
    /// 100% every few seconds forever, and a few RPM is not turning; a real stall and a one-off blip keep the old protection.
    /// </summary>
    static void FanSignalTests()
    {
        DateTime clock = new(2026, 10, 18, 23, 0, 0, DateTimeKind.Utc);
        (FanEngine e, FakeChannel fan, List<string> alerts) Rig()
        {
            var cfg = new AppConfig { FanControlEnabled = true };
            var e = new FanEngine(cfg) { UtcNow = () => clock };
            var fan = new FakeChannel("lhm:/lpc/it8689e/0/control/2", FanKind.CaseFan) { Rpm = 900 };
            e.Channels.Add(fan);
            var cc = Cfg(fan, FanKind.CaseFan, ChannelMode.Curve, min: 35);
            cc.TachMapped = true; cc.TachSeen = true;
            cc.Curve.Points = new() { new CurvePoint(20, 57), new CurvePoint(100, 57) };
            cfg.Channels.Add(cc);
            var alerts = new List<string>();
            e.Alert += alerts.Add;
            return (e, fan, alerts);
        }
        // the stall model: below 60% duty the fan stops (reads 0); at or above 60% it turns
        void Run(FanEngine e, FakeChannel f, int seconds, Func<double, double?> rpm, List<double>? duties = null)
        {
            for (int i = 0; i < seconds; i++)
            {
                f.Rpm = f.Applied < 0 ? 600 : rpm(f.Applied);
                clock = clock.AddSeconds(1); e.Tick(Snap(45, 45), 1);
                duties?.Add(f.Applied);
            }
        }
        double? Flaky(double duty) => duty < 60 ? 0 : 900;

        var (e1, f1, al1) = Rig();
        using (e1)
        {
            Run(e1, f1, 10, _ => 900);
            Check("stall test rig: the fan follows its 57% curve while it turns", Math.Abs(f1.Applied - 57) < 0.5 && e1.IsControlled(f1.Id), $"(applied {f1.Applied})");
            var duties = new List<double>();
            Run(e1, f1, 240, Flaky, duties);
            int surges = duties.Zip(duties.Skip(1)).Count(p => p.First < 100 && p.Second == 100);
            var st = e1.Status[f1.Id];
            Check($"a fan that stops below 60% and runs at 100% stops surging after {FanEngine.FlapLimit} surges: its minimum is raised to 75% and VANTA keeps driving it",
                surges == FanEngine.FlapLimit && e1.IsControlled(f1.Id) && !f1.Released && Math.Abs(f1.Applied - 75) < 0.5 && !st.FeedbackLost && st.Warning?.Contains("75%") == true && st.Warning.Contains("57"),
                $"(surges {surges}; applied {f1.Applied}; warning: {st.Warning})");
            Check("...with one alert that names the duty and suggests checking the fan", al1.Count(a => a.Contains("kept stopping")) == 1 && al1.Any(a => a.Contains("57") && a.Contains("75%") && a.Contains("obstruction")), string.Join(" | ", al1));
            Run(e1, f1, 600, Flaky);
            Check("...and it stays at the raised minimum without further surges or alerts", Math.Abs(f1.Applied - 75) < 0.5 && al1.Count(a => a.Contains("kept stopping")) == 1 && !e1.Status[f1.Id].FeedbackLost);
            Run(e1, f1, 240, d => d < 80 ? 0 : 900);
            Check("if it then stops at the raised minimum too, the minimum rises again (never lowered)", f1.Applied >= 85 && e1.IsControlled(f1.Id) && al1.Count(a => a.Contains("kept stopping")) == 2, $"(applied {f1.Applied})");
        }

        // the case tonight: a stuck fan reading a few RPM is stopped, not turning
        var (e4, f4, al4) = Rig();
        using (e4)
        {
            Run(e4, f4, 10, _ => 900);
            Run(e4, f4, 8, _ => 23);
            Check($"a driven fan reading 23 rpm (under {FanEngine.MinTurningRpm:0}) counts as stopped: 100% hold and alert", f4.Applied == 100 && e4.Status[f4.Id].FeedbackLost && al4.Any(a => a.Contains("speed signal lost")));
            Check("the 9 926 rpm glitch and 0 are still not turning; 650 rpm is", !FanEngine.Turning(23) && !FanEngine.Turning(0) && !FanEngine.Turning(9926) && FanEngine.Turning(650) && FanEngine.Spinning(23));
        }

        // a real stall: the signal never comes back, so the 100% hold stays (protection unchanged), never handed back
        var (e2, f2, al2) = Rig();
        using (e2)
        {
            Run(e2, f2, 10, _ => 900);
            Run(e2, f2, 300, _ => 0);
            Check("a fan that really stops keeps the 100% hold and its alert (no raised minimum)",
                f2.Applied == 100 && e2.Status[f2.Id].FeedbackLost && e2.IsControlled(f2.Id) && al2.Count(a => a.Contains("speed signal lost")) == 1 && !al2.Any(a => a.Contains("kept stopping")) && e2.Status[f2.Id].Warning == null);
        }

        // one blip, or returns that take longer than the quick-return window, keep the existing behaviour
        var (e3, f3, al3) = Rig();
        using (e3)
        {
            Run(e3, f3, 10, _ => 900);
            Run(e3, f3, 4, _ => 0);
            Run(e3, f3, 120, _ => 900);
            Check("a single dropout holds 100%, then the fan returns to its curve under VANTA", e3.IsControlled(f3.Id) && Math.Abs(f3.Applied - 57) < 0.5 && !al3.Any(a => a.Contains("kept stopping")), $"(applied {f3.Applied})");
            for (int k = 0; k < 4; k++) { Run(e3, f3, 4 + (int)FanEngine.QuickReturn.TotalSeconds, _ => 0); Run(e3, f3, 60, _ => 900); }
            Check("dropouts that last longer than the quick-return window are not counted as an unreliable signal", e3.IsControlled(f3.Id) && !al3.Any(a => a.Contains("kept stopping")));
        }
    }

    /// <summary>The configuration as JSON without the lighting appearance fields (to prove nothing else changed).</summary>
    static string WithoutAppearance(AppConfig c, bool keepColours = false)
    {
        var copy = ConfigStore.Parse(ConfigStore.ToJson(c))!;
        var r = copy.Rgb;
        r.Effect = EffectKind.Static; r.LightsOutRestoreEffect = r.LightsOutRestoreEffect == null ? null : EffectKind.Static;
        if (!keepColours) { r.ColorA = 0; r.ColorB = 0; }
        r.Brightness = 0; r.Speed = 0; r.Density = 0; r.Glow = 0; r.Lightning = false;
        return ConfigStore.ToJson(copy);
    }

    static void RevertTests()
    {
        var cfg = UiFixture.LoadConfig(null);
        if (LightsOut.IsOn(cfg.Rgb)) LightsOut.Toggle(cfg);   // the fixture copy was saved during Lights out: start with the lights on
        cfg.Rgb.Enabled = true;
        LightingLooks.Apply(cfg, LightingLooks.Starters.First(s => s.Name == "Window Rain"));
        using var svc = new AppServices(cfg);
        var edits = svc.LightingEdits;
        var start = LightingEditSession.Snapshot(cfg);
        string otherBefore = WithoutAppearance(cfg);
        int changedEvents = 0; edits.Changed += () => changedEvents++;

        Check("1.14: no session until something is edited", !edits.Active && !edits.HasChanges);
        // page-style edit: BeforeEdit, then the change
        edits.BeforeEdit("test: brightness"); cfg.Rgb.Brightness = 0.9;
        Check("1.14: starting a session raises no refresh before the change is applied (regression: it ended itself)", changedEvents == 0 && edits.Active && edits.HasChanges);
        edits.BeforeEdit("test: colour"); cfg.Rgb.ColorA = 0x123456;
        svc.ApplyLook(LightingLooks.Starters.First(s => s.Name == "Distant Thunder"), "tray");
        Check("1.14: later edits (page and tray) keep the original snapshot", edits.Baseline is { } b && LightingEditSession.SameAppearance(CopyLook(b), start));
        // things that are not appearance edits: inclusion, RAM mode, output, Lights out
        cfg.Rgb.Ram = false; cfg.Rgb.RamOnboardEffects = true; cfg.Rgb.Mouse = false;
        Check("1.14: Revert restores effect, colours, brightness, speed, density, glow and lightning", svc.RevertLighting("test") && LightingEditSession.SameAppearance(cfg, start));
        Check("1.14: Revert does not touch device inclusion or the RAM mode", !cfg.Rgb.Ram && cfg.Rgb.RamOnboardEffects && !cfg.Rgb.Mouse);
        cfg.Rgb.Ram = true; cfg.Rgb.RamOnboardEffects = false; cfg.Rgb.Mouse = true;
        Check("1.14: nothing outside the lighting appearance changed (fans, startup, verification, mappings, arrangement)", WithoutAppearance(cfg) == otherBefore);
        Check("1.14: Revert ends the session (the restored look is the new baseline)", !edits.Active && changedEvents == 1);
        Check("1.14: Revert with nothing to revert does nothing", !svc.RevertLighting("test") && LightingEditSession.SameAppearance(cfg, start));

        // back to the snapshot by hand: nothing to revert, the session ends by itself
        edits.BeforeEdit("test"); cfg.Rgb.Speed = 0.9; cfg.Rgb.Speed = start.Speed!.Value;
        Check("1.14: a session back at its snapshot has nothing to revert and ends", !edits.HasChanges && edits.EndIfUnchanged() && !edits.Active);

        // Keep: the current result becomes the baseline
        edits.BeforeEdit("test"); cfg.Rgb.Glow = 0.9;
        svc.KeepLighting("test");
        Check("1.14: Keep ends the session and changes nothing", !edits.Active && Math.Abs(cfg.Rgb.Glow - 0.9) < 1e-9);
        edits.BeforeEdit("test"); cfg.Rgb.Glow = 0.2; svc.RevertLighting("test");
        Check("1.14: after Keep, Revert returns to the kept settings", Math.Abs(cfg.Rgb.Glow - 0.9) < 1e-9);

        // Lights out and output off take precedence
        var dark = LightingEditSession.Snapshot(cfg);
        edits.BeforeEdit("test"); cfg.Rgb.Effect = EffectKind.Canopy; cfg.Rgb.ColorA = 0x445566;
        LightsOut.Toggle(cfg);
        Check("1.14: Lights out is not an edit (the session and its snapshot are unchanged)", edits.Active && edits.Baseline is { } bd && LightingEditSession.SameAppearance(CopyLook(bd), dark));
        var mouseDark = MouseLightingState.From(cfg.Rgb); var kbDark = KeyboardLightingState.From(cfg.Rgb, 3);
        svc.RevertLighting("test");
        Check("1.14: Revert during Lights out keeps everything dark", LightsOut.IsOn(cfg.Rgb) && Equals(MouseLightingState.From(cfg.Rgb), mouseDark) && Equals(KeyboardLightingState.From(cfg.Rgb, 3), kbDark));
        LightsOut.Toggle(cfg);
        Check("1.14: ...and the reverted look is what comes back with the lights", LightingEditSession.SameAppearance(cfg, dark) && !LightsOut.IsOn(cfg.Rgb));
        cfg.Rgb.Enabled = false;
        edits.BeforeEdit("test"); cfg.Rgb.Brightness = 0.15;
        svc.RevertLighting("test");
        Check("1.14: Revert with lighting output off keeps it off", !cfg.Rgb.Enabled && LightingEditSession.SameAppearance(cfg, dark) && MouseLightingState.From(cfg.Rgb) == null);
        Check("1.14: the edit session is never saved to the configuration (memory only)", !ConfigStore.ToJson(cfg).Contains("Snapshot") && !ConfigStore.ToJson(cfg).Contains("revert", StringComparison.OrdinalIgnoreCase));
    }

    static AppConfig CopyLook(LightingLook l) { var c = new AppConfig(); LightingLooks.Apply(c, l); return c; }

    static void ApplyColourTests()
    {
        var cfg = UiFixture.LoadConfig(null);
        if (LightsOut.IsOn(cfg.Rgb)) LightsOut.Toggle(cfg);   // start with the lights on (see RevertTests)
        cfg.Rgb.Enabled = true;
        LightingLooks.Apply(cfg, LightingLooks.Starters.First(s => s.Name == "Distant Thunder"));
        using var svc = new AppServices(cfg);
        var before = LightingEditSession.Snapshot(cfg);
        string rest = WithoutAppearance(cfg, keepColours: false);
        double br = cfg.Rgb.Brightness, sp = cfg.Rgb.Speed, de = cfg.Rgb.Density, gl = cfg.Rgb.Glow; bool li = cfg.Rgb.Lightning; var ef = cfg.Rgb.Effect;
        svc.ApplyColours(0xC0CED6, 0x627884, "test image");
        Check("1.14: applying image colours changes only the colour fields",
            cfg.Rgb.ColorA == 0xC0CED6 && cfg.Rgb.ColorB == 0x627884 && cfg.Rgb.Effect == ef && cfg.Rgb.Brightness == br && cfg.Rgb.Speed == sp && cfg.Rgb.Density == de && cfg.Rgb.Glow == gl && cfg.Rgb.Lightning == li
            && WithoutAppearance(cfg) == rest);
        Check("1.14: image colours join the Revert session", svc.LightingEdits.HasChanges && svc.RevertLighting("test") && LightingEditSession.SameAppearance(cfg, before));
        svc.ApplyColours(null, 0x2A4F4D, "test image");
        Check("1.14: one role can be changed alone", cfg.Rgb.ColorA == before.ColorA && cfg.Rgb.ColorB == 0x2A4F4D);
        svc.RevertLighting("test");
        LightsOut.Toggle(cfg);
        var md = MouseLightingState.From(cfg.Rgb);
        svc.ApplyColours(0xAABBCC, null, "test image");
        Check("1.14: image colours during Lights out keep the lights out", LightsOut.IsOn(cfg.Rgb) && Equals(MouseLightingState.From(cfg.Rgb), md) && cfg.Rgb.ColorA == 0xAABBCC);
        LightsOut.Toggle(cfg);
        cfg.Rgb.Enabled = false;
        svc.ApplyColours(0x112233, null, "test image");
        Check("1.14: image colours with output off keep it off", !cfg.Rgb.Enabled && MouseLightingState.From(cfg.Rgb) == null);
        cfg.Rgb.Enabled = true;
        Check("1.14: the result can be saved as a look through the existing feature", LightingLooks.SaveCurrent(cfg, "From my wallpaper") == null && cfg.Looks.Any(l => l.Name == "From my wallpaper" && l.ColorA == 0x112233));
    }

    static BitmapSource Img(int w, int h, Func<int, int, (byte r, byte g, byte b, byte a)> f)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var (r, g, b, a) = f(x, y); int o = (y * w + x) * 4; px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = a; }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4); bmp.Freeze(); return bmp;
    }

    static double MinPairDistance(IReadOnlyList<PaletteSwatch> s)
    {
        double m = double.MaxValue;
        for (int i = 0; i < s.Count; i++) for (int j = i + 1; j < s.Count; j++) m = Math.Min(m, ImagePalette.Distance(ImagePalette.ToOkLab(s[i].Color), ImagePalette.ToOkLab(s[j].Color)));
        return m;
    }

    static void PaletteTests()
    {
        var forest = UiFixture.RainyForestImage();
        var sw = Stopwatch.StartNew();
        var a = ImagePalette.FromBitmap(forest);
        double ms = sw.Elapsed.TotalMilliseconds;
        var b = ImagePalette.FromBitmap(forest);
        Check("1.14: a varied image gives 4-6 swatches", a.Swatches.Count is >= 4 and <= 6, $"({a.Swatches.Count}: {string.Join(" ", a.Swatches.Select(s => s.Hex))})");
        Check("1.14: palette extraction is deterministic", a.Swatches.Select(s => s.Color).SequenceEqual(b.Swatches.Select(s => s.Color)) && a.SuggestedPrimary == b.SuggestedPrimary && a.SuggestedSecondary == b.SuggestedSecondary);
        Check("1.14: swatches are clearly distinct (no near-duplicates)", MinPairDistance(a.Swatches) >= ImagePalette.SameColour * 0.6, $"(closest pair {MinPairDistance(a.Swatches):0.000} in OKLab)");
        Check("1.14: extraction of a 480×270 image is bounded and quick", ms < 500, $"({ms:0} ms)");
        Check("1.14: the suggested primary is a light highlight and differs from the secondary", ImagePalette.ToOkLab(a.SuggestedPrimary).L > 0.7 && a.SuggestedSecondary is int s2 && s2 != a.SuggestedPrimary);

        // mostly black with a small silver highlight and a teal area: black must not dominate the suggestions
        var nightly = Img(200, 150, (x, y) => x < 12 && y < 12 ? ((byte)200, (byte)212, (byte)222, (byte)255) : y > 120 ? ((byte)34, (byte)86, (byte)84, (byte)255) : ((byte)4, (byte)5, (byte)6, (byte)255));
        var n = ImagePalette.FromBitmap(nightly);
        int darkCount = n.Swatches.Count(s => ImagePalette.ToOkLab(s.Source).L < 0.18);
        Check("1.14: a dark image offers at most one near-black swatch, lifted so LEDs can show it", darkCount <= 1 && n.Swatches.All(s => ImagePalette.ToOkLab(s.Color).L >= ImagePalette.MinLedLightness - 0.01) && n.Swatches.Any(s => s.Lifted), string.Join(" ", n.Swatches.Select(s => s.Hex + (s.Lifted ? "^" : ""))));
        Check("1.14: a small highlight (0.6 % of the pixels) is still offered and suggested as the primary", ImagePalette.ToOkLab(n.SuggestedPrimary).L > 0.8 && n.Swatches.Any(s => s.Color == n.SuggestedPrimary), $"(primary #{n.SuggestedPrimary:X6})");
        Check("1.14: the suggested secondary is not black", n.SuggestedSecondary is int ns && ImagePalette.ToOkLab(ns).L >= ImagePalette.MinLedLightness - 0.01);

        // muted colours stay muted (no saturation boost)
        var muted = Img(120, 80, (x, y) => x < 60 ? ((byte)96, (byte)120, (byte)118, (byte)255) : ((byte)150, (byte)160, (byte)170, (byte)255));
        var m = ImagePalette.FromBitmap(muted);
        static double Chroma(int c) { var l = ImagePalette.ToOkLab(c); return Math.Sqrt(l.A * l.A + l.B * l.B); }
        Check("1.14: muted colours are kept muted (chroma never raised)", m.Swatches.All(s => Chroma(s.Color) <= Chroma(s.Source) + 0.01) && m.Swatches.Count >= 1);
        Check("1.14: an image with few colours says so instead of inventing more", m.Swatches.Count < ImagePalette.MinSwatches && m.Note != null);

        // transparency
        var half = Img(100, 100, (x, y) => x < 50 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)180, (byte)40, (byte)40, (byte)255));
        var h = ImagePalette.FromBitmap(half);
        Check("1.14: transparent pixels are ignored (only the visible part counts)", h.AnalysedPixels == 5000 && h.Swatches.All(s => (s.Color >> 16) > ((s.Color >> 8) & 255)));
        bool threw = false; string msg = "";
        try { ImagePalette.FromBitmap(Img(40, 40, (_, _) => ((byte)255, (byte)0, (byte)0, (byte)0))); } catch (PaletteException ex) { threw = true; msg = ex.Message; }
        Check("1.14: a fully transparent image gives a clear message", threw && msg.Contains("transparent"), msg);
    }

    /// <summary>A tiny, valid PNG whose header claims w x h pixels (a few bytes of image data; never decoded in full).</summary>
    static byte[] PngHeaderOnly(int w, int h)
    {
        static byte[] Be(int v) => BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v));
        static uint Crc(byte[] data) { uint crc = 0xFFFFFFFF; foreach (byte x in data) { crc ^= x; for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1)); } return crc ^ 0xFFFFFFFF; }
        static IEnumerable<byte> Chunk(string type, byte[] data) { var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray(); return Be(data.Length).Concat(td).Concat(Be((int)Crc(td))); }
        var ihdr = Be(w).Concat(Be(h)).Concat(new byte[] { 8, 6, 0, 0, 0 }).ToArray();
        return new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Chunk("IHDR", ihdr))
            .Concat(Chunk("IDAT", new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 })).Concat(Chunk("IEND", Array.Empty<byte>())).ToArray();
    }

    static void PaletteFileTests()
    {
        string dir = Path.Combine(RunMode.HardwareFree ? AppPaths.Data : Path.GetTempPath(), "palette-tests-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        try
        {
            string Err(string name, Action<string> make)
            {
                var p = Path.Combine(dir, name); make(p);
                try { ImagePalette.FromFile(p); return "(no error)"; } catch (PaletteException ex) { return ex.Message; }
            }
            var notImage = Err("text.png", p => File.WriteAllText(p, "not an image"));
            Check("1.14: a file that is not an image is reported plainly", notImage.Contains("not an image VANTA can read"), notImage);
            var empty = Err("empty.jpg", p => File.WriteAllBytes(p, Array.Empty<byte>()));
            Check("1.14: an empty file is reported plainly", empty.Contains("empty"), empty);
            var missing = Path.Combine(dir, "missing.png");
            string miss; try { ImagePalette.FromFile(missing); miss = "(no error)"; } catch (PaletteException ex) { miss = ex.Message; }
            Check("1.14: a missing file is reported plainly", miss.Contains("no longer exists"), miss);
            var sw = Stopwatch.StartNew();
            var huge = Err("huge.png", p => File.WriteAllBytes(p, PngHeaderOnly(60000, 40000)));
            Check("1.14: an image that is too large is refused from its header, before decoding", huge.Contains("60000") && huge.Contains("megapixels") && sw.ElapsedMilliseconds < 2000, $"{huge} ({sw.ElapsedMilliseconds} ms)");

            // a real 2400 x 1350 PNG: decoded small, quick, and the file is not held open afterwards
            var big = Path.Combine(dir, "big.png");
            var src = UiFixture.RainyForestImage(2400, 1350);
            using (var fs = File.Create(big)) { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(src)); enc.Save(fs); }
            sw.Restart();
            var r = ImagePalette.FromFile(big);
            double ms = sw.Elapsed.TotalMilliseconds;
            Check("1.14: a large image is decoded at most 320 px wide and analysed quickly", r.Thumbnail!.PixelWidth <= ImagePalette.WorkSize && r.Thumbnail.PixelHeight <= ImagePalette.WorkSize && r.ImageWidth == 2400 && ms < 3000, $"({r.Thumbnail.PixelWidth}x{r.Thumbnail.PixelHeight} from 2400x1350 in {ms:0} ms)");
            bool released; try { File.Delete(big); released = !File.Exists(big); } catch { released = false; }
            Check("1.14: the image file is not kept open or copied (it can be deleted right away)", released);
            Check("1.14: the same picture gives the same swatches from a file and from memory", r.Swatches.Count is >= 4 and <= 6);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static void WindowStateTests()
    {
        var s = new UiState();
        WindowPlacementKeeper.Record(s, WindowState.Normal, (100, 80, 1280, 820), 96);
        WindowPlacementKeeper.Record(s, WindowState.Maximized, null, 0);
        WindowPlacementKeeper.Record(s, WindowState.Minimized, null, 0);
        Check("1.14: minimising does not erase the remembered maximised state", s.Maximized && s.HasBounds && s.Left == 100 && s.Width == 1280);
        WindowPlacementKeeper.Record(s, WindowState.Normal, (200, 90, 1300, 840), 144);
        WindowPlacementKeeper.Record(s, WindowState.Minimized, null, 0);
        Check("1.14: minimising from normal keeps normal and its bounds", !s.Maximized && s.Left == 200 && s.Width == 1300 && s.Dpi == 144);
        WindowPlacementKeeper.Record(s, WindowState.Maximized, null, 0);
        Check("1.14: maximising keeps the normal restore bounds", s.Maximized && s.Left == 200 && s.Height == 840);
    }
}
