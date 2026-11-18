using System.Text.Json.Serialization;
using HwSuite.Lighting;

namespace HwSuite.Core;

/// <summary>A lit part on the D_LED chain.</summary>
/// <summary>TopFront/TopRear (1.15.3): the two radiator fans under the roof (front-side and rear-side).</summary>
public enum CasePartKind { FrontTop, FrontMiddle, FrontBottom, Rear, Logo, TopFront, TopRear }

/// <summary>Order of the parts along the D_LED chain (which part gets the first LEDs).</summary>
public enum CaseChainOrder { FrontDownRearLogo, FrontUpRearLogo, RearFrontDownLogo, LogoFrontDownRear }

/// <summary>
/// What is lit on the D_LED chain and where it sits in the case, for the atmosphere effects and the preview. 1.15 (owner,
/// 2026-10-21): three Thermalright TL-C12C-S front intakes stacked top to bottom, one at the rear (top of the back panel),
/// and the Lian Li logo at the bottom of the front. The physical chain order and the LEDs per fan are not verified yet:
/// when <see cref="LedsPerFan"/> is 0 the chain's LEDs are shared out evenly (a fan counts 1, the logo 0.4), so the
/// effects still move through the case in the right places on the preview, and the strip card says the split is assumed.
/// Changes only how colours are laid out across the chain's LEDs; the number of LEDs sent stays <see cref="RgbConfig.DLedCount"/>.
/// </summary>
public sealed class CaseLayout
{
    public int FrontFans { get; set; } = 3;
    public bool RearFan { get; set; } = true;
    public bool Logo { get; set; } = true;
    /// <summary>Lit fans under the roof (the Arctic radiator's two fans since 2026-10-21), 0..2. Older files: 0.</summary>
    public int TopFans { get; set; }
    /// <summary>LEDs on each fan; 0 = not known (shared out evenly).</summary>
    public int LedsPerFan { get; set; }
    /// <summary>LEDs on the logo; 0 = not known.</summary>
    public int LogoLeds { get; set; }
    public CaseChainOrder Order { get; set; } = CaseChainOrder.FrontDownRearLogo;
    /// <summary>
    /// The case hub copies one signal to every port (1.15.1: the owner's layout check on 2026-10-21 lit all four fans red,
    /// so the LANCOOL 215 hub is a splitter, not a chain). Every fan then shows the same <see cref="LedsPerFan"/> LEDs and the
    /// logo the first <see cref="LogoLeds"/> of them; the effects run on that one ring, drawn top to bottom of the case.
    /// </summary>
    public bool Mirrored { get; set; }
    /// <summary>Which LED (0-based) sits at the top of each fan, and whether the numbers run counter-clockwise (seen from the front).</summary>
    public int TopLed { get; set; }
    public bool CounterClockwise { get; set; }

    public static CaseLayout Default => new();
    public CaseLayout Clone() => (CaseLayout)MemberwiseClone();
    public void Normalize()
    {
        FrontFans = Math.Clamp(FrontFans, 0, 3);
        TopFans = Math.Clamp(TopFans, 0, 2);
        LedsPerFan = Math.Clamp(LedsPerFan, 0, 60);
        LogoLeds = Math.Clamp(LogoLeds, 0, 60);
        if (!Enum.IsDefined(Order)) Order = CaseChainOrder.FrontDownRearLogo;
        if (FrontFans == 0 && !RearFan && !Logo) FrontFans = 1;
        TopLed = Math.Clamp(TopLed, 0, Math.Max(0, LedsPerFan - 1));
    }

    [JsonIgnore] public bool CountsKnown => Mirrored ? LedsPerFan > 0 : (FansCount == 0 || LedsPerFan > 0) && (!Logo || LogoLeds > 0);
    [JsonIgnore] public int FansCount => FrontFans + (RearFan ? 1 : 0) + TopFans;
    /// <summary>LEDs the layout describes when the counts are known (else 0).</summary>
    [JsonIgnore] public int KnownTotal => !CountsKnown ? 0 : Mirrored ? Math.Max(LedsPerFan, Logo ? LogoLeds : 0) : FansCount * LedsPerFan + (Logo ? LogoLeds : 0);

