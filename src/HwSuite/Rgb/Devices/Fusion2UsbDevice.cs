using System.Text;
using HidSharp;
using HwSuite.Core;

namespace HwSuite.Lighting.Devices;

/// <summary>
/// Gigabyte RGB Fusion 2 USB controller (ITE IT5701/IT5702, VID 048D PID 5702) on the B660M DS3H.
/// Packet layout, command bytes and the colour byte order were read from OpenRGB's
/// GigabyteRGBFusion2USBController sources (64-byte feature reports, report id 0xCC, usage page 0xFF89 / usage 0xCC).
/// This class never issues the "save to flash" command (0x5E): everything it does is volatile.
/// The HID handle is shared by the onboard-LED sink and the D_LED strip sink, so every report goes through one lock.
/// </summary>
public sealed class Fusion2UsbDevice : IDisposable
{
    public const int Vid = 0x048D;
    public static readonly int[] Pids = { 0x5702, 0x8297, 0x8950, 0x5711 };
    const byte ReportId = 0xCC;
    const int PacketLength = 64;
    const ushort UsagePage = 0xFF89, Usage = 0xCC;
    const byte HdrDLed1Argb = 0x58;      // HDR_D_LED1_ARGB
    const int LedsPerStripPacket = 19;   // sizeof(PktRGB.leds) / 3

    // GB_FUSION2_LED_IDX values for the B660M DS3H DDR4 layout (OpenRGB it5702_11): D_LED=HDR_D_LED1, LED_C=LED2, PCI-E Accent=LED4.
    public const int LedC = 1;
    public const int PcieAccent = 3;

    readonly object _io = new();
    HidStream? _stream;
    uint _effectZoneMask;
    bool _stripEffectDisabledSent;
    readonly byte[]?[] _lastStripPacket = new byte[]?[16];
    readonly byte[] _argbEnum = new byte[6];

    public HidInfo? Info { get; private set; }
    public string ProductString { get; private set; } = "";
    public uint FirmwareRaw { get; private set; }
    public uint ChipId { get; private set; }
    public byte DeviceNum { get; private set; }
    public byte SupportFlags { get; private set; }
    /// <summary>Raw calibration word RGB Fusion stored in the controller for D_LED1 (report offset 44).</summary>
    public uint StripCalibrationWord { get; private set; }
    public int ProductId => Info?.Pid ?? 0;
    public bool IsOpen => _stream != null;
    public long Writes { get; private set; }
    public long WriteErrors { get; private set; }
    public long StripPacketsSent { get; private set; }
    public long StripPacketsSkipped { get; private set; }

    /// <summary>LED capacity class the controller currently reports for D_LED1 (0=32, 1=64, 2=256, 3=512, 4=1024).</summary>
    public int ReportedStripCapacityEnum => _argbEnum[0] & 0x0F;

    public static List<HidInfo> Enumerate()
    {
        var list = new List<HidInfo>();
        foreach (var pid in Pids) list.AddRange(HidEnumerator.Find(Vid, pid));
        return list;
    }

    public static HidInfo? Pick(List<HidInfo> all) =>
        all.FirstOrDefault(i => i.HasUsage(UsagePage, Usage) && i.MaxFeature >= PacketLength) ??
        all.FirstOrDefault(i => i.MaxFeature >= PacketLength);

    public bool Open(out string error)
    {
        error = "";
        var all = Enumerate();
        var pick = Pick(all);
        if (pick == null) { error = "IT5701/5702 HID interface not found"; return false; }
        if (!pick.Device.TryOpen(out var stream)) { error = "could not open " + pick.Path + " (in use by another program?)"; return false; }
        stream.ReadTimeout = 500; stream.WriteTimeout = 500;
        _stream = stream; Info = pick;
        if (!RefreshHardwareInfo()) { error = "controller did not answer the 0x60 info request"; Close(); return false; }
        return true;
    }

    bool SendFeature(byte[] buffer)
    {
        lock (_io)
        {
            try { _stream!.SetFeature(buffer); Writes++; return true; }
            catch (Exception ex) { WriteErrors++; Log.Warn("Fusion2 SetFeature failed: " + ex.Message); return false; }
        }
    }

    bool SendCc(byte a, byte b = 0, byte c = 0)
    {
        var buf = new byte[PacketLength];
        buf[0] = ReportId; buf[1] = a; buf[2] = b; buf[3] = c;
        return SendFeature(buf);
    }

