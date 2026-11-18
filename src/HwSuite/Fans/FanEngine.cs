using System.Collections.Concurrent;
using HwSuite.Core;
using HwSuite.Hardware;

namespace HwSuite.Fans;

public sealed class ChannelStatus
{
    public string Id { get; set; } = "";
    public ChannelMode Mode { get; set; }
    public double? SourceTempC { get; set; }
    public double? EffectiveTempC { get; set; }
    public double? TargetPercent { get; set; }
    public double? AppliedPercent { get; set; }
    public double? FloorPercent { get; set; }
    public string FloorBasis { get; set; } = "";
    public bool Controlled { get; set; }
    public bool FeedbackLost { get; set; }
    /// <summary>Set when VANTA raised this fan's minimum for the session because it kept stopping below it.</summary>
    public string? Warning { get; set; }
    public string State { get; set; } = "auto";
}

/// <summary>
/// Evaluates every channel's curve once per telemetry tick. Behaviour guarantees:
///  - only channels with an evidence-backed floor are driven (FanSafety); unidentified headers stay with the firmware;
///  - HwSuite writes only while it owns the hardware (IOwnership): a confirmed competing controller means no writes,
///    and an outside change to a GPU fan HwSuite set makes it stop writing instead of fighting;
///  - hysteresis, and separate ramp-up / step-down limits (no erratic revving);
///  - emergency: CPU >= 90 C or GPU >= 84 C forces 100 % on the channels HwSuite drives, until it has cooled;
///  - a missing or stale temperature hands the affected channel back to firmware/VBIOS control (which has its own
///    sensors); if that hand-back fails the channel is driven at 100 %;
///  - startup verification: a channel whose tachometer is known (mapped by a step test, or seen turning before) must show
///    rotation within <see cref="StartupWindow"/> of HwSuite taking it over; if not, it gets a bounded number of full-speed
///    start kicks, and if it still does not turn it is handed back to firmware control (100 % if that fails) and left
///    there for <see cref="StartFailRetry"/>, with an alert. It does not matter whether rotation was ever seen before;
///  - a verified tachometer that then reads zero while HwSuite drives the channel latches 100 % until it recovers;
///    a reading below <see cref="MinTurningRpm"/> counts as stopped. If the fan keeps coming back at full speed
///    (<see cref="FlapLimit"/> quick returns within <see cref="FlapWindow"/>), it stops below the curve's duty: VANTA raises
///    its minimum for the session (<see cref="FlapMargin"/> above the duty it stopped at, at most 100 %), with an alert,
///    instead of surging it between the curve and 100 % (the firmware could run it just as low, so it is not handed back);
///  - three consecutive write failures on a channel fault it: hand-back, then a 30 s pause before retrying;
///  - sleep: everything is handed back before suspend, and control resumes 10 s after resume;
///  - if ticks stop arriving for 10 s, every channel is handed back to firmware control.
/// </summary>
public sealed class FanEngine : IDisposable
{
    public const double CpuEmergencyC = 90, GpuEmergencyC = 84, CpuRecoverC = 80, GpuRecoverC = 74;
    /// <summary>Duty applied when a channel cannot be handed back to firmware control.</summary>
    public const double FallbackDutyPercent = 100;
    public const int FailuresBeforeFault = 3;
    public const int TachLossTicks = 3, TachRecoverTicks = 5;
    /// <summary>Feedback-loss detection only applies once HwSuite has driven the channel at or above this duty.</summary>
    public const double TachCheckMinDuty = 20;
    /// <summary>
    /// Tachometer readings above this are not a speed. Phase 9 (docs\phase9\mintest-ctl1): when a 3-pin case fan on the
    /// IT8689E stalled, its tachometer briefly reported 9 926 and 9 643 RPM before reading 0. Nothing on this PC spins near
    /// 5 000 RPM (case fans ~1 100, pump ~1 750, GPU fans < 3 000).
    /// </summary>
    public const double MaxPlausibleRpm = 5000;
    /// <summary>A reading that shows the device turning: above 0 and plausible.</summary>
    public static bool Spinning(double? rpm) => rpm is double r && r > 0 && r <= MaxPlausibleRpm;
    public static readonly TimeSpan FaultRetry = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ResumeGrace = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(10);
    /// <summary>
    /// How long a driven fan may take to show rotation. The IT8689E DC headers ramp slowly: the lower front fan, started
    /// from rest at its 50% floor (Phase 10, docs\phase10anstart-ctl3), first read a speed after 10.8, 6.0 and 4.8 s, and
    /// 8.4 s when the engine took it over from a stop. 15 s keeps a margin without leaving a stalled fan unnoticed for long.
    /// </summary>
    public static readonly TimeSpan StartupWindow = TimeSpan.FromSeconds(15);
    /// <summary>One start kick: full duty for this long.</summary>
    public static readonly TimeSpan StartKick = TimeSpan.FromSeconds(6);
    public const int MaxStartKicks = 2;
    /// <summary>After the kicks failed, the channel stays with the firmware this long before VANTA tries again.</summary>
    public static readonly TimeSpan StartFailRetry = TimeSpan.FromMinutes(10);
    /// <summary>
    /// A driven fan reading less than this is not turning usefully (the 3-pin case fans run 600+ RPM at 35%; the floors
    /// were measured with the same 200 RPM stall test). 1.14.1: the upper front fan (SYS_FAN2, 2026-10-18 from 23:19) was
    /// stuck and read 23 RPM at 81%, which the engine had counted as turning; the owner flicked it and it ran again.
    /// </summary>
    public const double MinTurningRpm = 200;
    public static bool Turning(double? rpm) => Spinning(rpm) && rpm >= MinTurningRpm;
    /// <summary>A lost speed signal that is back at full speed within this time counts as a quick return.</summary>
    public static readonly TimeSpan QuickReturn = TimeSpan.FromSeconds(30);
    /// <summary>This many quick returns within <see cref="FlapWindow"/> mean the fan stops at the duty it is driven at.</summary>
    public const int FlapLimit = 3;
    public static readonly TimeSpan FlapWindow = TimeSpan.FromMinutes(10);
    /// <summary>How far above the highest duty it stopped at the raised session minimum is set.</summary>
    public const double FlapMargin = 15;

