namespace HwSuite.Core;

/// <summary>
/// What each effect uses and how it is named. The six "atmosphere" effects (1.13) are procedural scenes inspired by the
/// owner's rainy-forest wallpaper; they use the secondary colour as their dim resting tone, the shared speed and
/// brightness, plus <see cref="RgbConfig.Density"/>, <see cref="RgbConfig.Glow"/> and (Distant Storm only)
/// <see cref="RgbConfig.Lightning"/>.
/// </summary>
public static class EffectInfo
{
    public static readonly EffectKind[] Classic = { EffectKind.Static, EffectKind.Breathing, EffectKind.GradientWave, EffectKind.ThermalReactive, EffectKind.Stealth };
    public static readonly EffectKind[] Scenes = { EffectKind.Rainfall, EffectKind.RollingMist, EffectKind.Fireflies, EffectKind.MoonlitWater, EffectKind.DistantStorm, EffectKind.Canopy };
    /// <summary>1.15 rain family (the owner's favourite is Rainfall): forming drips, rain on a pond, wind-driven downpour.</summary>
    public static readonly EffectKind[] RainScenes = { EffectKind.Drip, EffectKind.RainRipples, EffectKind.Downpour };
    /// <summary>1.20 effects made for the per-key AL80 (still running on every device): fire, a radar sweep, typing ripples.</summary>
    public static readonly EffectKind[] Scenes3 = { EffectKind.Embers, EffectKind.Radar, EffectKind.TypingRipples };

    public static bool IsScene(EffectKind k) => k is EffectKind.Rainfall or EffectKind.RollingMist or EffectKind.Fireflies or EffectKind.MoonlitWater or EffectKind.DistantStorm or EffectKind.Canopy || IsNewScene(k) || IsScene3(k);
    /// <summary>Scenes added in 1.15: saved in "Scene2", with <see cref="SceneFallback"/> in "Scene" for 1.13 and 1.14.</summary>
    public static bool IsNewScene(EffectKind k) => k is EffectKind.Drip or EffectKind.RainRipples or EffectKind.Downpour;
    /// <summary>Effects added in 1.20: saved in "Scene3", with <see cref="SceneFallback"/> in "Scene" for 1.13-1.19.</summary>
    public static bool IsScene3(EffectKind k) => k is EffectKind.Embers or EffectKind.Radar or EffectKind.TypingRipples;
    /// <summary>Reacts to key presses (VANTA listens for key positions only while this effect is chosen).</summary>
    public static bool UsesKeys(EffectKind k) => k == EffectKind.TypingRipples;

    /// <summary>The nearest 1.13 scene, saved for 1.13/1.14 (which do not know the 1.15 scenes and would reset an unknown one).</summary>
    public static EffectKind SceneFallback(EffectKind k) => k switch
    {
        EffectKind.Drip => EffectKind.Rainfall,
        EffectKind.RainRipples => EffectKind.MoonlitWater,
        EffectKind.Downpour => EffectKind.DistantStorm,
        EffectKind.Embers => EffectKind.Fireflies,
        EffectKind.Radar or EffectKind.TypingRipples => EffectKind.MoonlitWater,
        _ => k
    };

    /// <summary>
    /// The classic effect saved in the "Effect" field for a scene, so VANTA 1.12 and earlier can still read the
    /// configuration after a rollback (they do not know the scene names and would otherwise reset to defaults).
    /// </summary>
    public static EffectKind ClassicFallback(EffectKind k) => k switch
    {
        EffectKind.Rainfall or EffectKind.RollingMist or EffectKind.MoonlitWater or EffectKind.DistantStorm or EffectKind.Drip or EffectKind.RainRipples or EffectKind.Downpour => EffectKind.GradientWave,
        EffectKind.Fireflies or EffectKind.Canopy or EffectKind.Embers or EffectKind.TypingRipples => EffectKind.Breathing,
        EffectKind.Radar => EffectKind.GradientWave,
        _ => k
    };

    public static string Label(EffectKind k) => k switch
    {
        EffectKind.GradientWave => "Gradient Wave",
        EffectKind.ThermalReactive => "Thermal",
        EffectKind.RollingMist => "Rolling Mist",
        EffectKind.MoonlitWater => "Moonlit Water",
        EffectKind.DistantStorm => "Distant Storm",
        EffectKind.RainRipples => "Rain Ripples",
        EffectKind.TypingRipples => "Typing Ripples",
        _ => k.ToString()
    };

    /// <summary>Short name for the effect selector.</summary>
    public static string ShortLabel(EffectKind k) => k switch
    {
        EffectKind.RollingMist => "Mist",
        EffectKind.MoonlitWater => "Water",
        EffectKind.DistantStorm => "Storm",
        EffectKind.RainRipples => "Ripples",
        EffectKind.TypingRipples => "Typing",
        _ => Label(k)
    };

