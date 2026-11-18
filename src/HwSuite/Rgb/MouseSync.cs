using HwSuite.Core;

namespace HwSuite.Lighting;

/// <summary>
/// Timing of the Model O Wireless's onboard breathing, measured on the owner's mouse (2026-10-23, docs\mouse-sync\RESULTS.md):
///  - period = 0.512 s × speed byte (bytes 1, 2, 5, 11, 20 filmed; millisecond-stable, about 0.04 % of real time);
///  - a write (also an identical re-send) restarts the cycle at its brightest point; the darkest moment follows
///    <see cref="DipFraction"/> of a period after the light changes (0.504-0.506 over five writes in two videos);
///  - the mouse's light changes about 45 ms BEFORE the case fans show a frame sent at the same moment (median of 9
///    blinks, spread about ±25 ms from the strip's 60/s cadence), so the "latency" used here is the mouse relative to the
///    case fans: -45 ms.
/// VANTA's breathing is darkest at engine time n × period (as the fans show it), so a write sent at engine time t lines up
/// when t + latency + DipFraction × period is a whole number of periods.
/// </summary>
public static class MouseSync
{
    public const double StepSeconds = 0.512;
    public const byte MinByte = 1, MaxByte = 21;
    /// <summary>Where in the mouse's cycle the darkest moment falls, from the light change after a write (measured).</summary>
    public const double DipFraction = 0.505;
    /// <summary>Mouse light change relative to the case fans for a write and a frame sent together (measured, ms).</summary>
    public const int MeasuredLatencyMs = -45;

    /// <summary>VANTA's own breathing rate at a speed setting (0..1), in Hz.</summary>
    public static double BreathingHz(double speed) => 0.12 + Math.Clamp(speed, 0, 1) * 0.6;

    /// <summary>The mouse speed byte whose period is closest to VANTA's breathing period at this speed.</summary>
    public static byte SpeedByte(double speed) =>
        (byte)Math.Clamp((int)Math.Round(1 / BreathingHz(speed) / StepSeconds), MinByte, MaxByte);

    public static double PeriodSeconds(byte speedByte) => StepSeconds * speedByte;

    /// <summary>Breathing rate VANTA uses while the mouse is locked to it.</summary>
    public static double LockedHz(double speed) => 1 / PeriodSeconds(SpeedByte(speed));

    /// <summary>Whether this configuration locks the mouse's breathing to VANTA's.</summary>
    public static bool Active(RgbConfig c) => c.MouseSync && !c.MouseLive && c.Mouse && c.Effect == EffectKind.Breathing;

    /// <summary>
    /// The mouse's own breathing shape, as light output from 0 to 1 over the first half of its cycle (from the dark point to
    /// the peak, 1/64-period steps; the second half mirrors it). Filmed 2026-10-23 (test 5, byte 20, 4 cycles at 120 fps,
    /// "mouse off" level as black): a nearly straight ramp from about 0.45 to 1. It never fades deep: it only drops to
    /// black in a short dip at the dark point (<see cref="DipSeconds"/>, the same length at bytes 5 and 20).
    /// </summary>
    static readonly double[] HalfShape =
    {
        0.43, 0.45, 0.46, 0.48, 0.515, 0.545, 0.57, 0.595, 0.62, 0.635, 0.655, 0.68, 0.695, 0.71, 0.73, 0.745, 0.76,
        0.78, 0.805, 0.815, 0.83, 0.845, 0.865, 0.875, 0.89, 0.91, 0.92, 0.935, 0.95, 0.96, 0.97, 0.99, 1.0
    };
    /// <summary>Time (s) the mouse takes to come back from black to its ramp after the dark point; it goes down as fast.</summary>
    public const double DipSeconds = 0.25;

    /// <summary>
    /// Brightness (0..1) of the mouse's breathing at engine time <paramref name="time"/> for a cycle of
    /// <paramref name="rateHz"/>, dark at whole periods. VANTA's other devices use this while the mouse is locked, so all of
    /// them breathe with the same shape as the mouse, not only at the same rate.
    /// </summary>
    public static double Shape(double time, double rateHz)
    {
        double period = 1 / rateHz;
        double p = time * rateHz; p -= Math.Floor(p);
        double half = Math.Min(p, 1 - p);                  // 0 at the dark point, 0.5 at the peak
        double x = half * 64, i = Math.Floor(x);
        int k = (int)Math.Min(i, 31);
        double v = HalfShape[k] + (HalfShape[k + 1] - HalfShape[k]) * Math.Min(1, x - k);
        double fromDark = half * period;                    // seconds from the nearest dark point
        double dip = Math.Clamp(fromDark / DipSeconds, 0, 1);
        return v * dip * dip * (3 - 2 * dip);
    }

    /// <summary>Seconds to wait from engine time <paramref name="engineTime"/> before sending, so the mouse is dark when VANTA is.</summary>
    public static double WaitBeforeSend(double engineTime, double periodSeconds, double latencySeconds)
    {
        double x = (engineTime + latencySeconds + DipFraction * periodSeconds) % periodSeconds;
        if (x < 0) x += periodSeconds;
        double wait = periodSeconds - x;
        return wait >= periodSeconds ? 0 : wait;
    }
}
