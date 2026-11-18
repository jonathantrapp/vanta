using HwSuite.Lighting;

namespace HwSuite.Hardware.Al80;

/// <summary>One report to send, and how long the screen module needs before the next one.</summary>
public readonly record struct Al80Step(byte[] Report, double Pause);

/// <summary>
/// YUNZII AL80 (28E9:30AF) screen and VIA packets. Pure functions, no I/O. Ported from jonathantrapp/yunzii al80/protocol.py
/// and checked against the captures in snackdriven/al80-lcd (AL80_KNOWLEDGE_BASE.md) and on the owner's keyboard on 2026-11-12.
///
/// Screen report (64 bytes, sent after report ID 0): [0] opcode 0x40 announce / 0x41 data / 0x42 finish, [1..2] offset LE,
/// [3] payload length, [4..5] 16-bit additive checksum LE, [6] 0 (the keyboard echoes the report with 0x55 here), [7..] payload.
/// Module command inside a payload: A5 5A type lenHi lenLo crcHi crcLo data, crc = CRC-16/MODBUS over type lenHi lenLo.
///
/// Safety: this class can only build screen opcodes 0x40-0x42 and an allow-list of VIA commands. The firmware-update opcodes
/// 0xB0-0xB7, VIA 0x0A (EEPROM reset), 0x0B (bootloader jump), 0x06 (keymap reset) and 0x10 (macro reset) cannot be built.
/// The picture delete (type 0x0E) is deliberately absent: deleting frees a flash slot, and the next upload would be written
/// to flash again (storage test 2026-11-12: only the first 8 uploads after a clear survive a power cut).
/// </summary>
public static class Al80Protocol
{
    public const int Vid = 0x28E9, Pid = 0x30AF;
    public const ushort RawUsagePage = 0xFF60, RawUsage = 0x61;

    public const int Width = 96, Height = 160, StripHeight = 64;
    public const int FrameBytes = Width * Height * 2;
    public const int ReportSize = 64, Chunk = 56, Bank = 1024;

    public const byte OpAnnounce = 0x40, OpData = 0x41, OpFinish = 0x42;
    public const byte AckOk = 0x55, AckBusy = 0x0F;

    public const byte PkBattLevel = 0x06, PkBattState = 0x07, PkTime = 0x09, PkDate = 0x0A, PkGoHome = 0x0B, PkAddPic = 0x0C,
        PkNextPic = 0x0D, PkGoGif = 0x0F, PkEvent = 0x10, PkFrameLen = 0x11, PkGifNum = 0x12, PkGifFrame = 0x13;

    /// <summary>Storage test 2026-11-12 (owner's AL80): after a clear, cards 1-8 of 20 survived a full power cut, 9-20 did not.</summary>
    public const int FlashPictureSlots = 8;

    /// <summary>Announce settle before a still's setup. 300 ms in the vendor capture; 100 ms verified clean on 2026-11-12. 150 ms keeps a margin.</summary>
    public const double PictureAnnounceSettle = 0.15;

    public sealed record GifTarget(string Id, string Name, int Mode, int Height, int MaxFrames);
    public static readonly GifTarget Boot = new("boot", "Startup animation", 0, Height, 64);
    public static readonly GifTarget GifPage = new("page", "GIF page", 1, Height, 160);
    public static readonly GifTarget HomeStrip = new("home", "Home strip", 2, StripHeight, 42);
    public static readonly GifTarget[] GifTargets = { HomeStrip, GifPage, Boot };

