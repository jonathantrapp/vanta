using System.Text.Json.Serialization;
using HwSuite.Lighting;

namespace HwSuite.Core;

public enum CurveSource { Cpu, Gpu, MaxOfBoth }

public enum ChannelMode { Auto, Curve, Manual }

/// <summary>
/// Lighting effects. The atmosphere scenes (Rainfall onwards, 1.13) are saved in the separate "Scene" field next to a
/// classic fallback in "Effect", so an older VANTA can still read the file (see <see cref="EffectInfo.ClassicFallback"/>).
/// </summary>
/// <summary>Drip, RainRipples and Downpour arrived in 1.15: they are saved in "Scene2" (older builds ignore it) with a 1.13 scene in "Scene".</summary>
public enum EffectKind { Static, Breathing, GradientWave, ThermalReactive, Stealth, Rainfall, RollingMist, Fireflies, MoonlitWater, DistantStorm, Canopy, Drip, RainRipples, Downpour, Embers, Radar, TypingRipples }

/// <summary>Aio = one PWM cable driving the pump AND the radiator fans (Arctic Liquid Freezer II): pump floor applies.</summary>
public enum FanKind { Generic, CpuCooler, Pump, CaseFan, GpuFan, Aio }

/// <summary>Byte order of the colour channels on the addressable (D_LED) strip, as they appear in the packet.</summary>
public enum ChannelOrder { Grb, Rgb, Bgr, Rbg, Gbr, Brg }

public sealed class CurvePoint
{
    public double Temp { get; set; }
    public double Percent { get; set; }

    public CurvePoint() { }
    public CurvePoint(double temp, double percent) { Temp = temp; Percent = percent; }
}

public sealed class FanCurve
{
    public string Name { get; set; } = "Custom";
    public CurveSource Source { get; set; } = CurveSource.MaxOfBoth;
    public List<CurvePoint> Points { get; set; } = new();
    /// <summary>Temperature must fall this far below the last peak before the duty is allowed to drop.</summary>
    public double HysteresisC { get; set; } = 3;
    /// <summary>Maximum duty increase in percent per second.</summary>
    public double RampUpPerSec { get; set; } = 25;
    /// <summary>Maximum duty decrease in percent per second.</summary>
    public double StepDownPerSec { get; set; } = 3;
    public double MinPercent { get; set; } = 0;
    public double MaxPercent { get; set; } = 100;
    /// <summary>GPU only: below this temperature hand the fans back to the VBIOS automatic policy (zero-RPM capable cards). 0 = never.</summary>
    public double ReleaseToAutoBelowC { get; set; } = 0;

    public FanCurve Clone() => new()
    {
        Name = Name, Source = Source, HysteresisC = HysteresisC, RampUpPerSec = RampUpPerSec,
        StepDownPerSec = StepDownPerSec, MinPercent = MinPercent, MaxPercent = MaxPercent,
        ReleaseToAutoBelowC = ReleaseToAutoBelowC,
        Points = Points.Select(p => new CurvePoint(p.Temp, p.Percent)).ToList()
    };

    /// <summary>
    /// Duty (percent) for a temperature. Monotone cubic Hermite interpolation (Fritsch-Carlson / PCHIP): smooth, never
    /// overshoots between nodes, and identical to the curve drawn by the editor. Flat outside the first/last node.
    /// </summary>
    public double Evaluate(double tempC)
    {
        var p = Points.OrderBy(x => x.Temp).ToList();
        int n = p.Count;
        if (n == 0) return 50;
        if (n == 1 || tempC <= p[0].Temp) return Math.Clamp(p[0].Percent, 0, 100);
        if (tempC >= p[n - 1].Temp) return Math.Clamp(p[n - 1].Percent, 0, 100);

        var h = new double[n - 1];
        var d = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            h[i] = p[i + 1].Temp - p[i].Temp;
            d[i] = h[i] <= 1e-9 ? 0 : (p[i + 1].Percent - p[i].Percent) / h[i];
        }

