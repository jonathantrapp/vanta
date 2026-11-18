using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HwSuite.Core;

namespace HwSuite.Ui;

/// <summary>
/// Interaction and frame-timing evidence for the real window (offscreen): rapid navigation, page-transition frames, the
/// lighting preview clock, curve dragging through the mouse code path, lost capture, undo, resize and minimize. Writes a
/// JSON summary and a PNG strip of a page transition. Hardware output stays off (no lighting, no fan control).
/// </summary>
public static class MotionCheck
{
    sealed record Stat(string Name, int Samples, double MedianMs, double P95Ms, double MaxMs, double Over25Ms);

    static Stat Stats(string name, IEnumerable<double> xs)
    {
        var a = xs.Where(x => x > 0).OrderBy(x => x).ToArray();
        if (a.Length == 0) return new(name, 0, 0, 0, 0, 0);
        double P(double q) => a[Math.Min(a.Length - 1, (int)Math.Floor(q * (a.Length - 1)))];
        return new(name, a.Length, Math.Round(P(0.5), 2), Math.Round(P(0.95), 2), Math.Round(a[^1], 2), Math.Round(a.Count(x => x > 25) * 100.0 / a.Length, 1));
    }

    static void Pump(int ms)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { Application.Current.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { })); Thread.Sleep(2); }
    }

    public static int Run(MainWindow win, string dir)
    {
        Directory.CreateDirectory(dir);
        var results = new Dictionary<string, object>();
        var checks = new List<(string name, bool ok, string detail)>();
        void Check(string n, bool ok, string d = "") { checks.Add((n, ok, d)); Log.Info($"{(ok ? "PASS" : "FAIL")}  motion: {n} {d}"); }

        // render-loop frame intervals while something is happening
        var frames = new List<double>();
        var clock = Stopwatch.StartNew();
        double last = -1;
        void OnFrame(object? s, EventArgs e) { double now = clock.Elapsed.TotalMilliseconds; if (last >= 0) frames.Add(now - last); last = now; }
        CompositionTarget.Rendering += OnFrame;

        // 0. the render loop's own rate in this environment: on a VANTA page, and on a plain unstyled window
        List<double> RenderRate(int ms)
        {
            var xs = new List<double>(); double lastR = -1; var c = Stopwatch.StartNew();
            void F(object? s, EventArgs e) { double n = c.Elapsed.TotalMilliseconds; if (lastR >= 0) xs.Add(n - lastR); lastR = n; }
            CompositionTarget.Rendering += F; Pump(ms); CompositionTarget.Rendering -= F;
            return xs;
        }
        win.Navigate("system"); Pump(300);
        results["renderLoopVantaPage"] = Stats("render loop, VANTA System page, nothing animating", RenderRate(2000));
        var plain = new Window { Width = 400, Height = 300, Left = win.Left, Top = win.Top, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Content = new TextBlock { Text = "plain" } };
        plain.Show(); Pump(300);
        results["renderLoopPlainWindow"] = Stats("render loop with a plain WPF window shown", RenderRate(2000));
        // the same plain window, with content that changes every frame (forces a present each frame)
        var tb = (TextBlock)plain.Content; int fc = 0;
        void Change(object? s, EventArgs e) => tb.Text = (++fc).ToString();
        CompositionTarget.Rendering += Change;
        results["renderLoopPlainWindowChanging"] = Stats("render loop, plain window whose text changes every frame", RenderRate(2000));
        CompositionTarget.Rendering -= Change;
        plain.Close();
        results["renderTier"] = RenderCapability.Tier >> 16;
        if (Environment.GetEnvironmentVariable("HWSUITE_MOTION_DIAG") == "1")
        {
            win.Navigate("lighting"); Pump(600);
            var lpd = win.LightingView;
            var tk = (PresentationTicker)typeof(LightingPage).GetField("_ticker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(lpd)!;
            var viz = (FrameworkElement)typeof(LightingPage).GetField("_viz", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(lpd)!;
            results["diagLightingTickerOn"] = Stats("lighting page, ticker on", RenderRate(2000));
            tk.Stop(); Pump(200);
            results["diagLightingTickerOff"] = Stats("lighting page, ticker off", RenderRate(2000));
            tk.Start(); viz.Visibility = Visibility.Collapsed; Pump(200);
            results["diagLightingVizCollapsed"] = Stats("lighting page, ticker on, visualizer collapsed", RenderRate(2000));
            viz.Visibility = Visibility.Visible;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            int ticks = 0; timer.Tick += (_, _) => ticks++;
            var sw0 = Stopwatch.StartNew(); timer.Start(); Pump(2000); timer.Stop();
            results["diagDispatcherTimer250"] = $"{ticks} ticks in {sw0.ElapsedMilliseconds} ms";
        }

        // 1. rapid navigation (interrupting transitions)
        var pages = new[] { "dashboard", "fans", "lighting", "system" };
        var navMs = new List<double>();
        for (int i = 0; i < 40; i++)
        {
            var sw = Stopwatch.StartNew();
            win.Navigate(pages[i % 4]);
            Application.Current.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            navMs.Add(sw.Elapsed.TotalMilliseconds);
            Pump(i % 3 == 0 ? 15 : 60);
        }
        win.Navigate("dashboard");
        Pump(400);
        Check("rapid navigation ends on the requested page, fully visible", win.ActivePage is DashboardPage && Math.Abs(win.ActivePage.Opacity - 1) < 0.001, $"(opacity {win.ActivePage.Opacity:0.000})");
        results["navigationDispatchMs"] = Stats("navigate + render dispatch", navMs);

        // 2. page transition frame strip (Dashboard -> Lighting)
        frames.Clear(); last = -1;
        win.Navigate("lighting");
        var strip = new List<BitmapSource>();
        var t0 = Stopwatch.StartNew();
        foreach (var at in new[] { 0, 40, 80, 120, 160, 240 })
        {
            while (t0.ElapsedMilliseconds < at) Pump(2);
            strip.Add(win.Capture());
        }
        results["transitionFrames"] = Stats("render loop during a page transition", frames.ToList());
        SaveStrip(strip, Path.Combine(dir, "transition-strip.png"));

        // 3. lighting preview clock (output off: local preview only, nothing is sent). Its cost is measured as the
        //    difference from the same 3 s spent on a page without a preview (same pumping, same telemetry).
        win.Navigate("system"); Pump(500);
        var b0 = Process.GetCurrentProcess().TotalProcessorTime; var bw = Stopwatch.StartNew();
        Pump(3000);
        double basePct = (Process.GetCurrentProcess().TotalProcessorTime - b0).TotalMilliseconds / bw.Elapsed.TotalMilliseconds * 100 / Environment.ProcessorCount;
        win.Navigate("lighting");
        var lp = win.LightingView;
        Pump(500);
        var ticker = typeof(LightingPage).GetField("_ticker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(lp) as PresentationTicker;
        // direct cost of one preview frame: the update itself, then layout + render dispatch
        var upd = typeof(LightingPage).GetMethod("UpdateVisualizer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var updMs = new List<double>(); var rndMs = new List<double>();
        for (int i = 0; i < 30; i++)
        {
            var s1 = Stopwatch.StartNew(); upd.Invoke(lp, new object[] { i / 60.0 }); updMs.Add(s1.Elapsed.TotalMilliseconds);
            var s2 = Stopwatch.StartNew(); lp.UpdateLayout(); Application.Current.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { })); rndMs.Add(s2.Elapsed.TotalMilliseconds);
        }
        results["previewUpdateMs"] = Stats("preview update (UpdateVisualizer)", updMs);
        results["previewLayoutRenderMs"] = Stats("layout + render dispatch after a preview update", rndMs);
        ticker!.ResetIntervals();
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        Pump(3000);
        double cpuPct = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100 / Environment.ProcessorCount;
        var pi = ticker.IntervalsMs();
        results["previewClock"] = Stats($"lighting preview frames (target {ticker.TargetHz}/s)", pi);
        results["previewProcessCpuPctOfMachine"] = Math.Round(cpuPct, 3);
        results["baselineProcessCpuPctOfMachine"] = Math.Round(basePct, 3);
        results["previewCostPctOfMachine"] = Math.Round(cpuPct - basePct, 3);
        // Control: if a plain window that changes every frame cannot present faster than ~20/s here, the environment limits
        // presentation (seen with the display inactive) and the preview rate cannot be judged: reported, not counted.
        double control = ((Stat)results["renderLoopPlainWindowChanging"]).MedianMs;
        if (control > 50) Log.Warn($"SKIP  motion: lighting preview rate not judged: a plain changing window also presents only every {control:0} ms in this environment ({pi.Length} preview frames in 3 s)");
        else Check("lighting preview runs on its own clock while the page is visible", ticker.Running && pi.Length > 60, $"({pi.Length} frames in 3 s)");
        results["environmentPresentLimited"] = control > 50;
        win.Navigate("system");
        Pump(300);
        Check("the preview clock stops when the Lighting page is left", !ticker.Running);
        win.Navigate("lighting"); Pump(300);
        Check("and restarts when it is shown again", ticker.Running);
        win.WindowState = WindowState.Minimized; Pump(300);
        Check("the preview clock stops while the window is minimized", !ticker.Running);
        win.WindowState = WindowState.Normal; Pump(300);
        Check("and resumes after restore", ticker.Running);

        // 4. curve dragging through the same code path as the mouse
        win.Navigate("fans"); Pump(500);
        var ed = win.Fans.Editor;
        int node = 2;
        var start = ed.NodeValue(node);
        var p0 = ed.NodePixel(node);
        var path = Enumerable.Range(1, 120).Select(i => new Point(p0.X + Math.Sin(i / 120.0 * Math.PI) * 25, p0.Y - i * 0.9)).ToList();
        var stepMs = new List<double>();
        var sw2 = Stopwatch.StartNew();
        ed.SimulateDrag(node, path, () =>
        {
            ed.UpdateLayout();
            Application.Current.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            stepMs.Add(sw2.Elapsed.TotalMilliseconds); sw2.Restart();
        });
        var after = ed.NodeValue(node);
        var target = path[^1];
        var expected = ed.FromPixel(target);
        results["curveDragStepMs"] = Stats("drag step: move + evaluate + layout + render", stepMs);
        Check("the dragged node lands exactly where the pointer is (no easing lag)", Math.Abs(after.p - Math.Round(Math.Clamp(expected.Item2, 0, 100))) < 0.01, $"(node {after.t:0}°C {after.p:0}%, pointer {expected.Item1:0.0}°C {expected.Item2:0.0}%)");
        ed.Undo();
        Check("undo restores the curve from before the drag", ed.NodeValue(node) == start, $"({ed.NodeValue(node)} vs {start})");
        ed.Redo();
        Check("redo re-applies it", ed.NodeValue(node) == after);
        ed.Undo();

        // 5. live theme switching: resources replaced, custom-drawn controls re-rendered, nothing rebuilt
        var themeMs = new List<double>();
        var original = Themes.Current;
        foreach (var t in Themes.All.Concat(new[] { original }))
        {
            foreach (var page in new[] { "dashboard", "fans", "lighting", "system" })
            {
                win.Navigate(page); Pump(120);
                var swt = Stopwatch.StartNew();
                Themes.Apply(t, Application.Current.Resources);
                Application.Current.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
                themeMs.Add(swt.Elapsed.TotalMilliseconds);
            }
        }
        results["themeSwitchMs"] = Stats("theme switch: replace tokens + re-render the visible page", themeMs);
        Check("live theme switch leaves the window on the restored theme's canvas colour",
            win.Background is SolidColorBrush wb && wb.Color == original["Canvas"], $"(median {Stats("", themeMs).MedianMs} ms)");

        // 6. reduced motion: transitions become instant, the preview clock drops to its low rate
        Motion.ReducedOverride = true;
        win.Navigate("dashboard"); Pump(50);
        win.Navigate("lighting");
        Application.Current.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        Check("reduced motion: a page is fully visible immediately after navigation", Math.Abs(win.ActivePage.Opacity - 1) < 0.001, $"(opacity {win.ActivePage.Opacity:0.000})");
        ticker.ResetIntervals(); Pump(2100);
        var ri = ticker.IntervalsMs();
        results["reducedMotionPreview"] = Stats($"preview frames under reduced motion (target {PresentationTicker.ReducedHz}/s)", ri);
        Check($"reduced motion: the preview clock runs at about {PresentationTicker.ReducedHz}/s", ri.Length is >= 4 and <= 12, $"({ri.Length} frames in 2.1 s)");
        Motion.ReducedOverride = null;

        // 7. resize sweep (layout must not throw; lighting switches between one and two columns)
        win.Navigate("lighting");
        foreach (var (w, h) in new[] { (1280.0, 820.0), (1200.0, 760.0), (1600.0, 960.0), (2560.0, 1440.0), (1280.0, 820.0) })
        {
            win.Width = w; win.Height = h; Pump(250);
        }
        Check("resize sweep completed without errors", true);

        CompositionTarget.Rendering -= OnFrame;
        results["allFrames"] = Stats("render loop over the whole run", frames);
        results["checks"] = checks.Select(c => new { c.name, c.ok, c.detail }).ToList();
        File.WriteAllText(Path.Combine(dir, "motion.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Log.Info("motion summary: " + JsonSerializer.Serialize(results.Where(k => k.Key != "checks").ToDictionary(k => k.Key, k => k.Value)));
        int failed = checks.Count(c => !c.ok);
        Log.Info($"motion check finished: {checks.Count - failed} passed, {failed} failed");
        return failed;
    }

    static void SaveStrip(List<BitmapSource> frames, string path)
    {
        double scale = 0.35;
        int fw = (int)(frames[0].PixelWidth * scale), fh = (int)(frames[0].PixelHeight * scale);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            for (int i = 0; i < frames.Count; i++)
            {
                dc.DrawImage(frames[i], new Rect(i * (fw + 6), 0, fw, fh));
                var ft = new FormattedText($"{new[] { 0, 40, 80, 120, 160, 240 }[i]} ms", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiBrushes.Mono, 14, Brushes.White, 1);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), null, new Rect(i * (fw + 6) + 4, 4, ft.Width + 8, ft.Height + 4));
                dc.DrawText(ft, new Point(i * (fw + 6) + 8, 6));
            }
        }
        var rtb = new RenderTargetBitmap(frames.Count * (fw + 6), fh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
