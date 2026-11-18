using System.Windows;
using System.Windows.Media;

namespace HwSuite.Ui;

/// <summary>One theme: every semantic colour token the UI uses. Device RGB colours are never taken from here.</summary>
public sealed record ThemePalette(string Id, string Name, string Blurb, bool IsLight, IReadOnlyDictionary<string, Color> Tokens, double ShadowOpacity)
{
    public Color this[string token] => Tokens[token];
}

/// <summary>
/// Central design tokens and live theme switching. Every token is published as "&lt;Token&gt;Brush" (a frozen brush) and
/// "&lt;Token&gt;Color" in the application resources; templates and code reference them by key (DynamicResource /
/// SetResourceReference), so switching replaces the resources and every element follows without rebuilding pages or
/// touching hardware services. Custom-drawn controls read <see cref="UiBrushes"/> at render time and are invalidated on
/// <see cref="Changed"/>. Older key names (BgBrush, MutedBrush, ...) are kept as aliases of the semantic tokens.
/// </summary>
public static class Themes
{
    public const string DefaultId = "Vanta";

    /// <summary>Semantic tokens, in the order a reviewer reads them.</summary>
    public static readonly string[] TokenNames =
    {
        // surfaces
        "Canvas", "Sidebar", "Panel", "Control", "ControlHover", "ControlPressed", "Input", "Popup", "Track",
        // borders
        "Hairline", "ControlBorder", "ControlBorderStrong",
        // text
        "Text", "TextSecondary", "TextTertiary", "TextDisabled", "OnAccent",
        // interaction
        "Accent", "AccentHover", "AccentPressed", "AccentSoft", "Focus", "Selection", "HoverTint", "Overlay",
        // status (meaning is the same in every theme: green ok, amber/orange warning, red error)
        "Ok", "Warn", "Danger", "Info", "OkSoft", "WarnSoft", "DangerSoft",
        // charts and device preview chrome
        "ChartGrid", "ChartGridStrong", "Series1", "Series2", "Series3", "CurveFill", "FloorShade",
        "LedOff", "DeviceBody", "DeviceOutline",
        "ScrollThumb", "ScrollThumbHover", "Shadow"
    };

    /// <summary>Older resource names used across the app, mapped to the semantic token they now mean.</summary>
    static readonly (string alias, string token)[] Aliases =
    {
        ("BgBrush", "Canvas"), ("CardBrush", "Panel"), ("BorderBrush", "Hairline"), ("MutedBrush", "TextSecondary"),
        ("DimBrush", "TextTertiary"), ("AccentSecondaryBrush", "Series2"), ("AccentSoftBrush", "AccentSoft"),
        ("AppCanvas", "Canvas"), ("PanelSurface", "Panel"), ("ControlSurface", "Control"), ("BorderSubtle", "Hairline"),
        ("TextPrimary", "Text"), ("TextMuted", "TextTertiary"), ("AccentPrimary", "Accent"), ("AccentSubtle", "AccentSoft"),
        ("StatusNominal", "Ok"), ("StatusWarning", "Warn"), ("StatusDanger", "Danger"),
    };

