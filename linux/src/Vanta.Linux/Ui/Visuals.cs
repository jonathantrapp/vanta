// PORTED from src\HwSuite\Ui\Visuals.cs (Windows 1.20.0): WPF -> Avalonia 11 (mechanical pass by linux/tools/wpf2avalonia.py, then hand fixes).
using System.Globalization;
using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;

namespace HwSuite.Ui;

/// <summary>
/// Last-60-seconds trace drawn from recorded samples by their timestamps: thin line with a faint fill, auto-ranged with a
/// minimum span so noise stays flat. Missing readings and gaps in time break the line instead of being bridged.
/// </summary>
public sealed class Sparkline : Control
{
    List<(DateTime t, double? v)> _points = new();
    DateTime _now = DateTime.Now;
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(60);
    public double MinSpan { get; set; } = 10;
    /// <summary>Theme token of the line ("Series1"); resolved at render time.</summary>
    public string StrokeToken { get; set; } = "Series1";

    public void SetPoints(IEnumerable<(DateTime t, double? v)> points, DateTime now)
    {
        _points = points.ToList(); _now = now;
        InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 10 || h < 6) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        dc.DrawLine(new Pen(UiBrushes.Hairline, 1), new Point(0, h - 0.5), new Point(w, h - 0.5));
        var valid = _points.Where(p => p.v.HasValue).ToList();
        if (valid.Count < 2) return;
        var stroke = Themes.Brush(StrokeToken);
        double lo = valid.Min(p => p.v!.Value), hi = valid.Max(p => p.v!.Value);
        if (hi - lo < MinSpan) { double mid = (hi + lo) / 2; lo = mid - MinSpan / 2; hi = mid + MinSpan / 2; }
        Point P(DateTime t, double v) => new(w - (_now - t).TotalSeconds / Window.TotalSeconds * w, 1.5 + (hi - v) / (hi - lo) * (h - 3));
        var fillBrush = new SolidColorBrush(((ISolidColorBrush)stroke).Color, 0.12); var pen = new Pen(stroke, 1.5) { LineJoin = PenLineJoin.Round };
        foreach (var run in HistoryChart.Runs(_points, TimeSpan.FromSeconds(3.5)))
        {
            if (run.Count < 2) { dc.DrawEllipse(stroke, null, P(run[0].t, run[0].v), 1.4, 1.4); continue; }
            var line = new StreamGeometry();
            using (var c = line.Open())
            {
                c.BeginFigure(P(run[0].t, run[0].v), false, false);
                foreach (var p in run.Skip(1)) c.LineTo(P(p.t, p.v), true, false);
            }
            var fill = new StreamGeometry();
            using (var c = fill.Open())
            {
                c.BeginFigure(new Point(P(run[0].t, run[0].v).X, h), true, true);
                foreach (var p in run) c.LineTo(P(p.t, p.v), true, false);
                c.LineTo(new Point(P(run[^1].t, run[^1].v).X, h), true, false);
            }
            dc.DrawGeometry(fillBrush, null, fill);
            dc.DrawGeometry(null, pen, line);
        }
    }
}

