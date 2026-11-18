using HwSuite.Core;

namespace HwSuite.Lighting;

/// <summary>
/// 1.15 rain family, and the 1.13 rain scenes on the case layout. Same rules as the other scenes: every value is a pure
/// function of (time, settings, geometry, seed) from bounded hashed event slots; no state between frames, no allocation
/// per frame, nothing to catch up after a gap. Ideas from common addressable-LED effects (WLED's Drip, Ripple and Rain),
/// rebuilt for VANTA's devices: a 2-D key surface, short memory runs (LED 0 at the top) and a case with stacked fans.
/// </summary>
public static partial class Atmosphere
{
    struct Fall
    {
        // position (x, head y) in device units, how bright, trail length, envelope, splash level, slant per unit of fall
        public double X, Head, Amp, Trail, Env, Splash, Bottom, Width; public int Seg; public double Bead; public double Top;
        /// <summary>Ring (splitter hub): -1/+1 = runs down the left/right rim instead of falling straight (0 = straight).</summary>
        public int Side;
    }

    /// <summary>Where the ring's rim is at height y on one side (water runs down the rim from the top LED to the bottom one).</summary>
    static double RimX(double y, int side) => 0.18 + side * 0.1 * Math.Sqrt(Math.Max(0, 1 - Sq((y - 0.5) / 0.42)));

    static void RenderRain(EffectKind kind, EffectContext c, LedGeometry g, Span<Rgb> frame, Col a, Col b, double k, double t, double speed, double density, double rest)
    {
        int n = frame.Length;
        bool storm = kind is EffectKind.DistantStorm or EffectKind.Downpour;
        double flash = 0, flashAt = 0;
        if (storm && c.Lightning) flash = LightningLevel(t, out flashAt, out _);
        double gust = kind == EffectKind.Downpour ? Gust(t) : 0;
        var splashCol = Col.Lerp(a, Ivory, 0.2);

        if (kind == EffectKind.RainRipples)
        {
            Span<Ring> rings = stackalloc Ring[MaxSlots];
            int count = CollectRings(g, t, speed, density, rings);
            for (int i = 0; i < n; i++)
            {
                double line = g.Line[i];
                var water = b * (rest * (0.75 + 0.5 * Noise1(line * 1.4 + g.X[i] * 0.6 - c.Drift * 0.03, SetupSeed + 61)));
                double light = Math.Min(1, RingLight(g, i, rings[..count]));
                frame[i] = Col.Lerp(water, a, light).ToRgb(k);
            }
            return;
        }

        Span<Fall> falls = stackalloc Fall[MaxSlots];
        int fc = kind == EffectKind.Drip ? CollectDrips(g, t, speed, density, falls) : CollectFalls(g, t, speed, density, kind == EffectKind.Downpour, gust, falls);
        for (int i = 0; i < n; i++)
        {
            double line = g.Line[i];
            Col bg;
            if (storm)
            {
                double cloud = 0.6 * Noise1(line * 0.9 - t * 0.03, SetupSeed + 3) + 0.4 * Noise1(line * 2.1 - t * 0.05 + 4.2, SetupSeed + 4);
                bg = b * (rest * (0.55 + 0.8 * cloud));
            }
            else bg = b * (rest * (0.8 + 0.4 * Noise1(line * 1.3 - t * 0.025, SetupSeed + 1)));
            if (kind == EffectKind.Downpour)
            {
                // rain curtains: soft bright bands sweeping across the whole setup with the gusts
                double sweep = Smooth(0.55, 0.9, Noise1((line + 0.3 * (g.Case ? g.X[i] : 0)) * 2.4 - t * (0.25 + 0.35 * speed), SetupSeed + 71));
                bg = Col.Lerp(bg, a * 0.55, sweep * (0.12 + 0.3 * gust));
            }
            double drop = 0, splash = 0;
            FallLight(g, i, falls[..fc], kind == EffectKind.Drip, ref drop, ref splash);
            var col = Col.Lerp(bg, a, Math.Min(1, drop * (storm ? 0.85 : 1)));
            if (splash > 0) col = Col.Lerp(col, splashCol, Math.Min(1, splash));
            if (flash > 0)
            {
                // case: the light reaches the top first; elsewhere: brightest near the flash's place on the setup line
                double near = g.Case ? 0.6 + 0.4 * (1 - g.Y[i]) : 0.55 + 0.45 * Math.Exp(-Sq((line - flashAt) / 0.45));
                double behind = 0.6 + 0.4 * Noise1(line * 2.2 + 17.0, SetupSeed + 5);
                col = Col.Lerp(col, Col.Lerp(a, FlashWhite, 0.5), flash * near * behind * 0.5);
            }
            frame[i] = col.ToRgb(k);
        }
    }

