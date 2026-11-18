using HwSuite.Core;
using HwSuite.Lighting.Devices;

namespace HwSuite.Lighting;

/// <summary>
/// 1.20 effects made for the per-key AL80, rendered on every device in step:
/// <b>Embers</b> (fire rising from the bottom: the space-bar row, the bottom of the fan stack, the bottom of each memory
/// stick; sparks drift up and cool), <b>Radar</b> (one sweep shared by all devices: around the keyboard's centre, around
/// every fan ring, down each memory stick, a ping on single lights; contacts light as the beam passes) and
/// <b>Typing Ripples</b> (rings spread from the keys pressed, <see cref="KeyActivity"/>; other devices pulse).
/// Primary colour = flames / beam / ripples, secondary = embers / screen / background. Speed, brightness, density
/// (sparks / contacts / ripple size) and glow (resting level) as for the other scenes.
/// </summary>
public static partial class Atmosphere
{
    /// <summary>Where an LED sits on its own device: across 0..1, height 0 (top) .. 1 (bottom). Single lights: middle.</summary>
    static (double u, double h) LocalUH(LedGeometry g, int i)
    {
        if (g.Surface) return (g.Width > 0 ? g.X[i] / g.Width : 0.5, g.Height > 0 ? g.Y[i] / g.Height : 0.5);
        if (g.Case) return (g.X[i], g.Y[i]);
        int runs = g.SegmentLength.Length;
        return (runs > 1 ? g.Segment[i] / (double)(runs - 1) : 0.5, g.X[i]);   // memory: LED 0 at the top of each stick
    }

    static void RenderMore(EffectKind kind, EffectContext c, LedGeometry g, Span<Rgb> frame, Col a, Col b, double k, double t, double speed, double density, double rest)
    {
        switch (kind)
        {
            case EffectKind.Embers: RenderEmbers(c, g, frame, a, b, k, t, speed, density, rest); break;
            case EffectKind.Radar: RenderRadar(g, frame, a, b, k, t, speed, density, rest); break;
            default: RenderTyping(g, frame, a, b, k, speed, density, rest); break;
        }
    }

    // ---------------------------------------------------------------------------------------------- Embers
    struct Spark { public double U, H, Heat, R; }

    static void RenderEmbers(EffectContext c, LedGeometry g, Span<Rgb> frame, Col a, Col b, double k, double t, double speed, double density, double rest)
    {
        int n = frame.Length;
        double rate = 0.6 + 1.6 * speed;                         // flicker and rise speed
        var hot = Col.Lerp(a, new Col(255, 205, 90), 0.6);       // the hottest core and newborn sparks: glowing yellow, not white
        Span<Spark> sparks = stackalloc Spark[MaxSlots];
        int sc = CollectSparks(g, t, rate, density, sparks);
        bool single = !g.Surface && !g.Case && g.SegmentLength.All(l => l <= 2);
        for (int i = 0; i < n; i++)
        {
            var (u, h) = LocalUH(g, i);
            if (single)
            {
                // mouse, board, logo light: a hearth flicker
                double f = 0.45 + 0.35 * Noise1(t * 2.6 * rate + i * 3.1, g.Seed + 7) + 0.2 * Noise1(t * 7.3 * rate + i, g.Seed + 8);
                frame[i] = Col.Lerp(b * Math.Max(rest, 0.5), Col.Lerp(a, hot, Smooth(0.75, 1, f) * 0.6), f).ToRgb(k);
                continue;
            }
            // flame tongues: how high the fire reaches here right now (0 = only the bottom, 1 = the top)
            // tongues mostly reach the lower two or three rows and now and then leap higher
            double n1 = Noise1(u * 2.4 + t * 0.5 * rate, g.Seed + 11), n2 = Noise1(u * 7.0 - t * 1.7 * rate, g.Seed + 12);
            double reach = 0.10 + 0.30 * n1 * n1 + 0.22 * n2 + 0.10 * Noise1(t * 4.7 * rate + u * 17, g.Seed + 13);
            double fromBottom = 1 - h;
            double flame = Smooth(reach + 0.08, reach - 0.12, fromBottom);   // 1 below the tongue tip, 0 above it
            double flicker = 0.7 + 0.3 * Noise1(t * 6.1 * rate + u * 9 + h * 4, g.Seed + 14);
            double heat = Math.Clamp(flame * flicker * (0.35 + 0.65 * h * h), 0, 1);
            var col = Col.Lerp(b * rest, b, Smooth(0, 0.3, heat));          // dark embers everywhere, deep glow low down
            col = Col.Lerp(col, a, Smooth(0.3, 0.85, heat));
            col = Col.Lerp(col, hot, Smooth(0.85, 1, heat) * 0.8);
            // sparks
            double sp = 0, spHeat = 0;
            for (int s = 0; s < sc; s++)
            {
                double du = (u - sparks[s].U) * (g.Surface ? Math.Max(1, g.Width / Math.Max(1e-6, g.Height)) * 0.3 : 1);
                double dh = h - sparks[s].H;
                double d2 = du * du / Sq(sparks[s].R) + (dh < 0 ? dh * dh / Sq(sparks[s].R) : dh * dh / Sq(sparks[s].R * 2.2));   // short trail below
                double l = Math.Exp(-d2) * sparks[s].Heat;
                if (l > sp) { sp = l; spHeat = sparks[s].Heat; }
            }
            if (sp > 0.02) col = Col.Lerp(col, Col.Lerp(a, hot, spHeat), Math.Min(1, sp * 2.4));   // a spark glows: flame colour to yellow as it is born hot
            frame[i] = col.ToRgb(k);
        }
    }