/// <summary>
/// Temperature history over real time (default 5 minutes, recorded app-wide). The x axis is wall-clock minutes before
/// now; missing readings and time gaps break the lines. While history is shorter than the window, the uncovered part is
/// a quiet hatched band with a short "collecting" note instead of an empty plot.
/// </summary>
public sealed class HistoryChart : Control
{
    public sealed record Series(string Name, string Token, Func<HistoryPoint, double?> Value);
    public List<Series> SeriesList { get; } = new();
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);
    public double MinY { get; set; } = 30;
    public double MaxY { get; set; } = 70;
    List<HistoryPoint> _points = new();
    DateTime _now = DateTime.Now;
    DateTime? _started;

    public void SetData(List<HistoryPoint> points, DateTime now, DateTime? started)
    {
        _points = points; _now = now; _started = started;
        InvalidateVisual();
    }

    /// <summary>Contiguous runs of available values; a missing value or a time gap longer than <paramref name="maxGap"/> starts a new run.</summary>
    public static List<List<(DateTime t, double v)>> Runs(IEnumerable<(DateTime t, double? v)> pts, TimeSpan maxGap)
    {
        var runs = new List<List<(DateTime t, double v)>>();
        List<(DateTime t, double v)>? cur = null;
        DateTime? prev = null;
        foreach (var (t, v) in pts)
        {
            bool gap = prev is DateTime pt && t - pt > maxGap;
            prev = t;
            if (v is not double d) { cur = null; continue; }
            if (gap) cur = null;
            if (cur == null) { cur = new(); runs.Add(cur); }
            cur.Add((t, d));
        }
        return runs;
    }

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 80 || h < 60) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        const double left = 36, top = 8, bottom = 24, right = 10;
        var plot = new Rect(left, top, w - left - right, h - top - bottom);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        double lo = MinY, hi = MaxY;
        foreach (var s in SeriesList)
            foreach (var p in _points) if (s.Value(p) is double d) { lo = Math.Min(lo, Math.Floor(d / 10) * 10); hi = Math.Max(hi, Math.Ceiling(d / 10) * 10); }
        if (hi - lo < 20) hi = lo + 20;

        var grid = new Pen(UiBrushes.Grid, 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(plot.Top + plot.Height * i / 4.0) + 0.5;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Fx.Text((hi - (hi - lo) * i / 4.0).ToString("0", CultureInfo.InvariantCulture) + "°", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiBrushes.Ui, 12, UiBrushes.Muted, dpi);
            dc.DrawText(ft, new Point(plot.Left - 8 - ft.Width, y - ft.Height / 2));
        }
        int mins = Math.Max(1, (int)Math.Round(Window.TotalMinutes));
        for (int m = 0; m <= mins; m++)
        {
            double x = Math.Round(plot.Right - plot.Width * m / mins) + 0.5;
            dc.DrawLine(grid, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 4));
            var ft = Fx.Text(m == 0 ? "now" : $"−{m} min", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiBrushes.Ui, 12, UiBrushes.Muted, dpi);
            double tx = Math.Clamp(x - ft.Width / 2, plot.Left, plot.Right - ft.Width);
            dc.DrawText(ft, new Point(tx, plot.Bottom + 6));
        }

        double X(DateTime t) => plot.Right - (_now - t).TotalSeconds / Window.TotalSeconds * plot.Width;
        double Y(double v) => plot.Bottom - (Math.Clamp(v, lo, hi) - lo) / (hi - lo) * plot.Height;

        // warm-up: a compact hatched band over the part of the window not yet covered, with one short line of text
        var covered = _started is DateTime st ? _now - st : TimeSpan.Zero;
        if (covered < Window)
        {
            double edge = Math.Clamp(X(_now - covered), plot.Left, plot.Right);
            if (edge - plot.Left > 4)
            {
                var band = new Rect(plot.Left, plot.Top, edge - plot.Left, plot.Height);
                dc.PushClip(new RectangleGeometry(band));
                var hatch = new Pen(UiBrushes.Grid, 1);
                for (double x = band.Left - band.Height; x < band.Right; x += 10) dc.DrawLine(hatch, new Point(x, band.Bottom), new Point(x + band.Height, band.Top));
                dc.Pop();
                var msg = _points.Count == 0 ? "Waiting for readings" : $"Collecting · {(int)covered.TotalMinutes}:{covered.Seconds:00} of {(int)Window.TotalMinutes}:00";
                var ft = Fx.Text(msg, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiBrushes.Ui, 12, UiBrushes.Muted, dpi);
                if (band.Width > ft.Width + 24)
                {
                    var pill = new Rect(band.Left + (band.Width - ft.Width) / 2 - 10, plot.Top + 10, ft.Width + 20, ft.Height + 8);
                    dc.DrawRoundedRectangle(UiBrushes.Panel, new Pen(UiBrushes.GridStrong, 1), pill, 5, 5);
                    dc.DrawText(ft, new Point(pill.Left + 10, pill.Top + 4));
                }
                double ex = Math.Round(edge) + 0.5;
                dc.DrawLine(new Pen(UiBrushes.GridStrong, 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) }, new Point(ex, plot.Top), new Point(ex, plot.Bottom));
            }
        }

        dc.PushClip(new RectangleGeometry(plot));
        foreach (var s in SeriesList)
        {
            var stroke = Themes.Brush(s.Token);
            var pen = new Pen(stroke, 1.75) { LineJoin = PenLineJoin.Round };
            foreach (var run in Runs(_points.Select(p => (p.Time, s.Value(p))), TimeSpan.FromSeconds(3.5)))
            {
                if (run.Count == 1) { dc.DrawEllipse(stroke, null, new Point(X(run[0].t), Y(run[0].v)), 1.5, 1.5); continue; }
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(X(run[0].t), Y(run[0].v)), false, false);
                    foreach (var p in run.Skip(1)) c.LineTo(new Point(X(p.t), Y(p.v)), true, false);
                }
                dc.DrawGeometry(null, pen, g);
            }
            if (_points.Count > 0 && s.Value(_points[^1]) is double last)
                dc.DrawEllipse(stroke, new Pen(UiBrushes.Panel, 2), new Point(X(_points[^1].Time), Y(last)), 3.5, 3.5);
        }
        dc.Pop();
    }
}

/// <summary>
/// Hardware preview: one group per lighting device, laid out like the hardware where that is verified, with the device's
/// state written under it. Colours are the frames VANTA offers each device (or the onboard setting for devices that run
/// their own effect), never a camera image, and never the theme's accent. Honest by construction:
///  - case (1.15): the front fans, rear fan and logo drawn by the owner's case layout (CaseLayout splits the D_LED chain,
///    each fan shows its own LEDs; the card says when the split is assumed), with the chain itself LED by LED beneath;
///  - memory: one stick per detected ENE controller, in detection order (which slot each sits in is not known);
///  - keyboard: the verified key map (vendor KB.ini positions); per-key colours when live frames run, else one colour;
///  - mouse: one LED.
/// </summary>
public sealed class LightingVisualizer : Control
{
    public sealed class Part
    {
        public Rgb[] Leds = Array.Empty<Rgb>();
        public bool Lit;
        public string State = "";
        public Tone Tone;
    }

