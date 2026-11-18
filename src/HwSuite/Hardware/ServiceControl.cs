using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HwSuite.Hardware;

public enum ServiceRunState { NotInstalled, Stopped, StartPending, StopPending, Running, ContinuePending, PausePending, Paused, Unknown }

/// <summary>What a bounded start attempt established. Only <see cref="Running"/> is an observed success.</summary>
public enum ServiceStartStatus
{
    /// <summary>Observed Running within the wait.</summary>
    Running,
    /// <summary>The start request was accepted and the service was still StartPending when the wait ended: not yet running.</summary>
    StillStarting,
    /// <summary>The request was refused, or the service was observed back in Stopped after accepting it.</summary>
    Failed,
    /// <summary>The request was accepted but the final state could not be read, or was something other than the above.</summary>
    Uncertain
}

public sealed record ServiceStartOutcome(ServiceStartStatus Status, string Message)
{
    /// <summary>Text for the log and the System page, in the owner's terms. Never claims a start that was not observed.</summary>
    public string Describe(string service) => Status switch
    {
        ServiceStartStatus.Running => $"{service}: started (running)",
        ServiceStartStatus.StillStarting => $"{service}: Start requested; still starting.",
        ServiceStartStatus.Failed => $"{service}: NOT started ({Message})",
        _ => $"{service}: start requested; result not confirmed ({Message})"
    };
}

/// <summary>Minimal Service Control Manager access (query and start). Stopping stays with Toggle-Gigabyte.ps1, per the project rules.</summary>
public interface IServiceControl
{
    ServiceRunState Query(string name);
    /// <summary>Start a stopped service and wait (bounded) for it to report Running. Reports what was observed.</summary>
    ServiceStartOutcome Start(string name, TimeSpan timeout);
}

public sealed class WindowsServiceControl : IServiceControl
{
    const uint ScManagerConnect = 0x0001, ServiceQueryStatus = 0x0004, ServiceStart = 0x0010;
    const int ErrorServiceDoesNotExist = 1060, ErrorServiceAlreadyRunning = 1056;

    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenSCManagerW(string? machine, string? db, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatus(IntPtr svc, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool StartServiceW(IntPtr svc, uint argc, IntPtr argv);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(IntPtr h);

    public static readonly WindowsServiceControl Instance = new();

    static ServiceRunState Map(uint s) => s switch
    {
        1 => ServiceRunState.Stopped, 2 => ServiceRunState.StartPending, 3 => ServiceRunState.StopPending, 4 => ServiceRunState.Running,
        5 => ServiceRunState.ContinuePending, 6 => ServiceRunState.PausePending, 7 => ServiceRunState.Paused, _ => ServiceRunState.Unknown
    };

    public ServiceRunState Query(string name)
    {
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return ServiceRunState.Unknown;
        try
        {
            var svc = OpenServiceW(scm, name, ServiceQueryStatus);
            if (svc == IntPtr.Zero)
                return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist ? ServiceRunState.NotInstalled : ServiceRunState.Unknown;
            try { return QueryServiceStatus(svc, out var st) ? Map(st.CurrentState) : ServiceRunState.Unknown; }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public ServiceStartOutcome Start(string name, TimeSpan timeout)
    {
        if (Core.RunMode.Refuse($"start the {name} service")) return new(ServiceStartStatus.Failed, "hardware-free run");
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return new(ServiceStartStatus.Failed, "cannot open the service manager: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        try
        {
            var svc = OpenServiceW(scm, name, ServiceQueryStatus | ServiceStart);
            if (svc == IntPtr.Zero) return new(ServiceStartStatus.Failed, "cannot open service: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            try
            {
                if (!StartServiceW(svc, 0, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != ErrorServiceAlreadyRunning) return new(ServiceStartStatus.Failed, "start request refused: " + new Win32Exception(err).Message);
                }
                return WaitForRunning(() => QueryServiceStatus(svc, out var st) ? (Map(st.CurrentState), st.Win32ExitCode) : (ServiceRunState.Unknown, 0u), timeout, TimeSpan.FromMilliseconds(250));
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>
    /// After an accepted start request: poll until Running, a return to Stopped (after it was seen starting), or the deadline.
    /// At the deadline StartPending is "still starting"; anything else unread or unexpected is "uncertain". Bounded by
    /// <paramref name="timeout"/>; nothing keeps watching afterwards.
    /// </summary>
    public static ServiceStartOutcome WaitForRunning(Func<(ServiceRunState state, uint exitCode)> query, TimeSpan timeout, TimeSpan poll, Action<TimeSpan>? sleep = null, Func<TimeSpan>? elapsed = null)
    {
        sleep ??= Thread.Sleep;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        elapsed ??= () => sw.Elapsed;
        var last = ServiceRunState.Unknown;
        uint exit = 0;
        bool seenStarting = false;
        while (true)
        {
            (last, exit) = query();
            if (last == ServiceRunState.Running) return new(ServiceStartStatus.Running, "running");
            if (last == ServiceRunState.StartPending) seenStarting = true;
            else if (last == ServiceRunState.Stopped && seenStarting)
                return new(ServiceStartStatus.Failed, "it stopped again while starting" + (exit != 0 ? $" (exit code {exit})" : ""));
            if (elapsed() >= timeout) break;
            sleep(poll);
        }
        string waited = timeout.TotalSeconds.ToString("0");
        return last switch
        {
            ServiceRunState.StartPending => new(ServiceStartStatus.StillStarting, $"still StartPending after {waited} s"),
            ServiceRunState.Stopped => new(ServiceStartStatus.Uncertain, $"still Stopped {waited} s after the request was accepted" + (exit != 0 ? $" (exit code {exit})" : "")),
            ServiceRunState.Unknown => new(ServiceStartStatus.Uncertain, $"its state could not be read within {waited} s"),
            _ => new(ServiceStartStatus.Uncertain, $"{last} after {waited} s")
        };
    }
}
