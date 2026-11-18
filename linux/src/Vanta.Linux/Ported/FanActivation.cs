// PORTED from src\HwSuite\Ui\FanActivationDialog.cs (Windows 1.20.0): the FanActivation logic only (the WPF dialog is rebuilt in Avalonia).
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;

namespace HwSuite.Ui;

/// <summary>
/// What switching fan control on would do, channel by channel. The global switch drives every channel whose mode is not
/// Firmware (Auto), so turning it on can also start existing GPU or pump curves. The owner picks the channels explicitly;
/// nothing else is changed: unpicked channels go to Firmware, picked ones on Firmware go to Curve, every curve is kept.
/// </summary>
public static class FanActivation
{
    public sealed record Candidate(IFanChannel Channel, string Label, FanKind Kind, ChannelMode Mode, string CurveName, FanSafety.Assessment Safety)
    {
        /// <summary>Would be driven if fan control were switched on now, unchanged.</summary>
        public bool DrivenNow => Mode != ChannelMode.Auto;
    }

    /// <summary>Every channel VANTA is allowed to drive (evidence-backed floor), in engine order. Others cannot be picked.</summary>
    public static List<Candidate> Candidates(AppConfig cfg, IEnumerable<IFanChannel> channels)
    {
        var list = new List<Candidate>();
        lock (cfg.Sync)
            foreach (var ch in channels)
            {
                var cc = cfg.Channels.FirstOrDefault(c => c.Id == ch.Id);
                if (cc == null) continue;
                var a = FanSafety.Assess(cc, ch);
                if (!a.Controllable) continue;
                list.Add(new Candidate(ch, string.IsNullOrWhiteSpace(cc.Label) ? ch.Name : cc.Label, cc.Kind, cc.Mode, cc.Curve.Name, a));
            }
        return list;
    }

    public static HashSet<string> CaseFansOnly(IEnumerable<Candidate> c) => c.Where(x => x.Kind == FanKind.CaseFan).Select(x => x.Channel.Id).ToHashSet();

    /// <summary>Apply the owner's pick to the channel modes (curves untouched). Returns one line per changed channel.</summary>
    public static List<string> Apply(AppConfig cfg, IEnumerable<Candidate> candidates, ISet<string> picked)
    {
        var changes = new List<string>();
        lock (cfg.Sync)
            foreach (var c in candidates)
            {
                var cc = cfg.Channels.First(x => x.Id == c.Channel.Id);
                bool want = picked.Contains(c.Channel.Id);
                if (want && cc.Mode == ChannelMode.Auto) { cc.Mode = ChannelMode.Curve; changes.Add($"{c.Label}: Firmware -> Curve ({cc.Curve.Name})"); }
                else if (!want && cc.Mode != ChannelMode.Auto) { changes.Add($"{c.Label}: {cc.Mode} -> Firmware (curve kept)"); cc.Mode = ChannelMode.Auto; }
            }
        return changes;
    }

    public static string KindText(FanKind k) => k switch
    {
        FanKind.CaseFan => "case fan", FanKind.GpuFan => "GPU fan", FanKind.Aio => "AIO (pump + radiator fans)", FanKind.Pump => "pump",
        FanKind.CpuCooler => "CPU cooler", _ => "header"
    };
}
