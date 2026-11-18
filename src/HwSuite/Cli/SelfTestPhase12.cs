using System.Diagnostics;
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;
using HwSuite.Ui;

namespace HwSuite.Cli;

/// <summary>1.12 checks: hardware-free isolation, honest service restart status, per-look acknowledgement, saved looks,
/// device arrangement and window placement. Mocks and pure functions only: nothing here touches hardware.</summary>
public static partial class SelfTest
{
    /// <summary>Boundary refusals the isolation tests provoke on purpose (the run's final check expects exactly these).</summary>
    static int _provokedRefusals;

    static void Phase12Tests()
    {
        Guarded("1.12 isolation", IsolationTests);
        Guarded("1.12 service restart", ServiceRestartTests);
        Guarded("1.12 acknowledgement", AcknowledgementTests);
        Guarded("1.12 looks", LookTests);
        Guarded("1.12 arrangement", ArrangementTests);
        Guarded("1.12 window placement", WindowPlacementTests);
    }

    // ---------------------------------------------------------------------------------------------------------
    static void IsolationTests()
    {
        if (!RunMode.HardwareFree) { Log.Info("      isolation checks need --no-hardware (this run may open hardware read-only); skipped"); return; }
        Check("1.12: hardware-free run uses an isolated data folder and log", AppPaths.Isolated && AppPaths.Data == RunMode.IsolatedData
            && Path.GetDirectoryName(AppPaths.LogFile) == AppPaths.Data, $"({AppPaths.Data})");
        Check("1.12: hardware-free run never writes the owner's config", ConfigStore.ReadOnly);

        int before = RunMode.Refusals;
        // recovery: a claim record in the ISOLATED folder (unknown backend, so even a failed gate could not touch a fan)
        ControlRegistry.Mark("selftest-only", "isolation-probe");
        var outcome = FailsafeWatchdog.RestoreAll();
        bool stillListed = ControlRegistry.Load().Any(e => e.Backend == "selftest-only");
        ControlRegistry.Unmark("selftest-only", "isolation-probe");
        Check("1.12: recovery (RestoreAll) refuses in a hardware-free run and leaves the record untouched",
            outcome.Text.StartsWith("Hardware-free run") && stillListed && RunMode.Refusals == before + 1, $"({outcome.Text})");

        var svc = WindowsServiceControl.Instance.Start("VANTA-NoSuchService-SelfTest", TimeSpan.FromSeconds(1));
        Check("1.12: service start refuses in a hardware-free run", svc.Status == ServiceStartStatus.Failed && svc.Message == "hardware-free run" && RunMode.Refusals == before + 2);
        var script = GigabyteServices.Run("Status");
        Check("1.12: Toggle-Gigabyte.ps1 is never run in a hardware-free run", !script.ok && script.output == "hardware-free run" && RunMode.Refusals == before + 3);
        FailsafeWatchdog.RestoreServicesIfAllowed("self-test");
        Check("1.12: service restore after exit refuses in a hardware-free run", RunMode.Refusals == before + 4);
        Check("1.12: HID enumeration refuses in a hardware-free run", HidEnumerator.Find(0x258A).Count == 0 && RunMode.Refusals == before + 5);
        Check("1.12: NVML refuses to open in a hardware-free run", NvmlGpu.TryOpen(out var nerr) == null && nerr == "hardware-free run" && RunMode.Refusals == before + 6);

        using var app = new AppServices(new AppConfig());
        bool threw = false;
        try { app.InitializeHardware(); } catch (HardwareFreeViolation) { threw = true; }
        Check("1.12: hardware initialisation throws before opening anything", threw && app.Ownership == null && RunMode.Refusals == before + 7);
        threw = false;
        try { app.InitializeLighting(); } catch (HardwareFreeViolation) { threw = true; }
        Check("1.12: lighting initialisation throws before opening anything", threw && app.Rgb.Sinks.Count == 0 && RunMode.Refusals == before + 8);
        threw = false;
        try { app.Start(); } catch (HardwareFreeViolation) { threw = true; }
        Check("1.12: engines are never started in a hardware-free run", threw && !app.Rgb.IsActive && RunMode.Refusals == before + 9);
        _provokedRefusals += RunMode.Refusals - before;
    }

