using System.Diagnostics;
using HwSuite.Hardware.Al80;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>
/// 1.19.0: --al80 srgb ...: SignalRGB per-key tests for the owner to watch (VANTA closed). Every command first checks that
/// the keyboard reports a USB revision verified to have the SignalRGB handler (0x0122, 0x0125) and sends nothing otherwise:
/// on other builds (0x0119) IDs 0x21-0x28 reach the vendor's own handler. Commands that light keys end with 0x26 (the keyboard's own effect back)
/// unless --hold is given. Nothing here writes EEPROM.
/// </summary>
public static class Al80SrgbCli
{
    public const string Usage =
        "  --al80 srgb info                   firmware revision; if it has the SignalRGB handler (rev 0122/0125): QMK/protocol/LED count replies\n" +
        "  --al80 srgb rows [--ms T]          light the six key rows one after another (quick map check)\n" +
        "  --al80 srgb sweep [--ms T] [--from A] [--to B]   one LED at a time, printing the key it should be\n" +
        "  --al80 srgb pattern [--sec S]      Esc red, Space white, WASD green, arrows blue, Enter/Backspace yellow, F1/F12 purple\n" +
        "  --al80 srgb keyless [--ms T]       the five LEDs without a key, one at a time (orange)\n" +
        "  --al80 srgb led N [--sec S]        light one LED (N = LED index) white\n" +
        "  --al80 srgb wave [--sec S]         rainbow wave left to right by key position\n" +
        "  --al80 srgb rain [--sec S]         drops falling down the key rows (Rainfall check)\n" +
        "  --al80 srgb rate [--frames N]      full frames back to back: frames/s and write time\n" +
        "  --al80 srgb between                frames, then a clock update and a VIA read between frames, then frames\n" +
        "  --al80 srgb vanta [--effect E] [--sec S]   VANTA's real per-key service with your saved effect (or E)\n" +
        "  --al80 srgb off                    0x26: the keyboard's own effect back\n" +
        "  add --hold to keep the last colours instead of sending 0x26 at the end";

