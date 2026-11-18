using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Lighting;

namespace HwSuite.Ui;

public sealed class DashboardPage : UserControl
{
    /// <summary>One metric: label, big readout, 60 s sparkline, load bar, key/value stats.</summary>
    sealed class Tile
    {
        public Border Root = null!;
        public TextBlock Model = null!, Value = null!, Unit = null!;
        public Sparkline Spark = null!;
        public MeterBar Bar = null!;
        public readonly Dictionary<string, TextBlock> Stats = new();

        public static Tile Build(string icon, string label, string unit, string[] stats, string sparkToken, double sparkSpan)
        {
            var t = new Tile();
            var g = new StackPanel();
            var head = new DockPanel();
            var ic = Icons.Icon(icon, 16, "TextSecondaryBrush"); ic.Margin = new Thickness(0, 0, 8, 0);
            head.Children.Add(ic);
            t.Model = B.Text("", "Label"); DockPanel.SetDock(t.Model, Dock.Right); t.Model.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(t.Model);
            head.Children.Remove(t.Model); head.Children.Insert(1, t.Model);
            var title = B.Text(label, "CardTitle"); title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(title);
            g.Children.Add(head);

            var mid = new Grid { Margin = new Thickness(0, 10, 0, 0), Height = 42 };
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var num = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, MinWidth = 86 };
            t.Value = B.Fg(B.Text("--", "BigNumber"), "TextBrush"); t.Value.TextAlignment = TextAlignment.Left;
            num.Children.Add(t.Value);
            t.Unit = B.Text(unit, "Muted", 14, null, new Thickness(4, 0, 0, 6)); t.Unit.VerticalAlignment = VerticalAlignment.Bottom;
            num.Children.Add(t.Unit);
            mid.Children.Add(num);
            t.Spark = new Sparkline { StrokeToken = sparkToken, MinSpan = sparkSpan, Margin = new Thickness(14, 4, 0, 4), ToolTip = "Last 60 seconds" };
            Grid.SetColumn(t.Spark, 1);
            mid.Children.Add(t.Spark);
            g.Children.Add(mid);

            t.Bar = new MeterBar { Height = 4, Margin = new Thickness(0, 10, 0, 10) };
            g.Children.Add(t.Bar);

