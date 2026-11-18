// PORTED from src\HwSuite\Cli\FanMapping.cs (Windows 1.20.0): Super I/O sensors come from hwmon (HwmonSensor, HwmonFanChannel). Test logic unchanged; names fanN/ctlN keep the Windows 0-based numbering (pwm1 = ctl0).
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Fans;
using HwSuite.Hardware;

namespace HwSuite.Cli;

/// <summary>
/// Phase 9 fan mapping tools. <c>--fan-census</c> only reads: every Super I/O tachometer and PWM/DC control register
/// value, once per second. <c>--fan-step</c> drives ONE motherboard control through a short list of duties and records
/// every tachometer, so a control can be matched to the tachometer(s) that respond. It never touches control 0
/// (CPU_FAN: the Arctic pump), refuses duties below <see cref="UnattendedFloor"/> unless the owner is watching
/// (<c>--observed</c>), aborts to 100% if any spinning fan collapses, and always restores the header (same process, so
/// LibreHardwareMonitor's saved BIOS register values are written back).
/// </summary>
public static class FanMapping
{
    /// <summary>Lowest duty used without the owner watching. Not a measured stall point: a deliberately high bound.</summary>
    public const int UnattendedFloor = 40;
    /// <summary>A tachometer that was spinning and falls below this is treated as a possible stall.</summary>
    public const double CollapseRpm = 250;

    sealed record Row(double T, string Phase, Dictionary<string, double?> Rpm, Dictionary<string, double?> Duty, double? CpuC, double? GpuC);

    static (List<HwmonSensor> fans, List<HwmonSensor> controls) Board(AppServices app)
    {
        var all = app.Hub.Hwmon.Sensors.Where(s => s.Chip.IsSuperIo).ToList();
        return (all.Where(s => s.Kind == HwmonKind.Fan).OrderBy(s => s.Index).ToList(),
                all.Where(s => s.Kind == HwmonKind.Pwm).OrderBy(s => s.Index).ToList());
    }

    static Row Sample(AppServices app, double t, string phase)
    {
        var snap = app.Hub.Poll();   // updates hwmon under its gate
        var (fans, controls) = Board(app);
        return new Row(t, phase,
            fans.ToDictionary(f => $"fan{f.Index}", f => f.Value),
            controls.ToDictionary(c => $"ctl{c.Index}", c => c.Value),
            snap.CpuTempC, snap.GpuTempC);
    }

    static string Fmt(Row r) =>
        $"{r.T,6:0.0}s {r.Phase,-10} " + string.Join(" ", r.Rpm.Select(kv => $"{kv.Key}={(kv.Value is double v ? v.ToString("0") : "n/a"),5}"))
        + " | " + string.Join(" ", r.Duty.Select(kv => $"{kv.Key}={(kv.Value is double v ? v.ToString("0") : "n/a"),3}%"))
        + $" | cpu {(r.CpuC is double c ? c.ToString("0") : "n/a")}C gpu {(r.GpuC is double g ? g.ToString("0") : "n/a")}C";

    static void Save(string? path, object data)
    {
        if (path == null) return;
        try { File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log.Error("could not write " + path, ex); }
    }

    public static int Census(int seconds, string? jsonOut)
    {
        using var app = new AppServices(new AppConfig());
        app.InitializeHardware();
        var (fans, controls) = Board(app);
        Log.Info($"fan census (read only): {fans.Count} Super I/O tachometer inputs, {controls.Count} controls");
        foreach (var f in fans) Log.Info($"  tach    index {f.Index}  '{f.Name}'  id={f.Id}");
        foreach (var c in controls)
            Log.Info($"  control index {c.Index}  '{c.Name}'  id={c.Id}  pwm_enable={HwSuite.Platform.Sysfs.ReadText(c.Path + "_enable") ?? "?"}  duty={c.Value:0}%");
        foreach (var ch in app.Hub.Engine.Channels.OfType<HwmonFanChannel>())
            Log.Info($"  VANTA pairs {ch.ControlSensorId} with tach {(ch.FanSensorId ?? "none")} (by matching index only: not physical evidence)");
        var rows = new List<Row>();
        for (int i = 0; i < seconds; i++)
        {
            var r = Sample(app, i, "read");
            rows.Add(r); Log.Info(Fmt(r));
            Thread.Sleep(1000);
        }
        foreach (var key in rows[0].Rpm.Keys)
        {
            var v = rows.Select(r => r.Rpm[key]).Where(x => x.HasValue).Select(x => x!.Value).ToList();
            Log.Info(v.Count == 0 ? $"{key}: no reading" : $"{key}: min {v.Min():0} / mean {v.Average():0} / max {v.Max():0} RPM over {v.Count} samples");
        }
        Save(jsonOut, new { kind = "census", at = DateTime.Now, fans = fans.Select(f => new { f.Index, f.Name, f.Id }), controls = controls.Select(c => new { c.Index, c.Name, c.Id }), rows });
        return 0;
    }

