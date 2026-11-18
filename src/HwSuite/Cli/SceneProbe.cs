using HwSuite.Core;
using HwSuite.Lighting;

namespace HwSuite.Cli;

/// <summary>
/// --scene-probe [effect] [seconds]: prints how an atmosphere scene moves, as text, without hardware or a window. Rows are
/// the case's parts (default and a known 4 x 12 + 6 layout) and one memory stick's LEDs (LED 0 = top), columns are time
/// (10 per second); each cell is the brightest LED's level 0..9 above the resting tone. For tuning effects by eye.
/// </summary>
public static class SceneProbe
{
    public static int Run(string effectName, double seconds)
    {
        if (!Enum.TryParse<EffectKind>(effectName, true, out var kind) || !EffectInfo.IsScene(kind)) { Console.WriteLine("scene? " + string.Join(", ", EffectInfo.Scenes.Concat(EffectInfo.RainScenes))); return 2; }
        var look = LightingLooks.Starters.FirstOrDefault(l => l.Effect == kind);
        var cfg = new RgbConfig { Effect = kind, ColorA = look?.ColorA ?? 0xA9C4D4, ColorB = look?.ColorB ?? 0x2E6A6E, Brightness = 1, Speed = look?.Speed ?? 0.4, Density = look?.Density ?? EffectInfo.DefaultDensity(kind), Glow = look?.Glow ?? 0.45, Lightning = true };
        Console.WriteLine($"{EffectInfo.Label(kind)}: speed {cfg.Speed:0.00}, density {cfg.Density:0.00}, glow {cfg.Glow:0.00}; {seconds:0} s at 10 columns/s");
        foreach (var (title, layout, count) in new[] { ("case, LED split assumed (27)", CaseLayout.Default, 27), ("case, 4 x 12 + logo 6 (54)", new CaseLayout { LedsPerFan = 12, LogoLeds = 6 }, 54), ("case hub mirrored: one 8-LED ring, LED 1 at the top (rows = LEDs)", new CaseLayout { Mirrored = true, LedsPerFan = 8, LogoLeds = 3 }, 8) })
        {
            var lt = new LayoutTransform(null, 1, layout);
            var g = lt.GeometryFor(DeviceRole.Strip, count);
            var parts = layout.Mirrored ? Enumerable.Range(0, count).Select(i => (Kind: CasePartKind.FrontTop, Start: i, Count: 1)).ToList() : layout.Allocate(count).ToList();
            Console.WriteLine("  " + title);
            var rows = parts.Select(p => new System.Text.StringBuilder()).ToArray();
            var frame = new Rgb[count];
            for (int s = 0; s < seconds * 10; s++)
            {
                double t = 5 + s / 10.0;
                Effects.Render(kind, EffectContext.From(cfg, t, 45), g, frame);
                for (int p = 0; p < parts.Count; p++)
                {
                    double max = 0;
                    for (int i = parts[p].Start; i < parts[p].Start + parts[p].Count; i++) max = Math.Max(max, Level(frame[i], cfg));
                    rows[p].Append(Cell(max));
                }
            }
            for (int p = 0; p < parts.Count; p++) Console.WriteLine($"    {(layout.Mirrored ? $"ring LED {p + 1}" : CaseLayout.Name(parts[p].Kind)),-18} {rows[p]}");
        }
        var ram = LayoutTransform.Identity.GeometryFor(DeviceRole.Ram, 8, new[] { 8 });
        Console.WriteLine("  memory stick (8 LEDs, LED 0 at the top)");
        var rr = Enumerable.Range(0, 8).Select(_ => new System.Text.StringBuilder()).ToArray();
        var rf = new Rgb[8];
        for (int s = 0; s < seconds * 10; s++)
        {
            Effects.Render(kind, EffectContext.From(cfg, 5 + s / 10.0, 45), ram, rf);
            for (int i = 0; i < 8; i++) rr[i].Append(Cell(Level(rf[i], cfg)));
        }
        for (int i = 0; i < 8; i++) Console.WriteLine($"    LED {i,-14} {rr[i]}");
        return 0;
    }

    /// <summary>How far an LED is from the resting tone toward the primary colour, 0..1 (by luminance).</summary>
    static double Level(Rgb c, RgbConfig cfg)
    {
        var a = Rgb.FromInt(cfg.ColorA); var b = Rgb.FromInt(cfg.ColorB);
        double L(Rgb x) => 0.2126 * x.R + 0.7152 * x.G + 0.0722 * x.B;
        double rest = L(b) * Atmosphere.RestLevel(cfg.Glow), top = Math.Max(rest + 1, L(a));
        return Math.Clamp((L(c) - rest) / (top - rest), 0, 1.2);
    }

    static char Cell(double v) => v < 0.05 ? '.' : (char)('0' + Math.Clamp((int)Math.Round(v * 9), 0, 9));
}
