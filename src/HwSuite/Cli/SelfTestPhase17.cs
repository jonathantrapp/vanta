using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>
/// 1.17 checks: live mouse colour (MouseLive), from test 8 (2026-10-23) and SignalRGB's released plugin. Fake transport
/// and pure functions only; nothing here opens a device.
/// </summary>
public static partial class SelfTest
{
    static void Phase17Tests()
    {
        Guarded("1.17 mouse live", MouseLiveTests);
        Guarded("1.17.1 mouse scenes", MouseSceneTests);
    }

    /// <summary>How often the live mouse colour visibly lifts above its resting tone over two minutes of a scene.</summary>
    static (int events, double busy) MouseSceneActivity(EffectKind kind, int leds)
    {
        var cfg = new RgbConfig { Enabled = true, Mouse = true, Effect = kind, ColorA = 0xA8C8FF, ColorB = 0x19366B, Speed = 0.5, Brightness = 1 };
        var g = new LayoutTransform(null, 1, new CaseLayout()).GeometryFor(DeviceRole.Mouse, leds);
        var frame = new Rgb[leds];
        var lum = new List<double>();
        for (int f = 0; f < 120 * 20; f++)
        {
            Effects.Render(kind, EffectContext.From(cfg, f / 20.0, null), g, frame);
            var c = MouseSink.LiveColor(frame);
            lum.Add(0.2 * c.R + 0.7 * c.G + 0.1 * c.B);
        }
        var sorted = lum.OrderBy(x => x).ToArray();
        double rest = sorted[sorted.Length / 4], lift = Math.Max(12, rest * 0.35);
        int events = 0; bool up = false; int busy = 0;
        foreach (var l in lum)
        {
            bool now = l > rest + lift;
            if (now) busy++;
            if (now && !up) events++;
            up = now;
        }
        return (events, busy / (double)lum.Count);
    }

    static void MouseSceneTests()
    {
        Check("1.17.1: the mouse renders as 10 virtual LEDs, and its live colour is the brightest of them",
            MouseSink.BasePositions.Length == MouseSink.VirtualLeds && MouseSink.LiveColor(new[] { new Rgb(10, 10, 10), new Rgb(200, 210, 255), new Rgb(20, 20, 20) }) == new Rgb(200, 210, 255));
        var rows = new List<string>();
        bool better = true;
        foreach (var kind in new[] { EffectKind.Rainfall, EffectKind.Drip, EffectKind.Downpour, EffectKind.DistantStorm, EffectKind.Fireflies })
        {
            var one = MouseSceneActivity(kind, 1);
            var six = MouseSceneActivity(kind, MouseSink.VirtualLeds);
            rows.Add($"{kind}: {one.events} -> {six.events} per 2 min");
            if (kind is EffectKind.Rainfall or EffectKind.Drip or EffectKind.Downpour) better &= six.events >= 10 && six.events >= one.events;
        }
        Check("1.17.1: on the rain scenes the live mouse now catches drops (at least 10 per 2 minutes, no fewer than with one LED)", better, "(" + string.Join("; ", rows) + ")");
        var b = ConfigStore.Parse(ConfigStore.ToJson(new AppConfig { Rgb = { Balance = new ColorBalances { Mouse = new ColorBalance { R = 0.5 } } } }))!.Rgb.Balance;
        Check("1.17.1: the mouse colour balance round-trips; older files read as 'as sent'", b.Mouse.R == 0.5 && new ColorBalances().Mouse.IsIdentity &&
            ConfigStore.Parse("{\"Rgb\":{\"Balance\":{\"Ram\":{\"R\":0.4}}}}")!.Rgb.Balance is { } ob && (ob.Mouse == null || ob.Mouse.IsIdentity));
    }

