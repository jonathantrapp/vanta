namespace HwSuite.Hardware;

public sealed class FanReading
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public double? Rpm { get; set; }
    public double? Percent { get; set; }
}

public sealed class TempReading
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public double? Celsius { get; set; }
}

public sealed class TelemetrySnapshot
{
    public DateTime Time { get; init; } = DateTime.Now;

    public double? CpuTempC { get; set; }
    public double? CpuLoadPct { get; set; }
    public double? CpuClockMHz { get; set; }
    public double? CpuPowerW { get; set; }
    public string CpuTempSource { get; set; } = "unavailable";
    /// <summary>"ok", or why CpuTempC is null (no valid reading, failed update).</summary>
    public string CpuTempStatus { get; set; } = "not read";
    /// <summary>When the CPU temperature in this snapshot was successfully read (null if it was not).</summary>
    public DateTime? CpuTempReadUtc { get; set; }
    /// <summary>Heuristic warning that CPU values may not be updating; informational, never used to drive fans.</summary>
    public string? CpuTempSuspicion { get; set; }
    public string GpuTempStatus { get; set; } = "not read";

    public string GpuName { get; set; } = "";
    public double? GpuTempC { get; set; }
    public double? GpuLoadPct { get; set; }
    public double? GpuCoreMHz { get; set; }
    public double? GpuMemMHz { get; set; }
    public double? GpuPowerW { get; set; }

    public double? RamUsedPct { get; set; }
    public double? RamUsedGb { get; set; }
    public double? RamTotalGb { get; set; }

    public List<FanReading> Fans { get; } = new();
    public List<TempReading> Temps { get; } = new();

    /// <summary>Hottest of CPU and GPU, used by thermal lighting and failsafes.</summary>
    public double? MaxTempC
    {
        get
        {
            if (CpuTempC is double c && GpuTempC is double g) return Math.Max(c, g);
            return CpuTempC ?? GpuTempC;
        }
    }
}
