using System.Text.Json;
using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>
/// Source-review findings A-G (keyboard ownership, queued-write cancellation, keyboard cache and reconnect, explicit off,
/// RAM verification, keyboard profile, honest counters). Software only: fakes stand in for the bus and the HID device,
/// but the real sink worker, engine and controllers run.
/// </summary>
public static partial class SelfTest
{
    /// <summary>Records Apply calls; can hold an Apply open to simulate a transfer in progress.</summary>
    sealed class GatedSink : RgbSink
    {
        public readonly ManualResetEventSlim Gate = new(true);
        public readonly List<string> Events = new();
        public int Writes;
        public GatedSink() { Available = true; }
        public override string Name => "gated test sink";
        public override string Kind => "test";
        public override int LedCount => 2;
        public override double[] Positions => new[] { 0.0, 1.0 };
        protected override double MaxHz => 1000;
        public override bool EnabledByDefault => true;
        protected override bool Apply(Rgb[] frame)
        {
            lock (Events) Events.Add("apply-start");
            Gate.Wait(2000);
            Interlocked.Increment(ref Writes);
            lock (Events) Events.Add("apply-end");
            return true;
        }
        public override void Deactivate(Rgb steady) { lock (Events) Events.Add("deactivate"); }
    }

    sealed class FakeKbTransport : IKeyboardTransport
    {
        public readonly List<(KeyboardLightingState s, byte sleep)> Sent = new();
        public ILovbeeProfile? Prof = new(2, 4, "vendor profile 1", true);
        public string Sig = "kb-1";
        public int FailNext;
        public int Opens;
        /// <summary>Links opened at or before this generation fail every write (a stale handle; the device path is unchanged).</summary>
        public int DeadGen = -1;
        /// <summary>Stored writes take this long (to test ordering against live frames).</summary>
        public int StoredDelayMs;
        /// <summary>Runs inside Profile(), i.e. after a stored write was decided and right before its write boundary.</summary>
        public Action? OnProfile;
        /// <summary>Every write in the order it reached the "keyboard": "stored:&lt;brightness&gt;" or "live:&lt;R of the first key&gt;".</summary>
        public readonly List<string> Order = new();
        sealed class L : IKeyboardLink
        {
            readonly FakeKbTransport _t; readonly int _gen;
            public L(FakeKbTransport t, int gen) { _t = t; _gen = gen; }
            public string Describe => "fake";
            public bool Apply(KeyboardLightingState s, byte sleep, out string error)
            {
                if (_t.StoredDelayMs > 0) Thread.Sleep(_t.StoredDelayMs);
                lock (_t.Sent)
                {
                    if (_gen <= _t.DeadGen) { error = "simulated stale handle"; return false; }
                    if (_t.FailNext > 0) { _t.FailNext--; error = "simulated HID failure"; return false; }
                    _t.Sent.Add((s, sleep)); _t.Order.Add("stored:" + s.Brightness); error = ""; return true;
                }
            }
            public bool ApplyLive(byte[] payload, out string error)
            {
                lock (_t.Sent)
                {
                    if (_gen <= _t.DeadGen) { error = "simulated stale handle"; return false; }
                    if (_t.FailNext > 0) { _t.FailNext--; error = "simulated HID failure"; return false; }
                    _t.Live.Add(payload); _t.Order.Add("live:" + payload[2]); error = ""; return true;
                }
            }
            public void Dispose() { }
        }
        public readonly List<byte[]> Live = new();
        public int LiveCount { get { lock (Sent) return Live.Count; } }
        public byte[]? LastLive { get { lock (Sent) return Live.Count > 0 ? Live[^1] : null; } }
        public string Signature() => Sig;
        /// <summary>Phase 11: Open() itself fails while the device stays listed (same path).</summary>
        public volatile bool FailOpen;
        public int OpenCalls;
        public IKeyboardLink? Open(out string status)
        {
            Interlocked.Increment(ref OpenCalls);
            if (Sig.Length == 0) { status = "not connected"; return null; }
            if (FailOpen) { status = "simulated open failure"; return null; }
            int gen = Interlocked.Increment(ref Opens); status = "connected"; return new L(this, gen);
        }
        public ILovbeeProfile? Profile(out string detail) { OnProfile?.Invoke(); detail = Prof == null ? "Sleep timer unknown (test)" : "vendor profile 1 (test)"; return Prof; }
        public int Count { get { lock (Sent) return Sent.Count; } }
        public KeyboardLightingState? Last { get { lock (Sent) return Sent.Count > 0 ? Sent[^1].s : null; } }
    }

