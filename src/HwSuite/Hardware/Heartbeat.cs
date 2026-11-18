using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace HwSuite.Hardware;

/// <summary>
/// Liveness signal from the fan loop to the watchdog process. Both sides use only QueryUnbiasedInterruptTime
/// (100 ns units, monotonic, does not advance while the PC sleeps, so a sleep is never mistaken for a hang).
/// There is no fallback clock: if the query fails, the heartbeat records "unknown" and the watchdog makes no hang
/// decision rather than comparing values from two different time bases.
/// </summary>
public sealed class Heartbeat : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryUnbiasedInterruptTime(out ulong time100ns);

    /// <summary>Value written when the clock query failed.</summary>
    public const long Unknown = -1;

    public static string NameFor(int pid) => $"Local\\HwSuite.Heartbeat.{pid}";

    /// <summary>Clock source; replaced in tests to simulate failures.</summary>
    public static Func<long?> Clock { get; set; } = () => QueryUnbiasedInterruptTime(out var t) && t > 0 ? (long)t : null;

    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _view;
    bool _clockFailureLogged;

    Heartbeat(MemoryMappedFile mmf) { _mmf = mmf; _view = mmf.CreateViewAccessor(0, 8); }

    public static Heartbeat? CreateForCurrentProcess()
    {
        try { var hb = new Heartbeat(MemoryMappedFile.CreateOrOpen(NameFor(Environment.ProcessId), 8)); hb.Beat(); return hb; }
        catch { return null; }
    }

    public void Beat()
    {
        var now = Clock();
        if (now == null && !_clockFailureLogged) { _clockFailureLogged = true; Core.Log.Error("heartbeat: QueryUnbiasedInterruptTime failed; the watchdog cannot judge hangs while it fails"); }
        if (now != null) _clockFailureLogged = false;
        try { _view.Write(0, now ?? Unknown); } catch { }
    }

    /// <summary>
    /// Seconds since the last beat, from two readings of the same clock. Null when there is no heartbeat, the writer
    /// recorded a clock failure, this process's clock query fails, or the stored value is ahead of now.
    /// </summary>
    public static double? Age(long stored, long? now)
    {
        if (stored <= 0 || now is not long n || n < stored) return null;
        return (n - stored) / 1e7;
    }

    public static double? AgeSeconds(int pid)
    {
        long stored;
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(NameFor(pid), MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 8, MemoryMappedFileAccess.Read);
            stored = view.ReadInt64(0);
        }
        catch { return null; }
        return Age(stored, Clock());
    }

    public void Dispose() { try { _view.Dispose(); _mmf.Dispose(); } catch { } }
}

/// <summary>Counts how many consecutive samples repeated exactly. Used only as a warning heuristic on CPU package power.</summary>
public sealed class FreezeDetector
{
    readonly int _limit;
    double? _last;
    int _same;

    public FreezeDetector(int limitSamples) { _limit = limitSamples; }

    public int SameCount => _same;

    /// <summary>Feed one sample; true once the value has repeated exactly for the limit number of consecutive samples.</summary>
    public bool Frozen(double? value)
    {
        if (value is not double v) { _last = null; _same = 0; return false; }
        if (_last is double l && l.Equals(v)) _same++; else _same = 0;
        _last = v;
        return _same >= _limit;
    }
}
