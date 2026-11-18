using System.Diagnostics;
using HwSuite.Core;

namespace HwSuite.Lighting.Devices;

/// <summary>How faithfully the keyboard can follow an effect.</summary>
public enum KeyboardSync
{
    /// <summary>The keyboard shows exactly what the effect asks for (a fixed colour).</summary>
    Exact,
    /// <summary>A native keyboard effect runs on its own timing: similar, not synchronised.</summary>
    Native,
    /// <summary>Not available on the keyboard: a fixed colour is shown instead.</summary>
    Fallback,
    /// <summary>LEDs at brightness 0.</summary>
    Off
}

/// <summary>
/// One onboard lighting setting for the iLovbee i87. Values are the vendor's own units (DeviceDriver.exe, CDevRKKB):
/// mode index 0..20 (led.xml tc_led_mode1..21), speed and brightness as stored in the vendor profile (pro.dct), and a
/// random-colour flag. The keyboard's sleep timer is not part of the setting: it comes from the vendor profile
/// (<see cref="ILovbeeProfile"/>) and is sent back unchanged.
/// </summary>
public sealed record KeyboardLightingState(byte Mode, Rgb Color, byte Speed, byte Brightness, byte Random, string Description, bool Fallback)
{
    public KeyboardSync Sync { get; init; } = KeyboardSync.Exact;

    public bool Equals(KeyboardLightingState? o) =>
        o is not null && Mode == o.Mode && Color == o.Color && Speed == o.Speed && Brightness == o.Brightness && Random == o.Random;
    public override int GetHashCode() => HashCode.Combine(Mode, Color, Speed, Brightness, Random);

    /// <summary>Mode names from the vendor's led.xml (tc_led_mode1..21); index = position in this array.</summary>
    public static readonly string[] ModeNames =
    {
        "Neon Stream", "Ripples Shining", "Rotating Windmill", "Sine Wave", "Rainbow Roulette", "Stars Twinkle",
        "Layer Upon Layer", "Rich And Honored", "Marquee Effect", "Rotating Storm", "Serpentine Horse Race", "Retro Snake",
        "Diagonal Transformation", "Customize", "Ambilight", "Streamer", "Steady", "Breathing", "Neon", "Shadow Disappear",
        "Flash Away"
    };
    public const byte ModeSteady = 16, ModeBreathing = 17;
    /// <summary>
    /// Modes the vendor marks as animated with a selectable colour (KB.ini LedOptN = "animation speed light direct random
    /// colornum": speed=1 and colornum=1). Offered as the keyboard's stand-in for Gradient Wave.
    /// </summary>
    public static readonly byte[] NativeAnimatedModes = { 0, 1, 3, 5, 6, 7, 8, 9, 10, 11, 12, 17, 19, 20 };   // 13 Customize (per-key) and 14 Ambilight (screen) excluded
    /// <summary>Vendor brightness steps 0..5. KB.ini has no LightUI/BriNum/LightHW, so DeviceDriver.exe fills its brightness table with the identity.</summary>
    public const byte MaxBrightness = 5;
    /// <summary>Speed the vendor profile stores for every mode on this PC; used only when no profile can be read.</summary>
    public const byte VendorDefaultSpeed = 4;

    public static KeyboardLightingState TestRed(byte mode, byte speed) =>
        new(mode, new Rgb(255, 0, 0), speed, MaxBrightness, 0, $"test: {ModeNames[Math.Min(mode, (byte)20)]}, red", false);

    static byte BrightnessFor(double b) => b <= 0 ? (byte)0 : (byte)Math.Clamp((int)Math.Round(b * MaxBrightness), 1, MaxBrightness);

