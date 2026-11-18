using System.Diagnostics;
using System.Runtime.InteropServices;
using HwSuite.Core;

namespace HwSuite.Lighting;

/// <summary>
/// LINUX replacement for src\HwSuite\Rgb\PrecisionTimer.cs. Linux sleeps are already high resolution: clock_nanosleep on
/// CLOCK_MONOTONIC with a relative time (typically ~50-100 µs late with the default timer slack). Same API.
/// </summary>
public static class PrecisionTimer
{
    [StructLayout(LayoutKind.Sequential)] struct Timespec { public long Sec, Nsec; }
    [DllImport("libc")] static extern int clock_nanosleep(int clock, int flags, ref Timespec req, IntPtr rem);
    const int ClockMonotonic = 1;
    static bool _broken;

    public static bool HighResolutionOnThisThread => OperatingSystem.IsLinux() && !_broken;

    public static void Wait(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        if (OperatingSystem.IsLinux() && !_broken)
        {
            try
            {
                long ns = Math.Max(1000, duration.Ticks * 100);
                var ts = new Timespec { Sec = ns / 1_000_000_000, Nsec = ns % 1_000_000_000 };
                int rc = clock_nanosleep(ClockMonotonic, 0, ref ts, IntPtr.Zero);
                if (rc == 0 || rc == 4 /*EINTR*/) return;
            }
            catch (Exception ex) { _broken = true; Log.Warn("clock_nanosleep unavailable: " + ex.Message); }
        }
        Thread.Sleep(Math.Max(1, (int)Math.Ceiling(duration.TotalMilliseconds)));
    }

    /// <summary>Windows-only concern (timer resolution of hidden windows); nothing to do on Linux.</summary>
    public static void KeepTimerResolutionWhenHidden() { }

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
