// PORTED from src\HwSuite\AppServices.cs (Windows 1.20.0): SMBus RAM lighting is tried on Linux (no PawnIO gate);
// sleep/resume come from the control socket instead of Microsoft.Win32.SystemEvents. Everything else unchanged.
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite;

/// <summary>Recent events the owner should see (faults, lost feedback, ownership changes). Thread-safe, bounded.</summary>
public sealed class AlertLog
{
    readonly List<(DateTime time, string text)> _items = new();
    public void Add(string text)
    {
        lock (_items)
        {
            if (_items.Count > 0 && _items[^1].text == text && (DateTime.Now - _items[^1].time).TotalSeconds < 60) return;
            _items.Add((DateTime.Now, text));
            while (_items.Count > 50) _items.RemoveAt(0);
        }
    }
    public List<(DateTime time, string text)> Recent(TimeSpan window)
    {
        lock (_items) return _items.Where(i => DateTime.Now - i.time <= window).ToList();
    }
}

/// <summary>Composition root shared by the GUI, the diagnostics and the self-test.</summary>
public sealed class AppServices : IDisposable
{
    public AppConfig Config { get; }
    public TelemetryHub Hub { get; }
    public RgbEngine Rgb { get; private set; }
    public SmbusI801? Smbus { get; private set; }
    public EneDramController? Ene { get; private set; }
    public GigabyteGuard Gigabyte { get; }
    public OwnershipArbiter? Ownership { get; private set; }
    public AlertLog Alerts { get; } = new();
    /// <summary>Telemetry history recorded from every update, whichever page is open.</summary>
    public TelemetryHistory History { get; } = new();
    public List<string> Notes { get; } = new();
    /// <summary>"Revert changes": one in-memory snapshot of the lighting appearance (see <see cref="LightingEditSession"/>).</summary>
    public LightingEditSession LightingEdits { get; }
    /// <summary>1.18: the YUNZII AL80 screen/lighting service (null in fixtures and before Start). Opens nothing until Al80.Enabled.</summary>
    public HwSuite.Hardware.Al80.Al80Service? Al80 { get; private set; }
    /// <summary>Simulated UI state (screenshots and visual checks): no hardware is opened and nothing is written.</summary>
    public HwSuite.Ui.UiFixture? Fixture { get; init; }

    readonly AfterburnerConfigWatcher _afterburner = AfterburnerConfigWatcher.CreateLive();
    readonly Timer _saveTimer;
    bool _powerHooked;

    static string MouseStatePath => Path.Combine(AppPaths.Data, "mouse-lighting.json");
    static string KeyboardStatePath => Path.Combine(AppPaths.Data, "keyboard-lighting.json");

    readonly Lazy<ProcessPresence> _keyboardVendor = new(() => new ProcessPresence("DeviceDriver.exe"));
    const string KeyboardVendorRunning = "the iLovbee keyboard software (DeviceDriver.exe) is running";
    /// <summary>How the iLovbee software is detected (status line and report).</summary>
    public string KeyboardVendorWatch => _keyboardVendor.IsValueCreated ? _keyboardVendor.Value.Mode : "not started";

    /// <summary>
    /// Keyboard ownership asked before every live frame (also while lighting output is off): the vendor software's state
    /// from <see cref="ProcessPresence"/> (an observation at most <see cref="ProcessPresence.MaxAge"/> old, listed on the spot
    /// when the poll is late; an enumeration failure blocks the write), then the arbiter's decision.
    /// </summary>
    public string? LiveKeyboardOwnership()
    {
        if (Fixture != null) return Fixture.Keyboard.VendorBlock;
        try { if (_keyboardVendor.Value.IsRunning) return VendorReason(); }
        catch (Exception ex) { return "could not check for the iLovbee software: " + ex.Message; }
        return Ownership?.Check(HwResource.Keyboard) is { MayWrite: false } d ? d.Reason : null;
    }

