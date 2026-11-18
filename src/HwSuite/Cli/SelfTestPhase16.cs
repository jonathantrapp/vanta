using System.Diagnostics;
using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>
/// 1.16 checks: mouse breathing locked to VANTA's (MouseSync). The timing model comes from the 2026-10-23 measurements
/// (docs\mouse-sync\RESULTS.md). Pure functions and the fake mouse transport only; nothing here opens a device.
/// </summary>
public static partial class SelfTest
{
    static void Phase16Tests()
    {
        Guarded("1.16 mouse sync timing", MouseSyncTimingTests);
        Guarded("1.16 mouse sync controller", MouseSyncControllerTests);
        Guarded("1.16.2 mouse stream test", () =>
        {
            var p = MouseStream.Parse(new[] { "--mouse-stream", "10", "10,20,30", "--via", "receiver" }, out _);
            Check("1.16.2: test 8 plan parses (10 s at 10, 20 and 30 per second over the receiver)", p is { SecondsPerRate: 10, Via: "receiver", Countdown: 10 } && p.Rates.SequenceEqual(new[] { 10, 20, 30 }));
            Check("1.16.2: the stream test refuses rates above 60/s, phases over 30 s, more than 6 rates and an unknown link",
                MouseStream.Parse(new[] { "--mouse-stream", "10", "61" }, out _) == null && MouseStream.Parse(new[] { "--mouse-stream", "31", "10" }, out _) == null &&
                MouseStream.Parse(new[] { "--mouse-stream", "10", "1,2,3,4,5,6,7" }, out _) == null && MouseStream.Parse(new[] { "--mouse-stream", "10", "10", "--via", "bt" }, out _) == null);
            var live = GloriousWirelessDevice.BuildEffectReport(MouseLightingState.EffectStatic, MouseLightingState.NeutralSpeed, new[] { new Rgb(1, 2, 3) });
            Check("1.16.2: the live colour report is SignalRGB's packet except the speed byte (02 08 02 00 01 FF 04 00 sp RGB)",
                live[3] == 2 && live[4] == 8 && live[5] == 2 && live[6] == 0 && live[7] == 1 && live[8] == 0xFF && live[9] == 4 && live[10] == 0 && live[12] == 1 && live[13] == 2 && live[14] == 3);
        });
    }

