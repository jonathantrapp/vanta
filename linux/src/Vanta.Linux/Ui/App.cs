// LINUX: the application shell (src\HwSuite\App.xaml.cs on Windows), rewritten for Avalonia: startup with the engine in
// this process (as on Windows), tray menu, single instance, Lights-out hotkey hook point, fixture screenshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Ui;

namespace HwSuite;

public sealed class App : Application
{
    /// <summary>What to do once Avalonia is up: run the real app, or render fixture screenshots and exit.</summary>
    public static Func<App, IClassicDesktopStyleApplicationLifetime, Task<int>>? Run;
    public static bool StartMinimized;
    public static string? ThemeId;
    public static int ExitCode;

    AppServices? _svc;
    MainWindow? _window;
    TrayIcon? _tray;
    ControlSocket? _control;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://vanta/")) { Source = new Uri("avares://vanta/Ui/Theme.axaml") });
        string id = ThemeId ?? (ConfigStore.ReadOnly ? Themes.DefaultId : SavedTheme());
        ApplyThemeResources(Themes.Find(id));
    }

    public void ApplyThemeResources(ThemePalette t)
    {
        RequestedThemeVariant = t.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        Themes.Apply(t, (ResourceDictionary)Resources);
    }

    static string SavedTheme() { try { return ConfigStore.Load().Theme; } catch { return Themes.DefaultId; } }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var run = Run ?? ((app, d) => app.StartReal(d));
            Dispatcher.UIThread.Post(async () =>
            {
                try { ExitCode = await run(this, desktop); }
                catch (Exception ex) { Log.Error("UI run failed", ex); ExitCode = 1; desktop.Shutdown(1); }
            });
        }
        base.OnFrameworkInitializationCompleted();
    }

    // ---- the real app ------------------------------------------------------------------------------------------

    async Task<int> StartReal(IClassicDesktopStyleApplicationLifetime desktop)
    {
        RunMode.Assert("start the application with hardware");
        Dispatcher.UIThread.UnhandledException += (_, e) => { Log.Error("UI exception", e.Exception); e.Handled = true; };
        AppServices svc;
        try
        {
            svc = await Task.Run(() =>
            {
                var s = new AppServices();
                s.InitializeHardware();
                if (RebootNotice.Pending() is string notice) { s.Alerts.Add(notice); Log.Warn(notice); }
                s.InitializeLighting(allowRamRemap: true);
                return s;
            });
        }
        catch (Exception ex)
        {
            Log.Error("startup failed", ex);
            MessageBox.Show("Hardware initialisation failed:\n" + ex.Message, "VANTA", MessageBoxButton.OK, MessageBoxImage.Error);
            desktop.Shutdown(1);
            return 1;
        }
        _svc = svc;
        foreach (var n in svc.Notes) Log.Info("startup: " + n);
        FailsafeWatchdog.SpawnFor(Environment.ProcessId);
        svc.Start(heartbeat: true);
        _window = new MainWindow(svc);
        desktop.MainWindow = _window;
        BuildTray();
        _control = new ControlSocket(Program.ControlSocketPath, svc) { ShowWindow = () => Dispatcher.UIThread.Post(ShowWindow) };
        try { _control.Start(); } catch (Exception ex) { Log.Warn("control socket unavailable (second launch cannot bring the window forward): " + ex.Message); }
        desktop.Exit += (_, _) => Shutdown();
        if (!StartMinimized && !svc.Config.StartMinimized) _window.Show();
        Log.Info("VANTA for Linux started");
        return 0;
    }

    void Shutdown()
    {
        try { _window?.FlushPlacement(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _control?.Dispose(); } catch { }
        try { if (_svc != null) { ConfigStore.Save(_svc.Config); _svc.Dispose(); } }
        catch (Exception ex) { Log.Error("shutdown", ex); }
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
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(0);
    }

    /// <summary>
    /// Tray (StatusNotifierItem). KDE shows it out of the box; Fedora GNOME needs the "AppIndicator and KStatusNotifierItem
    /// Support" extension (package gnome-shell-extension-appindicator). Without a tray, launching VANTA again shows the window.
    /// </summary>
    void BuildTray()
    {
        var svc = _svc!;
        var menu = new NativeMenu();
        NativeMenuItem Item(string text, Action click, bool toggle = false)
        {
            var i = new NativeMenuItem(text) { ToggleType = toggle ? NativeMenuItemToggleType.CheckBox : NativeMenuItemToggleType.None };
            i.Click += (_, _) => click();
            menu.Items.Add(i);
            return i;
        }
        Item("Open VANTA", ShowWindow);
        menu.Items.Add(new NativeMenuItemSeparator());
        var fan = Item("Fan control", () => _window?.SetFanControl(!svc.Config.FanControlEnabled), true);
        var presets = new List<NativeMenuItem>();
        foreach (var name in FanPresets.Names)
        {
            var n = name;
            presets.Add(Item("Fan preset: " + n, () =>
            {
                FanPresets.ApplyPreset(svc.Config, n, svc.Hub.Engine.Channels.Where(c => c is GpuFanChannel || svc.Config.Channels.Any(cc => cc.Id == c.Id && cc.Mode == ChannelMode.Curve)));
                ConfigStore.Save(svc.Config);
                Log.Info($"tray: fan preset {n}");
            }, true));
        }
        menu.Items.Add(new NativeMenuItemSeparator());
        var light = Item("Lighting enabled", () =>
        {
            svc.Config.Rgb.Enabled = !svc.Config.Rgb.Enabled;
            ConfigStore.Save(svc.Config);
            if (svc.Config.Rgb.Enabled) svc.Ownership?.ResetStopAttempts();
            _window?.OnLightingChangedExternally();
        }, true);
        var looks = new NativeMenuItem("Lighting look") { Menu = new NativeMenu() };
        menu.Items.Add(looks);
        var stealth = Item("Lights out", () => _window?.ToggleLightsOut(), true);
        menu.Items.Add(new NativeMenuItemSeparator());
        Item("Release all fans", () => { svc.Config.FanControlEnabled = false; svc.Hub.Engine.ReleaseAll(); ConfigStore.Save(svc.Config); });
        Item("Quit (restore fans)", ExitApp);
        menu.NeedsUpdate += (_, _) =>
        {
            var c = svc.Config;
            fan.IsChecked = c.FanControlEnabled;
            for (int i = 0; i < presets.Count; i++) presets[i].IsChecked = FanPresets.Names.ElementAt(i) == c.Preset;
            light.IsChecked = c.Rgb.Enabled;
            stealth.IsChecked = LightsOut.IsOn(c.Rgb);
            FillLooksMenu(looks.Menu!, svc, () => _window?.OnLightingChangedExternally());
        };
        FillLooksMenu(looks.Menu!, svc, () => _window?.OnLightingChangedExternally());
        _tray = new TrayIcon { ToolTipText = "VANTA", Menu = menu, IsVisible = true };
        try { _tray.Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://vanta/Assets/vanta.ico"))); } catch { }
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    /// <summary>Tray "Lighting look": starters, then saved looks (the current one ticked), then Revert. Same apply path as the page.</summary>
    internal static void FillLooksMenu(NativeMenu parent, AppServices svc, Action after)
    {
        parent.Items.Clear();
        bool savedHeader = false; string? group = null;
        foreach (var look in LightingLooks.All(svc.Config))
        {
            if (look.Starter && look.Group != group) { if (group != null) parent.Items.Add(new NativeMenuItemSeparator()); group = look.Group; }
            if (!look.Starter && !savedHeader) { parent.Items.Add(new NativeMenuItemSeparator()); savedHeader = true; }
            var item = new NativeMenuItem(look.Name) { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = LightingLooks.Matches(svc.Config, look) };
            var l = look;
            item.Click += (_, _) => { svc.ApplyLook(l, "tray"); after(); };
            parent.Items.Add(item);
        }
        var edits = svc.LightingEdits;
        parent.Items.Add(new NativeMenuItemSeparator());
        var revert = new NativeMenuItem(edits.HasChanges && edits.Since is DateTime since ? $"Revert lighting changes (since {since:HH:mm})" : "Revert lighting changes") { IsEnabled = edits.HasChanges };
        revert.Click += (_, _) => { if (svc.RevertLighting("tray")) after(); };
        parent.Items.Add(revert);
    }

    // ---- fixture screenshots (no hardware) ------------------------------------------------------------------------

    public sealed record ShotSize(string Name, double Width, double Height, double Scale);
    public static readonly ShotSize[] AllSizes =
    {
        new("compact-1280x820", 1280, 820, 1.0), new("standard-1600x960", 1600, 960, 1.0),
        new("large-2560x1440", 2560, 1440, 1.0), new("scaled150-1707x960", 1707, 960, 1.5)
    };

    static void Pump(int ms) => WpfCompat.Wait(Task.Delay(ms).ContinueWith(_ => true));

    static void SavePng(RenderTargetBitmap bmp, string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        bmp.Save(path);
    }

    /// <summary>
    /// Renders simulated fixture states in the real window (nothing opened, configuration read-only), one PNG per
    /// theme x size x fixture. Returns the number of failures.
    /// </summary>
    public int RunFixtures(string shotDir, IEnumerable<ThemePalette> themes, IEnumerable<ShotSize> sizes, IEnumerable<UiFixture> fixtures, string? configPath = null)
    {
        ConfigStore.ReadOnly = true;
        int failures = 0;
        var now = DateTime.Now;
        var list = fixtures.ToList();
        foreach (var theme in themes)
        {
            ApplyThemeResources(theme);
            foreach (var size in sizes)
            {
                MainWindow.CaptureScale = size.Scale;
                foreach (var fx in list)
                {
                    try
                    {
                        var cfg = UiFixture.LoadConfig(configPath);
                        fx.Configure?.Invoke(cfg);
                        cfg.Theme = theme.Id;
                        using var svc = AppServices.ForFixture(cfg, fx, now);
                        var win = new MainWindow(svc) { Width = size.Width, Height = size.Height, ShowInTaskbar = false, ShowActivated = false };
                        win.ExitRequested = true;
                        win.Show();
                        Pump(400);
                        win.Navigate(fx.Page);
                        if (fx.SelectChannel != null) win.SelectFanChannel(fx.SelectChannel);
                        Pump(300);
                        fx.Prepare?.Invoke(win);
                        Pump(600);
                        win.Tick();
                        Pump(200);
                        string baseName = $"{theme.Id.ToLowerInvariant()}/{size.Name}/fixture-{fx.Id}";
                        SavePng(win.Capture(), System.IO.Path.Combine(shotDir, baseName + ".png"));
                        if (fx.Page == "lighting")
                        {
                            win.LightingView.ScrollToDevices();
                            Pump(500); win.Tick(); Pump(200);
                            SavePng(win.Capture(), System.IO.Path.Combine(shotDir, baseName + "-devices.png"));
                        }
                        win.Close();
                        Log.Info($"fixture {baseName}: rendered");
                    }
                    catch (Exception ex) { failures++; Log.Error($"fixture {fx.Id} ({theme.Id}, {size.Name})", ex); }
                }
            }
        }
        return failures;
    }
}
