using System.Diagnostics;
using System.Runtime.InteropServices;
using HwSuite.Core;
using HwSuite.Hardware;

namespace HwSuite.Fans;

/// <summary>
/// Crash/kill/hang protection. The GUI spawns "HwSuite.exe --watchdog &lt;pid&gt;" (hidden). It
///  - while the GUI runs, watches the fan loop's heartbeat; if it is older than HangSeconds and HwSuite drives any fan,
///    it hands those fans back to firmware control once per hang (bounded to RestoreTimeout);
///  - when the GUI process disappears for any reason, returns every channel recorded in controlled.json to firmware
///    control, then (if enabled and Windows is not shutting down) starts the Gigabyte services HwSuite had stopped.
/// Limits: it cannot help if it is itself killed (for example with the GUI's whole process tree), if Windows hangs or
/// crashes, on power loss, or if a hung GUI holds a hardware lock the hand-back needs. In those cases the fans keep
/// their last duty until the BIOS / GPU driver re-initialise them (reboot); the CPU and GPU still protect themselves
/// by throttling and thermal shutdown.
/// </summary>
public static class FailsafeWatchdog
{
    public const double HangSeconds = 30;
    public static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(20);

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    const int SmShuttingDown = 0x2000;

    public static void SpawnFor(int parentPid)
    {
        if (RunMode.Refuse("spawn the failsafe watchdog")) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new ProcessStartInfo(exe, $"--watchdog {parentPid}")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            Log.Info($"Failsafe watchdog spawned for pid {parentPid}");
        }
        catch (Exception ex) { Log.Error("could not spawn watchdog", ex); }
    }

    /// <summary>Hang decision, separated for tests: act once per hang while fans are under HwSuite control.</summary>
    public static bool ShouldActOnHang(double? heartbeatAgeSeconds, int controlledChannels, bool alreadyHandled) =>
        heartbeatAgeSeconds is double a && a > HangSeconds && controlledChannels > 0 && !alreadyHandled;

    public static int RunWatchdog(int parentPid)
    {
        if (RunMode.Refuse("run the failsafe watchdog")) return 0;
        Log.ConsoleEnabled = false;
        Process? parent = null;
        try { parent = Process.GetProcessById(parentPid); } catch (ArgumentException) { /* already gone */ }
        if (parent != null)
        {
            using (parent)
            {
                bool handled = false;
                while (!parent.WaitForExit(2000))
                {
                    var age = Heartbeat.AgeSeconds(parentPid);   // null: no heartbeat yet, or a clock failure (no decision)
                    if (ShouldActOnHang(age, ControlRegistry.Load().Count, handled))
                    {
                        Log.Error($"watchdog: HwSuite (pid {parentPid}) fan loop silent for {age:0} s; returning its fans to firmware control");
                        RestoreAllBounded(RestoreTimeout);
                        handled = true;
                    }
                    else if (age is double a && a < 5) handled = false;
                }
            }
        }
        Thread.Sleep(500);
        Log.Warn($"watchdog: parent {parentPid} ended, restoring hardware fan control");
        RestoreAll();
        RestoreServicesIfAllowed("watchdog after HwSuite ended");
        return 0;
    }

    static void RestoreAllBounded(TimeSpan timeout)
    {
        var t = new Thread(() => { try { RestoreAll(); } catch (Exception ex) { Log.Error("watchdog restore", ex); } }) { IsBackground = true };
        t.Start();
        if (!t.Join(timeout)) Log.Error($"watchdog: restore did not finish within {timeout.TotalSeconds:0} s (a hardware lock may be held by the hung process)");
    }

    /// <summary>Start again the Gigabyte services HwSuite recorded as stopped, unless disabled or Windows is ending the session.</summary>
    public static void RestoreServicesIfAllowed(string why)
    {
        if (RunMode.Refuse("restore Gigabyte services")) return;
        try
        {
            if (GetSystemMetrics(SmShuttingDown) != 0) { Log.Info("service restore skipped: Windows is shutting down or logging off"); return; }
            var cfg = ConfigStore.Load();
            if (!cfg.RestoreGigabyteOnExit) return;
            var guard = GigabyteGuard.CreateLive();
            if (guard.LoadRecord().StoppedServices.Count == 0) return;
            Log.Info($"restoring Gigabyte services HwSuite stopped ({why})");
            guard.RestoreRecorded();
        }
        catch (Exception ex) { Log.Error("service restore", ex); }
    }

    public static bool SessionEnding => GetSystemMetrics(SmShuttingDown) != 0;

    /// <summary>One claimed channel and what the hand-back did to it.</summary>
    public sealed record RestoreItem(string Backend, string Id, bool Ok, string Detail);
    public sealed record RestoreOutcome(int ExitCode, string Text, List<RestoreItem> Items);
    /// <summary>Exit code of --restore when at least one claimed channel could not be handed back (its record is kept).</summary>
    public const int RestoreFailedExit = 4;

    /// <summary>Exit code and one-paragraph summary for --restore (and the uninstaller that checks it).</summary>
    public static RestoreOutcome Summarize(List<RestoreItem> items, bool registryCleared)
    {
        if (items.Count == 0) return new(0, "Nothing was under VANTA's control; nothing to hand back.", items);
        var failed = items.Where(i => !i.Ok).ToList();
        int mb = items.Count(i => i.Ok && i.Backend == "lhm");
        var parts = new List<string>();
        int gpu = items.Count(i => i.Ok && i.Backend == "nvml");
        if (gpu > 0) parts.Add($"{gpu} GPU fan(s) returned to the VBIOS automatic curve");
        if (mb > 0) parts.Add($"{mb} motherboard header(s) set to 100%: the BIOS fan curve returns after a reboot (another process cannot hand BIOS control back)");
        if (failed.Count > 0) parts.Add($"NOT handed back: {string.Join("; ", failed.Select(f => $"{f.Backend}:{f.Id} ({f.Detail})"))}; their record is kept");
        if (!registryCleared && failed.Count == 0) parts.Add("the control record could not be updated");
        bool ok = failed.Count == 0 && registryCleared;
        return new(ok ? 0 : RestoreFailedExit, string.Join(". ", parts) + ".", items);
    }

    /// <summary>
    /// Release every channel listed in controlled.json. Safe to call from any process. Only channels actually handed back
    /// are removed from the record; a channel that could not be (NVML or LibreHardwareMonitor unavailable, sensor gone)
    /// stays listed so the outcome is visible and can be retried.
    /// </summary>
    public static RestoreOutcome RestoreAll()
    {
        // a hardware-free run claimed nothing and must never run real recovery (not even "nothing claimed" checks)
        if (RunMode.Refuse("run fan recovery (RestoreAll)")) return new(0, "Hardware-free run: recovery not run (nothing can have been claimed).", new());
        var entries = ControlRegistry.Load();
        var items = new List<RestoreItem>();
        if (entries.Count == 0) { Log.Info("restore: nothing under manual control"); return Summarize(items, true); }

        var nvml = entries.Where(e => e.Backend == "nvml").ToList();
        if (nvml.Count > 0)
        {
            NvmlGpu? gpu = null; string err = "";
            try { gpu = NvmlGpu.TryOpen(out err); } catch (Exception ex) { err = ex.Message; }
            foreach (var e in nvml)
            {
                bool ok = false; string detail;
                if (gpu == null) detail = "NVML unavailable: " + err;
                else if (!int.TryParse(e.Id, out var fan)) detail = "unrecognised fan id";
                else { try { ok = gpu.ReleaseToAuto(fan); detail = ok ? "VBIOS auto" : "NVML refused the release"; } catch (Exception ex) { detail = ex.Message; } }
                items.Add(new("nvml", e.Id, ok, detail));
            }
            gpu?.Dispose();
            Log.Info($"restore: {items.Count(i => i.Backend == "nvml" && i.Ok)}/{nvml.Count} GPU fan(s) returned to VBIOS auto");
        }

        // LibreHardwareMonitorLib 0.9.6 (IT87XX.RestoreDefaultFanPwmControl, verified in its IL) only restores registers that
        // the same LHM instance saved before its first write; a new instance in this process has nothing saved, so
        // SetDefault() would silently do nothing. The verified safe action available here is full speed.
        var lhmEntries = entries.Where(e => e.Backend == "lhm").ToList();
        if (lhmEntries.Count > 0)
        {
            using var lhm = new LhmService();
            if (lhm.Open())
            {
                var done = new List<string>();
                lock (lhm.Gate)
                {
                    foreach (var e in lhmEntries)
                    {
                        var c = lhm.Sensors.FirstOrDefault(s => s.Id == e.Id)?.Sensor.Control;
                        if (c == null) { items.Add(new("lhm", e.Id, false, "header not found")); continue; }
                        try { c.SetSoftware(Math.Min(100f, c.MaxSoftwareValue)); done.Add(e.Id); items.Add(new("lhm", e.Id, true, "100% until reboot")); }
                        catch (Exception ex) { items.Add(new("lhm", e.Id, false, ex.Message)); }
                    }
                }
                RebootNotice.Add(done);
                Log.Warn($"restore: {done.Count} motherboard header(s) set to 100% (BIOS control can only be restored by the process that took it, or by a reboot)");
            }
            else foreach (var e in lhmEntries) items.Add(new("lhm", e.Id, false, "LibreHardwareMonitor unavailable: " + lhm.Error));
        }
        foreach (var e in entries.Where(e => e.Backend is not ("nvml" or "lhm"))) items.Add(new(e.Backend, e.Id, false, "unknown backend"));

        bool cleared = true;
        try { foreach (var i in items.Where(i => i.Ok)) ControlRegistry.Unmark(i.Backend, i.Id); }
        catch { cleared = false; }
        cleared &= ControlRegistry.Load().All(e => items.Any(i => !i.Ok && i.Backend == e.Backend && i.Id == e.Id));
        var outcome = Summarize(items, cleared);
        if (outcome.ExitCode == 0) Log.Info("restore: " + outcome.Text); else Log.Error("restore: " + outcome.Text);
        return outcome;
    }
}

