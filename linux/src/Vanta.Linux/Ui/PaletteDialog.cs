// PORTED from src\HwSuite\Ui\PaletteDialog.cs (Windows 1.20.0): WPF -> Avalonia 11 (mechanical pass by linux/tools/wpf2avalonia.py, then hand fixes).
using HwSuite.Core;
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

/// <summary>
/// "Colours from image" (1.14): suggests 4-6 colours from a still image and lets the owner pick the current effect's
/// primary and/or secondary colour from them, with a simulated preview. Browsing sends nothing to any device and changes
/// no setting; only "Apply colours" does (through <see cref="AppServices.ApplyColours"/>: the colour fields only, part of
/// the Revert session). The image is decoded small on a background thread and is not stored anywhere.
/// </summary>
public sealed class PaletteDialog : DialogWindow
{
    readonly AppServices _svc;
    readonly Action<LightingVisualizer, RgbConfig, double> _render;
    readonly Func<string?> _pickFile;
    readonly RgbConfig _current;
    readonly EffectKind _effect;
    readonly LightingVisualizer _viz = new() { Height = 170 };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / 15) };
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    readonly TextBlock _source = B.Text("", "Label");
    readonly TextBlock _status = B.Para("", "Muted", new Thickness(0, 6, 0, 0));
    readonly Image _thumb = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
    readonly WrapPanel _swatches = new();
    readonly StackPanel _roles = new() { Margin = new Thickness(0, 14, 0, 0) };
    readonly Button _apply = new() { Content = "Apply colours", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(16, 4, 16, 4), IsDefault = true, IsEnabled = false };
    readonly Control _resultArea;
    CancellationTokenSource? _cts;

    public PaletteResult? Result { get; private set; }
    /// <summary>Chosen colours (null: keep the current one).</summary>
    public int? ChosenPrimary { get; private set; }
    public int? ChosenSecondary { get; private set; }
    public string? FileName { get; private set; }
    public string? Error { get; private set; }

    public PaletteDialog(AppServices svc, Action<LightingVisualizer, RgbConfig, double> render, Func<string?> pickFile)
    {
        _svc = svc; _render = render; _pickFile = pickFile;
        lock (svc.Config.Sync) _current = svc.Config.Rgb.WithEffect(LightingLooks.EffectiveEffect(svc.Config.Rgb));
        _effect = _current.Effect;
        Title = "Colours from image";
        SizeToContent = SizeToContent.Height; Width = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this.SetResourceReference(BackgroundProperty, "CanvasBrush"); this.SetResourceReference(ForegroundProperty, "TextBrush");

        var root = new StackPanel { Margin = new Thickness(24, 18, 24, 18) };
        root.Children.Add(B.Text("Colours from image", "CardTitle"));
        _source.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(_source);
        root.Children.Add(_status);

        var top = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var thumbFrame = new Border { Height = 140, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = _thumb, ClipToBounds = true };
        B.Bd(thumbFrame, "HairlineBrush"); B.Bg(thumbFrame, "InputBrush");
        top.Children.Add(thumbFrame);
        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        right.Children.Add(B.Text("Suggested colours", "Label"));
        _swatches.Margin = new Thickness(0, 8, 0, 0);
        right.Children.Add(_swatches);
        Grid.SetColumn(right, 1); top.Children.Add(right);

        var area = new StackPanel();
        area.Children.Add(top);
        area.Children.Add(_roles);
        var vizCard = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 8), Margin = new Thickness(0, 14, 0, 0) };
        B.Bd(vizCard, "HairlineBrush");
        var vizStack = new StackPanel();
        var pill = new StatusPill { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        pill.Set("Simulated preview · nothing is sent", Tone.Neutral);
        vizStack.Children.Add(pill);
        _viz.IsPreview = true;
        _viz.Arrangement = svc.Config.Arrangement.Clone();
        vizStack.Children.Add(_viz);
        vizCard.Child = vizStack;
        area.Children.Add(vizCard);
        area.Children.Add(B.Para("Screens and LEDs show colour differently: muted tones often look brighter or cooler on the LEDs, so treat these as starting points. "
            + "Tones too dark for the LEDs to show are lifted (marked ↑); the image itself is not saved.", "Muted", new Thickness(0, 10, 0, 0)));
        _resultArea = area;
        root.Children.Add(area);

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        var another = new Button { Content = "Choose another image…", Classes = { "GhostBtn" } };
        another.Click += (_, _) => { if (_pickFile() is string p) Load(p); };
        DockPanel.SetDock(another, Dock.Left); buttons.Children.Add(another);
        _apply.Classes.Add("PrimaryBtn");
        _apply.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        DockPanel.SetDock(_apply, Dock.Right); DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(_apply); buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        var frame = new Border { BorderThickness = new Thickness(1), Child = root };
        B.Bd(frame, "HairlineBrush"); B.Bg(frame, "CanvasBrush");
        Content = frame;

        _tick.Tick += (_, _) => DrawPreview();
        Loaded += (_, _) => { DrawPreview(); if (!Motion.Reduced && _svc.Fixture == null) _tick.Start(); };
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && !IsVisible) _tick.Stop(); };
        Closed += (_, _) => { _tick.Stop(); _cts?.Cancel(); };
        ShowEmpty();
    }

    void ShowEmpty()
    {
        Result = null; ChosenPrimary = ChosenSecondary = null; _apply.IsEnabled = false;
        _source.Text = "No image chosen";
        _resultArea.IsVisible = false;
        _status.Text = "Choose a still image (for example your wallpaper).";
    }

    /// <summary>Read an image file in the background; the dialog stays responsive and shows progress or a clear error.</summary>
    public void Load(string path)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        FileName = System.IO.Path.GetFileName(path);
        _source.Text = FileName;
        _status.Text = "Reading the image…";
        _resultArea.IsVisible = false; _apply.IsEnabled = false;
        Task.Run(() => ImagePalette.FromFile(path, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested) return;
            if (t.Exception?.GetBaseException() is Exception ex) ShowError(ex is PaletteException ? ex.Message : "The image could not be read: " + ex.Message);
            else if (t.Status == TaskStatus.RanToCompletion) ShowResult(t.Result, FileName);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void ShowError(string message, string? fileName = null)
    {
        if (fileName != null) { FileName = fileName; _source.Text = fileName; }
        Error = message; Result = null; ChosenPrimary = ChosenSecondary = null; _apply.IsEnabled = false;
        _resultArea.IsVisible = false;
        _status.Text = message + " Nothing was changed.";
        B.Fg(_status, "WarnBrush");
        Log.Info($"colours from image: {FileName}: {message}");
    }

    public void ShowResult(PaletteResult r, string? fileName)
    {
        Error = null; Result = r; FileName = fileName;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _source.Text = $"{fileName} · {r.ImageWidth} × {r.ImageHeight}" + (r.Thumbnail is { } th ? $" · analysed at {th.PixelWidth} × {th.PixelHeight}" : "");
        _status.Text = (r.Note != null ? r.Note + " " : "") + "Pick a colour for each role below; nothing changes until you apply.";
        _thumb.Source = r.Thumbnail?.ToAvalonia();
        _swatches.Children.Clear();
        foreach (var s in r.Swatches)
        {
            var chip = Chip(s.Color, 46, 30);
            ToolTip.SetTip(chip, $"{s.Hex} · about {s.Share * 100:0}% of the image" + (s.Lifted ? $" · lifted from #{s.Source:X6} so the LEDs can show it" : ""));
            var st = new StackPanel { Margin = new Thickness(0, 0, 10, 6) };
            st.Children.Add(chip);
            st.Children.Add(B.Text(s.Hex + (s.Lifted ? " ↑" : ""), "MonoMuted", null, null, new Thickness(0, 3, 0, 0)));
            _swatches.Children.Add(st);
        }
        // the roles the current effect uses, with their real names (drops, background, ...)
        _roles.Children.Clear();
        var (pr, sr) = EffectInfo.ColourRoles(_effect);
        ChosenPrimary = r.SuggestedPrimary;
        ChosenSecondary = EffectInfo.UsesSecondary(_effect) ? r.SuggestedSecondary : null;
        _roles.Children.Add(RoleRow(pr.Length > 0 ? $"Primary · {pr}" : "Primary", _current.ColorA, r, ChosenPrimary, v => ChosenPrimary = v));
        if (EffectInfo.UsesSecondary(_effect))
            _roles.Children.Add(RoleRow(sr.Length > 0 ? $"Secondary · {sr}" : "Secondary", _current.ColorB, r, ChosenSecondary, v => ChosenSecondary = v));
        else
            _roles.Children.Add(B.Para($"{EffectInfo.Label(_effect)} uses one colour; the secondary colour is left as it is.", "Muted", new Thickness(0, 6, 0, 0)));
        if (_effect is EffectKind.ThermalReactive or EffectKind.Stealth)
            _roles.Children.Add(B.Para($"{EffectInfo.Label(_effect)} does not show these colours (only devices that fall back to a fixed colour do); they are kept for other effects.", "Muted", new Thickness(0, 6, 0, 0)));
        _resultArea.IsVisible = true;
        UpdateApply();
        DrawPreview();
        Log.Info($"colours from image: {fileName}: {r.Swatches.Count} swatches ({string.Join(" ", r.Swatches.Select(x => x.Hex))}); nothing applied yet");
    }

    Control RoleRow(string label, int current, PaletteResult r, int? selected, Action<int?> set)
    {
        var row = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(B.Text(label, "Label"));
        var group = new Border { Classes = { "SegmentGroup" }, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        string name = "role" + Guid.NewGuid().ToString("N");
        RadioButton Option(int? value, int colour, string text)
        {
            var c = new StackPanel { Orientation = Orientation.Horizontal };
            c.Children.Add(Chip(colour, 16, 16));
            c.Children.Add(B.Text(text, null, null, null, new Thickness(6, 0, 0, 0)));
            var rb = new RadioButton { Content = c, Classes = { "Segment" }, GroupName = name, IsChecked = value == selected };
            rb.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, $"{label}: {(value == null ? "keep current " : "")}#{colour:X6}");
            rb.Checked += (_, _) => { set(value); UpdateApply(); DrawPreview(); };
            return rb;
        }
        line.Children.Add(Option(null, current, "Current"));
        foreach (var s in r.Swatches) line.Children.Add(Option(s.Color, s.Color, s.Hex));
        group.Child = line;
        var scroller = new ScrollViewer { Content = group, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        row.Children.Add(scroller);
        return row;
    }

    static Border Chip(int rgb, double w, double h)
    {
        var b = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)), VerticalAlignment = VerticalAlignment.Center };
        B.Bd(b, "ControlBorderStrongBrush");
        return b;
    }

    void UpdateApply()
    {
        bool change = (ChosenPrimary is int a && a != (_current.ColorA & 0xFFFFFF)) || (ChosenSecondary is int b && b != (_current.ColorB & 0xFFFFFF));
        _apply.IsEnabled = Result != null && change;
    }

    /// <summary>The proposed look: the current effect and every other setting, with only the chosen colours swapped in.</summary>
    public RgbConfig Proposal()
    {
        var p = _current.WithEffect(_effect);
        if (ChosenPrimary is int a) p.ColorA = a;
        if (ChosenSecondary is int b) p.ColorB = b;
        return p;
    }

    void DrawPreview()
    {
        if (!IsLoaded && _svc.Fixture == null) return;
        _render(_viz, Proposal(), _clock.Elapsed.TotalSeconds + 5);
    }

    /// <summary>Fixtures and checks: draw the preview now (a still frame).</summary>
    internal void RenderNow() => _render(_viz, Proposal(), 7.3);
}
