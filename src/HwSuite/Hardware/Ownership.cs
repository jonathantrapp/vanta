using System.Diagnostics;
using System.Text.Json;
using HwSuite.Core;

namespace HwSuite.Hardware;

/// <summary>Hardware that another program can also write to. HwSuite writes to one only while it can establish ownership.</summary>
public enum HwResource { GpuFans, SuperIo, Smbus, RgbUsb, Mouse, Keyboard }

public sealed record OwnershipDecision(bool MayWrite, string Reason)
{
    public static readonly OwnershipDecision Owned = new(true, "");
}

public interface IOwnership
{
    OwnershipDecision Check(HwResource resource);
    /// <summary>Called when HwSuite observes another program changing hardware it controls. Ownership is then withheld.</summary>
    void ReportExternalWrite(HwResource resource, string detail);
}

/// <summary>Used by tests and command-line tools that run with nothing else active.</summary>
public sealed class AssumeOwned : IOwnership
{
    public static readonly AssumeOwned Instance = new();
    public OwnershipDecision Check(HwResource resource) => OwnershipDecision.Owned;
    public void ReportExternalWrite(HwResource resource, string detail) { }
}

/// <summary>Process and service state, injectable for tests.</summary>
public interface ISystemProbe
{
    /// <summary>Lower-case process names currently running.</summary>
    HashSet<string> RunningProcessNames();
    ServiceRunState ServiceState(string name);
}

public sealed class LiveSystemProbe : ISystemProbe
{
    public static readonly LiveSystemProbe Instance = new();

    public HashSet<string> RunningProcessNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var p in Process.GetProcesses()) { try { set.Add(p.ProcessName); } catch { } finally { p.Dispose(); } } }
        catch { }
        return set;
    }

    public ServiceRunState ServiceState(string name) => WindowsServiceControl.Instance.Query(name);
}

/// <summary>
/// Gigabyte tools that access the Super I/O chip, the SMBus or the IT5701 lighting controller. Names are the ones
/// Toggle-Gigabyte.ps1 manages. Stopping goes through that script; HwSuite verifies the result itself, records exactly
/// which services it stopped, and later starts only those again.
/// </summary>
public sealed class GigabyteGuard
{
    public static readonly string[] Services = { "EasyTuneEngineService", "GService", "GCCService", "AppCenter", "GvService" };
    public static readonly string[] Processes = { "RGBFusion", "EasyTuneEngineService", "EasyTune", "SIV", "ThermalConsole", "GCC", "AppCenter", "CheckKill", "KillProcess" };

    public sealed class StopRecord
    {
        public List<string> StoppedServices { get; set; } = new();
        public List<string> KilledProcesses { get; set; } = new();
        public DateTime? LastStop { get; set; }
    }

    public sealed record StopResult(bool Verified, List<string> Stopped, List<string> StillRunning, string Message);

    readonly ISystemProbe _probe;
    readonly IServiceControl _services;
    readonly Func<(bool ok, string output)> _runStopScript;
    readonly string _recordPath;
    readonly object _gate = new();

    public GigabyteGuard(ISystemProbe probe, IServiceControl services, Func<(bool ok, string output)> runStopScript, string recordPath)
    {
        _probe = probe; _services = services; _runStopScript = runStopScript; _recordPath = recordPath;
    }

    public static GigabyteGuard CreateLive() =>
        new(LiveSystemProbe.Instance, WindowsServiceControl.Instance, () => GigabyteServices.Run("Stop"), Path.Combine(AppPaths.Data, "gigabyte-stopped.json"));

