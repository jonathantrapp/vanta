// PORTED from src\HwSuite\Ui\MainWindow.xaml.cs (Windows 1.20.0): WPF -> Avalonia 11 (mechanical pass by linux/tools/wpf2avalonia.py, then hand fixes).
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Lighting;
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

public partial class MainWindow : Window, IFixtureWindow
{
    readonly AppServices _svc;
    readonly DashboardPage _dash;
    readonly FansPage _fans;
    readonly LightingPage _light;
    readonly SystemPage _sys;
    readonly KeyboardPage _al80;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly WindowPlacementKeeper _placement;
    UserControl _active;

    public bool ExitRequested { get; set; }
    readonly TextBlock _lightsOutNote = B.Text("", "MonoMuted", 11);
    DateTime _lightsOutChecked = DateTime.MinValue;
    readonly ToggleButton _lightsOut = new() { Classes = { "LatchBtn" } };

    public MainWindow(AppServices svc)
    {
        InitializeComponent();
        _svc = svc;
        _dash = new DashboardPage(svc);
        _fans = new FansPage(svc);
        _light = new LightingPage(svc);
        _sys = new SystemPage(svc);
        _al80 = new KeyboardPage(svc);
        _active = _dash;
        PageHost.Content = _dash;

        MachineLine.Text = "B660M DS3H · i7-12700K · RTX 3070";
        ToolTip.SetTip(MachineLine, "Gigabyte B660M DS3H, Core i7-12700K, RTX 3070");
        if (svc.Fixture is UiFixture fx)
        {
            FixtureBadge.IsVisible = true;
            FixtureText.Text = $"Simulated fixture · {fx.Title}";
            ToolTip.SetTip(FixtureBadge, UiFixture.Notice + ". Readings, device states and history are simulated; nothing was read from or sent to hardware.");
        }
        Themes.Changed += OnThemeChanged;
        Closed += (_, _) => { Themes.Changed -= OnThemeChanged; _sys.Detach(); _timer.Stop(); };
        // size, position, maximized state and last page; never for fixtures or test runs
        _placement = new WindowPlacementKeeper(this, svc.Fixture == null && !ConfigStore.ReadOnly && !RunMode.HardwareFree);
        NavDashboard.Content = Icons.WithText("dashboard", "Dashboard", 16, typeof(RadioButton));
        NavFans.Content = Icons.WithText("cooling", "Cooling", 16, typeof(RadioButton));
        NavLighting.Content = Icons.WithText("lighting", "Lighting", 16, typeof(RadioButton));
        NavAl80.Content = Icons.WithText("keyboard", "AL80", 16, typeof(RadioButton));
        NavSystem.Content = Icons.WithText("system", "System", 16, typeof(RadioButton));
        NavDashboard.Checked += (_, _) => Show(_dash, "Dashboard", "dashboard");
        NavFans.Checked += (_, _) => Show(_fans, "Cooling", "fans");
        NavLighting.Checked += (_, _) => Show(_light, "Lighting", "lighting");
        NavAl80.Checked += (_, _) => Show(_al80, "AL80 keyboard", "al80");
        NavSystem.Checked += (_, _) => Show(_sys, "System", "system");
        // the last page opens again: it only switches the view (no control is activated, no lighting or fan setting changes)
        if (_placement.SavedPage is string lastPage && lastPage != "dashboard") Navigate(lastPage);

        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();

        EngineSwitch.IsChecked = svc.Config.FanControlEnabled;
        EngineSwitch.Click += (_, _) => SetFanControl(EngineSwitch.IsChecked == true);
        ReleaseButton.Click += (_, _) =>
        {
            svc.Config.FanControlEnabled = false;
            EngineSwitch.IsChecked = false;
            svc.Hub.Engine.ReleaseAll();
            ConfigStore.Save(svc.Config);
        };

        var lo = new StackPanel { Orientation = Orientation.Horizontal };
        var moon = Icons.Icon("moon", 15, "TextBrush"); moon.Margin = new Thickness(0, 0, 8, 0);
        lo.Children.Add(moon);
        lo.Children.Add(new TextBlock { Text = "Lights out", VerticalAlignment = VerticalAlignment.Center });
        _lightsOut.Content = lo;
        _lightsOut.Click += (_, _) => ToggleLightsOut();
        _lightsOut.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, "Lights out");
        _lightsOutNote.VerticalAlignment = VerticalAlignment.Center;
        _lightsOutNote.Margin = new Thickness(0, 0, 10, 0);
        HeaderActions.Children.Add(_lightsOutNote);
        HeaderActions.Children.Add(_lightsOut);

