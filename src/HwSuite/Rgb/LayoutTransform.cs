using HwSuite.Core;
using HwSuite.Lighting.Devices;

namespace HwSuite.Lighting;

/// <summary>
/// Maps each device's effect positions through the owner's arrangement (<see cref="DeviceArrangement"/>). Effects take
/// one position per LED on a shared line (phase units); only position-based effects (Gradient Wave) use it, so Static,
/// Breathing, Thermal and Stealth look the same wherever a device sits.
///
/// Not arranged: identity, exactly the 1.11 positions. Arranged: a group's positions are moved so their centre sits at
/// <c>X / CanvasWidth * PhaseScale</c>, keeping the existing spread inside the group untouched (the D_LED chain in chain
/// order, the board's two zones, the RAM controllers in detection order: none of these is re-mapped to a physical
/// position that has not been verified). The keyboard uses its verified key coordinates: turning it changes which keys
/// lead the wave. The vertical position does not matter (the wave travels left to right). Immutable; replaced as a
/// whole when an arrangement edit is completed. No device protocol or timing is involved.
/// </summary>
public sealed class LayoutTransform
{
    /// <summary>Phase units across the whole canvas width (about the span of the 1.11 default line, 0.02..1.16).</summary>
    public const double PhaseScale = 1.15;
    public static readonly LayoutTransform Identity = new(null, 0);

    readonly DeviceArrangement? _arr;
    /// <summary>1.15: the D_LED chain as the case (fans and logo); the atmosphere scenes use it, classic effects keep chain order.</summary>
    public CaseLayout Case { get; }
    readonly object _gate = new();
    readonly Dictionary<RgbSink, (double[] Source, double[] Mapped)> _cache = new();
    readonly Dictionary<RgbSink, LedGeometry> _geo = new();
    readonly Dictionary<(DeviceRole, int, string), LedGeometry> _roleGeo = new();

    public long Version { get; }
    public bool Active => _arr != null;

    public LayoutTransform(DeviceArrangement? arrangement, long version, CaseLayout? caseLayout = null)
    {
        _arr = arrangement is { Arranged: true } ? arrangement.Clone() : null;
        Version = version;
        Case = (caseLayout ?? CaseLayout.Default).Clone();
        Case.Normalize();
    }

    public static DeviceRole? RoleOf(RgbSink s) => s switch
    {
        DLedSink => DeviceRole.Strip, MotherboardSink => DeviceRole.Board, RamSink => DeviceRole.Ram,
        KeyboardSink => DeviceRole.Keyboard, MouseSink => DeviceRole.Mouse, _ => s.RoleHint
    };

    /// <summary>Positions the engine evaluates for this sink (cached per positions array; no allocation per frame).</summary>
    public double[] Map(RgbSink sink)
    {
        var source = sink.Positions;
        if (_arr == null || RoleOf(sink) is not DeviceRole role) return source;
        lock (_gate)
        {
            if (_cache.TryGetValue(sink, out var c) && ReferenceEquals(c.Source, source)) return c.Mapped;
            var mapped = MapPhases(role, source, _arr.Get(role));
            _cache[sink] = (source, mapped);
            return mapped;
        }
    }

    /// <summary>
    /// Geometry for the effects: setup-wide positions from <see cref="Map"/> plus the device-local layout (the keyboard's key
    /// surface, turned with the keyboard; memory as one LED run per controller; everything else one run in index order).
    /// Cached until the sink's positions change; no allocation per frame.
    /// </summary>
    public LedGeometry Geometry(RgbSink sink)
    {
        var line = Map(sink);
        lock (_gate)
        {
            if (_geo.TryGetValue(sink, out var g) && ReferenceEquals(g.Source, line)) return g;
            g = Build(RoleOf(sink), line, sink.LedRuns, line);
            _geo[sink] = g;
            return g;
        }
    }

