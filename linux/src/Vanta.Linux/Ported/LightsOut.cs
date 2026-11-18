// PORTED from src\HwSuite\Core\LightsOut.cs (Windows 1.20.0): only TryParseHotkey changes (WPF Key/VK -> evdev key codes).

namespace HwSuite.Core;

/// <summary>
/// "Lights out": one action that switches every included lighting zone to Stealth and, on the second press, restores
/// exactly the effect (and output state) that was active before. The restore point is kept in the config so it survives
/// a restart. Used by the header button, the tray menu and the global hotkey.
/// </summary>
public static class LightsOut
{
    public static bool IsOn(RgbConfig c) => c.Enabled && c.Effect == EffectKind.Stealth;

    /// <summary>One lighting zone as Lights out sees it.</summary>
    public sealed record Zone(string Name, bool Included, bool Available, string? Blocked, bool Writable);

    public sealed record Report(int Off, int Total, List<string> Lines)
    {
        public string Headline => Off == Total ? $"All {Total} zones off" : $"{Off} of {Total} zones off";
    }

    /// <summary>
    /// What Lights out can honestly claim: a zone is "off" only if it is included, connected, not owned by another program
    /// and writable (the keyboard only after its verification). Everything else is listed with the reason.
    /// </summary>
    public static Report Summarize(IEnumerable<Zone> zones)
    {
        var lines = new List<string>();
        int off = 0, total = 0;
        foreach (var z in zones)
        {
            total++;
            string? why = !z.Available ? "not connected" : !z.Included ? "not included in lighting" : z.Blocked != null ? z.Blocked : !z.Writable ? "not verified yet, so VANTA does not write to it" : null;
            if (why == null) { off++; lines.Add($"{z.Name}: off"); }
            else lines.Add($"{z.Name}: unchanged ({why})");
        }
        return new Report(off, total, lines);
    }

    /// <summary>Toggle; returns true when the lights are now out.</summary>
    public static bool Toggle(AppConfig cfg)
    {
        var c = cfg.Rgb;
        lock (cfg.Sync)
        {
            if (!IsOn(c))
            {
                c.LightsOutRestoreEffect = c.Effect == EffectKind.Stealth ? EffectKind.Static : c.Effect;
                c.LightsOutRestoreEnabled = c.Enabled;
                c.Effect = EffectKind.Stealth;
                c.Enabled = true;   // output must be on to switch the LEDs off; the restore point remembers if it was off
                return true;
            }
            c.Effect = c.LightsOutRestoreEffect ?? EffectKind.Static;
            c.Enabled = c.LightsOutRestoreEnabled ?? true;
            c.LightsOutRestoreEffect = null;
            c.LightsOutRestoreEnabled = null;
            return false;
        }
    }

    /// <summary>
    /// LINUX: parse "Ctrl+Shift+F12" style text into the same modifier bits as Windows (Alt 1, Ctrl 2, Shift 4, Win 8,
    /// NoRepeat 0x4000) and a Linux evdev key code (linux/input-event-codes.h), which the service's evdev reader matches.
    /// Same rules: exactly one key, at least one modifier.
    /// </summary>
    public static bool TryParseHotkey(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        uint? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= 0x0002; break;
                case "shift": modifiers |= 0x0004; break;
                case "alt": modifiers |= 0x0001; break;
                case "win": case "super": case "meta": modifiers |= 0x0008; break;
                default:
                    if (key != null) return false;
                    if (!EvdevKeys.TryGet(raw, out var k)) return false;
                    key = k; break;
            }
        }
        if (key == null || modifiers == 0) return false;
        vk = key.Value;
        modifiers |= 0x4000;
        return vk != 0;
    }
}
