using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HwSuite.Core;

namespace HwSuite.Ui;

/// <summary>
/// Theme brushes for custom-drawn controls, read at render time from the current theme (<see cref="Themes"/>). Custom
/// controls are invalidated when the theme changes, so nothing here is cached across themes. Device RGB colours never
/// come from here.
/// </summary>
static class UiBrushes
{
    public static Brush Accent => Themes.Brush("Accent");
    public static Brush AccentSoft => Themes.Brush("AccentSoft");
    public static Brush AccentSecondary => Themes.Brush("Series2");
    public static Brush Text => Themes.Brush("Text");
    public static Brush Muted => Themes.Brush("TextSecondary");
    public static Brush Dim => Themes.Brush("TextTertiary");
    public static Brush Grid => Themes.Brush("ChartGrid");
    public static Brush GridStrong => Themes.Brush("ChartGridStrong");
    public static Brush Track => Themes.Brush("Track");
    public static Brush Panel => Themes.Brush("Panel");
    public static Brush Popup => Themes.Brush("Popup");
    public static Brush Control => Themes.Brush("Control");
    public static Brush Hairline => Themes.Brush("Hairline");
    public static Brush Focus => Themes.Brush("Focus");
    public static Brush LedOff => Themes.Brush("LedOff");
    public static Brush DeviceBody => Themes.Brush("DeviceBody");
    public static Brush DeviceOutline => Themes.Brush("DeviceOutline");
    public static Brush Nominal => Themes.Brush("Ok");
    public static Brush Warn => Themes.Brush("Warn");
    public static Brush Danger => Themes.Brush("Danger");
    public static Brush Floor => Themes.Brush("FloorShade");
    public static Brush CurveFill => Themes.Brush("CurveFill");
    public static readonly Typeface Mono = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static readonly Typeface Ui = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static readonly Typeface UiSemibold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
}

/// <summary>Thin horizontal meter used for loads and fan duty.</summary>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MeterBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Null (default): the theme accent at render time.</summary>
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(MeterBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0..1</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    /// <summary>Move to a new reading with a short glide from the current position (presentation only; interruptible).</summary>
    public void AnimateTo(double v) => Motion.To(this, ValueProperty, Math.Clamp(v, 0, 1));
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    /// <summary>Draw no fill at all (a reading that is not available, as opposed to 0).</summary>
    public bool Missing { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = Math.Min(h / 2, 2);
        dc.DrawRoundedRectangle(UiBrushes.Track, null, new Rect(0, 0, w, h), r, r);
        if (Missing) return;
        double fw = Math.Clamp(Value, 0, 1) * w;
        if (fw > 0.5) dc.DrawRoundedRectangle(Fill ?? UiBrushes.Accent, null, new Rect(0, 0, fw, h), r, r);
    }
}

public sealed class ChartSeries
{
    public string Name { get; init; } = "";
    public Brush Stroke { get; init; } = Brushes.Gray;
    public List<double?> Values { get; } = new();
}

/// <summary>Rolling multi-series line chart (anti-aliased, fixed or auto Y range).</summary>
public sealed class LineChart : FrameworkElement
{
    public List<ChartSeries> Series { get; } = new();
    public int Capacity { get; set; } = 300;
    public double MinY { get; set; } = 20;
    public double MaxY { get; set; } = 100;
    public string Unit { get; set; } = "°C";

    public void Push(int series, double? value)
    {
        var s = Series[series];
        s.Values.Add(value);
        while (s.Values.Count > Capacity) s.Values.RemoveAt(0);
    }

