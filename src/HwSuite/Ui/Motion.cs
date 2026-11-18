using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace HwSuite.Ui;

/// <summary>
/// Shared motion system. Presentation only: nothing here feeds fan control, lighting output or safety decisions, and
/// raw values are always shown immediately. Every animation starts from the current value (interruptible) and uses
/// opacity or small transforms, never layout-changing properties. Reduced motion (Windows "Show animations" off)
/// turns transitions into instant changes.
/// </summary>
public static class Motion
{
    public static readonly Duration Hover = new(TimeSpan.FromMilliseconds(120));
    public static readonly Duration Press = new(TimeSpan.FromMilliseconds(100));
    public static readonly Duration Navigate = new(TimeSpan.FromMilliseconds(200));
    public static readonly Duration Expand = new(TimeSpan.FromMilliseconds(180));
    public static readonly Duration Value = new(TimeSpan.FromMilliseconds(160));
    public static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseInOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });

    static IEasingFunction Freeze(EasingFunctionBase e) { e.Freeze(); return e; }

    /// <summary>For tests and screenshots: force reduced motion regardless of the Windows setting.</summary>
    public static bool? ReducedOverride { get; set; }
    /// <summary>Windows "Show animations in Windows" off, or forced.</summary>
    public static bool Reduced => ReducedOverride ?? !SystemParameters.ClientAreaAnimation;

    /// <summary>Animate a double property from its current value (HandoffBehavior.SnapshotAndReplace: a new action interrupts cleanly).</summary>
    public static void To(UIElement target, DependencyProperty prop, double to, Duration? d = null, IEasingFunction? ease = null)
    {
        if (Reduced) { target.BeginAnimation(prop, null); target.SetValue(prop, to); return; }
        var a = new DoubleAnimation(to, d ?? Value) { EasingFunction = ease ?? EaseOut };
        target.BeginAnimation(prop, a, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Page entry: fade in and settle 6 px upwards. Interrupting it (fast navigation) restarts from the current state.</summary>
    public static void PageIn(FrameworkElement page)
    {
        var tt = page.RenderTransform as TranslateTransform;
        if (tt == null) { tt = new TranslateTransform(); page.RenderTransform = tt; }
        if (Reduced) { page.BeginAnimation(UIElement.OpacityProperty, null); page.Opacity = 1; tt.BeginAnimation(TranslateTransform.YProperty, null); tt.Y = 0; return; }
        page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, Navigate) { EasingFunction = EaseOut }, HandoffBehavior.SnapshotAndReplace);
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, Navigate) { EasingFunction = EaseOut }, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Content of an expanding section: fade in from slightly above.</summary>
    public static void RevealIn(FrameworkElement content)
    {
        if (Reduced) { content.Opacity = 1; return; }
        var tt = content.RenderTransform as TranslateTransform ?? new TranslateTransform();
        content.RenderTransform = tt;
        content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, Expand) { EasingFunction = EaseOut }, HandoffBehavior.SnapshotAndReplace);
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, 0, Expand) { EasingFunction = EaseOut }, HandoffBehavior.SnapshotAndReplace);
    }
}

/// <summary>
/// Runs periodic presentation work (status refresh timers) only while its window is shown: hidden to the tray or minimised
/// stops it, showing it again resumes it (and refreshes once). Fan control, lighting output, telemetry and the safety
/// monitors run on their own threads and are not affected.
/// </summary>
public static class UiActivity
{
    public static bool IsActive(FrameworkElement owner)
    {
        var w = owner as Window ?? Window.GetWindow(owner);
        return owner.IsVisible && (w == null || (w.IsVisible && w.WindowState != WindowState.Minimized));
    }

    public static void Bind(FrameworkElement owner, System.Windows.Threading.DispatcherTimer timer, Action? onResume = null)
    {
        Window? win = null;
        void Sync()
        {
            bool run = owner.IsLoaded && IsActive(owner);
            if (run == timer.IsEnabled) return;
            if (run) { timer.Start(); onResume?.Invoke(); } else timer.Stop();
        }
        void OnState(object? s, EventArgs e) => Sync();
        owner.IsVisibleChanged += (_, _) => Sync();
        owner.Loaded += (_, _) =>
        {
            if (win != null) win.StateChanged -= OnState;
            win = owner as Window ?? Window.GetWindow(owner);
            if (win != null) win.StateChanged += OnState;
            Sync();
        };
        owner.Unloaded += (_, _) => { if (win != null) win.StateChanged -= OnState; win = null; timer.Stop(); };
    }
}

/// <summary>
/// Presentation clock for previews (independent of device update rates: it never causes USB or SMBus traffic). Ticks on
/// WPF's render loop only while explicitly started and the owner is visible; stops itself when the window is minimized
/// or hidden. Under reduced motion it ticks at <see cref="ReducedHz"/>. Frame intervals are recorded for evidence.
/// </summary>
public sealed class PresentationTicker
{
    readonly Action<double> _tick;
    readonly FrameworkElement _owner;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Queue<double> _intervals = new();
    bool _subscribed, _wanted;
    double _due = -1, _lastFrame = -1;
    public double TargetHz { get; set; } = 60;
    public const double ReducedHz = 4;
    public long Frames { get; private set; }
    public bool Running => _subscribed;

    public PresentationTicker(FrameworkElement owner, Action<double> tick)
    {
        _owner = owner; _tick = tick;
        owner.IsVisibleChanged += (_, _) => Sync();
    }

    public void Start() { _wanted = true; Sync(); }
    public void Stop() { _wanted = false; Sync(); }

    /// <summary>Recent frame intervals in ms (bounded).</summary>
    public double[] IntervalsMs() { lock (_intervals) return _intervals.ToArray(); }
    public void ResetIntervals() { lock (_intervals) _intervals.Clear(); _lastFrame = -1; _due = -1; }

    void Sync()
    {
        var win = Window.GetWindow(_owner);
        bool visible = _owner.IsVisible && (win == null || win.WindowState != WindowState.Minimized);
        bool want = _wanted && visible;
        if (want == _subscribed) return;
        if (want) CompositionTarget.Rendering += OnRendering; else CompositionTarget.Rendering -= OnRendering;
        _subscribed = want;
        if (win != null) { win.StateChanged -= OnWindowState; if (_wanted) win.StateChanged += OnWindowState; }
    }

    void OnWindowState(object? sender, EventArgs e) => Sync();

    void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double period = 1000.0 / (Motion.Reduced ? ReducedHz : TargetHz);
        // the render loop runs at the display's rate (60, 144 Hz...); fire on the tick nearest each due time so the
        // average stays at the target instead of rounding every period up to a whole number of display frames
        if (_due >= 0 && now < _due - 2) return;
        _due = _due < 0 || now - _due > period ? now + period : _due + period;
        if (_lastFrame >= 0) lock (_intervals) { _intervals.Enqueue(now - _lastFrame); if (_intervals.Count > 4096) _intervals.Dequeue(); }
        _lastFrame = now;
        Frames++;
        _tick(now / 1000.0);
    }
}
