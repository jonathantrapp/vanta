using System.Diagnostics;
using HwSuite.Core;
using HwSuite.Hardware;
using HwSuite.Lighting.Devices;

namespace HwSuite.Lighting;

/// <summary>
/// One physical lighting target. The engine posts frames at 60 FPS; each frame-driven sink owns a worker thread that
/// throttles to the device's safe rate, drops unchanged frames, and isolates failures so that an unplugged or
/// sleeping peripheral can never stall the other devices. Counters keep apart what the engine offered (Posted), what
/// was tried (Attempts), what was written and accepted (FramesSent), what needed no hardware write (NoWrite, e.g.
/// RAM running an onboard effect), what was skipped as unchanged (FramesDropped), what was cancelled before reaching the
/// hardware (Cancelled) and what failed (Errors). None of them says the LEDs physically changed; only looking at them does.
///
/// Cancellation: every queued frame carries the epoch it was queued in. Excluding the device, revoking ownership, stopping
/// output or suspending calls <see cref="Invalidate"/>, which bumps the epoch and drops the queue; the worker re-checks
/// epoch and admission under the same lock right before the write. A transfer that had already started cannot be recalled:
/// it completes, is counted in InFlightAtInvalidate, and callers that must order a later write after it use WaitIdle.
///
/// Unchanged frames are skipped only while the device is known to still show the last one: <see cref="Invalidate"/> and
/// <see cref="ForgetDisplayed"/> (another write path changed the device, a reopened handle, a reconnect) end that, so the
/// next frame is written even if it is identical. A sink may also ask for unchanged frames to be refreshed
/// (<see cref="RefreshUnchangedAfter"/>).
/// </summary>
public abstract class RgbSink : IDisposable
{
    readonly AutoResetEvent _signal = new(false);
    readonly Thread _worker;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    volatile bool _stop;
    sealed record Queued(Rgb[] Frame, long Epoch, long Request);
    readonly object _queueGate = new();
    Queued? _pending;
    long _epoch;
    bool _inFlight;
    volatile bool _suspended;
    readonly ManualResetEventSlim _idle = new(true);
    Rgb[]? _last;
    volatile bool _forgetDisplayed;
    TimeSpan _lastSend = TimeSpan.FromSeconds(-10);
    int _consecutiveFailures;
    TimeSpan _retryAt;
    readonly object _pacingGate = new();
    readonly Queue<double> _successfulIntervals = new();
    TimeSpan? _lastSuccess;

    readonly Queue<double> _sendDurations = new();

    // ---- acknowledgement of the current request (the look the engine asks for now), from existing write results only
    readonly object _ackGate = new();
    long _request;          // request generation the engine set last (0: never asked)
    long _outcomeFor = -1;  // newest request generation that has an outcome
    bool _outcomeOk;
    bool _withholdAck, _ackFailed;

    /// <summary>
    /// The engine asks for a new look (effect, colours, brightness, speed, layout, or the device was re-admitted). Frames
    /// queued from now on carry this generation; only their write results count for <see cref="FrameAck"/>.
    /// </summary>
    public void Request(long generation) { lock (_ackGate) { if (generation > _request) _request = generation; } }
    public long RequestGeneration { get { lock (_ackGate) return _request; } }

    /// <summary>
    /// Whether the device's interface accepted a frame of the current request: Accepted after a successful write (or an
    /// unchanged frame skipped because the device already holds that accepted frame), Failed when the newest attempt for
    /// it failed, Pending until either happens. A later success clears an earlier failure. API acceptance only: it is not
    /// an optical confirmation that the LEDs changed.
    /// </summary>
    public LightAck FrameAck
    {
        get
        {
            lock (_ackGate)
            {
                if (_request == 0) return LightAck.Unknown;
                if (_outcomeFor < _request) return LightAck.Pending;
                return _outcomeOk ? LightAck.Accepted : LightAck.Failed;
            }
        }
    }

    void Outcome(long request, bool ok)
    {
        lock (_ackGate) { if (request >= _outcomeFor) { _outcomeFor = request; _outcomeOk = ok; } }
    }

    /// <summary>From Apply: it returned true, but the device has not (yet) accepted this request (retrying, nothing sent).</summary>
    protected void WithholdAcknowledgement() => _withholdAck = true;
    /// <summary>From Apply: it returned true (nothing to retry now), but the request was not accepted.</summary>
    protected void AcknowledgeAsFailed() => _ackFailed = true;

    /// <summary>Bounded accepted-frame intervals; these measure API completion, not physical LED refresh.</summary>
    public double[] SuccessfulIntervalsMs { get { lock (_pacingGate) return _successfulIntervals.ToArray(); } }
    /// <summary>Bounded durations of the device write calls (how long the bus/API took, not when LEDs changed).</summary>
    public double[] SendDurationsMs { get { lock (_pacingGate) return _sendDurations.ToArray(); } }
    public void ResetPacingSamples() { lock (_pacingGate) { _successfulIntervals.Clear(); _sendDurations.Clear(); _lastSuccess = null; } }

