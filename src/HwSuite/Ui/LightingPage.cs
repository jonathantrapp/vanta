using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Ui;

/// <summary>
/// Lighting: the effect, an honest hardware preview, and one card per device with a plain-language state derived from
/// configuration, ownership and what the device actually accepted (<see cref="LightingStatus"/>). Protocol detail (backend,
/// firmware, rates, counters) lives in each card's Details drawer. The page never changes protocol timing and never adds
/// stored-setting writes: it only edits the configuration the engine already reads.
/// </summary>
public sealed class LightingPage : UserControl
{
    sealed class ColorEditor
    {
        /// <summary>One-click hardware colours (pure channels where LEDs look best), then the owner's saved ones.</summary>
        static readonly (int rgb, string name)[] Builtin =
        {
            (0x38BDF8, "Ice cyan"), (0x6366F1, "Deep indigo"), (0xFFFFFF, "White"), (0xFFD7A8, "Warm white"),
            (0xFF0000, "Red"), (0xFF6A00, "Orange"), (0xFFB000, "Amber"), (0x00FF40, "Green"),
            (0x00E5C0, "Teal"), (0x0040FF, "Blue"), (0x8B00FF, "Violet"), (0xFF00C8, "Magenta")
        };

        public StackPanel Root { get; } = new();
        public event Action<int>? Changed;
        readonly Border _swatch = new() { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        readonly TextBox _hex = new() { Width = 96, ToolTip = "#RRGGBB, or R,G,B (0-255)" };
        readonly Slider _r = new() { Minimum = 0, Maximum = 255 }, _g = new() { Minimum = 0, Maximum = 255 }, _b = new() { Minimum = 0, Maximum = 255 };
        readonly TextBox _rv = NumBox(), _gv = NumBox(), _bv = NumBox();
        readonly WrapPanel _bar = new() { Margin = new Thickness(0, 10, 0, 6) };
        readonly Func<List<int>> _saved;
        readonly Action _savedChanged;
        int _value;
        bool _setting;

        static TextBox NumBox() => new() { Width = 52, MinHeight = 28, Padding = new Thickness(6, 2, 6, 2), HorizontalContentAlignment = HorizontalAlignment.Right };

        /// <summary>The editor's heading (names what the colour does in the current effect).</summary>
        public TextBlock Title { get; private set; } = null!;

        public ColorEditor(string title, Func<List<int>> saved, Action savedChanged)
        {
            _saved = saved; _savedChanged = savedChanged;
            B.Bd(_swatch, "ControlBorderStrongBrush");
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(_swatch);
            _hex.Margin = new Thickness(10, 0, 0, 0); _hex.VerticalAlignment = VerticalAlignment.Center;
            _hex.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, title + " colour");
            top.Children.Add(_hex);
            Title = B.Text(title, "CardTitle", 13.5, null, new Thickness(12, 0, 0, 0)); Title.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(Title);
            Root.Children.Add(top);
            Root.Children.Add(_bar);
            foreach (var (label, s, v) in new[] { ("R", _r, _rv), ("G", _g, _gv), ("B", _b, _bv) })
            {
                s.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, title + " " + label);
                Root.Children.Add(SliderRow(label, s, v, 22));
                s.ValueChanged += (_, _) => FromSliders();
                v.LostFocus += (_, _) => FromBoxes();
                v.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) FromBoxes(); };
            }
            _hex.LostFocus += (_, _) => FromHex();
            _hex.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) FromHex(); };
            RebuildBar();
        }

        Border Chip(int rgb, string tip)
        {
            var c = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            var b = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(c), BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = $"{tip}  #{rgb:X6}"
            };
            B.Bd(b, "ControlBorderBrush");
            return b;
        }

        public void RebuildBar()
        {
            _bar.Children.Clear();
            foreach (var (rgb, name) in Builtin)
            {
                var chip = Chip(rgb, name);
                chip.MouseLeftButtonUp += (_, _) => Set(rgb, true);
                _bar.Children.Add(chip);
            }
            var saved = _saved();
            if (saved.Count > 0) { var sep = new Border { Width = 1, Height = 22, Margin = new Thickness(2, 0, 8, 6) }; B.Bg(sep, "HairlineBrush"); _bar.Children.Add(sep); }
            foreach (var rgb in saved.ToList())
            {
                var chip = Chip(rgb, "Saved (right-click to remove)");
                chip.MouseLeftButtonUp += (_, _) => Set(rgb, true);
                chip.MouseRightButtonUp += (_, _) => { _saved().Remove(rgb); _savedChanged(); };
                _bar.Children.Add(chip);
            }
            var add = new Button { Content = "Save colour", Style = B.S("GhostBtn"), Padding = new Thickness(8, 0, 8, 0), MinHeight = 22, Height = 24, Margin = new Thickness(0, 0, 0, 6),
                ToolTip = "Save the current colour to this bar" };
            add.Click += (_, _) =>
            {
                var list = _saved();
                list.Remove(_value);
                list.Insert(0, _value);
                while (list.Count > 12) list.RemoveAt(list.Count - 1);
                _savedChanged();
            };
            _bar.Children.Add(add);
        }

        void FromSliders()
        {
            if (_setting) return;
            Set(((int)_r.Value << 16) | ((int)_g.Value << 8) | (int)_b.Value, true);
        }

        void FromBoxes()
        {
            if (_setting) return;
            int P(TextBox b, double fallback) => int.TryParse(b.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 255) : (int)fallback;
            Set((P(_rv, _r.Value) << 16) | (P(_gv, _g.Value) << 8) | P(_bv, _b.Value), true);
        }

        void FromHex()
        {
            var t = _hex.Text.Trim().TrimStart('#');
            var parts = t.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts.All(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 255))
            {
                var n = parts.Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                Set((n[0] << 16) | (n[1] << 8) | n[2], true);
            }
            else if (t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) Set(v, true);
            else _hex.Text = "#" + _value.ToString("X6");
        }

        public void Set(int rgb, bool notify)
        {
            _setting = true;
            _value = rgb;
            _r.Value = (rgb >> 16) & 0xFF; _g.Value = (rgb >> 8) & 0xFF; _b.Value = rgb & 0xFF;
            _rv.Text = $"{(rgb >> 16) & 0xFF}"; _gv.Text = $"{(rgb >> 8) & 0xFF}"; _bv.Text = $"{rgb & 0xFF}";
            _hex.Text = "#" + rgb.ToString("X6");
            _swatch.Background = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            _setting = false;
            if (notify) Changed?.Invoke(rgb);
        }
    }

    sealed class DeviceCard
    {
        public DeviceRole Role;
        public Border Root = null!;
        public StatusPill Pill = null!;
        public TextBlock Detail = null!, Advanced = null!;
        public CheckBox? Include;
    }

    readonly AppServices _svc;
    readonly DispatcherTimer _preview = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly ToggleButton _master = new() { Style = B.S("Switch"), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _masterNote = B.Text("", "Muted", null, null, new Thickness(10, 0, 24, 0));
    readonly List<(RadioButton rb, EffectKind kind)> _effects = new();
    // atmosphere effects: only the controls that do something for the selected effect are shown
    readonly Slider _density = new() { Minimum = 0, Maximum = 100 }, _glow = new() { Minimum = 0, Maximum = 100 };
    readonly TextBlock _densityVal = B.Fg(B.Text("", "Mono"), "TextBrush"), _glowVal = B.Fg(B.Text("", "Mono"), "TextBrush");
    readonly CheckBox _lightning = new() { Content = "Soft lightning (rare)", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock _effectNote = B.Para("", "Muted", new Thickness(0, 14, 0, 0));
    Grid _densityRow = null!, _speedRow = null!;
    Expander _fineTune = null!;
    /// <summary>Set while the animated preview is captured at explicit times (fixture films); null otherwise.</summary>
    double? _filmTime;
    // 1.14: revert bar (one edit session, see LightingEditSession), device shortcuts, colours from an image
    readonly StackPanel _revertBar = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 4, 0, 4), Visibility = Visibility.Collapsed };
    readonly TextBlock _revertNote = B.Text("", "Muted");
    readonly Button _revert = new() { Content = "Revert changes", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    readonly Button _keep = new() { Content = "Keep", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    readonly WrapPanel _deviceLinks = new() { Margin = new Thickness(0, 8, 0, 0) };
    readonly DispatcherTimer _highlight = new() { Interval = TimeSpan.FromMilliseconds(2500) };
    Border? _highlighted;
    Point _pressAt;
    DeviceRole? _pressRole;
    /// <summary>UI checks: how many times a device shortcut navigated, and to which device last.</summary>
    internal int DeviceFocusCount { get; private set; }
    internal DeviceRole? LastFocusedDevice { get; private set; }
    // the preview's own scene clock (see EffectContext.Drift); fixtures and films use time × rate, so they stay repeatable
    double _previewDrift, _previewDriftTime = double.NaN;
    readonly ColorEditor _colorA, _colorB;
    readonly Slider _speed = new() { Minimum = 0, Maximum = 100 }, _brightness = new() { Minimum = 0, Maximum = 100 };
    readonly TextBlock _speedVal = B.Fg(B.Text("", "Mono"), "TextBrush"), _brightVal = B.Fg(B.Text("", "Mono"), "TextBrush");
    readonly LightingVisualizer _viz = new() { Height = 240 };
    readonly StatusPill _vizPill = new();
    readonly TextBlock _vizNote = B.Para("", "Label", new Thickness(0, 10, 0, 0));
    readonly List<(RadioButton rb, ChannelOrder order)> _orders = new();
    readonly Slider _dledCount = new() { Minimum = 1, Maximum = 120, TickFrequency = 1, IsSnapToTickEnabled = true };
    readonly List<(RadioButton rb, int hz)> _rates = new();
    readonly TextBlock _abState = B.Text("", "MonoMuted", null, null, new Thickness(0, 6, 0, 0));
    readonly TextBlock _stripRate = B.Text("", "MonoMuted", null, null, new Thickness(0, 6, 0, 0));
    readonly Button _abShowA = new() { Content = "60", MinWidth = 40 };
    readonly Button _abShowB = new() { Content = "120", MinWidth = 40 };
    readonly Button _abAlternate = new() { Content = "Alternate", MinWidth = 40 };
    readonly Button _abStop = new() { Content = "Stop" };
    readonly ComboBox _abRate = new() { Width = 76, MinWidth = 76, ToolTip = "Rate to compare with 60" };
    int? _abAlternateFrom;
    DateTime _abNextSwitch, _abEnds;
    long _lastStripSent; DateTime _lastStripSample = DateTime.MinValue; double _stripAcceptedPerSec;
    readonly TextBlock _dledCountVal = B.Fg(B.Text("", "Mono"), "TextBrush");
    readonly TextBlock _calHint = B.Text("", "MonoMuted", null, null, new Thickness(0, 8, 0, 0));
    readonly Button _useCal = new() { Content = "Use controller order", HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    readonly ComboBox _sawRed = new(), _sawGreen = new(), _sawBlue = new();
    readonly TextBlock _orderResult = B.Para("", "Muted", new Thickness(0, 6, 0, 0));
    readonly Button _applySuggested = new() { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    readonly List<Button> _testButtons = new();
    ChannelOrder? _suggested;
    readonly List<DeviceCard> _cards = new();
    readonly Grid _deviceGrid = new();
    int _deviceColumns;
    bool _loading;

    public LightingPage(AppServices svc)
    {
        _svc = svc;
        _abStop.Style = B.S("GhostBtn"); _useCal.Style = B.S("GhostBtn"); _applySuggested.Style = B.S("PrimaryBtn");
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); ConfigStore.Save(_svc.Config); };
        _colorA = new ColorEditor("Primary", () => _svc.Config.Rgb.SavedColors, OnSavedColorsChanged);
        _colorB = new ColorEditor("Secondary", () => _svc.Config.Rgb.SavedColors, OnSavedColorsChanged);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // toolbar: output and effect (always visible, outside the scroll area)
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        var outBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
        var outLabel = B.Text("Lighting output", "CardTitle"); outLabel.VerticalAlignment = VerticalAlignment.Center; outLabel.Margin = new Thickness(0, 0, 12, 0);
        outBox.Children.Add(outLabel);
        _master.ToolTip = "Off: nothing is sent to any lighting device.";
        _master.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Lighting output");
        outBox.Children.Add(_master);
        _masterNote.VerticalAlignment = VerticalAlignment.Center;
        outBox.Children.Add(_masterNote);
        bar.Children.Add(outBox);
        FrameworkElement EffectGroup(EffectKind[] kinds, string? caption)
        {
            var group = new Border { Style = B.S("SegmentGroup"), Margin = new Thickness(0, 4, 0, 4) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var kind in kinds)
            {
                var k = kind;
                var rb = new RadioButton { Content = EffectInfo.ShortLabel(k), Style = B.S("Segment"), GroupName = "effect", Tag = k,
                    ToolTip = EffectInfo.Label(k) + ": " + EffectInfo.Describe(k) };
                rb.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, EffectInfo.Label(k));
                rb.Click += (_, _) => OnEffectClicked(k);
                _effects.Add((rb, k));
                row.Children.Add(rb);
            }
            group.Child = row;
            if (caption == null) return group;
            var wrap = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            var cap = B.Text(caption, "Muted"); cap.VerticalAlignment = VerticalAlignment.Center; cap.Margin = new Thickness(0, 0, 8, 0);
            wrap.Children.Add(cap); wrap.Children.Add(group);
            return wrap;
        }
        var classic = EffectGroup(EffectInfo.Classic, null);
        ((FrameworkElement)classic).Margin = new Thickness(0, 4, 14, 4);
        bar.Children.Add(classic);
        bar.Children.Add(EffectGroup(EffectInfo.Scenes, "Atmosphere"));
        bar.Children.Add(EffectGroup(EffectInfo.RainScenes, "Rain"));
        bar.Children.Add(EffectGroup(EffectInfo.Scenes3, "More"));
        bar.Children.Add(BuildLookRow());
        bar.Children.Add(BuildRevertBar());
        root.Children.Add(bar);

        // effect panel
        var tune = new StackPanel();
        var fromImage = new Button { Content = "Colours from image…", Style = B.S("GhostBtn"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, -4, 0, 10),
            ToolTip = "Pick a still image (for example your wallpaper) and choose this effect's colours from it. Nothing is sent until you apply." };
        fromImage.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Choose colours from an image");
        fromImage.Click += (_, _) => OpenColoursFromImage();
        tune.Children.Add(fromImage);
        tune.Children.Add(_colorA.Root);
        _colorB.Root.Margin = new Thickness(0, 16, 0, 0);
        tune.Children.Add(_colorB.Root);
        _speedVal.Width = 48; _brightVal.Width = 48;
        _speed.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Speed");
        _brightness.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Brightness");
        var sp = SliderRow("Speed", _speed, _speedVal, 86); sp.Margin = new Thickness(0, 16, 0, 0);
        _speedRow = sp;
        tune.Children.Add(sp);
        var br = SliderRow("Brightness", _brightness, _brightVal, 86);
        tune.Children.Add(br);
        _densityVal.Width = 48; _glowVal.Width = 48;
        _density.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Amount");
        _densityRow = SliderRow("Amount", _density, _densityVal, 86);
        tune.Children.Add(_densityRow);
        _lightning.ToolTip = "Distant Storm only: a rare, soft glow behind the clouds (gentle rise and fade, never a strobe, scaled by brightness). Off keeps the rain and clouds.";
        _lightning.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Soft lightning");
        tune.Children.Add(_lightning);
        _glow.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Background glow");
        var fine = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        fine.Children.Add(SliderRow("Background", _glow, _glowVal, 86));
        fine.Children.Add(B.Para("How brightly the secondary colour glows between drops, mist and lights. Lower keeps most LEDs dark.", "Muted", new Thickness(0, 4, 0, 0)));
        _fineTune = new Expander { Style = B.S("InlineExpander"), Header = B.Text("Fine tuning", "Label"), Content = fine, Margin = new Thickness(0, 10, 0, 0) };
        tune.Children.Add(_fineTune);
        tune.Children.Add(_effectNote);
        _effectPanel = B.Section("Effect", tune);

        // preview
        var vizHead = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(_vizPill, Dock.Right);
        vizHead.Children.Add(_vizPill);
        _arrangeButton.Style = B.S("GhostBtn");
        _arrangeButton.Margin = new Thickness(0, 0, 10, 0);
        _arrangeButton.ToolTip = "Place the device drawings like your desk. Gradient Wave, mist and other drifting effects then travel left to right across your arrangement; rain falls toward the bottom.";
        _arrangeButton.Click += (_, _) => SetArranging(!_viz.Arranging);
        DockPanel.SetDock(_arrangeButton, Dock.Right);
        vizHead.Children.Add(_arrangeButton);
        vizHead.Children.Add(B.Text("Hardware preview", "CardTitle"));
        var vizStack = new StackPanel();
        vizStack.Children.Add(vizHead);
        vizStack.Children.Add(BuildArrangeBar());
        vizStack.Children.Add(_viz);
        vizStack.Children.Add(BuildDeviceLinks());
        vizStack.Children.Add(_arrangeInfo);
        vizStack.Children.Add(_vizNote);
        _vizCard = B.Card(vizStack, new Thickness(0, 0, 0, 12));
        _vizCard.SizeChanged += (_, e) => SizeViz(e.NewSize.Width);
        _viz.Arrangement = _svc.Config.Arrangement.Clone();
        HookArrangeInput();

        // case strip setup (rarely needed)
        var stripHead = new StackPanel { Orientation = Orientation.Horizontal };
        stripHead.Children.Add(Icons.Icon("strip", 16, "TextSecondaryBrush"));
        stripHead.Children.Add(B.Text("Case lighting setup", "CardTitle", null, null, new Thickness(10, 0, 0, 0)));
        stripHead.Children.Add(B.Text("LEDs, order, rate", "Muted", null, null, new Thickness(12, 1, 0, 0)));
        var stripContent = BuildStripPanel();
        _stripExpander = new Expander { Header = stripHead, Content = stripContent, Margin = new Thickness(0, 0, 0, 12) };
        _stripExpander.Expanded += (_, _) => Motion.RevealIn(stripContent);
        var balHead = new StackPanel { Orientation = Orientation.Horizontal };
        balHead.Children.Add(Icons.Icon("strip", 16, "TextSecondaryBrush"));
        balHead.Children.Add(B.Text("Colour match", "CardTitle", null, null, new Thickness(10, 0, 0, 0)));
        balHead.Children.Add(B.Text("memory, case fans, board", "Muted", null, null, new Thickness(12, 1, 0, 0)));
        var balContent = BuildBalancePanel();
        _balanceExpander = new Expander { Header = balHead, Content = balContent, Margin = new Thickness(0, 0, 0, 12) };
        _balanceExpander.Expanded += (_, _) => Motion.RevealIn(balContent);

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _layout, Focusable = false };
        _layout.MaxWidth = 1880;
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _leftCol.Margin = new Thickness(0, 0, 16, 0);
        _layout.Children.Add(_leftCol);
        Grid.SetColumn(_rightCol, 1);
        _layout.Children.Add(_rightCol);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);
        _scroll.SizeChanged += (_, e) => ArrangeLayout(e.NewSize.Width);

        Content = root;
        BuildDevices();
        ArrangeLayout(1100);
        LoadFromConfig();
        _ticker = new PresentationTicker(this, UpdateVisualizer);

        _master.Click += (_, _) =>
        {
            if (_loading) return;
            _svc.Config.Rgb.Enabled = _master.IsChecked == true;
            UpdateMasterNote();
            ConfigStore.Save(_svc.Config);
            if (_svc.Config.Rgb.Enabled) _svc.Ownership?.ResetStopAttempts();
        };
        // every appearance edit first joins the edit session (the first one captures the revert point)
        _colorA.Changed += rgb => { if (!_loading) { Edit("primary colour"); _svc.Config.Rgb.ColorA = rgb; Save(); } };
        _colorB.Changed += rgb => { if (!_loading) { Edit("secondary colour"); _svc.Config.Rgb.ColorB = rgb; Save(); } };
        _speed.ValueChanged += (_, e) => { _speedVal.Text = $"{e.NewValue:0}%"; if (!_loading) { Edit("speed"); _svc.Config.Rgb.Speed = e.NewValue / 100.0; Save(); } };
        _brightness.ValueChanged += (_, e) => { _brightVal.Text = $"{e.NewValue:0}%"; if (!_loading) { Edit("brightness"); _svc.Config.Rgb.Brightness = e.NewValue / 100.0; Save(); } };
        _density.ValueChanged += (_, e) => { _densityVal.Text = $"{e.NewValue:0}%"; if (!_loading) { Edit("amount"); _svc.Config.Rgb.Density = e.NewValue / 100.0; Save(); SyncLookSelection(); } };
        _glow.ValueChanged += (_, e) => { _glowVal.Text = $"{e.NewValue:0}%"; if (!_loading) { Edit("background glow"); _svc.Config.Rgb.Glow = e.NewValue / 100.0; Save(); SyncLookSelection(); } };
        _lightning.Click += (_, _) => { if (_loading) return; Edit("lightning"); _svc.Config.Rgb.Lightning = _lightning.IsChecked == true; ConfigStore.Save(_svc.Config); UpdateEffectControls(_svc.Config.Rgb.Effect); SyncLookSelection(); UpdateRevertBar(); };
        _svc.LightingEdits.Changed += UpdateRevertBar;
        _highlight.Tick += (_, _) => { _highlight.Stop(); _highlighted?.ClearValue(Border.BorderBrushProperty); _highlighted = null; };
        _dledCount.ValueChanged += (_, e) => { _dledCountVal.Text = $"{e.NewValue:0}"; if (!_loading) { _svc.Config.Rgb.DLedCount = (int)e.NewValue; Save(); UpdateCaseNote(); } };
        _abShowA.Click += (_, _) => StartAb(60, alternate: false);
        _abShowB.Click += (_, _) => StartAb(AbHigh, alternate: false);
        _abAlternate.Click += (_, _) => StartAb(60, alternate: true);
        _abStop.Click += (_, _) => StopAb();
        _abRate.SelectionChanged += (_, _) => { _abShowB.Content = $"{AbHigh}"; };
        _useCal.Click += (_, _) =>
        {
            var d = _svc.Rgb.Sinks.OfType<MotherboardSink>().FirstOrDefault()?.Device;
            var ord = d == null ? null : Fusion2UsbDevice.OrderFromName(Fusion2UsbDevice.DecodeCalibration(d.StripCalibrationWord));
            if (ord is ChannelOrder o) { _svc.Config.Rgb.StripOrder = o; foreach (var (rb, k) in _orders) rb.IsChecked = k == o; ConfigStore.Save(_svc.Config); }
        };
        _preview.Tick += (_, _) => UpdatePreview();
        // status refresh only while the window is shown (hidden to the tray or minimised: no polling, no HID presence checks)
        UiActivity.Bind(this, _preview, onResume: UpdatePreview);
        Loaded += (_, _) =>
        {
            // WPF clears code-set RadioButton states when a group changes visual-tree scope, so re-apply on every attach
            foreach (var (rb, kind) in _effects) rb.IsChecked = kind == _svc.Config.Rgb.Effect;
            foreach (var (rb, ord) in _orders) rb.IsChecked = ord == _svc.Config.Rgb.StripOrder;
            foreach (var (rb, hz) in _rates) rb.IsChecked = hz == _svc.Config.Rgb.EffectiveDLedHz;
            _master.IsChecked = _svc.Config.Rgb.Enabled;
            UpdateMasterNote();
            UpdateRamControls();
            UpdateMouseControls();
            UpdatePreview();
            _ticker.Start();
        };
        Unloaded += (_, _) => { _ticker.Stop(); StopAb(); if (_viz.Arranging) SetArranging(false); };   // a comparison never outlives the page
    }

    void Save() { _saveTimer.Stop(); _saveTimer.Start(); UpdateRevertBar(); }

    /// <summary>An appearance edit is about to happen on this page: the first one of a session captures the revert point.</summary>
    void Edit(string what) => _svc.LightingEdits.BeforeEdit("Lighting page: " + what);

    FrameworkElement BuildRevertBar()
    {
        _revertNote.VerticalAlignment = VerticalAlignment.Center;
        _revertBar.Children.Add(_revertNote);
        _revert.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Revert lighting changes");
        _keep.Style = B.S("GhostBtn");
        _keep.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Keep lighting changes");
        _keep.ToolTip = "Keep the current settings as your new starting point; Revert then returns here.";
        _revert.Click += (_, _) => { if (_svc.RevertLighting("Lighting page")) SyncFromConfig(); else UpdateRevertBar(); };
        _keep.Click += (_, _) => { _svc.KeepLighting("Lighting page"); UpdateRevertBar(); };
        _revertBar.Children.Add(_revert);
        _revertBar.Children.Add(_keep);
        return _revertBar;
    }

    /// <summary>Shows the revert bar only while there is something to revert, naming what Revert returns to.</summary>
    void UpdateRevertBar()
    {
        var edits = _svc.LightingEdits;
        edits.EndIfUnchanged();
        bool show = edits.HasChanges;
        _revertBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || edits.Baseline is not { } b) return;
        var c = _svc.Config.Rgb;
        _revertNote.Text = edits.Since is DateTime since ? $"Changed since {since:HH:mm}" : "Changed";
        _revert.ToolTip = "Back to: " + b.Summary + (LightsOut.IsOn(c) ? ". Lights out stays on; this is the look when the lights come back." : !c.Enabled ? ". Lighting output stays off." : ".");
    }

    // ---- device shortcuts: the drawings (and a row of buttons) lead to each device's existing card --------------------
    static string DeviceTitle(DeviceRole r) => r switch
    {
        DeviceRole.Strip => "Case fans and logo", DeviceRole.Ram => "Memory", DeviceRole.Board => "Board accents", DeviceRole.Keyboard => "Keyboard", _ => "Mouse"
    };

    FrameworkElement BuildDeviceLinks()
    {
        var cap = B.Text("Device settings:", "Muted"); cap.VerticalAlignment = VerticalAlignment.Center; cap.Margin = new Thickness(0, 0, 4, 0);
        _deviceLinks.Children.Add(cap);
        foreach (var role in new[] { DeviceRole.Strip, DeviceRole.Ram, DeviceRole.Board, DeviceRole.Keyboard, DeviceRole.Mouse })
        {
            var r = role;
            var b = new Button { Content = DeviceTitle(r), Style = B.S("GhostBtn"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 2, 0),
                ToolTip = r == DeviceRole.Strip ? "The case fans and the Lian Li logo share one LED chain: one card." : $"Show the {DeviceTitle(r).ToLowerInvariant()} card." };
            b.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, $"Go to {DeviceTitle(r)} settings");
            b.Click += (_, _) => FocusDevice(r, moveFocus: true);
            _deviceLinks.Children.Add(b);
        }
        return _deviceLinks;
    }

    /// <summary>
    /// Bring a device's card into view with a short, restrained accent outline; from the keyboard also move focus into it.
    /// Navigation only: no setting changes, nothing is saved or sent.
    /// </summary>
    /// <summary>Open the case lighting setup and scroll its case layout into view (fixture captures).</summary>
    internal void ShowCaseSetup()
    {
        _stripExpander.IsExpanded = true;
        _scroll.UpdateLayout();
        _caseNote.BringIntoView();
        _scroll.UpdateLayout();
    }

    internal void FocusDevice(DeviceRole role, bool moveFocus)
    {
        var card = _cards.FirstOrDefault(c => c.Role == role);
        if (card == null) return;
        DeviceFocusCount++; LastFocusedDevice = role;
        _scroll.UpdateLayout();
        double y = card.Root.TranslatePoint(new Point(0, 0), _layout).Y;
        _scroll.ScrollToVerticalOffset(Math.Max(0, y - 8));
        _highlighted?.ClearValue(Border.BorderBrushProperty);
        card.Root.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        _highlighted = card.Root;
        _highlight.Stop(); _highlight.Start();
        if (moveFocus) card.Root.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }

    // ---- colours from an image ----------------------------------------------------------------------------------------
    string? PickImage()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = "Choose an image for lighting colours", Filter = ImagePalette.FileFilter, CheckFileExists = true };
        return d.ShowDialog(Window.GetWindow(this)) == true ? d.FileName : null;
    }

    /// <summary>The palette dialog, drawing its simulated preview through this page's preview renderer.</summary>
    internal PaletteDialog CreatePaletteDialog() => new(_svc, (v, cfg, t) => RenderPreview(v, cfg, t, forceSimulated: true), PickImage);

    void OpenColoursFromImage()
    {
        if (_svc.Fixture != null) return;   // simulated fixtures never open a file picker
        var path = PickImage();
        if (path == null) return;
        var dlg = CreatePaletteDialog();
        dlg.Owner = Window.GetWindow(this);
        dlg.Load(path);
        if (dlg.ShowDialog() != true) return;
        ApplyPalette(dlg.ChosenPrimary, dlg.ChosenSecondary, "image " + dlg.FileName);
    }

    /// <summary>Apply chosen colours (only the ones that differ): colour fields only, part of the Revert session.</summary>
    internal void ApplyPalette(int? primary, int? secondary, string from)
    {
        var c = _svc.Config.Rgb;
        int? a = primary is int p && p != (c.ColorA & 0xFFFFFF) ? p : null;
        int? b = secondary is int s && s != (c.ColorB & 0xFFFFFF) ? s : null;
        if (a == null && b == null) return;
        _svc.ApplyColours(a, b, from);
        SyncFromConfig();
    }

    /// <summary>
    /// A press at <paramref name="down"/> released at <paramref name="up"/> on the preview: outside Arrange mode, a click
    /// (moved at most 6 px, same device) goes to that device's card. In Arrange mode presses are drags and never navigate.
    /// </summary>
    internal bool ClickDevice(Point down, Point up)
    {
        if (_viz.Arranging || _viz.HitTest(down) is not DeviceRole r) return false;
        if ((up - down).Length > 6 || _viz.HitTest(up) != r) return false;
        FocusDevice(r, moveFocus: false);
        return true;
    }

    /// <summary>Centre of a device's drawing in preview coordinates (UI checks).</summary>
    internal Point? DeviceCentre(DeviceRole r)
    {
        foreach (var g in DeviceArrangement.Groups)
            if (g == r)
                for (double y = 4; y < _viz.ActualHeight; y += 6)
                    for (double x = 4; x < _viz.ActualWidth; x += 6)
                        if (_viz.HitTest(new Point(x, y)) == r) return new Point(x, y);
        return null;
    }

    internal bool RevertShown => _revertBar.Visibility == Visibility.Visible;
    internal void ClickRevert() => _revert.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    internal void ClickKeep() => _keep.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    /// <summary>Card currently outlined by a device shortcut (UI checks).</summary>
    internal DeviceRole? HighlightedDevice => _highlighted == null ? null : _cards.FirstOrDefault(c => c.Root == _highlighted)?.Role;

    void OnSavedColorsChanged()
    {
        _colorA.RebuildBar(); _colorB.RebuildBar();
        ConfigStore.Save(_svc.Config);
    }

    static Grid SliderRow(string label, Slider s, FrameworkElement value, double labelWidth)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = B.Text(label, "Muted"); l.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(l);
        Grid.SetColumn(s, 1); g.Children.Add(s);
        value.Margin = new Thickness(10, 0, 0, 0); value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 2); g.Children.Add(value);
        return g;
    }

    static Grid FieldRow(string label, UIElement control, string? tip = null)
    {
        var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = B.Text(label, "Muted"); l.VerticalAlignment = VerticalAlignment.Center;
        if (tip != null) l.ToolTip = tip;
        g.Children.Add(l);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    // ---- 1.15 case layout: which fans are on the chain, where, and how many LEDs each has -----------------------------
    readonly List<(RadioButton rb, int n)> _caseFront = new(), _caseTop2 = new();
    readonly CheckBox _caseRear = new() { Content = "Rear fan", Margin = new Thickness(0, 0, 14, 0) }, _caseLogo = new() { Content = "Logo" };
    readonly Slider _caseLeds = new() { Minimum = 0, Maximum = 40, TickFrequency = 1, IsSnapToTickEnabled = true }, _caseLogoLeds = new() { Minimum = 0, Maximum = 30, TickFrequency = 1, IsSnapToTickEnabled = true };
    readonly TextBlock _caseLedsVal = B.Fg(B.Text("", "Mono"), "TextBrush"), _caseLogoLedsVal = B.Fg(B.Text("", "Mono"), "TextBrush");
    readonly ComboBox _caseOrder = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly CheckBox _caseMirror = new() { Content = new TextBlock { Text = "Every fan shows the same lights (the case hub copies one signal)", TextWrapping = TextWrapping.Wrap } };
    readonly CheckBox _caseCcw = new() { Content = "Numbers run counter-clockwise", Margin = new Thickness(0, 4, 0, 0) };
    readonly Slider _caseTop = new() { Minimum = 1, Maximum = 8, TickFrequency = 1, IsSnapToTickEnabled = true };
    readonly TextBlock _caseTopVal = B.Fg(B.Text("", "Mono"), "TextBrush");
    readonly List<FrameworkElement> _caseChainRows = new();
    readonly TextBlock _caseNote = B.Text("", "Muted");
    readonly Button _caseUseTotal = new();

    void BuildCaseLayoutRows(StackPanel stack)
    {
        stack.Children.Add(new Border { Style = B.S("Divider") });
        stack.Children.Add(B.Para("Case layout: where each part of the chain sits, so rain falls down the front fans one after another, drips gather at the top fan and ripples spread from fan to fan. It only changes which colour each LED gets.", "Muted", new Thickness(0, 0, 0, 6)));
        _caseMirror.Click += (_, _) => EditCase(c => c.Mirrored = _caseMirror.IsChecked == true);
        _caseMirror.ToolTip = "Tick this if the layout check lit every fan the same (the LANCOOL 215's hub does: all four fans show the first LEDs). Effects then run on one ring that every fan shows.";
        stack.Children.Add(FieldRow("Hub", _caseMirror));
        var frontGroup = new Border { Style = B.S("SegmentGroup") };
        var frontRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var n in new[] { 0, 1, 2, 3 })
        {
            var rb = new RadioButton { Content = n.ToString(), Style = B.S("Segment"), GroupName = "caseFront", Tag = n, Padding = new Thickness(10, 0, 10, 0) };
            rb.Click += (_, _) => EditCase(c => c.FrontFans = n);
            _caseFront.Add((rb, n)); frontRow.Children.Add(rb);
        }
        frontGroup.Child = frontRow;
        stack.Children.Add(FieldRow("Front fans", frontGroup, "Front intake fans with lighting on this chain, stacked top to bottom."));
        var topGroup = new Border { Style = B.S("SegmentGroup") };
        var topRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var n in new[] { 0, 1, 2 })
        {
            var rb = new RadioButton { Content = n.ToString(), Style = B.S("Segment"), GroupName = "caseTop", Tag = n, Padding = new Thickness(10, 0, 10, 0) };
            rb.Click += (_, _) => EditCase(c => c.TopFans = n);
            _caseTop2.Add((rb, n)); topRow.Children.Add(rb);
        }
        topGroup.Child = topRow;
        stack.Children.Add(FieldRow("Top fans", topGroup, "Lit fans under the roof (the radiator fans)."));
        var parts = new StackPanel { Orientation = Orientation.Horizontal };
        parts.Children.Add(_caseRear); parts.Children.Add(_caseLogo);
        _caseRear.Click += (_, _) => EditCase(c => c.RearFan = _caseRear.IsChecked == true);
        _caseLogo.Click += (_, _) => EditCase(c => c.Logo = _caseLogo.IsChecked == true);
        stack.Children.Add(FieldRow("Also lit", parts));
        Grid SliderRow(Slider sl, TextBlock val, string name)
        {
            val.Width = 64; val.Margin = new Thickness(10, 0, 0, 0);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(sl); Grid.SetColumn(val, 1); row.Children.Add(val);
            sl.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, name);
            return row;
        }
        _caseLeds.ValueChanged += (_, e) => { _caseLedsVal.Text = e.NewValue < 1 ? "unknown" : $"{e.NewValue:0}"; if (!_loading) EditCase(c => c.LedsPerFan = (int)e.NewValue); };
        _caseLogoLeds.ValueChanged += (_, e) => { _caseLogoLedsVal.Text = e.NewValue < 1 ? "unknown" : $"{e.NewValue:0}"; if (!_loading) EditCase(c => c.LogoLeds = (int)e.NewValue); };
        stack.Children.Add(FieldRow("LEDs per fan", SliderRow(_caseLeds, _caseLedsVal, "LEDs per fan"), "How many LEDs each fan has (the same for every fan). Unknown: the chain's LEDs are shared out evenly."));
        stack.Children.Add(FieldRow("Logo LEDs", SliderRow(_caseLogoLeds, _caseLogoLedsVal, "Logo LEDs"), "How many LEDs the logo has. Unknown: it gets a small share."));
        foreach (var o in Enum.GetValues<CaseChainOrder>()) _caseOrder.Items.Add(new ComboBoxItem { Content = CaseLayout.OrderLabel(o), Tag = o });
        _caseOrder.SelectionChanged += (_, _) => { if (!_loading && _caseOrder.SelectedItem is ComboBoxItem { Tag: CaseChainOrder o }) EditCase(c => c.Order = o); };
        _caseOrder.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Chain order");
        var orderRow = FieldRow("Chain order", _caseOrder, "Which part gets the first LEDs on the chain. If rain seems to fall upward or start at the rear, try another order.");
        _caseChainRows.Add(orderRow);
        stack.Children.Add(orderRow);
        _caseTop.ValueChanged += (_, e) => { _caseTopVal.Text = $"{e.NewValue:0}"; if (!_loading) EditCase(c => c.TopLed = (int)e.NewValue - 1); };
        _caseCcw.Click += (_, _) => EditCase(c => c.CounterClockwise = _caseCcw.IsChecked == true);
        var ring = new StackPanel();
        ring.Children.Add(SliderRow(_caseTop, _caseTopVal, "LED at the top of each fan"));
        ring.Children.Add(_caseCcw);
        stack.Children.Add(FieldRow("Top LED", ring, "Which LED (1 = the first) sits at the top of each fan, from the rainbow layout check. Rain and drips start there."));
        _caseNote.TextWrapping = TextWrapping.Wrap; _caseNote.Margin = new Thickness(96, 4, 0, 0);
        stack.Children.Add(_caseNote);
        _caseUseTotal.Margin = new Thickness(96, 6, 0, 0); _caseUseTotal.HorizontalAlignment = HorizontalAlignment.Left;
        _caseUseTotal.Click += (_, _) =>
        {
            int total = _svc.Config.Rgb.CaseOrDefault.KnownTotal;
            if (total < 1 || total > 120) return;
            _svc.Config.Rgb.DLedCount = total; ConfigStore.Save(_svc.Config);
            bool was = _loading; _loading = true;
            try { _dledCount.Value = total; _dledCountVal.Text = $"{total}"; } finally { _loading = was; }
            UpdateCaseNote();
        };
        stack.Children.Add(_caseUseTotal);
        var show = new Button { Content = "Show the layout on the fans (10 s)", Margin = new Thickness(96, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            Tag = "Lights each part in its own colour for 10 s: front top red, front middle green, front bottom blue, rear amber, logo white. If a fan shows two colours or the wrong one, change the LEDs per fan or the chain order." };
        show.Click += (_, _) =>
        {
            var c = _svc.Config.Rgb.CaseOrDefault;
            _svc.Rgb.Sinks.OfType<DLedSink>().FirstOrDefault()?.ShowTestPattern(c.CheckPattern(Math.Clamp(_svc.Config.Rgb.DLedCount, 1, 120)), TimeSpan.FromSeconds(10));
        };
        _testButtons.Add(show);
        stack.Children.Add(show);
        stack.Children.Add(B.Para("Separate fans: front top red, front middle green, front bottom blue, rear amber, radiator fans cyan and magenta, logo white. If every fan shows the same, tick \"Every fan shows the same lights\" above. "
            + "Then the check shows a rainbow around each fan: LED 1 red, then orange, yellow, green, cyan, blue, purple; set the top LED to the one at 12 o'clock and the direction. Needs lighting output on.", "Muted", new Thickness(96, 4, 0, 0)));
    }

    void EditCase(Action<CaseLayout> change)
    {
        if (_loading) return;
        var c = _svc.Config.Rgb.CaseOrDefault.Clone();
        change(c);
        _svc.CommitCaseLayout(c);
        LoadCaseLayout();
    }

    void LoadCaseLayout()
    {
        var c = _svc.Config.Rgb.CaseOrDefault;
        bool was = _loading; _loading = true;
        try
        {
            foreach (var (rb, n) in _caseFront) rb.IsChecked = n == c.FrontFans;
            foreach (var (rb, n) in _caseTop2) rb.IsChecked = n == c.TopFans;
            _caseRear.IsChecked = c.RearFan; _caseLogo.IsChecked = c.Logo;
            _caseLeds.Value = c.LedsPerFan; _caseLedsVal.Text = c.LedsPerFan < 1 ? "unknown" : $"{c.LedsPerFan}";
            _caseLogoLeds.Value = c.LogoLeds; _caseLogoLedsVal.Text = c.LogoLeds < 1 ? "unknown" : $"{c.LogoLeds}";
            _caseLogoLeds.IsEnabled = c.Logo;
            foreach (ComboBoxItem it in _caseOrder.Items) if (it.Tag is CaseChainOrder o && o == c.Order) _caseOrder.SelectedItem = it;
            _caseMirror.IsChecked = c.Mirrored;
            foreach (var r in _caseChainRows) r.Visibility = c.Mirrored ? Visibility.Collapsed : Visibility.Visible;
            _caseTop.Maximum = Math.Max(1, c.LedsPerFan > 0 ? c.LedsPerFan : 8);
            _caseTop.Value = c.TopLed + 1; _caseTopVal.Text = $"{c.TopLed + 1}";
            _caseCcw.IsChecked = c.CounterClockwise;
        }
        finally { _loading = was; }
        UpdateCaseNote();
    }

    void UpdateCaseNote()
    {
        var c = _svc.Config.Rgb.CaseOrDefault;
        int count = Math.Clamp(_svc.Config.Rgb.DLedCount, 1, 120);
        var parts = c.Allocate(count);
        if (c.Mirrored)
        {
            int ring = parts.Count > 0 ? parts[0].Count : 0;
            _caseNote.Text = (c.LedsPerFan > 0 ? $"Every fan shows the same {ring} LEDs" : $"Every fan shows the same LEDs (set LEDs per fan; now {ring})")
                + (c.Logo ? $" and the logo the first {(c.LogoLeds > 0 ? c.LogoLeds : 3)} of them" : "")
                + ". Effects run on that one ring, so all fans move together. Separate fans need a controller with one output per fan (for example a Nollie8).";
            int want = c.KnownTotal;
            _caseUseTotal.Content = $"Set the LED count to {want}";
            _caseUseTotal.Visibility = c.CountsKnown && want != count && want <= 120 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        string split = string.Join(", ", parts.Select(p => $"{CaseLayout.Name(p.Kind)} {p.Count}"));
        if (!c.CountsKnown)
        {
            _caseNote.Text = $"LED split assumed: the chain's {count} LEDs are shared out ({split}). Set the LEDs per fan (from the box or manual) to place them exactly.";
            _caseUseTotal.Visibility = Visibility.Collapsed;
            return;
        }
        int total = c.KnownTotal;
        _caseNote.Text = total == count ? $"Layout matches the LED count ({count}): {split}."
            : total > count ? $"The layout needs {total} LEDs but the LED count is {count}: the last parts get fewer ({split})."
            : $"The layout covers {total} of the {count} LEDs; the rest belong to no part ({split}).";
        _caseUseTotal.Content = $"Set the LED count to {total}";
        _caseUseTotal.Visibility = total != count && total <= 120 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 1.15.2 colour match: per-device red/green/blue strength so one colour looks alike on different LEDs ----------
    readonly List<(Slider s, TextBlock v, Func<ColorBalances, ColorBalance> pick, char ch)> _balance = new();

    StackPanel BuildBalancePanel()
    {
        var stack = new StackPanel();
        stack.Children.Add(B.Para("The same colour can look different on different LEDs (violet comes out pinker on the memory than on the fans). Turn a colour down on the device where it is too strong. Changes show at once and apply only to what is sent; your colours and the preview stay as chosen.", "Muted", new Thickness(0, 0, 0, 8)));
        foreach (var (title, pick) in new (string, Func<ColorBalances, ColorBalance>)[] { ("Memory", b => b.Ram), ("Case fans", b => b.Strip), ("Board", b => b.Board), ("Mouse (live mode)", b => b.Mouse) })
        {
            stack.Children.Add(B.Text(title, "CardTitle", null, null, new Thickness(0, 8, 0, 2)));
            foreach (var (name, ch) in new[] { ("Red", 'R'), ("Green", 'G'), ("Blue", 'B') })
            {
                var sl = new Slider { Minimum = 20, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true };
                var val = B.Fg(B.Text("", "Mono"), "TextBrush"); val.Width = 48;
                sl.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, $"{title} {name.ToLowerInvariant()} strength");
                var p = pick; var c = ch;
                sl.ValueChanged += (_, e) =>
                {
                    val.Text = $"{e.NewValue:0}%";
                    if (_loading) return;
                    var bal = p(_svc.Config.Rgb.Balance);
                    double v = e.NewValue / 100.0;
                    if (c == 'R') bal.R = v; else if (c == 'G') bal.G = v; else bal.B = v;
                    Save();
                };
                _balance.Add((sl, val, pick, ch));
                stack.Children.Add(SliderRow(name, sl, val, 60));
            }
        }
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button Btn(string text, Action click, string tip) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6), ToolTip = tip }; b.Click += (_, _) => click(); buttons.Children.Add(b); return b; }
        void ShowAll(Rgb c)
        {
            _svc.Rgb.Sinks.OfType<DLedSink>().FirstOrDefault()?.ShowTestColor(c, TimeSpan.FromSeconds(15));
            _svc.Rgb.Sinks.OfType<RamSink>().FirstOrDefault()?.ShowTestColor(c, TimeSpan.FromSeconds(15));
            _svc.Rgb.Sinks.OfType<MouseSink>().FirstOrDefault()?.ShowTestColor(c, TimeSpan.FromSeconds(15));
        }
        Btn("Show white (15 s)", () => ShowAll(new Rgb(255, 255, 255)), "White on the case fans, the memory and the mouse for 15 s: adjust until they look the same white. Needs output on (memory: Streamed mode, mouse: Live).");
        Btn("Show the primary colour (15 s)", () => ShowAll(Rgb.FromInt(_svc.Config.Rgb.ColorA)), "Your primary colour on the case fans and the memory for 15 s.");
        Btn("Reset", () => { _svc.Config.Rgb.Balance = new ColorBalances(); ConfigStore.Save(_svc.Config); LoadBalance(); }, "Every device back to 100% (as sent).");
        stack.Children.Add(buttons);
        return stack;
    }

    void LoadBalance()
    {
        bool was = _loading; _loading = true;
        try
        {
            foreach (var (s, v, pick, ch) in _balance)
            {
                var b = pick(_svc.Config.Rgb.Balance);
                double x = ch == 'R' ? b.R : ch == 'G' ? b.G : b.B;
                s.Value = Math.Round(x * 100); v.Text = $"{s.Value:0}%";
            }
        }
        finally { _loading = was; }
    }

    StackPanel BuildStripPanel()
    {
        var stack = new StackPanel();
        stack.Children.Add(B.Para("The case fans and the Lian Li logo hang off the motherboard's D_LED header as one chain. These settings rarely need changing.", "Muted", new Thickness(0, 0, 0, 10)));

        var orderGroup = new Border { Style = B.S("SegmentGroup") };
        var orderRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, ord) in new[] { ("GRB", ChannelOrder.Grb), ("RGB", ChannelOrder.Rgb), ("BGR", ChannelOrder.Bgr), ("RBG", ChannelOrder.Rbg), ("GBR", ChannelOrder.Gbr), ("BRG", ChannelOrder.Brg) })
        {
            var rb = new RadioButton { Content = name, Style = B.S("Segment"), GroupName = "stripOrder", Tag = ord, Padding = new Thickness(8, 0, 8, 0) };
            rb.Click += (_, _) => { _svc.Config.Rgb.StripOrder = ord; ConfigStore.Save(_svc.Config); };
            _orders.Add((rb, ord));
            orderRow.Children.Add(rb);
        }
        orderGroup.Child = orderRow;
        stack.Children.Add(FieldRow("Byte order", orderGroup, "Byte order the strip expects. GRB is standard for WS2812B. Use the colour check below to confirm all three channels."));

        _dledCountVal.Width = 36;
        var count = new Grid();
        count.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        count.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        count.Children.Add(_dledCount);
        _dledCountVal.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_dledCountVal, 1); count.Children.Add(_dledCountVal);
        _dledCount.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "LED count");
        stack.Children.Add(FieldRow("LED count", count, "LEDs on the D_LED header: every fan and the logo on the chain together."));
        BuildCaseLayoutRows(stack);

        var rateGroup = new Border { Style = B.S("SegmentGroup") };
        var rateRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var hz in RgbConfig.DLedRates)
        {
            var rb = new RadioButton { Content = hz == 60 ? "60" : $"{hz}*", Style = B.S("Segment"), GroupName = "stripRate", Tag = hz, Padding = new Thickness(8, 0, 8, 0),
                ToolTip = hz == 60 ? "Default." : "Experimental: accepted by the controller in testing with no effect on board or RAM rates; whether it looks smoother is for you to judge." };
            rb.Click += (_, _) => { if (_loading) return; _svc.Config.Rgb.DLedMaxHz = hz; _svc.Config.Rgb.DLedExperimental120 = false; ConfigStore.Save(_svc.Config); };
            _rates.Add((rb, hz));
            rateRow.Children.Add(rb);
        }
        rateGroup.Child = rateRow;
        stack.Children.Add(FieldRow("Updates /s", rateGroup, "Strip updates per second. Only the strip follows this; board 20/s, RAM its own rate. * = experimental."));
        stack.Children.Add(_stripRate);

        foreach (var hz in RgbConfig.DLedRates.Where(h => h > 60)) _abRate.Items.Add(hz);
        _abRate.SelectedItem = 120;
        var ab = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var b in new FrameworkElement[] { _abRate, _abShowA, _abShowB, _abAlternate, _abStop }) { b.Margin = new Thickness(0, 0, 6, 6); ab.Children.Add(b); }
        _abShowA.ToolTip = "Show the strip at 60/s"; _abShowB.ToolTip = "Show the strip at the chosen rate"; _abAlternate.ToolTip = "Alternate between 60 and the chosen rate every 10 s (3 min)"; _abStop.ToolTip = "End the comparison";
        stack.Children.Add(FieldRow("Compare", ab, "Visual A/B of strip smoothness. Only the strip rate changes; nothing is saved. Needs an animated effect and output on."));
        stack.Children.Add(_abState);

        stack.Children.Add(new Border { Style = B.S("Divider") });
        var check = new Grid();
        check.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        check.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        check.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = new (string label, Rgb color, ComboBox box)[] { ("Red", new Rgb(255, 0, 0), _sawRed), ("Green", new Rgb(0, 255, 0), _sawGreen), ("Blue", new Rgb(0, 0, 255), _sawBlue) };
        var checkLabel = B.Text("Colour check", "Muted"); checkLabel.ToolTip = "Show each colour on the case fans for 8 s and pick what you saw; VANTA works out the byte order.";
        check.Children.Add(checkLabel);
        for (int i = 0; i < rows.Length; i++)
        {
            check.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var (label, color, box) = rows[i];
            var sw = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), Margin = new Thickness(0, 0, 8, 0) };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(sw); content.Children.Add(new TextBlock { Text = "Show " + label.ToLowerInvariant() });
            var b = new Button { Content = content, MinWidth = 110, Margin = new Thickness(0, 0, 8, 6) };
            b.Click += (_, _) => _svc.Rgb.Sinks.OfType<DLedSink>().FirstOrDefault()?.ShowTestColor(color, TimeSpan.FromSeconds(8));
            _testButtons.Add(b);
            Grid.SetRow(b, i); Grid.SetColumn(b, 1); check.Children.Add(b);
            foreach (var opt in new[] { "I saw...", "red", "green", "blue", "something else" }) box.Items.Add(opt);
            box.SelectedIndex = 0; box.Margin = new Thickness(0, 0, 0, 6);
            box.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Colour seen after showing " + label);
            box.SelectionChanged += (_, _) => UpdateOrderSuggestion();
            Grid.SetRow(box, i); Grid.SetColumn(box, 2); check.Children.Add(box);
        }
        stack.Children.Add(check);
        stack.Children.Add(_orderResult);
        stack.Children.Add(_applySuggested);
        _applySuggested.Click += (_, _) =>
        {
            if (_suggested is not ChannelOrder o) return;
            _svc.Config.Rgb.StripOrder = o;
            foreach (var (rb, k) in _orders) rb.IsChecked = k == o;
            ConfigStore.Save(_svc.Config);
            foreach (var box in new[] { _sawRed, _sawGreen, _sawBlue }) box.SelectedIndex = 0;
            _orderResult.Text = $"Byte order set to {o.ToString().ToUpperInvariant()}. Check again to confirm.";
            _applySuggested.Visibility = Visibility.Collapsed;
        };
        stack.Children.Add(_calHint);
        stack.Children.Add(_useCal);
        return stack;
    }

    int AbHigh => _abRate.SelectedItem is int v ? v : 120;

    void StartAb(int hz, bool alternate)
    {
        _abAlternateFrom = alternate ? hz : null;
        _abNextSwitch = DateTime.UtcNow.AddSeconds(10);
        _abEnds = DateTime.UtcNow.AddMinutes(alternate ? 3 : 10);
        _svc.Config.Rgb.DLedRateOverride = hz;
        Log.Info($"D_LED visual comparison: showing {hz}/s{(alternate ? $", alternating with {AbHigh}/s every 10 s" : "")} (not saved)");
    }

    void StopAb()
    {
        _abAlternateFrom = null;
        _svc.Config.Rgb.DLedRateOverride = null;
        _abState.Text = "";
    }

    void UpdateAbState()
    {
        var o = _svc.Config.Rgb.DLedRateOverride;
        if (o == null) { if (!_abState.Text.StartsWith("ended")) _abState.Text = ""; return; }
        var now = DateTime.UtcNow;
        if (now >= _abEnds) { StopAb(); _abState.Text = "ended: back to the saved rate"; return; }
        if (_abAlternateFrom != null && now >= _abNextSwitch)
        {
            _svc.Config.Rgb.DLedRateOverride = o == 60 ? AbHigh : 60;
            _abNextSwitch = now.AddSeconds(10);
        }
        o = _svc.Config.Rgb.DLedRateOverride;
        bool ready = _svc.Config.Rgb.Enabled && _svc.Config.Rgb.DLedStrip;
        _abState.Text = !ready ? "needs output on and the strip included"
            : $"showing {o}/s" + (_abAlternateFrom != null ? $"   next switch {Math.Max(0, (_abNextSwitch - now).TotalSeconds):0} s" : "");
    }

    static char? SawCode(ComboBox b) => b.SelectedIndex switch { 1 => 'R', 2 => 'G', 3 => 'B', 4 => 'X', _ => null };

    void UpdateOrderSuggestion()
    {
        var r = SawCode(_sawRed); var g = SawCode(_sawGreen); var b = SawCode(_sawBlue);
        _suggested = null;
        _applySuggested.Visibility = Visibility.Collapsed;
        if (r == null || g == null || b == null) { _orderResult.Text = ""; return; }
        var current = _svc.Config.Rgb.StripOrder;
        if (r == 'R' && g == 'G' && b == 'B') { _orderResult.Text = $"All correct: {current.ToString().ToUpperInvariant()}."; return; }
        var sol = Fusion2UsbDevice.SolveOrder(current, r.Value, g.Value, b.Value);
        if (sol == null) { _orderResult.Text = "Not a channel swap (repeated or impure colour). Check the LED count and look again."; return; }
        _suggested = sol;
        _orderResult.Text = $"Suggested {sol.Value.ToString().ToUpperInvariant()} (now {current.ToString().ToUpperInvariant()}).";
        _applySuggested.Visibility = Visibility.Visible;
    }

    void UpdateCalibrationHint()
    {
        var d = _svc.Rgb.Sinks.OfType<MotherboardSink>().FirstOrDefault()?.Device;
        if (d == null) { _calHint.Text = ""; _useCal.Visibility = Visibility.Collapsed; return; }
        var name = Fusion2UsbDevice.DecodeCalibration(d.StripCalibrationWord);
        var ord = Fusion2UsbDevice.OrderFromName(name);
        if (name == null) { _calHint.Text = "controller calibration: unrecognised"; _useCal.Visibility = Visibility.Collapsed; return; }
        bool same = ord == _svc.Config.Rgb.StripOrder;
        _calHint.Text = $"controller calibration {name}" + (same ? "  (matches)" : ord == null ? "  (not selectable)" : "  (differs)");
        _calHint.ToolTip = "The order stored in the IT5701 for this header.";
        _useCal.Visibility = !same && ord != null ? Visibility.Visible : Visibility.Collapsed;
        _useCal.Content = $"Use {name}";
    }

    void LoadFromConfig()
    {
        _loading = true;
        var c = _svc.Config.Rgb;
        _master.IsChecked = c.Enabled;
        foreach (var (rb, kind) in _effects) rb.IsChecked = kind == c.Effect;
        _colorA.Set(c.ColorA, false); _colorB.Set(c.ColorB, false);
        _speed.Value = c.Speed * 100; _brightness.Value = c.Brightness * 100;
        _speedVal.Text = $"{_speed.Value:0}%"; _brightVal.Text = $"{_brightness.Value:0}%";
        _density.Value = Math.Clamp(c.Density, 0, 1) * 100; _glow.Value = Math.Clamp(c.Glow, 0, 1) * 100; _lightning.IsChecked = c.Lightning;
        _densityVal.Text = $"{_density.Value:0}%"; _glowVal.Text = $"{_glow.Value:0}%";
        UpdateEffectControls(LightingLooks.EffectiveEffect(c));
        foreach (var (rb, ord) in _orders) rb.IsChecked = ord == c.StripOrder;
        _dledCount.Value = Math.Clamp(c.DLedCount, 1, 120);
        LoadCaseLayout();
        LoadBalance();
        foreach (var (rb, hz) in _rates) rb.IsChecked = hz == c.EffectiveDLedHz;
        _dledCountVal.Text = $"{_dledCount.Value:0}";
        UpdateMasterNote();
        RefreshLooks();
        _loading = false;
    }

    void UpdateMasterNote()
    {
        var c = _svc.Config.Rgb;
        _masterNote.Text = !c.Enabled ? "Off · nothing is sent" : LightsOut.IsOn(c) ? $"On · lights out (press Lights out to restore {DashboardPage.EffectName(c.LightsOutRestoreEffect ?? EffectKind.Static)})" : "On";
    }

    void OnEffectClicked(EffectKind kind)
    {
        if (LightingLooks.EffectiveEffect(_svc.Config.Rgb) != kind) Edit("effect");
        _svc.Config.Rgb.Effect = kind;
        UpdateEffectControls(kind);
        ConfigStore.Save(_svc.Config);
        SyncLookSelection();
        UpdateRevertBar();
        UpdatePreview();
    }

    /// <summary>Show only the controls the effect uses, name what each colour does, and say how devices show it.</summary>
    void UpdateEffectControls(EffectKind kind)
    {
        var c = _svc.Config.Rgb;
        var (pa, sa) = EffectInfo.ColourRoles(kind);
        _colorA.Title.Text = pa.Length > 0 ? $"Primary · {pa}" : "Primary";
        _colorB.Title.Text = sa.Length > 0 ? $"Secondary · {sa}" : "Secondary";
        _colorB.Root.Visibility = EffectInfo.UsesSecondary(kind) ? Visibility.Visible : Visibility.Collapsed;
        _speedRow.Visibility = EffectInfo.UsesSpeed(kind) ? Visibility.Visible : Visibility.Collapsed;
        bool scene = EffectInfo.IsScene(kind);
        _densityRow.Visibility = EffectInfo.UsesDensity(kind) ? Visibility.Visible : Visibility.Collapsed;
        if (EffectInfo.DensityLabel(kind) is string dl) ((TextBlock)_densityRow.Children[0]).Text = dl;
        _density.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, EffectInfo.DensityLabel(kind) ?? "Amount");
        _lightning.Visibility = EffectInfo.UsesLightning(kind) ? Visibility.Visible : Visibility.Collapsed;
        _fineTune.Visibility = EffectInfo.UsesGlow(kind) ? Visibility.Visible : Visibility.Collapsed;
        if (!scene) { _effectNote.Text = ""; _effectNote.Visibility = Visibility.Collapsed; return; }
        _effectNote.Visibility = Visibility.Visible;
        var cc = Companion.For(c.WithEffect(kind));
        var notes = new List<string> { EffectInfo.Describe(kind) };
        notes.Add(c.MouseLive ? "Mouse: live, so it shows the scene itself." : $"Mouse: an independent companion ({cc.Text}), not the shared animation.");
        if (Al80Kb != null) notes.Add("Keyboard: one colour for all keys, following the scene frame by frame.");
        else if (!(_svc.Config.KeyboardVerified && _svc.Config.KeyboardLiveVerified)) notes.Add("Keyboard: the same companion until its live frames are verified.");
        if (c.RamOnboardEffects) notes.Add("Memory is set to Onboard, so it shows the companion too; choose Streamed under Memory to show the effect itself.");
        if (EffectInfo.UsesLightning(kind) && c.Lightning && Motion.Reduced) notes.Add("Windows animations are off, so the preview does not flash; your lights still show the lightning (switch it off above).");
        _effectNote.Text = string.Join(" ", notes);
    }

    /// <summary>Re-reads the lighting settings (tray, hotkey, the header button and looks change them from outside the page).</summary>
    public void SyncFromConfig()
    {
        LoadFromConfig();
        UpdateRevertBar();
        UpdatePreview();
    }

    // ---- saved looks ------------------------------------------------------------------------------------------------
    readonly ComboBox _lookBox = new() { Width = 210, MinWidth = 210, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _saveLook = new() { Content = "Save current", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _lookMore = new() { Content = "···", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), MinWidth = 36, Padding = new Thickness(8, 0, 8, 0) };
    readonly MenuItem _renameLook = new() { Header = "Rename…" }, _deleteLook = new() { Header = "Delete…" };
    /// <summary>Applies from the look selector (tests: count of apply calls through the shared path).</summary>
    internal int LookApplies { get; private set; }

    FrameworkElement BuildLookRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 4, 0, 4) };
        var label = B.Text("Look", "CardTitle"); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(label);
        _lookBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Lighting look");
        _lookBox.ToolTip = "Apply a starter or saved look (effect, colours, brightness, speed). " + LightingLooks.StarterNote;
        _lookBox.SelectionChanged += (_, _) =>
        {
            if (_loading || _lookBox.SelectedItem is not ComboBoxItem { Tag: LightingLook look }) { UpdateLookMenu(); return; }
            ApplyLookFromPage(look);
        };
        row.Children.Add(_lookBox);
        _saveLook.ToolTip = "Save the current effect, colours, brightness and speed as a look. Nothing is sent to any device.";
        _saveLook.Click += (_, _) => SaveCurrentLook();
        row.Children.Add(_saveLook);
        _lookMore.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "More look actions");
        _lookMore.ToolTip = "Rename or delete the selected saved look";
        var menu = new ContextMenu();
        menu.Items.Add(_renameLook); menu.Items.Add(_deleteLook);
        _lookMore.ContextMenu = menu;
        _lookMore.Click += (_, _) => { UpdateLookMenu(); menu.PlacementTarget = _lookMore; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
        _renameLook.Click += (_, _) => RenameLook();
        _deleteLook.Click += (_, _) => DeleteLook();
        row.Children.Add(_lookMore);
        return row;
    }

    internal void ApplyLookFromPage(LightingLook look)
    {
        _svc.ApplyLook(look, "Lighting page");   // the same path as the tray menu
        LookApplies++;
        SyncFromConfig();
    }

    static FrameworkElement Swatch(int rgb, bool dim)
    {
        var b = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 4, 0), BorderThickness = new Thickness(1), Opacity = dim ? 0.35 : 1,
            Background = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)), VerticalAlignment = VerticalAlignment.Center };
        B.Bd(b, "ControlBorderBrush");
        return b;
    }

    static string LookTip(LightingLook l)
    {
        var lines = new List<string> { l.Summary };
        if (l.Note != null) lines.Add(char.ToUpperInvariant(l.Note[0]) + l.Note[1..] + ".");
        if (!l.UsesSecondary) lines.Add($"{LightingLook.EffectLabel(l.Effect)} uses only the primary colour; the secondary is kept for effects that use it.");
        if (l.Speed == null) lines.Add("Speed is not part of this look (the effect does not use it).");
        if (l.Effect == EffectKind.Breathing) lines.Add("The mouse and keyboard (unless its live frames run) breathe on their own timing, not in step.");
        if (l.Effect == EffectKind.GradientWave) lines.Add("Devices that cannot show a wave keep their existing fallback (a fixed primary colour, or the memory's own wave in Onboard mode).");
        if (EffectInfo.IsScene(l.Effect)) lines.Add(EffectInfo.Describe(l.Effect) + " The mouse shows an independent companion look.");
        if (l.Starter) lines.Add(LightingLooks.StarterNote);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Rebuild the look list and show which look the current settings match. Never applies anything.</summary>
    void RefreshLooks()
    {
        bool was = _loading; _loading = true;
        _lookBox.Items.Clear();
        _lookBox.Items.Add(new ComboBoxItem { Content = B.Text("Current settings", "Muted"), IsEnabled = false, Tag = "current" });
        void Header(string t) => _lookBox.Items.Add(new ComboBoxItem { Content = B.Text(t, "Caption"), IsEnabled = false, Focusable = false });
        string? group = null;
        foreach (var l in LightingLooks.All(_svc.Config))
        {
            if (l.Starter && l.Group != group) { group = l.Group; Header(group ?? "Starters"); }
            if (!l.Starter && _lookBox.Items.OfType<ComboBoxItem>().All(i => i.Tag as string != "saved")) _lookBox.Items.Add(new ComboBoxItem { Content = B.Text("Saved", "Caption"), IsEnabled = false, Focusable = false, Tag = "saved" });
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Swatch(l.ColorA, false));
            if (l.UsesSecondary) content.Children.Add(Swatch(l.ColorB, false));
            content.Children.Add(new TextBlock { Text = l.Name, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            if (l.Note != null) { var note = B.Text(l.Note, "Muted"); note.Margin = new Thickness(8, 0, 0, 0); note.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(note); }
            _lookBox.Items.Add(new ComboBoxItem { Content = content, Tag = l, ToolTip = LookTip(l) });
        }
        SyncLookSelection();
        _loading = was;
    }

    /// <summary>Select the look the current settings match (display only), else "Current settings".</summary>
    void SyncLookSelection()
    {
        bool was = _loading; _loading = true;
        var match = _lookBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is LightingLook l && LightingLooks.Matches(_svc.Config, l));
        var target = match ?? _lookBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        if (!ReferenceEquals(_lookBox.SelectedItem, target)) _lookBox.SelectedItem = target;
        _loading = was;
        UpdateLookMenu();
    }

    LightingLook? SelectedSavedLook => _lookBox.SelectedItem is ComboBoxItem { Tag: LightingLook { Starter: false } l } ? l : null;

    void UpdateLookMenu()
    {
        var l = SelectedSavedLook;
        _renameLook.IsEnabled = _deleteLook.IsEnabled = l != null;
        _renameLook.Header = l != null ? $"Rename “{l.Name}”…" : "Rename… (select a saved look)";
        _deleteLook.Header = l != null ? $"Delete “{l.Name}”…" : "Delete… (select a saved look)";
    }

    void SaveCurrentLook()
    {
        int n = _svc.Config.Looks.Count + 1;
        string suggestion = $"My look {n}";
        while (LightingLooks.NameProblem(_svc.Config, suggestion) != null && n < 999) suggestion = $"My look {++n}";
        var name = LookDialog.AskName(Window.GetWindow(this), "Save current look", "Saves the effect, colours, brightness and speed only. Nothing is sent to any device.", suggestion, "Save",
            v => LightingLooks.NameProblem(_svc.Config, v) ?? (LightingLooks.Capture(_svc.Config, v) == null ? "Stealth is not saved as a look; use Lights out instead." : _svc.Config.Looks.Count >= LightingLooks.MaxSaved ? $"At most {LightingLooks.MaxSaved} saved looks; delete one first." : null));
        if (name == null) return;
        if (LightingLooks.SaveCurrent(_svc.Config, name) is string err) { Log.Warn("look not saved: " + err); return; }
        ConfigStore.Save(_svc.Config);
        Log.Info($"lighting look saved: '{name}' (no device command)");
        RefreshLooks();
    }

    void RenameLook()
    {
        if (SelectedSavedLook is not LightingLook l) return;
        var name = LookDialog.AskName(Window.GetWindow(this), "Rename look", $"New name for “{l.Name}”.", l.Name, "Rename", v => LightingLooks.NameProblem(_svc.Config, v, l.Name));
        if (name == null || LightingLooks.Rename(_svc.Config, l.Name, name) != null) return;
        ConfigStore.Save(_svc.Config);
        RefreshLooks();
    }

    void DeleteLook()
    {
        if (SelectedSavedLook is not LightingLook l) return;
        if (!LookDialog.Confirm(Window.GetWindow(this), "Delete look", $"Delete the saved look “{l.Name}”? Your current lighting does not change.", "Delete")) return;
        LightingLooks.Delete(_svc.Config, l.Name);
        ConfigStore.Save(_svc.Config);
        RefreshLooks();
    }

    // ---- arrange devices ---------------------------------------------------------------------------------------------
    readonly Button _arrangeButton = new() { Content = "Arrange devices" };
    readonly StackPanel _arrangeBar = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10) };
    readonly List<(RadioButton rb, DeviceRole role)> _groupButtons = new();
    readonly Button _rotate = new() { Content = "Rotate 90°" }, _resetLayout = new() { Content = "Reset layout" };
    readonly TextBlock _arrangeInfo = B.Para("", "Muted", new Thickness(0, 8, 0, 0));
    DeviceArrangement _editArrangement = new();
    Point? _dragStart;
    DevicePlacement? _dragFrom;
    bool _dragMoved, _keyMoved;
    /// <summary>Completed arrangement edits saved (tests: one per drop or key release, never per drag step).</summary>
    internal int ArrangementCommits { get; private set; }
    internal bool Arranging => _viz.Arranging;
    internal bool StatusTimerRunning => _preview.IsEnabled;
    internal bool PreviewClockRunning => _ticker.Running;
    internal ComboBox LookSelector => _lookBox;
    internal DeviceArrangement EditArrangement => _editArrangement;
    internal void ResetArrangement() { _editArrangement.Reset(); CommitArrangement("reset"); }
    internal LightingVisualizer Visualizer => _viz;

    FrameworkElement BuildArrangeBar()
    {
        var groups = new Border { Style = B.S("SegmentGroup"), Margin = new Thickness(0, 0, 10, 6) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var r in DeviceArrangement.Groups)
        {
            var rb = new RadioButton { Content = r switch { DeviceRole.Strip => "Case fans", DeviceRole.Ram => "Memory", DeviceRole.Board => "Board", DeviceRole.Keyboard => "Keyboard", _ => "Mouse" },
                Style = B.S("Segment"), GroupName = "arrangeGroup", Padding = new Thickness(10, 0, 10, 0) };
            var role = r;
            rb.Checked += (_, _) => SelectGroup(role);
            _groupButtons.Add((rb, r));
            row.Children.Add(rb);
        }
        groups.Child = row;
        var line = new WrapPanel();
        line.Children.Add(groups);
        _rotate.Margin = new Thickness(0, 0, 8, 6); _resetLayout.Margin = new Thickness(0, 0, 8, 6);
        _rotate.ToolTip = "Turn the selected device a quarter turn clockwise (keyboard and mouse). Keys: R";
        _resetLayout.ToolTip = "Back to the default row; effects use the fixed default order again";
        _rotate.Click += (_, _) => RotateSelected();
        _resetLayout.Click += (_, _) => ResetArrangement();
        var done = new Button { Content = "Done", Style = B.S("PrimaryBtn"), Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(16, 0, 16, 0) };
        done.Click += (_, _) => SetArranging(false);
        line.Children.Add(_rotate); line.Children.Add(_resetLayout); line.Children.Add(done);
        _arrangeBar.Children.Add(line);
        _arrangeBar.Children.Add(B.Para("Drag a device, or select it and use the arrow keys (Shift: larger steps). R turns the keyboard or mouse. Changes are saved when you let go.", "Label", new Thickness(0, 0, 0, 2)));
        return _arrangeBar;
    }

    void SizeViz(double width)
    {
        // arranged (normal view): a little taller, because an arrangement usually has more than one row
        double hh = _viz.Arranging ? Math.Clamp(width * 0.42, 300, 560) : _svc.Config.Arrangement.Arranged ? Math.Clamp(width * 0.32, 220, 380) : Math.Clamp(width * 0.24, 200, 300);
        if (Math.Abs(_viz.Height - hh) > 2) _viz.Height = hh;
    }

    /// <summary>Enter or leave Arrange mode. Entering changes nothing; leaving keeps what was committed.</summary>
    internal void SetArranging(bool on)
    {
        _viz.Arranging = on;
        _editArrangement = _svc.Config.Arrangement.Clone();
        _viz.Arrangement = _editArrangement;
        _viz.Focusable = on;
        _arrangeBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _deviceLinks.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on) { _viz.Cursor = null; _viz.ToolTip = null; _pressRole = null; }
        _arrangeInfo.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _arrangeButton.Content = on ? "Arranging…" : "Arrange devices";
        _arrangeButton.IsEnabled = !on;
        if (on && _viz.Selected == null) _groupButtons.First(g => g.role == DeviceRole.Keyboard).rb.IsChecked = true;
        if (!on) { _viz.Selected = null; foreach (var (rb, _) in _groupButtons) rb.IsChecked = false; }
        SizeViz(_vizCard.ActualWidth > 0 ? _vizCard.ActualWidth : 1100);
        UpdateArrangeInfo();
        _viz.Redraw();
        if (on) _viz.Focus();
    }

    void SelectGroup(DeviceRole role)
    {
        _viz.Selected = role;
        foreach (var (rb, r) in _groupButtons) if (r == role && rb.IsChecked != true) rb.IsChecked = true;
        _rotate.IsEnabled = DeviceArrangement.Rotatable(role);
        UpdateArrangeInfo();
        _viz.Redraw();
    }

    void UpdateArrangeInfo()
    {
        _viz.ArrangeNotes.Clear();
        foreach (var g in DeviceArrangement.Groups)
            _viz.ArrangeNotes[g] = HwSuite.Lighting.LayoutTransform.ArrangeLabel(g, _svc.Config);
        _viz.AxisCaption = HwSuite.Lighting.LayoutTransform.AxisCaption(LightingLooks.EffectiveEffect(_svc.Config.Rgb));
        if (!_viz.Arranging) { _arrangeInfo.Text = ""; return; }
        var c = _svc.Config.Rgb;
        string sel = _viz.Selected is DeviceRole r ? HwSuite.Lighting.LayoutTransform.Participation(r, _svc.Config) + " " : "";
        var shown = LightingLooks.EffectiveEffect(c);
        string effect = shown is EffectKind.GradientWave || EffectInfo.IsScene(shown)
            ? HwSuite.Lighting.LayoutTransform.EffectNote(shown)
            : $"{LightingLook.EffectLabel(shown)} looks the same wherever a device sits; Gradient Wave and the atmosphere effects use positions.";
        string output = !c.Enabled ? " Lighting output is off: the arrangement is saved and used when output is on." : "";
        _arrangeInfo.Text = sel + effect + " Not every device is synchronised: the mouse keeps its own timing." + output;
    }

    void HookArrangeInput()
    {
        _viz.MouseLeftButtonDown += (_, e) =>
        {
            if (!_viz.Arranging) return;
            var p = e.GetPosition(_viz);
            if (_viz.HitTest(p) is not DeviceRole r) return;
            SelectGroup(r);
            _viz.Focus();
            BeginDrag(p);
            _viz.CaptureMouse();
            e.Handled = true;
        };
        _viz.MouseMove += (_, e) => { if (_dragStart != null && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragTo(e.GetPosition(_viz)); };
        // outside Arrange mode a click on a drawing goes to that device's card (a press that moves is not a click)
        _viz.MouseLeftButtonDown += (_, e) => { if (_viz.Arranging) { _pressRole = null; return; } _pressAt = e.GetPosition(_viz); _pressRole = _viz.HitTest(_pressAt); };
        _viz.MouseLeftButtonUp += (_, e) =>
        {
            var role = _pressRole; _pressRole = null;
            if (role != null && ClickDevice(_pressAt, e.GetPosition(_viz))) e.Handled = true;
        };
        _viz.MouseMove += (_, e) =>
        {
            if (_viz.Arranging) return;
            var r = _viz.HitTest(e.GetPosition(_viz));
            _viz.Cursor = r != null ? System.Windows.Input.Cursors.Hand : null;
            _viz.ToolTip = r is DeviceRole d ? $"{DeviceTitle(d)}: show its settings" : null;
        };
        _viz.MouseLeftButtonUp += (_, e) => { if (_dragStart != null) { _viz.ReleaseMouseCapture(); EndDrag(); e.Handled = true; } };
        _viz.LostMouseCapture += (_, _) => { if (_dragStart != null) EndDrag(); };
        _viz.GotKeyboardFocus += (_, _) => _viz.Redraw();
        _viz.LostKeyboardFocus += (_, _) => { if (_keyMoved) { _keyMoved = false; CommitArrangement("keys"); } _viz.Redraw(); };
        _viz.KeyDown += (_, e) => { if (_viz.Arranging && HandleArrangeKey(e.Key, System.Windows.Input.Keyboard.Modifiers)) e.Handled = true; };
        _viz.KeyUp += (_, e) =>
        {
            if (_keyMoved && e.Key is System.Windows.Input.Key.Left or System.Windows.Input.Key.Right or System.Windows.Input.Key.Up or System.Windows.Input.Key.Down)
            { _keyMoved = false; CommitArrangement("keys"); }
        };
        _viz.FocusVisualStyle = null;   // the selected device's outline shows focus (thicker, focus colour)
    }

    internal void BeginDrag(Point p)
    {
        if (_viz.Selected is not DeviceRole r) return;
        _dragStart = p; _dragFrom = _editArrangement.Get(r).Clone(); _dragMoved = false;
    }

    /// <summary>Preview only: the drawing follows the pointer; nothing is saved and the engine's layout is unchanged.</summary>
    internal void DragTo(Point p)
    {
        if (_dragStart is not Point s || _dragFrom == null || _viz.Selected is not DeviceRole r) return;
        double k = _viz.UnitsPerPixel;
        _editArrangement.Set(r, new DevicePlacement { X = _dragFrom.X + (p.X - s.X) * k, Y = _dragFrom.Y + (p.Y - s.Y) * k, Rotation = _dragFrom.Rotation });
        _dragMoved = true;
        _viz.Redraw();
    }

    internal void EndDrag()
    {
        bool moved = _dragMoved;
        _dragStart = null; _dragFrom = null; _dragMoved = false;
        if (moved) CommitArrangement("drag");
    }

    internal bool HandleArrangeKey(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods)
    {
        if (key is >= System.Windows.Input.Key.D1 and <= System.Windows.Input.Key.D5) { SelectGroup(DeviceArrangement.Groups[key - System.Windows.Input.Key.D1]); return true; }
        if (_viz.Selected is not DeviceRole r) return false;
        double step = (mods & System.Windows.Input.ModifierKeys.Shift) != 0 ? 40 : 10;
        var p = _editArrangement.Get(r);
        (double dx, double dy) = key switch
        {
            System.Windows.Input.Key.Left => (-step, 0.0), System.Windows.Input.Key.Right => (step, 0.0),
            System.Windows.Input.Key.Up => (0.0, -step), System.Windows.Input.Key.Down => (0.0, step), _ => (0.0, 0.0)
        };
        if (dx != 0 || dy != 0)
        {
            _editArrangement.Set(r, new DevicePlacement { X = p.X + dx, Y = p.Y + dy, Rotation = p.Rotation });
            _keyMoved = true;
            _viz.Redraw();
            return true;
        }
        if (key == System.Windows.Input.Key.R && DeviceArrangement.Rotatable(r)) { RotateSelected((mods & System.Windows.Input.ModifierKeys.Shift) != 0 ? -90 : 90); return true; }
        return false;
    }

    void RotateSelected(int by = 90)
    {
        if (_viz.Selected is not DeviceRole r || !DeviceArrangement.Rotatable(r)) return;
        var p = _editArrangement.Get(r);
        _editArrangement.Set(r, new DevicePlacement { X = p.X, Y = p.Y, Rotation = p.Rotation + by });
        CommitArrangement("rotate");
    }

    /// <summary>An edit is complete: save once and hand the engine its new layout (only Gradient Wave can change).</summary>
    void CommitArrangement(string how)
    {
        if (_editArrangement.SameAs(_svc.Config.Arrangement)) { _viz.Redraw(); return; }
        _svc.CommitArrangement(_editArrangement);
        ArrangementCommits++;
        Log.Info($"device arrangement saved ({how}): " + (_editArrangement.Arranged
            ? string.Join(", ", DeviceArrangement.Groups.Select(g => { var p = _editArrangement.Get(g); return $"{g} {p.X:0},{p.Y:0}" + (p.Rotation != 0 ? $" {p.Rotation}°" : ""); }))
            : "default row (fixed effect order)"));
        _editArrangement = _svc.Config.Arrangement.Clone();
        _viz.Arrangement = _editArrangement;
        UpdateArrangeInfo();
        SizeViz(_vizCard.ActualWidth > 0 ? _vizCard.ActualWidth : 1100);
        _viz.Redraw();
    }

    // ---- devices ------------------------------------------------------------------------------------------------

    void BuildDevices()
    {
        foreach (var view in LightingDevices.Build(_svc))
        {
            var card = new DeviceCard { Role = view.Role };
            var stack = new StackPanel();

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = Icons.Icon(view.Icon, 20, "TextSecondaryBrush"); ic.Margin = new Thickness(0, 0, 12, 0);
            top.Children.Add(ic);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(B.Text(view.Title, "CardTitle"));
            names.Children.Add(B.Text(view.Subtitle, "Label", null, null, new Thickness(0, 1, 0, 0)));
            Grid.SetColumn(names, 1); top.Children.Add(names);
            card.Pill = new StatusPill { Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(card.Pill, 2); top.Children.Add(card.Pill);
            stack.Children.Add(top);

            card.Detail = B.Para("", "Muted", new Thickness(32, 8, 0, 0));
            stack.Children.Add(card.Detail);

            var body = new StackPanel { Margin = new Thickness(32, 10, 0, 0) };
            if (view.Role == DeviceRole.Keyboard) body.Children.Add(Al80Kb != null ? BuildAl80Controls() : BuildKeyboardControls());
            if (view.Role == DeviceRole.Ram) body.Children.Add(BuildRamControls());
            if (view.Role == DeviceRole.Mouse) body.Children.Add(BuildMouseControls());

            card.Include = new CheckBox { Content = "Include in VANTA lighting", Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Off: VANTA sends nothing to this device; it keeps its own setting." };
            card.Include.IsChecked = IncludeFlag(view.Role);
            var role = view.Role; var cb = card.Include;
            cb.Click += (_, _) => { SetIncludeFlag(role, cb.IsChecked == true); ConfigStore.Save(_svc.Config); UpdatePreview(); };
            body.Children.Add(card.Include);

            card.Advanced = new TextBlock { FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            B.Fg(card.Advanced, "TextSecondaryBrush");
            var detHead = B.Text("Details", "Caption");
            var det = new Expander { Style = B.S("InlineExpander"), Header = detHead, Content = card.Advanced, Margin = new Thickness(0, 4, 0, 0) };
            body.Children.Add(det);
            stack.Children.Add(body);

            card.Root = new Border { Style = B.S("Card"), Child = stack };
            _cards.Add(card);
        }
    }

    /// <summary>
    /// Two independent columns (no row alignment, so a tall card leaves no hole beside a short one): left the case front,
    /// board and mouse, right the memory and the keyboard (the two cards with controls). One column when narrow.
    /// </summary>
    void LayoutDevices(double width)
    {
        int cols = width >= 900 ? 2 : 1;
        if (cols == _deviceColumns && _deviceGrid.Children.Count > 0) return;
        _deviceColumns = cols;
        foreach (var c in _cards) (c.Root.Parent as Panel)?.Children.Remove(c.Root);
        _deviceGrid.Children.Clear();
        _deviceGrid.ColumnDefinitions.Clear();
        _deviceGrid.RowDefinitions.Clear();
        DeviceCard? Get(DeviceRole r) => _cards.FirstOrDefault(c => c.Role == r);
        if (cols == 1)
        {
            var one = new StackPanel();
            foreach (var r in new[] { DeviceRole.Strip, DeviceRole.Ram, DeviceRole.Board, DeviceRole.Keyboard, DeviceRole.Mouse })
                if (Get(r) is DeviceCard c) { c.Root.Margin = new Thickness(0, 0, 0, 12); one.Children.Add(c.Root); }
            _deviceGrid.Children.Add(one);
            return;
        }
        _deviceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _deviceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var right = new StackPanel();
        foreach (var r in new[] { DeviceRole.Strip, DeviceRole.Board, DeviceRole.Mouse }) if (Get(r) is DeviceCard c) { c.Root.Margin = new Thickness(0, 0, 0, 12); left.Children.Add(c.Root); }
        foreach (var r in new[] { DeviceRole.Ram, DeviceRole.Keyboard }) if (Get(r) is DeviceCard c) { c.Root.Margin = new Thickness(0, 0, 0, 12); right.Children.Add(c.Root); }
        Grid.SetColumn(right, 1);
        _deviceGrid.Children.Add(left); _deviceGrid.Children.Add(right);
    }

    readonly List<(RadioButton rb, bool onboard)> _ramModes = new();
    readonly List<(RadioButton rb, int hz)> _ramRates = new();
    readonly StackPanel _ramRateRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock _ramExplain = B.Para("", "Muted", new Thickness(0, 8, 0, 6));

    /// <summary>
    /// RAM mode and its consequence. Streamed: every frame goes over the SMBus, in step with the other devices, at a measured
    /// CPU cost. Onboard: the modules run Breathing / their rainbow wave themselves, set once per change, then nothing is
    /// streamed, but they are not in step.
    /// </summary>
    StackPanel BuildRamControls()
    {
        var s = new StackPanel();
        var group = new Border { Style = B.S("SegmentGroup") };
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, onboard) in new[] { ("Streamed · in step", false), ("Onboard · own timing", true) })
        {
            var rb = new RadioButton { Content = label, Style = B.S("Segment"), GroupName = "ramMode" };
            rb.Click += (_, _) => { if (_loading) return; _svc.Config.Rgb.RamOnboardEffects = onboard; ConfigStore.Save(_svc.Config); UpdateRamControls(); UpdatePreview(); };
            _ramModes.Add((rb, onboard));
            seg.Children.Add(rb);
        }
        group.Child = seg;
        s.Children.Add(group);
        s.Children.Add(_ramExplain);

        var rl = B.Text("Streamed updates /s", "Muted", null, null, new Thickness(0, 0, 10, 0)); rl.VerticalAlignment = VerticalAlignment.Center;
        rl.ToolTip = "Streamed RAM updates per second during animated effects. Measured whole-machine CPU: 8/s 0.75%, 10/s 0.94%, 12/s 1.08%, 15/s 1.31%.";
        _ramRateRow.Children.Add(rl);
        var rg = new Border { Style = B.S("SegmentGroup") };
        var rr = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var hz in RgbConfig.RamRates)
        {
            var rb = new RadioButton { Content = $"{hz}", Style = B.S("Segment"), GroupName = "ramRate", Padding = new Thickness(10, 0, 10, 0), ToolTip = rl.ToolTip };
            rb.Click += (_, _) => { if (_loading) return; _svc.Config.Rgb.RamMaxHz = hz; ConfigStore.Save(_svc.Config); UpdateRamControls(); };
            _ramRates.Add((rb, hz));
            rr.Children.Add(rb);
        }
        rg.Child = rr;
        _ramRateRow.Children.Add(rg);
        s.Children.Add(_ramRateRow);
        UpdateRamControls();
        return s;
    }

    void UpdateRamControls()
    {
        var c = _svc.Config.Rgb;
        foreach (var (rb, onboard) in _ramModes) rb.IsChecked = onboard == c.RamOnboardEffects;
        foreach (var (rb, hz) in _ramRates) rb.IsChecked = hz == c.EffectiveRamHz;
        _ramRateRow.Visibility = c.RamOnboardEffects ? Visibility.Collapsed : Visibility.Visible;
        double cpu = c.EffectiveRamHz switch { 8 => 0.75, 10 => 0.94, 12 => 1.08, 15 => 1.31, _ => c.EffectiveRamHz * 0.094 };
        _ramExplain.Text = c.RamOnboardEffects
            ? "Breathing and Gradient Wave run on the memory's own controllers: VANTA sets them once per change and then streams nothing, so the memory adds no ongoing SMBus traffic. They keep their own timing and are not in step with the other devices; Gradient Wave becomes the modules' own rainbow wave. Static, Thermal and Stealth are still sent as colours. The atmosphere effects need Streamed; in Onboard mode the memory shows their calm companion instead."
            : $"Every animation frame is sent over the SMBus, so the memory stays in step with the other devices. Each frame keeps a CPU core busy for about 16 ms of bus transfer: measured about {cpu:0.0#}% of the whole machine at {c.EffectiveRamHz} updates/s.";
    }

    enum MouseMode { Own, Locked, Live }
    readonly List<(RadioButton rb, MouseMode mode)> _mouseModes = new();
    readonly TextBlock _mouseExplain = B.Para("", "Muted", new Thickness(0, 8, 0, 6));

    static MouseMode CurrentMouseMode(RgbConfig c) => c.MouseLive ? MouseMode.Live : c.MouseSync ? MouseMode.Locked : MouseMode.Own;

    /// <summary>
    /// Mouse lighting: onboard effects on its own timing (as before), breathing locked to VANTA's (MouseSync), or live
    /// (MouseLive: the frame colour streamed like the other devices, so it follows every effect and scene).
    /// </summary>
    StackPanel BuildMouseControls()
    {
        var s = new StackPanel();
        s.Children.Add(B.Text("Mouse lighting", "Muted", null, null, new Thickness(0, 0, 0, 6)));
        var group = new Border { Style = B.S("SegmentGroup"), HorizontalAlignment = HorizontalAlignment.Left };
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, mode) in new[] { ("Own timing", MouseMode.Own), ("Locked breathing", MouseMode.Locked), ("Live · every effect", MouseMode.Live) })
        {
            var rb = new RadioButton { Content = label, Style = B.S("Segment"), GroupName = "mouseSync" };
            rb.Click += (_, _) =>
            {
                if (_loading) return;
                _svc.Config.Rgb.MouseLive = mode == MouseMode.Live;
                _svc.Config.Rgb.MouseSync = mode == MouseMode.Locked;
                ConfigStore.Save(_svc.Config); UpdateMouseControls(); UpdatePreview();
            };
            _mouseModes.Add((rb, mode));
            seg.Children.Add(rb);
        }
        group.Child = seg;
        s.Children.Add(group);
        s.Children.Add(_mouseExplain);
        UpdateMouseControls();
        return s;
    }

    void UpdateMouseControls()
    {
        var c = _svc.Config.Rgb;
        var mode = CurrentMouseMode(c);
        foreach (var (rb, m) in _mouseModes) rb.IsChecked = m == mode;
        _mouseExplain.Text = mode switch
        {
            MouseMode.Live => "VANTA sends the mouse its colour of every frame, up to 30 per second, so it follows every effect and scene like the other devices (this is how SignalRGB drives this mouse; tested here at up to 60 per second without stutter). Nothing is stored on the mouse; it may use a little more battery.",
            MouseMode.Locked => $"Only for Breathing: the mouse runs its own breathing, VANTA matches its speed (0.512 s steps) and shape, and sets it at the moment that makes both darkest together, then lines it up again every 5 minutes (one write each time). Measured timing against the case fans: {c.MouseSyncLatencyMs:+0;-0} ms.",
            _ => "The mouse runs its own onboard effect, set once per change; it drifts against the other devices, and scenes show a calm companion colour."
        };
    }

    // ---- keyboard: guided verification, then capability-true sync ----------------------------------------------------
    enum KbStep { NotConnected, Vendor, NeedSleep, Ready, Sending, AskAnswer, Declined, Failed, Verified, LiveTrial, Live, LinkLost }
    readonly TextBlock _kbHead = B.Text("", null, 13.5, FontWeights.SemiBold);
    readonly TextBlock _kbBody = B.Para("", "Muted", new Thickness(0, 4, 0, 0));
    readonly Button _kbPrimary = new() { Margin = new Thickness(0, 0, 8, 0) };
    readonly Button _kbSecondary = new() { Margin = new Thickness(0, 0, 8, 0) };
    readonly Button _kbGhost = new();
    readonly StackPanel _kbSleepRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    readonly List<(RadioButton rb, byte idx)> _kbSleep = new();
    readonly StackPanel _kbWaveRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    readonly ComboBox _kbWave = new() { MinWidth = 240 };
    readonly ComboBox _kbMode = new() { Width = 210 };
    readonly StackPanel _kbActions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 8) };
    KbStep _kbStep = KbStep.NotConnected;
    bool _kbAwaitingAnswer, _kbSending, _kbDeclined, _kbFailed, _kbPresent;
    string _kbLastResult = "", _kbProfileDetail = "", _kbVendor = "";
    ILovbeeProfile? _kbProfile;
    DateTime _kbChecked = DateTime.MinValue;

    KeyboardSink? Kb => _svc.Rgb.Sinks.OfType<KeyboardSink>().FirstOrDefault();
    /// <summary>1.18.1: the YUNZII AL80 (real app only; fixtures still simulate the earlier keyboard).</summary>
    Al80Sink? Al80Kb => _svc.Fixture == null ? _svc.Rgb.Sinks.OfType<Al80Sink>().FirstOrDefault() : null;
    readonly System.Windows.Controls.Primitives.ToggleButton _al80Side = new();
    readonly System.Windows.Controls.Primitives.ToggleButton _al80PerKey = new();
    readonly TextBlock _al80Line = B.Text("", "MonoMuted", 11);

    StackPanel BuildAl80Controls()
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        _al80Side.Style = B.S("Switch");
        _al80Side.IsChecked = _svc.Config.Al80.SyncSideBar;
        _al80Side.Click += (_, _) => { lock (_svc.Config.Sync) _svc.Config.Al80.SyncSideBar = _al80Side.IsChecked == true; ConfigStore.Save(_svc.Config); };
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new StackPanel();
        t.Children.Add(B.Text("Logo / side light follows too", null));
        t.Children.Add(B.Para("The AL80's separate 3-LED light takes the keyboard's overall colour, also during per-key lighting. Off: it keeps its own effect.", "Label", new Thickness(0, 2, 0, 0)));
        row.Children.Add(t);
        Grid.SetColumn(_al80Side, 1); row.Children.Add(_al80Side);
        s.Children.Add(row);
        // 1.19.0: per-key frames on YUNZII's SignalRGB firmware
        _al80PerKey.Style = B.S("Switch");
        _al80PerKey.IsChecked = _svc.Config.Al80.PerKey;
        _al80PerKey.Click += (_, _) => { lock (_svc.Config.Sync) _svc.Config.Al80.PerKey = _al80PerKey.IsChecked == true; ConfigStore.Save(_svc.Config); };
        var pk = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        pk.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pk.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pt = new StackPanel();
        pt.Children.Add(B.Text("Per-key lighting", null));
        pt.Children.Add(B.Para("Every key gets its own colour (the AL80's firmware 1.2.5 has YUNZII's SignalRGB stream), so rain falls down the rows. Off, or on firmware without it: one colour for the board. The 3-LED logo/side light follows with the switch above.", "Label", new Thickness(0, 2, 0, 0)));
        pk.Children.Add(pt);
        Grid.SetColumn(_al80PerKey, 1); pk.Children.Add(_al80PerKey);
        s.Children.Add(pk);
        _al80Line.TextWrapping = TextWrapping.Wrap; _al80Line.TextTrimming = TextTrimming.None; _al80Line.TextAlignment = TextAlignment.Left;
        s.Children.Add(_al80Line);
        s.Children.Add(B.Para("Screen, stored animations, the keyboard's own effects and the keymap are on the AL80 page.", "Label", new Thickness(0, 4, 0, 0)));
        return s;
    }

    void UpdateAl80Card()
    {
        var a = _svc.Al80;
        _al80Line.Text = a == null ? "" : (_svc.Config.Al80.Enabled ? a.LightStatus + (a.PerKeyInfo.Length > 0 ? "\n" + a.PerKeyInfo : "") : "AL80 control is off (AL80 page)");
    }

    StackPanel BuildKeyboardControls()
    {
        _kbPrimary.Style = B.S("PrimaryBtn"); _kbGhost.Style = B.S("GhostBtn");
        var s = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        s.Children.Add(_kbHead);
        s.Children.Add(_kbBody);

        var sleepLabel = B.Text("Sleep timer", "Muted", null, null, new Thickness(0, 0, 10, 0)); sleepLabel.VerticalAlignment = VerticalAlignment.Center;
        _kbSleepRow.Children.Add(sleepLabel);
        var sg = new Border { Style = B.S("SegmentGroup") };
        var sr = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var idx in ILovbeeProfile.SleepChoices)
        {
            var rb = new RadioButton { Content = ILovbeeProfile.SleepText(idx), Style = B.S("Segment"), GroupName = "kbSleep", Padding = new Thickness(10, 0, 10, 0) };
            rb.Click += (_, _) => { lock (_svc.Config.Sync) _svc.Config.KeyboardSleepChoice = idx; ConfigStore.Save(_svc.Config); _kbChecked = DateTime.MinValue; };
            _kbSleep.Add((rb, idx));
            sr.Children.Add(rb);
        }
        sg.Child = sr;
        _kbSleepRow.Children.Add(sg);
        s.Children.Add(_kbSleepRow);

        _kbActions.Children.Add(_kbPrimary); _kbActions.Children.Add(_kbSecondary); _kbActions.Children.Add(_kbGhost);
        s.Children.Add(_kbActions);

        var waveLabel = B.Text("Gradient Wave on the keyboard", "Muted", null, null, new Thickness(0, 0, 10, 0)); waveLabel.VerticalAlignment = VerticalAlignment.Center;
        waveLabel.ToolTip = "Without live frames the keyboard cannot show the two-colour wave. Choose a fixed primary colour, or one of its own animations (runs on its own timing and may use its own colours).";
        _kbWaveRow.Children.Add(waveLabel);
        _kbWave.Items.Add("Fixed primary colour");
        foreach (var m in KeyboardLightingState.NativeAnimatedModes.Where(m => m != KeyboardLightingState.ModeBreathing))
            _kbWave.Items.Add(new ComboBoxItem { Content = $"Its own: {KeyboardLightingState.ModeNames[m]}", Tag = (int)m });
        _kbWave.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _svc.Config.Rgb.KeyboardWaveMode = _kbWave.SelectedItem is ComboBoxItem ci && ci.Tag is int mode ? mode : null;
            ConfigStore.Save(_svc.Config);
        };
        _kbWaveRow.Children.Add(_kbWave);
        s.Children.Add(_kbWaveRow);

        for (int i = 0; i < KeyboardLightingState.ModeNames.Length; i++) _kbMode.Items.Add($"{i,2}  {KeyboardLightingState.ModeNames[i]}");
        _kbMode.SelectedIndex = KeyboardLightingState.ModeSteady;
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var ml = B.Text("Test mode (advanced)", "Muted", null, null, new Thickness(0, 0, 10, 0)); ml.VerticalAlignment = VerticalAlignment.Center;
        modeRow.Children.Add(ml); modeRow.Children.Add(_kbMode);
        s.Children.Add(modeRow);

        if (Kb is KeyboardSink kb)
            kb.Controller.TestFinished += (sent, result) => Dispatcher.BeginInvoke(() =>
            {
                _kbSending = false;
                _kbLastResult = result;
                _kbAwaitingAnswer = sent;
                _kbFailed = !sent;
                if (sent) _kbDeclined = false;
                UpdateKeyboardCard();
            });
        _kbPrimary.Click += (_, _) =>
        {
            switch (_kbStep)
            {
                case KbStep.Ready: case KbStep.Failed: case KbStep.Declined: SendKeyboardTest(); break;
                case KbStep.LiveTrial:
                    lock (_svc.Config.Sync) { _svc.Config.KeyboardLiveVerified = true; _svc.Config.KeyboardLiveTrial = false; }
                    ConfigStore.Save(_svc.Config);
                    Log.Info("keyboard: owner confirmed live frames follow the effect (KeyboardLiveVerified = true)");
                    break;
                case KbStep.AskAnswer:
                    lock (_svc.Config.Sync) _svc.Config.KeyboardVerified = true;
                    ConfigStore.Save(_svc.Config);
                    _kbAwaitingAnswer = false;
                    Kb?.Controller.EndTestHold();
                    Log.Info("keyboard: owner confirmed the test (solid red); sync enabled (KeyboardVerified = true)");
                    break;
            }
            UpdateKeyboardCard();
        };
        _kbSecondary.Click += (_, _) =>
        {
            if (_kbStep == KbStep.Verified)
            {
                lock (_svc.Config.Sync) _svc.Config.KeyboardLiveTrial = true;
                Log.Info("keyboard: owner started a live-frame trial");
            }
            else if (_kbStep == KbStep.LiveTrial)
            {
                lock (_svc.Config.Sync) _svc.Config.KeyboardLiveTrial = false;
                Log.Info("keyboard: owner reported live frames did not follow the effect; onboard settings stay in use");
            }
            else if (_kbStep == KbStep.AskAnswer)
            {
                _kbAwaitingAnswer = false; _kbDeclined = true;
                Kb?.Controller.EndTestHold();
                Log.Info("keyboard: owner reported the test did not show solid red; sync stays locked");
            }
            UpdateKeyboardCard();
        };
        _kbGhost.Click += (_, _) =>
        {
            if (_kbStep == KbStep.AskAnswer) SendKeyboardTest();
            else if (_kbStep == KbStep.Live)
            {
                lock (_svc.Config.Sync) { _svc.Config.KeyboardLiveVerified = false; _svc.Config.KeyboardLiveTrial = false; }
                ConfigStore.Save(_svc.Config);
                Log.Info("keyboard: owner switched to onboard settings only (KeyboardLiveVerified = false)");
            }
            else if (_kbStep == KbStep.Verified)
            {
                lock (_svc.Config.Sync) _svc.Config.KeyboardVerified = false;
                ConfigStore.Save(_svc.Config);
                Log.Info("keyboard: owner re-locked sync");
            }
            UpdateKeyboardCard();
        };
        return s;
    }

    void SendKeyboardTest()
    {
        if (Kb is not KeyboardSink kb) return;
        var prof = ILovbeeProfile.Read(null, _svc.Config.KeyboardSleepChoice, out var detail);
        _kbProfile = prof; _kbProfileDetail = detail;
        if (prof == null) { _kbLastResult = "Not sent: " + detail; _kbFailed = true; return; }
        _kbSending = true; _kbFailed = false;
        kb.Controller.RequestTestWrite(KeyboardLightingState.TestRed((byte)Math.Max(0, _kbMode.SelectedIndex), prof.Speed));
    }

    void UpdateKeyboardCard()
    {
        if (Al80Kb != null) { UpdateAl80Card(); return; }
        var kb = Kb;
        var fx = _svc.Fixture;
        var now = DateTime.UtcNow;
        if (now - _kbChecked > TimeSpan.FromSeconds(2))
        {
            _kbChecked = now;
            if (fx != null) { _kbProfile = new ILovbeeProfile(2, 4, "vendor profile 1 (simulated)", true); _kbProfileDetail = fx.Keyboard.Profile; _kbPresent = fx.Keyboard.Present; }
            else
            {
                _kbProfile = ILovbeeProfile.Read(null, _svc.Config.KeyboardSleepChoice, out _kbProfileDetail);
                _kbPresent = LightingDevices.KeyboardPresent(_svc);
            }
            _kbVendor = _svc.LiveKeyboardOwnership() ?? "";
        }
        bool connected = fx?.Keyboard.Connected ?? kb?.Controller.Connected ?? false;
        bool gaveUp = fx != null ? fx.Keyboard.LinkStatus.Contains("reopen attempts") : kb?.Controller.ReopenGaveUp == true;
        bool liveNow = fx != null ? _svc.Config.Rgb.Enabled && _svc.Config.KeyboardLiveVerified && _svc.Config.Rgb.Effect != EffectKind.Stealth : kb?.Live == true;
        bool verified = _svc.Config.KeyboardVerified;
        _kbStep = !_kbPresent && !connected ? KbStep.NotConnected
            : _kbVendor.Length > 0 ? KbStep.Vendor
            : gaveUp ? KbStep.LinkLost
            : _kbSending ? KbStep.Sending
            : _kbAwaitingAnswer ? KbStep.AskAnswer
            : verified && _svc.Config.KeyboardLiveTrial ? KbStep.LiveTrial
            : verified && _svc.Config.KeyboardLiveVerified ? KbStep.Live
            : verified ? KbStep.Verified
            : _kbProfile == null ? KbStep.NeedSleep
            : _kbFailed ? KbStep.Failed
            : _kbDeclined ? KbStep.Declined
            : KbStep.Ready;
        string sleepLine = _kbProfile == null ? "" : _kbProfile.FromVendorProfile
            ? $"Its sleep timer stays at {ILovbeeProfile.SleepText(_kbProfile.Sleep)} (from the iLovbee app's {_kbProfile.Source})."
            : $"Its sleep timer will be set to {ILovbeeProfile.SleepText(_kbProfile.Sleep)} (your choice).";
        var mapping = KeyboardLightingState.From(_svc.Config.Rgb, kb?.SavedSpeed ?? KeyboardLightingState.VendorDefaultSpeed, ignoreOutputSwitch: true);
        (string head, string body, string? primary, string? secondary, string? ghost, string headKey) ui = _kbStep switch
        {
            KbStep.NotConnected => ("Keyboard not found", "Connect the iLovbee i87 (cable or receiver).", null, null, null, "TextBrush"),
            KbStep.Vendor => ("iLovbee software is running", "VANTA checks for it before every keyboard write, live frames included, and writes nothing while it runs. Close it from the tray to continue.", null, null, null, "WarnBrush"),
            KbStep.LinkLost => ("Keyboard link lost", "Writes failed and four reopen attempts did not recover it. Unplug the keyboard (or its receiver) and plug it in again; VANTA reconnects on its own.", null, null, null, "DangerBrush"),
            KbStep.NeedSleep => ("Choose the sleep timer first", _kbProfileDetail, null, null, null, "WarnBrush"),
            KbStep.Ready => ("Not verified yet", "One test: the keyboard turns solid red at full brightness and keeps it (the vendor's stored lighting command). " + sleepLine, "Send test (solid red)", null, null, "WarnBrush"),
            KbStep.Sending => ("Sending the test…", sleepLine, null, null, null, "TextBrush"),
            KbStep.AskAnswer => ("Is the keyboard solid red now?", "Yes enables lighting sync (once per change). No keeps it locked.", "Yes, sync it", "No", "Send again", "TextBrush"),
            KbStep.Declined => ("Kept locked", "Nothing more is sent. Note what the keyboard showed; the mode can be changed under Test mode.", "Try again", null, null, "WarnBrush"),
            KbStep.Failed => ("Test not sent", _kbLastResult, "Try again", null, null, "WarnBrush"),
            KbStep.LiveTrial => ("Is the keyboard following the effect?", "Live frames are running now: the keys should breathe or wave with the case fans and memory. Yes keeps live frames; No goes back to onboard settings.", "Yes, keep live", "No", null, "TextBrush"),
            KbStep.Live => (liveNow ? "Live frames · in step with the case and memory" : "Live frames ready",
                liveNow ? "Follows the effect key by key on the same clock as the case fans and memory (about 12 updates per second; nothing is stored per frame). Stealth and Lights out use the keyboard's stored brightness 0."
                        : "Every effect except Stealth is sent as live frames while lighting output is on. " + sleepLine,
                null, null, "Use onboard effects only", "TextBrush"),
            _ => ("Onboard setting · own timing", (mapping?.Description ?? "Not included") + ". Set once per change; not in step with the other devices. " + sleepLine, null, "Try live frames", "Re-lock", "TextBrush")
        };
        _kbHead.Text = ui.head; _kbHead.SetResourceReference(TextBlock.ForegroundProperty, ui.headKey);
        _kbBody.Text = ui.body;
        void Set(Button b, string? text) { b.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible; if (text != null) b.Content = text; }
        Set(_kbPrimary, ui.primary); Set(_kbSecondary, ui.secondary); Set(_kbGhost, ui.ghost);
        bool anyAction = ui.primary != null || ui.secondary != null || ui.ghost != null;
        _kbActions.Visibility = anyAction ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in new[] { _kbPrimary, _kbSecondary, _kbGhost }) b.IsEnabled = fx == null;   // fixtures never write
        _kbSleepRow.Visibility = _kbStep == KbStep.NeedSleep || (_kbProfile != null && !_kbProfile.FromVendorProfile && !verified) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (rb, idx) in _kbSleep) rb.IsChecked = _svc.Config.KeyboardSleepChoice == idx;
        _kbWaveRow.Visibility = _kbStep == KbStep.Verified ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)_kbMode.Parent).Visibility = verified ? Visibility.Collapsed : Visibility.Visible;
        if (_kbWaveRow.Visibility == Visibility.Visible)
        {
            _loading = true;
            var want = _svc.Config.Rgb.KeyboardWaveMode;
            _kbWave.SelectedItem = want == null ? _kbWave.Items[0] : _kbWave.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag! == want) ?? _kbWave.Items[0];
            _loading = false;
        }
    }

    bool IncludeFlag(DeviceRole r) => r switch
    {
        DeviceRole.Strip => _svc.Config.Rgb.DLedStrip, DeviceRole.Board => _svc.Config.Rgb.Motherboard, DeviceRole.Mouse => _svc.Config.Rgb.Mouse,
        DeviceRole.Ram => _svc.Config.Rgb.Ram, _ => _svc.Config.Rgb.Keyboard
    };

    void SetIncludeFlag(DeviceRole r, bool v)
    {
        switch (r)
        {
            case DeviceRole.Strip: _svc.Config.Rgb.DLedStrip = v; break;
            case DeviceRole.Board: _svc.Config.Rgb.Motherboard = v; break;
            case DeviceRole.Mouse: _svc.Config.Rgb.Mouse = v; break;
            case DeviceRole.Ram: _svc.Config.Rgb.Ram = v; break;
            case DeviceRole.Keyboard: _svc.Config.Rgb.Keyboard = v; break;
        }
    }

    List<LightingDeviceView> _views = new();

    void UpdatePreview()
    {
        var stripSink = _svc.Rgb.Sinks.OfType<DLedSink>().FirstOrDefault();
        if (stripSink != null && (DateTime.UtcNow - _lastStripSample).TotalSeconds >= 1)
        {
            double dt = (DateTime.UtcNow - _lastStripSample).TotalSeconds;
            if (_lastStripSample != DateTime.MinValue && dt < 5) _stripAcceptedPerSec = (stripSink.FramesSent - _lastStripSent) / dt;
            _lastStripSent = stripSink.FramesSent; _lastStripSample = DateTime.UtcNow;
        }
        _stripRate.Text = _svc.Rgb.IsActive && stripSink?.Available == true ? $"accepted {_stripAcceptedPerSec,4:0}/s of {_svc.Config.Rgb.EffectiveDLedHz}/s (render {_svc.Rgb.Fps:0.0} fps)" : "";
        _stripRate.ToolTip = "Updates the controller accepted. Unchanged frames are skipped, so still or slow effects show fewer.";
        UpdateAbState();
        bool canTest = _svc.Config.Rgb.Enabled && _svc.Config.Rgb.DLedStrip && stripSink?.Available == true && stripSink.Blocked == null;
        foreach (var b in _testButtons) b.IsEnabled = canTest;
        foreach (var b in _testButtons) b.ToolTip = !canTest ? "Needs output on and the case lighting included" : b.Tag is string tip ? tip : "Show for 8 s";
        if (stripSink?.TestRemainingSeconds > 0) _orderResult.Text = $"showing for {Math.Ceiling(stripSink.TestRemainingSeconds):0} s";
        else if (_orderResult.Text.StartsWith("showing for")) _orderResult.Text = "Pick what you saw.";
        UpdateCalibrationHint();
        UpdateMasterNote();
        SyncLookSelection();
        if (!_viz.Arranging && !ReferenceEquals(_viz.Arrangement, _svc.Config.Arrangement) && !_viz.Arrangement.SameAs(_svc.Config.Arrangement)) _viz.Arrangement = _svc.Config.Arrangement.Clone();
        if (_viz.Arranging) UpdateArrangeInfo();

        _views = LightingDevices.Build(_svc);
        foreach (var card in _cards)
        {
            var v = _views.FirstOrDefault(x => x.Role == card.Role);
            if (v == null) continue;
            // a device that has not yet accepted the current look says so (no lifetime totals stand in for it)
            bool waiting = !v.Status.Confirmed && v.Status.State is DeviceLightState.Shared or DeviceLightState.Independent or DeviceLightState.Dark;
            card.Pill.Set(waiting ? v.Status.Headline + " · waiting" : v.Status.Headline, waiting ? Tone.Info : StatusPill.ToneFor(v.Status.State));
            string confirm = v.Status.State is DeviceLightState.Shared or DeviceLightState.Independent or DeviceLightState.Dark
                ? (v.Status.Confirmed ? "" : " Waiting for the device to accept the current look.") : "";
            card.Detail.Text = v.Status.Detail + confirm;
            // the keyboard's guided panel says the same thing in more detail, except when something needs attention
            card.Detail.Visibility = card.Role == DeviceRole.Keyboard && Al80Kb == null ? Visibility.Collapsed : Visibility.Visible;
            card.Advanced.Text = v.Advanced;
            if (card.Include != null) { card.Include.IsChecked = IncludeFlag(card.Role); card.Include.IsEnabled = v.Facts.Available && _svc.Fixture == null; }
        }
        UpdateKeyboardCard();

        bool preview = !_svc.Config.Rgb.Enabled || _svc.Fixture != null;
        if (_svc.Fixture != null) _vizPill.Set("Simulated fixture · not a hardware observation", Tone.Warn);
        else if (!_svc.Config.Rgb.Enabled) _vizPill.Set("Simulated preview · output off", Tone.Neutral);
        else _vizPill.Set("What VANTA sends · not a camera view", Tone.Accent);
        const string Emitted = " LEDs are drawn as emitted light, so dim levels glow here as they do on the devices.";
        _vizNote.Text = preview
            ? "Nothing is being sent: this is the effect simulated through each device's capabilities. Devices with their own onboard effect are approximated." + Emitted
            : "Colours are the frames VANTA offers each device; devices running their own onboard effect show that setting (approximate timing). Front fans and logo share one LED chain and show its average colour; the chain is drawn LED by LED in order below them." + Emitted;

        if (!_ticker.Running) UpdateVisualizer(Environment.TickCount64 / 1000.0);   // reduced motion or hidden: status-rate updates only
    }

    // ---- layout -----------------------------------------------------------------------------------------------------
    readonly Grid _layout = new();
    readonly StackPanel _leftCol = new(), _rightCol = new();
    Border _vizCard = null!, _effectPanel = null!;
    Expander _stripExpander = null!, _balanceExpander = null!;
    ScrollViewer _scroll = null!;
    PresentationTicker _ticker = null!;
    bool? _wide;

    /// <summary>
    /// One scroll surface. Wide: effect controls and case setup on the left, preview and devices on the right. Narrow: one
    /// column, effect first.
    /// </summary>
    void ArrangeLayout(double width)
    {
        bool wide = width >= 1000;
        if (_wide != wide)
        {
            _wide = wide;
            _leftCol.Children.Clear(); _rightCol.Children.Clear();
            if (wide)
            {
                _layout.ColumnDefinitions[0].Width = new GridLength(380);
                _layout.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                _leftCol.Visibility = Visibility.Visible;
                _leftCol.Children.Add(_effectPanel); _leftCol.Children.Add(_stripExpander); _leftCol.Children.Add(_balanceExpander);
                _rightCol.Children.Add(_vizCard); _rightCol.Children.Add(_deviceGrid);
            }
            else
            {
                _layout.ColumnDefinitions[0].Width = new GridLength(0);
                _layout.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                _leftCol.Visibility = Visibility.Collapsed;
                _rightCol.Children.Add(_effectPanel); _rightCol.Children.Add(_vizCard); _rightCol.Children.Add(_deviceGrid); _rightCol.Children.Add(_stripExpander); _rightCol.Children.Add(_balanceExpander);
            }
        }
        LayoutDevices(Math.Min(wide ? width - 396 - 12 : width - 12, 1880 - 396));
    }

    // UI checks: which effect controls are shown
    internal bool SpeedShown => _speedRow.Visibility == Visibility.Visible;
    internal string? AmountShown => _densityRow.Visibility == Visibility.Visible ? ((TextBlock)_densityRow.Children[0]).Text : null;
    internal bool LightningShown => _lightning.Visibility == Visibility.Visible;
    internal bool FineTuningShown => _fineTune.Visibility == Visibility.Visible;

    /// <summary>Scroll so the hardware preview is in view (fixture films).</summary>
    internal void ScrollToVisualizer() { _vizCard.BringIntoView(); _scroll.UpdateLayout(); }

    /// <summary>Scroll so the device cards are in view (screenshots).</summary>
    internal void ScrollToDevices() { _deviceGrid.BringIntoView(); _scroll.UpdateLayout(); _scroll.ScrollToVerticalOffset(Math.Max(0, _deviceGrid.TranslatePoint(new Point(0, 0), _layout).Y - 8)); }

    // ---- hardware view: capability-true preview on an independent presentation clock -------------------------------
    static Rgb Scale(Rgb c, double k) => new((byte)Math.Clamp(c.R * k, 0, 255), (byte)Math.Clamp(c.G * k, 0, 255), (byte)Math.Clamp(c.B * k, 0, 255));

    static Rgb Hue(double h)
    {
        h = (h % 1 + 1) % 1 * 6;
        double x = 1 - Math.Abs(h % 2 - 1);
        var (r, g, b) = h < 1 ? (1, x, 0d) : h < 2 ? (x, 1, 0d) : h < 3 ? (0d, 1, x) : h < 4 ? (0d, x, 1d) : h < 5 ? (x, 0d, 1d) : (1d, 0d, x);
        return new Rgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    static string Short(DeviceStatus s) => s.State switch
    {
        DeviceLightState.Shared when !s.Confirmed => "waiting to confirm",
        DeviceLightState.Shared => "in step",
        DeviceLightState.Fault when s.Headline == "Update not accepted" => "update not accepted",
        DeviceLightState.Independent => s.Headline.StartsWith("Fixed") ? "fixed colour · own timing" : s.Headline.StartsWith("Companion") ? "companion" : "own timing",
        DeviceLightState.Dark => "off",
        DeviceLightState.Excluded => "excluded",
        DeviceLightState.OutputOff => "output off",
        DeviceLightState.Unavailable => s.Headline.ToLowerInvariant(),
        DeviceLightState.Blocked => "paused",
        DeviceLightState.NotVerified => "not verified",
        _ => "not responding"
    };

    /// <summary>
    /// Draws what each device is asked to show. Output on: the frames actually offered to streamed devices (at their own
    /// rates) and the onboard setting for devices that run their own effect. Output off, or a fixture: a local simulation
    /// through the same capability mapping. Device colours come from the lighting configuration only, never the theme.
    /// Nothing here sends anything to the hardware.
    /// </summary>
    void UpdateVisualizer(double t) => RenderPreview(_viz, _svc.Config.Rgb, t, forceSimulated: false);

    /// <summary>
    /// Draws a preview into <paramref name="viz"/>. The page's own preview: what VANTA sends (or a simulation while output is
    /// off / in a fixture). <paramref name="forceSimulated"/>: a proposed look (for example colours from an image) drawn as a
    /// simulation through the same capability mapping; nothing is sent.
    /// </summary>
    internal void RenderPreview(LightingVisualizer viz, RgbConfig rgbCfg, double t, bool forceSimulated)
    {
        bool scene = EffectInfo.IsScene(rgbCfg.Effect);
        double ownSpeed = scene ? Companion.For(rgbCfg).Speed : rgbCfg.Speed;   // companions breathe at their own (slower) speed
        double rate = 0.12 + ownSpeed * 0.6;
        // fixtures are still images: show breathing near its bright phase rather than wherever the clock happens to be
        if (_filmTime is double ft) t = ft;
        else if (_svc.Fixture != null) t = scene ? FixtureSceneTime(rgbCfg) : 0.41 / rate;
        double ownT = t + 0.25 / rate;   // devices on their own timing are drawn at a different phase (approximate)
        bool preview = forceSimulated || !rgbCfg.Enabled || _svc.Fixture != null;
        viz.IsPreview = preview;
        // the preview never flashes while Windows animations are off (the lights themselves still follow the setting)
        double? drift = forceSimulated || _filmTime != null || _svc.Fixture != null ? null : EffectContext.AdvanceDrift(ref _previewDrift, ref _previewDriftTime, t, rgbCfg.Speed);
        var ctx = EffectContext.From(rgbCfg, t, _svc.Hub.Latest.MaxTempC, lightningAllowed: !Motion.Reduced, drift: drift);
        // simulated colours use the same committed layout and the same renderer as the engine (a drag in progress changes only the drawing)
        var layout = _svc.Rgb.Layout;
        viz.CaseLayout = layout.Case;
        Rgb[] Eval(LedGeometry g, int count)
        {
            var f = new Rgb[count];
            Effects.Render(rgbCfg.Effect, ctx, g, f);
            return f;
        }
        LightingVisualizer.Part Part(LightingVisualizer.Part p, DeviceRole role)
        {
            var v = _views.FirstOrDefault(x => x.Role == role);
            if (v == null) { p.Lit = false; p.State = "not found"; p.Tone = Tone.Warn; return p; }
            p.State = Short(v.Status); p.Tone = StatusPill.ToneFor(v.Status.State);
            p.Lit = v.Status.State is DeviceLightState.Shared or DeviceLightState.Independent || (v.Status.State == DeviceLightState.OutputOff && IncludeFlag(role));
            return p;
        }

        // memory: per detected ENE controller; onboard mode approximates the modules' own effect
        var ram = _svc.Rgb.Sinks.OfType<RamSink>().FirstOrDefault();
        var counts = _svc.Fixture?.RamLeds ?? _svc.Ene?.Dimms.Where(d => d.Supported).Select(d => d.LedCount).ToArray() ?? Array.Empty<int>();
        int ramLeds = counts.Sum();
        Rgb[] rf;
        var onboard = RamSink.OnboardFor(new RgbConfig { RamOnboardEffects = rgbCfg.RamOnboardEffects, Effect = rgbCfg.Effect, ColorA = rgbCfg.ColorA, ColorB = rgbCfg.ColorB, Speed = rgbCfg.Speed, Brightness = rgbCfg.Brightness });
        if (RamSink.StillCompanion(rgbCfg) is Rgb stillRam) rf = Enumerable.Repeat(stillRam, ramLeds).ToArray();
        else if (onboard is { } ob)
        {
            rf = new Rgb[ramLeds];
            double sp = 0.15 + (4 - ob.speed) * 0.12;
            for (int i = 0; i < ramLeds; i++)
                rf[i] = ob.mode switch
                {
                    EneDramController.ModeBreathing => Scale(ob.color, 0.5 - 0.5 * Math.Cos(t * sp * 2 * Math.PI)),
                    EneDramController.ModeSpectrumCycleWave => Scale(Hue(t * sp + i * 0.06), Math.Clamp(rgbCfg.Brightness, 0, 1)),
                    _ => Rgb.Black
                };
        }
        else rf = preview || ram == null ? Eval(ram != null ? layout.Geometry(ram) : layout.GeometryFor(DeviceRole.Ram, ramLeds, counts), ramLeds) : ram.Preview;
        var dimms = new Rgb[counts.Length][];
        for (int d = 0, o = 0; d < counts.Length; o += counts[d], d++)
            dimms[d] = Enumerable.Range(0, counts[d]).Select(i => o + i < rf.Length ? rf[o + i] : default).ToArray();
        viz.Dimms = dimms;
        Part(viz.Ram, DeviceRole.Ram);

        var board = _svc.Rgb.Sinks.OfType<MotherboardSink>().FirstOrDefault();
        viz.Board.Leds = preview || board == null ? Eval(board != null ? layout.Geometry(board) : layout.GeometryFor(DeviceRole.Board, 2), 2) : board.Preview;
        Part(viz.Board, DeviceRole.Board);
        var stripSink = _svc.Rgb.Sinks.OfType<DLedSink>().FirstOrDefault();
        viz.Strip.Leds = !preview && stripSink?.Preview is { Length: > 0 } stripFrame ? stripFrame
            : Eval(stripSink != null ? layout.Geometry(stripSink) : layout.GeometryFor(DeviceRole.Strip, Math.Clamp(rgbCfg.DLedCount, 1, 120)), rgbCfg.DLedCount);
        Part(viz.Strip, DeviceRole.Strip);

        // mouse and keyboard: the mapped onboard setting, unless keyboard live frames run
        var ms = MouseLightingState.From(rgbCfg, ignoreOutputSwitch: true);
        viz.Mouse.Leds = new[] { ms == null || ms.Effect == MouseLightingState.EffectOff ? Rgb.Black
            : ms.Effect == MouseLightingState.EffectBreathingSingleColor ? Effects.Evaluate(EffectKind.Breathing, new EffectContext { Time = ownT, Speed = ownSpeed, Brightness = ctx.Brightness, ColorA = ms.Color ?? ctx.ColorA, ColorB = ctx.ColorB, ThermalC = ctx.ThermalC }, 0)   // its own timing: not in step
            : Scale(ms.Color ?? Rgb.Black, ms.Brightness / 255.0) };
        Part(viz.Mouse, DeviceRole.Mouse);

        if (Al80Kb is Al80Sink al)
        {
            viz.KeyboardKeys = Al80KeyMap.Leds;
            if (al.PerKey)
                // 1.19.0 SignalRGB firmware: a colour per LED at the firmware's key positions (what is streamed), or simulated
                viz.Keyboard.Leds = !preview && al.Preview is { Length: Al80KeyMap.Leds } pk ? pk : Eval(layout.Geometry(al), Al80KeyMap.Leds);
            else
            // AL80: one colour, the brightest of its virtual LEDs (what the sync sends), or the same simulated
            viz.Keyboard.Leds = new[] { !preview && al.Preview is { Length: 1 } p ? p[0] : Al80Sink.LiveColor(Eval(layout.Geometry(al), Al80Sink.VirtualLeds), EffectInfo.IsScene(rgbCfg.Effect)) };
            Part(viz.Keyboard, DeviceRole.Keyboard);
            viz.Redraw();
            return;
        }
        var kbs = Kb;
        viz.KeyboardKeys = ILovbeeKeyMap.Keys.Length;
        var km = KeyboardLightingState.From(rgbCfg, kbs?.SavedSpeed ?? KeyboardLightingState.VendorDefaultSpeed, ignoreOutputSwitch: true);
        bool kbLive = _svc.Config.KeyboardVerified && (_svc.Config.KeyboardLiveVerified || _svc.Config.KeyboardLiveTrial) && rgbCfg.Effect != EffectKind.Stealth;
        if (kbLive)
        {
            // what the engine sends when output is on (live frames), else the same positions simulated
            var kg = layout.GeometryFor(DeviceRole.Keyboard, ILovbeeKeyMap.Keys.Length);
            viz.Keyboard.Leds = !preview && kbs?.Preview is { Length: > 1 } kbFrame && kbFrame.Length == kg.Count ? kbFrame : Eval(kg, kg.Count);
        }
        else viz.Keyboard.Leds = new[] { km == null || km.Sync == KeyboardSync.Off ? Rgb.Black
            : km.Mode == KeyboardLightingState.ModeBreathing ? Effects.Evaluate(EffectKind.Breathing, new EffectContext { Time = ownT, Speed = ownSpeed, Brightness = ctx.Brightness, ColorA = km.Color, ColorB = ctx.ColorB, ThermalC = ctx.ThermalC }, 0)
            : Scale(km.Color, km.Brightness / (double)KeyboardLightingState.MaxBrightness) };
        Part(viz.Keyboard, DeviceRole.Keyboard);
        viz.Redraw();
    }

    /// <summary>
    /// Still fixture time for a scene: a moment with drops, fireflies or ripples in view; for Distant Storm with lightning
    /// on, a moment near a soft flash so the capture shows what it looks like. Deterministic (same image every run).
    /// </summary>
    static double FixtureSceneTime(RgbConfig c)
    {
        if (EffectInfo.UsesLightning(c.Effect) && c.Lightning)
            for (double t = 1; t < 400; t += 0.05)
                if (Atmosphere.LightningLevel(t, out _, out _) > 0.45) return t + 0.4;
        return 7.3;
    }

    /// <summary>
    /// The simulated preview at an explicit time, for the animated fixture capture (--film). Display only: it draws the
    /// same frames the engine would compute at that moment and sends nothing.
    /// </summary>
    internal FrameworkElement RenderPreviewAt(double t)
    {
        _filmTime = t;
        try { UpdateVisualizer(t); }
        finally { _filmTime = null; }
        _viz.UpdateLayout();
        return _viz;
    }
}