    public Part Strip { get; } = new();
    public Part Board { get; } = new();
    public Part Keyboard { get; } = new();
    /// <summary>1.19.0: which keyboard to draw, as its key map's LED count (87 = iLovbee i87, 84 = YUNZII AL80).</summary>
    public int KeyboardKeys { get; set; } = ILovbeeKeyMap.Keys.Length;
    public Part Mouse { get; } = new();
    public Rgb[][] Dimms { get; set; } = Array.Empty<Rgb[]>();
    /// <summary>1.15: what is on the D_LED chain and where (front fans, rear fan, logo), for drawing the case.</summary>
    public CaseLayout CaseLayout { get; set; } = CaseLayout.Default;
    public Part Ram { get; } = new();
    /// <summary>Output is off (or a fixture): the colours are a local simulation of the effect; nothing is being sent.</summary>
    public bool IsPreview { get; set; }

    /// <summary>Where the device groups are drawn. Not arranged and not arranging: the original fixed row.</summary>
    public DeviceArrangement Arrangement { get; set; } = new();
    /// <summary>Arrange mode: the whole canvas, outlines and the selected group's handles are drawn.</summary>
    public bool Arranging { get; set; }
    public DeviceRole? Selected { get; set; }
    /// <summary>Short per-group note shown under the name while arranging (for example "moves the wave", "preview only").</summary>
    public Dictionary<DeviceRole, string> ArrangeNotes { get; } = new();
    /// <summary>Caption at the bottom of the arrange canvas: how the current effect uses positions.</summary>
    public string AxisCaption { get; set; } = "Gradient Wave travels left → right";

    // last render's canvas mapping and device bounds (pixels), for hit testing and keyboard moves
    double _scale = 1, _ox, _oy;
    readonly Dictionary<DeviceRole, Rect> _hit = new();