/// <summary>
/// Motherboard headers a crash/hang recovery left at 100% (see FailsafeWatchdog.RestoreAll). Shown at the next start;
/// cleared when Windows has restarted since (the BIOS re-initialises the Super I/O on boot).
/// </summary>
public static class RebootNotice
{
    sealed class Data { public List<string> Channels { get; set; } = new(); public DateTime When { get; set; } }
    static string PathName => Path.Combine(AppPaths.Data, "headers-left-at-100.json");

    public static void Add(List<string> channels)
    {
        if (channels.Count == 0) return;
        try
        {
            var d = Load() ?? new Data();
            foreach (var c in channels) if (!d.Channels.Contains(c)) d.Channels.Add(c);
            d.When = DateTime.Now;
            File.WriteAllText(PathName, System.Text.Json.JsonSerializer.Serialize(d));
        }
        catch { }
    }

    static Data? Load()
    {
        try { return File.Exists(PathName) ? System.Text.Json.JsonSerializer.Deserialize<Data>(File.ReadAllText(PathName)) : null; }
        catch { return null; }
    }

    /// <summary>Pending notice text, or null. Removes the notice if Windows has booted since it was written.</summary>
    public static string? Pending()
    {
        var d = Load();
        if (d == null || d.Channels.Count == 0) return null;
        var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (boot > d.When) { try { File.Delete(PathName); } catch { } return null; }
        return $"After HwSuite ended unexpectedly at {d.When:t}, {d.Channels.Count} motherboard fan header(s) were set to 100% because BIOS control cannot be handed back from another process. Restart Windows to return them to the BIOS fan curve.";
    }
}
