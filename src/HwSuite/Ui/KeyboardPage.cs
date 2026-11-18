using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using HwSuite.Core;
using HwSuite.Hardware.Al80;

namespace HwSuite.Ui;

/// <summary>
/// 1.18: YUNZII AL80 page. Screen (home packets or the live System panel, stored GIFs, pictures, page switches), the stock
/// VIA lighting channels, and the keymap. All device traffic goes through <see cref="Al80Service"/> on its own thread.
/// </summary>
public sealed class KeyboardPage : UserControl
{
    readonly AppServices _svc;
    Al80Config C => _svc.Config.Al80;
    readonly ToggleButton _enabled = new() { Style = B.S("Switch") };
    readonly TextBlock _status = B.Text("", null, 12), _device = B.Text("", "MonoMuted", 11), _error = B.Text("", null, 11), _busy = B.Text("", "MonoMuted", 11);
    readonly RadioButton _modeHome = Seg("Home · clock + gauge", "al80mode"), _modeLive = Seg("Live System panel", "al80mode"), _modeMusic = Seg("Now playing", "al80mode"), _modeOff = Seg("Leave alone", "al80mode");
    readonly ToggleButton _clockSync = new() { Style = B.S("Switch") };
    readonly RadioButton[] _gauge = { Seg("Off", "al80g"), Seg("CPU", "al80g"), Seg("GPU", "al80g"), Seg("RAM", "al80g") };
    readonly Slider _interval = new() { Minimum = 1.2, Maximum = 30, Width = 220, IsSnapToTickEnabled = true, TickFrequency = 0.2 };
    readonly TextBlock _intervalText = B.Text("", "Mono", 12);
    readonly Image _preview = new() { Width = 96 * 2, Height = 160 * 2, Stretch = Stretch.Fill };
    readonly TextBlock _counts = B.Text("", "MonoMuted", 11);
    readonly ToggleButton _slotsFull = new() { Style = B.S("Switch") };
    readonly ToggleButton _deadlock = new() { Style = B.S("Switch") };
    readonly TextBlock _gameStatus = new();
    readonly TextBox _fps = new() { Width = 50, Text = "" };
    // lighting
    readonly RadioButton _zoneKeys = Seg("Keys", "al80z"), _zoneSide = Seg("Second zone", "al80z");
    readonly Slider _bright = new() { Minimum = 0, Maximum = 255, Width = 240 }, _speed = new() { Minimum = 0, Maximum = 255, Width = 240 },
        _hue = new() { Minimum = 0, Maximum = 255, Width = 240 }, _sat = new() { Minimum = 0, Maximum = 255, Width = 240 };
    readonly TextBlock _effect = B.Text("--", "Mono", 14);
    readonly TextBlock _syncNote = B.Text("", null, 12);
    readonly Border _swatch = new() { Width = 28, Height = 28, CornerRadius = new CornerRadius(6), Margin = new Thickness(10, 0, 0, 0) };
    bool _loading;
    readonly DispatcherTimer _lightDebounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly HashSet<byte> _lightDirty = new();
    // keymap
    readonly RadioButton[] _layers = Enumerable.Range(0, 4).Select(i => Seg($"Layer {i}", "al80l")).ToArray();
    readonly UniformGrid _keys = new() { Rows = 6, Columns = 15 };
    readonly TextBox _keycode = new() { Width = 120 };
    readonly TextBlock _keySel = B.Text("pick a key", "MonoMuted", 11);
    ushort[,]? _map;
    (int r, int c)? _sel;

