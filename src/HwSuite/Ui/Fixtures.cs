using System.Reflection;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Lighting;

namespace HwSuite.Ui;

/// <summary>A fan channel that exists only in a fixture: it reports fixed readings and refuses every write.</summary>
public sealed class FixtureFanChannel : IFanChannel
{
    public FixtureFanChannel(string id, string name, string backend, HwResource resource, string? tachId, double? rpm, double? percent)
    { Id = id; Name = name; Backend = backend; Resource = resource; TachReadingId = tachId; Rpm = rpm; Percent = percent; }
    public string Id { get; }
    public string Name { get; }
    public string Backend { get; }
    public FanKind DefaultKind => Resource == HwResource.GpuFans ? FanKind.GpuFan : FanKind.Generic;
    public bool CanControl => true;
    public bool HasTachSignal => Rpm is > 0;
    public bool HasTachSensor => TachReadingId != null;
    public double MinPercent => Resource == HwResource.GpuFans ? 30 : 0;
    public string? TachReadingId { get; }
    public double? Rpm { get; set; }
    public double? Percent { get; set; }
    public double? ReadRpm() => Rpm;
    public double? ReadPercent() => Percent;
    public bool SetPercent(double percent) => false;   // fixtures never drive anything
    public bool ReleaseToAuto() => true;
    public bool RestoreOriginal() => true;
    public void Relinquish() { }
    public HwResource Resource { get; }
    public int? ReadTargetPercent() => null;
}

/// <summary>Keyboard controller facts a fixture shows on the Lighting page (instead of a real controller).</summary>
public sealed class KeyboardFixture
{
    public bool Present = true, Connected = true;
    public string LinkStatus = "connected", Status = "live frames; stored fallback up to date";
    public long Applies = 3, LiveFrames = 41_220, Failures, LiveFailures, TestWrites;
    public string? VendorBlock;
    public string Profile = "vendor profile 1: sleep 10 min (kept)";
}

