using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>
/// --mouse-probe: owner-watched tests for the questions static analysis of Glorious CORE could not answer (stored or
/// temporary, speed byte to period, phase reset, multi-colour breathing). See docs\MOUSE_SYNC_TEST_PLAN.md.
/// Sends only the verified report-0x00 lighting sequence (effect + colours, wired brightness, wireless brightness, 30 ms
/// apart), never more than one application per second and never more than 20 per run. The last setting stays on the
/// mouse; VANTA sends its own setting again when it next connects.
/// </summary>
public static class MouseProbe
{
    const int MinGapMs = 1000, MaxApplies = 20;

    public sealed record Plan(byte Effect, byte[] Speeds, Rgb[] Colors, byte Brightness, string? Via, int HoldSeconds, int ResendMs, int Countdown, int Blink = 0, bool StripMarker = false);

    sealed record Apply(int Index, byte Effect, byte Speed, string Utc, double SinceStartMs, double ApplyMs, bool Ok, string Error, string Report, double? MarkerMs);

    public static string Usage =>
        "--mouse-probe <effect 0-8> <speed[,speed..]> [RRGGBB,RRGGBB..] [--brightness 0-255] [--via cable|receiver] [--hold S] [--resend-ms N] [--blink N] [--strip-marker] [--countdown S] [--out f.json]";

    /// <summary>Parse and check the arguments; null with a reason when the plan is not allowed.</summary>
    public static Plan? Parse(string[] args, out string error)
    {
        error = "";
        if (args.Length < 3) { error = "usage: " + Usage; return null; }
        if (!byte.TryParse(args[1], out var effect) || effect > GloriousWirelessDevice.MaxEffect) { error = $"effect must be 0-{GloriousWirelessDevice.MaxEffect}"; return null; }
        var speeds = new List<byte>();
        foreach (var part in args[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!byte.TryParse(part, out var sp)) { error = $"bad speed byte '{part}'"; return null; }
            // CORE's own range: (105 - speed%) / 5 = 1..21, or (105 - speed%) * 2 = 10..210 for Rave / Wave
            bool fast = effect is GloriousWirelessDevice.EffectRave or GloriousWirelessDevice.EffectWave;
            if (fast ? sp < 10 || sp > 210 : sp < 1 || sp > 21) { error = $"speed byte {sp} is outside the range Glorious CORE sends for effect {effect} ({(fast ? "10-210" : "1-21")})"; return null; }
            speeds.Add(sp);
        }
        if (speeds.Count == 0) { error = "no speed byte given"; return null; }
        var colors = new List<Rgb>();
        if (args.Length > 3 && !args[3].StartsWith("--"))
            foreach (var hex in args[3].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) { error = $"bad colour '{hex}' (use RRGGBB)"; return null; }
                colors.Add(Rgb.FromInt(v));
            }
        if (colors.Count > GloriousWirelessDevice.MaxColors) { error = $"at most {GloriousWirelessDevice.MaxColors} colours"; return null; }
        if (effect != MouseLightingState.EffectOff && colors.Count == 0) { error = "give at least one colour for an effect other than off"; return null; }
        if (effect == MouseLightingState.EffectOff) colors.Clear();

