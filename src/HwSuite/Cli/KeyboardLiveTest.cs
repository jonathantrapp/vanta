using System.Diagnostics;
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>
/// Owner-watched check of the iLovbee i87 live frame path (the vendor's music/real-time data, see
/// <see cref="ILovbeeKeyboardDevice.BuildLivePayload"/>). Fixed timeline so the owner can follow it by the clock; nothing is
/// stored on the keyboard unless --restore-stored is given (then exactly one stored command, the setting the keyboard had).
/// Refuses to run while DeviceDriver.exe runs. Successful HID writes do not prove anything was displayed: only the owner's
/// observation does.
/// </summary>
public static class KeyboardLiveTest
{
    sealed record Step(string Id, double Seconds, string Expect, Func<double, Rgb[]>? Frame);

    public static int Run(int countdownSeconds, bool restoreStored, string? jsonOut)
    {
        var procs = Process.GetProcessesByName("DeviceDriver");
        bool vendor = procs.Length > 0; foreach (var p in procs) p.Dispose();
        if (vendor) { Log.Error("keyboard live test: DeviceDriver.exe (iLovbee software) is running; close it first"); return 3; }
        var info = ILovbeeKeyboardDevice.Pick(ILovbeeKeyboardDevice.Enumerate());
        if (info == null) { Log.Error("keyboard live test: no FF00:0001 collection with 65-byte feature reports"); return 3; }
        using var dev = new ILovbeeKeyboardDevice();
        if (!dev.Open(info, out var err)) { Log.Error("keyboard live test: " + err); return 3; }

        int n = ILovbeeKeyMap.Keys.Length;
        var x = ILovbeeKeyMap.UnitX;
        Rgb[] Fill(Rgb c) => Enumerable.Repeat(c, n).ToArray();
        var cyan = new Rgb(0x38, 0xBD, 0xF8); var indigo = new Rgb(0x63, 0x66, 0xF1);
        // the same effect functions the engine uses (speed 0.6, brightness 1)
        EffectContext Ctx(double t) => new() { Time = t, Speed = 0.6, Brightness = 1, ColorA = cyan, ColorB = indigo };
        var steps = new List<Step>
        {
            new("A-red", 4, "all keys solid red", _ => Fill(new Rgb(255, 0, 0))),
            new("B-split", 4, "left half green, right half blue", _ => x.Select(v => v < 0.5 ? new Rgb(0, 255, 0) : new Rgb(0, 0, 255)).ToArray()),
            new("C-breathing", 8, "cyan breathing, about 2.1 s per breath, down to almost dark", t => Fill(Effects.Evaluate(EffectKind.Breathing, Ctx(t), 0))),
            new("D-wave", 8, "cyan/indigo gradient travelling left to right", t => x.Select(v => Effects.Evaluate(EffectKind.GradientWave, Ctx(t), KeyboardSink.WaveStart + KeyboardSink.WaveSpan * v)).ToArray()),
            new("E-pause", 10, "NO frames sent: does the last wave frame stay, or does the keyboard return to its own effect?", null),
            new("F-dim", 4, "all keys dim red (20%)", _ => Fill(new Rgb(51, 0, 0))),
        };

        Log.Info($"keyboard live test: {n} keys, {ILovbeeKeyMap.MatrixLeds}-LED frame, {ILovbeeKeyboardDevice.Frame(ILovbeeKeyboardDevice.BuildLivePayload(Fill(Rgb.Black))).Count} reports per frame, target {KeyboardSink.LiveHz}/s");
        double at = countdownSeconds;
        foreach (var s in steps) { Log.Info($"  t+{at,5:0.0}s  {s.Id,-12} {s.Seconds,4:0}s  expect: {s.Expect}"); at += s.Seconds; }
        Log.Info($"  t+{at,5:0.0}s  end{(restoreStored ? ": one stored command restores the keyboard's previous setting" : " (keyboard left as the last step shows it)")}");
        for (int i = countdownSeconds; i > 0; i--) { Log.Info($"starting in {i}"); Thread.Sleep(1000); }

        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        var results = new List<object>();
        double period = 1.0 / KeyboardSink.LiveHz;
        int totalErrors = 0;
        foreach (var s in steps)
        {
            double t0 = clock.Elapsed.TotalSeconds, end = t0 + s.Seconds;
            Log.Info($"STEP {s.Id} at t+{t0:0.00}s: {s.Expect}");
            var starts = new List<double>(); var durs = new List<double>(); int errors = 0;
            double next = t0;
            while (clock.Elapsed.TotalSeconds < end)
            {
                if (s.Frame == null) { Thread.Sleep(50); continue; }
                double now = clock.Elapsed.TotalSeconds;
                if (now < next) { PrecisionTimer.Wait(TimeSpan.FromSeconds(Math.Min(next - now, 0.05))); continue; }
                next += period;
                if (now - next > 0.2) next = now + period;   // fell behind: resync, never burst
                var payload = ILovbeeKeyboardDevice.BuildLivePayload(s.Frame(now - t0 + KeyboardSink.LiveLeadSecondsEstimate));
                var w0 = clock.Elapsed.TotalSeconds;
                bool ok = dev.ApplyLive(payload, out var e);
                durs.Add((clock.Elapsed.TotalSeconds - w0) * 1000); starts.Add(w0);
                if (!ok) { errors++; Log.Warn($"{s.Id}: write failed: {e}"); if (errors >= 3) break; }
            }
            totalErrors += errors;
            var gaps = starts.Zip(starts.Skip(1), (a, b) => (b - a) * 1000).OrderBy(v => v).ToList();
            string Pct(List<double> v, double q) => v.Count == 0 ? "n/a" : v[Math.Min(v.Count - 1, (int)(v.Count * q))].ToString("0.0");
            var ds = durs.OrderBy(v => v).ToList();
            Log.Info($"STEP {s.Id} done: {starts.Count} frames in {s.Seconds:0}s = {starts.Count / s.Seconds:0.0}/s; frame write median {Pct(ds, 0.5)} ms p95 {Pct(ds, 0.95)} ms max {Pct(ds, 1)} ms; start-to-start median {Pct(gaps, 0.5)} ms p95 {Pct(gaps, 0.95)} ms max {Pct(gaps, 1)} ms; errors {errors}");
            results.Add(new { step = s.Id, expect = s.Expect, seconds = s.Seconds, frames = starts.Count, perSecond = starts.Count / s.Seconds, writeMs = ds, gapMs = gaps, errors });
        }
        var cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / clock.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
        Log.Info($"live frames sent {dev.LiveWrites}, errors {totalErrors}, process CPU {cpu:0.000}% of the machine over {clock.Elapsed.TotalSeconds:0.0}s");

        string restore = "not requested";
        if (restoreStored)
        {
            var prev = LoadPrevious();
            var prof = ILovbeeProfile.Read(null, null, out var pd);
            if (prev == null) restore = "skipped: no record of the keyboard's previous stored setting";
            else if (prof == null) restore = "skipped: " + pd;
            else restore = dev.Apply(prev, prof.Sleep, out var re) ? $"sent: {prev.Description} ({pd})" : "failed: " + re;
            Log.Info("restore stored setting: " + restore);
        }
        try
        {
            if (jsonOut != null)
                File.WriteAllText(jsonOut, JsonSerializer.Serialize(new { at = DateTime.Now, keys = n, reportsPerFrame = 7, targetHz = KeyboardSink.LiveHz, cpuPercentMachine = cpu, totalErrors, restore, steps = results }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Error("could not write " + jsonOut, ex); }
        return totalErrors == 0 ? 0 : 1;
    }

    static KeyboardLightingState? LoadPrevious()
    {
        try
        {
            var path = Path.Combine(AppPaths.Data, "keyboard-lighting.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<KeyboardLightingState>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }
}
