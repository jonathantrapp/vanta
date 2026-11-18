using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Ui;

public sealed class SystemPage : UserControl
{
    readonly AppServices _svc;
    readonly Grid _info = new();
    readonly Dictionary<string, TextBlock> _infoRows = new();
    readonly TextBox _serviceOutput = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0), FontSize = 11 };
    readonly TextBox _log = new() { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 11, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly Queue<string> _logLines = new();
    readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly Button _stop = new() { Content = "Stop", ToolTip = "Run Toggle-Gigabyte.ps1 -Action Stop, then check that they really stopped." };
    readonly Button _start = new() { Content = "Restore", ToolTip = "Turn fan control and lighting off first, then start only the services VANTA stopped." };
    readonly Button _status = new() { Content = "Refresh", Style = B.S("GhostBtn") };
    readonly TextBlock _gbState = B.Text("", null, 12);
    readonly ToggleButton _restoreSwitch = new() { Style = B.S("Switch") };
    readonly Button _repointTask = new() { Content = "Point task here", Style = B.S("GhostBtn"), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    readonly TextBlock _watchdog = B.Text("", null, 12);
    readonly ToggleButton _logonSwitch = new() { Style = B.S("Switch") };
    readonly ToggleButton _autoStopSwitch = new() { Style = B.S("Switch") };
    readonly TextBlock _startupStatus = B.Text("", "MonoMuted", 11, null, new Thickness(0, 8, 0, 0));
    readonly Expander _inspector = new() { Margin = new Thickness(0, 0, 0, 8) };
    readonly TextBlock _inspect = new() { FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"), FontSize = 12, TextWrapping = TextWrapping.NoWrap };
    bool _logDirty;
    DateTime _lastInfo = DateTime.MinValue, _lastInspect = DateTime.MinValue;

    public SystemPage(AppServices svc)
    {
        _svc = svc;
        B.Fg(_inspect, "TextSecondaryBrush");
        _log.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
        _log.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"); _log.FontSize = 12;
        _serviceOutput.FontSize = 12;
        var body = new Grid { MaxWidth = 1880 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- left: appearance, startup, services, backends
        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var picker = new ThemePicker();
        picker.Picked += t => MainWindow.ApplyTheme(_svc, t.Id);
        var appearance = new StackPanel();
        appearance.Children.Add(picker);
        appearance.Children.Add(B.Para(Motion.Reduced
            ? "Animations: off (Windows \"Show animations\" is off), so transitions are instant."
            : "Animations follow Windows: turning off \"Show animations\" in Windows settings makes every transition instant.", "Label", new Thickness(2, 0, 0, 0)));
        left.Children.Add(Panel("Appearance", appearance, null, "Changes only how VANTA looks. Lighting colours on your devices are not affected."));
        _info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        _info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var key in new[] { "Privileges", "PawnIO", "GPU", "Sensors", "Super I/O", "SMBus", "RAM lighting", "Board lighting", "Mouse", "Keyboard", "Data folder" })
            AddInfo(key);

        var startup = new StackPanel();
        startup.Children.Add(SwitchRow("Run at logon", "Elevated scheduled task: starts VANTA minimized at logon with no UAC prompt. A second copy exits on its own.", _logonSwitch));
        startup.Children.Add(_repointTask);
        startup.Children.Add(SwitchRow("Auto-stop Gigabyte", "While fan or lighting control is on: stop EasyTune, GService and RGB Fusion and check they stopped (retried at most once a minute). Off, or a failed stop: VANTA withholds the hardware they use.", _autoStopSwitch));
        startup.Children.Add(SwitchRow("Restore on exit", "On a normal exit (and from the watchdog after a crash) start again only the services VANTA stopped, after fans and lighting are handed back. Not during Windows shutdown.", _restoreSwitch));
        _startupStatus.TextAlignment = TextAlignment.Left;
        _startupStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        startup.Children.Add(_startupStatus);
        left.Children.Add(Panel("Startup", startup));

        var svcStack = new StackPanel();
        _gbState.TextTrimming = TextTrimming.CharacterEllipsis;
        svcStack.Children.Add(_gbState);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        _stop.Margin = new Thickness(0, 0, 6, 0); _start.Margin = new Thickness(0, 0, 6, 0);
        btns.Children.Add(_stop); btns.Children.Add(_start); btns.Children.Add(_status);
        svcStack.Children.Add(btns);
        svcStack.Children.Add(_serviceOutput);
        left.Children.Add(Panel("Gigabyte services", svcStack, "EasyTune, GService and RGB Fusion use the same Super I/O chip, SMBus and lighting controller. While any runs, VANTA does not write to that hardware."));
        left.Children.Add(Panel("Hardware backends", _info));
        body.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = left, Focusable = false });

        // ---- right: safety, inspector, log
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var safety = new StackPanel();
        _watchdog.TextTrimming = TextTrimming.CharacterEllipsis;
        safety.Children.Add(_watchdog);
        // the core rules stay visible (details in the tooltip)
        foreach (var rule in new[]
        {
            "Only identified fans with an evidence-backed minimum are driven; all others stay on firmware.",
            $"CPU {FanEngine.CpuEmergencyC:0} °C or GPU {FanEngine.GpuEmergencyC:0} °C: every driven fan goes to 100%.",
            "Missing temperature: the affected fans go back to firmware. Lost fan signal on a driven fan: 100%.",
            "If VANTA stops or hangs, a separate watchdog hands the fans back (board headers 100% until reboot)."
        })
        {
            var line = B.Text("·  " + rule, "Muted", null, null, new Thickness(0, 4, 0, 0));
            line.TextWrapping = TextWrapping.Wrap; line.TextTrimming = TextTrimming.None;
            safety.Children.Add(line);
        }
        var sbtn = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var release = new Button { Content = "Release all fans", Style = B.S("PrimaryBtn"), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Hand every fan back to its firmware now and switch fan control off." };
        release.Click += (_, _) => { _svc.Config.FanControlEnabled = false; _svc.Hub.Engine.ReleaseAll(); ConfigStore.Save(_svc.Config); };
        var folder = new Button { Content = "Data folder", Style = B.S("GhostBtn") };
        folder.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Data) { UseShellExecute = true }); } catch { } };
        sbtn.Children.Add(release); sbtn.Children.Add(folder);
        safety.Children.Add(sbtn);
        right.Children.Add(Panel("Fan safety", safety,
            $"Only identified channels with an evidence-backed floor are driven: GPU fans (NVML minimum), AIO and pumps (60% policy floor), case and cooler fans after you confirm a minimum. " +
            $"CPU {FanEngine.CpuEmergencyC:0} °C or GPU {FanEngine.GpuEmergencyC:0} °C forces driven fans to 100%. A missing or stale temperature hands the affected fans back; a lost speed signal on a driven pump or fan holds 100%; " +
            "repeated write failures hand the channel back. Fans are handed back before sleep and taken again 10 s after wake."));

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Icons.Icon("chip", 16, "TextSecondaryBrush"));
        head.Children.Add(B.Text("Hardware inspector", "CardTitle", null, null, new Thickness(10, 0, 0, 0)));
        head.Children.Add(B.Text("bus counters, controllers, threads", "Muted", null, null, new Thickness(12, 1, 0, 0)));
        _inspector.Header = head;
        _inspector.Content = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360, Content = _inspect };
        _inspector.Expanded += (_, _) => { _lastInspect = DateTime.MinValue; UpdateInspector(); };
        Grid.SetRow(_inspector, 1);
        right.Children.Add(_inspector);

        var logGrid = new Grid();
        logGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        logGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        logGrid.Children.Add(B.Text("Event log", "CardTitle", null, null, new Thickness(0, 0, 0, 8)));
        Grid.SetRow(_log, 1);
        logGrid.Children.Add(_log);
        var logCard = new Border { Style = B.S("Card"), Child = logGrid };
        Grid.SetRow(logCard, 2);
        right.Children.Add(logCard);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        Content = body;

        _stop.Click += (_, _) => StopServices();
        _start.Click += (_, _) => RestoreServices();
        _status.Click += (_, _) => RunServiceAction("Status");
        _restoreSwitch.Click += (_, _) =>
        {
            lock (_svc.Config.Sync) _svc.Config.RestoreGigabyteOnExit = _restoreSwitch.IsChecked == true;
            ConfigStore.Save(_svc.Config);
        };
        _repointTask.Click += (_, _) => OnLogonToggled(force: true);
        _logonSwitch.Click += (_, _) => OnLogonToggled();
        _autoStopSwitch.Click += (_, _) =>
        {
            bool on = _autoStopSwitch.IsChecked == true;
            lock (_svc.Config.Sync) _svc.Config.AutoStopGigabyte = on;
            ConfigStore.Save(_svc.Config);
            _startupStatus.Text = on ? "auto-stop on" : "auto-stop off: Gigabyte tools keep running, their hardware stays withheld";
            if (on) _svc.Ownership?.ResetStopAttempts();
        };
        Loaded += async (_, _) =>
        {
            _autoStopSwitch.IsChecked = _svc.Config.AutoStopGigabyte;
            _restoreSwitch.IsChecked = _svc.Config.RestoreGigabyteOnExit;
            await RefreshTaskState();
        };
        Log.LineWritten += OnLogLine;
        _logTimer.Tick += (_, _) => { FlushLog(); if (_inspector.IsExpanded) UpdateInspector(); };
        UiActivity.Bind(this, _logTimer);   // the log view refreshes only while the window is shown (lines stay queued, bounded)
    }

    /// <summary>Open the Hardware Inspector drawer (screenshots).</summary>
    public void ExpandInspector() => _inspector.IsExpanded = true;

    static Border Panel(string title, UIElement content, string? tip = null, string? subtitle = null) =>
        B.Section(title, content, subtitle, null, new Thickness(0, 0, 0, 12), tip);

    static Grid SwitchRow(string title, string tip, ToggleButton sw)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 8), ToolTip = tip };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ts = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        ts.Children.Add(B.Text(title, null));
        var d = B.Para(tip, "Label", new Thickness(0, 2, 0, 0));
        ts.Children.Add(d);
        g.Children.Add(ts);
        sw.VerticalAlignment = VerticalAlignment.Top; sw.Margin = new Thickness(0, 2, 0, 0);
        sw.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, title);
        Grid.SetColumn(sw, 1);
        g.Children.Add(sw);
        g.Background = Brushes.Transparent;   // tooltip over the whole row
        return g;
    }

    async void OnLogonToggled(bool force = false)
    {
        bool want = force || _logonSwitch.IsChecked == true;
        _logonSwitch.IsEnabled = false;
        _startupStatus.Text = want ? "creating task..." : "removing task...";
        var (ok, message) = await Task.Run(() => want ? StartupTask.Create() : StartupTask.Remove());
        _startupStatus.Text = ok ? message : "failed: " + message;
        _startupStatus.ToolTip = _startupStatus.Text;
        await RefreshTaskState(keepStatus: true);
        _logonSwitch.IsEnabled = true;
    }

    /// <summary>Shows the task's real state as Windows reports it, including which executable it starts.</summary>
    async Task RefreshTaskState(bool keepStatus = false)
    {
        if (_svc.Fixture != null) return;   // fixtures query nothing
        var (exists, target) = await Task.Run(() => (StartupTask.Exists(), StartupTask.ReadTarget()));
        _logonSwitch.IsChecked = exists;
        bool mismatch = exists && !StartupTask.SameExecutable(target, Environment.ProcessPath);
        _repointTask.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
        if (keepStatus && !mismatch) return;
        _startupStatus.Text = !exists ? "task: not installed" : mismatch ? "task starts a different exe" : "task: installed";
        _startupStatus.ToolTip = !exists ? null : $"Task starts {target} --minimized" + (mismatch ? $"; this exe is {Environment.ProcessPath}" : "");
    }

    async void StopServices()
    {
        _stop.IsEnabled = _start.IsEnabled = _status.IsEnabled = false;
        _serviceOutput.Text = "Stopping and checking...";
        var r = await Task.Run(() => _svc.Gigabyte.StopAndRecord("owner pressed Stop services"));
        _svc.Ownership?.ResetStopAttempts();
        _serviceOutput.Text = (r.Verified ? "" : "NOT fully stopped. ") + r.Message;
        _stop.IsEnabled = _start.IsEnabled = _status.IsEnabled = true;
    }

    async void RestoreServices()
    {
        _stop.IsEnabled = _start.IsEnabled = _status.IsEnabled = false;
        // hand everything back first: never restart a competing controller while HwSuite still owns the hardware
        lock (_svc.Config.Sync) { _svc.Config.FanControlEnabled = false; _svc.Config.Rgb.Enabled = false; _svc.Config.AutoStopGigabyte = false; }
        _autoStopSwitch.IsChecked = false;
        ConfigStore.Save(_svc.Config);
        _serviceOutput.Text = "Handing fans and lighting back, then starting the services VANTA stopped...";
        var results = await Task.Run(() =>
        {
            _svc.Hub.Engine.ReleaseAll();
            Thread.Sleep(1500);   // let the lighting engine finish its last frame and the fan loop see the change
            return _svc.Gigabyte.RestoreRecorded();
        });
        _serviceOutput.Text = results.Count == 0 ? "VANTA had not stopped any service." : string.Join(Environment.NewLine, results) +
            Environment.NewLine + "Fan control, lighting output and auto-stop are now off.";
        _stop.IsEnabled = _start.IsEnabled = _status.IsEnabled = true;
    }

    void AddInfo(string key)
    {
        int r = _info.RowDefinitions.Count;
        _info.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        var k = B.Text(key, "Muted"); k.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(k, r); _info.Children.Add(k);
        var v = B.Text("", null); v.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(v, r); Grid.SetColumn(v, 1); _info.Children.Add(v);
        if (r > 0)
        {
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0) };
            B.Bd(line, "HairlineBrush");
            Grid.SetRow(line, r); Grid.SetColumnSpan(line, 2); _info.Children.Add(line);
        }
        _infoRows[key] = v;
    }

    void SetInfo(string key, string shortText, string? full = null)
    {
        _infoRows[key].Text = shortText;
        _infoRows[key].ToolTip = full ?? shortText;
    }

    void OnLogLine(string line)
    {
        lock (_logLines)
        {
            _logLines.Enqueue(line);
            while (_logLines.Count > 400) _logLines.Dequeue();
            _logDirty = true;
        }
    }

    void FlushLog()
    {
        string text;
        lock (_logLines)
        {
            if (!_logDirty) return;
            _logDirty = false;
            text = string.Join(Environment.NewLine, _logLines);
        }
        _log.Text = text;
        _log.ScrollToEnd();
    }

    /// <summary>The window closed (fixtures create several): stop listening to the process-wide log.</summary>
    public void Detach() => Log.LineWritten -= OnLogLine;

    async void RunServiceAction(string action)
    {
        _stop.IsEnabled = _start.IsEnabled = _status.IsEnabled = false;
        _serviceOutput.Text = $"Toggle-Gigabyte.ps1 -Action {action} ...";
        var (ok, output) = await Task.Run(() => GigabyteServices.Run(action));
        _serviceOutput.Text = (ok ? "" : "FAILED: ") + output;
        _stop.IsEnabled = _start.IsEnabled = _status.IsEnabled = true;
    }

    public void Refresh(TelemetrySnapshot s)
    {
        if ((DateTime.Now - _lastInfo).TotalSeconds < 2) return;
        _lastInfo = DateTime.Now;
        if (_svc.Fixture != null)
        {
            foreach (var (k, v) in new[] { ("Privileges", "Administrator (simulated)"), ("PawnIO", "2.0.1  signed (simulated)"), ("GPU", "RTX 3070  2 fans  30-100%"), ("Sensors", "LibreHardwareMonitor (simulated)"),
                ("Super I/O", "IT8689E  6 PWM  5 tach"), ("SMBus", "i801  open"), ("RAM lighting", "4 ENE controllers  5/8/5/8 LEDs"), ("Board lighting", "IT5701"), ("Mouse", "Model O  receiver"),
                ("Keyboard", "i87  verified, live frames"), ("Data folder", "%APPDATA%\\HwSuite") })
                SetInfo(k, v, UiFixture.Notice);
            _watchdog.Text = "Watchdog armed · simulated";
            _gbState.Text = "None running (simulated)";
            _startupStatus.Text = "Simulated fixture: the logon task was not queried";
            return;
        }
        var hub = _svc.Hub;
        bool admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        SetInfo("Privileges", admin ? "Administrator" : "Standard user", admin ? null : "Hardware access will fail without administrator rights.");
        SetInfo("PawnIO", hub.PawnIoInstalled ? $"{hub.PawnIoVersion ?? "?"}  signed" : "not installed", hub.PawnIoInstalled ? null : "winget install namazso.PawnIO");
        SetInfo("GPU", hub.Gpu != null ? $"{hub.Gpu.Name.Replace("NVIDIA GeForce ", "")}  {hub.Gpu.FanCount} fans  {hub.Gpu.MinFanPercent}-{hub.Gpu.MaxFanPercent}%" : "NVML unavailable",
            hub.Gpu != null ? $"{hub.Gpu.Name}, {hub.Gpu.FanCount} fans, NVML manual range {hub.Gpu.MinFanPercent}-{hub.Gpu.MaxFanPercent}%" : null);
        SetInfo("Sensors", hub.Lhm.IsOpen ? $"LibreHardwareMonitor  {hub.Lhm.Sensors.Count}" : "not open", hub.Lhm.IsOpen ? null : hub.Lhm.Error);
        var ctl = hub.Engine.Channels.Count(c => c.Backend.StartsWith("SuperIO"));
        SetInfo("Super I/O", ctl > 0 ? $"IT8689E  {ctl} PWM  {s.Fans.Count(f => f.Source.StartsWith("ITE"))} tach" : "not available");
        SetInfo("SMBus", _svc.Smbus != null ? $"i801  open" : "not open", _svc.Smbus != null ? $"{_svc.Smbus.Identity} (PCI {_svc.Smbus.PciVendor:X4}:{_svc.Smbus.PciDevice:X4})" : null);
        var ram = _svc.Ene?.Dimms;
        var mods = MemoryTopology.Modules;
        SetInfo("RAM lighting", ram is { Count: > 0 } ? $"{ram.Count(d => d.Supported)} ENE controllers  {string.Join("/", ram.Where(d => d.Supported).Select(d => d.LedCount))} LEDs" : "no ENE controllers",
            $"ENE lighting controllers found on the SMBus (which slot each sits in is not known). Installed memory (SMBIOS): {MemoryTopology.Summary(mods)}{Environment.NewLine}{MemoryTopology.Detail(mods)}");
        var mb = _svc.Rgb.Sinks.OfType<MotherboardSink>().FirstOrDefault();
        SetInfo("Board lighting", mb?.Available == true ? "IT5701" : "unavailable", mb?.Status);
        var mouse = _svc.Rgb.Sinks.OfType<MouseSink>().FirstOrDefault();
        SetInfo("Mouse", mouse?.Controller.Connected == true ? "Model O  connected" : "Model O  " + (mouse?.Controller.LinkStatus ?? "n/a"), mouse?.Status);
        var al80 = _svc.Al80;
        SetInfo("Keyboard", al80 == null ? "n/a" : "AL80  " + (!_svc.Config.Al80.Enabled ? "control off" : al80.Connected ? "connected" : "not connected"), al80?.Status);
        SetInfo("Data folder", AppPaths.Data.StartsWith(@"\\") ? "real profile (via share)" : "%APPDATA%\\HwSuite", AppPaths.Data + (AppPaths.ResolutionNote != null ? Environment.NewLine + AppPaths.ResolutionNote : ""));

        var ctrl = ControlRegistry.Load();
        _watchdog.Text = $"Watchdog armed   {ctrl.Count} channel(s) driven";
        _watchdog.ToolTip = $"A separate watchdog process takes over if VANTA ends or its fan loop is silent for {FailsafeWatchdog.HangSeconds:0} s: GPU fans return to the VBIOS curve; " +
                            "motherboard headers go to 100% (BIOS control can only be handed back by the process that took it; a reboot restores it). " +
                            "Nothing protects against the watchdog itself being killed, a Windows crash or power loss; the CPU and GPU still throttle on their own.";
        var own = _svc.Ownership;
        var rec = _svc.Gigabyte.LoadRecord();
        _gbState.Text = (own == null ? "" : own.GigabyteRunning.Count > 0 ? "Running: " + string.Join(", ", own.GigabyteRunning) : "None running") +
                        (rec.StoppedServices.Count > 0 ? "   stopped by VANTA: " + string.Join(", ", rec.StoppedServices) : "");
        _gbState.ToolTip = own?.Check(HwResource.SuperIo) is { MayWrite: false } d ? "Super I/O: " + d.Reason : null;
    }

    void UpdateInspector()
    {
        if ((DateTime.Now - _lastInspect).TotalSeconds < 1.5) return;
        _lastInspect = DateTime.Now;
        var sb = new System.Text.StringBuilder();
        var bus = _svc.Smbus;
        if (bus != null)
        {
            double tick = Stopwatch.Frequency;
            sb.AppendLine($"SMBus       {bus.Identity}  PCI {bus.PciVendor:X4}:{bus.PciDevice:X4}  SPD-WP {(bus.SpdWriteProtected == true ? "on" : "unknown")}");
            sb.AppendLine($"            tx {bus.TransactionCount}  NACK {bus.ErrorCount}  in PawnIO {bus.IoctlTicks / tick:0.0} s  mutex wait {bus.MutexWaitTicks / tick * 1000:0.0} ms  locks {bus.LockCount}");
        }
        else sb.AppendLine("SMBus       not open");
        foreach (var d in _svc.Ene?.Dimms ?? new List<EneDimm>())
            sb.AppendLine($"ENE 0x{d.Address:X2}    {d.Version,-18} LEDs {d.LedCount,2}  supported {d.Supported}  block {(d.WholeBlockOk is bool b ? (b ? "verified" : "fallback") : "unverified")}  writes {d.ModuleWrites}  skips {d.ModuleSkips}");
        sb.AppendLine();
        foreach (var s in _svc.Rgb.Sinks)
            sb.AppendLine($"{s.Name,-44} {s.StatsText}");
        sb.AppendLine($"{"lighting engine",-44} {_svc.Rgb.Fps:0.0} fps  target {_svc.Rgb.RenderHz}/s  ticks {_svc.Rgb.Ticks}");
        sb.AppendLine();
        try
        {
            foreach (var i in HidEnumerator.Find(HwSuite.Hardware.Al80.Al80Protocol.Vid, HwSuite.Hardware.Al80.Al80Protocol.Pid))
                sb.AppendLine($"HID {i.Vid:X4}:{i.Pid:X4} [{i.UsageText}] in {i.MaxInput}  {(i.HasUsage(0xFF60, 0x61) ? "<- AL80 screen/VIA" : "")}");
            foreach (var i in GloriousWirelessDevice.Enumerate())
                sb.AppendLine($"HID {i.Vid:X4}:{i.Pid:X4} [{i.UsageText}] feat {i.MaxFeature}");
        }
        catch (Exception ex) { sb.AppendLine("HID enumeration failed: " + ex.Message); }
        if (_svc.Al80 is { } al) sb.AppendLine($"AL80: {al.Status}; lighting {al.LightStatus}; {al.LightWrites} colour writes, {al.LightFailures} failures");
        sb.AppendLine();
        sb.AppendLine("Thread CPU (total since start)");
        foreach (var t in ThreadCpu.Snapshot().OrderByDescending(t => (t.User + t.Kernel).TotalMilliseconds).Take(10))
            sb.AppendLine($"  {t.Name,-46} user {t.User.TotalSeconds,8:0.00} s  kernel {t.Kernel.TotalSeconds,8:0.00} s");
        sb.AppendLine();
        sb.AppendLine($"data  {AppPaths.Data}");
        if (AppPaths.ResolutionNote != null) sb.AppendLine("      " + AppPaths.ResolutionNote);
        _inspect.Text = sb.ToString().TrimEnd();
    }
}
