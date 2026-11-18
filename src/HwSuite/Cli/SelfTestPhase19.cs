using HwSuite.Core;
using HwSuite.Hardware;
using HwSuite.Hardware.Al80;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>
/// 1.19 checks: YUNZII AL80 per-key lighting on YUNZII's SignalRGB firmware. Packet bytes and limits from the firmware's
/// disassembly (2026-11-12), the LED table from its compiled g_led_config, the stock-firmware guard (0x21-0x28 are never
/// sent to rev 0x0119 or unknown builds), no VIA lighting writes while streaming, no stream packet inside a screen transfer, enable/disable
/// order, and the sink's two shapes. No device is opened.
/// </summary>
public static partial class SelfTest
{
    static void Phase19Tests()
    {
        Guarded("1.19 SignalRGB packets", SrgbPacketTests);
        Guarded("1.19 AL80 key map", Al80KeyMapTests);
        Guarded("1.19 SignalRGB device guard", SrgbDeviceTests);
        Guarded("1.19 AL80 per-key service", SrgbServiceTests);
    }

    static void SrgbPacketTests()
    {
        var p = Al80Protocol.SrgbStreamReport(9, new byte[] { 1, 2, 3, 4, 5, 6 });
        Check("SRGB stream packet: 24 start count R G B ... in a 64-byte report", p.Length == 64 && Hx(p, 9) == "24 09 02 01 02 03 04 05 06" && p.Skip(9).All(b => b == 0), Hx(p, 9));
        bool Refuses(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }
        Check("SRGB stream refuses 10 LEDs in one packet (firmware error 0xFE)", Refuses(() => Al80Protocol.SrgbStreamReport(0, new byte[30])));
        Check("SRGB stream refuses LEDs past 83 (firmware error 0xFD)", Refuses(() => Al80Protocol.SrgbStreamReport(80, new byte[15])));
        Check("SRGB stream refuses an empty packet", Refuses(() => Al80Protocol.SrgbStreamReport(0, Array.Empty<byte>())));
        Check("SRGB stream accepts the last LEDs 81-83", !Refuses(() => Al80Protocol.SrgbStreamReport(81, new byte[9])));

        var frame = Enumerable.Range(0, Al80KeyMap.Leds).Select(i => new Rgb((byte)i, (byte)(i * 2), (byte)(255 - i))).ToArray();
        var packets = Al80Protocol.SrgbFrame(frame);
        Check("SRGB frame of 84 LEDs = 10 packets (9 x 9 + 3)", packets.Count == 10 && packets[^1][1] == 81 && packets[^1][2] == 3, $"({packets.Count})");
        var covered = new int[Al80KeyMap.Leds];
        foreach (var q in packets) for (int i = 0; i < q[2]; i++) { covered[q[1] + i]++; if (q[3 + 3 * i] != frame[q[1] + i].R || q[5 + 3 * i] != frame[q[1] + i].B) covered[q[1] + i] += 10; }
        Check("SRGB frame sends every LED once with its own colour", covered.All(c => c == 1));
        Check("SRGB frame refuses a frame of the wrong size", Refuses(() => Al80Protocol.SrgbFrame(new Rgb[80])));

        var next = (Rgb[])frame.Clone(); next[40] = new Rgb(9, 9, 9);
        var changed = Al80Protocol.SrgbChangedChunks(frame, next);
        Check("SRGB changed chunks: one LED changed = one packet", changed.Count(c => c) == 1 && changed[4] && Al80Protocol.SrgbFrame(next, onlyChunks: changed).Single()[1] == 36);
        Check("SRGB changed chunks: no previous frame = all packets", Al80Protocol.SrgbChangedChunks(null, next).All(c => c));

        // the SignalRGB builders can produce nothing but 0x21-0x28
        var built = new List<byte>();
        for (int c = 0; c < 256; c++) { try { built.Add(Al80Protocol.Srgb((byte)c)[0]); } catch (ArgumentException) { } }
        built.AddRange(packets.Select(x => x[0]));
        Check("SRGB builders only make 0x21-0x23 and 0x25-0x28 (queries/mode) and 0x24 (stream)", built.All(b => b is >= 0x21 and <= 0x28) && !built.Contains((byte)0x20));
        Check("SRGB builders never make 0xB0-0xB7, 0x0A, 0x0B, 0x06, 0x10 or vendor 0x20", !built.Any(b => b is >= 0xB0 and <= 0xB7 or 0x0A or 0x0B or 0x06 or 0x10 or 0x20));
        Check("SRGB revision gate: 0x0122 and the owner's stock 0x0125 (both disassembled), not 0x0119", Al80Protocol.IsSrgbRevision(0x0122) && Al80Protocol.IsSrgbRevision(0x0125) && !Al80Protocol.IsSrgbRevision(0x0119) && !Al80Protocol.IsSrgbRevision(null));
    }

