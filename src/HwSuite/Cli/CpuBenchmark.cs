using System.Diagnostics;
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Lighting;

namespace HwSuite.Cli;

/// <summary>
/// Lighting CPU profile. Caller holds the hardware instance lock and has stopped the Gigabyte services. Per phase:
/// whole-process CPU, CPU per HwSuite thread (user and kernel time), SMBus transaction rate with the time spent inside the
/// PawnIO transfer call versus waiting for the SMBus mutexes, and accepted updates per device. Fan control stays off.
/// </summary>
public static class CpuBenchmark
{
    public sealed class Phase
    {
        public string Name { get; set; } = "";
        public double Seconds { get; set; }
        public double CpuMachinePct { get; set; }
        public double CpuOneCorePct { get; set; }
        /// <summary>Same quantity from exact cycle counts (primary figure; the tick-based one above is 15.6 ms-quantized).</summary>
        public double CpuMachinePctCycles { get; set; }
        public Dictionary<string, double[]> ThreadMsPerSec { get; set; } = new();   // name -> [user, kernel]
        public double SmbusTxPerSec { get; set; }
        public double SmbusIoctlMsPerSec { get; set; }
        public double SmbusMutexWaitMsPerSec { get; set; }
        public double RamPerSec { get; set; }
        public double BoardPerSec { get; set; }
        public double StripPerSec { get; set; }
        public double EngineFps { get; set; }
        public long Errors { get; set; }
    }

