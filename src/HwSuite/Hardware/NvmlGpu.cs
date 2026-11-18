using System.Runtime.InteropServices;
using System.Text;
using HwSuite.Core;
using HwSuite.Fans;

namespace HwSuite.Hardware;

public sealed class GpuFanState
{
    public int Index { get; set; }
    public int Policy { get; set; }      // NVML: 0 = temperature-continuous (VBIOS auto), 1 = manual
    public int TargetPct { get; set; }
    public int SpeedPct { get; set; }
    public int? Rpm { get; set; }
}

public sealed class GpuSample
{
    public string Name { get; set; } = "";
    public double? TempC { get; set; }
    public double? CoreMHz { get; set; }
    public double? MemMHz { get; set; }
    public double? LoadPct { get; set; }
    public double? PowerW { get; set; }
    public List<GpuFanState> Fans { get; } = new();
}

/// <summary>
/// NVIDIA GPU access through the official NVML library (nvml.dll ships with the driver).
/// Every entry point used here was confirmed in nvml.dll's export table, and fan writes were
/// verified live on this RTX 3070 FE (2 independent fans).
/// </summary>
public sealed class NvmlGpu : IDisposable
{
    static class N
    {
        const string Lib = "nvml.dll";
        [StructLayout(LayoutKind.Sequential)] public struct FanSpeedInfo { public uint Version; public uint Fan; public uint Speed; }
        [StructLayout(LayoutKind.Sequential)] public struct Utilization { public uint Gpu; public uint Memory; }