    /// <summary>The same, with a process list right now: asked before every stored ("LED(Save)") and test write.</summary>
    public string? StoredKeyboardOwnership()
    {
        try { if (_keyboardVendor.Value.CheckNow()) return VendorReason(); }
        catch (Exception ex) { return "could not check for the iLovbee software: " + ex.Message; }
        return Ownership?.Check(HwResource.Keyboard) is { MayWrite: false } d ? d.Reason : null;
    }

    string VendorReason() => _keyboardVendor.Value.LastState == ProcessPresence.State.Unknown
        ? "could not list processes to check for the iLovbee software (DeviceDriver.exe); not writing until it can be checked"
        : KeyboardVendorRunning;

    static T? LoadState<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    static void SaveState<T>(string path, T? s)
    {
        if (ConfigStore.ReadOnly) return;   // test runs leave the owner's device state alone
        try { File.WriteAllText(path, JsonSerializer.Serialize(s)); } catch { }
    }

    /// <summary>Fixture services: the given in-memory configuration and simulated state, with no hardware opened.</summary>
    public static AppServices ForFixture(AppConfig cfg, HwSuite.Ui.UiFixture fixture, DateTime now)
    {
        var s = new AppServices(cfg) { Fixture = fixture };
        fixture.Populate(s, now);
        return s;
    }

    public AppServices(AppConfig? config = null)
    {
        Config = config ?? ConfigStore.Load();
        LightingEdits = new LightingEditSession(Config);
        Hub = new TelemetryHub(Config);
        Hub.Updated += s =>
        {
            var temps = s.Temps.Where(t => t.Celsius is > 0).Select(t => t.Celsius!.Value).ToList();
            History.Add(new HistoryPoint(s.Time, s.CpuTempC, s.GpuTempC, s.RamUsedPct, temps.Count > 0 ? temps.Max() : null));
        };
        Rgb = new RgbEngine(Config, () => Hub.Latest.MaxTempC) { Layout = new LayoutTransform(Config.Arrangement, 1, Config.Rgb.Case) };
        Gigabyte = GigabyteGuard.CreateLive();
        _saveTimer = new Timer(_ => { try { ConfigStore.Save(Config); } catch { } }, null, Timeout.Infinite, Timeout.Infinite);
    }

    void ScheduleSave() => _saveTimer.Change(2000, Timeout.Infinite);

    /// <summary>
    /// Apply a lighting look: the one path for the Lighting page and the tray menu. Changes the lighting configuration
    /// only (output on/off untouched; during Lights out only the look restored afterwards) and saves it.
    /// </summary>
    public void ApplyLook(LightingLook look, string from)
    {
        LightingEdits.BeforeEdit("look from " + from);
        LightingLooks.Apply(Config, look);
        ConfigStore.Save(Config);
        var c = Config.Rgb;
        Log.Info($"lighting look '{look.Name}' applied from {from}: {look.Summary}" + (LightsOut.IsOn(c) ? " (Lights out stays on; shown when the lights come back)" : !c.Enabled ? " (lighting output stays off)" : ""));
    }

    /// <summary>Revert the current lighting edit session (page or tray). False when there is nothing to revert.</summary>
    public bool RevertLighting(string from)
    {
        if (!LightingEdits.HasChanges) { LightingEdits.EndIfUnchanged(); return false; }
        LightingEdits.Revert(from);
        ConfigStore.Save(Config);
        return true;
    }

    public void KeepLighting(string from) => LightingEdits.Keep(from);

    /// <summary>
    /// Colours chosen from an image: only the given colour fields change (effect, brightness, speed, density, glow,
    /// lightning, inclusion and RAM mode are kept; Lights out and output off stay as they are). Part of the edit session.
    /// </summary>
    public void ApplyColours(int? primary, int? secondary, string from)
    {
        if (primary == null && secondary == null) return;
        LightingEdits.BeforeEdit(from);
        lock (Config.Sync)
        {
            if (primary is int a) Config.Rgb.ColorA = a & 0xFFFFFF;
            if (secondary is int b) Config.Rgb.ColorB = b & 0xFFFFFF;
        }
        ConfigStore.Save(Config);
        Log.Info($"lighting colours from {from}: " + string.Join(", ", new[] { primary is int p ? $"primary #{p:X6}" : null, secondary is int s ? $"secondary #{s:X6}" : null }.Where(x => x != null))
            + (LightsOut.IsOn(Config.Rgb) ? " (Lights out stays on)" : !Config.Rgb.Enabled ? " (lighting output stays off)" : ""));
    }

