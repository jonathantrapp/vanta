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

    public static bool Exists() => Schtasks($"/Query /TN \"{TaskName}\"").exit == 0;

    /// <summary>The task definition as Windows stored it (used by the self-test to prove RunLevel and arguments).</summary>
    public static string? ReadBackXml()
    {
        var (exit, output) = Schtasks($"/Query /TN \"{TaskName}\" /XML");
        return exit == 0 ? output : null;
    }

    /// <summary>The executable the installed task starts, or null if not installed / unreadable.</summary>
    public static string? ReadTarget() => ReadBackXml() is string xml ? TargetFromXml(xml) : null;

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

    /// <summary>True when the task's target and this executable are the same file.</summary>
    public static bool SameExecutable(string? a, string? b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a.Trim('"')), Path.GetFullPath(b.Trim('"')), StringComparison.OrdinalIgnoreCase);

    public static (bool ok, string message) Create(string? exePath = null)
    {
        if (RunMode.Refuse("create the logon task")) return (false, "hardware-free run");
        exePath ??= Environment.ProcessPath ?? "";
        if (!File.Exists(exePath)) return (false, "cannot locate the application executable");
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var tmp = Path.Combine(Path.GetTempPath(), "hwsuite-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(tmp, BuildXml(exePath, user), Encoding.Unicode);
            var (exit, output) = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            Log.Info($"startup task create: exit {exit} {output}");
            return exit == 0 ? (true, $"Created. VANTA will start minimized at logon for {user} with highest privileges.") : (false, output.Length > 0 ? output : $"schtasks exit code {exit}");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public static (bool ok, string message) Remove()
    {
        if (RunMode.Refuse("remove the logon task")) return (false, "hardware-free run");
        if (!Exists()) return (true, "The startup task is not installed.");
        var (exit, output) = Schtasks($"/Delete /TN \"{TaskName}\" /F");
        Log.Info($"startup task delete: exit {exit} {output}");
        return exit == 0 ? (true, "Removed. VANTA will no longer start at logon.") : (false, output.Length > 0 ? output : $"schtasks exit code {exit}");
    }
}
