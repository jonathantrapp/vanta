// PORTED from src\HwSuite\Fans\FanChannels.cs (Windows 1.20.0). Unchanged: ControlRegistry, IFanChannel, GpuFanChannel.
// Changed: LhmFanChannel (LibreHardwareMonitor) is replaced by HwmonFanChannel (Linux sysfs pwmN / pwmN_enable).
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Hardware;
using HwSuite.Platform;

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

/// <summary>
/// LINUX: one motherboard PWM output through hwmon sysfs (pwmN = duty 0-255, pwmN_enable: 0 full speed, 1 manual,
/// 2+ the chip's automatic modes; it87 semantics). The original enable mode and duty are kept from before the first
/// write and put back by <see cref="RestoreOriginal"/>; <see cref="ReleaseToAuto"/> returns to the chip's automatic
/// (BIOS curve) mode. Writing needs root (the vantad service). Blocked until <see cref="LinuxFanGate"/> says the header
/// map was verified on Linux.
/// </summary>
public sealed class HwmonFanChannel : IFanChannel
{
    readonly HwmonService _hw;
    readonly HwmonSensor _pwm;
    readonly HwmonSensor? _fan;
    int? _origEnable;
    long _origDuty;

    public HwmonFanChannel(HwmonService hw, HwmonSensor pwm, HwmonSensor? fan) { _hw = hw; _pwm = pwm; _fan = fan; }

    string EnablePath => _pwm.Path + "_enable";
    public string Id => "lhm:" + _pwm.Id;            // same id as Windows, so saved curves carry over (see HwmonSensor.Id)
    public string Name => $"{_pwm.Chip.DisplayName} {_pwm.Label}";
    public string Backend => "hwmon (" + _pwm.Chip.Name + ")";
    public FanKind DefaultKind => FanKind.Generic;
    public bool CanControl => File.Exists(EnablePath) && LinuxFanGate.MotherboardAllowed(out _);
    public bool HasTachSignal { get { lock (_hw.Gate) return _fan?.Value is double v && v > 0; } }
    public bool HasTachSensor => _fan != null;
    public double MinPercent => 0;
    public string ControlSensorId => _pwm.Id;
    public string? FanSensorId => _fan?.Id;
    public string? TachReadingId => _fan?.Id;
    public string PwmPath => _pwm.Path;

    public double? ReadRpm() { lock (_hw.Gate) return _fan?.Value; }
    public double? ReadPercent() { lock (_hw.Gate) return _pwm.Value; }

    public bool SetPercent(double percent)
    {
        if (!LinuxFanGate.MotherboardAllowed(out var why)) { Log.Warn($"{Id}: not driven: {why}"); return false; }
        lock (_hw.Gate)
        {
            if (_origEnable == null)
            {
                var e = Sysfs.ReadLong(EnablePath); var d = Sysfs.ReadLong(_pwm.Path);
                if (e == null || d == null) { Log.Error($"{Id}: cannot read {EnablePath}"); return false; }
                _origEnable = (int)e.Value; _origDuty = d.Value;
            }
            if (Sysfs.ReadLong(EnablePath) != 1 && !Sysfs.WriteText(EnablePath, "1", out var err1)) { Log.Error($"{Id}: manual mode: {err1}"); return false; }
            int duty = (int)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100);
            if (!Sysfs.WriteText(_pwm.Path, duty.ToString(), out var err2)) { Log.Error($"{Id}: duty: {err2}"); return false; }
        }
        ControlRegistry.Mark("hwmon", _pwm.Path);
        return true;
    }

    public bool ReleaseToAuto()
    {
        if (!Monitor.TryEnter(_hw.Gate, TimeSpan.FromSeconds(3)))
        {
            Log.Error($"release {Id}: hardware lock busy for 3 s, header left as it is");
            return false;
        }
        bool ok;
        try
        {
            string mode = _origEnable is int o && o >= 2 ? o.ToString() : "2";
            ok = Sysfs.WriteText(EnablePath, mode, out var err);
            if (!ok) Log.Error($"release {Id}: {err}");
            _origEnable = null;
        }
        finally { Monitor.Exit(_hw.Gate); }
        if (ok) ControlRegistry.Unmark("hwmon", _pwm.Path);
        return ok;
    }

    public void Relinquish()
    {
        _origEnable = null;
        ControlRegistry.Unmark("hwmon", _pwm.Path);
    }

    public HwResource Resource => HwResource.SuperIo;
    public int? ReadTargetPercent() => null;

    public bool RestoreOriginal()
    {
        bool ok = true;
        lock (_hw.Gate)
        {
            if (_origEnable is int e)
            {
                if (e == 1) ok &= Sysfs.WriteText(_pwm.Path, _origDuty.ToString(), out _);
                ok &= Sysfs.WriteText(EnablePath, e.ToString(), out _);
            }
            _origEnable = null;
        }
        if (ok) ControlRegistry.Unmark("hwmon", _pwm.Path);
        return ok;
    }

    /// <summary>Failsafe path (watchdog / service stop): put one recorded pwm back to automatic without any state.</summary>
    public static bool ForceAuto(string pwmPath, out string? error) => Sysfs.WriteText(pwmPath + "_enable", "2", out error);
}