    /// <summary>Running Gigabyte services and processes, as display names ("EasyTuneEngineService service").</summary>
    public List<string> RunningCompetitors()
    {
        var list = new List<string>();
        foreach (var s in Services)
        {
            var st = _probe.ServiceState(s);
            if (st is ServiceRunState.Running or ServiceRunState.StartPending or ServiceRunState.ContinuePending or ServiceRunState.Paused or ServiceRunState.PausePending)
                list.Add(s + " service");
        }
        var procs = _probe.RunningProcessNames();
        foreach (var p in Processes)
            if (procs.Contains(p) && !list.Any(x => x.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))) list.Add(p + " process");
        return list;
    }

    public StopRecord LoadRecord()
    {
        lock (_gate)
        {
            try { if (File.Exists(_recordPath)) return JsonSerializer.Deserialize<StopRecord>(File.ReadAllText(_recordPath)) ?? new(); }
            catch (Exception ex) { Log.Warn("gigabyte record unreadable: " + ex.Message); }
            return new StopRecord();
        }
    }

    void SaveRecord(StopRecord r)
    {
        lock (_gate)
        {
            try
            {
                if (r.StoppedServices.Count == 0 && r.KilledProcesses.Count == 0) { if (File.Exists(_recordPath)) File.Delete(_recordPath); }
                else File.WriteAllText(_recordPath, JsonSerializer.Serialize(r));
            }
            catch (Exception ex) { Log.Warn("gigabyte record not saved: " + ex.Message); }
        }
    }

    /// <summary>Run the stop script, then check for ourselves what actually stopped. Never assumes success from the exit code.</summary>
    public StopResult StopAndRecord(string reason)
    {
        var runningServicesBefore = Services.Where(s => _probe.ServiceState(s) == ServiceRunState.Running).ToList();
        var procsBefore = _probe.RunningProcessNames();
        var processesBefore = Processes.Where(p => procsBefore.Contains(p)).ToList();
        if (runningServicesBefore.Count == 0 && processesBefore.Count == 0) return new(true, new(), new(), "nothing to stop");

        Log.Info($"stopping Gigabyte tools ({reason}): {string.Join(", ", runningServicesBefore.Concat(processesBefore))}");
        var (ok, output) = _runStopScript();
        Thread.Sleep(800); // let the chips and bus settle before this app touches them

        var stillRunning = RunningCompetitors();
        var stoppedServices = runningServicesBefore.Where(s => _probe.ServiceState(s) is ServiceRunState.Stopped).ToList();
        var procsAfter = _probe.RunningProcessNames();
        var killed = processesBefore.Where(p => !procsAfter.Contains(p) && !Services.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();

        var rec = LoadRecord();
        foreach (var s in stoppedServices) if (!rec.StoppedServices.Contains(s, StringComparer.OrdinalIgnoreCase)) rec.StoppedServices.Add(s);
        foreach (var p in killed) if (!rec.KilledProcesses.Contains(p, StringComparer.OrdinalIgnoreCase)) rec.KilledProcesses.Add(p);
        rec.LastStop = DateTime.Now;
        SaveRecord(rec);

        bool verified = stillRunning.Count == 0;
        string msg = verified
            ? $"Stopped {string.Join(", ", stoppedServices.Concat(killed))}."
            : $"Still running after the stop attempt: {string.Join(", ", stillRunning)}" + (ok ? "" : $" (script: {Trim(output)})");
        Log.Info("gigabyte stop: " + msg);
        return new(verified, stoppedServices, stillRunning, msg);
    }

    /// <summary>One recorded service and what the bounded start attempt established (null status: no start was requested).</summary>
    public sealed record ServiceRestore(string Service, ServiceStartStatus? Status, string Text);

    /// <summary>
    /// Start again only the services HwSuite recorded as stopped, waiting at most <see cref="StartWait"/> each. Services that
    /// are no longer Stopped are left alone; killed GUI processes are not relaunched. Each result says only what was
    /// observed: "Start requested; still starting." when the service accepted the request but was still StartPending at the
    /// end of the wait (EasyTuneEngineService often is), never "started". A refused or failed start, or one whose result
    /// could not be confirmed, stays on the record so the next restore (the watchdog right after exit, or the next exit)
    /// can try again; it is still only a service VANTA itself stopped.
    /// </summary>
    public List<string> RestoreRecorded() => RestoreRecordedDetailed().Select(r => r.Text).ToList();

    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(15);

    public List<ServiceRestore> RestoreRecordedDetailed()
    {
        var rec = LoadRecord();
        var results = new List<ServiceRestore>();
        var keep = new List<string>();
        foreach (var s in rec.StoppedServices)
        {
            var st = _services.Query(s);
            if (st == ServiceRunState.NotInstalled) { results.Add(new(s, null, $"{s}: no longer installed")); continue; }
            if (st != ServiceRunState.Stopped) { results.Add(new(s, null, $"{s}: already {st}, left alone")); continue; }
            var outcome = _services.Start(s, StartWait);
            string text = outcome.Describe(s);
            if (outcome.Status is ServiceStartStatus.Failed or ServiceStartStatus.Uncertain) { keep.Add(s); text += "; kept on VANTA's restart list"; }
            results.Add(new(s, outcome.Status, text));
        }
        if (rec.KilledProcesses.Count > 0)
            results.Add(new("", null, $"not relaunched (user programs): {string.Join(", ", rec.KilledProcesses)}"));
        SaveRecord(new StopRecord { StoppedServices = keep, LastStop = keep.Count > 0 ? rec.LastStop : null });
        foreach (var r in results)
        {
            if (r.Status is ServiceStartStatus.Failed or ServiceStartStatus.Uncertain) Log.Warn("gigabyte restore: " + r.Text);
            else Log.Info("gigabyte restore: " + r.Text);
        }
        return results;
    }

    /// <summary>Drop services from the record that are running again (restarted by Windows, a reboot or the user).</summary>
    public void PruneRecord()
    {
        var rec = LoadRecord();
        int n = rec.StoppedServices.RemoveAll(s => _services.Query(s) is ServiceRunState.Running or ServiceRunState.StartPending or ServiceRunState.NotInstalled);
        if (n > 0) { SaveRecord(rec); Log.Info($"gigabyte record: {n} service(s) are running again, removed from the restore list"); }
    }

    static string Trim(string s) { s = s.Replace("\r", " ").Replace("\n", " "); return s.Length > 160 ? s[..160] + "..." : s; }
}

