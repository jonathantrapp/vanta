namespace HwSuite.Core;

/// <summary>Key names (as WPF's Key enum spells them, so saved hotkeys like "Ctrl+Shift+F12" still parse) to Linux evdev codes.</summary>
public static class EvdevKeys
{
    static readonly Dictionary<string, uint> Map = Build();

    static Dictionary<string, uint> Build()
    {
        var m = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        string row1 = "QWERTYUIOP", row2 = "ASDFGHJKL", row3 = "ZXCVBNM";
        for (int i = 0; i < row1.Length; i++) m[row1[i].ToString()] = (uint)(16 + i);
        for (int i = 0; i < row2.Length; i++) m[row2[i].ToString()] = (uint)(30 + i);
        for (int i = 0; i < row3.Length; i++) m[row3[i].ToString()] = (uint)(44 + i);
        for (int d = 1; d <= 9; d++) { m["D" + d] = (uint)(1 + d); m[d.ToString()] = (uint)(1 + d); }
        m["D0"] = m["0"] = 11;
        uint[] f = { 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 87, 88 };
        for (int i = 0; i < 12; i++) m["F" + (i + 1)] = f[i];
        for (int i = 13; i <= 24; i++) m["F" + i] = (uint)(183 + i - 13);
        m["Escape"] = m["Esc"] = 1; m["Tab"] = 15; m["Space"] = 57; m["Enter"] = m["Return"] = 28; m["Back"] = m["Backspace"] = 14;
        m["Insert"] = 110; m["Delete"] = m["Del"] = 111; m["Home"] = 102; m["End"] = 107; m["PageUp"] = m["Prior"] = 104; m["PageDown"] = m["Next"] = 109;
        m["Up"] = 103; m["Down"] = 108; m["Left"] = 105; m["Right"] = 106;
        m["Pause"] = 119; m["Scroll"] = 70; m["PrintScreen"] = m["Snapshot"] = 99;
        for (int d = 0; d <= 9; d++) m["NumPad" + d] = d == 0 ? 82u : (uint)new[] { 0, 79, 80, 81, 75, 76, 77, 71, 72, 73 }[d];
        return m;
    }

    public static bool TryGet(string name, out uint code) => Map.TryGetValue(name, out code);
}
