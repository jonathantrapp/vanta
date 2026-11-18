using System.Diagnostics;

namespace HwSuite.Hardware;

/// <summary>
/// Whether a vendor process (for example the iLovbee DeviceDriver.exe) is running, cheap enough to ask before every
/// keyboard live frame. Listing processes costs about 2-4 ms on this PC (331 processes, measured by the self-test), too
/// much for ~12 frames per second, so a background poll refreshes the answer every <see cref="PollInterval"/>.
///
/// Freshness contract (Phase 11): <see cref="IsRunning"/> answers from an observation that STARTED at most
/// <see cref="MaxAge"/> (250 ms) before the call. An observation is timestamped when its process list began, so its age is
/// never understated (a process that started while the list ran may be missed by that list). If the poll is late (thread
/// pool delay, the first read after an idle pause), the getter lists processes itself before answering. A failed
/// enumeration is <see cref="State.Unknown"/>, which <see cref="IsRunning"/> reports as running: the caller does not write.
/// This bounds the decision, not the transfer: a write already in progress when the vendor process starts completes.
/// <see cref="CheckNow"/> lists processes on the spot (used for the stored "LED(Save)" command and test writes).
/// Measured and rejected (Phase 10): the WMI process-start notification (Win32_ProcessStartTrace) arrived 1.8 s after a
/// process started, later than polling, so it is not used.
/// </summary>
public sealed class ProcessPresence : IDisposable
{
    public enum State { Absent, Running, Unknown }

    public static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(200);
    /// <summary>Oldest observation <see cref="IsRunning"/> answers from (measured from the start of its process list).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(250);
    /// <summary>Polling pauses when nobody has read the state for this long; the next read lists processes itself.</summary>
    public const int IdlePauseMs = 2000;

    readonly string _name;   // without ".exe", as Process.GetProcessesByName expects
    readonly Timer? _poll;
    readonly object _store = new();
    State _state = State.Unknown;
    long _observedAt = long.MinValue;   // Clock() when the list that produced _state started
    long _lastRead;
    long _polls;

    public TimeSpan PollInterval { get; }
    public long Polls => Interlocked.Read(ref _polls);
    /// <summary>Milliseconds since an arbitrary epoch; replaced by tests.</summary>
    public Func<long> Clock { get; set; } = () => Environment.TickCount64;
    /// <summary>Lists processes and says whether the vendor process is among them; replaced by tests. May throw.</summary>
    public Func<bool> Lister { get; set; }
    public string Mode => $"process list every {PollInterval.TotalMilliseconds:0} ms; live frames use a state at most {MaxAge.TotalMilliseconds:0} ms old (listed on the spot if the poll is late); stored writes list processes at the write";
    public State LastState { get { lock (_store) return _state; } }

    public ProcessPresence(string exeName, TimeSpan? poll = null, bool startTimer = true)
    {
        _name = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName[..^4] : exeName;
        PollInterval = poll ?? DefaultPoll;
        Lister = () =>
        {
            var p = Process.GetProcessesByName(_name);
            foreach (var x in p) x.Dispose();
            return p.Length > 0;
        };
        if (!startTimer) return;
        CheckNow();
        // polls only while someone reads the state (live frames); an idle app lists no processes
        _poll = new Timer(_ => { try { if (Clock() - Interlocked.Read(ref _lastRead) < IdlePauseMs) CheckNow(); } catch { } }, null, PollInterval, PollInterval);
    }

    /// <summary>Age in ms of the current observation at <paramref name="now"/> (measured from the start of its list).</summary>
    public long ObservationAgeMs(long now) { lock (_store) return _observedAt == long.MinValue ? long.MaxValue : now - _observedAt; }

    /// <summary>
    /// True if the process runs, or if that could not be established (Unknown). The answer comes from an observation at most
    /// <see cref="MaxAge"/> old; an older one is replaced by listing processes now.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            long now = Clock();
            Interlocked.Exchange(ref _lastRead, now);
            lock (_store)
                if (_observedAt != long.MinValue && now - _observedAt <= MaxAge.TotalMilliseconds) return _state != State.Absent;
            return Observe() != State.Absent;   // the poll is late or paused: list now rather than answer from an old state
        }
    }

    /// <summary>List processes now and update the state. True if running or unknown (enumeration failed).</summary>
    public bool CheckNow() => Observe() != State.Absent;

    State Observe()
    {
        Interlocked.Increment(ref _polls);
        long started = Clock();
        State s;
        try { s = Lister() ? State.Running : State.Absent; }
        catch { s = State.Unknown; }
        lock (_store)
        {
            // a slower, older list must not overwrite a newer observation
            if (started >= _observedAt) { _state = s; _observedAt = started; }
        }
        return s;
    }

    public void Dispose() { try { _poll?.Dispose(); } catch { } }
}