/// <summary>
/// Decides, every couple of seconds, which hardware HwSuite may write. Gigabyte tools block the Super I/O, SMBus and
/// IT5701 paths; MSI Afterburner blocks the GPU fans while its software curve or its own manual speed is active, and any
/// observed outside change to a GPU fan HwSuite set blocks them until the competitor exits or the owner retries.
/// </summary>
public sealed class OwnershipArbiter : IOwnership
{
    readonly AppConfig _cfg;
    readonly GigabyteGuard _gb;
    readonly ISystemProbe _probe;
    readonly Func<AfterburnerFanSetting> _afterburnerConfig;
    readonly Func<bool> _gpuForeignManual;
    readonly object _gate = new();
    readonly Dictionary<HwResource, OwnershipDecision> _state = new();

    DateTime _lastStopAttempt = DateTime.MinValue;
    int _stopAttempts;
    volatile bool _stopping;
    GigabyteGuard.StopResult? _lastStop;
    string? _gpuLatched;           // reason text while an outside GPU write is latched
    bool _gpuLatchedWithAfterburner;

    public const int MaxAutoStopAttempts = 5;
    public static readonly TimeSpan AutoStopRetry = TimeSpan.FromSeconds(60);

    public List<string> GigabyteRunning { get; private set; } = new();
    public bool AfterburnerRunning { get; private set; }
    public bool? AfterburnerAutoFan { get; private set; }
    public AfterburnerFanSetting? AfterburnerSetting { get; private set; }
    public DateTime LastRefresh { get; private set; }
    public GigabyteGuard Guard => _gb;
    public event Action<string>? Notice;

    public OwnershipArbiter(AppConfig cfg, GigabyteGuard gb, ISystemProbe probe, Func<AfterburnerFanSetting> afterburnerConfig, Func<bool> gpuForeignManual)
    {
        _cfg = cfg; _gb = gb; _probe = probe; _afterburnerConfig = afterburnerConfig; _gpuForeignManual = gpuForeignManual;
        foreach (HwResource r in Enum.GetValues(typeof(HwResource))) _state[r] = new(false, "ownership not checked yet");
    }

    public OwnershipDecision Check(HwResource resource)
    {
        lock (_gate) return _state[resource];
    }

    public void ReportExternalWrite(HwResource resource, string detail)
    {
        if (resource != HwResource.GpuFans) return;
        lock (_gate)
        {
            if (_gpuLatched != null) return;
            _gpuLatched = "another program changed a GPU fan HwSuite was driving (" + detail + "); HwSuite stopped writing to the GPU fans";
            _gpuLatchedWithAfterburner = AfterburnerRunning;
            _state[HwResource.GpuFans] = new(false, _gpuLatched);
        }
        Log.Warn("GPU fan ownership withdrawn: " + detail);
        Notice?.Invoke(_gpuLatched!);
    }

    /// <summary>Owner asked HwSuite to try again after fixing the other program.</summary>
    public void ResetGpuConflict()
    {
        lock (_gate) { _gpuLatched = null; }
        Log.Info("GPU fan ownership latch cleared by the owner");
        Refresh();
    }

    /// <summary>Owner pressed "Stop services" or toggled auto-stop: allow a fresh round of attempts.</summary>
    public void ResetStopAttempts() { _stopAttempts = 0; _lastStopAttempt = DateTime.MinValue; }

