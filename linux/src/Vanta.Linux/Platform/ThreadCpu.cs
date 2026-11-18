using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HwSuite.Core;

/// <summary>
/// LINUX replacement for src\HwSuite\Core\ThreadCpu.cs. Same API. "Cycles" are CPU nanoseconds here
/// (CLOCK_PROCESS_CPUTIME_ID), so CyclesPerSecond is 1e9 and every caller's cycles / rate arithmetic still gives seconds.
/// Thread ids are kernel tids (gettid), which is what ProcessThread.Id reports on Linux.
/// </summary>
public static class ThreadCpu
{
    [DllImport("libc")] static extern int gettid();
    [StructLayout(LayoutKind.Sequential)] struct Timespec { public long Sec, Nsec; }
    [DllImport("libc")] static extern int clock_gettime(int clock, out Timespec ts);
    const int ClockProcessCputime = 2;

    public static ulong ProcessCycles()
    {
        try { return clock_gettime(ClockProcessCputime, out var t) == 0 ? (ulong)(t.Sec * 1_000_000_000L + t.Nsec) : 0; }
        catch { return 0; }
    }

    public static double CyclesPerSecond() => 1e9;

    static readonly Dictionary<int, string> Names = new();

    public static void Register(string name)
    {
        int id;
        try { id = gettid(); } catch { id = Environment.CurrentManagedThreadId; }
        lock (Names) Names[id] = name;
    }

    public sealed record Sample(string Name, TimeSpan User, TimeSpan Kernel);

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