    /// <summary>Commit a finished arrangement edit: saved once, and the engine's layout replaced as a whole.</summary>
    public void CommitArrangement(DeviceArrangement arrangement)
    {
        lock (Config.Sync) Config.Arrangement = arrangement.Clone();
        Rgb.Layout = new LayoutTransform(arrangement, Rgb.Layout.Version + 1, Config.Rgb.Case);
        ConfigStore.Save(Config);
    }

    /// <summary>1.15: commit a case-layout edit (what is on the D_LED chain and where). Saved once; the engine's layout replaced as a whole.</summary>
    public void CommitCaseLayout(CaseLayout layout)
    {
        var c = layout.Clone(); c.Normalize();
        lock (Config.Sync) Config.Rgb.Case = c;
        Rgb.Layout = new LayoutTransform(Config.Arrangement, Rgb.Layout.Version + 1, c);
        ConfigStore.Save(Config);
        Log.Info($"case layout: {c.FrontFans} front fan(s){(c.RearFan ? ", rear fan" : "")}{(c.Logo ? ", logo" : "")}; {(c.CountsKnown ? $"{c.LedsPerFan} LEDs per fan, logo {c.LogoLeds}" : "LED split assumed")}; chain {CaseLayout.OrderLabel(c.Order)}");
    }

    public void InitializeHardware()
    {
        RunMode.Assert("initialise hardware");
        Hub.Initialize();
        Notes.AddRange(Hub.Notes);
        // First run: give every discovered channel a sensible curve but leave modes on Auto until the user opts in.
        lock (Config.Sync)
        {
            foreach (var ch in Hub.Engine.Channels)
            {
                var existing = Config.Channels.FirstOrDefault(c => c.Id == ch.Id);
                if (existing == null) Config.Channels.Add(ChannelDefaults.Create(ch, Config.Preset));
                else ChannelDefaults.Upgrade(existing, ch, Config.Preset);
            }
        }

        try { Gigabyte.PruneRecord(); } catch (Exception ex) { Log.Warn("gigabyte record: " + ex.Message); }
        Ownership = new OwnershipArbiter(Config, Gigabyte, LiveSystemProbe.Instance, () => _afterburner.Read(DateTime.UtcNow), Hub.GpuHasForeignManual);
        Hub.Arbiter = Ownership;
        Hub.Engine.Ownership = Ownership;
        Rgb.Ownership = Ownership;
        Ownership.Refresh();   // first ownership decision before anything can write
        Hub.Engine.ConfigDirty += ScheduleSave;
        Hub.Engine.Alert += Alerts.Add;
        Ownership.Notice += Alerts.Add;
    }

    internal static MouseLightingState? LoadMouseState()
    {
        try { return File.Exists(MouseStatePath) ? JsonSerializer.Deserialize<MouseLightingState>(File.ReadAllText(MouseStatePath)) : null; }
        catch { return null; }
    }

    static void SaveMouseState(MouseLightingState? s)
    {
        if (ConfigStore.ReadOnly) return;
        try { File.WriteAllText(MouseStatePath, JsonSerializer.Serialize(s)); } catch { }
    }