    // ---------------------------------------------------------------------------------------------------------
    static void ServiceRestartTests()
    {
        // the bounded wait itself (simulated clock: the poll advances it, so nothing waits 15 s)
        int clockMs = 0;
        ServiceStartOutcome Wait(Func<int, ServiceRunState> state) =>
            WindowsServiceControl.WaitForRunning(() => (state(clockMs), 0u), TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(250), d => clockMs += (int)d.TotalMilliseconds, () => TimeSpan.FromMilliseconds(clockMs));
        clockMs = 0; var running = Wait(ms => ms >= 2000 ? ServiceRunState.Running : ServiceRunState.StartPending);
        Check("1.12: a service seen Running within the wait is reported running", running.Status == ServiceStartStatus.Running && running.Describe("EasyTuneEngineService") == "EasyTuneEngineService: started (running)");
        clockMs = 0; var slow = Wait(_ => ServiceRunState.StartPending);
        Check("1.12: StartPending at the deadline is 'Start requested; still starting.' and the wait stays bounded",
            slow.Status == ServiceStartStatus.StillStarting && slow.Describe("EasyTuneEngineService") == "EasyTuneEngineService: Start requested; still starting." && clockMs is >= 15000 and <= 15250, $"({slow.Describe("EasyTuneEngineService")}, {clockMs} ms simulated)");
        clockMs = 0; var died = Wait(ms => ms >= 1000 ? ServiceRunState.Stopped : ServiceRunState.StartPending);
        Check("1.12: a service that stops again while starting is a failure, reported early", died.Status == ServiceStartStatus.Failed && clockMs < 2000, $"({died.Message})");
        clockMs = 0; var unread = Wait(_ => ServiceRunState.Unknown);
        Check("1.12: an unreadable state is uncertain, never success", unread.Status == ServiceStartStatus.Uncertain && !unread.Describe("X").Contains("started (running)"));
        clockMs = 0; var never = Wait(_ => ServiceRunState.Stopped);
        Check("1.12: still Stopped after an accepted request is uncertain, not success", never.Status == ServiceStartStatus.Uncertain);

        string rec = Path.Combine(AppPaths.Data, "selftest-gigabyte-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var probe = new FakeProbe();
            foreach (var s in new[] { "EasyTuneEngineService", "GService", "GCCService", "AppCenter" }) probe.Svc[s] = ServiceRunState.Running;
            probe.Svc["GvService"] = ServiceRunState.Stopped;   // stopped before VANTA: never started by it
            var services = new FakeServices(probe);
            var guard = new GigabyteGuard(probe, services, () =>
            {
                lock (probe) foreach (var s in new[] { "EasyTuneEngineService", "GService", "GCCService", "AppCenter" }) probe.Svc[s] = ServiceRunState.Stopped;
                return (true, "");
            }, rec);
            guard.StopAndRecord("self-test");
            services.Behaviour["EasyTuneEngineService"] = FakeServices.Mode.Slow;
            services.Behaviour["GService"] = FakeServices.Mode.Succeed;
            services.Behaviour["GCCService"] = FakeServices.Mode.Refuse;
            services.Behaviour["AppCenter"] = FakeServices.Mode.StopsAgain;
            var results = guard.RestoreRecordedDetailed();
            string T(string s) => results.First(r => r.Service == s).Text;
            Check("1.12: slow EasyTune start says 'Start requested; still starting.' (not started, not failed)",
                T("EasyTuneEngineService") == "EasyTuneEngineService: Start requested; still starting.", $"({T("EasyTuneEngineService")})");
            Check("1.12: a service observed running says started", T("GService") == "GService: started (running)");
            Check("1.12: a refused start request says NOT started", T("GCCService").Contains("NOT started") && T("GCCService").Contains("refused"));
            Check("1.12: a service that stops again is NOT started", T("AppCenter").Contains("NOT started"));
            Check("1.12: the wait passed to the service manager is the bounded 15 s", services.LastTimeout == GigabyteGuard.StartWait);
            Check("1.12: a service VANTA did not stop is never started", !services.Started.Contains("GvService") && results.All(r => r.Service != "GvService"));
            var kept = guard.LoadRecord().StoppedServices;
            Check("1.12: failed starts stay on the restart list; running and still-starting ones do not",
                kept.OrderBy(x => x).SequenceEqual(new[] { "AppCenter", "GCCService" }), $"(kept {string.Join(",", kept)})");
            // a second restore (the watchdog right after exit): only kept services, only while Stopped
            services.Started.Clear();
            lock (probe) probe.Svc["GCCService"] = ServiceRunState.Running;   // someone else started it meanwhile
            services.Behaviour["AppCenter"] = FakeServices.Mode.Succeed;
            var second = guard.RestoreRecorded();
            Check("1.12: the retry starts only a kept service that is still stopped", services.Started.SequenceEqual(new[] { "AppCenter" }) && second.Any(x => x.Contains("GCCService") && x.Contains("left alone")),
                $"(started {string.Join(",", services.Started)})");
            Check("1.12: after a successful retry the restart list is empty", guard.LoadRecord().StoppedServices.Count == 0);
            lock (probe) probe.Svc["EasyTuneEngineService"] = ServiceRunState.StartPending;
            var rec2 = guard.LoadRecord(); rec2.StoppedServices.Add("EasyTuneEngineService");
            File.WriteAllText(rec, JsonSerializer.Serialize(rec2));
            guard.PruneRecord();
            Check("1.12: a service still StartPending is pruned from the list, never started twice", guard.LoadRecord().StoppedServices.Count == 0);
        }
        finally { try { File.Delete(rec); } catch { } }
    }