    sealed class Runtime
    {
        public double? Current;
        public double? EffectiveTemp;
        public double LastApplied = -1;
        public DateTime LastSend = DateTime.MinValue;
        public bool Controlled;
        public bool ReleasedForZeroRpm;
        public int Failures;
        public DateTime FaultUntil = DateTime.MinValue;
        public string? FaultReason;
        public int TachMissing, TachOk;
        public bool FeedbackLost;
        public string? LastWithheld;
        /// <summary>Rotation seen since HwSuite took this channel over (reset by every hand-back).</summary>
        public bool StartVerified;
        public DateTime? StartDeadline;
        public DateTime KickUntil = DateTime.MinValue;
        public int StartKicks;
        public DateTime StartFailedUntil = DateTime.MinValue;
        public string? StartFailure;
        /// <summary>When the current loss began and the duty it was lost at; quick returns (time, duty at loss) in the flap window.</summary>
        public DateTime LossSince;
        public double LossDuty;
        public readonly List<(DateTime at, double duty)> QuickReturns = new();
        /// <summary>Session minimum raised because the fan kept stopping (0 = none), and why.</summary>
        public double RaisedFloor;
        public string? RaisedWhy;
    }

    /// <summary>Whether the current control period's start was verified, start kicks so far, and whether it is parked after a failed start.</summary>
    public (bool verified, int kicks, bool failed) StartState(string channelId)
    {
        lock (_tickGate) return _rt.TryGetValue(channelId, out var r) ? (r.StartVerified, r.StartKicks, UtcNow() < r.StartFailedUntil) : (false, 0, false);
    }

    readonly AppConfig _cfg;
    readonly Dictionary<string, Runtime> _rt = new();
    readonly Timer _stallGuard;
    readonly ConcurrentDictionary<string, (DateTime until, double duty)> _identify = new();
    readonly HashSet<string> _upgraded = new();
    readonly object _tickGate = new();
    DateTime _lastTick = DateTime.UtcNow;
    DateTime _resumeAt = DateTime.MinValue;
    bool _enabledLastTick;
    int _stallReleasing;

    public List<IFanChannel> Channels { get; } = new();
    public ConcurrentDictionary<string, ChannelStatus> Status { get; } = new();
    public bool Emergency { get; internal set; }
    public bool Suspended { get; private set; }
    public IOwnership Ownership { get; set; } = AssumeOwned.Instance;
    /// <summary>Clock used for timeouts (tests replace it).</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    /// <summary>Last time another program was seen changing a fan we control (for example MSI Afterburner).</summary>
    public DateTime LastExternalWrite { get; private set; } = DateTime.MinValue;
    public long ExternalWriteEvents { get; private set; }
    /// <summary>Raised when the engine changes persisted channel facts (tachometer history), so the owner can save the config.</summary>
    public event Action? ConfigDirty;
    /// <summary>Raised for events the owner should see (faults, lost feedback, lost temperature).</summary>
    public event Action<string>? Alert;

