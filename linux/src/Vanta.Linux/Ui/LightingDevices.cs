// PORTED from src\HwSuite\Ui\LightingDevices.cs (Windows 1.20.0): WPF -> Avalonia 11 (mechanical pass by linux/tools/wpf2avalonia.py, then hand fixes).
using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Ui;

/// <summary>One lighting device as the Lighting page shows it: names, facts, derived status and the Advanced details.</summary>
public sealed record LightingDeviceView(DeviceRole Role, string Title, string Subtitle, string Icon, DeviceFacts Facts, DeviceStatus Status, string Advanced, RgbSink? Sink);

/// <summary>
/// Builds <see cref="LightingDeviceView"/>s from the real sinks and controllers, or from a simulated fixture. Presence of
/// the mouse and keyboard is a read-only HID enumeration (cached for 2 s); nothing here writes to any device.
/// </summary>
public static class LightingDevices
{
    static readonly DeviceRole[] Order = { DeviceRole.Strip, DeviceRole.Ram, DeviceRole.Board, DeviceRole.Keyboard, DeviceRole.Mouse };
    static DateTime _hidChecked = DateTime.MinValue;
    static bool _mousePresent, _kbPresent;

    public static (string title, string sub, string icon) Names(DeviceRole r) => r switch
    {
        DeviceRole.Strip => ("Case fans and logo", "LANCOOL 215 · D_LED", "strip"),
        DeviceRole.Ram => ("Memory", "XPG Spectrix D60G", "dimm"),
        DeviceRole.Board => ("Board accents", "LED_C header · PCIe accent", "motherboard"),
        DeviceRole.Keyboard => ("Keyboard", "YUNZII AL80", "keyboard"),
        _ => ("Mouse", "Glorious Model O Wireless", "mouse")
    };

    public static List<LightingDeviceView> Build(AppServices svc)
    {
        var list = new List<LightingDeviceView>();
        if (svc.Fixture is UiFixture fx)
        {
            foreach (var r in Order)
            {
                if (!fx.Devices.TryGetValue(r, out var f)) continue;
                var (t, sub, ic) = Names(r);
                list.Add(new(r, t, sub, ic, f, LightingStatus.Evaluate(svc.Config, f), FixtureAdvanced(svc, fx, r), null));
            }
            return list;
        }
        RefreshHid();
        var cfg = svc.Config.Rgb;
        foreach (var r in Order)
        {
            RgbSink? sink = r switch
            {
                DeviceRole.Strip => svc.Rgb.Sinks.OfType<DLedSink>().FirstOrDefault(),
                DeviceRole.Board => svc.Rgb.Sinks.OfType<MotherboardSink>().FirstOrDefault(),
                DeviceRole.Ram => svc.Rgb.Sinks.OfType<RamSink>().FirstOrDefault() ?? (RgbSink?)svc.Rgb.Sinks.OfType<UnavailableSink>().FirstOrDefault(s => s.Name.Contains("RAM")),
                DeviceRole.Mouse => svc.Rgb.Sinks.OfType<MouseSink>().FirstOrDefault(),
                _ => (RgbSink?)svc.Rgb.Sinks.OfType<Al80Sink>().FirstOrDefault() ?? svc.Rgb.Sinks.OfType<KeyboardSink>().FirstOrDefault()
            };
            if (sink == null) continue;
            var (t, sub, ic) = Names(r);
            var facts = FactsFor(svc, r, sink);
            list.Add(new(r, t, sub, ic, facts, LightingStatus.Evaluate(svc.Config, facts), Advanced(svc, r, sink), sink));
        }
        return list;
    }

    static void RefreshHid()
    {
        if (DateTime.UtcNow - _hidChecked < TimeSpan.FromSeconds(2)) return;
        _hidChecked = DateTime.UtcNow;
        try { _mousePresent = GloriousWirelessDevice.Enumerate().Count > 0; } catch { _mousePresent = false; }
        try { _kbPresent = Hardware.Al80.Al80HidTransport.Find() != null; } catch { _kbPresent = false; }
    }

    public static bool KeyboardPresent(AppServices svc) => svc.Fixture?.Keyboard.Present ?? (RefreshHidAnd(() => _kbPresent));
    static bool RefreshHidAnd(Func<bool> f) { RefreshHid(); return f(); }

    static string? FaultText(string status) => status.StartsWith("faulted") ? status : null;

