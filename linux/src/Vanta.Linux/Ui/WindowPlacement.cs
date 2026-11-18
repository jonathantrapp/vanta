// PORTED from src\HwSuite\Ui\WindowPlacement.cs (Windows 1.20.0): Win32 window/monitor calls -> Avalonia (Position, Screens).
// UiState, Record and Fit (the tested rules) are unchanged.
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using HwSuite.Core;

namespace HwSuite.Ui;

/// <summary>Remembered main-window state (ui-state.json beside config.json). Display only: nothing here touches hardware.</summary>
public sealed class UiState
{
    /// <summary>Normal (restored) window rectangle in screen pixels, and the DPI of the monitor it was on.</summary>
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Dpi { get; set; } = 96;
    public bool Maximized { get; set; }
    public bool HasBounds { get; set; }
    /// <summary>Last main page: dashboard, fans, lighting or system.</summary>
    public string? Page { get; set; }
}

/// <summary>One monitor's work area (screen pixels) and DPI.</summary>
public readonly record struct MonitorArea(int Left, int Top, int Right, int Bottom, double Dpi)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>Restores and saves the main window's size, position, maximized state and last page (see the Windows file).</summary>
public sealed class WindowPlacementKeeper
{
    public static readonly string[] Pages = { "dashboard", "fans", "lighting", "al80", "system" };
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(1500);
    static string PathName => Path.Combine(AppPaths.Data, "ui-state.json");

    readonly Window _window;
    readonly bool _enabled;
    readonly DispatcherTimer _debounce;
    UiState _state;
    bool _restored;
    public int Saves { get; private set; }

    public WindowPlacementKeeper(Window window, bool enabled)
    {
        _window = window;
        _enabled = enabled;
        _state = enabled ? Load() ?? new UiState() : new UiState();
        _debounce = new DispatcherTimer { Interval = SaveDelay };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Capture(); Save(); };
        if (!enabled) return;
        if (_state.HasBounds) window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Opened += (_, _) => Restore();
        window.PositionChanged += (_, _) => Schedule();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty || e.Property == Visual.BoundsProperty) Schedule();
            if (e.Property == Visual.IsVisibleProperty && !window.IsVisible) Flush();
        };
    }

    public string? SavedPage => _enabled && _state.Page is string p && Pages.Contains(p) ? p : null;

    public void PageShown(string page)
    {
        if (!_enabled || !Pages.Contains(page) || _state.Page == page) return;
        _state.Page = page;
        Schedule();
    }

    void Schedule() { if (_enabled && _restored) { _debounce.Stop(); _debounce.Start(); } }

    public void Flush()
    {
        if (!_enabled || !_restored) return;
        _debounce.Stop();
        Capture();
        Save();
    }

    void Capture()
    {
        double scale = _window.RenderScaling;
        (int, int, int, int)? rect = _window.WindowState == WindowState.Normal && _window.Bounds.Width > 0
            ? (_window.Position.X, _window.Position.Y, (int)Math.Round(_window.Bounds.Width * scale), (int)Math.Round(_window.Bounds.Height * scale)) : null;
        Record(_state, _window.WindowState, rect, rect != null ? 96 * scale : 0);
    }

    /// <summary>
    /// What a save records for the current window state. Minimised is not a state to come back to: it keeps the last
    /// normal-or-maximised choice and the last normal bounds (a save while minimised used to clear "maximised"). Pure (tested).
    /// </summary>
    public static void Record(UiState s, WindowState state, (int Left, int Top, int Width, int Height)? normalRect, double dpi)
    {
        if (state == WindowState.Minimized) return;
        s.Maximized = state == WindowState.Maximized;
        if (state == WindowState.Normal && normalRect is { } r)
        {
            s.Left = r.Left; s.Top = r.Top; s.Width = r.Width; s.Height = r.Height;
            s.Dpi = dpi; s.HasBounds = true;
        }
    }

    void Restore()
    {
        _restored = true;
        if (!_state.HasBounds) return;
        var fit = Fit(_state, Monitors(_window), _window.MinWidth, _window.MinHeight);
        if (fit is { } f)
        {
            var screen = _window.Screens.ScreenFromPoint(new PixelPoint(f.Left + f.Width / 2, f.Top + f.Height / 2));
            double k = screen?.Scaling ?? _window.RenderScaling;
            _window.Position = new PixelPoint(f.Left, f.Top);
            _window.Width = f.Width / k; _window.Height = f.Height / k;
        }
        if (_state.Maximized) _window.WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Where a saved rectangle goes on the monitors that exist now. Null when there is no monitor. Pure (tested with
    /// simulated monitor sets: removed monitor, DPI change, off-screen, larger than the work area).
    /// </summary>
    public static (int Left, int Top, int Width, int Height)? Fit(UiState s, IReadOnlyList<MonitorArea> monitors, double minWidthDip, double minHeightDip)
    {
        if (monitors.Count == 0 || s.Width <= 0 || s.Height <= 0) return null;
        double savedDpi = s.Dpi is > 0 and < 1000 ? s.Dpi : 96;
        double wDip = s.Width * 96 / savedDpi, hDip = s.Height * 96 / savedDpi;
        long Overlap(MonitorArea m) => (long)Math.Max(0, Math.Min(m.Right, s.Left + s.Width) - Math.Max(m.Left, s.Left)) * Math.Max(0, Math.Min(m.Bottom, s.Top + s.Height) - Math.Max(m.Top, s.Top));
        var best = monitors.OrderByDescending(Overlap).First();
        bool overlaps = Overlap(best) > 0;
        if (!overlaps)
        {
            double cx = s.Left + s.Width / 2.0, cy = s.Top + s.Height / 2.0;
            best = monitors.OrderBy(m => Math.Pow(Math.Clamp(cx, m.Left, m.Right) - cx, 2) + Math.Pow(Math.Clamp(cy, m.Top, m.Bottom) - cy, 2)).First();
        }
        double k = best.Dpi / 96;
        int w = (int)Math.Round(Math.Clamp(Math.Max(wDip, minWidthDip) * k, 1, best.Width));
        int h = (int)Math.Round(Math.Clamp(Math.Max(hDip, minHeightDip) * k, 1, best.Height));
        int left, top;
        if (overlaps)
        {
            left = Math.Clamp(s.Left, best.Left, best.Right - w);
            top = Math.Clamp(s.Top, best.Top, best.Bottom - h);
        }
        else
        {
            left = best.Left + (best.Width - w) / 2;
            top = best.Top + (best.Height - h) / 2;
        }
        return (left, top, w, h);
    }

    static UiState? Load()
    {
        try { return File.Exists(PathName) ? JsonSerializer.Deserialize<UiState>(File.ReadAllText(PathName)) : null; }
        catch (Exception ex) { Log.Warn("window state not read: " + ex.Message); return null; }
    }

    void Save()
    {
        if (!_enabled || ConfigStore.ReadOnly || RunMode.HardwareFree) return;
        try
        {
            var tmp = PathName + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state));
            File.Move(tmp, PathName, true);
            Saves++;
        }
        catch (Exception ex) { Log.Warn("window state not saved: " + ex.Message); }
    }

    public static List<MonitorArea> Monitors(Window w)
    {
        var list = new List<MonitorArea>();
        try
        {
            foreach (var s in w.Screens.All)
                list.Add(new MonitorArea(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Right, s.WorkingArea.Bottom, 96 * s.Scaling));
        }
        catch { }
        return list;
    }
}
