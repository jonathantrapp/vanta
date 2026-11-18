using System.Diagnostics;
using HwSuite.Core;
using HwSuite.Hardware.Al80;

namespace HwSuite.Cli;

/// <summary>
/// --al80 &lt;command&gt;: one-shot AL80 checks while the VANTA app is closed (they need the instance lock and the interface).
/// Screen opcodes 0x40-0x42 and allow-listed VIA commands only (Al80Protocol); nothing here can send 0xB0-0xB7.
/// </summary>
public static class Al80Cli
{
    public const string Usage =
        "  --al80 probe                       list the AL80's interfaces and read VIA version, layers, lighting\n" +
        "  --al80 clock                       set the keyboard clock and date\n" +
        "  --al80 view home|gif|next          switch the screen page\n" +
        "  --al80 gauge N | gauge release     battery icon as a gauge (N percent) or back to the keyboard\n" +
        "  --al80 picture FILE                send one still (RAM once the 8 flash slots are full)\n" +
        "  --al80 panel                       render VANTA's System panel with sample readings and send it\n" +
        "  --al80 bench [N] [--window W]      time N solid-colour pictures\n" +
        "  --al80 gif FILE [home|page|boot] [--fps F]   store an animation (a flash write)\n" +
        "  --al80 light CH VAL BYTE [BYTE]    volatile VIA lighting set (CH 2 or 3; VAL 1 bright, 2 effect, 3 speed, 4 hue sat)\n" +
        "  --al80 nowplaying FILE.png [--send]  draw Spotify's current track (cover, title, artist); --send also shows it (a picture upload)\n" +
        "  --al80 deadlock FILE.png [--live]  draw the Deadlock stats panel (sample data; --live asks the Deadlock API for the signed-in Steam account); PNG only, no keyboard\n" +
        Al80SrgbCli.Usage;

