using System.Diagnostics;
using System.Runtime.InteropServices;
using HwSuite.Core;
using HwSuite.Hardware;

namespace HwSuite.Lighting;

/// <summary>
/// Renders synchronized effects and hands each device its own frame. The engine renders at 60 FPS, or at the D_LED
/// strip's chosen rate when that is higher; every other device is offered frames at 60 per second exactly as before and
/// keeps its own limit (board 20/s, RAM 15/s; mouse and keyboard are settings-driven). Effects are functions of time,
/// so the update rate changes smoothness only, never animation speed or brightness. Waits use a high-resolution timer
/// (PrecisionTimer), so pacing does not depend on the system timer resolution. A device whose hardware another
/// program owns (IOwnership) receives nothing, not even init or "steady look" writes. While output is off the loop idles.
/// </summary>
public sealed class RgbEngine : IDisposable
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    public static readonly TimeSpan ResumeGrace = TimeSpan.FromSeconds(5);
    public const int StandardHz = 60;

    readonly AppConfig _config;
    readonly Func<double?> _thermalSource;
    readonly Thread _thread;
    readonly HashSet<RgbSink> _activated = new();
    readonly object _gate = new();
    readonly object _pacingGate = new();
    readonly Queue<double> _renderIntervals = new();
    readonly FrameCadence _standardCadence = new();
    readonly Dictionary<RgbSink, (Rgb[] Frame, double T, bool Enabled)> _lastPosted = new();
    volatile bool _stop;
    volatile bool _suspended;
    bool _sinksSuspended;
    DateTime _resumeAt = DateTime.MinValue;
    /// <summary>Longest the engine waits for a device transfer that was already running before it writes a steady look or sleeps.</summary>
    public static readonly TimeSpan InFlightWait = TimeSpan.FromMilliseconds(300);
    bool _active;
    bool _timerRaised;
    double? _smoothedTemp;
    double _lastThermalTime;
    double _lastRender = double.NaN;
    // acknowledgement bookkeeping: a new request generation whenever the look (or a device's admission) changes
    long _requestGen;
    (EffectKind, int, int, double, double, bool, long, double, double, bool)? _lookSignature;
    readonly Dictionary<RgbSink, bool> _wasEnabled = new();

    public List<RgbSink> Sinks { get; } = new();
    /// <summary>Self-test hook: each rendered frame with the time it was sampled at. Null in normal runs.</summary>
    internal static Action<RgbSink, EffectContext, Rgb[]>? FrameTap;
    // scene clock (EffectContext.Drift): advanced at the speed in force, so a speed change never jumps the scene
    double _drift, _driftTime = double.NaN;
    public double Fps { get; private set; }
    public long Ticks { get; private set; }
    /// <summary>Rate the engine is currently rendering at (60, or the strip's rate when higher).</summary>
    public int RenderHz { get; private set; } = StandardHz;
    public bool IsActive => _active;
    public IOwnership Ownership { get; set; } = AssumeOwned.Instance;
    /// <summary>
    /// Spatial positions used for evaluating effects (the owner's device arrangement). Replaced as a whole when an edit is
    /// committed; identity until the owner arranges devices. Only position-based effects (Gradient Wave) can change.
    /// </summary>
    public LayoutTransform Layout { get; set; } = LayoutTransform.Identity;
    /// <summary>Recent render-to-render intervals in ms (engine pacing, not device acceptance).</summary>
    public double[] RenderIntervalsMs { get { lock (_pacingGate) return _renderIntervals.ToArray(); } }
    public void ResetPacingSamples() { lock (_pacingGate) { _renderIntervals.Clear(); _lastRender = double.NaN; } }

    public RgbEngine(AppConfig config, Func<double?> thermalSource)
    {
        _config = config;
        _thermalSource = thermalSource;
        _thread = new Thread(Loop) { IsBackground = true, Name = "RgbEngine", Priority = ThreadPriority.AboveNormal };
    }

    public void Start()
    {
        PrecisionTimer.KeepTimerResolutionWhenHidden();
        foreach (var s in Sinks) s.StartWorker();
        _thread.Start();
    }

    bool SinkEnabled(RgbSink s) => s switch
    {
        MotherboardSink => _config.Rgb.Motherboard,
        DLedSink => _config.Rgb.DLedStrip,
        MouseSink => _config.Rgb.Mouse,
        KeyboardSink => _config.Rgb.Keyboard,
        Al80Sink => _config.Rgb.Keyboard,
        RamSink => _config.Rgb.Ram,
        _ => s.EnabledByDefault
    };

    void RaiseTimer(bool on)
    {
        if (on == _timerRaised) return;
        if (on) timeBeginPeriod(1); else timeEndPeriod(1);
        _timerRaised = on;
    }

    /// <summary>Render rate for effects that do not change over time (Static, Stealth): frames are identical, so 10/s is enough.</summary>
    public const int StillHz = 10;

    /// <summary>StillHz for still effects; otherwise 60, or the strip rate when a strip-rate device is available, included and not owned by another program.</summary>
    int CurrentRenderHz()
    {
        if (_config.Rgb.Effect is EffectKind.Static or EffectKind.Stealth) return StillHz;
        int strip = _config.Rgb.EffectiveDLedHz;
        if (strip <= StandardHz) return StandardHz;
        foreach (var s in Sinks)
            if (s.FollowsStripRate && s.Available && SinkEnabled(s) && s.Blocked == null) return strip;
        return StandardHz;
    }

    void Loop()
    {
        ThreadCpu.Register("lighting engine");
        try
        {
            var sw = Stopwatch.StartNew();
            double next = 0, fpsStart = 0; int frames = 0;
            while (!_stop)
            {
                bool running = _config.Rgb.Enabled && !_suspended && DateTime.UtcNow >= _resumeAt;
                if (!running)
                {
                    lock (_gate) { if (_active && !_suspended) Deactivate(); }
                    RaiseTimer(false);
                    Fps = 0; frames = 0;
                    Thread.Sleep(50);
                    next = sw.Elapsed.TotalMilliseconds; fpsStart = next; _lastRender = double.NaN;
                    continue;
                }
                RaiseTimer(true);   // still useful for other waits in the process; frame pacing does not rely on it
                if (_sinksSuspended)
                {
                    // rendering restarts only after the resume grace period, so devices are re-admitted here (no burst at wake)
                    foreach (var s in Sinks) s.Resume();
                    _sinksSuspended = false;
                }
                int hz = CurrentRenderHz();
                RenderHz = hz;
                double frameMs = 1000.0 / hz;
                double now = sw.Elapsed.TotalMilliseconds;
                if (now < next) { PrecisionTimer.Wait(TimeSpan.FromMilliseconds(next - now)); continue; }
                next += frameMs;
                if (now - next > 200) next = now + frameMs; // fell behind: resync instead of bursting

                double t = sw.Elapsed.TotalSeconds;
                try { lock (_gate) RenderFrame(t); }
                catch (Exception ex) { Log.Error("RGB frame failed", ex); }

                double tMs = t * 1000;
                lock (_pacingGate)
                {
                    if (!double.IsNaN(_lastRender)) { _renderIntervals.Enqueue(tMs - _lastRender); if (_renderIntervals.Count > 8192) _renderIntervals.Dequeue(); }
                    _lastRender = tMs;
                }
                frames++; Ticks++;
                if (now - fpsStart >= 1000) { Fps = frames * 1000.0 / (now - fpsStart); frames = 0; fpsStart = now; }
            }
        }
        finally { RaiseTimer(false); }
    }

    void RenderFrame(double t)
    {
        var rgb = _config.Rgb;
        _active = true;

        // thermal source is low-pass filtered (about 3 s, independent of the render rate) so reactive colours never flicker
        var raw = _thermalSource();
        double thermalAlpha = 1 - Math.Exp(-Math.Max(0, t - _lastThermalTime) / 3.0);
        _lastThermalTime = t;
        if (raw is double r) _smoothedTemp = _smoothedTemp is double s ? s + (r - s) * thermalAlpha : r;
        bool standardFrame = _standardCadence.IsDue(t, StandardHz);
        bool still = rgb.Effect is EffectKind.Static or EffectKind.Stealth;
        var layout = Layout;
        var signature = (rgb.Effect, rgb.ColorA, rgb.ColorB, rgb.Brightness, rgb.Speed, rgb.RamOnboardEffects, layout.Version, rgb.Density, rgb.Glow, rgb.Lightning);
        if (_lookSignature != signature)
        {
            _lookSignature = signature;
            _requestGen++;
            foreach (var s in Sinks) s.Request(_requestGen);
        }

        var ctx = EffectContext.From(rgb, t, _smoothedTemp, drift: EffectContext.AdvanceDrift(ref _drift, ref _driftTime, t, rgb.Speed));

        foreach (var sink in Sinks)
        {
            if (!sink.Available) continue;
            if (!sink.FollowsStripRate && !standardFrame && RenderHz > StandardHz) continue;   // every other device keeps its 60/s offer cadence
            sink.Enabled = SinkEnabled(sink);
            var own = sink.Resource is HwResource res ? Ownership.Check(res) : OwnershipDecision.Owned;
            sink.Blocked = own.MayWrite ? null : own.Reason;
            if (!_wasEnabled.TryGetValue(sink, out var was) || was != sink.Enabled) { _wasEnabled[sink] = sink.Enabled; sink.Request(++_requestGen); }
            if (own.MayWrite && sink.Enabled && _activated.Add(sink))
            {
                sink.Request(++_requestGen);   // (re)admitted: only what it accepts from now on counts
                try { sink.Activate(); } catch (Exception ex) { Log.Error("Activate " + sink.Name, ex); }
            }
            if (!own.MayWrite) { _activated.Remove(sink); _lastPosted.Remove(sink); }   // re-run its init sequence when ownership returns
            var geometry = layout.Geometry(sink);
            var frame = new Rgb[sink.LedCount];
            // same timeline for every device; a slow transport samples it slightly ahead (RgbSink.LeadSeconds)
            var c = sink.LeadSeconds > 0 ? ctx.At(t + sink.LeadSeconds) : ctx;
            if (sink is RamSink && RamSink.StillCompanion(rgb) is Rgb companion) Array.Fill(frame, companion);   // Onboard mode keeps the memory still
            else Effects.Render(rgb.Effect, c, geometry, frame);
            FrameTap?.Invoke(sink, c, frame);
            // still effects: an identical frame is not posted again (the device worker stays asleep), except once a second
            // so a device whose last write failed still gets its retry
            if (still && _lastPosted.TryGetValue(sink, out var lp) && t - lp.T < sink.StillRepostSeconds && lp.Enabled == sink.Enabled && lp.Frame.AsSpan().SequenceEqual(frame)) continue;
            _lastPosted[sink] = (frame, t, sink.Enabled);
            sink.FrameTime = c.Time;
            sink.Post(frame);
        }
    }

    /// <summary>Cancel every device's queued work and wait (bounded) for transfers that were already running.</summary>
    void CancelQueued(IEnumerable<RgbSink> sinks, string reason)
    {
        var running = sinks.Where(s => s.Invalidate(reason)).ToList();
        var deadline = DateTime.UtcNow + InFlightWait;
        foreach (var s in running)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !s.WaitIdle(left)) Log.Warn($"{s.Name}: a transfer started before '{reason}' was still running after {InFlightWait.TotalMilliseconds:0} ms");
        }
    }

    /// <summary>
    /// What streamed devices are left showing when output stops or the app exits: the primary colour, or black while Lights
    /// out (Stealth) is on, so quitting VANTA with the lights out does not switch the RAM, strip and board LEDs back on.
    /// </summary>
    public static Rgb SteadyLook(RgbConfig c) => c.Effect == EffectKind.Stealth ? Rgb.Black : Rgb.FromInt(c.SteadyColor);

    /// <param name="exiting">
    /// App exit (not just output off): the mouse and keyboard also get a steady look instead of keeping their onboard
    /// animation, so everything is solid after VANTA quits (owner's choice, 2026-10-15; one stored write each at most).
    /// </param>
    void Deactivate(bool exiting = false)
    {
        CancelQueued(Sinks, exiting ? "app exit" : "output stopped");
        var steady = SteadyLook(_config.Rgb);
        foreach (var s in Sinks.Where(x => x.Available && _activated.Contains(x)))
        {
            if (s.Resource is HwResource res && !Ownership.Check(res).MayWrite) continue;   // never write while another program owns it
            try { s.Deactivate(steady); } catch (Exception ex) { Log.Error("Deactivate " + s.Name, ex); }
        }
        foreach (var s in Sinks.OfType<MouseSink>())
            try { if (exiting) s.DeactivateForExit(); else s.Deactivate(steady); } catch (Exception ex) { Log.Error("Deactivate mouse", ex); }
        foreach (var s in Sinks.OfType<KeyboardSink>())
            try { if (exiting) s.DeactivateForExit(); else s.Deactivate(steady); } catch (Exception ex) { Log.Error("Deactivate keyboard", ex); }
        _activated.Clear();
        _lastPosted.Clear();
        _wasEnabled.Clear();
        _active = false;
    }

    /// <summary>System is going to sleep: stop sending (no steady-look writes into a suspending bus).</summary>
    public void Suspend()
    {
        _suspended = true;
        foreach (var s in Sinks) s.Suspend();   // cancels queued frames and pending peripheral settings
        CancelQueued(Sinks, "system suspend");
        lock (_gate) { _activated.Clear(); _lastPosted.Clear(); _active = false; _sinksSuspended = true; }
    }

    /// <summary>Resumed: wait for USB and SMBus devices to settle, then re-run every device's init sequence.</summary>
    public void Resume()
    {
        lock (_gate) { _activated.Clear(); _lastPosted.Clear(); _resumeAt = DateTime.UtcNow + ResumeGrace; }
        _suspended = false;
    }

    public void Dispose()
    {
        _stop = true;
        try { _thread.Join(1000); } catch { }
        lock (_gate) { if (_active) Deactivate(exiting: true); }
        foreach (var s in Sinks) s.Dispose();
    }
}
