using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using System.Xml.Linq;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>Headless startup self-test. Never writes to fans or lighting hardware.</summary>
public static partial class SelfTest
{
    internal sealed class FakeChannel : IFanChannel
    {
        public FakeChannel(string id, FanKind kind, double min = 0) { Id = id; DefaultKind = kind; MinPercent = min; if (kind == FanKind.GpuFan) Res = HwResource.GpuFans; }
        public string Id { get; }
        public string Name => Id;
        public string Backend => "fake";
        public FanKind DefaultKind { get; }
        public bool CanControl => true;
        public double MinPercent { get; }
        public HwResource Res = HwResource.SuperIo;
        public HwResource Resource => Res;
        public double Applied = -1;
        public bool Released;
        public int Writes, Releases, Relinquishes;
        public bool FailWrites, FailRelease, ThrowOnWrite;
        public bool HasTach = true;
        public bool TachSensor = true;
        public double? Rpm = 1000;
        /// <summary>Simulates another program's target (GPU); null = report what HwSuite last applied.</summary>
        public int? ForeignTarget;
        public bool HasTachSignal => HasTach;
        public bool HasTachSensor => TachSensor;
        public double? ReadRpm() => Rpm;
        public double? ReadPercent() => Applied < 0 ? 40 : Applied;
        public bool SetPercent(double p)
        {
            if (ThrowOnWrite) throw new IOException("simulated device failure");
            if (FailWrites) return false;
            Applied = p; Released = false; Writes++; return true;
        }
        public bool ReleaseToAuto() { if (FailRelease) return false; Released = true; Releases++; Applied = -1; return true; }
        public bool RestoreOriginal() => ReleaseToAuto();
        public void Relinquish() { Relinquishes++; }
        public int? ReadTargetPercent() => Res == HwResource.GpuFans ? ForeignTarget ?? (Applied >= 0 ? (int)Math.Round(Applied) : null) : null;
    }

