// PORTED from src\HwSuite\Hardware\StartupTask.cs (Windows 1.20.0): Exists/ReadTarget/Create/Remove use an XDG autostart entry instead of schtasks; BuildXml/TargetFromXml kept for the shared tests.
using System.Diagnostics;
using System.Security;
using System.Text;
using HwSuite.Core;

namespace HwSuite.Hardware;

/// <summary>
/// "Run at Windows logon" as an elevated Task Scheduler task (RunLevel HighestAvailable = schtasks /RL HIGHEST), so the
/// app starts minimized at logon with administrator rights and without a UAC prompt. The task is defined with an XML
/// document so the default 72-hour execution limit and the battery stop conditions of a command-line task are avoided.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "HwSuite (Elevated Logon)";

    public static string BuildXml(string exePath, string userName, int delaySeconds = 15)
    {
        string E(string s) => SecurityElement.Escape(s) ?? "";
        var dir = Path.GetDirectoryName(exePath) ?? "";
        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Author>HwSuite</Author>
    <Description>Starts VANTA (HwSuite.exe) minimized at logon with highest privileges (no UAC prompt).</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{E(userName)}</UserId>
      <Delay>PT{delaySeconds}S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{E(userName)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>6</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{E(exePath)}</Command>
      <Arguments>--minimized</Arguments>
      <WorkingDirectory>{E(dir)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
    }

    static (int exit, string output) Schtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return (-1, "schtasks timed out"); }
            return (p.ExitCode, o.Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    // ---- LINUX: an XDG autostart entry instead of a scheduled task (the desktop starts it when you log in) ----
    public static string DesktopFile => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is string x && x.Length > 0 ? x
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart", "vanta.desktop");

    public static bool Exists() => File.Exists(DesktopFile);

    public static string BuildDesktopEntry(string exePath) =>
        "[Desktop Entry]\nType=Application\nName=VANTA\nComment=Fans, lighting and the AL80 keyboard\n" +
        $"Exec=\"{exePath}\" --minimized\nIcon=vanta\nTerminal=false\nX-GNOME-Autostart-enabled=true\nX-GNOME-Autostart-Delay=10\n";

    /// <summary>The executable the autostart entry starts, or null if not installed / unreadable.</summary>
    public static string? ReadTarget()
    {
        try
        {
            var line = File.ReadLines(DesktopFile).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal));
            if (line == null) return null;
            var v = line[5..].Trim();
            return v.StartsWith('"') ? v[1..v.IndexOf('"', 1)] : v.Split(' ')[0];
        }
        catch { return null; }
    }

    public static string? TargetFromXml(string xml)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            return doc.Descendants(ns + "Command").FirstOrDefault()?.Value;
        }
        catch { return null; }
    }

    /// <summary>True when the entry's target and this executable are the same file.</summary>
    public static bool SameExecutable(string? a, string? b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a.Trim('"')), Path.GetFullPath(b.Trim('"')), StringComparison.Ordinal);

    public static (bool ok, string message) Create(string? exePath = null)
    {
        if (RunMode.Refuse("create the logon task")) return (false, "hardware-free run");
        exePath ??= File.Exists("/usr/local/bin/vanta") ? "/usr/local/bin/vanta" : Environment.ProcessPath ?? "";
        if (!File.Exists(exePath)) return (false, "cannot locate the application executable");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
            File.WriteAllText(DesktopFile, BuildDesktopEntry(exePath));
            Log.Info("autostart entry written: " + DesktopFile);
            return (true, "Created. VANTA will start minimized when you log in.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static (bool ok, string message) Remove()
    {
        if (RunMode.Refuse("remove the logon task")) return (false, "hardware-free run");
        if (!Exists()) return (true, "The autostart entry is not installed.");
        try { File.Delete(DesktopFile); Log.Info("autostart entry removed"); return (true, "Removed. VANTA will no longer start when you log in."); }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
