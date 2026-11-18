using System.Diagnostics;
using System.Text;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>
/// Phase 4 autonomous hardware discovery and self-verification: sensors, PWM pulse test with tachometer deltas,
/// USB HID / SMBus RGB enumeration and a safe colour test. Everything is restored afterwards.
/// </summary>
public sealed class Diagnostics
{
    readonly StringBuilder _txt = new();
    readonly Dictionary<string, object?> _json = new();
    readonly bool _doPulse, _doRgbWrites;

    public Diagnostics(bool pulse = true, bool rgbWrites = true) { _doPulse = pulse; _doRgbWrites = rgbWrites; }

    void Out(string s) { _txt.AppendLine(s); Log.Info(s); }
    void Section(string name) { Out(""); Out("==== " + name + " ===="); }

    public int Run()
    {
        var started = DateTime.Now;
        Out($"HwSuite diagnostics {started:yyyy-MM-dd HH:mm:ss}");
        using var app = new AppServices(new AppConfig());
        try
        {
            SystemSection(app);
            app.InitializeHardware();
            app.InitializeLighting(allowRamRemap: true);
            SensorSection(app);
            HidSection(app);
            SmbusSection(app);
            if (_doPulse) PulseSection(app);
            if (_doRgbWrites) RgbWriteSection(app);
        }
        catch (Exception ex) { Out("DIAGNOSTICS ABORTED: " + ex); _json["fatal"] = ex.ToString(); }
        finally
        {
            try { app.Hub.Engine.ReleaseAll(); } catch { }
        }

        _json["startedAt"] = started;
        _json["finishedAt"] = DateTime.Now;
        var stamp = started.ToString("yyyyMMdd-HHmmss");
        var jsonPath = Path.Combine(AppPaths.Diagnostics, $"diagnostics-{stamp}.json");
        var txtPath = Path.Combine(AppPaths.Diagnostics, $"diagnostics-{stamp}.txt");
        File.WriteAllText(jsonPath, ConfigStore.ToJson(_json));
        File.WriteAllText(txtPath, _txt.ToString());
        File.Copy(jsonPath, Path.Combine(AppPaths.Diagnostics, "latest.json"), true);
        File.Copy(txtPath, Path.Combine(AppPaths.Diagnostics, "latest.txt"), true);
        Out("");
        Out("Report written: " + txtPath);
        return _json.ContainsKey("fatal") ? 2 : 0;
    }

    // ---------------------------------------------------------------------------------------------------------

