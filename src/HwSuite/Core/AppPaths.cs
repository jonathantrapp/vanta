using System.Text;

namespace HwSuite.Core;

public static class AppPaths
{
    /// <summary>
    /// %APPDATA%\HwSuite as the real user profile sees it. A process started from inside an MSIX-packaged app (for example
    /// a test run launched by a packaged desktop app) can have its %APPDATA% writes silently redirected to
    /// ...\Packages\&lt;family&gt;\LocalCache\Roaming, with no package identity to detect. CLI tests and the real app would
    /// then each read a different config.json. See <see cref="ResolveData"/>.
    /// </summary>
    public static readonly string Data;
    /// <summary>How <see cref="Data"/> was chosen when it is not the plain %APPDATA% path (logged at start; null otherwise).</summary>
    public static readonly string? ResolutionNote;
    /// <summary>The data folder is an isolated hardware-free test folder (not the owner's profile).</summary>
    public static bool Isolated => RunMode.IsolatedData != null;

    /// <summary>
    /// Writes a marker through %APPDATA% and looks for it through the administrative share (\\localhost\C$\...), which
    /// bypasses per-package file redirection. Marker visible: no redirection, use %APPDATA%. Share reachable but marker
    /// missing: writes are redirected, so use the share path to reach the real profile. Share unreachable (not elevated,
    /// share disabled, not a local drive): keep %APPDATA% and say so.
    /// </summary>
    internal static string ResolveData(out string? note)
    {
        note = null;
        if (RunMode.IsolatedData is string isolated)
        {
            // hardware-free test runs never read or write the owner's profile folder
            try { Directory.CreateDirectory(isolated); }
            catch (Exception ex) { note = $"isolated test data folder {isolated} is not writable ({ex.GetType().Name}: {ex.Message}); files and the log are not written"; }
            return isolated;
        }
        var logical = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HwSuite");
        try
        {
            Directory.CreateDirectory(logical);
            var unc = ToAdminShare(logical);
            if (unc == null) return logical;
            var marker = ".path-probe-" + Guid.NewGuid().ToString("N");
            var local = Path.Combine(logical, marker);
            File.WriteAllText(local, "HwSuite path probe");
            try
            {
                if (File.Exists(Path.Combine(unc, marker))) return logical;
                var uncParent = Path.GetDirectoryName(unc)!;
                if (!Directory.Exists(uncParent))
                {
                    note = $"could not check for %APPDATA% redirection ({uncParent} not reachable); using {logical}";
                    return logical;
                }
                Directory.CreateDirectory(unc);
                note = $"%APPDATA% writes from this process are redirected (packaged-app context); using the real profile folder {unc}";
                return unc;
            }
            finally { try { File.Delete(local); } catch { } }
        }
        catch (Exception ex)
        {
            note = $"%APPDATA% redirection check failed ({ex.GetType().Name}: {ex.Message}); using {logical}";
            return logical;
        }
    }

    /// <summary>C:\Users\x\... becomes \\localhost\C$\Users\x\...; null for a path that is not on a local drive letter.</summary>
    internal static string? ToAdminShare(string path)
    {
        if (path.Length < 3 || path[1] != ':' || path[2] != '\\' || !char.IsLetter(path[0])) return null;
        return @"\\localhost\" + char.ToUpperInvariant(path[0]) + "$" + path[2..];
    }

    public static string Config => Path.Combine(Data, "config.json");
    public static string LogFile => Path.Combine(Data, "hwsuite.log");
    public static string Diagnostics => Path.Combine(Data, "diagnostics");
    public static string ControlledState => Path.Combine(Data, "controlled.json");
    public static string ExeDir => AppContext.BaseDirectory;

    static AppPaths()
    {
        Data = ResolveData(out ResolutionNote);
        // never throw from here: a type-initialisation failure would surface far from its cause
        try { Directory.CreateDirectory(Data); Directory.CreateDirectory(Diagnostics); }
        catch (Exception ex) { ResolutionNote = (ResolutionNote == null ? "" : ResolutionNote + "; ") + $"data folder not writable ({ex.GetType().Name}: {ex.Message})"; }
    }
}

public static class Log
{
    static readonly object Gate = new();
    public static bool ConsoleEnabled;
    public static event Action<string>? LineWritten;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERR ", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static void RotateIfLarge()
    {
        try
        {
            var fi = new FileInfo(AppPaths.LogFile);
            if (fi.Exists && fi.Length > 2_000_000)
                File.Move(AppPaths.LogFile, AppPaths.LogFile + ".old", true);
        }
        catch { /* logging must never throw */ }
    }

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}";
        lock (Gate)
        {
            try { File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine, Encoding.UTF8); } catch { }
            if (ConsoleEnabled)
            {
                try { Console.WriteLine(line); } catch { }
            }
        }
        try { LineWritten?.Invoke(line); } catch { }
    }
}
