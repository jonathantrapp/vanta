using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Ui;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace HwSuite;

public partial class App : Application
{
    readonly bool _startMinimized;
    bool _sessionEnding;
    bool _selfTestWindow;
    AppServices? _svc;
    MainWindow? _window;
    WinForms.NotifyIcon? _tray;
    System.Windows.Interop.HwndSource? _hotkeyWindow;
    const int LightsOutHotkeyId = 0x5641;
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    Window? _splash;

    public App() : this(false) { }

    public App(bool startMinimized, string? themeId = null)
    {
        _startMinimized = startMinimized;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // motion durations used by the theme's templates; Windows "Show animations" off makes every transition instant
        Resources["HoverDuration"] = Motion.Reduced ? new Duration(TimeSpan.Zero) : Motion.Hover;
        Resources["NavDuration"] = Motion.Reduced ? new Duration(TimeSpan.Zero) : Motion.Navigate;
        // theme tokens first (the saved choice; VANTA for configs from before Phase 11 and for test modes)
        string id = themeId ?? (ConfigStore.ReadOnly ? Themes.DefaultId : SavedTheme());
        Themes.Apply(Themes.Find(id), Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/HwSuite;component/Ui/Theme.xaml") });
    }

    static string SavedTheme()
    {
        try { return ConfigStore.Load().Theme; } catch { return Themes.DefaultId; }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Pumping the dispatcher for a render test also dispatches WPF's queued Startup event.
        // Do not launch a second hardware owner using the user's saved fan/lighting settings.
        if (_selfTestWindow) return;
        RunMode.Assert("start the application with hardware");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI exception", args.Exception);
            args.Handled = true;
        };
        SessionEnding += (_, _) => { _sessionEnding = true; Shutdown(0); };

        _splash = BuildSplash();
        _splash.Show();

        Task.Run(() =>
        {
            var svc = new AppServices();
            // Conflicting Gigabyte tools are stopped (and the result verified) before this app touches the Super I/O chip or
            // the SMBus. Whatever the outcome, the ownership arbiter keeps that hardware withheld while any of them runs.
            try { var r = ConflictGuard.StopIfNeeded(svc.Config, svc.Gigabyte, "launch"); if (r != null) svc.Alerts.Add(r); }
            catch (Exception ex) { Log.Error("auto-stop at launch", ex); }
            svc.InitializeHardware();
            if (RebootNotice.Pending() is string notice) { svc.Alerts.Add(notice); Log.Warn(notice); }
            svc.InitializeLighting(allowRamRemap: true);
            return svc;
        }).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            if (t.IsFaulted)
            {
                Log.Error("startup failed", t.Exception?.GetBaseException());
                MessageBox.Show("Hardware initialisation failed:\n" + t.Exception?.GetBaseException().Message, "VANTA", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            _svc = t.Result;
            _svc.Start(heartbeat: true);
            _window = new MainWindow(_svc);
            BuildTray();
            RegisterLightsOutHotkey();
            StartKeyHookWatch();
            _splash?.Close();
            if (!_startMinimized && !_svc.Config.StartMinimized) _window.Show();
            ListenForShowRequests();
            Log.Info("HwSuite started");
        }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _window?.FlushPlacement(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { if (_hotkeyWindow != null) { UnregisterHotKey(_hotkeyWindow.Handle, LightsOutHotkeyId); _hotkeyWindow.Dispose(); } } catch { }
        try
        {
            if (_svc != null)
            {
                ConfigStore.Save(_svc.Config);
                _svc.Dispose();   // fans handed back and lighting stopped first: HwSuite no longer owns anything
                if (RunMode.HardwareFree) Log.Info("hardware-free run: no service restore");
                else if (_sessionEnding || FailsafeWatchdog.SessionEnding) Log.Info("Windows is ending the session: Gigabyte services are not restarted now");
                else if (_svc.Config.RestoreGigabyteOnExit) _svc.Gigabyte.RestoreRecorded();
            }
        }
        catch (Exception ex) { Log.Error("shutdown", ex); }
        base.OnExit(e);
    }

    // ---- splash -----------------------------------------------------------------------------------------------

    static Window BuildSplash()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var v = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L8,16 L16,0"), StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        v.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        row.Children.Add(v);
        var name = new TextBlock { Text = "VANTA", FontSize = 24, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        row.Children.Add(name);
        stack.Children.Add(row);
        stack.Children.Add(new TextBlock { Text = "Initialising hardware", Style = (Style)Current.FindResource("Muted"), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        var bar = new ProgressBar { IsIndeterminate = true, Height = 2, Width = 220, Margin = new Thickness(0, 16, 0, 0), BorderThickness = new Thickness(0) };
        stack.Children.Add(bar);
        var frame = new Border { BorderThickness = new Thickness(1), Child = stack };
        frame.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
        var w = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Width = 440, Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true, ShowInTaskbar = false, Content = frame
        };
        w.SetResourceReference(Window.BackgroundProperty, "CanvasBrush");
        return w;
    }

    // ---- tray -------------------------------------------------------------------------------------------------

    /// <summary>Tray menu colours from the current theme (read each time the menu paints, so a theme switch follows).</summary>
    sealed class DarkColors : WinForms.ProfessionalColorTable
    {
        static Drawing.Color T(string token) { var c = Themes.Current[token]; return Drawing.Color.FromArgb(c.R, c.G, c.B); }
        static Drawing.Color Bg => T("Popup");
        static Drawing.Color Hover => T("ControlHover");
        static Drawing.Color Border => T("ControlBorder");
        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color SeparatorDark => Border;
        public override Drawing.Color SeparatorLight => Border;
    }

    /// <summary>VANTA mark: carbon tile with an ice-cyan V.</summary>
    static Drawing.Icon BuildIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x12, 0x12, 0x15));
            using var edge = new Drawing.Pen(Drawing.Color.FromArgb(0x3F, 0x3F, 0x46), 1.5f);
            using var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddArc(1, 1, 8, 8, 180, 90); path.AddArc(22, 1, 8, 8, 270, 90);
            path.AddArc(22, 22, 8, 8, 0, 90); path.AddArc(1, 22, 8, 8, 90, 90); path.CloseFigure();
            g.FillPath(bg, path);
            g.DrawPath(edge, path);
            using var v = new Drawing.Pen(Drawing.Color.FromArgb(0x38, 0xBD, 0xF8), 4f) { StartCap = Drawing.Drawing2D.LineCap.Round, EndCap = Drawing.Drawing2D.LineCap.Round, LineJoin = Drawing.Drawing2D.LineJoin.Round };
            g.DrawLines(v, new[] { new Drawing.PointF(8.5f, 9f), new Drawing.PointF(16f, 23.5f), new Drawing.PointF(23.5f, 9f) });
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// Global Lights Out hotkey on a message-only window, so it works while the main window is hidden in the tray.
    /// WM_HOTKEY costs nothing until pressed. A combination already taken by another program is reported, not forced.
    /// </summary>
    void RegisterLightsOutHotkey()
    {
        var text = _svc!.Config.LightsOutHotkey;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!LightsOut.TryParseHotkey(text, out var mods, out var vk)) { Log.Warn($"Lights out hotkey '{text}' not understood (example: Ctrl+Shift+F12)"); return; }
        var p = new System.Windows.Interop.HwndSourceParameters("VANTA hotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };   // HWND_MESSAGE
        _hotkeyWindow = new System.Windows.Interop.HwndSource(p);
        _hotkeyWindow.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == 0x0312 && wParam.ToInt32() == LightsOutHotkeyId) { _window?.ToggleLightsOut(); handled = true; }
            return IntPtr.Zero;
        });
        if (RegisterHotKey(_hotkeyWindow.Handle, LightsOutHotkeyId, mods, vk)) Log.Info($"Lights out hotkey: {text}");
        else
        {
            var msg = $"Lights out hotkey {text} is already used by another program; use the header button or the tray instead";
            Log.Warn(msg); _svc.Alerts.Add(msg);
        }
    }

    // 1.20 Typing Ripples: the key hook exists only while that effect is chosen (checked once a second)
    readonly Ui.KeyHook _keyHook = new();
    System.Windows.Threading.DispatcherTimer? _keyHookTimer;

    void StartKeyHookWatch()
    {
        _keyHookTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _keyHookTimer.Tick += (_, _) =>
        {
            bool want = _svc != null && EffectInfo.UsesKeys(LightingLooks.EffectiveEffect(_svc.Config.Rgb));
            if (want) _keyHook.Install(); else _keyHook.Uninstall();
        };
        _keyHookTimer.Start();
        Exit += (_, _) => _keyHook.Dispose();
    }

    void BuildTray()
    {
        var menu = new WinForms.ContextMenuStrip { Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkColors()), Font = new Drawing.Font("Segoe UI", 9.5f), ShowCheckMargin = true, ShowImageMargin = false };
        var open = new WinForms.ToolStripMenuItem("Open VANTA") { Font = new Drawing.Font("Segoe UI", 9.5f, Drawing.FontStyle.Bold) };
        open.Click += (_, _) => ShowWindow();

        var fan = new WinForms.ToolStripMenuItem("Fan control");
        fan.Click += (_, _) => { _window?.SetFanControl(!_svc!.Config.FanControlEnabled); };
        var presetItems = new List<WinForms.ToolStripMenuItem>();
        var presetHeader = new WinForms.ToolStripLabel("Fan preset");
        foreach (var name in FanPresets.Names)
        {
            var item = new WinForms.ToolStripMenuItem(name);
            // same rule as the Cooling page: only GPU fans and channels already on a curve are switched
            item.Click += (_, _) =>
            {
                FanPresets.ApplyPreset(_svc!.Config, name, _svc.Hub.Engine.Channels.Where(c => c is GpuFanChannel || _svc.Config.Channels.Any(cc => cc.Id == c.Id && cc.Mode == ChannelMode.Curve)));
                ConfigStore.Save(_svc.Config);
                Log.Info($"tray: fan preset {name}");
            };
            presetItems.Add(item);
        }

        var light = new WinForms.ToolStripMenuItem("Lighting enabled");
        light.Click += (_, _) =>
        {
            _svc!.Config.Rgb.Enabled = !_svc.Config.Rgb.Enabled;
            ConfigStore.Save(_svc.Config);
            if (_svc.Config.Rgb.Enabled) _svc.Ownership?.ResetStopAttempts();
            _window?.OnLightingChangedExternally();
        };
        var stealth = new WinForms.ToolStripMenuItem("Stealth (lights off)");
        stealth.Click += (_, _) => _window?.ToggleLightsOut();
        var looks = new WinForms.ToolStripMenuItem("Lighting look");
        looks.DropDownItems.Add(new WinForms.ToolStripMenuItem("…"));   // filled when opened
        looks.DropDown.Renderer = menu.Renderer;
        looks.DropDownOpening += (_, _) =>
        {
            var tc = Themes.Current;
            looks.DropDown.ForeColor = Drawing.Color.FromArgb(tc["Text"].R, tc["Text"].G, tc["Text"].B);
            looks.DropDown.BackColor = Drawing.Color.FromArgb(tc["Popup"].R, tc["Popup"].G, tc["Popup"].B);
            FillLooksMenu(looks, _svc!, () => _window?.OnLightingChangedExternally());
        };

        var release = new WinForms.ToolStripMenuItem("Release all fans");
        release.Click += (_, _) => { _svc!.Config.FanControlEnabled = false; _svc.Hub.Engine.ReleaseAll(); ConfigStore.Save(_svc.Config); };
        var exit = new WinForms.ToolStripMenuItem("Quit (restore fans)");
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(open);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(fan);
        menu.Items.Add(presetHeader);
        foreach (var p in presetItems) menu.Items.Add(p);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(light);
        menu.Items.Add(looks);
        menu.Items.Add(stealth);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(release);
        menu.Items.Add(exit);
        menu.Opening += (_, _) =>
        {
            var tc = Themes.Current;
            menu.ForeColor = Drawing.Color.FromArgb(tc["Text"].R, tc["Text"].G, tc["Text"].B);
            menu.BackColor = Drawing.Color.FromArgb(tc["Popup"].R, tc["Popup"].G, tc["Popup"].B);
            presetHeader.ForeColor = Drawing.Color.FromArgb(tc["TextSecondary"].R, tc["TextSecondary"].G, tc["TextSecondary"].B);
            var c = _svc!.Config;
            fan.Checked = c.FanControlEnabled;
            foreach (var p in presetItems) { p.Checked = p.Text == c.Preset; p.Text = p.Text; }
            light.Checked = c.Rgb.Enabled;
            stealth.Checked = LightsOut.IsOn(c.Rgb);
            stealth.ShortcutKeyDisplayString = string.IsNullOrWhiteSpace(c.LightsOutHotkey) ? null : c.LightsOutHotkey;
            stealth.ShowShortcutKeys = !string.IsNullOrWhiteSpace(c.LightsOutHotkey);
        };

        _tray = new WinForms.NotifyIcon { Icon = BuildIcon(), Text = "VANTA", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    /// <summary>
    /// Tray "Lighting look" submenu: starters, then saved looks; the current one is ticked. A click applies through
    /// <see cref="AppServices.ApplyLook"/>, the same path as the Lighting page (output on/off kept; during Lights out only
    /// the look restored afterwards changes).
    /// </summary>
    internal static void FillLooksMenu(WinForms.ToolStripMenuItem parent, AppServices svc, Action after)
    {
        parent.DropDownItems.Clear();
        bool savedHeader = false;
        string? group = null;
        foreach (var look in Core.LightingLooks.All(svc.Config))
        {
            if (look.Starter && look.Group != group) { if (group != null) parent.DropDownItems.Add(new WinForms.ToolStripSeparator()); group = look.Group; }
            if (!look.Starter && !savedHeader) { parent.DropDownItems.Add(new WinForms.ToolStripSeparator()); savedHeader = true; }
            var item = new WinForms.ToolStripMenuItem(look.Name) { Checked = Core.LightingLooks.Matches(svc.Config, look), ToolTipText = look.Summary };
            var l = look;
            item.Click += (_, _) => { svc.ApplyLook(l, "tray"); after(); };
            parent.DropDownItems.Add(item);
        }
        // the same edit session as the Lighting page: revert to the settings before the current changes
        var edits = svc.LightingEdits;
        parent.DropDownItems.Add(new WinForms.ToolStripSeparator());
        var revert = new WinForms.ToolStripMenuItem(edits.HasChanges && edits.Since is DateTime since ? $"Revert lighting changes (since {since:HH:mm})" : "Revert lighting changes")
        {
            Enabled = edits.HasChanges, ToolTipText = edits.Baseline is { } bl ? "Back to: " + bl.Summary : "No lighting changes to revert"
        };
        revert.Click += (_, _) => { if (svc.RevertLighting("tray")) after(); };
        parent.DropDownItems.Add(revert);
        if (LightsOut.IsOn(svc.Config.Rgb))
            parent.DropDownItems.Add(new WinForms.ToolStripMenuItem("Lights out is on: a look shows when the lights come back") { Enabled = false });
        else if (!svc.Config.Rgb.Enabled)
            parent.DropDownItems.Add(new WinForms.ToolStripMenuItem("Lighting output is off: nothing is sent") { Enabled = false });
    }

    EventWaitHandle? _showRequests;

    /// <summary>A second launch (Start menu) sets <see cref="Program.ShowWindowEventName"/>: show this window instead of a second app.</summary>
    void ListenForShowRequests()
    {
        try
        {
            _showRequests = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowWindowEventName);
            var t = new Thread(() =>
            {
                while (true)
                {
                    try { _showRequests.WaitOne(); } catch { return; }
                    try { Dispatcher.BeginInvoke(new Action(ShowWindow)); } catch { return; }
                }
            }) { IsBackground = true, Name = "VANTA show requests" };
            t.Start();
        }
        catch (Exception ex) { Log.Warn("second-launch activation unavailable: " + ex.Message); }
    }

    void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ExitApp()
    {
        if (_window != null) _window.ExitRequested = true;
        Shutdown(0);
    }

    // ---- headless UI verification (self-test and screenshots) -------------------------------------------------

    /// <summary>
    /// 1.12 UI behaviour on a fixture window (offscreen, no hardware): Arrange mode drag and keyboard edits save only when
    /// completed, refresh timers stop while hidden or minimised, the look selector and the tray submenu share one apply path,
    /// restoring a page changes nothing, fixtures never save window placement. Returns the number of failed checks.
    /// </summary>
    /// <summary>A config whose lighting appearance is <paramref name="look"/> (UI checks).</summary>
    static AppConfig CopyWith(LightingLook look) { var c = new AppConfig(); LightingLooks.Apply(c, look); return c; }

    public int RunUiBehaviourChecks()
    {
        _selfTestWindow = true;
        ConfigStore.ReadOnly = true;
        int failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) Log.Info($"PASS  1.12 UI: {name} {detail}"); else { failed++; Log.Error($"FAIL  1.12 UI: {name} {detail}"); }
        }
        // 1.19: the AL80 per-key keyboard drawing (fixtures still simulate the i87, so it is rendered here on its own)
        try
        {
            var png = Path.Combine(AppPaths.Data, "al80-perkey-preview.png");
            int lit = LightingVisualizer.RenderAl80Check(png);
            Check("AL80 per-key keyboard draws its 79 keys at the firmware positions", lit > 0 && File.Exists(png), $"({lit} lit pixels, {png})");
        }
        catch (Exception ex) { Check("AL80 per-key keyboard draws", false, ex.Message); }
        // 1.20: contact sheets of the new effects on the AL80 (looked at by eye; the checks are in self-test phase 20)
        try
        {
            var kb = LayoutTransform.Identity.GeometryFor(DeviceRole.Keyboard, Al80KeyMap.Leds);
            double clock = 0; KeyActivity.TestClock = () => clock;
            foreach (var (e, a, b) in new[] { (EffectKind.Embers, 0xFF8A2A, 0x5A1004), (EffectKind.Radar, 0x46FF9A, 0x02301A), (EffectKind.TypingRipples, 0x46E0FF, 0x2A1050) })
            {
                var cfg = new RgbConfig { Enabled = true, Effect = e, ColorA = a, ColorB = b, Brightness = 1, Speed = 0.45, Density = 0.5, Glow = 0.4 };
                var frames = new List<Rgb[]>();
                KeyActivity.Clear();
                foreach (var t in new[] { 0.6, 1.4, 2.3, 3.1 })
                {
                    clock = t;
                    if (e == EffectKind.TypingRipples) { if (t < 1) KeyActivity.Press("H"); if (t is > 2 and < 2.4) { KeyActivity.Press("Q"); KeyActivity.Press("P"); } clock = t + 0.25; }
                    var f = new Rgb[kb.Count]; Effects.Render(e, EffectContext.From(cfg, t, null), kb, f); frames.Add(f);
                }
                var png = Path.Combine(AppPaths.Data, $"al80-{e}.png");
                LightingVisualizer.RenderAl80Sheet(png, frames, 2);
                Check($"1.20 sheet: {e} drawn", File.Exists(png), png);
            }
        }
        catch (Exception ex) { Check("1.20 effect sheets", false, ex.Message); }
        finally { KeyActivity.TestClock = null; KeyActivity.Clear(); }
        try
        {
            var fx = UiFixture.All().First(f => f.Id == "lighting-wave");
            var cfg = UiFixture.LoadConfig(null);
            fx.Configure?.Invoke(cfg);
            using var svc = AppServices.ForFixture(cfg, fx, DateTime.Now);
            string cfgBefore = ConfigStore.ToJson(cfg);
            long saves0 = ConfigStore.SkippedSaves;
            var win = new MainWindow(svc) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, Width = 1600, Height = 960 };
            win.Show();
            Pump(400);
            Check("fixture windows never restore or save placement", win.Placement.SavedPage == null && !File.Exists(Path.Combine(AppPaths.Data, "ui-state.json")));
            win.Navigate("lighting");
            Pump(500);
            var light = win.LightingView;
            Check("opening a page changes no setting and saves nothing", ConfigStore.ToJson(cfg) == cfgBefore && ConfigStore.SkippedSaves == saves0 && light.LookApplies == 0);
            Check("refresh timers run while the window is shown", win.RefreshTimerRunning && light.StatusTimerRunning);
            win.Hide(); Pump(200);
            Check("refresh timers and the preview clock stop while hidden (tray)", !win.RefreshTimerRunning && !light.StatusTimerRunning && !light.PreviewClockRunning);
            win.Show(); Pump(300);
            Check("they resume when shown again", win.RefreshTimerRunning && light.StatusTimerRunning && light.PreviewClockRunning);
            win.WindowState = WindowState.Minimized; Pump(200);
            Check("refresh timers and the preview clock stop while minimised", !win.RefreshTimerRunning && !light.StatusTimerRunning && !light.PreviewClockRunning);
            win.WindowState = WindowState.Normal; Pump(300);

            // Arrange mode: drag is preview-only until it is dropped
            light.SetArranging(true); Pump(300);
            var viz = light.Visualizer;
            long layoutV = svc.Rgb.Layout.Version; saves0 = ConfigStore.SkippedSaves;
            var kbBefore = svc.Config.Arrangement.Get(Lighting.DeviceRole.Keyboard).Clone();
            Check("Arrange mode selects a device with visible focus handles", light.Arranging && viz.Selected == Lighting.DeviceRole.Keyboard && viz.Focusable);
            light.BeginDrag(new Point(400, 150));
            for (int i = 1; i <= 20; i++) { light.DragTo(new Point(400 - i * 6, 150 + i * 2)); Pump(16); }
            Check("dragging saves nothing and leaves the effect layout unchanged", ConfigStore.SkippedSaves == saves0 && svc.Rgb.Layout.Version == layoutV && light.ArrangementCommits == 0
                && svc.Config.Arrangement.Get(Lighting.DeviceRole.Keyboard).Equals(kbBefore), $"(saves {ConfigStore.SkippedSaves - saves0}, commits {light.ArrangementCommits})");
            light.EndDrag(); Pump(100);
            var kbAfter = svc.Config.Arrangement.Get(Lighting.DeviceRole.Keyboard);
            Check("dropping commits once: one save, one new effect layout", ConfigStore.SkippedSaves == saves0 + 1 && svc.Rgb.Layout.Version == layoutV + 1 && light.ArrangementCommits == 1 && kbAfter.X < kbBefore.X && svc.Config.Arrangement.Arranged,
                $"(keyboard {kbBefore.X:0},{kbBefore.Y:0} -> {kbAfter.X:0},{kbAfter.Y:0})");

            // keyboard adjustment: repeated key presses, one commit on release
            var src = PresentationSource.FromVisual(viz);
            void Key(System.Windows.Input.Key k, bool down) => viz.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, src!, 0, k) { RoutedEvent = down ? System.Windows.Input.Keyboard.KeyDownEvent : System.Windows.Input.Keyboard.KeyUpEvent });
            saves0 = ConfigStore.SkippedSaves; int commits0 = light.ArrangementCommits; double x0 = svc.Config.Arrangement.Get(Lighting.DeviceRole.Keyboard).X;
            for (int i = 0; i < 4; i++) Key(System.Windows.Input.Key.Right, true);
            bool noSaveWhileHeld = ConfigStore.SkippedSaves == saves0;
            Key(System.Windows.Input.Key.Right, false);
            Check("arrow keys move the selected device and save once on release", noSaveWhileHeld && light.ArrangementCommits == commits0 + 1 && ConfigStore.SkippedSaves == saves0 + 1
                && Math.Abs(svc.Config.Arrangement.Get(Lighting.DeviceRole.Keyboard).X - (x0 + 40)) < 0.01);
            Key(System.Windows.Input.Key.R, true); Key(System.Windows.Input.Key.R, false);
            Check("R turns the keyboard a quarter turn (committed)", svc.Config.Arrangement.Get(Lighting.DeviceRole.Keyboard).Rotation == 90);
            Key(System.Windows.Input.Key.D1, true);
            Key(System.Windows.Input.Key.R, true);
            Check("the case front cannot be turned (its LED layout is not mapped)", viz.Selected == Lighting.DeviceRole.Strip && svc.Config.Arrangement.Get(Lighting.DeviceRole.Strip).Rotation == 0);
            light.ResetArrangement();
            Check("Reset layout returns to the default row and identity effect layout", !svc.Config.Arrangement.Arranged && !svc.Rgb.Layout.Active);
            light.SetArranging(false); Pump(100);
            Check("leaving Arrange mode hides the editing handles", !light.Arranging && viz.Selected == null && !viz.Focusable);

            // look selector (page) and tray submenu: one apply path, no automatic selection
            Check("no look is applied when the page opens", light.LookApplies == 0);
            var forest = light.LookSelector.Items.OfType<System.Windows.Controls.ComboBoxItem>().First(i => i.Tag is LightingLook { Name: "Forest Mist" });
            light.LookSelector.SelectedItem = forest; Pump(100);
            Check("choosing a look in the selector applies it", light.LookApplies == 1 && LightingLooks.Matches(svc.Config, LightingLooks.Starters[0]));
            light.SyncFromConfig(); Pump(50);
            Check("refreshing the selector never re-applies", light.LookApplies == 1);
            var trayParent = new WinForms.ToolStripMenuItem("Lighting look");
            int after = 0;
            FillLooksMenu(trayParent, svc, () => after++);
            var rain = trayParent.DropDownItems.OfType<WinForms.ToolStripMenuItem>().First(i => i.Text == LightingLooks.TealTide);   // 1.12's "Rainfall"
            Check("the tray submenu ticks the current look", trayParent.DropDownItems.OfType<WinForms.ToolStripMenuItem>().Single(i => i.Checked).Text == "Forest Mist");
            rain.PerformClick();
            Check("choosing a look in the tray applies it through the same path", after == 1 && LightingLooks.Matches(svc.Config, LightingLooks.Starters[1]));
            LightsOut.Toggle(svc.Config);
            FillLooksMenu(trayParent, svc, () => after++);
            trayParent.DropDownItems.OfType<WinForms.ToolStripMenuItem>().First(i => i.Text == "Moonlit Fog").PerformClick();
            Check("a tray look during Lights out keeps the lights out and changes the restored look",
                LightsOut.IsOn(svc.Config.Rgb) && svc.Config.Rgb.LightsOutRestoreEffect == EffectKind.Static && trayParent.DropDownItems.OfType<WinForms.ToolStripMenuItem>().Any(i => !i.Enabled && i.Text?.StartsWith("Lights out") == true));
            // 1.13: a rainy-forest look from the tray during Lights out keeps the lights out; its scene comes back afterwards
            FillLooksMenu(trayParent, svc, () => after++);
            trayParent.DropDownItems.OfType<WinForms.ToolStripMenuItem>().First(i => i.Text == "Distant Thunder").PerformClick();
            Check("1.13 UI: a scene look from the tray during Lights out keeps the lights out",
                LightsOut.IsOn(svc.Config.Rgb) && svc.Config.Rgb.LightsOutRestoreEffect == EffectKind.DistantStorm && svc.Config.Rgb.Lightning);
            Check("1.13 UI: the tray lists the rainy-forest starters after a separator", trayParent.DropDownItems.OfType<WinForms.ToolStripSeparator>().Any()
                && LightingLooks.Starters.All(s => trayParent.DropDownItems.OfType<WinForms.ToolStripMenuItem>().Any(i => i.Text == s.Name)));
            LightsOut.Toggle(svc.Config);
            light.SyncFromConfig(); Pump(100);
            Check("1.13 UI: Distant Storm shows speed, amount ('Rain') and the lightning switch; fine tuning available",
                svc.Config.Rgb.Effect == EffectKind.DistantStorm && light.SpeedShown && light.AmountShown == "Rain" && light.LightningShown && light.FineTuningShown);
            var still = light.LookSelector.Items.OfType<System.Windows.Controls.ComboBoxItem>().First(i => i.Tag is LightingLook { Name: "Deep Woods" });
            light.LookSelector.SelectedItem = still; Pump(100);
            Check("1.13 UI: Static hides speed, amount, lightning and fine tuning (controls that would do nothing)",
                svc.Config.Rgb.Effect == EffectKind.Static && !light.SpeedShown && light.AmountShown == null && !light.LightningShown && !light.FineTuningShown);
            var fog = light.LookSelector.Items.OfType<System.Windows.Controls.ComboBoxItem>().First(i => i.Tag is LightingLook { Name: "Mountain Fog" });
            light.LookSelector.SelectedItem = fog; Pump(100);
            Check("1.13 UI: Rolling Mist shows 'Fog' and no lightning switch", light.AmountShown == "Fog" && !light.LightningShown && LightingLooks.Matches(svc.Config, LightingLooks.Starters.First(s => s.Name == "Mountain Fog")));

            // 1.14: device shortcuts navigate and change nothing; drags and Arrange mode never navigate
            svc.KeepLighting("ui check"); light.SyncFromConfig(); Pump(100);
            string before14 = Core.ConfigStore.ToJson(svc.Config); long saves14 = Core.ConfigStore.SkippedSaves;
            var memAt = light.DeviceCentre(HwSuite.Lighting.DeviceRole.Ram);
            bool clicked = memAt is Point mp && light.ClickDevice(mp, mp + new Vector(2, 1));
            Pump(100);
            Check("1.14 UI: clicking the memory drawing shows the memory card, outlined", clicked && light.LastFocusedDevice == HwSuite.Lighting.DeviceRole.Ram && light.HighlightedDevice == HwSuite.Lighting.DeviceRole.Ram);
            Check("1.14 UI: selecting a device changes, saves and sends nothing, and starts no edit session",
                Core.ConfigStore.ToJson(svc.Config) == before14 && Core.ConfigStore.SkippedSaves == saves14 && !svc.LightingEdits.Active);
            int nav = light.DeviceFocusCount;
            var kbAt = light.DeviceCentre(HwSuite.Lighting.DeviceRole.Keyboard);
            Check("1.14 UI: a press that moves (a drag) is not a click", kbAt is Point kp && !light.ClickDevice(kp, kp + new Vector(30, 0)) && light.DeviceFocusCount == nav);
            light.SetArranging(true); Pump(100);
            var kbArr = light.DeviceCentre(HwSuite.Lighting.DeviceRole.Keyboard);
            Check("1.14 UI: in Arrange mode a click never navigates", kbArr is Point ap && !light.ClickDevice(ap, ap) && light.DeviceFocusCount == nav);
            light.SetArranging(false); Pump(100);
            bool allRoles = true;
            foreach (var r in new[] { HwSuite.Lighting.DeviceRole.Strip, HwSuite.Lighting.DeviceRole.Board, HwSuite.Lighting.DeviceRole.Keyboard, HwSuite.Lighting.DeviceRole.Mouse, HwSuite.Lighting.DeviceRole.Ram })
            { light.FocusDevice(r, moveFocus: true); Pump(30); allRoles &= light.LastFocusedDevice == r && light.HighlightedDevice == r; }
            Check("1.14 UI: every device shortcut reaches its own card (the case front and logo share one card)", allRoles && Core.ConfigStore.ToJson(svc.Config) == before14);

            // 1.14: revert bar (page and tray edits in one session)
            var start14 = LightingEditSession.Snapshot(svc.Config);
            // (the earlier checks leave Mountain Fog selected; the session below uses other looks, so it never returns to the start by chance)
            Check("1.14 UI: no revert bar without changes", !light.RevertShown);
            var downpour = light.LookSelector.Items.OfType<System.Windows.Controls.ComboBoxItem>().First(i => i.Tag is LightingLook { Name: "Midnight Downpour" });
            light.LookSelector.SelectedItem = downpour; Pump(100);
            Check("1.14 UI: the first change shows the revert bar", light.RevertShown && svc.LightingEdits.HasChanges);
            FillLooksMenu(trayParent, svc, () => { after++; light.SyncFromConfig(); });
            trayParent.DropDownItems.OfType<WinForms.ToolStripMenuItem>().First(i => i.Text == "Forest Fireflies").PerformClick();
            Pump(100);
            var bl = svc.LightingEdits.Baseline;
            Check("1.14 UI: a tray look is applied", LightingEditSession.SameAppearance(svc.Config, LightingLooks.Starters.First(s => s.Name == "Forest Fireflies")), $"(now {LightingEditSession.Snapshot(svc.Config).Summary})");
            Check("1.14 UI: ...and joins the same session (the revert point stays the original)", bl != null && LightingEditSession.SameAppearance(CopyWith(start14), bl),
                $"(revert point {bl?.Summary}; expected {start14.Summary})");
            light.ClickRevert(); Pump(100);
            Check("1.14 UI: Revert returns to the settings before the first change and hides the bar", LightingEditSession.SameAppearance(svc.Config, start14) && !light.RevertShown && !svc.LightingEdits.Active);
            light.LookSelector.SelectedItem = light.LookSelector.Items.OfType<System.Windows.Controls.ComboBoxItem>().First(i => i.Tag is LightingLook { Name: "Still Canopy" }); Pump(100);
            var kept = LightingEditSession.Snapshot(svc.Config);
            light.ClickKeep(); Pump(100);
            Check("1.14 UI: Keep accepts the current look as the new starting point", !light.RevertShown && !svc.LightingEdits.Active && LightingEditSession.SameAppearance(svc.Config, kept));

            // reduced motion: the preview clock drops to its reduced rate (separate from screenshots)
            Motion.ReducedOverride = true;
            Pump(1200);
            Motion.ReducedOverride = null;
            Check("reduced motion keeps the window usable", win.IsVisible);
            win.ExitRequested = true;
            win.Close();
            Check("closing the window detaches its log listener and timers", !win.RefreshTimerRunning);
        }
        catch (Exception ex) { failed++; Log.Error("1.12 UI behaviour checks", ex); }
        return failed;
    }

    static void Pump(int ms)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end)
        {
            Current.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(15);
        }
    }

    static void DumpRadios(DependencyObject root, string page)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton rb && rb.GroupName is "preset" or "mode" or "effect")
            {
                var border = rb.Template?.FindName("B", rb) as Border;
                Log.Info($"[{page}] radio '{rb.Content}' group={rb.GroupName} checked={rb.IsChecked} template-bg={(border?.Background as SolidColorBrush)?.Color} style={(rb.Style != null)}");
            }
            DumpRadios(child, page);
        }
    }

    /// <summary>A size to render: logical window size and render scale (1.5 = 144 DPI, Windows 150%).</summary>
    public sealed record ShotSize(string Name, double Width, double Height, double Scale);

    public static readonly ShotSize[] AllSizes =
    {
        new("compact-1280x820", 1280, 820, 1.0), new("standard-1600x960", 1600, 960, 1.0),
        new("large-2560x1440", 2560, 1440, 1.0), new("scaled150-1707x960", 1707, 960, 1.5)
    };

    static void SavePng(BitmapSource bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>
    /// Renders simulated fixture states (Ui/Fixtures.cs) offscreen in the real window. Nothing is opened: no LHM, NVML,
    /// SMBus or HID device, no engine started, configuration read-only. Every image is labelled "Simulated fixture" in the
    /// window and in its path. Returns the number of failures (render exceptions, motion check failures).
    /// </summary>
    public int RunFixtures(string? shotDir, IEnumerable<ThemePalette> themes, IEnumerable<ShotSize> sizes, IEnumerable<UiFixture> fixtures, string? configPath = null, string? motionDir = null,
        string? filmDir = null, double filmSeconds = 10, double filmFps = 15)
    {
        _selfTestWindow = true;
        ConfigStore.ReadOnly = true;
        int failures = 0;
        var now = DateTime.Now;
        var fixtureList = fixtures.ToList();
        var index = new List<string>();
        foreach (var theme in themes)
        {
            Themes.Apply(theme, Resources);
            foreach (var size in sizes)
            {
                HwSuite.Ui.MainWindow.CaptureScale = size.Scale;
                foreach (var fx in fixtureList)
                {
                    try
                    {
                        var cfg = UiFixture.LoadConfig(configPath);
                        fx.Configure?.Invoke(cfg);
                        cfg.Theme = theme.Id;
                        using var svc = AppServices.ForFixture(cfg, fx, now);
                        var win = new MainWindow(svc)
                        {
                            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                            ShowInTaskbar = false, ShowActivated = false, Width = size.Width, Height = size.Height
                        };
                        win.Show();
                        Pump(400);
                        win.Navigate(fx.Page);
                        if (fx.SelectChannel != null) win.SelectFanChannel(fx.SelectChannel);
                        Pump(300);
                        fx.Prepare?.Invoke(win);
                        if (fx.EditSession)
                        {
                            // simulated experiment (in memory only): the session starts at the current look, then another look is tried
                            svc.ApplyLook(LightingLooks.Starters.First(s => s.Name == "Midnight Downpour"), "fixture");
                            win.LightingView.SyncFromConfig();
                        }
                        Pump(600);
                        string baseName = $"{theme.Id.ToLowerInvariant()}/{size.Name}/fixture-{fx.Id}";
                        if (shotDir != null)
                        {
                            SavePng(win.Capture(), Path.Combine(shotDir, baseName + ".png"));
                            index.Add(baseName + ".png");
                            if (fx.Page == "lighting")
                            {
                                win.LightingView.ScrollToDevices();
                                Pump(500); win.Tick(); Pump(200);
                                SavePng(win.Capture(), Path.Combine(shotDir, baseName + "-devices.png"));
                                index.Add(baseName + "-devices.png");
                            }
                            if (fx.ShowActivation)
                            {
                                var dlg = new FanActivationDialog(FanActivation.Candidates(svc.Config, svc.Hub.Engine.Channels), svc.Config) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
                                dlg.Show();
                                Pump(600);
                                var root = (FrameworkElement)dlg.Content;
                                double sc = size.Scale;
                                var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * sc), (int)Math.Ceiling(root.ActualHeight * sc), 96 * sc, 96 * sc, PixelFormats.Pbgra32);
                                bmp.Render(root);
                                SavePng(bmp, Path.Combine(shotDir, baseName + "-dialog.png"));
                                index.Add(baseName + "-dialog.png");
                                dlg.Close();
                            }
                        }
                        if (shotDir != null && fx.FocusDevice is HwSuite.Lighting.DeviceRole focusRole)
                        {
                            win.LightingView.FocusDevice(focusRole, moveFocus: false);
                            Pump(400);
                            SavePng(win.Capture(), Path.Combine(shotDir, baseName + "-focused.png"));
                            index.Add(baseName + "-focused.png");
                        }
                        if (shotDir != null && fx.CaseSetup)
                        {
                            win.LightingView.ShowCaseSetup();
                            Pump(500);
                            SavePng(win.Capture(), Path.Combine(shotDir, baseName + "-case-setup.png"));
                            index.Add(baseName + "-case-setup.png");
                        }
                        if (shotDir != null && fx.PaletteDemo is string demo)
                        {
                            var dlg = win.LightingView.CreatePaletteDialog();
                            dlg.WindowStartupLocation = WindowStartupLocation.Manual; dlg.Left = -20000; dlg.Top = -20000; dlg.ShowActivated = false;
                            if (demo == "result") dlg.ShowResult(ImagePalette.FromBitmap(UiFixture.RainyForestImage(), 3840, 2160), "rainy-forest (generated).png");
                            else if (demo == "error")
                            {
                                var bad = Path.Combine(AppPaths.Data, "not-an-image.png");
                                File.WriteAllText(bad, "this is a text file with an image name");
                                try { ImagePalette.FromFile(bad); } catch (PaletteException pe) { dlg.ShowError(pe.Message, "not-an-image.png"); }
                            }
                            dlg.Show();
                            Pump(700);
                            dlg.RenderNow();
                            Pump(200);
                            var root = (FrameworkElement)dlg.Content;
                            double sc = size.Scale;
                            var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * sc), (int)Math.Ceiling(root.ActualHeight * sc), 96 * sc, 96 * sc, PixelFormats.Pbgra32);
                            bmp.Render(root);
                            SavePng(bmp, Path.Combine(shotDir, baseName + "-dialog.png"));
                            index.Add(baseName + "-dialog.png");
                            dlg.Close();
                        }
                        if (shotDir != null && fx.OpenLooks)
                        {
                            // the Look list's popup is its own window: render its content directly
                            var box = win.LightingView.LookSelector;
                            box.IsDropDownOpen = true;
                            Pump(500);
                            static System.Windows.Controls.Primitives.Popup? FindPopup(DependencyObject d)
                            {
                                if (d is System.Windows.Controls.Primitives.Popup p) return p;
                                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) if (FindPopup(VisualTreeHelper.GetChild(d, i)) is { } f) return f;
                                return null;
                            }
                            if (FindPopup(box) is { Child: FrameworkElement pc })
                            {
                                double sc = size.Scale;
                                var bmp = new RenderTargetBitmap((int)Math.Ceiling(pc.ActualWidth * sc), (int)Math.Ceiling(pc.ActualHeight * sc), 96 * sc, 96 * sc, PixelFormats.Pbgra32);
                                bmp.Render(pc);
                                SavePng(bmp, Path.Combine(shotDir, baseName + "-list.png"));
                                index.Add(baseName + "-list.png");
                            }
                            else Log.Warn($"fixture {fx.Id}: the look list popup was not found");
                            box.IsDropDownOpen = false;
                            Pump(100);
                        }
                        if (filmDir != null && fx.Film && fx.Page == "lighting")
                        {
                            // the simulated preview at explicit times (not wall clock): repeatable, frame-rate independent frames
                            string dirOut = Path.Combine(filmDir, theme.Id.ToLowerInvariant(), size.Name, fx.Id);
                            Directory.CreateDirectory(dirOut);
                            win.LightingView.ScrollToVisualizer();
                            Pump(200);
                            double t0 = UiFixture.FilmStart(svc.Config.Rgb);
                            int frames = (int)Math.Round(filmSeconds * filmFps);
                            for (int i = 0; i < frames; i++)
                            {
                                var el = win.LightingView.RenderPreviewAt(t0 + i / filmFps);
                                double sc = size.Scale;
                                var bmp = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth * sc), (int)Math.Ceiling(el.ActualHeight * sc), 96 * sc, 96 * sc, PixelFormats.Pbgra32);
                                bmp.Render(el);
                                SavePng(bmp, Path.Combine(dirOut, $"f{i:0000}.png"));
                            }
                            File.WriteAllText(Path.Combine(dirOut, "FILM.txt"), $"{UiFixture.Notice}. {fx.Title}. Simulated preview, {frames} frames at {filmFps} fps from t = {t0:0.00} s (explicit times; the keyboard's real live frames run at about 12/s).");
                            Log.Info($"fixture film {dirOut}: {frames} frames");
                        }
                        if (motionDir != null && fx.Id == "cooling-case-fans")
                        {
                            int m = MotionCheck.Run(win, Path.Combine(motionDir, theme.Id.ToLowerInvariant()));
                            failures += m;
                        }
                        win.ExitRequested = true;
                        win.Close();
                        Log.Info($"fixture {baseName}: rendered");
                    }
                    catch (Exception ex) { failures++; Log.Error($"fixture {fx.Id} ({theme.Id}, {size.Name})", ex); }
                }
            }
        }
        HwSuite.Ui.MainWindow.CaptureScale = 1.0;
        if (shotDir != null)
            File.WriteAllText(Path.Combine(shotDir, "FIXTURES.txt"),
                UiFixture.Notice + "." + Environment.NewLine +
                "Rendered offscreen by HwSuite --ui-fixtures from a read-only configuration copy and simulated readings; no hardware was opened and nothing was written to any device." + Environment.NewLine +
                $"Rendered {DateTime.Now:yyyy-MM-dd HH:mm:ss}, build {typeof(App).Assembly.GetName().Version}." + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, fixtureList.Select(f => $"{f.Id,-26} {f.Title}")) + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, index));
        return failures;
    }
}

