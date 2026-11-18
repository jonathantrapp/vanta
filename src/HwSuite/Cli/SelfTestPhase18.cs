using HwSuite.Core;
using HwSuite.Hardware;
using HwSuite.Hardware.Al80;
using HwSuite.Lighting;

namespace HwSuite.Cli;

/// <summary>
/// 1.18 checks: YUNZII AL80 packets against published captures (snackdriven/al80-lcd) and the owner's keyboard tests of
/// 2026-11-12, the forbidden opcodes, and the pipelined sender and service against a simulated keyboard. No device is opened.
/// </summary>
public static partial class SelfTest
{
    static void Phase18Tests()
    {
        Guarded("1.18 AL80 packets", Al80PacketTests);
        Guarded("1.18 AL80 sender", Al80SenderTests);
        Guarded("1.18 AL80 service", Al80ServiceTests);
        Guarded("1.18.1 AL80 lighting sync", Al80LightTests);
    }

    static string Hx(byte[] b, int n) => BitConverter.ToString(b, 0, n).Replace('-', ' ');

    static void Al80PacketTests()
    {
        var clock = Al80Protocol.ClockStream(new DateTime(2026, 7, 1, 14, 5, 9), 1);
        Check("AL80 time announce matches the vendor capture", Hx(clock[0].Report, 14) == "40 00 00 07 F6 02 00 A5 5A 09 00 03 C3 E1", Hx(clock[0].Report, 14));
        Check("AL80 clock sends the 24-hour hour (the module draws AM/PM itself)", clock[1].Report[7] == 14 && clock[1].Report[8] == 5 && clock[1].Report[9] == 9);
        Check("AL80 date data 2026-07-01 (Wednesday = 3)", Hx(clock[4].Report, 11) == "41 00 00 04 6A 00 00 1A 03 07 01", Hx(clock[4].Report, 11));
        Check("AL80 date announce", Hx(clock[3].Report, 14) == "40 00 00 07 A5 01 00 A5 5A 0A 00 04 01 50", Hx(clock[3].Report, 14));
        Check("AL80 Sunday is weekday 7", Al80Protocol.ClockStream(new DateTime(2026, 11, 15, 9, 0, 0), 1)[4].Report[8] == 7);
        Check("AL80 finish matches the capture", Hx(Al80Protocol.Finish, 5) == "42 00 00 38 7A" && Al80Protocol.Finish.Skip(7).All(b => b == 0));

        var pic = Al80Protocol.PictureStream(new byte[Al80Protocol.FrameBytes]);
        Check("AL80 still announce matches the capture", Hx(pic[0].Report, 15) == "40 00 00 08 CF 02 00 A5 5A 10 00 01 C5 B1 01", Hx(pic[0].Report, 15));
        Check("AL80 still setup (30,720 bytes) matches the capture", Hx(pic[1].Report, 14) == "41 00 00 07 21 03 00 A5 5A 0C 78 00 C3 93", Hx(pic[1].Report, 14));
        Check("AL80 still is 548 full blocks + a 32-byte tail", pic.Count == 2 + 549 + 1 && pic[^2].Report[3] == 0x20 && (pic[^2].Report[1] | pic[^2].Report[2] << 8) == 0x77E0);
        Check("AL80 still settles before setup and data", pic[0].Pause >= 0.1 && pic[1].Pause >= 0.03);

        var gif = Al80Protocol.GifStream(new[] { new byte[96 * 64 * 2], new byte[96 * 64 * 2] }, Al80Protocol.HomeStrip, 30);
        Check("AL80 GIF frame header checksum (mode 2, frame 0) = 95 01", Hx(gif[2].Report, 6) == "41 00 00 0A 95 01", Hx(gif[2].Report, 6));
        Check("AL80 GIF pauses 3 s on frame 0 (flash write)", gif[2].Pause == 3.0);
        Check("AL80 GIF banks end with a 16-byte block and a 30 ms pause", gif.Any(s => s.Report[3] == 0x10 && s.Pause == 0.03));
        Check("AL80 GIF frame count and fps close the upload", gif[^3].Report[14] == 2 && gif[^3].Report[15] == 2 && gif[^2].Report[15] == 30);
        try { Al80Protocol.GifStream(Enumerable.Repeat(new byte[96 * 64 * 2], 43).ToList(), Al80Protocol.HomeStrip, 30); Check("AL80 home strip refuses 43 frames", false); }
        catch (ArgumentException) { Check("AL80 home strip refuses 43 frames", true); }

        var gauge = Al80Protocol.GaugeStream(42);
        Check("AL80 gauge sends 'not charging' before the level (needed on USB)", gauge[0].Report[9] == Al80Protocol.PkBattState && gauge[0].Report[14] == 0
            && gauge[2].Report[9] == Al80Protocol.PkBattLevel && gauge[2].Report[14] == 42);
        Check("AL80 view home packet", Hx(Al80Protocol.ViewStream(Al80Protocol.View.Home)[0].Report, 14) == "40 00 00 07 53 01 00 A5 5A 0B 00 00 02 00");

        // forbidden opcodes cannot be built
        int refused = 0;
        for (int op = 0; op < 256; op++)
        {
            if (op is 0x40 or 0x41 or 0x42) continue;
            try { Al80Protocol.Report((byte)op, ReadOnlySpan<byte>.Empty); } catch (ArgumentException) { refused++; }
        }
        Check("AL80 screen builder refuses every opcode except 0x40-0x42 (incl. 0xB0-0xB7)", refused == 253, $"({refused})");
        int viaRefused = 0;
        foreach (var op in new byte[] { 0x0A, 0x0B, 0x06, 0x10, 0x0F, 0x13, 0x15, 0x40, 0x41, 0x42, 0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xFE })
            try { Al80Protocol.Via(op); } catch (ArgumentException) { viaRefused++; }
        Check("AL80 VIA builder refuses EEPROM reset, bootloader jump, resets, macro writes, 0xB0-0xB7", viaRefused == 19, $"({viaRefused}/19)");
        try { Al80Protocol.Via(0x02, 0x03); Check("AL80 VIA refuses keyboard value 3 (switch matrix)", false); } catch (ArgumentException) { Check("AL80 VIA refuses keyboard value 3 (switch matrix)", true); }
        bool noDelete = new[] { pic, gif, gauge, clock, Al80Protocol.GaugeReleaseStream() }.SelectMany(x => x).All(s => !(s.Report[7] == 0xA5 && s.Report[9] == 0x0E));
        Check("AL80 no stream contains a picture delete (0x0E would free a flash slot)", noDelete);

        var px = new byte[] { 0x00, 0x00, 0xFF, 255, 0xFF, 0xFF, 0xFF, 255 };   // BGRA red, white
        var c = Al80Protocol.Bgra32ToRgb565(px);
        Check("AL80 RGB565 big-endian (red F800, white FFFF)", Hx(c, 4) == "F8 00 FF FF", Hx(c, 4));
    }