    sealed class FakeEne : IEneModules
    {
        public readonly List<EneDimm> Mods = new();
        public readonly Dictionary<byte, Queue<EneOutcome>> Script = new();
        public readonly List<string> Calls = new();
        public FakeEne(params (byte addr, int leds)[] mods)
        {
            foreach (var (a, n) in mods) Mods.Add(new EneDimm { Address = a, LedCount = n, Supported = true });
        }
        public IReadOnlyList<EneDimm> Modules => Mods;
        public void EnterDirect(EneDimm d) => Calls.Add($"direct {d.Address:X2}");
        public int WriteFrame(EneDimm d, ReadOnlySpan<Rgb> leds) { Calls.Add($"frame {d.Address:X2}"); return leds.Length; }
        public EneOutcome SetHardwareEffect(EneDimm d, byte mode, Rgb color, byte speed, out string detail)
        {
            Calls.Add($"effect {d.Address:X2} m{mode}");
            var o = Script.TryGetValue(d.Address, out var q) && q.Count > 0 ? q.Dequeue() : EneOutcome.Confirmed;
            detail = o.ToString();
            return o;
        }
        public void SetStaticLook(EneDimm d, Rgb color) => Calls.Add($"static {d.Address:X2}");
        public int Count(string prefix) => Calls.Count(c => c.StartsWith(prefix));
    }