    public abstract string Name { get; }
    public abstract string Kind { get; }
    public string Status { get; protected set; } = "not started";
    public bool Available { get; protected set; }
    public abstract int LedCount { get; }
    public abstract double[] Positions { get; }
    protected abstract double MaxHz { get; }
    protected virtual int MinDelta => 1;
    /// <summary>Write an unchanged frame again once this long has passed since the last write (null: never). Frames must still be offered.</summary>
    protected virtual TimeSpan? RefreshUnchangedAfter => null;
    /// <summary>For still effects the engine re-offers an identical frame after this long (it skips identical frames in between).</summary>
    public virtual double StillRepostSeconds => 1.0;
    /// <summary>Hardware another program may also drive (null = none known).</summary>
    public virtual HwResource? Resource => null;
    /// <summary>
    /// How far ahead of the engine's clock this device's frames are sampled, to offset its own transfer time (seconds).
    /// Every device samples the same monotonic timeline; a device whose frame takes tens of milliseconds to reach its LEDs
    /// is given that timeline slightly ahead so it does not lag the others. 0 for devices whose transfer is negligible.
    /// </summary>
    public virtual double LeadSeconds => 0;
    /// <summary>Engine time (s) of the frame being posted, set by the engine just before <see cref="Post"/>.</summary>
    public double FrameTime { get; internal set; } = double.NaN;
    /// <summary>Which device group a stand-in sink plays (tests and benchmarks); the real sinks are recognised by type.</summary>
    public virtual DeviceRole? RoleHint => null;
    /// <summary>Independent LED runs (memory controllers); null = one run in index order.</summary>
    public virtual IReadOnlyList<int>? LedRuns => null;
    /// <summary>True only for the D_LED strip: it is offered frames at the strip rate; every other device at 60/s.</summary>
    public virtual bool FollowsStripRate => false;
    /// <summary>Whether the engine includes a device type it has no setting for (test devices).</summary>
    public virtual bool EnabledByDefault => false;
    /// <summary>The device's own limit on accepted updates per second.</summary>
    public double RateLimitHz => MaxHz;
    bool _enabled = true;
    /// <summary>Included in the effect. Turning it off cancels anything still queued.</summary>
    public bool Enabled
    {
        get => _enabled;
        set { bool was = _enabled; _enabled = value; if (was && !value) Invalidate("excluded"); }
    }
    string? _blocked;
    /// <summary>Set by the engine while another program owns the hardware: nothing is queued or written, and anything queued is cancelled.</summary>
    public string? Blocked
    {
        get => _blocked;
        set { var was = _blocked; _blocked = value; if (value != null && was == null) Invalidate("ownership lost: " + value); }
    }
    /// <summary>Whether a frame may be queued or written right now.</summary>
    public bool Admissible => _enabled && Available && _blocked == null && !_suspended;
    public bool Suspended => _suspended;
    public long FramesPosted { get; private set; }
    /// <summary>Applied without any hardware write (the device already shows the requested setting).</summary>
    public long NoWrite { get; private set; }
    /// <summary>Queued frames dropped by invalidation or by the admission re-check at the write boundary.</summary>
    public long Cancelled { get; private set; }
    /// <summary>Invalidations that found a transfer already running (it completed; it could not be recalled).</summary>
    public long InFlightAtInvalidate { get; private set; }
    public long Attempts { get; private set; }
    public long FramesSent { get; private set; }
    public long FramesDropped { get; private set; }
    public long Errors { get; private set; }
    public long Faults { get; private set; }
    public double LastSendMs { get; private set; }
    public double MaxSendMs { get; private set; }
    public Rgb[] Preview { get; protected set; } = Array.Empty<Rgb>();

    protected RgbSink()
    {
        _worker = new Thread(Run) { IsBackground = true, Name = "RgbSink" };
    }

    public virtual void StartWorker() { if (!_worker.IsAlive) { _worker.Name = "Rgb: " + Name; _worker.Start(); } }

    /// <summary>1.15.2: this device's colour balance (null or identity: frames are sent as rendered).</summary>
    protected virtual ColorBalance? OutputBalance => null;

    public virtual void Post(Rgb[] frame)
    {
        Preview = frame;   // the preview shows the colours as chosen; the balance applies to what is sent
        if (!Admissible) return;
        FramesPosted++;
        long request = RequestGeneration;
        var send = OutputBalance is ColorBalance b ? b.Apply(frame) : frame;
        lock (_queueGate) _pending = new Queued(send, _epoch, request);
        _signal.Set();
    }

    /// <summary>
    /// Cancel queued work. Returns true when a transfer was already running; it completes (bus and USB transfers cannot be
    /// recalled). Never blocks.
    /// </summary>
    public bool Invalidate(string reason)
    {
        bool inFlight;
        lock (_queueGate)
        {
            _epoch++;
            if (_pending != null) { _pending = null; Cancelled++; }
            inFlight = _inFlight;
            if (inFlight) InFlightAtInvalidate++;
        }
        _forgetDisplayed = true;   // what the device shows is no longer known to match the last frame
        return inFlight;
    }

    /// <summary>
    /// The device may no longer show the last frame written (something else wrote to it, or its handle was reopened): the
    /// next frame is written even if it is unchanged. Queued work is kept. Safe from any thread.
    /// </summary>
    public void ForgetDisplayed() => _forgetDisplayed = true;

    /// <summary>Wait (bounded) until no transfer is running. False if one is still running after the timeout.</summary>
    public bool WaitIdle(TimeSpan timeout) => _idle.Wait(timeout);

    /// <summary>System is going to sleep: cancel queued work and refuse new work until <see cref="Resume"/>.</summary>
    public virtual void Suspend() { _suspended = true; Invalidate("system suspend"); }
    /// <summary>Output is running again after resume (the engine calls this when it restarts rendering, after its grace period).</summary>
    public virtual void Resume() { _suspended = false; }

    /// <summary>Set by Apply when it succeeded without writing to the hardware.</summary>
    protected bool AppliedWithoutWrite { get; set; }
    /// <summary>Called from Apply when nothing reached the device, so the next identical frame is not dropped as "unchanged".</summary>
    protected void ForgetLastFrame() => _forgetLast = true;
    bool _forgetLast;

    /// <summary>Called once when lighting output starts (device init sequence).</summary>
    public virtual void Activate() { _last = null; }
    /// <summary>Called when lighting output stops or the app exits; should leave the hardware in a steady, sane look.</summary>
    public virtual void Deactivate(Rgb steady) { }
    /// <summary>Apply a frame to the hardware. Return false on failure.</summary>
    protected abstract bool Apply(Rgb[] frame);
    /// <summary>Called when the sink faults (3 failures in a row). Reopen the device if possible.</summary>
    protected virtual bool Recover() => false;

    public virtual string StatsText => Available
        ? $"offered {FramesPosted}  tried {Attempts}  written {FramesSent}  no-write {NoWrite}  unchanged {FramesDropped}  cancelled {Cancelled}  errors {Errors}"
        : "";