    // ---------------------------------------------------------------------------------------------------------
    static void AcknowledgementTests()
    {
        var sink = new TestSink();
        Check("1.12: a device never asked for a look has no acknowledgement", sink.FrameAck == LightAck.Unknown);
        sink.Request(1);
        sink.Post(new[] { new Rgb(10, 20, 30) });
        Check("1.12: a queued frame of the current look is pending", sink.FrameAck == LightAck.Pending);
        sink.DrainOnceForTest();
        Check("1.12: an accepted write acknowledges the current look", sink.FrameAck == LightAck.Accepted);
        sink.Request(2);
        Check("1.12: a new look is pending even though earlier writes were accepted (lifetime totals do not count)", sink.FrameAck == LightAck.Pending && sink.FramesSent > 0);
        sink.Fail = true;
        sink.Post(new[] { new Rgb(40, 50, 60) }); sink.DrainOnceForTest();
        Check("1.12: a failed write of the current look is reported failed", sink.FrameAck == LightAck.Failed);
        sink.Fail = false;
        sink.Post(new[] { new Rgb(40, 50, 61) }); sink.DrainOnceForTest();
        Check("1.12: a later successful update clears the earlier failure", sink.FrameAck == LightAck.Accepted);
        sink.Post(new[] { new Rgb(1, 1, 1) });   // queued under look 2
        sink.Request(3);
        sink.DrainOnceForTest();
        Check("1.12: a frame queued for the previous look does not acknowledge the new one", sink.FrameAck == LightAck.Pending);

        // status: unconfirmed devices are not "in step"
        var cfg = new AppConfig(); cfg.Rgb.Enabled = true; cfg.Rgb.Effect = EffectKind.Breathing; cfg.KeyboardVerified = true;
        DeviceFacts F(LightAck a) => new(DeviceRole.Strip, true, true, null, true, 0, 5000, null) { Ack = a };
        var acc = LightingStatus.Evaluate(cfg, F(LightAck.Accepted));
        var pend = LightingStatus.Evaluate(cfg, F(LightAck.Pending));
        var fail = LightingStatus.Evaluate(cfg, F(LightAck.Failed));
        var unknown = LightingStatus.Evaluate(cfg, F(LightAck.Unknown));
        Check("1.12: accepted current look: shared and confirmed", acc.State == DeviceLightState.Shared && acc.Confirmed);
        Check("1.12: pending current look: not confirmed (not counted in step)", pend.State == DeviceLightState.Shared && !pend.Confirmed);
        Check("1.12: failed current look: a fault even with many earlier successful writes", fail.State == DeviceLightState.Fault && !fail.Confirmed && fail.Headline == "Update not accepted");
        Check("1.12: unknown acknowledgement is never confirmed by lifetime totals", !unknown.Confirmed);
        var statuses = new[] { acc, pend, fail, unknown };
        Check("1.12: the in-step count includes only confirmed shared devices", statuses.Count(s => s.State == DeviceLightState.Shared && s.Confirmed) == 1);

        // mouse controller: acknowledgement from its own applied/failed state (no extra traffic)
        var tr = new FakeTransport();
        using (var mc = new MouseLightingController(tr, listenForDeviceChanges: false) { Debounce = TimeSpan.Zero, Backoff = new[] { TimeSpan.FromMilliseconds(30) } })
        {
            var mcfg = new RgbConfig { Enabled = true, Effect = EffectKind.Static, ColorA = 0x112233 };
            var s1 = MouseLightingState.From(mcfg)!;
            mc.SetDesired(s1);
            Check("1.12: mouse: a setting that was not yet written is pending or accepted only after the write",
                WaitUntil(() => mc.Ack == LightAck.Accepted, 3000) && tr.Link.Applies >= 1);
            int applies = tr.Link.Applies;
            tr.Link.Fail = true;
            mcfg.ColorA = 0x445566;
            mc.SetDesired(MouseLightingState.From(mcfg)!);
            Check("1.12: mouse: a failed write of the wanted setting is reported failed", WaitUntil(() => mc.Ack == LightAck.Failed, 3000));
            tr.Link.Fail = false;
            mcfg.ColorA = 0x778899;
            mc.SetDesired(MouseLightingState.From(mcfg)!);
            Check("1.12: mouse: a later accepted setting clears the failure", WaitUntil(() => mc.Ack == LightAck.Accepted, 3000) && tr.Link.Applies > applies);
        }

        // RAM onboard effect: a module that has not confirmed it withholds acknowledgement; exhausted retries are a failure
        var ene = new FakeEne((0x70, 5), (0x71, 8));
        var rcfg = new RgbConfig { Enabled = true, Effect = EffectKind.Breathing, RamOnboardEffects = true };
        using var ram = new RamSink(rcfg, ene);
        ene.Script[0x71] = new Queue<EneOutcome>(new[] { EneOutcome.Unconfirmed, EneOutcome.Unconfirmed, EneOutcome.Unconfirmed });
        ram.Request(1);
        ram.Post(new Rgb[13]); ram.DrainOnceForTest();
        Check("1.12: RAM onboard effect not confirmed on every module: not acknowledged yet", ram.FrameAck == LightAck.Pending);
        ram.Post(new Rgb[13]); ram.DrainOnceForTest();
        ram.Post(new Rgb[13]); ram.DrainOnceForTest();
        Check("1.12: RAM onboard effect still unconfirmed after the bounded retries: failed", ram.FrameAck == LightAck.Failed && ram.OnboardProblem != null);
    }