    /// <summary>1.21: read the Spotify track, draw it, save a 4x PNG (and the 96x160 original). No keyboard access.</summary>
    public static int NowPlayingPreview(string[] args)
    {
        var path = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : "nowplaying.png";
        int rc = 0;
        var t = new Thread(() =>
        {
            using var w = new NowPlayingWatcher();
            w.Start();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && (w.Latest == null || w.Latest.Art == null)) Thread.Sleep(200);
            var np = w.Latest;
            Console.WriteLine(np == null ? "Spotify: nothing playing" + (w.Error is string e ? $" ({e})" : "")
                : $"{np.App}: {np.Title} — {np.Artist} [{np.Album}] {(np.Playing ? "playing" : "paused")}, cover {(np.Art?.Length is int n ? n + " bytes" : "none")}");
            var px = np == null ? Al80Imaging.RenderNothingPlaying() : Al80Imaging.RenderNowPlaying(np.Title, np.Artist, np.Playing, Al80Imaging.Decode(np.Art));
            Save(px, path, 1);
            Save(px, Path.ChangeExtension(path, null) + "@4x.png", 4);
            Console.WriteLine("saved " + Path.GetFullPath(path));
        });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        return rc;
    }

    /// <summary>A made-up player for the preview.</summary>
    public static HwSuite.Hardware.Games.DeadlockSummary SampleDeadlock() => new(9, 3, "Phantom", 0xB794F6, 12, 8, 5.1, 4.2, 7.8,
        new[] { 1, 1, 2, 1, 2, 2, 1, 1, 1, 2 },
        new HwSuite.Hardware.Games.DeadlockMatch(1, 1, "Infernus", 7, 3, 9, 1, DateTime.UtcNow.AddHours(-2), 1900), DateTime.UtcNow);

    /// <summary>
    /// 1.22: draw the Deadlock panel and save it as a PNG (plus a 4x copy). Sample data by default; --live asks the Deadlock API for the
    /// Steam account signed in on this PC (that account number goes to api.deadlock-api.com in the URL, as it does in the app).
    /// No keyboard access.
    /// </summary>
    public static int DeadlockPreview(string[] args)
    {
        var path = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : "deadlock.png";
        HwSuite.Hardware.Games.DeadlockPanel panel = new(SampleDeadlock(), null, false);
        if (args.Contains("--live"))
        {
            var id = HwSuite.Hardware.Games.SteamAccount.FindLocal();
            if (id == null) { Console.WriteLine("no signed-in Steam account found on this PC"); return 1; }
            Console.WriteLine("asking the Deadlock API for the signed-in Steam account...");
            try
            {
                using var c = new HwSuite.Hardware.Games.DeadlockClient();
                var s = c.FetchAsync(id.Value, CancellationToken.None).GetAwaiter().GetResult();
                panel = new(s, null, false);
                Console.WriteLine($"{s.Rank}: {s.Wins}W {s.Losses}L over the last {s.Wins + s.Losses} scored matches, KDA {s.Kda:0.0}");
            }
            catch (Exception ex) { Console.WriteLine("failed: " + ex.Message); panel = new(null, ex.Message, false); }
        }
        var t = new Thread(() =>
        {
            var px = Al80Imaging.RenderDeadlock(panel);
            Save(px, path, 1);
            Save(px, Path.ChangeExtension(path, null) + "@4x.png", 4);
            Console.WriteLine("saved " + Path.GetFullPath(path));
        });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        return 0;
    }

    /// <summary>Diagnostic: log every change in what Spotify reports (title, play state, cover size + hash) for N minutes.</summary>
    public static int NowPlayingWatch(string[] args)
    {
        double minutes = args.Length > 2 && double.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 30;
        var outPath = args.Length > 3 ? args[3] : "nowplaying-watch.txt";
        Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager? mgr = null;
        string last = "";
        var end = DateTime.Now.AddMinutes(minutes);
        while (DateTime.Now < end)
        {
            string line;
            try
            {
                mgr ??= Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
                var s = mgr.GetSessions().FirstOrDefault(x => (x.SourceAppUserModelId ?? "").Contains("spotify", StringComparison.OrdinalIgnoreCase));
                if (s == null) line = "no Spotify session";
                else
                {
                    var p = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                    string thumb;
                    if (p.Thumbnail == null) thumb = "thumbnail null";
                    else
                    {
                        try
                        {
                            using var st = p.Thumbnail.OpenReadAsync().AsTask().GetAwaiter().GetResult();
                            using var ms = new MemoryStream();
                            st.AsStreamForRead().CopyTo(ms);
                            var h = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(ms.ToArray()))[..8];
                            thumb = $"cover {ms.Length} bytes {h}";
                        }
                        catch (Exception ex) { thumb = "cover read failed: " + ex.Message; }
                    }
                    line = $"{p.Title} | {p.Artist} | {s.GetPlaybackInfo()?.PlaybackStatus} | {thumb}";
                }
            }
            catch (Exception ex) { line = "error: " + ex.Message; mgr = null; }
            if (line != last) { var l = $"{DateTime.Now:HH:mm:ss.fff} {line}"; Console.WriteLine(l); File.AppendAllText(outPath, l + Environment.NewLine); last = line; }
            Thread.Sleep(300);
        }
        return 0;
    }

    static void Save(byte[] bgra, string path, int scale)
    {
        System.Windows.Media.Imaging.BitmapSource b = Al80Imaging.ToBitmap(bgra, Al80Protocol.Width, Al80Protocol.Height);
        if (scale > 1) b = new System.Windows.Media.Imaging.TransformedBitmap(b, new System.Windows.Media.ScaleTransform(scale, scale));
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(b));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static int Run(string[] args)
    {
        var cmd = args.Length > 1 ? args[1].ToLowerInvariant() : "probe";
        if (cmd == "probe") return Probe();
        using var dev = Al80Device.OpenUsb();
        if (int.TryParse(Opt(args, "--window"), out var w)) dev.Window = Math.Clamp(w, 1, 32);
        Console.WriteLine($"opened {dev.Name}");
        var sw = Stopwatch.StartNew();
        switch (cmd)
        {
            case "srgb":
                return Al80SrgbCli.Run(dev, args);
            case "nowplaying":
            {
                NowPlayingPreview(args);
                var np = new NowPlayingWatcher(); np.Start();
                for (int i = 0; i < 25 && np.Latest == null; i++) Thread.Sleep(200);
                byte[]? rgb = null;
                var th = new Thread(() =>
                {
                    var l = np.Latest;
                    rgb = Al80Imaging.ToRgb565(l == null ? Al80Imaging.RenderNothingPlaying() : Al80Imaging.RenderNowPlaying(l.Title, l.Artist, l.Playing, Al80Imaging.Decode(l.Art)));
                });
                th.SetApartmentState(ApartmentState.STA); th.Start(); th.Join();
                np.Dispose();
                dev.Send(Al80Protocol.PictureStream(rgb!));
                Console.WriteLine($"sent in {sw.Elapsed.TotalSeconds:0.00} s ({dev.Retries} resends)");
                return 0;
            }
            case "clock":
                dev.Send(Al80Protocol.ClockStream(DateTime.Now));
                Console.WriteLine($"clock set to {DateTime.Now:HH:mm:ss ddd d MMM yyyy}");
                return 0;
            case "view":
                var v = args.Length > 2 ? args[2] : "home";
                dev.Send(Al80Protocol.ViewStream(v == "gif" ? Al80Protocol.View.Gif : v == "next" ? Al80Protocol.View.NextPicture : Al80Protocol.View.Home));
                return 0;
            case "gauge":
                if (args.Length > 2 && args[2] == "release") dev.Send(Al80Protocol.GaugeReleaseStream());
                else dev.Send(Al80Protocol.GaugeStream(args.Length > 2 && int.TryParse(args[2], out var g) ? g : 50));
                return 0;
            case "picture":
            {
                if (args.Length < 3) { Console.WriteLine("usage: --al80 picture FILE"); return 2; }
                var px = Al80Imaging.Cover(Al80Imaging.Load(args[2]), Al80Protocol.Width, Al80Protocol.Height);
                dev.Send(Al80Protocol.PictureStream(Al80Protocol.Bgra32ToRgb565(px)));
                Console.WriteLine($"sent in {sw.Elapsed.TotalSeconds:0.00} s ({dev.Retries} resends)");
                return 0;
            }
            case "panel":
            {
                var px = Al80Imaging.RenderSystem(new Al80Readings(37, 52, 81, 66, 48, 15.2, DateTime.Now));
                dev.Send(Al80Protocol.PictureStream(Al80Protocol.Bgra32ToRgb565(px)));
                Console.WriteLine($"System panel (sample readings CPU 37% 52°, GPU 81% 66°, RAM 48%) sent in {sw.Elapsed.TotalSeconds:0.00} s");
                return 0;
            }
            case "bench":
            {
                int n = args.Length > 2 && int.TryParse(args[2], out var bn) ? Math.Clamp(bn, 1, 50) : 5;
                var times = new List<double>();
                for (int i = 0; i < n; i++)
                {
                    int shade = 255 * i / Math.Max(1, n - 1);
                    int v565 = ((shade >> 3) << 11) | ((80 >> 2) << 5) | ((255 - shade) >> 3);
                    var frame = new byte[Al80Protocol.FrameBytes];
                    for (int k = 0; k < frame.Length; k += 2) { frame[k] = (byte)(v565 >> 8); frame[k + 1] = (byte)v565; }
                    var t = Stopwatch.StartNew();
                    dev.Send(Al80Protocol.PictureStream(frame));
                    times.Add(t.Elapsed.TotalSeconds);
                    Console.WriteLine($"frame {i + 1}: {times[^1]:0.00} s");
                }
                Console.WriteLine($"average {times.Average():0.00} s per frame, window {dev.Window}, {dev.Retries} resends");
                return 0;
            }
            case "gif":
            {
                if (args.Length < 3) { Console.WriteLine("usage: --al80 gif FILE [home|page|boot] [--fps F]"); return 2; }
                var where = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : "home";
                var target = Al80Protocol.GifTargets.FirstOrDefault(t => t.Id == where) ?? Al80Protocol.HomeStrip;
                var (frames, fps) = Al80Imaging.LoadGif(args[2], target, int.TryParse(Opt(args, "--fps"), out var f) ? f : null);
                Console.WriteLine($"{frames.Count} frames at {fps} fps to {target.Name}");
                dev.Send(Al80Protocol.GifStream(frames, target, fps), (d, tot) => Console.Write($"\r{100 * d / tot}%"));
                Console.WriteLine();
                if (target != Al80Protocol.Boot) dev.Send(Al80Protocol.ViewStream(target == Al80Protocol.GifPage ? Al80Protocol.View.Gif : Al80Protocol.View.Home));
                Console.WriteLine($"stored in {sw.Elapsed.TotalSeconds:0.0} s");
                return 0;
            }
            case "light":
            {
                if (args.Length < 5 || !byte.TryParse(args[2], out var ch) || !byte.TryParse(args[3], out var val)) { Console.WriteLine("usage: --al80 light CH VAL BYTE [BYTE]"); return 2; }
                var data = args.Skip(4).Where(a => !a.StartsWith("--")).Select(a => Convert.ToByte(a, a.StartsWith("0x") ? 16 : 10)).ToArray();
                var r = dev.Via(Al80Protocol.LightSet(ch, val, data));
                var back = dev.Via(Al80Protocol.LightGet(ch, val));
                Console.WriteLine($"set {Hex(r, 6)} read back {Hex(back, 6)} (not saved)");
                return 0;
            }
            default:
                Console.WriteLine(Usage);
                return 2;
        }
    }

    static int Probe()
    {
        var all = Lighting.Devices.HidEnumerator.Find(Al80Protocol.Vid, Al80Protocol.Pid);
        if (all.Count == 0) { Console.WriteLine("no AL80 on USB"); return 1; }
        foreach (var i in all) Console.WriteLine(i + (i.HasUsage(Al80Protocol.RawUsagePage, Al80Protocol.RawUsage) ? "  <- screen/VIA" : ""));
        using var dev = Al80Device.OpenUsb();
        Console.WriteLine($"firmware revision {(dev.Revision is int rev ? rev.ToString("X4") : "unknown")}" + (dev.SrgbFirmware ? " = has the SignalRGB handler (see --al80 srgb info)" : " (no verified SignalRGB handler; SignalRGB commands are never sent to it)"));
        var p = dev.Via(Al80Protocol.Via(0x01));
        Console.WriteLine($"VIA protocol {p[1] << 8 | p[2]}, layers {dev.Via(Al80Protocol.Via(0x11))[1]}, macros {dev.Via(Al80Protocol.Via(0x0C))[1]}");
        foreach (var ch in new byte[] { 3, 2 })
            Console.WriteLine($"lighting channel {ch}: " + string.Join(", ", Enumerable.Range(1, 4).Select(v => { var r = dev.Via(Al80Protocol.LightGet(ch, (byte)v)); return $"{v}={r[3]}/{r[4]}"; })));
        return 0;
    }

    static string Hex(byte[] b, int n) => BitConverter.ToString(b, 0, n).Replace('-', ' ');
    static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
