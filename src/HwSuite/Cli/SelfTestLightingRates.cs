using System.Diagnostics;
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>D_LED strip rate checks. Software only: fake devices, real engine and scheduler, no hardware.</summary>
public static partial class SelfTest
{
    static void LightingRateTests()
    {
        // --- setting, migration and the runtime-only comparison override
        var defaults = new RgbConfig();
        var legacy = JsonSerializer.Deserialize<RgbConfig>("{\"DLedMaxHz\":60}")!;
        Check("strip rate defaults to 60 for new and older configs", defaults.EffectiveDLedHz == 60 && legacy.EffectiveDLedHz == 60);
        var flagged = ConfigStore.Parse("{\"Version\":3,\"Rgb\":{\"DLedMaxHz\":60,\"DLedExperimental120\":true}}")!;
        Check("an earlier build's 120 flag migrates to the rate setting", flagged.Rgb.EffectiveDLedHz == 120 && flagged.Rgb.DLedMaxHz == 120 && !flagged.Rgb.DLedExperimental120 && flagged.Version == 4);
        Check("unsupported rates fall back to at most 60",
            new RgbConfig { DLedMaxHz = 75 }.EffectiveDLedHz == 60 && new RgbConfig { DLedMaxHz = 500 }.EffectiveDLedHz == 60 && new RgbConfig { DLedMaxHz = 30 }.EffectiveDLedHz == 30);
        var cmp = new AppConfig();
        cmp.Rgb.DLedRateOverride = 144;
        var json = ConfigStore.ToJson(cmp);
        Check("the visual comparison override applies at run time but is never saved",
            cmp.Rgb.EffectiveDLedHz == 144 && !json.Contains("DLedRateOverride") && ConfigStore.Parse(json)!.Rgb.EffectiveDLedHz == 60);

        // --- other devices keep a 60/s offer cadence at every strip rate (board 20/s and RAM 15/s limits apply after that)
        foreach (int renderHz in RgbConfig.DLedRates)
        {
            var cadence = new FrameCadence();
            int offers = 0;
            for (int i = 0; i < renderHz * 10; i++) if (cadence.IsDue(i / (double)renderHz, 60)) offers++;
            Check($"other devices keep a 60/s offer cadence when the engine renders {renderHz}/s", offers == 600, $"({offers} in 10 s)");
        }
        var stalled = new FrameCadence();
        stalled.IsDue(0, 60);
        Check("cadence skips missed frames after a stall", stalled.IsDue(5, 60) && !stalled.IsDue(5.001, 60));

        // --- only smoothness changes: effects are functions of time, so 60 and 120 show identical colours at shared instants
        var ctx60 = new List<Rgb>(); var ctx120 = new List<Rgb>();
        for (int i = 0; i < 60; i++)
        {
            var c = new EffectContext { Time = i / 60.0, Speed = 0.6, Brightness = 0.7, ColorA = new Rgb(255, 0, 0), ColorB = new Rgb(0, 64, 255) };
            ctx60.Add(Effects.Evaluate(EffectKind.GradientWave, c, 0.1));
        }
        for (int i = 0; i < 120; i += 2)
        {
            var c = new EffectContext { Time = i / 120.0, Speed = 0.6, Brightness = 0.7, ColorA = new Rgb(255, 0, 0), ColorB = new Rgb(0, 64, 255) };
            ctx120.Add(Effects.Evaluate(EffectKind.GradientWave, c, 0.1));
        }
        Check("animation speed, colours and brightness do not depend on the update rate", ctx60.SequenceEqual(ctx120));
        var mb = MouseLightingState.From(new RgbConfig { Enabled = true, Effect = EffectKind.GradientWave });
        var ma = MouseLightingState.From(new RgbConfig { Enabled = true, Effect = EffectKind.GradientWave, DLedMaxHz = 144, DLedRateOverride = 120 });
        Check("the strip rate does not change the mouse's setting (mouse stays settings-driven)", Equals(mb, ma));

        // --- precise waits: no 15.6 ms rounding
        var w120 = PrecisionTimer.Measure(TimeSpan.FromMilliseconds(1000.0 / 120), 60);
        var w60 = PrecisionTimer.Measure(TimeSpan.FromMilliseconds(1000.0 / 60), 40);
        Check("precise wait holds 8.3 ms periods (no 15.6 ms timer rounding)", w120.median is > 7.5 and < 10.5 && w120.p95 < 13,
            $"(median {w120.median:0.00} p95 {w120.p95:0.00} max {w120.max:0.00} ms, high-resolution {PrecisionTimer.HighResolutionOnThisThread})");
        Check("precise wait holds 16.7 ms periods", w60.median is > 15.5 and < 18.5 && w60.p95 < 22, $"(median {w60.median:0.00} p95 {w60.p95:0.00} ms)");

        // --- the real engine and scheduler with fake devices: the strip device follows its rate, a standard one stays at 60
        foreach (int hz in new[] { 60, 120 })
        {
            var cfg = new AppConfig();
            cfg.Rgb.Enabled = true; cfg.Rgb.Effect = EffectKind.GradientWave; cfg.Rgb.Speed = 0.6; cfg.Rgb.Brightness = 0.7;
            cfg.Rgb.DLedMaxHz = hz;
            using var engine = new RgbEngine(cfg, () => 40);
            var strip = new TestSink { StripRate = true, Hz = hz };
            var standard = new TestSink { Hz = 60 };
            engine.Sinks.Add(strip); engine.Sinks.Add(standard);
            // the fake strip reads the rate through the same setting the real one uses
            engine.Start();
            Thread.Sleep(700);
            long s0 = strip.FramesSent, t0 = standard.FramesSent;
            strip.ResetPacingSamples();
            var sw = Stopwatch.StartNew();
            Thread.Sleep(2000);
            double el = sw.Elapsed.TotalSeconds;
            double stripRate = (strip.FramesSent - s0) / el, stdRate = (standard.FramesSent - t0) / el;
            var iv = strip.SuccessfulIntervalsMs; Array.Sort(iv);
            double p95 = iv.Length > 0 ? iv[(int)((iv.Length - 1) * 0.95)] : double.NaN;
            Check($"engine at a {hz}/s strip setting: strip device accepts about {hz}/s with steady pacing",
                stripRate >= hz * 0.92 && stripRate <= hz * 1.03 && p95 < 1000.0 / hz * 1.3 && engine.RenderHz == hz,
                $"({stripRate:0.0}/s, p95 interval {p95:0.00} ms, render {engine.RenderHz}/s)");
            Check($"engine at a {hz}/s strip setting: other device stays at 60/s", stdRate >= 55 && stdRate <= 61.5, $"({stdRate:0.0}/s)");
        }
    }
}
