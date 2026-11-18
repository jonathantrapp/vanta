using System.Text.Json;
using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;
using HwSuite.Hardware;

namespace HwSuite.Cli;

/// <summary>Phase 8 CPU work: RAM perceptual skip, RAM rate setting, still-effect posting. Software only.</summary>
public static partial class SelfTest
{
    sealed class CountingSink : RgbSink
    {
        public int Posts;
        public CountingSink() { Available = true; }
        public override string Name => "counting sink";
        public override string Kind => "test";
        public override int LedCount => 4;
        public override double[] Positions => new[] { 0.0, 0.25, 0.5, 0.75 };
        protected override double MaxHz => 60;
        public override bool EnabledByDefault => true;
        public override void StartWorker() { }
        public override void Post(Rgb[] frame) => Interlocked.Increment(ref Posts);
        protected override bool Apply(Rgb[] frame) => true;
        public override void Dispose() { }
    }


    static void AdditionTests()
    {
        // --- Lights out: one toggle, exact restore, survives a restart
        var cfg = new AppConfig();
        cfg.Rgb.Enabled = false; cfg.Rgb.Effect = EffectKind.GradientWave;
        bool off = LightsOut.Toggle(cfg);
        Check("Lights out switches every zone to Stealth (and turns output on so the LEDs go dark)", off && cfg.Rgb.Effect == EffectKind.Stealth && cfg.Rgb.Enabled);
        var reloaded = ConfigStore.Parse(JsonSerializer.Serialize(cfg))!;
        Check("the restore point survives a restart", reloaded.Rgb.LightsOutRestoreEffect == EffectKind.GradientWave && reloaded.Rgb.LightsOutRestoreEnabled == false);
        bool off2 = LightsOut.Toggle(reloaded);
        Check("the second press restores the previous effect and output state exactly", !off2 && reloaded.Rgb.Effect == EffectKind.GradientWave && !reloaded.Rgb.Enabled && reloaded.Rgb.LightsOutRestoreEffect == null);
        var st = new AppConfig(); st.Rgb.Enabled = true; st.Rgb.Effect = EffectKind.Stealth;
        Check("pressing it while Stealth is already on restores to Static", !LightsOut.Toggle(st) && st.Rgb.Effect == EffectKind.Static);
        Check("hotkey Ctrl+Shift+F12 parses (MOD_CONTROL|MOD_SHIFT|MOD_NOREPEAT, VK_F12)", LightsOut.TryParseHotkey("Ctrl+Shift+F12", out var m, out var vk) && m == 0x4006 && vk == 0x7B);
        Check("a hotkey without a modifier, or with two keys, is refused", !LightsOut.TryParseHotkey("F12", out _, out _) && !LightsOut.TryParseHotkey("Ctrl+A+B", out _, out _));

        // --- RAM onboard effects
        var rc = new RgbConfig { RamOnboardEffects = false, Effect = EffectKind.Breathing };
        Check("streamed mode never uses an onboard effect", RamSink.OnboardFor(rc) == null);
        rc.RamOnboardEffects = true; rc.Speed = 1; rc.Brightness = 0.5; rc.ColorA = 0x38BDF8;
        Check("onboard Breathing = ENE mode 2, fastest speed 0, colour scaled by brightness",
            RamSink.OnboardFor(rc) is { mode: 2, speed: 0 } ob && ob.color == new Rgb(0x1C, 0x5E, 0x7C));
        rc.Effect = EffectKind.GradientWave; rc.Speed = 0;
        Check("onboard Gradient Wave = ENE spectrum-cycle wave (mode 11), slowest speed 4", RamSink.OnboardFor(rc) is { mode: 11, speed: 4 });
        rc.Effect = EffectKind.Stealth;
        Check("onboard Stealth = ENE mode 0 (off)", RamSink.OnboardFor(rc) is { mode: 0 });
        rc.Effect = EffectKind.Static;
        Check("Static and Thermal stay streamed in onboard mode (still frames cost nothing)", RamSink.OnboardFor(rc) == null && RamSink.OnboardFor(new RgbConfig { RamOnboardEffects = true, Effect = EffectKind.ThermalReactive }) == null);
        Check("ENE speed scale: 0 = fastest .. 4 = slowest", EneDramController.SpeedFor(1) == 0 && EneDramController.SpeedFor(0) == 4 && EneDramController.SpeedFor(0.5) == 2);

        // --- defaults and swatches
        var fresh = new RgbConfig();
        Check("fresh configs default to ice-cyan to deep indigo", fresh.ColorA == 0x38BDF8 && fresh.ColorB == 0x6366F1 && !fresh.RamOnboardEffects);
        var sw = new AppConfig(); sw.Rgb.SavedColors.Add(0x123456);
        Check("saved swatch colours survive save and load", ConfigStore.Parse(JsonSerializer.Serialize(sw))!.Rgb.SavedColors.SequenceEqual(new[] { 0x123456 }));
    }

