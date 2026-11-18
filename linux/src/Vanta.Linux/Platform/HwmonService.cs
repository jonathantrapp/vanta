using HwSuite.Core;
using HwSuite.Platform;

namespace HwSuite.Hardware;

public enum HwmonKind { Temperature, Fan, Pwm }

/// <summary>One hwmon attribute group: tempN, fanN or pwmN of a chip.</summary>
public sealed class HwmonSensor
{
    public HwmonChip Chip { get; init; } = null!;
    public HwmonKind Kind { get; init; }
    /// <summary>The attribute number from sysfs (pwm1 = 1).</summary>
    public int Number { get; init; }
    public string Label { get; init; } = "";
    /// <summary>0-based, like LibreHardwareMonitor's sensor index on Windows (pwm1 = index 0).</summary>
    public int Index => Number - 1;
    public string Name => Label;
    /// <summary>Last value: °C, RPM, or duty percent (pwm 0-255 scaled).</summary>
    public double? Value { get; internal set; }
    public string Attr => Kind switch { HwmonKind.Temperature => $"temp{Number}", HwmonKind.Fan => $"fan{Number}", _ => $"pwm{Number}" };
    public string Path => System.IO.Path.Combine(Chip.Dir, Kind switch { HwmonKind.Temperature => Attr + "_input", HwmonKind.Fan => Attr + "_input", _ => Attr });
    /// <summary>
    /// Stable id. For a Super I/O chip it is the same string LibreHardwareMonitor uses on Windows
    /// ("/lpc/it8689e/0/control/0" = pwm1, "/lpc/it8689e/0/fan/0" = fan1, "/lpc/it8689e/0/temperature/0" = temp1; checked against the saved config.json), so the
    /// owner's config.json channels carry over. That channel order is the chip's register order in both drivers, but it
    /// is re-verified on Linux by a step test before any motherboard fan is driven (see LinuxFanGate).
    /// </summary>
    public string Id => Chip.IsSuperIo
        ? $"/lpc/{Chip.LhmName}/0/{Kind switch { HwmonKind.Temperature => "temperature", HwmonKind.Fan => "fan", _ => "control" }}/{Number - 1}"
        : $"/hwmon/{Chip.Name}/{Chip.Instance}/{Attr}";
}

/// <summary>One /sys/class/hwmon/hwmonN directory.</summary>
public sealed class HwmonChip
{
    public string Dir { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Distinguishes two chips with the same name (the device's sysfs name, e.g. "it87.2624", "coretemp.0").</summary>
    public string Instance { get; init; } = "";
    public bool IsSuperIo => Name.StartsWith("it8", StringComparison.Ordinal) || Name.StartsWith("nct", StringComparison.Ordinal);
    /// <summary>The it87 driver names the IT8689E "it8689"; LibreHardwareMonitor says "it8689e".</summary>
    public string LhmName => Name.StartsWith("it8", StringComparison.Ordinal) && !Name.EndsWith("e", StringComparison.Ordinal) ? Name + "e" : Name;
    public string DisplayName => Name switch
    {
        "coretemp" => "Intel CPU",
        _ when Name.StartsWith("it8", StringComparison.Ordinal) => "ITE " + LhmName.ToUpperInvariant(),
        "nvme" => "NVMe drive",
        _ => Name,
    };
}

/// <summary>
/// LINUX: all hwmon chips, read from sysfs. Takes the place of LhmService (LibreHardwareMonitor + PawnIO on Windows).
/// Sysfs reads are cheap and atomic per attribute; <see cref="Gate"/> keeps one update's values together.
/// </summary>
public sealed class HwmonService : IDisposable
{
    public const string Root = "/sys/class/hwmon";
    public object Gate { get; } = new();
    readonly List<HwmonChip> _chips = new();
    readonly List<HwmonSensor> _sensors = new();
    public bool IsOpen { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<HwmonChip> Chips { get { lock (Gate) return _chips.ToList(); } }
    public IReadOnlyList<HwmonSensor> Sensors { get { lock (Gate) return _sensors.ToList(); } }
    /// <summary>Attributes whose last read failed (path), for diagnostics.</summary>
    public HashSet<string> LastUpdateFailures { get; } = new();

    public string RootDir { get; init; } = Root;

    public bool Open()
    {
        if (RunMode.Refuse("open hwmon")) { Error = "hardware-free run"; return false; }
        lock (Gate)
        {
            _chips.Clear(); _sensors.Clear();
            if (!Directory.Exists(RootDir)) { Error = RootDir + " not found"; return false; }
            foreach (var dir in Directory.GetDirectories(RootDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Sysfs.ReadText(Path.Combine(dir, "name"));
                if (string.IsNullOrEmpty(name)) continue;
                string inst = "";
                try { inst = Path.GetFileName(Sysfs.RealPath(Path.Combine(dir, "device"))); } catch { }
                if (string.IsNullOrEmpty(inst)) inst = Path.GetFileName(dir);
                var chip = new HwmonChip { Dir = dir, Name = name, Instance = inst };
                _chips.Add(chip);
                AddAll(chip, "temp", "_input", HwmonKind.Temperature);
                AddAll(chip, "fan", "_input", HwmonKind.Fan);
                AddAll(chip, "pwm", "", HwmonKind.Pwm);
            }
            var ordered = _sensors.OrderBy(x => _chips.IndexOf(x.Chip)).ThenBy(x => x.Kind).ThenBy(x => x.Number).ToList();
            _sensors.Clear(); _sensors.AddRange(ordered);
            IsOpen = true;
            Error = null;
        }
        Update();
        return true;
    }

    void AddAll(HwmonChip chip, string prefix, string suffix, HwmonKind kind)
    {
        foreach (var f in Directory.GetFiles(chip.Dir, prefix + "*" + suffix))
        {
            var n = Path.GetFileName(f);
            var digits = n.Substring(prefix.Length, n.Length - prefix.Length - suffix.Length);
            if (!int.TryParse(digits, out int num)) continue;   // skips pwm1_enable etc.
            var label = Sysfs.ReadText(Path.Combine(chip.Dir, $"{prefix}{num}_label"));
            _sensors.Add(new HwmonSensor
            {
                Chip = chip, Kind = kind, Number = num,
                Label = label ?? kind switch { HwmonKind.Temperature => $"Temperature #{num}", HwmonKind.Fan => $"Fan #{num}", _ => $"Fan Control #{num}" },
            });
        }
    }

    public void Update()
    {
        lock (Gate)
        {
            LastUpdateFailures.Clear();
            foreach (var s in _sensors)
            {
                var raw = Sysfs.ReadLong(s.Path);
                if (raw == null) { s.Value = null; LastUpdateFailures.Add(s.Path); continue; }
                s.Value = s.Kind switch
                {
                    HwmonKind.Temperature => raw.Value / 1000.0,
                    HwmonKind.Fan => raw.Value,
                    _ => Math.Round(raw.Value * 100.0 / 255.0, 1),
                };
            }
        }
    }

    public List<string> Describe()
    {
        var lines = new List<string>();
        lock (Gate)
            foreach (var c in _chips)
            {
                lines.Add($"{c.Dir}: {c.Name} ({c.Instance})");
                foreach (var s in _sensors.Where(x => x.Chip == c))
                    lines.Add($"  {s.Attr,-7} {s.Label,-22} {(s.Value is double v ? v.ToString("0.#") : "-"),8}  id {s.Id}");
            }
        return lines;
    }

    public void Dispose() { IsOpen = false; }
}
