using System.Diagnostics;
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>
/// --mouse-stream (test 8): owner-attended check of live colour on the Model O Wireless, the way SignalRGB's released
/// plugin drives it (docs\mouse-sync\RESEARCH_LIVE_AND_FIRMWARE.md): one static-colour effect report per frame. Over the
/// receiver that's the effect report only; over the cable both brightness reports follow it, as in SignalRGB's wired plugin.
/// For each rate it streams a rainbow sweep (one turn every 3 s) for a few seconds, with 1.5 s of black between rates so
/// the owner can tell the phases apart while moving the mouse and watching for cursor stutter. Logs every write's
/// duration and failures. At the end the mouse gets VANTA's last applied setting back.
/// </summary>
public static class MouseStream
{
    public const int MaxHz = 60, MaxSecondsPerRate = 30;

    public sealed record Plan(int SecondsPerRate, int[] Rates, string? Via, int Countdown);

    public static string Usage => "--mouse-stream <seconds per rate> <hz[,hz..]> [--via cable|receiver] [--countdown S] [--out f.json]";

    public static Plan? Parse(string[] args, out string error)
    {
        error = "";
        if (args.Length < 3) { error = "usage: " + Usage; return null; }
        if (!int.TryParse(args[1], out var secs) || secs < 3 || secs > MaxSecondsPerRate) { error = $"seconds per rate must be 3-{MaxSecondsPerRate}"; return null; }
        var rates = new List<int>();
        foreach (var part in args[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var hz) || hz < 1 || hz > MaxHz) { error = $"rate '{part}' must be 1-{MaxHz} per second"; return null; }
            rates.Add(hz);
        }
        if (rates.Count is 0 or > 6) { error = "give 1-6 rates"; return null; }
        var via = Opt(args, "--via")?.ToLowerInvariant();
        if (via is not (null or "cable" or "receiver")) { error = "--via must be cable or receiver"; return null; }
        int countdown = 10;
        if (Opt(args, "--countdown") is string c && (!int.TryParse(c, out countdown) || countdown < 0 || countdown > 60)) { error = "--countdown must be 0-60 s"; return null; }
        return new Plan(secs, rates.ToArray(), via, countdown);
    }

    sealed record Phase(int Hz, int Writes, int Failures, double AchievedHz, double MeanMs, double P99Ms, double MaxMs, string? FirstError);

    public static int Run(string[] args)
    {
        var plan = Parse(args, out var perr);
        if (plan == null) { Log.Error("mouse stream: " + perr); return 2; }
        if (Process.GetProcessesByName("Glorious Core").Length > 0) { Log.Error("mouse stream: Glorious CORE is running; close it first"); return 4; }
        var all = GloriousWirelessDevice.Enumerate();
        var pick = plan.Via switch
        {
            "cable" => GloriousWirelessDevice.Pick(all, GloriousWirelessDevice.PidCable),
            "receiver" => GloriousWirelessDevice.Pick(all, GloriousWirelessDevice.PidDongle),
            _ => GloriousTransport.Preferred(all)
        };
        if (pick == null) { Log.Error("mouse stream: mouse not found"); return 3; }
        using var dev = new GloriousWirelessDevice();
        if (!dev.Open(pick, out var oerr)) { Log.Error("mouse stream: " + oerr); return 3; }
        var fw = dev.ReadFirmware();
        bool cable = dev.IsCable;
        Log.Info($"mouse stream: {(cable ? "cable" : "receiver")}, firmware {fw}; rates {string.Join(", ", plan.Rates)}/s, {plan.SecondsPerRate} s each; " +
                 (cable ? "effect + both brightness reports per frame" : "effect report only per frame") + ". Move the mouse in circles and watch for stutter.");
        for (int s = plan.Countdown; s > 0; s--) { Log.Info($"mouse stream: starting in {s} s"); Thread.Sleep(1000); }

        // full brightness once, so the stream itself only needs the colour
        if (!dev.Apply(new MouseLightingState(MouseLightingState.EffectStatic, Rgb.Black, MouseLightingState.NeutralSpeed, 255, "stream start", false), out var e0))
        { Log.Error("mouse stream: first write failed: " + e0); return 1; }

        var phases = new List<Phase>();
        var bright1 = GloriousWirelessDevice.BuildBrightnessReport(255, true);
        var bright0 = GloriousWirelessDevice.BuildBrightnessReport(255, false);
        var clock = Stopwatch.StartNew();
        int index = 0;
        foreach (var hz in plan.Rates)
        {
            index++;
            Log.Info($"mouse stream: phase {index}: {hz}/s for {plan.SecondsPerRate} s");
            var times = new List<double>();
            int failures = 0; string? firstError = null;
            double start = clock.Elapsed.TotalSeconds, period = 1.0 / hz;
            long n = 0;
            while (clock.Elapsed.TotalSeconds - start < plan.SecondsPerRate)
            {
                double due = start + n * period;
                double wait = due - clock.Elapsed.TotalSeconds;
                if (wait > 0.002) Thread.Sleep((int)(wait * 1000));
                double t = clock.Elapsed.TotalSeconds - start;
                var color = Rgb.FromHsv(t / 3.0 * 360, 1, 1);
                var report = GloriousWirelessDevice.BuildEffectReport(MouseLightingState.EffectStatic, MouseLightingState.NeutralSpeed, new[] { color });
                var sw = Stopwatch.StartNew();
                bool ok = dev.SendReport(report, out var err);
                if (ok && cable) ok = dev.SendReport(bright1, out err) && dev.SendReport(bright0, out err);
                times.Add(sw.Elapsed.TotalMilliseconds);
                if (!ok) { failures++; firstError ??= err; if (failures >= 10) { Log.Error($"mouse stream: 10 failures at {hz}/s ({firstError}); stopping"); break; } }
                n++;
                // fell behind (slow write): carry on from now instead of bursting
                if (clock.Elapsed.TotalSeconds - (start + n * period) > 0.2) n = (long)Math.Ceiling((clock.Elapsed.TotalSeconds - start) / period);
            }
            double elapsed = clock.Elapsed.TotalSeconds - start;
            var sorted = times.OrderBy(x => x).ToArray();
            var ph = new Phase(hz, times.Count, failures, Math.Round(times.Count / elapsed, 1), Math.Round(times.Average(), 2),
                Math.Round(sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))], 2), Math.Round(sorted[^1], 2), firstError);
            phases.Add(ph);
            Log.Info($"mouse stream: phase {index} done: {ph.Writes} writes ({ph.AchievedHz}/s achieved), {ph.Failures} failed; write time mean {ph.MeanMs} ms, 99% {ph.P99Ms} ms, max {ph.MaxMs} ms");
            if (failures >= 10) break;
            dev.SendReport(GloriousWirelessDevice.BuildEffectReport(MouseLightingState.EffectStatic, MouseLightingState.NeutralSpeed, new[] { Rgb.Black }), out _);
            Thread.Sleep(1500);
        }

        // give the mouse VANTA's own last setting back (writes are not stored, so nothing else is left behind)
        var restore = AppServices.LoadMouseState() ?? new MouseLightingState(MouseLightingState.EffectStatic, new Rgb(0x1A, 0x35, 0x65), MouseLightingState.NeutralSpeed, 255, "fallback", false);
        bool restored = dev.Apply(restore, out var rerr);
        Log.Info($"mouse stream: finished; {dev.ReportsSent} reports in total; mouse set back to VANTA's last setting ({restore.Description}): {(restored ? "ok" : "FAILED " + rerr)}");

        if (Opt(args, "--out") is string path)
        {
            try { File.WriteAllText(path, JsonSerializer.Serialize(new { generated = DateTime.Now, link = cable ? "cable" : "receiver", firmware = fw, plan.SecondsPerRate, phases, restored }, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { Log.Warn("mouse stream: could not write " + path + ": " + ex.Message); }
        }
        return phases.All(p => p.Failures == 0) && restored ? 0 : 1;
    }

    static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
