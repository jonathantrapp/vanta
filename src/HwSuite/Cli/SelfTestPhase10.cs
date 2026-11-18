using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;
using HwSuite.Ui;

namespace HwSuite.Cli;

/// <summary>
/// Phase 10: keyboard mode transitions (live frames vs the stored command), transport-failure recovery on an unchanged
/// device path, ownership checked on every transfer, case-fan startup verification. Mocks only: these prove the logic,
/// not what the keyboard or the fans physically do (the attended hardware runs in docs\phase10 do that).
/// </summary>
public static partial class SelfTest
{
    static Rgb[] Keys(Rgb c) => Enumerable.Repeat(c, ILovbeeKeyMap.Keys.Length).ToArray();

    static KeyboardSink NewKbSink(RgbConfig cfg, FakeKbTransport t, Func<string?>? owner = null)
    {
        var s = new KeyboardSink(cfg, t, () => true, owner ?? (() => null), null, null, listenForDeviceChanges: false, liveVerified: () => true);
        s.Controller.Debounce = TimeSpan.FromMilliseconds(60);
        s.Controller.EnumerateInterval = TimeSpan.FromMilliseconds(40);
        s.StartWorker();
        return s;
    }

    /// <summary>The engine re-posts a still frame about once a second; tests post more often to make waits short.</summary>
    static bool PostUntil(KeyboardSink s, Rgb[] frame, Func<bool> cond, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { s.Post(frame); if (cond()) return true; Thread.Sleep(40); }
        return cond();
    }

    static void Phase10Tests()
    {
        KeyboardTransitionTests();
        KeyboardRecoveryTests();
        KeyboardOwnershipTests();
        FanStartupTests();
        FanActivationTests();
        Check("output stop / exit during Lights out leaves streamed devices black; otherwise the primary colour",
            RgbEngine.SteadyLook(new RgbConfig { Effect = EffectKind.Stealth, ColorA = 0x2198AA }) == Rgb.Black
            && RgbEngine.SteadyLook(new RgbConfig { Effect = EffectKind.Breathing, ColorA = 0x2198AA }) == Rgb.FromInt(0x2198AA));
    }