    /// <summary>Downpour's wind, shared by every device: 0 (lull) .. 1 (gust), changing over several seconds.</summary>
    public static double Gust(double t) => Smooth(0.3, 0.8, 0.7 * Noise1(t * 0.11, SetupSeed + 81) + 0.3 * Noise1(t * 0.37 + 3.3, SetupSeed + 82));

    // ---- falling rain on the case, and Downpour everywhere -----------------------------------------------------------
    static int CollectFalls(LedGeometry g, double t, double speed, double density, bool downpour, double gust, Span<Fall> falls)
    {
        if (g.Count == 0) return 0;
        double sf = RainSpeedFactor(speed) * (downpour ? 1.55 : 1);
        double dens = downpour ? Math.Clamp(density * (0.55 + 0.7 * gust), 0, 1) : density;
        double slant = downpour ? 0.12 + 0.3 * gust : 0;
        int count = 0;
        if (g.Case)
        {
            // columns over the front stack (falling through every front fan to the logo) and over the rear fan
            bool hasFront = g.HasFrontFan, hasRear = g.HasRearFan;
            if (!hasFront && !hasRear) return 0;
            double frontTop = -0.05, frontBottom = 0.99, rearBottom = 0.36;
            double lifeMax = (frontBottom - frontTop) / (0.42 * RainSpeedFactor(0)) + 0.5;
            int slots = Math.Min(MaxSlots, (downpour ? 6 : 3) + (int)Math.Round((downpour ? 14 : 7) * dens));
            double chance = 0.2 + 0.8 * dens;
            for (int s = 0; s < slots && count < falls.Length; s++)
            {
                if (!Slot(g.Seed + 401, s, t, lifeMax * 1.05, lifeMax * 1.6, chance, lifeMax, out double age, out uint h)) continue;
                double u1 = U01(h); h = Next(h); double u2 = U01(h); h = Next(h); double u3 = U01(h); h = Next(h); double u4 = U01(h); h = Next(h);
                bool rear = hasRear && (!hasFront || U01(h) < 0.28);
                double cx = rear ? 0.82 : 0.18, bottom = rear ? rearBottom : g.Ring ? 0.92 : frontBottom;
                double v = (0.42 + 0.2 * u1) * sf;
                double land = (bottom - frontTop) / v;
                if (age >= land + 0.45) continue;
                double x0 = cx + (u4 - 0.5) * 0.16 - slant * 0.5 * (bottom - frontTop);
                double head = frontTop + v * Math.Min(age, land);
                falls[count++] = new Fall
                {
                    X = x0 + slant * (head - frontTop), Head = head, Amp = 0.55 + 0.45 * u2, Trail = (downpour ? 0.07 : 0.11) + 0.06 * u3,
                    Env = Smooth(0, 0.1, age), Splash = age > land ? Math.Exp(-(age - land) / 0.16) : 0, Bottom = bottom, Width = 0.055, Top = frontTop,
                    Side = g.Ring ? (u4 < 0.5 ? -1 : 1) : 0
                };
            }
            return count;
        }
        if (g.Surface)
        {
            const double Top = -0.035, SplashTime = 0.4;
            double bottom = g.Height, fall = bottom + 0.06 - Top;
            double lifeMax = fall / (0.26 * RainSpeedFactor(0)) + SplashTime;
            int slots = Math.Min(MaxSlots, 14 + (int)Math.Round(10 * dens));
            double chance = 0.25 + 0.75 * dens;
            for (int s = 0; s < slots && count < falls.Length; s++)
            {
                if (!Slot(g.Seed + 431, s, t, lifeMax * 1.05, lifeMax * 1.5, chance, lifeMax, out double age, out uint h)) continue;
                double u1 = U01(h); h = Next(h); double u2 = U01(h); h = Next(h); double u3 = U01(h); h = Next(h);
                int key = (int)(h % (uint)g.Count);
                double v = (0.3 + 0.16 * u1) * sf;
                double land = fall / v;
                if (age >= land + SplashTime) continue;
                double head = Top + v * Math.Min(age, land);
                double x0 = g.X[key] - slant * 0.5 * fall;
                falls[count++] = new Fall
                {
                    X = x0 + slant * (head - Top), Head = head, Amp = 0.5 + 0.45 * u2, Trail = 0.03 + 0.02 * u3, Env = Smooth(0, 0.08, age),
                    Splash = age > land ? Math.Exp(-(age - land) / 0.14) : 0, Bottom = bottom, Width = 0.02, Top = Top
                };
            }
            return count;
        }
        // linear runs: fast droplets, more of them in the gusts
        int slotsLin = Math.Clamp((int)Math.Round(g.Count / 27.0 * (8 + 16 * dens)), 1, MaxSlots);
        double chanceLin = (0.25 + 0.75 * dens) * Math.Min(1, g.Count / 6.0);
        int longest = g.SegmentLength.Max();
        double lifeMaxLin = (Math.Min(longest, 10) + 5) / (8 * RainSpeedFactor(0)) + 0.1;
        for (int s = 0; s < slotsLin && count < falls.Length; s++)
        {
            if (!Slot(g.Seed + 451, s, t, lifeMaxLin * 1.05, lifeMaxLin * 1.6, chanceLin, lifeMaxLin, out double age, out uint h)) continue;
            double u1 = U01(h); h = Next(h); double u2 = U01(h); h = Next(h); double u3 = U01(h); h = Next(h); double u4 = U01(h); h = Next(h);
            int seg = g.Segment[(int)(h % (uint)g.Count)];
            int len = g.SegmentLength[seg];
            double v = (8 + 4 * u1) * sf;
            double start, travel;
            if (len <= 10) { start = -1; travel = len + 3; }
            else { start = u4 * len * 0.6; travel = 5 + 4 * u3; }
            double life = travel / v;
            if (age >= life) continue;
            double env = Smooth(0, 0.06, age) * (len <= 10 ? 1 : 1 - Smooth(0.65, 1, age / life));
            falls[count++] = new Fall { Head = start + v * age, Amp = 0.5 + 0.45 * u2, Trail = 0.9 + 0.8 * u3, Env = env, Seg = seg, Bottom = len, Width = -1 };
        }
        return count;
    }

