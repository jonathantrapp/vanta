using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;

namespace HwSuite.Ui;

/// <summary>
/// Cooling: who controls each fan, the curve, and (separately) calibration. A curve is only "active" while fan control is on,
/// the channel's mode is not Firmware and the engine reports it driven; otherwise it is a saved curve preview and is drawn
/// and labelled as such. Everyday controls (mode, source, minimum/maximum) are separate from calibration and advanced
/// settings (name, header type, identify, confirmed minimum, ramps, hysteresis).
/// </summary>
public sealed class FansPage : UserControl
{
    sealed class RowUi
    {
        public IFanChannel Channel = null!;
        public ListBoxItem Item = null!;
        public TextBlock Name = null!, Sub = null!, Rpm = null!;
        public StatusPill State = null!;
    }

    sealed class Param
    {
        public FrameworkElement Root = null!;
        public Slider Slider = null!;
        public TextBlock Value = null!;
    }

    static readonly (string label, FanKind kind)[] Kinds =
    {
        ("Pump", FanKind.Pump), ("AIO (pump + radiator fans)", FanKind.Aio), ("CPU cooler fans", FanKind.CpuCooler), ("Case fan", FanKind.CaseFan),
        ("GPU fan", FanKind.GpuFan), ("Unclassified header", FanKind.Generic)
    };