    // ---- 7: switching fan control on shows (and lets the owner limit) what it activates ------------------------------
    static void FanActivationTests()
    {
        // the owner's saved state: GPU fans and the AIO already on Curve, case fans on Firmware with confirmed minimums
        var cfg = new AppConfig { FanControlEnabled = false };
        var gpu0 = new FakeChannel("gpu:0", FanKind.GpuFan, 30); var gpu1 = new FakeChannel("gpu:1", FanKind.GpuFan, 30);
        var aio = new FakeChannel(ChannelDefaults.CpuFanChannelId, FanKind.Aio);
        var rear = new FakeChannel("lhm:/lpc/it8689e/0/control/1", FanKind.CaseFan);
        var up = new FakeChannel("lhm:/lpc/it8689e/0/control/2", FanKind.CaseFan);
        var low = new FakeChannel("lhm:/lpc/it8689e/0/control/3", FanKind.CaseFan);
        var spare = new FakeChannel("lhm:/lpc/it8689e/0/control/4", FanKind.Generic);
        var chans = new IFanChannel[] { gpu0, gpu1, aio, rear, up, low, spare };
        cfg.Channels.Add(Cfg(gpu0, FanKind.GpuFan, ChannelMode.Curve, "Performance"));
        cfg.Channels.Add(Cfg(gpu1, FanKind.GpuFan, ChannelMode.Curve, "Performance"));
        cfg.Channels.Add(Cfg(aio, FanKind.Aio, ChannelMode.Curve, "Performance"));
        cfg.Channels.Add(Cfg(rear, FanKind.CaseFan, ChannelMode.Auto, "Performance", 50));
        cfg.Channels.Add(Cfg(up, FanKind.CaseFan, ChannelMode.Auto, "Performance", 35));
        cfg.Channels.Add(Cfg(low, FanKind.CaseFan, ChannelMode.Auto, "Performance", 50));
        cfg.Channels.Add(Cfg(spare, FanKind.Generic, ChannelMode.Auto));
        var curves = cfg.Channels.ToDictionary(c => c.Id, c => c.Curve);
        var cand = FanActivation.Candidates(cfg, chans);
        Check("activation lists every channel the switch could drive and marks GPU + AIO as already active",
            cand.Count == 6 && cand.Where(c => c.DrivenNow).Select(c => c.Channel.Id).ToHashSet().SetEquals(new[] { "gpu:0", "gpu:1", aio.Id }) && cand.All(c => c.Channel.Id != spare.Id),
            string.Join(", ", cand.Select(c => $"{c.Channel.Id}:{c.Mode}")));
        var pick = FanActivation.CaseFansOnly(cand);
        var changes = FanActivation.Apply(cfg, cand, pick);
        Check("'Case fans only': the three case fans go to Curve, GPU fans and the AIO to Firmware, every curve object untouched",
            pick.Count == 3 && cfg.Channels.Where(c => c.Kind == FanKind.CaseFan).All(c => c.Mode == ChannelMode.Curve)
            && cfg.Channels.Where(c => c.Kind is FanKind.GpuFan or FanKind.Aio).All(c => c.Mode == ChannelMode.Auto)
            && cfg.Channels.All(c => ReferenceEquals(c.Curve, curves[c.Id])) && changes.Count == 6 && !cfg.FanControlEnabled,
            string.Join("; ", changes));
        var cand2 = FanActivation.Candidates(cfg, chans);
        Check("keeping the current selection changes nothing", FanActivation.Apply(cfg, cand2, cand2.Where(c => c.DrivenNow).Select(c => c.Channel.Id).ToHashSet()).Count == 0);
        Check("the lower front floor (no measured restart) is shown as provisional; a measured restart at or below the floor is not",
            cand2.First(c => c.Channel.Id == low.Id).Safety.Provisional);
    }

