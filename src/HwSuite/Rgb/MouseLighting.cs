using System.Diagnostics;
using HidSharp;
using HwSuite.Core;
using HwSuite.Lighting.Devices;

namespace HwSuite.Lighting;

/// <summary>One open path to the mouse (cable or receiver).</summary>
public interface IMouseLink : IDisposable
{
    string Describe { get; }
    bool Apply(MouseLightingState state, out string error);
    /// <summary>Live mode: one frame colour, sent the way SignalRGB's released plugin does (see GloriousTransport).</summary>
    bool SendLive(Rgb color, out string error);
}

/// <summary>Finds and opens the preferred path: the cable when connected, otherwise the receiver (Glorious CORE does the same).</summary>
public interface IMouseTransport
{
    IMouseLink? OpenPreferred(out string status);
    /// <summary>Stable identity of the path currently preferred, or null if no mouse path is present.</summary>
    string? PreferredPath();
    /// <summary>All of the mouse's lighting-capable interfaces (cable and receiver), as one comparable string.</summary>
    string Signature();
}

public sealed class GloriousTransport : IMouseTransport
{
    public string? FirmwareText { get; private set; }
    /// <summary>The device behind the most recently opened link (for measurements).</summary>
    public GloriousWirelessDevice? LastDevice { get; private set; }

    sealed class Link : IMouseLink
    {
        public readonly GloriousWirelessDevice Dev;
        public Link(GloriousWirelessDevice d) { Dev = d; }
        public string Describe => (Dev.IsCable ? "cable" : "receiver") + (Dev.FirmwareText.Length > 0 ? " fw " + Dev.FirmwareText : "");
        public bool Apply(MouseLightingState s, out string error) => Dev.Apply(s, out error);
        /// <summary>
        /// Static-colour effect report on its own over the receiver; over the cable followed by both brightness reports
        /// (SignalRGB's Glorious_Model_O_Wireless plugins, receiver and wired, send exactly this every frame). No vendor gaps.
        /// </summary>
        public bool SendLive(Rgb c, out string error)
        {
            var report = GloriousWirelessDevice.BuildEffectReport(MouseLightingState.EffectStatic, MouseLightingState.NeutralSpeed, new[] { c });
            if (!Dev.SendReport(report, out error)) return false;
            if (!Dev.IsCable) return true;
            return Dev.SendReport(GloriousWirelessDevice.BuildBrightnessReport(255, true), out error)
                && Dev.SendReport(GloriousWirelessDevice.BuildBrightnessReport(255, false), out error);
        }
        public void Dispose() => Dev.Dispose();
    }

    internal static HidInfo? Preferred(List<HidInfo> all) =>
        GloriousWirelessDevice.Pick(all, GloriousWirelessDevice.PidCable) ?? GloriousWirelessDevice.Pick(all, GloriousWirelessDevice.PidDongle);

    public string? PreferredPath() => Preferred(GloriousWirelessDevice.Enumerate())?.Path;

    public string Signature()
    {
        var all = GloriousWirelessDevice.Enumerate();
        var picks = new[] { GloriousWirelessDevice.Pick(all, GloriousWirelessDevice.PidCable), GloriousWirelessDevice.Pick(all, GloriousWirelessDevice.PidDongle) };
        return string.Join("|", picks.Where(p => p != null).Select(p => p!.Path.ToLowerInvariant()).OrderBy(x => x));
    }

    public IMouseLink? OpenPreferred(out string status)
    {
        var pick = Preferred(GloriousWirelessDevice.Enumerate());
        if (pick == null) { status = "mouse not found (VID 258A PID 2011 cable / 2022 receiver)"; return null; }
        var d = new GloriousWirelessDevice();
        if (!d.Open(pick, out var err)) { status = err; return null; }
        FirmwareText = d.ReadFirmware();   // one read-only query per connection
        LastDevice = d;
        var link = new Link(d);
        status = link.Describe;
        return link;
    }
}

