// PORTED from src\HwSuite\Core\MemoryTopology.cs (Windows 1.20.0): WMI Win32_PhysicalMemory -> SMBIOS type 17 from sysfs. Rest unchanged.

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

    /// <summary>
    /// LINUX: SMBIOS type 17 (Memory Device) structures from /sys/firmware/dmi/entries/17-*/raw (readable by root, i.e. the
    /// vantad service). Layout per the SMBIOS spec 3.x: 0x0C size (MB, bit15 = KB units, 0x7FFF = see 0x1C extended size),
    /// 0x10 device locator string #, 0x15 speed, 0x17 manufacturer string #, 0x1A part number string #, 0x20 configured speed.
    /// Empty slots (size 0) are skipped, like Win32_PhysicalMemory.
    /// </summary>
    static void Load()
    {
        if (_cached != null) return;
        var list = new List<MemoryModule>();
        try
        {
            const string root = "/sys/firmware/dmi/entries";
            foreach (var dir in Directory.GetDirectories(root, "17-*").OrderBy(d => int.Parse(Path.GetFileName(d)[3..])))
            {
                var raw = File.ReadAllBytes(Path.Combine(dir, "raw"));
                if (raw.Length < 0x15) continue;
                int len = raw[1];
                var strings = new List<string>();
                int p = len;
                while (p < raw.Length && !(raw[p] == 0 && (p + 1 >= raw.Length || raw[p + 1] == 0)))
                {
                    int e = Array.IndexOf(raw, (byte)0, p); if (e < 0) e = raw.Length;
                    strings.Add(System.Text.Encoding.ASCII.GetString(raw, p, e - p)); p = e + 1;
                    if (p < raw.Length && raw[p] == 0) break;
                }
                string S(int off) => off < len && raw[off] > 0 && raw[off] <= strings.Count ? strings[raw[off] - 1].Trim() : "";
                ushort W(int off) => off + 1 < len ? BitConverter.ToUInt16(raw, off) : (ushort)0;
                int size = W(0x0C);
                if (size == 0 || size == 0xFFFF) continue;
                double gb = size == 0x7FFF && len >= 0x20 ? BitConverter.ToUInt32(raw, 0x1C) / 1024.0
                          : (size & 0x8000) != 0 ? (size & 0x7FFF) / 1048576.0 : size / 1024.0;
                int speed = W(0x20) is ushort cs and > 0 and < 0xFFFF ? cs : W(0x15);
                list.Add(new MemoryModule(S(0x10), gb, speed, S(0x17), S(0x1A)));
            }
        }
        catch (Exception ex) { _error = ex.Message + (ex is UnauthorizedAccessException ? " (SMBIOS needs root)" : ""); }
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
