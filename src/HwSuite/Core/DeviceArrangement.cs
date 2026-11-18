using System.Text.Json.Serialization;
using HwSuite.Lighting;

namespace HwSuite.Core;

/// <summary>Where one device group sits in the owner's arrangement (canvas units, centre of its drawing) and its turn.</summary>
public sealed class DevicePlacement
{
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Clockwise quarter turns in degrees (0, 90, 180, 270). Only the keyboard and the mouse turn.</summary>
    public int Rotation { get; set; }

    public DevicePlacement Clone() => new() { X = X, Y = Y, Rotation = Rotation };
    public override bool Equals(object? o) => o is DevicePlacement p && Math.Abs(p.X - X) < 1e-6 && Math.Abs(p.Y - Y) < 1e-6 && p.Rotation == Rotation;
    public override int GetHashCode() => HashCode.Combine(Math.Round(X, 3), Math.Round(Y, 3), Rotation);
}

/// <summary>
/// The owner's physical arrangement of the lighting device groups (Lighting > Arrange devices). A flat canvas of
/// <see cref="CanvasWidth"/> x <see cref="CanvasHeight"/> units seen from the front of the desk; each group keeps its
/// recognisable drawing and nominal size. Groups are the real, verified ones: the front fans and the Lian Li logo are
/// one D_LED chain and move together; the memory sticks are drawn in controller detection order (not a slot map);
/// nothing inside a group is re-mapped. Saved in config.json only when an edit is completed (drop, key release, rotate,
/// reset), never per drag step. Not part of a saved lighting look.
/// </summary>
public sealed class DeviceArrangement
{
    public const double CanvasWidth = 1000, CanvasHeight = 420, CaptionHeight = 40;

    /// <summary>False until the owner arranges devices: effects then use the fixed default order (identical to 1.11).</summary>
    public bool Arranged { get; set; }
    /// <summary>Keyed by <see cref="DeviceRole"/> name. Missing or invalid entries fall back to the default position.</summary>
    public Dictionary<string, DevicePlacement> Devices { get; set; } = new();

    public static readonly DeviceRole[] Groups = { DeviceRole.Strip, DeviceRole.Ram, DeviceRole.Board, DeviceRole.Keyboard, DeviceRole.Mouse };

    /// <summary>Nominal drawing size of each group, unrotated (canvas units, without its caption).</summary>
    public static (double W, double H) Size(DeviceRole r) => r switch
    {
        DeviceRole.Strip => (130, 250),
        DeviceRole.Ram => (150, 220),
        DeviceRole.Board => (130, 190),
        DeviceRole.Keyboard => (400, 150),
        _ => (70, 110)
    };

    /// <summary>Size after rotation (a quarter turn swaps width and height).</summary>
    public static (double W, double H) RotatedSize(DeviceRole r, int rotation)
    {
        var (w, h) = Size(r);
        return rotation is 90 or 270 ? (h, w) : (w, h);
    }

    public static bool Rotatable(DeviceRole r) => r is DeviceRole.Keyboard or DeviceRole.Mouse;

    /// <summary>The default row, in the same order and proportions as the preview before arranging.</summary>
    public static DevicePlacement Default(DeviceRole r) => r switch
    {
        DeviceRole.Strip => new() { X = 85, Y = 155 },
        DeviceRole.Ram => new() { X = 245, Y = 155 },
        DeviceRole.Board => new() { X = 385, Y = 155 },
        DeviceRole.Keyboard => new() { X = 660, Y = 155 },
        _ => new() { X = 925, Y = 155 }
    };

    public DevicePlacement Get(DeviceRole r) => Devices.TryGetValue(r.ToString(), out var p) && p != null ? p : Default(r);

    /// <summary>Keep the whole drawing (and its caption) inside the canvas; snap rotation; non-rotating groups stay at 0.</summary>
    public static DevicePlacement Clamp(DeviceRole r, DevicePlacement p)
    {
        int rot = Rotatable(r) ? ((int)Math.Round(p.Rotation / 90.0) * 90 % 360 + 360) % 360 : 0;
        var (w, h) = RotatedSize(r, rot);
        double x = double.IsFinite(p.X) ? p.X : Default(r).X, y = double.IsFinite(p.Y) ? p.Y : Default(r).Y;
        x = Math.Clamp(x, w / 2, CanvasWidth - w / 2);
        y = Math.Clamp(y, h / 2, Math.Max(h / 2, CanvasHeight - CaptionHeight - h / 2));
        return new() { X = Math.Round(x, 1), Y = Math.Round(y, 1), Rotation = rot };
    }

    /// <summary>After loading: drop unknown keys, repair invalid values.</summary>
    public void Normalize()
    {
        Devices ??= new();
        var fixedUp = new Dictionary<string, DevicePlacement>();
        foreach (var r in Groups)
            if (Devices.TryGetValue(r.ToString(), out var p) && p != null) fixedUp[r.ToString()] = Clamp(r, p);
        Devices = fixedUp;
    }

    public DeviceArrangement Clone() => new() { Arranged = Arranged, Devices = Devices.ToDictionary(k => k.Key, v => v.Value.Clone()) };

    /// <summary>Set one group (clamped); marks the arrangement as the owner's.</summary>
    public void Set(DeviceRole r, DevicePlacement p) { Devices[r.ToString()] = Clamp(r, p); Arranged = true; }

    /// <summary>Back to the default row and the fixed default effect order.</summary>
    public void Reset() { Devices.Clear(); Arranged = false; }

    public bool SameAs(DeviceArrangement o) =>
        o.Arranged == Arranged && Groups.All(g => Get(g).Equals(o.Get(g)));

    /// <summary>Bounding box of a group's drawing (rotation applied), in canvas units, without its caption.</summary>
    public (double L, double T, double W, double H) Bounds(DeviceRole r)
    {
        var p = Get(r);
        var (w, h) = RotatedSize(r, p.Rotation);
        return (p.X - w / 2, p.Y - h / 2, w, h);
    }

    [JsonIgnore] public static string AxisNote => "Gradient Wave travels left to right across the arrangement.";
}
