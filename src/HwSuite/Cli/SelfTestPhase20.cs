using System.Diagnostics;
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>
/// 1.20 checks: the Embers, Radar and Typing Ripples effects on every device geometry, their save format (older builds
/// read a fallback), what each is supposed to look like (fire hotter at the bottom, one sweep in step on every fan,
/// ripples starting at the pressed key), the key-position names, and render cost. No device is opened.
/// </summary>
public static partial class SelfTest
{
    static void Phase20Tests()
    {
        Guarded("1.20 effect settings", MoreSettingTests);
        Guarded("1.20 effects on every device", MoreRenderTests);
        Guarded("1.20 typing ripples", TypingTests);
    }

    static RgbConfig MoreCfg(EffectKind e) => new() { Enabled = true, Effect = e, ColorA = 0xFF8A2A, ColorB = 0x5A1004, Brightness = 1, Speed = 0.45, Density = 0.5, Glow = 0.4 };

    static void MoreSettingTests()
    {
        foreach (var e in EffectInfo.Scenes3)
        {
            var c = new AppConfig(); c.Rgb.Effect = e;
            var j = ConfigStore.ToJson(c);
            using var doc = JsonDocument.Parse(j);
            var rgb = doc.RootElement.GetProperty("Rgb");
            string? eff = rgb.GetProperty("Effect").GetString(), scene = rgb.GetProperty("Scene").GetString(), s3 = rgb.GetProperty("Scene3").GetString();
            Check($"1.20: {e} saved as Scene3, with a classic Effect and a 1.13 Scene for older builds",
                s3 == e.ToString() && EffectInfo.Classic.Any(k => k.ToString() == eff) && EffectInfo.Scenes.Any(k => k.ToString() == scene) && !rgb.TryGetProperty("Scene2", out _), $"(Effect {eff}, Scene {scene})");
            var back = ConfigStore.Parse(j)!;
            Check($"1.20: {e} survives save and load", back.Rgb.Effect == e);
            var older = ConfigStore.Parse($"{{\"Rgb\":{{\"Effect\":\"{eff}\",\"Scene\":\"{scene}\"}}}}")!;
            Check($"1.20: a build without Scene3 (1.13-1.19) reads {e} as {scene}", older.Rgb.Effect.ToString() == scene);
            var rev = ConfigStore.Parse($"{{\"Rgb\":{{\"Scene3\":\"{e}\",\"Scene\":\"{scene}\",\"Effect\":\"{eff}\"}}}}")!;
            Check($"1.20: {e} loads with the fields in any order", rev.Rgb.Effect == e);
            Check($"1.20: {e} has a label, description, colour roles and a starter look",
                EffectInfo.Label(e).Length > 0 && EffectInfo.Describe(e).Length > 40 && EffectInfo.ColourRoles(e).Primary.Length > 0 && LightingLooks.Starters.Any(l => l.Effect == e));
            Check($"1.20: {e} is an animated scene with a density control", EffectInfo.IsScene(e) && EffectInfo.Animated(e) && EffectInfo.DensityLabel(e) != null && !EffectInfo.IsNewScene(e));
        }
        Check("1.20: only Typing Ripples listens for keys", EffectInfo.Scenes3.Count(EffectInfo.UsesKeys) == 1 && EffectInfo.UsesKeys(EffectKind.TypingRipples)
            && !EffectInfo.Classic.Concat(EffectInfo.Scenes).Concat(EffectInfo.RainScenes).Any(EffectInfo.UsesKeys));
        var r15 = ConfigStore.Parse("{\"Rgb\":{\"Effect\":\"GradientWave\",\"Scene\":\"Rainfall\",\"Scene2\":\"Drip\"}}")!;
        Check("1.20: 1.15 rain settings still load", r15.Rgb.Effect == EffectKind.Drip);
    }

    static (string name, LedGeometry g)[] MoreGeometries()
    {
        var L = LayoutTransform.Identity;
        return new[]
        {
            ("AL80 keys", L.GeometryFor(DeviceRole.Keyboard, Al80KeyMap.Leds)),
            ("i87 keys", L.GeometryFor(DeviceRole.Keyboard, ILovbeeKeyMap.Keys.Length)),
            ("case fans", L.GeometryFor(DeviceRole.Strip, 27)),
            ("memory", L.GeometryFor(DeviceRole.Ram, 26, new[] { 5, 8, 5, 8 })),
            ("board", L.GeometryFor(DeviceRole.Board, 2)),
            ("mouse", L.GeometryFor(DeviceRole.Mouse, 1)),
        };
    }

