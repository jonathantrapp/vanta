// PORTED from src\HwSuite\Hardware\TelemetryHub.cs (Windows 1.20.0). Same public surface except: `Lhm` (LibreHardwareMonitor)
// becomes `Hwmon` (Linux sysfs), PawnIO properties report "not used on Linux". Loop, snapshot fields, ClassifyCpuTemp and
// the fan-engine drive are unchanged.
using System.Diagnostics;
using HwSuite.Core;
using HwSuite.Fans;

namespace HwSuite.Hardware;

/// <summary>Owns every telemetry source, produces one snapshot per second and drives the fan engine.</summary>
public sealed class TelemetryHub : IDisposable
{
    readonly AppConfig _cfg;
    readonly SystemStats _sys = new();
    readonly FreezeDetector _cpuPowerFreeze = new(15);
    readonly RaplPower _rapl = new();
    bool _suspicionLogged;
    Heartbeat? _heartbeat;
    Thread? _thread;
    volatile bool _stop;

    public OwnershipArbiter? Arbiter { get; set; }

    public NvmlGpu? Gpu { get; private set; }
    public HwmonService Hwmon { get; } = new();
    public FanEngine Engine { get; }
    public TelemetrySnapshot Latest { get; internal set; } = new();
    public List<string> Notes { get; } = new();
    public bool PawnIoInstalled { get; internal set; }   // always false on Linux (fixtures may set it)
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
        Gpu = NvmlGpu.TryOpen(out var err);
        if (Gpu == null) Notes.Add("NVML unavailable: " + err);
        else Notes.Add($"NVML: {Gpu.Name}, {Gpu.FanCount} fan(s), manual range {Gpu.MinFanPercent}-{Gpu.MaxFanPercent}%");

        if (Hwmon.Open())
        {
            var chips = Hwmon.Chips;
            Notes.Add($"hwmon: {chips.Count} chip(s): {string.Join(", ", chips.Select(c => c.Name))}; {Hwmon.Sensors.Count} sensors");
            if (!chips.Any(c => c.IsSuperIo))
                Notes.Add("hwmon: no Super I/O chip (it87) found: motherboard fans unavailable. See linux/PORTING_STATUS.md (IT8689E driver).");
            if (!chips.Any(c => c.Name == "coretemp")) Notes.Add("hwmon: coretemp not loaded: no CPU temperature");
            if (!LinuxFanGate.MotherboardAllowed(out var why)) Notes.Add("motherboard fans stay on BIOS control: " + why);
        }
        else Notes.Add("hwmon failed: " + Hwmon.Error);
        if (_rapl.Available) Notes.Add("CPU package power: intel-rapl powercap");

        BuildChannels();
        Latest = Poll();
    }

    void BuildChannels()
    {
        Engine.Channels.Clear();
        if (Gpu != null)
            for (int i = 0; i < Gpu.FanCount; i++) Engine.Channels.Add(new GpuFanChannel(Gpu, i));

        if (Hwmon.IsOpen)
        {
            var all = Hwmon.Sensors;
            foreach (var p in all.Where(s => s.Kind == HwmonKind.Pwm && s.Chip.IsSuperIo))
            {
                var fan = all.FirstOrDefault(f => f.Kind == HwmonKind.Fan && f.Chip == p.Chip && f.Number == p.Number);
                Engine.Channels.Add(new HwmonFanChannel(Hwmon, p, fan));
            }
        }
    }

    public void Start(bool heartbeat = false)
    {
        if (heartbeat) _heartbeat = Heartbeat.CreateForCurrentProcess();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Telemetry" };
        _thread.Start();
    }

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

        s.CpuLoadPct = _sys.CpuLoadPercent();
        s.CpuClockMHz = _sys.CpuAverageMHz() ?? _sys.CpuNominalMHz();
        if (_sys.Memory() is var (pct, used, total)) { s.RamUsedPct = pct; s.RamUsedGb = used; s.RamTotalGb = total; }
        s.CpuPowerW = _rapl.ReadWatts();

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

        if (Hwmon.IsOpen)
        {
            lock (Hwmon.Gate)
            {
                Hwmon.Update();
                var all = Hwmon.Sensors;
                var core = all.Where(x => x.Chip.Name == "coretemp" && x.Kind == HwmonKind.Temperature).ToList();
                // coretemp: "Package id 0" is the package sensor (what LHM calls "CPU Package"); "Core N" are the cores
                var pkg = core.FirstOrDefault(x => x.Label.StartsWith("Package id", StringComparison.Ordinal));
                bool pkgFailed = pkg != null && Hwmon.LastUpdateFailures.Contains(pkg.Path);
                if (pkg?.Value is double pv) { s.CpuTempC = pv; s.CpuTempSource = "coretemp " + pkg.Label; }
                else
                {
                    var cores = core.Where(x => x.Label.StartsWith("Core ", StringComparison.Ordinal) && x.Value.HasValue).Select(x => x.Value!.Value).ToList();
                    if (cores.Count > 0) { s.CpuTempC = cores.Max(); s.CpuTempSource = "coretemp hottest core"; }
                }
                // a failed sysfs read gives null (never a stale value), so "update failed" maps onto the same evidence rule
                (s.CpuTempC, s.CpuTempStatus) = ClassifyCpuTemp(s.CpuTempC, pkgFailed && s.CpuTempC == null);
                if (core.Count == 0) s.CpuTempStatus = "coretemp not loaded";
                if (s.CpuTempC != null) s.CpuTempReadUtc = DateTime.UtcNow;

                if (_cpuPowerFreeze.Frozen(s.CpuPowerW))
                    s.CpuTempSuspicion = $"CPU package power has repeated exactly for {_cpuPowerFreeze.SameCount} readings; CPU sensor values may not be updating";
                if ((s.CpuTempSuspicion != null) != _suspicionLogged)
                {
                    _suspicionLogged = s.CpuTempSuspicion != null;
                    if (_suspicionLogged) Log.Warn("telemetry: " + s.CpuTempSuspicion); else Log.Info("telemetry: CPU package power is changing again");
                }

                foreach (var x in all.Where(x => x.Chip.IsSuperIo))
                {
                    if (x.Kind == HwmonKind.Temperature)
                        s.Temps.Add(new TempReading { Id = x.Id, Name = x.Label, Source = x.Chip.DisplayName, Celsius = x.Value });
                    else if (x.Kind == HwmonKind.Fan)
                    {
                        var ctl = all.FirstOrDefault(c => c.Kind == HwmonKind.Pwm && c.Chip == x.Chip && c.Number == x.Number);
                        s.Fans.Add(new FanReading { Id = x.Id, Name = x.Label, Source = x.Chip.DisplayName, Rpm = x.Value, Percent = ctl?.Value });
                    }
                }
            }
        }
        else s.CpuTempStatus = "hwmon not open";
        return s;
    }

    /// <summary>Unchanged rule from Windows: a CPU temperature is used for control only with read-success evidence.</summary>
    public static (double? temp, string status) ClassifyCpuTemp(double? lhmValue, bool cpuUpdateFailed)
    {
        if (lhmValue == null) return (null, "no valid reading (the coretemp read failed)");
        if (cpuUpdateFailed) return (null, "stale: the CPU sensor update failed this cycle");
        return (lhmValue, "ok");
    }

    public void Dispose()
    {
        _stop = true;
        try { _thread?.Join(2000); } catch { }
        Engine.Dispose();     // releases every fan to firmware control first
        Hwmon.Dispose();
        Gpu?.Dispose();
        _heartbeat?.Dispose();
    }
}