    public void Redraw() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 50 || h < 40) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        const double left = 38, top = 8, bottom = 8, right = 8;
        var plot = new Rect(left, top, w - left - right, h - top - bottom);

        // dynamic range: widen if data leaves the preset window
        double lo = MinY, hi = MaxY;
        foreach (var s in Series)
            foreach (var v in s.Values) if (v is double d) { lo = Math.Min(lo, Math.Floor(d / 10) * 10); hi = Math.Max(hi, Math.Ceiling(d / 10) * 10); }
        if (hi - lo < 20) hi = lo + 20;

        var gridPen = new Pen(UiBrushes.Grid, 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(plot.Top + plot.Height * i / 4.0) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            double val = hi - (hi - lo) * i / 4.0;
            var ft = new FormattedText(val.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                UiBrushes.Mono, 10, UiBrushes.Muted, dpi);
            dc.DrawText(ft, new Point(plot.Left - 8 - ft.Width, y - ft.Height / 2));
        }

        foreach (var s in Series)
        {
            if (s.Values.Count < 2) continue;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool started = false;
                for (int i = 0; i < s.Values.Count; i++)
                {
                    if (s.Values[i] is not double v) { started = false; continue; }
                    double x = plot.Left + plot.Width * i / Math.Max(1, Capacity - 1) + plot.Width * (Capacity - s.Values.Count) / Math.Max(1, Capacity - 1);
                    double y = plot.Bottom - (Math.Clamp(v, lo, hi) - lo) / (hi - lo) * plot.Height;
                    if (!started) { ctx.BeginFigure(new Point(x, y), false, false); started = true; }
                    else ctx.LineTo(new Point(x, y), true, true);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, new Pen(s.Stroke, 1.25) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
        }
    }
}

/// <summary>
/// Fan curve editor: smooth monotone spline (the exact function the engine evaluates), draggable nodes,
/// double-click to add, right-click to delete, live temperature crosshair and minimum-duty shading.
/// The dragged node follows the pointer directly (no easing between pointer and node) and every move re-evaluates the
/// same curve the engine uses. Keyboard: Ctrl+Left/Right selects a node, arrows nudge it (Shift = 5), Delete removes,
/// Insert adds after it; Ctrl+Z / Ctrl+Y undo and redo whole gestures. Losing mouse capture ends a drag cleanly.
/// </summary>
public sealed class CurveEditor : FrameworkElement
{
    const double MinT = 20, MaxT = 100;
    const double PadL = 46, PadR = 18, PadT = 16, PadB = 32;

    FanCurve? _curve;
    int _hover = -1, _drag = -1, _sel = -1;
    readonly Stack<List<CurvePoint>> _undo = new(), _redo = new();
    // presentation only: the released node settles back to its resting size
    int _settle = -1;
    readonly System.Diagnostics.Stopwatch _settleClock = new();
    const double SettleMs = 140;

