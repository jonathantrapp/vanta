using System.Diagnostics;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>Phase 7 checks. Mocks and fault injection only: nothing here touches hardware.</summary>
public static partial class SelfTest
{
    sealed class FakeOwnership : IOwnership
    {
        readonly Dictionary<HwResource, OwnershipDecision> _d = new();
        public int ExternalReports;
        public OwnershipDecision Check(HwResource r) => _d.TryGetValue(r, out var v) ? v : OwnershipDecision.Owned;
        public void Set(HwResource r, bool may, string reason = "test competitor") => _d[r] = may ? OwnershipDecision.Owned : new(false, reason);
        public void ReportExternalWrite(HwResource r, string detail) { ExternalReports++; Set(r, false, "latched: " + detail); }
    }

    sealed class FakeProbe : ISystemProbe
    {
        public readonly HashSet<string> Procs = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, ServiceRunState> Svc = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RunningProcessNames() { lock (this) return new HashSet<string>(Procs, StringComparer.OrdinalIgnoreCase); }
        public ServiceRunState ServiceState(string name) { lock (this) return Svc.TryGetValue(name, out var s) ? s : ServiceRunState.NotInstalled; }
    }

    /// <summary>
    /// Fake service manager. Default: a start request succeeds and the service is Running. <see cref="Behaviour"/> per
    /// service simulates a slow start (accepted, StartPending at the deadline), a refused request, a service that stops
    /// again while starting, or an unreadable state; the wait itself runs through the real WaitForRunning logic.
    /// </summary>
    sealed class FakeServices : IServiceControl
    {
        public enum Mode { Succeed, Slow, Refuse, StopsAgain, Unreadable }
        readonly FakeProbe _p;
        public readonly List<string> Started = new();
        public readonly Dictionary<string, Mode> Behaviour = new(StringComparer.OrdinalIgnoreCase);
        public TimeSpan? LastTimeout;
        public FakeServices(FakeProbe p) { _p = p; }
        public ServiceRunState Query(string name) => _p.ServiceState(name);
        public ServiceStartOutcome Start(string name, TimeSpan timeout)
        {
            Started.Add(name);
            LastTimeout = timeout;
            var mode = Behaviour.TryGetValue(name, out var m) ? m : Mode.Succeed;
            if (mode == Mode.Refuse) return new(ServiceStartStatus.Failed, "start request refused: simulated access denied");
            int polls = 0;
            // simulated time: each poll advances a fake clock, so a 15 s wait costs nothing in the test
            return WindowsServiceControl.WaitForRunning(() =>
            {
                polls++;
                lock (_p)
                {
                    var st = mode switch
                    {
                        Mode.Succeed => polls >= 3 ? ServiceRunState.Running : ServiceRunState.StartPending,
                        Mode.Slow => ServiceRunState.StartPending,
                        Mode.StopsAgain => polls >= 2 ? ServiceRunState.Stopped : ServiceRunState.StartPending,
                        _ => ServiceRunState.Unknown
                    };
                    _p.Svc[name] = st == ServiceRunState.Unknown ? ServiceRunState.StartPending : st;
                    return (st, mode == Mode.StopsAgain && polls >= 2 ? 1067u : 0u);
                }
            }, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(1));
        }
    }

    sealed class FakeLink : IMouseLink
    {
        public int Applies;
        public volatile bool Fail;
        public volatile int DelayMs;
        public MouseLightingState? Last;
        public string Describe => "fake link";
        public bool Apply(MouseLightingState s, out string error)
        {
            if (DelayMs > 0) Thread.Sleep(DelayMs);
            Interlocked.Increment(ref Applies);
            if (Fail) { error = "simulated HID failure"; return false; }
            Last = s; error = ""; return true;
        }
        public int LiveSends;
        public Rgb? LastLive;
        public bool SendLive(Rgb c, out string error)
        {
            Interlocked.Increment(ref LiveSends);
            if (Fail) { error = "simulated HID failure"; return false; }
            LastLive = c; error = ""; return true;
        }
        public void Dispose() { }
    }

    sealed class FakeTransport : IMouseTransport
    {
        public volatile string? Path = "\\\\?\\hid#vid_258a&pid_2011#cable";
        public volatile string Sig = "cable|receiver";
        public readonly FakeLink Link = new();
        public int Opens;
        public IMouseLink? OpenPreferred(out string status) { Interlocked.Increment(ref Opens); status = "fake " + Path; return Path == null ? null : Link; }
        public string? PreferredPath() => Path;
        public string Signature() => Sig;
    }

    sealed class TestSink : RgbSink
    {
        int _concurrent;
        public int MaxConcurrent, ApplyCalls;
        public volatile int DelayMs;
        public volatile bool Fail;
        public volatile bool StripRate;
        public double Hz = 60;
        public TestSink() { Available = true; }
        public override string Name => "test sink";
        public override string Kind => "test";
        public override int LedCount => 1;
        public override double[] Positions => new[] { 0.5 };
        protected override double MaxHz => Hz;
        protected override int MinDelta => 0;   // every frame counts, so rates measure the scheduler alone
        public override bool FollowsStripRate => StripRate;
        public override bool EnabledByDefault => true;
        protected override bool Apply(Rgb[] frame)
        {
            int c = Interlocked.Increment(ref _concurrent);
            if (c > MaxConcurrent) MaxConcurrent = c;
            if (DelayMs > 0) Thread.Sleep(DelayMs);
            Interlocked.Decrement(ref _concurrent);
            ApplyCalls++;
            return !Fail;
        }
    }

