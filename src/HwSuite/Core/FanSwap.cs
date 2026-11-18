namespace HwSuite.Core;

/// <summary>
/// Settings change for the 2026-10 fan swap (six Thermalright TL-C12C-S 4-pin PWM fans replace the three 3-pin case fans
/// and the two radiator fans). The old minimums and start measurements were taken on the old 3-pin fans in DC mode, so
/// they no longer describe what is connected: they are cleared and the headers stay with the BIOS until the new fans are
/// measured in an attended test. Kept: the header-to-tachometer mapping (a property of the board, not of the fan),
/// curves, the CPU_FAN/AIO policy floor and every lighting setting. Pure: it changes only the configuration it is given.
/// </summary>
public static class FanSwap
{
    public const string Rear = "lhm:/lpc/it8689e/0/control/1";
    public const string Front = "lhm:/lpc/it8689e/0/control/2";
    public const string Spare = "lhm:/lpc/it8689e/0/control/3";

    /// <summary>Header, new label, whether VANTA may keep it on its curve once re-tested (the spare header has nothing connected).</summary>
    static readonly (string id, string label, bool stayOnCurve)[] Headers =
    {
        (Rear, "Rear exhaust—SYS_FAN1 (Thermalright)", true),
        (Front, "Front intake—SYS_FAN2 (Thermalright)", true),
        (Spare, "SYS_FAN3 (spare: nothing connected)", false),
    };

    /// <summary>Applies the swap to <paramref name="cfg"/> and lists every change. Running it again changes nothing.</summary>
    public static List<string> Prepare(AppConfig cfg)
    {
        var changes = new List<string>();
        lock (cfg.Sync)
        {
            if (cfg.FanControlEnabled) { cfg.FanControlEnabled = false; changes.Add("fan control: on -> off (the BIOS runs every fan until the new fans are tested)"); }
            foreach (var (id, label, stayOnCurve) in Headers)
            {
                var cc = cfg.Channels.FirstOrDefault(c => c.Id == id);
                if (cc == null) { changes.Add($"{id}: not in the settings yet (VANTA adds it with firmware control on next start)"); continue; }
                string name = cc.Label.Length > 0 ? cc.Label : id;
                if (cc.VerifiedMinPercent != null || cc.MinEvidence != null)
                    changes.Add($"{name}: confirmed minimum {Pct(cc.VerifiedMinPercent)} ({cc.MinEvidence ?? "no note"}) cleared: needs re-testing on the new fan");
                if (cc.MeasuredStartPercent != null || cc.StartEvidence != null)
                    changes.Add($"{name}: measured restart {Pct(cc.MeasuredStartPercent)} cleared: needs re-testing on the new fan");
                if (cc.TachSeen) changes.Add($"{name}: speed history reset (the old fan's)");
                cc.VerifiedMinPercent = null; cc.MinEvidence = null; cc.MeasuredStartPercent = null; cc.StartEvidence = null; cc.TachSeen = false;
                if (!stayOnCurve && cc.Mode != ChannelMode.Auto) { changes.Add($"{name}: {cc.Mode} -> firmware (nothing connected)"); cc.Mode = ChannelMode.Auto; }
                if (cc.Label != label) { changes.Add($"label \"{name}\" -> \"{label}\""); cc.Label = label; }
            }
        }
        return changes;
    }

    static string Pct(double? v) => v is double d ? $"{d:0}%" : "none";

    /// <summary>The backup file name written before the swap is applied (sorts by time).</summary>
    public static string BackupName(DateTime now) => $"config.pre-fan-swap-{now:yyyyMMdd-HHmmss}.json";
    public const string BackupPattern = "config.pre-fan-swap-*.json";
}
