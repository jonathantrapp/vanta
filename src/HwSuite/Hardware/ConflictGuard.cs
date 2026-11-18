using System.Diagnostics;
using System.Runtime.InteropServices;
using HwSuite.Core;

namespace HwSuite.Hardware;

/// <summary>Stops Gigabyte tools that would fight this app over the Super I/O chip and SMBus, when the user asked for that.</summary>
public static class ConflictGuard
{
    /// <summary>Acts only while fan or lighting control is switched on and the option is enabled (default).</summary>
    public static bool ShouldAutoStop(AppConfig cfg)
    {
        lock (cfg.Sync) return cfg.AutoStopGigabyte && (cfg.FanControlEnabled || cfg.Rgb.Enabled);
    }

    /// <summary>
    /// Runs Toggle-Gigabyte.ps1 -Action Stop if anything is running, then verifies the result and records what HwSuite
    /// stopped. Returns a short human-readable result, or null if nothing was needed. Whatever the outcome, the
    /// OwnershipArbiter keeps the affected hardware withheld while any of the tools is still running.
    /// </summary>
    public static string? StopIfNeeded(AppConfig cfg, GigabyteGuard guard, string reason)
    {
        if (!ShouldAutoStop(cfg)) return null;
        if (guard.RunningCompetitors().Count == 0) return null;
        var r = guard.StopAndRecord(reason);
        return r.Verified ? r.Message : "Could not stop every Gigabyte tool. " + r.Message;
    }
}

/// <summary>Result of reading Afterburner's fan setting. Known=false means ownership cannot be established from the file.</summary>
public sealed record AfterburnerFanSetting(bool Known, bool? SoftwareCurveOn, int? PeriodMs, string Detail);

/// <summary>
/// Reads Afterburner's config for ownership decisions. A read counts only if the file was not modified during the read
/// or within the settle time before it, and its content is identical to the previous check; so a file that is being
/// written, or has just changed, is "unknown" for at least one refresh cycle.
/// </summary>
public sealed class AfterburnerConfigWatcher
{
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    public sealed record FileSnapshot(DateTime LastWriteUtc, long Length);

    readonly Func<string?> _path;
    readonly Func<string, FileSnapshot?> _stat;
    readonly Func<string, string> _read;
    string? _lastText;
    string? _lastPath;

    AfterburnerFanSetting Unknown(string reason)
    {
        _lastText = null;
        _lastPath = null;
        return new(false, null, null, reason);
    }

    public AfterburnerConfigWatcher(Func<string?> path, Func<string, FileSnapshot?> stat, Func<string, string> read)
    {
        _path = path; _stat = stat; _read = read;
    }

    public static AfterburnerConfigWatcher CreateLive() => new(
        () => AfterburnerHelper.ConfigPath,
        p => { var fi = new FileInfo(p); return fi.Exists ? new FileSnapshot(fi.LastWriteTimeUtc, fi.Length) : null; },
        p =>
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        });

    public AfterburnerFanSetting Read(DateTime nowUtc)
    {
        string? path;
        try { path = _path(); } catch { path = null; }
        if (string.IsNullOrWhiteSpace(path)) return Unknown("config file location unknown");
        try
        {
            var before = _stat(path);
            if (before == null) return Unknown("config file not found");
            var text = _read(path);
            var after = _stat(path);
            if (after == null || after.LastWriteUtc != before.LastWriteUtc || after.Length != before.Length)
                return Unknown("config file changed while it was being read");
            if (nowUtc - after.LastWriteUtc < SettleTime)
                return Unknown("config file was modified moments ago");
            bool same = text == _lastText && string.Equals(path, _lastPath, StringComparison.OrdinalIgnoreCase);
            _lastText = text;
            _lastPath = path;
            if (!same) return new(false, null, null, "config file content changed since the last check (confirming on the next one)");
            return AfterburnerHelper.ParseStrict(text);
        }
        catch (Exception ex) { return Unknown("config file unreadable: " + ex.Message); }
    }
}

/// <summary>
/// MSI Afterburner support: reads its real config (read-only) to tell whether its software fan curve is active, and offers
/// the two safe helper actions. Key names come from Afterburner's own Profiles\MSIAfterburner.cfg.
/// </summary>
public static class AfterburnerHelper
{
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    public const string CheckboxText = "Enable user defined software automatic fan control";

    public static bool IsRunning
    {
        get { try { return Process.GetProcessesByName("MSIAfterburner").Length > 0; } catch { return false; } }
    }

