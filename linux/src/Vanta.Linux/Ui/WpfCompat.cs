using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Media;

namespace HwSuite.Ui;

/// <summary>
/// LINUX: small WPF-shaped helpers over Avalonia so the ported UI code (src\HwSuite\Ui, built in C#) keeps its structure.
/// Only things with a direct Avalonia equivalent live here; behaviour differences are handled in the ported files.
/// </summary>
public static class WpfCompat
{
    /// <summary>WPF SetResourceReference: bind a property to a (theme) resource key so it follows theme switches.</summary>
    public static void SetResourceReference(this StyledElement el, AvaloniaProperty prop, string key)
        => el.Bind(prop, el.GetResourceObservable(key));

    /// <summary>WPF DrawingContext.DrawRoundedRectangle.</summary>
    public static void DrawRoundedRectangle(this DrawingContext dc, IBrush? brush, IPen? pen, Rect r, double rx, double ry)
        => dc.DrawRectangle(brush, pen, r, rx, ry);

    /// <summary>Make a property re-render (WPF FrameworkPropertyMetadataOptions.AffectsRender) and register it.</summary>
    public static StyledProperty<T> Register<TOwner, T>(string name, T defaultValue = default!, bool affectsRender = true, Action<TOwner>? changed = null)
        where TOwner : Control
    {
        var p = AvaloniaProperty.Register<TOwner, T>(name, defaultValue);
        p.Changed.AddClassHandler<TOwner>((o, _) => { if (affectsRender) o.InvalidateVisual(); changed?.Invoke(o); });
        return p;
    }

    public static Rect Inflate(this Rect r, double x, double y) => new(r.X - x, r.Y - y, r.Width + 2 * x, r.Height + 2 * y);

    // WPF DrawingContext Push*/Pop: Avalonia returns a state to dispose. These keep a per-context stack so the ported
    // Push...; ...; Pop() sequences work unchanged (instance overloads take Rect/Matrix, so these geometry/transform
    // overloads are picked only for the WPF-style calls).
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DrawingContext, Stack<IDisposable>> Pushed = new();
    static void Remember(DrawingContext dc, IDisposable state) => Pushed.GetOrCreateValue(dc).Push(state);
    public static void PushClip(this DrawingContext dc, RectangleGeometry g) => Remember(dc, dc.PushClip(g.Rect));
    public static void PushTransform(this DrawingContext dc, Transform t) => Remember(dc, dc.PushTransform(t.Value));
    public static void PushOpacityWpf(this DrawingContext dc, double o) => Remember(dc, dc.PushOpacity(o));
    public static void Pop(this DrawingContext dc) { if (Pushed.TryGetValue(dc, out var s) && s.Count > 0) s.Pop().Dispose(); }