        [DllImport(Lib)] public static extern int nvmlInit_v2();
        [DllImport(Lib)] public static extern int nvmlShutdown();
        [DllImport(Lib)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport(Lib)] public static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
        [DllImport(Lib)] public static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensor, out uint temp);
        [DllImport(Lib)] public static extern int nvmlDeviceGetClockInfo(IntPtr device, uint type, out uint mhz);
        [DllImport(Lib)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization util);
        [DllImport(Lib)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport(Lib)] public static extern int nvmlDeviceGetNumFans(IntPtr device, out uint count);
        [DllImport(Lib)] public static extern int nvmlDeviceGetFanSpeed_v2(IntPtr device, uint fan, out uint pct);
        [DllImport(Lib)] public static extern int nvmlDeviceGetTargetFanSpeed(IntPtr device, uint fan, out uint pct);
        [DllImport(Lib)] public static extern int nvmlDeviceGetFanSpeedRPM(IntPtr device, ref FanSpeedInfo info);
        [DllImport(Lib)] public static extern int nvmlDeviceGetFanControlPolicy_v2(IntPtr device, uint fan, out int policy);
        [DllImport(Lib)] public static extern int nvmlDeviceGetMinMaxFanSpeed(IntPtr device, out uint min, out uint max);
        [DllImport(Lib)] public static extern int nvmlDeviceSetFanSpeed_v2(IntPtr device, uint fan, uint pct);
        [DllImport(Lib)] public static extern int nvmlDeviceSetDefaultFanSpeed_v2(IntPtr device, uint fan);
    }

    const uint ClockGraphics = 0, ClockMem = 2;

    readonly object _gate = new();
    readonly Dictionary<int, GpuFanState> _original = new();
    IntPtr _handle;
    bool _initialized;

    public string Name { get; private set; } = "";
    public int FanCount { get; private set; }
    public int MinFanPercent { get; private set; } = 30;
    public int MaxFanPercent { get; private set; } = 100;

    NvmlGpu() { }

    public static NvmlGpu? TryOpen(out string error)
    {
        error = "";
        if (RunMode.Refuse("open NVML")) { error = "hardware-free run"; return null; }
        var gpu = new NvmlGpu();
        try
        {
            int rc = N.nvmlInit_v2();
            if (rc != 0) { error = $"nvmlInit_v2 failed ({rc})"; return null; }
            gpu._initialized = true;
            rc = N.nvmlDeviceGetHandleByIndex_v2(0, out gpu._handle);
            if (rc != 0) { error = $"nvmlDeviceGetHandleByIndex_v2 failed ({rc})"; gpu.Dispose(); return null; }

            var nameBuf = new byte[96];
            if (N.nvmlDeviceGetName(gpu._handle, nameBuf, (uint)nameBuf.Length) == 0)
                gpu.Name = Encoding.ASCII.GetString(nameBuf).TrimEnd('\0');
            if (N.nvmlDeviceGetNumFans(gpu._handle, out var n) == 0) gpu.FanCount = (int)n;
            if (N.nvmlDeviceGetMinMaxFanSpeed(gpu._handle, out var mn, out var mx) == 0 && mx > 0)
            {
                gpu.MinFanPercent = (int)mn; gpu.MaxFanPercent = (int)mx;
            }
            return gpu;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = ex.Message;
            gpu.Dispose();
            return null;
        }
    }

    public GpuSample Read()
    {
        var s = new GpuSample { Name = Name };
        lock (_gate)
        {
            if (!_initialized) return s;
            if (N.nvmlDeviceGetTemperature(_handle, 0, out var t) == 0) s.TempC = t;
            if (N.nvmlDeviceGetClockInfo(_handle, ClockGraphics, out var core) == 0) s.CoreMHz = core;
            if (N.nvmlDeviceGetClockInfo(_handle, ClockMem, out var mem) == 0) s.MemMHz = mem;
            if (N.nvmlDeviceGetUtilizationRates(_handle, out var u) == 0) s.LoadPct = u.Gpu;
            if (N.nvmlDeviceGetPowerUsage(_handle, out var mw) == 0) s.PowerW = mw / 1000.0;
            for (int i = 0; i < FanCount; i++) s.Fans.Add(ReadFanLocked(i));
        }
        return s;
    }

    public GpuFanState ReadFan(int fan)
    {
        lock (_gate) return ReadFanLocked(fan);
    }

    GpuFanState ReadFanLocked(int fan)
    {
        var f = new GpuFanState { Index = fan };
        if (N.nvmlDeviceGetFanSpeed_v2(_handle, (uint)fan, out var sp) == 0) f.SpeedPct = (int)sp;
        if (N.nvmlDeviceGetTargetFanSpeed(_handle, (uint)fan, out var tg) == 0) f.TargetPct = (int)tg;
        if (N.nvmlDeviceGetFanControlPolicy_v2(_handle, (uint)fan, out var pol) == 0) f.Policy = pol;
        var info = new N.FanSpeedInfo { Version = 12u | (1u << 24), Fan = (uint)fan };
        if (N.nvmlDeviceGetFanSpeedRPM(_handle, ref info) == 0) f.Rpm = (int)info.Speed;
        return f;
    }

    /// <summary>Remember how the user's setup looked before we touched a fan (used by the diagnostics to leave no trace).</summary>
    public void RememberOriginal(int fan)
    {
        lock (_gate)
        {
            if (!_original.ContainsKey(fan)) _original[fan] = ReadFanLocked(fan);
        }
    }

    public bool SetFanPercent(int fan, double percent)
    {
        if (fan < 0 || fan >= FanCount) return false;
        int v = (int)Math.Round(Math.Clamp(percent, MinFanPercent, MaxFanPercent));
        lock (_gate)
        {
            if (!_initialized) return false;
            if (!_original.ContainsKey(fan)) _original[fan] = ReadFanLocked(fan);
            int rc = N.nvmlDeviceSetFanSpeed_v2(_handle, (uint)fan, (uint)v);
            if (rc != 0) { Log.Warn($"NVML SetFanSpeed_v2(fan {fan}, {v}%) failed rc={rc}"); return false; }
        }
        ControlRegistry.Mark("nvml", fan.ToString());
        return true;
    }

    /// <summary>Hand the fan back to the VBIOS automatic policy. This is the safe state for exits and crashes.</summary>
    public bool ReleaseToAuto(int fan)
    {
        if (fan < 0 || fan >= FanCount) return false;
        lock (_gate)
        {
            if (!_initialized) return false;
            int rc = N.nvmlDeviceSetDefaultFanSpeed_v2(_handle, (uint)fan);
            if (rc != 0) { Log.Warn($"NVML SetDefaultFanSpeed_v2(fan {fan}) failed rc={rc}"); return false; }
        }
        ControlRegistry.Unmark("nvml", fan.ToString());
        return true;
    }

    /// <summary>Put the fan back exactly as it was before the first write (manual@N% or auto).</summary>
    public bool RestoreOriginal(int fan)
    {
        GpuFanState? orig;
        lock (_gate) _original.TryGetValue(fan, out orig);
        if (orig == null) return ReleaseToAuto(fan);
        if (orig.Policy == 1)
        {
            int rc;
            lock (_gate) rc = N.nvmlDeviceSetFanSpeed_v2(_handle, (uint)fan, (uint)Math.Clamp(orig.TargetPct, MinFanPercent, MaxFanPercent));
            if (rc != 0) return false;
            ControlRegistry.Unmark("nvml", fan.ToString());
            return true;
        }
        return ReleaseToAuto(fan);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_initialized)
            {
                try { N.nvmlShutdown(); } catch { }
                _initialized = false;
            }
        }
    }
}