    public FanEngine(AppConfig cfg)
    {
        _cfg = cfg;
        _stallGuard = new Timer(_ => StallCheck(), null, 2000, 2000);
    }

    public static HwResource ResourceFor(IFanChannel ch) => ch.Resource;

    ChannelConfig ConfigFor(IFanChannel ch)
    {
        lock (_cfg.Sync)
        {
            var cc = _cfg.Channels.FirstOrDefault(c => c.Id == ch.Id);
            if (cc == null)
            {
                cc = ChannelDefaults.Create(ch, _cfg.Preset);
                _cfg.Channels.Add(cc);
                _upgraded.Add(ch.Id);
            }
            else if (_upgraded.Add(ch.Id)) ChannelDefaults.Upgrade(cc, ch, _cfg.Preset);
            return cc;
        }
    }

    /// <summary>The configured source only. A CPU-driven curve never silently falls back to the GPU temperature (or vice versa).</summary>
    public static double? SourceTemp(CurveSource src, TelemetrySnapshot s) => src switch
    {
        CurveSource.Cpu => s.CpuTempC,
        CurveSource.Gpu => s.GpuTempC,
        _ => s.CpuTempC is double c && s.GpuTempC is double g ? Math.Max(c, g) : null
    };

    static string SourceName(CurveSource src) => src switch { CurveSource.Cpu => "CPU", CurveSource.Gpu => "GPU", _ => "CPU and GPU" };

    Runtime RtFor(IFanChannel ch) => _rt.TryGetValue(ch.Id, out var r) ? r : _rt[ch.Id] = new Runtime();

    public bool IsControlled(string channelId) { lock (_tickGate) return _rt.TryGetValue(channelId, out var r) && r.Controlled; }

    public void Tick(TelemetrySnapshot s, double dt)
    {
        lock (_tickGate) TickLocked(s, dt);
    }

    void TickLocked(TelemetrySnapshot s, double dt)
    {
        var now = UtcNow();
        _lastTick = now;
        bool enabled;
        lock (_cfg.Sync) enabled = _cfg.FanControlEnabled;

        if (Suspended || now < _resumeAt)
        {
            foreach (var ch in Channels)
                Status[ch.Id] = new ChannelStatus { Id = ch.Id, State = Suspended ? "firmware control (system sleeping)" : "firmware control (waiting after resume)" };
            return;
        }

        RecordTachHistory();
        var identifying = ProcessIdentify(enabled);

        if (!enabled)
        {
            if (_enabledLastTick) ReleaseAllLocked("fan control switched off");
            _enabledLastTick = false;
            foreach (var ch in Channels)
                if (!identifying.Contains(ch.Id))
                    Status[ch.Id] = new ChannelStatus { Id = ch.Id, Mode = ChannelMode.Auto, State = "firmware control (fan control off)" };
            return;
        }
        _enabledLastTick = true;

        // thermal emergency with recovery hysteresis
        bool hot = (s.CpuTempC ?? 0) >= CpuEmergencyC || (s.GpuTempC ?? 0) >= GpuEmergencyC;
        bool cool = (s.CpuTempC ?? 0) < CpuRecoverC && (s.GpuTempC ?? 0) < GpuRecoverC;
        if (hot && !Emergency) Log.Warn($"THERMAL EMERGENCY cpu={s.CpuTempC:0.#} gpu={s.GpuTempC:0.#}: controlled channels to 100%");
        if (hot) Emergency = true; else if (Emergency && cool) { Emergency = false; Log.Info("Thermal emergency cleared"); }

        foreach (var ch in Channels)
        {
            if (identifying.Contains(ch.Id)) continue;   // an identify pulse owns this channel for a few seconds
            var rt = RtFor(ch);
            try { TickChannel(ch, rt, s, dt, now); }
            catch (Exception ex)
            {
                Log.Error($"fan engine channel {ch.Id}", ex);
                OnWriteFailure(ch, rt, ex.GetType().Name + ": " + ex.Message, now);
            }
        }
    }

    void RecordTachHistory()
    {
        bool dirty = false;
        foreach (var ch in Channels)
        {
            if (!ch.HasTachSensor) continue;
            double? rpm;
            try { rpm = ch.ReadRpm(); } catch { continue; }
            if (Spinning(rpm))
            {
                var cc = ConfigFor(ch);
                lock (_cfg.Sync) { if (!cc.TachSeen) { cc.TachSeen = true; dirty = true; } }
            }
        }
        if (dirty) ConfigDirty?.Invoke();
    }