    public CurveEditor() { Focusable = true; FocusVisualStyle = null; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    List<CurvePoint> Snapshot() => _curve!.Points.Select(p => new CurvePoint(p.Temp, p.Percent)).ToList();
    void Checkpoint() { if (_curve == null) return; _undo.Push(Snapshot()); if (_undo.Count > 100) { var keep = _undo.Take(100).Reverse().ToList(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); } _redo.Clear(); }
    void Restore(List<CurvePoint> pts) { _curve!.Points.Clear(); _curve.Points.AddRange(pts); _sel = Math.Min(_sel, _curve.Points.Count - 1); InvalidateVisual(); CurveEdited?.Invoke(); }
    public void Undo() { if (_curve == null || _undo.Count == 0) return; _redo.Push(Snapshot()); Restore(_undo.Pop()); }
    public void Redo() { if (_curve == null || _redo.Count == 0) return; _undo.Push(Snapshot()); Restore(_redo.Pop()); }

    public double Floor { get; set; }
    public string? FloorLabel { get; set; }
    public double? CurrentTemp { get; set; }
    public double? CurrentDuty { get; set; }
    public bool IsEditable { get; set; } = true;
    /// <summary>The curve is saved but not driving the fan (firmware owns it): drawn dashed and labelled; nothing reads as applied.</summary>
    public bool PreviewOnly { get; set; }
    public string? PreviewLabel { get; set; }
    public event Action? CurveEdited;

    public void SetCurve(FanCurve? curve)
    {
        if (!ReferenceEquals(curve, _curve)) { _undo.Clear(); _redo.Clear(); _sel = -1; }
        _curve = curve;
        _curve?.Points.Sort((a, b) => a.Temp.CompareTo(b.Temp));
        _hover = _drag = -1;
        InvalidateVisual();
    }

    public void Redraw() => InvalidateVisual();

    Rect Plot => new(PadL, PadT, Math.Max(10, ActualWidth - PadL - PadR), Math.Max(10, ActualHeight - PadT - PadB));
    Point ToPixel(double t, double p) => new(Plot.Left + (t - MinT) / (MaxT - MinT) * Plot.Width, Plot.Bottom - p / 100.0 * Plot.Height);
    internal (double t, double p) FromPixel(Point pt) =>
        (MinT + (pt.X - Plot.Left) / Plot.Width * (MaxT - MinT), (Plot.Bottom - pt.Y) / Plot.Height * 100.0);

    int HitNode(Point pt)
    {
        if (_curve == null) return -1;
        for (int i = 0; i < _curve.Points.Count; i++)
        {
            var np = ToPixel(_curve.Points[i].Temp, _curve.Points[i].Percent);
            if ((np - pt).Length <= 11) return i;
        }
        return -1;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_curve == null || !IsEditable) return;
        Focus();
        var pos = e.GetPosition(this);
        int hit = HitNode(pos);
        if (e.ChangedButton == MouseButton.Right)
        {
            if (hit >= 0 && _curve.Points.Count > 2) { Checkpoint(); _curve.Points.RemoveAt(hit); _hover = -1; _sel = -1; InvalidateVisual(); CurveEdited?.Invoke(); }
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        if (hit >= 0) { Checkpoint(); _drag = hit; _sel = hit; CaptureMouse(); InvalidateVisual(); }
        else if (e.ClickCount == 2 && Plot.Contains(pos))
        {
            var (t, p) = FromPixel(pos);
            t = Math.Round(Math.Clamp(t, MinT, MaxT)); p = Math.Round(Math.Clamp(p, 0, 100));
            if (_curve.Points.All(x => Math.Abs(x.Temp - t) >= 1))
            {
                Checkpoint();
                var np = new CurvePoint(t, p);
                _curve.Points.Add(np);
                _curve.Points.Sort((a, b) => a.Temp.CompareTo(b.Temp));
                _sel = _curve.Points.IndexOf(np);
                InvalidateVisual(); CurveEdited?.Invoke();
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_curve == null) return;
        var pos = e.GetPosition(this);
        if (_drag >= 0) MoveDragged(pos);
        else
        {
            int h = HitNode(pos);
            if (h != _hover) { _hover = h; Cursor = h >= 0 && IsEditable ? Cursors.Hand : Cursors.Arrow; InvalidateVisual(); }
        }
    }

    /// <summary>The dragged node goes exactly where the pointer is (clamped between its neighbours); no smoothing.</summary>
    void MoveDragged(Point pos)
    {
        var (t, p) = FromPixel(pos);
        double lo = _drag > 0 ? _curve!.Points[_drag - 1].Temp + 1 : MinT;
        double hi = _drag < _curve!.Points.Count - 1 ? _curve.Points[_drag + 1].Temp - 1 : MaxT;
        _curve.Points[_drag].Temp = Math.Round(Math.Clamp(t, lo, hi));
        _curve.Points[_drag].Percent = Math.Round(Math.Clamp(p, 0, 100));
        InvalidateVisual();
        CurveEdited?.Invoke();
    }

    /// <summary>Tests and motion evidence: drive a drag through the same code path as the mouse (checkpoint, move, release).</summary>
    internal void SimulateDrag(int index, IEnumerable<Point> path, Action? afterEachStep = null)
    {
        if (_curve == null || index < 0 || index >= _curve.Points.Count) return;
        Checkpoint(); _drag = index; _sel = index;
        foreach (var p in path) { MoveDragged(p); afterEachStep?.Invoke(); }
        EndDrag();
    }

    internal Point NodePixel(int index) => ToPixel(_curve!.Points[index].Temp, _curve.Points[index].Percent);
    internal (double t, double p) NodeValue(int index) => (_curve!.Points[index].Temp, _curve.Points[index].Percent);

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag >= 0) EndDrag();
    }

