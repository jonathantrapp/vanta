namespace HwSuite.Lighting.Devices;

/// <summary>
/// YUNZII AL80 per-key LEDs (stock firmware rev 0x0125 and YUNZII_AL80_SIGNALRGB_V0122.bin rev 0x0122 carry the identical
/// table; the .bin's SHA-256
/// 761272e5e7b542da709020faeccecbfc0daa3acab6875f828a565c44a632c815). Generated from the firmware's own compiled
/// <c>g_led_config</c> (matrix-to-LED table at file offset 0xF908, LED points at 0xF962, all flags 4), not from the
/// SignalRGB plugin, whose order is one off from LED 14 (it assumes a Mute LED; the knob has none). Keys are named by
/// matrix position through YUNZII's VIA JSON and keymap. X/Y are QMK LED units (0..224 across, 0..64 down).
/// Five LEDs have no key: 56 (right of '), 59 (right of LShift) and 78-80 (stacked at x 183 between RCtrl and Left).
/// Verified on the owner's AL80 2026-11-12 (row and colour-pattern tests): the order is right; the five key-less LEDs
/// never lit (not fitted). They are still sent (harmless) so the frame matches the firmware's 84.
/// </summary>
public static class Al80KeyMap
{
    public readonly record struct Key(int Led, string Name, double X, double Y, int Row, int Col)
    {
        public bool HasKey => Row >= 0;
    }

    /// <summary>LEDs the firmware drives (GET_TOTAL_LEDS 0x27 replies 0x54; the stream accepts start + count &lt;= 84).</summary>
    public const int Leds = 84;

    /// <summary>Index = LED index on the wire.</summary>
    public static readonly Key[] Keys =
    {
        new(0, "Esc", 0, 0, 0, 0),
        new(1, "F1", 19, 0, 0, 1),
        new(2, "F2", 34, 0, 0, 2),
        new(3, "F3", 49, 0, 0, 3),
        new(4, "F4", 63, 0, 0, 4),
        new(5, "F5", 82, 0, 0, 5),
        new(6, "F6", 97, 0, 0, 6),
        new(7, "F7", 112, 0, 0, 7),
        new(8, "F8", 127, 0, 0, 8),
        new(9, "F9", 146, 0, 0, 9),
        new(10, "F10", 161, 0, 0, 10),
        new(11, "F11", 175, 0, 0, 11),
        new(12, "F12", 190, 0, 0, 12),
        new(13, "Del", 209, 0, 0, 13),
        new(14, "`", 0, 13, 1, 0),
        new(15, "1", 15, 13, 1, 1),
        new(16, "2", 30, 13, 1, 2),
        new(17, "3", 45, 13, 1, 3),
        new(18, "4", 60, 13, 1, 4),
        new(19, "5", 75, 13, 1, 5),
        new(20, "6", 89, 13, 1, 6),
        new(21, "7", 104, 13, 1, 7),
        new(22, "8", 119, 13, 1, 8),
        new(23, "9", 135, 13, 1, 9),
        new(24, "0", 149, 13, 1, 10),
        new(25, "-", 164, 13, 1, 11),
        new(26, "=", 179, 13, 1, 12),
        new(27, "Backspace", 202, 13, 1, 13),
        new(28, "PgUp", 224, 13, 1, 14),
        new(29, "Tab", 4, 26, 2, 0),
        new(30, "Q", 22, 26, 2, 1),
        new(31, "W", 37, 26, 2, 2),
        new(32, "E", 52, 26, 2, 3),
        new(33, "R", 67, 26, 2, 4),
        new(34, "T", 82, 26, 2, 5),
        new(35, "Y", 97, 26, 2, 6),
        new(36, "U", 112, 26, 2, 7),
        new(37, "I", 127, 26, 2, 8),
        new(38, "O", 142, 26, 2, 9),
        new(39, "P", 157, 26, 2, 10),
        new(40, "[", 172, 26, 2, 11),
        new(41, "]", 187, 26, 2, 12),
        new(42, "\\", 202, 26, 2, 13),
        new(43, "PgDn", 224, 26, 2, 14),
        new(44, "CapsLock", 5, 39, 3, 0),
        new(45, "A", 26, 39, 3, 1),
        new(46, "S", 41, 39, 3, 2),
        new(47, "D", 56, 39, 3, 3),
        new(48, "F", 71, 39, 3, 4),
        new(49, "G", 86, 39, 3, 5),
        new(50, "H", 101, 39, 3, 6),
        new(51, "J", 116, 39, 3, 7),
        new(52, "K", 131, 39, 3, 8),
        new(53, "L", 146, 39, 3, 9),
        new(54, ";", 161, 39, 3, 10),
        new(55, "'", 176, 39, 3, 11),
        new(56, "(LED 56)", 184, 39, -1, -1),   // matrix (3,12): no key there in YUNZII's layout
        new(57, "Enter", 190, 39, 3, 13),
        new(58, "LShift", 9, 52, 4, 0),
        new(59, "(LED 59)", 24, 52, -1, -1),    // matrix (4,1): no key there (ANSI left shift spans it)
        new(60, "Z", 34, 52, 4, 2),
        new(61, "X", 49, 52, 4, 3),
        new(62, "C", 63, 52, 4, 4),
        new(63, "V", 78, 52, 4, 5),
        new(64, "B", 93, 52, 4, 6),
        new(65, "N", 108, 52, 4, 7),
        new(66, "M", 123, 52, 4, 8),
        new(67, ",", 138, 52, 4, 9),
        new(68, ".", 153, 52, 4, 10),
        new(69, "/", 168, 52, 4, 11),
        new(70, "RShift", 189, 52, 4, 12),
        new(71, "Up", 209, 52, 4, 13),
        new(72, "LCtrl", 2, 64, 5, 0),
        new(73, "LWin", 21, 64, 5, 1),
        new(74, "LAlt", 39, 64, 5, 2),
        new(75, "Space", 95, 64, 5, 6),
        new(76, "Fn", 151, 64, 5, 10),
        new(77, "RCtrl", 170, 64, 5, 11),
        new(78, "(LED 78)", 183, 60, -1, -1),
        new(79, "(LED 79)", 183, 62, -1, -1),
        new(80, "(LED 80)", 183, 64, -1, -1),
        new(81, "Left", 194, 64, 5, 12),
        new(82, "Down", 209, 64, 5, 13),
        new(83, "Right", 224, 64, 5, 14),
    };

    static double[]? _unitX;

    /// <summary>Left-to-right position of each LED, 0..1 across the keyboard.</summary>
    public static double[] UnitX => _unitX ??= Keys.Select(k => k.X / 224.0).ToArray();
}