    static void MouseLiveTests()
    {
        var tr = new FakeTransport();
        var steady = new MouseLightingState(MouseLightingState.EffectStatic, new Rgb(9, 9, 9), MouseLightingState.NeutralSpeed, 255, "wish", false);
        using (var mc = new MouseLightingController(tr, listenForDeviceChanges: false, lastApplied: steady) { Debounce = TimeSpan.Zero, LiveHz = 30, LiveKeepAlive = TimeSpan.FromMilliseconds(400) })
        {
            mc.SetDesired(steady);
            mc.SetLive(new Rgb(255, 0, 0));
            Check("1.17: live starts with one full write (static colour, full brightness), then streams colour only",
                WaitUntil(() => tr.Link.Applies == 1 && mc.Ack == LightAck.Accepted, 3000) && tr.Link.Last is { Effect: MouseLightingState.EffectStatic, Brightness: 255 } && tr.Link.Last.Color == new Rgb(255, 0, 0) && tr.Link.LiveSends == 0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int changes = 0;
            while (sw.ElapsedMilliseconds < 1000) { mc.SetLive(Rgb.FromHsv(changes * 7, 1, 1)); changes++; Thread.Sleep(5); }
            Thread.Sleep(100);
            int sent = tr.Link.LiveSends;
            Check("1.17: a colour changing every 5 ms is sent at most 30 times a second, and the newest colour wins",
                sent >= 20 && sent <= 33 && WaitUntil(() => tr.Link.LastLive == Rgb.FromHsv((changes - 1) * 7, 1, 1), 1000), $"({sent} sent for {changes} changes)");
            int before = tr.Link.LiveSends;
            Thread.Sleep(1300);
            int keep = tr.Link.LiveSends - before;
            Check("1.17: an unchanged colour is only repeated by the keep-alive", keep >= 2 && keep <= 4, $"({keep} in 1.3 s at 0.4 s)");
            Check("1.17: live keeps the onboard setting's applied record cleared and adds no onboard writes", tr.Link.Applies == 1 && mc.LastApplied == null);
            int applies = tr.Link.Applies;
            mc.SetLive(null);
            Check("1.17: leaving live writes the wanted onboard setting again (the mouse was showing a live colour)",
                WaitUntil(() => tr.Link.Applies == applies + 1 && Equals(tr.Link.Last, steady), 3000) && !mc.Live);
            tr.Link.Fail = true;
            int failsBefore = (int)mc.LiveFailures;
            mc.SetLive(new Rgb(1, 2, 3));
            Check("1.17: a failing live start is reported as failed and retried, not given up", WaitUntil(() => mc.LiveFailures >= failsBefore + 2, 3000) && mc.Ack == LightAck.Failed);
            tr.Link.Fail = false;
            Check("1.17: ... and recovers when the mouse answers again", WaitUntil(() => mc.Ack == LightAck.Accepted, 3000));
            mc.SetLive(Rgb.Black);
            Check("1.17.2: live never sends pure black: (0,0,0) goes out as (1,0,0), like OpenRGB's driver for this mouse",
                WaitUntil(() => tr.Link.LastLive == new Rgb(1, 0, 0), 3000) && mc.Ack == LightAck.Accepted && MouseLightingController.LiveWire(new Rgb(0, 0, 1)) == new Rgb(0, 0, 1));
        }

        var live = new RgbConfig { Enabled = true, Mouse = true, Effect = EffectKind.Breathing, MouseSync = true, MouseLive = true };
        Check("1.17: live takes precedence over locked breathing (VANTA keeps its own breathing shape and rate)",
            !MouseSync.Active(live) && EffectContext.From(live, 0, null).BreathingHz == null);
        var app = new AppConfig { Rgb = { Enabled = true, Mouse = true, Effect = EffectKind.Rainfall, MouseLive = true } };
        var st = LightingStatus.Evaluate(app, new DeviceFacts(DeviceRole.Mouse, true, true, null, true, 0, 1, null) { Ack = LightAck.Accepted });
        Check("1.17: with live on, the mouse card says it follows the scene itself", st.State == DeviceLightState.Shared && st.Headline.Contains("live"), $"({st.Headline})");
        Check("1.17: live is off by default and round-trips", new RgbConfig().MouseLive == false && ConfigStore.Parse(ConfigStore.ToJson(new AppConfig { Rgb = { MouseLive = true } }))!.Rgb.MouseLive);
    }
}
