using HwSuite.Core;
using HwSuite.Hardware.Al80;
using HwSuite.Lighting.Devices;

namespace HwSuite.Lighting;

/// <summary>
/// 1.18.1: the YUNZII AL80 as VANTA's keyboard lighting device (it replaces the iLovbee i87). The stock firmware has no
/// per-key live mode (al80-studio's 0x49 stream needs custom firmware), so the keyboard is rendered like the mouse: a
/// short strip of virtual LEDs across the keyboard's place in the arrangement, shown as their brightest colour, which
/// <see cref="Al80Service"/> streams as volatile VIA writes (solid effect, hue/saturation/brightness; up to 30 per second,
/// unchanged values not resent, nothing saved). Keys and side bar show the same colour.
/// </summary>
public sealed class Al80Sink : RgbSink
{
    readonly RgbConfig _cfg;
    readonly Func<Al80Service?> _svc;
    readonly Func<Al80Config> _al80;
    public const int VirtualLeds = 10;
    public static readonly double[] BasePositions = Enumerable.Range(0, VirtualLeds).Select(i => KeyboardSink.WaveStart + KeyboardSink.WaveSpan * i / (VirtualLeds - 1)).ToArray();

    public Al80Sink(RgbConfig cfg, Func<Al80Service?> service, Func<Al80Config> al80)
    {
        _cfg = cfg; _svc = service; _al80 = al80;
        Available = true;
        Status = "waiting for the AL80";
    }

    public override string Name => "YUNZII AL80 keyboard";
    public override string Kind => PerKey ? "USB HID (SignalRGB per-key stream)" : "USB HID (VIA colour stream)";

    /// <summary>1.19.0: per-key positions (firmware LED table) when the AL80 runs YUNZII's SignalRGB build.</summary>
    public static readonly double[] PerKeyPositions = Al80KeyMap.UnitX.Select(x => KeyboardSink.WaveStart + KeyboardSink.WaveSpan * x).ToArray();
    /// <summary>The service found the SignalRGB firmware (84 LEDs) and per-key is allowed: frames carry a colour per LED.</summary>
    public bool PerKey => _svc() is { PerKey: true } && _al80().Enabled;
    public override int LedCount => PerKey ? Al80KeyMap.Leds : VirtualLeds;
    public override double[] Positions => PerKey ? PerKeyPositions : BasePositions;
    public override DeviceRole? RoleHint => DeviceRole.Keyboard;
    protected override double MaxHz => Al80Service.LightMaxHz;
    /// <summary>A still look is offered again every second, which also keeps the service's idle release from firing.</summary>
    public override double StillRepostSeconds => 1.0;
    protected override TimeSpan? RefreshUnchangedAfter => TimeSpan.FromSeconds(1);

    /// <summary>Why nothing reaches the keyboard (null: it is being driven).</summary>
    public string? NotDriving => _svc() is not { } s ? "the AL80 service is not running"
        : !_al80().Enabled ? "AL80 control is off (AL80 page)"
        : !s.Connected ? "the AL80 is not connected (" + s.Status + ")" : null;

    /// <summary>
    /// Classic effects: the colour at the keyboard's centre (a wave glides across it in step). Scenes: the average over the
    /// keyboard's area, so drops and glints make a soft shimmer in the scene's colour instead of whole-board flashes
    /// (owner, 2026-11-12: the brightest-point flashes looked wrong on a keyboard).
    /// </summary>
    public static Rgb LiveColor(Rgb[] frame, bool scene)
    {
        if (frame.Length == 0) return Rgb.Black;
        if (!scene) return frame[frame.Length / 2];
        int r = 0, g = 0, b = 0;
        foreach (var c in frame) { r += c.R; g += c.G; b += c.B; }
        return new Rgb((byte)(r / frame.Length), (byte)(g / frame.Length), (byte)(b / frame.Length));
    }

    protected override bool Apply(Rgb[] frame)
    {
        if (frame.Length == Al80KeyMap.Leds)
        {
            Preview = frame;
            var ps = _svc();
            if (ps == null || NotDriving is not null) { Status = NotDriving ?? "not driving"; AppliedWithoutWrite = true; return true; }
            if (!ps.PerKey) { AppliedWithoutWrite = true; ForgetLastFrame(); return true; }   // firmware changed under us: next frame is sized again
            // the separate 3-LED light gets the board's colour of the frame, the way 1.18 drove the whole board
            ps.SetFrame(frame, LiveColor(frame, EffectInfo.IsScene(_cfg.Effect)));
            Status = ps.LightStatus;
            return true;
        }
        if (PerKey) { AppliedWithoutWrite = true; ForgetLastFrame(); return true; }   // a 10-LED frame queued before per-key started
        // classic effects: the colour at the keyboard's centre, so a wave glides across it in step; scenes: the brightest
        // point, so a drop, glint or flash anywhere on the keyboard shows
        var c = LiveColor(frame, EffectInfo.IsScene(_cfg.Effect));
        Preview = new[] { c };
        var s = _svc();
        if (s == null || NotDriving is not null) { Status = NotDriving ?? "not driving"; AppliedWithoutWrite = true; return true; }
        s.SetLight(c);
        Status = s.LightStatus;
        return true;
    }

    /// <summary>Output stopped or the app exits: the keyboard keeps the steady look, like the streamed devices.</summary>
    public override void Deactivate(Rgb steady)
    {
        if (_svc() is not { } s) return;
        if (s.PerKey) s.SetFrame(Enumerable.Repeat(steady, Al80KeyMap.Leds).ToArray(), steady, hold: true);
        else s.SetLight(steady, hold: true);
    }

    public override string StatsText => _svc() is { } s
        ? s.PerKey || s.FramesSent > 0 ? $"per-key frames {s.FramesSent}  errors {s.FrameErrors}" : $"colour writes {s.LightWrites}  failures {s.LightFailures}"
        : "";
}