    public static int Run(int seconds, string? jsonOut, string label)
    {
        seconds = Math.Clamp(seconds, 5, 60);
        var cfg = new AppConfig { AutoStopGigabyte = false };
        var rgb = cfg.Rgb;
        rgb.Enabled = false; rgb.Speed = 0.6; rgb.Brightness = 0.7; rgb.ColorA = 0xFF0000; rgb.ColorB = 0x0040FF; rgb.DLedMaxHz = 60;
        double cps = ThreadCpu.CyclesPerSecond();
        Log.Info($"[{label}] cycle counter {cps / 1e9:0.000} GHz (calibrated), {Environment.ProcessorCount} logical CPUs");
        using var app = new AppServices(cfg);
        app.InitializeHardware();
        app.InitializeLighting(allowRamRemap: false);
        app.Hub.Start(); app.Rgb.Start();
        Thread.Sleep(2500);
        var strip = app.Rgb.Sinks.OfType<DLedSink>().FirstOrDefault();
        var board = app.Rgb.Sinks.OfType<MotherboardSink>().FirstOrDefault();
        var ram = app.Rgb.Sinks.OfType<RamSink>().FirstOrDefault();

        const int D = RgbConfig.DefaultRamHz;
        var plan = new (string name, bool lit, EffectKind effect, bool strip, bool board, bool ram, int ramHz, bool onboard)[]
        {
            ("lighting off", false, EffectKind.Static, true, true, true, D, false),
            ("Static, all devices", true, EffectKind.Static, true, true, true, D, false),
            ("Stealth, all devices", true, EffectKind.Stealth, true, true, true, D, false),
            ("Gradient Wave, all devices", true, EffectKind.GradientWave, true, true, true, D, false),
            ("Breathing, all devices", true, EffectKind.Breathing, true, true, true, D, false),
            ("Thermal Reactive, all devices", true, EffectKind.ThermalReactive, true, true, true, D, false),
            ("Gradient Wave, RAM only", true, EffectKind.GradientWave, false, false, true, D, false),
            ("Gradient Wave, board only", true, EffectKind.GradientWave, false, true, false, D, false),
            ("Gradient Wave, strip only", true, EffectKind.GradientWave, true, false, false, D, false),
            ("Gradient Wave, all devices, RAM 8/s", true, EffectKind.GradientWave, true, true, true, 8, false),
            ("Gradient Wave, all devices, RAM 12/s", true, EffectKind.GradientWave, true, true, true, 12, false),
            ("Gradient Wave, all devices, RAM 15/s", true, EffectKind.GradientWave, true, true, true, 15, false),
            ("Breathing, all devices, RAM onboard", true, EffectKind.Breathing, true, true, true, D, true),
            ("Gradient Wave, all devices, RAM onboard", true, EffectKind.GradientWave, true, true, true, D, true),
        };
        var phases = new List<Phase>();
        try
        {
            foreach (var (name, lit, effect, s, b, r, ramHz, onboard) in plan)
            {
                rgb.RamMaxHz = ramHz; rgb.RamOnboardEffects = onboard; rgb.Effect = effect; rgb.DLedStrip = s; rgb.Motherboard = b; rgb.Ram = r; rgb.Mouse = false;
                // switching devices off needs a restart of output so excluded ones stop receiving frames cleanly
                rgb.Enabled = false; Thread.Sleep(300);
                rgb.Enabled = lit;
                Thread.Sleep(2500);
                var before = app.Rgb.Sinks.ToDictionary(x => x, x => (x.FramesSent, x.Errors));
                long tx0 = app.Smbus?.TransactionCount ?? 0, io0 = app.Smbus?.IoctlTicks ?? 0, mw0 = app.Smbus?.MutexWaitTicks ?? 0;
                long ticks0 = app.Rgb.Ticks;
                var t0 = ThreadCpu.Snapshot().ToDictionary(x => x.Name);
                using var proc = Process.GetCurrentProcess();
                var cpu0 = proc.TotalProcessorTime;
                ulong cyc0 = ThreadCpu.ProcessCycles();
                var sw = Stopwatch.StartNew();
                Thread.Sleep(seconds * 1000);
                double el = sw.Elapsed.TotalSeconds;
                proc.Refresh();
                double core = (proc.TotalProcessorTime - cpu0).TotalSeconds / el * 100;
                double cycPct = (ThreadCpu.ProcessCycles() - cyc0) / cps / el * 100 / Environment.ProcessorCount;
                var t1 = ThreadCpu.Snapshot();
                double tick = Stopwatch.Frequency;
                var p = new Phase
                {
                    Name = name, Seconds = el, CpuOneCorePct = core, CpuMachinePctCycles = cycPct, CpuMachinePct = core / Environment.ProcessorCount,
                    SmbusTxPerSec = ((app.Smbus?.TransactionCount ?? 0) - tx0) / el,
                    SmbusIoctlMsPerSec = ((app.Smbus?.IoctlTicks ?? 0) - io0) / tick * 1000 / el,
                    SmbusMutexWaitMsPerSec = ((app.Smbus?.MutexWaitTicks ?? 0) - mw0) / tick * 1000 / el,
                    RamPerSec = ram == null ? 0 : (ram.FramesSent - before[ram].FramesSent) / el,
                    BoardPerSec = board == null ? 0 : (board.FramesSent - before[board].FramesSent) / el,
                    StripPerSec = strip == null ? 0 : (strip.FramesSent - before[strip].FramesSent) / el,
                    EngineFps = (app.Rgb.Ticks - ticks0) / el,
                    Errors = app.Rgb.Sinks.Sum(x => x.Errors - before[x].Errors)
                };
                foreach (var th in t1)
                {
                    var prev = t0.GetValueOrDefault(th.Name);
                    double u = (th.User - (prev?.User ?? TimeSpan.Zero)).TotalMilliseconds / el;
                    double k = (th.Kernel - (prev?.Kernel ?? TimeSpan.Zero)).TotalMilliseconds / el;
                    if (u + k >= 0.05) p.ThreadMsPerSec[th.Name] = new[] { Math.Round(u, 2), Math.Round(k, 2) };
                }
                phases.Add(p);
                Log.Info($"[{label}] {name}: CPU {p.CpuMachinePctCycles:0.000}% of machine by cycles, {p.CpuMachinePct:0.000}% by ticks ({p.CpuOneCorePct:0.00}% of one core) | engine {p.EngineFps:0.0} FPS | " +
                         $"strip {p.StripPerSec:0.0}/s board {p.BoardPerSec:0.0}/s RAM {p.RamPerSec:0.0}/s | SMBus {p.SmbusTxPerSec:0}/s, in PawnIO {p.SmbusIoctlMsPerSec:0.0} ms/s, mutex wait {p.SmbusMutexWaitMsPerSec:0.00} ms/s | errors {p.Errors}");
                foreach (var kv in p.ThreadMsPerSec.OrderByDescending(x => x.Value[0] + x.Value[1]))
                    Log.Info($"      {kv.Key,-58} user {kv.Value[0],7:0.00} ms/s  kernel {kv.Value[1],7:0.00} ms/s");
            }
        }
        finally
        {
            rgb.Effect = EffectKind.Static; rgb.DLedStrip = rgb.Motherboard = rgb.Ram = true; rgb.RamMaxHz = D; rgb.RamOnboardEffects = false;
            rgb.Enabled = false;
            Thread.Sleep(500);
        }
        if (jsonOut != null)
        {
            try { File.WriteAllText(jsonOut, JsonSerializer.Serialize(new { label, generated = DateTime.Now, secondsPerPhase = seconds, logicalCpus = Environment.ProcessorCount, phases }, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { Log.Warn("could not write results: " + ex.Message); }
        }
        return phases.Any(p => p.Errors > 0) ? 1 : 0;
    }
}
