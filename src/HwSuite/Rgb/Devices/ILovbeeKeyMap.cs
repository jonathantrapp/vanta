namespace HwSuite.Lighting.Devices;

/// <summary>
/// iLovbee i87 key layout, generated from the vendor's Dev\013C\KB.ini (SHA-256 4c91893723a7e6eef67515816a7c406671a4f0904948e45612cba5c17c022d9d,
/// the file DeviceDriver.exe fd4fe74e… loads for PID 013C). Each [KEY] entry is "Kn=x0,y0,x1,y1, type,vk,?,matrix"; the
/// vendor parser (0x451861) stores the last field as the key's LED matrix index (record +4/+5/+6), and its music/real-time
/// thread (0x438850) writes a key's colour at 3 x matrix index of a 126-LED (0x7E) buffer in RGBIndex order, which is
/// R,G,B when KB.ini has no RGBIndex/MusicRGBIndex (0x451FCE). X/Y are the key rectangle centres in the vendor's
/// layout image pixels, used only for spatial effect offsets.
/// </summary>
public static class ILovbeeKeyMap
{
    public readonly record struct Key(int Matrix, string Name, double X, double Y);

    public const int MatrixLeds = 126;

    public static readonly Key[] Keys =
    {
        new(0, "Esc", 50.5, 37.0),
        new(12, "F1", 128.5, 37.0),
        new(18, "F2", 167.5, 37.0),
        new(24, "F3", 206.5, 37.0),
        new(30, "F4", 244.5, 37.0),
        new(36, "F5", 301.5, 37.0),
        new(42, "F6", 340.5, 37.0),
        new(48, "F7", 379.5, 37.0),
        new(54, "F8", 417.5, 37.0),
        new(60, "F9", 476.5, 36.0),
        new(66, "F10", 515.5, 36.0),
        new(72, "F11", 553.5, 36.0),
        new(78, "F12", 592.5, 36.0),
        new(84, "PrtSc", 641.5, 37.0),
        new(90, "SrcLock", 679.5, 37.0),
        new(96, "Pause", 718.5, 37.0),
        new(1, "`", 49.5, 92.0),
        new(7, "1", 88.5, 91.0),
        new(13, "2", 127.5, 91.0),
        new(19, "3", 166.5, 92.0),
        new(25, "4", 205.5, 92.0),
        new(31, "5", 244.5, 92.0),
        new(37, "6", 282.5, 92.0),
        new(43, "7", 320.5, 92.0),
        new(49, "8", 359.5, 92.0),
        new(55, "9", 398.5, 91.0),
        new(61, "0", 436.5, 91.0),
        new(67, "-", 475.5, 92.0),
        new(73, "?", 513.5, 92.0),
        new(79, "Backspace", 571.5, 91.0),
        new(85, "Insert", 640.5, 96.0),
        new(91, "Home", 678.5, 96.0),
        new(97, "PgUp", 717.5, 96.0),
        new(2, "Tab", 59.5, 132.0),
        new(8, "Q", 108.5, 132.0),
        new(14, "W", 147.5, 132.0),
        new(20, "E", 186.5, 132.0),
        new(26, "R", 224.5, 132.0),
        new(32, "T", 263.5, 131.0),
        new(38, "Y", 301.5, 131.0),
        new(44, "U", 340.5, 131.0),
        new(50, "I", 379.5, 131.0),
        new(56, "O", 417.5, 132.0),
        new(62, "P", 456.5, 132.0),
        new(68, "[", 495.5, 132.0),
        new(74, "]", 533.5, 132.0),
        new(80, "\\", 582.5, 132.0),
        new(86, "Delete", 639.5, 136.0),
        new(92, "End", 678.5, 136.0),
        new(98, "PgDn", 717.5, 136.0),
        new(3, "CapsLock", 63.5, 172.0),
        new(9, "A", 117.5, 172.0),
        new(15, "S", 155.5, 172.0),
        new(21, "D", 194.5, 172.0),
        new(27, "F", 233.5, 172.0),
        new(33, "G", 272.5, 172.0),
        new(39, "H", 311.5, 171.0),
        new(45, "J", 349.5, 171.0),
        new(51, "K", 388.5, 172.0),
        new(57, "L", 427.5, 172.0),
        new(63, ";", 466.5, 172.0),
        new(69, "'", 504.5, 172.0),
        new(81, "Enter", 568.5, 172.0),
        new(4, "LShift", 73.0, 212.0),
        new(10, "Z", 137.5, 212.0),
        new(16, "X", 176.5, 212.0),
        new(22, "C", 215.5, 212.0),
        new(28, "V", 253.5, 212.0),
        new(34, "B", 292.5, 212.0),
        new(40, "N", 331.5, 212.0),
        new(46, "M", 369.5, 212.0),
        new(52, ",", 409.5, 212.0),
        new(58, ".", 447.5, 212.0),
        new(64, "/", 485.5, 212.0),
        new(82, "RShift", 557.5, 212.0),
        new(94, "Up", 680.5, 212.0),
        new(5, "LCtrl", 54.0, 252.0),
        new(11, "LWin", 103.0, 252.0),
        new(17, "LAlt", 151.0, 252.0),
        new(35, "Space", 296.5, 252.0),
        new(53, "RAlt", 443.0, 252.0),
        new(59, "Fn", 491.0, 252.0),
        new(65, "App", 540.0, 252.0),
        new(83, "RCtrl", 588.0, 252.0),
        new(89, "Left", 641.5, 253.0),
        new(95, "Down", 680.5, 252.0),
        new(101, "Right", 719.5, 253.0),
    };

    static double[]? _positions;

    /// <summary>Left-to-right position of each key, 0..1 across the keyboard.</summary>
    public static double[] UnitX
    {
        get
        {
            if (_positions != null) return _positions;
            double min = Keys.Min(k => k.X), max = Keys.Max(k => k.X);
            return _positions = Keys.Select(k => (k.X - min) / Math.Max(1, max - min)).ToArray();
        }
    }
}
