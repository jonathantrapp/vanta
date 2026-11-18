using System.Runtime.InteropServices;
using HwSuite.Cli;
using HwSuite.Core;
using HwSuite.Lighting.Devices;
using Size = System.Windows.Size;
using HwSuite.Fans;

namespace HwSuite;

public static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
    /// <summary>Lets the running instance take the foreground when this (user-started) launch hands over to it.</summary>
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    const int AttachParentProcess = -1;

    static void EnableConsole()
    {
        AttachConsole(AttachParentProcess);
        try
        {
            var o = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(o);
            Console.SetError(o);
        }
        catch { }
        Log.ConsoleEnabled = true;
    }

    /// <summary>One process at a time may own the hardware: the GUI, or one hardware-touching command-line mode.</summary>
    public const string InstanceMutexName = @"Global\HwSuite.SingleInstance";
    /// <summary>Set by a second launch (Start menu, shortcut) to bring the running window forward instead of starting again.</summary>
    public const string ShowWindowEventName = @"Local\VANTA.ShowWindow";

    /// <summary>Take the instance lock for a hardware CLI mode; prints why and returns null if the GUI or another test holds it.</summary>
    static Mutex? AcquireHardwareLock(string mode)
    {
        var m = new Mutex(false, InstanceMutexName);
        bool got;
        try { got = m.WaitOne(0); } catch (AbandonedMutexException) { got = true; }
        if (got) return m;
        m.Dispose();
        Console.WriteLine($"{mode}: VANTA (the app or another hardware test) is running and owns the hardware. Close it first.");
        return null;
    }

    /// <summary>
    /// --fan-swap preview|apply|undo: the settings side of the 2026-10 fan swap (Core\FanSwap.cs). preview only prints;
    /// apply and undo refuse while VANTA runs (it would overwrite the file on exit), and apply writes a backup first.
    /// No hardware is touched.
    /// </summary>
    static int FanSwapCommand(string action)
    {
        EnableConsole();
        if (action is not ("preview" or "apply" or "undo")) { Console.WriteLine("usage: --fan-swap preview | apply | undo"); return 2; }
        string dir = Path.GetDirectoryName(AppPaths.Config)!;
        Console.WriteLine($"settings: {AppPaths.Config}");
        if (action == "preview")
        {
            if (!File.Exists(AppPaths.Config)) { Console.WriteLine("no settings file yet: nothing to change"); return 0; }
            var copy = ConfigStore.Parse(File.ReadAllText(AppPaths.Config));
            if (copy == null) { Console.WriteLine("the settings file could not be read: nothing changed"); return 1; }
            var changes = FanSwap.Prepare(copy);
            Console.WriteLine(changes.Count == 0 ? "already prepared: nothing would change" : "would change (nothing written; run --fan-swap apply):");
            foreach (var c in changes) Console.WriteLine("  - " + c);
            return 0;
        }

        bool othersRunning = System.Diagnostics.Process.GetProcessesByName("HwSuite").Any(p => p.Id != Environment.ProcessId);
        Mutex? hw = null;
        try { if (!othersRunning) hw = AcquireHardwareLock("--fan-swap " + action); }
        catch (UnauthorizedAccessException) { hw = null; }
        if (othersRunning || hw == null)
        {
            if (othersRunning) Console.WriteLine($"--fan-swap {action}: VANTA is running. Quit it from the tray first (its settings would overwrite this change).");
            return 3;
        }
        using (hw)
        {
            if (action == "apply")
            {
                if (!File.Exists(AppPaths.Config)) { Console.WriteLine("no settings file yet: nothing to change"); return 0; }
                string json = File.ReadAllText(AppPaths.Config);
                var cfg = ConfigStore.Parse(json);
                if (cfg == null) { Console.WriteLine("the settings file could not be read: nothing changed"); return 1; }
                var changes = FanSwap.Prepare(cfg);
                if (changes.Count == 0) { Console.WriteLine("already prepared: nothing changed"); return 0; }
                string backup = Path.Combine(dir, FanSwap.BackupName(DateTime.Now));
                File.WriteAllText(backup, json);
                ConfigStore.Save(cfg);
                var check = ConfigStore.Parse(File.ReadAllText(AppPaths.Config));
                bool ok = check != null && FanSwap.Prepare(check).Count == 0;
                Log.Info($"fan swap applied ({changes.Count} changes); backup {backup}");
                Console.WriteLine($"backup: {backup}");
                foreach (var c in changes) Console.WriteLine("  - " + c);
                Console.WriteLine(ok ? "saved and read back: prepared" : "WARNING: the saved settings did not read back as prepared; restore with --fan-swap undo");
                return ok ? 0 : 1;
            }
            // undo: the newest backup becomes the settings again; the current file is kept beside it
            var latest = Directory.GetFiles(dir, FanSwap.BackupPattern).OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
            if (latest == null) { Console.WriteLine("no fan-swap backup found: nothing changed"); return 1; }
            if (ConfigStore.Parse(File.ReadAllText(latest)) == null) { Console.WriteLine($"{latest} could not be read: nothing changed"); return 1; }
            if (File.Exists(AppPaths.Config)) File.Copy(AppPaths.Config, AppPaths.Config + ".before-fan-swap-undo", true);
            File.Copy(latest, AppPaths.Config, true);
            Log.Info($"fan swap undone from {latest}");
            Console.WriteLine($"restored {latest} (the replaced settings were kept as config.json.before-fan-swap-undo)");
            return 0;
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // Hardware-free runs are decided before anything touches AppPaths: they get an isolated data folder and log,
        // and every hardware, recovery, watchdog and service boundary refuses (RunMode).
        if (RunMode.IsHardwareFreeCommand(args))
        {
            RunMode.EnterHardwareFree(OptionValue(args, "--test-data") ?? RunMode.DefaultIsolatedFolder());
            ConfigStore.ReadOnly = true;
        }
        try { return MainCore(args); }
        catch (Exception ex) when (RunMode.HardwareFree)
        {
            // a hardware-free run owns nothing: no recovery, no watchdog, no service restore
            try { Log.Error("fatal (hardware-free run; no recovery attempted, nothing was claimed)", ex); } catch { }
            try { Console.Error.WriteLine("fatal: " + ex.Message); } catch { }
            return 1;
        }
    }

    /// <summary>Hardware-free runs only: --inject-fault startup|teardown|ui throws there, to prove failures stay isolated.</summary>
    static void InjectFault(string[] args, string point)
    {
        if (RunMode.HardwareFree && OptionValue(args, "--inject-fault") == point)
            throw new InvalidOperationException($"injected test fault at {point}");
    }

    /// <summary>After EnableConsole: where a hardware-free run keeps its files, and whether that folder is usable.</summary>
    static void LogIsolation()
    {
        if (!RunMode.HardwareFree) return;
        if (AppPaths.ResolutionNote != null) Log.Warn($"hardware-free run: {AppPaths.ResolutionNote}");
        else Log.Info($"hardware-free run: isolated data folder and log {AppPaths.Data}");
    }

    static int MainCore(string[] args)
    {
        Log.RotateIfLarge();
        if (!RunMode.HardwareFree && AppPaths.ResolutionNote is string note) Log.Warn("data folder: " + note);
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (mode is "--diagnose" or "--probe-fan" or "--lighting-test" or "--case-check" or "--hid-bench" or "--dled-rate-bench" or "--ene-probe"
            or "--cpu-bench" or "--fan-census" or "--fan-step" or "--fan-min-test" or "--fan-engine-check" or "--keyboard-live-test" or "--keyboard-transition-test" or "--fan-start-check" or "--sync-test" or "--lights-out-test" or "--keyboard-test" or "--identify-test" or "--engine-test" or "--mouse-test" or "--mouse-probe" or "--mouse-stream" or "--ui-shot" or "--ui-fixtures" or "--selftest" or "--keyboard-probe" or "--al80")
            ConfigStore.ReadOnly = true;   // test modes never write the owner's config.json
        try
        {
            switch (mode)
            {
                case "--watchdog":
                    return FailsafeWatchdog.RunWatchdog(args.Length > 1 && int.TryParse(args[1], out var pid) ? pid : 0);
                case "--fan-swap":
                    return FanSwapCommand(args.Length > 1 ? args[1].ToLowerInvariant() : "preview");
                case "--restore":
                {
                    EnableConsole();
                    // exit 0: nothing claimed, or every claimed channel handed back (motherboard headers: 100% until a reboot);
                    // 3: VANTA or a hardware test holds the hardware; 4: at least one channel was not handed back (record kept)
                    using var hw = args.Contains("--force") ? null : AcquireHardwareLock("--restore");
                    if (hw == null && !args.Contains("--force")) return 3;
                    var outcome = FailsafeWatchdog.RestoreAll();
                    Console.WriteLine(outcome.Text);
                    if (OptionValue(args, "--result") is string resultPath)
                        try { File.WriteAllText(resultPath, ConfigStore.ToJson(new { outcome.ExitCode, outcome.Text, outcome.Items })); } catch { }
                    return outcome.ExitCode;
                }
                case "--diagnose":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--diagnose");
                    if (hw == null) return 3;
                    return new Diagnostics(pulse: !args.Contains("--no-pulse"), rgbWrites: !args.Contains("--no-rgb")).Run();
                }
                case "--probe-fan":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--probe-fan");
                    if (hw == null) return 3;
                    return FanProbe.Run(args.Length > 1 ? args[1] : "0",
                        args.Length > 2 && int.TryParse(args[2], out var lo) ? lo : 30,
                        args.Length > 3 && int.TryParse(args[3], out var hi) ? hi : 100,
                        args.Length > 4 && int.TryParse(args[4], out var hold) ? hold : 10);
                }
                case "--fan-census":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--fan-census");
                    if (hw == null) return 3;
                    return FanMapping.Census(args.Length > 1 && int.TryParse(args[1], out var cs) ? Math.Clamp(cs, 1, 120) : 10, OptionValue(args, "--out"));
                }
                case "--fan-step":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--fan-step");
                    if (hw == null) return 3;
                    if (args.Length < 3 || !int.TryParse(args[1], out var ctl)) { Console.WriteLine("usage: --fan-step <control index 1-5> <duty,duty,...> [hold s] [--observed] [--out file.json]"); return 2; }
                    var duties = args[2].Split(',').Select(x => int.TryParse(x, out var v) ? v : -1).ToArray();
                    int hold = args.Length > 3 && int.TryParse(args[3], out var hs) ? hs : 12;
                    return FanMapping.Step(ctl, duties, hold, args.Contains("--observed"), OptionValue(args, "--out"));
                }
                case "--fan-engine-check":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--fan-engine-check");
                    if (hw == null) return 3;
                    return FanMapping.EngineCheck(args.Length > 1 && int.TryParse(args[1], out var eh) ? Math.Clamp(eh, 10, 120) : 30, OptionValue(args, "--out"));
                }
                case "--fan-min-test":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--fan-min-test");
                    if (hw == null) return 3;
                    if (args.Length < 2 || !int.TryParse(args[1], out var mctl)) { Console.WriteLine("usage: --fan-min-test <control index 1-5> --observed [--out file.json]"); return 2; }
                    return FanMapping.MinTest(mctl, args.Contains("--observed"), OptionValue(args, "--out"), args.Contains("--startup-only"),
                        int.TryParse(OptionValue(args, "--start-from"), out var sf) ? sf : 20);
                }
                case "--startup-task":
                    EnableConsole();
                    return StartupTaskCli(args.Length > 1 ? args[1].ToLowerInvariant() : "status");
                case "--case-check":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--case-check");
                    if (hw == null) return 3;
                    return CaseCheck.Run(args.Length > 1 && int.TryParse(args[1], out var ccs) ? ccs : 25, args.Contains("--two"));
                }
                case "--lighting-test":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--lighting-test");
                    if (hw == null) return 3;
                    return LiveTests.RunLighting();
                }
                case "--hid-bench":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--hid-bench");
                    if (hw == null) return 3;
                    return LiveTests.RunHidBench();
                }
                case "--dled-rate-bench":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--dled-rate-bench");
                    if (hw == null) return 3;
                    int secondsPerPhase = args.Length > 1 && int.TryParse(args[1], out var duration) ? duration : 20;
                    var rateArg = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--") && a.Contains(','));
                    int[] rates = rateArg != null ? rateArg.Split(',').Select(x => int.TryParse(x, out var v) ? v : 0).ToArray() : new[] { 60, 120 };
                    int outIdx = Array.IndexOf(args, "--out");
                    string? jsonOut = outIdx >= 0 && outIdx + 1 < args.Length ? args[outIdx + 1] : null;
                    return DLedRateBenchmark.Run(secondsPerPhase, rates, jsonOut, args.Contains("--strip-only"));
                }
                case "--lights-out-test":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--lights-out-test");
                    if (hw == null) return 3;
                    return LiveTests.RunLightsOutTest();
                }
                case "--keyboard-test":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--keyboard-test");
                    if (hw == null) return 3;
                    byte kbMode = args.Length > 1 && byte.TryParse(args[1], out var km) && km <= 20 ? km : KeyboardLightingState.ModeSteady;
                    byte kbBright = args.Length > 2 && byte.TryParse(args[2], out var kbr) ? kbr : KeyboardLightingState.MaxBrightness;
                    return LiveTests.RunKeyboardTestWrite(kbMode, kbBright);
                }
                case "--keyboard-live-test":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--keyboard-live-test");
                    if (hw == null) return 3;
                    int cd = args.Length > 1 && int.TryParse(args[1], out var c1) ? Math.Clamp(c1, 0, 120) : 5;
                    return KeyboardLiveTest.Run(cd, args.Contains("--restore-stored"), OptionValue(args, "--out"));
                }
                case "--keyboard-transition-test":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--keyboard-transition-test");
                    if (hw == null) return 3;
                    return KeyboardTransitionTest.Run(args.Contains("--replug"), OptionValue(args, "--out"), args.Contains("--revert-probe"));
                }
                case "--fan-start-check":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--fan-start-check");
                    if (hw == null) return 3;
                    if (args.Length < 2 || !int.TryParse(args[1], out var sctl)) { Console.WriteLine("usage: --fan-start-check <control 1-3> --observed [--duty D] [--repeat R] [--engine] [--engine-floor F] [--out f.json]"); return 2; }
                    return FanMapping.StartCheck(sctl, args.Contains("--observed"),
                        int.TryParse(OptionValue(args, "--duty"), out var sd) ? sd : null,
                        int.TryParse(OptionValue(args, "--repeat"), out var sr) ? sr : 3,
                        args.Contains("--engine"),
                        int.TryParse(OptionValue(args, "--engine-floor"), out var ef) ? ef : null,
                        OptionValue(args, "--out"));
                }
                case "--sync-test":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--sync-test");
                    if (hw == null) return 3;
                    int ss = args.Length > 1 && int.TryParse(args[1], out var s1) ? s1 : 20;
                    int sc = args.Length > 2 && int.TryParse(args[2], out var s2) ? Math.Clamp(s2, 0, 120) : 10;
                    var speeds = OptionValue(args, "--speeds")?.Split(',').Select(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1).Where(v => v >= 0).ToArray();
                    return SyncTest.Run(ss, sc, OptionValue(args, "--out"), speeds);
                }
                case "--keyboard-probe":
                {
                    EnableConsole();
                    return LiveTests.RunKeyboardProbe();
                }
                case "--ene-probe":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--ene-probe");
                    if (hw == null) return 3;
                    return LiveTests.RunEneProbe();
                }
                case "--cpu-bench":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--cpu-bench");
                    if (hw == null) return 3;
                    int secs2 = args.Length > 1 && int.TryParse(args[1], out var d2) ? d2 : 15;
                    int oi = Array.IndexOf(args, "--out"), li = Array.IndexOf(args, "--label");
                    return CpuBenchmark.Run(secs2, oi >= 0 && oi + 1 < args.Length ? args[oi + 1] : null, li >= 0 && li + 1 < args.Length ? args[li + 1] : "run");
                }
                case "--identify-test":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--identify-test");
                    if (hw == null) return 3;
                    return LiveTests.RunIdentify(args.Length > 1 && int.TryParse(args[1], out var hdr) ? hdr : 0);
                }
                case "--engine-test":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--engine-test");
                    if (hw == null) return 3;
                    return LiveTests.RunEngine(args.Length > 1 && int.TryParse(args[1], out var secs) ? secs : 90);
                }
                case "--mouse-test":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--mouse-test");
                    if (hw == null) return 3;
                    return LiveTests.RunMouse(args.Length > 1 && int.TryParse(args[1], out var ms) ? ms : 20);
                }
                case "--mouse-stream":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--mouse-stream");
                    if (hw == null) return 3;
                    return MouseStream.Run(args);
                }
                case "--al80":
                {
                    EnableConsole();
                    // reads Spotify and draws a PNG only: no device, so it runs next to VANTA
                    if (args.Length > 1 && args[1].Equals("nowplaying", StringComparison.OrdinalIgnoreCase) && !args.Contains("--send")) return Al80Cli.NowPlayingPreview(args);
                    if (args.Length > 1 && args[1].Equals("nowplaying-watch", StringComparison.OrdinalIgnoreCase)) return Al80Cli.NowPlayingWatch(args);
                    if (args.Length > 1 && args[1].Equals("deadlock", StringComparison.OrdinalIgnoreCase)) return Al80Cli.DeadlockPreview(args);   // draws a PNG only
                    using var hw = AcquireHardwareLock("--al80");
                    if (hw == null) return 3;
                    try { return Al80Cli.Run(args); }
                    catch (HwSuite.Hardware.Al80.Al80Exception ex) { Console.WriteLine("AL80: " + ex.Message); return 1; }
                }
                case "--mouse-probe":
                {
                    EnableConsole();
                    using var hw = AcquireHardwareLock("--mouse-probe");
                    if (hw == null) return 3;
                    return MouseProbe.Run(args);
                }
                case "--ui-shot":
                case "--ui-fixtures":
                {
                    // Phase 11: screenshots come only from simulated fixtures (no hardware is opened, nothing is written).
                    // --theme all|vanta,obsidian,porcelain  --size all|compact,standard,large,scaled150  --fixture id,...
                    EnableConsole();
                    LogIsolation();
                    var themeArg = (OptionValue(args, "--theme") ?? "all").ToLowerInvariant();
                    var themes = themeArg == "all" ? HwSuite.Ui.Themes.All : themeArg.Split(',').Select(HwSuite.Ui.Themes.Find).Distinct().ToArray();
                    var sizeArg = (OptionValue(args, "--size") ?? "standard").ToLowerInvariant();
                    var sizes = sizeArg == "all" ? App.AllSizes : App.AllSizes.Where(s => sizeArg.Split(',').Any(x => s.Name.StartsWith(x))).ToArray();
                    if (sizes.Length == 0 && sizeArg.Split('x') is [var sw, var sh] && int.TryParse(sw, out var w) && int.TryParse(sh, out var h))
                        sizes = new[] { new App.ShotSize($"{w}x{h}", w, h, double.TryParse(OptionValue(args, "--scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc) ? Math.Clamp(sc, 1, 3) : 1) };
                    var fixtureArg = OptionValue(args, "--fixture");
                    var fixtures = fixtureArg == null ? HwSuite.Ui.UiFixture.All() : HwSuite.Ui.UiFixture.All().Where(f => fixtureArg.Split(',').Contains(f.Id)).ToList();
                    string dir = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine(AppPaths.Data, "screenshots");
                    // --film <dir> [--film-seconds S] [--film-fps F]: also capture the simulated preview frame by frame (explicit times)
                    double filmSeconds = double.TryParse(OptionValue(args, "--film-seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fs) ? Math.Clamp(fs, 1, 120) : 10;
                    double filmFps = double.TryParse(OptionValue(args, "--film-fps"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ff) ? Math.Clamp(ff, 2, 60) : 15;
                    int failed = new App(true, themes[0].Id).RunFixtures(dir, themes, sizes, fixtures, OptionValue(args, "--fixture-config"), OptionValue(args, "--motion"), OptionValue(args, "--film"), filmSeconds, filmFps);
                    Console.WriteLine($"fixtures rendered to {dir}: {failed} failure(s)");
                    return failed == 0 ? 0 : 1;
                }
                case "--ui-idle-bench":
                {
                    // measurement only (simulated fixture, no hardware): UI CPU and memory per phase
                    EnableConsole();
                    LogIsolation();
                    return UiIdleBench.Run(args.Length > 1 && int.TryParse(args[1], out var ib) ? Math.Clamp(ib, 5, 300) : 20, OptionValue(args, "--out"), OptionValue(args, "--fixture") ?? "lighting-wave");
                }
                case "--scene-probe":
                    EnableConsole();
                    return SceneProbe.Run(args.Length > 1 ? args[1] : "Drip", args.Length > 2 && double.TryParse(args[2], out var sps) ? Math.Clamp(sps, 2, 60) : 12);
                case "--scene-bench":
                {
                    // measurement only (hardware-free): engine cost per effect with stand-in devices that accept frames instantly
                    EnableConsole();
                    LogIsolation();
                    int hz = int.TryParse(OptionValue(args, "--strip-hz"), out var shz) && RgbConfig.DLedRates.Contains(shz) ? shz : 60;
                    return SceneBench.Run(args.Length > 1 && int.TryParse(args[1], out var sb) ? Math.Clamp(sb, 3, 120) : 10, OptionValue(args, "--out"), hz);
                }
                case "--selftest":
                {
                    EnableConsole();
                    // unit checks always run; the read-only hardware section needs the instance lock. --no-hardware skips it
                    // (no Super I/O, SMBus, NVML or HID access at all); the UI check renders fixtures and touches no hardware.
                    if (args.Contains("--no-hardware"))
                    {
                        LogIsolation();
                        InjectFault(args, "startup");
                        int rc = SelfTest.Run(args.Contains("--ui"), hardware: false, injectUiFault: OptionValue(args, "--inject-fault") == "ui");
                        InjectFault(args, "teardown");
                        Console.WriteLine($"isolated test data and log: {AppPaths.Data}");
                        return rc;
                    }
                    using var hw = AcquireHardwareLock("--selftest (hardware section)");
                    return SelfTest.Run(args.Contains("--ui") && hw != null, hardware: hw != null);
                }
                case "--help":
                case "-h":
                case "/?":
                    EnableConsole();
                    Console.WriteLine("VANTA - Gigabyte + XPG hardware suite");
                    Console.WriteLine("  (no arguments)   launch the application");
                    Console.WriteLine("  --selftest [--ui] headless startup self-test (add --ui to also build the main window)");
                    Console.WriteLine("  --diagnose [--no-pulse] [--no-rgb]  hardware discovery, PWM pulse test and RGB test");
                    Console.WriteLine("  --restore [--force] [--result f.json] hand every fan VANTA took back (GPU: VBIOS auto; motherboard: 100% until reboot); exit 0 ok, 3 VANTA running, 4 not all handed back");
                    Console.WriteLine("  --selftest [--ui] [--no-hardware] [--test-data dir]  unit checks; --no-hardware never opens any device, uses an isolated data folder and log, and never runs recovery; --ui renders fixture screens");
                    Console.WriteLine("  --ui-fixtures <dir> [--theme t] [--size WxH] [--scale s]  render simulated fixture states (no hardware access)");
                    Console.WriteLine("  --startup-task on|off|status   install/remove the elevated logon task");
                    Console.WriteLine(Al80Cli.Usage);
                    Console.WriteLine("  --fan-swap preview|apply|undo  settings for the 2026-10 fan swap (VANTA must be closed for apply/undo)");
                    Console.WriteLine("  --identify-test N pulse motherboard header N (0-based) to 100% for 5 s and verify the release");
                    Console.WriteLine("  --lighting-test   60 FPS lighting engine through every effect on all devices");
                    Console.WriteLine("  --engine-test S   fan engine soak test on the GPU fans for S seconds");
                    Console.WriteLine("  --hid-bench       free-running D_LED strip throughput benchmark");
                    Console.WriteLine("  --dled-rate-bench S [60,90,120,144] [--strip-only] [--out file.json]  D_LED rate A/B (same effect, speed, brightness), S s per phase, two rounds");
                    Console.WriteLine("  --mouse-test S    mouse lighting traffic test for S seconds (verified commands only)");
                    Console.WriteLine("  " + MouseProbe.Usage);
                    Console.WriteLine("  " + MouseStream.Usage);
                    Console.WriteLine("                    owner-watched live colour test on the mouse (test 8)");
                    Console.WriteLine("                    owner-watched mouse lighting test (docs/MOUSE_SYNC_TEST_PLAN.md); max 1 write/s, 20 per run");
                    Console.WriteLine("  --cpu-bench S [--out file.json] [--label name]  lighting CPU profile per effect and per device");
                    Console.WriteLine("  --fan-census [S] [--out f.json]   read-only: every Super I/O tachometer and control value, 1/s for S s");
                    Console.WriteLine("  --fan-step N d,d,.. [hold] [--observed] [--out f.json]  step control N (1-5, never CPU_FAN); <40% only with --observed");
                    Console.WriteLine("  --fan-engine-check [S] [--out f.json]  fan engine holds the case fans at their confirmed minimums, then hands back");
                    Console.WriteLine("  --fan-min-test N --observed [--out f.json]  attended running-floor and startup test (stops the fan)");
                    Console.WriteLine("  --sync-test S [countdown] [--speeds a,b] [--out f.json]  owner-watched Breathing + Gradient Wave on all devices with the saved look");
                    Console.WriteLine("  --keyboard-live-test [countdown s] [--restore-stored] [--out f.json]  owner-watched keyboard live-frame timeline");
                    Console.WriteLine("  --keyboard-transition-test [--replug] [--out f.json]  owner-answered Lights out / live / onboard / output / reconnect steps");
                    Console.WriteLine("  --fan-start-check N --observed [--duty D] [--repeat R] [--engine] [--engine-floor F] [--out f.json]  attended restart at the chosen floor");
                    Console.WriteLine("  --minimized       start in the system tray");
                    return 0;
            }
            if (mode.StartsWith("--") && mode != "--minimized")
            {
                EnableConsole();
                Console.WriteLine("Unknown option: " + mode + " (try --help)");
                return 2;
            }
            return RunGui(args);
        }
        catch (Exception ex) when (!RunMode.HardwareFree)
        {
            Log.Error("fatal", ex);
            try { FailsafeWatchdog.RestoreAll(); } catch { }
            return 1;
        }
    }

    static string? OptionValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static int StartupTaskCli(string action)
    {
        switch (action)
        {
            case "on":
            case "create":
                var (ok1, m1) = Hardware.StartupTask.Create();
                Console.WriteLine(m1);
                return ok1 ? 0 : 1;
            case "off":
            case "remove":
                var (ok2, m2) = Hardware.StartupTask.Remove();
                Console.WriteLine(m2);
                return ok2 ? 0 : 1;
            default:
                Console.WriteLine(Hardware.StartupTask.Exists() ? "installed" : "not installed");
                return 0;
        }
    }

    static int RunGui(string[] args)
    {
        using var single = new Mutex(false, InstanceMutexName);
        bool created;
        try { created = single.WaitOne(0); } catch (AbandonedMutexException) { created = true; }
        if (!created)
        {
            // the running app listens on this event; a hardware test does not, so it gets the message instead
            if (!args.Contains("--minimized"))
            {
                bool shown = false;
                try { if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var ev)) using (ev) { AllowSetForegroundWindow(-1); shown = ev.Set(); } } catch { }
                if (!shown) System.Windows.MessageBox.Show("A VANTA hardware test is in progress (it owns the hardware). Start VANTA again when it has finished.", "VANTA");
                Log.Info(shown ? "second launch: brought the running VANTA window forward" : "second instance refused: a hardware test holds the instance lock");
            }
            else Log.Info("second instance refused: the instance lock is held");
            return 0;
        }

        // Last line of defence in-process: an unhandled exception hands fans back. This does not run on a forced kill
        // or a hard crash; the separate watchdog process covers those (see FailsafeWatchdog for its limits).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("unhandled exception: " + e.ExceptionObject);
            try { FailsafeWatchdog.RestoreAll(); } catch { }
        };

        bool elevated;
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent()) elevated = new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        Log.Info($"VANTA {typeof(Program).Assembly.GetName().Version} starting: exe {Environment.ProcessPath}, working folder {Environment.CurrentDirectory}, administrator {elevated}, settings {AppPaths.Data}");
        FailsafeWatchdog.SpawnFor(Environment.ProcessId);
        var app = new App(args.Contains("--minimized"));
        return app.Run();
    }
}
