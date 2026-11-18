using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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

/// <summary>
/// Restores the main window's size, position, maximized state and last page; saves them debounced (1.5 s after the last
/// move or resize, and when the window is hidden or the app exits). The saved rectangle is fitted to a monitor that exists
/// now: kept where it was when that place is still on a work area, otherwise centred on the monitor it overlaps most (or
/// the nearest), resized for that monitor's DPI and never larger than its work area. Disabled for fixtures and test runs.
/// Start minimised, close-to-tray and the single-instance hand-over are unchanged (they decide whether the window is shown;
/// this only decides where).
/// </summary>
public sealed class WindowPlacementKeeper
{
    public static readonly string[] Pages = { "dashboard", "fans", "lighting", "system" };
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
        window.SourceInitialized += (_, _) => Restore();
        window.LocationChanged += (_, _) => Schedule();
        window.SizeChanged += (_, _) => Schedule();
        window.StateChanged += (_, _) => Schedule();
        window.IsVisibleChanged += (_, _) => { if (!window.IsVisible) Flush(); };
    }

    /// <summary>The page to open at start (null: Dashboard). Navigating only switches the view; it changes no setting.</summary>
    public string? SavedPage => _enabled && _state.Page is string p && Pages.Contains(p) ? p : null;

    public void PageShown(string page)
    {
        if (!_enabled || !Pages.Contains(page) || _state.Page == page) return;
        _state.Page = page;
        Schedule();
    }

    void Schedule() { if (_enabled && _restored) { _debounce.Stop(); _debounce.Start(); } }

    /// <summary>Save now (window hidden to the tray, app exit).</summary>
    public void Flush()
    {
        if (!_enabled || !_restored) return;
        _debounce.Stop();
        Capture();
        Save();
    }

    void Capture()
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        (int, int, int, int)? rect = _window.WindowState == WindowState.Normal && GetWindowRect(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top
            ? (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;
        Record(_state, _window.WindowState, rect, rect != null ? DpiOf(hwnd) : 0);
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
        var hwnd = new WindowInteropHelper(_window).Handle;
        var fit = Fit(_state, Monitors(), _window.MinWidth, _window.MinHeight);
        if (fit is { } f)
        {
            // move first (a monitor with another DPI rescales the window once), then set the exact size for that monitor
            SetWindowPos(hwnd, IntPtr.Zero, f.Left, f.Top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            SetWindowPos(hwnd, IntPtr.Zero, f.Left, f.Top, f.Width, f.Height, SwpNoZOrder | SwpNoActivate);
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

    // ---- Win32 ---------------------------------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);
    const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    static double DpiOf(IntPtr hwnd) { try { var d = GetDpiForWindow(hwnd); return d > 0 ? d : 96; } catch { return 96; } }

    public static List<MonitorArea> Monitors()
    {
        var list = new List<MonitorArea>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT rc, IntPtr data) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfoW(h, ref mi))
                {
                    double dpi = 96;
                    try { if (GetDpiForMonitor(h, 0, out var dx, out _) == 0 && dx > 0) dpi = dx; } catch { }
                    list.Add(new MonitorArea(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom, dpi));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }
}