        _timer.Tick += (_, _) => Tick();
        // page refresh only while the window is shown; hidden in the tray or minimised it stops (hardware loops are separate)
        UiActivity.Bind(this, _timer, onResume: Tick);
        Closing += OnClosing;
    }

    /// <summary>Theme switched: custom-drawn controls re-render with the new tokens; nothing else is rebuilt.</summary>
    void OnThemeChanged()
    {
        InvalidateTree(this);
        Tick();
    }

    static void InvalidateTree(Visual d)
    {
        d.InvalidateVisual();
        foreach (var c in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(d)) InvalidateTree(c);
    }

    /// <summary>Switch the theme live and remember it. Hardware services are untouched.</summary>
    public static void ApplyTheme(AppServices svc, string id)
    {
        var t = Themes.Find(id);
        Themes.Apply(t, (ResourceDictionary)Application.Current!.Resources);
        lock (svc.Config.Sync) svc.Config.Theme = t.Id;
        ConfigStore.Save(svc.Config);   // read-only in test and fixture modes
        Log.Info("theme: " + t.Name);
    }

    /// <summary>Tests and diagnostics: whether the page refresh timer is running.</summary>
    internal bool RefreshTimerRunning => _timer.IsEnabled;
    internal WindowPlacementKeeper Placement => _placement;
    internal SystemPage SystemView => _sys;

    /// <summary>App exit: write the window state now (the debounce would not run any more).</summary>
    public void FlushPlacement() => _placement.Flush();

    void Show(UserControl page, string title, string tag)
    {
        _placement?.PageShown(tag);
        _active = page;
        PageHost.Content = page;
        PageTitleText.Text = title;
        Motion.PageIn(page);
        Tick();
    }

    public async void SetFanControl(bool on)
    {
        if (on && !_svc.Config.FanControlEnabled)
        {
            // The switch drives every channel not on Firmware, including GPU/AIO curves saved earlier: the owner picks the
            // channels first. Nothing is activated or changed silently (curves are never touched here).
            var candidates = FanActivation.Candidates(_svc.Config, _svc.Hub.Engine.Channels);
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            var dlg = new FanActivationDialog(candidates, _svc.Config);
            if (await dlg.ShowDialog<bool?>(this) != true) { EngineSwitch.IsChecked = false; return; }
            var changes = FanActivation.Apply(_svc.Config, candidates, dlg.Picked);
            Log.Info("fan control on; driven: " + (dlg.Picked.Count == 0 ? "none" : string.Join(", ", candidates.Where(c => dlg.Picked.Contains(c.Channel.Id)).Select(c => c.Label)))
                + (changes.Count > 0 ? "; mode changes: " + string.Join("; ", changes) : "; no mode changes"));
            _fans.Reload();
        }
        _svc.Config.FanControlEnabled = on;
        EngineSwitch.IsChecked = on;
        ConfigStore.Save(_svc.Config);
        // The ownership arbiter stops conflicting Gigabyte tools (verified) on its next refresh, within about 2 s.
        if (on) _svc.Ownership?.ResetStopAttempts();
    }

    public void Tick()
    {
        var snap = _svc.Hub.Latest;
        switch (_active)
        {
            case DashboardPage d: d.Refresh(snap); break;
            case FansPage f: f.Refresh(snap); break;
            case SystemPage s: s.Refresh(snap); break;
            case KeyboardPage k: k.Refresh(); break;
        }

        PageStatusText.Text = $"{snap.Time:HH:mm:ss}";
        UpdateLightsOutButton();
        var eng = _svc.Hub.Engine;
        EngineState.SetResourceReference(TextBlock.ForegroundProperty, eng.Emergency && _svc.Config.FanControlEnabled ? "DangerBrush" : "TextSecondaryBrush");
        if (!_svc.Config.FanControlEnabled) { EngineState.Text = "Off · firmware controls every fan"; ToolTip.SetTip(EngineState, "Every fan follows the motherboard BIOS or the GPU firmware."); }
        else if (eng.Emergency) { EngineState.Text = "Thermal emergency · driven fans at 100%"; ToolTip.SetTip(EngineState, "Thermal emergency: every driven fan is at 100% until temperatures recover."); }
        else
        {
            var st = eng.Status.Values.ToList();
            int driven = st.Count(x => x.Controlled);
            int yielded = st.Count(x => x.State.StartsWith("not writing"));
            int total = eng.Channels.Count;
            EngineState.Text = $"VANTA drives {driven} fan{(driven == 1 ? "" : "s")} · {Math.Max(0, total - driven)} on firmware" + (yielded > 0 ? $" · {yielded} yielded" : "");
            ToolTip.SetTip(EngineState, "Driven: VANTA sets the duty from your curve. On firmware: BIOS or GPU firmware controls it. Yielded: another program owns it.");
        }
    }

    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!ExitRequested && _svc.Config.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            ConfigStore.Save(_svc.Config);
        }
    }

    public void SelectFanChannel(string id) => _fans.SelectById(id);
    internal FansPage Fans => _fans;
    internal LightingPage LightingView => _light;
    IFixtureLightingView IFixtureWindow.LightingView => _light;
    internal UserControl ActivePage => _active;

    /// <summary>Header button, tray item and global hotkey: all zones to Stealth, or back to the previous effect.</summary>
    public void ToggleLightsOut()
    {
        bool off = LightsOut.Toggle(_svc.Config);
        ConfigStore.Save(_svc.Config);
        if (off) _svc.Ownership?.ResetStopAttempts();
        _lightsOutChecked = DateTime.MinValue;
        if (off) { var rep = LightsOut.Summarize(LightsOutZones()); Log.Info($"Lights out: {rep.Headline} ({string.Join("; ", rep.Lines)})"); }
        else Log.Info($"Lights on: restored {_svc.Config.Rgb.Effect}");
        OnLightingChangedExternally();
    }

    /// <summary>Effect or output changed outside the Lighting page (tray, hotkey, header).</summary>
    public void OnLightingChangedExternally()
    {
        _light.SyncFromConfig();
        UpdateLightsOutButton();
    }

    /// <summary>Lighting zones as Lights out sees them: only included, connected, unowned and writable zones count as off.</summary>
    List<LightsOut.Zone> LightsOutZones()
    {
        var z = new List<LightsOut.Zone>();
        var c = _svc.Config.Rgb;
        if (_svc.Fixture is UiFixture fx)
        {
            foreach (var (role, f) in fx.Devices)
            {
                bool inc = role switch { DeviceRole.Ram => c.Ram, DeviceRole.Board => c.Motherboard, DeviceRole.Strip => c.DLedStrip, DeviceRole.Mouse => c.Mouse, _ => c.Keyboard };
                z.Add(new(LightingDevices.Names(role).title, inc, f.Present && f.Available, f.Blocked, role != DeviceRole.Keyboard || _svc.Config.KeyboardVerified));
            }
            return z;
        }
        foreach (var s in _svc.Rgb.Sinks)
        {
            switch (s)
            {
                case RamSink: z.Add(new("RAM", c.Ram, s.Available, s.Blocked, true)); break;
                case MotherboardSink: z.Add(new("Board LEDs", c.Motherboard, s.Available, s.Blocked, true)); break;
                case DLedSink: z.Add(new("Case strip", c.DLedStrip, s.Available, s.Blocked, true)); break;
                case MouseSink m: z.Add(new("Mouse", c.Mouse, m.Controller.Connected, s.Blocked, true)); break;
                case Al80Sink:
                    z.Add(new("Keyboard", c.Keyboard, _svc.Al80?.Connected == true, s.Blocked, true)); break;
                case KeyboardSink k:
                    bool present = k.Controller.Connected;
                    try { present |= Lighting.Devices.ILovbeeKeyboardDevice.Pick(Lighting.Devices.ILovbeeKeyboardDevice.Enumerate()) != null; } catch { }
                    z.Add(new("Keyboard", c.Keyboard, present, _svc.LiveKeyboardOwnership(), _svc.Config.KeyboardVerified)); break;
            }
        }
        return z;
    }

    void UpdateLightsOutButton()
    {
        bool on = LightsOut.IsOn(_svc.Config.Rgb);
        _lightsOut.IsChecked = on;
        if (!on) { _lightsOutNote.Text = ""; ToolTip.SetTip(_lightsOutNote, null); }
        else if (DateTime.UtcNow - _lightsOutChecked > TimeSpan.FromSeconds(3))
        {
            _lightsOutChecked = DateTime.UtcNow;
            var rep = LightsOut.Summarize(LightsOutZones());
            _lightsOutNote.Text = rep.Headline;
            _lightsOutNote.SetResourceReference(TextBlock.ForegroundProperty, rep.Off == rep.Total ? "TextSecondaryBrush" : "WarnBrush");
            ToolTip.SetTip(_lightsOutNote, string.Join(Environment.NewLine, rep.Lines));
        }
        var hk = string.IsNullOrWhiteSpace(_svc.Config.LightsOutHotkey) ? "" : $"  ({_svc.Config.LightsOutHotkey})";
        ToolTip.SetTip(_lightsOut, (on ? "Lights are out. Press to restore " + (_svc.Config.Rgb.LightsOutRestoreEffect ?? EffectKind.Static) : "Switch every lighting zone to Stealth; press again to restore") + hk);
    }

    /// <summary>Navigate programmatically (tray menu and screenshots).</summary>
    public void Navigate(string page)
    {
        switch (page)
        {
            case "fans": NavFans.IsChecked = true; break;
            case "lighting": NavLighting.IsChecked = true; break;
            case "system": NavSystem.IsChecked = true; break;
            case "al80": NavAl80.IsChecked = true; break;
            case "system-inspector": NavSystem.IsChecked = true; _sys.ExpandInspector(); break;
            default: NavDashboard.IsChecked = true; break;
        }
    }

    /// <summary>Screenshot of the window content; <see cref="CaptureScale"/> 1.5 renders at 150 % scaling.</summary>
    public static double CaptureScale { get; set; } = 1.0;

    public RenderTargetBitmap Capture()
    {
        var target = (Control)Root;
        var size = new PixelSize((int)Math.Max(1, target.Bounds.Width * CaptureScale), (int)Math.Max(1, target.Bounds.Height * CaptureScale));
        var rtb = new RenderTargetBitmap(size, new Vector(96 * CaptureScale, 96 * CaptureScale));
        rtb.Render(target);
        return rtb;
    }
}
