using HwSuite.Core;

namespace HwSuite.Lighting;

public enum DeviceRole { Board, Strip, Ram, Mouse, Keyboard }

/// <summary>What a lighting device is doing, in the owner's terms.</summary>
public enum DeviceLightState
{
    /// <summary>Follows the shared clock (streamed frames or live frames), or shows the shared still colour.</summary>
    Shared,
    /// <summary>Runs its own onboard effect on its own timing (or a fixed fallback colour): not synchronised.</summary>
    Independent,
    /// <summary>Commanded off (Stealth / Lights out).</summary>
    Dark,
    /// <summary>Not included: VANTA sends nothing to it.</summary>
    Excluded,
    /// <summary>Lighting output is off: nothing is sent to any device.</summary>
    OutputOff,
    /// <summary>Not connected, or its backend is unavailable.</summary>
    Unavailable,
    /// <summary>Another program owns it (or ownership cannot be established): VANTA does not write.</summary>
    Blocked,
    /// <summary>Keyboard only: the owner has not confirmed a test write, so nothing is sent.</summary>
    NotVerified,
    /// <summary>Writes are failing (or the link gave up): enabled, but output is not established.</summary>
    Fault
}

/// <summary>
/// Whether a device's interface accepted the look VANTA asks for now (from existing write results; no extra traffic).
/// Accepted is API delivery, not an optical confirmation.
/// </summary>
public enum LightAck { Unknown, Pending, Accepted, Failed }

/// <summary>Runtime facts about one device, from its sink and controller (or from a simulated fixture).</summary>
public sealed record DeviceFacts(DeviceRole Role, bool Present, bool Available, string? Blocked, bool Included, long Errors, long Sent, string? Fault)
{
    /// <summary>Acknowledgement of the current request. Lifetime counters (Sent, Errors) never stand in for it.</summary>
    public LightAck Ack { get; init; } = LightAck.Unknown;
    /// <summary>Why it is unavailable (backend detail), shown when <see cref="Available"/> is false.</summary>
    public string? UnavailableReason { get; init; }
    /// <summary>Keyboard (YUNZII AL80): every frame's colour is streamed; no test write or live verification step exists.</summary>
    public bool ColourStream { get; init; }
}

public sealed record DeviceStatus(DeviceLightState State, string Headline, string Detail, bool Confirmed)
{
    /// <summary>The state is a problem the owner may want to act on (amber/red in the UI).</summary>
    public bool NeedsAttention => State is DeviceLightState.Fault or DeviceLightState.Blocked or DeviceLightState.Unavailable or DeviceLightState.NotVerified;
}