    // ---------------------------------------------------------------------------------------------------------
    static void LookTests()
    {
        var s = LightingLooks.Starters;
        bool Is(string n, EffectKind e, int a, int b, double br, double? sp) => s.Any(l => l.Name == n && l.Effect == e && l.ColorA == a && l.ColorB == b && Math.Abs(l.Brightness - br) < 1e-9 && l.Speed == sp && l.Starter);
        // 1.13: the Gradient Wave starter is named "Teal Tide" (values unchanged; "Rainfall" is now the falling-rain effect)
        Check("1.12: the four wallpaper starters are exactly as specified (1.13: 'Rainfall' renamed 'Teal Tide')",
            s.Count(l => l.Group == LightingLooks.ClassicGroup) == 4 && Is("Forest Mist", EffectKind.Breathing, 0x6EAAA5, 0xB8CBC9, 0.35, LightingLooks.Slow) && Is(LightingLooks.TealTide, EffectKind.GradientWave, 0x397780, 0xA9C4CC, 0.35, LightingLooks.Slow)
            && Is("Deep Woods", EffectKind.Static, 0x39746A, 0x39746A, 0.25, null) && Is("Moonlit Fog", EffectKind.Static, 0xB4CAD0, 0xB4CAD0, 0.30, null));
        double slow = LightingLooks.Slow, breathsPerSecond = 0.12 + slow * 0.6;
        Check("1.12: 'slow' is inside the existing speed range (no protocol timing change)", slow > 0 && slow < 1 && breathsPerSecond < 0.3, $"(speed {slow:0.00}: streamed breathing {breathsPerSecond:0.00}/s)");

        var cfg = UiFixture.LoadConfig(null);   // a copy of the owner's real configuration shape (read-only, in memory)
        cfg.Rgb.Enabled = true; cfg.Rgb.Effect = EffectKind.Breathing; cfg.Rgb.ColorA = 0x123456; cfg.Rgb.Speed = 0.66;
        Check("1.12: no starter is selected automatically", !LightingLooks.Starters.Any(l => LightingLooks.Matches(cfg, l)));
        string Strip(AppConfig c, bool withLooks)
        {
            var copy = ConfigStore.Parse(ConfigStore.ToJson(c))!;
            if (!withLooks) copy.Looks.Clear();
            return ConfigStore.ToJson(copy);
        }
        string beforeAll = Strip(cfg, false);
        var mouseBefore = MouseLightingState.From(cfg.Rgb); var kbBefore = KeyboardLightingState.From(cfg.Rgb, 3);
        Check("1.12: saving the current look succeeds", LightingLooks.SaveCurrent(cfg, "Evening") == null && cfg.Looks.Count == 1);
        Check("1.12: saving changes nothing else in the configuration (no other preference touched)", Strip(cfg, false) == beforeAll);
        Check("1.12: saving sends nothing: the device settings derived from the config are unchanged",
            Equals(MouseLightingState.From(cfg.Rgb), mouseBefore) && Equals(KeyboardLightingState.From(cfg.Rgb, 3), kbBefore));
        var back = ConfigStore.Parse(ConfigStore.ToJson(cfg))!;
        Check("1.12: saved looks survive save and load", back.Looks.Count == 1 && back.Looks[0].Name == "Evening" && back.Looks[0].ColorA == 0x123456 && back.Looks[0].Speed == 0.66);
        var lookJson = JsonSerializer.Serialize(cfg.Looks[0]);
        Check("1.12: a look stores only effect, colours, brightness and speed",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(lookJson)!.Keys.OrderBy(k => k).SequenceEqual(new[] { "Brightness", "ColorA", "ColorB", "Effect", "Name", "Speed" }), lookJson);

        // apply: only the look fields change
        var forest = LightingLooks.Starters[0];
        var c2 = ConfigStore.Parse(ConfigStore.ToJson(cfg))!;
        LightingLooks.Apply(c2, forest);
        var c2Norm = ConfigStore.Parse(ConfigStore.ToJson(c2))!; var cNorm = ConfigStore.Parse(ConfigStore.ToJson(cfg))!;
        c2Norm.Rgb.Effect = cNorm.Rgb.Effect; c2Norm.Rgb.ColorA = cNorm.Rgb.ColorA; c2Norm.Rgb.ColorB = cNorm.Rgb.ColorB; c2Norm.Rgb.Brightness = cNorm.Rgb.Brightness; c2Norm.Rgb.Speed = cNorm.Rgb.Speed;
        Check("1.12: applying a look changes only effect, colours, brightness and speed", ConfigStore.ToJson(c2Norm) == ConfigStore.ToJson(cNorm));
        Check("1.12: an applied look matches afterwards", LightingLooks.Matches(c2, forest));
        double speedBefore = c2.Rgb.Speed;
        LightingLooks.Apply(c2, LightingLooks.Starters[2]);
        Check("1.12: a static look leaves the speed alone (not part of it)", c2.Rgb.Speed == speedBefore && c2.Rgb.Effect == EffectKind.Static && c2.Rgb.ColorA == 0x39746A);

        // output disabled stays disabled
        var off = ConfigStore.Parse(ConfigStore.ToJson(cfg))!; off.Rgb.Enabled = false;
        LightingLooks.Apply(off, LightingLooks.Starters[1]);
        Check("1.12: applying with lighting output off keeps it off (nothing is sent)", !off.Rgb.Enabled && MouseLightingState.From(off.Rgb) == null && KeyboardLightingState.From(off.Rgb, 3) == null && off.Rgb.Effect == EffectKind.GradientWave);

        // Lights out: devices stay dark, the restored look changes
        var lo = ConfigStore.Parse(ConfigStore.ToJson(cfg))!; lo.Rgb.Enabled = true; lo.Rgb.Effect = EffectKind.Breathing;
        LightsOut.Toggle(lo);
        var darkMouse = MouseLightingState.From(lo.Rgb); var darkKb = KeyboardLightingState.From(lo.Rgb, 3);
        LightingLooks.Apply(lo, LightingLooks.Starters[3]);
        bool stillDark = LightsOut.IsOn(lo.Rgb) && Equals(MouseLightingState.From(lo.Rgb), darkMouse) && Equals(KeyboardLightingState.From(lo.Rgb, 3), darkKb)
            && Effects.Evaluate(lo.Rgb.Effect, new EffectContext { ColorA = Rgb.FromInt(lo.Rgb.ColorA), Brightness = lo.Rgb.Brightness }, 0.5) == Rgb.Black
            && RamSink.OnboardFor(new RgbConfig { RamOnboardEffects = true, Effect = lo.Rgb.Effect, ColorA = lo.Rgb.ColorA }) is { mode: EneDramController.ModeOff };
        Check("1.12: applying during Lights out lights nothing (mouse, keyboard, streamed and RAM onboard settings stay dark)", stillDark);
        Check("1.12: applying during Lights out changes the look restored afterwards", lo.Rgb.LightsOutRestoreEffect == EffectKind.Static && LightingLooks.Matches(lo, LightingLooks.Starters[3]));
        LightsOut.Toggle(lo);
        Check("1.12: Lights out off then shows the applied look", lo.Rgb.Effect == EffectKind.Static && lo.Rgb.ColorA == 0xB4CAD0 && Math.Abs(lo.Rgb.Brightness - 0.30) < 1e-9);
        var loCap = ConfigStore.Parse(ConfigStore.ToJson(cfg))!; loCap.Rgb.Enabled = true; loCap.Rgb.Effect = EffectKind.GradientWave; LightsOut.Toggle(loCap);
        Check("1.12: saving during Lights out saves the look that comes back, not Stealth", LightingLooks.Capture(loCap, "x")?.Effect == EffectKind.GradientWave);
        Check("1.12: Stealth itself is not saved as a look", LightingLooks.SaveCurrent(new AppConfig { Rgb = { Effect = EffectKind.Stealth, Enabled = false } }, "dark") != null);

        // names, rename, delete, edited starter saved as custom
        Check("1.12: names are validated (empty, starter name, duplicate, too long)",
            LightingLooks.NameProblem(cfg, " ") != null && LightingLooks.NameProblem(cfg, "teal tide") != null && LightingLooks.NameProblem(cfg, "evening") != null
            && LightingLooks.NameProblem(cfg, new string('x', 41)) != null && LightingLooks.NameProblem(cfg, "Night") == null);
        Check("1.12: rename and delete affect only saved looks", LightingLooks.Rename(cfg, "Evening", "Dusk") == null && cfg.Looks[0].Name == "Dusk"
            && LightingLooks.Rename(cfg, LightingLooks.TealTide, "Mine") != null && !LightingLooks.Delete(cfg, LightingLooks.TealTide) && LightingLooks.Starters.Count == 21);   // 1.15: 12 + 4 rain starters; 1.20: + 5 (fire, radar, typing)
        LightingLooks.Apply(cfg, LightingLooks.Starters[0]);
        cfg.Rgb.ColorA = 0x5E9A95;   // edited starter
        Check("1.12: an edited starter can be saved as a custom look; the starter is unchanged",
            LightingLooks.SaveCurrent(cfg, "Forest Mist (mine)") == null && LightingLooks.Find(cfg, "Forest Mist (mine)")?.ColorA == 0x5E9A95 && LightingLooks.Starters[0].ColorA == 0x6EAAA5);
        Check("1.12: delete removes the saved look", LightingLooks.Delete(cfg, "Dusk") && LightingLooks.Find(cfg, "Dusk") == null);
        // 1.13: a saved look named like a starter is kept under a "(saved)" name instead of being dropped
        var junk = LightingLooks.Normalize(new List<LightingLook> { new() { Name = "a", Effect = EffectKind.Stealth }, new() { Name = LightingLooks.TealTide }, new() { Name = "b", Brightness = double.NaN }, new() { Name = "B" }, new() { Name = "" } });
        Check("1.12: loaded looks are repaired and bounded (no Stealth or duplicates; 1.13: a starter-name clash is renamed, not dropped)",
            junk.Count == 2 && junk[0].Name == LightingLooks.TealTide + " (saved)" && junk[1].Name == "b" && junk[1].Brightness == 1);

        // the shared apply path (Lighting page and tray both call AppServices.ApplyLook)
        var shared = UiFixture.LoadConfig(null); shared.Rgb.Enabled = false;
        using (var svc = new AppServices(shared))
        {
            svc.ApplyLook(LightingLooks.Starters[1], "self-test");
            Check("1.12: the shared apply path applies the look and keeps output off", !shared.Rgb.Enabled && LightingLooks.Matches(shared, LightingLooks.Starters[1]));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    static void ArrangementTests()
    {
        var arr = new DeviceArrangement();
        var id = new LayoutTransform(arr, 1);
        var strip = DLedSink.PositionsFor(27);
        Check("1.12: not arranged: effect positions are exactly the 1.11 ones", !id.Active && ReferenceEquals(id.MapRole(DeviceRole.Strip, strip), strip) && ReferenceEquals(LayoutTransform.Identity.MapRole(DeviceRole.Ram, strip), strip));

        arr.Set(DeviceRole.Strip, new DevicePlacement { X = 800, Y = 100 });
        var moved = new LayoutTransform(arr, 2).MapRole(DeviceRole.Strip, strip);
        bool spread = Enumerable.Range(1, strip.Length - 1).All(i => Math.Abs((moved[i] - moved[i - 1]) - (strip[i] - strip[i - 1])) < 1e-12);
        Check("1.12: moving a group keeps its own LED spacing and chain order (nothing inside it is re-mapped)", spread && Math.Abs((moved.Min() + moved.Max()) / 2 - 0.8 * LayoutTransform.PhaseScale) < 1e-9);
        var ctx = new EffectContext { Time = 1.7, Speed = 0.4, Brightness = 0.8, ColorA = new Rgb(200, 30, 90), ColorB = new Rgb(10, 220, 120), ThermalC = 55 };
        bool same(EffectKind k) => Enumerable.Range(0, strip.Length).All(i => Effects.Evaluate(k, ctx, strip[i]) == Effects.Evaluate(k, ctx, moved[i]));
        Check("1.12: Static, Breathing, Thermal and Stealth do not change when a device moves", same(EffectKind.Static) && same(EffectKind.Breathing) && same(EffectKind.ThermalReactive) && same(EffectKind.Stealth));
        Check("1.12: Gradient Wave follows the arrangement", !same(EffectKind.GradientWave));

        var keys = KeyboardSink.LivePositions;
        var a0 = new DeviceArrangement(); a0.Set(DeviceRole.Keyboard, new DevicePlacement { X = 500, Y = 200 });
        var a180 = new DeviceArrangement(); a180.Set(DeviceRole.Keyboard, new DevicePlacement { X = 500, Y = 200, Rotation = 180 });
        var a90 = new DeviceArrangement(); a90.Set(DeviceRole.Keyboard, new DevicePlacement { X = 500, Y = 200, Rotation = 90 });
        var k0 = new LayoutTransform(a0, 3).MapRole(DeviceRole.Keyboard, keys); var k180 = new LayoutTransform(a180, 4).MapRole(DeviceRole.Keyboard, keys); var k90 = new LayoutTransform(a90, 5).MapRole(DeviceRole.Keyboard, keys);
        int esc = Array.FindIndex(ILovbeeKeyMap.Keys, k => k.Name == "Esc"), right = Array.FindIndex(ILovbeeKeyMap.Keys, k => k.Name == "Right");
        double target = 0.5 * LayoutTransform.PhaseScale;
        Check("1.12: keyboard keeps its verified key spread when moved (unturned)", Enumerable.Range(0, keys.Length).All(i => Math.Abs(k0[i] - (target + KeyboardSink.WaveSpan * (ILovbeeKeyMap.UnitX[i] - 0.5))) < 1e-9));
        Check("1.12: turning the keyboard 180° reverses which keys lead the wave", k0[esc] < k0[right] && k180[esc] > k180[right]);
        Check("1.12: a quarter turn spreads the keys by their rows (narrower than across)", k90.Max() - k90.Min() < (k0.Max() - k0.Min()) * 0.6);

        // persistence, repair, reset
        var saved = new AppConfig(); saved.Arrangement.Set(DeviceRole.Keyboard, new DevicePlacement { X = 420, Y = 200, Rotation = 270 });
        saved.Arrangement.Devices["Speaker"] = new DevicePlacement { X = 1, Y = 1 };
        var json = ConfigStore.ToJson(saved);
        var back = ConfigStore.Parse(json)!;
        Check("1.12: arrangement survives save and load; unknown groups are dropped", back.Arrangement.Arranged && back.Arrangement.Get(DeviceRole.Keyboard).Equals(new DevicePlacement { X = 420, Y = 200, Rotation = 270 }) && !back.Arrangement.Devices.ContainsKey("Speaker"));
        var bad = ConfigStore.Parse("{\"Arrangement\":{\"Arranged\":true,\"Devices\":{\"Strip\":{\"X\":-5000,\"Y\":99999,\"Rotation\":90},\"Mouse\":{\"X\":500,\"Y\":200,\"Rotation\":44}}}}")!;
        var bs = bad.Arrangement.Get(DeviceRole.Strip); var bm = bad.Arrangement.Get(DeviceRole.Mouse);
        Check("1.12: invalid placements are clamped onto the canvas; only the keyboard and mouse turn; turns snap to quarters",
            bs.Rotation == 0 && bs.X >= 65 && bs.Y <= DeviceArrangement.CanvasHeight && bm.Rotation == 0, $"(strip {bs.X},{bs.Y} r{bs.Rotation}; mouse r{bm.Rotation})");
        Check("1.12: placements keep the whole drawing inside the canvas", DeviceArrangement.Groups.All(g =>
        {
            var c = DeviceArrangement.Clamp(g, new DevicePlacement { X = 99999, Y = -99999, Rotation = 90 });
            var (w, h) = DeviceArrangement.RotatedSize(g, c.Rotation);
            return c.X + w / 2 <= DeviceArrangement.CanvasWidth + 1e-9 && c.Y - h / 2 >= -1e-9;
        }));
        var reset = back.Arrangement.Clone(); reset.Reset();
        Check("1.12: reset returns to the default row and the fixed effect order", !reset.Arranged && reset.Devices.Count == 0 && !new LayoutTransform(reset, 9).Active);
        Check("1.12: an arrangement is not part of a look", !JsonSerializer.Serialize(LightingLooks.Starters[0]).Contains("Arrangement"));

        // unsupported devices say so
        var cfg = new AppConfig { KeyboardVerified = true, KeyboardLiveVerified = false };
        Check("1.12: the mouse's position is labelled preview only", LayoutTransform.Participation(DeviceRole.Mouse, cfg).StartsWith("Preview only"));
        Check("1.12: a keyboard without verified live frames is labelled preview only", LayoutTransform.Participation(DeviceRole.Keyboard, cfg).StartsWith("Preview only"));
        cfg.Rgb.RamOnboardEffects = true;
        Check("1.12: memory in Onboard mode is labelled preview only for the wave", LayoutTransform.Participation(DeviceRole.Ram, cfg).Contains("preview only"));
        Check("1.12: memory is labelled as detection order, not slot order", LayoutTransform.Participation(DeviceRole.Ram, new AppConfig()).Contains("not a verified slot order"));
        Check("1.12: the case front stays one D_LED group (fans and logo)", LayoutTransform.Participation(DeviceRole.Strip, cfg).Contains("one D_LED chain"));

        // commit: engine layout replaced as a whole, config saved once (read-only here: counted, not written)
        using var svc = new AppServices(new AppConfig());
        long v0 = svc.Rgb.Layout.Version, saves0 = ConfigStore.SkippedSaves;
        var edit = svc.Config.Arrangement.Clone(); edit.Set(DeviceRole.Board, new DevicePlacement { X = 300, Y = 300 });
        svc.CommitArrangement(edit);
        Check("1.12: committing an edit replaces the engine layout once and saves once", svc.Rgb.Layout.Version == v0 + 1 && svc.Rgb.Layout.Active && ConfigStore.SkippedSaves == saves0 + 1 && svc.Config.Arrangement.Arranged);
    }

    // ---------------------------------------------------------------------------------------------------------
    static void WindowPlacementTests()
    {
        var one = new List<MonitorArea> { new(0, 0, 2560, 1400, 96) };
        var saved = new UiState { Left = 300, Top = 120, Width = 1600, Height = 960, Dpi = 96, HasBounds = true };
        Check("1.12: a placement on a present monitor is restored unchanged", WindowPlacementKeeper.Fit(saved, one, 1180, 740) == (300, 120, 1600, 960));
        var gone = new UiState { Left = 3000, Top = 200, Width = 1400, Height = 900, Dpi = 96, HasBounds = true };
        var f = WindowPlacementKeeper.Fit(gone, one, 1180, 740)!.Value;
        Check("1.12: a window on a monitor that is gone is moved fully onto a present one", f.Left >= 0 && f.Left + f.Width <= 2560 && f.Top >= 0 && f.Top + f.Height <= 1400, $"({f})");
        var hiDpi = new List<MonitorArea> { new(0, 0, 3840, 2100, 144) };
        var g = WindowPlacementKeeper.Fit(saved, hiDpi, 1180, 740)!.Value;
        Check("1.12: a DPI change keeps the window's logical size (1600 x 960 at 96 DPI -> 2400 x 1440 at 144)", g.Width == 2400 && g.Height == 1440, $"({g})");
        var small = new List<MonitorArea> { new(0, 0, 1280, 680, 96) };
        var h = WindowPlacementKeeper.Fit(saved, small, 1180, 740)!.Value;
        Check("1.12: a window larger than the work area is clamped into it", h.Width <= 1280 && h.Height <= 680 && h.Left >= 0 && h.Top >= 0, $"({h})");
        var half = new UiState { Left = 2400, Top = 1300, Width = 1600, Height = 960, Dpi = 96, HasBounds = true };
        var j = WindowPlacementKeeper.Fit(half, one, 1180, 740)!.Value;
        Check("1.12: a window partly off screen is pulled fully onto it", j.Left + j.Width <= 2560 && j.Top + j.Height <= 1400);
        var two = new List<MonitorArea> { new(0, 0, 1920, 1040, 96), new(-2560, -200, 0, 1240, 144) };
        var left = new UiState { Left = -2000, Top = 100, Width = 1600, Height = 960, Dpi = 144, HasBounds = true };
        Check("1.12: a placement on a second monitor (other DPI) stays there", WindowPlacementKeeper.Fit(left, two, 1180, 740)!.Value.Left < 0);
        Check("1.12: with no monitor information nothing is changed", WindowPlacementKeeper.Fit(saved, new List<MonitorArea>(), 1180, 740) == null);
        Check("1.12: a corrupt DPI value is treated as 96", WindowPlacementKeeper.Fit(new UiState { Left = 0, Top = 0, Width = 1600, Height = 960, Dpi = -3, HasBounds = true }, one, 1180, 740)!.Value.Width == 1600);
    }
}
