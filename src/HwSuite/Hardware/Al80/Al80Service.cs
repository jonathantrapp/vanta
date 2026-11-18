using Al80Keys = HwSuite.Lighting.Devices.Al80KeyMap;
using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using HwSuite.Core;
using HwSuite.Hardware.Games;
using HwSuite.Lighting;

namespace HwSuite.Hardware.Al80;

/// <summary>
/// Owns the AL80 on one STA thread (WPF drawing needs STA). Home mode sends only tiny status packets (clock, battery-icon
/// gauge). Live mode uploads the System panel as a picture when it changes, at most every <see cref="Al80Config.LiveIntervalSec"/>,
/// and only while <see cref="Al80Config.FlashSlotsFilled"/> says uploads land in RAM. Everything else (GIFs, pictures, views,
/// lighting, keymap) is queued work run on the same thread, so nothing interleaves on the interface.
/// </summary>
public sealed class Al80Service : IDisposable
{
    readonly AppConfig _cfg;
    readonly Func<TelemetrySnapshot> _latest;
    readonly Func<Al80Device>? _open;
    readonly ConcurrentQueue<(string label, Action<Al80Device> job)> _jobs = new();
    readonly CancellationTokenSource _stop = new();
    Thread? _thread;
    Al80Device? _dev;
    DateTime _nextConnect = DateTime.MinValue, _clockAt = DateTime.MinValue, _gaugeAt = DateTime.MinValue, _renderAt = DateTime.MinValue, _uploadAt = DateTime.MinValue;
    int? _gaugeSent;
    bool _gaugeActive, _suspended;
    byte[]? _shown;

    public Al80Service(AppConfig cfg, Func<TelemetrySnapshot> latest, Func<Al80Device>? open = null)
    {
        _cfg = cfg; _latest = latest; _open = open;
    }

    // ---- state read by the page (written only by the service thread)
    public volatile string Status = "not started";
    public volatile string? LastError;
    public volatile string Busy = "";
    public bool Connected => _dev != null;
    public string DeviceName { get; private set; } = "";
    public int? ViaProtocol { get; private set; }
    public int? Layers { get; private set; }
    public int SessionPictures { get; private set; }
    public double LastUploadSeconds { get; private set; }
    public DateTime? LastUploadTime { get; private set; }
    public BitmapSource? Preview { get; private set; }
    /// <summary>Lighting values read from the keyboard: (channel, value id) -> data bytes.</summary>
    public ConcurrentDictionary<(byte ch, byte val), byte[]> Lighting { get; } = new();
    public event Action? Changed;

    Al80Config C => _cfg.Al80;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "AL80" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Enqueue(string label, Action<Al80Device> job) { _jobs.Enqueue((label, job)); }

    public void Suspend() { _suspended = true; }
    public void Resume() { _suspended = false; _nextConnect = DateTime.MinValue; _clockAt = DateTime.MinValue; }