    /// <summary>
    /// Attended (owner present) running-floor and startup test for one DC case-fan header. Descends 60..20% in steps,
    /// waiting at each duty until the IT8689E's slow ramp settles, and stops at the first duty where the fan's tachometer
    /// falls below <see cref="StallRpm"/> (then 100% at once). Then startup: from a stop (0% until the tach reads below
    /// 100 RPM or 20 s), apply a candidate duty and see whether the fan starts within 12 s, from the lowest running duty
    /// upward in 5% steps (100% between tries). The recommendation is the higher of the two plus a 10-point margin; it is
    /// only a suggestion for the owner to confirm. Never touches control 0 (CPU_FAN, pump) and always restores the header.
    /// </summary>
    public static int MinTest(int controlIndex, bool observed, string? jsonOut, bool startupOnly = false, int startFrom = 20)
    {
        if (controlIndex <= 0) { Log.Error("fan-min-test: control 0 is CPU_FAN (Arctic pump connection) and is never tested"); return 2; }
        if (!observed) { Log.Error("fan-min-test: stops the fan; run only with the owner present (--observed)"); return 2; }
        using var app = new AppServices(new AppConfig());
        app.InitializeHardware();
        var ch = app.Hub.Engine.Channels.OfType<HwmonFanChannel>().FirstOrDefault(c => c.ControlSensorId.EndsWith($"/control/{controlIndex}"));
        if (ch == null || !ch.CanControl) { Log.Error($"fan-min-test: no controllable Super I/O control {controlIndex}"); return 3; }
        string tach = $"fan{controlIndex}";   // control N drives tachometer N (fan-step evidence, docs\phase9)
        var rows = new List<Row>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? abort = null;
        Row Take(string phase)
        {
            var r = Sample(app, sw.Elapsed.TotalSeconds, phase); rows.Add(r); Log.Info(Fmt(r));
            if ((r.CpuC ?? 0) >= 80 || (r.GpuC ?? 0) >= 80) throw new InvalidOperationException("thermal limit");
            return r;
        }
        // an implausible reading (a stalled fan briefly reads ~9 900 RPM on this chip) counts as not turning
        double? Rpm(Row r) => r.Rpm.TryGetValue(tach, out var v) ? (FanEngine.Spinning(v) ? v : 0) : null;
        // settle: wait until the last 3 readings move less than 15 RPM (the chip ramps ~30 RPM/s), at most maxS seconds
        double Settle(string phase, int maxS, Func<double, bool>? stopEarly = null)
        {
            var hist = new List<double>();
            for (int i = 0; i < maxS; i++)
            {
                Thread.Sleep(1000);
                double v = Rpm(Take(phase)) ?? 0; hist.Add(v);
                if (stopEarly != null && stopEarly(v)) return v;
                if (hist.Count >= 4 && hist.TakeLast(3).Max() - hist.TakeLast(3).Min() < 15) return hist.TakeLast(3).Average();
            }
            return hist.Count > 0 ? hist.TakeLast(3).Average() : 0;
        }

        Log.Info($"fan-min-test on {ch.ControlSensorId} (tach {tach}), owner present");
        var running = new List<(int duty, double rpm)>();
        int? stalledAt = null, startDuty = null;
        var startTries = new List<(int duty, double fromRpm, double reached, bool started)>();
        try
        {
            for (int i = 0; i < 4; i++) { Take("baseline"); Thread.Sleep(1000); }
            foreach (var d in startupOnly ? Array.Empty<int>() : new[] { 60, 50, 45, 40, 35, 30, 25, 20 })
            {
                ch.SetPercent(d);
                double v = Settle($"run {d}%", 25, x => x < StallRpm);
                if (v < StallRpm) { stalledAt = d; Log.Warn($"tach below {StallRpm:0} RPM at {d}%: back to 100%"); ch.SetPercent(100); Settle("recover", 15); break; }
                running.Add((d, Math.Round(v)));
            }
            int lowest = startupOnly ? Math.Clamp(startFrom, 15, 60) : running.Count > 0 ? running[^1].duty : 60;
            for (int d = lowest; d <= 60; d += 5)
            {
                ch.SetPercent(0);
                // at rest: the tach must read 0 for 8 consecutive seconds (a coasting fan can read 0 for a moment)
                int zeros = 0; double from = 0;
                for (int i = 0; i < 40 && zeros < 8; i++) { Thread.Sleep(1000); from = Rpm(Take($"stop before {d}%")) ?? 0; zeros = from < 1 ? zeros + 1 : 0; }
                if (zeros < 8) { Log.Warn($"fan did not come to rest at 0% within 40 s (last {from:0} RPM): startup test stopped"); break; }
                ch.SetPercent(d);
                double reached = 0; bool started = false;
                for (int i = 0; i < 15; i++) { Thread.Sleep(1000); reached = Rpm(Take($"start {d}%")) ?? 0; if (reached >= StallRpm && i >= 3) { started = true; break; } }
                startTries.Add((d, Math.Round(from), Math.Round(reached), started));
                Log.Info($"startup at {d}% from {from:0} RPM: {(started ? "started" : "did not start")} (reached {reached:0} RPM)");
                if (started) { startDuty = d; break; }
                ch.SetPercent(100); Settle("recover", 15);
            }
        }
        catch (Exception ex) { abort = ex.Message; Log.Warn("fan-min-test ABORT: " + abort); try { ch.SetPercent(100); } catch { } Thread.Sleep(3000); }
        finally
        {
            bool ok = ch.RestoreOriginal();
            Log.Info($"fan-min-test: header restored to its pre-test state ({(ok ? "ok" : "FAILED")}); registry entries left: {ControlRegistry.Load().Count}");
        }
        for (int i = 0; i < 6; i++) { Thread.Sleep(1000); Take("restored"); }
        int lowestRunning = startupOnly ? 0 : running.Count > 0 ? running[^1].duty : 60;
        int? suggestion = abort == null && startDuty is int sd ? (int)(Math.Ceiling((Math.Max(sd, lowestRunning) + 10) / 5.0) * 5) : null;
        Log.Info($"RESULT control {controlIndex}: lowest running duty {lowestRunning}% ({(stalledAt is int s ? $"tach collapsed at {s}%" : "no stall down to 20%")}); " +
                 $"starts from a stop at {(startDuty?.ToString() ?? "n/a")}%; suggested minimum {(suggestion?.ToString() ?? "n/a")}% (for the owner to confirm)");
        Save(jsonOut, new { kind = "min-test", control = controlIndex, tach, running = running.Select(r => new { r.duty, r.rpm }), stalledAt,
            startTries = startTries.Select(t => new { t.duty, t.fromRpm, t.reached, t.started }), startDuty, suggestion, abort, rows });
        return abort == null ? 0 : 4;
    }