    static int CollectSparks(LedGeometry g, double t, double rate, double density, Span<Spark> into)
    {
        int slots = Math.Min(into.Length, 2 + (int)Math.Round(12 * density));
        double life = 2.6 / rate + 0.6;
        int count = 0;
        for (int s = 0; s < slots && count < into.Length; s++)
        {
            if (!Slot(g.Seed + 501, s, t, life * 1.1, life * 2.4, 0.55 + 0.45 * density, life, out double age, out uint hh)) continue;
            double u0 = U01(hh); hh = Next(hh); double v = 0.75 + 0.5 * U01(hh); hh = Next(hh); double wob = U01(hh);
            if (g.Case)
            {
                // rise through the front fan stack (the column at x ~0.18), sometimes past the rear fan
                u0 = (wob < 0.8 ? 0.18 : 0.82) + (u0 - 0.5) * 0.14;
            }
            double p = age / life;                                            // 0 born at the bottom .. 1 gone at the top
            double hgt = 1.05 - p * 1.15 * v;
            if (hgt < -0.1) continue;
            into[count++] = new Spark
            {
                U = u0 + 0.04 * Math.Sin(age * 3.1 + wob * 6.28),
                H = hgt,
                Heat = Smooth(0, 0.08, age) * (1 - Smooth(0.6, 1, p)),
                R = g.Surface ? 0.06 : g.Case ? 0.06 : 0.09
            };
        }
        return count;
    }

    // ---------------------------------------------------------------------------------------------- Radar
    static void RenderRadar(LedGeometry g, Span<Rgb> frame, Col a, Col b, double k, double t, double speed, double density, double rest)
    {
        int n = frame.Length;
        double period = 4.6 - 3.2 * speed;                         // seconds per turn
        double theta = (t / period) % 1.0 * 2 * Math.PI;
        const double tail = 1.1;                                    // radians of afterglow
        var head = Col.Lerp(a, new Col(235, 255, 240), 0.35);
        var screen = b * Math.Max(rest, 0.12);
        // contacts: fixed for a few turns each, light when the beam passes and fade over most of a turn
        Span<(double x, double y, double ang)> contacts = stackalloc (double, double, double)[MaxSlots];
        int cc = 0;
        double cx = g.Width / 2, cy = g.Height / 2;
        if (g.Surface)
        {
            int slots = Math.Min(MaxSlots, 1 + (int)Math.Round(8 * density));
            for (int s = 0; s < slots; s++)
            {
                if (!Slot(g.Seed + 601, s, t, period * 3, period * 7, 0.5 + 0.5 * density, period * 4, out _, out uint hh)) continue;
                double x = U01(hh) * g.Width; hh = Next(hh); double y = U01(hh) * g.Height;
                contacts[cc++] = (x, y, Math.Atan2(y - cy, x - cx));
            }
        }
        for (int i = 0; i < n; i++)
        {
            double ang, ring = 0;
            bool ping = false;
            if (g.Surface)
            {
                double dx = g.X[i] - cx, dy = g.Y[i] - cy;
                ang = Math.Atan2(dy, dx);
                double r = Math.Sqrt(dx * dx + dy * dy) / Math.Max(1e-6, Math.Max(cx, cy));
                ring = 0.18 * (Math.Exp(-Sq((r - 0.36) / 0.05)) + Math.Exp(-Sq((r - 0.72) / 0.05)));
            }
            else if (g.Case && g.Part.Length > i && g.Part[i] >= 0)
            {
                int p = g.Part[i];
                if (g.PartKinds.Length > p && g.PartKinds[p] == CasePartKind.Logo) { ang = 0; ping = true; }
                else ang = Math.Atan2(g.Y[i] - g.PartY[p], g.X[i] - g.PartX[p]);
            }
            else if (!g.Surface && !g.Case && g.SegmentLength.Any(l => l > 2)) ang = LocalUH(g, i).h * 2 * Math.PI - Math.PI;   // memory: down the stick each turn
            else { ang = 0; ping = true; }

            double behind = ((theta - ang) % (2 * Math.PI) + 4 * Math.PI) % (2 * Math.PI);   // radians since the beam passed
            double glow = ping ? Math.Exp(-behind / 1.6) : Math.Exp(-behind / tail);
            double beam = ping ? 0 : Math.Exp(-Sq(behind / 0.12));
            var col = Col.Lerp(screen, a, Math.Max(glow * 0.85, ring));
            col = Col.Lerp(col, head, beam * 0.8);
            if (g.Surface)
            {
                for (int s = 0; s < cc; s++)
                {
                    double d = Math.Sqrt(Sq(g.X[i] - contacts[s].x) + Sq(g.Y[i] - contacts[s].y));
                    if (d > 0.06) continue;
                    double since = ((theta - contacts[s].ang) % (2 * Math.PI) + 4 * Math.PI) % (2 * Math.PI);
                    double blip = Math.Exp(-since / 4.0) * (1 - d / 0.06);
                    col = Col.Lerp(col, head, Math.Min(1, blip * 1.1));
                }
            }
            frame[i] = col.ToRgb(k);
        }
    }

