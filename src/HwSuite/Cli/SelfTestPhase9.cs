using System.Text.Json;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>Phase 9: keyboard live frames (shared clock), case-fan header defaults, fan-tool guard rails. Mocks only.</summary>
public static partial class SelfTest
{
    sealed class FakeFanChannel : IFanChannel
    {
        public FakeFanChannel(string id, string name) { Id = id; Name = name; }
        public string Id { get; }
        public string Name { get; }
        public string Backend => "SuperIO (PawnIO)";
        public FanKind DefaultKind => FanKind.Generic;
        public bool CanControl => true;
        public bool HasTachSignal => true;
        public bool HasTachSensor => true;
        public double MinPercent => 0;
        public double? ReadRpm() => 700;
        public double? ReadPercent() => 100;
        public bool SetPercent(double percent) => true;
        public bool ReleaseToAuto() => true;
        public bool RestoreOriginal() => true;
        public void Relinquish() { }
        public HwResource Resource => HwResource.SuperIo;
        public int? ReadTargetPercent() => null;
    }

    static void Phase9Tests()
    {
        // --- live payload: vendor layout (06 00 + 126 x RGB at 3 x matrix index), framed in 7 reports
        var keys = ILovbeeKeyMap.Keys;
        Check("i87 key map: 87 keys, unique matrix indices inside the 126-LED frame",
            keys.Length == 87 && keys.Select(k => k.Matrix).Distinct().Count() == 87 && keys.All(k => k.Matrix is >= 0 and < ILovbeeKeyMap.MatrixLeds));
        var colors = keys.Select((k, i) => new Rgb((byte)i, (byte)(i + 1), (byte)(i + 2))).ToArray();
        var p = ILovbeeKeyboardDevice.BuildLivePayload(colors);
        int esc = Array.FindIndex(keys, k => k.Name == "Esc"), right = Array.FindIndex(keys, k => k.Name == "Right");
        Check("live payload is 06 00 + 378 bytes", p.Length == 380 && p[0] == 0x06 && p[1] == 0x00);
        Check("each key's colour sits at 3 x matrix index in R,G,B order (Esc = 0, Right arrow = 101)",
            p[2] == colors[esc].R && p[3] == colors[esc].G && p[4] == colors[esc].B
            && p[2 + 101 * 3] == colors[right].R && p[2 + 101 * 3 + 2] == colors[right].B);
        var used = keys.Select(k => k.Matrix).ToHashSet();
        Check("matrix slots without a key stay 0", Enumerable.Range(0, 126).Where(m => !used.Contains(m)).All(m => p[2 + m * 3] == 0 && p[3 + m * 3] == 0 && p[4 + m * 3] == 0));
        var reports = ILovbeeKeyboardDevice.Frame(p);
        Check("framed as 7 feature reports [0A, 7, 1..7, 62 bytes], unused tail zero",
            reports.Count == 7 && reports.All(r => r.Length == 65 && r[0] == 0x0A && r[1] == 7) && reports.Select(r => (int)r[2]).SequenceEqual(Enumerable.Range(1, 7))
            && reports[6].Skip(3 + 380 - 6 * 62).All(b => b == 0));
        var ux = ILovbeeKeyMap.UnitX;
        Check("key positions run 0..1 left to right (Esc leftmost, Right arrow rightmost of the bottom row)",
            Math.Abs(ux.Min()) < 1e-9 && Math.Abs(ux.Max() - 1) < 1e-9 && ux[esc] < 0.01 && ux[right] > 0.99);

        // --- controller gating for live frames
        var t = new FakeKbTransport();
        bool live = false; string? owner = null;
        using (var ctl = new KeyboardLightingController(t, () => true, () => owner, listenForDeviceChanges: false, liveVerified: () => live))
        {
            ctl.SetLiveWanted(true);
            WaitFor(() => ctl.Connected);
            var r1 = ctl.SendLive(p, out var d1);
            Check("no live frame until the owner verified live frames", r1 == KeyboardLightingController.LiveResult.Skipped && t.LiveCount == 0, d1);
            live = true; owner = "the iLovbee keyboard software (DeviceDriver.exe) is running";
            var r2 = ctl.SendLive(p, out var d2);
            Check("no live frame while DeviceDriver.exe runs (checked at the write boundary)", r2 == KeyboardLightingController.LiveResult.Skipped && t.LiveCount == 0, d2);
            owner = null;
            Thread.Sleep(1100);   // live ownership is re-checked at most once a second
            var r3 = ctl.SendLive(p, out _);
            Check("after the vendor software exits, live frames go out", r3 == KeyboardLightingController.LiveResult.Sent && t.LiveCount == 1);
            ctl.SetBlocked("SMBus test owner");
            Check("the engine's ownership hint blocks a live frame immediately", ctl.SendLive(p, out _) == KeyboardLightingController.LiveResult.Skipped && t.LiveCount == 1);
            ctl.SetBlocked(null);
            ctl.SetSuspended(true);
            Check("no live frame while the system sleeps", ctl.SendLive(p, out _) == KeyboardLightingController.LiveResult.Skipped && t.LiveCount == 1);
            ctl.SetSuspended(false);
            t.FailNext = 1;
            Check("a failed live write is reported as a failure (counted, link re-checked)", ctl.SendLive(p, out _) == KeyboardLightingController.LiveResult.Failed && ctl.LiveFailures == 1);
        }

        // --- after live frames the stored setting is really written (the keyboard no longer shows it)
        var t2 = new FakeKbTransport();
        var off = new KeyboardLightingState(KeyboardLightingState.ModeSteady, Rgb.Black, 4, 0, 0, "Brightness 0", false) { Sync = KeyboardSync.Off };
        using (var ctl2 = new KeyboardLightingController(t2, () => true, () => null, lastApplied: off, listenForDeviceChanges: false, liveVerified: () => true) { Debounce = TimeSpan.FromMilliseconds(50) })
        {
            ctl2.SetLiveWanted(true);
            WaitFor(() => ctl2.Connected);
            ctl2.SendLive(p, out _);
            ctl2.SetLiveWanted(false);
            ctl2.SetDesired(off);
            WaitFor(() => t2.Count == 1);
            Check("Lights out after live frames sends the stored brightness 0 again (it was 'up to date' before the frames)", t2.Count == 1 && t2.Last?.Brightness == 0);
        }

        // --- the sink: live mode, positions, rate and no catch-up burst
        var cfg = new RgbConfig { Enabled = true, Keyboard = true, Effect = EffectKind.Breathing };
        var t3 = new FakeKbTransport();
        bool lv = false;
        var sink = new KeyboardSink(cfg, t3, () => true, () => null, null, null, listenForDeviceChanges: false, liveVerified: () => lv);
        try
        {
            sink.StartWorker();
            var f1 = new Rgb[sink.LedCount];
            sink.Post(f1);
            Check("without live verification the keyboard is one onboard zone (stored setting, own timing)", sink.LedCount == 1 && !sink.Live && sink.Wanted?.Mode == KeyboardLightingState.ModeBreathing);
            lv = true;
            sink.Post(new Rgb[sink.LedCount]);
            Check("with live frames every key is its own LED on the shared wave line",
                sink.LedCount == 87 && sink.Live && Math.Abs(sink.Positions.Min() - KeyboardSink.WaveStart) < 1e-9 && Math.Abs(sink.Positions.Max() - (KeyboardSink.WaveStart + KeyboardSink.WaveSpan)) < 1e-9);
            Check("live: the stored fallback is the effect's onboard setting, the transfer lead is applied and the rate limit is the live rate",
                sink.Wanted?.Mode == KeyboardLightingState.ModeBreathing && sink.LeadSeconds > 0 && Math.Abs(sink.RateLimitHz - KeyboardSink.LiveHz) < 1e-9);
            WaitFor(() => t3.LiveCount >= 1);
            int before = t3.LiveCount;
            var sw = System.Diagnostics.Stopwatch.StartNew(); int k = 0;
            while (sw.Elapsed.TotalSeconds < 2) { var fr = Enumerable.Repeat(new Rgb((byte)(k++ % 250 + 1), 0, 0), 87).ToArray(); sink.Post(fr); Thread.Sleep(16); }
            int in2s = t3.LiveCount - before;
            Check($"60/s offers become at most {KeyboardSink.LiveHz:0} live frames/s (latest frame wins, nothing queued)", in2s >= 22 && in2s <= 32, $"({in2s} in 2 s)");
            Thread.Sleep(1000);   // stall: nothing offered
            int afterStall = t3.LiveCount;
            sw.Restart();
            while (sw.Elapsed.TotalSeconds < 0.5) { sink.Post(Enumerable.Repeat(new Rgb((byte)(k++ % 250 + 1), 1, 1), 87).ToArray()); Thread.Sleep(16); }
            int burst = t3.LiveCount - afterStall;
            Check("no catch-up burst after a stall", burst <= 9, $"({burst} in 0.5 s)");
            int beforeOff = t3.LiveCount;
            sink.Deactivate(new Rgb(10, 20, 30));
            Thread.Sleep(200);
            Check("stopping output sends no closing live frame (the keyboard falls back to its stored setting, the effect's onboard look)", t3.LiveCount == beforeOff);
            sink.Activate();
            sink.Post(Enumerable.Repeat(new Rgb(1, 1, 1), 87).ToArray());
            WaitFor(() => t3.LiveCount > 0 && t3.Live[^1][2] == 1);
            cfg.Effect = EffectKind.Stealth;
            sink.Post(new Rgb[sink.LedCount]);
            Check("Stealth / Lights out leave live mode and use the stored brightness 0", !sink.Live && sink.Wanted?.Brightness == 0 && sink.LeadSeconds == 0);
            int beforeStop = t3.LiveCount;
            sink.Deactivate(new Rgb(10, 20, 30));
            Check("stopping output during Lights out sends no lit live frame (the keyboard stays dark)", t3.LiveCount == beforeStop);
        }
        finally { sink.Dispose(); }

        // --- config: the live opt-in persists, the trial never does
        var ac = new AppConfig { KeyboardLiveVerified = true, KeyboardLiveTrial = true };
        var json = JsonSerializer.Serialize(ac);
        var back = ConfigStore.Parse(json)!;
        Check("KeyboardLiveVerified is saved; a live trial is runtime only", back.KeyboardLiveVerified && !back.KeyboardLiveTrial && !json.Contains("KeyboardLiveTrial"));
        Check("older configs load with live frames off", ConfigStore.Parse("{\"Version\":4,\"KeyboardVerified\":true}")!.KeyboardLiveVerified == false);

        // --- case-fan headers: evidence-based labels, type upgrade only, still firmware-controlled until a minimum is confirmed
        var c1 = new FakeFanChannel("lhm:/lpc/it8689e/0/control/1", "ITE IT8689E Fan #2");
        var c2 = new FakeFanChannel("lhm:/lpc/it8689e/0/control/2", "ITE IT8689E Fan #3");
        var c4 = new FakeFanChannel("lhm:/lpc/it8689e/0/control/4", "ITE IT8689E Fan #5");
        Check("control 1 defaults to Rear exhaust, case fan", ChannelDefaults.For(c1) == ("Rear exhaust", FanKind.CaseFan));
        Check("control 2 = upper front intake on SYS_FAN2 (owner-confirmed), control 3 = lower on SYS_FAN3",
            ChannelDefaults.For(c2).label == "Front intake—SYS_FAN2 (upper)" && ChannelDefaults.CaseFanLabels["lhm:/lpc/it8689e/0/control/3"] == "Front intake—SYS_FAN3 (lower)");
        Check("controls 4 and 5 stay unidentified", ChannelDefaults.For(c4) == ("ITE IT8689E Fan #5", FanKind.Generic));
        var curve = new FanCurve { Name = "Performance", Points = { new CurvePoint(30, 40), new CurvePoint(75, 100) } };
        var saved = new ChannelConfig { Id = c2.Id, Label = c2.Name, Kind = FanKind.Generic, Mode = ChannelMode.Auto, Curve = curve };
        ChannelDefaults.Upgrade(saved, c2, "Balanced");
        Check("upgrade: auto label and Unclassified type replaced; curve and mode kept",
            saved.Label.StartsWith("Front intake") && saved.Kind == FanKind.CaseFan && ReferenceEquals(saved.Curve, curve) && saved.Mode == ChannelMode.Auto);
        var renamed = new ChannelConfig { Id = c1.Id, Label = "My rear fan", Kind = FanKind.Generic };
        ChannelDefaults.Upgrade(renamed, c1, "Balanced");
        Check("upgrade keeps a label the owner chose", renamed.Label == "My rear fan");
        Check("an identified case fan is still not driven without an owner-confirmed minimum", !FanSafety.Assess(saved, c2).Controllable);
        saved.VerifiedMinPercent = 45; saved.MinEvidence = "test";
        Check("with a confirmed minimum it may be driven, never below that minimum", FanSafety.Assess(saved, c2) is { Controllable: true, Floor: 45 });

        // --- a stalled 3-pin fan's tachometer can report nonsense: that is not a speed
        Check("tach plausibility: 700 RPM spinning; 0, null and the 9 926 RPM stall glitch are not",
            FanEngine.Spinning(700) && !FanEngine.Spinning(0) && !FanEngine.Spinning(null) && !FanEngine.Spinning(9926));

        // --- fan tools refuse unsafe requests before touching hardware
        Check("fan-step never steps control 0 (CPU_FAN, pump)", FanMapping.Step(0, new[] { 100 }, 5, false, null) == 2);
        Check("fan-step refuses duties below 40% unless the owner is watching", FanMapping.Step(2, new[] { 30 }, 5, false, null) == 2);
        Check("fan-min-test refuses to run unattended", FanMapping.MinTest(2, false, null) == 2);
        Check("fan-min-test never tests control 0", FanMapping.MinTest(0, true, null) == 2);
    }
}