    static DeviceFacts FactsFor(AppServices svc, DeviceRole r, RgbSink s)
    {
        var c = svc.Config.Rgb;
        switch (s)
        {
            case MouseSink m:
                return new(r, m.Controller.Connected || _mousePresent, true, s.Blocked, c.Mouse, m.Controller.Failures, m.Controller.Applies, null) { Ack = m.Controller.Ack };
            case Al80Sink a:
            {
                var al = svc.Al80;
                bool on = svc.Config.Al80.Enabled;
                return new(r, (al?.Connected ?? false) || _kbPresent, on, s.Blocked, c.Keyboard, al?.LightFailures ?? 0, al?.LightWrites ?? 0, null)
                    { Ack = s.FrameAck, ColourStream = true, UnavailableReason = on ? null : "AL80 control is off. Turn on \"Control the AL80\" on the AL80 page." };
            }
            case KeyboardSink k:
            {
                var ctl = k.Controller;
                string? fault = ctl.ReopenGaveUp ? ctl.LinkStatus : ctl.Status.StartsWith("gave up") ? ctl.Status : null;
                // display only: the arbiter's decision (refreshed every 2 s); writes still ask ownership at the write itself
                string? owner = s.Blocked ?? (svc.Ownership?.Check(Hardware.HwResource.Keyboard) is { MayWrite: false } d ? d.Reason : null);
                return new(r, ctl.Connected || _kbPresent, true, owner, c.Keyboard, ctl.Failures + ctl.LiveFailures, ctl.Applies + ctl.LiveFrames + ctl.TestWrites, fault)
                    { Ack = k.Live ? k.FrameAck : ctl.StoredAck };
            }
            case RamSink ram:
                return new(r, true, ram.Available, s.Blocked, c.Ram, s.Errors, s.FramesSent + ram.SettingsApplied, c.RamOnboardEffects ? ram.OnboardProblem : FaultText(s.Status)) { UnavailableReason = s.Status, Ack = s.FrameAck };
            case UnavailableSink:
                return new(r, true, false, null, c.Ram, 0, 0, null) { UnavailableReason = s.Status };
            default:
                bool inc = r == DeviceRole.Strip ? c.DLedStrip : c.Motherboard;
                return new(r, true, s.Available, s.Blocked, inc, s.Errors, s.FramesSent, FaultText(s.Status)) { UnavailableReason = s.Status, Ack = s.FrameAck };
        }
    }