    // ---- 3: case-fan startup verification, tach mapping independent of rotation history -------------------------------
    static void FanStartupTests()
    {
        // mapping from step-test evidence, not from rotation history
        var c3 = new FakeChannel("lhm:/lpc/it8689e/0/control/3", FanKind.CaseFan);
        var c4 = new FakeChannel("lhm:/lpc/it8689e/0/control/4", FanKind.Generic);
        var saved = new ChannelConfig { Id = c3.Id, Label = "Front intake—SYS_FAN3 (lower)", Kind = FanKind.CaseFan, TachSeen = false, VerifiedMinPercent = 50, MinEvidence = "test" };
        ChannelDefaults.Upgrade(saved, c3, "Balanced");
        var other = ChannelDefaults.Create(c4, "Balanced");
        Check("a step-test tach mapping is recorded for controls 1-3 even with TachSeen=false; not for control 4",
            saved.TachMapped && saved.TachEvidence?.Contains("fan-step") == true && !saved.TachSeen && !other.TachMapped);

        // provisional vs measured floors
        var lower = new ChannelConfig { Id = "x", Kind = FanKind.CaseFan, VerifiedMinPercent = 50, MinEvidence = "owner" };
        var a1 = FanSafety.Assess(lower, c3);
        lower.MeasuredStartPercent = 40;
        var a2 = FanSafety.Assess(lower, c3);
        lower.MeasuredStartPercent = 55;
        var a3 = FanSafety.Assess(lower, c3);
        Check("floor without a startup measurement is marked provisional; a measured restart at or below it is not",
            a1.Provisional && a1.Basis.Contains("provisional") && !a2.Provisional && a2.Basis.Contains("40% (measured)") && a3.Provisional,
            $"[{a1.Basis}] [{a2.Basis}] [{a3.Basis}]");

        DateTime clock = new(2026, 10, 14, 23, 0, 0, DateTimeKind.Utc);
        (FanEngine e, FakeChannel fan, AppConfig cfg, List<string> alerts) Rig(bool mapped, bool seen, double? rpm)
        {
            var cfg = new AppConfig { FanControlEnabled = true };
            var e = new FanEngine(cfg) { UtcNow = () => clock };
            var fan = new FakeChannel("fan3", FanKind.CaseFan) { Rpm = rpm };
            e.Channels.Add(fan);
            var cc = Cfg(fan, FanKind.CaseFan, ChannelMode.Curve, min: 50);
            cc.TachMapped = mapped; cc.TachSeen = seen;
            cfg.Channels.Add(cc);
            var alerts = new List<string>();
            e.Alert += alerts.Add;
            return (e, fan, cfg, alerts);
        }
        void Run(FanEngine e, int seconds) { for (int i = 0; i < seconds; i++) { clock = clock.AddSeconds(1); e.Tick(Snap(35, 35), 1); } }

        // a mapped fan that never turns this session (TachSeen false) is not exempt
        var (e1, f1, _, al1) = Rig(mapped: true, seen: false, rpm: 0);
        using (e1)
        {
            Run(e1, 3);
            Check("start check: a mapped fan is driven at its floor first, waiting for rotation", f1.Applied is >= 50 and < 100 && e1.Status[f1.Id].State.Contains("waiting for rotation"), e1.Status[f1.Id].State);
            Run(e1, (int)FanEngine.StartupWindow.TotalSeconds - 1);
            Check("no rotation within the startup window: a full-speed start kick, with an alert", f1.Applied == 100 && e1.StartState(f1.Id).kicks == 1 && al1.Any(a => a.Contains("did not start")), e1.Status[f1.Id].State);
            Run(e1, 25);
            Check($"after {FanEngine.MaxStartKicks} kicks without rotation it is handed back to firmware control (not left at a stalled duty)",
                f1.Released && !e1.IsControlled(f1.Id) && e1.StartState(f1.Id).failed && al1.Any(a => a.Contains("Handed back")), e1.Status[f1.Id].State);
            int writes = f1.Writes;
            Run(e1, 60);
            Check("...and stays with the firmware (no retry loop) until the retry time", f1.Writes == writes && e1.Status[f1.Id].State.Contains("retries in"), e1.Status[f1.Id].State);
            clock = clock.Add(FanEngine.StartFailRetry);
            f1.Rpm = 600;
            Run(e1, 3);
            Check("after the retry time VANTA takes it over again and verifies the start", e1.IsControlled(f1.Id) && e1.StartState(f1.Id).verified);
        }

        // a fan that starts during the kick returns to its curve
        var (e2, f2, _, _) = Rig(mapped: true, seen: false, rpm: 0);
        using (e2)
        {
            Run(e2, (int)FanEngine.StartupWindow.TotalSeconds + 2);
            bool kicked = f2.Applied == 100;
            f2.Rpm = 900;
            Run(e2, 2);
            Run(e2, 30);
            Check("a fan that starts during the start kick is verified and ramps back down to its curve", kicked && e2.StartState(f2.Id).verified && f2.Applied < 100 && f2.Applied >= 50, $"(applied {f2.Applied})");
            f2.Rpm = 0;
            Run(e2, 4);
            Check("a verified fan that stops later: feedback loss holds 100% (TachSeen not required)", f2.Applied == 100 && e2.Status[f2.Id].FeedbackLost);
        }

        // a fan already turning when VANTA takes over: verified at once, never kicked
        var (e3, f3, _, al3) = Rig(mapped: true, seen: false, rpm: 650);
        using (e3)
        {
            Run(e3, 15);
            Check("a fan turning at takeover is verified without a kick", e3.StartState(f3.Id) is { verified: true, kicks: 0 } && f3.Applied < 100 && al3.Count == 0);
        }

        // the stall glitch reading (~9 900 RPM) does not count as a start
        var (e4, f4, _, _) = Rig(mapped: true, seen: false, rpm: 9926);
        using (e4)
        {
            Run(e4, (int)FanEngine.StartupWindow.TotalSeconds + 2);
            Check("an implausible stall reading is not accepted as rotation", !e4.StartState(f4.Id).verified && f4.Applied == 100);
        }

        // no tachometer knowledge at all (unmapped, never seen): nothing to verify against, behaviour unchanged
        var (e5, f5, _, al5) = Rig(mapped: false, seen: false, rpm: 0);
        using (e5)
        {
            Run(e5, 30);
            Check("an unmapped header that never reported rotation is not start-checked (no evidence to check against)", f5.Applied is >= 50 and < 100 && al5.Count == 0);
        }

        // sleep/resume: the start is verified again after resume
        var (e6, f6, _, _) = Rig(mapped: true, seen: false, rpm: 700);
        using (e6)
        {
            Run(e6, 3);
            e6.Suspend(); e6.Resume();
            f6.Rpm = 0;
            clock = clock.Add(FanEngine.ResumeGrace);
            Run(e6, (int)FanEngine.StartupWindow.TotalSeconds + 3);
            Check("after resume a fan that does not turn again is kicked (start re-verified)", e6.StartState(f6.Id).kicks >= 1 && f6.Applied == 100);
        }
    }

