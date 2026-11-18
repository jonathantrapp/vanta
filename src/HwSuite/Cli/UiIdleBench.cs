using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using HwSuite.Core;
using HwSuite.Ui;

namespace HwSuite.Cli;

/// <summary>
/// Measurement only: CPU and memory of the UI under identical simulated-fixture conditions (no hardware is opened, no
/// engine runs, the configuration is read-only). Phases of <c>seconds</c> each: Lighting page shown (Gradient Wave),
/// Dashboard shown, window hidden (tray), window minimised. This file is kept identical in the 1.11.1 comparison build.
/// Limits: it measures the UI process without hardware I/O; it does not establish the overhead on live hardware.
/// </summary>
public static class UiIdleBench
{
    static void Pump(int ms)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end)
        {
            Application.Current.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(15);
        }
    }

    public static int Run(int seconds, string? outPath, string fixture = "lighting-wave")
    {
        ConfigStore.ReadOnly = true;
        var results = new List<Dictionary<string, object>>();
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = new App(true, "Vanta");
                // WPF queues the Startup event in the Application constructor and pumping the dispatcher would raise it:
                // suppress it exactly as the fixture renderer does, so no normal (hardware) start can happen here
                var guard = typeof(App).GetField("_selfTestWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?? throw new InvalidOperationException("startup guard field not found; not running");
                guard.SetValue(app, true);
                var fx = UiFixture.All().First(f => f.Id == fixture);
                var cfg = UiFixture.LoadConfig(null);
                fx.Configure?.Invoke(cfg);
                using var svc = AppServices.ForFixture(cfg, fx, DateTime.Now);
                var win = new MainWindow(svc) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, Width = 1600, Height = 960 };
                win.Show();
                Pump(1500);
                var proc = Process.GetCurrentProcess();
                void Phase(string name, Action setup)
                {
                    setup();
                    Pump(2000);   // settle
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    proc.Refresh();
                    var cpu0 = proc.TotalProcessorTime; int g0 = GC.CollectionCount(0);
                    var sw = Stopwatch.StartNew();
                    Pump(seconds * 1000);
                    proc.Refresh();
                    double el = sw.Elapsed.TotalSeconds, cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
                    long managed = GC.GetTotalMemory(false);
                    results.Add(new()
                    {
                        ["phase"] = name, ["seconds"] = Math.Round(el, 2),
                        ["cpuMsPerSecond"] = Math.Round(cpuMs / el, 2),
                        ["cpuPercentOfMachine"] = Math.Round(cpuMs / el / 10.0 / Environment.ProcessorCount, 3),
                        ["workingSetMB"] = Math.Round(proc.WorkingSet64 / 1048576.0, 1),
                        ["privateMB"] = Math.Round(proc.PrivateMemorySize64 / 1048576.0, 1),
                        ["managedMB"] = Math.Round(managed / 1048576.0, 1),
                        ["gen0PerMinute"] = Math.Round((GC.CollectionCount(0) - g0) * 60 / el, 1),
                        ["handles"] = proc.HandleCount
                    });
                    Console.WriteLine($"{name,-18} cpu {cpuMs / el,7:0.00} ms/s  ws {proc.WorkingSet64 / 1048576.0,6:0.0} MB  private {proc.PrivateMemorySize64 / 1048576.0,6:0.0} MB  managed {managed / 1048576.0,5:0.0} MB");
                }
                Phase("lighting-shown", () => { win.Navigate("lighting"); });
                Phase("dashboard-shown", () => { win.Navigate("dashboard"); });
                Phase("hidden-tray", () => { win.Navigate("lighting"); Pump(500); win.Hide(); });
                Phase("minimised", () => { win.Show(); Pump(500); win.WindowState = WindowState.Minimized; });
                win.ExitRequested = true;
                win.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) { Console.WriteLine("ui-idle-bench failed: " + error); return 1; }
        var doc = new Dictionary<string, object>
        {
            ["build"] = typeof(UiIdleBench).Assembly.GetName().Version?.ToString() ?? "?", ["fixture"] = fixture,
            ["processors"] = Environment.ProcessorCount, ["phaseSeconds"] = seconds, ["phases"] = results,
            ["limits"] = "Simulated fixture, no hardware I/O, engines not running; offscreen window. Not a measure of live hardware overhead."
        };
        if (outPath != null) File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(doc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