    /// <summary>Pixels of a rendered bitmap as BGRA32 (premultiplied).</summary>
    public static byte[] ReadPixels(this Avalonia.Media.Imaging.Bitmap b)
    {
        int w = b.PixelSize.Width, h = b.PixelSize.Height;
        var px = new byte[w * h * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { b.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), px.Length, w * 4); }
        finally { handle.Free(); }
        return px;
    }

    public static void ScrollToVerticalOffset(this ScrollViewer s, double y) => s.Offset = new Vector(s.Offset.X, Math.Max(0, y));

    /// <summary>WPF MoveFocus(First): focus the first focusable control inside.</summary>
    public static void FocusFirst(this Control c)
    {
        foreach (var d in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(c))
            if (d is InputElement ie && ie.Focusable && ie.IsEffectivelyEnabled && ie.IsEffectivelyVisible) { ie.Focus(); return; }
    }

    /// <summary>WPF Window.GetWindow(element).</summary>
    public static Window? WindowOf(Visual v) => v as Window ?? TopLevel.GetTopLevel(v) as Window;

    /// <summary>WPF StreamGeometryContext.BeginFigure(point, isFilled, isClosed). Skia fills an open figure as closed.</summary>
    public static void BeginFigure(this StreamGeometryContext c, Point p, bool isFilled, bool isClosed) => c.BeginFigure(p, isFilled);
    /// <summary>WPF StreamGeometryContext.LineTo(point, isStroked, isSmoothJoin).</summary>
    public static void LineTo(this StreamGeometryContext c, Point p, bool isStroked, bool isSmoothJoin) => c.LineTo(p);

    /// <summary>WPF (p1 - p2).Length.</summary>
    public static double Length(this Point v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);

    /// <summary>WPF LinearGradientBrush(colour1, colour2, angle in degrees).</summary>
    public static LinearGradientBrush Linear(Color a, Color b, double angleDeg)
    {
        double r = angleDeg * Math.PI / 180, dx = Math.Cos(r) / 2, dy = Math.Sin(r) / 2;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5 - dx, 0.5 - dy, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5 + dx, 0.5 + dy, RelativeUnit.Relative),
            GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
        };
    }

    /// <summary>Run a nested UI loop until the task completes (WPF's blocking ShowDialog / MessageBox semantics).</summary>
    public static T Wait<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            using var cts = new CancellationTokenSource();
            task.ContinueWith(_ => cts.Cancel(), TaskScheduler.FromCurrentSynchronizationContext());
            Avalonia.Threading.Dispatcher.UIThread.MainLoop(cts.Token);
        }
        return task.Result;
    }
}

/// <summary>WPF Cursors.</summary>
public static class Cursors
{
    public static readonly Cursor Hand = new(StandardCursorType.Hand);
    public static readonly Cursor Arrow = new(StandardCursorType.Arrow);
    public static readonly Cursor SizeAll = new(StandardCursorType.SizeAll);
    public static readonly Cursor SizeWE = new(StandardCursorType.SizeWestEast);
    public static readonly Cursor SizeNS = new(StandardCursorType.SizeNorthSouth);
    public static readonly Cursor IBeam = new(StandardCursorType.Ibeam);
    public static readonly Cursor Wait = new(StandardCursorType.Wait);
}

/// <summary>
/// WPF-style modal window: DialogResult closes it, ShowDialog() blocks (nested loop) and returns DialogResult.
/// Ported dialogs derive from this instead of Window.
/// </summary>
public class DialogWindow : Window
{
    bool? _result;
    public Window? Owner { get; set; }
    public bool? DialogResult
    {
        get => _result;
        set { _result = value; Close(value); }
    }

    public DialogWindow()
    {
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.SetResourceReference(BackgroundProperty, "PopupBrush");
        this.SetResourceReference(ForegroundProperty, "TextBrush");
    }

    public bool? ShowDialog()
    {
        var owner = Owner ?? (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner == null || !owner.IsVisible) { Show(); return WpfCompat.Wait(WhenClosed()); }
        return WpfCompat.Wait(ShowDialog<bool?>(owner).ContinueWith(t => _result ?? t.Result, TaskScheduler.FromCurrentSynchronizationContext()));
    }

    Task<bool?> WhenClosed()
    {
        var tcs = new TaskCompletionSource<bool?>();
        Closed += (_, _) => tcs.TrySetResult(_result);
        return tcs.Task;
    }
}

/// <summary>WPF OpenFileDialog ("Name|*.a;*.b|..." filter), blocking like the original, through the desktop portal.</summary>
public sealed class OpenFileDialog
{
    public string Filter { get; set; } = "";
    public string Title { get; set; } = "Open";
    public string FileName { get; private set; } = "";

    public bool? ShowDialog(Window? owner = null)
    {
        owner ??= (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner == null) return false;
        var types = new List<Avalonia.Platform.Storage.FilePickerFileType>();
        var parts = Filter.Split('|');
        for (int i = 0; i + 1 < parts.Length; i += 2)
            types.Add(new(parts[i]) { Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries) });
        var files = WpfCompat.Wait(owner.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = Title, AllowMultiple = false, FileTypeFilter = types }));
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path == null) return false;
        FileName = path;
        return true;
    }
}