    // ---- drips: gather, hang, let go, accelerate, splash --------------------------------------------------------------
    /// <summary>Drip timing at a speed setting: seconds to gather a bead, and gravity (device units per second squared).</summary>
    public static (double Gather, double Hang) DripTiming(double speed) => (2.6 - 1.6 * Math.Clamp(speed, 0, 1), 0.55 - 0.3 * Math.Clamp(speed, 0, 1));

    static int CollectDrips(LedGeometry g, double t, double speed, double density, Span<Fall> falls)
    {
        if (g.Count == 0) return 0;
        var (gather, hang) = DripTiming(speed);
        double gscale = 0.75 + 0.6 * Math.Clamp(speed, 0, 1);
        int count = 0;
        // distance to fall, gravity and drip points per device type
        double top, bottom, grav, width, lifeMax;
        int slots;
        if (g.Case)
        {
            top = g.Ring ? 0.08 : 0.11; bottom = g.Ring ? 0.92 : 0.99; grav = 1.9 * gscale; width = 0.06;   // the bead hangs at the top fan's top LEDs (ring: its top LED)
            slots = 1 + (int)Math.Round(3 * density);
        }
        else if (g.Surface)
        {
            top = 0; bottom = g.Height; grav = 0.9 * gscale; width = 0.022;   // the key surface starts at y = 0 (top row)
            slots = 2 + (int)Math.Round(9 * density);
        }
        else
        {
            top = 0; bottom = 1; grav = 4.5 * gscale; width = -1;   // linear: in run fractions (0 = first LED)
            slots = Math.Clamp((int)Math.Round(g.SegmentLength.Length * (0.6 + 1.2 * density)), 1, 12);
        }
        double fallTime = Math.Sqrt(2 * (bottom - top) / grav);
        lifeMax = 2.6 + 0.55 + fallTime / 0.75 + 0.5;
        for (int s = 0; s < slots && count < falls.Length; s++)
        {
            if (!Slot(g.Seed + 501, s, t, lifeMax * 1.1, lifeMax * 1.9, 0.55 + 0.45 * density, lifeMax, out double age, out uint h)) continue;
            double u1 = U01(h); h = Next(h); double u2 = U01(h); h = Next(h); double u3 = U01(h); h = Next(h);
            double ga = gather * (0.8 + 0.4 * u1), hg = hang * (0.7 + 0.6 * u3);
            double tf = Math.Sqrt(2 * (bottom - top) / grav);
            double life = ga + hg + tf + 0.45;
            if (age >= life) continue;
            var f = new Fall { Amp = 0.6 + 0.4 * u2, Top = top, Bottom = bottom, Width = width, Trail = width > 0 ? width * 1.6 : 0.6 };
            int led = (int)(h % (uint)g.Count);
            if (g.Case)
            {
                bool rear = g.HasRearFan && (U01(Next(h)) < 0.25 || !g.HasFrontFan);
                f.X = (rear ? 0.82 : 0.18) + (g.Ring ? 0 : (U01(Next(Next(h))) - 0.5) * 0.12);
                if (g.Ring) f.Side = U01(Next(Next(h))) < 0.5 ? -1 : 1;
                if (rear) { f.Bottom = 0.30; tf = Math.Sqrt(2 * (f.Bottom - top) / grav); life = ga + hg + tf + 0.45; if (age >= life) continue; }
            }
            else if (g.Surface) f.X = g.X[led];
            else f.Seg = g.Segment[led];
            if (age < ga + hg)
            {
                // the bead grows and brightens, then hangs with a slight wobble
                double grow = Smoother(Math.Min(1, age / ga));
                double wob = age > ga ? 0.04 * Math.Sin((age - ga) * 18) : 0;
                f.Bead = 0.25 + 0.75 * grow + wob; f.Head = top; f.Env = 1;
            }
            else if (age < ga + hg + tf)
            {
                double ft = age - ga - hg;
                f.Head = top + 0.5 * grav * ft * ft; f.Env = 1; f.Bead = -1;
            }
            else { f.Head = f.Bottom; f.Splash = Math.Exp(-(age - ga - hg - tf) / 0.22); f.Env = 1; f.Bead = -1; }
            falls[count++] = f;
        }
        return count;
    }