    /// <summary>Angle of LED <paramref name="i"/> of an <paramref name="n"/>-LED fan (radians, 0 = right, -pi/2 = top), from the top LED and direction.</summary>
    public double LedAngle(int i, int n)
    {
        if (n <= 0) return -Math.PI / 2;
        int k = ((i - TopLed) % n + n) % n;
        return -Math.PI / 2 + (CounterClockwise ? -1 : 1) * 2 * Math.PI * k / n;
    }

    /// <summary>The parts in chain order.</summary>
    public IReadOnlyList<CasePartKind> Chain()
    {
        var front = new List<CasePartKind>();
        if (FrontFans >= 1) front.Add(CasePartKind.FrontTop);
        if (FrontFans >= 3) front.Add(CasePartKind.FrontMiddle);
        if (FrontFans >= 2) front.Add(CasePartKind.FrontBottom);
        var up = Enumerable.Reverse(front).ToList();
        var rear = (RearFan ? new[] { CasePartKind.Rear } : Array.Empty<CasePartKind>())
            .Concat(new[] { CasePartKind.TopFront, CasePartKind.TopRear }.Take(TopFans)).ToArray();   // radiator fans follow the rear fan
        var logo = Logo ? new[] { CasePartKind.Logo } : Array.Empty<CasePartKind>();
        return Order switch
        {
            CaseChainOrder.FrontUpRearLogo => up.Concat(rear).Concat(logo).ToList(),
            CaseChainOrder.RearFrontDownLogo => rear.Concat(front).Concat(logo).ToList(),
            CaseChainOrder.LogoFrontDownRear => logo.Concat(front).Concat(rear).ToList(),
            _ => front.Concat(rear).Concat(logo).ToList()
        };
    }

    /// <summary>
    /// Which LEDs of a <paramref name="total"/>-LED chain belong to each part, in chain order. Known counts are used as
    /// given (LEDs past the layout belong to no part; a short chain cuts the last parts). Unknown counts share the chain out.
    /// </summary>
    public IReadOnlyList<(CasePartKind Kind, int Start, int Count)> Allocate(int total)
    {
        var chain = Chain();
        var result = new List<(CasePartKind, int, int)>();
        if (total <= 0 || chain.Count == 0) return result;
        // a splitter hub: one ring that every fan shows (the logo shows its first LEDs too)
        if (Mirrored) { result.Add((CasePartKind.FrontTop, 0, Math.Min(total, LedsPerFan > 0 ? LedsPerFan : total))); return result; }
        var counts = new int[chain.Count];
        if (CountsKnown)
            for (int i = 0; i < chain.Count; i++) counts[i] = chain[i] == CasePartKind.Logo ? LogoLeds : LedsPerFan;
        else
        {
            double W(CasePartKind k) => k == CasePartKind.Logo ? (LogoLeds > 0 ? -1 : 0.4) : (LedsPerFan > 0 ? -1 : 1);
            int fixedSum = chain.Sum(k => k == CasePartKind.Logo ? LogoLeds : LedsPerFan);
            int free = Math.Max(0, total - fixedSum);
            double wsum = chain.Sum(k => Math.Max(0, W(k)));
            // equal shares for equal parts: whole LEDs first, then the leftovers to the largest remainders (earlier parts win ties)
            var ideal = new double[chain.Count];
            int given = 0;
            for (int i = 0; i < chain.Count; i++)
            {
                var k = chain[i];
                if (W(k) < 0) { counts[i] = k == CasePartKind.Logo ? LogoLeds : LedsPerFan; continue; }
                ideal[i] = free * W(k) / Math.Max(1e-9, wsum);
                counts[i] = (int)Math.Floor(ideal[i]); given += counts[i];
            }
            foreach (int i in Enumerable.Range(0, chain.Count).Where(i => W(chain[i]) >= 0).OrderByDescending(i => Math.Round(ideal[i] - counts[i], 6)).ThenBy(i => i).Take(Math.Max(0, free - given)).ToList())
                counts[i]++;
        }
        int start = 0;
        for (int i = 0; i < chain.Count && start < total; i++)
        {
            int c = Math.Min(counts[i], total - start);
            if (c <= 0) continue;
            result.Add((chain[i], start, c));
            start += c;
        }
        return result;
    }