    public static bool UsesSecondary(EffectKind k) => k == EffectKind.GradientWave || IsScene(k);
    public static bool UsesSpeed(EffectKind k) => k is EffectKind.Breathing or EffectKind.GradientWave || IsScene(k);
    public static bool UsesDensity(EffectKind k) => IsScene(k);
    public static bool UsesGlow(EffectKind k) => IsScene(k);
    public static bool UsesLightning(EffectKind k) => k is EffectKind.DistantStorm or EffectKind.Downpour;
    /// <summary>Changes over time (the engine renders it at the animation rate).</summary>
    public static bool Animated(EffectKind k) => k is EffectKind.Breathing or EffectKind.GradientWave || IsScene(k);

    /// <summary>What the density slider controls for this effect (null: not used).</summary>
    public static string? DensityLabel(EffectKind k) => k switch
    {
        EffectKind.Rainfall or EffectKind.DistantStorm => "Rain",
        EffectKind.RollingMist => "Fog",
        EffectKind.Fireflies => "Fireflies",
        EffectKind.MoonlitWater => "Ripples",
        EffectKind.Canopy => "Light shafts",
        EffectKind.Drip => "Drips",
        EffectKind.RainRipples => "Raindrops",
        EffectKind.Downpour => "Rain",
        EffectKind.Embers => "Sparks",
        EffectKind.Radar => "Contacts",
        EffectKind.TypingRipples => "Ripple size",
        _ => null
    };

    public static double DefaultDensity(EffectKind k) => k switch
    {
        EffectKind.Rainfall => 0.30,
        EffectKind.DistantStorm => 0.45,
        EffectKind.RollingMist => 0.50,
        EffectKind.Fireflies => 0.40,
        EffectKind.MoonlitWater => 0.45,
        EffectKind.Canopy => 0.30,
        EffectKind.Drip => 0.40,
        EffectKind.RainRipples => 0.45,
        EffectKind.Downpour => 0.65,
        EffectKind.Embers => 0.45,
        EffectKind.Radar => 0.35,
        EffectKind.TypingRipples => 0.50,
        _ => 0.35
    };

    /// <summary>What the primary and secondary colours are for in this effect.</summary>
    public static (string Primary, string Secondary) ColourRoles(EffectKind k) => k switch
    {
        EffectKind.Rainfall => ("drops", "background"),
        EffectKind.DistantStorm => ("rain and flashes", "sky"),
        EffectKind.RollingMist => ("mist", "forest"),
        EffectKind.Fireflies => ("fireflies", "forest"),
        EffectKind.MoonlitWater => ("moonlight", "water"),
        EffectKind.Canopy => ("light", "canopy"),
        EffectKind.Drip => ("drips", "background"),
        EffectKind.RainRipples => ("raindrops", "water"),
        EffectKind.Downpour => ("rain", "sky"),
        EffectKind.Embers => ("flames", "embers"),
        EffectKind.Radar => ("beam", "screen"),
        EffectKind.TypingRipples => ("ripples", "background"),
        EffectKind.GradientWave => ("start", "end"),
        _ => ("", "")
    };

    public static string Describe(EffectKind k) => k switch
    {
        EffectKind.Static => "One colour. Sent once; nothing is streamed while it stays the same.",
        EffectKind.Breathing => "Primary colour fading in and out.",
        EffectKind.GradientWave => "Primary to secondary colour moving across the devices that can show it.",
        EffectKind.ThermalReactive => "Colour follows the hotter of CPU and GPU (smoothed over about 3 s).",
        EffectKind.Stealth => "LEDs off on every device VANTA can switch off.",
        EffectKind.Rainfall => "Sparse drops fall down the keyboard's key rows and through the stacked front fans with short fading trails, splashing on the logo; memory shows droplets running down its LEDs. Most LEDs stay dim.",
        EffectKind.RollingMist => "Slow, soft bands of mist drifting across your arranged devices over a dark forest tone.",
        EffectKind.Fireflies => "A few soft points of light fade in, linger and fade out over a dim forest green.",
        EffectKind.MoonlitWater => "Ripples spread across the keyboard; strips and memory show a slow travelling reflection.",
        EffectKind.DistantStorm => "Muted rain over drifting cloud, with rare soft lightning glowing behind the clouds (can be switched off).",
        EffectKind.Canopy => "The quietest scene: very slow shifts of deep green and blue-green, with an occasional soft shaft of light.",
        EffectKind.Drip => "Water gathers into a bead, hangs, then lets go: it falls faster and faster down the front fans, the key rows and the memory, and splashes at the bottom.",
        EffectKind.RainRipples => "Raindrops land on still water: a bright touch where each one lands, then rings spreading out across the keyboard, the case and along each LED run.",
        EffectKind.Downpour => "Heavy, slanted rain that comes in gusts: dense fast drops with rain curtains sweeping across, and optional soft lightning.",
        EffectKind.Embers => "A low fire: flames flicker up from the space-bar row and the bottom fans, sparks drift up the keys, the fan stack and the memory and cool from yellow to red.",
        EffectKind.Radar => "A radar screen: a beam sweeps around the keyboard's centre (and around every fan) leaving a fading afterglow; contacts light up as it passes. Memory: the beam runs down each stick once per turn.",
        EffectKind.TypingRipples => "Every key you press flashes and sends a ring of light out across the keyboard; the fans, memory and mouse pulse with your typing. VANTA sees only which key position was pressed, never what you type, and only while this effect is chosen.",
        _ => ""
    };
}