/// <summary>
/// A simulated UI state for screenshots and visual checks. It starts from a read-only copy of the owner's configuration
/// (docs\phase11\fixture-config.json, embedded; or --fixture-config) and changes it in memory only: ConfigStore is
/// read-only in this mode, no hardware backend is opened, no lighting or fan write can happen (the fan engine and the
/// lighting engine are never started, fan channels are <see cref="FixtureFanChannel"/>). Every capture is labelled
/// "Simulated fixture" in the window and in its file name; it is not a live hardware observation.
/// </summary>
public sealed class UiFixture
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Page { get; init; } = "dashboard";
    public string? SelectChannel { get; init; }
    public bool ShowActivation { get; set; }
    /// <summary>Lighting fixtures with an animated scene: --film captures their preview frame by frame.</summary>
    public bool Film { get; init; }
    /// <summary>Also capture the Look selector's open list (checks that names, notes and swatches fit).</summary>
    public bool OpenLooks { get; init; }
    /// <summary>Also capture the "Colours from image" dialog: "result" (a generated rainy-forest picture), "error" or "empty".</summary>
    public string? PaletteDemo { get; init; }
    /// <summary>Also capture the page with a device card selected through its shortcut.</summary>
    public DeviceRole? FocusDevice { get; init; }
    /// <summary>1.15: also capture the case lighting setup opened (the case layout editor).</summary>
    public bool CaseSetup { get; init; }
    /// <summary>Simulate a lighting edit session (so the Revert bar shows) before capturing.</summary>
    public bool EditSession { get; init; }
    public bool LightingScrollDevices { get; init; }
    public Action<AppConfig>? Configure { get; init; }
    /// <summary>Puts the rendered window into a view state (for example Arrange mode). Display only.</summary>
    public Action<HwSuite.Ui.MainWindow>? Prepare { get; init; }
    public Dictionary<DeviceRole, DeviceFacts> Devices { get; } = new();
    public KeyboardFixture Keyboard { get; } = new();
    public int[] RamLeds { get; set; } = { 5, 8, 5, 8 };
    public List<string> Alerts { get; } = new();
    public Dictionary<string, ChannelStatus> FanStatus { get; } = new();
    public bool Emergency { get; init; }
    public string MouseLinkStatus { get; set; } = "receiver";
    public string MouseLast { get; set; } = "Breathing, primary colour, speed 3";
    public bool MousePresent { get; set; } = true;
    public double MinutesOfHistory { get; init; } = 6;

    public const string Notice = "Simulated fixture: not a live hardware observation";

    /// <summary>Owner configuration copy (read-only). Never written back.</summary>
    public static AppConfig LoadConfig(string? path)
    {
        string json;
        if (path != null) json = File.ReadAllText(path);
        else
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("fixture-config.json");
            if (s == null) return new AppConfig();
            json = new StreamReader(s).ReadToEnd();
        }
        return ConfigStore.Parse(json) ?? new AppConfig();
    }

    static DeviceFacts Ok(DeviceRole r) => new(r, Present: true, Available: true, Blocked: null, Included: true, Errors: 0, Sent: 1200, Fault: null) { Ack = LightAck.Accepted };

    static UiFixture Base(string id, string title, string page, Action<AppConfig>? cfg = null, string? channel = null)
    {
        var f = new UiFixture { Id = id, Title = title, Page = page, Configure = cfg, SelectChannel = channel };
        foreach (DeviceRole r in Enum.GetValues(typeof(DeviceRole))) f.Devices[r] = Ok(r);
        return f;
    }

    const string Rear = "lhm:/lpc/it8689e/0/control/1", Upper = "lhm:/lpc/it8689e/0/control/2", Lower = "lhm:/lpc/it8689e/0/control/3";

    static void LightsOn(AppConfig c, EffectKind e)
    {
        c.Rgb.Effect = e; c.Rgb.Enabled = true; c.Rgb.LightsOutRestoreEffect = null; c.Rgb.LightsOutRestoreEnabled = null;
    }

    /// <summary>Every fixture, in capture order.</summary>
    public static List<UiFixture> All() => new()
    {
        Dashboard(),
        new UiFixture { Id = "dashboard-warmup", Title = "Dashboard, history still collecting", Page = "dashboard", MinutesOfHistory = 1.4, Configure = c => LightsOn(c, EffectKind.Breathing) } is var w ? WithDevices(w) : null!,
        Base("lighting-live", "Keyboard live, mouse onboard", "lighting", c => LightsOn(c, EffectKind.Breathing)),
        Base("lighting-wave", "Gradient Wave", "lighting", c => LightsOn(c, EffectKind.GradientWave)),
        Arranged("lighting-arrange", "Arrange devices (editing, keyboard selected)", editing: true),
        Arranged("lighting-arranged", "Arranged layout, normal view with the Rainfall look", editing: false),
        AckStates(),
        DashboardAck(),
        CaseFans(),
        FirmwarePreview(),
        LightsOutFixture(),
        DeviceProblems(),
        Recovery(),
        Activation(),
        Base("system-theme", "System and theme selector", "system"),
        SceneFixture("lighting-rain", "Window Rain (Rainfall, light rain)", "Window Rain"),
        SceneFixture("lighting-downpour", "Midnight Downpour (Rainfall, dense)", "Midnight Downpour"),
        SceneFixture("lighting-mist", "Mountain Fog (Rolling Mist)", "Mountain Fog"),
        SceneFixture("lighting-fireflies", "Forest Fireflies", "Forest Fireflies"),
        SceneFixture("lighting-water", "Moonlit Lake (Moonlit Water)", "Moonlit Lake"),
        SceneFixture("lighting-storm", "Distant Thunder (Distant Storm, soft lightning)", "Distant Thunder"),
        SceneFixture("lighting-pines", "Moon Through Pines (Canopy)", "Moon Through Pines"),
        SceneFixture("lighting-canopy", "Still Canopy (Canopy, quietest)", "Still Canopy"),
        SceneFixture("lighting-drip", "Dripping Leaves (Drip, 1.15)", "Dripping Leaves"),
        SceneFixture("lighting-pond", "Rain on the Pond (Rain Ripples, 1.15)", "Rain on the Pond"),
        SceneFixture("lighting-monsoon", "Monsoon (Downpour with lightning, 1.15)", "Monsoon"),
        SceneFixture("lighting-neon", "Neon Rain (Rainfall, 1.15 starter)", "Neon Rain"),
        SceneFixture("lighting-case-known", "Window Rain with a known case layout (4 fans x 12 + logo 6)", "Window Rain", c =>
        {
            c.Rgb.DLedCount = 54; c.Rgb.Case = new CaseLayout { LedsPerFan = 12, LogoLeds = 6 };
        }, caseSetup: true),
        SceneFixture("lighting-case-mirrored", "Dripping Leaves on the LANCOOL 215 splitter hub (every fan the same 8-LED ring, logo 3)", "Dripping Leaves", c =>
        {
            c.Rgb.DLedCount = 8; c.Rgb.Case = new CaseLayout { Mirrored = true, LedsPerFan = 8, LogoLeds = 3, TopFans = 2, TopLed = 3, CounterClockwise = true };
        }, caseSetup: true),
        SceneFixture("lighting-case-assumed", "Window Rain with the LED split assumed (27 LEDs)", "Window Rain", caseSetup: true),
        SceneFixture("lighting-scene-fallbacks", "Window Rain: memory Onboard, keyboard without live frames (companions)", "Window Rain", c =>
        {
            c.Rgb.RamOnboardEffects = true; c.KeyboardLiveVerified = false;
        }, scroll: true),
        SceneFixture("lighting-rain-turned", "Window Rain, arranged desk with the keyboard turned 90°", "Window Rain", c =>
        {
            DeskArrangement(c);
            c.Arrangement.Set(DeviceRole.Keyboard, new DevicePlacement { X = 230, Y = 230, Rotation = 90 });
        }),
        SceneFixture("lighting-looks-open", "Look list open (Teal Tide current, one saved look)", "Window Rain", c =>
        {
            LightingLooks.Apply(c, LightingLooks.Starters.First(s => s.Name == LightingLooks.TealTide));
            c.Looks.Add(new LightingLook { Name = "Late evening rain", Effect = EffectKind.Rainfall, ColorA = 0x9FB8C8, ColorB = 0x24484C, Brightness = 0.4, Speed = 0.25, Density = 0.25, Glow = 0.35 });
        }, openLooks: true),
        SceneFixture("lighting-arrange-rain", "Arrange devices during Window Rain (what moving each device changes)", "Window Rain", c => DeskArrangement(c), prepare: w => w.LightingView.SetArranging(true)),
        Base("lighting-revert", "Revert bar after trying Midnight Downpour (started from Window Rain)", "lighting", c => { LightsOn(c, EffectKind.Static); LightingLooks.Apply(c, LightingLooks.Starters.First(s => s.Name == "Window Rain")); }) is var rv
            ? new UiFixture { Id = rv.Id, Title = rv.Title, Page = rv.Page, Configure = rv.Configure, EditSession = true }.WithAllDevices() : null!,
        new UiFixture { Id = "lighting-device-memory", Title = "Memory card reached by clicking its drawing", Page = "lighting", FocusDevice = DeviceRole.Ram,
            Configure = c => { LightsOn(c, EffectKind.Static); LightingLooks.Apply(c, LightingLooks.Starters.First(s => s.Name == "Window Rain")); } }.WithAllDevices(),
        new UiFixture { Id = "lighting-palette", Title = "Colours from image: a generated rainy-forest picture (simulated preview)", Page = "lighting", PaletteDemo = "result",
            Configure = c => { LightsOn(c, EffectKind.Static); LightingLooks.Apply(c, LightingLooks.Starters.First(s => s.Name == "Window Rain")); } }.WithAllDevices(),
        new UiFixture { Id = "lighting-palette-error", Title = "Colours from image: a file that is not an image", Page = "lighting", PaletteDemo = "error",
            Configure = c => { LightsOn(c, EffectKind.Static); LightingLooks.Apply(c, LightingLooks.Starters.First(s => s.Name == "Window Rain")); } }.WithAllDevices(),
    };

    /// <summary>
    /// A generated stand-in for a rainy-forest wallpaper (deterministic; not the owner's image): slate sky with a soft moon,
    /// misty mountain bands, dark evergreen tree line, teal undergrowth, a few silver rain streaks.
    /// </summary>
    public static System.Windows.Media.Imaging.BitmapSource RainyForestImage(int w = 480, int h = 270)
    {
        var px = new byte[w * h * 4];
        var rnd = new Random(1314);
        double Hill(double x, double f, double ph) => Math.Sin(x * f + ph) * 0.5 + Math.Sin(x * f * 2.3 + ph * 1.7) * 0.25;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double u = x / (double)w, v = y / (double)h;
                // sky: slate blue to grey-teal, moon glow upper right
                double r = 70 + 40 * (1 - v), g = 92 + 38 * (1 - v), b = 110 + 34 * (1 - v);
                double moon = Math.Exp(-(((u - 0.78) * (u - 0.78)) + ((v - 0.18) * (v - 0.18)) * 3) / 0.004);
                r += 150 * moon; g += 150 * moon; b += 140 * moon;
                // far mountains (misty), near mountains
                if (v > 0.42 + 0.08 * Hill(u, 7, 0.4)) { r = 96 - 10 * v; g = 120 - 10 * v; b = 126 - 10 * v; }
                if (v > 0.56 + 0.07 * Hill(u, 11, 2.1)) { r = 54; g = 82; b = 84; }
                // mist band
                double mist = Math.Exp(-((v - 0.6) * (v - 0.6)) / 0.002);
                r += 60 * mist; g += 66 * mist; b += 66 * mist;
                // evergreen tree line (spiky) and teal undergrowth
                double tree = 0.68 + 0.08 * Math.Abs(Math.Sin(u * 90)) + 0.04 * Hill(u, 23, 1.3);
                if (v > tree) { r = 18; g = 44; b = 36; }
                if (v > 0.9) { r = 30; g = 76; b = 72; }
                int o = (y * w + x) * 4;
                double n = rnd.NextDouble() * 6 - 3;
                px[o] = (byte)Math.Clamp(b + n, 0, 255); px[o + 1] = (byte)Math.Clamp(g + n, 0, 255); px[o + 2] = (byte)Math.Clamp(r + n, 0, 255); px[o + 3] = 255;
            }
        // silver rain streaks
        for (int s = 0; s < 70; s++)
        {
            int x0 = rnd.Next(w), y0 = rnd.Next(h - 20), len = 6 + rnd.Next(10);
            for (int k = 0; k < len; k++) { int o = ((y0 + k) * w + x0) * 4; px[o] = 214; px[o + 1] = 206; px[o + 2] = 196; }
        }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>A starter look from the rainy-forest set, lights on (simulated).</summary>
    static UiFixture SceneFixture(string id, string title, string look, Action<AppConfig>? extra = null, bool scroll = false, bool openLooks = false, Action<HwSuite.Ui.MainWindow>? prepare = null, bool caseSetup = false)
    {
        var f = new UiFixture
        {
            Id = id, Title = title, Page = "lighting", Film = !openLooks && prepare == null, LightingScrollDevices = scroll, OpenLooks = openLooks, Prepare = prepare, CaseSetup = caseSetup,
            Configure = c =>
            {
                LightsOn(c, EffectKind.Static);
                LightingLooks.Apply(c, LightingLooks.Starters.First(s => s.Name == look));
                extra?.Invoke(c);
            }
        };
        return f.WithAllDevices();
    }

    /// <summary>First film time: a few seconds in; Distant Storm with lightning starts shortly before a soft flash.</summary>
    public static double FilmStart(RgbConfig c)
    {
        if (EffectInfo.UsesLightning(c.Effect) && c.Lightning)
            for (double t = 1; t < 400; t += 0.05)
                if (Atmosphere.LightningLevel(t, out _, out _) > 0.05) return Math.Max(0, t - 3);
        return c.Effect == EffectKind.RollingMist ? 40.0 : 3.0;   // mist: a stretch with both fog banks and clear forest
    }

    public static UiFixture? Find(string id) => All().FirstOrDefault(f => f.Id == id);

    static UiFixture Dashboard()
    {
        var f = Base("dashboard", "Dashboard, populated history", "dashboard", c => LightsOn(c, EffectKind.Breathing));
        return f;
    }

    static UiFixture WithDevices(UiFixture f) { foreach (DeviceRole r in Enum.GetValues(typeof(DeviceRole))) f.Devices[r] = Ok(r); return f; }

    static UiFixture Activation()
    {
        var f = Base("activation", "Fan-control activation dialog (your saved modes)", "fans", null, Lower);
        f.ShowActivation = true;
        return f;
    }

    static UiFixture CaseFans()
    {
        var f = Base("cooling-case-fans", "Case fans under VANTA control (GPU and AIO on firmware)", "fans", c =>
        {
            c.FanControlEnabled = true;
            foreach (var ch in c.Channels)
                ch.Mode = ch.Kind == FanKind.CaseFan ? ChannelMode.Curve : ChannelMode.Auto;   // the dialog's "Case fans only"
        }, Lower);
        f.FanStatus[Rear] = Driven(Rear, 52, 51, 39.5);
        f.FanStatus[Upper] = Driven(Upper, 41, 41, 39.5);
        f.FanStatus[Lower] = Driven(Lower, 58, 57, 39.5);
        return f;
    }

    static ChannelStatus Driven(string id, double target, double applied, double temp) => new()
    {
        Id = id, Mode = ChannelMode.Curve, Controlled = true, TargetPercent = target, AppliedPercent = applied, SourceTempC = temp, EffectiveTempC = temp, State = "curve"
    };

    static UiFixture FirmwarePreview() =>
        Base("cooling-firmware-preview", "Firmware-controlled fan with a saved curve", "fans", c => { c.FanControlEnabled = false; }, ChannelDefaults.CpuFanChannelId);

    static UiFixture LightsOutFixture() => Base("lights-out", "Lights out", "lighting", c =>
    {
        c.Rgb.Enabled = true; c.Rgb.Effect = EffectKind.Stealth; c.Rgb.LightsOutRestoreEffect = EffectKind.Breathing; c.Rgb.LightsOutRestoreEnabled = true;
    });

    /// <summary>A desk-like arrangement: PC on the right, keyboard turned slightly is not possible (quarter turns only), mouse beside it.</summary>
    static void DeskArrangement(AppConfig c)
    {
        var a = new DeviceArrangement();
        a.Set(DeviceRole.Keyboard, new DevicePlacement { X = 300, Y = 290 });
        a.Set(DeviceRole.Mouse, new DevicePlacement { X = 555, Y = 300, Rotation = 0 });
        a.Set(DeviceRole.Strip, new DevicePlacement { X = 915, Y = 140 });
        a.Set(DeviceRole.Ram, new DevicePlacement { X = 770, Y = 125 });
        a.Set(DeviceRole.Board, new DevicePlacement { X = 635, Y = 110 });
        c.Arrangement = a;
    }

    static UiFixture Arranged(string id, string title, bool editing) => Base(id, title, "lighting", c =>
    {
        LightsOn(c, EffectKind.GradientWave);
        LightingLooks.Apply(c, LightingLooks.Starters[1]);   // Rainfall
        DeskArrangement(c);
    }) is var f ? new UiFixture
    {
        Id = f.Id, Title = f.Title, Page = f.Page, Configure = f.Configure,
        Prepare = editing ? w => { w.LightingView.SetArranging(true); } : null
    }.WithAllDevices() : null!;

    UiFixture WithAllDevices() { foreach (DeviceRole r in Enum.GetValues(typeof(DeviceRole))) Devices[r] = Ok(r); return this; }

    /// <summary>Acknowledgement states: memory still waiting for the current look, the case front's last update failed.</summary>
    static UiFixture AckStates()
    {
        var f = Base("lighting-ack", "Current look: one device waiting, one update not accepted", "lighting", c => { LightsOn(c, EffectKind.Static); LightingLooks.Apply(c, LightingLooks.Starters[2]); });
        f.Devices[DeviceRole.Ram] = f.Devices[DeviceRole.Ram] with { Ack = LightAck.Pending };
        f.Devices[DeviceRole.Strip] = f.Devices[DeviceRole.Strip] with { Ack = LightAck.Failed, Errors = 2 };
        return f;
    }

    static UiFixture DashboardAck()
    {
        var f = Base("dashboard-ack", "Dashboard: one device waiting, one update not accepted", "dashboard", c => LightsOn(c, EffectKind.Breathing));
        f.Devices[DeviceRole.Ram] = f.Devices[DeviceRole.Ram] with { Ack = LightAck.Pending };
        f.Devices[DeviceRole.Strip] = f.Devices[DeviceRole.Strip] with { Ack = LightAck.Failed, Errors = 2 };
        return f;
    }

    static UiFixture DeviceProblems()
    {
        var f = Base("device-problems", "Disconnected and blocked devices", "lighting", c => LightsOn(c, EffectKind.Breathing));
        f.Devices[DeviceRole.Mouse] = f.Devices[DeviceRole.Mouse] with { Present = false, Sent = 0 };
        f.MousePresent = false; f.MouseLinkStatus = "not connected";
        f.Devices[DeviceRole.Board] = f.Devices[DeviceRole.Board] with { Blocked = "RGBFusion process is running; VANTA waits until it has exited" };
        f.Devices[DeviceRole.Strip] = f.Devices[DeviceRole.Strip] with { Blocked = "RGBFusion process is running; VANTA waits until it has exited" };
        f.Alerts.Add("Gigabyte tools running: RGBFusion process. Board lighting withheld until it exits.");
        return f;
    }

    static UiFixture Recovery()
    {
        var f = Base("recovery", "Recovery and error states", "lighting", c =>
        {
            LightsOn(c, EffectKind.Breathing);
            c.FanControlEnabled = true;
            foreach (var ch in c.Channels) ch.Mode = ch.Kind == FanKind.CaseFan ? ChannelMode.Curve : ChannelMode.Auto;
        }, Lower);
        f.Keyboard.LinkStatus = "keyboard link failed after 4 reopen attempts; waiting for it to be unplugged and plugged in again";
        f.Keyboard.Status = f.Keyboard.LinkStatus;
        f.Keyboard.LiveFailures = 5;
        f.Devices[DeviceRole.Keyboard] = f.Devices[DeviceRole.Keyboard] with { Errors = 5, Fault = "the keyboard link failed after 4 reopen attempts; unplug and reconnect it" };
        f.FanStatus[Rear] = Driven(Rear, 52, 51, 41);
        f.FanStatus[Upper] = Driven(Upper, 41, 41, 41);
        var lower = Driven(Lower, 58, 100, 41); lower.State = "start check: 100% start kick 1/2";
        f.FanStatus[Lower] = lower;
        f.Alerts.Add("Front intake—SYS_FAN3 (lower): did not start at 50%. Running it at 100% for 6 s (start attempt 1 of 2).");
        f.Alerts.Add("keyboard: the link failed after 4 reopen attempts; waiting for it to be unplugged and plugged in again");
        return f;
    }

    // ---- population ---------------------------------------------------------------------------------------------------

    /// <summary>Simulated channels for this PC's verified header layout and the owner's two GPU fans.</summary>
    public static List<FixtureFanChannel> Channels() => new()
    {
        new("gpu:0", "GPU Fan 1", "NVML", HwResource.GpuFans, "gpu:0", 1180, 34),
        new("gpu:1", "GPU Fan 2", "NVML", HwResource.GpuFans, "gpu:1", 1175, 34),
        new(ChannelDefaults.CpuFanChannelId, "ITE IT8689E Fan #1", "SuperIO (PawnIO)", HwResource.SuperIo, "/lpc/it8689e/0/fan/0", 1490, 62),
        new(Rear, "ITE IT8689E Fan #2", "SuperIO (PawnIO)", HwResource.SuperIo, "/lpc/it8689e/0/fan/1", 905, 51),
        new(Upper, "ITE IT8689E Fan #3", "SuperIO (PawnIO)", HwResource.SuperIo, "/lpc/it8689e/0/fan/2", 610, 41),
        new(Lower, "ITE IT8689E Fan #4", "SuperIO (PawnIO)", HwResource.SuperIo, "/lpc/it8689e/0/fan/3", 880, 57),
        new("lhm:/lpc/it8689e/0/control/4", "ITE IT8689E Fan #5", "SuperIO (PawnIO)", HwResource.SuperIo, "/lpc/it8689e/0/fan/4", 0, 40),
        new("lhm:/lpc/it8689e/0/control/5", "ITE IT8689E Fan #6", "SuperIO (PawnIO)", HwResource.SuperIo, null, null, 40),
    };

    /// <summary>Fill the services with this fixture's simulated state. No device is opened.</summary>
    public void Populate(AppServices svc, DateTime now)
    {
        var chans = Channels();
        svc.Hub.Engine.Channels.AddRange(chans);
        svc.Hub.PawnIoInstalled = true; svc.Hub.PawnIoVersion = "2.0.1 (simulated)";
        svc.Hub.Engine.Emergency = Emergency;
        foreach (var ch in chans)
        {
            if (FanStatus.TryGetValue(ch.Id, out var st)) { svc.Hub.Engine.Status[ch.Id] = st; if (st.AppliedPercent is double a) ch.Percent = a; }
            else svc.Hub.Engine.Status[ch.Id] = new ChannelStatus { Id = ch.Id, Mode = ChannelMode.Auto, State = svc.Config.FanControlEnabled ? "firmware control" : "firmware control (fan control off)" };
        }
        // 6 minutes of history (1 s samples), so the chart shows a fully populated window
        var rnd = new Random(11);
        var start = now - TimeSpan.FromMinutes(MinutesOfHistory);
        for (var t = start; t <= now; t += TimeSpan.FromSeconds(1))
        {
            double m = (t - start).TotalSeconds;
            double cpu = 41 + 6 * Math.Sin(m / 47.0) + 9 * Math.Max(0, Math.Sin(m / 95.0 - 1.2)) + rnd.NextDouble() * 1.2;
            double gpu = 38 + 4 * Math.Sin(m / 83.0 + 0.6) + rnd.NextDouble() * 0.8;
            svc.History.Add(new HistoryPoint(t, Math.Round(cpu, 1), Math.Round(gpu, 1), 46 + 2 * Math.Sin(m / 120.0), 44 + 1.5 * Math.Sin(m / 70.0)));
        }
        var last = svc.History.Since(TimeSpan.FromSeconds(2), now).LastOrDefault();
        var snap = new TelemetrySnapshot
        {
            Time = now, CpuTempC = last.Cpu, CpuLoadPct = 14.2, CpuClockMHz = 4689, CpuPowerW = 38.5, CpuTempSource = "Core (Tctl/Tdie) package (simulated)", CpuTempStatus = "ok",
            GpuName = "NVIDIA GeForce RTX 3070", GpuTempC = last.Gpu, GpuLoadPct = 7, GpuCoreMHz = 210, GpuMemMHz = 405, GpuPowerW = 24.6, GpuTempStatus = "ok",
            RamUsedPct = 46.3, RamUsedGb = 14.7, RamTotalGb = 31.8
        };
        foreach (var ch in chans.Where(c => c.TachReadingId != null))
            snap.Fans.Add(new FanReading { Id = ch.TachReadingId!, Name = ch.Name.Replace("ITE IT8689E ", ""), Source = ch.Resource == HwResource.GpuFans ? "NVML" : "ITE IT8689E", Rpm = ch.Rpm, Percent = ch.Percent });

        foreach (var (n, v) in new[] { ("System #1", 38.0), ("Chipset", 46.5), ("PCH", 44.0), ("VRM MOS", 41.0), ("PCIe x16", 35.0) })
            snap.Temps.Add(new TempReading { Id = n, Name = n, Source = "ITE IT8689E", Celsius = v });
        svc.Hub.Latest = snap;
        foreach (var a in Alerts) svc.Alerts.Add(a);
    }
}