    static void MouseSyncTimingTests()
    {
        Check("1.16: at 50 % speed VANTA breathes every 2.38 s; the closest mouse step is byte 5 (2.560 s)",
            MouseSync.SpeedByte(0.5) == 5 && Math.Abs(1 / MouseSync.LockedHz(0.5) - 2.56) < 1e-9);
        var bytes = Enumerable.Range(0, 101).Select(i => MouseSync.SpeedByte(i / 100.0)).ToArray();
        Check("1.16: the whole speed slider maps to mouse bytes 3..16, slower speed never gives a faster byte",
            bytes.Min() == 3 && bytes.Max() == 16 && bytes.Zip(bytes.Skip(1)).All(p => p.Second <= p.First), $"({bytes.Min()}..{bytes.Max()})");
        bool waitsOk = true;
        foreach (var (t, T, L) in new[] { (0.0, 2.56, -0.045), (100.3, 1.536, 0.2), (7.0, 10.752, 1.2), (2.56 * 4 + 0.045 - 0.505 * 2.56, 2.56, -0.045), (0.01, 2.56, -0.9) })
        {
            double w = MouseSync.WaitBeforeSend(t, T, L);
            double x = (t + w + L + MouseSync.DipFraction * T) % T;
            waitsOk &= w >= 0 && w < T && Math.Min(x, T - x) < 1e-6;
        }
        Check("1.16: the planned write makes the mouse's darkest point (latency + 0.505 of a period after the write) land on VANTA's, also for a negative latency", waitsOk);

        var cfg = new RgbConfig { Enabled = true, Mouse = true, Effect = EffectKind.Breathing, ColorA = 0xFF0000, Speed = 0.5, Brightness = 1, MouseSync = true };
        var ctx = EffectContext.From(cfg, 0, null);
        double T5 = MouseSync.PeriodSeconds(5);
        var dark = Effects.Evaluate(EffectKind.Breathing, ctx.At(3 * T5), 0.5);
        var bright = Effects.Evaluate(EffectKind.Breathing, ctx.At(3.5 * T5), 0.5);
        Check("1.16: locked: VANTA's breathing is darkest at whole periods of the mouse's cycle and brightest half-way",
            ctx.BreathingHz is double hz && Math.Abs(hz - 1 / T5) < 1e-12 && dark.R <= 11 && bright.R >= 250, $"({dark.R} / {bright.R})");
        double hz5 = 1 / T5;
        var shape = Enumerable.Range(0, 257).Select(i => MouseSync.Shape(i / 256.0 * T5, hz5)).ToArray();
        Check("1.16.1: locked breathing uses the mouse's filmed shape: black at the dark point, back on its ~45 % ramp within 0.25 s, straight ramp to full at half-way, mirrored",
            shape[0] == 0 && Math.Abs(MouseSync.Shape(0.3, 1 / MouseSync.PeriodSeconds(20)) - 0.43) < 0.03 && Math.Abs(shape[128] - 1) < 1e-9 &&
            shape.Take(129).Zip(shape.Skip(1).Take(128)).All(p => p.Second >= p.First - 1e-12) &&
            Enumerable.Range(0, 129).All(i => Math.Abs(shape[i] - shape[256 - i]) < 1e-9) &&
            Math.Abs(MouseSync.Shape(T5 / 4, hz5) - 0.76) < 0.01, $"(0.3 s at byte 20: {MouseSync.Shape(0.3, 1 / MouseSync.PeriodSeconds(20)):0.00}, quarter: {MouseSync.Shape(T5 / 4, hz5):0.00})");
        var own = EffectContext.From(new RgbConfig { Enabled = true, Effect = EffectKind.Breathing, ColorA = 0xFF0000, Speed = 0.5 }, 0, null);
        var ownMid = Effects.Evaluate(EffectKind.Breathing, own.At(1.0), 0.5);
        double rate = 0.12 + 0.5 * 0.6, phase = 0.5 - 0.5 * Math.Cos(2 * Math.PI * rate * 1.0);
        Check("1.16: sync off: breathing keeps VANTA's own rate exactly as before",
            own.BreathingHz == null && Math.Abs(ownMid.R - Math.Round(255 * (0.04 + 0.96 * phase * phase * (3 - 2 * phase)))) <= 1);
        Check("1.16: sync only applies to Breathing with the mouse included",
            !MouseSync.Active(new RgbConfig { MouseSync = true, Mouse = false, Effect = EffectKind.Breathing }) &&
            !MouseSync.Active(new RgbConfig { MouseSync = true, Effect = EffectKind.Static }) &&
            !MouseSync.Active(new RgbConfig { MouseSync = true, Effect = EffectKind.Rainfall }) &&
            EffectContext.From(new RgbConfig { MouseSync = true, Effect = EffectKind.GradientWave }, 0, null).BreathingHz == null);

        var synced = MouseLightingState.From(cfg)!;
        cfg.MouseSync = false;
        var plain = MouseLightingState.From(cfg)!;
        Check("1.16: locked mouse setting: single-colour breathing at byte 5, marked synced; unlocked keeps CORE's speed byte 11",
            synced.Synced && synced.Effect == MouseLightingState.EffectBreathingSingleColor && synced.Speed == 5 && Math.Abs(synced.SyncPeriodSeconds - 2.56) < 1e-9 &&
            !plain.Synced && plain.Speed == 11 && !Equals(synced, plain));
        Check("1.16: the synced flag survives the saved mouse setting (old saves read as not synced)",
            System.Text.Json.JsonSerializer.Deserialize<MouseLightingState>(System.Text.Json.JsonSerializer.Serialize(synced))?.Synced == true &&
            System.Text.Json.JsonSerializer.Deserialize<MouseLightingState>("{\"Effect\":5,\"Color\":{\"R\":1,\"G\":2,\"B\":3},\"Speed\":11,\"Brightness\":255,\"Description\":\"x\",\"Fallback\":false}")?.Synced == false);
        var back = ConfigStore.Parse(ConfigStore.ToJson(new AppConfig { Rgb = { MouseSync = true, MouseSyncLatencyMs = 450 } }))!;
        Check("1.16: the sync switch and mouse timing round-trip; configs without them read as off / the measured -45 ms",
            back.Rgb.MouseSync && back.Rgb.MouseSyncLatencyMs == 450 && new RgbConfig() is { MouseSync: false, MouseSyncLatencyMs: MouseSync.MeasuredLatencyMs } && MouseSync.MeasuredLatencyMs == -45);
    }

