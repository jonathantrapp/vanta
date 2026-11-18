// PORTED from src\HwSuite\Ui\Builders.cs (Windows 1.20.0): WPF -> Avalonia 11 (mechanical pass by linux/tools/wpf2avalonia.py, then hand fixes).
using System.Globalization;
using System.Text.RegularExpressions;
using HwSuite.Core;
using HwSuite.Fans;
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
/// Tiny helpers so the pages can be assembled in code with the shared theme styles. Colours are attached by resource key
/// (<see cref="Ref"/>), never by capturing a brush, so a theme switch restyles code-built elements too.
/// </summary>
static class B
{
    /// <summary>The current brush for a key. Only for values re-assigned on every refresh; static colours use <see cref="Ref"/>.</summary>
    public static IBrush Br(string key) => Themes.Brush(key);

    /// <summary>Bind a brush property to a theme resource (follows theme switches).</summary>
    public static T Ref<T>(T el, AvaloniaProperty prop, string key) where T : Control { el.SetResourceReference(prop, key); return el; }
    public static TextBlock Fg(TextBlock t, string key) { t.SetResourceReference(TextBlock.ForegroundProperty, key); return t; }
    public static void Fg(Control c, string key) => c.SetResourceReference(TemplatedControl.ForegroundProperty, key);
    public static void Bg(Border b, string key) => b.SetResourceReference(Border.BackgroundProperty, key);
    public static void Bd(Border b, string key) => b.SetResourceReference(Border.BorderBrushProperty, key);

    public static string N(double? v, string format = "0.0", string na = "--") =>
        v.HasValue ? v.Value.ToString(format, CultureInfo.InvariantCulture) : na;

    public static TextBlock Text(string text, string? style = null, double? size = null, FontWeight? weight = null, Thickness? margin = null, TextAlignment? align = null)
    {
        var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
        if (style != null) t.Classes.Add(style);
        else t.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        if (size.HasValue) t.FontSize = Math.Max(12, size.Value);   // nothing the owner reads is smaller than 12 px
        if (weight.HasValue) t.FontWeight = weight.Value;
        if (margin.HasValue) t.Margin = margin.Value;
        if (align.HasValue) t.TextAlignment = align.Value;
        return t;
    }

    /// <summary>Wrapping body text (explanations): never trimmed.</summary>
    public static TextBlock Para(string text, string style = "Muted", Thickness? margin = null, double? size = null)
    {
        var t = Text(text, style, size, null, margin);
        t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None;
        return t;
    }

    public static Border Card(Control child, Thickness? margin = null, double padding = 18)
    {
        var b = new Border { Classes = { "Card" }, Child = child, Padding = new Thickness(padding) };
        if (margin.HasValue) b.Margin = margin.Value;
        return b;
    }

    /// <summary>Card with a section title (and optional right-aligned element and subtitle).</summary>
    public static Border Section(string title, Control content, string? subtitle = null, Control? right = null, Thickness? margin = null, string? tip = null)
    {
        var s = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, subtitle == null ? 12 : 4), LastChildFill = true };
        if (right != null) { DockPanel.SetDock(right, Dock.Right); head.Children.Add(right); }
        var t = Text(title, "CardTitle"); t.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(t);
        s.Children.Add(head);
        if (subtitle != null) s.Children.Add(Para(subtitle, "Muted", new Thickness(0, 0, 0, 12)));
        s.Children.Add(content);
        var b = Card(s, margin ?? new Thickness(0, 0, 0, 12));
        if (tip != null) ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>Label on the left, fixed-width monospace value on the right.</summary>
    public static Grid KeyValue(string label, out TextBlock value, double valueWidth = 90, string initial = "--")
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Text(label, "Muted"));
        value = Text(initial, "Mono");
        value.Width = valueWidth;
        Grid.SetColumn(value, 1);
        g.Children.Add(value);
        return g;
    }

    public static Grid Row(params (Control el, GridLength width)[] cols)
    {
        var g = new Grid();
        for (int i = 0; i < cols.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = cols[i].width });
            Grid.SetColumn(cols[i].el, i);
            g.Children.Add(cols[i].el);
        }
        return g;
    }

    public static readonly GridLength Star = new(1, GridUnitType.Star);
    public static readonly GridLength Auto = GridLength.Auto;
    public static GridLength Px(double v) => new(v);
}

/// <summary>Tone of a status pill. Meanings are the same in every theme.</summary>
public enum Tone { Neutral, Accent, Ok, Warn, Danger, Info }

/// <summary>Small rounded status label: coloured dot + text on a tinted background. Never colour alone: the text says it.</summary>
public sealed class StatusPill : Border
{
    readonly TextBlock _text;
    readonly Ellipse _dot;
    Tone _tone = (Tone)(-1);

    public StatusPill(string text = "", Tone tone = Tone.Neutral)
    {
        CornerRadius = new CornerRadius(11);
        Padding = new Thickness(9, 3, 11, 3);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        _dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        _text = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        sp.Children.Add(_dot); sp.Children.Add(_text);
        Child = sp;
        Set(text, tone);
    }

    public string Text => _text.Text;
    public Tone Tone => _tone;