    // ---------------------------------------------------------------------------------------------- Typing Ripples
    static readonly Dictionary<int, Dictionary<string, int>> KeyIndex = new();

    static int IndexOfKey(int count, string key)
    {
        lock (KeyIndex)
        {
            if (!KeyIndex.TryGetValue(count, out var map))
            {
                map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var keys = KeyboardKeyMaps.For(count);
                if (keys != null) for (int i = 0; i < keys.Length; i++) if (keys[i].HasKey) map.TryAdd(keys[i].Name, i);
                KeyIndex[count] = map;
            }
            foreach (var name in key.Split('|')) if (map.TryGetValue(name, out var idx)) return idx;   // "Del|Delete": AL80 then i87 spelling
            return -1;
        }
    }

    [ThreadStatic] static (double age, string key)[]? _typingBuf;

    static void RenderTyping(LedGeometry g, Span<Rgb> frame, Col a, Col b, double k, double speed, double density, double rest)
    {
        int n = frame.Length;
        var buf = _typingBuf ??= new (double, string)[KeyActivity.Capacity];   // per render thread, no allocation per frame
        int en = KeyActivity.Recent(buf);
        double v = 0.45 + 0.9 * speed;                 // keyboard widths per second
        double width = 0.025 + 0.05 * density;          // ring thickness
        double fade = 0.55 + 0.9 * density;             // seconds a ring lives (bigger ripples travel further)
        var flashCol = Col.Lerp(a, new Col(255, 255, 255), 0.45);
        double pulse = 0;
        for (int e = 0; e < en; e++) pulse += Math.Exp(-buf[e].Item1 / 0.45);
        pulse = Math.Min(1, pulse * 0.7);
        bool single = !g.Surface && !g.Case && g.SegmentLength.All(l => l <= 2);
        for (int i = 0; i < n; i++)
        {
            var bg = b * Math.Max(rest, 0.08);
            double light = 0, flash = 0;
            if (g.Surface)
            {
                for (int e = 0; e < en; e++)
                {
                    int ki = IndexOfKey(g.Count, buf[e].Item2);
                    double age = buf[e].Item1;
                    double kx, ky;
                    if (ki >= 0 && ki < g.Count) { kx = g.X[ki]; ky = g.Y[ki]; }
                    else { kx = g.Width / 2; ky = g.Height / 2; }   // a key this map does not have: from the centre
                    double d = Math.Sqrt(Sq(g.X[i] - kx) + Sq(g.Y[i] - ky));
                    double life = Math.Exp(-age / fade);
                    light = Math.Max(light, Math.Exp(-Sq((d - age * v) / width)) * life);
                    if (ki == i) flash = Math.Max(flash, Math.Exp(-age / 0.3));
                }
            }
            else if (g.Case || single)
            {
                // fans, logo, mouse, board: a pulse with every press; fan rings sparkle a little around the rim
                light = pulse * (g.Case ? 0.75 + 0.25 * Noise1(g.X[i] * 9 + g.Y[i] * 7 + KeyActivity.Now * 2, g.Seed + 31) : 1);
            }
            else
            {
                // memory: each press sends a pulse down every stick
                double h = LocalUH(g, i).h;
                for (int e = 0; e < en; e++)
                {
                    double age = buf[e].Item1;
                    light = Math.Max(light, Math.Exp(-Sq((h - age * 1.6) / 0.14)) * Math.Exp(-age / 1.1));
                }
            }
            var col = Col.Lerp(bg, a, Math.Min(1, light));
            if (flash > 0) col = Col.Lerp(col, flashCol, flash);
            frame[i] = col.ToRgb(k);
        }
    }
}