        byte brightness = 255;
        if (Option(args, "--brightness") is string b && !byte.TryParse(b, out brightness)) { error = "brightness must be 0-255"; return null; }
        var via = Option(args, "--via")?.ToLowerInvariant();
        if (via is not (null or "cable" or "receiver")) { error = "--via must be cable or receiver"; return null; }
        int hold = 10, resend = 0, countdown = 5;
        if (Option(args, "--hold") is string h && (!int.TryParse(h, out hold) || hold < 2 || hold > 120)) { error = "--hold must be 2-120 s"; return null; }
        if (Option(args, "--resend-ms") is string r && (!int.TryParse(r, out resend) || resend < MinGapMs || resend > 60000)) { error = $"--resend-ms must be {MinGapMs}-60000"; return null; }
        if (Option(args, "--countdown") is string c && (!int.TryParse(c, out countdown) || countdown < 0 || countdown > 60)) { error = "--countdown must be 0-60 s"; return null; }
        if (resend > 0 && speeds.Count > 1) { error = "--resend-ms works with one speed byte only"; return null; }
        int blink = 0;
        if (Option(args, "--blink") is string bl)
        {
            // alternate the static colour with off, N writes, hold seconds apart: clean on/off steps for timing the write delay
            if (!int.TryParse(bl, out blink) || blink < 2 || blink > MaxApplies) { error = $"--blink must be 2-{MaxApplies}"; return null; }
            if (effect != MouseLightingState.EffectStatic || speeds.Count > 1 || resend > 0) { error = "--blink works with effect 4 (static), one speed byte and no --resend-ms"; return null; }
        }
        int writes = blink > 0 ? blink : speeds.Count * (resend > 0 ? 2 : 1);
        if (writes > MaxApplies) { error = $"more than {MaxApplies} applications in one run"; return null; }
        return new Plan(effect, speeds.ToArray(), colors.ToArray(), brightness, via, hold, resend, countdown, blink, args.Contains("--strip-marker"));
    }

    public static int Run(string[] args)
    {
        var plan = Parse(args, out var perr);
        if (plan == null) { Log.Error("mouse probe: " + perr); return 2; }
        if (Process.GetProcessesByName("Glorious Core").Length > 0) { Log.Error("mouse probe: Glorious CORE is running and would send its own lighting; close it first"); return 4; }

        // --strip-marker: the case fans (streamed, a few ms behind) flash white at each mouse write, as a timing reference on video
        AppServices? app = null; DLedSink? strip = null; RgbConfig? srgb = null;
        if (plan.StripMarker)
        {
            var owner = ConfigStore.Load();
            var acfg = new AppConfig { AutoStopGigabyte = false };   // the runner stops and restores the Gigabyte services
            srgb = acfg.Rgb;
            srgb.Enabled = false; srgb.DLedStrip = true; srgb.Motherboard = false; srgb.Ram = false; srgb.Mouse = false; srgb.Keyboard = false;
            srgb.DLedCount = Math.Clamp(owner.Rgb.DLedCount, 8, 120); srgb.StripOrder = owner.Rgb.StripOrder; srgb.DLedMaxHz = 60;
            srgb.Effect = EffectKind.Static; srgb.ColorA = 0; srgb.Brightness = 1;
            app = new AppServices(acfg);
            app.InitializeHardware();
            app.InitializeLighting(allowRamRemap: false);
            strip = app.Rgb.Sinks.OfType<DLedSink>().FirstOrDefault();
            if (strip?.Available != true) { Log.Error("mouse probe: D_LED controller unavailable for --strip-marker"); app.Dispose(); return 3; }
            app.Rgb.Start();
            Thread.Sleep(2500);
            var own = app.Ownership!.Check(Hardware.HwResource.RgbUsb);
            if (!own.MayWrite) { Log.Error("mouse probe: IT5701 not owned: " + own.Reason); app.Dispose(); return 4; }
            srgb.Enabled = true;
            Thread.Sleep(800);
            Log.Info($"mouse probe: strip marker ready ({srgb.DLedCount} LEDs, dark between markers)");
        }
        try { return RunWith(plan, args, strip, srgb); }
        finally
        {
            if (app != null)
            {
                srgb!.Enabled = false;
                Thread.Sleep(800);
                app.Dispose();
            }
        }
    }

    static int RunWith(Plan plan, string[] args, DLedSink? strip, RgbConfig? srgb)
    {
        var all = GloriousWirelessDevice.Enumerate();
        var pick = plan.Via switch
        {
            "cable" => GloriousWirelessDevice.Pick(all, GloriousWirelessDevice.PidCable),
            "receiver" => GloriousWirelessDevice.Pick(all, GloriousWirelessDevice.PidDongle),
            _ => GloriousTransport.Preferred(all)
        };
        if (pick == null) { Log.Error($"mouse probe: mouse not found{(plan.Via != null ? " on the " + plan.Via : "")} (VID 258A PID 2011 cable / 2022 receiver)"); return 3; }
        using var dev = new GloriousWirelessDevice();
        if (!dev.Open(pick, out var oerr)) { Log.Error("mouse probe: " + oerr); return 3; }
        var fw = dev.ReadFirmware();
        string link = dev.IsCable ? "cable" : "receiver";
        string colours = plan.Colors.Length == 0 ? "none" : string.Join(",", plan.Colors.Select(x => x.ToInt().ToString("X6")));
        Log.Info($"mouse probe: {link}, firmware {fw}; effect {plan.Effect}, speed byte(s) {string.Join(",", plan.Speeds)}, colours {colours}, brightness {plan.Brightness}, " +
                 $"hold {plan.HoldSeconds} s{(plan.ResendMs > 0 ? $", re-send after {plan.ResendMs} ms" : "")}");

        for (int s = plan.Countdown; s > 0; s--) { Log.Info($"mouse probe: first write in {s} s"); Thread.Sleep(1000); }

        var log = new List<Apply>();
        var clock = Stopwatch.StartNew();
        double lastStart = double.NegativeInfinity;
        bool Send(byte speed, bool off = false)
        {
            double wait = lastStart + MinGapMs - clock.Elapsed.TotalMilliseconds;
            if (wait > 0) Thread.Sleep((int)Math.Ceiling(wait));
            byte effect = off ? MouseLightingState.EffectOff : plan.Effect;
            var report = GloriousWirelessDevice.BuildEffectReport(effect, speed, off ? Array.Empty<Rgb>() : plan.Colors);
            double marker = double.NaN;
            if (strip != null)
            {
                // marker on just before the mouse write; off again 200 ms later
                var white = Enumerable.Repeat(new Rgb(255, 255, 255), srgb!.DLedCount).ToArray();
                strip.ShowTestPattern(white, TimeSpan.FromMilliseconds(150));
                marker = clock.Elapsed.TotalMilliseconds;
                strip.Post(new Rgb[srgb.DLedCount]);
                var sk = strip; int n = srgb.DLedCount;
                _ = Task.Delay(200).ContinueWith(_ => sk.Post(new Rgb[n]));
            }
            lastStart = clock.Elapsed.TotalMilliseconds;
            var t = Stopwatch.StartNew();
            bool ok = dev.Apply(report, plan.Brightness, out var err);
            var a = new Apply(log.Count + 1, effect, speed, dev.LastApplyStartUtc.ToString("O"), Math.Round(lastStart, 1), Math.Round(t.Elapsed.TotalMilliseconds, 1), ok, err,
                string.Join(" ", report.Take(12 + Math.Max(1, plan.Colors.Length) * 3).Select(x => x.ToString("X2"))), double.IsNaN(marker) ? null : Math.Round(marker, 1));
            log.Add(a);
            Log.Info($"mouse probe: write {a.Index} effect {effect} speed {speed} at {a.Utc} (+{a.SinceStartMs:0} ms) {(ok ? "ok" : "FAILED " + err)} in {a.ApplyMs:0} ms" +
                     (strip != null ? $"; strip marker posted {a.SinceStartMs - a.MarkerMs:0.0} ms before" : ""));
            return ok;
        }

        bool allOk = true;
        if (plan.Blink > 0)
            for (int i = 0; i < plan.Blink; i++) { allOk &= Send(plan.Speeds[0], off: i % 2 == 1); Thread.Sleep(plan.HoldSeconds * 1000); }
        else foreach (var speed in plan.Speeds)
        {
            allOk &= Send(speed);
            if (plan.ResendMs > 0)
            {
                Thread.Sleep(Math.Max(0, plan.ResendMs - (int)(clock.Elapsed.TotalMilliseconds - lastStart)));
                allOk &= Send(speed);
            }
            Thread.Sleep(plan.HoldSeconds * 1000);
        }
        Log.Info($"mouse probe: done, {log.Count} write(s), {dev.ReportsSent} feature reports, slowest SetFeature {dev.MaxReportMs:0.0} ms; the last setting stays on the mouse");

        if (Option(args, "--out") is string path)
        {
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    generated = DateTime.Now, link, firmware = fw, effect = plan.Effect, speeds = plan.Speeds.Select(x => (int)x), colours, brightness = plan.Brightness,
                    plan.HoldSeconds, plan.ResendMs, plan.Blink, plan.StripMarker, writes = log
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Warn("mouse probe: could not write " + path + ": " + ex.Message); }
        }
        return allOk ? 0 : 1;
    }

    static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
