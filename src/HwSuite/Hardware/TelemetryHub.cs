using System.Diagnostics;
using HwSuite.Core;
using HwSuite.Fans;
using LibreHardwareMonitor.Hardware;

namespace HwSuite.Hardware;

/// <summary>Owns every telemetry source, produces one snapshot per second and drives the fan engine.</summary>
public sealed class TelemetryHub : IDisposable
{
    readonly AppConfig _cfg;
    readonly SystemStats _sys = new();
    /// <summary>Warning heuristic: 15 identical package-power samples in a row (see TelemetrySnapshot.CpuTempSuspicion).</summary>
    readonly FreezeDetector _cpuPowerFreeze = new(15);
    bool _suspicionLogged;
    Heartbeat? _heartbeat;
    Thread? _thread;
    volatile bool _stop;

    /// <summary>Refreshed every two seconds on the telemetry thread, before the fan engine runs.</summary>
    public OwnershipArbiter? Arbiter { get; set; }

    public NvmlGpu? Gpu { get; private set; }
    public LhmService Lhm { get; } = new();
    public FanEngine Engine { get; }
    public TelemetrySnapshot Latest { get; internal set; } = new();
    public List<string> Notes { get; } = new();
    public bool PawnIoInstalled { get; internal set; }
    public string? PawnIoVersion { get; internal set; }
    public int IntervalMs { get; set; } = 1000;
    public event Action<TelemetrySnapshot>? Updated;

    public TelemetryHub(AppConfig cfg)
    {
        _cfg = cfg;
        Engine = new FanEngine(cfg);
    }

    public void Initialize()
    {
        PawnIoInstalled = PawnIoNative.IsInstalled();
        PawnIoVersion = PawnIoInstalled ? PawnModule.PawnIoVersionString() : null;
        Notes.Add(PawnIoInstalled ? $"PawnIO {PawnIoVersion ?? "?"} detected" : "PawnIO not installed: Super I/O, CPU temperature and SMBus RAM lighting unavailable");

        Gpu = NvmlGpu.TryOpen(out var err);
        if (Gpu == null) Notes.Add("NVML unavailable: " + err);
        else Notes.Add($"NVML: {Gpu.Name}, {Gpu.FanCount} fan(s), manual range {Gpu.MinFanPercent}-{Gpu.MaxFanPercent}%");

        if (Lhm.Open()) Notes.Add($"LibreHardwareMonitor open, {Lhm.Sensors.Count} sensors");
        else Notes.Add("LibreHardwareMonitor failed: " + Lhm.Error);

        BuildChannels();
        Latest = Poll();
    }

    void BuildChannels()
    {
        Engine.Channels.Clear();
        if (Gpu != null)
            for (int i = 0; i < Gpu.FanCount; i++) Engine.Channels.Add(new GpuFanChannel(Gpu, i));

        if (Lhm.IsOpen)
        {
            var all = Lhm.Sensors;
            foreach (var c in all.Where(s => s.Type == SensorType.Control && s.Sensor.Control != null))
            {
                var fan = all.FirstOrDefault(f => f.Type == SensorType.Fan && ReferenceEquals(f.Hardware, c.Hardware) && f.Index == c.Index);
                Engine.Channels.Add(new LhmFanChannel(Lhm, c, fan));
            }
        }
    }

    public void Start(bool heartbeat = false)
    {
        if (heartbeat) _heartbeat = Heartbeat.CreateForCurrentProcess();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Telemetry" };
        _thread.Start();
    }

    /// <summary>True when NVML reports a GPU fan in manual mode that HwSuite is not driving (another program set it).</summary>
    public bool GpuHasForeignManual()
    {
        if (Gpu == null) return false;
        for (int i = 0; i < Gpu.FanCount; i++)
            if (Gpu.ReadFan(i).Policy == 1 && !Engine.IsControlled($"gpu:{i}")) return true;
        return false;
    }

    void Loop()
    {
        ThreadCpu.Register("telemetry + fan engine");
        var sw = Stopwatch.StartNew();
        double last = 0, lastOwnership = -10;
        while (!_stop)
        {
            double t0 = sw.Elapsed.TotalSeconds;
            try
            {
                if (Arbiter != null && t0 - lastOwnership >= 2)
                {
                    lastOwnership = t0;
                    try { Arbiter.Refresh(); } catch (Exception ex) { Log.Error("ownership refresh", ex); }
                }
                var snap = Poll();
                Latest = snap;
                try { Updated?.Invoke(snap); } catch { }
                double dt = Math.Clamp(t0 - last, 0.1, 5);
                Engine.Tick(snap, dt);
                _heartbeat?.Beat();
            }
            catch (Exception ex) { Log.Error("telemetry loop", ex); }
            last = t0;
            int sleep = IntervalMs - (int)((sw.Elapsed.TotalSeconds - t0) * 1000);
            if (sleep > 0) Thread.Sleep(sleep);
        }
    }