    /// <summary>Light from falling drops (drop) and landing splashes (splash) at LED i.</summary>
    static void FallLight(LedGeometry g, int i, ReadOnlySpan<Fall> falls, bool drip, ref double drop, ref double splash)
    {
        for (int d = 0; d < falls.Length; d++)
        {
            ref readonly var f = ref falls[d];
            if (f.Width > 0)
            {
                double dx = g.X[i] - (f.Side != 0 && f.Bead < 0 && f.Splash <= 0 ? RimX(f.Head, f.Side) : f.X);
                double wx = Math.Exp(-Sq(dx / f.Width));
                if (wx < 1e-3 && f.Splash <= 0) continue;
                if (drip && f.Bead >= 0)
                {
                    // hanging bead: a point at the drip's top that swells
                    double wy0 = Math.Exp(-Sq((g.Y[i] - f.Top) / (f.Width * 1.4)));
                    drop += f.Amp * f.Bead * wx * wy0;
                    continue;
                }
                if (f.Splash > 0)
                {
                    // landed: a short flash spreading a little sideways along the bottom (case: the logo and the bottom fan)
                    double sw = f.Width * (2.2 + 1.5 * (1 - f.Splash));
                    double near = Math.Exp(-Sq(dx / sw)) * Math.Exp(-Sq((f.Bottom - g.Y[i]) / (f.Width * 1.3)));
                    splash += (drip ? 0.9 : 0.6) * f.Amp * f.Splash * near;
                    continue;
                }
                double dy = f.Head - g.Y[i];
                double ahead = f.Width * 0.9;
                double wy = dy < -ahead ? 0 : dy < 0 ? Sq(1 + dy / ahead) : Math.Exp(-dy / f.Trail);
                drop += f.Amp * f.Env * wx * wy;
            }
            else
            {
                if (g.Segment[i] != f.Seg) continue;
                int len = g.SegmentLength[f.Seg];
                double p = g.X[i] * Math.Max(1, len - 1);
                if (drip)
                {
                    double span = Math.Max(1, len - 1);
                    if (f.Bead >= 0) { drop += f.Amp * f.Bead * Math.Exp(-Sq(p / 0.6)); continue; }
                    double head = f.Head * span;   // run fractions -> LEDs
                    if (f.Splash > 0) { splash += 0.7 * f.Amp * f.Splash * Math.Exp(-Sq((span - p) / 1.2)); continue; }
                    double dl = head - p;
                    drop += f.Amp * (dl < 0 ? Math.Exp(-Sq(dl / 0.5)) : Math.Exp(-dl / f.Trail));
                    continue;
                }
                double dlin = f.Head - p;
                drop += f.Amp * f.Env * (dlin < 0 ? Math.Exp(-Sq(dlin / 0.5)) : Math.Exp(-dlin / f.Trail));
            }
        }
    }

