using HwSuite.Core;
using HwSuite.Lighting.Devices;

namespace HwSuite.Lighting;

/// <summary>
/// Where one device's LEDs are, for the effects. Two coordinate systems are kept apart:
/// - <see cref="Line"/>: setup-wide position (phase units on the shared line, the owner's arrangement applied). Broad,
///   setup-wide features use it: mist, canopy light, storm clouds and lightning, the travelling reflection.
/// - <see cref="X"/>/<see cref="Y"/>: device-local. The keyboard is a surface (its verified KB.ini key centres in units of
///   the keyboard's width, turned with the keyboard so Y points down the arrangement); every other output is linear (X =
///   position along its own LED run in index order, 0..1; Y = 0). Rain, fireflies and ripples use these, so they stay
///   on one device and respect its real key positions and gaps. Linear outputs never pretend to be a 2-D surface.
/// - 1.15, <see cref="Case"/>: the D_LED chain as the owner's case (<see cref="CaseLayout"/>): each fan's LEDs on a ring
///   around its centre (drawn halfway in, so a fan whose LEDs light its blades from the hub reads the same), the front
///   fans stacked, the rear fan at the top of the back, the logo along the bottom front. Case units: 0..1 across, 0..1 down.
/// Immutable; built once per layout change (no allocation per frame).
/// </summary>
public sealed class LedGeometry
{
    public double[] Line { get; }
    public double[] X { get; }
    public double[] Y { get; }
    /// <summary>Linear: which independent LED run each LED belongs to (one per memory controller). Surface: 0.</summary>
    public int[] Segment { get; }
    /// <summary>Linear: LEDs in each run.</summary>
    public int[] SegmentLength { get; }
    /// <summary>The keyboard's key map (two-dimensional). False for every other output.</summary>
    public bool Surface { get; }
    /// <summary>Surface extent (units of keyboard width) after its turn. Linear: 1 x 0.</summary>
    public double Width { get; }
    public double Height { get; }
    /// <summary>Per-device seed: each device gets its own deterministic drops, fireflies and ripples.</summary>
    public uint Seed { get; }
    public int Rotation { get; }
    public int Count => Line.Length;
    /// <summary>The case layout (D_LED chain mapped to fans and logo). Not a keyboard <see cref="Surface"/>.</summary>
    public bool Case { get; init; }
    /// <summary>Case: which part each LED belongs to (-1: past the layout); the parts' kinds, centres and radii.</summary>
    public int[] Part { get; init; } = Array.Empty<int>();
    public CasePartKind[] PartKinds { get; init; } = Array.Empty<CasePartKind>();
    public double[] PartX { get; init; } = Array.Empty<double>();
    public double[] PartY { get; init; } = Array.Empty<double>();
    public bool HasFrontFan { get; init; }
    public bool HasRearFan { get; init; }
    /// <summary>Case on a splitter hub: one ring (centre 0.18, 0.5; half-width 0.1, half-height 0.42) that every fan shows.</summary>
    public bool Ring { get; init; }
    internal double[]? Source { get; init; }

    LedGeometry(double[] line, double[] x, double[] y, int[] seg, int[] segLen, bool surface, double w, double h, uint seed, int rotation)
    { Line = line; X = x; Y = y; Segment = seg; SegmentLength = segLen; Surface = surface; Width = w; Height = h; Seed = seed; Rotation = rotation; }

    /// <summary>Linear runs in index order. <paramref name="reverse"/>: travel from the last LED of each run to the first.</summary>
    public static LedGeometry Linear(double[] line, IReadOnlyList<int>? segments, uint seed, bool reverse = false, double[]? source = null)
    {
        int n = line.Length;
        var lens = segments != null && segments.Sum() == n && segments.All(s => s > 0) ? segments.ToArray() : new[] { n };
        var x = new double[n]; var y = new double[n]; var seg = new int[n];
        for (int s = 0, o = 0; s < lens.Length; o += lens[s], s++)
            for (int i = 0; i < lens[s]; i++)
            {
                double u = lens[s] == 1 ? 0.5 : (double)i / (lens[s] - 1);
                x[o + i] = reverse ? 1 - u : u; seg[o + i] = s;
            }
        return new LedGeometry(line, x, y, seg, lens, false, 1, 0, seed, 0) { Source = source };
    }