    static void MoreRenderTests()
    {
        KeyActivity.Clear();
        double clock = 0;
        KeyActivity.TestClock = () => clock;   // the renders below follow the effect time, as the engine's frames do in real time
        try
        {
        foreach (var e in EffectInfo.Scenes3)
        {
            var cfg = MoreCfg(e);
            foreach (var (name, g) in MoreGeometries())
            {
                var f = new Rgb[g.Count];
                var seen = new HashSet<Rgb>(); int lit = 0; bool threw = false;
                try
                {
                    for (double t = 0.1; t < 12; t += 0.37)
                    {
                        clock = t;
                        if (e == EffectKind.TypingRipples && ((int)(t * 3)) % 4 == 0) KeyActivity.Press("W");
                        Effects.Render(e, EffectContext.From(cfg, t, null), g, f);
                        foreach (var x in f) { seen.Add(x); if (x.R + x.G + x.B > 20) lit++; }
                    }
                }
                catch (Exception ex) { threw = true; Log.Warn($"{e} on {name}: {ex.Message}"); }
                Check($"1.20: {e} renders on {name}: changing and not dark", !threw && seen.Count > 2 && lit > 0, $"({seen.Count} colours)");
            }
            var off = MoreCfg(e); off.Brightness = 0;
            var kb = LayoutTransform.Identity.GeometryFor(DeviceRole.Keyboard, Al80KeyMap.Leds);
            var fo = new Rgb[kb.Count];
            Effects.Render(e, EffectContext.From(off, 3, null), kb, fo);
            Check($"1.20: {e} at brightness 0 is dark", fo.All(x => x == Rgb.Black));
        }
        }
        finally { KeyActivity.TestClock = null; KeyActivity.Clear(); }

        // Embers: the bottom key row is hotter than the top row, on average
        {
            var kb = LayoutTransform.Identity.GeometryFor(DeviceRole.Keyboard, Al80KeyMap.Leds);
            var f = new Rgb[kb.Count]; double top = 0, bottom = 0;
            for (double t = 0; t < 20; t += 0.25)
            {
                Effects.Render(EffectKind.Embers, EffectContext.From(MoreCfg(EffectKind.Embers), t, null), kb, f);
                foreach (var k in Al80KeyMap.Keys.Where(k => k.HasKey))
                {
                    double v = f[k.Led].R + f[k.Led].G + f[k.Led].B;
                    if (k.Row == 0) top += v; else if (k.Row == 5) bottom += v / 9.0 * 14;   // 9 keys in the bottom row, 14 in the top
                }
            }
            Check("1.20: Embers burns hotter on the space-bar row than on the F-row", bottom > top * 1.6, $"(bottom {bottom:0} vs top {top:0})");
            var ram = LayoutTransform.Identity.GeometryFor(DeviceRole.Ram, 8, new[] { 8 });
            var fr = new Rgb[8]; double r0 = 0, r7 = 0;
            for (double t = 0; t < 20; t += 0.25) { Effects.Render(EffectKind.Embers, EffectContext.From(MoreCfg(EffectKind.Embers), t, null), ram, fr); r0 += fr[0].R; r7 += fr[7].R; }
            Check("1.20: Embers on memory burns at the bottom of the stick (LED 0 is the top)", r7 > r0 * 1.3, $"(bottom {r7:0} top {r0:0})");
        }

        // Radar: the beam moves, and every fan sweeps in step
        {
            var kb = LayoutTransform.Identity.GeometryFor(DeviceRole.Keyboard, Al80KeyMap.Leds);
            var cfg = MoreCfg(EffectKind.Radar); cfg.Density = 0;
            int Brightest(double t) { var f = new Rgb[kb.Count]; Effects.Render(EffectKind.Radar, EffectContext.From(cfg, t, null), kb, f); int bi = 0; for (int i = 1; i < f.Length; i++) if (f[i].R + f[i].G + f[i].B > f[bi].R + f[bi].G + f[bi].B) bi = i; return bi; }
            var spots = Enumerable.Range(0, 8).Select(s => Brightest(s * 0.4)).Distinct().Count();
            Check("1.20: Radar's beam moves around the keyboard", spots >= 4, $"({spots} different brightest keys in 8 moments)");
            var cs = LayoutTransform.Identity.GeometryFor(DeviceRole.Strip, 27);
            var fc = new Rgb[cs.Count];
            Effects.Render(EffectKind.Radar, EffectContext.From(cfg, 1.3, null), cs, fc);
            var parts = Enumerable.Range(0, cs.Count).Where(i => cs.Part[i] >= 0 && cs.PartKinds[cs.Part[i]] != CasePartKind.Logo).GroupBy(i => cs.Part[i]).ToList();
            var angles = parts.Select(gp => { int bi = gp.OrderByDescending(i => fc[i].R + fc[i].G + fc[i].B).First(); return Math.Atan2(cs.Y[bi] - cs.PartY[gp.Key], cs.X[bi] - cs.PartX[gp.Key]); }).ToList();
            bool inStep = angles.Count < 2 || angles.All(a => Math.Abs(Math.IEEERemainder(a - angles[0], 2 * Math.PI)) < 0.9);
            Check("1.20: Radar sweeps around every fan in step", parts.Count >= 1 && inStep, $"({parts.Count} fans, beam angles {string.Join(" ", angles.Select(a => a.ToString("0.0")))})");
        }

        // cost: 84 keys per frame, well under a millisecond
        foreach (var e in EffectInfo.Scenes3)
        {
            var kb = LayoutTransform.Identity.GeometryFor(DeviceRole.Keyboard, Al80KeyMap.Leds);
            var f = new Rgb[kb.Count]; var cfg = MoreCfg(e);
            for (int i = 0; i < 20; i++) KeyActivity.Press("Space");
            var sw = Stopwatch.StartNew(); int frames = 0;
            while (sw.ElapsedMilliseconds < 300) { Effects.Render(e, EffectContext.From(cfg, frames / 60.0, null), kb, f); frames++; }
            double ms = sw.Elapsed.TotalMilliseconds / frames;
            Check($"1.20: {e} renders an 84-key frame in under 1 ms", ms < 1.0, $"({ms:0.000} ms)");
            KeyActivity.Clear();
        }
    }