    void Run()
    {
        ThreadCpu.Register("device worker: " + Name);
        while (!_stop)
        {
            _signal.WaitOne(100);
            if (_stop) break;
            Queued? q;
            lock (_queueGate) { q = _pending; _pending = null; }
            if (q == null) continue;
            var frame = q.Frame;

            var now = _clock.Elapsed;
            if (now < _retryAt) continue;

            // Schedule from the START of the previous send (not its end) so a send's own duration does not stretch the period,
            // and allow 1 ms of tolerance so a frame that arrives marginally early is not held back a whole period.
            // Only the newest frame is ever kept, so a slow send never produces a catch-up burst.
            var interval = TimeSpan.FromSeconds(1.0 / Math.Max(0.05, MaxHz));
            var wait = _lastSend + interval - TimeSpan.FromMilliseconds(1.0) - now;
            if (wait > TimeSpan.Zero)
            {
                // precise wait (PrecisionTimer): a Thread.Sleep here rounded up to a 15.6 ms tick whenever the 1 ms timer
                // request was not in effect, halving the 60/s strip rate
                lock (_queueGate) { if (_pending == null && q.Epoch == _epoch) _pending = q; }
                PrecisionTimer.Wait(wait < TimeSpan.FromMilliseconds(50) ? wait : TimeSpan.FromMilliseconds(50));
                _signal.Set();
                continue;
            }

            if (_forgetDisplayed) { _forgetDisplayed = false; _last = null; }
            bool refreshDue = RefreshUnchangedAfter is TimeSpan refresh && now - _lastSend >= refresh;
            if (_last != null && _last.Length == frame.Length && !refreshDue && !Differs(_last, frame, MinDelta))
            {
                FramesDropped++;
                Outcome(q.Request, true);   // the device holds this frame from an accepted write
                continue;
            }

            // admission at the write boundary: anything invalidated after it was queued is dropped here
            lock (_queueGate)
            {
                if (q.Epoch != _epoch || !Admissible) { Cancelled++; continue; }
                _inFlight = true;
                _idle.Reset();
            }
            try
            {
                var started = _clock.Elapsed;
                Attempts++;
                AppliedWithoutWrite = false;
                _withholdAck = false; _ackFailed = false;
                bool ok = Apply(frame);
                LastSendMs = (_clock.Elapsed - started).TotalMilliseconds;
                MaxSendMs = Math.Max(MaxSendMs, LastSendMs);
                lock (_pacingGate) { _sendDurations.Enqueue(LastSendMs); if (_sendDurations.Count > 8192) _sendDurations.Dequeue(); }
                if (ok)
                {
                    var completed = _clock.Elapsed;
                    lock (_pacingGate)
                    {
                        if (_lastSuccess is TimeSpan previous)
                        {
                            _successfulIntervals.Enqueue((completed - previous).TotalMilliseconds);
                            if (_successfulIntervals.Count > 8192) _successfulIntervals.Dequeue();
                        }
                        _lastSuccess = completed;
                    }
                    bool reached = !_forgetLast;   // ForgetLastFrame: nothing reached the device
                    _last = _forgetLast ? null : frame; _forgetLast = false; _lastSend = started; _consecutiveFailures = 0;
                    if (AppliedWithoutWrite) NoWrite++; else FramesSent++;
                    if (_ackFailed) Outcome(q.Request, false);
                    else if (reached && !_withholdAck) Outcome(q.Request, true);
                }
                else { Outcome(q.Request, false); Fail("write failed"); }
            }
            catch (Exception ex) { Outcome(q.Request, false); Fail(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                lock (_queueGate) _inFlight = false;
                _idle.Set();
            }
        }
    }

    void Fail(string why)
    {
        Errors++;
        _last = null;
        if (++_consecutiveFailures >= 3)
        {
            Faults++;
            _consecutiveFailures = 0;
            bool recovered = false;
            try { recovered = Recover(); } catch (Exception ex) { Log.Warn($"{Name}: recovery failed: {ex.Message}"); }
            Status = recovered ? $"recovered after errors ({why})" : $"faulted ({why}), retrying in 10 s";
            _retryAt = _clock.Elapsed + TimeSpan.FromSeconds(recovered ? 1 : 10);
            Log.Warn($"{Name}: 3 consecutive failures ({why}); {(recovered ? "device reopened" : "pausing 10 s")}");
        }
    }

    static bool Differs(Rgb[] a, Rgb[] b, int minDelta)
    {
        for (int i = 0; i < a.Length; i++) if (a[i].MaxDelta(b[i]) >= minDelta) return true;
        return false;
    }

    public virtual void Dispose()
    {
        _stop = true; _signal.Set();
        try { _worker.Join(500); } catch { }
        _signal.Dispose();
    }

    /// <summary>Tests only: run the queued frame through the same admission and write path synchronously.</summary>
    internal bool DrainOnceForTest()
    {
        Queued? q;
        lock (_queueGate) { q = _pending; _pending = null; }
        if (q == null) return false;
        lock (_queueGate) { if (q.Epoch != _epoch || !Admissible) { Cancelled++; return false; } }
        AppliedWithoutWrite = false;
        _withholdAck = false; _ackFailed = false; _forgetLast = false;
        bool ok;
        try { ok = Apply(q.Frame); } catch { ok = false; }
        if (ok)
        {
            if (AppliedWithoutWrite) NoWrite++; else FramesSent++;
            if (_ackFailed) Outcome(q.Request, false); else if (!_forgetLast && !_withholdAck) Outcome(q.Request, true);
            _forgetLast = false;
        }
        else { Errors++; Outcome(q.Request, false); }
        return ok;
    }
}

public sealed class MotherboardSink : RgbSink
{
    readonly RgbConfig _cfg;
    readonly object _reopenGate = new();
    Fusion2UsbDevice? _dev;
    DateTime _lastReopen = DateTime.MinValue;
    public override string Name => "Gigabyte motherboard (LED_C, PCI-E accent)";
    public override string Kind => "USB HID";
    public override int LedCount => 2;
    public static readonly double[] BasePositions = { 0.28, 0.32 };
    public override double[] Positions => BasePositions;
    public override HwResource? Resource => HwResource.RgbUsb;
    protected override double MaxHz => Math.Clamp(_cfg.MotherboardMaxHz, 1, 60);
    public Fusion2UsbDevice? Device => _dev;

    public MotherboardSink(RgbConfig cfg) { _cfg = cfg; }
    protected override ColorBalance? OutputBalance => _cfg.Balance?.Board;

    public bool TryOpen()
    {
        try
        {
            var d = new Fusion2UsbDevice();
            if (!d.Open(out var err)) { Status = err; Available = false; return false; }
            _dev = d; Available = true;
            Status = $"{d.ProductString}, fw 0x{d.FirmwareRaw:X8}";
            return true;
        }
        catch (Exception ex) { Status = ex.Message; Available = false; return false; }
    }