    public static string? InstallDir
    {
        get
        {
            try
            {
                var p = Process.GetProcessesByName("MSIAfterburner").FirstOrDefault();
                var path = p?.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path)) return Path.GetDirectoryName(path);
            }
            catch { }
            var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MSI Afterburner");
            return Directory.Exists(def) ? def : null;
        }
    }

    public static string? ConfigPath
    {
        get { var d = InstallDir; return d == null ? null : Path.Combine(d, "Profiles", "MSIAfterburner.cfg"); }
    }

    /// <summary>Display-only parse of SwAutoFanControl / SwAutoFanControlPeriod (ownership uses ParseStrict).</summary>
    public static (bool? autoFan, int? periodMs) ParseFanSettings(string cfgText)
    {
        var strict = ParseStrict(cfgText);
        return (strict.Known ? strict.SoftwareCurveOn : null, strict.PeriodMs);
    }

    public static (bool? autoFan, int? periodMs) ReadFanSettings()
    {
        try
        {
            var p = ConfigPath;
            return p != null && File.Exists(p) ? ParseFanSettings(ReadShared(p)) : (null, null);
        }
        catch { return (null, null); }
    }

    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// Strict reading of Afterburner's fan setting. Known only when the [Settings] section contains exactly one
    /// SwAutoFanControl entry whose value is exactly 0 or 1. Anything else (missing section or key, duplicates,
    /// other values, the key outside [Settings]) is unknown.
    /// </summary>
    public static AfterburnerFanSetting ParseStrict(string text)
    {
        string? section = null;
        bool sawSettings = false;
        var values = new List<string>();
        int? period = null;
        bool outside = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("[") && line.EndsWith("]")) { section = line; if (line == "[Settings]") sawSettings = true; continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (key == "SwAutoFanControl")
            {
                if (section == "[Settings]") values.Add(val); else outside = true;
            }
            else if (key == "SwAutoFanControlPeriod" && section == "[Settings]" && int.TryParse(val, out var ms)) period = ms;
        }
        if (!sawSettings) return new(false, null, period, "no [Settings] section (unreadable or incomplete file)");
        if (outside) return new(false, null, period, "SwAutoFanControl found outside [Settings]");
        if (values.Count == 0) return new(false, null, period, "SwAutoFanControl entry missing (incomplete file?)");
        if (values.Count > 1) return new(false, null, period, $"{values.Count} SwAutoFanControl entries");
        return values[0] switch
        {
            "1" => new(true, true, period, "SwAutoFanControl=1"),
            "0" => new(true, false, period, "SwAutoFanControl=0"),
            _ => new(false, null, period, $"unexpected SwAutoFanControl value '{values[0]}'")
        };
    }

    public static string HowToFix(int? periodMs)
    {
        string every = periodMs is int ms ? $"every {ms / 1000.0:0.#} s" : "periodically";
        return "1. In MSI Afterburner, click the Settings (gear) button.\n" +
               "2. Open the Fan tab.\n" +
               $"3. Untick \"{CheckboxText}\". That option re-applies its own curve {every}, overwriting the fan speeds HwSuite sets.\n" +
               "4. Click OK. On the main window, make sure the fan speed is on Auto (not a fixed manual speed).\n" +
               "5. HwSuite then sees a stable SwAutoFanControl=0 and no manual speed, and drives the RTX 3070 fans itself.\n" +
               "Prefer a file edit? Close Afterburner first, then set SwAutoFanControl=0 in Profiles\\MSIAfterburner.cfg.";
    }

    /// <summary>Bring the running Afterburner window forward (or start it hidden in the tray -> launching again shows the window).</summary>
    public static bool ShowWindow()
    {
        try
        {
            var p = Process.GetProcessesByName("MSIAfterburner").FirstOrDefault();
            if (p == null) return false;
            if (p.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(p.MainWindowHandle, 9);   // SW_RESTORE
                return SetForegroundWindow(p.MainWindowHandle);
            }
            // minimized to the tray: starting the executable again asks the single running instance to show its window
            var exe = p.MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe)) { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); return true; }
        }
        catch (Exception ex) { Log.Warn("show Afterburner failed: " + ex.Message); }
        return false;
    }

    public static void OpenConfigFolder()
    {
        try
        {
            var cfg = ConfigPath;
            if (cfg != null && File.Exists(cfg)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{cfg}\"") { UseShellExecute = true });
            else if (InstallDir is string d) Process.Start(new ProcessStartInfo("explorer.exe", d) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("open Afterburner folder failed: " + ex.Message); }
    }
}
