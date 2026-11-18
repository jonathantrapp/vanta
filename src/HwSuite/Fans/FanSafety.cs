using HwSuite.Core;
using HwSuite.Hardware;

namespace HwSuite.Fans;

/// <summary>
/// Decides whether HwSuite may drive a channel at all, and the lowest duty it may use. A floor must come from evidence
/// about the connected device; a missing tachometer reading proves nothing about what duty is safe. Channels without
/// that evidence stay under their existing (firmware or VBIOS) control.
/// </summary>
public static class FanSafety
{
    /// <summary>Conservative application policy for a header that also powers a pump. Not a measured stall point.</summary>
    public const double PumpPolicyFloor = 60;

    /// <summary>
    /// <paramref name="Provisional"/>: the floor is an owner-approved limit whose startup from a stop was not measured at or
    /// below it (no measurement, or a measured start above the floor); VANTA then relies on its startup verification.
    /// </summary>
    public sealed record Assessment(bool Controllable, double Floor, string Basis, string? Blocker, bool Provisional = false, double? MeasuredStart = null);

    /// <summary>"startup measured at 40%" or "provisional: startup at this floor not measured".</summary>
    public static string StartupNote(Assessment a) => !a.Controllable ? "" :
        a.MeasuredStart is double m && !a.Provisional ? $"restarts from a stop at {m:0}% (measured)"
        : a.MeasuredStart is double m2 ? $"provisional: measured restart {m2:0}% is above this floor"
        : "provisional: restart from a stop at this floor not measured";

    public static Assessment Assess(ChannelConfig cc, IFanChannel ch)
    {
        double hw = Math.Max(0, ch.MinPercent);
        if (!ch.CanControl) return new(false, 100, "", "read-only channel");

        if (ch.Resource == HwResource.GpuFans)
        {
            // the driver reports the lowest manual duty it accepts; the VBIOS curve itself goes lower (fan stop)
            return new(true, Math.Max(hw, 1), $"NVML-reported minimum {hw:0}%", null);
        }

        switch (cc.Kind)
        {
            case FanKind.Aio:
                return new(true, Math.Max(PumpPolicyFloor, hw),
                    $"{PumpPolicyFloor:0}% policy floor: one PWM signal drives the pump and the radiator fans (conservative choice, not a measured stall point)", null);
            case FanKind.Pump:
                return new(true, Math.Max(PumpPolicyFloor, hw), $"{PumpPolicyFloor:0}% policy floor for pumps (conservative choice, not a measured stall point)", null);
            case FanKind.CaseFan:
            case FanKind.CpuCooler:
            case FanKind.GpuFan:   // a "GPU fan" type on a motherboard header gets no NVML limit, so it needs evidence too
                if (cc.VerifiedMinPercent is double v && v >= 1 && v <= 100)
                {
                    double floor = Math.Max(v, hw);
                    bool provisional = !(cc.MeasuredStartPercent is double ms && ms <= floor);
                    string ev = string.IsNullOrWhiteSpace(cc.MinEvidence) ? "owner-confirmed minimum" : "owner-confirmed: " + cc.MinEvidence!.Trim();
                    var a = new Assessment(true, floor, "", null, provisional, cc.MeasuredStartPercent);
                    return a with { Basis = $"{floor:0}% ({ev}); {StartupNote(a)}" };
                }
                return new(false, 100, "", "needs a confirmed minimum duty for the connected fans before HwSuite can drive it");
            default:
                return new(false, 100, "", "unidentified header: set what is connected (header type) first");
        }
    }

    /// <summary>Channels a preset may switch to Curve mode.</summary>
    public static bool PresetEligible(ChannelConfig cc, IFanChannel ch) => Assess(cc, ch).Controllable;
}
