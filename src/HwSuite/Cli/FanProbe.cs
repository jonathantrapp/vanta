using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;

namespace HwSuite.Cli;

/// <summary>
/// Time-series probe for one Super I/O (or GPU) channel: holds a low then a high duty for a while, logs every
/// tachometer once per second and always restores the channel. Used to classify headers (pump vs fan) with evidence.
/// </summary>
public static class FanProbe
{
    public static int Run(string channelSelector, int lowPercent, int highPercent, int holdSeconds)
    {
        using var app = new AppServices(new AppConfig());
        app.InitializeHardware();
        var channels = app.Hub.Engine.Channels;
        IFanChannel? ch = int.TryParse(channelSelector, out var idx)
            ? channels.Where(c => c.Backend.StartsWith("SuperIO")).ElementAtOrDefault(idx)
            : channels.FirstOrDefault(c => c.Id == channelSelector);
        if (ch == null) { Log.Error("probe: channel not found: " + channelSelector); return 3; }

        Log.Info($"probe: {ch.Name} ({ch.Id}) low {lowPercent}% / high {highPercent}% / hold {holdSeconds}s");
        var ids = app.Hub.Poll().Fans.Select(f => f).ToList();
        Log.Info("t(s)  phase  " + string.Join("  ", ids.Select(f => f.Name.PadLeft(10))));

        void Sample(string phase, int t)
        {
            var s = app.Hub.Poll();
            if ((s.CpuTempC ?? 0) >= 80 || (s.GpuTempC ?? 0) >= 78) throw new InvalidOperationException("thermal abort");
            Log.Info($"{t,4}  {phase,-5}  " + string.Join("  ", ids.Select(f => (s.Fans.FirstOrDefault(x => x.Id == f.Id)?.Rpm?.ToString("0") ?? "n/a").PadLeft(10))));
        }

        try
        {
            for (int t = 0; t < 4; t++) { Sample("base", t); Thread.Sleep(1000); }
            ch.SetPercent(Math.Max(lowPercent, ch.MinPercent));
            for (int t = 0; t < holdSeconds; t++) { Thread.Sleep(1000); Sample($"{lowPercent}%", t + 1); }
            ch.SetPercent(highPercent);
            for (int t = 0; t < holdSeconds; t++) { Thread.Sleep(1000); Sample($"{highPercent}%", t + 1); }
        }
        finally
        {
            ch.RestoreOriginal();
            Log.Info("probe: channel restored");
        }
        for (int t = 0; t < 6; t++) { Thread.Sleep(1000); Sample("rest", t + 1); }
        return 0;
    }
}