    public TelemetrySnapshot Poll()
    {
        var s = new TelemetrySnapshot();

        // driverless baseline
        s.CpuLoadPct = _sys.CpuLoadPercent();
        s.CpuClockMHz = _sys.CpuNominalMHz();
        if (_sys.Memory() is var (pct, used, total)) { s.RamUsedPct = pct; s.RamUsedGb = used; s.RamTotalGb = total; }

        if (Gpu != null)
        {
            var g = Gpu.Read();
            s.GpuName = g.Name; s.GpuTempC = g.TempC; s.GpuLoadPct = g.LoadPct; s.GpuCoreMHz = g.CoreMHz;
            s.GpuMemMHz = g.MemMHz; s.GpuPowerW = g.PowerW;
            s.GpuTempStatus = g.TempC.HasValue ? "ok" : "NVML temperature read failed";
            foreach (var f in g.Fans)
                s.Fans.Add(new FanReading { Id = $"gpu:{f.Index}", Name = $"GPU Fan {f.Index + 1}", Source = "NVML", Rpm = f.Rpm, Percent = f.SpeedPct });
        }
        else s.GpuTempStatus = "NVML unavailable";

        if (Lhm.IsOpen)
        {
            // Keep values and failure evidence from the same update even if a UI diagnostic polls concurrently.
            lock (Lhm.Gate)
            {
            Lhm.Update();
            var all = Lhm.Sensors;
            var cpu = all.Where(x => x.HardwareType == HardwareType.Cpu).ToList();

            var pkg = cpu.FirstOrDefault(x => x.Type == SensorType.Temperature && x.Name == "CPU Package")
                      ?? cpu.FirstOrDefault(x => x.Type == SensorType.Temperature && x.Name == "Core Max");
            if (pkg?.Value is double pv) { s.CpuTempC = pv; s.CpuTempSource = "LHM " + pkg.Name; }
            else
            {
                var cores = cpu.Where(x => x.Type == SensorType.Temperature && x.Name.StartsWith("Core #") && !x.Name.Contains("Distance"))
                               .Select(x => x.Value).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                if (cores.Count > 0) { s.CpuTempC = cores.Max(); s.CpuTempSource = "LHM hottest core"; }
            }

            var cpuTotal = cpu.FirstOrDefault(x => x.Type == SensorType.Load && x.Name == "CPU Total");
            if (cpuTotal?.Value is double tl) s.CpuLoadPct = tl;

            var clocks = cpu.Where(x => x.Type == SensorType.Clock && x.Name.StartsWith("Core #") && !x.Name.Contains("Effective"))
                            .Select(x => x.Value).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (clocks.Count > 0) s.CpuClockMHz = clocks.Average();

            var pw = cpu.FirstOrDefault(x => x.Type == SensorType.Power && x.Name == "CPU Package");
            if (pw?.Value is double pwv) s.CpuPowerW = pwv;

            // Read-success evidence. LibreHardwareMonitorLib 0.9.6 (IntelCpu.Update, verified in its IL) sets the package and
            // core temperatures to null whenever the thermal-status MSR read (0x1B1 / 0x19C) fails or its "reading valid"
            // bit 31 is clear, so a failed read arrives here as a missing value. If the CPU's Update() throws, LHM keeps
            // the previous values: that is the stale case, detected from the exception.
            (s.CpuTempC, s.CpuTempStatus) = ClassifyCpuTemp(s.CpuTempC, Lhm.LastUpdateFailures.Contains(HardwareType.Cpu));
            if (s.CpuTempC != null) s.CpuTempReadUtc = DateTime.UtcNow;

            // Secondary heuristic only: package power repeating exactly is suspicious, but neither proves the temperature
            // stale nor (when it changes) proves it fresh. It is reported, never used for control.
            if (_cpuPowerFreeze.Frozen(s.CpuPowerW))
                s.CpuTempSuspicion = $"CPU package power has repeated exactly for {_cpuPowerFreeze.SameCount} readings; CPU sensor values may not be updating";
            if ((s.CpuTempSuspicion != null) != _suspicionLogged)
            {
                _suspicionLogged = s.CpuTempSuspicion != null;
                if (_suspicionLogged) Log.Warn("telemetry: " + s.CpuTempSuspicion); else Log.Info("telemetry: CPU package power is changing again");
            }

            foreach (var x in all.Where(x => x.HardwareType is HardwareType.SuperIO or HardwareType.EmbeddedController or HardwareType.Motherboard))
            {
                if (x.Type == SensorType.Temperature)
                    s.Temps.Add(new TempReading { Id = x.Id, Name = x.Name, Source = x.HardwareName, Celsius = x.Value });
                else if (x.Type == SensorType.Fan)
                {
                    var ctl = all.FirstOrDefault(c => c.Type == SensorType.Control && ReferenceEquals(c.Hardware, x.Hardware) && c.Index == x.Index);
                    s.Fans.Add(new FanReading { Id = x.Id, Name = x.Name, Source = x.HardwareName, Rpm = x.Value, Percent = ctl?.Value });
                }
            }
            }
        }
        else s.CpuTempStatus = "LibreHardwareMonitor not open";
        return s;
    }

    /// <summary>
    /// Use a CPU temperature for control only with read-success evidence: LHM supplied a value (it reports null when the
    /// MSR read fails or is flagged invalid) and the CPU's Update() did not throw this cycle (if it throws, LHM keeps the
    /// previous, now stale, value). Nothing here infers freshness from other sensors.
    /// </summary>
    public static (double? temp, string status) ClassifyCpuTemp(double? lhmValue, bool cpuUpdateFailed)
    {
        if (lhmValue == null) return (null, "no valid reading (the thermal-status MSR read failed or was flagged invalid)");
        if (cpuUpdateFailed) return (null, "stale: the CPU sensor update failed this cycle");
        return (lhmValue, "ok");
    }

    public void Dispose()
    {
        _stop = true;
        try { _thread?.Join(2000); } catch { }
        Engine.Dispose();     // releases every fan to firmware control first
        Lhm.Dispose();
        Gpu?.Dispose();
        _heartbeat?.Dispose();
    }
}

