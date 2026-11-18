using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;
using HwSuite.Cli;

namespace HwSuite.Platform;

/// <summary>
/// `sudo vanta --fan-verify`: proves on Linux which tachometer follows each Super I/O PWM output, the same evidence the
/// Windows step tests gave, and only then opens <see cref="LinuxFanGate"/>. WRITES to the fan headers, so:
///  - it needs the owner at the PC and asks for "yes" first; the engine must be stopped (it refuses otherwise);
///  - duties stay at or above 40 % (FanMapping.UnattendedFloor) — never a stall test;
///  - one header at a time: 100 % then 40 %, 8 s each; every other header stays on its BIOS curve;
///  - each header is returned to its original pwm_enable mode right after its step, and again in `finally`
///    (also on Ctrl+C), so an error cannot leave a header in manual mode.
/// A header "follows" when its own tach moves by at least 25 % between the two steps and no other tach moves more.
/// </summary>
public static class FanVerify
{
    const int HoldMs = 8000;
    const double HighDuty = 100, LowDuty = FanMapping.UnattendedFloor;

    public static int Run(bool assumeYes)
    {
        Log.ConsoleEnabled = true;
        if (!LibC.IsRoot) { Console.WriteLine("fan verify writes to the fan headers: run it with sudo"); return 1; }
        var hw = new HwmonService();
        if (!hw.Open()) { Console.WriteLine("hwmon: " + hw.Error); return 1; }
        var chip = hw.Chips.FirstOrDefault(c => c.IsSuperIo);
        if (chip == null) { Console.WriteLine("no Super I/O chip in hwmon: the it87 driver for the IT8689E is not loaded (see PORTING_STATUS.md)"); return 1; }
        var pwms = hw.Sensors.Where(s => s.Chip == chip && s.Kind == HwmonKind.Pwm && File.Exists(s.Path + "_enable")).ToList();
        var fans = hw.Sensors.Where(s => s.Chip == chip && s.Kind == HwmonKind.Fan).ToList();
        Console.WriteLine($"{chip.DisplayName}: {pwms.Count} PWM output(s), {fans.Count} tach input(s)");
        Console.WriteLine($"Each header in turn goes to {HighDuty}% then {LowDuty}% for {HoldMs / 1000} s each, then back to its BIOS curve.");
        Console.WriteLine("The pump/AIO on CPU_FAN and every other fan keep running. About " + (pwms.Count * 2 * HoldMs / 60000.0).ToString("0") + " minutes.");
        if (!assumeYes)
        {
            Console.Write("Type yes to start: ");
            if ((Console.ReadLine() ?? "").Trim().ToLowerInvariant() != "yes") { Console.WriteLine("nothing written"); return 2; }
        }

        var originals = pwms.ToDictionary(p => p, p => (enable: Sysfs.ReadText(p.Path + "_enable"), duty: Sysfs.ReadText(p.Path)));
        void Restore(HwmonSensor p)
        {
            var o = originals[p];
            if (o.enable == "1" && o.duty != null) Sysfs.WriteText(p.Path, o.duty, out _);
            if (!Sysfs.WriteText(p.Path + "_enable", o.enable ?? "2", out var err)) Console.WriteLine($"  WARNING: could not restore {p.Attr}_enable: {err}");
        }
        ConsoleCancelEventHandler onCancel = (_, e) => { foreach (var p in pwms) Restore(p); };
        Console.CancelKeyPress += onCancel;

        var map = new Dictionary<int, int>();
        try
        {
            foreach (var p in pwms)
            {
                Console.WriteLine($"\npwm{p.Number} ({p.Label}):");
                if (!Sysfs.WriteText(p.Path + "_enable", "1", out var e1)) { Console.WriteLine("  cannot switch to manual: " + e1); continue; }
                var high = Step(hw, p, HighDuty, fans);
                var low = Step(hw, p, LowDuty, fans);
                Restore(p);
                int best = 0; double bestRel = 0, secondRel = 0;
                foreach (var f in fans)
                {
                    double h = high.GetValueOrDefault(f.Number), l = low.GetValueOrDefault(f.Number);
                    double rel = h > 0 ? (h - l) / h : 0;
                    Console.WriteLine($"  fan{f.Number}: {h,6:0} rpm at {HighDuty}%  {l,6:0} rpm at {LowDuty}%  ({rel * 100:+0;-0}%)");
                    if (rel > bestRel) { secondRel = bestRel; bestRel = rel; best = f.Number; } else if (rel > secondRel) secondRel = rel;
                }
                bool clear = bestRel >= 0.25 && bestRel - secondRel >= 0.15;
                map[p.Number] = clear ? best : 0;
                Console.WriteLine(clear ? $"  -> pwm{p.Number} drives fan{best}" + (best != p.Number ? "  (NOT the same number: VANTA pairs by number, check before enabling!)" : "")
                                        : "  -> no tachometer clearly followed (nothing connected, a 3-pin fan on a PWM-only header, or a hub)");
            }
        }
        finally
        {
            foreach (var p in pwms) Restore(p);
            Console.CancelKeyPress -= onCancel;
        }

        var mismatched = map.Where(kv => kv.Value != 0 && kv.Value != kv.Key).ToList();
        Console.WriteLine();
        if (mismatched.Count > 0)
        {
            Console.WriteLine("Some headers drive a tachometer with a different number. The gate stays closed; report the table above.");
            return 1;
        }
        LinuxFanGate.Save(new LinuxFanGate.Record
        {
            Chip = chip.Name, VerifiedUtc = DateTime.UtcNow, PwmToFan = map,
            Note = $"vanta --fan-verify: {map.Count(kv => kv.Value != 0)} header(s) with a following tach, {map.Count(kv => kv.Value == 0)} without",
        });
        Console.WriteLine($"Verified. Motherboard fan control is now allowed ({LinuxFanGate.FilePath}). Restart the engine: sudo systemctl restart vantad");
        return 0;
    }

    static Dictionary<int, double> Step(HwmonService hw, HwmonSensor pwm, double duty, List<HwmonSensor> fans)
    {
        Sysfs.WriteText(pwm.Path, ((int)Math.Round(duty * 255 / 100)).ToString(), out _);
        Thread.Sleep(HoldMs - 2000);
        var sums = fans.ToDictionary(f => f.Number, _ => 0.0);
        for (int i = 0; i < 4; i++)   // average the last 2 s
        {
            hw.Update();
            foreach (var f in fans) sums[f.Number] += f.Value ?? 0;
            Thread.Sleep(500);
        }
        return sums.ToDictionary(kv => kv.Key, kv => kv.Value / 4);
    }
}
