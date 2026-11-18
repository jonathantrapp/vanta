using System.Diagnostics;
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Lighting;

namespace HwSuite.Cli;

/// <summary>
/// Bounded A/B run of the production lighting engine for the D_LED strip rate. Caller holds the hardware instance lock
/// and has stopped the Gigabyte services. Every phase uses the same effect, colours, speed and brightness; only the strip
/// rate changes. Order: lighting off (CPU baseline), then A,B,(C...) twice, so drift shows up as a difference between
/// rounds. All counts are what the controller's HID API accepted; nothing here observes the LEDs themselves.
/// </summary>
public static class DLedRateBenchmark
{
    public sealed class Phase
    {
        public string Name { get; set; } = "";
        public int TargetHz { get; set; }
        public int Round { get; set; }
        public double Seconds { get; set; }
        public double EngineFps { get; set; }
        public double EngineIntervalMedianMs { get; set; }
        public double EngineIntervalP95Ms { get; set; }
        public double StripAcceptedPerSec { get; set; }
        public double StripPostedPerSec { get; set; }
        public double StripUnchangedPerSec { get; set; }
        public double StripIntervalMedianMs { get; set; }
        public double StripIntervalP95Ms { get; set; }
        public double StripIntervalP99Ms { get; set; }
        public double StripIntervalMaxMs { get; set; }
        public double StripIntervalStdevMs { get; set; }
        public double StripWithin20PctOfPeriod { get; set; }
        public double StripSendMedianMs { get; set; }
        public double StripSendMaxMs { get; set; }
        public double BoardAcceptedPerSec { get; set; }
        public double BoardIntervalP95Ms { get; set; }
        public double BoardIntervalMaxMs { get; set; }
        public double RamAcceptedPerSec { get; set; }
        public double RamIntervalP95Ms { get; set; }
        public double RamIntervalMaxMs { get; set; }
        public double CpuPercentOneCore { get; set; }
        public double CpuPercentMachine { get; set; }
        public long HidErrors { get; set; }
        public long SmbusErrors { get; set; }
        public long SinkErrors { get; set; }
        public long MouseApplications { get; set; }
        public bool HighResolutionWaits { get; set; }
    }

    static double Pct(double[] sorted, double p) => sorted.Length == 0 ? double.NaN : sorted[(int)Math.Round((sorted.Length - 1) * p)];