/// <summary>
/// Derives each device's plain-language state from the configuration, ownership and what the device actually accepted.
/// "Enabled" alone is never reported as working: a device with only failed writes is a fault, and one that has not yet
/// accepted an update says so. The mouse never claims synchronisation for animated effects: no safe live-update path was
/// found for it in the implementations inspected (Glorious CORE's handler for this model, OpenRGB).
/// </summary>
public static class LightingStatus
{
    public static DeviceStatus Evaluate(AppConfig app, DeviceFacts f)
    {
        var c = app.Rgb;
        if (!f.Present) return new(DeviceLightState.Unavailable, "Not connected", NotConnectedDetail(f.Role), false);
        if (!f.Available) return new(DeviceLightState.Unavailable, "Unavailable", f.UnavailableReason ?? "Its control interface could not be opened.", false);
        if (f.Blocked != null) return new(DeviceLightState.Blocked, "Paused by another program", Sentence(f.Blocked) + " VANTA does not write to it until that changes.", false);
        if (!f.Included) return new(DeviceLightState.Excluded, "Excluded", "Not included in VANTA lighting: nothing is sent to it, and it keeps its own setting.", false);
        if (f.Role == DeviceRole.Keyboard && !f.ColourStream && !app.KeyboardVerified)
            return new(DeviceLightState.NotVerified, "Not verified", "Nothing is sent until you confirm one test write below.", false);
        if (!c.Enabled) return new(DeviceLightState.OutputOff, "Output off", "Lighting output is off: nothing is sent. The device keeps whatever it showed last.", false);
        // confirmed only by an acceptance of the CURRENT request; lifetime totals say nothing about the look asked for now
        bool confirmed = f.Ack == LightAck.Accepted;
        if (f.Fault != null)
            return new(DeviceLightState.Fault, "Not responding", Sentence(f.Fault) + " Output to it is not established.", false);
        if (f.Ack == LightAck.Failed)
            return new(DeviceLightState.Fault, "Update not accepted", "The last update for the current look failed; VANTA retries within its usual limits. Earlier successful updates do not count for the current look.", false);
        if (f.Ack == LightAck.Unknown && f.Errors > 0 && f.Sent == 0)
            return new(DeviceLightState.Fault, "Not responding", $"{f.Errors} write(s) failed and none was accepted. Output to it is not established.", false);
        if (c.Effect == EffectKind.Stealth)
            return new(DeviceLightState.Dark, LightsOut.IsOn(c) ? "Lights out" : "Off (Stealth)", "Commanded off.", confirmed);
        if (f.Role == DeviceRole.Keyboard && f.ColourStream)
            return new(DeviceLightState.Shared, c.Effect == EffectKind.Static ? "Shared colour (live)" : "Following shared animation (live)",
                "VANTA sends the keyboard its colour of each frame, up to 30 per second, in step with the other devices. All keys and the side bar show one colour: the stock firmware has no per-key live mode. Nothing is saved on the keyboard.", confirmed);

        bool animated = c.Effect is EffectKind.Breathing or EffectKind.GradientWave;
        bool scene = EffectInfo.IsScene(c.Effect);
        string name = EffectInfo.Label(c.Effect);
        if (scene)
        {
            var cc = Companion.For(c);
            switch (f.Role)
            {
                case DeviceRole.Ram when c.RamOnboardEffects:
                    return new(DeviceLightState.Independent, cc.Breathing ? "Companion look · own timing" : "Companion colour",
                        $"{name} needs streamed memory lighting. Memory is set to Onboard, which VANTA keeps: the sticks show a calm companion ({cc.Text}) " +
                        (cc.Breathing ? "breathing on their own controllers, set once per change." : "that is sent once and not streamed.") +
                        " Choose Streamed under Memory to show the effect itself (it costs CPU while it animates).", confirmed);
                case DeviceRole.Ram:
                    return new(DeviceLightState.Shared, "Following shared animation",
                        $"Streamed over the SMBus, up to {c.EffectiveRamHz} updates per second. Each stick is its own short LED run; droplets and glints run along it in LED order.", confirmed);
                case DeviceRole.Mouse when c.MouseLive:
                    return new(DeviceLightState.Shared, "Following shared animation (live)",
                        "Live: VANTA sends the mouse its colour of each frame, up to 30 per second, like the other devices (the way SignalRGB drives this mouse). Nothing is stored on the mouse.", confirmed);
                case DeviceRole.Mouse:
                    return new(DeviceLightState.Independent, "Companion effect (independent)",
                        $"The mouse cannot receive {name}: no safe live-update path was found in the implementations inspected (Glorious CORE, OpenRGB). It shows a calm companion instead ({cc.Text}), stored once per change and never streamed; it is not synchronised with the other devices.", confirmed);
                case DeviceRole.Keyboard when app.KeyboardLiveVerified || app.KeyboardLiveTrial:
                    return new(DeviceLightState.Shared, "Following shared animation",
                        (c.Effect is EffectKind.Rainfall or EffectKind.DistantStorm or EffectKind.Drip or EffectKind.Downpour ? "Live frames: rain falls down the key rows by their verified positions" : c.Effect is EffectKind.MoonlitWater or EffectKind.RainRipples ? "Live frames: ripples spread across the verified key positions" : "Live frames follow the shared clock key by key") +
                        $" (about 12 per second). Its stored fallback is the companion look ({cc.Text}).", confirmed);
                case DeviceRole.Keyboard:
                    return new(DeviceLightState.Independent, "Companion look", $"Without verified live frames the keyboard cannot show {name}; it stores a calm companion ({cc.Text}) once per change.", confirmed);
                case DeviceRole.Strip:
                    return new(DeviceLightState.Shared, "Following shared animation", $"Streamed over USB, up to {c.EffectiveDLedHz} updates per second. " + (c.CaseOrDefault.Mirrored ? "The case hub copies one signal to every fan, so all fans show the same ring: rain and drips run down its rim, ripples spread around it." : "The fans and logo are one LED chain laid out as the case: rain falls down the front fans, drips gather at the top, ripples spread from fan to fan."), confirmed);
                default:
                    return new(DeviceLightState.Shared, "Following shared animation", "Streamed over USB, up to 20 updates per second. Two zones only, so this shows soft glints rather than the full effect.", confirmed);
            }
        }
        switch (f.Role)
        {
            case DeviceRole.Ram when c.RamOnboardEffects && animated:
                return new(DeviceLightState.Independent, "Onboard effect · own timing",
                    (c.Effect == EffectKind.GradientWave ? "Runs the modules' own rainbow wave (they have no two-colour wave)" : "Breathes on the modules' own controllers") +
                    ": set once per change, then nothing is streamed over the SMBus. Not synchronised with the other devices.", confirmed);
            case DeviceRole.Ram:
                return new(DeviceLightState.Shared, Shared(c), $"Streamed over the SMBus, up to {c.EffectiveRamHz} updates per second while animating.", confirmed);
            case DeviceRole.Mouse when c.Effect == EffectKind.Static:
                return new(DeviceLightState.Shared, "Shared colour", "The colour is stored on the mouse once per change; it is never streamed.", confirmed);
            case DeviceRole.Mouse when c.MouseLive:
                return new(DeviceLightState.Shared, c.Effect == EffectKind.Static ? "Shared colour (live)" : "Following shared animation (live)",
                    "Live: VANTA sends the mouse its colour of each frame, up to 30 per second, like the other devices. Nothing is stored on the mouse.", confirmed);
            case DeviceRole.Mouse when MouseSync.Active(c):
                return new(DeviceLightState.Shared, "Breathing locked to the shared clock",
                    $"The mouse breathes on its own timer, which runs in {MouseSync.StepSeconds} s steps. VANTA breathes every {MouseSync.PeriodSeconds(MouseSync.SpeedByte(c.Speed)):0.000} s to match it, " +
                    $"times the write so both are darkest together (mouse timing {c.MouseSyncLatencyMs:+0;-0} ms against the case fans, measured) and lines it up again every 5 minutes.", confirmed);
            case DeviceRole.Mouse when c.Effect == EffectKind.Breathing:
                return new(DeviceLightState.Independent, "Independent onboard effect",
                    "Breathes on the mouse's own timing, so it is not synchronised with the other devices. No safe live-update path was found in the implementations inspected (Glorious CORE, OpenRGB).", confirmed);
            case DeviceRole.Mouse:
                return new(DeviceLightState.Independent, "Fixed colour (effect not supported)",
                    "The mouse cannot show this effect; it shows the primary colour and is not synchronised with the other devices.", confirmed);
            case DeviceRole.Keyboard when app.KeyboardLiveVerified || app.KeyboardLiveTrial:
                return new(DeviceLightState.Shared, Shared(c), "Live frames follow the shared clock key by key (about 12 per second); the effect's onboard look is stored as its fallback.", confirmed);
            case DeviceRole.Keyboard when c.Effect == EffectKind.Static:
                return new(DeviceLightState.Shared, "Shared colour", "The colour is stored on the keyboard once per change.", confirmed);
            case DeviceRole.Keyboard:
                return new(DeviceLightState.Independent, "Onboard effect · own timing", "Runs the keyboard's own effect, set once per change; not synchronised with the other devices.", confirmed);
            default:
                return new(DeviceLightState.Shared, Shared(c), f.Role == DeviceRole.Strip ? $"Streamed over USB, up to {c.EffectiveDLedHz} updates per second while animating." : "Streamed over USB, up to 20 updates per second while animating.", confirmed);
        }
    }

    static string Shared(RgbConfig c) => c.Effect == EffectKind.Static ? "Shared colour" : "Following shared animation";

    static string NotConnectedDetail(DeviceRole r) => r switch
    {
        DeviceRole.Mouse => "The mouse (cable or receiver) was not found.",
        DeviceRole.Keyboard => "The keyboard was not found. Connect it by cable or receiver.",
        DeviceRole.Ram => "No RAM lighting controllers were found on the SMBus.",
        _ => "The motherboard lighting controller was not found."
    };

    static string Sentence(string s) { s = s.Trim(); if (s.Length == 0) return s; s = char.ToUpperInvariant(s[0]) + s[1..]; return s.EndsWith('.') ? s : s + "."; }
}
