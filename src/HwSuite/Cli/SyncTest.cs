using System.Diagnostics;
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Lighting;

namespace HwSuite.Cli;

/// <summary>
/// Owner-watched all-device sync check with the owner's saved colours, speed, brightness and device choices (config is
/// read only). Timeline: Breathing, then Gradient Wave, on the shared engine clock; per device it records accepted updates,
/// accepted-update spacing, errors, and the process CPU. Delivery numbers say what the hardware accepted, not what was
/// seen: only the owner (or a video) can confirm that the devices brighten and dim together.
/// Caller holds the hardware instance lock and has stopped the Gigabyte services. Fan control stays off.
/// </summary>
public static class SyncTest
{
    /// <param name="breathingSpeeds">Breathing is run once per speed (default: the saved speed), each segment preceded by
    /// 1.5 s of black so a video can be cut into segments; Gradient Wave then runs at the saved speed.</param>
    public static int Run(int secondsPerEffect, int countdown, string? jsonOut, double[]? breathingSpeeds = null)
    {
        secondsPerEffect = Math.Clamp(secondsPerEffect, 5, 120);
        var owner = ConfigStore.Load();
        var cfg = ConfigStore.Parse(JsonSerializer.Serialize(owner)) ?? new AppConfig();
        cfg.FanControlEnabled = false; cfg.AutoStopGigabyte = false;
        cfg.Rgb.Enabled = false;
        Log.Info($"sync test: colours #{cfg.Rgb.ColorA:X6}/#{cfg.Rgb.ColorB:X6}, speed {cfg.Rgb.Speed:0.00}, brightness {cfg.Rgb.Brightness:0.00}; keyboard verified {cfg.KeyboardVerified}, live {cfg.KeyboardLiveVerified}");
        using var app = new AppServices(cfg);
        app.InitializeHardware();
        app.InitializeLighting(allowRamRemap: false);
        app.Hub.Start(); app.Rgb.Start();
        double cps = ThreadCpu.CyclesPerSecond();
        double savedSpeed = cfg.Rgb.Speed;
        var plan = (breathingSpeeds is { Length: > 0 } ? breathingSpeeds : new[] { savedSpeed }).Select(sp => (effect: EffectKind.Breathing, speed: Math.Clamp(sp, 0, 1)))
            .Append((EffectKind.GradientWave, savedSpeed)).ToArray();
        Log.Info($"sync test: keyboard lead {KeyboardSink.LiveLeadSecondsEstimate * 1000:0} ms");
        double at = countdown;
        foreach (var (e, sp) in plan) { Log.Info($"  t+{at,5:0}s  1.5 s black, then {e} at speed {sp:0.00} for {secondsPerEffect}s"); at += secondsPerEffect + 3.5; }
        Log.Info($"  t+{at,5:0}s  output off: every device is left on the primary colour");
        for (int i = countdown; i > 0; i--) { Log.Info($"starting in {i}"); Thread.Sleep(1000); }

        var phases = new List<object>();
        long totalErrors = 0;
        try
        {
            foreach (var (effect, speed) in plan)
            {
                int colorA = cfg.Rgb.ColorA;
                cfg.Rgb.ColorA = 0; cfg.Rgb.Effect = EffectKind.Static; cfg.Rgb.Enabled = true;   // segment marker: black (live frames, nothing stored)
                Log.Info($"MARKER black at {DateTime.Now:HH:mm:ss.fff}");
                Thread.Sleep(1500);
                cfg.Rgb.ColorA = colorA; cfg.Rgb.Speed = speed;
                cfg.Rgb.Effect = effect;
                Log.Info($"EFFECT {effect} speed {speed:0.00} now ({DateTime.Now:HH:mm:ss.fff}; breathing period {1 / (0.12 + speed * 0.6):0.000} s)");
                Thread.Sleep(2000);   // settle: device init, mouse/keyboard settings
                foreach (var s in app.Rgb.Sinks) s.ResetPacingSamples();
                var before = app.Rgb.Sinks.ToDictionary(x => x, x => (x.FramesSent, x.Errors, x.Attempts));
                var kb = app.Rgb.Sinks.OfType<KeyboardSink>().FirstOrDefault();
                long kbLive0 = kb?.Controller.LiveFrames ?? 0;
                long ticks0 = app.Rgb.Ticks;
                ulong cyc0 = ThreadCpu.ProcessCycles();
                var sw = Stopwatch.StartNew();
                Thread.Sleep(secondsPerEffect * 1000);
                double el = sw.Elapsed.TotalSeconds;
                double cpu = (ThreadCpu.ProcessCycles() - cyc0) / cps / el * 100 / Environment.ProcessorCount;
                var devices = new List<object>();
                foreach (var s in app.Rgb.Sinks.Where(x => x.Available))
                {
                    var iv = s.SuccessfulIntervalsMs.OrderBy(v => v).ToArray();
                    string P(double q) => iv.Length == 0 ? "n/a" : iv[Math.Min(iv.Length - 1, (int)(iv.Length * q))].ToString("0.0");
                    long sent = s.FramesSent - before[s].FramesSent, err = s.Errors - before[s].Errors;
                    totalErrors += err;
                    string extra = s is KeyboardSink k ? $" live={k.Live} liveFrames {k.Controller.LiveFrames - kbLive0}" : s is MouseSink m ? $" mouse setting applies {m.Controller.Applies} (onboard effect, own timing)" : "";
                    Log.Info($"   {s.Name,-52} accepted {sent / el,6:0.0}/s  spacing median {P(0.5)} ms p95 {P(0.95)} ms max {P(1)} ms  errors {err}{extra}");
                    devices.Add(new { device = s.Name, perSecond = sent / el, medianMs = iv.Length > 0 ? iv[iv.Length / 2] : (double?)null, p95Ms = iv.Length > 0 ? iv[Math.Min(iv.Length - 1, (int)(iv.Length * 0.95))] : (double?)null, maxMs = iv.Length > 0 ? iv[^1] : (double?)null, errors = err, extra });
                }
                double fps = (app.Rgb.Ticks - ticks0) / el;
                Log.Info($"EFFECT {effect} speed {speed:0.00} done: engine {fps:0.0} FPS, CPU {cpu:0.000}% of the machine (cycles)");
                phases.Add(new { effect = effect.ToString(), speed, seconds = el, engineFps = fps, cpuMachinePct = cpu, devices });
            }
        }
        finally
        {
            cfg.Rgb.Effect = EffectKind.Static;
            cfg.Rgb.Enabled = false;
            Thread.Sleep(1500);   // the engine deactivates every device onto the primary colour
        }
        if (jsonOut != null)
        {
            try { File.WriteAllText(jsonOut, JsonSerializer.Serialize(new { generated = DateTime.Now, secondsPerEffect, keyboardLeadMs = KeyboardSink.LiveLeadSecondsEstimate * 1000, phases, totalErrors }, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { Log.Warn("could not write results: " + ex.Message); }
        }
        return totalErrors == 0 ? 0 : 1;
    }
}
