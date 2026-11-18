namespace HwSuite.Core;

/// <summary>One telemetry sample. A null value is a reading that was not available at that time (drawn as a gap).</summary>
public readonly record struct HistoryPoint(DateTime Time, double? Cpu, double? Gpu, double? Ram, double? Board);

/// <summary>
/// App-wide telemetry history with real timestamps, recorded from every telemetry update whichever page is open (or with
/// the window hidden). Bounded to <see cref="Capacity"/>; thread-safe. Values are stored exactly as read: nothing is
/// interpolated or carried forward, so missing readings stay missing.
/// </summary>
public sealed class TelemetryHistory
{
    public static readonly TimeSpan Capacity = TimeSpan.FromMinutes(10);
    readonly object _gate = new();
    readonly List<HistoryPoint> _points = new();
    public DateTime? FirstSample { get; private set; }

    public void Add(HistoryPoint p)
    {
        lock (_gate)
        {
            FirstSample ??= p.Time;
            _points.Add(p);
            int drop = 0;
            while (drop < _points.Count && p.Time - _points[drop].Time > Capacity) drop++;
            if (drop > 0) _points.RemoveRange(0, drop);
        }
    }

    /// <summary>Samples newer than <paramref name="window"/> before <paramref name="now"/>, oldest first.</summary>
    public List<HistoryPoint> Since(TimeSpan window, DateTime now)
    {
        lock (_gate) return _points.Where(p => now - p.Time <= window).ToList();
    }

    public int Count { get { lock (_gate) return _points.Count; } }
}