    static void Al80KeyMapTests()
    {
        var k = Al80KeyMap.Keys;
        Check("AL80 map: 84 LEDs, index = LED", k.Length == 84 && k.Select((x, i) => x.Led == i).All(b => b));
        Check("AL80 map: 79 keys + 5 LEDs without a key (56, 59, 78, 79, 80)", k.Count(x => x.HasKey) == 79 && k.Where(x => !x.HasKey).Select(x => x.Led).SequenceEqual(new[] { 56, 59, 78, 79, 80 }));
        Check("AL80 map: every matrix position at most once, the knob (0,14) unlit", k.Where(x => x.HasKey).Select(x => (x.Row, x.Col)).Distinct().Count() == 79 && !k.Any(x => x.Row == 0 && x.Col == 14));
        Check("AL80 map follows the firmware, not the plugin (LED 14 = `, not Mute)", k[0].Name == "Esc" && k[13].Name == "Del" && k[14].Name == "`" && k[28].Name == "PgUp" && k[57].Name == "Enter" && k[75].Name == "Space" && k[83].Name == "Right");
        Check("AL80 map: rows run top to bottom (Esc above `, above Tab, ... above Space)", k[0].Y < k[14].Y && k[14].Y < k[29].Y && k[29].Y < k[44].Y && k[44].Y < k[58].Y && k[58].Y < k[75].Y);
        Check("AL80 map: X spans the board (Esc left, Right arrow right)", k[0].X == 0 && k[83].X == 224 && Al80KeyMap.UnitX.All(x => x is >= 0 and <= 1));
        Check("Keyboard maps by LED count: 87 = i87, 84 = AL80, others none", KeyboardKeyMaps.For(87)?.Length == 87 && KeyboardKeyMaps.For(84)?.Length == 84 && KeyboardKeyMaps.For(10) == null);

        var g = LayoutTransform.Identity.GeometryFor(DeviceRole.Keyboard, Al80KeyMap.Leds);
        Check("AL80 geometry is a key surface (rain falls down the rows)", g.Surface && g.Count == 84 && g.Y[75] > g.Y[0] && g.X[83] > g.X[0], $"surface {g.Surface}");
        var g87 = LayoutTransform.Identity.GeometryFor(DeviceRole.Keyboard, ILovbeeKeyMap.Keys.Length);
        Check("i87 geometry unchanged (still its key surface)", g87.Surface && g87.Count == 87);
        var line = LayoutTransform.Identity.GeometryFor(DeviceRole.Keyboard, 84).Line;
        Check("AL80 wave positions run left to right across the keyboard's span", Math.Abs(line[0] - KeyboardSink.WaveStart) < 1e-9 && Math.Abs(line[83] - (KeyboardSink.WaveStart + KeyboardSink.WaveSpan)) < 1e-9);
    }