    static void TypingTests()
    {
        Check("1.20: key names: letters, digits, F-keys, Space, Del (AL80 and i87 spellings)",
            Ui.KeyHook.NameOf(0x57) == "W" && Ui.KeyHook.NameOf(0x31) == "1" && Ui.KeyHook.NameOf(0x7B) == "F12" && Ui.KeyHook.NameOf(0x20) == "Space" && Ui.KeyHook.NameOf(0x2E) == "Del|Delete" && Ui.KeyHook.NameOf(0xFF) == "?");
        double now = 100;
        KeyActivity.TestClock = () => now;
        try
        {
            KeyActivity.Clear();
            var kb = LayoutTransform.Identity.GeometryFor(DeviceRole.Keyboard, Al80KeyMap.Leds);
            var cfg = MoreCfg(EffectKind.TypingRipples); cfg.ColorA = 0x00FFFF; cfg.ColorB = 0x100020;
            var f = new Rgb[kb.Count];
            Effects.Render(EffectKind.TypingRipples, EffectContext.From(cfg, 1, null), kb, f);
            int quiet = f.Max(x => x.R + x.G + x.B);
            int w = Array.FindIndex(Al80KeyMap.Keys, k => k.Name == "W"), p = Array.FindIndex(Al80KeyMap.Keys, k => k.Name == "P");
            KeyActivity.Press("W");
            now += 0.02;
            Effects.Render(EffectKind.TypingRipples, EffectContext.From(cfg, 1, null), kb, f);
            int bw = f[w].R + f[w].G + f[w].B;
            Check("1.20: a press lights its own key at once (and nothing is lit with no presses)", bw >= f.Max(x => x.R + x.G + x.B) && bw > quiet * 3, $"(W {bw}, quiet max {quiet})");
            now += 0.45;
            Effects.Render(EffectKind.TypingRipples, EffectContext.From(cfg, 1, null), kb, f);
            var dists = Enumerable.Range(0, kb.Count).Where(i => Al80KeyMap.Keys[i].HasKey).OrderByDescending(i => f[i].R + f[i].G + f[i].B).Take(5)
                .Select(i => Math.Sqrt(Math.Pow(kb.X[i] - kb.X[w], 2) + Math.Pow(kb.Y[i] - kb.Y[w], 2))).ToList();
            Check("1.20: half a second later the ring has moved out from W", dists.Average() > 0.12, $"(brightest keys {dists.Average():0.00} keyboard widths from W)");
            now += 3.5;
            Effects.Render(EffectKind.TypingRipples, EffectContext.From(cfg, 1, null), kb, f);
            Check("1.20: presses older than 3 s are gone", f.Max(x => x.R + x.G + x.B) <= quiet + 3);
            var cs = LayoutTransform.Identity.GeometryFor(DeviceRole.Strip, 27); var fc = new Rgb[cs.Count];
            Effects.Render(EffectKind.TypingRipples, EffectContext.From(cfg, 1, null), cs, fc); int fq = fc.Max(x => x.B);
            KeyActivity.Press("Space"); now += 0.05;
            Effects.Render(EffectKind.TypingRipples, EffectContext.From(cfg, 1, null), cs, fc);
            Check("1.20: the fans pulse when a key is pressed", fc.Max(x => x.B) > fq + 60, $"({fq} -> {fc.Max(x => x.B)})");
            KeyActivity.Press("VolumeKnob?"); now += 0.02;
            Effects.Render(EffectKind.TypingRipples, EffectContext.From(cfg, 1, null), kb, f);
            Check("1.20: a key the map does not have ripples from the centre (no crash)", f.Any(x => x.R + x.G + x.B > quiet * 2));
        }
        finally { KeyActivity.TestClock = null; KeyActivity.Clear(); }
    }
}
