using HwSuite.Lighting;

namespace HwSuite.Core;

/// <summary>
/// How strongly one device's red, green and blue are driven (1 = as sent). Different LED parts render the same values
/// differently: 1.15.2, the owner's photos (2026-10-21) showed violet #8B00FF blue-violet on the Thermalright fans but
/// pink on the XPG D60G memory, whose red is stronger relative to its blue. Applied to what is sent only: the preview and
/// the saved colours stay as chosen. Gains only ever reduce a channel (0.2..1), never boost it.
/// </summary>
public sealed class ColorBalance
{
    public double R { get; set; } = 1;
    public double G { get; set; } = 1;
    public double B { get; set; } = 1;

    public bool IsIdentity => Math.Abs(R - 1) < 1e-6 && Math.Abs(G - 1) < 1e-6 && Math.Abs(B - 1) < 1e-6;
    public void Normalize() { R = Fix(R); G = Fix(G); B = Fix(B); }
    static double Fix(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.2, 1) : 1;
    public ColorBalance Clone() => (ColorBalance)MemberwiseClone();

    public Rgb Apply(Rgb c) => IsIdentity ? c : new Rgb((byte)Math.Round(c.R * Fix(R)), (byte)Math.Round(c.G * Fix(G)), (byte)Math.Round(c.B * Fix(B)));

    /// <summary>A balanced copy of a frame (the same array when nothing changes).</summary>
    public Rgb[] Apply(Rgb[] frame)
    {
        if (IsIdentity) return frame;
        var o = new Rgb[frame.Length];
        for (int i = 0; i < frame.Length; i++) o[i] = Apply(frame[i]);
        return o;
    }

    public override string ToString() => IsIdentity ? "as sent" : $"red {R * 100:0}%, green {G * 100:0}%, blue {B * 100:0}%";
}

/// <summary>Per-device colour balance for the streamed outputs (1.15.2). Absent in older files: every device as sent.</summary>
public sealed class ColorBalances
{
    public ColorBalance Ram { get; set; } = new();
    public ColorBalance Strip { get; set; } = new();
    public ColorBalance Board { get; set; } = new();
    /// <summary>Mouse, live mode only (1.17.1): its LEDs make dim blues look pale lavender, as the memory did with violet.</summary>
    public ColorBalance Mouse { get; set; } = new();
    public void Normalize() { Ram ??= new(); Strip ??= new(); Board ??= new(); Mouse ??= new(); Ram.Normalize(); Strip.Normalize(); Board.Normalize(); Mouse.Normalize(); }
}
