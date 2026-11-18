using System.Text.Json;

namespace HwSuite.Core;

public static class ConfigStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.Config))
            {
                var cfg = Parse(File.ReadAllText(AppPaths.Config));
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Config load failed, using defaults", ex);
            try { File.Copy(AppPaths.Config, AppPaths.Config + ".corrupt", true); } catch { }
        }
        return new AppConfig();
    }

    /// <summary>Deserialize and migrate a saved config (any earlier version) to the current version.</summary>
    public static AppConfig? Parse(string json)
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
        if (cfg == null) return null;
        if (cfg.Version < 2)
        {
            // v2 turned the D_LED strip on by default and added the strip channel order / auto-stop options
            cfg.Rgb.DLedStrip = true;
            if (Math.Abs(cfg.Rgb.MotherboardMaxHz - 30) < 0.01) cfg.Rgb.MotherboardMaxHz = 20; // old default
            cfg.Version = 2;
        }
        if (cfg.Version < 3)
        {
            // v3 added evidence-backed minimums, tachometer history and service restore. New fields take their
            // defaults; nothing the owner configured is rewritten. Channels that relied on the removed arbitrary
            // floors (unclassified 50%, tachless 25%) keep their settings but stay under firmware control until a
            // minimum is confirmed (see FanSafety).
            cfg.Version = 3;
        }
        if (cfg.Version < 4 || cfg.Rgb.DLedExperimental120)
        {
            // v4: the strip rate is one setting (DLedMaxHz). An earlier Phase 7 build stored its 120 option as a flag.
            if (cfg.Rgb.DLedExperimental120) cfg.Rgb.DLedMaxHz = 120;
            cfg.Rgb.DLedExperimental120 = false;
            cfg.Version = 4;
        }
        // 1.12 additions take their defaults when absent; invalid entries are repaired, never trusted
        cfg.Arrangement ??= new();
        cfg.Rgb.Balance ??= new();
        cfg.Rgb.Balance.Normalize();
        cfg.Arrangement.Normalize();
        cfg.Looks = LightingLooks.Normalize(cfg.Looks);
        return cfg;
    }

    /// <summary>
    /// Set by test, benchmark and screenshot modes: they build their own in-memory configs and must never overwrite the
    /// owner's settings (since the data-folder fix they would reach the real config.json even from a packaged-app context).
    /// </summary>
    public static bool ReadOnly { get; set; }
    public static long SkippedSaves;

    public static void Save(AppConfig cfg)
    {
        if (ReadOnly) { Interlocked.Increment(ref SkippedSaves); return; }
        try
        {
            string json;
            lock (cfg.Sync) json = JsonSerializer.Serialize(cfg, Options);
            var tmp = AppPaths.Config + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.Config, true);
        }
        catch (Exception ex) { Log.Error("Config save failed", ex); }
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
