using System.Runtime.InteropServices;
using HwSuite.Core;
using HwSuite.Lighting;

namespace HwSuite.Ui;

/// <summary>
/// 1.20 Typing Ripples: a Windows low-level keyboard hook (WH_KEYBOARD_LL) that turns each key press into a key POSITION
/// name ("W", "Space", "Del|Delete") for <see cref="KeyActivity"/>. Installed only while Typing Ripples is the chosen
/// effect and removed as soon as it is not. It never blocks or changes a key (every event is passed on at once), ignores
/// auto-repeat, and keeps no characters, text or timing beyond KeyActivity's last 24 presses for 3 s. Nothing is logged.
/// Runs on the UI thread (the hook needs its message loop).
/// </summary>
public sealed class KeyHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    readonly LowLevelKeyboardProc _proc;   // kept alive while hooked
    readonly HashSet<uint> _down = new();
    IntPtr _hook;
    public bool Installed => _hook != IntPtr.Zero;

    public KeyHook() { _proc = Proc; }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Warn($"Typing Ripples: keyboard hook not installed (error {Marshal.GetLastWin32Error()})");
        else Log.Info("Typing Ripples: listening for key positions (no text is kept)");
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero; _down.Clear(); KeyActivity.Clear();
        Log.Info("Typing Ripples: stopped listening for keys");
    }

    IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    if (_down.Add(k.vkCode)) KeyActivity.Press(NameOf(k.vkCode));   // first press only: auto-repeat ignored
                }
                else if (msg is WM_KEYUP or WM_SYSKEYUP) _down.Remove(k.vkCode);
            }
        }
        catch { /* never let the hook fail a key */ }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>A virtual-key code as key-map names (AL80 first, then the i87's spelling). Unknown keys ripple from the centre.</summary>
    public static string NameOf(uint vk) => vk switch
    {
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x70 and <= 0x7B => "F" + (vk - 0x6F),
        0x1B => "Esc", 0x2E => "Del|Delete", 0xC0 => "`", 0xBD => "-", 0xBB => "=|+", 0x08 => "Backspace", 0x21 => "PgUp",
        0x09 => "Tab", 0xDB => "[", 0xDD => "]", 0xDC => "\\", 0x22 => "PgDn", 0x14 => "CapsLock", 0xBA => ";", 0xDE => "'",
        0x0D => "Enter", 0xA0 or 0x10 => "LShift", 0xA1 => "RShift", 0x26 => "Up", 0xA2 or 0x11 => "LCtrl", 0xA3 => "RCtrl",
        0x5B => "LWin", 0x5C => "RWin|Fn", 0xA4 or 0x12 => "LAlt", 0xA5 => "RAlt|Fn", 0x20 => "Space", 0x25 => "Left", 0x28 => "Down", 0x27 => "Right",
        0xBC => ",", 0xBE => ".", 0xBF => "/", 0x2D => "Insert", 0x24 => "Home", 0x23 => "End", 0x2C => "PrtSc", 0x91 => "SrcLock", 0x13 => "Pause",
        0x5D => "App",
        _ => "?"
    };

    public void Dispose() => Uninstall();
}