        var m = new double[n];
        if (n == 2) { m[0] = m[1] = d[0]; }
        else
        {
            for (int i = 1; i < n - 1; i++)
            {
                if (d[i - 1] * d[i] <= 0) { m[i] = 0; continue; }
                double w1 = 2 * h[i] + h[i - 1], w2 = h[i] + 2 * h[i - 1];
                m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]);
            }
            m[0] = EndSlope(h[0], h[1], d[0], d[1]);
            m[n - 1] = EndSlope(h[n - 2], h[n - 3], d[n - 2], d[n - 3]);
        }

        int seg = 0;
        while (seg < n - 2 && tempC > p[seg + 1].Temp) seg++;
        double s = (tempC - p[seg].Temp) / Math.Max(h[seg], 1e-9);
        double s2 = s * s, s3 = s2 * s;
        double y = (2 * s3 - 3 * s2 + 1) * p[seg].Percent
                 + (s3 - 2 * s2 + s) * h[seg] * m[seg]
                 + (-2 * s3 + 3 * s2) * p[seg + 1].Percent
                 + (s3 - s2) * h[seg] * m[seg + 1];
        return Math.Clamp(y, 0, 100);
    }

    static double EndSlope(double h0, double h1, double d0, double d1)
    {
        double m = ((2 * h0 + h1) * d0 - h0 * d1) / (h0 + h1);
        if (Math.Sign(m) != Math.Sign(d0)) return 0;
        if (Math.Sign(d0) != Math.Sign(d1) && Math.Abs(m) > 3 * Math.Abs(d0)) return 3 * d0;
        return m;
    }
}

public sealed class ChannelConfig
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public FanKind Kind { get; set; } = FanKind.Generic;
    public ChannelMode Mode { get; set; } = ChannelMode.Auto;
    public double ManualPercent { get; set; } = 50;
    public FanCurve Curve { get; set; } = new();
    /// <summary>
    /// Lowest duty the owner has confirmed keeps the connected device running (case fans and CPU cooler fans need this
    /// before HwSuite will drive them). Null = not confirmed, so the header stays under firmware control.
    /// </summary>
    public double? VerifiedMinPercent { get; set; }
    /// <summary>How the minimum was established, in the owner's words (for example "watched all three fans spin at 30%").</summary>
    public string? MinEvidence { get; set; }
    /// <summary>
    /// Runtime history: set once this channel's tachometer has reported a plausible speed while VANTA ran. Kept for older
    /// configs and unidentified headers; startup and feedback checks also use <see cref="TachMapped"/>, which does not depend
    /// on rotation having been seen.
    /// </summary>
    public bool TachSeen { get; set; }
    /// <summary>
    /// Evidence (not runtime history) that this control's tachometer is known: the control moved exactly this tachometer in
    /// a step test. A mapped fan that does not turn while VANTA drives it is a fault even if it never turned this session.
    /// </summary>
    public bool TachMapped { get; set; }
    public string? TachEvidence { get; set; }
    /// <summary>
    /// Lowest duty at which the connected fan was observed (attended test) to start from a full stop. Null = not measured.
    /// A confirmed minimum below this, or with no measurement, is an owner-approved provisional limit: its startup is
    /// not established, so VANTA verifies every start (FanEngine).
    /// </summary>
    public double? MeasuredStartPercent { get; set; }
    public string? StartEvidence { get; set; }
}

public sealed class RgbConfig
{
    /// <summary>A copy with another effect (shallow; for computing a device's setting for that effect, never saved).</summary>
    public RgbConfig WithEffect(EffectKind e) { var c = (RgbConfig)MemberwiseClone(); c.Effect = e; return c; }