    public GigabyteGuard.StopResult? LastStopResult => _lastStop;

    public void Refresh()
    {
        bool fanOn, rgbOn, autoStop;
        lock (_cfg.Sync) { fanOn = _cfg.FanControlEnabled; rgbOn = _cfg.Rgb.Enabled; autoStop = _cfg.AutoStopGigabyte; }

        var running = _gb.RunningCompetitors();
        GigabyteRunning = running;
        if (running.Count > 0 && (fanOn || rgbOn) && autoStop && !_stopping
            && _stopAttempts < MaxAutoStopAttempts && DateTime.UtcNow - _lastStopAttempt >= AutoStopRetry)
        {
            _stopping = true;
            _stopAttempts++;
            _lastStopAttempt = DateTime.UtcNow;
            Task.Run(() =>
            {
                try { _lastStop = _gb.StopAndRecord("conflict detected while HwSuite control is on"); Notice?.Invoke(_lastStop.Message); }
                catch (Exception ex) { _lastStop = new(false, new(), running, "stop failed: " + ex.Message); Log.Error("auto-stop", ex); }
                finally { _stopping = false; }
            });
        }

        string gbReason = "";
        if (running.Count > 0)
        {
            string names = string.Join(", ", running);
            gbReason = _stopping ? $"stopping {names}; HwSuite waits until they have exited"
                : !autoStop ? $"{names} running and auto-stop is off; HwSuite does not write while they may"
                : !(fanOn || rgbOn) ? $"{names} running"
                : _stopAttempts >= MaxAutoStopAttempts ? $"{names} still running after {_stopAttempts} stop attempts; HwSuite is not writing (use System > Stop services)"
                : $"{names} running; " + (_lastStop is { Verified: false } ? "the last stop attempt failed (" + _lastStop.Message + "), retrying" : "stop pending");
        }

        var procs = _probe.RunningProcessNames();
        AfterburnerRunning = procs.Contains("MSIAfterburner");
        string? gpuReason = null;
        if (AfterburnerRunning)
        {
            AfterburnerFanSetting ab;
            try { ab = _afterburnerConfig(); } catch (Exception ex) { ab = new(false, null, null, "read failed: " + ex.Message); }
            AfterburnerSetting = ab;
            AfterburnerAutoFan = ab.Known ? ab.SoftwareCurveOn : null;
            // Only a complete, stable, explicit SwAutoFanControl=0 can let HwSuite take the GPU fans while Afterburner runs.
            if (!ab.Known) gpuReason = "MSI Afterburner is running and its fan setting is unknown (" + ab.Detail + "), so ownership of the GPU fans cannot be established";
            else if (ab.SoftwareCurveOn == true) gpuReason = "MSI Afterburner's software fan curve is on (SwAutoFanControl=1) and re-applies its own speeds; HwSuite leaves the GPU fans to it";
            else if (_gpuForeignManual()) gpuReason = "MSI Afterburner holds a fixed manual GPU fan speed; HwSuite leaves the GPU fans to it";
        }
        else { AfterburnerAutoFan = null; AfterburnerSetting = null; }

        lock (_gate)
        {
            if (_gpuLatched != null && _gpuLatchedWithAfterburner && !AfterburnerRunning)
            {
                _gpuLatched = null;   // the program that was fighting has exited
                Log.Info("MSI Afterburner exited: GPU fan ownership latch cleared");
            }
            var gb = running.Count > 0 ? new OwnershipDecision(false, gbReason) : OwnershipDecision.Owned;
            _state[HwResource.SuperIo] = gb;
            _state[HwResource.Smbus] = gb;
            _state[HwResource.RgbUsb] = gb;
            _state[HwResource.GpuFans] = _gpuLatched != null ? new(false, _gpuLatched) : gpuReason != null ? new(false, gpuReason) : OwnershipDecision.Owned;
            _state[HwResource.Mouse] = procs.Contains("Glorious Core")
                ? new(false, "Glorious CORE is running and also writes to the mouse; HwSuite leaves the mouse lighting alone")
                : OwnershipDecision.Owned;
            _state[HwResource.Keyboard] = procs.Contains("DeviceDriver")
                ? new(false, "the iLovbee keyboard software (DeviceDriver.exe) is running")
                : OwnershipDecision.Owned;
        }
        LastRefresh = DateTime.Now;
    }
}