    public void InitializeLighting(bool allowRamRemap = true)
    {
        RunMode.Assert("initialise lighting devices");
        var rgb = Config.Rgb;

        var mb = new MotherboardSink(rgb);
        mb.TryOpen();
        Rgb.Sinks.Add(mb);
        // must come after the motherboard sink: its Activate() runs the controller reset that re-enables built-in strip effects
        Rgb.Sinks.Add(new DLedSink(rgb, mb));

        Rgb.Sinks.Add(new MouseSink(rgb, new GloriousTransport(), LoadMouseState(), SaveMouseState));

        if (OperatingSystem.IsLinux())
        {
            Smbus = SmbusI801.TryOpen(out var err);
            if (Smbus == null)
            {
                Rgb.Sinks.Add(new UnavailableSink("XPG Spectrix D60G RAM (ENE SMBus)", "SMBus i801", "SMBus backend unavailable: " + err));
            }
            else if (Smbus.PciVendor != 0x8086)
            {
                Rgb.Sinks.Add(new UnavailableSink("XPG Spectrix D60G RAM (ENE SMBus)", "SMBus i801", $"unexpected SMBus controller {Smbus.PciVendor:X4}:{Smbus.PciDevice:X4}"));
            }
            else
            {
                Ene = new EneDramController(Smbus);
                try { Ene.Detect(allowRamRemap); }
                catch (Exception ex) { Log.Error("ENE detection failed", ex); }
                Rgb.Sinks.Add(new RamSink(rgb, Ene));
            }
        }
        else
        {
            Rgb.Sinks.Add(new UnavailableSink("XPG Spectrix D60G RAM (ENE SMBus)", "SMBus i801", "not running on Linux"));
        }

        // 1.18.1: the YUNZII AL80 is the keyboard (the iLovbee i87 was removed from the app at the owner's request).
        Al80 ??= new HwSuite.Hardware.Al80.Al80Service(Config, () => Hub.Latest);
        Rgb.Sinks.Add(new Al80Sink(rgb, () => Al80, () => Config.Al80));
        foreach (var s in Rgb.Sinks) Notes.Add($"RGB {s.Name}: {(s.Available ? "ready" : "unavailable")} - {s.Status}");
    }

    public void Start(bool heartbeat = false)
    {
        RunMode.Assert("start the fan and lighting engines");
        Hub.Start(heartbeat);
        Rgb.Start();
        if (Fixture == null) { Al80 ??= new HwSuite.Hardware.Al80.Al80Service(Config, () => Hub.Latest); Al80.Start(); }
        _powerHooked = true;   // LINUX: suspend/resume arrive through the control socket from the systemd sleep hook (PowerEvent)
    }

    /// <summary>LINUX: called by the control socket ("suspend" / "resume", sent by /usr/lib/systemd/system-sleep/vanta).</summary>
    public void PowerEvent(bool suspend)
    {
        try
        {
            if (suspend)
            {
                Log.Info("system suspend: handing fans back to firmware and pausing lighting");
                Hub.Engine.Suspend();
                Rgb.Suspend();
                Al80?.Suspend();
            }
            else
            {
                Log.Info("system resume");
                Hub.Engine.Resume();
                Rgb.Resume();
                Al80?.Resume();
                Ownership?.ResetStopAttempts();
            }
        }
        catch (Exception ex) { Log.Error("power event", ex); }
    }

    public void Dispose()
    {
        _powerHooked = false;
        try { Rgb.Dispose(); } catch (Exception ex) { Log.Error("rgb dispose", ex); }
        try { Al80?.Dispose(); } catch (Exception ex) { Log.Error("al80 dispose", ex); }
        try { Hub.Dispose(); } catch (Exception ex) { Log.Error("hub dispose", ex); }
        try { Smbus?.Dispose(); } catch { }
        try { _saveTimer.Dispose(); } catch { }
        if (_keyboardVendor.IsValueCreated) try { _keyboardVendor.Value.Dispose(); } catch { }
        // controlled.json is not cleared here: a channel whose hand-back failed stays listed so the watchdog retries it.
        var left = ControlRegistry.Load();
        if (left.Count > 0) Log.Error($"{left.Count} channel(s) could not be handed back at exit; the watchdog will retry: " + string.Join(", ", left.Select(e => e.Backend + ":" + e.Id)));
    }
}