    // ---- 1: Lights out <-> live frames ---------------------------------------------------------------------------
    static void KeyboardTransitionTests()
    {
        var orange = new Rgb(250, 120, 0);
        var cfg = new RgbConfig { Enabled = true, Keyboard = true, Effect = EffectKind.Static, ColorA = 0xFA7800 };
        var t = new FakeKbTransport();
        var sink = NewKbSink(cfg, t);
        try
        {
            PostUntil(sink, Keys(orange), () => t.LiveCount >= 1);
            Check("static colour reaches the keyboard as a live frame", t.LiveCount >= 1 && t.LastLive?[2] == orange.R);

            cfg.Effect = EffectKind.Stealth;
            PostUntil(sink, Keys(Rgb.Black), () => t.Count >= 1);
            Check("Lights out: the stored brightness 0 is written", t.Count == 1 && t.Last?.Brightness == 0);

            cfg.Effect = EffectKind.Static;
            int liveBefore = t.LiveCount;
            bool resent = PostUntil(sink, Keys(orange), () => t.LiveCount > liveBefore, 1500);
            Check("Lights out -> the SAME static colour: the restored frame reaches the keyboard (not dropped as 'unchanged')",
                resent && t.LastLive?[2] == orange.R, $"(live frames {liveBefore} -> {t.LiveCount})");

            cfg.Effect = EffectKind.Stealth;
            PostUntil(sink, Keys(Rgb.Black), () => t.Count >= 2);
            cfg.Effect = EffectKind.Breathing;
            liveBefore = t.LiveCount;
            Check("Lights out -> Breathing: live frames resume", PostUntil(sink, Keys(new Rgb(10, 10, 10)), () => t.LiveCount > liveBefore, 1500));
            cfg.Effect = EffectKind.Stealth;
            PostUntil(sink, Keys(Rgb.Black), () => t.Count >= 3);
            cfg.Effect = EffectKind.GradientWave;
            liveBefore = t.LiveCount;
            Check("Lights out -> Gradient Wave: live frames resume", PostUntil(sink, Keys(new Rgb(20, 30, 40)), () => t.LiveCount > liveBefore, 1500));
            var stored = t.Sent.Select(x => x.s).ToList();
            Check("each Lights out wrote one stored brightness 0", stored.Count(x => x.Brightness == 0) == 3, string.Join(", ", stored.Select(x => x.Description + "/" + x.Brightness)));

            // Lights out undone before the debounce expired: the stored off is never written
            cfg.Effect = EffectKind.Static;
            PostUntil(sink, Keys(orange), () => t.LastLive?[2] == orange.R);
            bool fallback = PostUntil(sink, Keys(orange), () => t.Last?.Brightness > 0 && t.Last?.Color == orange, 1500);
            Check("a settled live effect keeps its onboard look stored underneath (the keyboard's fallback when frames stop)",
                fallback && t.Last?.Mode == KeyboardLightingState.ModeSteady && sink.Live, t.Last?.Description ?? "none");
            int storedCount = t.Count;
            cfg.Effect = EffectKind.Stealth; sink.Post(Keys(Rgb.Black));
            cfg.Effect = EffectKind.Static;
            liveBefore = t.LiveCount;
            PostUntil(sink, Keys(orange), () => false, 400);
            Check("Lights out undone within the debounce: no stored write, and the static frame is sent again",
                t.Count == storedCount && t.LiveCount > liveBefore, $"(stored {storedCount} -> {t.Count}, live {liveBefore} -> {t.LiveCount})");
        }
        finally { sink.Dispose(); }

        // a stored write decided before live frames were wanted again is abandoned at its write boundary
        var cfg2 = new RgbConfig { Enabled = true, Keyboard = true, Effect = EffectKind.Static, ColorA = 0x00FF00 };
        var t2 = new FakeKbTransport();
        var s2 = NewKbSink(cfg2, t2);
        try
        {
            PostUntil(s2, Keys(new Rgb(0, 255, 0)), () => t2.LiveCount >= 1);
            int raced = 0;
            t2.OnProfile = () => { if (raced++ == 0) { cfg2.Effect = EffectKind.Static; s2.Post(Keys(new Rgb(0, 255, 0))); } };
            cfg2.Effect = EffectKind.Stealth;
            s2.Post(Keys(Rgb.Black));
            WaitFor(() => raced > 0, 2000);
            PostUntil(s2, Keys(new Rgb(0, 255, 0)), () => false, 400);
            Check("a stale stored brightness 0 is not written after live frames were wanted again", !t2.Order.Contains("stored:0") && raced > 0, string.Join(",", t2.Order));
            Check("...live frames continue after it", t2.Order.LastOrDefault()?.StartsWith("live:") == true, t2.Order.LastOrDefault() ?? "");
        }
        finally { s2.Dispose(); }

        // a live frame that waits behind a running stored write goes out after it (the newest setting wins)
        var t3 = new FakeKbTransport { StoredDelayMs = 300 };
        var off = KeyboardLightingState.From(new RgbConfig { Enabled = true, Effect = EffectKind.Stealth }, 4)!;
        using (var ctl = new KeyboardLightingController(t3, () => true, () => null, listenForDeviceChanges: false, liveVerified: () => true) { Debounce = TimeSpan.FromMilliseconds(30) })
        {
            ctl.SetDesired(off);
            WaitFor(() => ctl.Connected);
            Thread.Sleep(150);   // the stored write is now running (300 ms)
            ctl.SetDesired(null); ctl.SetLiveWanted(true);
            var r = ctl.SendLive(ILovbeeKeyboardDevice.BuildLivePayload(Keys(new Rgb(77, 0, 0))), out var d);
            WaitFor(() => t3.Order.Count >= 2, 2000);
            Check("live frame queued behind a running stored write is written after it, never interleaved",
                r == KeyboardLightingController.LiveResult.Sent && t3.Order.SequenceEqual(new[] { "stored:0", "live:77" }), string.Join(",", t3.Order) + " " + d);
        }

        // output stops right after a change (still inside the settle time): the stored fallback is written at the stop
        var cfg5 = new RgbConfig { Enabled = true, Keyboard = true, Effect = EffectKind.Static, ColorA = 0x0000FF };
        var t5 = new FakeKbTransport();
        var s5 = NewKbSink(cfg5, t5);
        try
        {
            PostUntil(s5, Keys(new Rgb(0, 0, 255)), () => t5.Last?.Color == new Rgb(0, 0, 255));
            s5.Controller.Debounce = TimeSpan.FromSeconds(30);
            cfg5.ColorA = 0xFFDC00;
            var yellow = Rgb.FromInt(0xFFDC00);
            PostUntil(s5, Keys(yellow), () => t5.LastLive?[2] == yellow.R, 1500);
            int before = t5.Count;
            s5.Deactivate(yellow);
            Check("output stopped inside the settle time: the new stored fallback (yellow) is written once at the stop, so the keyboard falls back to it",
                t5.Count == before + 1 && t5.Last?.Color == yellow && t5.Last?.Brightness > 0, $"(stored {before} -> {t5.Count}, last {t5.Last?.Description})");
            s5.Deactivate(yellow);
            Check("...and only once (a second stop has nothing pending)", t5.Count == before + 1);
        }
        finally { s5.Dispose(); }

        // live frames that do not change are refreshed about once a second (keeps a still colour shown, detects a dead handle)
        var cfg4 = new RgbConfig { Enabled = true, Keyboard = true, Effect = EffectKind.Static };
        var t4 = new FakeKbTransport();
        var s4 = NewKbSink(cfg4, t4);
        try
        {
            PostUntil(s4, Keys(new Rgb(5, 6, 7)), () => t4.LiveCount >= 1);
            int n0 = t4.LiveCount;
            PostUntil(s4, Keys(new Rgb(5, 6, 7)), () => false, 3100);
            int n = t4.LiveCount - n0;
            Check($"an unchanged live frame is re-sent about every {KeyboardSink.LiveRefresh.TotalSeconds:0.00} s (the keyboard reverts after a few seconds without frames), not at the live rate", n is >= 3 and <= 5, $"({n} in 3.1 s)");
        }
        finally { s4.Dispose(); }
    }

