namespace HwSuite.Lighting.Devices;

/// <summary>
/// 1.19.0: the keyboard role can be the iLovbee i87 (87 keys, KB.ini centres) or the YUNZII AL80 on SignalRGB firmware
/// (84 LEDs, firmware g_led_config points). Geometry, wave positions and the preview pick the map by LED count, which
/// differs between the two, so nothing else has to know which keyboard is attached.
/// </summary>
public static class KeyboardKeyMaps
{
    public readonly record struct KeyPos(string Name, double X, double Y, bool HasKey);

    static readonly KeyPos[] I87 = ILovbeeKeyMap.Keys.Select(k => new KeyPos(k.Name, k.X, k.Y, true)).ToArray();
    static readonly KeyPos[] Al80 = Al80KeyMap.Keys.Select(k => new KeyPos(k.Name, k.X, k.Y, k.HasKey)).ToArray();

    /// <summary>Key centres for a keyboard frame of <paramref name="count"/> LEDs (index = LED), or null if no map has that count.</summary>
    public static KeyPos[]? For(int count) => count == I87.Length ? I87 : count == Al80.Length ? Al80 : null;

    /// <summary>Left-to-right 0..1 per key of the map with that count (null as above).</summary>
    public static double[]? UnitX(int count) => count == I87.Length ? ILovbeeKeyMap.UnitX : count == Al80.Length ? Al80KeyMap.UnitX : null;

    /// <summary>The map's key pitch (one key unit) in its own coordinates: 38.6 px for the i87 drawing, about 14.9 for QMK units.</summary>
    public static double Unit(int count) => count == Al80.Length ? 224.0 / 15 : 38.6;
}
