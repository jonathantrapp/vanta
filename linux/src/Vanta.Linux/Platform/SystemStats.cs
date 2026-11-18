using HwSuite.Platform;

namespace HwSuite.Hardware;

/// <summary>
/// LINUX replacement for src\HwSuite\Hardware\SystemStats.cs: CPU load from /proc/stat (difference between calls), clocks
/// from cpufreq, memory from /proc/meminfo (MemTotal - MemAvailable, the same "in use" Windows Task Manager shows).
/// </summary>
public sealed class SystemStats
{
    ulong _lastIdle, _lastTotal;
    readonly object _gate = new();

    public double? CpuLoadPercent()
    {
        try
        {
            var line = File.ReadLines("/proc/stat").First();     // cpu  user nice system idle iowait irq softirq steal ...
            var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(ulong.Parse).ToArray();
            ulong idle = v[3] + v[4], total = 0;
            foreach (var x in v) total += x;
            lock (_gate)
            {
                ulong dIdle = idle - _lastIdle, dTotal = total - _lastTotal;
                bool first = _lastTotal == 0;
                _lastIdle = idle; _lastTotal = total;
                if (first || dTotal == 0) return null;
                return Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            }
        }
        catch { return null; }
    }

    /// <summary>Rated base clock (cpufreq base_frequency, Intel P-cores), MHz.</summary>
    public double? CpuNominalMHz()
    {
        var khz = Sysfs.ReadLong("/sys/devices/system/cpu/cpu0/cpufreq/base_frequency") ?? Sysfs.ReadLong("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq");
        return khz / 1000.0;
    }

    /// <summary>Average current clock over all logical CPUs (cpufreq scaling_cur_freq), MHz.</summary>
    public double? CpuAverageMHz()
    {
        try
        {
            var vals = Directory.GetDirectories("/sys/devices/system/cpu", "cpu*")
                .Select(d => Sysfs.ReadLong(Path.Combine(d, "cpufreq", "scaling_cur_freq")))
                .Where(v => v.HasValue).Select(v => v!.Value / 1000.0).ToList();
            return vals.Count > 0 ? vals.Average() : null;
        }
        catch { return null; }
    }

    public (double usedPercent, double usedGb, double totalGb)? Memory()
    {
        try
        {
            long total = 0, avail = -1;
            foreach (var l in File.ReadLines("/proc/meminfo"))
            {
                if (l.StartsWith("MemTotal:", StringComparison.Ordinal)) total = KiB(l);
                else if (l.StartsWith("MemAvailable:", StringComparison.Ordinal)) avail = KiB(l);
                if (total > 0 && avail >= 0) break;
            }
            if (total <= 0 || avail < 0) return null;
            double totalGb = total / 1048576.0, usedGb = (total - avail) / 1048576.0;
            return (100.0 * (total - avail) / total, usedGb, totalGb);
        }
        catch { return null; }
    }

    static long KiB(string line) => long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
}

/// <summary>CPU package power from the intel-rapl powercap energy counter (needs root to read on recent kernels).</summary>
public sealed class RaplPower
{
    const string Dir = "/sys/class/powercap/intel-rapl:0";
    long _lastUj = -1; DateTime _lastAt;
    long _maxUj;
    public bool Available => File.Exists(Path.Combine(Dir, "energy_uj")) && Sysfs.ReadLong(Path.Combine(Dir, "energy_uj")) != null;

    public double? ReadWatts()
    {
        var uj = Sysfs.ReadLong(Path.Combine(Dir, "energy_uj"));
        if (uj == null) return null;
        var now = DateTime.UtcNow;
        double? w = null;
        if (_lastUj >= 0)
        {
            if (_maxUj == 0) _maxUj = Sysfs.ReadLong(Path.Combine(Dir, "max_energy_range_uj")) ?? long.MaxValue;
            long d = uj.Value - _lastUj;
            if (d < 0) d += _maxUj;                          // counter wrapped
            double s = (now - _lastAt).TotalSeconds;
            if (s > 0.05) w = d / 1e6 / s;
        }
        _lastUj = uj.Value; _lastAt = now;
        return w;
    }
}
