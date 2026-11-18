using System.Diagnostics;
using HwSuite.Core;
using HwSuite.Hardware;
using HwSuite.Lighting.Devices;

namespace HwSuite.Lighting;

public interface IKeyboardLink : IDisposable
{
    string Describe { get; }
    bool Apply(KeyboardLightingState s, byte sleep, out string error);
    /// <summary>One volatile live frame (06 00 + 126 x RGB); see <see cref="ILovbeeKeyboardDevice.BuildLivePayload"/>.</summary>
    bool ApplyLive(byte[] payload, out string error);
}

public interface IKeyboardTransport
{
    /// <summary>Interface set of the keyboard (changes only when it is plugged in or out).</summary>
    string Signature();
    IKeyboardLink? Open(out string status);
    /// <summary>Vendor profile values to keep; null means "do not write" (see <see cref="ILovbeeProfile.Read"/>).</summary>
    ILovbeeProfile? Profile(out string detail);
}

public sealed class ILovbeeTransport : IKeyboardTransport
{
    readonly Func<byte?> _sleepChoice;
    public ILovbeeTransport(Func<byte?>? sleepChoice = null) { _sleepChoice = sleepChoice ?? (() => null); }

    sealed class Link : IKeyboardLink
    {
        readonly ILovbeeKeyboardDevice _dev;
        public Link(ILovbeeKeyboardDevice d) { _dev = d; }
        public string Describe => "FF00:0001, 65-byte feature reports";
        public bool Apply(KeyboardLightingState s, byte sleep, out string error) => _dev.Apply(s, sleep, out error);
        public bool ApplyLive(byte[] payload, out string error) => _dev.ApplyLive(payload, out error);
        public void Dispose() => _dev.Dispose();
    }

    public string Signature() => string.Join("|", ILovbeeKeyboardDevice.Enumerate().Select(i => i.Path).OrderBy(p => p));

    public IKeyboardLink? Open(out string status)
    {
        var all = ILovbeeKeyboardDevice.Enumerate();
        if (all.Count == 0) { status = "not connected"; return null; }
        var info = ILovbeeKeyboardDevice.Pick(all);
        if (info == null) { status = $"connected, but no FF00:0001 collection with 65-byte feature reports ({all.Count} interfaces)"; return null; }
        var dev = new ILovbeeKeyboardDevice();
        if (!dev.Open(info, out var err)) { status = err; return null; }
        status = "connected";
        return new Link(dev);
    }

    public ILovbeeProfile? Profile(out string detail) => ILovbeeProfile.Read(null, _sleepChoice(), out detail);
}

