using System.Diagnostics;
using HidSharp;
using HwSuite.Core;

namespace HwSuite.Lighting.Devices;

/// <summary>
/// One onboard lighting setting for the Glorious Model O Wireless. Byte values follow Glorious CORE's own code
/// (MouseV2ProDeviceHandler, used for "Model O Wireless"): effect codes from LEDEffectBufferValue$1, speed byte
/// (105 - speed%) / 5, brightness byte = brightness% / 100 * 255.
/// </summary>
public sealed record MouseLightingState(byte Effect, Rgb? Color, byte Speed, byte Brightness, string Description, bool Fallback)
{
    public const byte EffectOff = 0, EffectStatic = 4, EffectBreathingSingleColor = 5;
    /// <summary>Speed byte sent with effects that do not animate (the value for 50 %).</summary>
    public const byte NeutralSpeed = 11;

    /// <summary>Two settings are the same if the mouse would receive the same reports; descriptions do not count.</summary>
    public bool Equals(MouseLightingState? other) =>
        other is not null && Effect == other.Effect && Color == other.Color && Speed == other.Speed && Brightness == other.Brightness && Synced == other.Synced;

    public override int GetHashCode() => HashCode.Combine(Effect, Color, Speed, Brightness, Synced);

    /// <summary>Breathing locked to VANTA's (MouseSync): the write is timed to the shared clock and repeated now and then.</summary>
    public bool Synced { get; init; }

    /// <summary>Period of the onboard breathing for a synced setting, in seconds.</summary>
    public double SyncPeriodSeconds => MouseSync.PeriodSeconds(Speed);

    /// <summary>
    /// The mouse runs its own effects, so HwSuite maps its effect to an onboard one once. Effects the mouse cannot run are
    /// shown as a fixed colour and are never re-sent as frames or with temperature changes.
    /// </summary>
    public static MouseLightingState? From(RgbConfig rgb, bool ignoreOutputSwitch = false)
    {
        if ((!rgb.Enabled && !ignoreOutputSwitch) || !rgb.Mouse) return null;
        byte bright = (byte)Math.Floor(Math.Clamp(rgb.Brightness, 0, 1) * 255);
        int speedUi = (int)Math.Round(Math.Clamp(rgb.Speed, 0, 1) * 100);
        byte speed = (byte)((105 - speedUi) / 5);
        var a = Rgb.FromInt(rgb.ColorA);
        if (EffectInfo.IsScene(rgb.Effect))
        {
            // no safe live-update path exists for this mouse, so a scene is never streamed to it: it gets one calm onboard
            // setting (a companion), stored once per change like any other effect
            var cc = Companion.For(rgb);
            var cfg = rgb.WithEffect(cc.Breathing ? EffectKind.Breathing : EffectKind.Static);
            cfg.ColorA = cc.Color; cfg.Speed = cc.Speed;
            var st = From(cfg, ignoreOutputSwitch)!;
            return st with { Description = $"companion for {EffectInfo.Label(rgb.Effect)}: {cc.Text} (independent onboard effect; does not follow the shared animation)", Fallback = true };
        }
        // Speed only matters for animated effects and brightness not at all when off, so they are normalised there:
        // moving those sliders then sends nothing.
        return rgb.Effect switch
        {
            EffectKind.Static => new(EffectStatic, a, NeutralSpeed, bright, "static colour (onboard)", false),
            EffectKind.Breathing when MouseSync.Active(rgb) => new(EffectBreathingSingleColor, a, MouseSync.SpeedByte(rgb.Speed), bright,
                $"single-colour breathing locked to VANTA's ({MouseSync.PeriodSeconds(MouseSync.SpeedByte(rgb.Speed)):0.000} s cycle, timed write)", false) { Synced = true },
            EffectKind.Breathing => new(EffectBreathingSingleColor, a, speed, bright, "single-colour breathing (onboard effect, own timing: not synchronised)", false),
            EffectKind.Stealth => new(EffectOff, null, NeutralSpeed, 255, "LEDs off (onboard)", false),
            EffectKind.GradientWave => new(EffectStatic, a, NeutralSpeed, bright, "Gradient Wave is not available on the mouse: fixed primary colour", true),
            _ => new(EffectStatic, a, NeutralSpeed, bright, "Thermal Reactive is not available on the mouse: fixed primary colour, not updated with temperature", true)
        };
    }
}