    static Color C(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        uint v = Convert.ToUInt32(hex, 16);
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    static ThemePalette Make(string id, string name, string blurb, bool light, double shadow, params (string k, string hex)[] t)
    {
        var d = t.ToDictionary(x => x.k, x => C(x.hex));
        var missing = TokenNames.Where(n => !d.ContainsKey(n)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"theme {id} lacks tokens: {string.Join(", ", missing)}");
        return new ThemePalette(id, name, blurb, light, d, shadow);
    }

    /// <summary>The original VANTA look: near-black titanium surfaces, ice-cyan accent. Tertiary text raised to AA.</summary>
    public static readonly ThemePalette Vanta = Make("Vanta", "VANTA", "Titanium dark with ice-cyan accents", false, 0.5,
        ("Canvas", "#09090B"), ("Sidebar", "#0C0C0E"), ("Panel", "#121215"), ("Control", "#1A1A1F"), ("ControlHover", "#232329"),
        ("ControlPressed", "#16161A"), ("Input", "#0E0E11"), ("Popup", "#17171B"), ("Track", "#26262C"),
        ("Hairline", "#26262B"), ("ControlBorder", "#34343B"), ("ControlBorderStrong", "#6B6B75"),
        ("Text", "#F4F4F5"), ("TextSecondary", "#B4B4BC"), ("TextTertiary", "#8E8E98"), ("TextDisabled", "#5C5C66"), ("OnAccent", "#06121A"),
        ("Accent", "#38BDF8"), ("AccentHover", "#7DD3FC"), ("AccentPressed", "#0EA5E9"), ("AccentSoft", "#0C2D48"), ("Focus", "#7DD3FC"),
        ("Selection", "#1E4A6B"), ("HoverTint", "#14FFFFFF"), ("Overlay", "#B3000000"),
        ("Ok", "#34D399"), ("Warn", "#F59E0B"), ("Danger", "#F87171"), ("Info", "#7DD3FC"),
        ("OkSoft", "#0D2A20"), ("WarnSoft", "#2E2210"), ("DangerSoft", "#341616"),
        ("ChartGrid", "#1F1F24"), ("ChartGridStrong", "#2E2E35"), ("Series1", "#38BDF8"), ("Series2", "#A5B4FC"), ("Series3", "#B4B4BC"),
        ("CurveFill", "#5538BDF8"), ("FloorShade", "#66000000"),
        ("LedOff", "#1E1E23"), ("DeviceBody", "#18181C"), ("DeviceOutline", "#3A3A42"),
        ("ScrollThumb", "#3A3A42"), ("ScrollThumbHover", "#5A5A64"), ("Shadow", "#000000"));

    /// <summary>Warm charcoal, ivory text, champagne gold reserved for selection, focus and primary actions.</summary>
    public static readonly ThemePalette Obsidian = Make("Obsidian", "Obsidian Gold", "Warm charcoal with champagne-gold details", false, 0.45,
        ("Canvas", "#0E0D0B"), ("Sidebar", "#121110"), ("Panel", "#181614"), ("Control", "#211E1B"), ("ControlHover", "#2A2622"),
        ("ControlPressed", "#1C1A17"), ("Input", "#13110F"), ("Popup", "#1E1B18"), ("Track", "#2C2824"),
        ("Hairline", "#2B2722"), ("ControlBorder", "#3B362F"), ("ControlBorderStrong", "#7A705F"),
        ("Text", "#F2ECE1"), ("TextSecondary", "#BFB5A5"), ("TextTertiary", "#988E7E"), ("TextDisabled", "#625A4E"), ("OnAccent", "#17130C"),
        ("Accent", "#CFB27C"), ("AccentHover", "#DEC697"), ("AccentPressed", "#B89A62"), ("AccentSoft", "#2D2618"), ("Focus", "#E4CF9F"),
        ("Selection", "#4A3E26"), ("HoverTint", "#12FFF6E6"), ("Overlay", "#B3000000"),
        ("Ok", "#7DC795"), ("Warn", "#F2934A"), ("Danger", "#EF7B6E"), ("Info", "#A9BFD8"),
        ("OkSoft", "#16261B"), ("WarnSoft", "#33210F"), ("DangerSoft", "#361A16"),
        ("ChartGrid", "#24211D"), ("ChartGridStrong", "#36312A"), ("Series1", "#CFB27C"), ("Series2", "#93AECF"), ("Series3", "#BFB5A5"),
        ("CurveFill", "#4ACFB27C"), ("FloorShade", "#66000000"),
        ("LedOff", "#24211E"), ("DeviceBody", "#1D1A17"), ("DeviceOutline", "#423B33"),
        ("ScrollThumb", "#3D3730"), ("ScrollThumbHover", "#5E564B"), ("Shadow", "#000000"));

    /// <summary>Warm porcelain and white panels, graphite text, platinum hairlines, a restrained deep-blue accent.</summary>
    public static readonly ThemePalette Porcelain = Make("Porcelain", "Porcelain Platinum", "Light porcelain with graphite text and deep-blue accents", true, 0.14,
        ("Canvas", "#F3F0EB"), ("Sidebar", "#EAE6DF"), ("Panel", "#FFFFFF"), ("Control", "#F6F4F0"), ("ControlHover", "#ECE8E2"),
        ("ControlPressed", "#E3DED6"), ("Input", "#FFFFFF"), ("Popup", "#FFFFFF"), ("Track", "#E4E0DA"),
        ("Hairline", "#E2DDD5"), ("ControlBorder", "#CEC8BE"), ("ControlBorderStrong", "#857F76"),
        ("Text", "#1D2127"), ("TextSecondary", "#474D56"), ("TextTertiary", "#626872"), ("TextDisabled", "#A39E96"), ("OnAccent", "#FFFFFF"),
        ("Accent", "#1F4F8F"), ("AccentHover", "#2A62AA"), ("AccentPressed", "#173D70"), ("AccentSoft", "#E2EAF5"), ("Focus", "#1F4F8F"),
        ("Selection", "#C9D8EE"), ("HoverTint", "#0A1D2127"), ("Overlay", "#661D2127"),
        ("Ok", "#1C7A4C"), ("Warn", "#94570A"), ("Danger", "#B42318"), ("Info", "#1F4F8F"),
        ("OkSoft", "#E5F3EB"), ("WarnSoft", "#FBF0DF"), ("DangerSoft", "#FBE6E3"),
        ("ChartGrid", "#EEEAE4"), ("ChartGridStrong", "#D8D2C8"), ("Series1", "#1F4F8F"), ("Series2", "#8A4FB5"), ("Series3", "#626872"),
        ("CurveFill", "#331F4F8F"), ("FloorShade", "#1A1D2127"),
        ("LedOff", "#D6D1C9"), ("DeviceBody", "#F1EEE9"), ("DeviceOutline", "#B6B0A6"),
        ("ScrollThumb", "#CEC8BE"), ("ScrollThumbHover", "#A7A198"), ("Shadow", "#3A2E22"));

    public static readonly ThemePalette[] All = { Vanta, Obsidian, Porcelain };

    public static ThemePalette Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Vanta;

    public static ThemePalette Current { get; private set; } = Vanta;
    /// <summary>Raised on the UI thread after the resources were replaced.</summary>
    public static event Action? Changed;

    static readonly Dictionary<string, Brush> _brushes = new();

    /// <summary>Current frozen brush for a token ("Accent") or resource key ("AccentBrush", "MutedBrush").</summary>
    public static Brush Brush(string tokenOrKey)
    {
        var token = TokenFor(tokenOrKey);
        lock (_brushes) if (_brushes.TryGetValue(token, out var b)) return b;
        return Freeze(new SolidColorBrush(Current[token]));
    }

    public static string TokenFor(string key)
    {
        foreach (var (a, t) in Aliases) if (a == key) return t;
        return key.EndsWith("Brush") ? key[..^5] : key;
    }

    static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>Publish a theme's tokens into <paramref name="res"/> (the application resources) and notify custom controls.</summary>
    public static void Apply(ThemePalette theme, ResourceDictionary res)
    {
        Current = theme;
        lock (_brushes)
        {
            _brushes.Clear();
            foreach (var name in TokenNames)
            {
                var c = theme[name];
                var b = Freeze(new SolidColorBrush(c));
                _brushes[name] = b;
                res[name + "Brush"] = b;
                res[name + "Color"] = c;
            }
            foreach (var (alias, token) in Aliases) res[alias] = _brushes[token];
        }
        res["ShadowOpacity"] = theme.ShadowOpacity;
        res["ThemeIsLight"] = theme.IsLight;
        Changed?.Invoke();
    }

    // ---- contrast (WCAG 2.x) ----------------------------------------------------------------------------------------

    static double Channel(byte v) { double c = v / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
    public static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    /// <summary>Contrast ratio of an (optionally translucent) foreground over an opaque background.</summary>
    public static double Contrast(Color fg, Color bg)
    {
        if (fg.A < 255) fg = Blend(fg, bg);
        double a = Luminance(fg) + 0.05, b = Luminance(bg) + 0.05;
        return Math.Max(a, b) / Math.Min(a, b);
    }

    public static Color Blend(Color fg, Color bg)
    {
        double a = fg.A / 255.0;
        return Color.FromRgb((byte)Math.Round(fg.R * a + bg.R * (1 - a)), (byte)Math.Round(fg.G * a + bg.G * (1 - a)), (byte)Math.Round(fg.B * a + bg.B * (1 - a)));
    }

    /// <summary>
    /// Pairs the UI actually uses, with the WCAG AA minimum that applies: 4.5 for text (all text here is normal size), 3.0 for
    /// meaningful non-text UI (focus ring, selection marks, chart series, switch/checkbox outlines).
    /// </summary>
    public static IEnumerable<(Color fg, Color bg, double min, string what)> ContrastPairs(ThemePalette t)
    {
        foreach (var surface in new[] { "Canvas", "Sidebar", "Panel", "Control", "Popup" })
        {
            yield return (t["Text"], t[surface], 4.5, $"primary text on {surface}");
            yield return (t["TextSecondary"], t[surface], 4.5, $"secondary text on {surface}");
        }
        foreach (var surface in new[] { "Canvas", "Panel" })
        {
            yield return (t["TextTertiary"], t[surface], 4.5, $"tertiary text on {surface}");
            yield return (t["Accent"], t[surface], 4.5, $"accent text on {surface}");
            yield return (t["Focus"], t[surface], 3.0, $"focus ring on {surface}");
            foreach (var s in new[] { "Ok", "Warn", "Danger" }) yield return (t[s], t[surface], 4.5, $"{s} text on {surface}");
            foreach (var s in new[] { "Series1", "Series2" }) yield return (t[s], t[surface], 3.0, $"chart {s} on {surface}");
            yield return (t["ControlBorderStrong"], t[surface], 3.0, $"switch/checkbox outline on {surface}");
        }
        yield return (t["Text"], t["AccentSoft"], 4.5, "text on the selected-item fill");
        yield return (t["Text"], Blend(t["HoverTint"], t["Control"]), 4.5, "text on a hovered control");
        yield return (t["OnAccent"], t["Accent"], 4.5, "primary-button text on the accent");
        yield return (t["OnAccent"], t["AccentHover"], 4.5, "primary-button text on the hovered accent");
        yield return (t["Accent"], t["Control"], 3.0, "selection underline on a control");
        yield return (t["Text"], t["Selection"], 4.5, "selected text");
        foreach (var s in new[] { ("Ok", "OkSoft"), ("Warn", "WarnSoft"), ("Danger", "DangerSoft") })
            yield return (t[s.Item1], t[s.Item2], 4.5, $"{s.Item1} text on its tinted banner");
        yield return (t["Text"], t["WarnSoft"], 4.5, "body text on a warning banner");
    }
}