    static int _pass, _fail, _warn;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Log.Info($"PASS  {name} {detail}"); }
        else { _fail++; Log.Error($"FAIL  {name} {detail}"); }
    }

    static void Warn(string text) { _warn++; Log.Warn("WARN  " + text); }

    static void Guarded(string group, Action tests)
    {
        try { tests(); }
        catch (Exception ex) { Check($"{group} tests ran to completion", false, ex.GetType().Name + ": " + ex.Message); }
    }

    internal static TelemetrySnapshot Snap(double? cpu, double? gpu) => new()
    {
        CpuTempC = cpu, GpuTempC = gpu,
        CpuTempStatus = cpu.HasValue ? "ok" : "no valid reading (test)", GpuTempStatus = gpu.HasValue ? "ok" : "NVML read failed (test)"
    };

    internal static ChannelConfig Cfg(FakeChannel ch, FanKind kind, ChannelMode mode, string preset = "Balanced", double? min = null) =>
        new() { Id = ch.Id, Kind = kind, Mode = mode, Curve = FanPresets.CurveFor(preset, kind), VerifiedMinPercent = min, MinEvidence = min.HasValue ? "self-test" : null };

    public static int Run(bool withUi, bool hardware = true, bool injectUiFault = false)
    {
        Log.Info("self-test starting");

        // --- configuration round trip
        var cfg = new AppConfig();
        FanPresets.ApplyPreset(cfg, "Balanced", new[] { new FakeChannel("a", FanKind.CaseFan) });
        var json = ConfigStore.ToJson(cfg);
        var back = ConfigStore.FromJson<AppConfig>(json);
        Check("config JSON round trip", back != null && back.Channels.Count == 1 && back.Channels[0].Curve.Points.Count >= 4);

        // --- curve interpolation and presets
        foreach (var p in FanPresets.Names)
        foreach (FanKind k in Enum.GetValues(typeof(FanKind)))
        {
            var c = FanPresets.CurveFor(p, k);
            bool mono = true; double prev = -1;
            foreach (var pt in c.Points.OrderBy(x => x.Temp)) { if (pt.Percent < prev) mono = false; prev = pt.Percent; }
            Check($"preset {p}/{k} monotonic and reaches 100%", mono && c.Points.Max(x => x.Percent) >= 100);
            if (k is FanKind.Pump or FanKind.Aio) Check($"preset {p}/{k} never below 60%", c.Points.Min(x => x.Percent) >= 60);
        }

        // --- fan engine behaviour on fake channels (identified devices with evidence-backed floors)
        var ecfg = new AppConfig { FanControlEnabled = true };
        using (var engine = new FanEngine(ecfg))
        {
            var fan = new FakeChannel("fan", FanKind.CaseFan);
            var pump = new FakeChannel("pump", FanKind.Pump);
            var unknown = new FakeChannel("unknown", FanKind.Generic);
            var gpu = new FakeChannel("gpu", FanKind.GpuFan, 30);
            foreach (var ch in new[] { fan, pump, unknown, gpu }) engine.Channels.Add(ch);
            ecfg.Channels.Add(Cfg(fan, FanKind.CaseFan, ChannelMode.Curve, min: 20));
            ecfg.Channels.Add(Cfg(pump, FanKind.Pump, ChannelMode.Curve));
            ecfg.Channels.Add(Cfg(unknown, FanKind.Generic, ChannelMode.Curve));
            ecfg.Channels.Add(Cfg(gpu, FanKind.GpuFan, ChannelMode.Curve));

            // cold start
            engine.Tick(Snap(30, 30), 1);
            Check("pump floor holds at 60% when cold", pump.Applied >= 60, $"(applied {pump.Applied})");
            Check("unidentified header is never driven, even when set to Curve", unknown.Writes == 0 && unknown.Applied < 0,
                $"(writes {unknown.Writes}, state '{engine.Status.GetValueOrDefault("unknown")?.State}')");
            Check("GPU fan respects NVML minimum 30%", gpu.Applied >= 30, $"(applied {gpu.Applied})");

            // ramp up is rate limited (20 %/s for Balanced)
            double before = fan.Applied;
            engine.Tick(Snap(80, 80), 1);
            Check("ramp-up limited to configured rate", fan.Applied - before <= 21 && fan.Applied > before, $"({before} -> {fan.Applied})");
            for (int i = 0; i < 6; i++) engine.Tick(Snap(80, 80), 1);
            double peak = fan.Applied;
            Check("duty reaches curve target when hot", peak >= 80, $"(peak {peak})");

            // hysteresis: 2 C below the peak must not lower the duty
            engine.Tick(Snap(78, 78), 1);
            Check("hysteresis holds duty inside the deadband", Math.Abs(fan.Applied - peak) < 1.01, $"({peak} -> {fan.Applied})");

            // large drop lowers duty slowly (3 %/s)
            engine.Tick(Snap(50, 50), 1);
            Check("step-down limited to configured rate", peak - fan.Applied <= 3.01 && fan.Applied < peak, $"({peak} -> {fan.Applied})");

            // emergency override
            engine.Tick(Snap(95, 60), 1);
            Check("thermal emergency forces 100% on driven channels", fan.Applied == 100 && pump.Applied == 100 && engine.Emergency);
            Check("thermal emergency does not take over an unidentified header", unknown.Writes == 0);
            engine.Tick(Snap(60, 60), 1);
            Check("emergency clears after cooling", !engine.Emergency);

            // switching engine off releases everything
            ecfg.FanControlEnabled = false;
            engine.Tick(Snap(40, 40), 1);
            Check("disabling the engine releases channels", fan.Released && pump.Released && gpu.Released);
        }

        // --- Phase 6: AIO defaults and identify pulse
        var cpuFan = new FakeChannel(ChannelDefaults.CpuFanChannelId, FanKind.Generic);
        var (cpuLabel, cpuKind) = ChannelDefaults.For(cpuFan);
        Check("CPU_FAN defaults to the Arctic Liquid Freezer II 240 label and AIO type", cpuLabel == "Arctic Liquid Freezer II 240 (CPU_FAN)" && cpuKind == FanKind.Aio, $"({cpuLabel})");
        var legacy = new ChannelConfig { Id = cpuFan.Id, Label = cpuFan.Name, Kind = FanKind.Generic };
        ChannelDefaults.Upgrade(legacy, cpuFan, "Balanced");
        Check("older saved config is upgraded to the friendly label", legacy.Label == cpuLabel && legacy.Kind == FanKind.Aio);
        var custom = new ChannelConfig { Id = cpuFan.Id, Label = "My pump", Kind = FanKind.Pump };
        ChannelDefaults.Upgrade(custom, cpuFan, "Balanced");
        Check("user-customised label and type are never overwritten", custom.Label == "My pump" && custom.Kind == FanKind.Pump);

        var idCfg = new AppConfig { FanControlEnabled = false };
        using (var ie = new FanEngine(idCfg))
        {
            var hub = new FakeChannel("syshdr", FanKind.Generic);
            ie.Channels.Add(hub);
            ie.StartIdentify("syshdr", 1);
            ie.Tick(Snap(30, 30), 1);
            Check("identify pulse drives the header to 100% even with the engine off", hub.Applied == 100 && ie.IdentifyRemaining("syshdr") > 0);
            Check("identify status is reported", ie.Status.TryGetValue("syshdr", out var ist) && ist.State.Contains("identify"));
            Thread.Sleep(1300);
            ie.Tick(Snap(30, 30), 1);
            Check("identify pulse ends and hands the header back to firmware", hub.Released && ie.IdentifyRemaining("syshdr") == 0);
        }

        // --- Phase 6: D_LED strip packets (layout identical to OpenRGB SetStripColors)
        var strip = Enumerable.Range(0, 27).Select(i => new Rgb((byte)(10 + i), (byte)(100 + i), (byte)(200 + i))).ToArray();
        var grb = Fusion2UsbDevice.BuildStripPackets(strip, ChannelOrder.Grb);
        Check("27 LEDs are split into 2 packets (19 + 8)", grb.Count == 2 && grb[0][4] == 57 && grb[1][4] == 24);
        Check("strip packet header is report 0xCC, HDR_D_LED1_ARGB 0x58, byte offsets 0 and 57",
            grb[0][0] == 0xCC && grb[0][1] == 0x58 && grb[0][2] == 0 && grb[0][3] == 0 && grb[1][2] == 57 && grb[1][3] == 0 && grb.All(p => p.Length == 64));
        Check("GRB order puts G, R, B on the wire", grb[0][5] == 100 && grb[0][6] == 10 && grb[0][7] == 200, $"(first LED bytes {grb[0][5]},{grb[0][6]},{grb[0][7]})");
        var rgbP = Fusion2UsbDevice.BuildStripPackets(strip, ChannelOrder.Rgb);
        Check("RGB order puts R, G, B on the wire", rgbP[0][5] == 10 && rgbP[0][6] == 100 && rgbP[0][7] == 200);
        var bgrP = Fusion2UsbDevice.BuildStripPackets(strip, ChannelOrder.Bgr);
        Check("BGR order puts B, G, R on the wire", bgrP[0][5] == 200 && bgrP[0][6] == 100 && bgrP[0][7] == 10);
        Check("LED 19 starts the second packet", grb[1][5] == 100 + 19 && grb[1][6] == 10 + 19 && grb[1][7] == 200 + 19);
        var big = Fusion2UsbDevice.BuildStripPackets(Enumerable.Repeat(new Rgb(1, 2, 3), 120).ToArray(), ChannelOrder.Grb);
        Check("120 LEDs use 7 packets and a 16-bit byte offset", big.Count == 7 && big[6][2] == (342 & 0xFF) && big[6][3] == (342 >> 8) && big[6][4] == 18, $"(last offset {big[6][2] | (big[6][3] << 8)})");
        Check("calibration words decode to channel order names", Fusion2UsbDevice.DecodeCalibration(0x00010002) == "GRB" && Fusion2UsbDevice.DecodeCalibration(0x00000102) == "RGB" && Fusion2UsbDevice.DecodeCalibration(0x00020100) == "BGR" && Fusion2UsbDevice.DecodeCalibration(0xFFFFFFFF) == null);
        Check("LED capacity classes follow OpenRGB (32/64/256/512/1024)", Fusion2UsbDevice.CapacityEnumFor(27) == 0 && Fusion2UsbDevice.CapacityEnumFor(33) == 1 && Fusion2UsbDevice.CapacityEnumFor(65) == 2 && Fusion2UsbDevice.CapacityEnumFor(513) == 4);

        // --- Phase 6: elevated logon task definition
        var xml = XDocument.Parse(StartupTask.BuildXml(@"C:\Program Files\Hw Suite\HwSuite.exe", @"PC\me & co"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Check("logon task runs with the highest available privileges", xml.Descendants(ns + "RunLevel").FirstOrDefault()?.Value == "HighestAvailable");
        Check("logon task starts the app minimized with no execution time limit",
            xml.Descendants(ns + "Arguments").FirstOrDefault()?.Value == "--minimized" && xml.Descendants(ns + "ExecutionTimeLimit").FirstOrDefault()?.Value == "PT0S");
        Check("logon task XML escapes the user name and keeps the path intact",
            xml.Descendants(ns + "Command").FirstOrDefault()?.Value == @"C:\Program Files\Hw Suite\HwSuite.exe" && xml.Descendants(ns + "UserId").First().Value == @"PC\me & co");

        // --- lighting maths
        var ctx = new EffectContext { Time = 1.0, Speed = 0.5, Brightness = 1, ColorA = new Rgb(255, 0, 0), ColorB = new Rgb(0, 0, 255), ThermalC = 45 };
        Check("static effect returns colour A", Effects.Evaluate(EffectKind.Static, ctx, 0.3) == new Rgb(255, 0, 0));
        Check("stealth effect is black", Effects.Evaluate(EffectKind.Stealth, ctx, 0.3) == Rgb.Black);
        Check("brightness scales output", Effects.Evaluate(EffectKind.Static, new EffectContext { ColorA = new Rgb(200, 100, 50), Brightness = 0.5 }, 0) == new Rgb(100, 50, 25));
        var cold = Effects.Thermal(30); var hot = Effects.Thermal(85);
        Check("thermal ramp cold is blue-ish, hot is red-ish", cold.B > cold.R && hot.R > hot.B);
        Check("gradient stays within endpoints", Enumerable.Range(0, 20).All(i => { var c = Effects.Evaluate(EffectKind.GradientWave, ctx, i / 20.0); return c.R + c.B >= 250 && c.R + c.B <= 260; }));

        // --- Phase 7: ownership, failure handling, mouse, telemetry, compatibility (mocks and fault injection only)
        // each group is isolated: an exception (for example a test file that cannot be written) is a failed check, and
        // the remaining groups still run; nothing here can reach recovery
        Guarded("phase 7", RunPhase7);
        Guarded("lighting rates", LightingRateTests);
        Guarded("phase 8", Phase8Tests);
        Guarded("phase 9", Phase9Tests);
        Guarded("phase 10", Phase10Tests);
        Guarded("phase 11", Phase11Tests);
        Guarded("1.12", Phase12Tests);
        Guarded("1.13", Phase13Tests);
        Guarded("1.14", Phase14Tests);
        Guarded("1.15", Phase15Tests);
        Guarded("1.16", Phase16Tests);
        Guarded("1.17", Phase17Tests);
        Guarded("1.18", Phase18Tests);
        Guarded("1.19", Phase19Tests);
        Guarded("1.20", Phase20Tests);
        Guarded("1.22", Phase22Tests);

        // --- real hardware discovery (read-only)
        if (!hardware) Warn("hardware discovery skipped: HwSuite (or a hardware test) is running and owns the hardware");
        else
        {
            using var app = new AppServices(new AppConfig());
            try
            {
                app.InitializeHardware();
                foreach (var n in app.Hub.Notes) Log.Info("      " + n);
                var s1 = app.Hub.Poll(); Thread.Sleep(1000); var s2 = app.Hub.Poll();
                Check("telemetry poll returns", s2 != null);
                if (app.Hub.Gpu != null)
                {
                    Check("GPU temperature available", s2!.GpuTempC.HasValue, $"({s2.GpuTempC} C)");
                    Check("GPU fan tachometers available", s2.Fans.Count(f => f.Id.StartsWith("gpu:") && f.Rpm.HasValue) == app.Hub.Gpu.FanCount);
                }
                else Warn("NVML not available on this machine");
                if (app.Hub.PawnIoInstalled)
                {
                    Check("LibreHardwareMonitor opened with PawnIO", app.Hub.Lhm.IsOpen);
                    if (!s2!.CpuTempC.HasValue) Warn("CPU temperature unavailable through LibreHardwareMonitor: " + s2.CpuTempStatus);
                    else Check("CPU temperature available with read-success evidence", s2.CpuTempStatus == "ok" && s2.CpuTempReadUtc != null, $"({s2.CpuTempC:0.#} C via {s2.CpuTempSource})");
                    if (!s2.Fans.Any(f => !f.Id.StartsWith("gpu:"))) Warn("no motherboard fan tachometers found");
                }
                else Warn("PawnIO not installed: motherboard sensors and SMBus unavailable");

                var own = app.Ownership!;
                Log.Info($"      ownership: GPU '{own.Check(HwResource.GpuFans).Reason}' | Super I/O '{own.Check(HwResource.SuperIo).Reason}' | Gigabyte running: {string.Join(", ", own.GigabyteRunning)}");
                Check("ownership arbiter produced a decision for every resource", Enum.GetValues<HwResource>().All(r => own.Check(r).Reason != "ownership not checked yet"));

                app.InitializeLighting(allowRamRemap: false);
                foreach (var s in app.Rgb.Sinks) Log.Info($"      sink {s.Name}: available={s.Available} ({s.Status})");
                Check("lighting sinks constructed", app.Rgb.Sinks.Count >= 3);
            }
            catch (Exception ex) { Check("hardware discovery", false, ex.ToString()); }
        }

        // --- UI construction (optional, needs an STA thread)
        if (withUi)
        {
            Exception? uiError = null;
            var t = new Thread(() =>
            {
                try
                {
                    // fixture states only: the UI check opens no hardware (Phase 11)
                    var app = new App(true, Ui.Themes.DefaultId);
                    if (injectUiFault) app.Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("injected test fault in the UI thread")));
                    int f = app.RunFixtures(null, Ui.Themes.All, new[] { App.AllSizes[1] }, Ui.UiFixture.All());
                    if (f > 0) throw new InvalidOperationException($"{f} fixture render(s) failed (see log)");
                    int b = app.RunUiBehaviourChecks();
                    if (b > 0) throw new InvalidOperationException($"{b} UI behaviour check(s) failed (see log)");
                }
                catch (Exception ex) { uiError = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            bool completed = t.Join(240000);   // 14 fixtures x 3 themes plus the 1.12 behaviour checks
            Check("main window builds and renders its pages", completed && uiError == null,
                !completed ? "UI check timed out" : uiError?.ToString() ?? "");
        }

        if (RunMode.HardwareFree)
            Check("hardware-free run reached no hardware, recovery, watchdog or service boundary except the ones its isolation tests provoke",
                RunMode.Refusals == _provokedRefusals, $"({RunMode.Refusals} refusals, {_provokedRefusals} provoked on purpose; refused: {string.Join("; ", RunMode.Refused)})");
        Log.Info($"self-test finished: {_pass} passed, {_fail} failed, {_warn} warnings");
        return _fail == 0 ? 0 : 1;
    }
}