    static void MouseSyncControllerTests()
    {
        var cfg = new RgbConfig { Enabled = true, Mouse = true, Effect = EffectKind.Breathing, ColorA = 0x00FFCC, Speed = 1.0, Brightness = 1, MouseSync = true };
        var want = MouseLightingState.From(cfg)!;
        double T = want.SyncPeriodSeconds;   // byte 3: 1.536 s
        var clock = Stopwatch.StartNew();
        bool clockOn = false;
        var tr = new FakeTransport();
        using (var mc = new MouseLightingController(tr, listenForDeviceChanges: false)
        {
            Debounce = TimeSpan.Zero, LatencySeconds = 0.3, ResyncInterval = TimeSpan.FromSeconds(2),
            EngineTime = () => clockOn ? 100.3 + clock.Elapsed.TotalSeconds : null
        })
        {
            mc.SetDesired(want);
            Thread.Sleep(600);
            Check("1.16: a synced setting is not sent while the lighting clock is unknown", tr.Link.Applies == 0 && mc.Status.Contains("lighting clock"), $"({mc.Status})");
            clockOn = true;
            bool first = WaitUntil(() => mc.SyncedApplies >= 1, (int)(T * 1000) + 2500);
            double x = (mc.LastSyncEngineTime + 0.3 + MouseSync.DipFraction * T) % T;
            double off = Math.Min(x, T - x) * 1000;
            Check("1.16: the synced write goes out at the planned moment of the cycle (within 5 ms)", first && off < 5 && Math.Abs(mc.LastSyncErrorMs) < 5,
                $"({off:0.0} ms from the cycle point, {mc.LastSyncErrorMs:+0.0;-0.0} ms from plan)");
            bool again = WaitUntil(() => mc.SyncedApplies >= 2, 2000 + (int)(T * 1000) + 2500);
            x = (mc.LastSyncEngineTime + 0.3 + MouseSync.DipFraction * T) % T;
            off = Math.Min(x, T - x) * 1000;
            Check("1.16: the same setting is lined up again after the re-sync interval, again on the cycle point", again && off < 15 && Equals(tr.Link.Last, want), $"({off:0.0} ms)");
            Check("1.16: re-syncs keep the setting accepted (not reported pending or failed)", mc.Ack == LightAck.Accepted);
        }

        // a saved synced setting from an earlier run is still lined up once (VANTA's clock restarts at launch); an unsynced one is not re-sent
        var tr2 = new FakeTransport();
        using (var mc = new MouseLightingController(tr2, listenForDeviceChanges: false, lastApplied: want)
        { Debounce = TimeSpan.Zero, LatencySeconds = 0.3, EngineTime = () => clock.Elapsed.TotalSeconds })
        {
            mc.SetDesired(want);
            Check("1.16: after a restart the saved synced setting is lined up once", WaitUntil(() => mc.SyncedApplies == 1, (int)(T * 1000) + 2500));
            Thread.Sleep(1200);
            Check("1.16: ... and not again before the re-sync interval", mc.SyncedApplies == 1 && tr2.Link.Applies == 1);
        }
        cfg.MouseSync = false;
        var unsynced = MouseLightingState.From(cfg)!;
        var tr3 = new FakeTransport();
        using (var mc = new MouseLightingController(tr3, listenForDeviceChanges: false, lastApplied: unsynced)
        { Debounce = TimeSpan.Zero, ResyncInterval = TimeSpan.FromMilliseconds(200), EngineTime = () => clock.Elapsed.TotalSeconds })
        {
            mc.SetDesired(unsynced);
            Thread.Sleep(1000);
            Check("1.16: sync off: an unchanged saved setting is never re-sent (no re-syncs)", tr3.Link.Applies == 0 && mc.SyncedApplies == 0);
        }
    }
}
