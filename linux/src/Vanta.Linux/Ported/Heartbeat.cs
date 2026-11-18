// PORTED from src\HwSuite\Hardware\Heartbeat.cs (Windows 1.20.0): clock = CLOCK_MONOTONIC; shared 8 bytes = mapped file in /run/vanta
// instead of a named Windows mapping. Age() and the no-substitute-clock rule are unchanged.
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
    /// <summary>LINUX: CLOCK_MONOTONIC (what Stopwatch uses on Linux) also stops during suspend, like QueryUnbiasedInterruptTime.</summary>
    static long? Monotonic100ns() { long t = System.Diagnostics.Stopwatch.GetTimestamp(); return t > 0 ? (long)(t * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency)) : null; }

    /// <summary>Value written when the clock query failed.</summary>
    public const long Unknown = -1;

    /// <summary>LINUX: named shared memory is not available in .NET on Linux, so the 8 bytes live in a small file mapped by both processes.</summary>
    public static string NameFor(int pid) => Path.Combine(RunDir, $"heartbeat.{pid}");
    /// <summary>Root (the headless service): /run/vanta. A user session: $XDG_RUNTIME_DIR/vanta. Otherwise a temp folder.</summary>
    public static string RunDir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            var d = Platform.LibC.IsRoot && Directory.Exists("/run/vanta") ? "/run/vanta"
                  : !string.IsNullOrEmpty(xdg) ? Path.Combine(xdg, "vanta") : Path.Combine(Path.GetTempPath(), "vanta-" + Environment.UserName);
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    /// <summary>Clock source; replaced in tests to simulate failures.</summary>
    public static Func<long?> Clock { get; set; } = Monotonic100ns;

    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _view;
    bool _clockFailureLogged;

    Heartbeat(MemoryMappedFile mmf) { _mmf = mmf; _view = mmf.CreateViewAccessor(0, 8); }

    public static Heartbeat? CreateForCurrentProcess()
    {
        try { var path = NameFor(Environment.ProcessId); using (var f = new FileStream(path, FileMode.Create, FileAccess.ReadWrite)) f.SetLength(8); var hb = new Heartbeat(MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 8, MemoryMappedFileAccess.ReadWrite)) { _path = path }; hb.Beat(); return hb; }
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
            using var f = new FileStream(NameFor(pid), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var b = new byte[8];
            if (f.Read(b, 0, 8) != 8) return null;
            stored = BitConverter.ToInt64(b, 0);
        }
        catch { return null; }
        return Age(stored, Clock());
    }

    string? _path;
    public void Dispose() { try { _view.Dispose(); _mmf.Dispose(); } catch { } try { if (_path != null) File.Delete(_path); } catch { } }
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
