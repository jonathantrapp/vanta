// PORTED from src\HwSuite\Ui\Motion.cs (Windows 1.20.0): rewritten for Avalonia (WPF Storyboards -> a small tween clock).
// Same rules: presentation only, every animation starts from the current value and is interruptible, reduced motion = instant.
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace HwSuite.Ui;

/// <summary>Durations used across the UI (WPF Duration stand-in).</summary>
public readonly record struct Duration(TimeSpan TimeSpan)
{
    public static implicit operator Duration(TimeSpan t) => new(t);
}

public interface IEasingFunction { double Ease(double t); }
sealed class CubicOut : IEasingFunction { public double Ease(double t) { t = 1 - t; return 1 - t * t * t; } }
sealed class CubicInOut : IEasingFunction { public double Ease(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }

/// <summary>
/// LINUX stand-in for WPF's CompositionTarget.Rendering: a ~60 Hz UI-thread tick that runs only while something listens.
/// </summary>
public static class CompositionTarget
{
    static readonly DispatcherTimer Timer = new(TimeSpan.FromMilliseconds(1000.0 / 60), DispatcherPriority.Render, (_, _) => Fire());
    static event EventHandler? _rendering;
    static int _count;
    static void Fire() { try { _rendering?.Invoke(null, EventArgs.Empty); } catch (Exception ex) { Core.Log.Error("render tick", ex); } }
    public static event EventHandler? Rendering
    {
        add { _rendering += value; if (++_count == 1) Timer.Start(); }
        remove { _rendering -= value; if (--_count <= 0) { _count = 0; Timer.Stop(); } }
    }
}

public static class Motion
{
    public static readonly Duration Hover = TimeSpan.FromMilliseconds(120);
    public static readonly Duration Press = TimeSpan.FromMilliseconds(100);
    public static readonly Duration Navigate = TimeSpan.FromMilliseconds(200);
    public static readonly Duration Expand = TimeSpan.FromMilliseconds(180);
    public static readonly Duration Value = TimeSpan.FromMilliseconds(160);
    public static readonly IEasingFunction EaseOut = new CubicOut();
    public static readonly IEasingFunction EaseInOut = new CubicInOut();

    public static bool? ReducedOverride { get; set; }
    /// <summary>GNOME "reduce animations" (org.gnome.desktop.interface enable-animations=false) via GTK_ENABLE_ANIMATIONS / env, or forced.</summary>
    public static bool Reduced => ReducedOverride ?? SystemReduced;
    static readonly bool SystemReduced = Environment.GetEnvironmentVariable("GTK_ENABLE_ANIMATIONS") == "0" || Environment.GetEnvironmentVariable("VANTA_REDUCED_MOTION") == "1";

    sealed class Tween { public double From, To; public Stopwatch Clock = Stopwatch.StartNew(); public double Ms; public IEasingFunction Ease = EaseOut; }
    static readonly Dictionary<(AvaloniaObject, AvaloniaProperty), Tween> Active = new();
    static bool _hooked;

    /// <summary>Animate a double property from its current value; a new call for the same property replaces the old one.</summary>
    public static void To(AvaloniaObject target, AvaloniaProperty<double> prop, double to, Duration? d = null, IEasingFunction? ease = null)
    {
        var key = (target, (AvaloniaProperty)prop);
        if (Reduced) { Active.Remove(key); target.SetValue(prop, to); return; }
        double from = (double)target.GetValue(prop)!;
        Active[key] = new Tween { From = from, To = to, Ms = (d ?? Value).TimeSpan.TotalMilliseconds, Ease = ease ?? EaseOut };
        if (!_hooked) { CompositionTarget.Rendering += Step; _hooked = true; }
    }

    static void Step(object? s, EventArgs e)
    {
        foreach (var (key, tw) in Active.ToList())
        {
            double t = Math.Clamp(tw.Clock.Elapsed.TotalMilliseconds / Math.Max(1, tw.Ms), 0, 1);
            key.Item1.SetValue(key.Item2, tw.From + (tw.To - tw.From) * tw.Ease.Ease(t));
            if (t >= 1) Active.Remove(key);
        }
        if (Active.Count == 0) { CompositionTarget.Rendering -= Step; _hooked = false; }
    }