    /// <summary>Close and reopen the IT5701 handle (after repeated failures or a resume), then re-run its init sequence. At most every 5 s.</summary>
    public bool ReopenDevice()
    {
        lock (_reopenGate)
        {
            if ((DateTime.UtcNow - _lastReopen).TotalSeconds < 5) return _dev?.IsOpen == true;
            _lastReopen = DateTime.UtcNow;
            var d = new Fusion2UsbDevice();
            if (!d.Open(out var err)) { Log.Warn("IT5701 reopen failed: " + err); return false; }
            var old = _dev;
            _dev = d;
            try { old?.Dispose(); } catch { }
            try { d.InitializeLighting(); } catch { }
            Log.Info("IT5701 controller reopened");
            return true;
        }
    }

    protected override bool Recover() => ReopenDevice();

    public override void Activate() { base.Activate(); _dev?.InitializeLighting(); }

    protected override bool Apply(Rgb[] f)
    {
        var dev = _dev;
        if (dev == null) return false;
        bool ok = dev.SetLed(Fusion2UsbDevice.LedC, f[0]) & dev.SetLed(Fusion2UsbDevice.PcieAccent, f[1]);
        return ok & dev.Apply();
    }

    public override void Deactivate(Rgb steady)
    {
        var dev = _dev;
        if (dev == null) return;
        steady = _cfg.Balance?.Board?.Apply(steady) ?? steady;
        try { dev.SetLed(Fusion2UsbDevice.LedC, steady); dev.SetLed(Fusion2UsbDevice.PcieAccent, steady); dev.Apply(); } catch { }
    }

    public override void Dispose() { base.Dispose(); _dev?.Dispose(); }
}

/// <summary>
/// The D_LED addressable header on the IT5701 (the LANCOOL 215 case fans). Shares the motherboard sink's HID handle.
/// Channel order is chosen by the user (default GRB, the standard WS2812B order) and applied when the packets are built.
/// A short single-colour test pattern can override the effect so the owner can check red, green and blue separately.
/// </summary>
public sealed class DLedSink : RgbSink
{
    readonly RgbConfig _cfg;
    readonly MotherboardSink _board;
    double[] _positions = Array.Empty<double>();
    int _positionsFor = -1;
    Rgb? _testColor;
    DateTime _testUntil;

    public override string Name => $"D_LED header: case fans ({LedCount} LEDs)";
    public override string Kind => "USB HID (IT5701)";
    public override int LedCount => Math.Clamp(_cfg.DLedCount, 1, 120);
    public override HwResource? Resource => HwResource.RgbUsb;
    public override double[] Positions
    {
        get
        {
            int n = LedCount;
            if (n != _positionsFor)
            {
                _positions = PositionsFor(n);
                _positionsFor = n;
            }
            return _positions;
        }
    }
    /// <summary>Chain order along the shared line (which LED sits on which fan, or on the logo, is not mapped).</summary>
    public static double[] PositionsFor(int n) => Enumerable.Range(0, n).Select(i => n == 1 ? 0.12 : 0.02 + 0.22 * i / (n - 1)).ToArray();
    protected override double MaxHz => _cfg.EffectiveDLedHz;
    public override bool FollowsStripRate => true;

    public DLedSink(RgbConfig cfg, MotherboardSink board)
    {
        _cfg = cfg; _board = board;
        Available = board.Available && board.Device != null;
        Status = Available ? DescribeStatus() : "needs the IT5701 motherboard controller (" + board.Status + ")";
    }

    public string CurrentStatus() => Available && _board.Device != null ? DescribeStatus() : Status;

    string DescribeStatus()
    {
        var d = _board.Device!;
        var cal = Fusion2UsbDevice.DecodeCalibration(d.StripCalibrationWord);
        return $"{_cfg.StripOrder.ToString().ToUpperInvariant()} byte order, controller capacity class {d.ReportedStripCapacityEnum}, " +
               $"controller calibration {(cal ?? $"unrecognised 0x{d.StripCalibrationWord:X8}")}";
    }

    /// <summary>Show one pure colour on the whole strip for a few seconds (channel-order check).</summary>
    public void ShowTestColor(Rgb color, TimeSpan duration) { _testPattern = null; _testColor = color; _testUntil = DateTime.UtcNow + duration; }
    /// <summary>1.15 layout check: one colour per LED for a while (the case parts in distinct colours), then the effect again.</summary>
    public void ShowTestPattern(Rgb[] pattern, TimeSpan duration) { _testPattern = pattern; _testColor = Rgb.Black; _testUntil = DateTime.UtcNow + duration; }
    Rgb[]? _testPattern;
    public Rgb? ActiveTestColor => _testColor is Rgb c && DateTime.UtcNow < _testUntil ? c : null;
    public double TestRemainingSeconds => _testColor != null ? Math.Max(0, (_testUntil - DateTime.UtcNow).TotalSeconds) : 0;

    public override void Post(Rgb[] frame)
    {
        if (ActiveTestColor is Rgb t)
            frame = _testPattern is Rgb[] p ? Enumerable.Range(0, frame.Length).Select(i => i < p.Length ? p[i] : Rgb.Black).ToArray() : Enumerable.Repeat(t, frame.Length).ToArray();
        base.Post(frame);
    }

    public override void Activate()
    {
        base.Activate();
        _board.Device?.PrepareStrip(LedCount);
        if (_board.Device != null) Status = DescribeStatus();
    }

    protected override bool Apply(Rgb[] frame) => _board.Device?.SetStripColors(frame, _cfg.StripOrder) ?? false;
    protected override ColorBalance? OutputBalance => _cfg.Balance?.Strip;
    protected override bool Recover() => _board.ReopenDevice();

    public override void Deactivate(Rgb steady)
    {
        // leave the strip showing a steady colour (HwSuite never sends the controller's save command)
        steady = _cfg.Balance?.Strip?.Apply(steady) ?? steady;
        try { _board.Device?.SetStripColors(Enumerable.Repeat(steady, LedCount).ToArray(), _cfg.StripOrder); } catch { }
    }
}

/// <summary>
/// Glorious Model O Wireless. Not frame-driven: the mouse runs its own onboard effect, and MouseLightingController
/// sends a setting only when it changes (see that class for the rules). The engine's frames only feed the preview.
/// </summary>
public sealed class MouseSink : RgbSink
{
    readonly RgbConfig _cfg;
    readonly MouseLightingController _ctl;
    public override string Name => "Glorious Model O Wireless";
    public override string Kind => "USB HID (onboard effects)";
    /// <summary>
    /// The mouse is rendered as a tiny strip of this many virtual LEDs around its place (1.17.1): the atmosphere scenes
    /// size their rain, drips and fireflies by LED count, so a single LED almost never caught any. Live mode shows the
    /// brightest of them; the onboard modes ignore the frame.
    /// </summary>
    public const int VirtualLeds = 10;
    public override int LedCount => VirtualLeds;
    public static readonly double[] BasePositions = Enumerable.Range(0, VirtualLeds).Select(i => 0.952 + i * 0.004).ToArray();