    public static ushort Crc16Modbus(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    public static byte[] Report(byte op, ReadOnlySpan<byte> payload, int offset = 0, int? length = null)
    {
        if (op is not (OpAnnounce or OpData or OpFinish)) throw new ArgumentException($"opcode 0x{op:X2} is not a screen opcode");
        if (payload.Length > Chunk) throw new ArgumentException($"payload of {payload.Length} bytes does not fit one report");
        var r = new byte[ReportSize];
        r[0] = op; r[1] = (byte)offset; r[2] = (byte)(offset >> 8); r[3] = (byte)(length ?? payload.Length);
        payload.CopyTo(r.AsSpan(7));
        int sum = 0;
        for (int i = 0; i < ReportSize; i++) sum += r[i];
        r[4] = (byte)sum; r[5] = (byte)(sum >> 8);
        return r;
    }

    public static byte[] Command(byte kind, ReadOnlySpan<byte> data, int? declared = null)
    {
        int len = declared ?? data.Length;
        var head = new[] { kind, (byte)(len >> 8), (byte)len };
        var crc = Crc16Modbus(head);
        var c = new byte[7 + data.Length];
        c[0] = 0xA5; c[1] = 0x5A; c[2] = head[0]; c[3] = head[1]; c[4] = head[2]; c[5] = (byte)(crc >> 8); c[6] = (byte)crc;
        data.CopyTo(c.AsSpan(7));
        return c;
    }

    public static byte[] Finish => Report(OpFinish, ReadOnlySpan<byte>.Empty, 0, Chunk);

    public static List<byte[]> DataReports(ReadOnlySpan<byte> blob)
    {
        var list = new List<byte[]>((blob.Length + Chunk - 1) / Chunk);
        for (int off = 0; off < blob.Length; off += Chunk)
            list.Add(Report(OpData, blob.Slice(off, Math.Min(Chunk, blob.Length - off)), off));
        return list;
    }

    /// <summary>A command with no bulk data: a view switch, or a one-byte home-page value.</summary>
    public static List<Al80Step> Simple(byte kind, params byte[] data) =>
        new() { new(Report(OpAnnounce, Command(kind, data)), 0.03), new(Finish, 0.03) };

    public enum View { Home, Gif, NextPicture }

    public static List<Al80Step> ViewStream(View v) => Simple(v switch { View.Home => PkGoHome, View.Gif => PkGoGif, _ => PkNextPic });

    /// <summary>
    /// Clock and date. Send the real 24-hour hour: the module draws 12-hour with AM/PM itself (2026-11-12: hour 14 showed
    /// "2:0x PM"; hour 2 sent for 2 pm showed "AM"). The vendor app sends the pair three times.
    /// </summary>
    public static List<Al80Step> ClockStream(DateTime now, int repeat = 3)
    {
        var values = new (byte kind, byte[] data)[]
        {
            (PkTime, new[] { (byte)now.Hour, (byte)now.Minute, (byte)now.Second }),
            (PkDate, new[] { (byte)(now.Year % 100), (byte)(now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek), (byte)now.Month, (byte)now.Day }),
        };
        var once = new List<Al80Step>();
        foreach (var (kind, data) in values)
        {
            once.Add(new(Report(OpAnnounce, Command(kind, ReadOnlySpan<byte>.Empty, data.Length)), 0.06));
            once.Add(new(Report(OpData, data), 0.06));
            once.Add(new(Finish, 0.06));
        }
        var all = new List<Al80Step>();
        for (int i = 0; i < repeat; i++) all.AddRange(once);
        return all;
    }

    /// <summary>
    /// The home page's battery icon as a load meter. Verified 2026-11-12: the level alone is ignored while the keyboard is on
    /// USB (it reports "charging" and the icon stays full); "not charging" (type 0x07 = 0) plus the level shows bars.
    /// Resolution is coarse (a few bars). Resend every few seconds: the keyboard may report its own state again.
    /// </summary>
    public static List<Al80Step> GaugeStream(int percent)
    {
        var s = Simple(PkBattState, 0);
        s.AddRange(Simple(PkBattLevel, (byte)Math.Clamp(percent, 0, 100)));
        return s;
    }

    /// <summary>Put the firmware back in charge of the battery icon: "charging" (the cable is in) and a full level.</summary>
    public static List<Al80Step> GaugeReleaseStream()
    {
        var s = Simple(PkBattState, 1);
        s.AddRange(Simple(PkBattLevel, 100));
        return s;
    }

    /// <summary>Store a 96x160 still and show it. Both settles are needed or it never appears.</summary>
    public static List<Al80Step> PictureStream(ReadOnlySpan<byte> rgb565, double announceSettle = PictureAnnounceSettle)
    {
        if (rgb565.Length != FrameBytes) throw new ArgumentException($"a picture is {FrameBytes} bytes, got {rgb565.Length}");
        var steps = new List<Al80Step>
        {
            new(Report(OpAnnounce, Command(PkEvent, new byte[] { 0x01 })), announceSettle),
            new(Report(OpData, Command(PkAddPic, ReadOnlySpan<byte>.Empty, rgb565.Length)), 0.03),
        };
        foreach (var r in DataReports(rgb565)) steps.Add(new(r, 0));
        steps.Add(new(Finish, 0.03));
        return steps;
    }

    /// <summary>Store an animation in one of the three GIF slots (a flash write: the module pauses 3 s on frame 0 and every 16th).</summary>
    public static List<Al80Step> GifStream(IReadOnlyList<byte[]> frames, GifTarget target, int fps)
    {
        if (frames.Count < 1 || frames.Count > target.MaxFrames) throw new ArgumentException($"{target.Name} holds 1 to {target.MaxFrames} frames, got {frames.Count}");
        int size = Width * target.Height * 2;
        byte mode = (byte)target.Mode;
        fps = Math.Clamp(fps, 1, 60);
        var steps = new List<Al80Step>
        {
            new(Report(OpAnnounce, Command(PkGifNum, new byte[] { mode, 0 })), 0.03),
            new(Report(OpData, Command(PkGifFrame, new byte[] { mode, 0 })), 0.03),
        };
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].Length != size) throw new ArgumentException($"frame {i} is {frames[i].Length} bytes, expected {size}");
            steps.Add(new(Report(OpData, Command(PkEvent, new byte[] { 0x02, mode, (byte)i })), i % 16 == 0 ? 3.0 : 0.03));
            steps.Add(new(Report(OpData, Command(PkFrameLen, ReadOnlySpan<byte>.Empty, size)), 0));
            for (int b = 0; b < size; b += Bank)
            {
                var bank = DataReports(frames[i].AsSpan(b, Math.Min(Bank, size - b)));
                for (int k = 0; k < bank.Count; k++) steps.Add(new(bank[k], k == bank.Count - 1 ? 0.03 : 0));
            }
        }
        steps.Add(new(Report(OpData, Command(PkGifNum, new byte[] { mode, (byte)frames.Count })), 0.03));
        steps.Add(new(Report(OpData, Command(PkGifFrame, new byte[] { mode, (byte)fps })), 0.03));
        steps.Add(new(Finish, 0.03));
        return steps;
    }

    /// <summary>Packed BGRA32 (WPF) pixels to RGB565, high byte first, row by row.</summary>
    public static byte[] Bgra32ToRgb565(ReadOnlySpan<byte> bgra)
    {
        var o = new byte[bgra.Length / 4 * 2];
        for (int i = 0, j = 0; i + 3 < bgra.Length; i += 4, j += 2)
        {
            int v = ((bgra[i + 2] >> 3) << 11) | ((bgra[i + 1] >> 2) << 5) | (bgra[i] >> 3);
            o[j] = (byte)(v >> 8); o[j + 1] = (byte)v;
        }
        return o;
    }

    // ------------------------------------------------------------------ VIA (same interface, 32 meaningful bytes)

    /// <summary>
    /// VIA commands VANTA may send. Reads: 0x01 protocol, 0x02 keyboard value, 0x04 keycode, 0x08 lighting, 0x0C/0x0D/0x0E
    /// macro count/size/buffer, 0x11 layers, 0x12 keymap buffer. Writes: 0x05 one keycode, 0x07 lighting (volatile),
    /// 0x09 lighting save. Never: 0x0A EEPROM reset, 0x0B bootloader jump, 0x06/0x10 resets, 0xB0-0xB7 firmware update.
    /// </summary>
    public static readonly byte[] ViaAllowed = { 0x01, 0x02, 0x04, 0x05, 0x07, 0x08, 0x09, 0x0C, 0x0D, 0x0E, 0x11, 0x12 };

    public static byte[] Via(params byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 32) throw new ArgumentException("a VIA request is 1 to 32 bytes");
        if (!ViaAllowed.Contains(bytes[0])) throw new ArgumentException($"VIA command 0x{bytes[0]:X2} is not allowed");
        if (bytes[0] == 0x02 && bytes.Length > 1 && bytes[1] is not (0x01 or 0x02 or 0x04)) throw new ArgumentException($"keyboard value 0x{bytes[1]:X2} is not allowed");
        var r = new byte[ReportSize];
        bytes.CopyTo(r, 0);
        return r;
    }

    /// <summary>Stock lighting channels (read on 2026-11-12): 3 = per-key matrix, 2 = a second zone (likely the side bar).</summary>
    public const byte ChannelKeys = 3, ChannelSide = 2;
    public const byte LightBrightness = 1, LightEffect = 2, LightSpeed = 3, LightColor = 4;

    public static byte[] LightGet(byte channel, byte value) => Via(0x08, channel, value);
    public static byte[] LightSet(byte channel, byte value, params byte[] data) => Via(new byte[] { 0x07, channel, value }.Concat(data).ToArray());
    public static byte[] LightSave(byte channel) => Via(0x09, channel);
    public static byte[] KeycodeGet(byte layer, byte row, byte col) => Via(0x04, layer, row, col);
    public static byte[] KeycodeSet(byte layer, byte row, byte col, ushort code) => Via(0x05, layer, row, col, (byte)(code >> 8), (byte)code);

    // ------------------------------------------------------------------ SignalRGB per-key stream (1.19.0)
    // YUNZII_AL80_SIGNALRGB_V0122.bin (USB rev 0x0122), disassembled 2026-11-12: the raw-HID loop (0x0800BB34) runs the
    // SignalRGB switch for 0x21-0x28 BEFORE the vendor/screen handler and VIA, replies are full 64-byte reports with the
    // command in byte 0, the stream accepts start + count <= 84 and count <= 9 (else byte 1 = 0xFD / 0xFE, nothing set),
    // and 0x25/0x26 never write EEPROM (mode helper with write = 0 / reload). The owner's stock firmware rev 0x0125 (dumped
    // read-only 2026-11-12, SHA-256 1bef0557...) has the identical handler at the same place and the identical LED table.
    // The older 10-min build rev 0x0119 has NO such handler: there these IDs reach the vendor's own 0x21-0x34 switch
    // (meaning unknown). So they are only ever sent to a keyboard reporting a revision verified by disassembly.

    public const byte SrgbQmkVersion = 0x21, SrgbProtocol = 0x22, SrgbUniqueId = 0x23, SrgbStream = 0x24, SrgbEnable = 0x25,
        SrgbDisable = 0x26, SrgbTotalLeds = 0x27, SrgbFirmwareType = 0x28;
    public const int SrgbMaxPerPacket = 9;
    public const byte SrgbErrBounds = 0xFD, SrgbErrCount = 0xFE;

    /// <summary>USB revisions (bcdDevice) verified by disassembly to carry the SignalRGB handler: 0x0122 (YUNZII's SignalRGB
    /// download) and 0x0125 (the owner's stock firmware). Not 0x0119 (10-min sleep build), not unknown revisions.</summary>
    public static readonly int[] SrgbRevisions = { 0x0122, 0x0125 };

    public static bool IsSrgbRevision(int? bcd) => bcd is int r && SrgbRevisions.Contains(r);

    /// <summary>One SignalRGB command without data (queries, enable, disable).</summary>
    public static byte[] Srgb(byte cmd)
    {
        if (cmd is < SrgbQmkVersion or > SrgbFirmwareType || cmd == SrgbStream) throw new ArgumentException($"0x{cmd:X2} is not a SignalRGB query/mode command");
        var r = new byte[ReportSize];
        r[0] = cmd;
        return r;
    }

    /// <summary>[0x24, start, count, R,G,B x count]; count 1..9 and start + count within the firmware's LED count.</summary>
    public static byte[] SrgbStreamReport(int start, ReadOnlySpan<byte> rgb, int leds = Lighting.Devices.Al80KeyMap.Leds)
    {
        int count = rgb.Length / 3;
        if (rgb.Length % 3 != 0 || count < 1 || count > SrgbMaxPerPacket) throw new ArgumentException($"a stream packet carries 1 to {SrgbMaxPerPacket} LEDs, got {rgb.Length} bytes");
        if (start < 0 || start + count > leds) throw new ArgumentException($"LEDs {start}..{start + count - 1} are outside 0..{leds - 1}");
        var r = new byte[ReportSize];
        r[0] = SrgbStream; r[1] = (byte)start; r[2] = (byte)count;
        rgb.CopyTo(r.AsSpan(3));
        return r;
    }

    /// <summary>A whole frame (one colour per LED, index = LED) as stream packets of up to 9 LEDs: 84 LEDs = 10 packets.</summary>
    public static List<byte[]> SrgbFrame(IReadOnlyList<Rgb> leds, int ledCount = Lighting.Devices.Al80KeyMap.Leds, bool[]? onlyChunks = null)
    {
        if (leds.Count != ledCount) throw new ArgumentException($"a frame is {ledCount} colours, got {leds.Count}");
        var list = new List<byte[]>((ledCount + SrgbMaxPerPacket - 1) / SrgbMaxPerPacket);
        var buf = new byte[SrgbMaxPerPacket * 3];
        for (int start = 0, chunk = 0; start < ledCount; start += SrgbMaxPerPacket, chunk++)
        {
            if (onlyChunks != null && !onlyChunks[chunk]) continue;
            int n = Math.Min(SrgbMaxPerPacket, ledCount - start);
            for (int i = 0; i < n; i++) { var c = leds[start + i]; buf[3 * i] = c.R; buf[3 * i + 1] = c.G; buf[3 * i + 2] = c.B; }
            list.Add(SrgbStreamReport(start, buf.AsSpan(0, n * 3), ledCount));
        }
        return list;
    }

    /// <summary>Which 9-LED chunks differ between two frames (null previous: all).</summary>
    public static bool[] SrgbChangedChunks(IReadOnlyList<Rgb>? previous, IReadOnlyList<Rgb> next)
    {
        int chunks = (next.Count + SrgbMaxPerPacket - 1) / SrgbMaxPerPacket;
        var d = new bool[chunks];
        for (int i = 0; i < next.Count; i++)
            if (previous == null || previous.Count != next.Count || !previous[i].Equals(next[i])) d[i / SrgbMaxPerPacket] = true;
        return d;
    }
}