    static RadioButton Seg(string text, string group) => new() { Content = text, Style = B.S("Segment"), GroupName = group };
    static Border Group(params RadioButton[] rb) { var p = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var r in rb) p.Children.Add(r); return new Border { Style = B.S("SegmentGroup"), Child = p, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) }; }
    static Border Panel(string title, UIElement content, string? subtitle = null) => B.Section(title, content, subtitle, null, new Thickness(0, 0, 0, 12));
    static Button Btn(string text, Action click, bool primary = false, string? tip = null)
    {
        var b = new Button { Content = text, Style = B.S(primary ? "PrimaryBtn" : "GhostBtn"), Margin = new Thickness(0, 0, 6, 6), ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }
    static WrapPanel Wrap(params UIElement[] e) { var w = new WrapPanel(); foreach (var x in e) w.Children.Add(x); return w; }
    static Grid LabeledRow(string label, UIElement el, UIElement? right = null)
    {
        var parts = new List<(UIElement, GridLength)> { (B.Text(label, "Muted"), B.Px(110)), (el, B.Auto) };
        if (right != null) { if (right is FrameworkElement fe) fe.Margin = new Thickness(10, 0, 0, 0); parts.Add((right, B.Auto)); }
        var g = B.Row(parts.ToArray());
        g.Margin = new Thickness(0, 4, 0, 4);
        foreach (UIElement c in g.Children) if (c is FrameworkElement f) f.VerticalAlignment = VerticalAlignment.Center;
        return g;
    }
    static Grid SwitchRow(string title, string tip, ToggleButton sw)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 8), ToolTip = tip, Background = Brushes.Transparent };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ts = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        ts.Children.Add(B.Text(title, null));
        ts.Children.Add(B.Para(tip, "Label", new Thickness(0, 2, 0, 0)));
        g.Children.Add(ts);
        sw.VerticalAlignment = VerticalAlignment.Top;
        sw.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, title);
        Grid.SetColumn(sw, 1); g.Children.Add(sw);
        return g;
    }

    public KeyboardPage(AppServices svc)
    {
        _svc = svc;
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.NearestNeighbor);
        B.Fg(_error, "WarnBrush");
        foreach (var t in new[] { _status, _device, _error, _busy, _counts, _gameStatus }) { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; t.TextAlignment = TextAlignment.Left; }

        var body = new Grid { MaxWidth = 1880 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var right = new StackPanel();

        // ---- connection
        var conn = new StackPanel();
        conn.Children.Add(SwitchRow("Control the AL80", "VANTA opens the keyboard's screen/VIA interface (USB cable only). Close VIA and the YUNZII web app first: only one program can use it.", _enabled));
        conn.Children.Add(_status); conn.Children.Add(_device); conn.Children.Add(_busy); conn.Children.Add(_error);
        conn.Children.Add(B.Para("Screen completely black, even after a power cut? Press Fn+Del: it switches the screen on and off, and the keyboard remembers it.", "Label", new Thickness(0, 6, 0, 0)));
        left.Children.Add(Panel("YUNZII AL80", conn));

        // ---- screen
        var scr = new StackPanel();
        scr.Children.Add(Group(_modeHome, _modeLive, _modeMusic, _modeOff));
        var homeBox = new StackPanel();
        homeBox.Children.Add(SwitchRow("Keep the clock set", "Sends the time and date on connect and every 10 minutes. The keyboard draws the 12-hour clock itself.", _clockSync));
        homeBox.Children.Add(LabeledRow("Battery icon", Group(_gauge)));
        homeBox.Children.Add(B.Para("The battery icon shows a few bars, so it is a rough load meter. Only small status packets are sent; no picture is stored.", "Label"));
        scr.Children.Add(homeBox);
        var liveBox = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        liveBox.Children.Add(LabeledRow("Every", _interval, _intervalText));
        var pv = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left, Child = _preview, Margin = new Thickness(0, 6, 0, 6) };
        B.Bd(pv, "BorderBrush"); pv.Background = Brushes.Black;
        liveBox.Children.Add(pv);
        liveBox.Children.Add(B.Para("Now playing: the Spotify song's album cover with its title and artist, read from Windows' media controls. One picture per song change (or play/pause); when Spotify is closed the keyboard's home screen comes back. Per-key lighting pauses for about a second while each picture is sent.", "Label", new Thickness(0, 0, 0, 4)));
        liveBox.Children.Add(B.Para("A picture takes about 1.2 s to send, so the panel changes at most about once a second. Readings come from VANTA's sensors. Smooth motion belongs in a stored GIF.", "Label"));
        scr.Children.Add(liveBox);
        scr.Children.Add(_counts);
        left.Children.Add(Panel("Screen", scr));

        // ---- games (1.22)
        var gm = new StackPanel();
        gm.Children.Add(SwitchRow("Deadlock profile", "While deadlock.exe runs, the lights go to full brightness and the AL80 screen shows your rank, win rate and recent matches. Everything goes back when the game closes. The stats come from the Deadlock API (the data Statlocker is built on), so they can differ a little from statlocker.gg, and are fetched only while the game runs. The screen part needs 'Control the AL80' on and the flash slots confirmed full.", _deadlock));
        gm.Children.Add(_gameStatus);
        left.Children.Add(Panel("Games", gm));

        // ---- storage
        var st = new StackPanel();
        st.Children.Add(B.Para("Storage test on this keyboard (12 Nov 2026): after the pictures were cleared, 20 numbered cards were uploaded and the power was cut. Cards 1-8 came back; 9-20 did not. So only the first 8 pictures after a clear go to flash, and later ones live in RAM until power-off.", "Muted", new Thickness(0, 0, 0, 6)));
        st.Children.Add(SwitchRow("The 8 flash slots are full", "Live pictures are sent only while this is on, so they cannot wear the flash. Turn it off if you delete pictures with the YUNZII software. Deleting frees a slot, and the next 8 uploads would be stored again. VANTA never deletes pictures.", _slotsFull));
        left.Children.Add(Panel("Flash wear", st));

        // ---- animations and pictures
        var an = new StackPanel();
        an.Children.Add(B.Para("Stored GIFs play on the keyboard itself, smoothly (up to 60 fps). Each upload is a one-time flash write.", "Muted", new Thickness(0, 0, 0, 8)));
        an.Children.Add(Wrap(
            Btn("GIF to home strip…", () => PickGif(Al80Protocol.HomeStrip), true, "96 x 64, up to 42 frames, plays under the clock"),
            Btn("GIF to GIF page…", () => PickGif(Al80Protocol.GifPage), false, "96 x 160, up to 160 frames"),
            Btn("Startup animation…", () => PickGif(Al80Protocol.Boot), false, "96 x 160, up to 64 frames, plays at power-on")));
        an.Children.Add(LabeledRow("Frame rate", _fps, B.Text("fps (blank: from the GIF)", "Label")));
        an.Children.Add(Wrap(
            Btn("Picture…", PickPicture, false, "96 x 160 still. With the flash slots full it stays until the keyboard loses power."),
            Btn("Show home", () => View(Al80Protocol.View.Home)),
            Btn("Show GIF page", () => View(Al80Protocol.View.Gif)),
            Btn("Next picture", () => View(Al80Protocol.View.NextPicture))));
        right.Children.Add(Panel("Animations and pictures", an));

        // ---- lighting
        var li = new StackPanel();
        B.Fg(_syncNote, "AccentBrush"); _syncNote.TextWrapping = TextWrapping.Wrap; _syncNote.TextTrimming = TextTrimming.None; _syncNote.TextAlignment = TextAlignment.Left; _syncNote.Margin = new Thickness(0, 0, 0, 6);
        li.Children.Add(_syncNote);
        li.Children.Add(Group(_zoneKeys, _zoneSide));
        var minus = Btn("−", () => StepEffect(-1)); var plus = Btn("+", () => StepEffect(1));
        minus.MinWidth = plus.MinWidth = 34;
        li.Children.Add(LabeledRow("Effect", Wrap(minus, _effect, plus)));
        _effect.Margin = new Thickness(6, 4, 12, 0); _effect.MinWidth = 30;
        li.Children.Add(LabeledRow("Brightness", _bright));
        li.Children.Add(LabeledRow("Speed", _speed));
        li.Children.Add(LabeledRow("Hue", _hue, _swatch));
        li.Children.Add(LabeledRow("Saturation", _sat));
        li.Children.Add(Wrap(Btn("Save to keyboard", SaveLighting, true, "Writes the current zone's values to the keyboard's memory (VIA save), so they survive a power cut."),
                             Btn("Reload", () => _svc.Al80?.Enqueue("read lighting", d => { _svc.Al80!.ReadLighting(d); Dispatcher.BeginInvoke(LoadLighting); }))));
        li.Children.Add(B.Para("Changes apply live and are not saved until you press Save. The stock firmware numbers its effects; no name list is published, so step through them and watch the keys.", "Label"));
        right.Children.Add(Panel("Lighting", li));

        // ---- keymap
        var km = new StackPanel();
        km.Children.Add(Group(_layers));
        _keys.Margin = new Thickness(0, 4, 0, 8);
        km.Children.Add(_keys);
        km.Children.Add(Wrap(_keySel, new TextBlock { Width = 12 }, _keycode, new TextBlock { Width = 6 }, Btn("Set key", SetKey, true, "Writes this key to the keyboard's memory (it persists, like a VIA remap)."), Btn("Read layer", ReadLayer)));
        km.Children.Add(B.Para("Matrix 6 x 15; empty cells are gaps in the board. Keycodes are QMK/VIA: a name (A, ESC, F1, LCTL…) or hex (0x0029).", "Label"));
        right.Children.Add(Panel("Keymap", km));

        body.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = left, Focusable = false });
        var rs = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = right, Focusable = false };
        Grid.SetColumn(rs, 1); body.Children.Add(rs);
        Content = body;

        LoadSettings();
        _enabled.Click += (_, _) => Set(() => C.Enabled = _enabled.IsChecked == true, "AL80 control " + (_enabled.IsChecked == true ? "on" : "off"));
        _modeHome.Checked += (_, _) => Set(() => C.Mode = "home", "AL80 screen: home");
        _modeLive.Checked += (_, _) => Set(() => C.Mode = "live", "AL80 screen: live panel");
        _modeMusic.Checked += (_, _) => Set(() => C.Mode = "music", "AL80 screen: now playing (Spotify)");
        _modeOff.Checked += (_, _) => Set(() => C.Mode = "off", "AL80 screen: left alone");
        _clockSync.Click += (_, _) => Set(() => C.ClockSync = _clockSync.IsChecked == true, null);
        string[] gauges = { "off", "cpu", "gpu", "ram" };
        for (int i = 0; i < 4; i++) { var g = gauges[i]; _gauge[i].Checked += (_, _) => Set(() => C.Gauge = g, null); }
        _interval.ValueChanged += (_, _) => { if (!_loading) Set(() => C.LiveIntervalSec = _interval.Value, null); _intervalText.Text = $"{_interval.Value:0.0} s"; };
        _slotsFull.Click += (_, _) => Set(() => C.FlashSlotsFilled = _slotsFull.IsChecked == true, "AL80 flash slots full: " + (_slotsFull.IsChecked == true ? "yes" : "no"));
        _deadlock.Click += (_, _) => Set(() => _svc.Config.Games.DeadlockEnabled = _deadlock.IsChecked == true, "Deadlock profile " + (_deadlock.IsChecked == true ? "on" : "off"));
        if (_svc.Games != null) _svc.Games.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });

        _zoneKeys.IsChecked = true;
        _zoneKeys.Checked += (_, _) => LoadLighting(); _zoneSide.Checked += (_, _) => LoadLighting();
        _bright.ValueChanged += (_, _) => LightChanged(Al80Protocol.LightBrightness);
        _speed.ValueChanged += (_, _) => LightChanged(Al80Protocol.LightSpeed);
        _hue.ValueChanged += (_, _) => LightChanged(Al80Protocol.LightColor);
        _sat.ValueChanged += (_, _) => LightChanged(Al80Protocol.LightColor);
        _lightDebounce.Tick += (_, _) => { _lightDebounce.Stop(); FlushLighting(); };

        _layers[0].IsChecked = true;
        foreach (var l in _layers) l.Checked += (_, _) => ReadLayer();
        BuildKeyGrid();
        if (_svc.Al80 != null) _svc.Al80.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });
    }

    void Set(Action change, string? log)
    {
        if (_loading) return;
        lock (_svc.Config.Sync) change();
        ConfigStore.Save(_svc.Config);
        if (log != null) Log.Info(log);
        _svc.Al80?.Invalidate();
        Refresh();
    }

    void LoadSettings()
    {
        _loading = true;
        _enabled.IsChecked = C.Enabled;
        (C.Mode switch { "live" => _modeLive, "music" => _modeMusic, "off" => _modeOff, _ => _modeHome }).IsChecked = true;
        _clockSync.IsChecked = C.ClockSync;
        _gauge[Math.Max(0, Array.IndexOf(new[] { "off", "cpu", "gpu", "ram" }, C.Gauge))].IsChecked = true;
        _interval.Value = Math.Clamp(C.LiveIntervalSec, 1.2, 30);
        _intervalText.Text = $"{_interval.Value:0.0} s";
        _slotsFull.IsChecked = C.FlashSlotsFilled;
        _deadlock.IsChecked = _svc.Config.Games.DeadlockEnabled;
        _loading = false;
    }

    public void Refresh()
    {
        _gameStatus.Text = _svc.Games?.Status ?? "";
        var a = _svc.Al80;
        if (a == null) { _status.Text = "Not available in this mode."; return; }
        _status.Text = a.Status;
        _device.Text = a.Connected ? $"{a.DeviceName} · VIA protocol {a.ViaProtocol} · {a.Layers} layers" : "";
        _busy.Text = a.Busy.Length > 0 ? "working: " + a.Busy : "";
        _error.Text = a.LastError ?? "";
        if (a.Preview != null) _preview.Source = a.Preview;
        _counts.Text = $"pictures this session {a.SessionPictures}" + (a.LastUploadTime is DateTime t ? $" · last {t:HH:mm:ss} in {a.LastUploadSeconds:0.00} s" : "") +
                       $" · all time {C.LifetimePictures} pictures, {C.LifetimeGifUploads} GIF writes";
        _syncNote.Text = a.LightSyncing ? "Following VANTA's lighting (" + a.LightStatus.Replace("following VANTA · ", "") + "). Changes here are overridden; exclude the keyboard on the Lighting page to use its own effects." : "";
        if (!_effectLoaded && a.Lighting.Count > 0) LoadLighting();
    }

    bool _effectLoaded;
    byte Zone => _zoneSide.IsChecked == true ? Al80Protocol.ChannelSide : Al80Protocol.ChannelKeys;

    void LoadLighting()
    {
        var a = _svc.Al80; if (a == null) return;
        _loading = true;
        if (a.Lighting.TryGetValue((Zone, 1), out var br)) _bright.Value = br[0];
        if (a.Lighting.TryGetValue((Zone, 2), out var ef)) { _effect.Text = ef[0].ToString(); _effectLoaded = true; }
        if (a.Lighting.TryGetValue((Zone, 3), out var sp)) _speed.Value = sp[0];
        if (a.Lighting.TryGetValue((Zone, 4), out var co)) { _hue.Value = co[0]; _sat.Value = co[1]; }
        _loading = false;
        UpdateSwatch();
    }

    void UpdateSwatch()
    {
        double h = _hue.Value / 255 * 360, s = _sat.Value / 255;
        double c = s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = 1 - c;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        _swatch.Background = new SolidColorBrush(Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255)));
    }

    void LightChanged(byte value)
    {
        if (value == Al80Protocol.LightColor) UpdateSwatch();
        if (_loading) return;
        _lightDirty.Add(value);
        _lightDebounce.Stop(); _lightDebounce.Start();
    }

    /// <summary>1.19.0: while per-key colours stream (SignalRGB mode) VIA lighting writes are not sent at all.</summary>
    bool RefuseWhileStreaming(Al80Service a)
    {
        if (!a.PerKeyStreaming) return false;
        _lightDirty.Clear();
        _syncNote.Text = "VANTA is streaming per-key colours (SignalRGB mode), so the keyboard's own effect settings are not sent. Exclude the keyboard on the Lighting page first.";
        return true;
    }

    void FlushLighting()
    {
        var a = _svc.Al80; if (a == null || _lightDirty.Count == 0) return;
        if (RefuseWhileStreaming(a)) return;
        byte ch = Zone;
        var sets = _lightDirty.Select(v => (v, v switch
        {
            Al80Protocol.LightBrightness => new[] { (byte)_bright.Value },
            Al80Protocol.LightSpeed => new[] { (byte)_speed.Value },
            _ => new[] { (byte)_hue.Value, (byte)_sat.Value },
        })).ToList();
        _lightDirty.Clear();
        a.Enqueue("lighting", d =>
        {
            foreach (var (v, data) in sets)
            {
                d.Via(Al80Protocol.LightSet(ch, v, data));
                var back = d.Via(Al80Protocol.LightGet(ch, v));
                a.Lighting[(ch, v)] = new[] { back[3], back[4] };
            }
        });
    }

    void StepEffect(int delta)
    {
        var a = _svc.Al80; if (a == null) return;
        if (RefuseWhileStreaming(a)) return;
        byte ch = Zone;
        int cur = a.Lighting.TryGetValue((ch, 2), out var e) ? e[0] : 0;
        int next = Math.Clamp(cur + delta, 0, 255);
        _effect.Text = next.ToString();
        a.Enqueue($"effect {next}", d =>
        {
            d.Via(Al80Protocol.LightSet(ch, Al80Protocol.LightEffect, (byte)next));
            var back = d.Via(Al80Protocol.LightGet(ch, Al80Protocol.LightEffect));
            a.Lighting[(ch, 2)] = new[] { back[3], back[4] };
            Dispatcher.BeginInvoke(() => _effect.Text = back[3].ToString());
        });
    }

    void SaveLighting()
    {
        byte ch = Zone;
        var a = _svc.Al80;
        if (a == null) return;
        if (a.LightSyncing)
        {
            MessageBox.Show(Window.GetWindow(this), "The keyboard is following VANTA's lighting right now, so Save would store VANTA's current colour as the keyboard's own setting.\n\nTurn off \"Include in VANTA lighting\" for the keyboard on the Lighting page first, then set and save its own effect here.",
                "VANTA · AL80", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        a.Enqueue("save lighting", d => { d.Via(Al80Protocol.LightSave(ch)); a.MarkSaved(ch); });
        Log.Info($"AL80 lighting channel {ch} saved");
    }

    void View(Al80Protocol.View v)
    {
        _svc.Al80?.Enqueue("show " + v, d => d.Send(Al80Protocol.ViewStream(v)));
        _svc.Al80?.Invalidate();
    }

    int? FpsOverride => int.TryParse(_fps.Text, out var f) ? Math.Clamp(f, 1, 60) : null;

    void PickGif(Al80Protocol.GifTarget target)
    {
        var a = _svc.Al80; if (a == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "GIF animation|*.gif", Title = "Animation for the " + target.Name };
        if (dlg.ShowDialog() != true) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Store this animation in the {target.Name}?\n\nIt is a flash write (one per upload) and takes about 2 seconds per frame (the keyboard pauses to write flash every 16 frames).",
            "VANTA · AL80", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        string path = dlg.FileName; int? fps = FpsOverride;
        // the live panel would cover the animation at once: home strip -> Home mode, GIF page -> leave the screen alone
        if (C.Mode is "live" or "music" && target != Al80Protocol.Boot) (target == Al80Protocol.HomeStrip ? _modeHome : _modeOff).IsChecked = true;
        a.Enqueue($"GIF to {target.Name}", d =>
        {
            var (frames, rate) = Al80Imaging.LoadGif(path, target, fps);
            a.Busy = $"GIF to {target.Name}: {frames.Count} frames at {rate} fps";
            d.Send(Al80Protocol.GifStream(frames, target, rate), (n, tot) => a.Busy = $"GIF to {target.Name}: {100 * n / tot}%", allowBetween: false);
            if (target != Al80Protocol.Boot) d.Send(Al80Protocol.ViewStream(target == Al80Protocol.GifPage ? Al80Protocol.View.Gif : Al80Protocol.View.Home));
            lock (_svc.Config.Sync) C.LifetimeGifUploads++;
            ConfigStore.Save(_svc.Config);
            Log.Info($"AL80: stored {frames.Count}-frame GIF ({rate} fps) in the {target.Name} from {path}");
        });
    }

    void PickPicture()
    {
        var a = _svc.Al80; if (a == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Title = "Picture for the AL80 screen (96 x 160)" };
        if (dlg.ShowDialog() != true) return;
        if (!C.FlashSlotsFilled && MessageBox.Show(Window.GetWindow(this), "The flash slots are not marked full, so this picture may be written to flash. Send it?", "VANTA · AL80", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        string path = dlg.FileName;
        if (C.Mode is "live" or "music") { _modeOff.IsChecked = true; }   // otherwise the live panel would replace it at once
        a.Enqueue("picture", d => a.UploadPicture(d, Al80Imaging.ToRgb565(Al80Imaging.Cover(Al80Imaging.Load(path), Al80Protocol.Width, Al80Protocol.Height))));
    }

    // ---------------------------------------------------------------- keymap
    static readonly HashSet<(int, int)> Gaps = new() { (5, 3), (5, 4), (5, 5), (5, 7), (5, 8), (5, 9) };

    void BuildKeyGrid()
    {
        _keys.Children.Clear();
        for (int r = 0; r < 6; r++)
            for (int c = 0; c < 15; c++)
            {
                var (rr, cc) = (r, c);
                var t = new TextBlock { FontSize = 10, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                t.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                B.Fg(t, "TextSecondaryBrush");
                var cell = new Border { Child = t, Height = 30, Margin = new Thickness(1.5), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand };
                B.Bd(cell, "BorderBrush");
                if (Gaps.Contains((r, c))) { cell.Opacity = 0.25; cell.Cursor = null; }
                else cell.MouseLeftButtonUp += (_, _) => SelectKey(rr, cc);
                t.Text = _map is { } m ? KeyName(m[r, c]) : "";
                cell.ToolTip = $"row {r}, col {c}" + (_map is { } mm ? $" · 0x{mm[r, c]:X4}" : "");
                if (_sel == (r, c)) B.Bd(cell, "AccentBrush");
                _keys.Children.Add(cell);
            }
    }

    int Layer => Math.Max(0, Array.FindIndex(_layers, l => l.IsChecked == true));

    void SelectKey(int r, int c)
    {
        _sel = (r, c);
        _keySel.Text = $"layer {Layer}, row {r}, col {c}";
        if (_map != null) _keycode.Text = KeyName(_map[r, c]);
        BuildKeyGrid();
    }

    void ReadLayer()
    {
        var a = _svc.Al80; if (a == null) return;
        int layer = Layer;
        a.Enqueue($"read layer {layer}", d =>
        {
            var map = new ushort[6, 15];
            // keymap buffer: 6 x 15 x 2 bytes per layer, 28 bytes per request
            int total = 6 * 15 * 2, start = layer * total;
            var buf = new byte[total];
            for (int off = 0; off < total; off += 28)
            {
                int n = Math.Min(28, total - off), at = start + off;
                var r = d.Via(Al80Protocol.Via(0x12, (byte)(at >> 8), (byte)at, (byte)n));
                Array.Copy(r, 4, buf, off, n);
            }
            for (int i = 0; i < 90; i++) map[i / 15, i % 15] = (ushort)(buf[2 * i] << 8 | buf[2 * i + 1]);
            Dispatcher.BeginInvoke(() => { _map = map; BuildKeyGrid(); if (_sel is (int sr, int sc)) _keycode.Text = KeyName(map[sr, sc]); });
        });
    }

    void SetKey()
    {
        var a = _svc.Al80; if (a == null || _sel is not (int r, int c)) return;
        if (ParseKey(_keycode.Text) is not ushort code) { _keySel.Text = "unknown keycode: " + _keycode.Text; return; }
        int layer = Layer;
        string old = _map != null ? KeyName(_map[r, c]) : "?";
        if (MessageBox.Show(Window.GetWindow(this), $"Change layer {layer}, row {r}, col {c} from {old} to {KeyName(code)} (0x{code:X4})?\n\nThis is saved on the keyboard.", "VANTA · AL80 keymap", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        a.Enqueue($"set key L{layer} {r},{c}", d =>
        {
            d.Via(Al80Protocol.KeycodeSet((byte)layer, (byte)r, (byte)c, code));
            var back = d.Via(Al80Protocol.KeycodeGet((byte)layer, (byte)r, (byte)c));
            ushort got = (ushort)(back[4] << 8 | back[5]);
            Log.Info($"AL80 keymap: layer {layer} ({r},{c}) {old} -> {KeyName(got)} (0x{got:X4})");
            Dispatcher.BeginInvoke(() => { if (_map != null) _map[r, c] = got; BuildKeyGrid(); });
        });
    }

    static readonly Dictionary<ushort, string> Names = BuildNames();
    static Dictionary<ushort, string> BuildNames()
    {
        var d = new Dictionary<ushort, string> { [0] = "", [1] = "▽" };
        for (int i = 0; i < 26; i++) d[(ushort)(0x04 + i)] = ((char)('A' + i)).ToString();
        for (int i = 0; i < 9; i++) d[(ushort)(0x1E + i)] = (i + 1).ToString();
        d[0x27] = "0";
        string[] misc = { "ENT", "ESC", "BSPC", "TAB", "SPC", "-", "=", "[", "]", "\\", "#", ";", "'", "`", ",", ".", "/", "CAPS" };
        for (int i = 0; i < misc.Length; i++) d[(ushort)(0x28 + i)] = misc[i];
        for (int i = 0; i < 12; i++) d[(ushort)(0x3A + i)] = "F" + (i + 1);
        string[] nav = { "PSCR", "SCRL", "PAUS", "INS", "HOME", "PGUP", "DEL", "END", "PGDN", "RGHT", "LEFT", "DOWN", "UP" };
        for (int i = 0; i < nav.Length; i++) d[(ushort)(0x46 + i)] = nav[i];
        d[0x65] = "APP";
        string[] mods = { "LCTL", "LSFT", "LALT", "LGUI", "RCTL", "RSFT", "RALT", "RGUI" };
        for (int i = 0; i < 8; i++) d[(ushort)(0xE0 + i)] = mods[i];
        d[0xA8] = "MUTE"; d[0xA9] = "VOLU"; d[0xAA] = "VOLD"; d[0xAB] = "MNXT"; d[0xAC] = "MPRV"; d[0xAE] = "MPLY";
        return d;
    }

    public static string KeyName(ushort code)
    {
        if (Names.TryGetValue(code, out var n)) return n;
        if (code is >= 0x5220 and <= 0x523F) return "MO(" + (code & 0x1F) + ")";   // QMK MO(layer), VIA protocol 12
        return $"0x{code:X4}";
    }

    public static ushort? ParseKey(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ushort.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out var h)) return h;
        var hit = Names.FirstOrDefault(kv => kv.Value.Length > 0 && string.Equals(kv.Value, s, StringComparison.OrdinalIgnoreCase));
        if (hit.Value != null) return hit.Key;
        if (s.StartsWith("MO(", StringComparison.OrdinalIgnoreCase) && s.EndsWith(")") && int.TryParse(s[3..^1], out var l) && l is >= 0 and < 16) return (ushort)(0x5220 + l);
        return null;
    }
}