    // ---- rain ripples: a touch where a drop lands, then a ring spreading out ----------------------------------------
    struct Ring { public double X, Y, Radius, Amp, Core, Width; public int Seg; }

    static int CollectRings(LedGeometry g, double t, double speed, double density, Span<Ring> rings)
    {
        if (g.Count == 0) return 0;
        double tempo = 0.75 + 0.6 * Math.Clamp(speed, 0, 1);
        int count = 0;
        double life, v, width; int slots;
        if (g.Case) { life = 2.2; v = 0.42; width = 0.07; slots = 2 + (int)Math.Round(5 * density); }
        else if (g.Surface) { life = 2.4; v = 0.17; width = 0.02; slots = 3 + (int)Math.Round(10 * density); }
        else { life = 1.6; v = 5.5; width = 0.7; slots = Math.Clamp((int)Math.Round(g.Count / 27.0 * (3 + 9 * density)) + g.SegmentLength.Length - 1, 1, MaxSlots); }
        double lifeMax = life / 0.75;
        for (int s = 0; s < slots && count < rings.Length; s++)
        {
            if (!Slot(g.Seed + 601, s, t, lifeMax * 1.1, lifeMax * 1.8, 0.5 + 0.5 * density, lifeMax, out double age, out uint h)) continue;
            double l = life / tempo;
            if (age >= l) continue;
            double u1 = U01(h); h = Next(h); double u2 = U01(h); h = Next(h); double u3 = U01(h); h = Next(h);
            int led = (int)(h % (uint)g.Count);
            double p = age / l;
            var r = new Ring
            {
                Radius = v * tempo * (0.85 + 0.3 * u2) * age, Width = width,
                Amp = (0.45 + 0.3 * u1) * Math.Pow(1 - p, 1.6) * Smooth(0, 0.08, age),
                Core = (0.6 + 0.4 * u3) * Math.Exp(-age / 0.18)
            };
            if (g.Surface || g.Case) { r.X = g.X[led]; r.Y = g.Y[led]; }
            else { r.Seg = g.Segment[led]; r.X = g.X[led] * Math.Max(1, g.SegmentLength[r.Seg] - 1); }
            rings[count++] = r;
        }
        return count;
    }

    static double RingLight(LedGeometry g, int i, ReadOnlySpan<Ring> rings)
    {
        double sum = 0;
        for (int r = 0; r < rings.Length; r++)
        {
            ref readonly var rg = ref rings[r];
            double d;
            if (g.Surface || g.Case) d = Math.Sqrt(Sq(g.X[i] - rg.X) + Sq(g.Y[i] - rg.Y));
            else
            {
                if (g.Segment[i] != rg.Seg) continue;
                d = Math.Abs(g.X[i] * Math.Max(1, g.SegmentLength[rg.Seg] - 1) - rg.X);
            }
            sum += rg.Amp * (Math.Exp(-Sq((d - rg.Radius) / rg.Width)) + 0.3 * Math.Exp(-Sq((d - rg.Radius * 0.6) / rg.Width)))
                 + rg.Core * Math.Exp(-Sq(d / (rg.Width * 1.2)));
        }
        return sum;
    }
}
