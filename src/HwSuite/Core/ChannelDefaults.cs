using HwSuite.Fans;

namespace HwSuite.Core;

/// <summary>
/// Board knowledge supplied by the owner, applied as friendly defaults. Verified at the hardware level in Phase 4:
/// Super I/O PWM control #1 drives tachometer #1 (CPU_FAN). The owner states that header carries the single
/// consolidated cable of the ARCTIC Liquid Freezer II 240 (pump plus both radiator fans on one PWM signal).
/// </summary>
public static class ChannelDefaults
{
    public const string CpuFanChannelId = "lhm:/lpc/it8689e/0/control/0";
    public const string AioLabel = "Arctic Liquid Freezer II 240 (CPU_FAN)";

    /// <summary>
    /// Case-fan headers identified in Phase 9 (docs\phase9, fan-step and census runs after the owner moved the three 3-pin
    /// case fans from the SATA hub to SYS_FAN1-3): Super I/O control N moves only tachometer N for N = 1, 2, 3, and lowering
    /// the duty lowers the RPM of these 3-pin fans, so the headers run them in voltage (DC) mode. Control 1 drives the one
    /// fan whose speed differs from the matched front pair, which the owner wired to SYS_FAN1 (rear exhaust). Control 2
    /// drives the upper front intake (the owner watched it stop first in the attended test) and the owner confirmed that
    /// fan's cable is on SYS_FAN2; control 3 is therefore the lower front intake on SYS_FAN3.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CaseFanLabels = new Dictionary<string, string>
    {
        ["lhm:/lpc/it8689e/0/control/1"] = "Rear exhaust",
        ["lhm:/lpc/it8689e/0/control/2"] = "Front intake—SYS_FAN2 (upper)",
        ["lhm:/lpc/it8689e/0/control/3"] = "Front intake—SYS_FAN3 (lower)",
    };

    /// <summary>
    /// Controls whose tachometer is known from step tests: control 0 -> tachometer 0 (Phase 4, CPU_FAN) and control N ->
    /// tachometer N for N = 1..3 (Phase 9 fan-step: only that tachometer moved). Controls 4 and 5 moved no tachometer.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TachMappings = new Dictionary<string, string>
    {
        [CpuFanChannelId] = "Phase 4 identify pulse: control 0 moves tachometer 0 (CPU_FAN)",
        ["lhm:/lpc/it8689e/0/control/1"] = @"Phase 9 fan-step (docs\phase9\fanmap1-ctl1): control 1 moves only tachometer 1",
        ["lhm:/lpc/it8689e/0/control/2"] = @"Phase 9 fan-step (docs\phase9\fanmap1-ctl2): control 2 moves only tachometer 2",
        ["lhm:/lpc/it8689e/0/control/3"] = @"Phase 9 fan-step (docs\phase9\fanmap1-ctl3): control 3 moves only tachometer 3",
    };

    public static (string label, FanKind kind) For(IFanChannel ch) =>
        ch.Id == CpuFanChannelId ? (AioLabel, FanKind.Aio)
        : CaseFanLabels.TryGetValue(ch.Id, out var l) ? (l, FanKind.CaseFan)
        : (ch.Name, ch.DefaultKind);

    public static ChannelConfig Create(IFanChannel ch, string preset)
    {
        var (label, kind) = For(ch);
        var cc = new ChannelConfig { Id = ch.Id, Label = label, Kind = kind, Mode = ChannelMode.Auto, Curve = FanPresets.CurveFor(preset, kind) };
        ApplyTachMapping(cc, ch);
        return cc;
    }

    /// <summary>
    /// Upgrade a config saved by an earlier build without overwriting anything the user customised:
    /// the friendly label replaces only an auto-generated one, the AIO type replaces only "Unclassified".
    /// </summary>
    public static void Upgrade(ChannelConfig cc, IFanChannel ch, string preset)
    {
        var (label, kind) = For(ch);
        if (string.IsNullOrWhiteSpace(cc.Label) || cc.Label == ch.Name) cc.Label = label;
        if (ch.Id == CpuFanChannelId && cc.Kind == FanKind.Generic)
        {
            cc.Kind = kind;
            cc.Curve = FanPresets.CurveFor(preset, kind);
        }
        // identified case-fan headers: only the type changes (Unclassified -> Case fan); the saved curve, mode and any
        // minimum stay as they are. A case fan still needs an owner-confirmed minimum before HwSuite drives it (FanSafety).
        if (CaseFanLabels.ContainsKey(ch.Id) && cc.Kind == FanKind.Generic) cc.Kind = FanKind.CaseFan;
        ApplyTachMapping(cc, ch);
    }

    /// <summary>Record the step-test tachometer mapping (only adds; a channel without a tachometer sensor gets nothing).</summary>
    static void ApplyTachMapping(ChannelConfig cc, IFanChannel ch)
    {
        if (!cc.TachMapped && ch.HasTachSensor && TachMappings.TryGetValue(ch.Id, out var ev)) { cc.TachMapped = true; cc.TachEvidence = ev; }
    }

    public static string DisplayName(AppConfig cfg, IFanChannel ch)
    {
        lock (cfg.Sync)
        {
            var cc = cfg.Channels.FirstOrDefault(c => c.Id == ch.Id);
            return cc != null && !string.IsNullOrWhiteSpace(cc.Label) ? cc.Label : For(ch).label;
        }
    }
}