    /// <summary>Read-only info query: send 0x60, read the 64-byte IT8297Report back.</summary>
    public bool RefreshHardwareInfo()
    {
        if (_stream == null) return false;
        lock (_io)
        {
            if (!SendCc(0x60)) return false;
            var buf = new byte[PacketLength];
            buf[0] = ReportId;
            try { _stream.GetFeature(buf); }
            catch (Exception ex) { Log.Warn("Fusion2 GetFeature failed: " + ex.Message); return false; }

            DeviceNum = buf[2];
            FirmwareRaw = BitConverter.ToUInt32(buf, 4);
            _argbEnum[0] = (byte)(buf[8] & 0x0F); _argbEnum[1] = (byte)((buf[8] >> 4) & 0x0F);
            _argbEnum[2] = (byte)(buf[9] & 0x0F); _argbEnum[3] = (byte)((buf[9] >> 4) & 0x0F);
            _argbEnum[4] = (byte)(buf[10] & 0x0F); _argbEnum[5] = (byte)((buf[10] >> 4) & 0x0F);
            SupportFlags = buf[11];
            ProductString = Encoding.ASCII.GetString(buf, 12, 28).TrimEnd('\0', ' ');
            StripCalibrationWord = BitConverter.ToUInt32(buf, 44);
            ChipId = BitConverter.ToUInt32(buf, 56);
            return ProductString.Length > 0;
        }
    }

    /// <summary>OpenRGB's init sequence: lamp-array off (if supported), controller reset, beat off.</summary>
    public void InitializeLighting()
    {
        lock (_io)
        {
            if ((SupportFlags & 0x02) != 0) SendCc(0x48, 0);
            for (byte reg = 0x20; reg <= 0x27; reg++) SendCc(reg);
            SendCc(0x28, 0xFF, 0x00);   // fast apply
            SendCc(0x31, 0);            // beat off
            _effectZoneMask = 0;
            // the reset above can re-enable built-in strip effects, so the strip must be prepared again
            _stripEffectDisabledSent = false;
            Array.Clear(_lastStripPacket);
        }
    }

    /// <summary>Static effect on one onboard LED (PktEffect, colour packed B,G,R at offset 14).</summary>
    public bool SetLed(int led, Rgb color, byte brightness = 255)
    {
        if (_stream == null || led < 0 || led > 7) return false;
        var buf = new byte[PacketLength];
        buf[0] = ReportId;
        buf[1] = (byte)(0x20 + led);
        BitConverter.GetBytes(1u << led).CopyTo(buf, 2);   // zone0
        buf[11] = 1;                                        // EFFECT_STATIC
        buf[12] = brightness;                               // max brightness
        buf[14] = color.B; buf[15] = color.G; buf[16] = color.R;
        lock (_io) { _effectZoneMask |= 1u << led; }
        return SendFeature(buf);
    }

    /// <summary>Apply the pending zone mask (PktEffectApply: 0xCC 0x28 zone_sel0).</summary>
    public bool Apply()
    {
        if (_stream == null) return false;
        uint mask;
        lock (_io) { mask = _effectZoneMask; _effectZoneMask = 0; }
        var buf = new byte[PacketLength];
        buf[0] = ReportId; buf[1] = 0x28;
        BitConverter.GetBytes(mask).CopyTo(buf, 2);
        return SendFeature(buf);
    }

    // ---- D_LED addressable strip -------------------------------------------------------------------------------

    /// <summary>OpenRGB LedCountToEnum: capacity classes 32 / 64 / 256 / 512 / 1024.</summary>
    public static int CapacityEnumFor(int leds) => leds <= 32 ? 0 : leds <= 64 ? 1 : leds <= 256 ? 2 : leds <= 512 ? 3 : 4;

    /// <summary>
    /// Byte offsets of R, G, B inside each 3-byte LED slot. The channel order name is the order of the bytes on the wire,
    /// exactly as OpenRGB's calibration words encode it (RGB = 0x00000102, GRB = 0x00010002, BGR = 0x00020100).
    /// </summary>
    public static (int r, int g, int b) ByteOffsets(ChannelOrder order) => order switch
    {
        ChannelOrder.Rgb => (0, 1, 2),
        ChannelOrder.Bgr => (2, 1, 0),
        ChannelOrder.Rbg => (0, 2, 1),
        ChannelOrder.Gbr => (2, 0, 1),
        ChannelOrder.Brg => (1, 2, 0),
        _ => (1, 0, 2)   // GRB: standard WS2812B
    };

    /// <summary>Decode a stored calibration word to its channel-order name (e.g. "GRB"), or null when it is not a valid permutation.</summary>
    public static string? DecodeCalibration(uint word)
    {
        int r = (byte)(word >> 16), g = (byte)(word >> 8), b = (byte)word;
        if (r > 2 || g > 2 || b > 2 || r == g || r == b || g == b) return null;
        var chars = new char[3];
        chars[r] = 'R'; chars[g] = 'G'; chars[b] = 'B';
        return new string(chars);
    }

    public static ChannelOrder? OrderFromName(string? name) => name switch
    {
        "GRB" => ChannelOrder.Grb, "RGB" => ChannelOrder.Rgb, "BGR" => ChannelOrder.Bgr,
        "RBG" => ChannelOrder.Rbg, "GBR" => ChannelOrder.Gbr, "BRG" => ChannelOrder.Brg, _ => null
    };