/// <summary>
/// Sends the mouse its lighting only when the effective setting changes, on its own thread.
///  - no frames: the owner's effect is mapped once to an onboard effect (MouseLightingState.From);
///  - dedupe: a setting equal to the last successful one is never re-sent; debounce: rapid changes (slider drags)
///    collapse into one write after the setting has been still for Debounce;
///  - failures: up to MaxAttempts tries with growing back-off, then it waits for the next change or a reconnection;
///  - reconnection: when the preferred path changes (cable plugged/unplugged, receiver re-enumerated) the current
///    setting is applied once on the new path;
///  - isolation: every HID call happens on this controller's thread, so a slow or missing mouse cannot block the UI,
///    fan control, telemetry or the other lighting devices.
/// </summary>
public sealed class MouseLightingController : IDisposable
{
    /// <summary>Wait before retry 1 and 2 (attempt 3 is the last).</summary>
    public TimeSpan[] Backoff { get; set; } = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };
    public const int MaxAttempts = 3;

    readonly IMouseTransport _transport;
    readonly AutoResetEvent _signal = new(false);
    readonly Thread _thread;
    readonly object _gate = new();
    volatile bool _stop;
    volatile bool _deviceChanged = true;   // open on the first pass
    readonly Action<MouseLightingState?>? _persist;
    IMouseLink? _link;
    string? _linkPath;
    string? _signature;
    bool _everConnected;
    MouseLightingState? _desired, _lastApplied, _gaveUpOn;
    DateTime _changedAt = DateTime.MinValue, _nextTry = DateTime.MinValue, _lastEnumerate = DateTime.MinValue;
    string? _blocked;
    int _attempt;
    bool _reapply;

    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Synced breathing (MouseSync): the lighting engine's clock in seconds, the one VANTA's breathing is computed from; null
    /// while unknown (engine not rendering). A synced setting is only sent once this is known.
    /// </summary>
    public Func<double?>? EngineTime { get; set; }
    /// <summary>Synced breathing: when the mouse's light changes relative to the other devices for the same moment (MouseSync).</summary>
    public double LatencySeconds { get; set; } = MouseSync.MeasuredLatencyMs / 1000.0;
    /// <summary>
    /// Synced breathing: the same setting is sent again this often to cancel the drift between the mouse's timer and the
    /// PC's (measured about 0.04 %, so at most about 0.12 s in 5 minutes). One write each time.
    /// </summary>
    public TimeSpan ResyncInterval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Synced writes that were timed to the shared clock (including re-syncs).</summary>
    public long SyncedApplies { get; private set; }
    /// <summary>Engine time at the start of the last synced write, and how far it was from the planned moment (ms).</summary>
    public double LastSyncEngineTime { get; private set; } = double.NaN;
    public double LastSyncErrorMs { get; private set; }
    DateTime _lastSynced = DateTime.MinValue;
    bool _alignedThisRun;

    /// <summary>Live mode (test 8, 2026-10-23: 10-60/s over the receiver, no failures, no cursor stutter): frames per second at most.</summary>
    public int LiveHz { get; set; } = 30;
    /// <summary>Live mode: an unchanged colour is sent again this often, so a dropped radio packet does not stick.</summary>
    public TimeSpan LiveKeepAlive { get; set; } = TimeSpan.FromSeconds(1);
    public long LiveFrames { get; private set; }
    public long LiveFailures { get; private set; }
    public bool Live { get { lock (_gate) return _live != null; } }
    Rgb? _live, _liveSent;
    DateTime _liveSentAt = DateTime.MinValue;
    bool _liveStarted;

    /// <summary>
    /// Live mode: the colour the mouse should show now (the engine's frame for it), or null to leave live mode. Leaving it
    /// makes the wanted onboard setting be written again, because the mouse is showing the last live colour.
    /// </summary>
    public void SetLive(Rgb? color)
    {
        lock (_gate)
        {
            if (color == _live) return;
            if (color == null) { _liveStarted = false; _lastApplied = null; _changedAt = UtcNow() - Debounce; }
            _live = color;
        }
        _signal.Set();
    }
    public TimeSpan EnumerateInterval { get; set; } = TimeSpan.FromSeconds(2);
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public long Applies { get; private set; }
    public long Failures { get; private set; }
    public long Retries { get; private set; }
    public long Reconnects { get; private set; }
    /// <summary>Device-list notifications that did not change the mouse's interfaces (and so caused no I/O).</summary>
    public long IgnoredDeviceChanges { get; private set; }
    public long DesiredChanges { get; private set; }
    public double LastApplyMs { get; private set; }
    public double MaxApplyMs { get; private set; }
    public string? LastError { get; private set; }
    public string Status { get; private set; } = "starting";
    public string LinkStatus { get; private set; } = "not connected";
    public bool Connected => _link != null;
    public MouseLightingState? LastApplied { get { lock (_gate) return _lastApplied; } }

    /// <summary>
    /// Whether the mouse accepted the setting wanted now (its stored onboard setting; the applied record survives restarts
    /// because the mouse keeps it): Accepted when the last applied setting equals it, Failed after a failed write for it
    /// (retrying or given up), Pending otherwise. API acceptance, not an optical confirmation.
    /// </summary>
    public HwSuite.Lighting.LightAck Ack
    {
        get
        {
            lock (_gate)
            {
                if (_live != null) return _liveStarted && _liveSent == _live ? HwSuite.Lighting.LightAck.Accepted : LiveFailures > 0 && !_liveStarted ? HwSuite.Lighting.LightAck.Failed : HwSuite.Lighting.LightAck.Pending;
                if (_desired == null) return HwSuite.Lighting.LightAck.Unknown;
                if (Equals(_desired, _lastApplied) && !_reapply) return HwSuite.Lighting.LightAck.Accepted;
                if (Equals(_desired, _gaveUpOn) || _attempt > 0) return HwSuite.Lighting.LightAck.Failed;
                return HwSuite.Lighting.LightAck.Pending;
            }
        }
    }

    /// <param name="lastApplied">Setting the mouse was last given (persisted across restarts), so an unchanged setting is not re-sent at startup.</param>
    /// <param name="persist">Called after each successful application.</param>
    public MouseLightingController(IMouseTransport transport, bool listenForDeviceChanges = true,
        MouseLightingState? lastApplied = null, Action<MouseLightingState?>? persist = null)
    {
        _transport = transport;
        _lastApplied = lastApplied;
        _persist = persist;
        if (listenForDeviceChanges)
        {
            try { DeviceList.Local.Changed += (_, _) => NotifyDeviceChange(); } catch { }
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "MouseLighting" };
        _thread.Start();
    }

    /// <summary>Cheap; called from the lighting engine every frame. Only a different value counts as a change.</summary>
    public void SetDesired(MouseLightingState? state)
    {
        lock (_gate)
        {
            if (Equals(state, _desired)) return;
            _desired = state;
            _changedAt = UtcNow();
            _attempt = 0;
            _nextTry = DateTime.MinValue;
            DesiredChanges++;
        }
        _signal.Set();
    }

    /// <summary>Another program owns the mouse (or HwSuite must not write now): keep the wish, send nothing.</summary>
    public void SetBlocked(string? reason)
    {
        lock (_gate) { if (_blocked == reason) return; _blocked = reason; }
        _signal.Set();
    }

    public void NotifyDeviceChange() { _deviceChanged = true; _signal.Set(); }

    /// <summary>
    /// App exit: apply <paramref name="state"/> now (no settle time) through the normal write path (ownership, link, retry
    /// rules) and wait for it, at most <paramref name="timeout"/>. True when the mouse accepted it or already had it.
    /// </summary>
    public bool ApplyNowAndWait(MouseLightingState state, TimeSpan timeout)
    {
        if (Equals(state, LastApplied)) return true;
        lock (_gate)
        {
            _desired = state; _changedAt = UtcNow() - Debounce; _attempt = 0; _nextTry = DateTime.MinValue; _gaveUpOn = null;
            DesiredChanges++;
        }
        _signal.Set();
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end) { if (Equals(state, LastApplied)) return true; Thread.Sleep(20); }
        return Equals(state, LastApplied);
    }

    volatile bool _suspended;
    /// <summary>System sleep: nothing is written until resume; the wanted setting is kept.</summary>
    public void SetSuspended(bool on) { _suspended = on; _signal.Set(); }

    void Run()
    {
        ThreadCpu.Register("mouse lighting");
        while (!_stop)
        {
            _signal.WaitOne(200);
            if (_stop) break;
            try { Step(); }
            catch (Exception ex) { LastError = ex.Message; Log.Error("mouse lighting", ex); }
        }
        try { _link?.Dispose(); } catch { }
    }

    void Step()
    {
        var now = UtcNow();
        // a mouse that could not be opened is looked for again every 10 s while a setting is waiting (no writes involved)
        if (_link == null && _desired != null && now - _lastEnumerate >= TimeSpan.FromSeconds(10)) _deviceChanged = true;
        if (_deviceChanged && now - _lastEnumerate >= EnumerateInterval)
        {
            _deviceChanged = false;
            _lastEnumerate = now;
            // Only a change in the mouse's own interfaces matters; other USB/HID changes cause no I/O at all.
            var sig = _transport.Signature();
            if (sig == _signature && _link != null) IgnoredDeviceChanges++;
            else
            {
                _signature = sig;
                var path = _transport.PreferredPath();
                if (path == _linkPath && _link != null) IgnoredDeviceChanges++;
                else
                {
                    try { _link?.Dispose(); } catch { }
                    _link = null; _linkPath = null;
                    if (path != null)
                    {
                        _link = _transport.OpenPreferred(out var st);
                        LinkStatus = st;
                        if (_link != null)
                        {
                            _linkPath = path;
                            bool reconnection = _everConnected;
                            _everConnected = true;
                            if (reconnection) Reconnects++;
                            // One controlled re-application on a genuine reconnection; at first connection the normal
                            // comparison with the last applied (persisted) setting decides.
                            lock (_gate) { _reapply = reconnection && (_lastApplied != null || _desired != null); _gaveUpOn = null; _attempt = 0; _nextTry = DateTime.MinValue; _liveStarted = false; }
                            Log.Info($"mouse lighting: {(reconnection ? "reconnected" : "connected")} via {st}");
                        }
                    }
                    else LinkStatus = "not connected";
                }
            }
        }
        else if (_deviceChanged) { _signal.Set(); Thread.Sleep(50); }

        MouseLightingState? want; bool reapply; string? blocked;
        lock (_gate) { want = _desired; reapply = _reapply; blocked = _blocked; }

        if (_suspended) { Status = "not writing: system sleeping"; return; }
        if (blocked != null) { Status = "not writing: " + blocked; return; }
        Rgb? live;
        lock (_gate) live = _live;
        if (live is Rgb lc) { StepLive(lc, now); return; }
        if (want == null) { Status = _lastApplied == null ? "idle (mouse not included or lighting off)" : "idle; mouse keeps its last setting"; return; }
        if (_link == null) { Status = "waiting for the mouse"; return; }
        bool same = Equals(want, LastApplied);
        // a synced setting is lined up once per run (VANTA's clock starts again at every launch), then every ResyncInterval
        if (same && !reapply && want.Synced && !Equals(want, _gaveUpOn) && (!_alignedThisRun || now - _lastSynced >= ResyncInterval)) reapply = true;
        if (same && !reapply) { Status = "up to date: " + want.Description; return; }
        if (Equals(want, _gaveUpOn) && !reapply) { Status = $"gave up after {MaxAttempts} attempts ({LastError}); waiting for a change or reconnection"; return; }

        DateTime changedAt, nextTry;
        lock (_gate) { changedAt = _changedAt; nextTry = _nextTry; }
        if (now - changedAt < Debounce) { Status = "waiting for the setting to settle"; _signal.Set(); Thread.Sleep(Math.Min(50, (int)Debounce.TotalMilliseconds)); return; }
        if (now < nextTry) { Status = $"retry in {(nextTry - now).TotalSeconds:0.0} s"; Thread.Sleep(50); _signal.Set(); return; }

        if (_suspended) return;
        double planned = double.NaN;
        if (want.Synced)
        {
            // wait for the moment that makes the mouse's darkest point land on VANTA's (MouseSync.WaitBeforeSend)
            if (EngineTime?.Invoke() is not double t0) { Status = "synced breathing: waiting for the lighting clock"; Thread.Sleep(50); _signal.Set(); return; }
            double wait = MouseSync.WaitBeforeSend(t0, want.SyncPeriodSeconds, LatencySeconds);
            if (wait > 0.25) { Status = $"synced breathing: lining up with the cycle ({wait:0.0} s)"; Thread.Sleep((int)Math.Min(200, (wait - 0.2) * 1000)); _signal.Set(); return; }
            planned = t0 + wait;
            if (wait > 0) PrecisionSleep(wait);
            lock (_gate) if (!Equals(_desired, want)) return;   // changed while waiting: start over with the new setting
        }
        double sendTime = want.Synced && EngineTime?.Invoke() is double ts ? ts : double.NaN;
        var sw = Stopwatch.StartNew();
        bool ok = _link.Apply(want, out var err);
        double ms = sw.Elapsed.TotalMilliseconds;
        LastApplyMs = ms; MaxApplyMs = Math.Max(MaxApplyMs, ms);
        lock (_gate)
        {
            if (ok)
            {
                _lastApplied = want; _reapply = false; _attempt = 0; _gaveUpOn = null;
                Applies++;
                if (want.Synced)
                {
                    SyncedApplies++; _alignedThisRun = true; _lastSynced = now;
                    LastSyncEngineTime = sendTime; LastSyncErrorMs = (sendTime - planned) * 1000;
                }
                try { _persist?.Invoke(want); } catch { }
                Status = "up to date: " + want.Description;
                Log.Info($"mouse lighting applied ({want.Description}) in {ms:0} ms" + (want.Synced ? $"; synced write #{SyncedApplies} at engine {sendTime:0.000} s ({LastSyncErrorMs:+0.0;-0.0} ms from plan)" : ""));
            }
            else
            {
                Failures++;
                LastError = err;
                _attempt++;
                if (_attempt >= MaxAttempts) { _gaveUpOn = want; _reapply = false; Status = $"gave up after {MaxAttempts} attempts ({err})"; Log.Warn("mouse lighting: " + Status); }
                else { Retries++; _nextTry = UtcNow() + Backoff[Math.Min(_attempt - 1, Backoff.Length - 1)]; Status = $"write failed ({err}), retry {_attempt}/{MaxAttempts - 1}"; }
                // a failed write may mean the path went away: re-check it before retrying
                _deviceChanged = true;
            }
        }
    }

    /// <summary>
    /// What is actually sent for a live colour: never pure black. OpenRGB's driver for this mouse (SinowealthGMOWController)
    /// always replaces (0,0,0) with (1,0,0) in static colours, which suggests the firmware treats an all-zero colour
    /// specially; (1,0,0) looks the same.
    /// </summary>
    public static Rgb LiveWire(Rgb c) => c == Rgb.Black ? new Rgb(1, 0, 0) : c;

    void StepLive(Rgb color, DateTime now)
    {
        if (_link == null) { Status = "live: waiting for the mouse"; return; }
        if (!_liveStarted)
        {
            // full brightness once through the normal write; after that only the colour is streamed
            var first = new MouseLightingState(MouseLightingState.EffectStatic, LiveWire(color), MouseLightingState.NeutralSpeed, 255, "live colour", false);
            if (!_link.Apply(first, out var e1))
            {
                LiveFailures++; LastError = e1; _deviceChanged = true;
                Status = $"live: first write failed ({e1}); trying again";
                Log.Warn("mouse lighting: live start failed: " + e1);
                Thread.Sleep(500); return;
            }
            lock (_gate) { _liveStarted = true; _liveSent = color; _lastApplied = null; }
            _liveSentAt = UtcNow(); LiveFrames++;
            Status = "live: following the shared animation";
            Log.Info($"mouse lighting: live colour started via {LinkStatus} (up to {LiveHz}/s)");
            return;
        }
        double since = (now - _liveSentAt).TotalSeconds, gap = 1.0 / Math.Clamp(LiveHz, 1, 60);
        bool changed;
        lock (_gate) changed = color != _liveSent;
        if (!changed && now - _liveSentAt < LiveKeepAlive) { Status = $"live: following the shared animation ({LiveFrames} frames)"; return; }
        if (since < gap) { Thread.Sleep((int)Math.Ceiling((gap - since) * 1000)); _signal.Set(); return; }
        if (_link.SendLive(LiveWire(color), out var err))
        {
            lock (_gate) _liveSent = color;
            _liveSentAt = UtcNow(); LiveFrames++;
            Status = $"live: following the shared animation ({LiveFrames} frames)";
        }
        else
        {
            LiveFailures++; LastError = err; _deviceChanged = true;
            if (LiveFailures % 20 == 1) Log.Warn($"mouse lighting: live frame failed ({err}); {LiveFailures} so far");
            Status = $"live: frame failed ({err})";
            Thread.Sleep(200);
        }
    }

    /// <summary>Sleep for a short, exact time (the last few ms spin), for timing a synced write.</summary>
    static void PrecisionSleep(double seconds)
    {
        var end = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        long left;
        while ((left = end - Stopwatch.GetTimestamp()) > Stopwatch.Frequency / 40) Thread.Sleep(1);   // Sleep(1) can overshoot by ~16 ms
        while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(50);
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Set();
        try { _thread.Join(1500); } catch { }
    }
}
