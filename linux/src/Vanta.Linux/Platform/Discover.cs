using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Hardware.Al80;
using HwSuite.Lighting.Devices;

namespace HwSuite.Platform;

/// <summary>
/// `vanta --discover`: what the Linux port can see on this PC. READ-ONLY: it lists sysfs, loaded modules, NVML readings and
/// USB HID descriptors, and names the i2c adapter. It never writes a fan, never talks on the SMBus (no ENE probe: that is a
/// separate step with the owner present), and never sends a HID report. Safe to run while anything else is running.
/// </summary>
public static class Discover
{
    static int _warn;
    static void Line(string s = "") => Console.WriteLine(s);
    static void Ok(string s) => Console.WriteLine("  [ok]   " + s);
    static void Warn(string s) { _warn++; Console.WriteLine("  [WARN] " + s); }
    static void Info(string s) => Console.WriteLine("  [info] " + s);

    public static int Run()
    {
        NativeResolver.Install();
        Line($"VANTA for Linux discovery — {DateTime.Now:yyyy-MM-dd HH:mm}  (read-only: nothing is written to any device)");
        Line($"kernel {Sysfs.ReadText("/proc/sys/kernel/osrelease")}, running as {(LibC.IsRoot ? "root" : "a normal user (some readings need root)")}");
        var board = $"{Sysfs.ReadText("/sys/class/dmi/id/board_vendor")} {Sysfs.ReadText("/sys/class/dmi/id/board_name")} BIOS {Sysfs.ReadText("/sys/class/dmi/id/bios_version")}";
        Line("board: " + board);
        var cmdline = Sysfs.ReadText("/proc/cmdline") ?? "";

        Line(); Line("Kernel modules");
        var mods = new HashSet<string>(File.Exists("/proc/modules") ? File.ReadLines("/proc/modules").Select(l => l.Split(' ')[0]) : Array.Empty<string>());
        foreach (var (m, why) in new[] { ("it87", "motherboard fans + temps (IT8689E)"), ("coretemp", "CPU temperature"), ("i2c_i801", "SMBus (RAM lighting)"), ("i2c_dev", "/dev/i2c-* access"), ("nvidia", "NVIDIA driver (GPU temp + fans)") })
            if (mods.Contains(m)) Ok($"{m} loaded — {why}"); else Warn($"{m} NOT loaded — {why}");
        if (cmdline.Contains("acpi_enforce_resources=lax")) Info("kernel option acpi_enforce_resources=lax is set");

        Line(); Line("hwmon sensors");
        var hw = new HwmonService();
        if (hw.Open())
        {
            foreach (var l in hw.Describe()) Line("  " + l);
            var sio = hw.Chips.FirstOrDefault(c => c.IsSuperIo);
            if (sio == null) Warn("no Super I/O chip: install the it87 driver for the IT8689E (see PORTING_STATUS.md)");
            else
            {
                Ok($"Super I/O {sio.DisplayName}: {hw.Sensors.Count(s => s.Chip == sio && s.Kind == HwmonKind.Pwm)} PWM output(s), {hw.Sensors.Count(s => s.Chip == sio && s.Kind == HwmonKind.Fan)} tach input(s)");
                foreach (var p in hw.Sensors.Where(s => s.Chip == sio && s.Kind == HwmonKind.Pwm))
                {
                    var en = Sysfs.ReadText(p.Path + "_enable");
                    var fan = hw.Sensors.FirstOrDefault(f => f.Chip == sio && f.Kind == HwmonKind.Fan && f.Number == p.Number);
                    Info($"pwm{p.Number}: duty {p.Value:0}%, mode {en switch { "0" => "0 (full speed)", "1" => "1 (manual)", null => "?", _ => en + " (automatic)" }}, fan{p.Number} {(fan?.Value is double r ? r.ToString("0") + " rpm" : "-")}  -> VANTA id lhm:{p.Id}");
                }
            }
        }
        else Warn("hwmon: " + hw.Error);
        Info("motherboard fan control: " + (LinuxFanGate.MotherboardAllowed(out var gate) ? "verified on Linux" : gate));

        Line(); Line("GPU (NVML)");
        try
        {
            var gpu = NvmlGpu.TryOpen(out var err);
            if (gpu == null) Warn("NVML: " + err);
            else
            {
                using (gpu)
                {
                    var g = gpu.Read();
                    Ok($"{gpu.Name}: {g.TempC:0} °C, load {g.LoadPct:0}%, {gpu.FanCount} fan(s), manual range {gpu.MinFanPercent}-{gpu.MaxFanPercent}%");
                    foreach (var f in g.Fans) Info($"fan {f.Index}: {f.SpeedPct}% (target {f.TargetPct}%), policy {(f.Policy == 0 ? "auto" : "manual")}");
                }
            }
        }
        catch (Exception ex) { Warn("NVML: " + ex.Message); }

        Line(); Line("SMBus (RAM lighting) — adapter only, no transactions");
        var a = SmbusI801.FindAdapter();
        if (a == null) Warn("no 'SMBus I801' adapter visible (needs i2c-i801 + i2c-dev)");
        else
        {
            Ok($"{a.Value.name} at {a.Value.dev}");
            try
            {
                using var f = new FileStream(a.Value.dev, FileMode.Open, FileAccess.ReadWrite);
                Ok($"{a.Value.dev} can be opened by this user");
            }
            catch (Exception ex) { Info($"{a.Value.dev} cannot be opened by this user ({ex.GetType().Name}); the root service can"); }
            var bound = Directory.Exists("/sys/bus/i2c/devices") ? Directory.GetDirectories("/sys/bus/i2c/devices").Select(Path.GetFileName).Where(n => n!.StartsWith(Path.GetFileName(a.Value.dev).Replace("i2c-", "") + "-")).ToList() : new();
            if (bound.Count > 0) Info("kernel drivers bound on this bus (VANTA never touches these addresses): " + string.Join(", ", bound.Select(b => b + "=" + (Sysfs.ReadText($"/sys/bus/i2c/devices/{b}/name") ?? "?"))));
        }

        Line(); Line("Memory modules (SMBIOS)");
        var mem = MemoryTopology.Modules;
        if (mem.Count == 0) Warn(MemoryTopology.Detail(mem));
        else foreach (var l in MemoryTopology.Detail(mem).Split(Environment.NewLine)) Info(l);

        Line(); Line("USB HID devices VANTA drives");
        try
        {
            var all = HidSharp.DeviceList.Local.GetHidDevices().ToList();
            Info($"{all.Count} hidraw interface(s) in total");
            Report("Gigabyte IT5701 RGB (048D)", HidEnumerator.Find(Fusion2UsbDevice.Vid));
            Report("YUNZII AL80", HidEnumerator.Find(Al80Protocol.Vid, Al80Protocol.Pid), i => i.HasUsage(Al80Protocol.RawUsagePage, Al80Protocol.RawUsage) ? "raw-HID screen/lighting interface" : null);
            Report("Glorious mouse (258A)", HidEnumerator.Find(GloriousWirelessDevice.Vid).Where(i => i.Pid != 0x013C).ToList());
        }
        catch (Exception ex) { Warn("HID enumeration: " + ex.Message); }

        Line();
        Line(_warn == 0 ? "Discovery finished: no warnings." : $"Discovery finished: {_warn} warning(s) above.");
        return _warn == 0 ? 0 : 1;
    }

    static void Report(string what, List<HidInfo> found, Func<HidInfo, string?>? note = null)
    {
        if (found.Count == 0) { Warn($"{what}: not found"); return; }
        foreach (var i in found)
        {
            string access;
            try { using var s = i.Device.Open(); access = "openable by this user"; }
            catch { access = "not openable by this user (udev rule needed, or run as root)"; }
            Ok($"{what}: {i} — {i.Path} — {access}{(note?.Invoke(i) is string n ? " — " + n : "")}");
        }
    }
}