    static void Phase8Tests()
    {
        ReliabilityTests();
        AdditionTests();

        // --- ENE RAM: byte order and perceptual skip
        var packed = EneDramController.Pack(new[] { new Rgb(1, 2, 3), new Rgb(10, 20, 30) });
        Check("RAM frames are packed R, B, G per LED", packed.SequenceEqual(new byte[] { 1, 3, 2, 10, 30, 20 }));
        var written = new byte[] { 100, 100, 100 };
        Check("RAM: the first frame after activation is always written", EneDramController.NeedsWrite(null, written));
        Check("RAM: a change of 1 on every channel is skipped", !EneDramController.NeedsWrite(written, new byte[] { 101, 99, 101 }));
        Check("RAM: a change of 2 on one channel is written", EneDramController.NeedsWrite(written, new byte[] { 100, 102, 100 }));
        Check("RAM: slow fades still go out (compared with the last frame written, not the last offered)",
            !EneDramController.NeedsWrite(written, new byte[] { 101, 100, 100 }) && EneDramController.NeedsWrite(written, new byte[] { 102, 100, 100 }));
        Check("RAM: a different LED count is always written", EneDramController.NeedsWrite(written, new byte[] { 100, 100, 100, 0, 0, 0 }));

        // --- RAM rate setting
        Check("RAM animation rate defaults to 10/s for new and older configs",
            new RgbConfig().EffectiveRamHz == 10 && JsonSerializer.Deserialize<RgbConfig>("{\"DLedMaxHz\":60}")!.EffectiveRamHz == 10);
        Check("RAM rate is clamped to 4..20/s",
            new RgbConfig { RamMaxHz = 0 }.EffectiveRamHz == 4 && new RgbConfig { RamMaxHz = 500 }.EffectiveRamHz == 20);
        var saved = ConfigStore.Parse(JsonSerializer.Serialize(new AppConfig { Rgb = { RamMaxHz = 15 } }));
        Check("RAM rate choice survives save and load", saved!.Rgb.EffectiveRamHz == 15);

        // --- data folder: one config for CLI runs and the real app
        Check("admin-share path mapping", AppPaths.ToAdminShare(@"C:\Users\x\AppData\Roaming\HwSuite") == @"\\localhost\C$\Users\x\AppData\Roaming\HwSuite"
            && AppPaths.ToAdminShare(@"\\server\share\x") == null);
        if (AppPaths.Isolated)
        {
            // hardware-free run: the owner's profile folder is not touched at all, so the redirect check is not made here
            Check("hardware-free run uses its isolated data folder, not the owner's profile", AppPaths.Data == RunMode.IsolatedData
                && !AppPaths.Data.Contains(Path.Combine("AppData", "Roaming", "HwSuite"), StringComparison.OrdinalIgnoreCase), $"({AppPaths.Data})");
        }
        else
        {
            bool viaShare = AppPaths.Data.StartsWith(@"\\");
            bool sameAsProfile = viaShare;
            string probeNote = "";
            if (!viaShare)
            {
                // a probe that cannot be written is a failed check, never an exception out of the self-test
                var probeFile = Path.Combine(AppPaths.Data, ".selftest-" + Guid.NewGuid().ToString("N"));
                try
                {
                    File.WriteAllText(probeFile, "x");
                    var share = AppPaths.ToAdminShare(probeFile);
                    sameAsProfile = share == null || !Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(share))) || File.Exists(share);
                }
                catch (Exception ex) { sameAsProfile = false; probeNote = $"; probe file not written: {ex.GetType().Name}: {ex.Message}"; }
                finally { try { File.Delete(probeFile); } catch { } }
            }
            Check("the data folder is the real profile folder (not a packaged-app redirect)", sameAsProfile,
                $"({AppPaths.Data}{(AppPaths.ResolutionNote != null ? "; " + AppPaths.ResolutionNote : "")}{probeNote})");
        }

        Check("test runs never write the owner's config.json", ConfigStore.ReadOnly);

        // --- still effects: render at 10/s and do not repost identical frames (heartbeat once a second)
        var cfg = new AppConfig();
        cfg.Rgb.Enabled = true; cfg.Rgb.Effect = EffectKind.Static; cfg.Rgb.DLedStrip = cfg.Rgb.Motherboard = cfg.Rgb.Ram = cfg.Rgb.Mouse = false;
        var sink = new CountingSink();
        using (var engine = new RgbEngine(cfg, () => null))
        {
            engine.Sinks.Add(sink);
            engine.Start();
            Thread.Sleep(2100);
            int stillPosts = sink.Posts; long stillTicks = engine.Ticks; int stillHz = engine.RenderHz;
            Check("Static renders at 10/s", stillHz == RgbEngine.StillHz && stillTicks is >= 15 and <= 25, $"({stillTicks} renders in 2.1 s)");
            Check("Static posts an unchanged frame only once a second", stillPosts is >= 2 and <= 4, $"({stillPosts} posts in 2.1 s)");
            cfg.Rgb.Effect = EffectKind.GradientWave;
            Thread.Sleep(1000);
            int animPosts = sink.Posts - stillPosts;
            Check("animated effects return to 60/s at once", engine.RenderHz == 60 && animPosts is >= 45, $"({animPosts} posts in 1 s)");
            cfg.Rgb.Enabled = false;
            Thread.Sleep(200);
        }
    }
}