    static readonly Dictionary<int, Brush> Cache = new();
    /// <summary>
    /// LED drive level (0..255, proportional to emitted light) to a screen value: the screen's values are gamma-encoded,
    /// so drawing the drive level directly makes dim LEDs look almost black while the real LEDs visibly glow. Drawn as
    /// emitted light (sRGB encoding of the linear level). Display only: the frames themselves are unchanged.
    /// </summary>
    static readonly byte[] EmittedToScreen = Enumerable.Range(0, 256).Select(v =>
    {
        double l = v / 255.0;
        double s = l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(s * 255), 0, 255);
    }).ToArray();

    public static Color ScreenColor(Rgb c) => Color.FromRgb(EmittedToScreen[c.R], EmittedToScreen[c.G], EmittedToScreen[c.B]);

    static IBrush Led(Rgb c)
    {
        int k = (c.R << 16) | (c.G << 8) | c.B;
        lock (Cache)
        {
            if (!Cache.TryGetValue(k, out var b))
            {
                if (Cache.Count > 4096) Cache.Clear();
                b = new SolidColorBrush(ScreenColor(c)); Cache[k] = b;
            }
            return b;
        }
    }
    static bool On(Rgb c) => (c.R | c.G | c.B) != 0;
    static IBrush LedOr(Rgb[] f, int i, bool lit) => lit && i < f.Length && On(f[i]) ? Led(f[i]) : UiBrushes.LedOff;
    static Rgb Mean(Rgb[] f)
    {
        if (f.Length == 0) return Rgb.Black;
        int r = 0, g = 0, b = 0;
        foreach (var c in f) { r += c.R; g += c.G; b += c.B; }
        return new Rgb((byte)(r / f.Length), (byte)(g / f.Length), (byte)(b / f.Length));
    }

    public void Redraw() => InvalidateVisual();

    readonly Dictionary<(string, double, int), FormattedText> _text = new();
    FormattedText T(string s, double size, IBrush brush, bool semibold, double dpi)
    {
        if (_text.Count > 128) _text.Clear();
        // brushes change with the theme and the state tone: they are part of the key via their colour
        int colour = brush is SolidColorBrush sb ? (sb.Color.A << 24 | sb.Color.R << 16 | sb.Color.G << 8 | sb.Color.B) : 0;
        var key = (s + (semibold ? "\u0001" : ""), size * 1000 + dpi, colour);
        if (!_text.TryGetValue(key, out var ft))
        {
            ft = Fx.Text(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, semibold ? UiBrushes.UiSemibold : UiBrushes.Ui, size, brush, dpi);
            _text[key] = ft;
        }
        return ft;
    }

    static IBrush ToneBrush(Tone t) => t switch
    {
        Tone.Ok => UiBrushes.Nominal, Tone.Warn => UiBrushes.Warn, Tone.Danger => UiBrushes.Danger, Tone.Accent => UiBrushes.Accent, Tone.Info => Themes.Brush("Info"), _ => UiBrushes.Muted
    };

    void Caption(DrawingContext dc, Rect slot, string name, Part p, double y, double dpi, string? note = null)
    {
        var a = T(name, 13, UiBrushes.Text, true, dpi);
        dc.DrawText(a, new Point(slot.Left + (slot.Width - a.Width) / 2, y));
        string second = note ?? p.State;
        if (second.Length == 0) return;
        var b = T(second, 12, note != null ? UiBrushes.Muted : ToneBrush(p.Tone), false, dpi);
        // the status may spill a little past a narrow slot (the mouse at compact sizes) rather than being cut off
        b.MaxTextWidth = Math.Max(40, Math.Min(slot.Width + 44, Bounds.Width - 8)); b.MaxLineCount = 1; b.Trimming = TextTrimming.CharacterEllipsis;
        double bx = slot.Left + (slot.Width - b.Width) / 2;
        dc.DrawText(b, new Point(Math.Clamp(bx, 2, Math.Max(2, Bounds.Width - b.Width - 2)), y + a.Height + 1));
    }

    static readonly (string name, double units)[] Wide =
    {
        ("Backspace", 2), ("Tab", 1.5), ("\\", 1.5), ("CapsLock", 1.75), ("Enter", 2.25), ("LShift", 2.25), ("RShift", 2.75),
        ("LCtrl", 1.25), ("LWin", 1.25), ("LAlt", 1.25), ("RAlt", 1.25), ("Fn", 1.25), ("App", 1.25), ("RCtrl", 1.25), ("Space", 6.25)
    };

    static string NameOf(DeviceRole r) => r switch
    {
        DeviceRole.Strip => "Case fans", DeviceRole.Ram => "Memory", DeviceRole.Board => "Board", DeviceRole.Keyboard => "Keyboard", _ => "Mouse"
    };

    Part PartOf(DeviceRole r) => r switch
    {
        DeviceRole.Strip => Strip, DeviceRole.Ram => Ram, DeviceRole.Board => Board, DeviceRole.Keyboard => Keyboard, _ => Mouse
    };

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        _hit.Clear();
        if (w < 300 || h < 140) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var outline = new Pen(UiBrushes.DeviceOutline, 1);
        if (!Arranging && !Arrangement.Arranged) { RenderRow(dc, w, h, dpi, outline); return; }
        RenderArranged(dc, w, h, dpi, outline);
    }

    /// <summary>The original fixed row (identical to 1.11), used until the owner arranges devices.</summary>
    void RenderRow(DrawingContext dc, double w, double h, double dpi, Pen outline)
    {
        double capH = 40, top = 4, bodyH = h - capH - top - 6, capY = top + bodyH + 8;
        // slots: case front, memory, board, keyboard, mouse
        double[] weight = { 1.5, 1.25, 1.0, 3.2, 0.8 };   // 1.15: the case shows four fans now
        double total = weight.Sum(), x0 = 0;
        var slots = new Rect[5];
        for (int i = 0; i < 5; i++) { double sw = w * weight[i] / total; slots[i] = new Rect(x0, top, sw, bodyH); x0 += sw; }
        var roles = new[] { DeviceRole.Strip, DeviceRole.Ram, DeviceRole.Board, DeviceRole.Keyboard, DeviceRole.Mouse };
        for (int i = 0; i < 5; i++)
        {
            DrawDevice(dc, roles[i], slots[i], outline, dpi);
            Caption(dc, slots[i], NameOf(roles[i]), PartOf(roles[i]), capY, dpi);
            _hit[roles[i]] = slots[i];
        }
    }

    /// <summary>Canvas mode: each group at its arranged place and turn. Arranging shows the whole canvas; otherwise the content is fitted.</summary>
    void RenderArranged(DrawingContext dc, double w, double h, double dpi, Pen outline)
    {
        double cw = DeviceArrangement.CanvasWidth, ch = DeviceArrangement.CanvasHeight;
        double l = 0, t = 0, r = cw, b = ch;
        if (!Arranging)
        {
            l = double.MaxValue; t = double.MaxValue; r = double.MinValue; b = double.MinValue;
            foreach (var g in DeviceArrangement.Groups)
            {
                var (bl, bt, bw, bh) = Arrangement.Bounds(g);
                l = Math.Min(l, bl); t = Math.Min(t, bt); r = Math.Max(r, bl + bw); b = Math.Max(b, bt + bh + DeviceArrangement.CaptionHeight);
            }
            l -= 6; t -= 4; r += 6; b += 2;
        }
        _scale = Math.Min(w / (r - l), h / (b - t));
        _ox = (w - (r - l) * _scale) / 2 - l * _scale;
        _oy = (h - (b - t) * _scale) / 2 - t * _scale;

        if (Arranging)
        {
            var area = new Rect(_ox, _oy, cw * _scale, ch * _scale);
            var dash = new Pen(UiBrushes.Hairline, 1) { DashStyle = DashStyles.Dash };
            dc.DrawRoundedRectangle(null, dash, area, 8, 8);
            var axis = T(AxisCaption, 12, UiBrushes.Dim, false, dpi);
            dc.DrawText(axis, new Point(area.Right - axis.Width - 10, area.Bottom - axis.Height - 6));
        }

        foreach (var g in DeviceArrangement.Groups.OrderBy(x => x == Selected ? 1 : 0))   // the selected group on top
        {
            var p = Arrangement.Get(g);
            var (uw, uh) = DeviceArrangement.Size(g);
            var centre = new Point(_ox + p.X * _scale, _oy + p.Y * _scale);
            var box = new Rect(centre.X - uw * _scale / 2, centre.Y - uh * _scale / 2, uw * _scale, uh * _scale);
            if (p.Rotation != 0) dc.PushTransform(new RotateTransform(p.Rotation, centre.X, centre.Y));
            DrawDevice(dc, g, box, outline, dpi);
            if (p.Rotation != 0) dc.Pop();
            var (rw, rh) = DeviceArrangement.RotatedSize(g, p.Rotation);
            var bounds = new Rect(centre.X - rw * _scale / 2, centre.Y - rh * _scale / 2, rw * _scale, rh * _scale);
            _hit[g] = bounds;
            var capSlot = new Rect(bounds.Left - 30, bounds.Top, bounds.Width + 60, bounds.Height);
            Caption(dc, capSlot, NameOf(g), PartOf(g), bounds.Bottom + 6, dpi, Arranging && ArrangeNotes.TryGetValue(g, out var n) ? n : null);
            if (Arranging)
            {
                var pad = bounds; pad.Inflate(6, 6);
                bool sel = g == Selected;
                var pen = sel ? new Pen(IsFocused ? UiBrushes.Focus : UiBrushes.Accent, IsFocused ? 2.5 : 2) : new Pen(UiBrushes.Hairline, 1) { DashStyle = DashStyles.Dash };
                dc.DrawRoundedRectangle(null, pen, pad, 6, 6);
                if (sel)
                    foreach (var c in new[] { pad.TopLeft, pad.TopRight, pad.BottomLeft, pad.BottomRight })
                        dc.DrawRectangle(UiBrushes.Accent, null, new Rect(c.X - 3.5, c.Y - 3.5, 7, 7));
            }
        }
    }

    /// <summary>Group under a point (topmost first), from the last render.</summary>
    public DeviceRole? HitTest(Point p)
    {
        if (Selected is DeviceRole s && _hit.TryGetValue(s, out var sr) && Inflate(sr).Contains(p)) return s;
        foreach (var g in DeviceArrangement.Groups.Reverse()) if (_hit.TryGetValue(g, out var r) && Inflate(r).Contains(p)) return g;
        return null;
    }
    static Rect Inflate(Rect r) { r.Inflate(6, 6); return r; }

    /// <summary>Canvas units per pixel of the last arranged render (for converting drags).</summary>
    public double UnitsPerPixel => _scale > 0 ? 1 / _scale : 1;

    void DrawDevice(DrawingContext dc, DeviceRole r, Rect s, Pen outline, double dpi)
    {
        switch (r)
        {
            case DeviceRole.Strip: DrawCase(dc, s, outline); break;
            case DeviceRole.Ram: DrawMemory(dc, s, outline, dpi); break;
            case DeviceRole.Board: DrawBoard(dc, s, outline); break;
            case DeviceRole.Keyboard: DrawKeyboard(dc, s, outline); break;
            default: DrawMouse(dc, s, outline); break;
        }
    }

    // ---- case: seen through the side glass, front panel on the left: the front fans stacked top to bottom, the rear fan
    // at the top of the back panel, the logo along the bottom front. Each fan shows its own LEDs (CaseLayout's split of
    // the D_LED chain); the ribbon below is the chain in order.
    (CaseLayout? layout, int n, IReadOnlyList<(CasePartKind Kind, int Start, int Count)> parts, CasePartKind[] fans) _partsFor = (null, -1, Array.Empty<(CasePartKind, int, int)>(), Array.Empty<CasePartKind>());
    static bool HasPart(IReadOnlyList<(CasePartKind Kind, int Start, int Count)> parts, CasePartKind k) { for (int i = 0; i < parts.Count; i++) if (parts[i].Kind == k) return true; return false; }
    static Rgb MeanOf(Rgb[] f, int start, int count)
    {
        int r = 0, g = 0, b = 0, n = 0;
        for (int i = start; i < start + count && i < f.Length; i++, n++) { r += f[i].R; g += f[i].G; b += f[i].B; }
        return n == 0 ? Rgb.Black : new Rgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }
    static readonly Dictionary<int, Brush> GlowCache = new();
    /// <summary>The soft light a fan's blades throw (its LEDs' mean colour, translucent); cached like the LED brushes.</summary>
    static IBrush Glow(Rgb c)
    {
        int key = (c.R << 16) | (c.G << 8) | c.B;
        lock (GlowCache)
        {
            if (!GlowCache.TryGetValue(key, out var b))
            {
                if (GlowCache.Count > 4096) GlowCache.Clear();
                var sc = ScreenColor(c);
                b = new SolidColorBrush(Color.FromArgb(80, sc.R, sc.G, sc.B)); GlowCache[key] = b;
            }
            return b;
        }
    }

    void DrawCase(DrawingContext dc, Rect s, Pen outline)
    {
        double top = s.Top, bodyH = s.Height;
        double ribbonH = 10;
        double panelH = bodyH - ribbonH - 10, panelW = Math.Min(s.Width - 12, panelH * 0.74);
        var panel = new Rect(s.Left + (s.Width - panelW) / 2, top, panelW, panelH);
        dc.DrawRoundedRectangle(UiBrushes.DeviceBody, outline, panel, 6, 6);
        // front mesh and rear wall
        var mesh = new Rect(panel.Left + 3, panel.Top + 3, panelW * 0.06, panelH - 6);
        dc.DrawRoundedRectangle(UiBrushes.Control, null, mesh, 2, 2);
        if (!ReferenceEquals(_partsFor.layout, CaseLayout) || _partsFor.n != Strip.Leds.Length) _partsFor = (CaseLayout, Strip.Leds.Length, CaseLayout.Allocate(Strip.Leds.Length), CaseLayout.Chain().Where(k => k != CasePartKind.Logo).ToArray());
        var parts = _partsFor.parts;
        double fanR = Math.Min(panelW * 0.17, panelH * 0.135);
        Point At(CasePartKind k) { var (x, y, _) = CaseLayout.Place(k); return new Point(panel.Left + panelW * x, panel.Top + panelH * (0.02 + 0.96 * y)); }
        // a splitter hub: every fan shows the same ring, the logo its first LEDs
        IEnumerable<(CasePartKind Kind, int Start, int Count)> drawn = parts;
        if (CaseLayout.Mirrored && parts.Count == 1)
        {
            var ringPart = parts[0];
            drawn = CaseLayout.Chain().Select(k => k == CasePartKind.Logo
                ? (k, 0, Math.Min(Math.Max(1, CaseLayout.LogoLeds > 0 ? CaseLayout.LogoLeds : 3), Strip.Leds.Length))
                : (k, ringPart.Start, ringPart.Count)).ToList();
        }
        foreach (var (kind, start, count) in drawn)
        {
            var mean = MeanOf(Strip.Leds, start, count);
            bool lit = Strip.Lit && On(mean);
            var c = At(kind);
            if (kind == CasePartKind.Logo)
            {
                double lw = panelW * 0.34, lh = Math.Max(5, panelH * 0.03);
                double lx = c.X - lw / 2, ly = panel.Bottom - lh - 5, cell = lw / count;
                for (int i = 0; i < count; i++)
                    dc.DrawRoundedRectangle(LedOr(Strip.Leds, start + i, Strip.Lit), null, new Rect(lx + i * cell, ly, Math.Max(1.5, cell + 0.3), lh), 1.5, 1.5);
                continue;
            }
            if (kind is CasePartKind.TopFront or CasePartKind.TopRear)
            {
                // a radiator fan under the roof, seen edge-on through the side glass: a slab with its LEDs in a row
                double sw = panelW * 0.2, sh = Math.Max(6, panelH * 0.035);
                var slab = new Rect(c.X - sw / 2, c.Y - sh / 2, sw, sh);
                if (lit) dc.DrawRoundedRectangle(Glow(mean), null, new Rect(slab.Left, slab.Bottom, sw, sh * 1.6), 2, 2);
                dc.DrawRoundedRectangle(UiBrushes.Control, outline, slab, 2, 2);
                double cw = sw / Math.Max(1, count);
                for (int i = 0; i < count; i++)
                    dc.DrawEllipse(LedOr(Strip.Leds, start + i, Strip.Lit), null, new Point(slab.Left + cw * (i + 0.5), slab.Top + sh / 2), Math.Min(cw * 0.35, sh * 0.35), Math.Min(cw * 0.35, sh * 0.35));
                continue;
            }
            // the blades glow with the fan's mean colour; its LEDs sit around the hub ring
            if (lit) dc.DrawEllipse(Glow(mean), null, c, fanR * 0.95, fanR * 0.95);
            dc.DrawEllipse(null, outline, c, fanR, fanR);
            for (int bl = 0; bl < 9; bl++)
            {
                double a = bl * Math.PI * 2 / 9;
                dc.DrawLine(outline, new Point(c.X + Math.Cos(a) * fanR * 0.38, c.Y + Math.Sin(a) * fanR * 0.38), new Point(c.X + Math.Cos(a + 0.55) * fanR * 0.9, c.Y + Math.Sin(a + 0.55) * fanR * 0.9));
            }
            dc.DrawEllipse(UiBrushes.Control, outline, c, fanR * 0.3, fanR * 0.3);
            double dot = Math.Max(1.6, Math.Min(fanR * 0.13, fanR * 2.6 / Math.Max(1, count)));
            for (int i = 0; i < count; i++)
            {
                double a = CaseLayout.LedAngle(i, count);
                var p = new Point(c.X + Math.Cos(a) * fanR * 0.68, c.Y + Math.Sin(a) * fanR * 0.68);
                dc.DrawEllipse(LedOr(Strip.Leds, start + i, Strip.Lit), null, p, dot, dot);
            }
        }
        // parts in the layout that got no LEDs (a short chain): drawn unlit
        foreach (var k in _partsFor.fans)
            if (!CaseLayout.Mirrored && !HasPart(parts, k) && k is not (CasePartKind.TopFront or CasePartKind.TopRear)) dc.DrawEllipse(null, new Pen(UiBrushes.LedOff, 1) { DashStyle = DashStyles.Dash }, At(k), fanR, fanR);
        // chain ribbon in chain order
        int n = Math.Max(1, Strip.Leds.Length);
        double rw = Math.Min(s.Width - 16, n * 7.0), cell2 = rw / n, rx = s.Left + (s.Width - rw) / 2, ry = top + panelH + 8;
        for (int i = 0; i < Strip.Leds.Length; i++)
            dc.DrawRoundedRectangle(LedOr(Strip.Leds, i, Strip.Lit), null, new Rect(rx + i * cell2 + 0.5, ry, Math.Max(1.5, cell2 - 1.5), ribbonH), 1.5, 1.5);
    }

    // ---- memory: one stick per detected controller (detection order; slots unknown)
    void DrawMemory(DrawingContext dc, Rect s, Pen outline, double dpi)
    {
        double top = s.Top, bodyH = s.Height;
        int n = Dimms.Length;
        if (n == 0)
        {
            var ft = T("no controllers", 12, UiBrushes.Muted, false, dpi);
            dc.DrawText(ft, new Point(s.Left + (s.Width - ft.Width) / 2, top + bodyH / 2 - ft.Height / 2));
            return;
        }
        double stickW = Math.Clamp(s.Width / (n * 1.9), 12, 26), gap = stickW * 0.7, total2 = n * stickW + (n - 1) * gap;
        double sh = Math.Min(bodyH - 6, stickW * 9);
        for (int d = 0; d < n; d++)
        {
            var leds = Dimms[d];
            var stick = new Rect(s.Left + (s.Width - total2) / 2 + d * (stickW + gap), top + (bodyH - sh) / 2, stickW, sh);
            dc.DrawRoundedRectangle(UiBrushes.DeviceBody, outline, stick, 3, 3);
            int count = Math.Max(1, leds.Length);
            double lbH = sh * 0.62, cellH = lbH / Math.Max(8, count), ledH = Math.Max(3, cellH - 2.5);
            for (int i = 0; i < leds.Length; i++)
            {
                // LED 0 at the top of the lit slots (observed on the owner's sticks, 2026-10-15)
                double y = stick.Top + 6 + lbH - (leds.Length - i) * cellH + (cellH - ledH);
                dc.DrawRoundedRectangle(LedOr(leds, i, Ram.Lit), null, new Rect(stick.Left + 3, y, stickW - 6, ledH), 1, 1);
            }
            for (int pin = 0; pin < 4; pin++) dc.DrawRectangle(UiBrushes.DeviceOutline, null, new Rect(stick.Left + 3 + pin * (stickW - 6) / 4, stick.Bottom - 6, Math.Max(1, (stickW - 6) / 4 - 2), 3));
        }
    }

    // ---- board accents: two zones on a board outline
    void DrawBoard(DrawingContext dc, Rect s, Pen outline)
    {
        double top = s.Top, bodyH = s.Height;
        double bw = Math.Min(s.Width - 24, bodyH * 0.95), bh = Math.Min(bodyH - 6, bw * 1.05);
        var board = new Rect(s.Left + (s.Width - bw) / 2, top + (bodyH - bh) / 2, bw, bh);
        dc.DrawRoundedRectangle(UiBrushes.DeviceBody, outline, board, 4, 4);
        dc.DrawRectangle(null, outline, new Rect(board.Left + bw * 0.38, board.Top + bh * 0.18, bw * 0.26, bh * 0.24));   // socket
        for (int z = 0; z < 2; z++)
        {
            var r = z == 0 ? new Rect(board.Left + bw * 0.08, board.Top + bh * 0.12, bw * 0.12, bh * 0.36) : new Rect(board.Left + bw * 0.12, board.Top + bh * 0.66, bw * 0.76, bh * 0.07);
            dc.DrawRoundedRectangle(LedOr(Board.Leds, z, Board.Lit), null, r, 2, 2);
        }
    }

    // ---- keyboard: verified key positions (KB.ini), standard key widths
    void DrawKeyboard(DrawingContext dc, Rect s, Pen outline)
    {
        double top = s.Top, bodyH = s.Height;
        var keys = KeyboardKeyMaps.For(KeyboardKeys) ?? KeyboardKeyMaps.For(ILovbeeKeyMap.Keys.Length)!;
        bool al80 = keys.Length == Al80KeyMap.Leds;
        double U = KeyboardKeyMaps.Unit(keys.Length), gap = U * 5 / 38.6, pad = U * 8 / 38.6;
        // i87: key centres with standard widths. AL80: QMK LED points (where the LED sits, rows 12.8 apart), so each key
        // spans halfway to its row neighbours, capped at its standard width; keys are as tall as the row pitch allows
        var tiles = al80 ? Al80Tiles(keys, U) : null;
        double KeyH = al80 ? 12.8 : U;
        double L(int i, KeyboardKeyMaps.KeyPos k) => tiles != null ? tiles[i].L : k.X - Units(k.Name) * U / 2;
        double R(int i, KeyboardKeyMaps.KeyPos k) => tiles != null ? tiles[i].R : k.X + Units(k.Name) * U / 2;
        var idx = Enumerable.Range(0, keys.Length).Where(i => keys[i].HasKey).ToArray();
        double minX = idx.Min(i => L(i, keys[i])), maxX = idx.Max(i => R(i, keys[i]));
        double minY = idx.Min(i => keys[i].Y) - KeyH / 2, maxY = idx.Max(i => keys[i].Y) + KeyH / 2;
        double sc = Math.Min((s.Width - 28) / (maxX - minX + 2 * pad), (bodyH - 4) / (maxY - minY + 2 * pad));
        double kw = (maxX - minX + 2 * pad) * sc, kh = (maxY - minY + 2 * pad) * sc;
        var body = new Rect(s.Left + (s.Width - kw) / 2, top + (bodyH - kh) / 2, kw, kh);
        dc.DrawRoundedRectangle(UiBrushes.DeviceBody, outline, body, 6, 6);
        bool perKey = Keyboard.Leds.Length == keys.Length;
        for (int i = 0; i < keys.Length; i++)
        {
            var k = keys[i];
            var c = perKey ? Keyboard.Leds[i] : Keyboard.Leds.Length > 0 ? Keyboard.Leds[0] : Rgb.Black;
            var brush = Keyboard.Lit && On(c) ? Led(c) : UiBrushes.LedOff;
            // AL80 LEDs 56, 59, 78-80 are in the firmware table but not fitted on the owner's board (none lit in the
            // 2026-11-12 test): nothing is drawn for them
            if (!k.HasKey) continue;
            double left = L(i, k) + gap / 2, kwid = Math.Max(1, R(i, k) - L(i, k) - gap), khei = KeyH - gap;
            var r = new Rect(body.Left + (left - minX + pad) * sc, body.Top + (k.Y - khei / 2 - minY + pad) * sc, kwid * sc, khei * sc);
            dc.DrawRoundedRectangle(brush, null, r, 1.5, 1.5);
        }
    }

    /// <summary>
    /// Self-test (1.19): draw the AL80 alone with a per-key test frame (a blue drop per column, orange for the key-less
    /// LEDs) to a PNG, and return how many clearly coloured pixels it has. Needs the app's theme resources (UI thread).
    /// </summary>
    public static int RenderAl80Check(string path)
    {
        var v = new LightingVisualizer { Width = 900, Height = 260, KeyboardKeys = Al80KeyMap.Leds };
        var leds = new Rgb[Al80KeyMap.Leds];
        for (int i = 0; i < leds.Length; i++)
        {
            var k = Al80KeyMap.Keys[i];
            leds[i] = !k.HasKey ? new Rgb(255, 120, 0) : new Rgb(20, (byte)(60 + 3 * k.Y), (byte)(120 + 2 * k.Y));
        }
        v.Keyboard.Leds = leds; v.Keyboard.Lit = true;
        v.Measure(new Size(900, 260)); v.Arrange(new Rect(0, 0, 900, 260)); v.UpdateLayout();
        using var rtb = new RenderTargetBitmap(new PixelSize(900, 260));
        using (var dc = rtb.CreateDrawingContext()) v.DrawKeyboard(dc, new Rect(0, 0, 900, 260), new Pen(Brushes.Gray, 1));
        rtb.Save(path);
        var px = rtb.ReadPixels();
        int n = 0;
        for (int i = 0; i < px.Length; i += 4) if (px[i] > 110 && px[i + 3] > 200) n++;   // blue channel lit
        return n;
    }

    /// <summary>Self-test (1.20): several AL80 frames drawn in a grid to one PNG (rows of <paramref name="cols"/>), for looking at effects.</summary>
    public static void RenderAl80Sheet(string path, IReadOnlyList<Rgb[]> frames, int cols)
    {
        const int w = 560, h = 170;
        int rows = (frames.Count + cols - 1) / cols;
        using var rtb = new RenderTargetBitmap(new PixelSize(cols * w, rows * h));
        using (var dc = rtb.CreateDrawingContext())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 22)), null, new Rect(0, 0, cols * w, rows * h));
            for (int i = 0; i < frames.Count; i++)
            {
                var v = new LightingVisualizer { KeyboardKeys = Al80KeyMap.Leds };
                v.Keyboard.Leds = frames[i]; v.Keyboard.Lit = true;
                v.DrawKeyboard(dc, new Rect(i % cols * w, i / cols * h, w, h), new Pen(Brushes.DimGray, 1));
            }
        }
        rtb.Save(path);
    }

    static (double L, double R)[]? _al80Tiles;

    /// <summary>AL80 key tiles in QMK units: halfway to the row neighbours' LEDs, at most the key's standard width.</summary>
    static (double L, double R)[] Al80Tiles(KeyboardKeyMaps.KeyPos[] keys, double U)
    {
        if (_al80Tiles != null) return _al80Tiles;
        var t = new (double L, double R)[keys.Length];
        foreach (var row in Enumerable.Range(0, keys.Length).Where(i => keys[i].HasKey).GroupBy(i => keys[i].Y))
        {
            var r = row.OrderBy(i => keys[i].X).ToArray();
            // the gap between two neighbouring LEDs is split in proportion to the keys' standard widths (LAlt | Space |
            // Fn come out about 1.25u | 6.25u | 1.25u); the row's first and last key get their standard width
            double Bound(int a, int b) { double wa = Units(keys[a].Name), wb = Units(keys[b].Name); return keys[a].X + (keys[b].X - keys[a].X) * wa / (wa + wb); }
            for (int j = 0; j < r.Length; j++)
            {
                double w = Units(keys[r[j]].Name) * U;
                double l = j > 0 ? Bound(r[j - 1], r[j]) : double.NaN, rr = j < r.Length - 1 ? Bound(r[j], r[j + 1]) : double.NaN;
                if (double.IsNaN(l)) l = double.IsNaN(rr) ? keys[r[j]].X - w / 2 : rr - w;
                if (double.IsNaN(rr)) rr = l + w;
                t[r[j]] = (l, rr);
            }
        }
        return _al80Tiles = t;
    }

    // ---- mouse
    void DrawMouse(DrawingContext dc, Rect s, Pen outline)
    {
        double top = s.Top, bodyH = s.Height;
        double mh = Math.Min(Math.Min(bodyH * 0.8, 96), s.Width / 0.58), mw = mh * 0.58;
        var m = new Rect(s.Left + (s.Width - mw) / 2, top + (bodyH - mh) / 2, mw, mh);
        dc.DrawRoundedRectangle(UiBrushes.DeviceBody, outline, m, mw / 2, mw / 2.4);
        dc.DrawLine(outline, new Point(m.Left + mw / 2, m.Top + 2), new Point(m.Left + mw / 2, m.Top + mh * 0.36));
        var c = Mouse.Leds.Length > 0 ? Mouse.Leds[0] : Rgb.Black;
        dc.DrawEllipse(Mouse.Lit && On(c) ? Led(c) : UiBrushes.LedOff, null, new Point(m.Left + mw / 2, m.Top + mh * 0.66), mw * 0.16, mw * 0.16);
    }

    static double Units(string name) { foreach (var (n, u) in Wide) if (n == name) return u; return 1; }
}
