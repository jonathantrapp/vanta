using System.Text.Json;
using HwSuite.Core;
using HwSuite.Hardware;
using LibreHardwareMonitor.Hardware;

namespace HwSuite.Fans;

/// <summary>
/// Persists which channels this process has taken manual control of, so a watchdog process can hand them back to
/// the firmware if the application is killed or crashes.
/// </summary>
public static class ControlRegistry
{
    static readonly object G = new();

    public sealed class Entry
    {
        public string Backend { get; set; } = "";
        public string Id { get; set; } = "";
    }

    public static List<Entry> Load()
    {
        lock (G)
        {
            try
            {
                if (File.Exists(AppPaths.ControlledState))
                    return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(AppPaths.ControlledState)) ?? new();
            }
            catch { }
            return new();
        }
    }

    static void Save(List<Entry> list)
    {
        try { File.WriteAllText(AppPaths.ControlledState, JsonSerializer.Serialize(list)); } catch { }
    }

    public static void Mark(string backend, string id)
    {
        lock (G)
        {
            var l = Load();
            if (!l.Any(e => e.Backend == backend && e.Id == id)) { l.Add(new Entry { Backend = backend, Id = id }); Save(l); }
        }
    }

    public static void Unmark(string backend, string id)
    {
        lock (G)
        {
            var l = Load();
            if (l.RemoveAll(e => e.Backend == backend && e.Id == id) > 0) Save(l);
        }
    }

    public static void Clear() { lock (G) Save(new List<Entry>()); }
}

public interface IFanChannel
{
    string Id { get; }
    string Name { get; }
    string Backend { get; }
    FanKind DefaultKind { get; }
    bool CanControl { get; }
    /// <summary>True when a live tachometer signal is available for this channel (fan hubs often return none).</summary>
    bool HasTachSignal { get; }
    /// <summary>True when the hardware has a tachometer input paired with this control (whether or not it reads a speed now).</summary>
    bool HasTachSensor { get; }
    double MinPercent { get; }
    double? ReadRpm();
    double? ReadPercent();
    bool SetPercent(double percent);
    bool ReleaseToAuto();
    /// <summary>Return the channel to exactly the state it had before our first write.</summary>
    bool RestoreOriginal();
    /// <summary>Drop HwSuite's claim on the channel without writing to it (another program now controls it), so the watchdog will not touch it either.</summary>
    void Relinquish();
    /// <summary>Which hardware this channel belongs to, for ownership decisions.</summary>
    HwResource Resource { get; }
    /// <summary>The duty the hardware is currently targeting, when the backend can report it (NVML). Used to see another program's writes.</summary>
    int? ReadTargetPercent();
    /// <summary>Id of this channel's tachometer reading in the telemetry snapshot (null: no tachometer input).</summary>
    string? TachReadingId => Id;
}

public sealed class GpuFanChannel : IFanChannel
{
    readonly NvmlGpu _gpu;
    readonly int _index;
    public GpuFanChannel(NvmlGpu gpu, int index) { _gpu = gpu; _index = index; }

    public string Id => $"gpu:{_index}";
    public string Name => $"GPU Fan {_index + 1}";
    public string Backend => "NVML";
    public FanKind DefaultKind => FanKind.GpuFan;
    public bool CanControl => true;
    public bool HasTachSignal => true;
    public bool HasTachSensor => true;
    public double MinPercent => _gpu.MinFanPercent;
    public double? ReadRpm() => _gpu.ReadFan(_index).Rpm;
    public double? ReadPercent() => _gpu.ReadFan(_index).SpeedPct;
    public bool SetPercent(double percent) => _gpu.SetFanPercent(_index, percent);
    public bool ReleaseToAuto() => _gpu.ReleaseToAuto(_index);
    public bool RestoreOriginal() => _gpu.RestoreOriginal(_index);
    public void Relinquish() => ControlRegistry.Unmark("nvml", _index.ToString());
    public HwResource Resource => HwResource.GpuFans;
    public int? ReadTargetPercent() => _gpu.ReadFan(_index).TargetPct;
    public int TargetPercent => _gpu.ReadFan(_index).TargetPct;
    public int Policy => _gpu.ReadFan(_index).Policy;
}

public sealed class LhmFanChannel : IFanChannel
{
    readonly LhmService _lhm;
    readonly LhmSensor _control;
    readonly LhmSensor? _fan;
    ControlMode? _origMode;
    float _origValue;

    public LhmFanChannel(LhmService lhm, LhmSensor control, LhmSensor? fan)
    {
        _lhm = lhm; _control = control; _fan = fan;
    }

    public string Id => "lhm:" + _control.Id;
    public string Name => $"{_control.HardwareName} {_control.Name}";
    public string Backend => "SuperIO (PawnIO)";
    public FanKind DefaultKind => FanKind.Generic;
    public bool CanControl => _control.Sensor.Control != null;
    public bool HasTachSignal { get { lock (_lhm.Gate) return _fan?.Value is double v && v > 0; } }
    public bool HasTachSensor => _fan != null;
    public double MinPercent => _control.Sensor.Control?.MinSoftwareValue ?? 0;
    public string ControlSensorId => _control.Id;
    public string? FanSensorId => _fan?.Id;
    public string? TachReadingId => _fan?.Id;

    public double? ReadRpm() { lock (_lhm.Gate) return _fan?.Value; }
    public double? ReadPercent() { lock (_lhm.Gate) return _control.Value; }

    public bool SetPercent(double percent)
    {
        lock (_lhm.Gate)
        {
            var c = _control.Sensor.Control;
            if (c == null) return false;
            if (_origMode == null) { _origMode = c.ControlMode; _origValue = c.SoftwareValue; }
            c.SetSoftware((float)Math.Clamp(percent, c.MinSoftwareValue, c.MaxSoftwareValue));
        }
        ControlRegistry.Mark("lhm", _control.Id);
        return true;
    }

    public bool ReleaseToAuto()
    {
        // TryEnter: the stall guard calls this from another thread, and must not hang behind a stuck hardware read
        if (!Monitor.TryEnter(_lhm.Gate, TimeSpan.FromSeconds(3)))
        {
            Log.Error($"release {Id}: hardware lock busy for 3 s, header left as it is");
            return false;
        }
        try { _control.Sensor.Control?.SetDefault(); _origMode = null; }
        finally { Monitor.Exit(_lhm.Gate); }
        ControlRegistry.Unmark("lhm", _control.Id);
        return true;
    }

    public void Relinquish()
    {
        _origMode = null;
        ControlRegistry.Unmark("lhm", _control.Id);
    }

    public HwResource Resource => HwResource.SuperIo;
    public int? ReadTargetPercent() => null;

    public bool RestoreOriginal()
    {
        lock (_lhm.Gate)
        {
            var c = _control.Sensor.Control;
            if (c == null) return false;
            if (_origMode == ControlMode.Software) c.SetSoftware(_origValue); else c.SetDefault();
            _origMode = null;
        }
        ControlRegistry.Unmark("lhm", _control.Id);
        return true;
    }
}
