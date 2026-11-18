namespace HwSuite.Lighting;

/// <summary>Clock-driven offer cadence, preserving phase without replaying missed frames.</summary>
public sealed class FrameCadence
{
    double _next;
    public bool IsDue(double seconds, double hz)
    {
        if (seconds + 0.001 < _next) return false;
        double period = 1.0 / hz;
        _next += period;
        if (seconds - _next > period) _next = seconds + period;
        return true;
    }
}