    static string Advanced(AppServices svc, DeviceRole r, RgbSink s)
    {
        var c = svc.Config.Rgb;
        var lines = new List<string>();
        switch (s)
        {
            case DLedSink d:
                lines.Add($"Interface   IT5701 USB HID, D_LED header ({d.LedCount} LEDs, {c.StripOrder.ToString().ToUpperInvariant()} byte order)");
                lines.Add($"Rate        up to {c.EffectiveDLedHz} updates/s; unchanged frames are not resent; no save command is ever sent");
                lines.Add("Layout      " + CaseSummary(c));
                lines.Add($"Status      {d.CurrentStatus()}");
                break;
            case MotherboardSink:
                lines.Add("Interface   IT5701 USB HID: LED_C header and PCI-E accent; never sends the save command (0x5E)");
                lines.Add($"Rate        up to {c.MotherboardMaxHz:0} updates/s");
                lines.Add($"Status      {s.Status}");
                break;
            case RamSink ram:
                lines.Add("Interface   ENE controllers on the SMBus (PawnIO i801), system-wide SMBus mutex; never sends the ENE save command (0xAA)");
                foreach (var dimm in svc.Ene?.Dimms.Where(x => x.Supported) ?? Enumerable.Empty<EneDimm>())
                    lines.Add($"Controller  0x{dimm.Address:X2}  {dimm.Version}  {dimm.LedCount} LEDs (which slot it sits in is not known)");
                lines.Add(c.RamOnboardEffects ? "Mode        onboard: effects run on the controllers; Static, Thermal and Stealth are still sent as colours" : $"Mode        streamed, up to {c.EffectiveRamHz} frames/s while animating");
                if (ram.OnboardDetail != null) lines.Add("Onboard     " + ram.OnboardDetail);
                break;
            case MouseSink m:
                lines.Add("Interface   USB HID (cable or receiver), the 3 reports Glorious CORE sends per setting; never streamed");
                lines.Add($"Link        {m.Controller.LinkStatus}");
                lines.Add($"Last sent   {(m.Controller.LastApplied is { } la ? la.Description : "nothing yet")}");
                lines.Add("Limit       no safe live-update path was found in the implementations inspected (Glorious CORE's handler for this model, OpenRGB)");
                break;
            case Al80Sink a when a.PerKey:
                lines.Add("Interface   USB HID FF60:0061, YUNZII SignalRGB firmware: 0x25 on, 0x24 colour packets (9 LEDs each), 0x26 off; nothing saved");
                lines.Add($"Rate        up to {Hardware.Al80.Al80Service.PerKeyMaxHz:0} frames/s, {Lighting.Devices.Al80KeyMap.Leds} LEDs at the firmware's key positions, changed packets only (whole frame every 200 ms: the firmware drops its SignalRGB flag after 500 ms of silence)");
                lines.Add($"Firmware    {svc.Al80?.PerKeyInfo}");
                lines.Add($"3-LED light {(svc.Config.Al80.SyncSideBar ? $"follows (volatile VIA channel 2, up to {Hardware.Al80.Al80Service.SideMaxHz:0} changes/s; {svc.Al80?.SideWrites ?? 0} updates)" : "keeps its own effect (switch off)")}");
                lines.Add($"Status      {svc.Al80?.LightStatus ?? "not running"}; {svc.Al80?.FramesSent ?? 0} frames, {svc.Al80?.FrameErrors ?? 0} errors");
                break;
            case Al80Sink a:
                lines.Add("Interface   USB HID FF60:0061 (VIA), volatile lighting writes only; Save is never sent by the sync");
                lines.Add($"Rate        up to {Hardware.Al80.Al80Service.LightMaxHz:0} colour updates/s; one colour for every key{(svc.Config.Al80.SyncSideBar ? " and the side bar" : "")}");
                lines.Add("Limit       " + (svc.Al80?.PerKeyInfo is { Length: > 0 } pk ? pk : "the stock firmware has no per-key live mode") + "; effects show as the keyboard's colour of each frame");
                lines.Add($"Status      {svc.Al80?.LightStatus ?? "not running"}");
                break;
            case KeyboardSink k:
                lines.Add($"Interface   USB HID FF00:0001, 65-byte feature reports; live frames {(k.Live ? "running" : "idle")}, about {KeyboardSink.LiveHz:0}/s target");
                lines.Add($"Sync        {k.Controller.Status}");
                lines.Add($"Link        {k.Controller.LinkStatus}");
                lines.Add($"Writes      {k.Controller.Applies} stored, {k.Controller.TestWrites} test, {k.Controller.Failures} failed; live frames {k.Controller.LiveFrames}, failed {k.Controller.LiveFailures}");
                lines.Add($"Ownership   {svc.KeyboardVendorWatch}");
                break;
            default:
                lines.Add($"Status      {s.Status}");
                break;
        }
        if (s.StatsText.Length > 0) lines.Add("Counters    " + s.StatsText);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The case layout in one line: which part gets which LEDs, and whether the split is set or assumed.</summary>
    static string CaseSummary(RgbConfig c)
    {
        var cl = c.CaseOrDefault;
        var parts = cl.Allocate(Math.Clamp(c.DLedCount, 1, 120));
        return (cl.CountsKnown ? "set on this card: " : "split assumed (set it on this card): ") + string.Join(", ", parts.Select(p => $"{CaseLayout.Name(p.Kind)} {p.Start + 1}-{p.Start + p.Count}"));
    }

    static string FixtureAdvanced(AppServices svc, UiFixture fx, DeviceRole r) => r switch
    {
        DeviceRole.Strip => $"Interface   IT5701 USB HID, D_LED header (27 LEDs, GRB)\nRate        up to {svc.Config.Rgb.EffectiveDLedHz} updates/s\nLayout      {CaseSummary(svc.Config.Rgb)}\n{UiFixture.Notice}",
        DeviceRole.Keyboard => $"Sync        {fx.Keyboard.Status}\nLink        {fx.Keyboard.LinkStatus}\nWrites      {fx.Keyboard.Applies} stored, {fx.Keyboard.TestWrites} test, {fx.Keyboard.Failures} failed; live frames {fx.Keyboard.LiveFrames}, failed {fx.Keyboard.LiveFailures}\n{UiFixture.Notice}",
        DeviceRole.Mouse => $"Link        {fx.MouseLinkStatus}\nLast sent   {fx.MouseLast}\nLimit       no safe live-update path was found in the implementations inspected (Glorious CORE, OpenRGB)\n{UiFixture.Notice}",
        DeviceRole.Ram => $"Controllers {fx.RamLeds.Length} ENE controllers, {string.Join("/", fx.RamLeds)} LEDs (slot positions not known)\n{UiFixture.Notice}",
        _ => UiFixture.Notice
    };
}