            var kv = new Grid();
            kv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kv.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < stats.Length; i++)
            {
                kv.RowDefinitions.Add(new RowDefinition { Height = new GridLength(23) });
                var l = B.Text(stats[i], "Muted"); Grid.SetRow(l, i); l.VerticalAlignment = VerticalAlignment.Center; kv.Children.Add(l);
                var v = B.Fg(B.Text("--", "Mono"), "TextBrush"); Grid.SetRow(v, i); Grid.SetColumn(v, 1); v.VerticalAlignment = VerticalAlignment.Center; kv.Children.Add(v);
                t.Stats[stats[i]] = v;
            }
            g.Children.Add(kv);
            t.Root = new Border { Style = B.S("Card"), Child = g };
            return t;
        }
    }

    sealed class FanRow
    {
        public Grid Root = null!;
        public TextBlock Name = null!, Sub = null!, Rpm = null!, Duty = null!, RpmUnit = null!;
        public StatusPill Mode = null!;
        public MeterBar Bar = null!;
        public Border IconHost = null!;
        public string? IconName;
    }

    /// <summary>Readouts at or above this turn red; below it no red is used anywhere on the page.</summary>
    public const double HotC = 85;

    readonly AppServices _svc;
    readonly Tile _cpu, _gpu, _mem, _board;
    readonly HistoryChart _chart = new() { MinHeight = 220 };
    readonly StackPanel _fanList = new();
    readonly Dictionary<string, FanRow> _fanRows = new();
    readonly StackPanel _alerts = new();
    readonly AfterburnerBanner _banner;
    readonly TextBlock _fanEmpty;
    readonly Grid _lower = new();
    readonly Border _chartCard, _coolCard, _statusCard;
    readonly StackPanel _rightStack = new();
    readonly Dictionary<string, (TextBlock value, TextBlock detail)> _status = new();
    DateTime _lastSample = DateTime.MinValue;
    bool? _wide;

    public DashboardPage(AppServices svc)
    {
        _svc = svc;
        var root = new Grid { MaxWidth = 1880 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _banner = new AfterburnerBanner(svc);
        var alertStack = new StackPanel();
        alertStack.Children.Add(_banner);
        alertStack.Children.Add(_alerts);
        root.Children.Add(alertStack);

        var tiles = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        for (int i = 0; i < 4; i++) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _cpu = Tile.Build("chip", "CPU", "°C", new[] { "Load", "Clock", "Package power" }, "Series1", 6);
        _gpu = Tile.Build("gpu", "GPU", "°C", new[] { "Load", "Core clock", "Board power" }, "Series2", 6);
        _mem = Tile.Build("dimm", "Memory", "%", new[] { "In use", "Installed", "Modules" }, "Series3", 4);
        _board = Tile.Build("motherboard", "Motherboard", "°C", new[] { "Hottest sensor", "Coolest sensor", "Sensors" }, "Series3", 6);
        var all = new[] { _cpu, _gpu, _mem, _board };
        for (int i = 0; i < 4; i++)
        {
            all[i].Root.Margin = new Thickness(0, 0, i < 3 ? 12 : 0, 0);
            Grid.SetColumn(all[i].Root, i);
            tiles.Children.Add(all[i].Root);
        }
        Grid.SetRow(tiles, 1);
        root.Children.Add(tiles);

        // temperature history
        _chart.SeriesList.Add(new HistoryChart.Series("CPU", "Series1", p => p.Cpu));
        _chart.SeriesList.Add(new HistoryChart.Series("GPU", "Series2", p => p.Gpu));
        var legend = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (name, token) in new[] { ("CPU", "Series1Brush"), ("GPU", "Series2Brush") })
        {
            var sw = new Border { Width = 14, Height = 3, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) };
            B.Bg(sw, token);
            legend.Children.Add(sw);
            legend.Children.Add(B.Text(name, "Muted"));
        }
        var chartBody = new Grid();
        chartBody.Children.Add(_chart);
        _chartCard = B.Section("Temperature · last 5 minutes", chartBody, null, legend, new Thickness(0));
        _chartCard.VerticalAlignment = VerticalAlignment.Top;
        // the section's inner stack must let the chart fill: replace it with a two-row grid
        var cs = (StackPanel)_chartCard.Child;
        var head = cs.Children[0]; cs.Children.Clear();
        var cg = new Grid();
        cg.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        cg.Children.Add(head);
        Grid.SetRow(chartBody, 1); cg.Children.Add(chartBody);
        _chartCard.Child = cg;

        // cooling list
        var coolStack = new StackPanel();
        _fanEmpty = B.Text("No fan channels or tachometer readings", "Muted", null, null, new Thickness(0, 4, 0, 0));
        coolStack.Children.Add(_fanEmpty);
        coolStack.Children.Add(_fanList);
        _coolCard = B.Section("Cooling", coolStack, null, null, new Thickness(0, 0, 0, 12));

        // status summary
        var st = new StackPanel();
        foreach (var key in new[] { "Fan control", "Lighting", "Hardware access" }) st.Children.Add(StatusRow(key));
        _statusCard = B.Section("Status", st, null, null, new Thickness(0));
        _rightStack.Children.Add(_coolCard);
        _rightStack.Children.Add(_statusCard);

        Grid.SetRow(_lower, 2);
        root.Children.Add(_lower);
        ArrangeLower(true);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = root, Focusable = false };
        SizeChanged += (_, e) => { ArrangeLower(e.NewSize.Width >= 1180); SizeChart(); };
        _rightStack.SizeChanged += (_, _) => SizeChart();
        _tiles = tiles;
    }

    readonly Grid _tiles;

    /// <summary>
    /// The history chart uses the height that is there (down to 260, up to 700 px): beside the cooling column it matches
    /// that column or the free height, whichever is larger, so the two read as one row; it never grows without limit.
    /// </summary>
    void SizeChart()
    {
        double free = ActualHeight - _tiles.ActualHeight - 12 - (_alerts.ActualHeight + _banner.ActualHeight);
        double target = _wide == true ? Math.Max(free, _rightStack.ActualHeight) : 380;
        double h = Math.Clamp(target, 260, 700);
        if (Math.Abs(_chartCard.Height - h) > 1 || double.IsNaN(_chartCard.Height)) _chartCard.Height = h;
    }

    FrameworkElement StatusRow(string key)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var k = B.Text(key, "Muted"); g.Children.Add(k);
        var sp = new StackPanel();
        var v = B.Text("", null); v.TextWrapping = TextWrapping.Wrap; v.TextTrimming = TextTrimming.None;
        var d = B.Para("", "Label"); d.Margin = new Thickness(0, 2, 0, 0);
        sp.Children.Add(v); sp.Children.Add(d);
        Grid.SetColumn(sp, 1); g.Children.Add(sp);
        _status[key] = (v, d);
        return g;
    }

    /// <summary>Wide: history (3/5) beside cooling + status (2/5). Narrow: history full width, cooling and status side by side below.</summary>
    void ArrangeLower(bool wide)
    {
        if (_wide == wide) return;
        _wide = wide;
        _lower.Children.Clear(); _lower.ColumnDefinitions.Clear(); _lower.RowDefinitions.Clear();
        _rightStack.Children.Clear();
        if (wide)
        {
            _lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            _lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 440, MaxWidth = 720 });
            _chartCard.Margin = new Thickness(0, 0, 12, 0);
            _chartCard.VerticalAlignment = VerticalAlignment.Top;
            _lower.Children.Add(_chartCard);
            _coolCard.Margin = new Thickness(0, 0, 0, 12); _statusCard.Margin = new Thickness(0);
            _rightStack.Children.Add(_coolCard); _rightStack.Children.Add(_statusCard);
            Grid.SetColumn(_rightStack, 1); Grid.SetRow(_rightStack, 0);
            _lower.Children.Add(_rightStack);
        }
        else
        {
            _lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            _lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            _lower.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _lower.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _chartCard.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetColumnSpan(_chartCard, 2); Grid.SetColumn(_chartCard, 0);
            _lower.Children.Add(_chartCard);
            _coolCard.Margin = new Thickness(0, 0, 12, 0); _statusCard.Margin = new Thickness(0);
            Grid.SetRow(_coolCard, 1); Grid.SetColumn(_coolCard, 0);
            Grid.SetRow(_statusCard, 1); Grid.SetColumn(_statusCard, 1); _statusCard.VerticalAlignment = VerticalAlignment.Top;
            _lower.Children.Add(_coolCard); _lower.Children.Add(_statusCard);
            return;
        }
        Grid.SetColumnSpan(_chartCard, 1);
    }

    static Grid FanColumns(UIElement icon, UIElement name, UIElement rpm, UIElement bar, UIElement pct, UIElement mode)
    {
        var g = new Grid();
        foreach (var w in new[] { 28.0, -1, 88, 56, 46, 136 })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        var cells = new[] { icon, name, rpm, bar, pct, mode };
        for (int i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            if (cells[i] is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(cells[i]);
        }
        ((FrameworkElement)bar).Margin = new Thickness(14, 0, 0, 0);
        ((FrameworkElement)mode).Margin = new Thickness(14, 0, 0, 0);
        return g;
    }

    public void Refresh(TelemetrySnapshot s)
    {
        bool fresh = s.Time != _lastSample;
        _cpu.Model.Text = "i7-12700K";
        _cpu.Value.Text = B.N(s.CpuTempC, "0");
        Meter(_cpu.Bar, s.CpuLoadPct);
        _cpu.Stats["Load"].Text = B.N(s.CpuLoadPct, "0") + " %";
        _cpu.Stats["Clock"].Text = B.N(s.CpuClockMHz, "0") + " MHz";
        _cpu.Stats["Package power"].Text = B.N(s.CpuPowerW, "0.0") + " W";
        _cpu.Root.ToolTip = s.CpuTempC.HasValue ? "Temperature source: " + s.CpuTempSource : "CPU temperature unavailable: " + s.CpuTempStatus;

        _gpu.Model.Text = s.GpuName.Replace("NVIDIA GeForce ", "");
        _gpu.Value.Text = B.N(s.GpuTempC, "0");
        Meter(_gpu.Bar, s.GpuLoadPct);
        _gpu.Stats["Load"].Text = B.N(s.GpuLoadPct, "0") + " %";
        _gpu.Stats["Core clock"].Text = B.N(s.GpuCoreMHz, "0") + " MHz";
        _gpu.Stats["Board power"].Text = B.N(s.GpuPowerW, "0.0") + " W";
        _gpu.Root.ToolTip = "NVML";

        var mods = MemoryTopology.Modules;
        _mem.Model.Text = MemoryTopology.SpeedText(mods);
        _mem.Value.Text = B.N(s.RamUsedPct, "0");
        Meter(_mem.Bar, s.RamUsedPct);
        _mem.Stats["In use"].Text = B.N(s.RamUsedGb, "0.0") + " GB";
        _mem.Stats["Installed"].Text = B.N(s.RamTotalGb, "0.0") + " GB";
        _mem.Stats["Modules"].Text = MemoryTopology.Summary(mods);
        _mem.Root.ToolTip = "Installed modules (SMBIOS):" + Environment.NewLine + MemoryTopology.Detail(mods);

        var temps = s.Temps.Where(t => t.Celsius.HasValue && t.Celsius > 0).Select(t => t.Celsius!.Value).ToList();
        _board.Model.Text = "IT8689E";
        _board.Value.Text = temps.Count > 0 ? temps.Max().ToString("0") : "--";
        Meter(_board.Bar, temps.Count > 0 ? temps.Max() : null);
        _board.Stats["Hottest sensor"].Text = temps.Count > 0 ? temps.Max().ToString("0.0") + " °C" : "--";
        _board.Stats["Coolest sensor"].Text = temps.Count > 0 ? temps.Min().ToString("0.0") + " °C" : "--";
        _board.Stats["Sensors"].Text = temps.Count.ToString();
        _board.Root.ToolTip = temps.Count > 0 ? "Super I/O via PawnIO" : "Super I/O unavailable";

        // red is reserved for thermal emergencies and faults
        _cpu.Value.SetResourceReference(TextBlock.ForegroundProperty, s.CpuTempC >= HotC ? "DangerBrush" : "TextBrush");
        _gpu.Value.SetResourceReference(TextBlock.ForegroundProperty, s.GpuTempC >= HotC ? "DangerBrush" : "TextBrush");
        _board.Value.SetResourceReference(TextBlock.ForegroundProperty, temps.Count > 0 && temps.Max() >= HotC ? "DangerBrush" : "TextBrush");

        if (fresh)
        {
            _lastSample = s.Time;
            var now = s.Time;
            var hist = _svc.History.Since(_chart.Window, now);
            var last60 = hist.Where(p => now - p.Time <= TimeSpan.FromSeconds(60)).ToList();
            _cpu.Spark.SetPoints(last60.Select(p => (p.Time, p.Cpu)), now);
            _gpu.Spark.SetPoints(last60.Select(p => (p.Time, p.Gpu)), now);
            _mem.Spark.SetPoints(last60.Select(p => (p.Time, p.Ram)), now);
            _board.Spark.SetPoints(last60.Select(p => (p.Time, p.Board)), now);
            _chart.SetData(hist, now, _svc.History.FirstSample);
        }

        RefreshFans(s);
        RefreshStatus();

        var alerts = new List<(string text, bool danger)>();
        if (_svc.Hub.Engine.Emergency) alerts.Add(("Thermal emergency: every driven fan is at 100% until temperatures recover.", true));
        if (!_svc.Hub.PawnIoInstalled) alerts.Add(("PawnIO is not installed: CPU temperature, motherboard fans and memory lighting are unavailable.", false));
        var own = _svc.Ownership;
        if (own != null && own.GigabyteRunning.Count > 0)
        {
            var d = own.Check(HwResource.SuperIo);
            alerts.Add(("Gigabyte tools running: " + string.Join(", ", own.GigabyteRunning) + ". " +
                (_svc.Config.FanControlEnabled || _svc.Config.Rgb.Enabled ? "Board fans, memory and board lighting are withheld: " + d.Reason + "." :
                 "They are stopped (and checked) when fan control or lighting is switched on" + (_svc.Config.AutoStopGigabyte ? "." : ", if auto-stop is on (System).")), false));
        }
        if (s.CpuTempC == null) alerts.Add(("CPU temperature: " + s.CpuTempStatus + ". Fans that follow it stay on firmware until it returns.", false));
        if (s.CpuTempSuspicion != null) alerts.Add(("Heuristic warning: " + s.CpuTempSuspicion + ".", false));
        foreach (var (time, text) in _svc.Alerts.Recent(TimeSpan.FromMinutes(30)).TakeLast(4)) alerts.Add(($"{time:HH:mm}  {text}", false));
        _banner.Update();
        SyncAlerts(alerts);
    }

    static void Meter(MeterBar bar, double? pct)
    {
        bar.Missing = !pct.HasValue;
        bar.AnimateTo((pct ?? 0) / 100.0);
        bar.InvalidateVisual();
    }

    void RefreshFans(TelemetrySnapshot s)
    {
        var channels = _svc.Hub.Engine.Channels;
        _fanEmpty.Visibility = channels.Count == 0 && s.Fans.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var ch in channels)
        {
            if (!_fanRows.TryGetValue(ch.Id, out var row))
            {
                row = BuildFanRow();
                _fanRows[ch.Id] = row;
                _fanList.Children.Add(row.Root);
            }
            var names = FanNames.For(_svc.Config, ch);
            row.Name.Text = names.Primary;
            row.Sub.Text = names.Secondary;
            var reading = ch.TachReadingId is string tid ? s.Fans.FirstOrDefault(f => f.Id == tid) : null;
            row.RpmUnit.Visibility = ch.TachReadingId == null ? Visibility.Collapsed : Visibility.Visible;
            if (ch.TachReadingId == null) { row.Rpm.Text = "no tach"; row.Rpm.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); row.Rpm.ToolTip = "This header has no tachometer input: its speed cannot be read."; }
            else if (reading?.Rpm is double rpm) { row.Rpm.Text = rpm.ToString("0"); row.Rpm.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); row.Rpm.ToolTip = rpm < 1 ? "The tachometer reads 0 rpm: stopped, or nothing connected." : $"{rpm:0} rpm"; }
            else { row.Rpm.Text = "—"; row.Rpm.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); row.Rpm.ToolTip = "No reading available right now (different from 0 rpm)."; }
            _svc.Hub.Engine.Status.TryGetValue(ch.Id, out var st);
            double? pct = st?.AppliedPercent ?? reading?.Percent ?? ch.ReadPercent();
            Meter(row.Bar, pct);
            row.Duty.Text = pct.HasValue ? pct.Value.ToString("0") + "%" : "—";
            var (mode, tone, full) = ModeFor(ch, st);
            row.Mode.Set(mode, tone);
            row.Root.ToolTip = $"{names.Primary} ({names.Secondary})" + (full.Length > 0 ? Environment.NewLine + full : "");
            var icon = IconFor(ch);
            if (icon != row.IconName) { row.IconName = icon; row.IconHost.Child = Icons.Icon(icon, 16, "TextSecondaryBrush"); }
        }
    }

    void RefreshStatus()
    {
        var c = _svc.Config;
        var eng = _svc.Hub.Engine;
        int driven = eng.Status.Values.Count(x => x.Controlled);
        Set("Fan control", !c.FanControlEnabled ? "Off · BIOS and GPU firmware control every fan" : $"On · VANTA drives {driven} of {eng.Channels.Count}",
            !c.FanControlEnabled ? "Switch it on in the sidebar; you choose the fans first." : "Others stay on firmware.");
        var devices = LightingDevices.Build(_svc);
        // "in step" needs the current look accepted by the device's interface; a device still waiting is not counted
        int following = devices.Count(d => d.Status.State == DeviceLightState.Shared && d.Status.Confirmed);
        var waiting = devices.Where(d => d.Status.State == DeviceLightState.Shared && !d.Status.Confirmed).ToList();
        int independent = devices.Count(d => d.Status.State == DeviceLightState.Independent);
        string light = !c.Rgb.Enabled ? "Output off" : LightsOut.IsOn(c.Rgb) ? "Lights out" : $"{EffectName(c.Rgb.Effect)} · {following} of {devices.Count} devices in step";
        string detail = !c.Rgb.Enabled || LightsOut.IsOn(c.Rgb) ? "" : independent > 0 ? $"{independent} run {(EffectInfo.IsScene(c.Rgb.Effect) ? "a calm companion look" : "their own onboard effect")} ({string.Join(", ", devices.Where(d => d.Status.State == DeviceLightState.Independent).Select(d => d.Title.ToLowerInvariant()))})." : "";
        if (c.Rgb.Enabled && !LightsOut.IsOn(c.Rgb) && waiting.Count > 0)
            detail = (detail.Length > 0 ? detail + " " : "") + $"Waiting for {string.Join(", ", waiting.Select(d => d.Title.ToLowerInvariant()))} to accept the current look.";
        var attention = devices.Where(d => d.Status.NeedsAttention).ToList();
        if (attention.Count > 0) detail = (detail.Length > 0 ? detail + " " : "") + string.Join("; ", attention.Select(d => $"{d.Title}: {d.Status.Headline.ToLowerInvariant()}")) + ".";
        Set("Lighting", light, detail);
        var own = _svc.Ownership;
        Set("Hardware access", own == null ? (_svc.Fixture != null ? "No conflicting programs (simulated)" : "Checking") : own.GigabyteRunning.Count > 0 ? "Gigabyte tools running" : "No conflicting programs",
            own?.GigabyteRunning.Count > 0 ? string.Join(", ", own.GigabyteRunning) : "");
    }

    void Set(string key, string value, string detail)
    {
        var (v, d) = _status[key];
        v.Text = value; d.Text = detail;
        d.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public static string EffectName(EffectKind e) => EffectInfo.Label(e);

    void SyncAlerts(List<(string text, bool danger)> alerts)
    {
        while (_alerts.Children.Count > alerts.Count) _alerts.Children.RemoveAt(_alerts.Children.Count - 1);
        while (_alerts.Children.Count < alerts.Count)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var ic = Icons.Icon("warn", 16, "WarnBrush"); ic.Margin = new Thickness(0, 0, 10, 0);
            g.Children.Add(ic);
            var t = B.Text("", null); t.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(t, 1); g.Children.Add(t);
            var b = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), Child = g };
            _alerts.Children.Add(b);
        }
        for (int i = 0; i < alerts.Count; i++)
        {
            var b = (Border)_alerts.Children[i];
            var t = (TextBlock)((Grid)b.Child).Children[1];
            t.Text = alerts[i].text;
            b.ToolTip = alerts[i].text;
            bool danger = alerts[i].danger;
            ((System.Windows.Shapes.Path)((Grid)b.Child).Children[0]).SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, danger ? "DangerBrush" : "WarnBrush");
            B.Bg(b, danger ? "DangerSoftBrush" : "WarnSoftBrush");
            B.Bd(b, danger ? "DangerBrush" : "HairlineBrush");
        }
        if (alerts.Count > 0) ((Border)_alerts.Children[^1]).Margin = new Thickness(0, 0, 0, 12);
    }

    string IconFor(IFanChannel ch)
    {
        if (ch.Resource == HwResource.GpuFans) return "gpu";
        var cc = _svc.Config.Channels.FirstOrDefault(c => c.Id == ch.Id);
        return cc?.Kind is FanKind.Aio or FanKind.Pump or FanKind.CpuCooler ? "aio" : "cooling";
    }

    (string mode, Tone tone, string full) ModeFor(IFanChannel ch, ChannelStatus? st)
    {
        string fw = ch.Resource == HwResource.GpuFans ? "GPU firmware" : "BIOS";
        if (st == null || !_svc.Config.FanControlEnabled) return (fw, Tone.Neutral, st?.State ?? "");
        if (st.State.StartsWith("not writing")) return ("Yielded", Tone.Warn, st.State);
        if (st.State.StartsWith("start check")) return ("Start check", Tone.Warn, st.State);
        if (st.State.StartsWith("faulted")) return ("Fault", Tone.Danger, st.State);
        if (st.Controlled && st.FeedbackLost) return ("Holding 100%", Tone.Warn, st.State);
        if (st.Controlled && st.Warning != null) return ("Raised minimum", Tone.Warn, st.State + "; " + st.Warning);
        if (st.Controlled) return (st.Mode == ChannelMode.Manual ? "VANTA manual" : "VANTA curve", Tone.Accent, st.State);
        return (fw, Tone.Neutral, st.State);
    }

    static FanRow BuildFanRow()
    {
        var row = new FanRow();
        row.IconHost = new Border();
        var names = new StackPanel();
        row.Name = B.Text("", null);
        row.Sub = B.Text("", "Label");
        names.Children.Add(row.Name); names.Children.Add(row.Sub);
        row.Rpm = B.Text("--", "Mono");
        var rpmCell = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        rpmCell.Children.Add(row.Rpm);
        row.RpmUnit = B.Text("rpm", "Label", null, null, new Thickness(4, 1, 0, 0));
        rpmCell.Children.Add(row.RpmUnit);
        row.Bar = new MeterBar { Height = 4 };
        row.Duty = B.Text("--", "MonoMuted");
        row.Mode = new StatusPill();
        var g = FanColumns(row.IconHost, names, rpmCell, row.Bar, row.Duty, row.Mode);
        g.MinHeight = 44;
        var wrap = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = g, Padding = new Thickness(0, 2, 0, 2) };
        B.Bd(wrap, "HairlineBrush");
        row.Root = new Grid(); row.Root.Children.Add(wrap);
        return row;
    }
}
