using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HwSuite.Core;

/// <summary>Registry of HwSuite's long-running threads by OS thread id, so their CPU time can be read for profiling.</summary>
public static class ThreadCpu
{
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);
    [DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    static double _cyclesPerSecond;

    /// <summary>
    /// Whole-process CPU in cycles. Unlike the user/kernel times (which advance in 15.6 ms ticks), cycle counts are exact,
    /// so they resolve the small costs of still effects.
    /// </summary>
    public static ulong ProcessCycles() => QueryProcessCycleTime(GetCurrentProcess(), out var c) ? c : 0;

    /// <summary>Cycle-counter rate, calibrated once by spinning this thread for 200 ms of wall time.</summary>
    public static double CyclesPerSecond()
    {
        if (_cyclesPerSecond > 0) return _cyclesPerSecond;
        var best = 0.0;
        for (int i = 0; i < 3; i++)
        {
            QueryThreadCycleTime(GetCurrentThread(), out var c0);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 200) { }
            QueryThreadCycleTime(GetCurrentThread(), out var c1);
            best = Math.Max(best, (c1 - c0) / sw.Elapsed.TotalSeconds);   // max: a preempted spin under-counts, never over-counts
        }
        return _cyclesPerSecond = best;
    }

    static readonly Dictionary<int, string> Names = new();

    /// <summary>Call at the top of a worker thread.</summary>
    public static void Register(string name)
    {
        lock (Names) Names[(int)GetCurrentThreadId()] = name;
    }

    public sealed record Sample(string Name, TimeSpan User, TimeSpan Kernel);

    /// <summary>CPU times of the registered threads still alive; everything else is summed as "other".</summary>
    public static List<Sample> Snapshot()
    {
        var list = new List<Sample>();
        using var p = Process.GetCurrentProcess();
        TimeSpan ou = TimeSpan.Zero, ok = TimeSpan.Zero;
        Dictionary<int, string> names;
        lock (Names) names = new(Names);
        foreach (ProcessThread t in p.Threads)
        {
            try
            {
                if (names.TryGetValue(t.Id, out var n)) list.Add(new Sample(n, t.UserProcessorTime, t.PrivilegedProcessorTime));
                else { ou += t.UserProcessorTime; ok += t.PrivilegedProcessorTime; }
            }
            catch { }
        }
        list.Add(new Sample("other threads", ou, ok));
        return list;
    }
}