    /// <summary>Page entry: fade in and settle 6 px upwards.</summary>
    public static void PageIn(Control page) => Enter(page, 6, Navigate);
    /// <summary>Content of an expanding section: fade in from slightly above.</summary>
    public static void RevealIn(Control content) => Enter(content, -4, Expand);

    static void Enter(Control c, double dy, Duration d)
    {
        var tt = c.RenderTransform as TranslateTransform;
        if (tt == null) { tt = new TranslateTransform(); c.RenderTransform = tt; }
        if (Reduced) { c.Opacity = 1; tt.Y = 0; return; }
        c.Opacity = 0; tt.Y = dy;
        To(c, Visual.OpacityProperty, 1, d);
        To(tt, TranslateTransform.YProperty, 0, d);
    }
}

/// <summary>Runs periodic presentation work only while its window is shown (not minimised or hidden to the tray).</summary>
public static class UiActivity
{
    public static bool IsActive(Control owner)
    {
        var w = owner as Window ?? TopLevel.GetTopLevel(owner) as Window;
        return owner.IsEffectivelyVisible && (w == null || (w.IsVisible && w.WindowState != WindowState.Minimized));
    }

    public static void Bind(Control owner, DispatcherTimer timer, Action? onResume = null)
    {
        Window? win = null;
        void Sync()
        {
            bool run = owner.IsLoaded && IsActive(owner);
            if (run == timer.IsEnabled) return;
            if (run) { timer.Start(); onResume?.Invoke(); } else timer.Stop();
        }
        void OnState(object? s, AvaloniaPropertyChangedEventArgs e) { if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty) Sync(); }
        owner.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) Sync(); };
        owner.Loaded += (_, _) =>
        {
            if (win != null) win.PropertyChanged -= OnState;
            win = owner as Window ?? TopLevel.GetTopLevel(owner) as Window;
            if (win != null) win.PropertyChanged += OnState;
            Sync();
        };
        owner.Unloaded += (_, _) => { if (win != null) win.PropertyChanged -= OnState; win = null; timer.Stop(); };
    }
}

/// <summary>Presentation clock for previews (never causes device traffic); ticks only while started and visible.</summary>
public sealed class PresentationTicker
{
    readonly Action<double> _tick;
    readonly Control _owner;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Queue<double> _intervals = new();
    bool _subscribed, _wanted;
    double _due = -1, _lastFrame = -1;
    Window? _win;
    public double TargetHz { get; set; } = 60;
    public const double ReducedHz = 4;
    public long Frames { get; private set; }
    public bool Running => _subscribed;

    public PresentationTicker(Control owner, Action<double> tick)
    {
        _owner = owner; _tick = tick;
        owner.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) Sync(); };
        owner.AttachedToVisualTree += (_, _) => Sync();
    }

    public void Start() { _wanted = true; Sync(); }
    public void Stop() { _wanted = false; Sync(); }
    public double[] IntervalsMs() { lock (_intervals) return _intervals.ToArray(); }
    public void ResetIntervals() { lock (_intervals) _intervals.Clear(); _lastFrame = -1; _due = -1; }

    void Sync()
    {
        var win = TopLevel.GetTopLevel(_owner) as Window;
        if (!ReferenceEquals(win, _win))
        {
            if (_win != null) _win.PropertyChanged -= OnWindowState;
            _win = win;
            if (_win != null) _win.PropertyChanged += OnWindowState;
        }
        bool visible = _owner.IsEffectivelyVisible && (win == null || win.WindowState != WindowState.Minimized);
        bool want = _wanted && visible;
        if (want == _subscribed) return;
        if (want) CompositionTarget.Rendering += OnRendering; else CompositionTarget.Rendering -= OnRendering;
        _subscribed = want;
    }

    void OnWindowState(object? s, AvaloniaPropertyChangedEventArgs e) { if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty) Sync(); }

    void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double period = 1000.0 / (Motion.Reduced ? ReducedHz : TargetHz);
        if (_due >= 0 && now < _due - 2) return;
        _due = _due < 0 || now - _due > period ? now + period : _due + period;
        if (_lastFrame >= 0) lock (_intervals) { _intervals.Enqueue(now - _lastFrame); if (_intervals.Count > 4096) _intervals.Dequeue(); }
        _lastFrame = now;
        Frames++;
        _tick(now / 1000.0);
    }
}