    /// <summary>The live colour for a frame of virtual LEDs: the brightest one, so a drop or glint anywhere on the mouse shows.</summary>
    public static Rgb LiveColor(Rgb[] frame)
    {
        if (frame.Length == 0) return Rgb.Black;
        var best = frame[0]; int bestL = -1;
        foreach (var c in frame) { int l = 2 * c.R + 5 * c.G + c.B; if (l > bestL) { bestL = l; best = c; } }
        return best;
    }

    Rgb? _testColor;
    DateTime _testUntil;
    /// <summary>Colour match (live mode only): one colour on the mouse for a while, then the effect again.</summary>
    public void ShowTestColor(Rgb color, TimeSpan duration) { _testColor = color; _testUntil = DateTime.UtcNow + duration; }
    public override double[] Positions => BasePositions;
    public override HwResource? Resource => HwResource.Mouse;
    protected override double MaxHz => 1;
    public MouseLightingController Controller => _ctl;

    public MouseSink(RgbConfig cfg, IMouseTransport transport, MouseLightingState? lastApplied, Action<MouseLightingState?>? persist, bool listenForDeviceChanges = true)
    {
        _cfg = cfg;
        _ctl = new MouseLightingController(transport, listenForDeviceChanges, lastApplied, persist) { EngineTime = EngineNow };
        Available = true;
        Status = "looking for the mouse";
    }

    public override void StartWorker() { }   // the controller has its own thread

    readonly object _clockGate = new();
    double _frameTime = double.NaN;
    long _frameStamp;

    /// <summary>The engine's clock now, extrapolated from the last frame; null when no frame came in the last 2 s.</summary>
    double? EngineNow()
    {
        lock (_clockGate)
        {
            if (double.IsNaN(_frameTime)) return null;
            double since = (Stopwatch.GetTimestamp() - _frameStamp) / (double)Stopwatch.Frequency;
            return since > 2 ? null : _frameTime + since;
        }
    }

    public override void Post(Rgb[] frame)
    {
        lock (_clockGate) { _frameTime = FrameTime; _frameStamp = Stopwatch.GetTimestamp(); }
        _ctl.LatencySeconds = Math.Clamp(_cfg.MouseSyncLatencyMs, -1000, 3000) / 1000.0;
        var want = MouseLightingState.From(_cfg);
        if (_cfg.MouseLive && Enabled)
        {
            // live: the engine's frame colour for the mouse, streamed (the onboard setting is kept as the wish for afterwards)
            var c = _testColor is Rgb tc && DateTime.UtcNow < _testUntil ? tc : LiveColor(frame);
            Preview = new[] { c };
            c = _cfg.Balance?.Mouse?.Apply(c) ?? c;
            _ctl.SetBlocked(Blocked);
            _ctl.SetDesired(want);
            _ctl.SetLive(c);
            Status = $"{_ctl.LinkStatus}; {_ctl.Status}";
            return;
        }
        _ctl.SetLive(null);
        Preview = new[] { want?.Color ?? Rgb.Black };
        _ctl.SetBlocked(Blocked);
        _ctl.SetDesired(Enabled ? want : null);
        Status = $"{_ctl.LinkStatus}; {_ctl.Status}";
    }

    public override void Deactivate(Rgb steady)
    {
        if (_ctl.Live)
        {
            // live: the mouse shows the last frame, so it gets the same steady colour as the streamed devices
            _ctl.SetLive(null);
            _ctl.SetDesired(new MouseLightingState(MouseLightingState.EffectStatic, steady, MouseLightingState.NeutralSpeed, 255, "steady colour after live", false));
            return;
        }
        _ctl.SetDesired(null);   // the mouse keeps its own last setting
    }

    /// <summary>
    /// App exit: the owner chose a solid look after quitting (2026-10-15). An animated onboard setting (breathing) is
    /// replaced by the static primary colour, one setting write; a static colour or "off" is left as it is.
    /// </summary>
    public string DeactivateForExit()
    {
        var current = MouseLightingState.From(_cfg);
        _ctl.SetBlocked(Blocked);
        if (Blocked != null) { _ctl.SetLive(null); _ctl.SetDesired(null); return "not written: " + Blocked; }
        if (_ctl.Live)
        {
            _ctl.SetLive(null);
            var solid = new MouseLightingState(MouseLightingState.EffectStatic, RgbEngine.SteadyLook(_cfg), MouseLightingState.NeutralSpeed, 255, "steady colour after live", false);
            bool done = _ctl.ApplyNowAndWait(solid, TimeSpan.FromSeconds(2));
            _ctl.SetDesired(null);
            Log.Info("mouse lighting at exit (live): " + (done ? "steady colour" : "steady colour not confirmed within 2 s"));
            return done ? "steady colour" : "not confirmed";
        }
        if (current == null || current.Effect != MouseLightingState.EffectBreathingSingleColor) { _ctl.SetDesired(null); return "no change (not animated)"; }
        var look = MouseLightingState.From(_cfg.SteadyConfig())!;   // classic effects: the primary colour; scenes: their calm companion colour
        bool ok = _ctl.ApplyNowAndWait(look, TimeSpan.FromSeconds(2));
        _ctl.SetDesired(null);
        Log.Info("mouse lighting at exit: " + (ok ? "static primary colour (solid after quit)" : "static colour not confirmed within 2 s"));
        return ok ? "static primary colour" : "not confirmed";
    }
    public override void Suspend() { base.Suspend(); _ctl.SetSuspended(true); }
    public override void Resume() { base.Resume(); _ctl.SetSuspended(false); }

    protected override bool Apply(Rgb[] frame) => true;

    public override string StatsText =>
        $"applied {_ctl.Applies}  setting changes {_ctl.DesiredChanges}  failures {_ctl.Failures}  retries {_ctl.Retries}  reconnects {_ctl.Reconnects}  last {_ctl.LastApplyMs:0} ms";