    /// <summary>
    /// The keyboard's onboard setting for an effect. Its only lighting command is the vendor's stored "LED(Save)" setting,
    /// so it is sent once per settled change, never per frame. The direction of the vendor's speed scale is not established,
    /// so the speed saved in the vendor profile is kept rather than mapped. <paramref name="waveMode"/>: native mode to run
    /// for Gradient Wave (null: fixed primary colour).
    /// </summary>
    public static KeyboardLightingState? From(RgbConfig rgb, byte savedSpeed, bool ignoreOutputSwitch = false)
    {
        if ((!rgb.Enabled && !ignoreOutputSwitch) || !rgb.Keyboard) return null;
        var a = Rgb.FromInt(rgb.ColorA);
        byte bright = BrightnessFor(rgb.Brightness);
        int? wave = rgb.KeyboardWaveMode is int w && NativeAnimatedModes.Contains((byte)w) ? w : null;
        if (EffectInfo.IsScene(rgb.Effect))
        {
            // stored setting for a scene: its calm companion (what the keyboard falls back to between live frames, or shows
            // when live frames are not verified); the scene itself only reaches the keys as live frames
            var cc = Companion.For(rgb);
            var col = Rgb.FromInt(cc.Color);
            string what = $"Companion for {EffectInfo.Label(rgb.Effect)}: {cc.Text}";
            return cc.Breathing
                ? new(ModeBreathing, col, savedSpeed, bright, 0, what + " (native breathing, own timing)", true) { Sync = KeyboardSync.Native }
                : new(ModeSteady, col, savedSpeed, bright, 0, what, true) { Sync = KeyboardSync.Fallback };
        }
        return rgb.Effect switch
        {
            EffectKind.Static => new(ModeSteady, a, savedSpeed, bright, 0, "Steady, primary colour", false) { Sync = KeyboardSync.Exact },
            EffectKind.Breathing => new(ModeBreathing, a, savedSpeed, bright, 0, "Native breathing, primary colour (own timing)", false) { Sync = KeyboardSync.Native },
            EffectKind.Stealth => new(ModeSteady, Rgb.Black, savedSpeed, 0, 0, "Brightness 0", false) { Sync = KeyboardSync.Off },
            EffectKind.GradientWave when wave is int m => new((byte)m, a, savedSpeed, bright, 0, $"Native {ModeNames[m]} (own animation, not synchronised)", true) { Sync = KeyboardSync.Native },
            EffectKind.GradientWave => new(ModeSteady, a, savedSpeed, bright, 0, "Gradient Wave is not available: fixed primary colour", true) { Sync = KeyboardSync.Fallback },
            _ => new(ModeSteady, a, savedSpeed, bright, 0, "Thermal is not available: fixed primary colour", true) { Sync = KeyboardSync.Fallback }
        };
    }
}

/// <summary>
/// The vendor app's saved profiles, read only. Evidence (DeviceDriver.exe, see docs\phase8\keyboard-evidence.md):
/// the folder is SHGetSpecialFolderPathW(CSIDL_LOCAL_APPDATA) + "\RKKB\" + "%04X\" (PID) = %LOCALAPPDATA%\RKKB\013C;
/// pro.dct holds three 0x3E74-byte profile records (ID at +4, current mode at +0x213C, 48-byte mode records at +0x2140,
/// sleep index at +0x3E14, sent as byte 10 of the 02 29 command); curid.dct holds the active profile's ID and the vendor
/// selects the profile whose ID matches, else the first one (0x445E05). The keyboard itself is never asked for its
/// settings by the vendor for this device class (HidD_GetFeature is used only by CDev3632/CDevG5KB), so the profile is
/// the only source for the sleep timer.
/// </summary>
public sealed record ILovbeeProfile(byte Sleep, byte Speed, string Source, bool FromVendorProfile)
{
    public const int ProfileSize = 0x3E74, IdOffset = 4, ModeOffset = 0x213C, RecordsOffset = 0x2140, RecordSize = 48, SleepOffset = 0x3E14;

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RKKB", "013C", "pro.dct");

    public static string SleepText(byte s) => s switch { 1 => "10 min", 2 => "20 min", 3 => "40 min", 4 => "60 min", 5 => "off", _ => $"index {s}" };
    public static readonly byte[] SleepChoices = { 1, 2, 3, 4, 5 };

    /// <summary>
    /// Sleep index to send with a lighting command, or null (do not write). <paramref name="choice"/> is an explicit owner
    /// choice used only when no vendor profile exists; it is reported as a choice, never as "preserved".
    /// </summary>
    public static ILovbeeProfile? Read(string? path, byte? choice, out string detail)
    {
        path ??= ResolvePath();
        byte[] b;
        try
        {
            if (!File.Exists(path))
            {
                if (choice is byte c && SleepChoices.Contains(c))
                {
                    detail = $"no vendor profile: sleep timer will be set to {SleepText(c)} (your choice)";
                    return new(c, KeyboardLightingState.VendorDefaultSpeed, "your choice", false);
                }
                detail = "Sleep timer unknown: no vendor profile was found, and every lighting command also sets the sleep timer. Choose a sleep timer (or open the iLovbee app once) before VANTA writes to the keyboard.";
                return null;
            }
            b = File.ReadAllBytes(path);
        }
        catch (Exception ex) { detail = "vendor profile unreadable: " + ex.Message; return null; }
        return Parse(b, ReadCurrentId(Path.Combine(Path.GetDirectoryName(path)!, "curid.dct")), out detail);
    }