    static bool WaitUntil(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(10); }
        return cond();
    }

    static void RunPhase7()
    {
        FanSafetyTests();
        OwnershipEngineTests();
        FailureTests();
        GigabyteTests();
        AfterburnerTests();
        TelemetryAndHeartbeatTests();
        MousePacketTests();
        MouseControllerTests();
        SchedulerTests();
        ChannelOrderTests();
        CompatibilityTests();
    }

    // ---------------------------------------------------------------------------------------------------------
    static void FanSafetyTests()
    {
        var aio = new FakeChannel("aio", FanKind.Aio);
        var gpu = new FakeChannel("gpu", FanKind.GpuFan, 30);
        var generic = new FakeChannel("generic", FanKind.Generic);
        var caseNoMin = new FakeChannel("caseNoMin", FanKind.CaseFan);
        var caseMin = new FakeChannel("caseMin", FanKind.CaseFan);

        var a = FanSafety.Assess(Cfg(aio, FanKind.Aio, ChannelMode.Auto), aio);
        Check("AIO floor is 60% and labelled a policy, not a measurement", a.Controllable && a.Floor == 60 && a.Basis.Contains("policy") && a.Basis.Contains("not a measured"), $"({a.Basis})");
        var g = FanSafety.Assess(Cfg(gpu, FanKind.GpuFan, ChannelMode.Auto), gpu);
        Check("GPU fan floor comes from the NVML-reported minimum", g.Controllable && g.Floor == 30 && g.Basis.Contains("NVML"));
        var u = FanSafety.Assess(Cfg(generic, FanKind.Generic, ChannelMode.Curve), generic);
        Check("unidentified header is not controllable", !u.Controllable && (u.Blocker ?? "").Contains("unidentified"));
        var n = FanSafety.Assess(Cfg(caseNoMin, FanKind.CaseFan, ChannelMode.Curve), caseNoMin);
        Check("case fan without a confirmed minimum is not controllable (no arbitrary floor)", !n.Controllable && (n.Blocker ?? "").Contains("confirmed minimum"));
        var m = FanSafety.Assess(Cfg(caseMin, FanKind.CaseFan, ChannelMode.Curve, min: 35), caseMin);
        Check("case fan with a confirmed minimum uses exactly that floor", m.Controllable && m.Floor == 35 && m.Basis.Contains("owner-confirmed"));
        var gpuTypeOnBoard = new FakeChannel("boardGpuType", FanKind.Generic);
        var bg = FanSafety.Assess(Cfg(gpuTypeOnBoard, FanKind.GpuFan, ChannelMode.Curve), gpuTypeOnBoard);
        Check("'GPU fan' type on a motherboard header still needs a confirmed minimum", !bg.Controllable);

        var cfg = new AppConfig();
        cfg.Channels.Add(Cfg(aio, FanKind.Aio, ChannelMode.Auto));
        cfg.Channels.Add(Cfg(gpu, FanKind.GpuFan, ChannelMode.Auto));
        cfg.Channels.Add(Cfg(generic, FanKind.Generic, ChannelMode.Auto));
        cfg.Channels.Add(Cfg(caseNoMin, FanKind.CaseFan, ChannelMode.Auto));
        cfg.Channels.Add(Cfg(caseMin, FanKind.CaseFan, ChannelMode.Auto, min: 30));
        var switched = FanPresets.ApplyPreset(cfg, "Performance", new IFanChannel[] { aio, gpu, generic, caseNoMin, caseMin });
        Check("a preset switches only identified channels with evidence-backed floors to Curve",
            switched.OrderBy(x => x).SequenceEqual(new[] { "aio", "caseMin", "gpu" }) &&
            cfg.Channels.First(c => c.Id == "generic").Mode == ChannelMode.Auto && cfg.Channels.First(c => c.Id == "caseNoMin").Mode == ChannelMode.Auto,
            $"(switched {string.Join(",", switched)})");

        // a curve that asks for 5% on a fan confirmed at 35%
        var ecfg = new AppConfig { FanControlEnabled = true };
        using var e = new FanEngine(ecfg);
        var low = new FakeChannel("low", FanKind.CaseFan);
        var blind = new FakeChannel("blind", FanKind.CaseFan) { TachSensor = false, HasTach = false, Rpm = null };
        e.Channels.Add(low); e.Channels.Add(blind);
        var lowCurve = new FanCurve { Source = CurveSource.MaxOfBoth, HysteresisC = 0, RampUpPerSec = 100, StepDownPerSec = 100, Points = { new CurvePoint(30, 5), new CurvePoint(80, 100) } };
        ecfg.Channels.Add(new ChannelConfig { Id = "low", Kind = FanKind.CaseFan, Mode = ChannelMode.Curve, Curve = lowCurve.Clone(), VerifiedMinPercent = 35, MinEvidence = "test" });
        ecfg.Channels.Add(new ChannelConfig { Id = "blind", Kind = FanKind.CaseFan, Mode = ChannelMode.Curve, Curve = lowCurve.Clone() });
        for (int i = 0; i < 5; i++) e.Tick(Snap(30, 30), 10);
        Check("confirmed minimum holds when the curve asks for less", low.Applied >= 35 && low.Applied <= 36, $"(applied {low.Applied})");
        Check("a tachless case fan with no confirmed minimum is left alone (no 25% floor applied)", blind.Writes == 0, $"(writes {blind.Writes})");
    }

    // ---------------------------------------------------------------------------------------------------------
    static void OwnershipEngineTests()
    {
        var cfg = new AppConfig { FanControlEnabled = true };
        using var e = new FanEngine(cfg);
        var own = new FakeOwnership();
        e.Ownership = own;
        var gpu = new FakeChannel("gpu:0", FanKind.GpuFan, 30);
        var aio = new FakeChannel("aio", FanKind.Aio);
        e.Channels.Add(gpu); e.Channels.Add(aio);
        cfg.Channels.Add(Cfg(gpu, FanKind.GpuFan, ChannelMode.Curve));
        cfg.Channels.Add(Cfg(aio, FanKind.Aio, ChannelMode.Curve));

        own.Set(HwResource.GpuFans, false, "MSI Afterburner's software fan curve is on");
        for (int i = 0; i < 3; i++) e.Tick(Snap(50, 60), 1);
        Check("a confirmed competing GPU controller means zero HwSuite GPU writes", gpu.Writes == 0 && gpu.Releases == 0,
            $"(writes {gpu.Writes}, state '{e.Status["gpu:0"].State}')");
        Check("Super I/O control is unaffected by a GPU conflict", aio.Writes > 0);

        own.Set(HwResource.GpuFans, true);
        e.Tick(Snap(50, 60), 1);
        int before = gpu.Writes;
        Check("GPU fans are driven once ownership is established", before > 0);

        own.Set(HwResource.GpuFans, false);
        e.Tick(Snap(50, 60), 1); e.Tick(Snap(50, 60), 1);
        Check("losing GPU ownership yields without writing (no hand-back that would override the other program)",
            gpu.Writes == before && gpu.Releases == 0 && gpu.Relinquishes == 1, $"(writes {gpu.Writes}, releases {gpu.Releases}, relinquish {gpu.Relinquishes})");

        own.Set(HwResource.SuperIo, false, "EasyTuneEngineService service running");
        int aioWrites = aio.Writes;
        e.Tick(Snap(50, 60), 1); e.Tick(Snap(50, 60), 1);
        Check("a Gigabyte competitor makes HwSuite hand the Super I/O header back once and stop writing", aio.Releases == 1 && aio.Writes == aioWrites);
        Check("identify is refused while another program owns the header", e.StartIdentify("aio") != null && aio.Writes == aioWrites);

        // outside write detected on a GPU fan HwSuite drives: stop, do not fight
        var cfg2 = new AppConfig { FanControlEnabled = true };
        using var e2 = new FanEngine(cfg2);
        var own2 = new FakeOwnership();
        e2.Ownership = own2;
        var g2 = new FakeChannel("gpu:1", FanKind.GpuFan, 30);
        e2.Channels.Add(g2);
        cfg2.Channels.Add(Cfg(g2, FanKind.GpuFan, ChannelMode.Curve));
        e2.Tick(Snap(50, 60), 1);
        int w = g2.Writes;
        g2.ForeignTarget = (int)g2.Applied + 12;   // another program rewrites the fan
        e2.Tick(Snap(50, 60), 1);
        for (int i = 0; i < 4; i++) e2.Tick(Snap(50, 60), 1);
        Check("an outside change to a GPU fan is reported and HwSuite stops writing instead of re-asserting",
            own2.ExternalReports == 1 && g2.Writes == w && g2.Relinquishes == 1 && g2.Releases == 0, $"(reports {own2.ExternalReports}, writes {g2.Writes - w} after)");
    }

    // ---------------------------------------------------------------------------------------------------------
    static void FailureTests()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        var cfg = new AppConfig { FanControlEnabled = true };
        using var e = new FanEngine(cfg) { UtcNow = () => now };
        var alerts = new List<string>();
        e.Alert += alerts.Add;
        var aio = new FakeChannel("aio", FanKind.Aio);
        e.Channels.Add(aio);
        cfg.Channels.Add(Cfg(aio, FanKind.Aio, ChannelMode.Curve));

        e.Tick(Snap(45, 40), 1);
        Check("AIO is driven while the CPU temperature is valid", aio.Writes > 0 && aio.Applied >= 60);
        e.Tick(Snap(null, 80), 1);
        Check("CPU temperature lost: the AIO is handed back to firmware, not driven from the GPU temperature",
            aio.Releases == 1 && aio.Applied < 0 && e.Status["aio"].State.Contains("CPU temperature unavailable"), $"('{e.Status["aio"].State}')");
        int w = aio.Writes;
        for (int i = 0; i < 3; i++) e.Tick(Snap(null, 80), 1);
        Check("no writes while the temperature stays missing", aio.Writes == w);
        e.Tick(Snap(45, 40), 1);
        Check("control resumes when the temperature returns", aio.Writes > w);
        Check("temperature loss raised an alert", alerts.Any(a => a.Contains("handed back")));

        aio.FailRelease = true;
        e.Tick(Snap(null, 40), 1);
        Check("if the hand-back fails, the channel is driven at 100%", aio.Applied == 100, $"(applied {aio.Applied})");
        aio.FailRelease = false;

        // MaxOfBoth needs both sources
        var cfgM = new AppConfig { FanControlEnabled = true };
        using var em = new FanEngine(cfgM);
        var cf = new FakeChannel("case", FanKind.CaseFan);
        em.Channels.Add(cf);
        var cc = Cfg(cf, FanKind.CaseFan, ChannelMode.Curve, min: 30);
        cc.Curve.Source = CurveSource.MaxOfBoth;
        cfgM.Channels.Add(cc);
        em.Tick(Snap(40, null), 1);
        Check("a 'hotter of CPU and GPU' curve with one source missing is treated as missing", cf.Writes == 0);

        // three consecutive write failures fault the channel; retries are spaced, not continuous
        var cfgF = new AppConfig { FanControlEnabled = true };
        var t = now;
        using var ef = new FanEngine(cfgF) { UtcNow = () => t };
        var bad = new FakeChannel("bad", FanKind.Aio);
        ef.Channels.Add(bad);
        cfgF.Channels.Add(Cfg(bad, FanKind.Aio, ChannelMode.Curve));
        ef.Tick(Snap(45, 40), 1);
        bad.FailWrites = true;
        int attempts = 0;
        for (int i = 0; i < 3; i++) { t = t.AddSeconds(6); ef.Tick(Snap(45, 40), 1); attempts++; }
        t = t.AddSeconds(1); ef.Tick(Snap(45, 40), 1);
        Check("three write failures fault the channel and hand it back", bad.Releases == 1 && ef.Status["bad"].State.Contains("faulted"), $"('{ef.Status["bad"].State}')");
        bad.FailWrites = false;
        int wf = bad.Writes;
        for (int i = 0; i < 4; i++) { t = t.AddSeconds(6); ef.Tick(Snap(45, 40), 1); }
        Check("no writes during the 30 s fault pause", bad.Writes == wf);
        t = t.AddSeconds(10); ef.Tick(Snap(45, 40), 1);
        Check("control is retried after the pause", bad.Writes > wf);

        var cfgX = new AppConfig { FanControlEnabled = true };
        var tx = now;
        using var ex = new FanEngine(cfgX) { UtcNow = () => tx };
        var thrower = new FakeChannel("thrower", FanKind.Aio);
        ex.Channels.Add(thrower);
        cfgX.Channels.Add(Cfg(thrower, FanKind.Aio, ChannelMode.Curve));
        ex.Tick(Snap(45, 40), 1);
        thrower.ThrowOnWrite = true;
        for (int i = 0; i < 3; i++) { tx = tx.AddSeconds(6); ex.Tick(Snap(45, 40), 1); }
        tx = tx.AddSeconds(1); ex.Tick(Snap(45, 40), 1);
        Check("device exceptions count as failures and fault the channel", thrower.Releases == 1 && ex.Status["thrower"].State.Contains("faulted"));

        // feedback that worked before and disappears: hold 100%
        var cfgT = new AppConfig { FanControlEnabled = true };
        using var et = new FanEngine(cfgT);
        var tach = new FakeChannel("tach", FanKind.Aio) { Rpm = 1500 };
        et.Channels.Add(tach);
        cfgT.Channels.Add(Cfg(tach, FanKind.Aio, ChannelMode.Curve));
        var talerts = new List<string>();
        et.Alert += talerts.Add;
        et.Tick(Snap(40, 40), 1); et.Tick(Snap(40, 40), 1);
        Check("a working tachometer is recorded in the config", cfgT.Channels[0].TachSeen);
        tach.Rpm = 0;
        for (int i = 0; i < FanEngine.TachLossTicks + 1; i++) et.Tick(Snap(40, 40), 1);
        Check("lost speed signal on a driven pump/AIO holds 100% and alerts", tach.Applied == 100 && et.Status["tach"].FeedbackLost && talerts.Any(a => a.Contains("speed signal lost")),
            $"(applied {tach.Applied})");
        tach.Rpm = 1600;
        for (int i = 0; i < FanEngine.TachRecoverTicks + 1; i++) et.Tick(Snap(40, 40), 1);
        Check("the 100% hold ends once the speed signal is back", !et.Status["tach"].FeedbackLost);

        // sleep and resume
        var cfgS = new AppConfig { FanControlEnabled = true };
        var ts = now;
        using var es = new FanEngine(cfgS) { UtcNow = () => ts };
        var sl = new FakeChannel("sleep", FanKind.Aio);
        es.Channels.Add(sl);
        cfgS.Channels.Add(Cfg(sl, FanKind.Aio, ChannelMode.Curve));
        es.Tick(Snap(45, 40), 1);
        es.Suspend();
        Check("suspend hands every driven channel back", sl.Releases == 1);
        int ws = sl.Writes;
        es.Tick(Snap(45, 40), 1);
        es.Resume();
        ts = ts.AddSeconds(5); es.Tick(Snap(45, 40), 1);
        Check("no writes while suspended or during the resume grace period", sl.Writes == ws);
        ts = ts.AddSeconds(6); es.Tick(Snap(45, 40), 1);
        Check("control resumes after the grace period", sl.Writes > ws);
    }

    // ---------------------------------------------------------------------------------------------------------
    static void GigabyteTests()
    {
        string rec = Path.Combine(Path.GetTempPath(), "hwsuite-selftest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var probe = new FakeProbe();
            probe.Svc["EasyTuneEngineService"] = ServiceRunState.Running;
            probe.Svc["GService"] = ServiceRunState.Running;
            probe.Svc["GCCService"] = ServiceRunState.Stopped;      // installed but already stopped by the owner
            probe.Procs.Add("EasyTuneEngineService"); probe.Procs.Add("RGBFusion");
            var services = new FakeServices(probe);
            bool scriptWorks = true;
            var guard = new GigabyteGuard(probe, services, () =>
            {
                if (scriptWorks) lock (probe)
                {
                    probe.Svc["EasyTuneEngineService"] = ServiceRunState.Stopped; probe.Svc["GService"] = ServiceRunState.Stopped;
                    probe.Procs.Clear();
                }
                return (true, "");
            }, rec);

            var r = guard.StopAndRecord("test");
            var record = guard.LoadRecord();
            Check("stop is verified by re-checking, and only services that were running are recorded",
                r.Verified && record.StoppedServices.OrderBy(x => x).SequenceEqual(new[] { "EasyTuneEngineService", "GService" }) && !record.StoppedServices.Contains("GCCService"),
                $"(recorded {string.Join(",", record.StoppedServices)})");
            Check("killed user programs are recorded separately", record.KilledProcesses.Contains("RGBFusion"));

            lock (probe) probe.Svc["GService"] = ServiceRunState.Running;   // someone else started it meanwhile
            var results = guard.RestoreRecorded();
            Check("restore starts only recorded services that are still stopped",
                services.Started.SequenceEqual(new[] { "EasyTuneEngineService" }) && results.Any(x => x.Contains("GService") && x.Contains("left alone")),
                $"(started {string.Join(",", services.Started)})");
            Check("a service that was already stopped before HwSuite is never started", !services.Started.Contains("GCCService"));
            Check("the restore list is cleared after restoring", guard.LoadRecord().StoppedServices.Count == 0);

            // stop that does not take effect
            lock (probe) { probe.Svc["EasyTuneEngineService"] = ServiceRunState.Running; }
            scriptWorks = false;
            var r2 = guard.StopAndRecord("test failure");
            Check("a stop that does not take effect is reported as failed, not assumed", !r2.Verified && r2.StillRunning.Any(x => x.Contains("EasyTuneEngineService")));

            // arbiter: auto-stop off -> withhold; GPU unaffected
            var cfg = new AppConfig { FanControlEnabled = true, AutoStopGigabyte = false };
            var arb = new OwnershipArbiter(cfg, guard, probe, () => new AfterburnerFanSetting(true, false, 2000, "test"), () => false);
            arb.Refresh();
            var sio = arb.Check(HwResource.SuperIo);
            Check("Gigabyte tool running with auto-stop off: Super I/O, SMBus and IT5701 writes are withheld",
                !sio.MayWrite && sio.Reason.Contains("auto-stop is off") && !arb.Check(HwResource.Smbus).MayWrite && !arb.Check(HwResource.RgbUsb).MayWrite, $"({sio.Reason})");
            Check("no Afterburner: GPU fans stay owned", arb.Check(HwResource.GpuFans).MayWrite);

            // arbiter: auto-stop on -> one background attempt, verified, then owned
            scriptWorks = true;
            cfg.AutoStopGigabyte = true;
            arb.Refresh();
            Check("auto-stop runs in the background while the hardware stays withheld", !arb.Check(HwResource.SuperIo).MayWrite);
            WaitUntil(() => arb.LastStopResult != null, 5000);
            arb.Refresh();
            Check("after a verified stop the Super I/O is owned again", arb.LastStopResult?.Verified == true && arb.Check(HwResource.SuperIo).MayWrite, $"({arb.Check(HwResource.SuperIo).Reason})");

            // arbiter: failing stop is bounded
            lock (probe) probe.Svc["EasyTuneEngineService"] = ServiceRunState.Running;
            scriptWorks = false;
            var arb2 = new OwnershipArbiter(cfg, guard, probe, () => new AfterburnerFanSetting(true, false, 2000, "test"), () => false);
            int stops = 0;
            arb2.Notice += _ => Interlocked.Increment(ref stops);
            arb2.Refresh();
            WaitUntil(() => arb2.LastStopResult != null, 5000);
            for (int i = 0; i < 5; i++) arb2.Refresh();
            Thread.Sleep(200);
            Check("a failing auto-stop is retried at most once a minute, and the hardware stays withheld",
                stops == 1 && !arb2.Check(HwResource.SuperIo).MayWrite, $"(attempts {stops}, reason '{arb2.Check(HwResource.SuperIo).Reason}')");
        }
        finally { try { File.Delete(rec); } catch { } }
    }

    // ---------------------------------------------------------------------------------------------------------
    static void AfterburnerTests()
    {
        const string full = "[Settings]\r\nFoo=1\r\nSwAutoFanControl=0\r\nSwAutoFanControlPeriod=2000\r\n[ATIADLHAL]\r\nX=1\r\n";
        var p0 = AfterburnerHelper.ParseStrict(full);
        var p1 = AfterburnerHelper.ParseStrict(full.Replace("SwAutoFanControl=0", "SwAutoFanControl=1"));
        Check("strict parse: complete file with SwAutoFanControl=0/1 is known", p0.Known && p0.SoftwareCurveOn == false && p0.PeriodMs == 2000 && p1.Known && p1.SoftwareCurveOn == true);
        Check("strict parse: missing key (truncated file) is unknown", !AfterburnerHelper.ParseStrict("[Settings]\r\nFoo=1\r\n").Known);
        Check("strict parse: no [Settings] section is unknown", !AfterburnerHelper.ParseStrict("SwAutoFanControl=0\r\n").Known);
        Check("strict parse: conflicting duplicate entries are unknown", !AfterburnerHelper.ParseStrict(full + "[Settings]\r\nSwAutoFanControl=1\r\n").Known);
        Check("strict parse: values other than exactly 0 or 1 are unknown",
            !AfterburnerHelper.ParseStrict(full.Replace("SwAutoFanControl=0", "SwAutoFanControl=10")).Known &&
            !AfterburnerHelper.ParseStrict(full.Replace("SwAutoFanControl=0", "SwAutoFanControl=")).Known);
        Check("strict parse: the key outside [Settings] is unknown", !AfterburnerHelper.ParseStrict(full + "[Other]\r\nSwAutoFanControl=0\r\n").Known);
        Check("strict parse: an empty or unreadable file is unknown", !AfterburnerHelper.ParseStrict("").Known);

        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        string text = full;
        var mtime = now.AddMinutes(-10);
        long len = text.Length;
        bool changeDuringRead = false, throwOnRead = false;
        var w = new AfterburnerConfigWatcher(() => "cfg",
            _ => new AfterburnerConfigWatcher.FileSnapshot(mtime, len),
            _ =>
            {
                if (throwOnRead) throw new IOException("sharing violation");
                if (changeDuringRead) { mtime = now; len++; }
                return text;
            });
        var r1 = w.Read(now);
        var r2 = w.Read(now);
        Check("watcher: the first read is not trusted until confirmed; the identical second read is", !r1.Known && r2.Known && r2.SoftwareCurveOn == false, $"({r1.Detail} / {r2.Detail})");
        text = full.Replace("SwAutoFanControl=0", "SwAutoFanControl=1"); len = text.Length;
        var r3 = w.Read(now);
        Check("watcher: changed content is unknown for one cycle", !r3.Known && r3.Detail.Contains("changed"));
        changeDuringRead = true;
        var r4 = w.Read(now);
        Check("watcher: a file modified during the read is unknown", !r4.Known && r4.Detail.Contains("while it was being read"));
        changeDuringRead = false;
        var r5 = w.Read(now.AddSeconds(1));
        Check("watcher: a file modified moments ago is unknown", !r5.Known && r5.Detail.Contains("moments ago"));
        throwOnRead = true;
        Check("watcher: an unreadable file is unknown", !w.Read(now.AddMinutes(1)).Known);

        string? location = "first.cfg";
        bool exists = true;
        var recovery = new AfterburnerConfigWatcher(() => location,
            _ => exists ? new AfterburnerConfigWatcher.FileSnapshot(now.AddMinutes(-10), full.Length) : null,
            _ => full);
        recovery.Read(now); recovery.Read(now);
        exists = false;
        Check("watcher: missing file revokes ownership", !recovery.Read(now).Known);
        exists = true;
        Check("watcher: restored identical file needs two fresh stable reads", !recovery.Read(now).Known && recovery.Read(now).Known);
        location = "second.cfg";
        Check("watcher: a different path with identical content needs confirmation", !recovery.Read(now).Known && recovery.Read(now).Known);
        location = null;
        Check("watcher: lost path revokes ownership", !recovery.Read(now).Known);
        location = "second.cfg";
        Check("watcher: recovered path needs confirmation", !recovery.Read(now).Known && recovery.Read(now).Known);

        // ownership never granted on an unknown read
        string rec = Path.Combine(Path.GetTempPath(), "hwsuite-selftest-" + Guid.NewGuid().ToString("N") + ".json");
        var probe = new FakeProbe();
        probe.Procs.Add("MSIAfterburner");
        var guard = new GigabyteGuard(probe, new FakeServices(probe), () => (true, ""), rec);
        AfterburnerFanSetting setting = new(false, null, null, "config file content changed since the last check");
        bool foreign = false;
        var cfg = new AppConfig { FanControlEnabled = true };
        var arb = new OwnershipArbiter(cfg, guard, probe, () => setting, () => foreign);
        arb.Refresh();
        Check("Afterburner running with an unknown fan setting: GPU fans are not acquired", !arb.Check(HwResource.GpuFans).MayWrite && arb.Check(HwResource.GpuFans).Reason.Contains("unknown"));
        setting = new(true, true, 2000, "SwAutoFanControl=1"); arb.Refresh();
        Check("Afterburner software curve on: GPU fans are not acquired", !arb.Check(HwResource.GpuFans).MayWrite);
        setting = new(true, false, 2000, "SwAutoFanControl=0"); foreign = true; arb.Refresh();
        Check("Afterburner holding a manual speed: GPU fans are not acquired", !arb.Check(HwResource.GpuFans).MayWrite);
        foreign = false; arb.Refresh();
        Check("Afterburner running with an explicit, stable SwAutoFanControl=0 and no manual speed: GPU fans may be driven", arb.Check(HwResource.GpuFans).MayWrite);
        arb.ReportExternalWrite(HwResource.GpuFans, "test");
        arb.Refresh();
        Check("an observed outside GPU write latches ownership off", !arb.Check(HwResource.GpuFans).MayWrite);
        probe.Procs.Remove("MSIAfterburner"); arb.Refresh();
        Check("the latch clears when Afterburner (running at the time) exits", arb.Check(HwResource.GpuFans).MayWrite);
        probe.Procs.Add("Glorious Core"); arb.Refresh();
        Check("Glorious CORE running: mouse lighting writes are withheld", !arb.Check(HwResource.Mouse).MayWrite);
        try { File.Delete(rec); } catch { }
    }

    // ---------------------------------------------------------------------------------------------------------
    static void TelemetryAndHeartbeatTests()
    {
        var ok = TelemetryHub.ClassifyCpuTemp(42, false);
        var stale = TelemetryHub.ClassifyCpuTemp(42, true);
        var missing = TelemetryHub.ClassifyCpuTemp(null, false);
        Check("CPU temperature is used only with read-success evidence", ok.temp == 42 && ok.status == "ok" && stale.temp == null && stale.status.StartsWith("stale") && missing.temp == null);

        var fd = new FreezeDetector(15);
        bool frozen = false;
        for (int i = 0; i < 16; i++) frozen = fd.Frozen(17.25);
        var fd2 = new FreezeDetector(15);
        bool moving = false;
        for (int i = 0; i < 40; i++) moving |= fd2.Frozen(17.25 + i * 0.01);
        Check("power-freeze heuristic flags 15 exact repeats and nothing else", frozen && !moving && !fd.Frozen(null) && fd.SameCount == 0);

        Check("heartbeat age uses one clock: 10 s apart reads 10 s", Heartbeat.Age(50_000_000, 150_000_000) == 10);
        Check("heartbeat: a recorded clock failure gives no age (no hang decision)", Heartbeat.Age(Heartbeat.Unknown, 150_000_000) == null);
        Check("heartbeat: a failed clock query on the reading side gives no age", Heartbeat.Age(50_000_000, null) == null);
        Check("heartbeat: a stored time ahead of now gives no age", Heartbeat.Age(150_000_000, 50_000_000) == null && Heartbeat.Age(0, 50_000_000) == null);

        var savedClock = Heartbeat.Clock;
        try
        {
            using var hb = Heartbeat.CreateForCurrentProcess();
            if (hb == null) Warn("heartbeat shared memory unavailable in this session");
            else
            {
                Heartbeat.Clock = () => null;
                hb.Beat();
                Check("heartbeat: when the clock query fails the watchdog sees 'unknown', not a substitute time", Heartbeat.AgeSeconds(Environment.ProcessId) == null);
                Heartbeat.Clock = savedClock;
                hb.Beat();
                var age = Heartbeat.AgeSeconds(Environment.ProcessId);
                Check("heartbeat: a normal beat reads back as fresh", age is double a && a >= 0 && a < 2, $"({age})");
            }
        }
        finally { Heartbeat.Clock = savedClock; }

        Check("watchdog acts on a silent fan loop only while HwSuite drives fans, once per hang",
            FailsafeWatchdog.ShouldActOnHang(31, 2, false) && !FailsafeWatchdog.ShouldActOnHang(31, 0, false) &&
            !FailsafeWatchdog.ShouldActOnHang(31, 2, true) && !FailsafeWatchdog.ShouldActOnHang(null, 2, false) && !FailsafeWatchdog.ShouldActOnHang(10, 2, false));
    }

    // ---------------------------------------------------------------------------------------------------------
    static void MousePacketTests()
    {
        var red = new MouseLightingState(MouseLightingState.EffectStatic, new Rgb(255, 0, 0), 11, 255, "static", false);
        var p = GloriousWirelessDevice.BuildEffectReport(red);
        Check("mouse effect report follows Glorious CORE's layout (static red, profile 1)",
            p.Length == 65 && p[0] == 0 && p[3] == 2 && p[4] == 8 && p[5] == 2 && p[6] == 0 && p[7] == 1 && p[8] == 0xFF && p[9] == 4 && p[10] == 0 && p[11] == 11 &&
            p[12] == 255 && p[13] == 0 && p[14] == 0 && p.Skip(15).All(b => b == 0), $"({string.Join(" ", p.Take(16).Select(b => b.ToString("X2")))})");
        var off = GloriousWirelessDevice.BuildEffectReport(new MouseLightingState(MouseLightingState.EffectOff, null, 11, 255, "off", false));
        Check("mouse 'off' report carries no colour (length code 5)", off[4] == 5 && off[9] == 0 && off[12] == 0);
        var wired = GloriousWirelessDevice.BuildBrightnessReport(200, true);
        var wireless = GloriousWirelessDevice.BuildBrightnessReport(200, false);
        Check("mouse brightness reports: wired [7]=1, wireless [7]=0, value at [8]",
            wired[3] == 2 && wired[4] == 2 && wired[5] == 2 && wired[6] == 2 && wired[7] == 1 && wired[8] == 200 && wireless[7] == 0 && wireless[8] == 200);

        // multi-colour builder (mouse probe): same bytes as the single-colour path, colours packed from byte 12
        Check("mouse multi-colour builder gives the same static red report", p.SequenceEqual(GloriousWirelessDevice.BuildEffectReport(4, 11, new[] { new Rgb(255, 0, 0) })));
        var two = GloriousWirelessDevice.BuildEffectReport(3, 2, new[] { new Rgb(0, 255, 204), new Rgb(255, 0, 127) });
        Check("mouse breathing with 2 colours: length code 11, colours at 12-17",
            two[4] == 11 && two[9] == 3 && two[11] == 2 && two[12] == 0 && two[13] == 255 && two[14] == 204 && two[15] == 255 && two[16] == 0 && two[17] == 127 && two.Skip(18).All(b => b == 0));
        var seven = GloriousWirelessDevice.BuildEffectReport(3, 11, Enumerable.Range(1, 7).Select(i => new Rgb((byte)i, (byte)(i * 2), (byte)(i * 3))).ToArray());
        Check("mouse breathing with 7 colours: length code 26, last colour at 30-32", seven[4] == 26 && seven[30] == 7 && seven[31] == 14 && seven[32] == 21 && seven.Skip(33).All(b => b == 0));
        var seamless = GloriousWirelessDevice.BuildEffectReport(2, 11, new[] { new Rgb(0, 0, 255) });
        Check("mouse Seamless Breathing writes red into slot 0 like Glorious CORE", seamless[12] == 255 && seamless[13] == 0 && seamless[14] == 0);
        bool tooMany = false, badEffect = false;
        try { GloriousWirelessDevice.BuildEffectReport(3, 11, new Rgb[8]); } catch (ArgumentOutOfRangeException) { tooMany = true; }
        try { GloriousWirelessDevice.BuildEffectReport(9, 11, new[] { new Rgb(1, 1, 1) }); } catch (ArgumentOutOfRangeException) { badEffect = true; }
        Check("mouse builder refuses 8 colours and effect codes above 8", tooMany && badEffect);
        var plan = MouseProbe.Parse(new[] { "--mouse-probe", "3", "1,2,5,11,20", "00FFCC,FF007F", "--hold", "30", "--via", "receiver" }, out _);
        Check("mouse probe: speed sweep with 2 colours parses", plan != null && plan.Speeds.Length == 5 && plan.Colors.Length == 2 && plan.Via == "receiver" && plan.HoldSeconds == 30 && plan.Brightness == 255);
        Check("mouse probe refuses unsafe plans (speed out of CORE's range, effect 9, 8 colours, re-send under 1 s, no colour)",
            MouseProbe.Parse(new[] { "--mouse-probe", "5", "30", "FF0000" }, out _) == null &&
            MouseProbe.Parse(new[] { "--mouse-probe", "9", "11", "FF0000" }, out _) == null &&
            MouseProbe.Parse(new[] { "--mouse-probe", "3", "11", string.Join(",", Enumerable.Range(1, 8).Select(i => i.ToString("X6"))) }, out _) == null &&
            MouseProbe.Parse(new[] { "--mouse-probe", "5", "11", "FF0000", "--resend-ms", "500" }, out _) == null &&
            MouseProbe.Parse(new[] { "--mouse-probe", "5", "11" }, out _) == null);
        var blink = MouseProbe.Parse(new[] { "--mouse-probe", "4", "11", "FFFFFF", "--blink", "10", "--hold", "2", "--strip-marker" }, out _);
        Check("mouse probe: --blink alternates a static colour with off (10 writes) with the strip marker; refused for other effects or with re-sends",
            blink is { Blink: 10, StripMarker: true, HoldSeconds: 2 } &&
            MouseProbe.Parse(new[] { "--mouse-probe", "5", "11", "FFFFFF", "--blink", "10" }, out _) == null &&
            MouseProbe.Parse(new[] { "--mouse-probe", "4", "11", "FFFFFF", "--blink", "21" }, out _) == null &&
            MouseProbe.Parse(new[] { "--mouse-probe", "4", "11", "FFFFFF", "--blink", "4", "--resend-ms", "2000" }, out _) == null);
        Check("mouse probe: off needs no colour; Rave/Wave accept CORE's 10-210 speed range",
            MouseProbe.Parse(new[] { "--mouse-probe", "0", "11" }, out _) is { Colors.Length: 0 } && MouseProbe.Parse(new[] { "--mouse-probe", "7", "100", "FF0000" }, out _) != null);

        var rgb = new RgbConfig { Enabled = true, Mouse = true, Effect = EffectKind.Static, ColorA = 0x00FF00, Speed = 0.5, Brightness = 0.5 };
        var st = MouseLightingState.From(rgb)!;
        Check("mouse mapping: static colour, speed byte (105-50)/5=11, brightness 50% = 127", st.Effect == 4 && st.Color == new Rgb(0, 255, 0) && st.Speed == 11 && st.Brightness == 127 && !st.Fallback);
        var staticAtOtherSpeed = MouseLightingState.From(new RgbConfig { Enabled = true, Mouse = true, Effect = EffectKind.Static, ColorA = 0x00FF00, Speed = 0.9, Brightness = 0.5 });
        Check("moving the speed slider on a static colour does not count as a mouse change", Equals(st, staticAtOtherSpeed));
        Check("settings that differ only in description are the same mouse setting",
            Equals(st, st with { Description = "other", Fallback = true }) && !Equals(st, st with { Brightness = 128 }));
        rgb.Effect = EffectKind.Breathing; rgb.Speed = 0;
        Check("mouse speed byte for breathing: 0% -> 21, 100% -> 1",
            MouseLightingState.From(rgb)!.Speed == 21 && MouseLightingState.From(new RgbConfig { Enabled = true, Effect = EffectKind.Breathing, Speed = 1 })!.Speed == 1);
        Check("Breathing maps once to the onboard single-colour breathing effect", MouseLightingState.From(rgb)!.Effect == 5);
        rgb.Effect = EffectKind.ThermalReactive;
        var th = MouseLightingState.From(rgb)!;
        Check("Thermal Reactive falls back to a fixed colour that is not updated with temperature", th.Fallback && th.Effect == 4 && th.Description.Contains("not updated with temperature"));
        rgb.Effect = EffectKind.Stealth;
        Check("Stealth maps to the onboard off effect", MouseLightingState.From(rgb)!.Effect == 0);
        rgb.Mouse = false;
        Check("mouse excluded or output off means no setting at all", MouseLightingState.From(rgb) == null && MouseLightingState.From(new RgbConfig { Enabled = false }) == null);

        HidInfo Info(int pid, string path) => new() { Path = path, Vid = 0x258A, Pid = pid, MaxFeature = 65, Usages = new() { (0xFFFF, 0) }, Device = null! };
        var both = new List<HidInfo> { Info(0x2022, "receiver"), Info(0x2011, "cable") };
        Check("the cable path is preferred when connected (as Glorious CORE does), else the receiver",
            GloriousTransport.Preferred(both)?.Path == "cable" && GloriousTransport.Preferred(new List<HidInfo> { Info(0x2022, "receiver") })?.Path == "receiver");
    }

    static MouseLightingController MakeMouse(FakeTransport t, MouseLightingState? last = null) =>
        new(t, listenForDeviceChanges: false, lastApplied: last)
        {
            Debounce = TimeSpan.FromMilliseconds(60),
            EnumerateInterval = TimeSpan.FromMilliseconds(20),
            Backoff = new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100) }
        };

    static void MouseControllerTests()
    {
        var red = new MouseLightingState(4, new Rgb(255, 0, 0), 11, 255, "static", false);

        var t = new FakeTransport();
        using (var c = MakeMouse(t))
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000) { c.SetDesired(red with { }); Thread.Sleep(3); }   // ~60+ FPS of an unchanged setting
            WaitUntil(() => t.Link.Applies >= 1, 2000);
            Thread.Sleep(300);
            Check("an unchanged setting posted every frame produces exactly one mouse write", t.Link.Applies == 1, $"(applies {t.Link.Applies})");
            sw.Restart();
            while (sw.ElapsedMilliseconds < 600) { c.SetDesired(red with { }); Thread.Sleep(5); }
            Check("zero recurring writes after a successful application of unchanged settings", t.Link.Applies == 1);

            for (int i = 0; i < 20; i++) { c.SetDesired(red with { Color = new Rgb((byte)(i * 10), 0, 0) }); Thread.Sleep(10); }
            WaitUntil(() => t.Link.Applies >= 2, 2000);
            Thread.Sleep(300);
            Check("a burst of 20 changes (slider drag) is debounced into one write of the final value",
                t.Link.Applies == 2 && t.Link.Last?.Color == new Rgb(190, 0, 0), $"(applies {t.Link.Applies}, last {t.Link.Last?.Color})");

            int before = t.Link.Applies, opens = t.Opens;
            for (int i = 0; i < 10; i++) { c.NotifyDeviceChange(); Thread.Sleep(30); }
            Thread.Sleep(200);
            Check("unrelated USB/HID changes (mouse interfaces unchanged) cause no mouse I/O", t.Link.Applies == before && t.Opens == opens && c.IgnoredDeviceChanges >= 1,
                $"(ignored {c.IgnoredDeviceChanges})");

            t.Path = "receiver"; t.Sig = "receiver";
            c.NotifyDeviceChange();
            WaitUntil(() => t.Link.Applies > before, 2000);
            Thread.Sleep(300);
            Check("a genuine reconnection (cable unplugged, receiver path) re-applies once", t.Link.Applies == before + 1 && c.Reconnects == 1, $"(applies {t.Link.Applies - before})");
            for (int i = 0; i < 5; i++) { c.NotifyDeviceChange(); Thread.Sleep(30); }
            Thread.Sleep(200);
            Check("no further writes after the reconnection re-apply", t.Link.Applies == before + 1);

            c.SetBlocked("Glorious CORE is running");
            c.SetDesired(red with { Color = new Rgb(0, 0, 255) });
            Thread.Sleep(400);
            int blockedApplies = t.Link.Applies;
            Check("no writes while another program owns the mouse", blockedApplies == before + 1);
            c.SetBlocked(null);
            WaitUntil(() => t.Link.Applies > blockedApplies, 2000);
            Check("the pending setting is applied once ownership returns", t.Link.Applies == blockedApplies + 1);
        }

        var tf = new FakeTransport();
        tf.Link.Fail = true;
        using (var c = MakeMouse(tf))
        {
            c.SetDesired(red);
            Thread.Sleep(1500);
            Check("failed writes are retried a bounded number of times with back-off",
                tf.Link.Applies == MouseLightingController.MaxAttempts && c.Status.Contains("gave up"), $"(attempts {tf.Link.Applies}, '{c.Status}')");
            Thread.Sleep(500);
            Check("no further retries until the setting changes or the mouse reconnects", tf.Link.Applies == MouseLightingController.MaxAttempts);
        }

        var ts = new FakeTransport();
        ts.Link.DelayMs = 800;
        using (var c = MakeMouse(ts))
        {
            c.SetDesired(red);
            Thread.Sleep(200);   // the slow write is now in progress on the mouse thread
            var sw = Stopwatch.StartNew();
            double worst = 0;
            for (int i = 0; i < 50; i++)
            {
                var one = Stopwatch.StartNew();
                c.SetDesired(red with { Color = new Rgb(0, (byte)i, 0) });
                c.SetBlocked(null);
                worst = Math.Max(worst, one.Elapsed.TotalMilliseconds);
            }
            Check("a slow mouse write never blocks the caller (lighting engine / UI)", worst < 20, $"(worst call {worst:0.0} ms during an 800 ms write)");
        }

        var tp = new FakeTransport();
        using (var c = MakeMouse(tp, last: red))
        {
            c.SetDesired(red with { });
            Thread.Sleep(500);
            Check("restarting HwSuite with the setting the mouse already has sends nothing", tp.Link.Applies == 0);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    static void SchedulerTests()
    {
        using (var s = new TestSink { DelayMs = 100 })
        {
            s.StartWorker();
            var sw = Stopwatch.StartNew();
            double worstPost = 0;
            int i = 0;
            while (sw.ElapsedMilliseconds < 1000)
            {
                var one = Stopwatch.StartNew();
                s.Post(new[] { new Rgb((byte)(i++ % 250), 0, 0) });
                worstPost = Math.Max(worstPost, one.Elapsed.TotalMilliseconds);
                Thread.Sleep(10);
            }
            long attempts = s.Attempts;
            Thread.Sleep(400);
            Check("slow device I/O: sends never overlap and the engine's post never blocks",
                s.MaxConcurrent == 1 && worstPost < 5, $"(max concurrent {s.MaxConcurrent}, worst post {worstPost:0.0} ms)");
            Check("slow device I/O: newest frame only, no catch-up burst afterwards",
                attempts <= 12 && s.Attempts - attempts <= 1 && s.FramesPosted >= 50, $"(posted {s.FramesPosted}, tried {attempts}, after stop +{s.Attempts - attempts})");
        }
        using (var s = new TestSink { Fail = true })
        {
            s.StartWorker();
            var sw = Stopwatch.StartNew();
            int i = 0;
            while (sw.ElapsedMilliseconds < 1500) { s.Post(new[] { new Rgb((byte)(i++ % 250), 1, 1) }); Thread.Sleep(10); }
            Check("a failing device faults after 3 errors and pauses instead of retrying every frame", s.Faults == 1 && s.Attempts == 3 && s.FramesSent == 0,
                $"(tried {s.Attempts}, faults {s.Faults})");
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    static void ChannelOrderTests()
    {
        var all = Enum.GetValues<ChannelOrder>();
        Check("all six strip channel orders are byte permutations", all.Length == 6 && all.All(o => { var (r, g, b) = Fusion2UsbDevice.ByteOffsets(o); return new[] { r, g, b }.OrderBy(x => x).SequenceEqual(new[] { 0, 1, 2 }); }));
        bool solved = true;
        foreach (var actual in all)
        foreach (var setting in all)
        {
            var name = actual.ToString().ToUpperInvariant();
            var (ro, go, bo) = Fusion2UsbDevice.ByteOffsets(setting);
            var s = Fusion2UsbDevice.SolveOrder(setting, name[ro], name[go], name[bo]);
            if (s != actual) solved = false;
        }
        Check("red/green/blue check: any wrong order is identified from the three observations (36 combinations)", solved);
        Check("red/green/blue check: correct observations keep the current order", Fusion2UsbDevice.SolveOrder(ChannelOrder.Grb, 'R', 'G', 'B') == ChannelOrder.Grb);
        Check("red/green/blue check: inconsistent answers are not treated as an order problem",
            Fusion2UsbDevice.SolveOrder(ChannelOrder.Grb, 'R', 'R', 'B') == null && Fusion2UsbDevice.SolveOrder(ChannelOrder.Grb, 'W', 'G', 'B') == null);
        Check("correcting red alone would be ambiguous (two orders fix red but only one fixes all three)",
            all.Count(o => Fusion2UsbDevice.ByteOffsets(o).r == Fusion2UsbDevice.ByteOffsets(ChannelOrder.Rgb).r) == 2);
    }

    // ---------------------------------------------------------------------------------------------------------
    static void CompatibilityTests()
    {
        const string v2 = """
        {
          "Version": 2, "FanControlEnabled": true, "AutoStopGigabyte": true, "StartMinimized": false, "MinimizeToTray": true, "Preset": "Balanced",
          "Channels": [
            { "Id": "gpu:0", "Label": "GPU Fan 1", "Kind": "GpuFan", "Mode": "Curve", "ManualPercent": 50,
              "Curve": { "Name": "Balanced", "Source": "Gpu", "Points": [ { "Temp": 40, "Percent": 33 }, { "Temp": 80, "Percent": 100 } ], "HysteresisC": 3, "RampUpPerSec": 20, "StepDownPerSec": 3, "MinPercent": 0, "MaxPercent": 100, "ReleaseToAutoBelowC": 0 } },
            { "Id": "lhm:/lpc/it8689e/0/control/0", "Label": "Arctic Liquid Freezer II 240 (CPU_FAN)", "Kind": "Aio", "Mode": "Curve", "ManualPercent": 70,
              "Curve": { "Name": "Balanced", "Source": "Cpu", "Points": [ { "Temp": 30, "Percent": 65 }, { "Temp": 80, "Percent": 100 } ] } },
            { "Id": "lhm:/lpc/it8689e/0/control/1", "Label": "Front fans", "Kind": "CaseFan", "Mode": "Curve", "ManualPercent": 50,
              "Curve": { "Name": "Custom", "Source": "MaxOfBoth", "Points": [ { "Temp": 30, "Percent": 25 }, { "Temp": 80, "Percent": 100 } ] } }
          ],
          "Rgb": { "Effect": "GradientWave", "ColorA": 16711680, "ColorB": 16447, "Speed": 0.5, "Brightness": 1, "Enabled": true, "Motherboard": true, "Mouse": true, "Ram": true,
                   "DLedStrip": true, "DLedCount": 27, "StripOrder": "Bgr", "DLedMaxHz": 60, "MouseMaxHz": 1, "MotherboardMaxHz": 20 }
        }
        """;
        var c = ConfigStore.Parse(v2)!;
        Check("a Phase 6 (v2) config loads and migrates to the current version", c.Version == 4 && c.Channels.Count == 3 && c.FanControlEnabled && c.Rgb.EffectiveDLedHz == 60);
        Check("existing lighting settings are kept", c.Rgb.StripOrder == ChannelOrder.Bgr && c.Rgb.DLedCount == 27 && c.Rgb.Effect == EffectKind.GradientWave && c.Rgb.Enabled);
        Check("existing fan settings are kept (AIO curve, GPU curve, names)",
            c.Channels[1].Kind == FanKind.Aio && c.Channels[1].Mode == ChannelMode.Curve && c.Channels[0].Curve.Points.Count == 2 && c.Channels[2].Label == "Front fans");
        var front = new FakeChannel("lhm:/lpc/it8689e/0/control/1", FanKind.Generic);
        Check("a case fan curve from v2 stays configured but is not driven until its minimum is confirmed", !FanSafety.Assess(c.Channels[2], front).Controllable && c.Channels[2].Mode == ChannelMode.Curve);
        Check("new options default safely (service restore on, no confirmed minimums)", c.RestoreGigabyteOnExit && c.Channels.All(x => x.VerifiedMinPercent == null));
        c.Rgb.StripOrder = ChannelOrder.Gbr;
        var round = ConfigStore.Parse(ConfigStore.ToJson(c))!;
        Check("new channel orders survive a save/load round trip", round.Rgb.StripOrder == ChannelOrder.Gbr);
        Check("startup task target is read back from the task XML",
            StartupTask.TargetFromXml(StartupTask.BuildXml(@".\dist\HwSuite\HwSuite.exe", @"PC\me")) == @".\dist\HwSuite\HwSuite.exe" &&
            StartupTask.SameExecutable(@".\dist\HwSuite\HwSuite.exe", Path.GetFullPath(@".\dist\HwSuite\HwSuite.exe").ToUpperInvariant()));
    }
}