/// <summary>
/// Keyboard lighting behind an explicit opt-in. Until the owner confirms one test write (<see cref="AppConfig.KeyboardVerified"/>)
/// the only write is a test the owner starts. Afterwards HwSuite sends the keyboard's onboard setting once per settled
/// change (debounced: the command is the vendor's stored "LED(Save)", so never per frame), and, once live frames are
/// verified too, volatile live frames from the sink worker (<see cref="SendLive"/>).
///
/// Live frames are shown over the stored setting, and the keyboard returns to its stored setting by itself a few seconds
/// after the last live frame (the owner saw this on 2026-10-14). The stored setting is therefore kept as the fallback for the
/// current effect while live frames run (the sink keeps <c>desired</c> set), so stopping, exiting, sleeping or a gap in the
/// frames ends on the effect's own onboard look, never on an old colour or an earlier Lights out. When live frames stop
/// for a stored mode (Lights out, onboard only), the stored setting is written again so it takes over at once.
///
/// Every write goes through one gate, so a stored setting and a live frame never overlap, and each re-checks at that gate:
///  - ownership: <c>liveOwnership</c> (the vendor software's current state, then the arbiter's decision) is asked before
///    every write, each live frame included; nothing is cached here. A transfer already running when the vendor software
///    starts completes (it cannot be recalled);
///  - staleness: a stored setting is written only if it is still the wanted one and live frames are not wanted; a live frame
///    only while live frames are wanted. Whatever was decided last wins, in either direction.
/// While the vendor software owned the keyboard, the keyboard may have been changed, so the cached state is dropped and one
/// re-sync follows when it exits. A genuine reconnection also triggers one re-sync.
///
/// Link recovery distinguishes three cases:
///  - absence: the keyboard is not listed (never connected, or unplugged). Nothing is opened and nothing is counted; it is
///    opened when its interfaces appear (a later appearance counts as a reconnection and triggers one re-sync);
///  - transport failure: a transfer failed, or the keyboard is listed but Open() fails. The handle is closed and reopened on
///    the unchanged device path after <see cref="ReopenBackoff"/>, at most <see cref="MaxReopenAttempts"/> attempts in all,
///    whether an attempt fails inside Open() (no handle at all) or opens a handle whose writes fail again. Only a
///    successful write restores the budget. Never while the system sleeps. Once used up, it waits for a genuine device change;
///  - genuine device change: the interface set differs from the one seen when the failure began (including the keyboard
///    disappearing). That ends the failure episode and starts over as absence/reconnection. A device-list notification on
///    its own (any HID device) only re-enumerates: an unchanged interface set neither reopens a working handle nor restarts
///    a recovery that gave up.
/// Whenever the keyboard may no longer show what was last sent (stored write, reopen, reconnect, vendor release),
/// <see cref="DisplayStateLost"/> tells the sink to resend.
/// </summary>
public sealed class KeyboardLightingController : IDisposable
{
    public const int MaxAttempts = 3;
    /// <summary>After a test write, sync waits this long (or until the owner answers) so the result stays visible.</summary>
    public static readonly TimeSpan TestHold = TimeSpan.FromSeconds(90);
    public const string VendorMarker = "DeviceDriver";
    public const int MaxReopenAttempts = 4;