/// <summary>
/// Glorious Model O Wireless (VID 258A, receiver PID 2022 / cable PID 2011), vendor-defined collection usage page 0xFFFF
/// with 65-byte feature reports (report ID 0). Lighting is three feature reports, 30 ms apart, exactly as Glorious CORE
/// sends them: effect + colours, wired brightness, wireless brightness. The effect report addresses onboard profile 1,
/// so it is treated as a write to the mouse's stored settings and kept rare (see MouseLightingController).
/// </summary>
public sealed class GloriousWirelessDevice : IDisposable
{
    public const int Vid = 0x258A;
    public const int PidDongle = 0x2022, PidCable = 0x2011;
    const int PacketSize = 65;
    const int GapMs = 30;   // Glorious CORE: sendReportToDevice(device, buffer, 30) for every lighting report

    HidStream? _stream;
    public HidInfo? Info { get; private set; }
    public bool IsCable => Info?.Pid == PidCable;
    public bool IsOpen => _stream != null;
    public string FirmwareText { get; private set; } = "";
    /// <summary>Lighting applications (each = 3 feature reports).</summary>
    public long Writes { get; private set; }
    public long ReportsSent { get; private set; }
    public double LastReportMs { get; private set; }
    public double MaxReportMs { get; private set; }

    public static List<HidInfo> Enumerate()
    {
        var list = HidEnumerator.Find(Vid, PidDongle);
        list.AddRange(HidEnumerator.Find(Vid, PidCable));
        return list;
    }

    /// <summary>Pick the vendor-defined collection that accepts 65-byte feature reports (usage page 0xFFFF).</summary>
    public static HidInfo? Pick(List<HidInfo> all, int pid) =>
        all.Where(i => i.Pid == pid && i.MaxFeature == PacketSize)
           .OrderByDescending(i => i.Usages.Any(u => u.Page == 0xFFFF))
           .FirstOrDefault();

    public bool Open(HidInfo info, out string error)
    {
        error = "";
        if (info.MaxFeature != PacketSize) { error = $"unexpected feature report length {info.MaxFeature}"; return false; }
        if (!info.Device.TryOpen(out var stream)) { error = "could not open " + info.Path; return false; }
        stream.ReadTimeout = 500; stream.WriteTimeout = 500;
        _stream = stream; Info = info;
        return true;
    }

    /// <summary>Read-only firmware query (same bytes as Glorious CORE PrepareReadFWVersionBuffer).</summary>
    public string ReadFirmware()
    {
        if (_stream == null) return "";
        try
        {
            var send = new byte[PacketSize];
            if (IsCable) send[0x03] = 0x02;
            send[0x04] = 0x03; send[0x06] = 0x81;
            _stream.SetFeature(send);
            Thread.Sleep(50);
            var recv = new byte[PacketSize];
            _stream.GetFeature(recv);
            FirmwareText = $"{recv[7]}.{recv[8]}.{recv[9]}.{recv[10]}";
        }
        catch (Exception ex) { FirmwareText = "n/a (" + ex.Message + ")"; }
        return FirmwareText;
    }

    public static byte[] BuildEffectReport(MouseLightingState s) =>
        BuildEffectReport(s.Effect, s.Speed, s.Color.HasValue && s.Effect != MouseLightingState.EffectOff ? new[] { s.Color.Value } : Array.Empty<Rgb>());

    /// <summary>Highest effect code in Glorious CORE's LEDEffectBufferValue$1 (Wave).</summary>
    public const byte MaxEffect = 8;
    public const byte EffectSeamlessBreathing = 2, EffectBreathingMulti = 3, EffectRave = 7, EffectWave = 8;
    /// <summary>Glorious CORE never sends more than 7 colours (its effect reset loads a 7-colour palette); more is untested.</summary>
    public const int MaxColors = 7;

