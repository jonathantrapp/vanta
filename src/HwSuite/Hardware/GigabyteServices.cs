using System.Diagnostics;
using System.Text;
using HwSuite.Core;

namespace HwSuite.Hardware;

/// <summary>
/// Runs the project's Toggle-Gigabyte.ps1 so Gigabyte services/processes (EasyTune, RGB Fusion, GService...) do not
/// fight this application over the SMBus and the Super I/O chip.
/// </summary>
public static class GigabyteServices
{
    public static string? FindScript()
    {
        var candidates = new List<string> { Path.Combine(AppPaths.ExeDir, "Toggle-Gigabyte.ps1") };
        var dir = new DirectoryInfo(AppPaths.ExeDir);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "Toggle-Gigabyte.ps1"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static (bool ok, string output) Run(string action)
    {
        if (RunMode.Refuse($"run Toggle-Gigabyte.ps1 -Action {action}")) return (false, "hardware-free run");
        var script = FindScript();
        if (script == null) return (false, "Toggle-Gigabyte.ps1 not found next to the application");
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Action {action}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return (false, "timed out"); }
            return (p.ExitCode == 0, output.Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Any Gigabyte service (by service state) or tool process running. Note the GService service runs GCloud.exe, so a
    /// process-name check for "GService" alone misses it; GigabyteGuard checks service state instead.
    /// </summary>
    public static bool AnyRunning()
    {
        try { return GigabyteGuard.CreateLive().RunningCompetitors().Count > 0; } catch { return false; }
    }
}