    readonly IKeyboardTransport _transport;
    readonly Func<bool> _verified;
    readonly Func<string?> _liveOwnership;
    readonly Action<KeyboardLightingState?>? _persist;
    readonly AutoResetEvent _signal = new(false);
    readonly Thread _thread;
    readonly object _gate = new();
    /// <summary>Held for every write (stored, test, live) and while the handle is swapped.</summary>
    readonly object _writeGate = new();
    volatile bool _stop;
    volatile bool _deviceChanged = true;
    volatile bool _suspended;
    volatile bool _liveWanted;
    volatile bool _transportFailed;
    /// <summary>Live frames were shown since the last stored write: leaving live mode re-applies the stored setting.</summary>
    bool _liveSinceStored;
    volatile IKeyboardLink? _link;
    readonly Func<bool> _liveVerified;
    string? _signature;
    /// <summary>Interface set when the current failure episode began; a different set (or none) is a genuine device change.</summary>
    string? _failSignature;
    bool _everConnected, _reapply, _vendorSeen;
    KeyboardLightingState? _desired, _lastApplied, _gaveUpOn, _test;
    string? _blocked;
    DateTime _changedAt = DateTime.MinValue, _nextTry = DateTime.MinValue, _lastEnumerate = DateTime.MinValue, _holdUntil = DateTime.MinValue, _lastOwnershipPoll = DateTime.MinValue;
    DateTime _reopenAt = DateTime.MinValue;
    int _attempt, _reopenAttempts;

    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan[] Backoff { get; set; } = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };
    public TimeSpan EnumerateInterval { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Wait before reopen attempt 1, 2, 3, 4 after a transfer failed.</summary>
    public TimeSpan[] ReopenBackoff { get; set; } = { TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8) };
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public long Applies { get; private set; }
    public long Failures { get; private set; }
    public long TestWrites { get; private set; }
    /// <summary>Opened again after the device path changed (unplugged and plugged in).</summary>
    public long Reconnects { get; private set; }
    /// <summary>Closed and reopened after a failed transfer.</summary>
    public long Reopens { get; private set; }
    /// <summary>The reopen attempts are used up; waiting for a device-list change.</summary>
    public bool ReopenGaveUp { get; private set; }
    public long LiveFrames { get; private set; }
    public long LiveFailures { get; private set; }
    /// <summary>Ownership checks made at the write boundary (every write asks once).</summary>
    public long OwnershipChecks => Interlocked.Read(ref _ownershipChecks);
    long _ownershipChecks;
    /// <summary>Owner confirmed (by watching) that live frames show on the keyboard: <see cref="AppConfig.KeyboardLiveVerified"/>.</summary>
    public bool LiveVerified => _verified() && _liveVerified();
    public long Resyncs { get; private set; }
    public string Status { get; private set; } = "starting";
    public string LinkStatus { get; private set; } = "not connected";
    public string ProfileDetail { get; private set; } = "";
    public string? LastTestResult { get; private set; }
    public bool Connected => _link != null;
    public bool Holding => UtcNow() < _holdUntil;
    public KeyboardLightingState? LastApplied { get { lock (_gate) return _lastApplied; } }

    /// <summary>
    /// Whether the keyboard accepted the stored setting wanted now: Accepted when the last applied setting equals it,
    /// Failed after a failed write for it (retrying or given up), Pending otherwise. Live frames have their own
    /// acknowledgement (the sink's). API acceptance, not an optical confirmation.
    /// </summary>
    public HwSuite.Lighting.LightAck StoredAck
    {
        get
        {
            lock (_gate)
            {
                if (_desired == null) return HwSuite.Lighting.LightAck.Unknown;
                if (Equals(_desired, _lastApplied) && !_reapply) return HwSuite.Lighting.LightAck.Accepted;
                if (Equals(_desired, _gaveUpOn) || _attempt > 0) return HwSuite.Lighting.LightAck.Failed;
                return HwSuite.Lighting.LightAck.Pending;
            }
        }
    }
    /// <summary>Raised (on the controller thread) after each test request, with whether it was sent.</summary>
    public event Action<bool, string>? TestFinished;
    /// <summary>The keyboard may no longer show the last live frame (stored write, reopen, reconnect, vendor release): resend it.</summary>
    public event Action<string>? DisplayStateLost;

    public KeyboardLightingController(IKeyboardTransport transport, Func<bool> verified, Func<string?>? liveOwnership = null,
        KeyboardLightingState? lastApplied = null, Action<KeyboardLightingState?>? persist = null, bool listenForDeviceChanges = true,
        Func<bool>? liveVerified = null)
    {
        _transport = transport; _verified = verified; _liveVerified = liveVerified ?? (() => false); _liveOwnership = liveOwnership ?? (() => null); _lastApplied = lastApplied; _persist = persist;
        if (listenForDeviceChanges) { try { HidSharp.DeviceList.Local.Changed += (_, _) => NotifyDeviceChange(); } catch { } }
        _thread = new Thread(Run) { IsBackground = true, Name = "KeyboardLighting" };
        _thread.Start();
    }

    /// <summary>Settings-driven sync target. Ignored until the keyboard is verified.</summary>
    public void SetDesired(KeyboardLightingState? s)
    {
        lock (_gate)
        {
            if (Equals(s, _desired)) return;
            _desired = s; _changedAt = UtcNow(); _attempt = 0; _nextTry = DateTime.MinValue; _gaveUpOn = null;
        }
        _signal.Set();
    }

    /// <summary>
    /// The sink is streaming live frames (keeps the keyboard link open). Turning it off after live frames re-applies the
    /// stored setting (after the usual settle time), so a stored mode takes over at once instead of after the keyboard's timeout.
    /// </summary>
    public void SetLiveWanted(bool on)
    {
        if (_liveWanted == on) return;
        _liveWanted = on;
        if (!on) lock (_gate) if (_liveSinceStored) { _reapply = true; _changedAt = UtcNow(); _gaveUpOn = null; _attempt = 0; }
        _signal.Set();
    }

    /// <summary>
    /// Output is stopping (lighting off, app exit): if a stored setting is still waiting out its settle time, write it now,
    /// on the caller's thread, so the keyboard falls back to the current effect rather than the one before. Bounded: one
    /// write attempt, same ownership, profile and staleness checks as any stored write. Returns what happened.
    /// </summary>
    public string FlushPending()
    {
        KeyboardLightingState? want; bool pending;
        lock (_gate) { want = _desired; pending = want != null && !Equals(want, _lastApplied) && !Equals(want, _gaveUpOn); }
        if (!pending || want == null) return "nothing pending";
        if (!_verified() || DeferStoredWrites) return "not written (not verified or deferred)";
        if (_transport.Profile(out var detail) is not ILovbeeProfile p) return "not written: " + detail;
        bool ok; string e = "";
        lock (_writeGate)
        {
            KeyboardLightingState? current; lock (_gate) current = _desired;
            if (!Equals(current, want)) return "setting changed; not written";
            var block = WriteBlock(stored: true);
            if (block != null) return "not written: " + block;
            var link = _link;
            if (link == null || _transportFailed) return "not written: keyboard not connected";
            ok = link.Apply(want, p.Sleep, out e);
        }
        if (!ok) { lock (_gate) { Failures++; _lastApplied = null; } MarkTransportFailed("stored write at output stop failed: " + e); return "write failed: " + e; }
        lock (_gate) { _lastApplied = want; _attempt = 0; Applies++; }
        try { _persist?.Invoke(want); } catch { }
        Log.Info($"keyboard lighting applied at output stop ({want.Description}; {p.Source}): the keyboard falls back to it");
        return "written: " + want.Description;
    }

    /// <summary>
    /// App exit (owner's choice, 2026-10-15): store <paramref name="look"/> (the steady primary colour) so the keyboard is
    /// solid like the streamed devices after VANTA quits, instead of running the effect's onboard animation. One stored write,
    /// on the caller's thread, with the same checks as every stored write (verified, profile, ownership at the write, healthy
    /// link). Nothing is written when the keyboard already holds that setting. The next start stores the effect's look again.
    /// </summary>
    public string StoreExitLook(KeyboardLightingState look)
    {
        if (!_verified() || DeferStoredWrites) return "not written (not verified or deferred)";
        lock (_gate) if (Equals(look, _lastApplied)) return "already showing it";
        if (_transport.Profile(out var detail) is not ILovbeeProfile p) return "not written: " + detail;
        bool ok; string e = "";
        lock (_writeGate)
        {
            var block = WriteBlock(stored: true);
            if (block != null) return "not written: " + block;
            var link = _link;
            if (link == null || _transportFailed) return "not written: keyboard not connected";
            ok = link.Apply(look, p.Sleep, out e);
        }
        if (!ok) { lock (_gate) { Failures++; _lastApplied = null; } return "write failed: " + e; }
        lock (_gate) { _lastApplied = look; Applies++; }
        try { _persist?.Invoke(look); } catch { }
        Log.Info($"keyboard lighting applied at exit ({look.Description}; {p.Source}): the keyboard stays solid until VANTA starts again");
        return "written: " + look.Description;
    }

    /// <summary>Tests only: hold stored writes back (to see what live frames do over the current stored setting).</summary>
    public bool DeferStoredWrites { get; set; }
    public bool LiveWanted => _liveWanted;

    public enum LiveResult { Sent, Skipped, Failed }

    /// <summary>
    /// Send one live frame on the caller's thread (the sink worker). Nothing is sent unless the stored command was verified
    /// AND live frames were verified by the owner, live frames are wanted, the system is awake, no test result is being
    /// shown, the link is healthy, and, checked again at the write gate right before the transfer, nobody else owns the
    /// keyboard. A frame that is not allowed is skipped, not counted as a device failure.
    /// </summary>
    public LiveResult SendLive(byte[] payload, out string detail)
    {
        detail = "";
        if (!LiveVerified) { detail = "live frames not verified"; return LiveResult.Skipped; }
        if (_suspended) { detail = "system sleeping"; return LiveResult.Skipped; }
        if (UtcNow() < _holdUntil) { detail = "showing the test result"; return LiveResult.Skipped; }
        if (!_liveWanted) { detail = "live frames not wanted (the stored setting is in use)"; return LiveResult.Skipped; }
        if (_link == null || _transportFailed) { _signal.Set(); detail = _transportFailed ? "reopening the keyboard link" : "waiting for the keyboard"; return LiveResult.Skipped; }
        bool ok; string err;
        lock (_writeGate)
        {
            if (!_liveWanted) { detail = "live frames not wanted (the stored setting is in use)"; return LiveResult.Skipped; }
            var link = _link;
            if (link == null || _transportFailed) { detail = "waiting for the keyboard"; return LiveResult.Skipped; }
            var block = WriteBlock();
            if (block != null) { detail = "not writing: " + block; return LiveResult.Skipped; }
            ok = link.ApplyLive(payload, out err);
        }
        if (!ok)
        {
            LiveFailures++;
            MarkTransportFailed("live frame failed: " + err);
            detail = err; return LiveResult.Failed;
        }
        LiveFrames++;
        // the stored setting stays on the keyboard underneath; it is re-applied when live frames stop for a stored mode
        lock (_gate) { _liveSinceStored = true; _reopenAttempts = 0; }
        return LiveResult.Sent;
    }

    /// <summary>Engine's ownership decision (a hint; every write re-checks ownership itself).</summary>
    public void SetBlocked(string? reason)
    {
        lock (_gate) { if (_blocked == reason) return; _blocked = reason; if (reason?.Contains(VendorMarker) == true) _vendorSeen = true; }
        _signal.Set();
    }

    /// <summary>
    /// Device list changed (arrival/removal of any HID device): re-check the keyboard's interfaces soon. Only a different
    /// interface set (or the keyboard disappearing) is a genuine change; see the class remarks.
    /// </summary>
    public void NotifyDeviceChange()
    {
        _deviceChanged = true; _lastEnumerate = DateTime.MinValue; _signal.Set();
    }

    /// <summary>System sleep: nothing is written or reopened (not even a test) until resume.</summary>
    public void SetSuspended(bool on) { _suspended = on; _signal.Set(); }

    /// <summary>The owner pressed the test button: send exactly one LED command (no retries).</summary>
    public void RequestTestWrite(KeyboardLightingState s)
    {
        lock (_gate) _test = s;
        _signal.Set();
    }

    /// <summary>The owner answered (or dismissed) the test: sync may restore the effect's setting.</summary>
    public void EndTestHold()
    {
        lock (_gate) { _holdUntil = DateTime.MinValue; _changedAt = UtcNow() - Debounce; }
        _signal.Set();
        RaiseDisplayStateLost("test result dismissed");
    }

    /// <summary>
    /// Ownership check for stored ("LED(Save)") and test writes when it differs from the one asked before live frames (the
    /// app: a process list right at the write, while live frames read a state polled every 250 ms). Null: same check.
    /// </summary>
    public Func<string?>? StoredWriteOwnership { get; set; }

    /// <summary>Block reason at the write boundary: sleep, then the ownership check, then the engine's hint.</summary>
    string? WriteBlock(bool stored = false)
    {
        if (_suspended) return "system sleeping";
        string? live = null;
        Interlocked.Increment(ref _ownershipChecks);
        try { live = (stored ? StoredWriteOwnership ?? _liveOwnership : _liveOwnership)(); } catch (Exception ex) { live = "ownership check failed: " + ex.Message; }
        if (live?.Contains(VendorMarker) == true) lock (_gate) _vendorSeen = true;
        string? hint; lock (_gate) hint = _blocked;
        return live ?? hint;
    }

    /// <summary>A transfer failed or a listed keyboard could not be opened: start (or continue) a bounded recovery episode.</summary>
    void MarkTransportFailed(string why)
    {
        lock (_gate)
        {
            if (!_transportFailed) { _failSignature = _signature; ScheduleReopen(why); }
            _transportFailed = true;
        }
        _signal.Set();
    }

    /// <summary>Caller holds _gate. The next attempt after its backoff, or give up when the budget is used.</summary>
    void ScheduleReopen(string why)
    {
        if (_reopenAttempts >= MaxReopenAttempts)
        {
            ReopenGaveUp = true;
            LinkStatus = $"keyboard link failed after {MaxReopenAttempts} reopen attempts; waiting for it to be unplugged and plugged in again";
            Log.Warn($"keyboard: {why}; {LinkStatus}");
        }
        else
        {
            _reopenAt = UtcNow() + ReopenBackoff[Math.Min(_reopenAttempts, ReopenBackoff.Length - 1)];
            Log.Warn($"keyboard: {why}; reopening the handle (attempt {_reopenAttempts + 1}/{MaxReopenAttempts})");
        }
    }

    void RaiseDisplayStateLost(string why) { try { DisplayStateLost?.Invoke(why); } catch { } }

    void Run()
    {
        ThreadCpu.Register("keyboard lighting");
        while (!_stop)
        {
            _signal.WaitOne(250);
            if (_stop) break;
            try { Step(); }
            catch (Exception ex) { Status = "error: " + ex.Message; Log.Error("keyboard lighting", ex); }
        }
        lock (_writeGate) { try { _link?.Dispose(); } catch { } _link = null; }
    }

    void ManageLink(DateTime now, bool needLink)
    {
        if (!needLink || _suspended) return;   // nothing is opened or reopened while the system sleeps
        bool look = _deviceChanged || now - _lastEnumerate >= EnumerateInterval;

        if (_transportFailed)
        {
            // failure recovery, with or without a handle (a failed Open() leaves none): a genuine device change ends the
            // episode; otherwise reopen on the unchanged path, backed off and counted across failed opens
            if (look)
            {
                _deviceChanged = false; _lastEnumerate = now;
                var cur = _transport.Signature();
                if (cur != _failSignature) { EndRecoveryOnDeviceChange(cur); return; }
            }
            int attempt;
            lock (_gate)
            {
                if (ReopenGaveUp || now < _reopenAt) return;
                attempt = ++_reopenAttempts;
            }
            if (!Reopen($"reopened after a failed transfer (attempt {attempt}/{MaxReopenAttempts})", failure: true))
                lock (_gate) ScheduleReopen($"reopen attempt {attempt} failed ({LinkStatus})");
            return;
        }

        if (!(_deviceChanged || _link == null) || !look) return;
        _deviceChanged = false; _lastEnumerate = now;
        var sig = _transport.Signature();
        if (sig == _signature && _link != null) return;   // ordinary notification: same interfaces, the handle still works
        _signature = sig;
        if (sig.Length == 0) { CloseLink("not connected"); return; }   // absent: nothing to open, nothing counted
        if (!Reopen(null, failure: false)) MarkTransportFailed("the keyboard is listed but could not be opened (" + LinkStatus + ")");
    }

    /// <summary>The interface set changed (or the keyboard disappeared) during recovery: a genuine device change.</summary>
    void EndRecoveryOnDeviceChange(string sig)
    {
        lock (_gate) { _reopenAttempts = 0; ReopenGaveUp = false; _reopenAt = DateTime.MinValue; }
        _transportFailed = false;
        _signature = sig.Length == 0 ? sig : null;   // a listed keyboard is opened by the next discovery pass
        CloseLink(sig.Length == 0 ? "not connected (unplugged)" : "device changed; opening it again");
        Log.Info("keyboard: " + LinkStatus + " (failure recovery ended by a device change)");
        _deviceChanged = true; _lastEnumerate = DateTime.MinValue; _signal.Set();
    }

    void CloseLink(string status)
    {
        lock (_writeGate) { try { _link?.Dispose(); } catch { } _link = null; }
        LinkStatus = status;
    }

    /// <summary>Close any handle and open a fresh one. False when Open() failed (then no handle remains).</summary>
    bool Reopen(string? why, bool failure)
    {
        IKeyboardLink? fresh; string st;
        lock (_writeGate)   // no transfer runs on a handle that is being closed
        {
            try { _link?.Dispose(); } catch { }
            _link = null;
            fresh = _transport.Open(out st);
            _link = fresh;
        }
        LinkStatus = st;
        if (fresh == null) { if (why != null) Log.Warn($"keyboard: {why}: {st}"); return false; }
        _transportFailed = false;
        bool reconnection = _everConnected;
        _everConnected = true;
        if (failure) Reopens++;
        else if (reconnection) Reconnects++;
        if (reconnection && !failure)
        {
            // one bounded re-sync: the keyboard may have lost or changed its setting
            lock (_gate) { _reapply = true; _gaveUpOn = null; _attempt = 0; _nextTry = DateTime.MinValue; }
        }
        Log.Info($"keyboard: {(failure ? why : reconnection ? "reconnected" : "connected")} ({fresh.Describe})");
        if (reconnection) RaiseDisplayStateLost(failure ? "link reopened" : "reconnected");
        return true;
    }

    void Step()
    {
        var now = UtcNow();
        KeyboardLightingState? want, test;
        lock (_gate) { want = _desired; test = _test; }
        bool verified = _verified();
        ManageLink(now, test != null || (verified && (want != null || _liveWanted)));

        if (test != null) { RunTest(test, now); return; }

        if (!verified) { Status = "armed: awaiting one-click verification"; return; }

        // poll ownership occasionally even with nothing to send, so a vendor session is noticed and followed by a re-sync
        string? block;
        if (want != null || now - _lastOwnershipPoll > TimeSpan.FromSeconds(5)) { _lastOwnershipPoll = now; block = WriteBlock(); }
        else { lock (_gate) block = _blocked; if (_suspended) block = "system sleeping"; }
        if (block != null) { Status = "not writing: " + block; return; }
        bool released = false;
        lock (_gate)
        {
            if (_vendorSeen)
            {
                // the vendor app may have rewritten the keyboard: forget what we think it shows, re-sync once
                _vendorSeen = false; released = true;
                if (_lastApplied != null) { _lastApplied = null; Resyncs++; Log.Info("keyboard: vendor software released the keyboard; one re-sync"); }
            }
        }
        if (released) RaiseDisplayStateLost("vendor software released the keyboard");

        if (now < _holdUntil) { Status = "showing the test result"; return; }
        if (want == null)
        {
            if (_liveWanted && LiveVerified) { Status = _transportFailed ? (ReopenGaveUp ? LinkStatus : "reopening the keyboard link") : _link == null ? "waiting for the keyboard" : "live frames (no stored fallback set)"; return; }
            Status = LastApplied == null ? "idle (keyboard not included or lighting off)" : "idle; keyboard keeps its last setting"; return;
        }
        if (_transportFailed) { Status = ReopenGaveUp ? LinkStatus : "reopening the keyboard link"; return; }
        if (_link == null) { Status = "waiting for the keyboard"; return; }
        bool reapply; lock (_gate) reapply = _reapply;
        if (Equals(want, LastApplied) && !(reapply && !_liveWanted)) { Status = (_liveWanted ? "live frames; stored fallback up to date: " : "up to date: ") + want.Description; return; }
        if (DeferStoredWrites) { Status = "stored write deferred (test)"; return; }
        KeyboardLightingState? gaveUp; lock (_gate) gaveUp = _gaveUpOn;
        if (Equals(want, gaveUp)) { Status = $"gave up after {MaxAttempts} attempts; waiting for a change or reconnection"; return; }

        DateTime changedAt, nextTry;
        lock (_gate) { changedAt = _changedAt; nextTry = _nextTry; }
        if (now - changedAt < Debounce) { Status = "waiting for the setting to settle"; Thread.Sleep(100); _signal.Set(); return; }
        if (now < nextTry) { Status = $"retry in {(nextTry - now).TotalSeconds:0.0} s"; Thread.Sleep(100); _signal.Set(); return; }

        if (_transport.Profile(out var detail) is not ILovbeeProfile p) { ProfileDetail = detail; Status = "not writing: " + detail; return; }
        ProfileDetail = detail;
        var sw = Stopwatch.StartNew();
        bool ok; string e = "";
        lock (_writeGate)
        {
            // the setting may have changed, or live frames resumed, since it was read: never write a stale stored setting
            KeyboardLightingState? current; lock (_gate) current = _desired;
            if (!Equals(current, want)) { Status = "setting changed before the write; not written"; _signal.Set(); return; }
            block = WriteBlock(stored: true);   // ownership re-checked right at the write
            if (block != null) { Status = "not writing: " + block; return; }
            var link = _link;
            if (link == null || _transportFailed) { Status = "waiting for the keyboard"; return; }
            ok = link.Apply(want, p.Sleep, out e);
        }
        lock (_gate)
        {
            if (ok)
            {
                _lastApplied = want; _attempt = 0; _gaveUpOn = null; _reapply = false; _reopenAttempts = 0; Applies++;
                if (!_liveWanted) _liveSinceStored = false;
                Status = (_liveWanted ? "live frames; stored fallback up to date: " : "up to date: ") + want.Description;
            }
            else
            {
                Failures++; _attempt++; _lastApplied = null;   // unknown what it shows now
                if (_attempt >= MaxAttempts) { _gaveUpOn = want; _reapply = false; Status = $"gave up after {MaxAttempts} attempts ({e})"; }
                else { _nextTry = UtcNow() + Backoff[Math.Min(_attempt - 1, Backoff.Length - 1)]; Status = $"write failed ({e}), retry {_attempt}/{MaxAttempts - 1}"; }
            }
        }
        if (ok)
        {
            try { _persist?.Invoke(want); } catch { }
            Log.Info($"keyboard lighting applied ({want.Description}; {p.Source}, sleep {ILovbeeProfile.SleepText(p.Sleep)}) in {sw.ElapsedMilliseconds} ms");
            RaiseDisplayStateLost("stored setting written");
        }
        else { MarkTransportFailed("stored write failed: " + e); Log.Warn("keyboard lighting: " + Status); }
    }

    void RunTest(KeyboardLightingState test, DateTime now)
    {
        lock (_gate) _test = null;
        string result;
        bool sent = false;
        if (_link == null) result = "Not sent: keyboard " + LinkStatus + ".";
        else if (_transport.Profile(out var pd) is not ILovbeeProfile prof) { ProfileDetail = pd; result = "Not sent: " + pd; }
        else
        {
            ProfileDetail = pd;
            string err = ""; string? block;
            lock (_writeGate)
            {
                block = WriteBlock(stored: true);
                if (block == null && _link is IKeyboardLink link) { sent = link.Apply(test, prof.Sleep, out err); TestWrites++; }
                else if (block == null) err = "keyboard " + LinkStatus;
            }
            if (block != null) result = "Not sent: " + block + ".";
            else
            {
                lock (_gate)
                {
                    // the keyboard now shows the test (or, after a failure, something unknown): never leave a stale "up to date"
                    _lastApplied = sent ? test : null;
                    _holdUntil = sent ? now + TestHold : DateTime.MinValue;
                    _gaveUpOn = null;
                    if (sent) _vendorSeen = false;   // written after the vendor software left: the cache is accurate again
                }
                result = sent ? $"Sent: {test.Description} ({pd})." : "Write failed: " + err;
                if (!sent) MarkTransportFailed("test write failed: " + err);
                else { try { _persist?.Invoke(test); } catch { } RaiseDisplayStateLost("test write"); }
            }
        }
        LastTestResult = result;
        Log.Info("keyboard test write: " + result);
        try { TestFinished?.Invoke(sent, result); } catch { }
    }

    public void Dispose()
    {
        _stop = true; _signal.Set();
        try { _thread.Join(1500); } catch { }
    }
}