    static void Al80SenderTests()
    {
        var img = new byte[Al80Protocol.FrameBytes];
        new Random(7).NextBytes(img);
        var fake = new FakeAl80Transport();
        using (var dev = new Al80Device(fake) { Window = 8 })
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            dev.Send(Al80Protocol.PictureStream(img, 0.001));
            Check("AL80 pipelined picture arrives byte-exact", fake.Picture != null && fake.Picture.AsSpan().SequenceEqual(img));
            Check("AL80 at most 8 reports in flight, and more than 1", fake.MaxInFlight is >= 2 and <= 8, $"(max {fake.MaxInFlight})");

            var busy = new FakeAl80Transport { BusyEvery = 97 };
            using var dev2 = new Al80Device(busy) { Window = 8 };
            dev2.Send(Al80Protocol.PictureStream(img, 0.001));
            Check("AL80 'busy' replies are resent and the picture still arrives", busy.Picture != null && busy.Picture.AsSpan().SequenceEqual(img), $"(resends {dev2.Retries})");

            var frames = Enumerable.Range(0, 3).Select(i => Enumerable.Repeat((byte)(i * 40 + 1), 96 * 64 * 2).ToArray()).ToList();
            var g = new FakeAl80Transport();
            using var dev3 = new Al80Device(g);
            var steps = Al80Protocol.GifStream(frames, Al80Protocol.HomeStrip, 24).Select(s => s with { Pause = Math.Min(s.Pause, 0.001) }).ToList();
            dev3.Send(steps);
            Check("AL80 GIF banks reassemble into 3 exact frames", g.Gifs.TryGetValue(2, out var got) && got.Count == 3 && got.Zip(frames).All(p => p.First.AsSpan().SequenceEqual(p.Second)));

            var v = new FakeAl80Transport();
            using var dev4 = new Al80Device(v);
            dev4.Via(Al80Protocol.LightSet(3, 1, 0x40));
            Check("AL80 VIA lighting set then read back", dev4.Via(Al80Protocol.LightGet(3, 1))[3] == 0x40);
            bool threw = false;
            try { dev4.Via(new byte[64] { 0x0B, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }); }
            catch (Al80Exception) { threw = true; }
            Check("AL80 device refuses a hand-built bootloader jump", threw && v.Sent.All(r => r[0] != 0x0B));
            threw = false;
            try { dev4.Send(new[] { new Al80Step(Enumerable.Repeat((byte)0xB1, 64).ToArray(), 0) }); } catch (Al80Exception) { threw = true; }
            Check("AL80 device refuses a hand-built 0xB1 step", threw && v.Sent.All(r => r[0] != 0xB1));
        }
    }

    static void Al80ServiceTests()
    {
        var cfg = new AppConfig();
        cfg.Al80.Enabled = true; cfg.Al80.Mode = "home"; cfg.Al80.Gauge = "cpu"; cfg.Al80.ClockSync = true;
        var snap = new TelemetrySnapshot { CpuLoadPct = 40, CpuTempC = 50, GpuLoadPct = 10, GpuTempC = 45, RamUsedPct = 30 };
        var fake = new FakeAl80Transport();
        using var svc = new Al80Service(cfg, () => snap, () => new Al80Device(fake));
        svc.Start();
        bool Wait(Func<bool> cond, int ms) { var end = Environment.TickCount64 + ms; while (Environment.TickCount64 < end) { if (cond()) return true; Thread.Sleep(50); } return cond(); }
        bool IsCmd(byte[] r, byte kind) => r[7] == 0xA5 && r[8] == 0x5A && r[9] == kind;
        Check("AL80 service (home): clock sent on connect", Wait(() => fake.Snapshot().Any(r => IsCmd(r, Al80Protocol.PkTime)), 3000));
        Check("AL80 service (home): CPU load drives the battery icon", Wait(() => fake.Snapshot().Any(r => IsCmd(r, Al80Protocol.PkBattLevel) && r[14] == 40), 3000));
        Check("AL80 service (home): no picture uploaded", fake.Picture == null && !fake.Snapshot().Any(r => IsCmd(r, Al80Protocol.PkAddPic)));

        cfg.Al80.Mode = "live";
        Thread.Sleep(1600);
        Check("AL80 service (live): no pictures until the flash slots are confirmed full", fake.Picture == null && svc.Preview != null, svc.Status);
        Check("AL80 service (live): battery icon handed back", fake.Snapshot().Any(r => IsCmd(r, Al80Protocol.PkBattState) && r[14] == 1));
        cfg.Al80.FlashSlotsFilled = true; cfg.Al80.LiveIntervalSec = 1.2;
        Check("AL80 service (live): System panel uploaded once confirmed", Wait(() => fake.Picture != null, 4000), svc.Status);
        int n = svc.SessionPictures;
        Thread.Sleep(600);
        Check("AL80 service (live): an unchanged panel is not re-sent within the interval", svc.SessionPictures <= n + 1);
        cfg.Al80.Enabled = false;
        Check("AL80 service: switched off releases the device", Wait(() => !svc.Connected, 2000));
    }

    static void Al80LightTests()
    {
        Check("AL80 HSV: red", Al80Service.ToHsv(new Rgb(255, 0, 0)) == (0, 255, 255));
        Check("AL80 HSV: green = hue 85", Al80Service.ToHsv(new Rgb(0, 255, 0)) == (85, 255, 255), Al80Service.ToHsv(new Rgb(0, 255, 0)).ToString());
        Check("AL80 HSV: blue = hue 171", Al80Service.ToHsv(new Rgb(0, 0, 255)) == (171, 255, 255), Al80Service.ToHsv(new Rgb(0, 0, 255)).ToString());
        Check("AL80 HSV: dim grey has no saturation", Al80Service.ToHsv(new Rgb(40, 40, 40)) == (0, 0, 40));
        Check("AL80 HSV: black is brightness 0", Al80Service.ToHsv(Rgb.Black).v == 0);

        var cfg = new AppConfig();
        cfg.Al80.Enabled = true; cfg.Al80.Mode = "off";
        var fake = new FakeAl80Transport();
        fake.Lighting[(3, 1)] = new byte[] { 129, 0 }; fake.Lighting[(3, 2)] = new byte[] { 5, 0 }; fake.Lighting[(3, 3)] = new byte[] { 128, 0 }; fake.Lighting[(3, 4)] = new byte[] { 0, 255 };
        fake.Lighting[(2, 1)] = new byte[] { 129, 0 }; fake.Lighting[(2, 2)] = new byte[] { 4, 0 }; fake.Lighting[(2, 3)] = new byte[] { 0, 0 }; fake.Lighting[(2, 4)] = new byte[] { 0, 255 };
        var snap = new TelemetrySnapshot();
        using var svc = new Al80Service(cfg, () => snap, () => new Al80Device(fake));
        svc.Start();
        bool Wait(Func<bool> cond, int ms) { var end = Environment.TickCount64 + ms; while (Environment.TickCount64 < end) { if (cond()) return true; Thread.Sleep(20); } return cond(); }
        Wait(() => svc.Connected, 3000);
        List<byte[]> Via(byte cmd) => fake.Snapshot().Where(r => r[0] == cmd).ToList();
        int before = fake.Snapshot().Count;
        svc.SetLight(new Rgb(0, 0, 255));
        Check("AL80 sync: solid effect on keys and side bar", Wait(() => fake.Lighting[(3, 2)][0] == 1 && fake.Lighting[(2, 2)][0] == 1, 2000));
        Check("AL80 sync: colour and brightness on both", Wait(() => fake.Lighting[(3, 4)][0] == 171 && fake.Lighting[(2, 4)][0] == 171 && fake.Lighting[(3, 1)][0] == 255, 2000));
        int writes = Via(0x07).Count;
        svc.SetLight(new Rgb(0, 0, 255)); Thread.Sleep(300);
        Check("AL80 sync: an unchanged colour is not resent", Via(0x07).Count == writes, $"({writes} -> {Via(0x07).Count})");
        svc.SetLight(new Rgb(0, 0, 128)); Thread.Sleep(300);
        var after = fake.Snapshot().Skip(before).Where(r => r[0] == 0x07).ToList();
        Check("AL80 sync: a brightness-only change sends only brightness", after.Skip(writes).All(r => r[2] == 1) && fake.Lighting[(3, 1)][0] == 128);
        Check("AL80 sync: never sends Save", Via(0x09).Count == 0);
        svc.ReleaseLight();
        Check("AL80 sync released: stored effect, colour, brightness, speed restored", Wait(() => fake.Lighting[(3, 2)][0] == 5 && fake.Lighting[(2, 2)][0] == 4 && fake.Lighting[(3, 1)][0] == 129 && fake.Lighting[(3, 4)][1] == 255, 2000));

        cfg.Al80.SyncSideBar = false;
        svc.SetLight(new Rgb(255, 0, 0));
        Check("AL80 sync without the side bar leaves channel 2 alone", Wait(() => fake.Lighting[(3, 2)][0] == 1, 2000) && fake.Lighting[(2, 2)][0] == 4);
        Check("AL80 sync: frames stopping returns the keyboard's own effect after the idle time", Wait(() => fake.Lighting[(3, 2)][0] == 5, (int)Al80Service.LightIdleRelease.TotalMilliseconds + 2000));
        svc.SetLight(Rgb.FromInt(0x00FF00), hold: true);
        Thread.Sleep((int)Al80Service.LightIdleRelease.TotalMilliseconds + 600);
        Check("AL80 sync: a held (exit) colour stays", fake.Lighting[(3, 2)][0] == 1 && fake.Lighting[(3, 4)][0] == 85);

        // lighting writes between picture packets: the picture still arrives exact
        var img = new byte[Al80Protocol.FrameBytes]; new Random(3).NextBytes(img);
        var f2 = new FakeAl80Transport();
        using var dev = new Al80Device(f2);
        int calls = 0;
        dev.Between = d => { calls++; d.Via(Al80Protocol.LightSet(3, 1, (byte)calls)); };
        dev.Send(Al80Protocol.PictureStream(img, 0.001), allowBetween: true);
        Check("AL80 lighting writes between picture packets keep the picture exact", f2.Picture != null && f2.Picture.AsSpan().SequenceEqual(img) && calls >= 10, $"({calls} lighting slots)");
        var gifSteps = Al80Protocol.GifStream(new[] { new byte[96 * 64 * 2] }, Al80Protocol.HomeStrip, 10).Select(x => x with { Pause = Math.Min(x.Pause, 0.001) }).ToList();
        calls = 0; dev.Send(gifSteps, allowBetween: false);
        Check("AL80 no lighting writes inside a GIF upload", calls == 0);
        calls = 0; dev.Send(Al80Protocol.PictureStream(img, 0.001));
        Check("AL80 by default nothing is interleaved inside a screen transfer (hardware 2026-11-12)", calls == 0);

        var sink = new Al80Sink(new RgbConfig(), () => null, () => cfg.Al80);
        Check("AL80 sink plays the keyboard role with 10 virtual LEDs", sink.RoleHint == DeviceRole.Keyboard && sink.LedCount == 10 && sink.Positions.Length == 10);
        var facts = new DeviceFacts(DeviceRole.Keyboard, true, true, null, true, 0, 5, null) { ColourStream = true, Ack = LightAck.Accepted };
        var st = LightingStatus.Evaluate(new AppConfig { KeyboardVerified = false, Rgb = new RgbConfig { Enabled = true, Keyboard = true, Effect = EffectKind.GradientWave } }, facts);
        Check("AL80 keyboard status: shared live, no test-write step", st.State == DeviceLightState.Shared && st.Headline.Contains("live"), st.Headline);
    }
}