    readonly AppServices _svc;
    readonly ListBox _list = new();
    readonly Dictionary<string, RowUi> _rows = new();
    internal CurveEditor Editor => _editor;
    readonly CurveEditor _editor = new() { Height = 320, Margin = new Thickness(0, 8, 0, 6) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly TextBlock _title = B.Text("", "PageTitle"), _subtitle = B.Text("", "Muted", null, null, new Thickness(0, 2, 0, 0));
    readonly TextBlock _rpm = B.Fg(B.Text("--", "BigNumber"), "TextBrush"), _rpmUnit = B.Text("rpm", "Muted", 14, null, new Thickness(6, 0, 0, 5));
    readonly TextBlock _duty = B.Text("", "Muted");
    readonly TextBlock _tachNote = B.Para("", "Muted"), _hint = B.Para("", "Muted", new Thickness(0, 8, 0, 0));
    readonly TextBlock _identifyNote = B.Para("", "Muted", new Thickness(0, 6, 0, 0));
    readonly TextBlock _curveTitle = B.Text("", "CardTitle"), _curveNote = B.Para("", "Muted", new Thickness(0, 2, 0, 0));
    // control banner
    readonly Border _banner = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 16, 0, 16), BorderThickness = new Thickness(1) };
    readonly TextBlock _bannerTitle = B.Text("", "CardTitle"), _bannerBody = B.Para("", "Muted", new Thickness(0, 3, 0, 0));
    readonly System.Windows.Shapes.Path _bannerIcon = Icons.Icon("info", 20);
    readonly TextBox _labelBox = new() { Width = 300 };
    readonly Button _identify = new() { Content = "Identify (5 s at 100%)" };
    readonly Button _identifyLong = new() { Content = "10 s", Margin = new Thickness(6, 0, 0, 0) };
    readonly AfterburnerBanner _afterburner;
    readonly StackPanel _minPanel = new() { Margin = new Thickness(0, 16, 0, 0) };
    readonly TextBox _minBox = new() { Width = 64 };
    readonly TextBox _evidenceBox = new() { Width = 340 };
    readonly Button _minSave = new() { Content = "Save minimum", Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _minClear = new() { Content = "Clear", Margin = new Thickness(4, 0, 0, 0) };
    readonly Slider _testDuty = new() { Minimum = FanEngine.MinTestDuty, Maximum = 100, Value = 40, Width = 180, IsSnapToTickEnabled = true, TickFrequency = 5, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _testHold = new() { Content = "Hold 40% for 15 s", Margin = new Thickness(10, 0, 0, 0) };
    readonly TextBlock _minNote = B.Para("", "Muted", new Thickness(0, 6, 0, 0));
    readonly TextBlock _safetyLine = B.Para("", "Muted", new Thickness(0, 10, 0, 0));
    readonly ComboBox _kind = new() { Width = 240 }, _source = new() { Width = 220 };
    readonly RadioButton _modeAuto, _modeCurve, _modeManual;
    readonly Param _hyst, _up, _down, _min, _max, _release, _manual;
    readonly List<RadioButton> _presetButtons = new();
    readonly Expander _advanced = new() { Margin = new Thickness(0, 12, 0, 0) };

    IFanChannel? _sel;
    FanCurve _work = new();
    bool _loading;

    public FansPage(AppServices svc)
    {
        _svc = svc;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); ConfigStore.Save(_svc.Config); };
        _labelBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Display name");
        _minBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Lowest safe duty in percent");
        _evidenceBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "How you confirmed it");

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- left: channels, then the preset (affects several channels)
        var left = new Grid { Margin = new Thickness(0, 0, 16, 0) };
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var listGrid = new Grid();
        listGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        listGrid.Children.Add(B.Text("Fans", "CardTitle", null, null, new Thickness(16, 14, 0, 8)));
        Grid.SetRow(_list, 1);
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is ListBoxItem li && li.Tag is IFanChannel ch) SelectChannel(ch); };
        listGrid.Children.Add(_list);
        var listCard = new Border { Style = B.S("Card"), Padding = new Thickness(0, 0, 0, 6), Child = listGrid };
        left.Children.Add(listCard);

        var presetStack = new StackPanel();
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var name in FanPresets.Names)
        {
            var rb = new RadioButton { Content = name, Style = B.S("Segment"), GroupName = "preset", Tag = name, IsChecked = name == _svc.Config.Preset };
            rb.Click += OnPresetClicked;
            _presetButtons.Add(rb);
            seg.Children.Add(rb);
        }
        presetStack.Children.Add(new Border { Style = B.S("SegmentGroup"), Child = seg });
        _hint.Text = "Rewrites the curves of the fans VANTA may drive. Which fans are driven is still your choice when fan control is switched on.";
        presetStack.Children.Add(_hint);
        var presetCard = B.Section("Curve preset", presetStack, null, null, new Thickness(0, 12, 0, 0));
        Grid.SetRow(presetCard, 1);
        left.Children.Add(presetCard);
        root.Children.Add(left);

        // ---- right: the selected fan
        var ed = new StackPanel { MaxWidth = 1120, HorizontalAlignment = HorizontalAlignment.Left };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel();
        titleStack.Children.Add(_title);
        titleStack.Children.Add(_subtitle);
        head.Children.Add(titleStack);
        var meas = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var rpmRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        rpmRow.Children.Add(_rpm); rpmRow.Children.Add(_rpmUnit);
        _rpmUnit.VerticalAlignment = VerticalAlignment.Bottom;
        meas.Children.Add(rpmRow);
        _duty.HorizontalAlignment = HorizontalAlignment.Right;
        meas.Children.Add(_duty);
        Grid.SetColumn(meas, 1);
        head.Children.Add(meas);
        ed.Children.Add(head);

        // who controls it (prominent)
        var bg = new Grid();
        bg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bannerIcon.Margin = new Thickness(0, 0, 12, 0); _bannerIcon.VerticalAlignment = VerticalAlignment.Top;
        bg.Children.Add(_bannerIcon);
        var bs = new StackPanel(); bs.Children.Add(_bannerTitle); bs.Children.Add(_bannerBody);
        Grid.SetColumn(bs, 1); bg.Children.Add(bs);
        _banner.Child = bg;
        ed.Children.Add(_banner);

        // everyday controls
        var everyday = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var modeBorder = new Border { Style = B.S("SegmentGroup") };
        var modePanel = new StackPanel { Orientation = Orientation.Horizontal };
        _modeAuto = Seg("Firmware", "mode", ChannelMode.Auto);
        _modeCurve = Seg("VANTA curve", "mode", ChannelMode.Curve);
        _modeManual = Seg("Fixed duty", "mode", ChannelMode.Manual);
        modePanel.Children.Add(_modeAuto); modePanel.Children.Add(_modeCurve); modePanel.Children.Add(_modeManual);
        modeBorder.Child = modePanel;
        everyday.Children.Add(Field("Mode while fan control is on", modeBorder, new Thickness(0, 0, 28, 12)));
        _source.Items.Add("CPU temperature"); _source.Items.Add("GPU temperature"); _source.Items.Add("Hotter of CPU and GPU");
        _source.SelectionChanged += OnSourceChanged;
        everyday.Children.Add(Field("Curve follows", _source, new Thickness(0, 0, 0, 12)));
        ed.Children.Add(everyday);

        // curve
        var curveHead = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        curveHead.Children.Add(_curveTitle);
        curveHead.Children.Add(_curveNote);
        ed.Children.Add(curveHead);
        _editor.CurveEdited += OnCurveEdited;
        ed.Children.Add(_editor);
        ed.Children.Add(B.Para("Drag a point to change it · double-click adds a point · right-click removes one · keyboard: Ctrl+←/→ selects, arrows move (Shift ×5), Delete, Insert · Ctrl+Z / Ctrl+Y undo and redo", "Label", new Thickness(2, 0, 0, 14)));

        _min = MakeParam("Minimum duty", 0, 100, 1, "0", " %");
        _max = MakeParam("Maximum duty", 20, 100, 1, "0", " %");
        _manual = MakeParam("Fixed duty", 0, 100, 1, "0", " %");
        var everydayParams = new UniformGrid { Columns = 3 };
        foreach (var p in new[] { _min, _max, _manual }) everydayParams.Children.Add(p.Root);
        ed.Children.Add(everydayParams);
        _safetyLine.TextWrapping = TextWrapping.Wrap;
        ed.Children.Add(_safetyLine);

        // advanced and calibration
        var adv = new StackPanel();
        _hyst = MakeParam("Hysteresis", 0, 10, 0.5, "0.0", " °C");
        _up = MakeParam("Ramp up", 1, 100, 1, "0", " %/s");
        _down = MakeParam("Step down", 0.5, 20, 0.5, "0.0", " %/s");
        _release = MakeParam("Hand back to VBIOS below", 0, 60, 1, "0", " °C");
        var advParams = new UniformGrid { Columns = 3 };
        foreach (var p in new[] { _hyst, _up, _down, _release }) advParams.Children.Add(p.Root);
        adv.Children.Add(B.Text("Response", "Caption", null, null, new Thickness(0, 0, 0, 8)));
        adv.Children.Add(advParams);

        adv.Children.Add(new Border { Style = B.S("Divider") });
        adv.Children.Add(B.Text("Identity", "Caption", null, null, new Thickness(0, 0, 0, 8)));
        var idRow = new WrapPanel();
        idRow.Children.Add(Field("Display name", _labelBox, new Thickness(0, 0, 20, 10)));
        foreach (var k in Kinds) _kind.Items.Add(k.label);
        _kind.SelectionChanged += OnKindChanged;
        idRow.Children.Add(Field("Header type", _kind, new Thickness(0, 0, 20, 10)));
        var idButtons = new StackPanel { Orientation = Orientation.Horizontal };
        _identify.ToolTip = "Drive this header at 100% for 5 s to hear or see which fans it controls. The board smooths changes (about 50-100 rpm/s); a header already at full speed will not change.";
        _identify.Click += (_, _) => { if (_sel != null) ShowRefusal(_svc.Hub.Engine.StartIdentify(_sel.Id, 5)); };
        _identifyLong.ToolTip = "The same pulse for 10 seconds, for fans that spin up slowly.";
        _identifyLong.Click += (_, _) => { if (_sel != null) ShowRefusal(_svc.Hub.Engine.StartIdentify(_sel.Id, 10)); };
        idButtons.Children.Add(_identify); idButtons.Children.Add(_identifyLong);
        idRow.Children.Add(Field("Find this header", idButtons, new Thickness(0, 0, 0, 10)));
        adv.Children.Add(idRow);
        adv.Children.Add(_identifyNote);
        _labelBox.TextChanged += (_, _) => OnLabelChanged();

        BuildMinimumPanel();
        adv.Children.Add(_minPanel);
        adv.Children.Add(new Border { Style = B.S("Divider") });
        adv.Children.Add(B.Text("Tachometer and engine", "Caption", null, null, new Thickness(0, 0, 0, 6)));
        adv.Children.Add(_tachNote);

        var advHead = new StackPanel { Orientation = Orientation.Horizontal };
        advHead.Children.Add(B.Text("Calibration and advanced", "CardTitle"));
        advHead.Children.Add(B.Text("ramps, name, header type, identify, confirmed minimum", "Muted", null, null, new Thickness(12, 1, 0, 0)));
        _advanced.Header = advHead;
        _advanced.Content = adv;
        _advanced.Expanded += (_, _) => Motion.RevealIn(adv);
        ed.Children.Add(_advanced);

        var card = new Border { Style = B.S("Card"), Padding = new Thickness(24, 20, 24, 20), Child = ed };
        _afterburner = new AfterburnerBanner(svc);
        var rightStack = new StackPanel();
        rightStack.Children.Add(_afterburner);
        rightStack.Children.Add(card);
        var rightScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rightStack, Focusable = false };
        Grid.SetColumn(rightScroll, 1);
        root.Children.Add(rightScroll);

        Content = root;
        BuildChannelRows();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        else _title.Text = "No controllable fan channels found";
        SizeChanged += (_, e) => _editor.Height = Math.Clamp(e.NewSize.Height * 0.40, 260, 430);
        // WPF clears code-set RadioButton states when a group changes visual-tree scope, so re-apply on every attach
        Loaded += (_, _) => ReapplyChecks();
    }

    static FrameworkElement Field(string label, FrameworkElement control, Thickness margin)
    {
        var s = new StackPanel { Margin = margin };
        s.Children.Add(B.Text(label, "Caption", null, null, new Thickness(0, 0, 0, 6)));
        s.Children.Add(control);
        return s;
    }

    void ShowRefusal(string? reason)
    {
        if (reason != null) _identifyNote.Text = "Not started: " + reason;
    }

    void BuildMinimumPanel()
    {
        _minPanel.Children.Add(new Border { Style = B.S("Divider"), Margin = new Thickness(0, 0, 0, 12) });
        var minHead = B.Text("Confirmed minimum", "Caption", null, null, new Thickness(0, 0, 0, 4));
        _minPanel.Children.Add(minHead);
        _minPanel.Children.Add(B.Para("A case-fan or cooler header is driven only after you confirm the lowest duty at which every fan on it keeps spinning. " +
            $"Pick a duty, press Hold, and watch or listen for 15 s; repeat higher if any fan stops. Below {FanEngine.MinTestDuty:0}% is not offered; pumps and AIO headers cannot be tested this way.", "Muted", new Thickness(0, 0, 0, 10)));
        var row = new WrapPanel();
        var minField = new StackPanel { Orientation = Orientation.Horizontal };
        minField.Children.Add(_minBox);
        minField.Children.Add(B.Text("%", "Muted", null, null, new Thickness(6, 7, 0, 0)));
        row.Children.Add(Field("Lowest safe duty", minField, new Thickness(0, 0, 20, 10)));
        _evidenceBox.ToolTip = "For example: watched all three case fans keep spinning at 35%";
        row.Children.Add(Field("How you confirmed it", _evidenceBox, new Thickness(0, 0, 12, 10)));
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 10) };
        _minClear.Style = B.S("GhostBtn");
        btns.Children.Add(_minSave); btns.Children.Add(_minClear);
        row.Children.Add(btns);
        _minPanel.Children.Add(row);
        var test = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        test.Children.Add(_testDuty);
        test.Children.Add(_testHold);
        _minPanel.Children.Add(Field("Check a duty (attended)", test, new Thickness(0, 4, 0, 0)));
        _minPanel.Children.Add(_minNote);

        _testDuty.ValueChanged += (_, e) => _testHold.Content = $"Hold {e.NewValue:0}% for 15 s";
        _testHold.Click += (_, _) => { if (_sel != null) ShowRefusal(_svc.Hub.Engine.StartHold(_sel.Id, 15, _testDuty.Value)); };
        _minSave.Click += (_, _) =>
        {
            if (_sel == null) return;
            if (!double.TryParse(_minBox.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < FanEngine.MinTestDuty || v > 100)
            { _minNote.Text = $"Enter a duty between {FanEngine.MinTestDuty:0} and 100."; return; }
            if (string.IsNullOrWhiteSpace(_evidenceBox.Text)) { _minNote.Text = "Describe how you confirmed it (what you watched or heard)."; return; }
            var cc = ConfigFor(_sel);
            lock (_svc.Config.Sync) { cc.VerifiedMinPercent = Math.Round(v); cc.MinEvidence = _evidenceBox.Text.Trim() + $" (confirmed {DateTime.Now:yyyy-MM-dd})"; }
            ConfigStore.Save(_svc.Config);
            Log.Info($"{_sel.Id}: owner confirmed minimum {cc.VerifiedMinPercent:0}% ({cc.MinEvidence})");
            SelectChannel(_sel);
        };
        _minClear.Click += (_, _) =>
        {
            if (_sel == null) return;
            var cc = ConfigFor(_sel);
            lock (_svc.Config.Sync) { cc.VerifiedMinPercent = null; cc.MinEvidence = null; }
            ConfigStore.Save(_svc.Config);
            SelectChannel(_sel);
        };
    }

    void ReapplyChecks()
    {
        _loading = true;
        foreach (var rb in _presetButtons) rb.IsChecked = (rb.Tag as string) == _svc.Config.Preset;
        if (_sel != null)
        {
            var cc = ConfigFor(_sel);
            _modeAuto.IsChecked = cc.Mode == ChannelMode.Auto;
            _modeCurve.IsChecked = cc.Mode == ChannelMode.Curve;
            _modeManual.IsChecked = cc.Mode == ChannelMode.Manual;
        }
        _loading = false;
    }

    RadioButton Seg(string text, string group, ChannelMode mode)
    {
        var rb = new RadioButton { Content = text, Style = B.S("Segment"), GroupName = group, Tag = mode };
        rb.Click += (_, _) => OnModeClicked(mode);
        return rb;
    }

    Param MakeParam(string label, double min, double max, double step, string format, string suffix)
    {
        var p = new Param();
        var stack = new StackPanel { Margin = new Thickness(0, 0, 24, 14) };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(B.Text(label, "Muted"));
        p.Value = B.Fg(B.Text("", "Mono"), "TextBrush");
        Grid.SetColumn(p.Value, 1);
        top.Children.Add(p.Value);
        stack.Children.Add(top);
        p.Slider = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, TickFrequency = step, IsSnapToTickEnabled = true, Margin = new Thickness(0, 6, 0, 0) };
        p.Slider.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, label);
        p.Value.Text = p.Slider.Value.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + suffix;
        stack.Children.Add(p.Slider);
        p.Slider.ValueChanged += (_, e) =>
        {
            p.Value.Text = e.NewValue.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + suffix;
            if (!_loading) OnParamChanged();
        };
        p.Root = stack;
        return p;
    }

    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Channel modes changed elsewhere (fan-activation dialog): show them.</summary>
    public void Reload() { if (_sel != null) SelectChannel(_sel); }

    /// <summary>Select a channel programmatically (screenshots).</summary>
    public void SelectById(string id)
    {
        foreach (var item in _list.Items)
            if (item is ListBoxItem li && li.Tag is IFanChannel ch && ch.Id == id) { _list.SelectedItem = li; return; }
    }

    void OnLabelChanged()
    {
        if (_loading || _sel == null) return;
        var cc = ConfigFor(_sel);
        var text = _labelBox.Text.Trim();
        lock (_svc.Config.Sync) cc.Label = text.Length > 0 ? text : _sel.Name;
        var n = FanNames.For(_svc.Config, _sel);
        _title.Text = n.Primary;
        if (_rows.TryGetValue(_sel.Id, out var row)) { row.Name.Text = n.Primary; row.Sub.Text = n.Secondary; }
        _saveTimer.Stop(); _saveTimer.Start();
    }

    void BuildChannelRows()
    {
        foreach (var ch in _svc.Hub.Engine.Channels)
        {
            var ui = new RowUi { Channel = ch };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var n = FanNames.For(_svc.Config, ch);
            ui.Name = B.Text(n.Primary, null, 13.5, FontWeights.SemiBold);
            g.Children.Add(ui.Name);
            ui.Sub = B.Text(n.Secondary, "Label", null, null, new Thickness(0, 3, 8, 0));
            ui.Sub.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(ui.Sub, 1);
            g.Children.Add(ui.Sub);
            var rp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            ui.Rpm = B.Fg(B.Text("--", "Mono"), "TextBrush");
            rp.Children.Add(ui.Rpm);
            if (ch.TachReadingId != null) rp.Children.Add(B.Text("rpm", "Label", null, null, new Thickness(4, 1, 0, 0)));
            Grid.SetColumn(rp, 1);
            g.Children.Add(rp);
            ui.State = new StatusPill { Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetRow(ui.State, 1); Grid.SetColumn(ui.State, 1);
            g.Children.Add(ui.State);
            ui.Item = new ListBoxItem { Content = g, Tag = ch };
            ui.Item.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, n.Primary);
            _rows[ch.Id] = ui;
            _list.Items.Add(ui.Item);
        }
    }

    ChannelConfig ConfigFor(IFanChannel ch)
    {
        lock (_svc.Config.Sync)
        {
            var cc = _svc.Config.Channels.FirstOrDefault(c => c.Id == ch.Id);
            if (cc == null)
            {
                cc = new ChannelConfig { Id = ch.Id, Label = ch.Name, Kind = ch.DefaultKind, Mode = ChannelMode.Auto, Curve = FanPresets.CurveFor(_svc.Config.Preset, ch.DefaultKind) };
                _svc.Config.Channels.Add(cc);
            }
            return cc;
        }
    }

    void SelectChannel(IFanChannel ch)
    {
        _sel = ch;
        var cc = ConfigFor(ch);
        _loading = true;
        try
        {
            var n = FanNames.For(_svc.Config, ch);
            _title.Text = n.Primary;
            _subtitle.Text = n.Secondary + (cc.Label.Length > 0 && cc.Label != n.Primary ? "   ·   your label: " + cc.Label : "");
            _subtitle.ToolTip = "id " + ch.Id;
            _labelBox.Text = cc.Label;
            _identify.Visibility = _identifyLong.Visibility = ch.Resource == HwResource.SuperIo ? Visibility.Visible : Visibility.Collapsed;
            _identifyNote.Text = "";
            _kind.SelectedIndex = Math.Max(0, Array.FindIndex(Kinds, k => k.kind == cc.Kind));
            _modeAuto.IsChecked = cc.Mode == ChannelMode.Auto;
            _modeCurve.IsChecked = cc.Mode == ChannelMode.Curve;
            _modeManual.IsChecked = cc.Mode == ChannelMode.Manual;
            _modeAuto.Content = ch.Resource == HwResource.GpuFans ? "GPU firmware" : "BIOS";
            lock (_svc.Config.Sync) _work = cc.Curve.Clone();
            _source.SelectedIndex = (int)_work.Source;
            _hyst.Slider.Value = _work.HysteresisC;
            _up.Slider.Value = _work.RampUpPerSec;
            _down.Slider.Value = _work.StepDownPerSec;
            _min.Slider.Value = _work.MinPercent;
            _max.Slider.Value = _work.MaxPercent;
            _release.Slider.Value = _work.ReleaseToAutoBelowC;
            _manual.Slider.Value = cc.ManualPercent;
            _editor.SetCurve(_work);
            _minBox.Text = cc.VerifiedMinPercent is double vm ? vm.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "";
            _evidenceBox.Text = cc.MinEvidence ?? "";
            ApplyVisibility(cc);
        }
        finally { _loading = false; }
        Refresh(_svc.Hub.Latest);
    }

    void ApplyVisibility(ChannelConfig cc)
    {
        bool gpu = _sel?.Resource == HwResource.GpuFans;
        _release.Root.Visibility = gpu ? Visibility.Visible : Visibility.Collapsed;
        _manual.Root.Visibility = cc.Mode == ChannelMode.Manual ? Visibility.Visible : Visibility.Collapsed;
        _editor.IsEditable = cc.Mode != ChannelMode.Auto;   // as before: a Firmware-mode channel's saved curve is shown, not edited
        _minPanel.Visibility = !gpu && cc.Kind is FanKind.CaseFan or FanKind.CpuCooler or FanKind.GpuFan ? Visibility.Visible : Visibility.Collapsed;
        if (_sel != null)
        {
            var a = FanSafety.Assess(cc, _sel);
            _modeCurve.IsEnabled = _modeManual.IsEnabled = a.Controllable;
            _modeCurve.ToolTip = _modeManual.ToolTip = a.Controllable ? null : "Not available yet: " + a.Blocker;
            _editor.Floor = a.Controllable ? Math.Max(a.Floor, _work.MinPercent) : _work.MinPercent;
            string? label = null;
            if (a.Controllable)
            {
                if (gpu) label = $"NVML minimum {a.Floor:0}%";
                else if (cc.Kind == FanKind.Aio) label = "AIO floor 60% (one cable drives the pump and the radiator fans)";
                else if (cc.Kind == FanKind.Pump) label = "pump floor 60%";
                else label = a.Provisional ? $"confirmed minimum {a.Floor:0}% (restart not measured)" : $"confirmed minimum {a.Floor:0}% · restarts from a stop at {a.MeasuredStart:0}%";
            }
            _editor.FloorLabel = label;
            string why = gpu ? "the GPU driver's minimum" : cc.Kind is FanKind.Aio or FanKind.Pump ? "pump policy" : a.Provisional ? "your confirmed minimum; restart from a stop not measured" : $"your confirmed minimum; restarts from a stop at {a.MeasuredStart:0}%";
            _safetyLine.Text = a.Controllable ? $"VANTA never drives this fan below {a.Floor:0}% ({why})." : "VANTA leaves this fan to its firmware until it is set up: " + a.Blocker + ".";
            _safetyLine.ToolTip = a.Controllable ? "Basis: " + a.Basis : null;
        }
        _editor.Redraw();
    }

    void Commit()
    {
        if (_sel == null || _loading) return;
        var cc = ConfigFor(_sel);
        lock (_svc.Config.Sync)
        {
            cc.Curve = _work.Clone();
            cc.Curve.Name = "Custom";
        }
        _saveTimer.Stop(); _saveTimer.Start();
    }

    void OnCurveEdited() => Commit();

    void OnParamChanged()
    {
        if (_sel == null) return;
        _work.HysteresisC = _hyst.Slider.Value;
        _work.RampUpPerSec = _up.Slider.Value;
        _work.StepDownPerSec = _down.Slider.Value;
        _work.MinPercent = _min.Slider.Value;
        _work.MaxPercent = Math.Max(_max.Slider.Value, _min.Slider.Value);
        _work.ReleaseToAutoBelowC = _release.Slider.Value;
        var cc = ConfigFor(_sel);
        lock (_svc.Config.Sync) cc.ManualPercent = _manual.Slider.Value;
        Commit();
        ApplyVisibility(cc);
    }

    void OnSourceChanged(object? s, SelectionChangedEventArgs e)
    {
        if (_loading || _sel == null || _source.SelectedIndex < 0) return;
        _work.Source = (CurveSource)_source.SelectedIndex;
        Commit();
    }

    void OnKindChanged(object? s, SelectionChangedEventArgs e)
    {
        if (_loading || _sel == null || _kind.SelectedIndex < 0) return;
        var cc = ConfigFor(_sel);
        var kind = Kinds[_kind.SelectedIndex].kind;
        lock (_svc.Config.Sync) { cc.Kind = kind; cc.Curve = FanPresets.CurveFor(_svc.Config.Preset, kind); }
        ConfigStore.Save(_svc.Config);
        SelectChannel(_sel);
    }

    void OnModeClicked(ChannelMode mode)
    {
        if (_sel == null) return;
        var cc = ConfigFor(_sel);
        if (mode != ChannelMode.Auto && !FanSafety.Assess(cc, _sel).Controllable) { ReapplyChecks(); return; }
        lock (_svc.Config.Sync) cc.Mode = mode;
        ApplyVisibility(cc);
        ConfigStore.Save(_svc.Config);
        Refresh(_svc.Hub.Latest);
    }

    void OnPresetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string name) return;
        var switched = FanPresets.ApplyPreset(_svc.Config, name, _svc.Hub.Engine.Channels);
        ConfigStore.Save(_svc.Config);
        int total = _svc.Hub.Engine.Channels.Count;
        _hint.Text = $"{name}: curves rewritten; {switched.Count} of {total} fans set to VANTA curve. The others keep their mode (unidentified, or no confirmed minimum).";
        if (_sel != null) SelectChannel(_sel);
    }

    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Who controls a channel right now, in plain words.</summary>
    (string pill, Tone tone, string title, string body, bool active) Control(IFanChannel ch, ChannelConfig cc, ChannelStatus? st, double? rpm)
    {
        bool gpu = ch.Resource == HwResource.GpuFans;
        string fw = gpu ? "GPU firmware" : "BIOS";
        string owner = gpu ? "The GPU firmware (VBIOS)" : "The motherboard BIOS";
        var a = FanSafety.Assess(cc, ch);
        string rpmText = rpm is double r ? $"{r:0} rpm measured" : "no speed reading";
        if (!_svc.Config.FanControlEnabled)
            return (fw, Tone.Neutral, $"{fw} controls this fan", $"{owner} sets its speed because fan control is off (sidebar). " +
                (cc.Mode == ChannelMode.Auto ? "This fan is also set to stay on firmware." : "This fan is set to follow its VANTA curve once fan control is switched on; nothing below is applied now."), false);
        if (cc.Mode == ChannelMode.Auto)
            return (fw, Tone.Neutral, $"{fw} controls this fan", $"{owner} sets its speed: this fan is set to stay on firmware. The curve below is saved for later and is not applied.", false);
        if (!a.Controllable)
            return ("Needs setup", Tone.Warn, $"{fw} controls this fan (needs setup)", "VANTA leaves it to its firmware until it is set up: " + a.Blocker + ".", false);
        if (st == null) return (fw, Tone.Neutral, $"{fw} controls this fan", "VANTA has not taken it over yet.", false);
        if (st.State.StartsWith("not writing"))
            return ("Yielded", Tone.Warn, "Another program controls this fan", "VANTA is not writing to it: " + st.State["not writing: ".Length..] + ".", false);
        if (st.State.StartsWith("start check"))
            return ("Start check", Tone.Warn, "VANTA is checking that this fan starts", $"{Cap(st.State)}. {rpmText}.", true);
        if (st.State.StartsWith("faulted"))
            return ("Fault", Tone.Danger, "Writes to this fan failed", Cap(st.State) + ".", false);
        if (st.Controlled && st.FeedbackLost)
            return ("Holding 100%", Tone.Warn, "Speed signal lost: VANTA holds 100%",
                $"{Cap(rpmText)}. VANTA runs this fan at full speed until it reads a real speed again (a fan reading under {FanEngine.MinTurningRpm:0} rpm counts as stopped). If it is stuck, check it for an obstruction.", true);
        if (st.Controlled && st.Warning != null)
            return ($"VANTA {st.AppliedPercent:0}%", Tone.Warn, "VANTA controls this fan with a raised minimum",
                $"{Cap(st.Warning)}. Commanded {st.AppliedPercent:0}% · {rpmText}. The fan may be sticking: check it, and plan to clean or replace it. The raised minimum lasts until VANTA restarts.", true);
        if (st.Controlled)
        {
            string dutyText = st.AppliedPercent is double ap ? $"commanded {ap:0}%" : "commanded duty unknown";
            string src = st.EffectiveTempC is double t ? $" · following {SourceName(_work.Source)} at {t:0.0} °C" : "";
            string extra = st.State.Contains("waiting for rotation") ? " · waiting to see it turn" : "";
            return (cc.Mode == ChannelMode.Manual ? $"VANTA {st.AppliedPercent:0}%" : $"VANTA {st.AppliedPercent:0}%", Tone.Accent, "VANTA controls this fan",
                $"{Cap(dutyText)} · {rpmText}{src}{extra}.", true);
        }
        return (fw, Tone.Neutral, $"{fw} controls this fan", Cap(st.State) + ".", false);
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    static string SourceName(CurveSource s) => s switch { CurveSource.Cpu => "the CPU", CurveSource.Gpu => "the GPU", _ => "the hotter of CPU and GPU" };

    public void Refresh(TelemetrySnapshot s)
    {
        _afterburner.Update();
        foreach (var ui in _rows.Values)
        {
            var fan = ui.Channel.TachReadingId is string tid ? s.Fans.FirstOrDefault(f => f.Id == tid) : null;
            ui.Rpm.Text = ui.Channel.TachReadingId == null ? "no tach" : fan?.Rpm is double r ? r.ToString("0") : "—";
            var cc = ConfigFor(ui.Channel);
            _svc.Hub.Engine.Status.TryGetValue(ui.Channel.Id, out var st);
            var c = Control(ui.Channel, cc, st, fan?.Rpm);
            ui.State.Set(c.pill, c.tone);
        }

        if (_sel == null) return;
        var cur = ConfigFor(_sel);
        var reading = _sel.TachReadingId is string sid ? s.Fans.FirstOrDefault(f => f.Id == sid) : null;
        _rpm.Text = _sel.TachReadingId == null ? "—" : reading?.Rpm is double rr ? rr.ToString("0") : "—";
        _rpm.ToolTip = _sel.TachReadingId == null ? "No tachometer input on this header" : reading?.Rpm == null ? "No reading right now (different from 0 rpm)" : null;
        _svc.Hub.Engine.Status.TryGetValue(_sel.Id, out var status);
        var ctl = Control(_sel, cur, status, reading?.Rpm);

        _bannerTitle.Text = ctl.title;
        _bannerBody.Text = ctl.body;
        string tone = ctl.tone switch { Tone.Accent => "Accent", Tone.Warn => "Warn", Tone.Danger => "Danger", _ => "TextSecondary" };
        B.Bg(_banner, ctl.tone switch { Tone.Accent => "AccentSoftBrush", Tone.Warn => "WarnSoftBrush", Tone.Danger => "DangerSoftBrush", _ => "ControlBrush" });
        B.Bd(_banner, ctl.tone == Tone.Neutral ? "HairlineBrush" : tone + "Brush");
        _bannerIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, tone + "Brush");
        _duty.Text = ctl.active && status?.AppliedPercent is double ap ? $"commanded {ap:0}%" : reading?.Percent is double rp ? $"duty {rp:0}% (set by firmware)" : "";

        double? temp = FanEngine.SourceTemp(_work.Source, s);
        _editor.CurrentTemp = temp;
        _editor.CurrentDuty = ctl.active ? status?.AppliedPercent : null;
        _editor.PreviewOnly = !ctl.active;
        _editor.PreviewLabel = ctl.active ? null : "Saved curve preview · not applied";
        _curveTitle.Text = ctl.active ? (cur.Mode == ChannelMode.Manual ? "Curve (not used: fixed duty)" : "Active curve") : "Saved curve preview";
        _curveNote.Text = ctl.active ? $"VANTA follows this curve now. Edits apply within a second." : "Not applied: edits are saved and take effect only when VANTA controls this fan.";
        _editor.Redraw();

        bool tachSeen, tachMapped; string? tachEvidence;
        lock (_svc.Config.Sync) { tachSeen = cur.TachSeen; tachMapped = cur.TachMapped; tachEvidence = cur.TachEvidence; }
        string tach = !_sel.HasTachSensor ? "No tachometer input: VANTA cannot see whether its fans spin and does not assume they do."
            : _sel.HasTachSignal ? (tachMapped ? $"Tachometer mapped by a step test ({tachEvidence}). When VANTA takes this fan over it must see rotation within {FanEngine.StartupWindow.TotalSeconds:0} s, else it runs it at 100% (up to {FanEngine.MaxStartKicks} tries) and then hands it back to the firmware." : "Tachometer reports a speed.")
            : tachMapped || tachSeen ? "Its tachometer is known and now reads 0. If VANTA drives it, a fan that does not start gets full-speed start kicks and is then handed back to the firmware; one that stops later is held at 100% until the signal returns."
            : "Its tachometer input has never reported a speed (nothing on the sense wire, or a hub without one).";
        _tachNote.Text = tach + (status?.State is string st2 && st2.Length > 0 ? $"  Engine: {st2}." : "");

        double left = _svc.Hub.Engine.IdentifyRemaining(_sel.Id);
        _identify.IsEnabled = _identifyLong.IsEnabled = _testHold.IsEnabled = left <= 0;
        _identify.Content = left > 0 ? $"Holding {Math.Ceiling(left):0} s" : "Identify (5 s at 100%)";
    }
}