    public override void Dispose() { _ctl.Dispose(); base.Dispose(); }
}

public sealed class RamSink : RgbSink
{
    readonly RgbConfig _cfg;
    readonly IEneModules _ene;
    double[] _positions = Array.Empty<double>();
    int _count;
    public override string Name => $"XPG Spectrix D60G ({_ene.Modules.Count(d => d.Supported)} ENE controllers, SMBus)";
    public override string Kind => "SMBus i801";
    public override int LedCount => _count;
    public override double[] Positions => _positions;
    public override HwResource? Resource => HwResource.Smbus;
    protected override double MaxHz => _cfg.EffectiveRamHz;
    public IEneModules Controller => _ene;
    /// <summary>Onboard settings sent (each = one register sequence per module), separate from streamed frame writes.</summary>
    public long SettingsApplied { get; private set; }
    public long SettingsUnconfirmed { get; private set; }

    /// <summary>LEDs per supported controller, in detection order (each is its own LED run for the atmosphere effects).</summary>
    public int[] SegmentLengths { get; private set; } = Array.Empty<int>();
    public override IReadOnlyList<int>? LedRuns => SegmentLengths;

    protected override ColorBalance? OutputBalance => _cfg.Balance?.Ram;

    /// <summary>1.15.2 colour match: show one colour on every LED for a while (streamed mode only), then the effect again.</summary>
    public void ShowTestColor(Rgb color, TimeSpan duration) { _testColor = color; _testUntil = DateTime.UtcNow + duration; }
    Rgb? _testColor;
    DateTime _testUntil;
    public override void Post(Rgb[] frame)
    {
        if (_testColor is Rgb t && DateTime.UtcNow < _testUntil) frame = Enumerable.Repeat(t, frame.Length).ToArray();
        base.Post(frame);
    }

    public RamSink(RgbConfig cfg, IEneModules ene)
    {
        _cfg = cfg; _ene = ene;
        var supported = ene.Modules.Where(d => d.Supported).ToList();
        SegmentLengths = supported.Select(d => d.LedCount).ToArray();
        _positions = PositionsFor(SegmentLengths); _count = _positions.Length;
        Available = _count > 0;
        Status = Available ? string.Join("; ", supported.Select(d => d.ToString())) : "no supported ENE DRAM controllers";
    }

    /// <summary>Controllers in detection order along the shared line (not a verified slot order).</summary>
    public static double[] PositionsFor(IReadOnlyList<int> ledCounts)
    {
        var pos = new List<double>();
        for (int s = 0; s < ledCounts.Count; s++)
        {
            double start = 0.38 + 0.47 * s / Math.Max(1, ledCounts.Count);
            double width = 0.42 / Math.Max(1, ledCounts.Count);
            for (int i = 0; i < ledCounts[s]; i++)
                pos.Add(start + width * (ledCounts[s] == 1 ? 0 : (double)i / (ledCounts[s] - 1)));
        }
        return pos.ToArray();
    }

    public override void Activate()
    {
        base.Activate();
        _onboard = null; _onboardConfirmed.Clear(); _onboardAttempts = 0; _onboardMode = false;
        foreach (var d in _ene.Modules.Where(d => d.Supported)) _ene.EnterDirect(d);
    }

    public const int OnboardMaxAttempts = 3;
    /// <summary>Onboard setting wanted on the modules (null: streaming in direct mode).</summary>
    (byte mode, Rgb color, byte speed)? _onboard;
    /// <summary>Modules whose read-back confirmed the current onboard setting. Only those are skipped.</summary>
    readonly HashSet<byte> _onboardConfirmed = new();
    int _onboardAttempts;
    bool _onboardMode;
    public string? OnboardDetail { get; private set; }
    /// <summary>The onboard setting could not be confirmed on every module after the bounded retries (null when fine).</summary>
    public string? OnboardProblem { get; private set; }

    /// <summary>
    /// The onboard effect for the current settings, or null when the RAM is streamed. Static and Thermal Reactive always
    /// stream (still frames cost nothing; thermal colours change slowly). Breathing runs the ENE breathing mode with the
    /// primary colour; Gradient Wave runs the ENE spectrum-cycle wave (the chip has no two-colour wave, so the colours
    /// are its own rainbow); Stealth switches the modules off.
    /// </summary>
    public static (byte mode, Rgb color, byte speed)? OnboardFor(RgbConfig c)
    {
        if (!c.RamOnboardEffects) return null;
        if (EffectInfo.IsScene(c.Effect))
        {
            // the scenes need streamed frames; Onboard mode is the owner's choice and is kept: the modules show the
            // scene's companion instead (breathing on their own controller, or a still colour that streams nothing after
            // the first frame, see StillCompanion)
            var cc = Companion.For(c);
            if (!cc.Breathing) return null;
            var col = Rgb.FromInt(cc.Color);
            double kk = Math.Clamp(c.Brightness, 0, 1);
            return (EneDramController.ModeBreathing, new Rgb((byte)(col.R * kk), (byte)(col.G * kk), (byte)(col.B * kk)), EneDramController.SpeedFor(cc.Speed));
        }
        var a = Rgb.FromInt(c.ColorA);
        double k = Math.Clamp(c.Brightness, 0, 1);
        var scaled = new Rgb((byte)(a.R * k), (byte)(a.G * k), (byte)(a.B * k));
        byte speed = EneDramController.SpeedFor(c.Speed);
        return c.Effect switch
        {
            EffectKind.Breathing => (EneDramController.ModeBreathing, scaled, speed),
            EffectKind.GradientWave => (EneDramController.ModeSpectrumCycleWave, scaled, speed),
            EffectKind.Stealth => (EneDramController.ModeOff, Rgb.Black, (byte)2),
            _ => null
        };
    }

    /// <summary>
    /// Onboard mode with a scene whose companion is a still colour: the engine sends that colour (already scaled by
    /// brightness) instead of the scene, through the normal direct path; unchanged frames are not written again.
    /// </summary>
    public static Rgb? StillCompanion(RgbConfig c)
    {
        if (!c.RamOnboardEffects || !EffectInfo.IsScene(c.Effect)) return null;
        var cc = Companion.For(c);
        return cc.Breathing ? null : Rgb.FromInt(cc.Color).Scale(c.Brightness);
    }