    // ---- 2: failed handle recovery ----------------------------------------------------------------------------------
    static void KeyboardRecoveryTests()
    {
        var p = ILovbeeKeyboardDevice.BuildLivePayload(Keys(new Rgb(1, 2, 3)));
        var t = new FakeKbTransport();
        using (var ctl = new KeyboardLightingController(t, () => true, () => null, listenForDeviceChanges: false, liveVerified: () => true) { EnumerateInterval = TimeSpan.FromMilliseconds(40) })
        {
            ctl.SetLiveWanted(true);
            WaitFor(() => ctl.Connected);
            Check("live frame on the first handle", ctl.SendLive(p, out _) == KeyboardLightingController.LiveResult.Sent && t.Opens == 1);
            t.DeadGen = t.Opens;   // the handle goes stale; the device path (signature) does not change
            Check("a write on the stale handle fails", ctl.SendLive(p, out _) == KeyboardLightingController.LiveResult.Failed);
            bool reopened = WaitFor(() => t.Opens >= 2, 2000);
            Check("transport failure: the handle is closed and reopened although the device path is unchanged", reopened, $"(opens {t.Opens})");
            WaitFor(() => ctl.SendLive(p, out _) == KeyboardLightingController.LiveResult.Sent, 2000);
            Check("live frames flow again on the reopened handle", t.LiveCount >= 2 && ctl.Reopens >= 1, $"(live {t.LiveCount}, reopens {ctl.Reopens})");

            // an ordinary device-list notification with the same interface set keeps the open handle
            int opens = t.Opens;
            ctl.NotifyDeviceChange();
            Thread.Sleep(300);
            Check("an ordinary device-list notification (same device path) does not reopen a working handle", t.Opens == opens, $"(opens {opens} -> {t.Opens})");

            // bounded: every reopened handle is dead too (the sink keeps offering frames, as its refresh does)
            ctl.ReopenBackoff = new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(200) };
            t.DeadGen = int.MaxValue;
            WaitFor(() => { ctl.SendLive(p, out _); return ctl.ReopenGaveUp; }, 6000);
            int after = t.Opens;
            Thread.Sleep(600);
            Check($"reopen is bounded: after {KeyboardLightingController.MaxReopenAttempts} attempts it stops and waits for a device change",
                ctl.ReopenGaveUp && t.Opens == after && t.Opens - opens <= KeyboardLightingController.MaxReopenAttempts, $"(opens {opens} -> {t.Opens})");
            t.DeadGen = -1;
            t.Sig = "kb-1 (replugged)";   // Phase 11: only a genuine change (different interfaces) restarts recovery
            ctl.NotifyDeviceChange();
            Check("a device change after giving up starts one new recovery round", WaitFor(() => t.Opens > after, 2000) && WaitFor(() => ctl.SendLive(p, out _) == KeyboardLightingController.LiveResult.Sent, 2000));

            // no reopen while the system sleeps
            t.DeadGen = t.Opens;
            ctl.SendLive(p, out _);
            ctl.SetSuspended(true);
            int sleeping = t.Opens;
            Thread.Sleep(700);
            Check("no reopen attempt while the system sleeps", t.Opens == sleeping, $"(opens {sleeping} -> {t.Opens})");
            ctl.SetSuspended(false);
            Check("after resume the failed handle is reopened", WaitFor(() => t.Opens > sleeping, 3000));
        }

