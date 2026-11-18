using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>Live end-to-end verification of the lighting engine and the fan engine against the real hardware.</summary>
public static class LiveTests
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Log.Info($"PASS  {name} {detail}"); }
        else { _fail++; Log.Error($"FAIL  {name} {detail}"); }
    }

    // ---------------------------------------------------------------------------------------------------------
    public static int RunLighting()
    {
        using var app = new AppServices(new AppConfig());
        app.InitializeHardware();
        app.InitializeLighting(allowRamRemap: true);
        app.Hub.Start();
        app.Rgb.Start();

        var rgb = app.Config.Rgb;
        rgb.Enabled = true; rgb.Brightness = 1; rgb.Speed = 0.6; rgb.ColorA = 0xFF0000; rgb.ColorB = 0x0040FF;
        rgb.Motherboard = rgb.Ram = rgb.Mouse = true;

        long smbusErr0 = app.Smbus?.ErrorCount ?? 0;
        var mb = app.Rgb.Sinks.OfType<MotherboardSink>().FirstOrDefault();
        var ram = app.Rgb.Sinks.OfType<RamSink>().FirstOrDefault();
        var mouse = app.Rgb.Sinks.OfType<MouseSink>().FirstOrDefault();
        var dled = app.Rgb.Sinks.OfType<DLedSink>().FirstOrDefault();
        rgb.DLedStrip = true;
        long hidErr0 = mb?.Device?.WriteErrors ?? 0;
        if (dled != null) Log.Info($"D_LED sink: {dled.LedCount} LEDs, {rgb.StripOrder} order, {dled.CurrentStatus()}");
        long mouseApplies0 = mouse?.Controller.Applies ?? 0;
        var mouseSettings = new List<MouseLightingState?>();

        // The mouse stays included throughout: animated phases must not stream to it (one onboard setting per change).
        var phases = new (EffectKind kind, int seconds, bool mouseOn)[]
        {
            (EffectKind.Static, 4, true),
            (EffectKind.Breathing, 6, true),
            (EffectKind.GradientWave, 6, true),
            (EffectKind.ThermalReactive, 5, true),
            (EffectKind.Stealth, 3, true),
            (EffectKind.Static, 3, true)
        };

        var fpsSamples = new List<double>();
        foreach (var (kind, seconds, mouseOn) in phases)
        {
            rgb.Effect = kind; rgb.Mouse = mouseOn;
            mouseSettings.Add(MouseLightingState.From(rgb));
            long mbBefore = mb?.FramesSent ?? 0, ramBefore = ram?.FramesSent ?? 0, mouseBefore = mouse?.Controller.Applies ?? 0, dledBefore = dled?.FramesSent ?? 0;
            Thread.Sleep(seconds * 1000);
            double dledRate = ((dled?.FramesSent ?? 0) - dledBefore) / (double)seconds;
            double mbRate = ((mb?.FramesSent ?? 0) - mbBefore) / (double)seconds;
            double ramRate = ((ram?.FramesSent ?? 0) - ramBefore) / (double)seconds;
            double mouseCount = (mouse?.Controller.Applies ?? 0) - mouseBefore;
            fpsSamples.Add(app.Rgb.Fps);
            Log.Info($"phase {kind,-16} engine {app.Rgb.Fps,5:0.0} FPS | D_LED {dledRate,5:0.0}/s | motherboard {mbRate,5:0.0}/s | RAM {ramRate,5:0.0}/s | mouse {mouseCount} writes");
            if (kind is EffectKind.Breathing or EffectKind.GradientWave && dled != null && dled.Available)
                Check($"D_LED strip delivers {kind} frames", dledRate >= 15, $"({dledRate:0.0}/s, target up to 60)");
            if (kind == EffectKind.Breathing && mb != null) Check("motherboard rate within safe cap", mbRate <= 31, $"({mbRate:0.0}/s, cap 30)");
            if (kind == EffectKind.Breathing && ram != null && ram.Available) Check("RAM sink delivers animation", ramRate >= 1, $"({ramRate:0.0}/s)");
        }

        Check("render engine near 60 FPS", fpsSamples.Count > 0 && fpsSamples.Min() >= 50 && fpsSamples.Max() <= 70, $"(min {fpsSamples.Min():0.0}, max {fpsSamples.Max():0.0})");

        // hand the lights back to the user's red scheme
        rgb.Effect = EffectKind.Static; rgb.Enabled = false;
        Thread.Sleep(2500);

        if (app.Smbus != null)
        {
            long errDelta = app.Smbus.ErrorCount - smbusErr0;
            Check("zero SMBus errors while animating RAM", errDelta == 0, $"({app.Smbus.TransactionCount} transactions, {errDelta} errors)");
        }
        if (mb?.Device != null)
        {
            Check("zero HID write errors on motherboard and D_LED strip", mb.Device.WriteErrors - hidErr0 == 0, $"({mb.Device.Writes} writes, strip packets sent {mb.Device.StripPacketsSent}, unchanged-skipped {mb.Device.StripPacketsSkipped})");
        }
        if (dled != null) Check("D_LED sink had no errors", dled.Errors == 0, $"(errors {dled.Errors})");
        if (mouse != null && mouse.Controller.Connected)
        {
            long applies = mouse.Controller.Applies - mouseApplies0;
            int changes = 0;
            for (int i = 0; i < mouseSettings.Count; i++) if (i == 0 || !Equals(mouseSettings[i], mouseSettings[i - 1])) changes++;
            Check("mouse: at most one write per change of its effective setting, none per frame", applies <= changes,
                $"({applies} writes for {changes} setting changes over {phases.Sum(p => p.seconds)} s of 60 FPS frames; last apply {mouse.Controller.LastApplyMs:0} ms)");
            Check("mouse lighting had no failures", mouse.Controller.Failures == 0, $"(failures {mouse.Controller.Failures}, {mouse.Controller.LastError})");
        }
        else Log.Warn("mouse not connected: mouse checks skipped");
        foreach (var s in app.Rgb.Sinks) Log.Info($"sink {s.Name}: {s.StatsText}");
        Log.Info($"lighting test finished: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------------------
    /// <summary>
    /// Bounded live mouse check with the vendor's lighting reports: connects, then offers the same static red setting at
    /// 60 FPS for the given time. Expected: exactly one lighting application (3 feature reports), timing measured.
    /// Leaves the mouse static red (the colour it was left in by earlier phases).
    /// </summary>
    public static int RunMouse(int seconds)
    {
        var transport = new GloriousTransport();
        using var ctl = new MouseLightingController(transport, listenForDeviceChanges: true);
        var red = MouseLightingState.From(new RgbConfig { Enabled = true, Mouse = true, Effect = EffectKind.Static, ColorA = 0xFF0000, Brightness = 1 })!;
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!ctl.Connected && DateTime.UtcNow < end) Thread.Sleep(50);
        if (!ctl.Connected) { Log.Error("mouse test: mouse not found or not openable: " + ctl.LinkStatus); return 3; }
        Log.Info($"mouse test: link {ctl.LinkStatus}; preferred path {transport.PreferredPath()}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long posts = 0;
        double worstPost = 0;
        while (sw.Elapsed.TotalSeconds < Math.Clamp(seconds, 3, 120))
        {
            var one = System.Diagnostics.Stopwatch.StartNew();
            ctl.SetDesired(red with { });
            worstPost = Math.Max(worstPost, one.Elapsed.TotalMilliseconds);
            posts++;
            Thread.Sleep(16);
        }
        Thread.Sleep(1000);
        var dev = transport.LastDevice;
        Log.Info($"mouse test: {posts} posts of an unchanged setting in {sw.Elapsed.TotalSeconds:0} s -> {ctl.Applies} application(s), " +
                 $"{dev?.ReportsSent ?? 0} feature reports; apply {ctl.LastApplyMs:0} ms (incl. 3 x 30 ms vendor gaps), " +
                 $"slowest single SetFeature {dev?.MaxReportMs ?? 0:0.0} ms; worst post {worstPost:0.00} ms; ignored device changes {ctl.IgnoredDeviceChanges}");
        Check("one lighting application for an unchanged setting offered at ~60 FPS", ctl.Applies == 1, $"({ctl.Applies})");
        Check("exactly three feature reports were sent", (dev?.ReportsSent ?? -1) == 3, $"({dev?.ReportsSent})");
        Check("no failures", ctl.Failures == 0, $"({ctl.LastError})");
        Check("posting never blocked the caller", worstPost < 5, $"({worstPost:0.00} ms)");
        Log.Info($"mouse test finished: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------------------
    /// <summary>
    /// Phase 8 ENE block-size probe: for each RAM module, write dim test patterns to its direct colour registers with
    /// 3-byte blocks (current method), 6-byte blocks, and one block for the whole module, plus one select followed by
    /// sequential 3-byte blocks; read every byte back. Leaves every module on steady red. Never touches flash.
    /// </summary>
    public static int RunEneProbe()
    {
        var bus = SmbusI801.TryOpen(out var err);
        if (bus == null) { Log.Error("SMBus unavailable: " + err); return 3; }
        using (bus)
        {
            var ene = new EneDramController(bus);
            ene.Detect(allowRemap: false);
            foreach (var d in ene.Dimms.Where(x => x.Supported))
            {
                int full = d.LedCount * 3;
                foreach (var (chunk, reselect, label) in new[] { (3, true, "3-byte blocks (current)"), (6, true, "6-byte blocks"), (full, true, $"one {full}-byte block"), (3, false, "one select, sequential 3-byte blocks") })
                {
                    bool ok = ene.ProbeBlockWrite(d, chunk, reselect, (byte)(chunk * 3 + (reselect ? 1 : 2)), out var detail);
                    Check($"0x{d.Address:X2} {d.Version} ({d.LedCount} LEDs): {label}", ok, "(" + detail + ")");
                }
                ene.SetStaticLook(d, new Rgb(255, 0, 0));
            }
            Log.Info($"ENE probe: SMBus transactions {bus.TransactionCount}, errors {bus.ErrorCount}");
        }
        Log.Info($"ENE probe finished: {_pass} passed, {_fail} failed (a FAIL means that write method is not usable on this module)");
        return 0;
    }

    // ---------------------------------------------------------------------------------------------------------
    /// <summary>Real identify pulse on a motherboard header: tachometer must climb during the pulse and the header must be released afterwards.</summary>
    public static int RunIdentify(int headerIndex)
    {
        using var app = new AppServices(new AppConfig());
        app.InitializeHardware();
        var ch = app.Hub.Engine.Channels.Where(c => c is LhmFanChannel).ElementAtOrDefault(headerIndex) as LhmFanChannel;
        if (ch == null) { Log.Error("no such motherboard header"); return 3; }
        app.Hub.Start();   // telemetry thread drives the engine tick; fan control itself stays off
        double? Rpm() => app.Hub.Latest.Fans.FirstOrDefault(f => f.Id == ch.FanSensorId)?.Rpm;

        Log.Info($"identify test on {ch.Name} (tach sensor {(ch.FanSensorId ?? "none")})");
        for (int i = 0; i < 3; i++) { Thread.Sleep(1000); Log.Info($"firmware rpm {Rpm()?.ToString("0") ?? "n/a"}"); }
        // A header the BIOS already runs at full speed cannot be heard to change, so start from a lower duty,
        // like a quieter case-fan header would be, and let the IT8689E's PWM smoothing settle first.
        Log.Info("settling the header at 40% so the pulse has something to change");
        ch.SetPercent(40);
        for (int i = 0; i < 9; i++) { Thread.Sleep(1000); Log.Info($"at 40%   rpm {Rpm()?.ToString("0") ?? "n/a"}"); }
        var before = Rpm();
        app.Hub.Engine.StartIdentify(ch.Id, 5);
        double peak = 0, lowest = double.MaxValue, lastDuring = 0; bool sawIdentify = false;
        for (int i = 0; i < 6; i++)
        {
            Thread.Sleep(1000);
            var r = Rpm(); if (r is double d) { peak = Math.Max(peak, d); lowest = Math.Min(lowest, d); lastDuring = d; }
            if (app.Hub.Engine.Status.TryGetValue(ch.Id, out var st) && st.State.Contains("identify")) sawIdentify = true;
            Log.Info($"pulse   rpm {r?.ToString("0") ?? "n/a"}   remaining {app.Hub.Engine.IdentifyRemaining(ch.Id):0.0}s");
        }
        for (int i = 0; i < 8; i++) { Thread.Sleep(1000); Log.Info($"after   rpm {Rpm()?.ToString("0") ?? "n/a"}"); }
        Check("engine reported the identify pulse", sawIdentify);
        if (ch.FanSensorId != null && before is double b && b > 0)
            Check("tachometer reversed and climbed during the pulse (the IT8689E smooths PWM, so the rise builds slowly)", lastDuring - lowest >= 100, $"(start {b:0}, lowest {lowest:0}, end of pulse {lastDuring:0})");
        else Log.Info("this header has no tachometer signal: the pulse can only be judged by ear or eye");
        Check("pulse finished and the header was released", app.Hub.Engine.IdentifyRemaining(ch.Id) == 0 && ControlRegistry.Load().Count == 0);
        Log.Info($"identify test finished: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>Free-running D_LED benchmark: how many full 27-LED frames per second does the IT5701 HID pipe accept with nothing else running?</summary>
    public static int RunHidBench(int frames = 240)
    {
        using var dev = new Fusion2UsbDevice();
        if (!dev.Open(out var err)) { Log.Error("bench: " + err); return 3; }
        dev.InitializeLighting();
        var cfg = new RgbConfig();
        dev.PrepareStrip(cfg.DLedCount);
        var a = Enumerable.Repeat(new Rgb(0, 40, 0), cfg.DLedCount).ToArray();
        var b = Enumerable.Repeat(new Rgb(0, 0, 40), cfg.DLedCount).ToArray();
        var lat = new List<double>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            var t0 = sw.Elapsed.TotalMilliseconds;
            dev.SetStripColors(i % 2 == 0 ? a : b, cfg.StripOrder);
            lat.Add(sw.Elapsed.TotalMilliseconds - t0);
        }
        double total = sw.Elapsed.TotalSeconds;
        lat.Sort();
        Log.Info($"D_LED free-run: {frames} frames ({dev.StripPacketsSent} packets) in {total:0.00}s = {frames / total:0.0} frames/s; per-frame latency median {lat[lat.Count / 2]:0.0} ms, p95 {lat[(int)(lat.Count * 0.95)]:0.0} ms, max {lat[^1]:0.0} ms; HID errors {dev.WriteErrors}");
        dev.SetStripColors(Enumerable.Repeat(new Rgb(255, 0, 0), cfg.DLedCount).ToArray(), cfg.StripOrder);
        return dev.WriteErrors == 0 ? 0 : 1;
    }

    public static int RunEngine(int seconds)
    {
        using var app = new AppServices(new AppConfig());
        app.InitializeHardware();
        var gpuChannels = app.Hub.Engine.Channels.Where(c => c is GpuFanChannel).ToList();
        if (gpuChannels.Count == 0) { Log.Error("no GPU fan channels"); return 3; }

        FanPresets.ApplyPreset(app.Config, "Balanced", gpuChannels);
        app.Config.FanControlEnabled = true;
        app.Hub.Start();

        var gpu = app.Hub.Gpu!;
        var origPolicy = gpu.ReadFan(0).Policy;
        Log.Info($"engine test for {seconds}s on {gpuChannels.Count} GPU fan(s) (original policy {origPolicy})");

        var applied = new List<double>();
        var rpm = new List<(double duty, double rpm)>();
        double maxStep = 0, prev = -1;
        var end = DateTime.Now.AddSeconds(seconds);
        int t = 0;
        while (DateTime.Now < end)
        {
            Thread.Sleep(2000); t += 2;
            var s = app.Hub.Latest;
            foreach (var ch in gpuChannels)
            {
                app.Hub.Engine.Status.TryGetValue(ch.Id, out var st);
                var fan = s.Fans.FirstOrDefault(f => f.Id == ch.Id);
                Log.Info($"t={t,3}s gpu {s.GpuTempC,4:0.#} C  {ch.Name}: target {st?.TargetPercent,4:0}%  applied {st?.AppliedPercent,4:0}%  {fan?.Rpm,5:0} RPM  state '{st?.State}'");
                if (ch == gpuChannels[0] && st?.AppliedPercent is double a)
                {
                    applied.Add(a);
                    if (prev >= 0) maxStep = Math.Max(maxStep, Math.Abs(a - prev));
                    prev = a;
                    if (fan?.Rpm is double r) rpm.Add((a, r));
                }
            }
            // second half: exercise manual override on fan 1 and return to curve
            if (t == seconds / 2 && gpuChannels.Count > 1)
            {
                var cc = app.Config.Channels.First(c => c.Id == gpuChannels[1].Id);
                lock (app.Config.Sync) { cc.Mode = ChannelMode.Manual; cc.ManualPercent = 60; }
                Log.Info("-> GPU Fan 2 set to Manual 60%");
            }
            if (t == seconds * 3 / 4 && gpuChannels.Count > 1)
            {
                var cc = app.Config.Channels.First(c => c.Id == gpuChannels[1].Id);
                lock (app.Config.Sync) { cc.Mode = ChannelMode.Curve; }
                Log.Info("-> GPU Fan 2 back to Curve");
            }
        }

        Check("duty applied on the GPU fan", applied.Count > 0 && applied.All(a => a >= gpu.MinFanPercent), $"(values {string.Join(",", applied.Select(a => a.ToString("0")))})");
        Check("no erratic revving (largest step between samples <= 8 points)", maxStep <= 8, $"(max step {maxStep:0.#})");
        var f2 = app.Hub.Engine.Status.GetValueOrDefault(gpuChannels.Count > 1 ? gpuChannels[1].Id : "");

        // release everything and verify the hardware really went back to firmware control
        app.Config.FanControlEnabled = false;
        Thread.Sleep(3000);
        var after = gpu.Read();
        foreach (var f in after.Fans) Log.Info($"after release: fan {f.Index} policy {f.Policy} (0 = VBIOS auto) target {f.TargetPct}% speed {f.SpeedPct}% rpm {f.Rpm}");
        Check("fans returned to VBIOS automatic control after release", after.Fans.All(f => f.Policy == 0), $"(policies {string.Join(",", after.Fans.Select(f => f.Policy))})");
        Check("crash-recovery registry is empty after release", ControlRegistry.Load().Count == 0);
        Log.Info($"engine test finished: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>Read-only: the keyboard's HID interfaces, the collection HwSuite would use, and the vendor profile values it would preserve. Sends nothing.</summary>
    public static int RunKeyboardProbe()
    {
        var all = HwSuite.Lighting.Devices.ILovbeeKeyboardDevice.Enumerate();
        Log.Info($"keyboard 258A:013C: {all.Count} HID interfaces");
        foreach (var i in all) Log.Info("  " + i);
        var pick = HwSuite.Lighting.Devices.ILovbeeKeyboardDevice.Pick(all);
        Log.Info(pick == null ? "no FF00:0001 collection with 65-byte feature reports" : "lighting collection: " + pick.Path);
        var prof = HwSuite.Lighting.Devices.ILovbeeProfile.Read(null, null, out var detail);
        Log.Info($"vendor profile: {detail}" + (prof != null ? $" -> sleep byte {prof.Sleep}, saved speed {prof.Speed} ({prof.Source})" : " -> keyboard writes refused"));
        var red = HwSuite.Lighting.Devices.KeyboardLightingState.TestRed(HwSuite.Lighting.Devices.KeyboardLightingState.ModeSteady, prof?.Speed ?? 4);
        var frames = HwSuite.Lighting.Devices.ILovbeeKeyboardDevice.Frame(HwSuite.Lighting.Devices.ILovbeeKeyboardDevice.BuildLedPayload(red, prof?.Sleep ?? 0));
        Log.Info("test write would send (NOT sent): " + string.Join(" | ", frames.Select(f => Convert.ToHexString(f, 0, 16) + "...")));
        bool vendorRunning = System.Diagnostics.Process.GetProcessesByName("DeviceDriver").Length > 0;
        Log.Info("vendor software (DeviceDriver.exe) running: " + vendorRunning);
        return pick != null && prof != null ? 0 : 1;
    }

    /// <summary>
    /// Live Lights out check (bounded, about 40 s; keyboard excluded unless verified). Streamed RAM: the ENE direct-colour
    /// registers are read back and must all be 0. Onboard RAM: the mode register must read 0. Board and strip: writes after
    /// the toggle must succeed. Mouse: the off setting must be applied. Ends like a normal exit (steady primary colour).
    /// </summary>
    public static int RunLightsOutTest()
    {
        var owner = ConfigStore.Load();   // the owner's colours and inclusions; nothing is saved
        var cfg = new AppConfig { AutoStopGigabyte = false, KeyboardVerified = owner.KeyboardVerified };
        cfg.Rgb.ColorA = owner.Rgb.ColorA; cfg.Rgb.ColorB = owner.Rgb.ColorB; cfg.Rgb.Brightness = 0.7; cfg.Rgb.Speed = 0.6;
        cfg.Rgb.Keyboard = owner.KeyboardVerified;
        int fails = 0;
        void Check(string n, bool ok, string d = "") { if (!ok) fails++; Log.Info($"{(ok ? "PASS" : "FAIL")}  lights-out live: {n} {d}"); }
        using var app = new AppServices(cfg);
        app.InitializeHardware();
        app.InitializeLighting(allowRamRemap: false);
        app.Hub.Start(); app.Rgb.Start();
        Thread.Sleep(2000);
        var ram = app.Rgb.Sinks.OfType<RamSink>().FirstOrDefault();
        var board = app.Rgb.Sinks.OfType<MotherboardSink>().FirstOrDefault();
        var strip = app.Rgb.Sinks.OfType<DLedSink>().FirstOrDefault();
        var mouse = app.Rgb.Sinks.OfType<MouseSink>().FirstOrDefault();
        try
        {
            foreach (bool onboard in new[] { false, true })
            {
                cfg.Rgb.RamOnboardEffects = onboard;
                cfg.Rgb.Effect = onboard ? EffectKind.Breathing : EffectKind.GradientWave;
                cfg.Rgb.Enabled = true;
                Thread.Sleep(5000);
                long b0 = board?.FramesSent ?? 0, s0 = strip?.FramesSent ?? 0;
                bool off = LightsOut.Toggle(cfg);
                Check($"[{(onboard ? "onboard RAM" : "streamed RAM")}] Lights out switched to Stealth", off && cfg.Rgb.Effect == EffectKind.Stealth);
                Thread.Sleep(4000);
                if (app.Ene != null)
                    foreach (var d in app.Ene.Dimms.Where(x => x.Supported))
                    {
                        var rb = app.Ene.ReadBack(d);
                        if (onboard) Check($"RAM 0x{d.Address:X2}: onboard mode register reads 0 (off)", rb is { } r1 && r1.mode == 0 && r1.directFlag == 0, rb is { } x1 ? $"(direct {x1.directFlag}, mode {x1.mode})" : "(read failed)");
                        else Check($"RAM 0x{d.Address:X2}: every direct colour register reads 0", rb is { } r2 && r2.directFlag == 1 && r2.direct.All(v => v == 0), rb is { } x2 ? $"(direct {x2.directFlag}, max {x2.direct.Max()})" : "(read failed)");
                    }
                if (board?.Available == true) Check("board LEDs: off frame written and accepted", board.FramesSent > b0 && board.Preview.All(c => (c.R | c.G | c.B) == 0), $"({board.FramesSent - b0} writes)");
                if (strip?.Available == true) Check("case strip: off frame written and accepted", strip.FramesSent > s0 && strip.Preview.All(c => (c.R | c.G | c.B) == 0), $"({strip.FramesSent - s0} writes)");
                if (mouse != null) Check("mouse: off setting applied", mouse.Controller.LastApplied?.Effect == MouseLightingState.EffectOff, mouse.Controller.Status);
                bool back = !LightsOut.Toggle(cfg);
                Thread.Sleep(3000);
                Check("Lights out pressed again restores the previous effect", back && cfg.Rgb.Effect == (onboard ? EffectKind.Breathing : EffectKind.GradientWave));
                if (onboard && app.Ene != null)
                    foreach (var d in app.Ene.Dimms.Where(x => x.Supported))
                        Check($"RAM 0x{d.Address:X2}: onboard breathing restored (mode 2)", app.Ene.ReadBack(d) is { } r3 && r3.mode == EneDramController.ModeBreathing);
            }
        }
        finally
        {
            // leave the hardware as the owner's own setting would: Stealth when their lights are out, else a normal stop
            cfg.Rgb.RamOnboardEffects = false;
            cfg.Rgb.Effect = owner.Rgb.Effect; cfg.Rgb.Enabled = owner.Rgb.Enabled;
            Thread.Sleep(3000);
            cfg.Rgb.Enabled = false;
            Thread.Sleep(800);
        }
        Log.Info($"lights-out live test finished: {fails} failed");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// One owner-approved keyboard test write: Steady (mode 16), red, full brightness, sleep byte from the vendor profile.
    /// Goes through the real controller (live ownership check at the write, profile rules, framing). Nothing else is sent.
    /// </summary>
    public static int RunKeyboardTestWrite(byte mode = KeyboardLightingState.ModeSteady, byte brightness = KeyboardLightingState.MaxBrightness)
    {
        string? Owner()
        {
            var p = System.Diagnostics.Process.GetProcessesByName("DeviceDriver");
            bool running = p.Length > 0; foreach (var x in p) x.Dispose();
            return running ? "the iLovbee keyboard software (DeviceDriver.exe) is running" : null;
        }
        var prof = ILovbeeProfile.Read(null, null, out var detail);
        Log.Info("keyboard test: profile: " + detail);
        if (prof == null) { Log.Warn("keyboard test: not sent (" + detail + ")"); return 2; }
        var test = KeyboardLightingState.TestRed(mode, prof.Speed) with { Brightness = Math.Min(brightness, KeyboardLightingState.MaxBrightness) };
        Log.Info($"keyboard test: mode {mode} ({KeyboardLightingState.ModeNames[Math.Min((int)mode, 20)]}), red, brightness {test.Brightness}");
        var frames = ILovbeeKeyboardDevice.Frame(ILovbeeKeyboardDevice.BuildLedPayload(test, prof.Sleep));
        Log.Info("keyboard test: report = " + string.Join(" | ", frames.Select(f => Convert.ToHexString(f, 0, 16) + "...")));
        string? result = null; bool sent = false;
        using var ctl = new KeyboardLightingController(new ILovbeeTransport(), () => false, Owner, listenForDeviceChanges: false);
        ctl.TestFinished += (ok, r) => { sent = ok; result = r; };
        ctl.RequestTestWrite(test);
        var end = DateTime.UtcNow.AddSeconds(10);
        while (result == null && DateTime.UtcNow < end) Thread.Sleep(50);
        Log.Info($"keyboard test: {(sent ? "SENT" : "NOT SENT")}: {result ?? "timed out"} (writes: test {ctl.TestWrites}, sync {ctl.Applies})");
        return sent ? 0 : 1;
    }
}
