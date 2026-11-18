// PORTED from src\HwSuite\Hardware\ServiceControl.cs (Windows 1.20.0): the SCM P/Invoke class becomes "not installed"; types and WaitForRunning unchanged.
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

/// <summary>
/// LINUX: there are no Gigabyte Windows services to watch, so every query is "not installed" and nothing is started. The
/// class keeps its Windows name so the shared ownership code (GigabyteGuard, LiveSystemProbe) links unchanged.
/// </summary>
public sealed class WindowsServiceControl : IServiceControl
{
    public static readonly WindowsServiceControl Instance = new();
    public ServiceRunState Query(string name) => ServiceRunState.NotInstalled;
    public ServiceStartOutcome Start(string name, TimeSpan timeout)
    {
        // same hardware-free boundary as Windows (the isolation self-test counts this refusal)
        if (Core.RunMode.Refuse($"start the {name} service")) return new(ServiceStartStatus.Failed, "hardware-free run");
        return new(ServiceStartStatus.Failed, "Windows services do not exist on Linux");
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
