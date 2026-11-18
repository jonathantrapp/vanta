using System.Runtime.InteropServices;

namespace HwSuite.Hardware;

/// <summary>Driverless CPU load / clock / memory readings (used when LibreHardwareMonitor has no ring-0 backend).</summary>
public sealed class SystemStats
{
    [StructLayout(LayoutKind.Sequential)]
    struct FileTime { public uint Low; public uint High; public ulong Value => ((ulong)High << 32) | Low; }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length; public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessorPowerInformation
    {
        public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState;
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLen, IntPtr output, uint outputLen);

    const int ProcessorInformationLevel = 11;

    ulong _prevIdle, _prevKernel, _prevUser;
    bool _hasPrev;

    public double? CpuLoadPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        ulong i = idle.Value, k = kernel.Value, u = user.Value;
        double? result = null;
        if (_hasPrev)
        {
            ulong di = i - _prevIdle, dk = k - _prevKernel, du = u - _prevUser;
            ulong total = dk + du; // kernel time already includes idle time
            if (total > 0) result = Math.Clamp(100.0 * (total - di) / total, 0, 100);
        }
        _prevIdle = i; _prevKernel = k; _prevUser = u; _hasPrev = true;
        return result;
    }

    /// <summary>Average of the per-logical-processor "current" MHz that Windows reports (nominal P-state, not true boost).</summary>
    public double? CpuNominalMHz()
    {
        int n = Environment.ProcessorCount;
        int size = Marshal.SizeOf<ProcessorPowerInformation>();
        IntPtr buf = Marshal.AllocHGlobal(size * n);
        try
        {
            if (CallNtPowerInformation(ProcessorInformationLevel, IntPtr.Zero, 0, buf, (uint)(size * n)) != 0) return null;
            double sum = 0;
            for (int idx = 0; idx < n; idx++)
                sum += Marshal.PtrToStructure<ProcessorPowerInformation>(buf + idx * size).CurrentMhz;
            return sum / n;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public (double usedPercent, double usedGb, double totalGb)? Memory()
    {
        var st = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref st)) return null;
        double total = st.TotalPhys / 1073741824.0;
        double used = (st.TotalPhys - st.AvailPhys) / 1073741824.0;
        return (st.MemoryLoad, used, total);
    }
}