    void Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Al80Exception ex) { Drop(ex.Message); }
            catch (Exception ex) { Log.Error("AL80 loop", ex); Drop(ex.Message); }
            WaitHandle.WaitAny(new[] { _stop.Token.WaitHandle, _wake }, _waitMs);
            _waitMs = 250;
        }
        try { if (_dev != null) FlushLight(_dev); } catch { }
        try { if (_dev != null && _gaugeActive) _dev.Send(Al80Protocol.GaugeReleaseStream()); } catch { }
        Close();
    }

    void Drop(string why)
    {
        LastError = why;
        Status = "disconnected: " + why;
        Log.Warn("AL80: " + why);
        Close();
        _nextConnect = DateTime.UtcNow.AddSeconds(5);
        Changed?.Invoke();
    }

    void Close()
    {
        try { _dev?.Dispose(); } catch { }
        _dev = null; _shown = null; _deadKey = null; _gaugeSent = null; _gaugeActive = false; _lightSent = null; _lightActive = false;
        _frameSent = null; _frameActive = false; PerKeyLeds = 0;
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        if (!C.Enabled || _suspended)
        {
            if (_dev != null)
            {
                try { if (_lightActive) { ReleaseLight(); FlushLight(_dev); } } catch { }
                try { if (_frameActive) { ReleaseFrame(); FlushFrame(_dev); } } catch { }
                try { if (_gaugeActive) _dev.Send(Al80Protocol.GaugeReleaseStream()); } catch { }
                Close();
            }
            if (Music.Running) { Music.Stop(); _musicKey = null; _musicHome = false; }
            Status = _suspended ? "paused for sleep" : "off (VANTA leaves the AL80 alone)";
            while (_jobs.TryDequeue(out _)) { }
            return;
        }
        if (_dev == null)
        {
            if (now < _nextConnect) return;
            Connect();
            if (_dev == null) return;
        }
        var dev = _dev;
        FlushLight(dev);

        while (_jobs.TryDequeue(out var j))
        {
            Busy = j.label;
            Changed?.Invoke();
            try { j.job(dev); Log.Info("AL80: " + j.label + " done"); }
            catch (Al80Exception) { Busy = ""; throw; }
            catch (Exception ex) { LastError = j.label + ": " + ex.Message; Log.Warn("AL80 " + j.label + ": " + ex.Message); }
            Busy = "";
            Changed?.Invoke();
        }

        var panel = _panelOverride;                       // 1.22: a game profile shows its own panel while the game runs (never saved)
        if (_overrideWas && panel == null) LeaveOverride(dev);
        _overrideWas = panel != null;
        string mode = panel != null ? "deadlock" : C.Mode;
        if (mode == "music") Music.Start(); else if (Music.Running) { Music.Stop(); _musicKey = null; _musicHome = false; }
        switch (mode)
        {
            case "home": HomeTick(dev, now); break;
            case "live": LiveTick(dev, now); break;
            case "music": MusicTick(dev, now); break;
            case "deadlock": DeadlockTick(dev, now, panel!); break;
            default:
                if (_gaugeActive) { dev.Send(Al80Protocol.GaugeReleaseStream()); _gaugeActive = false; _gaugeSent = null; }
                Status = "connected · screen left as it is";
                break;
        }
    }

    void Connect()
    {
        try
        {
            var d = _open != null ? _open() : Al80Device.OpenUsb();
            _dev = d;
            DeviceName = d.Name;
            LastError = null;
            try
            {
                ViaProtocol = d.Via(Al80Protocol.Via(0x01)) is var p ? p[1] << 8 | p[2] : null;
                Layers = d.Via(Al80Protocol.Via(0x11))[1];
                ReadLighting(d);
                CaptureStored();
            }
            catch (Al80Exception ex) { Log.Warn("AL80 VIA read: " + ex.Message); }
            DetectPerKey(d);
            _clockAt = DateTime.MinValue; _gaugeAt = DateTime.MinValue; _shown = null;
            Status = "connected";
            Log.Info($"AL80 connected: {d.Name}, VIA protocol {ViaProtocol}, {Layers} layers");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _dev = null;
            Status = "not connected: " + ex.Message;
            _nextConnect = DateTime.UtcNow.AddSeconds(5);
        }
    }

    public void ReadLighting(Al80Device d)
    {
        foreach (var ch in new[] { Al80Protocol.ChannelKeys, Al80Protocol.ChannelSide })
            for (byte v = 1; v <= 4; v++)
            {
                try { var r = d.Via(Al80Protocol.LightGet(ch, v)); Lighting[(ch, v)] = new[] { r[3], r[4] }; }
                catch (Al80Exception ex) when (ex.Message.StartsWith("no VIA reply")) { }
            }
    }

    // ------------------------------------------------------------------ live lighting (VANTA's frame colour)
    // Verified 2026-11-12 on the owner's AL80: VIA lighting writes without the save command are lost at power-off (RAM
    // only), effect 1 is a solid colour on both channels (3 keys, 2 side bar) and follows hue/saturation, 0 is off.
    readonly AutoResetEvent _wake = new(false);
    readonly object _lightGate = new();
    (byte h, byte s, byte v)? _lightWant, _lightSent;
    DateTime _lightAt;
    bool _lightHold, _lightActive;
    long _lastFlushTicks;
    int _waitMs = 250;
    readonly Dictionary<(byte ch, byte val), byte[]> _stored = new();
    public long LightWrites { get; private set; }
    public long LightFailures { get; private set; }
    public string LightStatus { get; private set; } = "not syncing";
    public Rgb? LightShown { get; private set; }
    /// <summary>The engine stopped offering frames this long ago without holding a colour: the keyboard gets its own setting back.</summary>
    public static readonly TimeSpan LightIdleRelease = TimeSpan.FromSeconds(3);
    public const double LightMaxHz = 30;

    public static (byte h, byte s, byte v) ToHsv(Rgb c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == c.R) h = 60 * (((c.G - c.B) / (double)d + 6) % 6);
            else if (max == c.G) h = 60 * ((c.B - c.R) / (double)d + 2);
            else h = 60 * ((c.R - c.G) / (double)d + 4);
        }
        return ((byte)(Math.Round(h / 360 * 256) % 256), (byte)(max == 0 ? 0 : Math.Round(255.0 * d / max)), (byte)max);
    }

    /// <summary>Show this colour on the keys (and the side bar when synced); <paramref name="hold"/> keeps it after frames stop (steady look at exit).</summary>
    public void SetLight(Rgb c, bool hold = false)
    {
        lock (_lightGate) { _lightWant = ToHsv(c); _lightAt = DateTime.UtcNow; _lightHold = hold; LightShown = c; }
        _wake.Set();
    }

    /// <summary>Stop syncing: the keyboard's own stored effect, brightness and colour come back.</summary>
    public void ReleaseLight()
    {
        lock (_lightGate) { _lightWant = null; _lightHold = false; }
        _wake.Set();
    }

    public bool LightSyncing => _lightActive || _frameActive;

    byte[] LightChannels => C.SyncSideBar ? new[] { Al80Protocol.ChannelKeys, Al80Protocol.ChannelSide } : new[] { Al80Protocol.ChannelKeys };

    void CaptureStored()
    {
        // after a reconnect without a power cut the keyboard still shows VANTA's live colour: keep the earlier snapshot then
        if (Lighting.TryGetValue((Al80Protocol.ChannelKeys, Al80Protocol.LightEffect), out var e) && e[0] == 1 && _stored.Count > 0) return;
        _stored.Clear();
        foreach (var kv in Lighting) _stored[kv.Key] = kv.Value.ToArray();
    }

    /// <summary>The page saved a channel: what it saved is the keyboard's own setting from now on.</summary>
    public void MarkSaved(byte ch)
    {
        foreach (var kv in Lighting.Where(k => k.Key.ch == ch)) _stored[kv.Key] = kv.Value.ToArray();
    }

    void Set(Al80Device d, byte ch, byte val, params byte[] data) { d.Via(Al80Protocol.LightSet(ch, val, data)); LightWrites++; }

    // ------------------------------------------------------------------ per-key frames (1.19.0, SignalRGB firmware only)
    // 0x25 once when frames start, then up to 10 stream packets per frame (changed 9-LED chunks; the whole frame once a
    // second), 0x26 when frames stop without a hold, the keyboard is excluded, AL80 control is turned off, or sleep. A held
    // frame (VANTA exiting / output off) stays on the keys until the next power cut, when the stored effect returns by
    // itself (nothing is written to EEPROM). Never mixed with VIA lighting writes (the device refuses them while active).
    Rgb[]? _frameWant, _frameSent;
    DateTime _frameAt, _frameFullAt;
    bool _frameHold, _frameActive;
    long _lastFrameTicks;
    public const double PerKeyMaxHz = 30;
    /// <summary>
    /// Whole frame at least this often while streaming. Firmware 1.2.2/1.2.5 lighting task: the SignalRGB flag turns on when
    /// a stream packet came &lt; 100 ms ago and off after 500 ms without one (then a saved LED setting is restored).
    /// </summary>
    public static readonly TimeSpan PerKeyKeepAlive = TimeSpan.FromMilliseconds(200);
    /// <summary>LEDs the SignalRGB firmware reported (0x27), 0 when per-key frames are not available.</summary>
    public int PerKeyLeds { get; private set; }
    public string PerKeyInfo { get; private set; } = "";
    public long FramesSent { get; private set; }
    public long FrameErrors { get; private set; }
    /// <summary>Per-key frames can be streamed now: SignalRGB firmware, it answered 84 LEDs, and the owner allows it.</summary>
    public bool PerKey => _dev != null && PerKeyLeds == Al80Keys.Leds && C.PerKey;
    public bool PerKeyStreaming => _frameActive;

    void DetectPerKey(Al80Device d)
    {
        PerKeyLeds = 0; PerKeyInfo = "";
        if (!d.SrgbFirmware) { PerKeyInfo = d.Revision is int r ? $"firmware rev {r:X4} has no SignalRGB handler: one colour for the board" : "firmware revision unknown: one colour for the board"; return; }
        try
        {
            var total = d.SrgbQuery(Al80Protocol.SrgbTotalLeds);
            var proto = d.SrgbQuery(Al80Protocol.SrgbProtocol);
            var type = d.SrgbQuery(Al80Protocol.SrgbFirmwareType);
            if (total == null) { PerKeyInfo = "SignalRGB firmware did not answer the LED count"; return; }
            PerKeyLeds = total[1];
            PerKeyInfo = $"SignalRGB firmware rev {d.Revision:X4}, protocol {(proto is { } p ? $"{p[1]}.{p[2]}.{p[3]}" : "?")}, type {(type is { } t ? t[1] : '?')}, {total[1]} LEDs";
            if (PerKeyLeds != Al80Keys.Leds) PerKeyInfo += $" (expected {Al80Keys.Leds}: per-key frames off)";
            Log.Info("AL80: " + PerKeyInfo);
        }
        catch (Al80Exception ex) { PerKeyInfo = "SignalRGB query failed: " + ex.Message; Log.Warn("AL80 " + PerKeyInfo); }
    }

    /// <summary>
    /// Show these colours on the keys (index = LED, <see cref="Al80Keys"/>). <paramref name="side"/> is the colour for the
    /// separate 3-LED light (QMK rgblight, VIA channel 2) while <see cref="Al80Config.SyncSideBar"/> is on; null keeps the
    /// last one. <paramref name="hold"/> keeps them after frames stop.
    /// </summary>
    public void SetFrame(Rgb[] leds, Rgb? side = null, bool hold = false)
    {
        if (leds.Length != Al80Keys.Leds) throw new ArgumentException($"an AL80 frame is {Al80Keys.Leds} colours");
        lock (_lightGate) { _frameWant = (Rgb[])leds.Clone(); _frameAt = DateTime.UtcNow; _frameHold = hold; if (side is Rgb c) _sideWant = ToHsv(c); }
        _wake.Set();
    }

    // ---- the 3-LED light (rgblight, VIA channel 2) while per-key frames stream (1.19.0)
    // Ghidra (1.2.5 VIA handler): channel 2 sets go to rgblight only (sethsv/mode/enable with write = 0, "rgblight ...
    // [NOEEPROM]"); they never touch rgb_matrix or the SignalRGB flag. Only 0x09 on channel 2 saves. So volatile channel-2
    // writes may run between stream frames (same device lock, never inside a screen transfer); channel 3 and 0x09 stay refused.
    (byte h, byte s, byte v)? _sideWant, _sideSent;
    bool _sideActive;
    long _lastSideTicks;
    public const double SideMaxHz = 10;
    public long SideWrites { get; private set; }

    void FlushSide(Al80Device dev, bool streaming)
    {
        (byte h, byte s, byte v)? want;
        lock (_lightGate) want = streaming && C.SyncSideBar ? _sideWant : null;
        const byte ch = Al80Protocol.ChannelSide;
        if (want is not { } w)
        {
            if (!_sideActive) return;
            // the light's own setting back (what was read at connect), like the 1.18 release path
            if (_stored.TryGetValue((ch, Al80Protocol.LightEffect), out var e)) Set(dev, ch, Al80Protocol.LightEffect, e[0]);
            if (_stored.TryGetValue((ch, Al80Protocol.LightColor), out var c)) Set(dev, ch, Al80Protocol.LightColor, c[0], c[1]);
            if (_stored.TryGetValue((ch, Al80Protocol.LightBrightness), out var b)) Set(dev, ch, Al80Protocol.LightBrightness, b[0]);
            if (_stored.TryGetValue((ch, Al80Protocol.LightSpeed), out var sp)) Set(dev, ch, Al80Protocol.LightSpeed, sp[0]);
            _sideActive = false; _sideSent = null;
            Log.Info("AL80 3-LED light: its own effect restored");
            return;
        }
        if (_sideActive && _sideSent == w) return;
        long now = Environment.TickCount64;
        if (_sideActive && now - _lastSideTicks < 1000 / SideMaxHz) return;
        _lastSideTicks = now;
        if (!_sideActive)
        {
            Set(dev, ch, Al80Protocol.LightEffect, 1);   // solid
            _sideActive = true; _sideSent = null;
            Log.Info("AL80 3-LED light: following VANTA (solid colour, nothing saved)");
        }
        if (_sideSent is not { } last || last.h != w.h || last.s != w.s) Set(dev, ch, Al80Protocol.LightColor, w.h, w.s);
        if (_sideSent is not { } last2 || last2.v != w.v) Set(dev, ch, Al80Protocol.LightBrightness, w.v);
        _sideSent = w;
        SideWrites++;
    }

    /// <summary>Stop per-key frames: the keyboard's own effect comes back (0x26).</summary>
    public void ReleaseFrame()
    {
        lock (_lightGate) { _frameWant = null; _frameHold = false; }
        _wake.Set();
    }

    void FlushFrame(Al80Device dev)
    {
        Rgb[]? want;
        lock (_lightGate)
        {
            if (_frameWant != null && !_frameHold && DateTime.UtcNow - _frameAt > LightIdleRelease) _frameWant = null;
            want = PerKey ? _frameWant : null;   // per-key turned off (or firmware changed): release
        }
        if (want == null)
        {
            if (_frameActive)
            {
                dev.SrgbMode(false);
                _frameActive = false; _frameSent = null;
                LightStatus = "not syncing · the keyboard's own effect";
                Log.Info("AL80 per-key: released (0x26), keyboard's own effect back");
            }
            FlushSide(dev, false);
            return;
        }
        if (_frameActive) FlushSide(dev, true);   // between frames: the 3-LED light follows at up to 10 changes/s
        long nowTicks = Environment.TickCount64;
        long due = _lastFrameTicks + (long)(1000 / PerKeyMaxHz) - nowTicks;
        // the firmware drops its SignalRGB flag after 500 ms without a stream packet (Ghidra, 1.2.5 lighting task): keep it alive
        if (_frameActive) _waitMs = Math.Min(_waitMs, 100);
        bool full = !_frameActive || DateTime.UtcNow - _frameFullAt > PerKeyKeepAlive;
        if (!full && _frameSent != null && want.AsSpan().SequenceEqual(_frameSent)) return;
        if (_frameActive && due > 0) { _waitMs = (int)Math.Clamp(due, 1, 250); return; }
        _lastFrameTicks = nowTicks;
        if (!_frameActive)
        {
            dev.SrgbMode(true);
            _frameActive = true; _frameSent = null;
            Log.Info("AL80 per-key: streaming VANTA's frames (0x25; nothing saved)");
        }
        var packets = Al80Protocol.SrgbFrame(want, onlyChunks: full ? null : Al80Protocol.SrgbChangedChunks(_frameSent, want));
        var err = dev.SrgbWrite(packets);
        if (err != null) { FrameErrors++; Log.Warn("AL80 per-key: keyboard reported " + err); }
        if (full) _frameFullAt = DateTime.UtcNow;
        _frameSent = want;
        FramesSent++;
        LightStatus = $"following VANTA per key · {FramesSent} frames";
    }

    void FlushLight(Al80Device dev)
    {
        if (PerKey || _frameActive) { FlushFrame(dev); return; }
        long nowTicks = Environment.TickCount64;
        (byte h, byte s, byte v)? want;
        lock (_lightGate)
        {
            if (_lightWant != null && !_lightHold && DateTime.UtcNow - _lightAt > LightIdleRelease) _lightWant = null;
            want = _lightWant;
        }
        if (want == _lightSent && (want != null) == _lightActive) return;
        long due = _lastFlushTicks + (long)(1000 / LightMaxHz) - nowTicks;
        if (want != null && _lightActive && due > 0) { _waitMs = (int)Math.Clamp(due, 1, 250); return; }   // come back when the next write is due
        _lastFlushTicks = nowTicks;
        try
        {
            if (want is { } w)
            {
                var chans = LightChannels;
                if (!_lightActive)
                {
                    foreach (var ch in chans) Set(dev, ch, Al80Protocol.LightEffect, 1);
                    _lightActive = true; _lightSent = null;
                    Log.Info("AL80 lighting: following VANTA (solid effect, live colour; nothing saved)");
                }
                if (_lightSent is not { } last || last.h != w.h || last.s != w.s) foreach (var ch in chans) Set(dev, ch, Al80Protocol.LightColor, w.h, w.s);
                if (_lightSent is not { } last2 || last2.v != w.v) foreach (var ch in chans) Set(dev, ch, Al80Protocol.LightBrightness, w.v);
                _lightSent = w;
                LightStatus = $"following VANTA · hue {w.h} sat {w.s} brightness {w.v}";
            }
            else if (_lightActive)
            {
                foreach (var ch in new[] { Al80Protocol.ChannelKeys, Al80Protocol.ChannelSide })
                {
                    if (_stored.TryGetValue((ch, Al80Protocol.LightEffect), out var e)) Set(dev, ch, Al80Protocol.LightEffect, e[0]);
                    if (_stored.TryGetValue((ch, Al80Protocol.LightColor), out var c)) Set(dev, ch, Al80Protocol.LightColor, c[0], c[1]);
                    if (_stored.TryGetValue((ch, Al80Protocol.LightBrightness), out var b)) Set(dev, ch, Al80Protocol.LightBrightness, b[0]);
                    if (_stored.TryGetValue((ch, Al80Protocol.LightSpeed), out var sp)) Set(dev, ch, Al80Protocol.LightSpeed, sp[0]);
                }
                _lightActive = false; _lightSent = null;
                LightStatus = "not syncing · the keyboard's own effect";
                Log.Info("AL80 lighting: keyboard's own effect restored");
            }
        }
        catch (Al80Exception) { LightFailures++; throw; }
    }

    void HomeTick(Al80Device dev, DateTime now)
    {
        if (C.ClockSync && now - _clockAt > TimeSpan.FromMinutes(10))
        {
            dev.Send(Al80Protocol.ClockStream(DateTime.Now));
            _clockAt = now;
        }
        var snap = _latest();
        double? v = C.Gauge switch { "cpu" => snap.CpuLoadPct, "gpu" => snap.GpuLoadPct, "ram" => snap.RamUsedPct, _ => null };
        if (v is double pct)
        {
            int p = (int)Math.Round(Math.Clamp(pct, 0, 100));
            // the icon shows a few bars; resend on a real change, and every 5 s in case the keyboard reports its own state
            if (_gaugeSent is not int last || Math.Abs(last - p) >= 5 || now - _gaugeAt > TimeSpan.FromSeconds(5))
            {
                dev.Send(Al80Protocol.GaugeStream(p));
                _gaugeSent = p; _gaugeAt = now; _gaugeActive = true;
            }
        }
        else if (_gaugeActive) { dev.Send(Al80Protocol.GaugeReleaseStream()); _gaugeActive = false; _gaugeSent = null; }
        Status = "home · " + (C.ClockSync ? "clock synced" : "clock not synced") + (C.Gauge != "off" ? $" · battery icon = {C.Gauge.ToUpperInvariant()} {(_gaugeSent is int g ? g + "%" : "--")}" : "");
    }

    void LiveTick(Al80Device dev, DateTime now)
    {
        if (_gaugeActive) { dev.Send(Al80Protocol.GaugeReleaseStream()); _gaugeActive = false; _gaugeSent = null; }
        if (now - _renderAt < TimeSpan.FromSeconds(1)) return;
        _renderAt = now;
        var s = _latest();
        var px = Al80Imaging.RenderSystem(new Al80Readings(s.CpuLoadPct, s.CpuTempC, s.GpuLoadPct, s.GpuTempC, s.RamUsedPct, s.RamUsedGb, DateTime.Now));
        Preview = Al80Imaging.ToBitmap(px, Al80Protocol.Width, Al80Protocol.Height);
        if (!C.FlashSlotsFilled)
        {
            Status = "live · preview only: pictures are not sent until the flash slots are confirmed full (see Storage)";
            Changed?.Invoke();
            return;
        }
        var rgb = Al80Imaging.ToRgb565(px);
        if (_shown != null && rgb.AsSpan().SequenceEqual(_shown)) { Status = "live · screen up to date"; return; }
        if (now - _uploadAt < TimeSpan.FromSeconds(Math.Max(1.2, C.LiveIntervalSec))) return;
        UploadPicture(dev, rgb);
        _uploadAt = DateTime.UtcNow;
        Status = $"live · last picture {LastUploadSeconds:0.00} s";
    }

    // ------------------------------------------------------------------ 1.21 now playing (Spotify via Windows media controls)
    /// <summary>The Spotify watcher; runs only while the screen mode is "music".</summary>
    public NowPlayingWatcher Music { get; } = new();
    string? _musicKey;
    bool _musicHome;
    public NowPlayingInfo? MusicShown { get; private set; }
    /// <summary>Minimum time between two now-playing pictures (skipping through tracks quickly sends only the one you stop on).</summary>
    public static readonly TimeSpan MusicMinGap = TimeSpan.FromSeconds(2);

    void MusicTick(Al80Device dev, DateTime now)
    {
        var np = Music.Latest;
        if (np == null)
        {
            // Spotify closed or idle: the keyboard's own home screen (clock + gauge) instead of a stale cover
            if (!_musicHome) { dev.Send(Al80Protocol.ViewStream(Al80Protocol.View.Home)); _musicHome = true; _shown = null; _musicKey = null; MusicShown = null; }
            HomeTick(dev, now);
            Status = "now playing · Spotify is not playing anything: home screen" + (Music.Error is string e ? $" ({e})" : "");
            if (Preview == null || _musicKey == null) { Preview = Al80Imaging.ToBitmap(Al80Imaging.RenderNothingPlaying(), Al80Protocol.Width, Al80Protocol.Height); _musicKey = ""; Changed?.Invoke(); }
            return;
        }
        if (_gaugeActive) { dev.Send(Al80Protocol.GaugeReleaseStream()); _gaugeActive = false; _gaugeSent = null; }
        if (np.Key != _musicKey || _shown == null)
        {
            var cover = Al80Imaging.Decode(np.Art);
            if (np.Art != null && cover == null && np.Key != _musicKey) Log.Warn($"AL80 now playing: the cover for '{np.Title}' ({np.Art.Length} bytes) could not be decoded");
            var px = Al80Imaging.RenderNowPlaying(np.Title, np.Artist, np.Playing, cover);
            Preview = Al80Imaging.ToBitmap(px, Al80Protocol.Width, Al80Protocol.Height);
            if (np.Key != _musicKey) Changed?.Invoke();
            _musicKey = np.Key;
            if (!C.FlashSlotsFilled)
            {
                Status = $"now playing · {np.Title} — {np.Artist} · preview only: pictures are not sent until the flash slots are confirmed full (see Flash wear)";
                return;
            }
            if (now - _uploadAt < MusicMinGap) { _musicKey = null; return; }   // try again next tick
            var rgb = Al80Imaging.ToRgb565(px);
            if (_shown == null || !rgb.AsSpan().SequenceEqual(_shown))
            {
                UploadPicture(dev, rgb);
                _uploadAt = DateTime.UtcNow;
                Log.Info($"AL80 now playing: {np.Title} — {np.Artist}{(np.Playing ? "" : " (paused)")}{(np.Art == null ? " (no cover yet)" : "")}, {LastUploadSeconds:0.00} s");
            }
            _musicHome = false;
            MusicShown = np;
        }
        Status = $"now playing · {np.Title} — {np.Artist}{(np.Playing ? "" : " · paused")}";
    }

    // ------------------------------------------------------------------ 1.22 game profile panel (Deadlock stats)
    volatile Func<DeadlockPanel>? _panelOverride;
    string? _deadKey;
    bool _overrideWas;
    /// <summary>Minimum time between two Deadlock pictures.</summary>
    public static readonly TimeSpan DeadlockMinGap = TimeSpan.FromSeconds(2);
    /// <summary>Set by the game profile while Deadlock runs: the screen shows this panel instead of the saved mode. Null ends it. Runtime only.</summary>
    public void SetPanelOverride(Func<DeadlockPanel>? panel) { _panelOverride = panel; Changed?.Invoke(); }
    public bool PanelOverrideActive => _panelOverride != null;

    static string DeadlockLine(DeadlockPanel p) => p.Summary is { } s ? $"{s.Rank} · {s.Wins}W {s.Losses}L" : p.Error ?? "loading";

    void DeadlockTick(Al80Device dev, DateTime now, Func<DeadlockPanel> source)
    {
        if (_gaugeActive) { dev.Send(Al80Protocol.GaugeReleaseStream()); _gaugeActive = false; _gaugeSent = null; }
        var p = source();
        string previewNote = C.FlashSlotsFilled ? "" : " · preview only: pictures are not sent until the flash slots are confirmed full (see Flash wear)";
        if (p.Key == _deadKey && (_shown != null || !C.FlashSlotsFilled)) { Status = "Deadlock · " + DeadlockLine(p) + previewNote; return; }
        var px = Al80Imaging.RenderDeadlock(p);
        Preview = Al80Imaging.ToBitmap(px, Al80Protocol.Width, Al80Protocol.Height);
        if (!C.FlashSlotsFilled)
        {
            Status = "Deadlock · " + DeadlockLine(p) + previewNote;
            _deadKey = p.Key; Changed?.Invoke();
            return;
        }
        if (now - _uploadAt < DeadlockMinGap) return;                // try again next tick
        var rgb = Al80Imaging.ToRgb565(px);
        if (_shown == null || !rgb.AsSpan().SequenceEqual(_shown))
        {
            UploadPicture(dev, rgb);
            _uploadAt = DateTime.UtcNow;
            Log.Info($"AL80 Deadlock: {DeadlockLine(p)}, {LastUploadSeconds:0.00} s");
        }
        _deadKey = p.Key;
        Status = "Deadlock · " + DeadlockLine(p);
    }

    /// <summary>The game ended: forget the game picture and put the screen back (live and music redraw themselves; the rest return to the keyboard's home screen).</summary>
    void LeaveOverride(Al80Device dev)
    {
        _shown = null; _deadKey = null; _musicKey = null; _musicHome = false;
        if (C.Mode is not ("live" or "music"))
        {
            try { dev.Send(Al80Protocol.ViewStream(Al80Protocol.View.Home)); }
            catch (Exception ex) { Log.Warn("AL80: could not return to the home screen after the game: " + ex.Message); }
        }
        Log.Info("AL80: game panel ended, screen back to '" + C.Mode + "'");
        Changed?.Invoke();
    }

    /// <summary>Upload one still (RGB565) on the service thread.</summary>
    public void UploadPicture(Al80Device dev, byte[] rgb565)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        dev.Send(Al80Protocol.PictureStream(rgb565));
        LastUploadSeconds = sw.Elapsed.TotalSeconds;
        LastUploadTime = DateTime.Now;
        _shown = rgb565;
        SessionPictures++;
        lock (_cfg.Sync) C.LifetimePictures++;
        Changed?.Invoke();
    }

    /// <summary>Forget what the screen shows (after a view switch or a manual picture), so live mode sends a fresh frame.</summary>
    public void Invalidate() => _shown = null;

    public void Dispose()
    {
        _stop.Cancel();
        try { _thread?.Join(3000); } catch { }
        Music.Dispose();
    }
}