    void TickChannel(IFanChannel ch, Runtime rt, TelemetrySnapshot s, double dt, DateTime now)
    {
        var cc = ConfigFor(ch);
        var st = new ChannelStatus { Id = ch.Id, Mode = cc.Mode };
        Status[ch.Id] = st;

        double? rpm = ch.HasTachSensor ? ch.ReadRpm() : null;
        bool tachSeen; lock (_cfg.Sync) tachSeen = cc.TachSeen;

        if (!ch.CanControl || cc.Mode == ChannelMode.Auto)
        {
            if (rt.Controlled) HandBack(ch, rt, "set to firmware control");
            st.State = !ch.CanControl ? "read-only" : "firmware control";
            if (tachSeen && ch.HasTachSensor && !Spinning(rpm)) st.State += "; tachometer reads 0 (or an implausible value) although it reported a speed before: check the device";
            return;
        }

        var safety = FanSafety.Assess(cc, ch);
        st.FloorPercent = safety.Controllable ? safety.Floor : null;
        st.FloorBasis = safety.Basis;
        if (!safety.Controllable)
        {
            if (rt.Controlled) HandBack(ch, rt, safety.Blocker ?? "not controllable");
            st.State = "left under firmware control: " + safety.Blocker;
            return;
        }

        if (now < rt.StartFailedUntil)
        {
            if (rt.Controlled && !HandBack(ch, rt, "did not start") && ch.SetPercent(FallbackDutyPercent)) { rt.LastApplied = FallbackDutyPercent; rt.LastSend = now; }
            st.State = $"firmware control: {rt.StartFailure}; VANTA retries in {Math.Ceiling((rt.StartFailedUntil - now).TotalMinutes):0} min";
            st.Controlled = rt.Controlled;
            return;
        }

        var resource = ResourceFor(ch);
        var own = Ownership.Check(resource);
        if (!own.MayWrite)
        {
            if (rt.Controlled) Yield(ch, rt, resource, own.Reason);
            if (rt.LastWithheld != own.Reason) { Log.Warn($"{ch.Id}: not writing ({own.Reason})"); rt.LastWithheld = own.Reason; }
            st.State = "not writing: " + own.Reason;
            return;
        }
        rt.LastWithheld = null;

        if (now < rt.FaultUntil)
        {
            st.State = $"faulted ({rt.FaultReason}); retrying in {(rt.FaultUntil - now).TotalSeconds:0} s";
            st.Controlled = rt.Controlled;
            return;
        }

        FanCurve curve; double manual; FanKind kind;
        lock (_cfg.Sync) { curve = cc.Curve.Clone(); manual = cc.ManualPercent; kind = cc.Kind; }

        double? temp = SourceTemp(curve.Source, s);
        st.SourceTempC = temp;
        double target;
        string state;
        bool jumpUp = false; // failsafe duties are applied at once, never ramped

        if (Emergency) { target = 100; state = "thermal emergency"; rt.Current = 100; }
        else if (cc.Mode == ChannelMode.Manual) { target = manual; state = "manual"; }
        else if (temp is not double t)
        {
            // Temperature lost or stale: give the channel back to firmware/VBIOS control, which reads its own sensors.
            string why = $"{SourceName(curve.Source)} temperature unavailable ({(curve.Source == CurveSource.Gpu ? s.GpuTempStatus : curve.Source == CurveSource.Cpu ? s.CpuTempStatus : s.CpuTempStatus + " / " + s.GpuTempStatus)})";
            if (rt.Controlled)
            {
                Alert?.Invoke($"{ChannelDefaults.DisplayName(_cfg, ch)}: {why}; handed back to firmware control");
                if (!HandBack(ch, rt, why) && ch.SetPercent(FallbackDutyPercent)) { rt.LastApplied = FallbackDutyPercent; rt.LastSend = now; }
            }
            st.State = rt.Controlled ? why + $"; hand-back failed, holding {FallbackDutyPercent:0}%" : why + "; firmware control until it returns";
            st.Controlled = rt.Controlled;
            st.AppliedPercent = rt.Controlled ? rt.LastApplied : null;
            return;
        }
        else
        {
            // GPU zero-RPM style release: give the fans back to the VBIOS below the threshold
            if (ch.Resource == HwResource.GpuFans && curve.ReleaseToAutoBelowC > 0)
            {
                if (!rt.ReleasedForZeroRpm && t < curve.ReleaseToAutoBelowC) rt.ReleasedForZeroRpm = true;
                else if (rt.ReleasedForZeroRpm && t >= curve.ReleaseToAutoBelowC + 3) rt.ReleasedForZeroRpm = false;
                if (rt.ReleasedForZeroRpm)
                {
                    if (rt.Controlled) HandBack(ch, rt, "zero-RPM zone");
                    st.EffectiveTempC = t; st.State = "VBIOS auto (zero-RPM zone)";
                    return;
                }
            }

            // hysteresis: rise immediately, fall only after the deadband is exceeded
            if (rt.EffectiveTemp is not double eff || t >= eff) rt.EffectiveTemp = t;
            else if (eff - t > curve.HysteresisC) rt.EffectiveTemp = t + curve.HysteresisC;
            st.EffectiveTempC = rt.EffectiveTemp;
            target = curve.Evaluate(rt.EffectiveTemp!.Value);
            state = "curve";
        }

        bool tachKnown = ch.HasTachSensor && (cc.TachMapped || tachSeen);
        // Startup verification: rotation must appear after HwSuite takes the channel over, whatever the rotation history.
        if (tachKnown && rt.Controlled && !rt.StartVerified)
        {
            if (Turning(rpm))
            {
                rt.StartVerified = true; rt.StartDeadline = null; rt.KickUntil = DateTime.MinValue;
                if (rt.StartKicks > 0) Log.Info($"{ch.Id}: turning after {rt.StartKicks} start kick(s) ({rpm:0} RPM)");
                rt.StartKicks = 0;
            }
            else
            {
                rt.StartDeadline ??= now + StartupWindow;
                if (now >= rt.StartDeadline && now >= rt.KickUntil)
                {
                    if (rt.StartKicks < MaxStartKicks)
                    {
                        rt.StartKicks++;
                        rt.KickUntil = now + StartKick;
                        rt.StartDeadline = rt.KickUntil + TimeSpan.FromSeconds(2);
                        Log.Warn($"{ch.Id}: no rotation {StartupWindow.TotalSeconds:0} s after VANTA drove it at {rt.LastApplied:0}%: start kick {rt.StartKicks}/{MaxStartKicks} (100% for {StartKick.TotalSeconds:0} s)");
                        Alert?.Invoke($"{ChannelDefaults.DisplayName(_cfg, ch)}: did not start at {rt.LastApplied:0}%. Running it at 100% for {StartKick.TotalSeconds:0} s (start attempt {rt.StartKicks} of {MaxStartKicks}).");
                    }
                    else
                    {
                        rt.StartFailure = $"no rotation after {MaxStartKicks} full-speed start attempts";
                        rt.StartFailedUntil = now + StartFailRetry;
                        Log.Error($"{ch.Id}: {rt.StartFailure}; handing back to firmware control for {StartFailRetry.TotalMinutes:0} min");
                        Alert?.Invoke($"{ChannelDefaults.DisplayName(_cfg, ch)}: did not start even at 100%. Handed back to firmware control; check the fan and its cable (VANTA retries in {StartFailRetry.TotalMinutes:0} min).");
                        if (!HandBack(ch, rt, "did not start") && ch.SetPercent(FallbackDutyPercent)) { rt.LastApplied = FallbackDutyPercent; rt.LastSend = now; }
                        st.State = $"firmware control: {rt.StartFailure}";
                        st.Controlled = rt.Controlled;
                        return;
                    }
                }
            }
        }
        if (now < rt.KickUntil) { target = FallbackDutyPercent; jumpUp = true; state = $"start check: 100% start kick {rt.StartKicks}/{MaxStartKicks}"; }
        else if (tachKnown && rt.Controlled && !rt.StartVerified) state += " (waiting for rotation)";

        // Feedback that was verified and disappears while HwSuite drives the channel: hold full speed until it returns.
        if (tachKnown && rt.Controlled && rt.StartVerified && rt.LastApplied >= TachCheckMinDuty)
        {
            if (Turning(rpm))
            {
                rt.TachMissing = 0;
                if (rt.FeedbackLost && ++rt.TachOk >= TachRecoverTicks)
                {
                    rt.FeedbackLost = false; rt.TachOk = 0;
                    Log.Info($"{ch.Id}: tachometer signal is back");
                    if (now - rt.LossSince <= QuickReturn) RaiseFloorIfFlapping(ch, rt, now);
                }
            }
            else
            {
                rt.TachOk = 0;
                if (!rt.FeedbackLost && ++rt.TachMissing >= TachLossTicks)
                {
                    rt.FeedbackLost = true; rt.LossSince = now; rt.LossDuty = rt.LastApplied;
                    Log.Error($"{ch.Id}: tachometer reads {(Spinning(rpm) ? $"{rpm:0} RPM" : "0")} at {rt.LastApplied:0}% although it worked before: holding {FallbackDutyPercent:0}%");
                    Alert?.Invoke($"{ChannelDefaults.DisplayName(_cfg, ch)}: speed signal lost while driven at {rt.LastApplied:0}%. Holding {FallbackDutyPercent:0}% until it returns. Check the pump/fan cable.");
                }
            }
        }
        if (rt.FeedbackLost) { target = FallbackDutyPercent; jumpUp = true; state = "speed signal lost: holding 100%"; }
        st.FeedbackLost = rt.FeedbackLost;

        double floor = Math.Max(Math.Max(safety.Floor, curve.MinPercent), rt.RaisedFloor);
        st.Warning = rt.RaisedFloor > 0 ? rt.RaisedWhy : null;
        double ceiling = Math.Clamp(curve.MaxPercent, floor, 100);
        target = Math.Clamp(target, floor, ceiling);
        if (Emergency || rt.FeedbackLost || now < rt.KickUntil) target = 100;
        st.TargetPercent = target;

        // start from the real current duty so taking control never causes a jump
        rt.Current ??= ch.ReadPercent() is double nowPct ? Math.Clamp(nowPct, floor, 100) : target;
        double cur = rt.Current.Value;
        if (!Emergency)
        {
            if (target > cur) cur = Math.Min(target, cur + Math.Max(0.5, curve.RampUpPerSec) * dt);
            else cur = Math.Max(target, cur - Math.Max(0.2, curve.StepDownPerSec) * dt);
        }
        else cur = 100;
        if (jumpUp && cur < target) cur = target;
        cur = Math.Max(cur, floor);
        rt.Current = cur;

        double rounded = Math.Round(cur);
        bool changed = Math.Abs(rounded - rt.LastApplied) >= 1;
        bool refresh = (now - rt.LastSend).TotalSeconds >= 5; // periodic re-assert (chip reset, resume)

        // Another program overwrote a GPU fan we set (NVML exposes the current target): stop writing, do not fight.
        if (rt.Controlled && rt.LastApplied >= 0 && ch.ReadTargetPercent() is int observed && Math.Abs(observed - rt.LastApplied) >= 2)
        {
            ExternalWriteEvents++;
            LastExternalWrite = DateTime.Now;
            string detail = $"{ch.Name} target became {observed}%, HwSuite had set {rt.LastApplied:0}%";
            Ownership.ReportExternalWrite(resource, detail);
            Yield(ch, rt, resource, detail);
            st.State = "not writing: another program is controlling this fan (" + detail + ")";
            return;
        }

        if (changed || refresh)
        {
            if (ch.SetPercent(rounded)) { rt.LastApplied = rounded; rt.LastSend = now; rt.Controlled = true; rt.Failures = 0; }
            else { OnWriteFailure(ch, rt, "write failed", now); state += " (write failed)"; }
        }
        st.Controlled = rt.Controlled;
        st.AppliedPercent = rt.LastApplied >= 0 ? rt.LastApplied : null;
        st.State = state;
    }