    public void Set(string text, Tone tone)
    {
        _text.Text = text;
        if (tone == _tone) return;
        _tone = tone;
        var (fg, bg) = tone switch
        {
            Tone.Ok => ("OkBrush", "OkSoftBrush"),
            Tone.Warn => ("WarnBrush", "WarnSoftBrush"),
            Tone.Danger => ("DangerBrush", "DangerSoftBrush"),
            Tone.Accent => ("AccentBrush", "AccentSoftBrush"),
            Tone.Info => ("InfoBrush", "ControlBrush"),
            _ => ("TextSecondaryBrush", "ControlBrush")
        };
        _dot.SetResourceReference(Shape.FillProperty, fg);
        _text.SetResourceReference(TextBlock.ForegroundProperty, tone == Tone.Neutral ? "TextSecondaryBrush" : fg);
        this.SetResourceReference(BackgroundProperty, bg);
    }

    public static Tone ToneFor(DeviceLightState s) => s switch
    {
        DeviceLightState.Shared => Tone.Ok,
        DeviceLightState.Independent => Tone.Info,
        DeviceLightState.Fault => Tone.Danger,
        DeviceLightState.Blocked or DeviceLightState.Unavailable or DeviceLightState.NotVerified => Tone.Warn,
        _ => Tone.Neutral
    };
}

/// <summary>
/// Short primary names for fan channels ("Rear exhaust", "Front intake · Upper") with the physical header and hardware
/// as secondary detail ("SYS_FAN2 · Super I/O"). Derived from the owner's own labels; the labels are not changed. Header
/// names follow the verified mapping on this board (Phase 9 step tests: control 0 = CPU_FAN, control N = SYS_FAN N for
/// 1..3); controls 4 and 5 are unidentified.
/// </summary>
public static class FanNames
{
    public sealed record Names(string Primary, string Secondary, string Header);

    static readonly Regex HeaderToken = new(@"\b(CPU_FAN|CPU_OPT|SYS_FAN\d|PUMP_FAN|PUMP)\b", RegexOptions.IgnoreCase);
    static readonly Regex Paren = new(@"\(([^)]*)\)");

    public static string Header(IFanChannel ch)
    {
        if (ch.Id.StartsWith("gpu:") && int.TryParse(ch.Id[4..], out var g)) return $"GPU fan {g + 1}";
        int i = ch.Id.LastIndexOf("/control/", StringComparison.Ordinal);
        if (ch.Id.Contains("it8689e") && i >= 0 && int.TryParse(ch.Id[(i + 9)..], out var n))
            return n == 0 ? "CPU_FAN" : n <= 3 ? $"SYS_FAN{n}" : $"Header {n + 1} (unidentified)";
        return ch.Backend;
    }

    public static Names For(AppConfig cfg, IFanChannel ch)
    {
        ChannelConfig? cc; lock (cfg.Sync) cc = cfg.Channels.FirstOrDefault(c => c.Id == ch.Id);
        string label = cc != null && !string.IsNullOrWhiteSpace(cc.Label) ? cc.Label.Trim() : ch.Name;
        var kind = cc?.Kind ?? ch.DefaultKind;
        string header = Header(ch);
        string backend = ch.Resource == Hardware.HwResource.GpuFans ? "NVML" : ch.Backend.StartsWith("SuperIO") ? "Super I/O" : ch.Backend;
        string? model = null;
        string primary;

        if (label == ch.Name || label.StartsWith("ITE IT8689E Fan #"))
            primary = header.StartsWith("Header") ? "Unassigned " + header.Replace(" (unidentified)", "").ToLowerInvariant() : header;
        else if (kind is FanKind.Aio or FanKind.Pump or FanKind.CpuCooler && label.Length > 18)
        {
            model = HeaderToken.Replace(Paren.Replace(label, m => HeaderToken.IsMatch(m.Groups[1].Value) ? "" : m.Value), "").Trim(' ', '-', '—', '–');
            primary = kind == FanKind.Pump ? "Pump" : "CPU cooler";
        }
        else
        {
            string position = "";
            string s = Paren.Replace(label, m =>
            {
                var inner = m.Groups[1].Value.Trim();
                if (HeaderToken.IsMatch(inner)) return "";
                if (Regex.IsMatch(inner, "^(upper|lower|top|bottom|left|right|front|rear|middle)$", RegexOptions.IgnoreCase)) { position = char.ToUpperInvariant(inner[0]) + inner[1..].ToLowerInvariant(); return ""; }
                return m.Value;
            });
            s = HeaderToken.Replace(s, "");
            s = Regex.Replace(s, @"\s*[—–]\s*|\s+-\s+", " ").Trim(' ', '-', '—', '–', '·');
            s = Regex.Replace(s, @"\s{2,}", " ");
            primary = s.Length == 0 ? header : s;
            if (position.Length > 0) primary += " · " + position;
        }
        var parts = new List<string>();
        if (primary.StartsWith("Unassigned")) parts.Add("not identified");
        else if (!primary.Contains(header, StringComparison.OrdinalIgnoreCase)) parts.Add(header);
        if (model != null && model.Length > 0) parts.Add(model);
        parts.Add(backend);
        return new(primary, string.Join(" · ", parts), header);
    }
}