    /// <summary>
    /// The keyboard's verified key centres, turned clockwise by <paramref name="rotation"/> like its drawing, so "down"
    /// is down the arrangement (with no turn: from the function row towards the space bar).
    /// </summary>
    public static LedGeometry Keyboard(double[] line, int rotation, uint seed, double[]? source = null)
    {
        var keys = KeyboardKeyMaps.For(line.Length) ?? KeyboardKeyMaps.For(ILovbeeKeyMap.Keys.Length)!;
        int n = keys.Length;
        double minX = keys.Min(k => k.X), maxX = keys.Max(k => k.X), minY = keys.Min(k => k.Y), maxY = keys.Max(k => k.Y);
        double w = Math.Max(1, maxX - minX);
        var x = new double[n]; var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double u = (keys[i].X - minX) / w, v = (keys[i].Y - minY) / w;
            (x[i], y[i]) = rotation switch { 90 => (-v, u), 180 => (-u, -v), 270 => (v, -u), _ => (u, v) };
        }
        double x0 = x.Min(), y0 = y.Min();
        for (int i = 0; i < n; i++) { x[i] -= x0; y[i] -= y0; }
        var lineFull = line.Length == n ? line : Enumerable.Range(0, n).Select(i => i < line.Length ? line[i] : 0).ToArray();
        return new LedGeometry(lineFull, x, y, new int[n], new[] { n }, true, x.Max(), y.Max(), seed, rotation) { Source = source };
    }

    /// <summary>The D_LED chain laid out as the case (see <see cref="CaseLayout"/>). LEDs past the layout sit at the case centre, unlit by events.</summary>
    public static LedGeometry CaseFront(double[] line, CaseLayout layout, uint seed, double[]? source = null)
    {
        int n = line.Length;
        var parts = layout.Allocate(n);
        if (layout.Mirrored && parts.Count == 1)
        {
            // one ring that every fan shows: drawn tall, top to bottom of the front column, so rain falls past its LEDs,
            // a drip gathers at its top LED and splashes at its bottom one; LEDs past the ring sit at the centre
            int ring = parts[0].Count;
            var mx = new double[n]; var my = new double[n]; var mp = new int[n];
            Array.Fill(mx, 0.18); Array.Fill(my, 0.5); Array.Fill(mp, -1);
            for (int i = 0; i < ring; i++) { double a = layout.LedAngle(i, ring); mx[i] = 0.18 + 0.1 * Math.Cos(a); my[i] = 0.5 + 0.42 * Math.Sin(a); mp[i] = 0; }
            return new LedGeometry(line, mx, my, new int[n], new[] { n }, false, 1, 1, seed, 0)
            {
                Source = source, Case = true, Ring = true, Part = mp, PartKinds = new[] { CasePartKind.FrontTop }, PartX = new[] { 0.18 }, PartY = new[] { 0.5 }, HasFrontFan = true, HasRearFan = false
            };
        }
        var x = new double[n]; var y = new double[n]; var part = new int[n];
        Array.Fill(part, -1); Array.Fill(x, 0.5); Array.Fill(y, 0.5);
        var kinds = new CasePartKind[parts.Count]; var px = new double[parts.Count]; var py = new double[parts.Count];
        for (int p = 0; p < parts.Count; p++)
        {
            var (kind, start, count) = parts[p];
            var (cx, cy, r) = CaseLayout.Place(kind);
            kinds[p] = kind; px[p] = cx; py[p] = cy;
            for (int i = 0; i < count; i++)
            {
                int led = start + i;
                part[led] = p;
                if (kind == CasePartKind.Logo) { x[led] = cx - 0.1 + 0.2 * (count == 1 ? 0.5 : (double)i / (count - 1)); y[led] = cy; continue; }
                double a = layout.LedAngle(i, count);   // LED TopLed at the top, clockwise unless set otherwise (blended halfway in)
                x[led] = cx + Math.Cos(a) * r * 0.5; y[led] = cy + Math.Sin(a) * r * 0.5;
            }
        }
        var seg = new int[n];
        return new LedGeometry(line, x, y, seg, new[] { n }, false, 1, 1, seed, 0) {
            Source = source, Case = true, Part = part, PartKinds = kinds, PartX = px, PartY = py,
            HasFrontFan = kinds.Any(p => p is CasePartKind.FrontTop or CasePartKind.FrontMiddle or CasePartKind.FrontBottom), HasRearFan = kinds.Contains(CasePartKind.Rear)
        };
    }

    public static uint SeedFor(DeviceRole? role) => role switch
    {
        DeviceRole.Strip => 0x51A1u, DeviceRole.Board => 0xB0A2u, DeviceRole.Ram => 0x2A13u, DeviceRole.Keyboard => 0x4B44u, DeviceRole.Mouse => 0x3055u, _ => 0x7777u
    };
}

