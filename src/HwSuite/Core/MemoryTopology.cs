using System.Management;

namespace HwSuite.Core;

/// <summary>
/// Installed memory modules as SMBIOS reports them (Win32_PhysicalMemory), read once. This is the source for "how many
/// DIMMs": the number of ENE lighting controllers found on the SMBus is a separate fact and is never used as a DIMM count,
/// and which controller sits in which slot is not known.
/// </summary>
public sealed record MemoryModule(string Locator, double CapacityGb, int SpeedMts, string Manufacturer, string PartNumber);

public static class MemoryTopology
{
    static List<MemoryModule>? _cached;
    static string? _error;

    public static IReadOnlyList<MemoryModule> Modules { get { Load(); return _cached!; } }
    public static string? Error { get { Load(); return _error; } }

    static void Load()
    {
        if (_cached != null) return;
        var list = new List<MemoryModule>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceLocator, Capacity, ConfiguredClockSpeed, Speed, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
            foreach (ManagementObject m in searcher.Get())
            {
                using (m)
                {
                    double gb = m["Capacity"] is ulong c ? c / 1073741824.0 : 0;
                    int speed = m["ConfiguredClockSpeed"] is uint cs && cs > 0 ? (int)cs : m["Speed"] is uint sp ? (int)sp : 0;
                    list.Add(new MemoryModule(((m["DeviceLocator"] as string) ?? "").Trim(), gb, speed, ((m["Manufacturer"] as string) ?? "").Trim(), ((m["PartNumber"] as string) ?? "").Trim()));
                }
            }
        }
        catch (Exception ex) { _error = ex.Message; }
        _cached = list;
    }

    /// <summary>"4 x 8 GB" when all modules match, "8 + 16 GB" otherwise, "unknown" when SMBIOS gave nothing.</summary>
    public static string Summary(IReadOnlyList<MemoryModule> mods)
    {
        if (mods.Count == 0) return "unknown";
        var sizes = mods.Select(m => m.CapacityGb).ToList();
        return sizes.Distinct().Count() == 1 ? $"{mods.Count} x {sizes[0]:0} GB" : string.Join(" + ", sizes.Select(s => $"{s:0}")) + " GB";
    }

    public static string SpeedText(IReadOnlyList<MemoryModule> mods) =>
        mods.Count == 0 ? "" : mods.Select(m => m.SpeedMts).Distinct().Count() == 1 && mods[0].SpeedMts > 0 ? $"{mods[0].SpeedMts} MT/s" : "mixed speeds";

    public static string Detail(IReadOnlyList<MemoryModule> mods) =>
        mods.Count == 0 ? "SMBIOS reported no memory modules" + (Error != null ? $" ({Error})" : "")
        : string.Join(Environment.NewLine, mods.Select(m => $"{m.Locator}: {m.CapacityGb:0} GB {m.SpeedMts} MT/s, {m.Manufacturer} {m.PartNumber}"));
}