    protected override bool Apply(Rgb[] f)
    {
        var onboard = OnboardFor(_cfg);
        if (onboard is { } o)
        {
            (byte mode, Rgb color, byte speed) want = (o.mode, _cfg.Balance?.Ram?.Apply(o.color) ?? o.color, o.speed);
            _onboardMode = true;
            if (_onboard != want) { _onboard = want; _onboardConfirmed.Clear(); _onboardAttempts = 0; OnboardProblem = null; }
            var pending = _ene.Modules.Where(x => x.Supported && !_onboardConfirmed.Contains(x.Address)).ToList();
            if (pending.Count == 0 || _onboardAttempts >= OnboardMaxAttempts)
            {
                AppliedWithoutWrite = true;   // the modules animate on their own (or retries are exhausted): nothing sent
                if (pending.Count > 0) AcknowledgeAsFailed();   // retries exhausted: not every module confirmed it
                return true;
            }
            _onboardAttempts++;
            var notes = new List<string>();
            int failed = 0;
            foreach (var d in pending)
            {
                var outcome = _ene.SetHardwareEffect(d, want.mode, want.color, want.speed, out var detail);
                notes.Add($"0x{d.Address:X2} {detail}");
                if (outcome == EneOutcome.Confirmed) _onboardConfirmed.Add(d.Address);
                else { failed++; if (outcome == EneOutcome.Unconfirmed) SettingsUnconfirmed++; }
            }
            SettingsApplied++;
            OnboardDetail = string.Join("; ", notes);
            Log.Info($"RAM onboard effect (attempt {_onboardAttempts}/{OnboardMaxAttempts}): " + OnboardDetail);
            if (failed > 0 && _onboardAttempts >= OnboardMaxAttempts)
            {
                OnboardProblem = $"{failed} of {_ene.Modules.Count(x => x.Supported)} RAM modules did not confirm the onboard effect";
                Log.Warn("RAM onboard effect: " + OnboardProblem + "; not retried until the setting changes");
                AcknowledgeAsFailed();
            }
            else if (failed > 0) WithholdAcknowledgement();   // not confirmed on every module yet: retried on the next cycles
            return true;   // the write itself went out; unconfirmed modules are retried (bounded) on the next cycles
        }
        if (_onboardMode)
        {
            // back to streaming: re-enter direct mode before the next frame
            _onboardMode = false; _onboard = null; _onboardConfirmed.Clear(); _onboardAttempts = 0; OnboardProblem = null;
            foreach (var d in _ene.Modules.Where(x => x.Supported)) _ene.EnterDirect(d);
        }
        int offset = 0;
        bool ok = true;
        foreach (var d in _ene.Modules.Where(x => x.Supported))
        {
            if (offset + d.LedCount > f.Length) break;
            if (_ene.WriteFrame(d, f.AsSpan(offset, d.LedCount)) < 0) ok = false;
            offset += d.LedCount;
        }
        return ok;
    }

    public override void Deactivate(Rgb steady)
    {
        steady = _cfg.Balance?.Ram?.Apply(steady) ?? steady;
        foreach (var d in _ene.Modules.Where(d => d.Supported)) { try { _ene.SetStaticLook(d, steady); } catch { } }
    }
}

/// <summary>Placeholder shown when the SMBus backend cannot be opened. Never touches hardware.</summary>
public sealed class UnavailableSink : RgbSink
{
    readonly string _name, _kind;
    public override string Name => _name;
    public override string Kind => _kind;
    public override int LedCount => 0;
    public override double[] Positions => Array.Empty<double>();
    protected override double MaxHz => 1;
    public UnavailableSink(string name, string kind, string reason) { _name = name; _kind = kind; Status = reason; Available = false; }
    protected override bool Apply(Rgb[] frame) => true;
}

/// <summary>
/// iLovbee i87. Two paths, both behind the owner's verification:
/// - stored setting (the vendor's "LED(Save)" command, physically confirmed): sent once per settled change by
///   KeyboardLightingController; used for Stealth / Lights out (brightness 0) and whenever live frames are not verified;
/// - live frames (the vendor's music/real-time data, 06 00 + 126 x RGB, see ILovbeeKeyboardDevice.BuildLivePayload):
///   frame-driven from the shared engine clock at up to <see cref="LiveHz"/> per second, only after the owner has watched
///   them show on the keyboard (<see cref="AppConfig.KeyboardLiveVerified"/>). Each key gets its own position, so Gradient
///   Wave travels across the keys. Nothing is stored by a live frame.
/// The vendor profile's saved speed is used for stored animated modes and its sleep timer is always sent back unchanged.
/// </summary>
public sealed class KeyboardSink : RgbSink
{
    /// <summary>
    /// Live frame rate. One frame is 7 feature reports with the vendor's 8 ms gap after each (about 60 ms); the vendor's
    /// own loop runs at about 16 frames/s. Measured per run in the live test; not raised by removing the vendor's gaps.
    /// </summary>
    public const double LiveHz = 15;
    /// <summary>
    /// How far ahead the keyboard samples the shared clock. Measured, not derived: with 60 ms the keyboard's breathing led
    /// the case strip by ~29 ms on the owner's recording (docs\phase9ideo-sync2.md: r 0.989, same 2.08 s period, no drift),
    /// so the effective transport delay relative to the strip is about 30 ms.
    /// </summary>
    public const double LiveLeadSecondsEstimate = 0.03;
    /// <summary>Keyboard span on the shared Gradient Wave line (phase units: 1.0 = one full wavelength), left to right.</summary>
    public const double WaveStart = 0.86, WaveSpan = 0.30;
    /// <summary>
    /// An unchanged live frame is written again after this long. The keyboard returns to its stored setting a few seconds
    /// after the last live frame (owner's observation, 2026-10-14; measured in docs\phase10), so a still colour must be
    /// refreshed; a broken handle is also noticed within about a second even when nothing changes.
    /// </summary>
    public static readonly TimeSpan LiveRefresh = TimeSpan.FromMilliseconds(750);
    public static readonly double[] LivePositions = ILovbeeKeyMap.UnitX.Select(x => WaveStart + WaveSpan * x).ToArray();
    static readonly double[] StoredPosition = { 0.99 };