/// <summary>
/// The calm look a scene leaves where it cannot be shown (mouse, keyboard without live frames, memory in Onboard mode)
/// and at quit: one onboard setting per change, labelled as an independent companion, never streamed.
/// </summary>
public readonly record struct Companion(bool Breathing, int Color, double Speed, string Text)
{
    public static Companion For(RgbConfig c)
    {
        var a = Rgb.FromInt(c.ColorA); var b = Rgb.FromInt(c.ColorB);
        (bool breathe, double toward, string what) = c.Effect switch
        {
            EffectKind.Rainfall => (false, 0.45, "steady"),
            EffectKind.DistantStorm => (false, 0.35, "steady"),
            EffectKind.Canopy => (false, 0.25, "steady"),
            EffectKind.RollingMist => (true, 0.55, "slow breathing"),
            EffectKind.Fireflies => (true, 1.0, "slow breathing"),
            EffectKind.MoonlitWater => (true, 0.5, "slow breathing"),
            EffectKind.Drip => (false, 0.4, "steady"),
            EffectKind.RainRipples => (true, 0.5, "slow breathing"),
            EffectKind.Downpour => (false, 0.4, "steady"),
            EffectKind.Embers => (true, 0.6, "slow breathing"),
            EffectKind.Radar => (false, 0.35, "steady"),
            EffectKind.TypingRipples => (false, 0.4, "steady"),
            _ => (false, 1.0, "steady")
        };
        var col = Rgb.Lerp(b, a, toward);
        return new(breathe, col.ToInt(), Math.Clamp(c.Speed, 0, 1) * 0.5, $"{what} {col}");
    }
}

/// <summary>
/// Procedural atmosphere scenes. Every value is a pure function of (time, settings, geometry, seed): drops, fireflies,
/// ripples, light shafts and lightning come from a bounded number of event slots whose cycles are hashed, so frames are
/// repeatable, independent of the frame rate, need no state between frames, never reseed abruptly, and a long gap (sleep,
/// a stalled frame) simply lands at a later moment with no catch-up work. No allocation per frame (bounded stackalloc).
/// </summary>
public static partial class Atmosphere
{
    public const int MaxSlots = 24;
    /// <summary>Setup-wide features (mist, shafts, clouds, lightning) share one seed so every device agrees on them.</summary>
    const uint SetupSeed = 0xF0E57u;

