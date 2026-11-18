using HwSuite.Core;

namespace HwSuite.Fans;

public static class FanPresets
{
    public static readonly string[] Names = { "Silent", "Balanced", "Performance" };

    static FanCurve Make(string name, CurveSource src, double hyst, double up, double down, params (double t, double p)[] pts) => new()
    {
        Name = name, Source = src, HysteresisC = hyst, RampUpPerSec = up, StepDownPerSec = down,
        Points = pts.Select(x => new CurvePoint(x.t, x.p)).ToList()
    };

    public static FanCurve CurveFor(string preset, FanKind kind)
    {
        preset = Names.Contains(preset) ? preset : "Balanced";
        return (kind, preset) switch
        {
            (FanKind.GpuFan, "Silent") => Make("Silent", CurveSource.Gpu, 4, 10, 2, (40, 30), (55, 33), (65, 42), (72, 58), (78, 78), (84, 100)),
            (FanKind.GpuFan, "Performance") => Make("Performance", CurveSource.Gpu, 2, 35, 6, (40, 40), (50, 50), (58, 62), (65, 78), (72, 92), (76, 100)),
            (FanKind.GpuFan, _) => Make("Balanced", CurveSource.Gpu, 3, 20, 3, (40, 33), (50, 38), (60, 48), (68, 62), (75, 80), (80, 100)),

            (FanKind.Pump, "Silent") => Make("Silent", CurveSource.Cpu, 4, 10, 2, (30, 60), (60, 65), (75, 80), (90, 100)),
            (FanKind.Pump, "Performance") => Make("Performance", CurveSource.Cpu, 2, 35, 6, (30, 80), (50, 90), (65, 100)),
            (FanKind.Pump, _) => Make("Balanced", CurveSource.Cpu, 3, 20, 3, (30, 70), (60, 80), (75, 90), (85, 100)),

            // One PWM signal drives the pump and both radiator fans, so the curve never drops below the pump floor.
            (FanKind.Aio, "Silent") => Make("Silent", CurveSource.Cpu, 4, 10, 2, (30, 60), (50, 62), (60, 68), (70, 80), (80, 95), (90, 100)),
            (FanKind.Aio, "Performance") => Make("Performance", CurveSource.Cpu, 2, 35, 6, (30, 75), (45, 85), (60, 100)),
            (FanKind.Aio, _) => Make("Balanced", CurveSource.Cpu, 3, 20, 3, (30, 65), (50, 70), (60, 78), (70, 90), (80, 100)),

            (FanKind.CpuCooler, "Silent") => Make("Silent", CurveSource.Cpu, 4, 10, 2, (35, 25), (50, 30), (60, 40), (70, 60), (80, 85), (90, 100)),
            (FanKind.CpuCooler, "Performance") => Make("Performance", CurveSource.Cpu, 2, 35, 6, (35, 40), (45, 55), (55, 70), (65, 85), (75, 100)),
            (FanKind.CpuCooler, _) => Make("Balanced", CurveSource.Cpu, 3, 20, 3, (35, 30), (50, 40), (60, 55), (70, 75), (80, 95), (85, 100)),

            (_, "Silent") => Make("Silent", CurveSource.MaxOfBoth, 4, 10, 2, (30, 25), (50, 28), (60, 38), (70, 55), (78, 80), (85, 100)),
            (_, "Performance") => Make("Performance", CurveSource.MaxOfBoth, 2, 35, 6, (30, 40), (40, 50), (50, 62), (60, 75), (70, 90), (75, 100)),
            _ => Make("Balanced", CurveSource.MaxOfBoth, 3, 20, 3, (30, 30), (45, 35), (55, 45), (65, 60), (75, 80), (82, 100)),
        };
    }

    /// <summary>
    /// Rewrites the curves of the given channels. Only channels HwSuite may drive (an identified device with an
    /// evidence-backed floor, see FanSafety) are switched to Curve mode; every other channel keeps its mode, so a preset
    /// never takes over an unidentified header.
    /// </summary>
    public static List<string> ApplyPreset(AppConfig cfg, string preset, IEnumerable<IFanChannel> channels)
    {
        var switched = new List<string>();
        lock (cfg.Sync)
        {
            cfg.Preset = preset;
            foreach (var ch in channels)
            {
                var cc = cfg.Channels.FirstOrDefault(c => c.Id == ch.Id);
                if (cc == null)
                {
                    cc = ChannelDefaults.Create(ch, preset);
                    cfg.Channels.Add(cc);
                }
                cc.Curve = CurveFor(preset, cc.Kind);
                if (FanSafety.PresetEligible(cc, ch)) { cc.Mode = ChannelMode.Curve; switched.Add(ch.Id); }
            }
        }
        return switched;
    }
}