    /// <summary>
    /// Where a part sits, in case units (front panel at x = 0.18, rear at 0.82, top y = 0, bottom 1), and its radius.
    /// The front fans are evenly stacked; the rear fan sits near the top of the back panel; the logo at the bottom front.
    /// </summary>
    public static (double X, double Y, double R) Place(CasePartKind k) => k switch
    {
        CasePartKind.FrontTop => (0.18, 0.19, 0.15),
        CasePartKind.FrontMiddle => (0.18, 0.50, 0.15),
        CasePartKind.FrontBottom => (0.18, 0.81, 0.15),
        CasePartKind.Rear => (0.82, 0.19, 0.15),
        CasePartKind.TopFront => (0.36, 0.035, 0.1),
        CasePartKind.TopRear => (0.58, 0.035, 0.1),
        _ => (0.18, 0.985, 0.0)
    };

    /// <summary>Colour each part shows in the layout check (distinct and easy to name).</summary>
    public static (Rgb Color, string Name) CheckColor(CasePartKind k) => k switch
    {
        CasePartKind.FrontTop => (new Rgb(255, 0, 0), "red"),
        CasePartKind.FrontMiddle => (new Rgb(0, 255, 0), "green"),
        CasePartKind.FrontBottom => (new Rgb(0, 0, 255), "blue"),
        CasePartKind.Rear => (new Rgb(255, 150, 0), "amber"),
        CasePartKind.TopFront => (new Rgb(0, 255, 255), "cyan"),
        CasePartKind.TopRear => (new Rgb(255, 0, 255), "magenta"),
        _ => (new Rgb(255, 255, 255), "white")
    };

    /// <summary>The layout check frame for a chain of <paramref name="total"/> LEDs: each part in its colour, LEDs outside the layout off.</summary>
    public Rgb[] CheckPattern(int total)
    {
        var f = new Rgb[Math.Max(0, total)];
        if (Mirrored)
        {
            // a rainbow around the ring: LED 1 red, then orange, yellow, green, cyan, blue, purple, pink (shows where LED 1 sits and which way they run)
            int n = Math.Min(total, LedsPerFan > 0 ? LedsPerFan : total);
            for (int i = 0; i < n; i++) f[i] = Hue((double)i / Math.Max(1, n));
            return f;
        }
        foreach (var (kind, start, count) in Allocate(total))
            for (int i = start; i < start + count; i++) f[i] = CheckColor(kind).Color;
        return f;
    }

    static Rgb Hue(double h)
    {
        double r = Math.Clamp(Math.Abs(h * 6 - 3) - 1, 0, 1), g = Math.Clamp(2 - Math.Abs(h * 6 - 2), 0, 1), b = Math.Clamp(2 - Math.Abs(h * 6 - 4), 0, 1);
        return new Rgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    public static string Name(CasePartKind k) => k switch
    {
        CasePartKind.FrontTop => "front top fan", CasePartKind.FrontMiddle => "front middle fan", CasePartKind.FrontBottom => "front bottom fan",
        CasePartKind.Rear => "rear fan", CasePartKind.TopFront => "radiator fan (front)", CasePartKind.TopRear => "radiator fan (rear)", _ => "logo"
    };

    public static string OrderLabel(CaseChainOrder o) => o switch
    {
        CaseChainOrder.FrontUpRearLogo => "Front bottom → top, rear, logo",
        CaseChainOrder.RearFrontDownLogo => "Rear, front top → bottom, logo",
        CaseChainOrder.LogoFrontDownRear => "Logo, front top → bottom, rear",
        _ => "Front top → bottom, rear, logo"
    };
}
