using HwSuite.Core;
using LibreHardwareMonitor.Hardware;

namespace HwSuite.Hardware;

public sealed class LhmSensor
{
    public IHardware Hardware { get; init; } = null!;
    public ISensor Sensor { get; init; } = null!;
    public string Id => Sensor.Identifier.ToString();
    public string HardwareName => Hardware.Name;
    public HardwareType HardwareType => Hardware.HardwareType;
    public SensorType Type => Sensor.SensorType;
    public string Name => Sensor.Name;
    public int Index => Sensor.Index;
    public double? Value => Sensor.Value is float f ? f : null;
}

/// <summary>
/// LibreHardwareMonitorLib wrapper (CPU temperatures, motherboard Super I/O temperatures, fan tachometers and PWM controls).
/// Ring-0 access goes exclusively through the signed PawnIO driver. The GPU is deliberately disabled here:
/// NVML (NvmlGpu) is the single owner of the NVIDIA fans.
/// </summary>
public sealed class LhmService : IDisposable
{
    public object Gate { get; } = new();
    Computer? _pc;
    readonly List<LhmSensor> _sensors = new();

    public bool IsOpen => _pc != null;
    public string? Error { get; private set; }
    public IReadOnlyList<LhmSensor> Sensors { get { lock (Gate) return _sensors.ToList(); } }

    public bool Open()
    {
        if (RunMode.Refuse("open LibreHardwareMonitor")) { Error = "hardware-free run"; return false; }
        lock (Gate)
        {
            try
            {
                _pc = new Computer
                {
                    IsCpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsGpuEnabled = false,
                    IsControllerEnabled = false,
                    IsMemoryEnabled = false,
                    IsStorageEnabled = false,
                    IsNetworkEnabled = false,
                    IsBatteryEnabled = false,
                    IsPsuEnabled = false
                };
                _pc.Open();
                Rebuild();
                UpdateLocked();
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.GetType().Name + ": " + ex.Message;
                Log.Error("LibreHardwareMonitor open failed", ex);
                try { _pc?.Close(); } catch { }
                _pc = null;
                return false;
            }
        }
    }

    void Rebuild()
    {
        _sensors.Clear();
        if (_pc == null) return;
        foreach (var hw in _pc.Hardware) Collect(hw);
    }

    void Collect(IHardware hw)
    {
        foreach (var s in hw.Sensors) _sensors.Add(new LhmSensor { Hardware = hw, Sensor = s });
        foreach (var sub in hw.SubHardware) Collect(sub);
    }

    public void Update()
    {
        lock (Gate) UpdateLocked();
    }

    /// <summary>Hardware types whose Update() threw during the most recent update (their sensor values are stale).</summary>
    public HashSet<HardwareType> LastUpdateFailures { get; } = new();

    void UpdateLocked()
    {
        if (_pc == null) return;
        LastUpdateFailures.Clear();
        foreach (var hw in _pc.Hardware) UpdateHw(hw);
        // LibreHardwareMonitor activates Super I/O sensors lazily, so refresh the cached list after every update.
        Rebuild();
    }

    void UpdateHw(IHardware hw)
    {
        try { hw.Update(); }
        catch (Exception ex)
        {
            LastUpdateFailures.Add(hw.HardwareType);
            Log.Warn($"LHM update failed for {hw.Name}: {ex.Message}");
        }
        foreach (var sub in hw.SubHardware) UpdateHw(sub);
    }

    /// <summary>Text dump used by diagnostics.</summary>
    public List<string> Describe()
    {
        var lines = new List<string>();
        lock (Gate)
        {
            if (_pc == null) { lines.Add("LHM not open: " + Error); return lines; }
            void Walk(IHardware hw, int depth)
            {
                lines.Add($"{new string(' ', depth * 2)}[{hw.HardwareType}] {hw.Name}  id={hw.Identifier}");
                foreach (var s in hw.Sensors.OrderBy(x => x.SensorType).ThenBy(x => x.Index))
                    lines.Add($"{new string(' ', depth * 2 + 2)}{s.SensorType,-12} {s.Name,-34} = {(s.Value.HasValue ? s.Value.Value.ToString("0.##") : "n/a"),8}  control={(s.Control != null ? s.Control.ControlMode.ToString() : "-")}  id={s.Identifier}");
                foreach (var sub in hw.SubHardware) Walk(sub, depth + 1);
            }
            foreach (var hw in _pc.Hardware) Walk(hw, 0);
        }
        return lines;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            try { _pc?.Close(); } catch { }
            _pc = null;
            _sensors.Clear();
        }
    }
}