    /// <summary>Give the channel back to firmware/VBIOS control. Returns false if the hardware refused.</summary>
    bool HandBack(IFanChannel ch, Runtime rt, string why)
    {
        bool ok;
        try { ok = ch.ReleaseToAuto(); }
        catch (Exception ex) { Log.Error($"hand-back {ch.Id}", ex); ok = false; }
        if (ok)
        {
            rt.Controlled = false; rt.Current = null; rt.LastApplied = -1; rt.EffectiveTemp = null; rt.FeedbackLost = false; rt.TachMissing = 0;
            rt.StartVerified = false; rt.StartDeadline = null; rt.KickUntil = DateTime.MinValue; rt.StartKicks = 0;
            Log.Info($"{ch.Id} handed back to firmware control ({why})");
        }
        else Log.Error($"{ch.Id}: hand-back to firmware failed ({why})");
        return ok;
    }

    /// <summary>
    /// Records a quick return (stopped at the curve's duty, turning again at 100%). After <see cref="FlapLimit"/> of them
    /// within <see cref="FlapWindow"/> the fan's minimum for this session is raised to <see cref="FlapMargin"/> above the
    /// highest duty it stopped at (at most 100%), with an alert. Never lowers a minimum; not saved to the configuration.
    /// </summary>
    void RaiseFloorIfFlapping(IFanChannel ch, Runtime rt, DateTime now)
    {
        rt.QuickReturns.Add((rt.LossSince, rt.LossDuty));
        rt.QuickReturns.RemoveAll(q => now - q.at > FlapWindow);
        if (rt.QuickReturns.Count < FlapLimit) return;

        double lo = rt.QuickReturns.Min(q => q.duty), hi = rt.QuickReturns.Max(q => q.duty);
        string duty = Math.Round(lo) == Math.Round(hi) ? $"{lo:0}%" : $"{lo:0}-{hi:0}%";
        double span = Math.Max(1, (now - rt.QuickReturns[0].at).TotalSeconds);
        string within = span < 120 ? $"{span:0} s" : $"{span / 60:0} min";
        int n = rt.QuickReturns.Count;
        rt.QuickReturns.Clear();
        double raised = Math.Min(100, Math.Ceiling((hi + FlapMargin) / 5) * 5);
        if (raised <= rt.RaisedFloor) raised = Math.Min(100, rt.RaisedFloor + 10);
        rt.RaisedFloor = raised;
        rt.RaisedWhy = $"kept at {raised:0}% or more: it stopped {n} times within {within} at {duty} and ran again at 100%";
        Log.Error($"{ch.Id}: stopped {n} times within {within} at {duty}, turning again each time at 100%: minimum raised to {raised:0}% for this session");
        Alert?.Invoke($"{ChannelDefaults.DisplayName(_cfg, ch)}: kept stopping at {duty} and only ran again at full speed. " +
            $"VANTA now keeps it at {raised:0}% or more until VANTA restarts, instead of surging it to 100%. The fan may be sticking: check it for an obstruction and plan to clean or replace it.");
    }

