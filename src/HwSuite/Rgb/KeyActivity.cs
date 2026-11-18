using System.Diagnostics;

namespace HwSuite.Lighting;

/// <summary>
/// 1.20 Typing Ripples: the last few key presses as (time, key name), in memory only. Fed by <c>Ui.KeyHook</c> while the
/// effect is chosen. Only the key's position name is kept (e.g. "W", "Space"), never characters, text or order beyond
/// the last <see cref="Capacity"/> presses, and each entry is dropped after <see cref="Lifetime"/>. Nothing is logged or saved.
/// </summary>
public static class KeyActivity
{
    public const int Capacity = 24;
    public static readonly double Lifetime = 3.0;
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly (double t, string key)[] Ring = new (double, string)[Capacity];
    static int _next, _count;
    static readonly object Gate = new();
    /// <summary>Tests: a fixed clock instead of the real one (seconds).</summary>
    public static Func<double>? TestClock { get; set; }

    public static double Now => TestClock?.Invoke() ?? Clock.Elapsed.TotalSeconds;
    public static long Presses { get; private set; }

    public static void Press(string key)
    {
        lock (Gate)
        {
            Ring[_next] = (Now, key);
            _next = (_next + 1) % Capacity;
            _count = Math.Min(Capacity, _count + 1);
            Presses++;
        }
    }

    public static void Clear() { lock (Gate) { _count = 0; _next = 0; } }

    /// <summary>Presses younger than <see cref="Lifetime"/>, as (age in seconds, key name), newest last. No allocation when empty.</summary>
    public static int Recent(Span<(double age, string key)> into)
    {
        lock (Gate)
        {
            double now = Now; int n = 0;
            for (int k = _count; k >= 1 && n < into.Length; k--)
            {
                var e = Ring[(_next - k + Capacity) % Capacity];
                double age = now - e.t;
                if (age >= 0 && age < Lifetime) into[n++] = (age, e.key);
            }
            return n;
        }
    }
}
