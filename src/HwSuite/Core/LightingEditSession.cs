namespace HwSuite.Core;

/// <summary>
/// "Revert changes" for lighting experiments: one in-memory snapshot of the lighting appearance, no history.
///
/// Rules (1.14):
/// - The first real appearance edit, from any entry point (Lighting page controls, the Look selector, the tray's look
///   menu, colours from an image), calls <see cref="BeforeEdit"/>, which captures the settings as they were. Later edits
///   keep that original snapshot.
/// - Lights out, lighting output on/off, opening a page, selecting a device, device inclusion and the RAM mode are not
///   appearance edits: they neither start nor end a session, and Revert never touches them.
/// - <see cref="Revert"/> restores the snapshot through the shared look path (<see cref="LightingLooks.Apply"/>), so
///   Lights out and output off keep precedence: while dark, only the look shown when the lights come back changes.
///   It then ends the session; the restored settings are the new baseline.
/// - <see cref="Keep"/> accepts the current result as the new baseline (ends the session without changing anything).
/// - A session whose settings are back to exactly the snapshot has nothing to revert and ends by itself.
/// - Memory only: quitting VANTA keeps whatever is current, like before. The snapshot is shown with its time, so an old one
///   never undoes a later choice by surprise.
/// Appearance = effect (the one shown when the lights are on), primary and secondary colour, brightness, speed, density,
/// glow and lightning. Nothing else (fans, startup, verification, calibration, ownership, mappings, inclusion, RAM mode).
/// </summary>
public sealed class LightingEditSession
{
    readonly AppConfig _cfg;
    LightingLook? _baseline;

    public LightingEditSession(AppConfig cfg) { _cfg = cfg; }

    public DateTime? Since { get; private set; }
    /// <summary>
    /// Raised when a session ends (reverted, kept, or back to its snapshot), for UI refresh. Not raised when one starts:
    /// at that moment the edit has not been applied yet, so a refresh would see "no changes" (callers refresh after it).
    /// </summary>
    public event Action? Changed;

    public bool Active => _baseline != null;
    /// <summary>A copy of the snapshot (null without a session).</summary>
    public LightingLook? Baseline => _baseline?.Copy();

    /// <summary>There is something to revert: a session exists and the settings differ from its snapshot.</summary>
    public bool HasChanges => _baseline is { } b && !SameAppearance(_cfg, b);

    /// <summary>Call right before an appearance edit. Captures the snapshot only if no session is running.</summary>
    public void BeforeEdit(string from)
    {
        if (_baseline != null) return;
        _baseline = Snapshot(_cfg);
        Since = DateTime.Now;
        Log.Info($"lighting edit session started ({from}); revert point: {_baseline.Summary}");
    }

    /// <summary>Restore the snapshot (Lights out and output off keep precedence) and end the session. False if there was none.</summary>
    public bool Revert(string from)
    {
        if (_baseline is not { } b) return false;
        _baseline = null; Since = null;
        LightingLooks.Apply(_cfg, b);
        Log.Info($"lighting changes reverted ({from}): {b.Summary}" + (LightsOut.IsOn(_cfg.Rgb) ? " (Lights out stays on; shown when the lights come back)" : !_cfg.Rgb.Enabled ? " (lighting output stays off)" : ""));
        Changed?.Invoke();
        return true;
    }

    /// <summary>Accept the current settings as the new baseline.</summary>
    public void Keep(string from)
    {
        if (_baseline == null) return;
        _baseline = null; Since = null;
        Log.Info($"lighting changes kept ({from})");
        Changed?.Invoke();
    }

    /// <summary>End a session that has nothing to revert (the settings are back to the snapshot). True if one ended.</summary>
    public bool EndIfUnchanged()
    {
        if (_baseline == null || HasChanges) return false;
        _baseline = null; Since = null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>The appearance fields, all filled in (so applying the snapshot restores each of them).</summary>
    public static LightingLook Snapshot(AppConfig cfg)
    {
        lock (cfg.Sync)
        {
            var c = cfg.Rgb;
            return new LightingLook
            {
                Name = "your settings before the changes", Effect = LightingLooks.EffectiveEffect(c), ColorA = c.ColorA & 0xFFFFFF, ColorB = c.ColorB & 0xFFFFFF,
                Brightness = c.Brightness, Speed = c.Speed, Density = c.Density, Glow = c.Glow, Lightning = c.Lightning
            };
        }
    }

    public static bool SameAppearance(AppConfig cfg, LightingLook s)
    {
        lock (cfg.Sync)
        {
            var c = cfg.Rgb;
            return LightingLooks.EffectiveEffect(c) == s.Effect && (c.ColorA & 0xFFFFFF) == s.ColorA && (c.ColorB & 0xFFFFFF) == s.ColorB
                && Math.Abs(c.Brightness - s.Brightness) < 1e-9 && Math.Abs(c.Speed - (s.Speed ?? c.Speed)) < 1e-9
                && Math.Abs(c.Density - (s.Density ?? c.Density)) < 1e-9 && Math.Abs(c.Glow - (s.Glow ?? c.Glow)) < 1e-9 && c.Lightning == (s.Lightning ?? c.Lightning);
        }
    }
}