public static class RasterImageExtensions
{
    /// <summary>A decoded picture as an Avalonia bitmap (for Image.Source).</summary>
    public static Avalonia.Media.Imaging.WriteableBitmap ToAvalonia(this Core.RasterImage img)
    {
        var wb = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(img.PixelWidth, img.PixelHeight), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using var fb = wb.Lock();
        for (int y = 0; y < img.PixelHeight; y++)
            System.Runtime.InteropServices.Marshal.Copy(img.Bgra, y * img.PixelWidth * 4, fb.Address + y * fb.RowBytes, img.PixelWidth * 4);
        return wb;
    }
}

public enum MessageBoxButton { OK, OKCancel, YesNo, YesNoCancel }
public enum MessageBoxImage { None, Information, Warning, Error, Question }
public enum MessageBoxResult { None, OK, Cancel, Yes, No }

/// <summary>WPF MessageBox: a small themed dialog, blocking like the original.</summary>
public static class MessageBox
{
    public static MessageBoxResult Show(string text) => Show(null, text, "VANTA", MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption) => Show(null, text, caption, MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons) => Show(null, text, caption, buttons, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon) => Show(null, text, caption, buttons, icon);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        var dlg = new DialogWindow { Title = caption, Owner = owner, MaxWidth = 560 };
        var result = MessageBoxResult.None;
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 });
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        void Add(string label, MessageBoxResult r, bool primary = false)
        {
            var b = new Button { Content = label, MinWidth = 84, Classes = { primary ? "PrimaryBtn" : "Btn" } };
            b.Click += (_, _) => { result = r; dlg.DialogResult = r is MessageBoxResult.OK or MessageBoxResult.Yes; };
            row.Children.Add(b);
        }
        switch (buttons)
        {
            case MessageBoxButton.OK: Add("OK", MessageBoxResult.OK, true); break;
            case MessageBoxButton.OKCancel: Add("Cancel", MessageBoxResult.Cancel); Add("OK", MessageBoxResult.OK, true); break;
            case MessageBoxButton.YesNo: Add("No", MessageBoxResult.No); Add("Yes", MessageBoxResult.Yes, true); break;
            default: Add("Cancel", MessageBoxResult.Cancel); Add("No", MessageBoxResult.No); Add("Yes", MessageBoxResult.Yes, true); break;
        }
        panel.Children.Add(row);
        dlg.Content = panel;
        dlg.ShowDialog();
        return result == MessageBoxResult.None ? (buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel) : result;
    }
}

/// <summary>WPF FormattedText had a pixels-per-dip argument; Avalonia's does not.</summary>
public static class Fx
{
    public static FormattedText Text(string text, CultureInfo culture, FlowDirection flow, Typeface tf, double size, IBrush? brush, double pixelsPerDip = 1)
        => new(text, culture, flow, tf, size, brush);
}

/// <summary>WPF VisualTreeHelper.GetDpi(...).PixelsPerDip: Avalonia text layout is already in DIPs.</summary>
public static class VisualTreeHelper
{
    public readonly record struct Dpi(double PixelsPerDip, double DpiScaleX, double DpiScaleY);
    public static Dpi GetDpi(Visual v) { double s = TopLevel.GetTopLevel(v)?.RenderScaling ?? 1; return new(s, s, s); }
}

/// <summary>WPF DashStyles.</summary>
public static class DashStyles
{
    public static IDashStyle Dash => DashStyle.Dash;
    public static IDashStyle Dot => DashStyle.Dot;
    public static IDashStyle DashDot => DashStyle.DashDot;
}

/// <summary>Font families: the Windows build uses Segoe UI / Cascadia; Fedora has Adwaita / Cantarell / Noto / DejaVu.</summary>
public static class Fonts
{
    public const string Ui = "Inter, Adwaita Sans, Cantarell, Noto Sans, DejaVu Sans";
    public const string Display = "Inter Display, Inter, Adwaita Sans, Cantarell, Noto Sans";
    public const string Mono = "Cascadia Mono, Adwaita Mono, JetBrains Mono, Noto Sans Mono, DejaVu Sans Mono";
}