    /// <summary>Another controller owns the hardware. GPU fans are not touched at all (a hand-back would override that program); Super I/O headers are handed back once.</summary>
    void Yield(IFanChannel ch, Runtime rt, HwResource resource, string reason)
    {
        if (resource == HwResource.GpuFans)
        {
            ch.Relinquish();
            rt.Controlled = false; rt.Current = null; rt.LastApplied = -1; rt.EffectiveTemp = null;
            rt.StartVerified = false; rt.StartDeadline = null; rt.KickUntil = DateTime.MinValue; rt.StartKicks = 0;
            Log.Warn($"{ch.Id}: yielded to another controller without writing ({reason})");
        }
        else HandBack(ch, rt, "yielding: " + reason);
    }

    void OnWriteFailure(IFanChannel ch, Runtime rt, string why, DateTime now)
    {
        if (++rt.Failures < FailuresBeforeFault) return;
        rt.Failures = 0;
        rt.FaultReason = why;
        rt.FaultUntil = now + FaultRetry;
        Log.Error($"{ch.Id}: {FailuresBeforeFault} consecutive failures ({why}); handing back and pausing {FaultRetry.TotalSeconds:0} s");
        Alert?.Invoke($"{ChannelDefaults.DisplayName(_cfg, ch)}: repeated write failures ({why}). Handed back to firmware control; retrying in {FaultRetry.TotalSeconds:0} s.");
        if (rt.Controlled && !HandBack(ch, rt, "fault"))
        {
            try { if (ch.SetPercent(FallbackDutyPercent)) rt.LastApplied = FallbackDutyPercent; } catch { }
        }
    }

