namespace HwSuite.Lighting;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Black => new(0, 0, 0);

    public static Rgb FromInt(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public int ToInt() => (R << 16) | (G << 8) | B;

    public static Rgb Lerp(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    public Rgb Scale(double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return new Rgb((byte)Math.Round(R * factor), (byte)Math.Round(G * factor), (byte)Math.Round(B * factor));
    }

    public static Rgb FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return new Rgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public int MaxDelta(Rgb o) => Math.Max(Math.Abs(R - o.R), Math.Max(Math.Abs(G - o.G), Math.Abs(B - o.B)));

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