    /// <summary>Geometry for a role without a sink (simulated preview, fixtures, tests). Memory takes its controllers' LED counts.</summary>
    public LedGeometry GeometryFor(DeviceRole role, int count, IReadOnlyList<int>? ramSegments = null)
    {
        var key = (role, count, ramSegments == null ? "" : string.Join(",", ramSegments));
        lock (_gate)
        {
            if (_roleGeo.TryGetValue(key, out var g)) return g;
            double[] basePos = role switch
            {
                DeviceRole.Strip => DLedSink.PositionsFor(Math.Max(1, count)),
                DeviceRole.Board => MotherboardSink.BasePositions,
                DeviceRole.Ram => RamSink.PositionsFor(ramSegments ?? new[] { count }),
                DeviceRole.Keyboard => KeyboardKeyMaps.UnitX(count) is { } ux ? ux.Select(x => KeyboardSink.WaveStart + KeyboardSink.WaveSpan * x).ToArray() : new[] { 0.99 },
                _ => MouseSink.BasePositions
            };
            var line = MapRole(role, basePos);
            if (line.Length != count) line = Enumerable.Range(0, count).Select(i => i < line.Length ? line[i] : i / (double)Math.Max(1, count)).ToArray();
            g = Build(role, line, ramSegments, null);
            if (_roleGeo.Count > 64) _roleGeo.Clear();
            _roleGeo[key] = g;
            return g;
        }
    }

    /// <summary>The keyboard's quarter turn in the arrangement (0 until arranged).</summary>
    public int KeyboardRotation => _arr?.Get(DeviceRole.Keyboard).Rotation ?? 0;

    LedGeometry Build(DeviceRole? role, double[] line, IReadOnlyList<int>? segments, double[]? source)
    {
        uint seed = LedGeometry.SeedFor(role);
        if (role == DeviceRole.Keyboard && KeyboardKeyMaps.For(line.Length) != null) return LedGeometry.Keyboard(line, KeyboardRotation, seed, source);
        // memory: LED 0 is at the top of each stick (owner, 2026-10-15: drops sent last LED -> first rose up the real
        // sticks), so droplets run first -> last, i.e. downward; the preview draws LED 0 at the top too
        if (role == DeviceRole.Ram) return LedGeometry.Linear(line, segments, seed, reverse: false, source);
        if (role == DeviceRole.Strip) return LedGeometry.CaseFront(line, Case, seed, source);
        return LedGeometry.Linear(line, null, seed, false, source);
    }

    /// <summary>Same mapping for a role's base positions (preview without sinks, fixtures).</summary>
    public double[] MapRole(DeviceRole role, double[] basePositions) => _arr == null ? basePositions : MapPhases(role, basePositions, _arr.Get(role));

    public static double[] MapPhases(DeviceRole role, double[] basePositions, DevicePlacement p)
    {
        double target = p.X / DeviceArrangement.CanvasWidth * PhaseScale;
        var mapped = new double[basePositions.Length];
        if (basePositions.Length == 0) return mapped;
        if (role == DeviceRole.Keyboard && KeyboardKeyMaps.For(basePositions.Length) is { } keys)
        {
            // verified key centres (KB.ini), scaled by the keyboard's width so a quarter turn keeps true proportions
            double minX = keys.Min(k => k.X), maxX = keys.Max(k => k.X), minY = keys.Min(k => k.Y), maxY = keys.Max(k => k.Y);
            double width = Math.Max(1, maxX - minX), cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            for (int i = 0; i < keys.Length; i++)
            {
                double u = (keys[i].X - cx) / width, v = (keys[i].Y - cy) / width;
                double along = p.Rotation switch { 90 => -v, 180 => -u, 270 => v, _ => u };
                mapped[i] = target + KeyboardSink.WaveSpan * along;
            }
            return mapped;
        }
        double min = basePositions.Min(), max = basePositions.Max(), centre = (min + max) / 2;
        for (int i = 0; i < basePositions.Length; i++) mapped[i] = basePositions[i] - centre + target;
        return mapped;
    }

    /// <summary>What a group's arrangement can change, in the owner's words (shown in Arrange mode).</summary>
    public static string Participation(DeviceRole role, AppConfig cfg) => role switch
    {
        DeviceRole.Mouse => "Preview only: the mouse runs its own onboard effect, so its position does not change its lighting.",
        DeviceRole.Keyboard when !(cfg.KeyboardVerified && cfg.KeyboardLiveVerified) => "Preview only: the keyboard uses its own onboard effect until live frames are verified.",
        DeviceRole.Keyboard => "Gradient Wave, mist and canopy light cross the keys by their verified positions; turning the keyboard changes which keys lead, and rain falls toward the bottom of the arrangement.",
        DeviceRole.Ram when cfg.Rgb.RamOnboardEffects => "Gradient Wave and the atmosphere effects: preview only while the memory runs its onboard effects (they keep their own timing).",
        DeviceRole.Ram => "Gradient Wave and mist follow its position. Sticks are drawn in controller detection order, not a verified slot order.",
        DeviceRole.Strip => "Gradient Wave and mist follow its position. The fans and logo are one D_LED chain and move together; inside the case, rain falls down the front fans and the rear fan by the case layout set on its card.",
        _ => "Gradient Wave and mist follow its position (LED_C and PCIe accent)."
    };