    /// <summary>%LOCALAPPDATA%\RKKB\013C\pro.dct, read through the admin share when this process's AppData is redirected (packaged-app context).</summary>
    public static string ResolvePath()
    {
        var p = DefaultPath;
        if (File.Exists(p) || AppPaths.ResolutionNote == null) return p;
        var share = AppPaths.ToAdminShare(p);
        return share != null && File.Exists(share) ? share : p;
    }

    static uint? ReadCurrentId(string path)
    {
        try { var c = File.ReadAllBytes(path); return c.Length >= 4 ? BitConverter.ToUInt32(c, 0) : null; } catch { return null; }
    }

    public static ILovbeeProfile? Parse(byte[] b, uint? currentId, out string detail)
    {
        if (b.Length != ProfileSize * 3) { detail = $"vendor profile has an unexpected size ({b.Length} bytes, expected {ProfileSize * 3}); not writing"; return null; }
        var ids = new uint[3]; var sleeps = new byte[3]; var speeds = new byte[3];
        for (int p = 0; p < 3; p++)
        {
            int baseOff = p * ProfileSize;
            ids[p] = BitConverter.ToUInt32(b, baseOff + IdOffset);
            int sleep = BitConverter.ToInt32(b, baseOff + SleepOffset);
            int mode = BitConverter.ToInt32(b, baseOff + ModeOffset);
            if (sleep is < 0 or > 5 || mode is < 0 or > 20) { detail = $"vendor profile {p + 1} has out-of-range values (sleep {sleep}, mode {mode}); not writing"; return null; }
            sleeps[p] = (byte)sleep;
            speeds[p] = b[baseOff + RecordsOffset + mode * RecordSize];
        }
        int active = currentId is uint id ? Array.IndexOf(ids, id) : -1;
        string how = active >= 0 ? "active profile (curid.dct)" : "first profile (no curid.dct match; the vendor app does the same)";
        if (active < 0) active = 0;
        detail = $"sleep timer kept at {SleepText(sleeps[active])}: vendor profile {active + 1}, {how}";
        return new(sleeps[active], speeds[active], $"vendor profile {active + 1}", true);
    }
}

/// <summary>
/// iLovbee i87 (VID 258A, PID 013C), RK protocol as implemented by the vendor's DeviceDriver.exe (CDevRKKB): the HID
/// collection with usage page 0xFF00, usage 0x0001 and 65-byte feature reports; payloads are split into 62-byte chunks,
/// each sent as feature report 0x0A [0A, chunk count, 1-based index, 62 bytes], 8 ms apart. The LED command
/// (02 29 ...) is logged by the vendor as "LED(Save)", so it is treated as a write to the keyboard's stored settings:
/// HwSuite sends it only on an explicit test or, after verification, once per settled change.
/// </summary>
public sealed class ILovbeeKeyboardDevice : IDisposable
{
    public const int Vid = 0x258A, Pid = 0x013C;
    public const int ReportLength = 65, ChunkData = 62, PayloadLength = 60;
    public const byte ReportId = 0x0A;
    public const int GapMs = 8, SettleMs = 50;

    HidSharp.HidStream? _stream;
    /// <summary>One payload at a time: the chunks of a stored setting and of a live frame must never interleave.</summary>
    readonly object _io = new();
    public HidInfo? Info { get; private set; }
    public long Writes { get; private set; }
    public long LiveWrites { get; private set; }

