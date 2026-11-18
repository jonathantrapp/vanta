using System.Text.Json.Serialization;

namespace HwSuite.Core;

/// <summary>
/// A saved lighting look: effect, primary and secondary colour, brightness and speed, and for the atmosphere effects their
/// density, glow and (Distant Storm) lightning. Nothing else: no fan settings, startup options, device inclusion,
/// verification, RAM streaming mode, arrangement, LED counts or order, or rates. A field is null when the effect does not
/// use it: applying the look then leaves that setting alone (older saved looks have no atmosphere fields at all).
/// </summary>
public sealed class LightingLook
{
    public string Name { get; set; } = "";
    EffectKind _effect = EffectKind.Static;
    /// <summary>Saved as "Effect" (classic fallback, readable by 1.12) plus "Scene", like <see cref="RgbConfig.Effect"/>.</summary>
    [JsonIgnore] public EffectKind Effect { get => _effect; set => _effect = value; }
    [JsonPropertyName("Effect")]
    public EffectKind SavedEffect { get => EffectInfo.ClassicFallback(_effect); set { if (!EffectInfo.IsScene(_effect)) _effect = value; } }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EffectKind? Scene { get => EffectInfo.IsScene(_effect) ? EffectInfo.SceneFallback(_effect) : null; set { if (value is EffectKind sc && EffectInfo.IsScene(sc) && !EffectInfo.IsNewScene(_effect)) _effect = sc; } }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EffectKind? Scene2 { get => EffectInfo.IsNewScene(_effect) ? _effect : null; set { if (value is EffectKind sc && EffectInfo.IsNewScene(sc)) _effect = sc; } }
    public int ColorA { get; set; }
    public int ColorB { get; set; }
    public double Brightness { get; set; } = 1;
    public double? Speed { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Density { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Glow { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Lightning { get; set; }
    /// <summary>Shown under a starter's name in the selector (for example what it was called before).</summary>
    [JsonIgnore] public string? Note { get; init; }
    /// <summary>Starter group heading in the selector.</summary>
    [JsonIgnore] public string? Group { get; init; }
    /// <summary>Built in (a starter look); never stored, renamed or deleted.</summary>
    [JsonIgnore] public bool Starter { get; init; }

    public LightingLook Copy(string? name = null) => new()
    {
        Name = name ?? Name, Effect = Effect, ColorA = ColorA, ColorB = ColorB, Brightness = Brightness, Speed = Speed,
        Density = Density, Glow = Glow, Lightning = Lightning
    };

    /// <summary>The secondary colour shows in Gradient Wave and the atmosphere effects; other effects keep it for later.</summary>
    [JsonIgnore] public bool UsesSecondary => EffectInfo.UsesSecondary(Effect);
    [JsonIgnore] public bool UsesSpeed => EffectInfo.UsesSpeed(Effect);

    [JsonIgnore] public string Summary
    {
        get
        {
            string colours = UsesSecondary ? $"#{ColorA:X6} → #{ColorB:X6}" : $"#{ColorA:X6}";
            string speed = UsesSpeed && Speed is double s ? (s <= LightingLooks.Slow + 0.001 ? " · slow" : $" · speed {s * 100:0}%") : "";
            string density = EffectInfo.DensityLabel(Effect) is string dl && Density is double d ? $" · {dl.ToLowerInvariant()} {d * 100:0}%" : "";
            string lightning = EffectInfo.UsesLightning(Effect) && Lightning is bool l ? (l ? " · lightning" : " · no lightning") : "";
            return $"{EffectLabel(Effect)} · {colours} · {Brightness * 100:0}%{speed}{density}{lightning}";
        }
    }

    public static string EffectLabel(EffectKind e) => EffectInfo.Label(e);
}

/// <summary>
/// Starter and saved looks, and the one apply path shared by the Lighting page and the tray menu. Applying only edits
/// the lighting configuration the engine already reads (so existing debouncing, settle times and the 1.11.1 solid look
/// at quit all apply unchanged); it never switches lighting output on, and during Lights out it changes only the look
/// restored afterwards. Saving, renaming and deleting change config.json only: no device command is sent.
/// </summary>
public static class LightingLooks
{
    /// <summary>"Slow" on the existing speed slider (0..1): Breathing about 0.24 breaths/s on the streamed devices. The
    /// mouse and keyboard breathe on their own timing at their own mapped speed; nothing about protocol timing changes.</summary>
    public const double Slow = 0.20;
    public const int MaxSaved = 24;
    public const int MaxNameLength = 40;

    public const string ClassicGroup = "Starters", ForestGroup = "Rainy forest", RainGroup = "Rain (1.15)", MoreGroup = "Fire, radar, typing (1.20)";
    /// <summary>1.12 called the teal/silver Gradient Wave starter "Rainfall"; that name now belongs to the falling-rain effect.</summary>
    public const string TealTide = "Teal Tide", FormerRainfall = "Rainfall";

    /// <summary>
    /// Built-in looks, never stored. The first four are the 1.12 wallpaper starters with unchanged values (the Gradient
    /// Wave one renamed from "Rainfall" to "Teal Tide"); the "Rainy forest" set uses the 1.13 atmosphere effects. Design
    /// starting points, not measured LED calibration: RGB LEDs can show muted colours differently. Starters are matched
    /// by their values, so renaming one never changes anybody's settings.
    /// </summary>
    public static readonly IReadOnlyList<LightingLook> Starters = new[]
    {
        new LightingLook { Name = "Forest Mist", Effect = EffectKind.Breathing, ColorA = 0x6EAAA5, ColorB = 0xB8CBC9, Brightness = 0.35, Speed = Slow, Starter = true, Group = ClassicGroup },
        new LightingLook { Name = TealTide, Effect = EffectKind.GradientWave, ColorA = 0x397780, ColorB = 0xA9C4CC, Brightness = 0.35, Speed = Slow, Starter = true, Group = ClassicGroup, Note = "called Rainfall in 1.12" },
        new LightingLook { Name = "Deep Woods", Effect = EffectKind.Static, ColorA = 0x39746A, ColorB = 0x39746A, Brightness = 0.25, Speed = null, Starter = true, Group = ClassicGroup },
        new LightingLook { Name = "Moonlit Fog", Effect = EffectKind.Static, ColorA = 0xB4CAD0, ColorB = 0xB4CAD0, Brightness = 0.30, Speed = null, Starter = true, Group = ClassicGroup },

        Scene("Window Rain", EffectKind.Rainfall, 0xA9C4D4, 0x2E6A6E, 0.55, 0.30, 0.30, 0.45),
        Scene("Midnight Downpour", EffectKind.Rainfall, 0x8EA6C6, 0x26405A, 0.55, 0.55, 0.80, 0.40),
        Scene("Mountain Fog", EffectKind.RollingMist, 0xB6C8CC, 0x2F5A50, 0.50, 0.25, 0.50, 0.50),
        Scene("Moon Through Pines", EffectKind.Canopy, 0xCFDCE6, 0x1F4C3B, 0.50, 0.20, 0.70, 0.45),
        Scene("Forest Fireflies", EffectKind.Fireflies, 0xC6D9A6, 0x24503A, 0.55, 0.35, 0.40, 0.40),
        Scene("Moonlit Lake", EffectKind.MoonlitWater, 0xD0DCE8, 0x2A4A66, 0.50, 0.30, 0.45, 0.45),
        Scene("Distant Thunder", EffectKind.DistantStorm, 0x9DB2C4, 0x2C4250, 0.55, 0.40, 0.45, 0.45, lightning: true),
        Scene("Still Canopy", EffectKind.Canopy, 0x9CC7A8, 0x22523E, 0.45, 0.05, 0.15, 0.55),

        Scene("Dripping Leaves", EffectKind.Drip, 0xBFE6D2, 0x1E4A3A, 0.60, 0.35, 0.40, 0.45, group: RainGroup),
        Scene("Rain on the Pond", EffectKind.RainRipples, 0xC8DCEC, 0x21435C, 0.55, 0.35, 0.45, 0.45, group: RainGroup),
        Scene("Monsoon", EffectKind.Downpour, 0xA9C0D8, 0x1F3646, 0.60, 0.60, 0.70, 0.40, lightning: true, group: RainGroup),
        Scene("Neon Rain", EffectKind.Rainfall, 0x46E0FF, 0x2A1050, 0.60, 0.45, 0.45, 0.45, group: RainGroup),

        Scene("Campfire", EffectKind.Embers, 0xFF8A2A, 0x5A1004, 0.65, 0.45, 0.45, 0.40, group: MoreGroup),
        Scene("Blue Flame", EffectKind.Embers, 0x6FD8FF, 0x0A1E5A, 0.60, 0.45, 0.40, 0.40, group: MoreGroup),
        Scene("Sonar", EffectKind.Radar, 0x46FF9A, 0x02301A, 0.60, 0.35, 0.35, 0.35, group: MoreGroup),
        Scene("Night Radar", EffectKind.Radar, 0x46E0FF, 0x2A1050, 0.60, 0.40, 0.35, 0.35, group: MoreGroup),
        Scene("Neon Typing", EffectKind.TypingRipples, 0x46E0FF, 0x2A1050, 0.65, 0.50, 0.50, 0.35, group: MoreGroup),
    };

    static LightingLook Scene(string name, EffectKind e, int a, int b, double brightness, double speed, double density, double glow, bool lightning = false, string group = ForestGroup) => new()
    {
        Name = name, Effect = e, ColorA = a, ColorB = b, Brightness = brightness, Speed = speed, Density = density, Glow = glow,
        Lightning = EffectInfo.UsesLightning(e) ? lightning : null, Starter = true, Group = group
    };

    public const string StarterNote = "Starter looks are design starting points inspired by your wallpaper, not measured LED calibration; LEDs can show muted colours differently.";

    public static List<LightingLook> All(AppConfig cfg) { lock (cfg.Sync) return Starters.Concat(cfg.Looks.Select(l => l.Copy())).ToList(); }

    /// <summary>The look the owner would get back: during Lights out, the effect restored afterwards.</summary>
    public static EffectKind EffectiveEffect(RgbConfig c) => LightsOut.IsOn(c) ? c.LightsOutRestoreEffect ?? EffectKind.Static : c.Effect;

    /// <summary>The current look under a name. Null when the effect is Stealth outside Lights out (use Lights out instead).</summary>
    public static LightingLook? Capture(AppConfig cfg, string name)
    {
        lock (cfg.Sync)
        {
            var c = cfg.Rgb;
            var e = EffectiveEffect(c);
            if (e == EffectKind.Stealth) return null;
            var look = new LightingLook { Name = name.Trim(), Effect = e, ColorA = c.ColorA & 0xFFFFFF, ColorB = c.ColorB & 0xFFFFFF, Brightness = Math.Clamp(c.Brightness, 0, 1) };
            look.Speed = look.UsesSpeed ? Math.Clamp(c.Speed, 0, 1) : null;
            look.Density = EffectInfo.UsesDensity(e) ? Math.Clamp(c.Density, 0, 1) : null;
            look.Glow = EffectInfo.UsesGlow(e) ? Math.Clamp(c.Glow, 0, 1) : null;
            look.Lightning = EffectInfo.UsesLightning(e) ? c.Lightning : null;
            return look;
        }
    }

    /// <summary>
    /// The one apply path (Lighting page and tray). Output on/off is never changed. During Lights out the devices stay dark:
    /// only the restore point and the colours (unused while dark) change, so pressing Lights out again shows this look.
    /// </summary>
    public static void Apply(AppConfig cfg, LightingLook look)
    {
        lock (cfg.Sync)
        {
            var c = cfg.Rgb;
            if (LightsOut.IsOn(c)) c.LightsOutRestoreEffect = look.Effect == EffectKind.Stealth ? EffectKind.Static : look.Effect;
            else c.Effect = look.Effect;
            c.ColorA = look.ColorA & 0xFFFFFF;
            c.ColorB = look.ColorB & 0xFFFFFF;
            c.Brightness = Math.Clamp(look.Brightness, 0, 1);
            if (look.Speed is double s) c.Speed = Math.Clamp(s, 0, 1);
            if (look.Density is double d) c.Density = Math.Clamp(d, 0, 1);
            if (look.Glow is double g) c.Glow = Math.Clamp(g, 0, 1);
            if (look.Lightning is bool l) c.Lightning = l;
        }
    }

    /// <summary>The current settings are this look (ignores fields the look does not set).</summary>
    public static bool Matches(AppConfig cfg, LightingLook look)
    {
        lock (cfg.Sync)
        {
            var c = cfg.Rgb;
            return EffectiveEffect(c) == look.Effect && (c.ColorA & 0xFFFFFF) == (look.ColorA & 0xFFFFFF)
                && (!look.UsesSecondary || (c.ColorB & 0xFFFFFF) == (look.ColorB & 0xFFFFFF))
                && Math.Abs(c.Brightness - look.Brightness) < 0.005
                && (!look.UsesSpeed || look.Speed is not double s || Math.Abs(c.Speed - s) < 0.005)
                && (!EffectInfo.UsesDensity(look.Effect) || look.Density is not double d || Math.Abs(c.Density - d) < 0.005)
                && (!EffectInfo.UsesGlow(look.Effect) || look.Glow is not double g || Math.Abs(c.Glow - g) < 0.005)
                && (!EffectInfo.UsesLightning(look.Effect) || look.Lightning is not bool l || c.Lightning == l);
        }
    }

    /// <summary>Why a name cannot be used (null when it can). Starter names are reserved; names are unique, case-insensitive.</summary>
    public static string? NameProblem(AppConfig cfg, string name, string? renaming = null)
    {
        name = name.Trim();
        if (name.Length == 0) return "Enter a name.";
        if (name.Length > MaxNameLength) return $"Use at most {MaxNameLength} characters.";
        if (Starters.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) return "That is a starter look's name; choose another.";
        lock (cfg.Sync)
            if (cfg.Looks.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(l.Name, renaming, StringComparison.OrdinalIgnoreCase)))
                return "A saved look already has that name.";
        return null;
    }

    /// <summary>Save the current settings as a new look. Returns an error text, or null when saved.</summary>
    public static string? SaveCurrent(AppConfig cfg, string name)
    {
        if (NameProblem(cfg, name) is string problem) return problem;
        var look = Capture(cfg, name);
        if (look == null) return "Stealth is not saved as a look; use Lights out instead.";
        lock (cfg.Sync)
        {
            if (cfg.Looks.Count >= MaxSaved) return $"At most {MaxSaved} saved looks; delete one first.";
            cfg.Looks.Add(look);
        }
        return null;
    }

    public static string? Rename(AppConfig cfg, string oldName, string newName)
    {
        if (NameProblem(cfg, newName, oldName) is string problem) return problem;
        lock (cfg.Sync)
        {
            var l = cfg.Looks.FirstOrDefault(x => string.Equals(x.Name, oldName, StringComparison.OrdinalIgnoreCase));
            if (l == null) return "That look no longer exists.";
            l.Name = newName.Trim();
        }
        return null;
    }

    public static bool Delete(AppConfig cfg, string name)
    {
        lock (cfg.Sync) return cfg.Looks.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    public static LightingLook? Find(AppConfig cfg, string name) => All(cfg).FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// After loading: valid, unique, bounded; Stealth entries and duplicate names are dropped. A saved look whose name is
    /// now a starter's (new starters arrived in 1.13) is kept under a "(saved)" name instead of being dropped. Atmosphere
    /// looks missing a field get that effect's default; fields an effect does not use are cleared.
    /// </summary>
    public static List<LightingLook> Normalize(List<LightingLook>? looks)
    {
        var result = new List<LightingLook>();
        var all = looks ?? new();
        // a new name must not clash with a starter, a look already kept, or another saved look still to come
        bool Taken(string n, LightingLook self) => Starters.Any(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))
            || result.Any(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase))
            || all.Any(o => o != null && !ReferenceEquals(o, self) && string.Equals(o.Name?.Trim(), n, StringComparison.OrdinalIgnoreCase));
        foreach (var l in all)
        {
            if (l == null || string.IsNullOrWhiteSpace(l.Name) || !Enum.IsDefined(l.Effect) || l.Effect == EffectKind.Stealth) continue;
            var name = l.Name.Trim();
            if (name.Length > MaxNameLength) name = name[..MaxNameLength];
            if (result.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            if (Starters.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                string stem = name.Length > MaxNameLength - 10 ? name[..(MaxNameLength - 10)] : name, renamed = stem + " (saved)";
                for (int i = 2; Taken(renamed, l) && i < 100; i++) renamed = $"{stem} (saved {i})";
                if (Taken(renamed, l)) continue;
                name = renamed;
            }
            var c = l.Copy(name);
            c.ColorA &= 0xFFFFFF; c.ColorB &= 0xFFFFFF;
            c.Brightness = double.IsFinite(c.Brightness) ? Math.Clamp(c.Brightness, 0, 1) : 1;
            c.Speed = c.UsesSpeed && c.Speed is double s && double.IsFinite(s) ? Math.Clamp(s, 0, 1) : c.UsesSpeed ? 0.5 : null;
            c.Density = !EffectInfo.UsesDensity(c.Effect) ? null : c.Density is double d && double.IsFinite(d) ? Math.Clamp(d, 0, 1) : EffectInfo.DefaultDensity(c.Effect);
            c.Glow = !EffectInfo.UsesGlow(c.Effect) ? null : c.Glow is double g && double.IsFinite(g) ? Math.Clamp(g, 0, 1) : 0.5;
            c.Lightning = !EffectInfo.UsesLightning(c.Effect) ? null : c.Lightning ?? true;
            result.Add(c);
            if (result.Count >= MaxSaved) break;
        }
        return result;
    }
}