    /// <summary>
    /// The short label under a device in Arrange mode: what moving it changes for the current effect. Device-local scenes
    /// (rain, fireflies, keyboard ripples) stay on the device wherever it sits; only their faint backgrounds follow it.
    /// </summary>
    public static string ArrangeLabel(DeviceRole role, AppConfig cfg)
    {
        if (Participation(role, cfg).StartsWith("Preview only") || (role == DeviceRole.Ram && cfg.Rgb.RamOnboardEffects)) return "preview only";
        var e = LightingLooks.EffectiveEffect(cfg.Rgb);
        return e switch
        {
            EffectKind.Rainfall or EffectKind.DistantStorm or EffectKind.Downpour => role == DeviceRole.Board ? "own glints" : "own rain",
            EffectKind.Drip => "own drips",
            EffectKind.RainRipples => "own ripples",
            EffectKind.Fireflies => "own fireflies",
            EffectKind.MoonlitWater when role == DeviceRole.Keyboard => "own ripples",
            EffectKind.Embers => "own fire",
            EffectKind.Radar => role == DeviceRole.Keyboard ? "own sweep" : "sweep in step",
            EffectKind.TypingRipples => role == DeviceRole.Keyboard ? "ripples from your keys" : "pulses with typing",
            EffectKind.GradientWave => "moves the wave",
            _ when EffectInfo.IsScene(e) => "moves the effect",
            _ => "no effect now"
        };
    }

    /// <summary>One-line caption for the arrange canvas.</summary>
    public static string AxisCaption(EffectKind e) => e switch
    {
        EffectKind.Rainfall or EffectKind.Drip => "rain stays on each device",
        EffectKind.RainRipples => "ripples stay on each device",
        EffectKind.Downpour => "gusts sweep left → right",
        EffectKind.Fireflies => "fireflies stay on each device",
        EffectKind.DistantStorm => "clouds drift left → right",
        EffectKind.RollingMist => "mist drifts left → right",
        EffectKind.Canopy => "light and shade drift left → right",
        EffectKind.MoonlitWater => "reflections travel left → right",
        EffectKind.Embers => "fire rises on each device",
        EffectKind.Radar => "every device sweeps in step",
        EffectKind.TypingRipples => "ripples start at the keys you press",
        _ => "Gradient Wave travels left → right"
    };

    /// <summary>How the current effect uses positions, for the Arrange note (setup-wide vs device-local).</summary>
    public static string EffectNote(EffectKind e) => e switch
    {
        EffectKind.GradientWave => "Gradient Wave travels left to right across the arrangement.",
        EffectKind.RollingMist or EffectKind.Canopy => $"{EffectInfo.Label(e)} drifts left to right across the whole arrangement (setup-wide).",
        EffectKind.Rainfall => "Rain is device-local: it falls down the keyboard's key rows (toward the bottom of the arrangement if the keyboard is turned) and runs along each LED chain; only its faint background drifts across the arrangement.",
        EffectKind.DistantStorm => "Rain is device-local; the clouds and the rare lightning glow are setup-wide and follow the arrangement.",
        EffectKind.Fireflies => "Fireflies are device-local: each device has its own, wherever it sits.",
        EffectKind.Drip => "Drips are device-local: they form at the top of the keyboard, each memory stick and the front fans, then fall to the bottom.",
        EffectKind.RainRipples => "Ripples are device-local: each device has its own raindrops; only the faint water tone drifts across the arrangement.",
        EffectKind.Downpour => "Rain is device-local; the gusts, rain curtains and the optional lightning are setup-wide and follow the arrangement.",
        EffectKind.MoonlitWater => "Ripples are device-local on the keyboard; the reflection on the other devices travels across the arrangement.",
        EffectKind.Embers => "Fire is device-local: it rises from the bottom of the keyboard (the bottom of the arrangement if it is turned), the fan stack and each memory stick.",
        EffectKind.Radar => "One sweep shared by every device: around the keyboard's centre, around each fan, down each memory stick, a ping on single lights. Moving devices changes nothing.",
        EffectKind.TypingRipples => "Ripples start at the key you press and spread across the keyboard; every other device pulses at the same moment.",
        _ => $"{EffectInfo.Label(e)} looks the same wherever a device sits."
    };
}