    static void SrgbDeviceTests()
    {
        // stock revision: nothing in 0x21-0x28 may leave VANTA
        var stock = new FakeAl80Transport { Revision = 0x0119 };
        using (var d = new Al80Device(stock))
        {
            bool q = false, w = false, m = false;
            try { d.SrgbQuery(Al80Protocol.SrgbTotalLeds); } catch (InvalidOperationException) { q = true; }
            try { d.SrgbWrite(Al80Protocol.SrgbFrame(new Rgb[84])); } catch (InvalidOperationException) { w = true; }
            try { d.SrgbMode(true); } catch (InvalidOperationException) { m = true; }
            Check("Older firmware (rev 0119): SignalRGB query, stream and mode are refused", q && w && m);
            Check("Older firmware: no 0x21-0x28 report was written", stock.SrgbOnStock == 0 && !stock.Snapshot().Any(r => r[0] is >= 0x21 and <= 0x28));
        }

        var f = new FakeAl80Transport { Revision = 0x0122 };
        using var dev = new Al80Device(f);
        Check("SignalRGB firmware: 0x27 answers 84 LEDs, 0x22 protocol 1.0.6, 0x28 type 2",
            dev.SrgbQuery(Al80Protocol.SrgbTotalLeds)?[1] == 84 && dev.SrgbQuery(Al80Protocol.SrgbProtocol) is { } pr && pr[1] == 1 && pr[3] == 6 && dev.SrgbQuery(Al80Protocol.SrgbFirmwareType)?[1] == 2);
        dev.SrgbMode(true);
        var frame = Enumerable.Range(0, 84).Select(i => new Rgb((byte)(i * 3), 0, (byte)(255 - i * 3))).ToArray();
        var err = dev.SrgbWrite(Al80Protocol.SrgbFrame(frame));
        Check("SignalRGB stream lands every LED's colour, no error", err == null && f.SrgbOn && f.SrgbLeds.SequenceEqual(frame) && f.SrgbErrors == 0);
        bool refused = false;
        try { dev.Via(Al80Protocol.LightSet(3, 1, 200)); } catch (InvalidOperationException) { refused = true; }
        Check("While streaming, VIA lighting writes to the keys (channel 3) are refused (and not sent)", refused && !f.Snapshot().Any(r => r[0] == 0x07));
        bool saveRefused = false;
        try { dev.Via(Al80Protocol.LightSave(2)); } catch (InvalidOperationException) { saveRefused = true; }
        Check("While streaming, the 3-LED light (channel 2) may be set (volatile) but never saved", dev.Via(Al80Protocol.LightSet(2, 1, 99))[0] == 0x07 && saveRefused && !f.Snapshot().Any(r => r[0] == 0x09));
        Check("While streaming, VIA reads still work", dev.Via(Al80Protocol.Via(0x01))[0] == 0x01);
        dev.SrgbMode(false);
        Check("0x26 ends SignalRGB mode; VIA lighting writes allowed again", !f.SrgbOn && dev.Via(Al80Protocol.LightSet(3, 1, 100))[0] == 0x07);

        // stream packets never fall inside a screen transfer (one lock)
        var f2 = new FakeAl80Transport { Revision = 0x0122 };
        using var d2 = new Al80Device(f2);
        d2.SrgbMode(true);
        var img = new byte[Al80Protocol.FrameBytes];
        var streamer = new Thread(() => { for (int i = 0; i < 200; i++) d2.SrgbWrite(Al80Protocol.SrgbFrame(frame)); });
        streamer.Start();
        d2.Send(Al80Protocol.PictureStream(img, 0.001));
        streamer.Join();
        var sent = f2.Snapshot();
        int first = sent.FindIndex(r => r[0] == 0x40), last = sent.FindLastIndex(r => r[0] == 0x42);
        Check("No stream packet between a picture's announce and finish", first >= 0 && last > first && !sent.Skip(first).Take(last - first + 1).Any(r => r[0] == 0x24) && f2.Picture != null,
            $"({sent.Count(r => r[0] == 0x24)} stream packets around one picture)");
    }