    public static int Run(Al80Device dev, string[] args)
    {
        var sub = args.Length > 2 ? args[2].ToLowerInvariant() : "info";
        Console.WriteLine($"firmware revision: {(dev.Revision is int r ? r.ToString("X4") : "unknown")}");
        if (!dev.SrgbFirmware)
        {
            Console.WriteLine("this revision is not verified to have the SignalRGB handler (0122/0125): no SignalRGB command was sent (on other builds 0x21-0x28 mean something else).");
            return 1;
        }
        bool hold = args.Contains("--hold");
        int Int(string name, int def) => int.TryParse(Opt(args, name), out var v) ? v : def;
        double Dbl(string name, double def) => double.TryParse(Opt(args, name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;

        if (sub == "info") return Info(dev);
        if (sub == "vanta") return Vanta(dev, args);
        if (sub == "off") { dev.SrgbMode(false); Console.WriteLine("0x26 sent: the keyboard's own effect should be back"); return 0; }

        var frame = new Rgb[Al80KeyMap.Leds];
        void Show() { var e = dev.SrgbWrite(Al80Protocol.SrgbFrame(frame)); if (e != null) Console.WriteLine("keyboard reported: " + e); }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _cancel = true; };
        dev.SrgbMode(true);
        Console.WriteLine("0x25 sent (SignalRGB mode on; nothing saved)");
        try
        {
            switch (sub)
            {
                case "rows":
                {
                    int ms = Int("--ms", 2000);
                    var rows = Al80KeyMap.Keys.Where(k => k.HasKey).GroupBy(k => k.Row).OrderBy(g => g.Key);
                    foreach (var g in rows)
                    {
                        if (_cancel) break;
                        Array.Fill(frame, Rgb.Black);
                        foreach (var k in g) frame[k.Led] = new Rgb(255, 255, 255);
                        Show();
                        Console.WriteLine($"row {g.Key}: " + string.Join(" ", g.OrderBy(k => k.X).Select(k => k.Name)));
                        Thread.Sleep(ms);
                    }
                    Array.Fill(frame, Rgb.Black);
                    foreach (var k in Al80KeyMap.Keys.Where(k => !k.HasKey)) frame[k.Led] = new Rgb(255, 120, 0);
                    Show();
                    Console.WriteLine("LEDs without a key (orange): " + string.Join(", ", Al80KeyMap.Keys.Where(k => !k.HasKey).Select(k => k.Led)));
                    Thread.Sleep(ms);
                    break;
                }
                case "sweep":
                {
                    int ms = Int("--ms", 700), from = Math.Clamp(Int("--from", 0), 0, Al80KeyMap.Leds - 1), to = Math.Clamp(Int("--to", Al80KeyMap.Leds - 1), from, Al80KeyMap.Leds - 1);
                    for (int i = from; i <= to && !_cancel; i++)
                    {
                        Array.Fill(frame, new Rgb(0, 0, 24));          // dim blue background, so the board is visibly in the test
                        frame[i] = new Rgb(255, 255, 255);
                        Show();
                        var k = Al80KeyMap.Keys[i];
                        Console.WriteLine($"LED {i,2} -> {k.Name}{(k.HasKey ? $"  (matrix {k.Row},{k.Col})" : "  (no key in the firmware table)")}");
                        Thread.Sleep(ms);
                    }
                    break;
                }
                case "pattern":
                {
                    // keys anyone can recognise, one colour per group: an off-by-one map lights the neighbours instead
                    Array.Fill(frame, Rgb.Black);
                    void Paint(Rgb c, params string[] names)
                    {
                        foreach (var n in names) { int i = Array.FindIndex(Al80KeyMap.Keys, k => k.Name == n); if (i >= 0) frame[i] = c; }
                    }
                    Paint(new Rgb(255, 0, 0), "Esc");
                    Paint(new Rgb(255, 255, 255), "Space");
                    Paint(new Rgb(0, 255, 0), "W", "A", "S", "D");
                    Paint(new Rgb(0, 80, 255), "Up", "Left", "Down", "Right");
                    Paint(new Rgb(255, 200, 0), "Enter", "Backspace");
                    Paint(new Rgb(255, 0, 255), "F1", "F12");
                    double sec = Dbl("--sec", 20);
                    Console.WriteLine("Esc red, Space white, W A S D green, the four arrows blue, Enter and Backspace yellow, F1 and F12 purple; everything else dark");
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < sec && !_cancel) { Show(); Thread.Sleep(200); }   // keep-alive: the firmware drops SignalRGB after 500 ms of silence
                    break;
                }
                case "keyless":
                {
                    // the five LEDs with no key in the firmware table, one at a time
                    int ms = Int("--ms", 4000), n = 0;
                    foreach (var k in Al80KeyMap.Keys.Where(k => !k.HasKey))
                    {
                        if (_cancel) break;
                        Array.Fill(frame, Rgb.Black); frame[k.Led] = new Rgb(255, 120, 0);
                        Console.WriteLine($"#{++n}: LED {k.Led} (firmware position x {k.X}, y {k.Y})");
                        var sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < ms && !_cancel) { Show(); Thread.Sleep(200); }
                    }
                    break;
                }
                case "led":
                {
                    int n = args.Length > 3 && int.TryParse(args[3], out var v) ? v : 0;
                    if (n < 0 || n >= Al80KeyMap.Leds) { Console.WriteLine($"LED index 0..{Al80KeyMap.Leds - 1}"); return 2; }
                    Array.Fill(frame, Rgb.Black); frame[n] = new Rgb(255, 255, 255); Show();
                    Console.WriteLine($"LED {n} ({Al80KeyMap.Keys[n].Name}) lit");
                    Thread.Sleep((int)(Dbl("--sec", 5) * 1000));
                    break;
                }
                case "wave":
                case "rain":
                {
                    double sec = Dbl("--sec", 10);
                    var sw = Stopwatch.StartNew();
                    int frames = 0;
                    var rnd = new Random(7);
                    var drops = new List<(double x, double y, double v)>();
                    double last = 0;
                    while (sw.Elapsed.TotalSeconds < sec && !_cancel)
                    {
                        double t = sw.Elapsed.TotalSeconds, dt = t - last; last = t;
                        if (sub == "wave")
                            for (int i = 0; i < frame.Length; i++) frame[i] = Hsv((Al80KeyMap.Keys[i].X / 224.0 - t * 0.4) % 1.0);
                        else
                        {
                            // drops fall from the function row to the bottom row (QMK y 0 -> 64) with short trails
                            if (rnd.NextDouble() < dt * 6) drops.Add((rnd.NextDouble() * 224, -8, 40 + rnd.NextDouble() * 40));
                            for (int d = drops.Count - 1; d >= 0; d--) { var p = drops[d]; p.y += p.v * dt; drops[d] = p; if (p.y > 80) drops.RemoveAt(d); }
                            for (int i = 0; i < frame.Length; i++)
                            {
                                var k = Al80KeyMap.Keys[i];
                                double lum = 0.04;
                                foreach (var p in drops)
                                {
                                    double dx = Math.Abs(k.X - p.x), dy = p.y - k.Y;
                                    if (dx < 9 && dy > -4 && dy < 24) lum = Math.Max(lum, (1 - dx / 9) * (dy < 0 ? 1 + dy / 4 : 1 - dy / 24));
                                }
                                frame[i] = new Rgb((byte)(40 * lum), (byte)(140 * lum), (byte)(255 * lum));
                            }
                        }
                        Show(); frames++;
                        Thread.Sleep(Math.Max(1, (int)(1000 / Al80Service.PerKeyMaxHz) - 2));
                    }
                    Console.WriteLine($"{frames} frames in {sw.Elapsed.TotalSeconds:0.0} s ({frames / sw.Elapsed.TotalSeconds:0.0}/s)");
                    break;
                }
                case "rate":
                {
                    int n = Math.Clamp(Int("--frames", 300), 10, 5000);
                    var times = new List<double>(n);
                    var all = Stopwatch.StartNew();
                    for (int f = 0; f < n && !_cancel; f++)
                    {
                        for (int i = 0; i < frame.Length; i++) frame[i] = Hsv((i / (double)frame.Length + f / 60.0) % 1.0);
                        var t = Stopwatch.StartNew();
                        Show();
                        times.Add(t.Elapsed.TotalMilliseconds);
                    }
                    times.Sort();
                    Console.WriteLine($"{times.Count} full frames (10 packets each) in {all.Elapsed.TotalSeconds:0.00} s = {times.Count / all.Elapsed.TotalSeconds:0.0} frames/s");
                    Console.WriteLine($"write time per frame: median {times[times.Count / 2]:0.0} ms, 95th {times[(int)(times.Count * 0.95)]:0.0} ms, max {times[^1]:0.0} ms; packets total {dev.SrgbPackets}");
                    break;
                }
                case "between":
                {
                    for (int round = 0; round < 3 && !_cancel; round++)
                    {
                        for (int f = 0; f < 60; f++) { for (int i = 0; i < frame.Length; i++) frame[i] = Hsv((i / 84.0 + f / 60.0) % 1.0); Show(); Thread.Sleep(30); }
                        var sw = Stopwatch.StartNew();
                        dev.Send(Al80Protocol.ClockStream(DateTime.Now, 1));
                        Console.WriteLine($"round {round + 1}: clock update between frames in {sw.Elapsed.TotalMilliseconds:0} ms");
                        var p = dev.Via(Al80Protocol.Via(0x01));
                        Console.WriteLine($"round {round + 1}: VIA protocol read between frames: {p[1] << 8 | p[2]}");
                    }
                    break;
                }
                default:
                    Console.WriteLine(Usage);
                    return 2;
            }
        }
        finally
        {
            if (!hold) { try { dev.SrgbMode(false); Console.WriteLine("0x26 sent: the keyboard's own effect should be back"); } catch (Exception ex) { Console.WriteLine("0x26 failed: " + ex.Message); } }
            else Console.WriteLine("--hold: last colours kept (SignalRGB mode stays on until 0x26 or a power cut)");
        }
        return 0;
    }

    static volatile bool _cancel;

    /// <summary>
    /// VANTA's real per-key path end to end on the keyboard: <see cref="Al80Service"/> (its own thread, keep-alive,
    /// 0x25/0x26, changed-chunk packets) fed by the engine's own effect renderer with the owner's saved lighting settings and
    /// arrangement (read-only; the screen is left alone). Reports frames, packets, errors and timing.
    /// </summary>
    static int Vanta(Al80Device opened, string[] args)
    {
        double sec = double.TryParse(Opt(args, "--sec"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 20;
        var cfg = Core.ConfigStore.Load();
        var rgb = cfg.Rgb;
        if (Opt(args, "--effect") is string en && Enum.TryParse<Core.EffectKind>(en, true, out var ek)) rgb = rgb.WithEffect(ek);
        cfg.Al80.Enabled = true; cfg.Al80.Mode = "off"; cfg.Al80.PerKey = true; if (args.Contains("--no-side")) cfg.Al80.SyncSideBar = false;   // copy in memory only: never saved (CLI is read-only)
        opened.Dispose();   // the service opens the keyboard itself, the way VANTA does
        var snap = new Hardware.TelemetrySnapshot();
        using var svc = new Al80Service(cfg, () => snap);
        svc.Start();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!svc.PerKey && DateTime.UtcNow < until) Thread.Sleep(50);
        Console.WriteLine($"service: {svc.Status}; {svc.PerKeyInfo}");
        if (!svc.PerKey) { Console.WriteLine("per-key not available; nothing streamed"); return 1; }
        var layout = new LayoutTransform(cfg.Arrangement, 1, rgb.Case);
        var sink = new Al80Sink(rgb, () => svc, () => cfg.Al80);
        var geo = layout.GeometryFor(DeviceRole.Keyboard, Al80KeyMap.Leds);
        Console.WriteLine($"effect {rgb.Effect}, speed {rgb.Speed:0.00}, brightness {rgb.Brightness:0.00}, colours {rgb.ColorA:X6}/{rgb.ColorB:X6}; geometry surface={geo.Surface}, {geo.Count} LEDs, keyboard turn {layout.KeyboardRotation}");
        double drift = 0, driftTime = 0;
        var frame = new Rgb[Al80KeyMap.Leds];
        var clock = Stopwatch.StartNew();
        int offered = 0; long lit = 0;
        while (clock.Elapsed.TotalSeconds < sec && !_cancel)
        {
            double t = clock.Elapsed.TotalSeconds;
            var ctx = EffectContext.From(rgb, t, 45, drift: EffectContext.AdvanceDrift(ref drift, ref driftTime, t, rgb.Speed));
            Effects.Render(rgb.Effect, ctx, geo, frame);
            svc.SetFrame(frame, Al80Sink.LiveColor(frame, Core.EffectInfo.IsScene(rgb.Effect)));
            offered++; lit += frame.Count(c => c.R + c.G + c.B > 30);
            Thread.Sleep(1000 / 60);
        }
        Console.WriteLine($"{offered} frames offered at 60/s in {clock.Elapsed.TotalSeconds:0.0} s; service wrote {svc.FramesSent} frames ({svc.FramesSent / clock.Elapsed.TotalSeconds:0.0}/s), {svc.FrameErrors} keyboard errors; average {lit / (double)Math.Max(1, offered):0.0} lit LEDs per frame");
        Console.WriteLine($"status: {svc.LightStatus}; 3-LED light updates {svc.SideWrites}; last error: {svc.LastError ?? "none"}");
        if (args.Contains("--hold")) { svc.SetFrame(frame, hold: true); Thread.Sleep(500); Console.WriteLine("held the last frame"); }
        else { svc.ReleaseFrame(); Thread.Sleep(800); Console.WriteLine("released: 0x26, the keyboard's own effect back"); }
        return svc.FrameErrors == 0 && svc.LastError == null ? 0 : 1;
    }

    static int Info(Al80Device dev)
    {
        foreach (var (cmd, what) in new[] { (Al80Protocol.SrgbQmkVersion, "QMK version"), (Al80Protocol.SrgbProtocol, "SignalRGB protocol"), (Al80Protocol.SrgbUniqueId, "unique id"), (Al80Protocol.SrgbTotalLeds, "total LEDs"), (Al80Protocol.SrgbFirmwareType, "firmware type") })
        {
            var r = dev.SrgbQuery(cmd);
            Console.WriteLine($"0x{cmd:X2} {what,-18}: " + (r == null ? "no reply" : BitConverter.ToString(r, 0, 8).Replace('-', ' ') + cmd switch
            {
                Al80Protocol.SrgbQmkVersion => $"  = {r[1]}.{r[2]}.{r[3]}",
                Al80Protocol.SrgbProtocol => $"  = {r[1]}.{r[2]}.{r[3]}",
                Al80Protocol.SrgbTotalLeds => $"  = {r[1]} (expected {Al80KeyMap.Leds})",
                Al80Protocol.SrgbFirmwareType => r[1] == 2 ? "  = VIA build" : r[1] == 1 ? "  = mainline" : "",
                _ => ""
            }));
        }
        return 0;
    }

    static Rgb Hsv(double h)
    {
        h = (h % 1 + 1) % 1 * 6; int i = (int)h; double f = h - i;
        byte q = (byte)(255 * (1 - f)), t = (byte)(255 * f);
        return i switch { 0 => new(255, t, 0), 1 => new(q, 255, 0), 2 => new(0, 255, t), 3 => new(0, q, 255), 4 => new(t, 0, 255), _ => new(255, 0, q) };
    }

    static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