    /// <summary>
    /// Effect + colours report as PrepareLEDEffectAndColorsBuffer builds it: length code colours * 3 + 5, colours from
    /// byte 12. For Seamless Breathing CORE always writes red into the first slot, whatever colours it holds.
    /// </summary>
    public static byte[] BuildEffectReport(byte effect, byte speed, IReadOnlyList<Rgb> colors)
    {
        if (effect > MaxEffect) throw new ArgumentOutOfRangeException(nameof(effect), $"effect code {effect} is not one Glorious CORE sends (0-{MaxEffect})");
        if (colors.Count > MaxColors) throw new ArgumentOutOfRangeException(nameof(colors), $"{colors.Count} colours; at most {MaxColors}");
        var b = new byte[PacketSize];
        b[3] = 0x02;
        b[4] = (byte)(colors.Count * 3 + 5);
        b[5] = 0x02;
        b[6] = 0x00;
        b[7] = 0x01;          // onboard profile 1 (Glorious CORE: index of the current profile + 1; OpenRGB also uses 1)
        b[8] = 0xFF;
        b[9] = effect;
        b[10] = 0x00;
        b[11] = speed;
        if (effect == EffectSeamlessBreathing && colors.Count > 0) { b[12] = 0xFF; b[13] = 0; b[14] = 0; }
        else for (int i = 0; i < colors.Count; i++) { b[12 + i * 3] = colors[i].R; b[13 + i * 3] = colors[i].G; b[14 + i * 3] = colors[i].B; }
        return b;
    }

    public static byte[] BuildBrightnessReport(byte brightness, bool wired)
    {
        var b = new byte[PacketSize];
        b[3] = 0x02; b[4] = 0x02; b[5] = 0x02; b[6] = 0x02;
        b[7] = (byte)(wired ? 0x01 : 0x00);
        b[8] = brightness;
        return b;
    }

    /// <summary>Send one lighting setting (3 feature reports). Returns false on any HID error.</summary>
    public bool Apply(MouseLightingState s, out string error) => Apply(BuildEffectReport(s), s.Brightness, out error);

    /// <summary>When the most recent lighting application sent its effect report (for lining up test videos).</summary>
    public DateTime LastApplyStartUtc { get; private set; }

    /// <summary>Send a prepared effect report followed by both brightness reports, 30 ms apart.</summary>
    public bool Apply(byte[] effectReport, byte brightness, out string error)
    {
        error = "";
        if (_stream == null) { error = "not open"; return false; }
        if (effectReport.Length != PacketSize) { error = "effect report must be 65 bytes"; return false; }
        var reports = new[] { effectReport, BuildBrightnessReport(brightness, true), BuildBrightnessReport(brightness, false) };
        LastApplyStartUtc = DateTime.UtcNow;
        try
        {
            foreach (var r in reports)
            {
                var sw = Stopwatch.StartNew();
                _stream.SetFeature(r);
                LastReportMs = sw.Elapsed.TotalMilliseconds;
                MaxReportMs = Math.Max(MaxReportMs, LastReportMs);
                ReportsSent++;
                Thread.Sleep(GapMs);
            }
            Writes++;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            Log.Warn("Glorious write failed: " + error);
            return false;
        }
    }

    /// <summary>
    /// Live colour (mouse stream test): one effect report on its own, no brightness reports and no vendor gap, the way
    /// SignalRGB's released plugin drives this mouse through the receiver every frame. Returns false on any HID error.
    /// </summary>
    public bool SendReport(byte[] report, out string error)
    {
        error = "";
        if (_stream == null) { error = "not open"; return false; }
        if (report.Length != PacketSize) { error = "report must be 65 bytes"; return false; }
        try
        {
            var sw = Stopwatch.StartNew();
            _stream.SetFeature(report);
            LastReportMs = sw.Elapsed.TotalMilliseconds;
            MaxReportMs = Math.Max(MaxReportMs, LastReportMs);
            ReportsSent++;
            return true;
        }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    public void Close()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
    }

    public void Dispose() => Close();
}

/// <summary>
/// iLovbee i87 keyboard probe. VID 258A / PID 013C is NOT in OpenRGB's supported list and the OpenRGB source
/// documents that reused Sinowealth VID/PIDs have bricked other keyboards, so this class is read-only by design:
/// it enumerates descriptors and never opens a write path.
/// </summary>
public static class KeyboardProbe
{
    public const int Vid = 0x258A, Pid = 0x013C;
    public static List<HidInfo> Enumerate() => HidEnumerator.Find(Vid, Pid);
}