    /// <summary>
    /// Channel-order check. With the strip set to <paramref name="current"/>, HwSuite showed pure red, then pure green,
    /// then pure blue; the owner reports the colour seen each time ('R', 'G' or 'B'). Every wire position is then
    /// known, so the order that makes all three correct follows directly. Returns null when the three answers are not
    /// a permutation of R, G and B (then it is not a byte-order problem, or a colour was misread).
    /// </summary>
    public static ChannelOrder? SolveOrder(ChannelOrder current, char sawWhenRed, char sawWhenGreen, char sawWhenBlue)
    {
        var seen = new[] { char.ToUpperInvariant(sawWhenRed), char.ToUpperInvariant(sawWhenGreen), char.ToUpperInvariant(sawWhenBlue) };
        if (seen.Any(c => c is not ('R' or 'G' or 'B')) || seen.Distinct().Count() != 3) return null;
        var (ro, go, bo) = ByteOffsets(current);
        var wire = new char[3];
        wire[ro] = seen[0]; wire[go] = seen[1]; wire[bo] = seen[2];
        return OrderFromName(new string(wire));
    }

    /// <summary>
    /// Builds the PktRGB feature reports for one frame: [0]=0xCC, [1]=0x58 (HDR_D_LED1_ARGB), [2..3]=byte offset (LE),
    /// [4]=byte count, then up to 19 LEDs of 3 bytes starting at [5]. Identical chunking to OpenRGB's SetStripColors.
    /// </summary>
    public static List<byte[]> BuildStripPackets(ReadOnlySpan<Rgb> colors, ChannelOrder order)
    {
        var (ro, go, bo) = ByteOffsets(order);
        var packets = new List<byte[]>();
        int sent = 0, k = 0, left = colors.Length;
        while (left > 0)
        {
            int n = Math.Min(LedsPerStripPacket, left);
            left -= n;
            var p = new byte[PacketLength];
            p[0] = ReportId; p[1] = HdrDLed1Argb;
            p[2] = (byte)(sent & 0xFF); p[3] = (byte)((sent >> 8) & 0xFF);
            p[4] = (byte)(n * 3);
            for (int i = 0; i < n; i++, k++)
            {
                int o = 5 + i * 3;
                p[o + ro] = colors[k].R; p[o + go] = colors[k].G; p[o + bo] = colors[k].B;
            }
            sent += n * 3;
            packets.Add(p);
        }
        return packets;
    }

    /// <summary>
    /// Prepare the D_LED1 output: raise the LED capacity class if the controller reports a smaller one (never lowers it),
    /// and disable the built-in strip effect (command 0x32, bit 0x01) so direct colour data is shown.
    /// </summary>
    public void PrepareStrip(int ledCount)
    {
        if (_stream == null) return;
        lock (_io)
        {
            int need = CapacityEnumFor(ledCount);
            if (need > ReportedStripCapacityEnum)
            {
                var buf = new byte[PacketLength];
                buf[0] = ReportId; buf[1] = 0x34;
                var e = (byte[])_argbEnum.Clone(); e[0] = (byte)need;
                buf[2] = (byte)((e[1] << 4) | e[0]); buf[3] = (byte)((e[3] << 4) | e[2]); buf[4] = (byte)((e[5] << 4) | e[4]);
                SendFeature(buf);
                Log.Info($"D_LED capacity raised to class {need} for {ledCount} LEDs");
                RefreshHardwareInfo();
            }
            if (!_stripEffectDisabledSent)
            {
                SendCc(0x32, 0x01);       // bit set = built-in effect disabled for D_LED1
                Thread.Sleep(50);         // reference implementation waits 50 ms here
                _stripEffectDisabledSent = true;
            }
        }
    }

    /// <summary>Send one strip frame. Packets identical to the previous frame's are not retransmitted.</summary>
    public bool SetStripColors(ReadOnlySpan<Rgb> colors, ChannelOrder order)
    {
        if (_stream == null || colors.Length == 0) return false;
        var packets = BuildStripPackets(colors, order);
        bool ok = true;
        lock (_io)
        {
            if (!_stripEffectDisabledSent) PrepareStrip(colors.Length);
            for (int i = 0; i < packets.Count && i < _lastStripPacket.Length; i++)
            {
                var last = _lastStripPacket[i];
                if (last != null && last.AsSpan().SequenceEqual(packets[i])) { StripPacketsSkipped++; continue; }
                if (SendFeature(packets[i])) { _lastStripPacket[i] = packets[i]; StripPacketsSent++; }
                else { ok = false; _lastStripPacket[i] = null; }
            }
        }
        return ok;
    }

    public void Close()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
    }

    public void Dispose() => Close();
}