    // ---- identify pulse ---------------------------------------------------------------------------------------

    /// <summary>Drive one channel to 100% for a few seconds so the owner can find which fan it controls by sound or sight.</summary>
    public string? StartIdentify(string channelId, int seconds = 5) => StartHold(channelId, seconds, 100);

    /// <summary>Lowest duty the owner may test with a hold (and only on identified case-fan / cooler headers).</summary>
    public const double MinTestDuty = 20;

    /// <summary>
    /// Owner-started test: hold one header at <paramref name="duty"/> for a few seconds (identify pulse at 100%, or a
    /// minimum-duty check while the owner watches the fans). Durations are capped at 20 s; duties below 100% are only
    /// allowed on case-fan and CPU-cooler headers, never on pumps, AIOs or unidentified headers.
    /// </summary>
    public string? StartHold(string channelId, int seconds, double duty)
    {
        var ch = Channels.FirstOrDefault(c => c.Id == channelId);
        if (ch == null) return "unknown channel";
        var own = Ownership.Check(ResourceFor(ch));
        if (!own.MayWrite) { Log.Warn($"hold on {channelId} refused: {own.Reason}"); return own.Reason; }
        duty = Math.Clamp(duty, MinTestDuty, 100);
        if (duty < 100)
        {
            var cc = ConfigFor(ch);
            if (cc.Kind is not (FanKind.CaseFan or FanKind.CpuCooler) || ch.Resource != HwResource.SuperIo)
                return "a reduced-duty test is only offered for identified case-fan or CPU-cooler headers";
        }
        _identify[channelId] = (UtcNow().AddSeconds(Math.Clamp(seconds, 1, 20)), duty);
        Log.Info($"hold started on {channelId}: {duty:0}% for {seconds}s (owner action)");
        return null;
    }