    // ---- primitives ---------------------------------------------------------------------------------------------
    static uint Hash(uint a, uint b, uint c)
    {
        uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u) * 0x85EBCA77u ^ (c + 0x165667B1u) * 0xC2B2AE3Du;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return h;
    }
    static uint Next(uint h) { h ^= h << 13; h ^= h >> 17; h ^= h << 5; return h * 0x2545F491u + 0x9E3779B9u; }
    static double U01(uint h) => (h >> 8) / 16777216.0;

    public static double Smooth(double e0, double e1, double x) { double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); }
    static double Smoother(double t) { t = Math.Clamp(t, 0, 1); return t * t * t * (t * (t * 6 - 15) + 10); }

    /// <summary>Smooth value noise, 0..1, no period (hashed lattice).</summary>
    public static double Noise1(double x, uint seed)
    {
        double fl = Math.Floor(x); long i = (long)fl; double f = x - fl;
        double a = U01(Hash(seed, (uint)i, 0x51u)), b = U01(Hash(seed, (uint)(i + 1), 0x51u));
        return a + (b - a) * Smoother(f);
    }

    public static double Noise2(double x, double y, uint seed)
    {
        double fx = Math.Floor(x), fy = Math.Floor(y); long ix = (long)fx, iy = (long)fy; double tx = Smoother(x - fx), ty = Smoother(y - fy);
        double a = U01(Hash(seed, (uint)ix, (uint)iy)), b = U01(Hash(seed, (uint)(ix + 1), (uint)iy));
        double c = U01(Hash(seed, (uint)ix, (uint)(iy + 1))), d = U01(Hash(seed, (uint)(ix + 1), (uint)(iy + 1)));
        return (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty;
    }

    /// <summary>
    /// One bounded event slot. Time is cut into cycles of the slot's own period (hashed, with a hashed phase so slots never
    /// line up); in each cycle an event happens with probability <paramref name="chance"/> at a hashed delay that leaves
    /// room for <paramref name="lifeMax"/>, so a slot's events never overlap. Returns the event's age and its hash.
    /// </summary>
    static bool Slot(uint seed, int slot, double t, double periodMin, double periodMax, double chance, double lifeMax, out double age, out uint h)
    {
        uint hs = Hash(seed, (uint)slot, 0xA5A5u);
        double period = periodMin + (periodMax - periodMin) * U01(hs);
        double tt = t + period * U01(Next(hs)) + 1000 * period;   // phase offset; +1000 periods keeps early times positive
        double cyc = Math.Floor(tt / period);
        double local = tt - cyc * period;
        h = Hash(seed ^ 0x9E37u, (uint)slot, (uint)(long)cyc);
        age = 0;
        if (U01(h) >= chance) return false;
        h = Next(h);
        age = local - Math.Max(0, period - lifeMax) * U01(h);
        h = Next(h);
        return age >= 0;
    }

    readonly struct Col
    {
        public readonly double R, G, B;
        public Col(double r, double g, double b) { R = r; G = g; B = b; }
        public Col(Rgb c) { R = c.R; G = c.G; B = c.B; }
        public static Col operator *(Col c, double k) => new(c.R * k, c.G * k, c.B * k);
        public static Col operator +(Col a, Col b) => new(a.R + b.R, a.G + b.G, a.B + b.B);
        public static Col Lerp(Col a, Col b, double t) { t = Math.Clamp(t, 0, 1); return new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t); }
        public Rgb ToRgb(double k) => new((byte)Math.Clamp(Math.Round(R * k), 0, 255), (byte)Math.Clamp(Math.Round(G * k), 0, 255), (byte)Math.Clamp(Math.Round(B * k), 0, 255));
    }

    static readonly Col Ivory = new(242, 230, 200);
    static readonly Col FlashWhite = new(226, 233, 242);

    /// <summary>How bright the resting tone is: Glow 0..1 -> 6..40 % of the secondary colour.</summary>
    public static double RestLevel(double glow) => 0.06 + 0.34 * Math.Clamp(glow, 0, 1);

    // ---- entry point --------------------------------------------------------------------------------------------
    public static void Render(EffectKind kind, EffectContext c, LedGeometry g, Span<Rgb> frame)
    {
        var a = new Col(c.ColorA); var b = new Col(c.ColorB);
        double k = Math.Clamp(c.Brightness, 0, 1);
        double t = c.Time;
        double speed = Math.Clamp(c.Speed, 0, 1), density = Math.Clamp(c.Density, 0, 1), rest = RestLevel(c.Glow);
        int n = Math.Min(frame.Length, g.Count);
        if (EffectInfo.IsScene3(kind))
        {
            RenderMore(kind, c, g, frame[..n], a, b, k, t, speed, density, rest);
            for (int i = n; i < frame.Length; i++) frame[i] = (b * rest).ToRgb(k);
            return;
        }
        if (EffectInfo.IsNewScene(kind) || (g.Case && kind is EffectKind.Rainfall or EffectKind.DistantStorm))
        {
            RenderRain(kind, c, g, frame[..n], a, b, k, t, speed, density, rest);
            for (int i = n; i < frame.Length; i++) frame[i] = (b * rest).ToRgb(k);
            return;
        }
        switch (kind)
        {
            case EffectKind.Rainfall:
            case EffectKind.DistantStorm:
            {
                bool storm = kind == EffectKind.DistantStorm;
                Span<Drop> drops = stackalloc Drop[MaxSlots];
                int count = CollectDrops(g, t, speed, density, drops);
                double flash = 0, flashAt = 0;
                if (storm && c.Lightning) flash = LightningLevel(t, out flashAt, out _);
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
                    double drop = Math.Min(1, DropLight(g, i, drops[..count]) * (storm ? 0.8 : 1));
                    var col = Col.Lerp(bg, a, drop);
                    if (flash > 0)
                    {
                        double near = 0.55 + 0.45 * Math.Exp(-Sq((line - flashAt) / 0.45));
                        double behind = 0.6 + 0.4 * Noise1(line * 2.2 + 17.0, SetupSeed + 5);   // uneven, as if through cloud
                        col = Col.Lerp(col, Col.Lerp(a, FlashWhite, 0.5), flash * near * behind * 0.5);
                    }
                    frame[i] = col.ToRgb(k);
                }
                break;
            }
            case EffectKind.RollingMist:
            {
                // three layers drifting the same way at different speeds and scales, so the bands slide over each other and
                // never repeat; a soft threshold turns them into separate banks of fog with dark forest between them
                double drift = c.Drift * 0.064;
                double thresh = 0.66 - 0.2 * density;   // about 30 % of the line under fog at the default density
                for (int i = 0; i < n; i++)
                {
                    double x = g.Line[i] + 0.35 * g.Y[i];
                    // banks about one or two devices wide (the whole line is ~1.15), passing a device in a few seconds
                    double fog = 0.55 * Noise1(x * 4.0 - drift * 4.0, SetupSeed + 11) + 0.3 * Noise1(x * 7.5 - drift * 5.2 + 7.3, SetupSeed + 12) + 0.15 * Noise1(x * 1.5 - drift * 1.0 + 3.1, SetupSeed + 13);
                    double m = Smooth(thresh - 0.06, thresh + 0.1, fog);
                    double tone = Noise1(x * 2.0 - drift * 1.5 + 11, SetupSeed + 14);
                    var mist = Col.Lerp(Col.Lerp(b, a, 0.4), a, tone * 0.8);   // grey-teal to silver-blue
                    var bg = b * (rest * 0.85);
                    frame[i] = Col.Lerp(bg, mist, m * 0.55).ToRgb(k);
                }
                break;
            }
            case EffectKind.Fireflies:
            {
                Span<Fly> flies = stackalloc Fly[MaxSlots];
                int count = CollectFlies(g, t, speed, density, flies, a);
                var bg = b * rest;
                for (int i = 0; i < n; i++)
                {
                    double sum = 0; var acc = new Col(0, 0, 0);
                    for (int f = 0; f < count; f++)
                    {
                        ref readonly var fl = ref flies[f];
                        double w;
                        if (g.Surface) w = Math.Exp(-(Sq(g.X[i] - fl.X) + Sq(g.Y[i] - fl.Y)) / (2 * 0.026 * 0.026));
                        else if (g.Case) w = Math.Exp(-(Sq(g.X[i] - fl.X) + Sq(g.Y[i] - fl.Y)) / (2 * 0.075 * 0.075));
                        else
                        {
                            if (g.Segment[i] != fl.Seg) continue;
                            double dl = (g.X[i] - fl.X) * Math.Max(1, g.SegmentLength[fl.Seg] - 1);
                            w = Math.Exp(-Sq(dl / 0.6));
                        }
                        w *= fl.Amp;
                        if (w < 1e-4) continue;
                        sum += w; acc += fl.Color * w;
                    }
                    frame[i] = (sum > 0 ? Col.Lerp(bg, acc * (1 / sum), Math.Min(1, sum)) : bg).ToRgb(k);
                }
                break;
            }
            case EffectKind.MoonlitWater:
            {
                double tempo = 0.7 + 0.6 * speed;
                Span<Ripple> ripples = stackalloc Ripple[8];
                int count = g.Surface || g.Case ? CollectRipples(g, t, tempo, density, ripples) : 0;
                double unit = g.Case ? 3.0 : 1.0;   // case units are about three keyboard units per LED gap
                for (int i = 0; i < n; i++)
                {
                    double line = g.Line[i];
                    double light;
                    Col water;
                    if (g.Surface || g.Case)
                    {
                        water = b * (rest * (0.75 + 0.5 * Noise2(g.X[i] * 5 / unit + c.Drift * 0.04, g.Y[i] * 5 / unit - c.Drift * 0.03, g.Seed + 21)));
                        double ring = 0;
                        for (int r = 0; r < count; r++)
                        {
                            ref readonly var rp = ref ripples[r];
                            double d = Math.Sqrt(Sq(g.X[i] - rp.X) + Sq(g.Y[i] - rp.Y));
                            ring += rp.Amp * (Math.Exp(-Sq((d - rp.Radius * unit) / (0.022 * unit))) + 0.25 * Math.Exp(-Sq((d - (rp.Radius - 0.07) * unit) / (0.02 * unit))));
                        }
                        double glint = Smooth(0.74, 0.95, Noise1((line + 0.4 * g.Y[i]) * 7.0 - c.Drift * 0.18, SetupSeed + 22)) * 0.14;   // a faint moon path sliding across
                        light = Math.Min(1, ring + glint);
                    }
                    else
                    {
                        water = b * (rest * (0.8 + 0.4 * Noise1(line * 1.5 + t * 0.02, SetupSeed + 23)));
                        // a slow travelling reflection: the line position plus each LED's place in its own run, so a glint
                        // slides along the strip or up a memory stick instead of the whole device pulsing at once
                        double xx = line * 3.0 + g.X[i] * 0.9;
                        double v = 0.65 * Noise1(xx - c.Drift * 0.22, SetupSeed + 24) + 0.35 * Noise1(xx * 2.3 + c.Drift * 0.12 + 9, SetupSeed + 25);
                        light = Smooth(0.5, 0.86, v) * 0.75;
                    }
                    frame[i] = Col.Lerp(water, a, light).ToRgb(k);
                }
                break;
            }
            case EffectKind.Canopy:
            {
                double drift = c.Drift * 0.011;
                double body = 0.25 + 0.55 * Math.Clamp(c.Glow, 0, 1);
                var deep = b * body;
                var shadow = new Col(b.R * 0.7, b.G * 0.86, Math.Min(255, b.B * 1.06 + 4)) * (body * 0.72);   // blue-green shadow
                var jade = Col.Lerp(b, a, 0.35) * (body * 1.15);                                              // muted jade
                Span<Shaft> shafts = stackalloc Shaft[6];
                int count = CollectShafts(t, speed, density, shafts);
                for (int i = 0; i < n; i++)
                {
                    double x = g.Line[i] + 0.25 * g.Y[i];
                    double n1 = Noise1(x * 1.6 - drift, SetupSeed + 31), n2 = Noise1(x * 0.8 + drift * 0.7 + 5, SetupSeed + 32);
                    var col = Col.Lerp(Col.Lerp(shadow, deep, Smooth(0.2, 0.8, n2)), jade, Smooth(0.55, 0.9, n1) * 0.6);
                    double light = 0;
                    for (int s = 0; s < count; s++)
                    {
                        double sx = g.Line[i] + 0.5 * g.Y[i];
                        light += shafts[s].Amp * Math.Exp(-Sq((sx - shafts[s].X) / 0.07));
                    }
                    frame[i] = Col.Lerp(col, a, Math.Min(0.6, light)).ToRgb(k);
                }
                break;
            }
        }
        for (int i = n; i < frame.Length; i++) frame[i] = (b * rest).ToRgb(k);
    }

    static double Sq(double x) => x * x;

    // ---- rain -----------------------------------------------------------------------------------------------------
    struct Drop
    {
        public double X, Head, Amp, Trail, Env, Splash; public int Seg; public double Len;
    }

    /// <summary>Fall speed multiplier from the shared speed slider (0..1).</summary>
    public static double RainSpeedFactor(double speed) => 0.55 + 0.9 * Math.Clamp(speed, 0, 1);

    /// <summary>
    /// The drops alive at time t (bounded by <see cref="MaxSlots"/>). Surface: each drop falls from above the top row to
    /// the bottom row of the keyboard in a column picked from a real key, with its own speed, brightness and trail, then
    /// leaves a short splash on the bottom row. Linear: a droplet runs along one LED run in index order with a short trail
    /// (short runs such as a memory stick are crossed end to end; the long case chain gets short runs).
    /// </summary>
    static int CollectDrops(LedGeometry g, double t, double speed, double density, Span<Drop> drops)
    {
        double sf = RainSpeedFactor(speed);
        int count = 0;
        if (g.Count == 0) return 0;
        if (g.Surface)
        {
            const double Top = -0.035, VMin = 0.26, VSpread = 0.14, SplashTime = 0.55;
            double bottom = g.Height, fall = bottom + 0.06 - Top;
            double lifeMax = fall / (VMin * RainSpeedFactor(0)) + SplashTime;
            int slots = Math.Min(MaxSlots, 12 + (int)Math.Round(12 * density));
            double chance = 0.15 + 0.85 * density;
            for (int s = 0; s < slots && count < drops.Length; s++)
            {
                if (!Slot(g.Seed, s, t, lifeMax * 1.1, lifeMax * 1.7, chance, lifeMax, out double age, out uint h)) continue;
                double u1 = U01(h); h = Next(h); double u2 = U01(h); h = Next(h); double u3 = U01(h); h = Next(h); double u4 = U01(h); h = Next(h);
                int key = (int)(h % (uint)g.Count);
                double v = (VMin + VSpread * u1) * sf;
                double land = fall / v;
                if (age >= land + SplashTime) continue;
                drops[count++] = new Drop
                {
                    X = g.X[key] + (u4 - 0.5) * 0.01, Head = Top + v * Math.Min(age, land), Amp = 0.55 + 0.45 * u2, Trail = 0.04 + 0.03 * u3,
                    Env = Smooth(0, 0.12, age), Splash = age > land ? Math.Exp(-(age - land) / 0.18) : 0, Len = bottom
                };
            }
            return count;
        }
        int segs = g.SegmentLength.Length;
        int slotsLin = Math.Clamp((int)Math.Round(g.Count / 27.0 * (5 + 10 * density)), 1, MaxSlots);
        double chanceLin = (0.15 + 0.85 * density) * Math.Min(1, g.Count / 6.0);
        int longest = g.SegmentLength.Max();
        double lifeMaxLin = (Math.Min(longest, 10) + 5) / (5 * RainSpeedFactor(0)) + 0.1;
        for (int s = 0; s < slotsLin && count < drops.Length; s++)
        {
            if (!Slot(g.Seed, s, t, lifeMaxLin * 1.1, lifeMaxLin * 1.8, chanceLin, lifeMaxLin, out double age, out uint h)) continue;
            double u1 = U01(h); h = Next(h); double u2 = U01(h); h = Next(h); double u3 = U01(h); h = Next(h); double u4 = U01(h); h = Next(h);
            int seg = g.Segment[(int)(h % (uint)g.Count)];
            int len = g.SegmentLength[seg];
            double v = (5 + 3 * u1) * sf;   // LEDs per second
            double start, travel;
            if (len <= 10) { start = -1; travel = len + 3; }
            else { start = u4 * len * 0.55; travel = 6 + 5 * u3; }
            double life = travel / v;
            if (age >= life) continue;
            double env = Smooth(0, 0.08, age) * (len <= 10 ? 1 : 1 - Smooth(0.65, 1, age / life));
            drops[count++] = new Drop { X = 0, Head = start + v * age, Amp = 0.55 + 0.45 * u2, Trail = 1.2 + 1.0 * u3, Env = env, Seg = seg, Len = len };
        }
        return count;
    }

    static double DropLight(LedGeometry g, int i, ReadOnlySpan<Drop> drops)
    {
        double sum = 0;
        for (int d = 0; d < drops.Length; d++)
        {
            ref readonly var dr = ref drops[d];
            if (g.Surface)
            {
                double dx = g.X[i] - dr.X;
                if (Math.Abs(dx) > 0.16) continue;
                double wx = Math.Exp(-Sq(dx / 0.022)), fall = 0;
                if (wx > 1e-3)
                {
                    double dy = dr.Head - g.Y[i];   // > 0: the head has passed this key (trail); < 0: just ahead of it
                    double wy = dy < -0.025 ? 0 : dy < 0 ? Sq(1 + dy / 0.025) : Math.Exp(-dy / dr.Trail);
                    fall = wx * wy * (dr.Splash > 0 ? dr.Splash : 1);
                }
                // landed: a short, faint splash on the bottom row around the drop's column
                double splash = dr.Splash > 0 ? 0.4 * dr.Splash * Math.Exp(-Sq(dx / 0.05)) * Math.Exp(-Sq((dr.Len - g.Y[i]) / 0.025)) : 0;
                sum += dr.Amp * dr.Env * (fall + splash);
            }
            else
            {
                if (g.Segment[i] != dr.Seg) continue;
                double p = g.X[i] * Math.Max(1, dr.Len - 1);
                double dl = dr.Head - p;
                double w = dl < 0 ? Math.Exp(-Sq(dl / 0.55)) : Math.Exp(-dl / dr.Trail);
                sum += dr.Amp * dr.Env * w;
            }
        }
        return sum;
    }

    // ---- lightning (Distant Storm only) ---------------------------------------------------------------------------
    /// <summary>Lightning windows: at most one soft flash per window, starting in its first 40 %, so flashes are at least 24 s apart (about 50 an hour).</summary>
    public const double LightningWindow = 40, LightningChance = 0.55, FlashRise = 0.45, FlashDecay = 1.1, FlashLength = 5.0;

    /// <summary>
    /// Soft illumination behind the clouds, 0..1: a gentle rise over <see cref="FlashRise"/> s and a slow decay. Never a
    /// repeated flash or strobe. Rare: on average one every ~70 s with long quiet intervals. Shared by all devices.
    /// </summary>
    public static double LightningLevel(double t, out double centre, out uint h)
    {
        double w = Math.Floor(t / LightningWindow);
        h = Hash(SetupSeed + 41, (uint)(long)w, 7);
        centre = 0;
        if (U01(h) >= LightningChance) return 0;
        h = Next(h);
        double start = w * LightningWindow + U01(h) * LightningWindow * 0.4;
        double age = t - start;
        if (age < 0 || age > FlashLength) return 0;
        h = Next(h); double amp = 0.7 + 0.3 * U01(h);
        h = Next(h); centre = -0.1 + 1.35 * U01(h);
        double rise = Smooth(0, FlashRise, age);
        double decay = age <= FlashRise ? 1 : Math.Exp(-(age - FlashRise) / FlashDecay);
        double tail = 1 - Smooth(FlashLength - 1.0, FlashLength, age);   // fades to exactly 0 before the window moves on
        return amp * rise * decay * tail;
    }

    // ---- fireflies ------------------------------------------------------------------------------------------------
    struct Fly { public double X, Y, Amp; public int Seg; public Col Color; }

    static int CollectFlies(LedGeometry g, double t, double speed, double density, Span<Fly> flies, Col a)
    {
        if (g.Count == 0) return 0;
        double tempo = 0.75 + 0.5 * speed;
        const double FadeIn = 1.2, HoldMax = 1.5, FadeOut = 1.7;
        double lifeMax = (FadeIn + HoldMax + FadeOut) / 0.75;
        int slots = Math.Clamp((int)Math.Round(g.Count * 0.09 * (0.4 + 1.4 * density)), 1, 16);
        double chance = 0.75 * Math.Min(1, g.Count / 8.0);
        var warm = Col.Lerp(a, Ivory, 0.5);
        int count = 0;
        for (int s = 0; s < slots && count < flies.Length; s++)
        {
            if (!Slot(g.Seed + 101, s, t, lifeMax * 1.05, lifeMax * 1.6, chance, lifeMax, out double age, out uint h)) continue;
            double u1 = U01(h); h = Next(h); double u2 = U01(h); h = Next(h); double u3 = U01(h); h = Next(h); double u4 = U01(h); h = Next(h); double u5 = U01(h); h = Next(h);
            double fi = FadeIn / tempo, hold = (0.5 + 1.0 * u1) / tempo, fo = FadeOut / tempo, life = fi + hold + fo;
            if (age >= life) continue;
            double env = age < fi ? Smoother(age / fi) : age < fi + hold ? 1 : 1 - Smoother((age - fi - hold) / fo);
            int led = (int)(h % (uint)g.Count);
            double p = age / life;
            var fly = new Fly { Amp = env * (0.5 + 0.4 * u2), Color = u3 < 0.3 ? warm : a };
            if (g.Surface) { fly.X = g.X[led] + (u4 - 0.5) * 0.04 * p; fly.Y = g.Y[led] + (u5 - 0.5) * 0.03 * p; }
            else if (g.Case) { fly.X = g.X[led] + (u4 - 0.5) * 0.35 * p; fly.Y = g.Y[led] + (u5 - 0.5) * 0.35 * p; }   // drifts toward a neighbouring fan
            else
            {
                fly.Seg = g.Segment[led];
                int len = g.SegmentLength[fly.Seg];
                fly.X = g.X[led] + (u4 - 0.5) * 0.8 * p / Math.Max(1, len - 1);
            }
            flies[count++] = fly;
        }
        return count;
    }

    // ---- water ----------------------------------------------------------------------------------------------------
    struct Ripple { public double X, Y, Radius, Amp; }

    static int CollectRipples(LedGeometry g, double t, double tempo, double density, Span<Ripple> ripples)
    {
        const double Life = 3.6;
        double lifeMax = Life / 0.7;
        int slots = Math.Min(ripples.Length, 2 + (int)Math.Round(3 * density));
        int count = 0;
        for (int s = 0; s < slots; s++)
        {
            if (!Slot(g.Seed + 211, s, t, lifeMax * 1.1, lifeMax * 1.7, 0.85, lifeMax, out double age, out uint h)) continue;
            double life = Life / tempo;
            if (age >= life) continue;
            double u1 = U01(h); h = Next(h); double u2 = U01(h); h = Next(h); double u3 = U01(h); h = Next(h); double u4 = U01(h);
            double p = age / life;
            ripples[count++] = new Ripple
            {
                X = g.Width * (0.05 + 0.9 * u1), Y = g.Height * u2,
                Radius = 0.11 * tempo * (0.8 + 0.4 * u3) * age,
                Amp = (0.4 + 0.25 * u4) * Math.Pow(1 - p, 2) * Smooth(0, 0.25, age)
            };
        }
        return count;
    }

    // ---- canopy light shafts --------------------------------------------------------------------------------------
    struct Shaft { public double X, Amp; }

    static int CollectShafts(double t, double speed, double density, Span<Shaft> shafts)
    {
        int slots = Math.Min(shafts.Length, 1 + (int)Math.Round(3 * density));
        if (density <= 0.001) return 0;
        const double In = 3.0, Hold = 2.5, Out = 3.5, Life = In + Hold + Out;
        int count = 0;
        for (int s = 0; s < slots; s++)
        {
            if (!Slot(SetupSeed + 301, s, t, 16, 26, 0.65, Life, out double age, out uint h)) continue;
            if (age >= Life) continue;
            double env = age < In ? Smoother(age / In) : age < In + Hold ? 1 : 1 - Smoother((age - In - Hold) / Out);
            double u1 = U01(h); h = Next(h); double u2 = U01(h);
            shafts[count++] = new Shaft { X = -0.05 + 1.25 * u1 + 0.01 * age, Amp = env * (0.35 + 0.2 * u2) };
        }
        return count;
    }
}
