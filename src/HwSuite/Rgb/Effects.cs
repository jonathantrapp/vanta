using HwSuite.Core;

namespace HwSuite.Lighting;

public sealed class EffectContext
{
    public double Time { get; init; }
    public double Speed { get; init; }       // 0..1
    public double Brightness { get; init; }  // 0..1
    public Rgb ColorA { get; init; }
    public Rgb ColorB { get; init; }
    public double? ThermalC { get; init; }   // smoothed max(CPU, GPU) temperature
    /// <summary>Atmosphere effects: how much happens (0..1), the resting glow (0..1) and, for Distant Storm, lightning on.</summary>
    public double Density { get; init; } = 0.35;
    public double Glow { get; init; } = 0.5;
    public bool Lightning { get; init; }
    /// <summary>
    /// Scene clock for the atmosphere effects' slow drift (mist, canopy, water): seconds of motion accumulated at the
    /// speed in force at each moment (<see cref="DriftRate"/>), so moving the speed slider changes how fast the scene moves
    /// from then on and never where it is. Elapsed time × speed would jump the whole pattern after VANTA has run a while.
    /// </summary>
    public double Drift { get; init; }
    /// <summary>Breathing rate in Hz when it is locked to the mouse (<see cref="MouseSync"/>); null = VANTA's own rate.</summary>
    public double? BreathingHz { get; init; }

    /// <summary>Drift per second at a speed setting (1.0 at 50 %).</summary>
    public static double DriftRate(double speed) => 0.4 + 1.2 * Math.Clamp(speed, 0, 1);

    /// <summary>The same look sampled at another moment (a device whose transport needs a lead).</summary>
    public EffectContext At(double time) => new()
    {
        Time = time, Speed = Speed, Brightness = Brightness, ColorA = ColorA, ColorB = ColorB, ThermalC = ThermalC,
        Density = Density, Glow = Glow, Lightning = Lightning, Drift = Drift + (time - Time) * DriftRate(Speed), BreathingHz = BreathingHz
    };

    /// <param name="drift">The renderer's accumulated scene clock; null = time × the current rate (constant speed: tests, fixtures).</param>
    public static EffectContext From(RgbConfig c, double time, double? thermal, bool lightningAllowed = true, double? drift = null) => new()
    {
        Time = time, Speed = c.Speed, Brightness = c.Brightness, ColorA = Rgb.FromInt(c.ColorA), ColorB = Rgb.FromInt(c.ColorB), ThermalC = thermal,
        Density = c.Density, Glow = c.Glow, Lightning = lightningAllowed && c.Lightning && EffectInfo.UsesLightning(c.Effect),
        Drift = drift ?? time * DriftRate(c.Speed),
        BreathingHz = MouseSync.Active(c) ? MouseSync.LockedHz(c.Speed) : null
    };

    /// <summary>
    /// Advances a renderer's scene clock to <paramref name="time"/> at the current speed (engine and preview each keep one).
    /// A long gap just moves the scene on; there is nothing to catch up.
    /// </summary>
    public static double AdvanceDrift(ref double drift, ref double lastTime, double time, double speed)
    {
        if (double.IsNaN(lastTime) || time < lastTime) drift = time * DriftRate(speed);
        else drift += (time - lastTime) * DriftRate(speed);
        lastTime = time;
        return drift;
    }
}

/// <summary>Pure functions producing the colour of one LED at spatial position 0..1 for a given effect.</summary>
public static class Effects
{
    /// <summary>
    /// One whole frame for a device. Classic effects are evaluated per LED from its setup-wide position exactly as before
    /// (<see cref="Evaluate"/>); the atmosphere scenes use the device's geometry (<see cref="Atmosphere"/>). The engine and
    /// the simulated preview both call this, so the preview shows the same algorithm the hardware frames come from.
    /// </summary>
    public static void Render(EffectKind kind, EffectContext c, LedGeometry g, Span<Rgb> frame)
    {
        if (EffectInfo.IsScene(kind)) { Atmosphere.Render(kind, c, g, frame); return; }
        var line = g.Line;
        for (int i = 0; i < frame.Length; i++) frame[i] = Evaluate(kind, c, line.Length > i ? line[i] : 0);
    }

    public static Rgb Evaluate(EffectKind kind, EffectContext c, double position)
    {
        if (EffectInfo.IsScene(kind))
        {
            // a single LED at this position (callers that have no geometry); the engine and preview use Render
            Span<Rgb> one = stackalloc Rgb[1];
            Atmosphere.Render(kind, c, LedGeometry.Linear(new[] { position }, null, LedGeometry.SeedFor(null)), one);
            return one[0];
        }
        Rgb color = kind switch
        {
            EffectKind.Static => c.ColorA,
            EffectKind.Breathing => Breathing(c),
            EffectKind.GradientWave => GradientWave(c, position),
            EffectKind.ThermalReactive => Thermal(c.ThermalC),
            EffectKind.Stealth => Rgb.Black,
            _ => c.ColorA
        };
        return kind == EffectKind.Stealth ? Rgb.Black : color.Scale(c.Brightness);
    }

    static Rgb Breathing(EffectContext c)
    {
        // locked to the mouse: every device breathes with the mouse's own (filmed) shape, dark at time n / rate
        if (c.BreathingHz is double locked) return c.ColorA.Scale(MouseSync.Shape(c.Time, locked));
        double rate = MouseSync.BreathingHz(c.Speed);       // Hz; dark at time n / rate
        double phase = 0.5 - 0.5 * Math.Cos(2 * Math.PI * rate * c.Time);
        double eased = 0.04 + 0.96 * phase * phase * (3 - 2 * phase); // smoothstep, never fully dark
        return c.ColorA.Scale(eased);
    }

    static Rgb GradientWave(EffectContext c, double position)
    {
        double rate = 0.08 + c.Speed * 0.5;
        double wave = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (position * 1.0 - c.Time * rate));
        return Rgb.Lerp(c.ColorA, c.ColorB, wave);
    }

    // Cold -> hot ramp used by Thermal Reactive mode (blue, green, amber, red).
    static readonly (double t, Rgb c)[] ThermalStops =
    {
        (30, new Rgb(0x1E, 0x40, 0xFF)),
        (45, new Rgb(0x00, 0xC8, 0x6E)),
        (60, new Rgb(0xFF, 0xB0, 0x00)),
        (75, new Rgb(0xFF, 0x30, 0x10)),
        (85, new Rgb(0xE1, 0x1D, 0x48)),
    };

    public static Rgb Thermal(double? temp)
    {
        if (temp is not double t) return new Rgb(0x40, 0x40, 0x40);
        if (t <= ThermalStops[0].t) return ThermalStops[0].c;
        for (int i = 1; i < ThermalStops.Length; i++)
        {
            if (t <= ThermalStops[i].t)
            {
                var a = ThermalStops[i - 1]; var b = ThermalStops[i];
                return Rgb.Lerp(a.c, b.c, (t - a.t) / (b.t - a.t));
            }
        }
        return ThermalStops[^1].c;
    }
}
