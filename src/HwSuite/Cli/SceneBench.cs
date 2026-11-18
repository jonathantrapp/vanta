using System.Diagnostics;
using HwSuite.Core;
using HwSuite.Lighting;

namespace HwSuite.Cli;

/// <summary>
/// Measurement only (hardware-free): the lighting engine's own cost per effect, with stand-in devices shaped like the
/// owner's (27-LED D_LED chain at its strip rate, 2 board zones at 20/s, four memory controllers 5/8/5/8 at 10/s, the
/// 87-key keyboard at its live-frame rate). The stand-ins accept every frame instantly: no USB or SMBus transfer is
/// made, so this isolates rendering and scheduling. It also counts how many frames each stand-in would really have
/// written (changed frames after the existing unchanged-frame skip), which is what would reach the buses.
/// </summary>
public static class SceneBench
{
    sealed class Stand : RgbSink
    {
        readonly int _n; readonly double[] _pos; readonly double _hz; readonly DeviceRole _role; readonly int[]? _runs; readonly bool _strip;
        public long Writes;
        public Stand(DeviceRole role, double[] pos, double hz, bool strip = false, int[]? runs = null)
        { _role = role; _pos = pos; _n = pos.Length; _hz = hz; _strip = strip; _runs = runs; Available = true; }
        public override string Name => "stand-in " + _role;
        public override string Kind => "none";
        public override int LedCount => _n;
        public override double[] Positions => _pos;
        protected override double MaxHz => _hz;
        public override bool FollowsStripRate => _strip;
        public override bool EnabledByDefault => true;
        public override DeviceRole? RoleHint => _role;
        public override IReadOnlyList<int>? LedRuns => _runs;
        protected override bool Apply(Rgb[] frame) { Interlocked.Increment(ref Writes); return true; }
    }

    public static int Run(int seconds, string? outPath, int stripHz)
    {
        ConfigStore.ReadOnly = true;
        if (!RunMode.HardwareFree) { Console.WriteLine("scene-bench runs only hardware-free"); return 2; }
        var effects = new[] { EffectKind.Static, EffectKind.GradientWave }.Concat(EffectInfo.Scenes).Concat(EffectInfo.RainScenes).ToArray();
        var rows = new List<Dictionary<string, object>>();
        var proc = Process.GetCurrentProcess();
        foreach (var e in effects)
        {
            var cfg = new AppConfig();
            var r = cfg.Rgb;
            r.Enabled = true; r.DLedMaxHz = stripHz;
            r.DLedStrip = r.Motherboard = r.Ram = r.Mouse = r.Keyboard = false;   // stand-ins only (EnabledByDefault)
            var look = LightingLooks.Starters.FirstOrDefault(l => l.Effect == e);
            if (look != null) LightingLooks.Apply(cfg, look); else { r.Effect = e; r.ColorA = 0x397780; r.ColorB = 0xA9C4CC; r.Brightness = 0.35; r.Speed = 0.2; }
            var sinks = new[]
            {
                new Stand(DeviceRole.Strip, DLedSink.PositionsFor(27), stripHz, strip: true),
                new Stand(DeviceRole.Board, MotherboardSink.BasePositions, 20),
                new Stand(DeviceRole.Ram, RamSink.PositionsFor(new[] { 5, 8, 5, 8 }), 10, runs: new[] { 5, 8, 5, 8 }),
                new Stand(DeviceRole.Keyboard, KeyboardSink.LivePositions, KeyboardSink.LiveHz),
            };
            using var engine = new RgbEngine(cfg, () => 45);
            engine.Sinks.AddRange(sinks);
            engine.Start();
            Thread.Sleep(1500);   // settle
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            proc.Refresh();
            double cps = ThreadCpu.CyclesPerSecond();
            ulong cyc0 = ThreadCpu.ProcessCycles(); long alloc0 = GC.GetTotalAllocatedBytes(true); long ticks0 = engine.Ticks;
            var writes0 = sinks.Select(s => s.Writes).ToArray();
            var sw = Stopwatch.StartNew();
            Thread.Sleep(seconds * 1000);
            proc.Refresh();
            double el = sw.Elapsed.TotalSeconds, cpuMs = (ThreadCpu.ProcessCycles() - cyc0) / cps * 1000;   // exact cycle counts, not 15.6 ms ticks
            long alloc = GC.GetTotalAllocatedBytes(true) - alloc0; long frames = engine.Ticks - ticks0;
            var writes = sinks.Select((s, i) => (s.Writes - writes0[i]) / el).ToArray();
            r.Enabled = false;
            // the effect function alone, all four devices, same look: microseconds per rendered frame
            var layout = LayoutTransform.Identity;
            var geos = sinks.Select(s => layout.Geometry(s)).ToArray();
            var bufs = geos.Select(g => new Rgb[g.Count]).ToArray();
            var ctx = EffectContext.From(r, 100, 45);
            for (int w = 0; w < 200; w++) for (int i = 0; i < geos.Length; i++) Effects.Render(e, ctx.At(100 + w / 60.0), geos[i], bufs[i]);
            var rw = Stopwatch.StartNew(); int reps = 3000;
            for (int w = 0; w < reps; w++) for (int i = 0; i < geos.Length; i++) Effects.Render(e, ctx.At(200 + w / 60.0), geos[i], bufs[i]);
            double usPerFrame = rw.Elapsed.TotalMilliseconds * 1000 / reps;
            rows.Add(new()
            {
                ["effect"] = EffectInfo.Label(e), ["look"] = look?.Name ?? "-", ["seconds"] = Math.Round(el, 1),
                ["renderHz"] = Math.Round(frames / el, 1),
                ["cpuMsPerSecond"] = Math.Round(cpuMs / el, 2), ["cpuPercentOfMachine"] = Math.Round(cpuMs / el / 10.0 / Environment.ProcessorCount, 3),
                ["allocatedKBPerSecond"] = Math.Round(alloc / 1024.0 / el, 1), ["renderMicrosecondsAllDevices"] = Math.Round(usPerFrame, 2),
                ["writesPerSecond"] = new Dictionary<string, double> { ["strip"] = Math.Round(writes[0], 1), ["board"] = Math.Round(writes[1], 1), ["ram"] = Math.Round(writes[2], 1), ["keyboard"] = Math.Round(writes[3], 1) },
                ["privateMB"] = Math.Round(proc.PrivateMemorySize64 / 1048576.0, 1)
            });
            Console.WriteLine($"{EffectInfo.Label(e),-14} render {frames / el,6:0.0}/s  {usPerFrame,6:0.0} us/frame  cpu {cpuMs / el,6:0.00} ms/s ({cpuMs / el / 10.0 / Environment.ProcessorCount:0.000}%)  alloc {alloc / 1024.0 / el,7:0.0} KB/s  writes/s strip {writes[0],5:0.0} board {writes[1],4:0.0} ram {writes[2],4:0.0} kb {writes[3],4:0.0}");
        }
        var doc = new Dictionary<string, object>
        {
            ["build"] = typeof(SceneBench).Assembly.GetName().Version?.ToString() ?? "?", ["processors"] = Environment.ProcessorCount,
            ["stripHz"] = stripHz, ["phaseSeconds"] = seconds, ["rows"] = rows,
            ["limits"] = "Stand-in devices accept frames instantly: no USB/SMBus transfer cost is included (RAM streaming costs CPU in the real bus driver whichever animated effect runs). Process CPU includes the idle process; compare rows, not absolute values."
        };
        if (outPath != null) File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(doc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
