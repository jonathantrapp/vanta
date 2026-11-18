using System.Diagnostics;
using System.Runtime.InteropServices;
using HwSuite.Core;

namespace HwSuite.Lighting;

/// <summary>
/// Frame pacing that does not depend on the system timer resolution. Thread.Sleep rounds up to the 15.6 ms default tick
/// whenever a 1 ms timeBeginPeriod request is not in effect for the process (Windows 11 may ignore it for processes
/// with no visible window, such as HwSuite in the tray), which turned a 60/s strip target into ~32 accepted updates/s in
/// the Phase 7 A/B run. A high-resolution waitable timer (Windows 10 1803+) wakes within about 0.3 ms on this PC
/// (docs\phase7\timer-probe). Each thread gets its own timer; if one cannot be created, Thread.Sleep is used.
/// </summary>
public static class PrecisionTimer
{
    const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    sealed class Handle
    {
        public readonly IntPtr Value;
        public Handle(IntPtr v) { Value = v; }
        ~Handle() { if (Value != IntPtr.Zero) CloseHandle(Value); }
    }

    [ThreadStatic] static Handle? _timer;
    [ThreadStatic] static bool _tried;
    static int _optOutDone;

    /// <summary>True when the calling thread waits on a high-resolution timer (false = Thread.Sleep fallback).</summary>
    public static bool HighResolutionOnThisThread { get { EnsureTimer(); return _timer != null; } }

    static void EnsureTimer()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            var h = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (h != IntPtr.Zero) _timer = new Handle(h);
            else Log.Warn($"high-resolution timer unavailable on thread '{Thread.CurrentThread.Name}' (error {Marshal.GetLastWin32Error()}); using Thread.Sleep");
        }
        catch (Exception ex) { Log.Warn("high-resolution timer unavailable: " + ex.Message); }
    }

    /// <summary>Block the calling thread for about <paramref name="duration"/>. Durations of 0 or less return at once.</summary>
    public static void Wait(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        EnsureTimer();
        if (_timer != null)
        {
            long due = -Math.Max(1, duration.Ticks);   // negative = relative, in 100 ns units
            if (SetWaitableTimer(_timer.Value, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(_timer.Value, (uint)Math.Min(int.MaxValue, duration.TotalMilliseconds + 100));
                return;
            }
        }
        Thread.Sleep(Math.Max(1, (int)Math.Ceiling(duration.TotalMilliseconds)));
    }

    /// <summary>
    /// Ask Windows to honour this process's timer-resolution request even while it has no visible window
    /// (SetProcessInformation, ProcessPowerThrottling, PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION cleared).
    /// Only affects timer resolution, not CPU speed or efficiency mode.
    /// </summary>
    public static void KeepTimerResolutionWhenHidden()
    {
        if (Interlocked.Exchange(ref _optOutDone, 1) == 1) return;
        try
        {
            var state = new PowerThrottlingState { Version = 1, ControlMask = 0x4, StateMask = 0 };
            if (!SetProcessInformation(GetCurrentProcess(), 4, ref state, Marshal.SizeOf<PowerThrottlingState>()))
                Log.Info($"timer-resolution opt-out not applied (error {Marshal.GetLastWin32Error()}); high-resolution waits are used regardless");
        }
        catch (Exception ex) { Log.Info("timer-resolution opt-out unavailable: " + ex.Message); }
    }

    /// <summary>Measure the calling thread's actual wait accuracy for a period (used by tests and the benchmark).</summary>
    public static (double median, double p95, double max) Measure(TimeSpan period, int samples)
    {
        var list = new List<double>(samples);
        var sw = Stopwatch.StartNew();
        double last = 0;
        for (int i = 0; i < samples; i++)
        {
            Wait(period);
            double t = sw.Elapsed.TotalMilliseconds;
            list.Add(t - last); last = t;
        }
        list.Sort();
        return (list[list.Count / 2], list[(int)((list.Count - 1) * 0.95)], list[^1]);
    }
}