    static bool WaitFor(Func<bool> cond, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (cond()) return true; Thread.Sleep(20); }
        return cond();
    }

    static void ReliabilityTests()
    {
        QueueCancellationTests();
        KeyboardControllerTests();
        KeyboardProfileTests();
        RamReliabilityTests();
    }

    // ---- B: queued writes after exclusion, ownership loss, output stop and suspend ------------------------------------
    static void QueueCancellationTests()
    {
        var f = new[] { new Rgb(10, 20, 30), new Rgb(40, 50, 60) };
        var s = new GatedSink();
        s.Post(f); s.Enabled = false;
        Check("B: a frame queued before the device is excluded never reaches the hardware", !s.DrainOnceForTest() && s.Writes == 0 && s.Cancelled == 1);
        s = new GatedSink();
        s.Post(f); s.Blocked = "another program owns it";
        Check("B: a frame queued before ownership is lost never reaches the hardware", !s.DrainOnceForTest() && s.Writes == 0 && s.Cancelled == 1);
        s = new GatedSink();
        s.Post(f); s.Suspend();
        long posted = s.FramesPosted;
        s.Post(f);
        Check("B: suspend cancels queued work and refuses new work", !s.DrainOnceForTest() && s.Writes == 0 && s.FramesPosted == posted);
        s.Resume(); s.Post(f);
        Check("B: work is accepted again after resume", s.DrainOnceForTest() && s.Writes == 1);

        // a transfer already running completes (it cannot be recalled), is counted, and can be waited for
        var g = new GatedSink();
        g.StartWorker();
        g.Gate.Reset();
        g.Post(f);
        WaitFor(() => { lock (g.Events) return g.Events.Contains("apply-start"); });
        bool inFlight = g.Invalidate("test");
        bool idleEarly = g.WaitIdle(TimeSpan.FromMilliseconds(50));
        g.Post(new[] { new Rgb(1, 2, 3), new Rgb(4, 5, 6) });   // queued behind the running transfer
        g.Enabled = false;                                      // then excluded
        g.Gate.Set();
        bool idle = g.WaitIdle(TimeSpan.FromSeconds(2));
        Thread.Sleep(150);
        Check("B: an in-flight transfer is reported, completes, and is waited for with a bound", inFlight && !idleEarly && idle && g.InFlightAtInvalidate >= 1, $"(writes {g.Writes}, in-flight invalidations {g.InFlightAtInvalidate})");
        Check("B: the frame queued behind it was cancelled, not written", g.Writes == 1 && g.Cancelled >= 1);
        g.Dispose();

        // engine integration: stopping output waits for the running transfer before the steady look; suspend stops all writes
        var cfg = new AppConfig();
        cfg.Rgb.Enabled = true; cfg.Rgb.Effect = EffectKind.GradientWave; cfg.Rgb.DLedStrip = cfg.Rgb.Motherboard = cfg.Rgb.Ram = cfg.Rgb.Mouse = cfg.Rgb.Keyboard = false;
        var es = new GatedSink();
        using (var engine = new RgbEngine(cfg, () => null))
        {
            engine.Sinks.Add(es);
            engine.Start();
            WaitFor(() => es.Writes > 5);
            es.Gate.Reset();
            WaitFor(() => { lock (es.Events) return es.Events.Count > 0 && es.Events[^1] == "apply-start"; });
            cfg.Rgb.Enabled = false;
            Thread.Sleep(120);
            es.Gate.Set();
            WaitFor(() => { lock (es.Events) return es.Events.Contains("deactivate"); });
            List<string> ev; lock (es.Events) ev = es.Events.ToList();
            int lastEnd = ev.LastIndexOf("apply-end"), deact = ev.IndexOf("deactivate");
            Check("B: output off writes the steady look only after the running transfer finished", deact > lastEnd && lastEnd >= 0, $"(events {string.Join(",", ev.TakeLast(4))})");
            int afterOff = es.Writes;
            Thread.Sleep(300);
            Check("B: nothing is written after output stops", es.Writes == afterOff);

            cfg.Rgb.Enabled = true;
            WaitFor(() => es.Writes > afterOff + 5);
            engine.Suspend();
            int atSuspend = es.Writes;
            Thread.Sleep(400);
            Check("B: no frame is written after suspend returns", es.Writes == atSuspend, $"({es.Writes - atSuspend} after)");
            engine.Resume();
            Thread.Sleep(800);
            Check("B: no resume burst: nothing is written during the resume grace period", es.Writes == atSuspend);
            cfg.Rgb.Enabled = false;
            Thread.Sleep(150);
        }
    }

    // ---- A and C: keyboard ownership at the write boundary, cache, reconnect, vendor handoff, verification ---------------
    static void KeyboardControllerTests()
    {
        var blue = KeyboardLightingState.From(new RgbConfig { Enabled = true, Effect = EffectKind.Static, ColorA = 0x0000FF }, 4)!;
        var green = KeyboardLightingState.From(new RgbConfig { Enabled = true, Effect = EffectKind.Static, ColorA = 0x00FF00 }, 4)!;
        var red = KeyboardLightingState.TestRed(KeyboardLightingState.ModeSteady, 4);
        string? owner = "the iLovbee keyboard software (DeviceDriver.exe) is running";
        bool verified = false;
        var t = new FakeKbTransport();
        string? lastTest = null;
        using (var ctl = new KeyboardLightingController(t, () => verified, () => owner, listenForDeviceChanges: false) { Debounce = TimeSpan.FromMilliseconds(80) })
        {
            ctl.TestFinished += (_, r) => lastTest = r;
            // A: lighting output off (no frames, so the engine never passes an ownership decision): the test still checks
            ctl.RequestTestWrite(red);
            WaitFor(() => lastTest != null);
            Check("A: the test write checks ownership itself and never fights DeviceDriver.exe (lighting off)", t.Count == 0 && lastTest?.Contains("DeviceDriver") == true, lastTest ?? "");
            owner = null; lastTest = null;
            ctl.RequestTestWrite(red);
            WaitFor(() => lastTest != null);
            Check("A: with the vendor software closed, the test sends exactly one write with the profile's sleep byte", t.Count == 1 && t.Sent[0].sleep == 2, lastTest ?? "");
            Check("C: after a test the cached state is what the keyboard shows (red), not a stale earlier setting", Equals(ctl.LastApplied, red));

            ctl.SetDesired(blue);
            Thread.Sleep(400);
            Check("before verification, settings changes send nothing", t.Count == 1, ctl.Status);
            verified = true;
            Thread.Sleep(400);
            Check("C: after a test, sync waits while the owner looks at the result", t.Count == 1, ctl.Status);
            ctl.EndTestHold();
            WaitFor(() => t.Count == 2);
            Check("C: once the owner answers, the effect's colour is restored (red -> blue)", t.Count == 2 && Equals(t.Last, blue), ctl.Status);
            Check("C: a vendor sighting before a successful test does not cause an extra stored write", ctl.Resyncs == 0, $"resyncs {ctl.Resyncs}");

            // C: a test after syncing must not leave "up to date: blue" while the keyboard shows red
            lastTest = null;
            ctl.RequestTestWrite(red);
            WaitFor(() => lastTest != null);
            ctl.EndTestHold();
            WaitFor(() => t.Count == 4);
            Check("C: a red test after blue sync is followed by one re-sync to blue", t.Count == 4 && Equals(t.Last, blue));

            // C: vendor handoff - while it ran, nothing; after it exits, exactly one re-sync
            owner = "the iLovbee keyboard software (DeviceDriver.exe) is running";
            ctl.SetDesired(green);
            Thread.Sleep(500);
            Check("C: nothing is written while the vendor software owns the keyboard", t.Count == 4, ctl.Status);
            long resyncsBefore = ctl.Resyncs;
            ctl.SetDesired(blue);   // back to what it last had
            owner = null;
            WaitFor(() => t.Count == 5, 8000);
            Thread.Sleep(400);
            Check("C: after the vendor software exits, one re-sync (it may have changed the keyboard)", t.Count == 5 && ctl.Resyncs - resyncsBefore == 1, $"{ctl.Status}; resyncs {ctl.Resyncs - resyncsBefore}");

            // C: reconnection: one bounded re-sync even though the wanted setting did not change
            t.Sig = "kb-2";
            ctl.NotifyDeviceChange();
            WaitFor(() => t.Count == 6, 5000);
            Thread.Sleep(400);
            Check("C: a genuine reconnection re-applies the setting once", t.Count == 6 && ctl.Reconnects == 1, ctl.Status);

            // C: failed writes are bounded; the latch clears on change
            t.FailNext = 10;
            ctl.SetDesired(green);
            WaitFor(() => ctl.Status.StartsWith("gave up"), 15000);
            Check("C: failed writes stop after 3 attempts", ctl.Failures == 3 && ctl.Status.StartsWith("gave up"), ctl.Status);
            t.FailNext = 0;
            ctl.SetDesired(blue);
            WaitFor(() => Equals(t.Last, blue) && t.Count == 7, 5000);
            Check("C: a new setting clears the give-up latch", t.Count == 7);

            // verification and re-lock
            verified = false;
            ctl.SetDesired(green);
            Thread.Sleep(400);
            Check("re-locking stops sync immediately", t.Count == 7, ctl.Status);
            ctl.SetSuspended(true); verified = true; lastTest = null;
            ctl.RequestTestWrite(red);
            WaitFor(() => lastTest != null);
            Thread.Sleep(300);
            Check("nothing is written while the system is suspending (not even a test)", t.Count == 7 && lastTest?.Contains("sleeping") == true, lastTest ?? "");
            ctl.SetSuspended(false);
        }

        // restart: the persisted last setting prevents a redundant stored write
        var t2 = new FakeKbTransport();
        using (var ctl2 = new KeyboardLightingController(t2, () => true, () => null, lastApplied: blue, listenForDeviceChanges: false) { Debounce = TimeSpan.FromMilliseconds(50) })
        {
            ctl2.SetDesired(blue);
            Thread.Sleep(600);
            Check("C: after a restart an unchanged setting is not written again", t2.Count == 0, ctl2.Status);
        }

        // capability mapping (the preview uses the same mapping)
        var c = new RgbConfig { Enabled = true, ColorA = 0x112233, Brightness = 1 };
        c.Effect = EffectKind.Static; var k = KeyboardLightingState.From(c, 4)!;
        Check("keyboard Static = Steady primary colour (exact)", k.Mode == 16 && k.Sync == KeyboardSync.Exact && k.Color == new Rgb(0x11, 0x22, 0x33));
        c.Effect = EffectKind.Breathing; k = KeyboardLightingState.From(c, 4)!;
        Check("keyboard Breathing = native breathing, labelled as its own timing", k.Mode == 17 && k.Sync == KeyboardSync.Native);
        c.Effect = EffectKind.GradientWave; k = KeyboardLightingState.From(c, 4)!;
        Check("keyboard Gradient Wave = fixed primary colour by default (labelled fallback)", k.Mode == 16 && k.Sync == KeyboardSync.Fallback);
        c.KeyboardWaveMode = 3; k = KeyboardLightingState.From(c, 4)!;
        Check("keyboard Gradient Wave can use a native mode, labelled not synchronised", k.Mode == 3 && k.Sync == KeyboardSync.Native && k.Description.Contains("not synchronised"));
        c.KeyboardWaveMode = 13; k = KeyboardLightingState.From(c, 4)!;
        Check("per-key Customize is never used as a wave stand-in", k.Mode == 16);
        c.Effect = EffectKind.ThermalReactive; k = KeyboardLightingState.From(c, 4)!;
        Check("keyboard Thermal = fixed primary colour (labelled fallback)", k.Sync == KeyboardSync.Fallback);
        c.Effect = EffectKind.Stealth; k = KeyboardLightingState.From(c, 4)!;
        Check("D: keyboard Stealth / Lights out = brightness 0", k.Brightness == 0 && k.Sync == KeyboardSync.Off);
        c.Enabled = false;
        Check("preview mapping ignores the output switch but nothing is wanted while output is off",
            KeyboardLightingState.From(c, 4) == null && KeyboardLightingState.From(c, 4, ignoreOutputSwitch: true) != null);
    }

    // ---- F: the sleep timer comes from the vendor's active profile, or from an explicit choice ------------------------
    static void KeyboardProfileTests()
    {
        byte[] Profiles(uint[] ids, int[] sleeps)
        {
            var b = new byte[ILovbeeProfile.ProfileSize * 3];
            for (int p = 0; p < 3; p++)
            {
                int o = p * ILovbeeProfile.ProfileSize;
                BitConverter.GetBytes(ids[p]).CopyTo(b, o + ILovbeeProfile.IdOffset);
                BitConverter.GetBytes(sleeps[p]).CopyTo(b, o + ILovbeeProfile.SleepOffset);
                BitConverter.GetBytes(16).CopyTo(b, o + ILovbeeProfile.ModeOffset);
                b[o + ILovbeeProfile.RecordsOffset + 16 * ILovbeeProfile.RecordSize] = (byte)(p + 2);
            }
            return b;
        }
        var ids = new uint[] { 0x8B11F6, 0x8B1115, 0x8B1134 };
        var bytes = Profiles(ids, new[] { 1, 3, 5 });
        var p2 = ILovbeeProfile.Parse(bytes, 0x8B1115, out var d2);
        Check("F: the active profile is the one whose ID matches curid.dct (as the vendor app selects it)", p2 is { Sleep: 3, Speed: 3, FromVendorProfile: true } && d2.Contains("profile 2"), d2);
        var p1 = ILovbeeProfile.Parse(bytes, 0x12345678, out var d1);
        Check("F: no curid match falls back to the first profile, like the vendor app", p1?.Sleep == 1 && d1.Contains("first profile"), d1);
        Check("F: a profile of the wrong size is not trusted", ILovbeeProfile.Parse(new byte[100], null, out _) == null);
        var bad = Profiles(ids, new[] { 9, 3, 5 });
        Check("F: out-of-range profile values are not trusted", ILovbeeProfile.Parse(bad, null, out _) == null);
        var missing = Path.Combine(Path.GetTempPath(), "vanta-no-such-dir-" + Guid.NewGuid().ToString("N"), "pro.dct");
        var none = ILovbeeProfile.Read(missing, null, out var dn);
        Check("F: no vendor profile and no choice: nothing is written, and the reason is shown", none == null && dn.StartsWith("Sleep timer unknown"), dn);
        var chosen = ILovbeeProfile.Read(missing, 3, out var dc);
        Check("F: an explicit choice is used and reported as a choice, not as preserved", chosen is { Sleep: 3, FromVendorProfile: false } && dc.Contains("your choice") && !dc.Contains("kept"), dc);
        var real = ILovbeeProfile.Read(null, null, out var rd);
        Check("F: the owner's vendor profile reads (read only)", real != null || !File.Exists(ILovbeeProfile.ResolvePath()), rd);
    }

    // ---- D, E, G: explicit off, RAM onboard confirmation and retries, honest counters --------------------------------
    static void RamReliabilityTests()
    {
        Check("D: an LED going from 1 to 0 is always written (no perceptual skip on off)", EneDramController.NeedsWrite(new byte[] { 1, 1, 1 }, new byte[] { 0, 0, 0 }));
        Check("D: a single channel going dark is written", EneDramController.NeedsWrite(new byte[] { 200, 1, 0 }, new byte[] { 200, 0, 0 }));
        Check("D: tiny changes that stay lit are still skipped", !EneDramController.NeedsWrite(new byte[] { 100, 1, 0 }, new byte[] { 101, 2, 0 }));

        var ene = new FakeEne((0x39, 5), (0x3A, 8));
        var cfg = new RgbConfig { Enabled = true, RamOnboardEffects = true, Effect = EffectKind.Breathing };
        var ram = new RamSink(cfg, ene);
        ram.Activate();
        var frame = new Rgb[13];
        ene.Script[0x3A] = new Queue<EneOutcome>(new[] { EneOutcome.Unconfirmed, EneOutcome.Unconfirmed, EneOutcome.Unconfirmed });
        void Offer() { frame[0] = new Rgb((byte)(frame[0].R + 7), 0, 0); ram.Post(frame); ram.DrainOnceForTest(); }
        Offer();
        Check("E: the onboard effect is written to every module", ene.Count("effect 39") == 1 && ene.Count("effect 3A") == 1);
        Offer();
        Check("E: only the module that did not confirm is retried", ene.Count("effect 39") == 1 && ene.Count("effect 3A") == 2);
        Offer(); Offer(); Offer();
        Check("E: retries are bounded (3 attempts), then the problem is reported", ene.Count("effect 3A") == 3 && ram.OnboardProblem != null, ram.OnboardProblem ?? "");
        Check("G: cycles that send nothing count as no-write, not as hardware writes", ram.NoWrite >= 2 && ram.FramesSent == 3 && ram.SettingsUnconfirmed == 3, $"(written {ram.FramesSent}, no-write {ram.NoWrite})");
        cfg.Speed = 0.1;   // a different onboard setting: confirmations and attempts start over
        Offer();
        Check("E: a changed setting is written to every module again", ene.Count("effect 39") == 2 && ene.Count("effect 3A") == 4 && ram.OnboardProblem == null);

        // all confirmed: steady state sends nothing
        var ene2 = new FakeEne((0x39, 5), (0x3A, 8));
        var cfg2 = new RgbConfig { Enabled = true, RamOnboardEffects = true, Effect = EffectKind.GradientWave };
        var ram2 = new RamSink(cfg2, ene2);
        ram2.Activate();
        var f2 = new Rgb[13];
        for (int i = 0; i < 5; i++) { f2[0] = new Rgb((byte)(i * 9 + 1), 0, 0); ram2.Post(f2); ram2.DrainOnceForTest(); }
        Check("G: onboard steady state: 1 hardware write, then no-write (0 SMBus traffic)", ene2.Count("effect") == 2 && ram2.FramesSent == 1 && ram2.NoWrite == 4);
        cfg2.RamOnboardEffects = false;
        ram2.Post(f2); ram2.DrainOnceForTest();
        Check("E: onboard -> streamed re-enters direct mode on every module before the next frame",
            ene2.Calls.Skip(2).Take(4).SequenceEqual(new[] { "direct 39", "direct 3A", "frame 39", "frame 3A" }) || ene2.Count("direct") >= 4, string.Join(",", ene2.Calls));
        cfg2.Effect = EffectKind.Stealth; cfg2.RamOnboardEffects = true;
        ram2.Post(new Rgb[13]); ram2.DrainOnceForTest();
        Check("D: Stealth in onboard mode switches the modules off (ENE mode 0)", ene2.Calls.Count(c => c.EndsWith("m0")) == 2);

        // Lights out reporting is honest about devices it cannot switch off
        var zones = new List<LightsOut.Zone>
        {
            new("RAM", true, true, null, true), new("Case strip", true, true, null, true), new("Board", false, true, null, true),
            new("Mouse", true, false, null, true), new("Keyboard", true, true, null, false), new("Keyboard2", true, true, "DeviceDriver.exe running", true)
        };
        var rep = LightsOut.Summarize(zones);
        Check("D: Lights out reports which zones are actually switched off and why others are not",
            rep.Off == 2 && rep.Lines.Any(l => l.Contains("Board") && l.Contains("not included")) && rep.Lines.Any(l => l.Contains("Mouse") && l.Contains("not connected"))
            && rep.Lines.Any(l => l.Contains("Keyboard") && l.Contains("not verified")) && rep.Lines.Any(l => l.Contains("DeviceDriver")), string.Join(" | ", rep.Lines));
    }
}