    public double IdentifyRemaining(string channelId) =>
        _identify.TryGetValue(channelId, out var v) ? Math.Max(0, (v.until - UtcNow()).TotalSeconds) : 0;

    HashSet<string> ProcessIdentify(bool engineEnabled)
    {
        var active = new HashSet<string>();
        foreach (var kv in _identify.ToArray())
        {
            var ch = Channels.FirstOrDefault(c => c.Id == kv.Key);
            if (ch == null) { _identify.TryRemove(kv.Key, out _); continue; }
            var rt = RtFor(ch);
            var (until, duty) = kv.Value;
            if (UtcNow() < until && Ownership.Check(ResourceFor(ch)).MayWrite)
            {
                if (ch.SetPercent(duty)) { rt.Controlled = true; rt.LastApplied = duty; rt.LastSend = UtcNow(); rt.Current = duty; }
                Status[ch.Id] = new ChannelStatus { Id = ch.Id, TargetPercent = duty, AppliedPercent = duty, Controlled = true,
                    State = duty >= 100 ? "identify: 100% pulse" : $"owner test: holding {duty:0}%" };
                active.Add(ch.Id);
            }
            else
            {
                _identify.TryRemove(kv.Key, out _);
                var cc = ConfigFor(ch);
                bool keep = engineEnabled && cc.Mode != ChannelMode.Auto && FanSafety.Assess(cc, ch).Controllable && Ownership.Check(ResourceFor(ch)).MayWrite;
                if (keep) { rt.Current = kv.Value.duty; rt.LastApplied = kv.Value.duty; } // the curve continues from the held duty
                else HandBack(ch, rt, "identify finished");
                Log.Info($"hold finished on {ch.Id}");
            }
        }
        return active;
    }

    // ---- release, sleep, stall --------------------------------------------------------------------------------

    /// <summary>Hand every channel HwSuite drives back to firmware control (safe state).</summary>
    public void ReleaseAll() { lock (_tickGate) ReleaseAllLocked("release all"); }

    void ReleaseAllLocked(string why)
    {
        _identify.Clear();
        foreach (var ch in Channels)
        {
            try
            {
                var rt = RtFor(ch);
                if (rt.Controlled) HandBack(ch, rt, why);
                rt.EffectiveTemp = null; rt.FeedbackLost = false; rt.TachMissing = 0;
                rt.StartFailedUntil = DateTime.MinValue; rt.StartFailure = null;   // switching control off and on again retries at once
                rt.QuickReturns.Clear();
            }
            catch (Exception ex) { Log.Error("release " + ch.Id, ex); }
        }
        Log.Info($"All fan channels released to firmware control ({why})");
    }

    /// <summary>System is about to sleep: hand everything back and stop writing until resume.</summary>
    public void Suspend()
    {
        lock (_tickGate)
        {
            Suspended = true;
            ReleaseAllLocked("system suspend");
        }
    }

    /// <summary>System resumed: wait a grace period so chips and USB devices settle, then resume control.</summary>
    public void Resume()
    {
        lock (_tickGate)
        {
            Suspended = false;
            _resumeAt = UtcNow() + ResumeGrace;
            _lastTick = UtcNow();
            foreach (var rt in _rt.Values) { rt.Current = null; rt.LastApplied = -1; rt.EffectiveTemp = null; rt.Failures = 0; rt.StartVerified = false; rt.StartDeadline = null; rt.KickUntil = DateTime.MinValue; rt.StartKicks = 0; }
        }
        Log.Info($"system resumed: fan control resumes in {ResumeGrace.TotalSeconds:0} s");
    }

    void StallCheck()
    {
        try
        {
            bool enabled; lock (_cfg.Sync) enabled = _cfg.FanControlEnabled;
            var now = UtcNow();
            if (!enabled || !_enabledLastTick || Suspended || now < _resumeAt || now - _lastTick <= StallLimit) return;
            if (Interlocked.Exchange(ref _stallReleasing, 1) == 1) return;   // a release attempt is already running
            try
            {
                Log.Error("telemetry loop stalled > 10 s: releasing fans to firmware control");
                _enabledLastTick = false;
                // Not under _tickGate: the stalled tick may hold it. Channel hand-back uses its own bounded lock.
                foreach (var ch in Channels)
                {
                    try { if (_rt.TryGetValue(ch.Id, out var rt) && rt.Controlled) HandBack(ch, rt, "telemetry stalled"); }
                    catch (Exception ex) { Log.Error("stall release " + ch.Id, ex); }
                }
            }
            finally { Interlocked.Exchange(ref _stallReleasing, 0); }
        }
        catch { }
    }

    public void Dispose()
    {
        _stallGuard.Dispose();
        ReleaseAll();
    }
}