    static void SrgbServiceTests()
    {
        bool Wait(Func<bool> cond, int ms) { var end = Environment.TickCount64 + ms; while (Environment.TickCount64 < end) { if (cond()) return true; Thread.Sleep(20); } return cond(); }
        var snap = new TelemetrySnapshot();

        // stock: per-key off, the 1.18 colour path still works, and no SignalRGB ID is sent
        {
            var cfg = new AppConfig(); cfg.Al80.Enabled = true; cfg.Al80.Mode = "off";
            var stock = new FakeAl80Transport { Revision = 0x0119 };
            using var svc = new Al80Service(cfg, () => snap, () => new Al80Device(stock));
            svc.Start();
            Wait(() => svc.Connected, 3000);
            Wait(() => svc.PerKeyInfo.Length > 0, 3000);   // Connected is set just before the firmware detection runs
            Check("Older firmware: per-key not offered, reason shown", !svc.PerKey && svc.PerKeyInfo.Contains("no SignalRGB handler"), svc.PerKeyInfo);
            svc.SetLight(new Rgb(0, 0, 255));
            Check("Older firmware: single-colour VIA fallback still drives the keys", Wait(() => stock.Lighting.TryGetValue((3, 2), out var e) && e[0] == 1, 2000));
            Check("Older firmware: the service never sent 0x21-0x28", stock.SrgbOnStock == 0);
            var sink = new Al80Sink(new RgbConfig(), () => svc, () => cfg.Al80);
            Check("Older firmware: the sink stays 10 virtual LEDs", !sink.PerKey && sink.LedCount == Al80Sink.VirtualLeds);
        }

        var c = new AppConfig(); c.Al80.Enabled = true; c.Al80.Mode = "off";
        var f = new FakeAl80Transport { Revision = 0x0122 };
        // the keyboard's own settings, read at connect (ch 3 keys, ch 2 the 3-LED light)
        f.Lighting[(3, 1)] = new byte[] { 129, 0 }; f.Lighting[(3, 2)] = new byte[] { 5, 0 }; f.Lighting[(3, 3)] = new byte[] { 128, 0 }; f.Lighting[(3, 4)] = new byte[] { 0, 255 };
        f.Lighting[(2, 1)] = new byte[] { 200, 0 }; f.Lighting[(2, 2)] = new byte[] { 4, 0 }; f.Lighting[(2, 3)] = new byte[] { 7, 0 }; f.Lighting[(2, 4)] = new byte[] { 30, 255 };
        using var s = new Al80Service(c, () => snap, () => new Al80Device(f));
        s.Start();
        Wait(() => s.Connected, 3000);
        Check("SignalRGB firmware detected: 84 LEDs, per-key on", Wait(() => s.PerKey, 2000) && s.PerKeyLeds == 84, s.PerKeyInfo);
        var sk = new Al80Sink(new RgbConfig(), () => s, () => c.Al80);
        Check("Per-key sink: 84 LEDs at the firmware key positions", sk.PerKey && sk.LedCount == 84 && sk.Positions.Length == 84 && sk.Kind.Contains("SignalRGB"));

        var frame = Enumerable.Range(0, 84).Select(i => new Rgb(0, (byte)i, 200)).ToArray();
        s.SetFrame(frame, new Rgb(0, 0, 255));
        Check("Per-key: 0x25 then the frame lands on the keys", Wait(() => f.SrgbOn && f.SrgbLeds.SequenceEqual(frame), 2000));
        int enables() => f.Snapshot().Count(r => r[0] == 0x25);
        int before = f.Snapshot().Count(r => r[0] == 0x24);
        var next = (Rgb[])frame.Clone(); next[3] = new Rgb(255, 0, 0);
        s.SetFrame(next);
        Wait(() => f.SrgbLeds[3].R == 255, 2000);
        int added = f.Snapshot().Count(r => r[0] == 0x24) - before;
        Check("Per-key: one changed key sends one packet (not the whole frame)", added is >= 1 and <= 2 && f.SrgbLeds[3].R == 255, $"({added})");
        for (int i = 0; i < 20; i++) { var x = (Rgb[])next.Clone(); x[i % 84] = new Rgb(1, (byte)i, 1); s.SetFrame(x); Thread.Sleep(10); }
        Check("Per-key: 0x25 sent once while frames keep coming", enables() == 1, $"({enables()})");
        Check("Per-key: no VIA write to the keys (channel 3) and no save while streaming", !f.Snapshot().Any(r => (r[0] == 0x07 && r[1] == 3) || r[0] == 0x09));
        Check("Per-key: the 3-LED light follows (solid, blue, full brightness; volatile channel 2)", Wait(() => f.Lighting[(2, 2)][0] == 1 && f.Lighting[(2, 4)][0] == 171 && f.Lighting[(2, 1)][0] == 255, 2000) && s.SideWrites >= 1);
        int sw0 = f.Snapshot().Count(r => r[0] == 0x07 && r[1] == 2);
        for (int i = 0; i < 30; i++) { s.SetFrame(frame, new Rgb((byte)(i * 8), 0, 255)); Thread.Sleep(10); }
        Thread.Sleep(300);
        int sw1 = f.Snapshot().Count(r => r[0] == 0x07 && r[1] == 2) - sw0;
        Check("Per-key: the 3-LED light changes at most ~10 times a second", sw1 <= 12, $"({sw1} channel-2 writes for 30 colour changes in ~0.6 s)");
        c.Al80.SyncSideBar = false;
        s.SetFrame(frame, new Rgb(255, 0, 0));
        Check("'Side light follows' off: the 3-LED light gets its own effect back while the keys keep streaming", Wait(() => f.Lighting[(2, 2)][0] == 4, 2000) && f.SrgbOn);
        c.Al80.SyncSideBar = true;
        Check("Per-key: the stream never left the 84 LEDs (no firmware error)", f.SrgbErrors == 0);

        Check("Per-key: frames stopping hand the keys back (0x26) after the idle time", Wait(() => !f.SrgbOn, (int)Al80Service.LightIdleRelease.TotalMilliseconds + 2000));
        s.SetFrame(Enumerable.Repeat(new Rgb(0, 80, 255), 84).ToArray(), hold: true);
        Wait(() => f.SrgbOn, 2000);
        Thread.Sleep((int)Al80Service.LightIdleRelease.TotalMilliseconds + 600);
        Check("Per-key: a held (exit) frame stays on", f.SrgbOn && f.SrgbLeds[0] == new Rgb(0, 80, 255));
        int k0 = f.Snapshot().Count(r => r[0] == 0x24);
        Thread.Sleep(1000);
        int kept = f.Snapshot().Count(r => r[0] == 0x24) - k0;
        Check("Per-key: a still frame is re-sent every ~200 ms (the firmware drops SignalRGB after 500 ms of silence)", kept >= 40, $"({kept} packets in 1 s = {kept / 10} frames)");
        sk.Deactivate(new Rgb(10, 20, 30));
        Check("Per-key sink: output off holds the steady colour on every key", Wait(() => f.SrgbLeds.All(x => x == new Rgb(10, 20, 30)), 2000));

        c.Al80.PerKey = false;
        Check("Per-key switched off: 0x26 sent, the sink falls back to one colour", Wait(() => !f.SrgbOn, 2000) && !s.PerKey && sk.LedCount == Al80Sink.VirtualLeds);
        c.Al80.PerKey = true;
        s.SetFrame(frame);
        Wait(() => f.SrgbOn, 2000);
        c.Al80.Enabled = false;
        Check("AL80 control off: 0x26 before the keyboard is let go", Wait(() => !s.Connected, 3000) && !f.SrgbOn);
        Check("Released: the 3-LED light's own effect, colour, brightness and speed are back", f.Lighting[(2, 2)][0] == 4 && f.Lighting[(2, 4)][0] == 30 && f.Lighting[(2, 1)][0] == 200 && f.Lighting[(2, 3)][0] == 7);
        Check("Per-key service never saved anything (no 0x09)", !f.Snapshot().Any(r => r[0] == 0x09));
        Check("Per-key service never sent a forbidden ID", !f.Snapshot().Any(r => r[0] is >= 0xB0 and <= 0xB7 or 0x0A or 0x0B or 0x06 or 0x10 or 0x20));
    }
}