    /// <summary>
    /// Fan-engine check on the identified case-fan headers with the owner's saved minimums (config read only): each is put
    /// in Manual at 0% so the engine itself must clamp it to its confirmed floor, held, then set back to firmware control.
    /// Passes when every fan keeps turning at a duty at or above its floor and the hand-back leaves no channel claimed.
    /// GPU fans and CPU_FAN are forced to firmware control (Auto) in this run.
    /// </summary>
    public static int EngineCheck(int holdSeconds, string? jsonOut)
    {
        var cfg = ConfigStore.Parse(JsonSerializer.Serialize(ConfigStore.Load())) ?? new AppConfig();
        cfg.AutoStopGigabyte = false; cfg.Rgb.Enabled = false;
        var ids = ChannelDefaults.CaseFanLabels.Keys.ToList();
        foreach (var c in cfg.Channels)
        {
            if (ids.Contains(c.Id)) { c.Mode = ChannelMode.Manual; c.ManualPercent = 0; }
            else c.Mode = ChannelMode.Auto;
        }
        var floors = cfg.Channels.Where(c => ids.Contains(c.Id)).ToDictionary(c => c.Id, c => c.VerifiedMinPercent);
        if (floors.Count != ids.Count || floors.Values.Any(v => v is null)) { Log.Error("engine check: the case-fan headers need confirmed minimums in the config first"); return 2; }
        cfg.FanControlEnabled = true;
        using var app = new AppServices(cfg);
        app.InitializeHardware();
        app.Hub.Start();
        var chans = app.Hub.Engine.Channels.OfType<HwmonFanChannel>().Where(c => ids.Contains(c.Id)).ToList();
        var samples = new List<object>();
        bool ok = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < holdSeconds; i++)
        {
            Thread.Sleep(1000);
            foreach (var ch in chans)
            {
                double? duty = ch.ReadPercent(), rpm = ch.ReadRpm();
                string state = app.Hub.Engine.Status.TryGetValue(ch.Id, out var st) ? st.State : "?";
                samples.Add(new { t = Math.Round(sw.Elapsed.TotalSeconds, 1), ch.Id, duty, rpm, state });
                if (i == holdSeconds - 1)
                {
                    bool good = duty >= floors[ch.Id] - 0.5 && FanEngine.Spinning(rpm) && rpm >= StallRpm;
                    ok &= good;
                    Log.Info($"{(good ? "PASS" : "FAIL")}  {ChannelDefaults.CaseFanLabels[ch.Id]}: asked 0%, engine holds {duty:0}% (floor {floors[ch.Id]:0}%), {rpm:0} RPM, state '{state}'");
                }
            }
            if (i % 5 == 4) Log.Info("  " + string.Join("  ", chans.Select(c => $"{c.Id[^9..]} {c.ReadPercent():0}% {c.ReadRpm():0} RPM")));
        }
        lock (cfg.Sync) foreach (var c in cfg.Channels.Where(c => ids.Contains(c.Id))) c.Mode = ChannelMode.Auto;
        Thread.Sleep(3000);
        int claimed = ControlRegistry.Load().Count(e => ids.Any(id => id.EndsWith(e.Id)));
        bool released = chans.All(c => !app.Hub.Engine.IsControlled(c.Id)) && claimed == 0;
        ok &= released;
        Log.Info($"{(released ? "PASS" : "FAIL")}  set back to firmware control: no case-fan header still claimed");
        for (int i = 0; i < 10; i++) { Thread.Sleep(1000); if (i % 3 == 2) Log.Info("  after hand-back: " + string.Join("  ", chans.Select(c => $"{c.Id[^9..]} {c.ReadRpm():0} RPM"))); }
        Save(jsonOut, new { kind = "engine-check", floors, ok, samples });
        return ok ? 0 : 1;
    }

    /// <summary>Tachometer reading treated as "not turning" in the attended minimum test.</summary>
    public const double StallRpm = 200;

    /// <summary>
    /// Attended (owner present) startup check of one case-fan header at its chosen floor; never a search for a lower one.
    /// A: <paramref name="repeat"/> times: 0% until the tachometer reads 0 for 8 s (the fan is at rest), then exactly
    /// <paramref name="duty"/> (default: the owner's confirmed minimum from the config), and the fan must exceed
    /// <see cref="StallRpm"/> within 15 s; it is then left running at that duty for 5 s. The first failure ends part A at once
    /// (100%, no higher duties tried). B (<paramref name="engine"/>): the fan is stopped again and VANTA's FanEngine takes the
    /// header over (Curve, floor <paramref name="engineFloor"/> or the confirmed minimum) from the stop, so its startup
    /// verification runs on the real header: it must see rotation, or kick to 100% and recover. Thermal limit 80 C aborts
    /// to 100%. Never touches control 0 (CPU_FAN, pump); the header is always restored to its pre-test (BIOS) state.
    /// </summary>
    public static int StartCheck(int controlIndex, bool observed, int? duty, int repeat, bool engine, int? engineFloor, string? jsonOut)
    {
        if (controlIndex is < 1 or > 3) { Log.Error("fan-start-check: only the identified case-fan controls 1-3 (never CPU_FAN)"); return 2; }
        if (!observed) { Log.Error("fan-start-check: stops the fan; run only with the owner present (--observed)"); return 2; }
        var owner = ConfigStore.Load();
        string id = $"lhm:/lpc/it8689e/0/control/{controlIndex}";
        var saved = owner.Channels.FirstOrDefault(c => c.Id == id);
        int d = duty ?? (saved?.VerifiedMinPercent is double v ? (int)Math.Round(v) : -1);
        if (d is < 20 or > 100) { Log.Error("fan-start-check: no duty given and no confirmed minimum in the config"); return 2; }
        repeat = Math.Clamp(repeat, 1, 5);
        var cfg = new AppConfig { FanControlEnabled = false, AutoStopGigabyte = false };
        using var app = new AppServices(cfg);
        app.InitializeHardware();
        var ch = app.Hub.Engine.Channels.OfType<HwmonFanChannel>().FirstOrDefault(c => c.Id == id);
        if (ch == null || !ch.CanControl) { Log.Error($"fan-start-check: no controllable {id}"); return 3; }
        string tach = $"fan{controlIndex}";
        var rows = new List<Row>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? abort = null;
        Row Take(string phase)
        {
            var r = Sample(app, sw.Elapsed.TotalSeconds, phase); rows.Add(r); Log.Info(Fmt(r));
            if ((r.CpuC ?? 0) >= 80 || (r.GpuC ?? 0) >= 80) throw new InvalidOperationException("thermal limit");
            return r;
        }
        double? Rpm(Row r) => r.Rpm.TryGetValue(tach, out var x) ? (FanEngine.Spinning(x) ? x : 0) : null;
        bool ToRest(string phase)
        {
            ch.SetPercent(0);
            int zeros = 0; double last = 0;
            for (int i = 0; i < 40 && zeros < 8; i++) { Thread.Sleep(1000); last = Rpm(Take(phase)) ?? 0; zeros = last < 1 ? zeros + 1 : 0; }
            if (zeros < 8) Log.Warn($"fan did not come to rest at 0% within 40 s (last {last:0} RPM)");
            return zeros >= 8;
        }

        Log.Info($"fan-start-check on {id} ({ChannelDefaults.CaseFanLabels.GetValueOrDefault(id)}), duty {d}%, {repeat} start(s){(engine ? ", then the engine takeover" : "")}; owner present");
        var tries = new List<object>();
        int started = 0;
        var engineEvents = new List<object>();
        bool engineVerified = false; int engineKicks = 0; bool engineGaveUp = false;
        try
        {
            for (int i = 0; i < 3; i++) { Take("baseline"); Thread.Sleep(1000); }
            for (int n = 1; n <= repeat; n++)
            {
                if (!ToRest($"stop {n}")) { abort = "fan did not come to rest"; break; }
                ch.SetPercent(d);
                double reached = 0, firstSeen = -1; bool ok = false;
                var t0 = sw.Elapsed.TotalSeconds;
                for (int i = 0; i < 15; i++)
                {
                    Thread.Sleep(1000);
                    reached = Rpm(Take($"start {n} at {d}%")) ?? 0;
                    if (reached > 0 && firstSeen < 0) firstSeen = sw.Elapsed.TotalSeconds - t0;
                    if (reached >= StallRpm && i >= 3) { ok = true; break; }
                }
                tries.Add(new { attempt = n, duty = d, started = ok, firstRotationSeconds = firstSeen < 0 ? (double?)null : Math.Round(firstSeen, 1), reachedRpm = Math.Round(reached) });
                Log.Info($"START {n}/{repeat} at {d}%: {(ok ? "started" : "DID NOT START")} (first rotation after {(firstSeen < 0 ? "-" : firstSeen.ToString("0.0"))} s, reached {reached:0} RPM)");
                if (!ok) { ch.SetPercent(100); Thread.Sleep(5000); break; }
                started++;
                for (int i = 0; i < 5; i++) { Thread.Sleep(1000); Take($"running {n} at {d}%"); }
            }

            if (engine && abort == null)
            {
                int floor = engineFloor ?? d;
                if (!ToRest("stop for engine")) throw new InvalidOperationException("fan did not come to rest");
                // the engine takes the stopped header over, exactly as when fan control is switched on
                var cc = ChannelDefaults.Create(ch, "Balanced");
                cc.Mode = ChannelMode.Curve; cc.VerifiedMinPercent = floor; cc.MinEvidence = "fan-start-check (test only)";
                cc.Curve = new FanCurve { Name = "test", Source = CurveSource.Gpu, HysteresisC = 2, RampUpPerSec = 35, StepDownPerSec = 6, Points = { new CurvePoint(20, floor), new CurvePoint(90, floor) } };
                lock (cfg.Sync) { cfg.Channels.RemoveAll(c => c.Id != id && c.Mode != ChannelMode.Auto); cfg.Channels.RemoveAll(c => c.Id == id); cfg.Channels.Add(cc); foreach (var c in cfg.Channels.Where(c => c.Id != id)) c.Mode = ChannelMode.Auto; cfg.FanControlEnabled = true; }
                app.Hub.Engine.Alert += a => { engineEvents.Add(new { t = Math.Round(sw.Elapsed.TotalSeconds, 1), alert = a }); Log.Warn("ENGINE ALERT: " + a); };
                Log.Info($"ENGINE takeover from a stop, floor {floor}% (tach mapped: {cc.TachMapped}; TachSeen not set)");
                var t0 = sw.Elapsed.TotalSeconds;
                while (sw.Elapsed.TotalSeconds - t0 < 50)
                {
                    var snap = app.Hub.Poll();
                    app.Hub.Engine.Tick(snap, 1);
                    var st = app.Hub.Engine.Status.GetValueOrDefault(id);
                    var (ver, kicks, failed) = app.Hub.Engine.StartState(id);
                    engineVerified |= ver; engineKicks = Math.Max(engineKicks, kicks); engineGaveUp |= failed;
                    var r = Take("engine");
                    engineEvents.Add(new { t = Math.Round(r.T, 1), rpm = Rpm(r), duty = r.Duty.GetValueOrDefault($"ctl{controlIndex}"), state = st?.State, verified = ver, kicks });
                    if (ver && sw.Elapsed.TotalSeconds - t0 > 15) break;
                    if (failed) break;
                    Thread.Sleep(1000);
                }
                lock (cfg.Sync) { cc.Mode = ChannelMode.Auto; }
                app.Hub.Engine.Tick(app.Hub.Poll(), 1);
                Log.Info($"ENGINE result: verified {engineVerified}, start kicks {engineKicks}, gave up {engineGaveUp}; handed back {(!app.Hub.Engine.IsControlled(id))}");
            }
        }
        catch (Exception ex) { abort = ex.Message; Log.Warn("fan-start-check ABORT: " + abort); try { ch.SetPercent(100); } catch { } Thread.Sleep(3000); }
        finally
        {
            try { app.Hub.Engine.ReleaseAll(); } catch { }
            bool ok = ch.RestoreOriginal();
            Log.Info($"fan-start-check: header restored to its pre-test state ({(ok ? "ok" : "FAILED")}); registry entries left: {ControlRegistry.Load().Count}");
        }
        for (int i = 0; i < 6; i++) { Thread.Sleep(1000); Take("restored"); }
        Log.Info($"RESULT control {controlIndex}: {started}/{repeat} starts from rest at {d}%" + (engine ? $"; engine takeover: verified {engineVerified}, kicks {engineKicks}, gave up {engineGaveUp}" : "") + (abort != null ? $"; aborted: {abort}" : ""));
        Save(jsonOut, new { kind = "start-check", control = controlIndex, duty = d, repeat, started, tries, engine, engineFloor, engineVerified, engineKicks, engineGaveUp, engineEvents, abort, rows });
        return abort == null && started == repeat ? 0 : 4;
    }

    public static int Step(int controlIndex, int[] duties, int holdSeconds, bool observed, string? jsonOut)
    {
        if (controlIndex <= 0) { Log.Error("fan-step: control 0 is CPU_FAN (Arctic pump connection) and is never stepped by this tool"); return 2; }
        var low = duties.Where(d => d < UnattendedFloor).ToList();
        if (low.Count > 0 && !observed) { Log.Error($"fan-step: {string.Join(",", low)}% is below {UnattendedFloor}%; lower duties only with --observed (the owner watching the fans)"); return 2; }
        if (duties.Any(d => d < 0 || d > 100) || duties.Length == 0) { Log.Error("fan-step: duties must be 0..100"); return 2; }
        holdSeconds = Math.Clamp(holdSeconds, 4, 30);

        using var app = new AppServices(new AppConfig());
        app.InitializeHardware();
        var ch = app.Hub.Engine.Channels.OfType<HwmonFanChannel>().FirstOrDefault(c => c.ControlSensorId.EndsWith($"/control/{controlIndex}"));
        if (ch == null || !ch.CanControl) { Log.Error($"fan-step: no controllable Super I/O control {controlIndex}"); return 3; }
        Log.Info($"fan-step on {ch.ControlSensorId} duties {string.Join(",", duties)}% hold {holdSeconds}s{(observed ? " (owner observing)" : "")}");

        var rows = new List<Row>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? abortReason = null;
        Row Take(string phase) { var r = Sample(app, sw.Elapsed.TotalSeconds, phase); rows.Add(r); Log.Info(Fmt(r)); return r; }

        for (int i = 0; i < 5; i++) { Take("baseline"); Thread.Sleep(1000); }
        var baseline = rows.Last().Rpm;
        var spinning = baseline.Where(kv => kv.Value is double v && v >= CollapseRpm * 2).Select(kv => kv.Key).ToList();
        Log.Info("spinning at baseline: " + (spinning.Count == 0 ? "none" : string.Join(", ", spinning)));
        try
        {
            foreach (var d in duties)
            {
                ch.SetPercent(d);
                for (int i = 0; i < holdSeconds; i++)
                {
                    Thread.Sleep(1000);
                    var r = Take($"{d}%");
                    if ((r.CpuC ?? 0) >= 80 || (r.GpuC ?? 0) >= 80) { abortReason = "thermal limit"; break; }
                    var collapsed = spinning.Where(k => r.Rpm.TryGetValue(k, out var v) && (!FanEngine.Spinning(v) || v < CollapseRpm)).ToList();
                    if (collapsed.Count > 0) { abortReason = $"{string.Join(",", collapsed)} fell below {CollapseRpm:0} RPM at {d}%"; break; }
                }
                if (abortReason != null) break;
            }
            if (abortReason != null)
            {
                Log.Warn("fan-step ABORT: " + abortReason + "; header to 100% for 5 s, then restored");
                ch.SetPercent(100);
                for (int i = 0; i < 5; i++) { Thread.Sleep(1000); Take("abort100"); }
            }
        }
        finally
        {
            bool ok = ch.RestoreOriginal();
            Log.Info($"fan-step: header restored to its pre-test state ({(ok ? "ok" : "FAILED")}); registry entries left: {ControlRegistry.Load().Count}");
        }
        for (int i = 0; i < 8; i++) { Thread.Sleep(1000); Take("restored"); }

        // response summary: mean RPM over the last half of each phase
        var summary = new Dictionary<string, Dictionary<string, double?>>();
        foreach (var phase in rows.Select(r => r.Phase).Distinct())
        {
            var pr = rows.Where(r => r.Phase == phase).ToList();
            var tail = pr.Skip(pr.Count / 2).ToList();
            summary[phase] = tail[0].Rpm.Keys.ToDictionary(k => k, k =>
            {
                var v = tail.Select(r => r.Rpm[k]).Where(x => x.HasValue).Select(x => x!.Value).ToList();
                return v.Count == 0 ? (double?)null : Math.Round(v.Average());
            });
            Log.Info($"summary {phase,-10} " + string.Join(" ", summary[phase].Select(kv => $"{kv.Key}={(kv.Value?.ToString("0") ?? "n/a")}")));
        }
        Save(jsonOut, new { kind = "step", control = controlIndex, duties, holdSeconds, observed, abortReason, summary, rows });
        return abortReason == null ? 0 : 4;
    }
}