    void SystemSection(AppServices app)
    {
        Section("System");
        var info = new Dictionary<string, string>
        {
            ["os"] = Environment.OSVersion.VersionString,
            ["machine"] = Environment.MachineName,
            ["elevated"] = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator).ToString(),
            ["pawnioInstalled"] = PawnIoNative.IsInstalled().ToString(),
            ["pawnioVersion"] = PawnModule.PawnIoVersionString() ?? "n/a",
            ["gigabyteToolsRunning"] = GigabyteServices.AnyRunning().ToString(),
            ["afterburnerRunning"] = (Process.GetProcessesByName("MSIAfterburner").Length > 0).ToString(),
        };
        foreach (var kv in info) Out($"{kv.Key,-22} {kv.Value}");
        _json["system"] = info;
        if (info["afterburnerRunning"] == "True")
            Out("note: MSI Afterburner is running and may also write GPU fan speeds");
    }

    void SensorSection(AppServices app)
    {
        Section("Sensors");
        foreach (var n in app.Hub.Notes) Out(n);
        var lines = app.Hub.Lhm.Describe();
        foreach (var l in lines) Out(l);
        var census = app.Hub.Lhm.Sensors.GroupBy(x => $"{x.HardwareType}/{x.Type}").Select(g => $"{g.Key}={g.Count()}");
        Out("LHM sensor census: " + string.Join(", ", census));
        var snap = app.Hub.Poll();
        Out($"snapshot: {snap.Fans.Count} fan readings, {snap.Temps.Count} board temperature readings");
        Out($"CPU temp: {Fmt(snap.CpuTempC)} C ({snap.CpuTempSource})  load {Fmt(snap.CpuLoadPct)} %  clock {Fmt(snap.CpuClockMHz)} MHz  power {Fmt(snap.CpuPowerW)} W");
        Out($"GPU {snap.GpuName}: temp {Fmt(snap.GpuTempC)} C  load {Fmt(snap.GpuLoadPct)} %  core {Fmt(snap.GpuCoreMHz)} MHz  mem {Fmt(snap.GpuMemMHz)} MHz  power {Fmt(snap.GpuPowerW)} W");
        Out($"RAM used {Fmt(snap.RamUsedGb)} / {Fmt(snap.RamTotalGb)} GB");
        foreach (var f in snap.Fans) Out($"fan   {f.Source,-22} {f.Name,-16} {Fmt(f.Rpm),7} RPM  {Fmt(f.Percent),5} %");
        foreach (var t in snap.Temps) Out($"temp  {t.Source,-22} {t.Name,-24} {Fmt(t.Celsius)} C");
        Out($"controllable channels: {app.Hub.Engine.Channels.Count(c => c.CanControl)} of {app.Hub.Engine.Channels.Count}");
        foreach (var c in app.Hub.Engine.Channels) Out($"  channel {c.Id,-40} {c.Name} [{c.Backend}] min {c.MinPercent}%");
        _json["sensorNotes"] = app.Hub.Notes;
        _json["lhmTree"] = lines;
        _json["snapshot"] = snap;
    }

    void HidSection(AppServices app)
    {
        Section("USB HID enumeration");
        var results = new Dictionary<string, object?>();

        var fusion = Fusion2UsbDevice.Enumerate();
        foreach (var h in fusion) Out("IT5701/5702: " + h);
        var mb = app.Rgb.Sinks.OfType<MotherboardSink>().FirstOrDefault();
        if (mb?.Device is { } d)
        {
            Out($"  controller answered: product '{d.ProductString}', fw raw 0x{d.FirmwareRaw:X8}, chip id 0x{d.ChipId:X8}, support flags 0x{d.SupportFlags:X2}, device num {d.DeviceNum}");
            var calName = Fusion2UsbDevice.DecodeCalibration(d.StripCalibrationWord);
            Out($"  D_LED1: stored calibration word 0x{d.StripCalibrationWord:X8} = {(calName ?? "not a valid channel permutation")}; reported LED capacity class {d.ReportedStripCapacityEnum} (0=32, 1=64, 2=256, 3=512, 4=1024)");
            results["motherboard"] = new { product = d.ProductString, firmware = $"0x{d.FirmwareRaw:X8}", chipId = $"0x{d.ChipId:X8}", stripCalibration = $"0x{d.StripCalibrationWord:X8}", stripCalibrationName = calName, stripCapacityClass = d.ReportedStripCapacityEnum, interfaces = fusion.Select(x => x.ToString()).ToList() };
        }
        else { Out("  motherboard controller: " + (mb?.Status ?? "no sink")); results["motherboard"] = new { status = mb?.Status }; }

        var gl = GloriousWirelessDevice.Enumerate();
        foreach (var h in gl) Out("Glorious: " + h);
        var mouse = app.Rgb.Sinks.OfType<MouseSink>().FirstOrDefault();
        Out("  mouse sink: " + (mouse?.Status ?? "none"));
        results["mouse"] = new { interfaces = gl.Select(x => x.ToString()).ToList(), status = mouse?.Status };

        var kb = KeyboardProbe.Enumerate();
        foreach (var h in kb) Out("Keyboard 258A:013C: " + h);
        Out("  keyboard: read-only (no write path by design)");
        results["keyboard"] = new { interfaces = kb.Select(x => x.ToString()).ToList(), writePath = "disabled by design" };

        // every other HID device from the same VID, so the user can verify the PID mapping
        var all258a = HidEnumerator.Find(0x258A).Select(h => h.ToString()).Distinct().ToList();
        results["allSinowealthInterfaces"] = all258a;
        _json["hid"] = results;
    }

    void SmbusSection(AppServices app)
    {
        Section("SMBus / ENE DRAM");
        var res = new Dictionary<string, object?>();
        if (app.Smbus == null)
        {
            var s = app.Rgb.Sinks.FirstOrDefault(x => x.Kind.StartsWith("SMBus"));
            Out("SMBus backend not available: " + s?.Status);
            res["status"] = s?.Status;
        }
        else
        {
            Out($"controller: {app.Smbus.Identity} PCI {app.Smbus.PciVendor:X4}:{app.Smbus.PciDevice:X4}  SPD write-protected: {app.Smbus.SpdWriteProtected?.ToString() ?? "unknown"}");
            res["identity"] = app.Smbus.Identity;
            res["pci"] = $"{app.Smbus.PciVendor:X4}:{app.Smbus.PciDevice:X4}";
            res["spdWriteProtected"] = app.Smbus.SpdWriteProtected;
            if (app.Ene != null)
            {
                foreach (var j in app.Ene.Journal) Out("  " + j);
                Out($"ENE controllers found: {app.Ene.Dimms.Count}");
                foreach (var dimm in app.Ene.Dimms) Out("  " + dimm);
                res["dimms"] = app.Ene.Dimms.Select(x => new { address = $"0x{x.Address:X2}", version = x.Version, leds = x.LedCount, supported = x.Supported }).ToList();
                res["journal"] = app.Ene.Journal;
            }
            Out($"SMBus transactions so far: {app.Smbus.TransactionCount}, errors (NACK/timeouts): {app.Smbus.ErrorCount}");
            Out("error codes seen: " + (app.Smbus.ErrorCodes.Count == 0 ? "none" : string.Join(", ", app.Smbus.ErrorCodes.Select(kv => $"0x{kv.Key:X8} x{kv.Value}"))));
            BusMap(app, res);
        }
        _json["smbus"] = res;
    }

    /// <summary>Read-only receive-byte scan of the whole SMBus (like i2cdetect). Proves the transport works and shows what is on the bus.</summary>
    void BusMap(AppServices app, Dictionary<string, object?> res)
    {
        var bus = app.Smbus!;
        var acks = new List<string>();
        var codes = new Dictionary<int, int>();
        using (bus.Lock())
        {
            for (byte a = 0x08; a <= 0x77; a++)
            {
                if (bus.ReadByte(a, out var v)) acks.Add($"0x{a:X2}"); else codes[bus.LastHr] = codes.GetValueOrDefault(bus.LastHr) + 1;
            }
        }
        Out("bus scan 0x08-0x77 (receive byte): ACK at " + (acks.Count == 0 ? "nothing" : string.Join(" ", acks)));
        Out("  non-ACK result codes: " + string.Join(", ", codes.Select(kv => $"0x{kv.Key:X8} x{kv.Value}")));
        res["busAck"] = acks; res["busNonAckCodes"] = codes.ToDictionary(kv => $"0x{kv.Key:X8}", kv => kv.Value);
        // SPD EEPROM sanity read: byte 2 of page 0 is the DRAM device type (0x0C = DDR4). Read-only.
        var spd = new List<string>();
        using (bus.Lock())
        {
            for (byte a = 0x50; a <= 0x53; a++)
                spd.Add(bus.ReadByteData(a, 0x02, out var t) ? $"0x{a:X2}: type 0x{t:X2}{(t == 0x0C ? " (DDR4)" : "")}" : $"0x{a:X2}: no answer (0x{bus.LastHr:X8})");
        }
        Out("SPD device-type reads: " + string.Join("; ", spd));
        res["spdReads"] = spd;
    }

    // ---------------------------------------------------------------------------------------------------------

    static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0.#") : "n/a";

    static Dictionary<string, double?> RpmMap(TelemetrySnapshot s) =>
        s.Fans.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().Rpm);

    TelemetrySnapshot Settle(AppServices app, int seconds)
    {
        TelemetrySnapshot snap = app.Hub.Poll();
        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            snap = app.Hub.Poll();
            if ((snap.CpuTempC ?? 0) >= 85 || (snap.GpuTempC ?? 0) >= 78)
            {
                Out($"SAFETY ABORT: temperature too high (cpu {Fmt(snap.CpuTempC)}, gpu {Fmt(snap.GpuTempC)})");
                throw new InvalidOperationException("thermal abort");
            }
        }
        return snap;
    }

    void PulseSection(AppServices app)
    {
        Section("PWM pulse test (35% -> 75% -> restore, tachometer deltas)");
        var results = new List<object>();
        var channels = app.Hub.Engine.Channels.Where(c => c.CanControl).ToList();
        if (channels.Count == 0) Out("no controllable channels");

        foreach (var ch in channels)
        {
            var baseline = app.Hub.Poll();
            if ((baseline.CpuTempC ?? 0) >= 70 || (baseline.GpuTempC ?? 0) >= 70)
            {
                Out($"skip {ch.Name}: system too warm for a pulse test");
                continue;
            }
            var before = RpmMap(baseline);
            Out($"-- {ch.Name} ({ch.Id}) [{ch.Backend}] current duty {Fmt(ch.ReadPercent())}%");
            Dictionary<string, double?> low, high, after;
            bool okLow, okHigh;
            try
            {
                // GPU fans settle in about 4 s; the IT8689E smooths PWM changes at roughly 100 RPM/s, so give it 8 s.
                int hold = ch.Backend == "NVML" ? 4 : 8;
                okLow = ch.SetPercent(Math.Max(35, ch.MinPercent));
                low = RpmMap(Settle(app, hold));
                okHigh = ch.SetPercent(75);
                high = RpmMap(Settle(app, hold));
            }
            finally
            {
                ch.RestoreOriginal();
            }
            after = RpmMap(Settle(app, 3));

            var responders = new List<string>();
            var matrix = new Dictionary<string, object>();
            foreach (var id in before.Keys)
            {
                double? l = low.GetValueOrDefault(id), h = high.GetValueOrDefault(id), b = before[id], a = after.GetValueOrDefault(id);
                double delta = (h ?? 0) - (l ?? 0);
                double deviation = Math.Max(Math.Abs((l ?? 0) - (b ?? 0)), Math.Abs((h ?? 0) - (b ?? 0)));
                bool responds = Math.Abs(delta) >= Math.Max(120, 0.10 * (b ?? 0)) || (b > 0 && deviation >= Math.Max(150, 0.12 * (b ?? 0)));
                var name = app.Hub.Poll().Fans.FirstOrDefault(f => f.Id == id)?.Name ?? id;
                matrix[id] = new { name, baseline = b, at35 = l, at75 = h, after = a, delta, responds };
                Out($"   {name,-18} base {Fmt(b),6}  @35% {Fmt(l),6}  @75% {Fmt(h),6}  delta {delta,6:0}  after {Fmt(a),6}  {(responds ? "<-- RESPONDS" : "")}");
                if (responds) responders.Add(name);
            }
            Out($"   writes accepted: 35%={okLow} 75%={okHigh}; responding tachometers: {(responders.Count == 0 ? "none" : string.Join(", ", responders))}");
            results.Add(new { channel = ch.Id, name = ch.Name, backend = ch.Backend, writes = new { okLow, okHigh }, responders, matrix });
            Thread.Sleep(2000);
        }
        _json["pulseTest"] = results;
    }

    // ---------------------------------------------------------------------------------------------------------

    void RgbWriteSection(AppServices app)
    {
        Section("RGB safe-colour test (dim green, then restore to the user's red scheme)");
        var res = new Dictionary<string, object?>();
        var testColor = new Rgb(0, 48, 0);
        var home = new Rgb(0xFF, 0, 0);

        var mb = app.Rgb.Sinks.OfType<MotherboardSink>().FirstOrDefault();
        if (mb?.Device is { } dev)
        {
            long errBefore = dev.WriteErrors;
            dev.InitializeLighting();
            bool ok = dev.SetLed(Fusion2UsbDevice.LedC, testColor) & dev.SetLed(Fusion2UsbDevice.PcieAccent, testColor) & dev.Apply();
            Thread.Sleep(1500);
            bool back = dev.SetLed(Fusion2UsbDevice.LedC, home) & dev.SetLed(Fusion2UsbDevice.PcieAccent, home) & dev.Apply();
            Out($"motherboard: test write ok={ok}, restore ok={back}, HID write errors={dev.WriteErrors - errBefore}");

            // D_LED addressable strip (case fans): dim green for 1.5 s, then the red scheme, in the configured channel order
            var order = new AppConfig().Rgb.StripOrder;
            int count = new AppConfig().Rgb.DLedCount;
            long stripErr0 = dev.WriteErrors;
            dev.PrepareStrip(count);
            bool s1 = dev.SetStripColors(Enumerable.Repeat(testColor, count).ToArray(), order);
            Thread.Sleep(1500);
            bool s2 = dev.SetStripColors(Enumerable.Repeat(home, count).ToArray(), order);
            Out($"D_LED strip ({count} LEDs, {order} order): test write ok={s1}, restore ok={s2}, HID write errors={dev.WriteErrors - stripErr0}");
            res["motherboard"] = new { testOk = ok, restoreOk = back, errors = dev.WriteErrors - errBefore, strip = new { leds = count, order = order.ToString(), testOk = s1, restoreOk = s2, errors = dev.WriteErrors - stripErr0 } };
        }
        else { Out("motherboard: skipped (" + mb?.Status + ")"); res["motherboard"] = new { skipped = mb?.Status }; }

        // The mouse stores lighting in its onboard profile, so diagnostics no longer writes it; --mouse-test is the bounded mouse check.
        var mouse = app.Rgb.Sinks.OfType<MouseSink>().FirstOrDefault();
        Out("mouse: no lighting write in --diagnose (use --mouse-test); link " + (mouse?.Controller.LinkStatus ?? "n/a"));
        res["mouse"] = new { skipped = "no writes in --diagnose", link = mouse?.Controller.LinkStatus };

        var ram = app.Rgb.Sinks.OfType<RamSink>().FirstOrDefault();
        if (ram != null && app.Smbus != null && app.Ene != null && app.Ene.Dimms.Any(d => d.Supported))
        {
            long err0 = app.Smbus.ErrorCount, tx0 = app.Smbus.TransactionCount;
            var perDimm = new List<object>();
            foreach (var d in app.Ene.Dimms.Where(x => x.Supported))
            {
                app.Ene.EnterDirect(d);
                var frame = Enumerable.Repeat(testColor, d.LedCount).ToArray();
                int sent = app.Ene.WriteFrame(d, frame);
                perDimm.Add(new { address = $"0x{d.Address:X2}", d.Version, leds = d.LedCount, ledsWritten = sent });
                Out($"RAM {d}: wrote {sent}/{d.LedCount} LEDs");
            }
            Thread.Sleep(1500);
            foreach (var d in app.Ene.Dimms.Where(x => x.Supported)) app.Ene.SetStaticLook(d, home);
            long errs = app.Smbus.ErrorCount - err0, tx = app.Smbus.TransactionCount - tx0;
            Out($"RAM: {tx} SMBus transactions, {errs} errors");
            res["ram"] = new { dimms = perDimm, transactions = tx, errors = errs };
        }
        else { Out("RAM: skipped (" + ram?.Status + ")"); res["ram"] = new { skipped = ram?.Status ?? "no RAM sink" }; }

        _json["rgbTest"] = res;
    }
}