        // sink level: the SAME static colour is restored after a reopen and after a reconnect with the same path
        var orange = new Rgb(250, 120, 0);
        var cfg = new RgbConfig { Enabled = true, Keyboard = true, Effect = EffectKind.Static };
        var t2 = new FakeKbTransport();
        var s = NewKbSink(cfg, t2);
        try
        {
            PostUntil(s, Keys(orange), () => t2.LiveCount >= 1);
            t2.DeadGen = t2.Opens;
            int before = t2.LiveCount;
            bool restored = PostUntil(s, Keys(orange), () => t2.LiveCount > before && t2.Opens >= 2, 4000);
            Check("stale handle under a still colour: detected by the refresh, reopened, and the same colour sent again",
                restored && t2.LastLive?[2] == orange.R, $"(opens {t2.Opens}, live {before} -> {t2.LiveCount})");

            t2.Sig = ""; s.Controller.NotifyDeviceChange();
            PostUntil(s, Keys(orange), () => !s.Controller.Connected, 2000);
            int unplugged = t2.LiveCount;
            t2.Sig = "kb-1"; s.Controller.NotifyDeviceChange();
            bool back = PostUntil(s, Keys(orange), () => t2.LiveCount > unplugged, 3000);
            Check("unplug -> plug (same device path): the same static colour is sent again after the reconnect", back && t2.LastLive?[2] == orange.R && s.Controller.Reconnects >= 1,
                $"(reconnects {s.Controller.Reconnects}, live {unplugged} -> {t2.LiveCount})");
        }
        finally { s.Dispose(); }
    }

    // ---- 4: ownership is checked on every live transfer --------------------------------------------------------------
    static void KeyboardOwnershipTests()
    {
        var p = ILovbeeKeyboardDevice.BuildLivePayload(Keys(new Rgb(9, 9, 9)));
        var t = new FakeKbTransport();
        string? owner = null; int checks = 0;
        using (var ctl = new KeyboardLightingController(t, () => true, () => { checks++; return owner; }, listenForDeviceChanges: false, liveVerified: () => true))
        {
            ctl.SetLiveWanted(true);
            WaitFor(() => ctl.Connected);
            ctl.SendLive(p, out _);
            int c0 = checks;
            for (int i = 0; i < 5; i++) ctl.SendLive(p, out _);
            Check("every live transfer runs its own ownership check (no cached answer)", checks - c0 == 5, $"({checks - c0} checks for 5 transfers)");
            owner = "the iLovbee keyboard software (DeviceDriver.exe) is running";
            int sent = t.LiveCount;
            var r = ctl.SendLive(p, out var why);
            Check("the vendor software appearing between two frames blocks the very next frame", r == KeyboardLightingController.LiveResult.Skipped && t.LiveCount == sent, why);
            owner = null;
            Check("its exit allows the next frame at once (no 1 s cache)", ctl.SendLive(p, out _) == KeyboardLightingController.LiveResult.Sent);
        }

        // the real detector: a process list every 250 ms; live frames read that state, stored writes list processes at the write
        using var presence = new ProcessPresence("PING.EXE");
        var sw = System.Diagnostics.Stopwatch.StartNew(); int n = 0;
        while (sw.ElapsedMilliseconds < 300) { presence.CheckNow(); n++; }
        double listMs = sw.Elapsed.TotalMilliseconds / n;
        sw.Restart(); long reads = 0;
        while (sw.ElapsedMilliseconds < 50) { _ = presence.IsRunning; reads++; }
        Log.Info($"vendor process detection: {presence.Mode}; a process list takes {listMs:0.00} ms ({n} lists), reading the polled state {sw.Elapsed.TotalMilliseconds * 1000 / reads:0.000} us; poll cost {listMs / presence.PollInterval.TotalMilliseconds * 100:0.00}% of one core");
        Check("vendor process detection reports PING.EXE absent before it starts", !presence.IsRunning);
        var ping = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 3 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        var started = System.Diagnostics.Stopwatch.StartNew();
        using (var proc = System.Diagnostics.Process.Start(ping)!)
        {
            bool fresh = presence.CheckNow();
            double freshMs = started.Elapsed.TotalMilliseconds;
            Log.Info($"vendor process detection: fresh list saw the new process after {freshMs:0} ms: {fresh}");
            Check("a stored/test write's fresh check sees a process that just started", fresh);
            proc.WaitForExit(5000);
        }
        Check("...the polled state follows it out after it exits within one poll interval (+list time)", WaitFor(() => !presence.IsRunning, 1000));
        // Polled latency, deterministic part: the real poll timer with a simulated process list (no child process). The
        // vendor app appearing is seen within one poll interval plus a list, whatever the machine or sandbox allows.
        {
            bool vendorUp = false;
            // the detector's own poll timer; only the process list is simulated
            using var sim = new ProcessPresence("vanta-selftest-simulated.exe", presence.PollInterval) { Lister = () => vendorUp };
            WaitFor(() => { _ = sim.IsRunning; return false; }, 400);   // read continuously, as live frames do
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            vendorUp = true;
            bool seen = WaitFor(() => sim.IsRunning, 2000);
            double ms = t0.Elapsed.TotalMilliseconds;
            Check($"live frames read a state at most one poll ({presence.PollInterval.TotalMilliseconds:0} ms) plus a list old (simulated process list, real timer)", seen && ms < presence.PollInterval.TotalMilliseconds + 150, $"({ms:0} ms)");
        }
        // Polled latency, integration part: a real child process. Only meaningful if the child lives longer than one poll:
        // a process that exits within a poll interval can be missed by design (the vendor app runs for minutes). In a
        // sandbox where PING.EXE exits at once this is reported as not applicable, never as a pass.
        var started2 = System.Diagnostics.Stopwatch.StartNew();
        using (var proc2 = System.Diagnostics.Process.Start(ping)!)
        {
            bool seen2 = WaitFor(() => presence.IsRunning, 2000);
            double lat2 = started2.Elapsed.TotalMilliseconds;
            proc2.WaitForExit(5000);
            double lived = proc2.HasExited ? (proc2.ExitTime - proc2.StartTime).TotalMilliseconds : double.PositiveInfinity;
            Log.Info($"vendor process detection: the polled state (what live frames read) saw a new process after {lat2:0} ms; the child lived {lived:0} ms (exit {(proc2.HasExited ? proc2.ExitCode : -1)})");
            double needed = presence.PollInterval.TotalMilliseconds + 150;
            if (lived < needed)
                Warn($"polled-latency integration check not applicable here: the test process lived only {lived:0} ms (< {needed:0} ms), so a {presence.PollInterval.TotalMilliseconds:0} ms poll may miss it by design; the simulated check above covers the contract");
            else
                Check($"a real process is seen by the polled state within one poll ({presence.PollInterval.TotalMilliseconds:0} ms) plus a list", seen2 && lat2 < needed, $"({lat2:0} ms; child lived {lived:0} ms)");
        }
        if (listMs > 8) Warn($"a process list takes {listMs:0.0} ms");
    }
}