    /// <summary>Capture can be lost without a mouse-up (Alt+Tab, a dialog, the window losing focus): end the drag cleanly.</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag >= 0) EndDrag();
    }

    void EndDrag()
    {
        int released = _drag;
        _drag = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
        // drop an empty gesture from the undo history (clicked a node without moving it)
        if (_undo.Count > 0 && _curve != null && _undo.Peek().Select(p => (p.Temp, p.Percent)).SequenceEqual(_curve.Points.Select(p => (p.Temp, p.Percent)))) _undo.Pop();
        StartSettle(released);
        InvalidateVisual();
    }

    void StartSettle(int index)
    {
        if (Motion.Reduced || index < 0) return;
        _settle = index;
        _settleClock.Restart();
        CompositionTarget.Rendering -= OnSettleFrame;
        CompositionTarget.Rendering += OnSettleFrame;
    }

    void OnSettleFrame(object? sender, EventArgs e)
    {
        if (_settleClock.Elapsed.TotalMilliseconds >= SettleMs) { CompositionTarget.Rendering -= OnSettleFrame; _settle = -1; }
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_curve == null || !IsEditable) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (ctrl && e.Key == Key.Z && !shift) { Undo(); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift))) { Redo(); e.Handled = true; return; }
        int n = _curve.Points.Count;
        if (ctrl && (e.Key == Key.Right || e.Key == Key.Left)) { _sel = _sel < 0 ? 0 : Math.Clamp(_sel + (e.Key == Key.Right ? 1 : -1), 0, n - 1); InvalidateVisual(); e.Handled = true; return; }
        if (_sel < 0 || _sel >= n) return;
        double step = shift ? 5 : 1;
        var pt = _curve.Points[_sel];
        switch (e.Key)
        {
            case Key.Left: case Key.Right:
            {
                double lo = _sel > 0 ? _curve.Points[_sel - 1].Temp + 1 : MinT, hi = _sel < n - 1 ? _curve.Points[_sel + 1].Temp - 1 : MaxT;
                Checkpoint(); pt.Temp = Math.Clamp(pt.Temp + (e.Key == Key.Right ? step : -step), lo, hi); break;
            }
            case Key.Up: case Key.Down:
                Checkpoint(); pt.Percent = Math.Clamp(pt.Percent + (e.Key == Key.Up ? step : -step), 0, 100); break;
            case Key.Delete:
                if (n <= 2) return;
                Checkpoint(); _curve.Points.RemoveAt(_sel); _sel = Math.Min(_sel, _curve.Points.Count - 1); break;
            case Key.Insert:
            {
                double t1 = pt.Temp, t2 = _sel < n - 1 ? _curve.Points[_sel + 1].Temp : MaxT;
                if (t2 - t1 < 2) return;
                double t = Math.Round((t1 + t2) / 2);
                Checkpoint(); var np = new CurvePoint(t, Math.Round(_curve.Evaluate(t)));
                _curve.Points.Insert(_sel + 1, np); _sel++; break;
            }
            default: return;
        }
        e.Handled = true;
        InvalidateVisual();
        CurveEdited?.Invoke();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_drag < 0 && _hover != -1) { _hover = -1; InvalidateVisual(); }
    }

    FormattedText Text(string s, double size, Brush brush, Typeface? face = null) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? UiBrushes.Mono, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 120 || ActualHeight < 120) return;
        var plot = Plot;
        dc.DrawRoundedRectangle(UiBrushes.Panel, new Pen(IsKeyboardFocused ? UiBrushes.Focus : UiBrushes.Hairline, IsKeyboardFocused ? 2 : 1),
            new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1), 6, 6);

        // grid + axis labels (12 px, secondary text colour)
        var gridPen = new Pen(UiBrushes.Grid, 1);
        var strongPen = new Pen(UiBrushes.GridStrong, 1);
        for (int p = 0; p <= 100; p += 20)
        {
            double y = Math.Round(ToPixel(MinT, p).Y) + 0.5;
            dc.DrawLine(p == 0 ? strongPen : gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Text($"{p}%", 12, UiBrushes.Muted);
            dc.DrawText(ft, new Point(plot.Left - 10 - ft.Width, y - ft.Height / 2));
        }
        for (int t = (int)MinT; t <= (int)MaxT; t += 10)
        {
            double x = Math.Round(ToPixel(t, 0).X) + 0.5;
            dc.DrawLine(t == (int)MinT ? strongPen : gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var ft = Text($"{t}", 12, UiBrushes.Muted);
            dc.DrawText(ft, new Point(x - ft.Width / 2, plot.Bottom + 8));
        }
        var unit = Text("°C", 12, UiBrushes.Muted);
        dc.DrawText(unit, new Point(plot.Left - 10 - unit.Width, plot.Bottom + 8));

        if (_curve == null || _curve.Points.Count == 0) return;
        if (PreviewOnly) dc.PushOpacity(0.72);

        // minimum duty shading
        if (Floor > 0.5)
        {
            var fy = ToPixel(MinT, Floor).Y;
            dc.DrawRectangle(UiBrushes.Floor, null, new Rect(plot.Left, fy, plot.Width, plot.Bottom - fy));
            dc.DrawLine(new Pen(UiBrushes.Muted, 1) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) }, new Point(plot.Left, fy), new Point(plot.Right, fy));
            var lbl = Text(FloorLabel ?? $"minimum {Floor:0}%", 12, UiBrushes.Muted, UiBrushes.Ui);
            dc.DrawText(lbl, new Point(plot.Left + 8, Math.Min(plot.Bottom - lbl.Height - 6, fy + 6)));
        }

        // spline (sampled from the same function the engine uses)
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var c1 = line.Open())
        using (var c2 = area.Open())
        {
            bool first = true;
            for (double px = plot.Left; px <= plot.Right + 0.01; px += 2)
            {
                double t = MinT + (px - plot.Left) / plot.Width * (MaxT - MinT);
                var pt = ToPixel(t, _curve.Evaluate(t));
                if (first) { c1.BeginFigure(pt, false, false); c2.BeginFigure(new Point(pt.X, plot.Bottom), true, true); c2.LineTo(pt, false, false); first = false; }
                else { c1.LineTo(pt, true, true); c2.LineTo(pt, false, false); }
            }
            c2.LineTo(new Point(plot.Right, plot.Bottom), false, false);
        }
        line.Freeze(); area.Freeze();
        var fill = new LinearGradientBrush(((SolidColorBrush)UiBrushes.CurveFill).Color, Color.FromArgb(0, ((SolidColorBrush)UiBrushes.CurveFill).Color.R, ((SolidColorBrush)UiBrushes.CurveFill).Color.G, ((SolidColorBrush)UiBrushes.CurveFill).Color.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        var curvePen = new Pen(UiBrushes.Accent, 2.2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (PreviewOnly) curvePen.DashStyle = new DashStyle(new double[] { 3, 2 }, 0);
        dc.DrawGeometry(null, curvePen, line);
        if (PreviewOnly) dc.Pop();

        // live crosshair
        if (CurrentTemp is double ct && ct >= MinT - 5 && ct <= MaxT + 5)
        {
            double cx = Math.Round(ToPixel(Math.Clamp(ct, MinT, MaxT), 0).X) + 0.5;
            var dash = new Pen(UiBrushes.Muted, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            dc.DrawLine(dash, new Point(cx, plot.Top), new Point(cx, plot.Bottom));
            double curveVal = _curve.Evaluate(ct);
            var onCurve = ToPixel(Math.Clamp(ct, MinT, MaxT), curveVal);
            if (!PreviewOnly) dc.DrawLine(dash, new Point(plot.Left, onCurve.Y), new Point(plot.Right, onCurve.Y));
            dc.DrawEllipse(PreviewOnly ? UiBrushes.Panel : UiBrushes.Text, new Pen(PreviewOnly ? UiBrushes.Muted : UiBrushes.Panel, 2), onCurve, 5, 5);
            if (CurrentDuty is double cd && !PreviewOnly)
            {
                var dy = ToPixel(MinT, Math.Clamp(cd, 0, 100)).Y;
                dc.DrawRectangle(UiBrushes.Text, null, new Rect(plot.Left - 6, dy - 1.5, 6, 3));
            }
            string lab = PreviewOnly ? $"{ct:0.0} °C now  ·  this curve would ask {curveVal:0}%  ·  not applied"
                : CurrentDuty is double dd ? $"{ct:0.0} °C  ·  curve {curveVal:0}%  ·  commanded {dd:0}%" : $"{ct:0.0} °C  ·  curve {curveVal:0}%";
            var ft = Text(lab, 12, UiBrushes.Text, UiBrushes.Ui);
            dc.DrawRoundedRectangle(UiBrushes.Popup, new Pen(UiBrushes.GridStrong, 1),
                new Rect(plot.Right - ft.Width - 18, plot.Top + 6, ft.Width + 14, ft.Height + 8), 5, 5);
            dc.DrawText(ft, new Point(plot.Right - ft.Width - 11, plot.Top + 10));
        }
        if (PreviewOnly && PreviewLabel is string pl)
        {
            var ft = Text(pl, 12, UiBrushes.Muted, UiBrushes.UiSemibold);
            dc.DrawRoundedRectangle(UiBrushes.Control, new Pen(UiBrushes.GridStrong, 1), new Rect(plot.Left + 6, plot.Top + 6, ft.Width + 16, ft.Height + 8), 5, 5);
            dc.DrawText(ft, new Point(plot.Left + 14, plot.Top + 10));
        }

        // nodes
        for (int i = 0; i < _curve.Points.Count; i++)
        {
            var np = ToPixel(_curve.Points[i].Temp, _curve.Points[i].Percent);
            bool active = i == _hover || i == _drag;
            double r = active ? 7 : 5.5;
            if (i == _settle && !active) r = 5.5 + 1.5 * Math.Max(0, 1 - _settleClock.Elapsed.TotalMilliseconds / SettleMs);
            if (i == _sel && IsKeyboardFocused) dc.DrawEllipse(null, new Pen(UiBrushes.Focus, 1.5) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) }, np, r + 4, r + 4);
            dc.DrawEllipse(UiBrushes.Panel, new Pen(active || i == _sel ? UiBrushes.Text : UiBrushes.Accent, 2), np, r, r);
            if (active || (i == _sel && IsKeyboardFocused))
            {
                var ft = Text($"{_curve.Points[i].Temp:0} °C  {_curve.Points[i].Percent:0} %", 12, UiBrushes.Text);
                double bx = Math.Min(np.X + 12, plot.Right - ft.Width - 12), by = Math.Max(np.Y - 34, plot.Top + 2);
                dc.DrawRoundedRectangle(UiBrushes.Popup, new Pen(UiBrushes.GridStrong, 1), new Rect(bx - 6, by - 3, ft.Width + 12, ft.Height + 6), 4, 4);
                dc.DrawText(ft, new Point(bx, by));
            }
        }
    }
}