    public static int Run(int seconds, int[] rates, string? jsonOut, bool stripOnly = false)
    {
        seconds = Math.Clamp(seconds, 5, 60);
        rates = rates.Where(r => RgbConfig.DLedRates.Contains(r)).Distinct().ToArray();
        if (!rates.Contains(60)) rates = new[] { 60 }.Concat(rates).ToArray();
        var cfg = new AppConfig();
        var rgb = cfg.Rgb;
        rgb.Enabled = false;
        rgb.DLedStrip = true; rgb.Motherboard = !stripOnly; rgb.Ram = !stripOnly; rgb.Mouse = !stripOnly;   // strip-only isolates the strip's own CPU cost
        rgb.DLedMaxHz = 60;
        rgb.Effect = EffectKind.GradientWave;
        // Fixed for every phase: the update rate must never change animation velocity, colours or intensity.
        rgb.Speed = 0.6; rgb.Brightness = 0.7;
        rgb.ColorA = 0xFF0000; rgb.ColorB = 0x0040FF;
        cfg.AutoStopGigabyte = false;   // the runner stops and restores the services; the arbiter must find them stopped

        using var app = new AppServices(cfg);
        app.InitializeHardware();
        app.InitializeLighting(allowRamRemap: false);
        var strip = app.Rgb.Sinks.OfType<DLedSink>().FirstOrDefault();
        var board = app.Rgb.Sinks.OfType<MotherboardSink>().FirstOrDefault();
        var ram = app.Rgb.Sinks.OfType<RamSink>().FirstOrDefault();
        var mouse = app.Rgb.Sinks.OfType<MouseSink>().FirstOrDefault();
        if (strip?.Available != true) { Log.Error("D_LED A/B: controller unavailable"); return 3; }
        app.Hub.Start(); app.Rgb.Start();
        Thread.Sleep(2500);   // two ownership refreshes
        var own = app.Ownership!.Check(Hardware.HwResource.RgbUsb);
        if (!own.MayWrite) { Log.Error("D_LED A/B: IT5701 not owned: " + own.Reason); return 4; }

        var phases = new List<Phase>();
        var order = new List<(string name, int hz, int round, bool lit)> { ("lighting off (CPU baseline)", 0, 0, false) };
        for (int round = 1; round <= 2; round++) foreach (var r in rates) order.Add(($"{r}/s", r, round, true));

        try
        {
            foreach (var (name, hz, round, lit) in order)
            {
                rgb.DLedRateOverride = lit ? hz : null;
                rgb.Enabled = lit;
                Thread.Sleep(2000);   // activation and rate change settle outside the measurement window
                var before = app.Rgb.Sinks.ToDictionary(s => s, s => (s.FramesSent, s.Errors, s.FramesPosted, s.FramesDropped));
                long hidErr = board?.Device?.WriteErrors ?? 0, busErr = app.Smbus?.ErrorCount ?? 0;
                long mouseApplies = mouse?.Controller.Applies ?? 0;
                foreach (var s in app.Rgb.Sinks) s.ResetPacingSamples();
                app.Rgb.ResetPacingSamples();
                using var proc = Process.GetCurrentProcess();
                var cpu0 = proc.TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                Thread.Sleep(seconds * 1000);
                double el = clock.Elapsed.TotalSeconds;
                proc.Refresh();
                double cpuCore = (proc.TotalProcessorTime - cpu0).TotalSeconds / el * 100;

                double Rate(Lighting.RgbSink? s) => s == null ? double.NaN : (s.FramesSent - before[s].FramesSent) / el;
                double[] Sorted(double[] v) { var c = (double[])v.Clone(); Array.Sort(c); return c; }
                var si = Sorted(strip.SuccessfulIntervalsMs);
                var sd = Sorted(strip.SendDurationsMs);
                var ei = Sorted(app.Rgb.RenderIntervalsMs);
                var bi = Sorted(board?.SuccessfulIntervalsMs ?? Array.Empty<double>());
                var ri = Sorted(ram?.SuccessfulIntervalsMs ?? Array.Empty<double>());
                double period = hz > 0 ? 1000.0 / hz : double.NaN;
                double mean = si.Length > 0 ? si.Average() : double.NaN;
                var p = new Phase
                {
                    Name = name, TargetHz = hz, Round = round, Seconds = el,
                    EngineFps = lit ? ei.Length / el : 0, EngineIntervalMedianMs = Pct(ei, .5), EngineIntervalP95Ms = Pct(ei, .95),
                    StripAcceptedPerSec = Rate(strip),
                    StripPostedPerSec = (strip.FramesPosted - before[strip].FramesPosted) / el,
                    StripUnchangedPerSec = (strip.FramesDropped - before[strip].FramesDropped) / el,
                    StripIntervalMedianMs = Pct(si, .5), StripIntervalP95Ms = Pct(si, .95), StripIntervalP99Ms = Pct(si, .99), StripIntervalMaxMs = Pct(si, 1),
                    StripIntervalStdevMs = si.Length > 1 ? Math.Sqrt(si.Sum(x => (x - mean) * (x - mean)) / (si.Length - 1)) : double.NaN,
                    StripWithin20PctOfPeriod = si.Length > 0 && hz > 0 ? si.Count(x => Math.Abs(x - period) <= period * 0.2) / (double)si.Length : double.NaN,
                    StripSendMedianMs = Pct(sd, .5), StripSendMaxMs = Pct(sd, 1),
                    BoardAcceptedPerSec = Rate(board), BoardIntervalP95Ms = Pct(bi, .95), BoardIntervalMaxMs = Pct(bi, 1),
                    RamAcceptedPerSec = Rate(ram), RamIntervalP95Ms = Pct(ri, .95), RamIntervalMaxMs = Pct(ri, 1),
                    CpuPercentOneCore = cpuCore, CpuPercentMachine = cpuCore / Environment.ProcessorCount,
                    HidErrors = (board?.Device?.WriteErrors ?? 0) - hidErr, SmbusErrors = (app.Smbus?.ErrorCount ?? 0) - busErr,
                    SinkErrors = app.Rgb.Sinks.Sum(s => s.Errors - before[s].Errors),
                    MouseApplications = (mouse?.Controller.Applies ?? 0) - mouseApplies,
                    HighResolutionWaits = PrecisionTimer.HighResolutionOnThisThread
                };
                phases.Add(p);
                Log.Info(lit
                    ? $"[{name} round {round}] engine {p.EngineFps:0.0} FPS (render interval median {p.EngineIntervalMedianMs:0.00} p95 {p.EngineIntervalP95Ms:0.00} ms) | " +
                      $"strip accepted {p.StripAcceptedPerSec:0.0}/s of {hz} (posted {p.StripPostedPerSec:0.0}/s, unchanged {p.StripUnchangedPerSec:0.0}/s); interval median {p.StripIntervalMedianMs:0.00} p95 {p.StripIntervalP95Ms:0.00} p99 {p.StripIntervalP99Ms:0.00} max {p.StripIntervalMaxMs:0.00} sd {p.StripIntervalStdevMs:0.00} ms; " +
                      $"{p.StripWithin20PctOfPeriod:P0} within 20% of the period; HID call median {p.StripSendMedianMs:0.00} max {p.StripSendMaxMs:0.00} ms | " +
                      $"board {p.BoardAcceptedPerSec:0.0}/s (p95 {p.BoardIntervalP95Ms:0.0} max {p.BoardIntervalMaxMs:0.0} ms) | RAM {p.RamAcceptedPerSec:0.0}/s (p95 {p.RamIntervalP95Ms:0.0} max {p.RamIntervalMaxMs:0.0} ms) | " +
                      $"CPU {p.CpuPercentOneCore:0.00}% of one core ({p.CpuPercentMachine:0.00}% of machine) | errors HID {p.HidErrors} SMBus {p.SmbusErrors} sinks {p.SinkErrors}; mouse writes {p.MouseApplications}"
                    : $"[{name}] CPU {p.CpuPercentOneCore:0.00}% of one core ({p.CpuPercentMachine:0.00}% of machine)");
            }
        }
        finally
        {
            rgb.DLedRateOverride = null;
            rgb.Effect = EffectKind.Static;
            rgb.Enabled = false;   // leaves every device on the steady primary colour (red); nothing is saved
            Thread.Sleep(500);
        }

        // Summary per rate, and the provisional delivery/contention verdicts (thresholds are this test's, not a protocol limit).
        var baseline = phases.Where(p => p.TargetHz == 60).ToList();
        double offCpu = phases.First(p => p.TargetHz == 0).CpuPercentOneCore;
        bool anyFail = false;
        var summary = new List<object>();
        foreach (var r in rates)
        {
            var ps = phases.Where(p => p.TargetHz == r).ToList();
            double acc = ps.Average(p => p.StripAcceptedPerSec), p95 = ps.Max(p => p.StripIntervalP95Ms), cpu = ps.Average(p => p.CpuPercentOneCore);
            double boardR = ps.Average(p => p.BoardAcceptedPerSec), ramR = ps.Average(p => p.RamAcceptedPerSec);
            double boardBase = baseline.Average(p => p.BoardAcceptedPerSec), ramBase = baseline.Average(p => p.RamAcceptedPerSec);
            double boardP95 = ps.Max(p => p.BoardIntervalP95Ms), boardP95Base = baseline.Max(p => p.BoardIntervalP95Ms);
            double ramP95 = ps.Max(p => p.RamIntervalP95Ms), ramP95Base = baseline.Max(p => p.RamIntervalP95Ms);
            bool delivered = acc >= 0.9 * r && p95 <= 1.25 * 1000.0 / r;
            bool noContention = stripOnly || boardR >= 0.9 * boardBase && ramR >= 0.9 * ramBase && boardP95 <= boardP95Base * 1.2 + 2 && ramP95 <= ramP95Base * 1.2 + 2;
            bool clean = ps.All(p => p.HidErrors == 0 && p.SmbusErrors == 0 && p.SinkErrors == 0 && p.MouseApplications == 0);
            anyFail |= !(delivered && noContention && clean) && r == 60;
            Log.Info($"SUMMARY {r}/s: strip accepted {acc:0.0}/s (worst p95 interval {p95:0.00} ms) -> {(delivered ? "delivered" : "NOT delivered")}; " +
                     $"board {boardR:0.0}/s vs {boardBase:0.0}, RAM {ramR:0.0}/s vs {ramBase:0.0} -> {(noContention ? "no measurable contention" : "CONTENTION")}; " +
                     $"{(clean ? "no errors, no mouse writes" : "ERRORS or mouse writes")}; CPU {cpu:0.00}% of one core (lighting off {offCpu:0.00}%)");
            summary.Add(new { rateHz = r, stripAcceptedPerSec = acc, worstP95IntervalMs = p95, delivered, noContention, clean, cpuPercentOneCore = cpu, boardPerSec = boardR, ramPerSec = ramR });
        }
        Log.Info("All counts are updates the IT5701 accepted over USB. They do not show when or whether the LEDs changed; smoothness needs a visual comparison.");
        if (jsonOut != null)
        {
            try
            {
                File.WriteAllText(jsonOut, JsonSerializer.Serialize(new { generated = DateTime.Now, secondsPerPhase = seconds, stripOnly, effect = "GradientWave", speed = 0.6, brightness = 0.7, leds = strip.LedCount, phases, summary },
                    new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                Log.Info("results written to " + jsonOut);
            }
            catch (Exception ex) { Log.Warn("could not write results: " + ex.Message); }
        }
        return anyFail ? 1 : 0;
    }
}
