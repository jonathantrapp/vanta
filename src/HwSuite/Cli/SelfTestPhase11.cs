using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>
/// Phase 11 regression checks (mocks and fault injection only; nothing touches hardware):
///  - keyboard link recovery when reopening itself fails (no handle at all), initial absence, genuine device changes;
///  - the vendor-process freshness contract (ProcessPresence) under delayed polling and enumeration failures;
///  - theme tokens: measured WCAG contrast for every theme;
///  - lighting status derivation (what each device is actually doing).
/// </summary>
public static partial class SelfTest
{
    static void Phase11Tests()
    {
        KeyboardOpenFailureTests();
        PresenceFreshnessTests();
        ThemeContrastTests();
        LightingStatusTests();
        RestoreOutcomeTests();
        ThemeSettingTests();
        ExitLookTests();
    }

    /// <summary>Owner's choice (2026-10-15): after quitting, keyboard and mouse are solid like the streamed devices.</summary>
    static void ExitLookTests()
    {
        // keyboard, Breathing with live frames: at exit one stored write of the steady primary colour
        var cfg = new RgbConfig { Enabled = true, Keyboard = true, Effect = EffectKind.Breathing, ColorA = 0x2198AA, Brightness = 0.7 };
        var t = new FakeKbTransport();
        var s = NewKbSink(cfg, t);
        try
        {
            PostUntil(s, Keys(new Rgb(10, 20, 30)), () => t.LiveCount >= 2 && t.Count >= 1);
            int before = t.Count;
            Check("exit look: while live, the keyboard's stored fallback is the effect's onboard breathing", t.Last?.Mode == KeyboardLightingState.ModeBreathing, $"(mode {t.Last?.Mode})");
            var r = s.DeactivateForExit();
            Check("exit look: quitting during Breathing stores the steady primary colour on the keyboard (one stored write)",
                t.Count == before + 1 && t.Last?.Mode == KeyboardLightingState.ModeSteady && t.Last?.Color == Rgb.FromInt(0x2198AA) && t.Last?.Brightness > 0, $"({r}; writes {before} -> {t.Count})");
        }
        finally { s.Dispose(); }

        // keyboard, Static: already solid, no extra write
        cfg = new RgbConfig { Enabled = true, Keyboard = true, Effect = EffectKind.Static, ColorA = 0x2198AA };
        t = new FakeKbTransport(); s = NewKbSink(cfg, t);
        try
        {
            PostUntil(s, Keys(Rgb.FromInt(0x2198AA)), () => t.Count >= 1);
            int before = t.Count;
            var r = s.DeactivateForExit();
            Check("exit look: quitting during Static writes nothing more to the keyboard", t.Count == before, $"({r}; writes {before} -> {t.Count})");
        }
        finally { s.Dispose(); }

        // keyboard, Lights out: keeps stored brightness 0
        cfg = new RgbConfig { Enabled = true, Keyboard = true, Effect = EffectKind.Stealth };
        t = new FakeKbTransport(); s = NewKbSink(cfg, t);
        try
        {
            PostUntil(s, Keys(Rgb.Black), () => t.Count >= 1);
            int before = t.Count;
            s.DeactivateForExit();
            Check("exit look: quitting during Lights out leaves the keyboard off (no new write)", t.Count == before && t.Last?.Brightness == 0);
        }
        finally { s.Dispose(); }

        // keyboard, output switched off (not quitting): unchanged, the onboard breathing stays as the fallback
        cfg = new RgbConfig { Enabled = true, Keyboard = true, Effect = EffectKind.Breathing, ColorA = 0x2198AA };
        t = new FakeKbTransport(); s = NewKbSink(cfg, t);
        try
        {
            PostUntil(s, Keys(new Rgb(10, 20, 30)), () => t.LiveCount >= 2 && t.Count >= 1);
            int before = t.Count;
            s.Deactivate(Rgb.FromInt(0x2198AA));
            Check("exit look: switching lighting output off (not quitting) writes no steady colour", t.Count == before && t.Last?.Mode == KeyboardLightingState.ModeBreathing);
        }
        finally { s.Dispose(); }

        // mouse: breathing -> static primary colour at exit; static -> nothing; blocked -> nothing
        foreach (var (effect, blocked, expectWrite, name) in new[]
        {
            (EffectKind.Breathing, (string?)null, true, "quitting during Breathing sets the mouse to the static primary colour"),
            (EffectKind.Static, null, false, "quitting during Static writes nothing more to the mouse"),
            (EffectKind.Breathing, "Glorious CORE is running", false, "quitting while another program owns the mouse writes nothing")
        })
        {
            var mc = new RgbConfig { Enabled = true, Mouse = true, Effect = effect, ColorA = 0x2198AA, Brightness = 0.7 };
            var mt = new FakeTransport();
            var ms = new MouseSink(mc, mt, null, null, listenForDeviceChanges: false);
            ms.Controller.Debounce = TimeSpan.FromMilliseconds(50);
            ms.Controller.EnumerateInterval = TimeSpan.FromMilliseconds(20);
            try
            {
                var end = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < end && mt.Link.Applies == 0) { ms.Post(new[] { Rgb.Black }); Thread.Sleep(30); }
                int before = mt.Link.Applies;
                ms.Blocked = blocked;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = ms.DeactivateForExit();
                bool wrote = mt.Link.Applies > before;
                Check("exit look: " + name, wrote == expectWrite && (!expectWrite || mt.Link.Last?.Effect == MouseLightingState.EffectStatic && mt.Link.Last?.Color == Rgb.FromInt(0x2198AA)) && sw.ElapsedMilliseconds < 2500,
                    $"({r}; applies {before} -> {mt.Link.Applies}; {sw.ElapsedMilliseconds} ms)");
            }
            finally { ms.Dispose(); }
        }
    }

    static void ThemeSettingTests()
    {
        var old = ConfigStore.Parse("{\"Version\":4,\"FanControlEnabled\":false,\"Preset\":\"Performance\"}")!;
        Check("phase 11: a config saved before Phase 11 opens in the VANTA theme", old.Theme == "Vanta" && HwSuite.Ui.Themes.Find(old.Theme) == HwSuite.Ui.Themes.Vanta);
        var cfg = new AppConfig { Preset = "Silent", FanControlEnabled = true };
        cfg.Theme = "Porcelain";
        var back = ConfigStore.Parse(ConfigStore.ToJson(cfg))!;
        Check("phase 11: the chosen theme is saved and nothing else changes", back.Theme == "Porcelain" && back.Preset == "Silent" && back.FanControlEnabled);
        Check("phase 11: an unknown theme name falls back to VANTA", HwSuite.Ui.Themes.Find("Neon") == HwSuite.Ui.Themes.Vanta);
        Check("phase 11: every theme defines every token", HwSuite.Ui.Themes.All.All(t => HwSuite.Ui.Themes.TokenNames.All(t.Tokens.ContainsKey)));
    }

    static KeyboardLightingController NewRecoveryController(FakeKbTransport t) =>
        new(t, () => true, () => null, listenForDeviceChanges: false, liveVerified: () => true)
        {
            EnumerateInterval = TimeSpan.FromMilliseconds(40),
            ReopenBackoff = new[] { TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(180), TimeSpan.FromMilliseconds(240) }
        };

    /// <summary>Offer live frames for a while, as the sink's refresh does while the link is down.</summary>
    static void Offer(KeyboardLightingController ctl, byte[] p, int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { ctl.SendLive(p, out _); Thread.Sleep(20); }
    }

    static void KeyboardOpenFailureTests()
    {
        var p = ILovbeeKeyboardDevice.BuildLivePayload(Keys(new Rgb(9, 8, 7)));
        int max = KeyboardLightingController.MaxReopenAttempts;

        // A: transport failure, then every reopen fails inside Open() (no handle exists between attempts)
        var t = new FakeKbTransport();
        using (var ctl = NewRecoveryController(t))
        {
            ctl.SetLiveWanted(true);
            WaitFor(() => ctl.Connected);
            Check("phase 11: live frame on the first handle", ctl.SendLive(p, out _) == KeyboardLightingController.LiveResult.Sent);
            t.DeadGen = t.Opens; t.FailOpen = true;
            int calls0 = t.OpenCalls;
            ctl.SendLive(p, out _);   // fails: transport failure
            Offer(ctl, p, 2500);
            int calls = t.OpenCalls - calls0;
            Check($"phase 11: repeated Open() failures stay bounded ({max} attempts) with no handle", calls <= max && ctl.ReopenGaveUp, $"(open calls {calls}, gave up {ctl.ReopenGaveUp})");
            Check("phase 11: failed reopens are not counted as reconnections", ctl.Reconnects == 0, $"(reconnects {ctl.Reconnects})");
            int frozen = t.OpenCalls;
            t.FailOpen = false;
            Offer(ctl, p, 600);
            Check("phase 11: after giving up, an unchanged device path is not reopened again", t.OpenCalls == frozen, $"(open calls {frozen} -> {t.OpenCalls})");
            ctl.NotifyDeviceChange();   // an unrelated HID notification: same interfaces
            Offer(ctl, p, 400);
            Check("phase 11: an unrelated device notification does not restart recovery", t.OpenCalls == frozen && ctl.ReopenGaveUp, $"(open calls {frozen} -> {t.OpenCalls})");

            // genuine device change: unplug (no interfaces), then plug in again
            t.Sig = ""; ctl.NotifyDeviceChange();
            Offer(ctl, p, 300);
            Check("phase 11: unplugging during recovery is seen as absence (no failure counted)", !ctl.ReopenGaveUp && ctl.LinkStatus.Contains("not connected"), $"({ctl.LinkStatus})");
            t.Sig = "kb-1"; ctl.NotifyDeviceChange();
            bool back = WaitFor(() => ctl.SendLive(p, out _) == KeyboardLightingController.LiveResult.Sent, 2000);
            Check("phase 11: a genuine replug recovers the link and counts as a reconnection", back && ctl.Reconnects == 1, $"(reconnects {ctl.Reconnects})");
        }

        // B: budget kept across failed opens: open fails twice, then succeeds onto a dead handle, and so on
        t = new FakeKbTransport();
        using (var ctl = NewRecoveryController(t))
        {
            ctl.SetLiveWanted(true);
            WaitFor(() => ctl.Connected);
            t.DeadGen = int.MaxValue;   // every handle (old and new) fails its writes
            int calls0 = t.OpenCalls;
            var flip = new Thread(() => { for (int i = 0; i < 60; i++) { t.FailOpen = i % 3 != 2; Thread.Sleep(40); } }) { IsBackground = true };
            flip.Start();
            Offer(ctl, p, 3000);
            int calls = t.OpenCalls - calls0;
            Check("phase 11: the retry budget is shared by failed opens and failing reopened handles", calls <= max && ctl.ReopenGaveUp, $"(open calls {calls})");
        }

        // C: initial absence: nothing listed, nothing counted, connects when it appears
        t = new FakeKbTransport { Sig = "" };
        using (var ctl = NewRecoveryController(t))
        {
            ctl.SetLiveWanted(true);
            Offer(ctl, p, 500);
            Check("phase 11: initial absence is not a failure (no give-up, status not connected)", !ctl.ReopenGaveUp && !ctl.Connected && ctl.LinkStatus.Contains("not connected"), $"({ctl.LinkStatus})");
            t.Sig = "kb-1";
            Check("phase 11: a keyboard that appears later is opened", WaitFor(() => ctl.SendLive(p, out _) == KeyboardLightingController.LiveResult.Sent, 2000) && ctl.Reconnects == 0);
        }

        // D: present but Open() keeps failing from the start: bounded too
        t = new FakeKbTransport { FailOpen = true };
        using (var ctl = NewRecoveryController(t))
        {
            ctl.SetLiveWanted(true);
            Offer(ctl, p, 2500);
            Check("phase 11: a listed keyboard that never opens is retried a bounded number of times", t.OpenCalls <= max + 1 && ctl.ReopenGaveUp, $"(open calls {t.OpenCalls})");
        }

        // E: no Open() at all while asleep, even in recovery without a handle
        t = new FakeKbTransport();
        using (var ctl = NewRecoveryController(t))
        {
            ctl.SetLiveWanted(true);
            WaitFor(() => ctl.Connected);
            t.DeadGen = t.Opens; t.FailOpen = true;
            ctl.SendLive(p, out _);
            Offer(ctl, p, 150);   // one failed reopen: the handle is gone
            ctl.SetSuspended(true);
            int calls = t.OpenCalls;
            Offer(ctl, p, 700);
            Check("phase 11: no reopen attempt while asleep (no handle, recovery pending)", t.OpenCalls == calls, $"(open calls {calls} -> {t.OpenCalls})");
            t.FailOpen = false; t.DeadGen = -1;
            ctl.SetSuspended(false);
            Check("phase 11: after resume the pending recovery continues and live frames flow", WaitFor(() => ctl.SendLive(p, out _) == KeyboardLightingController.LiveResult.Sent, 3000));
        }
    }

    static void PresenceFreshnessTests()
    {
        // fake clock and process list: the getter never answers from an observation older than MaxAge
        long now = 0; int lists = 0; bool running = false; bool fail = false;
        using var pr = new ProcessPresence("fake.exe", TimeSpan.FromMilliseconds(200), startTimer: false)
        {
            Clock = () => now,
            // each list takes 5 ms of (fake) time
            Lister = () => { lists++; now += 5; if (fail) throw new InvalidOperationException("simulated enumeration failure"); return running; }
        };
        pr.CheckNow();
        int l0 = lists;
        now += 100; _ = pr.IsRunning;
        Check("phase 11: presence answers from an observation younger than MaxAge without listing", lists == l0);
        now += (long)ProcessPresence.MaxAge.TotalMilliseconds + 1;   // the poll timer is late (thread pool delayed)
        running = true;
        bool r = pr.IsRunning;
        Check($"phase 11: a delayed poll never yields a state older than {ProcessPresence.MaxAge.TotalMilliseconds:0} ms (lists on the spot)", r && lists == l0 + 1);
        fail = true; now += 1000;
        Check("phase 11: an enumeration failure is reported as running/unknown (conservative), not as absent", pr.IsRunning && pr.LastState == ProcessPresence.State.Unknown);
        fail = false; running = false; now += 1000;
        Check("phase 11: presence recovers after the failure", !pr.IsRunning && pr.LastState == ProcessPresence.State.Absent);
        Check("phase 11: the observation is timestamped when its list started (age includes the list time)", pr.ObservationAgeMs(now) == 5, $"({pr.ObservationAgeMs(now)} ms)");
    }

    static void ThemeContrastTests()
    {
        foreach (var th in HwSuite.Ui.Themes.All)
        {
            foreach (var (fg, bg, min, what) in HwSuite.Ui.Themes.ContrastPairs(th))
            {
                double c = HwSuite.Ui.Themes.Contrast(fg, bg);
                Check($"theme {th.Name}: {what} contrast {c:0.00} >= {min}", c >= min);
            }
        }
    }

    static void LightingStatusTests()
    {
        var cfg = new AppConfig { KeyboardVerified = true, KeyboardLiveVerified = true };
        cfg.Rgb.Enabled = true; cfg.Rgb.Effect = EffectKind.Breathing;
        DeviceFacts F(DeviceRole r) => new(r, Present: true, Available: true, Blocked: null, Included: true, Errors: 0, Sent: 10, Fault: null);
        var S = (DeviceRole r) => LightingStatus.Evaluate(cfg, F(r));
        Check("phase 11: streamed strip follows the shared animation", S(DeviceRole.Strip).State == DeviceLightState.Shared);
        Check("phase 11: the mouse breathes on its own (independent onboard effect)", S(DeviceRole.Mouse).State == DeviceLightState.Independent);
        Check("phase 11: a live-verified keyboard follows the shared animation", S(DeviceRole.Keyboard).State == DeviceLightState.Shared);
        cfg.KeyboardLiveVerified = false;
        Check("phase 11: a keyboard without live frames runs an independent onboard effect", S(DeviceRole.Keyboard).State == DeviceLightState.Independent);
        cfg.Rgb.RamOnboardEffects = true;
        Check("phase 11: RAM onboard mode is independent", S(DeviceRole.Ram).State == DeviceLightState.Independent);
        cfg.Rgb.Enabled = false;
        Check("phase 11: output off is reported for every device", S(DeviceRole.Strip).State == DeviceLightState.OutputOff && S(DeviceRole.Mouse).State == DeviceLightState.OutputOff);
        cfg.Rgb.Enabled = true;
        Check("phase 11: an excluded device says so", LightingStatus.Evaluate(cfg, F(DeviceRole.Board) with { Included = false }).State == DeviceLightState.Excluded);
        Check("phase 11: a blocked device says so", LightingStatus.Evaluate(cfg, F(DeviceRole.Board) with { Blocked = "RGB Fusion running" }).State == DeviceLightState.Blocked);
        Check("phase 11: a missing device is unavailable", LightingStatus.Evaluate(cfg, F(DeviceRole.Mouse) with { Present = false }).State == DeviceLightState.Unavailable);
        Check("phase 11: enabled with only errors is not reported as working", LightingStatus.Evaluate(cfg, F(DeviceRole.Strip) with { Sent = 0, Errors = 5 }).State == DeviceLightState.Fault);
        cfg.KeyboardVerified = false;
        Check("phase 11: an unverified keyboard is not claimed as synchronised", S(DeviceRole.Keyboard).State == DeviceLightState.NotVerified);
        cfg.Rgb.Effect = EffectKind.Stealth;
        Check("phase 11: Stealth reads as lights off", S(DeviceRole.Strip).State == DeviceLightState.Dark);
        cfg.Rgb.Effect = EffectKind.GradientWave;
        var mouse = S(DeviceRole.Mouse);
        Check("phase 11: the mouse never claims all-device synchronisation", mouse.State != DeviceLightState.Shared && mouse.Detail.Contains("not synchronised"));
    }

    static void RestoreOutcomeTests()
    {
        var empty = FailsafeWatchdog.Summarize(new List<FailsafeWatchdog.RestoreItem>(), registryCleared: true);
        Check("phase 11: --restore with nothing claimed exits 0", empty.ExitCode == 0);
        var ok = FailsafeWatchdog.Summarize(new() { new("nvml", "0", true, "VBIOS auto"), new("lhm", "x", true, "100% (BIOS curve after reboot)") }, registryCleared: true);
        Check("phase 11: --restore that handed everything back exits 0 and says motherboard headers stay at 100% until reboot", ok.ExitCode == 0 && ok.Text.Contains("reboot"));
        var bad = FailsafeWatchdog.Summarize(new() { new("nvml", "0", false, "NVML unavailable") }, registryCleared: false);
        Check("phase 11: a failed hand-back exits non-zero and keeps the record", bad.ExitCode == FailsafeWatchdog.RestoreFailedExit);
    }
}