    /// <summary>
    /// Live frame payload, as DeviceDriver.exe's music/real-time thread builds it (CDevRKKB::SendMusicData 0x413220 called
    /// from 0x43AED8 with length 0x17A and bRGB set because KB.ini has RGBKb=1): 06 00, then 126 LEDs x R,G,B at
    /// 3 x matrix index (<see cref="ILovbeeKeyMap"/>). The vendor sends it continuously (one frame per loop, 5 ms apart plus
    /// 8 ms per chunk) and never as "LED(Save)"; entering its Music mode sends no stored command first (0x4220A9).
    /// </summary>
    public static byte[] BuildLivePayload(ReadOnlySpan<Rgb> keyColors)
    {
        var p = new byte[2 + ILovbeeKeyMap.MatrixLeds * 3];
        p[0] = 0x06; p[1] = 0x00;
        var keys = ILovbeeKeyMap.Keys;
        for (int i = 0; i < keys.Length && i < keyColors.Length; i++)
        {
            int o = 2 + keys[i].Matrix * 3;
            p[o] = keyColors[i].R; p[o + 1] = keyColors[i].G; p[o + 2] = keyColors[i].B;
        }
        return p;
    }

    /// <summary>Send one live frame (no settle wait: nothing is stored). Caller has checked the opt-in gate and ownership.</summary>
    public bool ApplyLive(byte[] payload, out string error)
    {
        error = "";
        lock (_io)
        {
            if (_stream == null) { error = "not open"; return false; }
            try
            {
                foreach (var r in Frame(payload))
                {
                    _stream.SetFeature(r);
                    PrecisionTimer.Wait(TimeSpan.FromMilliseconds(GapMs));   // the vendor's 8 ms per chunk, without 15.6 ms tick rounding
                }
                LiveWrites++;
                return true;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
        }
    }

    public static List<HidInfo> Enumerate() => HidEnumerator.Find(Vid, Pid);

    public static HidInfo? Pick(IEnumerable<HidInfo> all) =>
        all.FirstOrDefault(i => i.Pid == Pid && i.MaxFeature == ReportLength && i.HasUsage(0xFF00, 0x0001));

    /// <summary>The 60-byte LED command: 02 29 mode+1 01 speed brightness R G B random sleep, zero padded.</summary>
    public static byte[] BuildLedPayload(KeyboardLightingState s, byte sleep)
    {
        var p = new byte[PayloadLength];
        p[0] = 0x02; p[1] = 0x29;
        p[2] = (byte)(Math.Min(s.Mode, (byte)20) + 1);
        p[3] = 0x01;
        p[4] = s.Speed;
        p[5] = Math.Min(s.Brightness, KeyboardLightingState.MaxBrightness);
        p[6] = s.Color.R; p[7] = s.Color.G; p[8] = s.Color.B;   // RGBIndex is commented out in KB.ini: vendor default order R, G, B
        p[9] = s.Random;
        p[10] = sleep;
        return p;
    }

    /// <summary>Split a payload into 65-byte feature reports [0A, count, index, 62 data bytes]; unused bytes are zero.</summary>
    public static List<byte[]> Frame(byte[] payload)
    {
        int count = Math.Max(1, (payload.Length + ChunkData - 1) / ChunkData);
        var list = new List<byte[]>();
        for (int i = 0; i < count; i++)
        {
            var r = new byte[ReportLength];
            r[0] = ReportId; r[1] = (byte)count; r[2] = (byte)(i + 1);
            int n = Math.Min(ChunkData, payload.Length - i * ChunkData);
            Array.Copy(payload, i * ChunkData, r, 3, n);
            list.Add(r);
        }
        return list;
    }

    public bool Open(HidInfo info, out string error)
    {
        error = "";
        if (info.MaxFeature != ReportLength || !info.HasUsage(0xFF00, 0x0001)) { error = $"not the lighting collection ({info.UsageText}, feature {info.MaxFeature})"; return false; }
        if (!info.Device.TryOpen(out var stream)) { error = "could not open " + info.Path; return false; }
        stream.WriteTimeout = 500; stream.ReadTimeout = 500;
        _stream = stream; Info = info;
        return true;
    }

    /// <summary>Send one LED setting. Caller has checked the opt-in gate and ownership.</summary>
    public bool Apply(KeyboardLightingState s, byte sleep, out string error)
    {
        error = "";
        lock (_io)
        {
            if (_stream == null) { error = "not open"; return false; }
            try
            {
                foreach (var r in Frame(BuildLedPayload(s, sleep)))
                {
                    _stream.SetFeature(r);
                    Thread.Sleep(GapMs);
                }
                Thread.Sleep(SettleMs);
                Writes++;
                return true;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
        }
    }

    public void Dispose() { lock (_io) { try { _stream?.Dispose(); } catch { } _stream = null; } }
}