    readonly RgbConfig _cfg;
    readonly KeyboardLightingController _ctl;
    readonly IKeyboardTransport _transport;
    byte _savedSpeed = KeyboardLightingState.VendorDefaultSpeed;
    DateTime _profileRead = DateTime.MinValue;
    volatile bool _live;
    bool _sentLive, _wasLive;
    public override string Name => "iLovbee i87 keyboard";
    public override string Kind => _live ? "USB HID (live frames)" : "USB HID (onboard effects)";
    public override int LedCount => _ctl.LiveVerified ? ILovbeeKeyMap.Keys.Length : 1;
    public override double[] Positions => _ctl.LiveVerified ? LivePositions : StoredPosition;
    public override HwResource? Resource => HwResource.Keyboard;
    protected override double MaxHz => _live ? LiveHz : 1;
    protected override TimeSpan? RefreshUnchangedAfter => _live ? LiveRefresh : null;
    public override double StillRepostSeconds => _live ? 0.25 : 1.0;
    public override double LeadSeconds => _live ? LiveLeadSecondsEstimate : 0;
    public KeyboardLightingController Controller => _ctl;
    public KeyboardLightingState? Wanted { get; private set; }
    /// <summary>True while the keyboard follows the shared clock frame by frame.</summary>
    public bool Live => _live;
    /// <summary>Speed stored in the vendor profile (kept for every mode; refreshed every 30 s).</summary>
    public byte SavedSpeed { get; private set; } = KeyboardLightingState.VendorDefaultSpeed;

    public KeyboardSink(RgbConfig cfg, IKeyboardTransport transport, Func<bool> verified, Func<string?> liveOwnership, KeyboardLightingState? lastApplied,
        Action<KeyboardLightingState?>? persist, bool listenForDeviceChanges = true, Func<bool>? liveVerified = null)
    {
        _cfg = cfg; _transport = transport;
        _ctl = new KeyboardLightingController(transport, verified, liveOwnership, lastApplied, persist, listenForDeviceChanges, liveVerified);
        // a stored write, reopen, reconnect or vendor release changes what the keyboard shows: the next live frame must go out
        _ctl.DisplayStateLost += _ => ForgetDisplayed();
        Available = true;
        Status = "looking for the keyboard";
    }

    /// <summary>Whether an effect runs as live frames (everything except Stealth, which uses the confirmed stored brightness 0).</summary>
    public static bool StreamsLive(RgbConfig c) => c.Enabled && c.Keyboard && c.Effect != EffectKind.Stealth;

    public override void Post(Rgb[] frame)
    {
        if (DateTime.UtcNow - _profileRead > TimeSpan.FromSeconds(30))
        {
            _profileRead = DateTime.UtcNow;
            if (_transport.Profile(out _) is ILovbeeProfile p) _savedSpeed = p.Speed;
        }
        bool live = Enabled && _ctl.LiveVerified && StreamsLive(_cfg);
        if (live != _wasLive)
        {
            // live frames <-> stored setting: drop anything queued for the old mode, and resend even an identical frame
            _wasLive = live;
            Invalidate(live ? "keyboard: back to live frames" : "keyboard: the stored setting takes over");
        }
        _live = live;
        _ctl.SetLiveWanted(live);
        // the stored setting is the effect's onboard look, also while live frames run: it is what the keyboard falls back to
        var want = KeyboardLightingState.From(_cfg, _savedSpeed);
        Wanted = want;
        SavedSpeed = _savedSpeed;
        _ctl.SetBlocked(Blocked);
        _ctl.SetDesired(Enabled ? want : null);
        Status = $"{_ctl.LinkStatus}; {_ctl.Status}";
        if (live) base.Post(frame);
        else Preview = new[] { want?.Color ?? Rgb.Black };
    }

    protected override bool Apply(Rgb[] frame)
    {
        if (!_live || frame.Length != ILovbeeKeyMap.Keys.Length) { AppliedWithoutWrite = true; ForgetLastFrame(); return true; }
        var r = _ctl.SendLive(ILovbeeKeyboardDevice.BuildLivePayload(frame), out var detail);
        if (r == KeyboardLightingController.LiveResult.Sent) { _sentLive = true; return true; }
        if (r == KeyboardLightingController.LiveResult.Skipped) { AppliedWithoutWrite = true; ForgetLastFrame(); return true; }
        Log.Warn("keyboard live frame failed: " + detail);
        return false;
    }

    protected override bool Recover() { _ctl.NotifyDeviceChange(); return false; }

    public override void Deactivate(Rgb steady)
    {
        // no closing live frame: the keyboard returns by itself to its stored setting, kept as the effect's onboard look;
        // a change still inside its settle time is written now, so that fallback is the current effect
        if (_sentLive || Wanted != null) { var r = _ctl.FlushPending(); if (r != "nothing pending") Log.Info("keyboard at output stop: " + r); }
        _sentLive = false;
        _live = false; _wasLive = false;
        _ctl.SetLiveWanted(false);
        _ctl.SetDesired(null);
    }
    public override void Suspend() { base.Suspend(); _ctl.SetSuspended(true); }
    public override void Resume() { base.Resume(); _ctl.SetSuspended(false); }

    /// <summary>
    /// App exit: the owner chose a solid look after quitting (2026-10-15). Instead of falling back to the effect's onboard
    /// animation, the keyboard stores the steady primary colour (one stored write; none if it already shows it). Lights out
    /// keeps its stored brightness 0. Replaces the output-stop flush, so quitting never writes twice.
    /// </summary>
    public string DeactivateForExit()
    {
        string r;
        if (_cfg.Effect == EffectKind.Stealth || !Enabled) { Deactivate(Rgb.Black); return "no change"; }
        var look = KeyboardLightingState.From(_cfg.SteadyConfig(), _savedSpeed);   // classic: primary colour; scenes: companion colour
        _sentLive = false; _live = false; _wasLive = false;
        _ctl.SetLiveWanted(false);
        _ctl.SetDesired(null);
        r = look == null ? "no change (not included)" : _ctl.StoreExitLook(look);
        Log.Info("keyboard at exit: " + r);
        return r;
    }

    public override string StatsText => _live || _ctl.LiveFrames > 0
        ? $"live frames {_ctl.LiveFrames}  live failures {_ctl.LiveFailures}  stored applies {_ctl.Applies}  test writes {_ctl.TestWrites}  failures {_ctl.Failures}"
        : $"applied {_ctl.Applies}  test writes {_ctl.TestWrites}  failures {_ctl.Failures}";

    public override void Dispose() { _ctl.Dispose(); base.Dispose(); }
}