    EffectKind _effect = EffectKind.Static;
    /// <summary>The effect. Saved as "Effect" (a classic effect older builds understand) plus "Scene" for the atmosphere effects.</summary>
    [JsonIgnore] public EffectKind Effect { get => _effect; set => _effect = value; }
    /// <summary>Saved form of <see cref="Effect"/>: the effect itself, or a scene's classic fallback. Order-independent with <see cref="Scene"/>.</summary>
    [JsonPropertyName("Effect")]
    public EffectKind SavedEffect { get => EffectInfo.ClassicFallback(_effect); set { if (!EffectInfo.IsScene(_effect)) _effect = value; } }
    /// <summary>Saved form of an atmosphere effect (null for the classic ones). Older builds ignore this field.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EffectKind? Scene { get => EffectInfo.IsScene(_effect) ? EffectInfo.SceneFallback(_effect) : null; set { if (value is EffectKind s && EffectInfo.IsScene(s) && !EffectInfo.IsNewScene(_effect) && !EffectInfo.IsScene3(_effect)) _effect = s; } }
    /// <summary>Saved form of a 1.15 rain effect (null otherwise); "Scene" then holds the nearest 1.13 scene for older builds.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EffectKind? Scene2 { get => EffectInfo.IsNewScene(_effect) ? _effect : null; set { if (value is EffectKind s && EffectInfo.IsNewScene(s) && !EffectInfo.IsScene3(_effect)) _effect = s; } }
    /// <summary>Saved form of a 1.20 effect (Embers, Radar, Typing Ripples; null otherwise). Older builds ignore it and read
    /// "Scene" (the nearest 1.13 scene) and "Effect" (a classic effect) instead.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EffectKind? Scene3 { get => EffectInfo.IsScene3(_effect) ? _effect : null; set { if (value is EffectKind s && EffectInfo.IsScene3(s)) _effect = s; } }
    /// <summary>What is lit on the D_LED chain and where (1.15). Null in older files: the default layout.</summary>
    public CaseLayout? Case { get; set; }
    [JsonIgnore] public CaseLayout CaseOrDefault => Case ?? CaseLayout.Default;
    /// <summary>1.15.2: per-device colour balance (memory, case fans, board), so one colour looks alike on different LEDs.</summary>
    public ColorBalances Balance { get; set; } = new();
    /// <summary>Atmosphere effects: how much happens (rain, fog, fireflies, ripples, light shafts), 0..1.</summary>
    public double Density { get; set; } = 0.35;
    /// <summary>Atmosphere effects: how bright the resting tone (secondary colour) glows, 0..1. 0.5 keeps most LEDs dim.</summary>
    public double Glow { get; set; } = 0.5;
    /// <summary>Distant Storm only: rare soft lightning behind the clouds. No other effect ever shows lightning.</summary>
    public bool Lightning { get; set; } = true;
    /// <summary>
    /// The calm colour a scene leaves on devices that cannot show it (mouse, onboard memory, keyboard without live frames)
    /// and at quit: the secondary tone lifted toward the primary. Classic effects: the primary colour, unchanged.
    /// </summary>
    [JsonIgnore] public int SteadyColor => EffectInfo.IsScene(_effect) ? Companion.For(this).Color : ColorA & 0xFFFFFF;
    /// <summary>A copy showing the steady look (exit and output stop): Static with <see cref="SteadyColor"/>. Never saved.</summary>
    public RgbConfig SteadyConfig() { var c = (RgbConfig)MemberwiseClone(); c.ColorA = SteadyColor; c.Effect = EffectKind.Static; return c; }
    public int ColorA { get; set; } = 0x38BDF8;   // 0xRRGGBB; fresh configs: ice-cyan to deep indigo
    public int ColorB { get; set; } = 0x6366F1;
    /// <summary>Colours the owner saved to the swatch bar (0xRRGGBB), newest first.</summary>
    public List<int> SavedColors { get; set; } = new();
    public double Speed { get; set; } = 0.5;       // 0..1
    public double Brightness { get; set; } = 1.0;  // 0..1
    public bool Enabled { get; set; } = false;     // RGB output is opt-in; nothing is sent until enabled
    public bool Motherboard { get; set; } = true;
    public bool Mouse { get; set; } = true;
    /// <summary>
    /// Breathing: lock the mouse's onboard breathing to VANTA's (2026-10-23 measurements, docs\mouse-sync\RESULTS.md). VANTA's
    /// breathing period snaps to the mouse's 0.512 s steps and the mouse write is timed so both are dark together. Off by default.
    /// </summary>
    public bool MouseSync { get; set; }
    /// <summary>
    /// Stream the mouse's frame colour live, like every other device (test 8, 2026-10-23), so it follows every effect and
    /// scene. Takes precedence over <see cref="MouseSync"/>. Off by default.
    /// </summary>
    public bool MouseLive { get; set; }
    /// <summary>
    /// When the mouse's light changes relative to the case fans for a write and a frame sent at the same moment. Measured
    /// -45 ms on 2026-10-23 (the mouse is the quicker of the two); negative means the mouse leads.
    /// </summary>
    public int MouseSyncLatencyMs { get; set; } = -45;
    /// <summary>Include the iLovbee keyboard. Nothing is sent to it until <see cref="AppConfig.KeyboardVerified"/>.</summary>
    public bool Keyboard { get; set; } = true;
    /// <summary>Native keyboard mode used for Gradient Wave (its own animation, not synchronised); null = fixed primary colour.</summary>
    public int? KeyboardWaveMode { get; set; }
    public bool Ram { get; set; } = true;
    public bool DLedStrip { get; set; } = true;
    /// <summary>Number of LEDs on the D_LED header (27 for the original LANCOOL 215 fans, from the owner's OpenRGB profile; the 2026-10 Thermalright fans need a recount).</summary>
    public int DLedCount { get; set; } = 27;
    /// <summary>Standard WS2812B order is GRB. Flip this if the case-fan colours look swapped.</summary>
    public ChannelOrder StripOrder { get; set; } = ChannelOrder.Grb;
    /// <summary>
    /// D_LED strip target updates per second. 60 is the default; the higher choices in <see cref="DLedRates"/> are
    /// experimental (Phase 7 A/B: they change how often the strip is sent new colours, not the animation speed).
    /// Board LEDs and RAM keep their own limits, and the mouse and keyboard stay settings-driven.
    /// </summary>
    public double DLedMaxHz { get; set; } = 60;
    /// <summary>Selectable strip rates. Anything above 60 is experimental pending the owner's visual comparison.</summary>
    public static readonly int[] DLedRates = { 60, 90, 120, 144 };
    /// <summary>Written by an earlier Phase 7 build for its 120 option; migrated into DLedMaxHz when a config loads.</summary>
    public bool DLedExperimental120 { get; set; } = false;
    /// <summary>Runtime-only rate used by the visual A/B comparison; never saved.</summary>
    [JsonIgnore] public int? DLedRateOverride { get; set; }
    /// <summary>The strip rate in effect: the A/B override if one is running, else the saved choice (unknown values fall back to at most 60).</summary>
    [JsonIgnore] public int EffectiveDLedHz =>
        DLedRateOverride is int o && DLedRates.Contains(o) ? o
        : DLedExperimental120 ? 120
        : DLedRates.Contains((int)Math.Round(DLedMaxHz)) ? (int)Math.Round(DLedMaxHz)
        : (int)Math.Clamp(Math.Round(DLedMaxHz), 1, 60);
    /// <summary>Unused since Phase 7 (the mouse no longer receives frames); kept so older config files still load.</summary>
    public double MouseMaxHz { get; set; } = 1.0;
    /// <summary>Only two onboard LEDs share the USB pipe with the 27-LED strip, so they get the smaller share.</summary>
    public double MotherboardMaxHz { get; set; } = 20;
    /// <summary>
    /// RAM updates per second during animated effects. Each RAM frame is about 16.6 ms of SMBus bus time, and PawnIO's
    /// SmbusI801 module spins the CPU for the whole transfer (Phase 8 profile), so this rate sets HwSuite's CPU cost
    /// almost on its own. Still effects send nothing after the first frame at any rate.
    /// </summary>
    public double RamMaxHz { get; set; } = DefaultRamHz;
    /// <summary>
    /// RAM "Onboard" mode: Breathing and Gradient Wave run on the ENE controllers themselves (set once per change, then no
    /// SMBus traffic and no CPU), instead of being streamed. Not synchronised with the other devices.
    /// </summary>
    public bool RamOnboardEffects { get; set; } = false;
    /// <summary>Effect and output state to restore when Lights Out is pressed again (null while the lights are on).</summary>
    [JsonIgnore] public EffectKind? LightsOutRestoreEffect { get; set; }
    /// <summary>Saved form of <see cref="LightsOutRestoreEffect"/> (a scene's classic fallback; see <see cref="SavedEffect"/>).</summary>
    [JsonPropertyName("LightsOutRestoreEffect")]
    public EffectKind? SavedRestoreEffect
    {
        get => LightsOutRestoreEffect is EffectKind e ? EffectInfo.ClassicFallback(e) : null;
        set { if (!(LightsOutRestoreEffect is EffectKind cur && EffectInfo.IsScene(cur))) LightsOutRestoreEffect = value; }
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EffectKind? LightsOutRestoreScene
    {
        get => LightsOutRestoreEffect is EffectKind e && EffectInfo.IsScene(e) ? EffectInfo.SceneFallback(e) : null;
        set { if (value is EffectKind s && EffectInfo.IsScene(s) && !(LightsOutRestoreEffect is EffectKind cur && EffectInfo.IsNewScene(cur))) LightsOutRestoreEffect = s; }
    }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EffectKind? LightsOutRestoreScene2
    {
        get => LightsOutRestoreEffect is EffectKind e && EffectInfo.IsNewScene(e) ? e : null;
        set { if (value is EffectKind s && EffectInfo.IsNewScene(s)) LightsOutRestoreEffect = s; }
    }
    public bool? LightsOutRestoreEnabled { get; set; }
    public const int DefaultRamHz = 10;
    public static readonly int[] RamRates = { 8, 10, 12, 15 };
    [JsonIgnore] public int EffectiveRamHz => (int)Math.Clamp(Math.Round(RamMaxHz), 4, 20);
}

public sealed class AppConfig
{
    public int Version { get; set; } = 4;
    public bool FanControlEnabled { get; set; } = false;
    /// <summary>Stop EasyTune / GService / RGB Fusion at launch (and when control is switched on) so they cannot fight over the chips. Only acts while fan or RGB control is enabled.</summary>
    public bool AutoStopGigabyte { get; set; } = true;
    /// <summary>On a normal exit (and from the watchdog after a crash), start again only the Gigabyte services HwSuite itself stopped.</summary>
    public bool RestoreGigabyteOnExit { get; set; } = true;
    /// <summary>
    /// Set by the owner after the keyboard test write showed the expected colour. Until then the only keyboard write is
    /// the test the owner clicks; afterwards lighting changes are synced to the keyboard (once per settled change).
    /// </summary>
    public bool KeyboardVerified { get; set; } = false;
    /// <summary>
    /// Set by the owner after watching the keyboard follow live frames (the vendor's volatile music/real-time data) in the
    /// live test. Until then the keyboard uses only the stored command (onboard effects, own timing). Requires KeyboardVerified.
    /// </summary>
    public bool KeyboardLiveVerified { get; set; } = false;
    /// <summary>Runtime only: the owner pressed "Try live frames" and is looking; becomes KeyboardLiveVerified on "Yes".</summary>
    [JsonIgnore] public bool KeyboardLiveTrial { get; set; }
    /// <summary>
    /// Sleep timer (vendor index 1..5) the owner explicitly chose for keyboard lighting commands, used only when no vendor
    /// profile exists (every lighting command also sets the sleep timer). Null: no choice made, so nothing is written.
    /// </summary>
    public byte? KeyboardSleepChoice { get; set; }
    /// <summary>Global Lights Out hotkey, e.g. "Ctrl+Shift+F12". Empty disables it.</summary>
    public string LightsOutHotkey { get; set; } = "Ctrl+Shift+F12";
    public bool StartMinimized { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public string Preset { get; set; } = "Balanced";
    /// <summary>Appearance: "Vanta" (default, also for configs saved before Phase 11), "Obsidian" or "Porcelain". Display only.</summary>
    public string Theme { get; set; } = "Vanta";
    public List<ChannelConfig> Channels { get; set; } = new();
    public RgbConfig Rgb { get; set; } = new();
    /// <summary>Lighting > Arrange devices: where the device groups sit (Gradient Wave follows it once arranged).</summary>
    public DeviceArrangement Arrangement { get; set; } = new();
    /// <summary>The owner's saved lighting looks (effect, colours, brightness, speed only). Starter looks are built in, not stored.</summary>
    public List<LightingLook> Looks { get; set; } = new();
    /// <summary>1.18: YUNZII AL80 keyboard screen, lighting and keymap (Hardware\Al80). Older builds ignore this section.</summary>
    public Al80Config Al80 { get; set; } = new();
    /// <summary>1.22: what VANTA does while a game runs (Deadlock: full lighting brightness and a stats panel on the AL80). Older builds ignore this section.</summary>
    public GameProfilesConfig Games { get; set; } = new();

    [JsonIgnore]
    public object Sync { get; } = new();
}

/// <summary>
/// 1.22 game profiles. While a game's process runs, VANTA raises the lighting brightness and (for Deadlock) draws the player's
/// rank and recent matches on the AL80 screen. Everything is undone when the game exits. Nothing is fetched while the game is closed.
/// </summary>
public sealed class GameProfilesConfig
{
    /// <summary>Master switch for the Deadlock profile.</summary>
    public bool DeadlockEnabled { get; set; } = true;
    /// <summary>Process names that mean Deadlock is running (with or without ".exe"). The installed game is deadlock.exe; older builds were project8.exe.</summary>
    public List<string> DeadlockExes { get; set; } = new() { "deadlock.exe", "project8.exe" };
    /// <summary>Lighting brightness (0..1) while the game runs. A brightness already at or above this is left alone.</summary>
    public double DeadlockBrightness { get; set; } = 1.0;
    /// <summary>Show the stats panel on the AL80 screen (needs the AL80 control switch on and the flash slots confirmed full, like every picture).</summary>
    public bool DeadlockStats { get; set; } = true;
    /// <summary>Steam account number (the 32-bit one) for the Deadlock API. Null: the Steam account that signed in most recently on this PC.</summary>
    public long? DeadlockAccountId { get; set; }
    /// <summary>Minutes between stats refreshes while the game runs.</summary>
    public int DeadlockRefreshMin { get; set; } = 3;
}

/// <summary>AL80 settings. Strings, not enums, so a later value never resets an older build's config.</summary>
public sealed class Al80Config
{
    /// <summary>Master switch: VANTA opens the AL80 at all. Off by default until the owner turns it on.</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>"home" (clock + gauge packets, no pictures), "live" (System panel pictures), "music" (1.21: Spotify cover + title as a picture on each track change), "off" (screen untouched).</summary>
    public string Mode { get; set; } = "home";
    /// <summary>Home mode: keep the keyboard clock set.</summary>
    public bool ClockSync { get; set; } = true;
    /// <summary>Home mode: battery icon as a load meter: "off", "cpu", "gpu", "ram".</summary>
    public string Gauge { get; set; } = "cpu";
    /// <summary>Live mode: minimum seconds between pictures (one takes about 1.2 s; 0.9 fps is the ceiling).</summary>
    public double LiveIntervalSec { get; set; } = 3;
    /// <summary>
    /// The owner's keyboard has its 8 flash picture slots filled, so further uploads stay in RAM (storage test 2026-11-12).
    /// Live pictures are only sent while this is true; it becomes false if the owner reports clearing pictures elsewhere.
    /// </summary>
    public bool FlashSlotsFilled { get; set; } = false;
    public string? StorageNote { get; set; }
    public long LifetimePictures { get; set; }
    public long LifetimeGifUploads { get; set; }
    /// <summary>Lighting sync: the AL80's separate 3-LED light (rgblight, VIA channel 2) follows VANTA too, also during per-key lighting (1.19).</summary>
    public bool SyncSideBar { get; set; } = true;
    /// <summary>
    /// 1.19.0: on firmware with YUNZII's SignalRGB handler (USB rev 0x0125 stock, 0x0122) VANTA streams a colour per key instead of one colour for the
    /// board. Off: the single-colour VIA path is used even on that firmware. Other firmware (0x0119) always uses the VIA path.
    /// </summary>
    public bool PerKey { get; set; } = true;
}
