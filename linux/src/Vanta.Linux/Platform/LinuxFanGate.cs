using System.Text.Json;
using HwSuite.Core;

namespace HwSuite.Fans;

/// <summary>
/// LINUX safety gate for motherboard fan headers. On Windows the header map (pwm N drives tach N, which fan is where,
/// DC minimums) was proven with step tests. On Linux the same map is expected (both drivers number the IT8689E's outputs
/// in register order) but it is a different driver, so VANTA does not drive any motherboard header until a Linux step
/// test has confirmed it and written this file. GPU fans (NVML, same driver semantics as Windows) are not gated.
/// </summary>
public static class LinuxFanGate
{
    public sealed class Record
    {
        public string Chip { get; set; } = "";
        public DateTime VerifiedUtc { get; set; }
        /// <summary>pwm number -> fan (tach) number that followed it in the step test; 0 = no tach moved.</summary>
        public Dictionary<int, int> PwmToFan { get; set; } = new();
        public string Note { get; set; } = "";
    }

    public static string FilePath => Path.Combine(AppPaths.Data, "linux-fans-verified.json");

    static Record? _cached; static DateTime _cachedAt;

    public static Record? Load()
    {
        if (_cached != null && (DateTime.UtcNow - _cachedAt).TotalSeconds < 5) return _cached;
        try { _cached = File.Exists(FilePath) ? JsonSerializer.Deserialize<Record>(File.ReadAllText(FilePath)) : null; }
        catch { _cached = null; }
        _cachedAt = DateTime.UtcNow;
        return _cached;
    }

    public static void Save(Record r)
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
        _cached = r; _cachedAt = DateTime.UtcNow;
    }

    /// <summary>Test hook: force the gate open or shut regardless of the file (null = use the file).</summary>
    public static bool? Override { get; set; }

    public static bool MotherboardAllowed(out string reason)
    {
        if (Override is bool o) { reason = o ? "" : "blocked by test override"; return o; }
        var r = Load();
        if (r == null) { reason = "the motherboard fan headers have not been verified on Linux yet (run: vanta --fan-verify)"; return false; }
        reason = "";
        return true;
    }
}
