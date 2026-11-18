using System.Diagnostics;
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Hardware;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace HwSuite.Cli;

/// <summary>
/// Owner-watched keyboard transition test on the real iLovbee i87, through the same RgbEngine, KeyboardSink and
/// controller the app uses (keyboard only; every other lighting device is excluded). A small always-on-top window shows
/// each step and asks the owner what the keyboard shows; the answers are the evidence (successful HID writes only prove
/// the keyboard accepted the reports). Stored writes happen only where the app itself would make them (Lights out =
/// brightness 0, the onboard setting), debounced as in the app. Needs KeyboardVerified and KeyboardLiveVerified in the
/// owner's config (read only). Refuses while DeviceDriver.exe runs.
/// </summary>
public static class KeyboardTransitionTest
{
    sealed class Prompt : IDisposable
    {
        readonly Thread _ui;
        WinForms.Form? _form;
        WinForms.Label? _step, _question;
        WinForms.Button[] _buttons = Array.Empty<WinForms.Button>();
        TaskCompletionSource<string>? _answer;
        readonly ManualResetEventSlim _ready = new();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

        public Prompt()
        {
            _ui = new Thread(() =>
            {
                var bg = Drawing.Color.FromArgb(0x12, 0x12, 0x15); var fg = Drawing.Color.FromArgb(0xF4, 0xF4, 0xF5);
                _form = new WinForms.Form
                {
                    Text = "VANTA keyboard test", TopMost = true, FormBorderStyle = WinForms.FormBorderStyle.FixedToolWindow, BackColor = bg, ForeColor = fg,
                    StartPosition = WinForms.FormStartPosition.Manual, Width = 760, Height = 250, ShowInTaskbar = true
                };
                var wa = WinForms.Screen.PrimaryScreen!.WorkingArea;
                _form.Location = new Drawing.Point(wa.Left + (wa.Width - _form.Width) / 2, wa.Top + 20);
                _step = new WinForms.Label { Left = 18, Top = 12, Width = 720, Height = 64, Font = new Drawing.Font("Segoe UI", 12f) };
                _question = new WinForms.Label { Left = 18, Top = 80, Width = 720, Height = 70, Font = new Drawing.Font("Segoe UI Semibold", 15f), ForeColor = Drawing.Color.FromArgb(0x38, 0xBD, 0xF8) };
                _form.Controls.Add(_step); _form.Controls.Add(_question);
                _buttons = new[] { "Yes", "No", "Not sure" }.Select((t, i) =>
                {
                    var b = new WinForms.Button { Text = t, Left = 18 + i * 150, Top = 160, Width = 136, Height = 40, Font = new Drawing.Font("Segoe UI", 11f), Enabled = false, BackColor = Drawing.Color.FromArgb(0x27, 0x27, 0x2A), FlatStyle = WinForms.FlatStyle.Flat };
                    b.Click += (_, _) => _answer?.TrySetResult(b.Text);
                    _form.Controls.Add(b);
                    return b;
                }).ToArray();
                _form.Shown += (_, _) => { ShowWindow(_form.Handle, 5); _ready.Set(); };   // a launcher's "hidden" start flag only affects the first ShowWindow
                WinForms.Application.Run(_form);
            }) { IsBackground = true, Name = "keyboard test prompt" };
            _ui.SetApartmentState(ApartmentState.STA);
            _ui.Start();
            _ready.Wait(5000);
        }

        void Ui(Action a) { try { _form?.Invoke(a); } catch { } }

        public void Show(string step, string text)
        {
            Ui(() => { _step!.Text = step; _question!.Text = text; foreach (var b in _buttons) b.Enabled = false; });
        }

        /// <summary>Ask and wait for the owner's answer (null after the timeout). <paramref name="labels"/>: button texts (default Yes / No / Not sure).</summary>
        public string? Ask(string step, string question, TimeSpan timeout, string[]? labels = null)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _answer = tcs;
            labels ??= new[] { "Yes", "No", "Not sure" };
            Ui(() =>
            {
                _step!.Text = step; _question!.Text = question;
                for (int i = 0; i < _buttons.Length; i++) { _buttons[i].Visible = i < labels.Length; if (i < labels.Length) { _buttons[i].Text = labels[i]; _buttons[i].Width = labels[i].Length > 10 ? 220 : 136; _buttons[i].Left = 18 + i * 236; } _buttons[i].Enabled = true; }
                _form!.Activate();
            });
            bool got = tcs.Task.Wait(timeout);
            Ui(() => { foreach (var b in _buttons) b.Enabled = false; });
            return got ? tcs.Task.Result : null;
        }

        public void Dispose() { Ui(() => _form?.Close()); }
    }

    static readonly Rgb Orange = new(255, 110, 0), Cyan = new(0x38, 0xBD, 0xF8), Indigo = new(0x63, 0x66, 0xF1), Magenta = new(230, 0, 200),
        Yellow = new(255, 220, 0), Green = new(0, 230, 40), Red = new(255, 0, 0);

    public static int Run(bool withReplug, string? jsonOut, bool revertProbe = false)
    {
        var owner = ConfigStore.Load();
        if (!owner.KeyboardVerified || !owner.KeyboardLiveVerified) { Log.Error("keyboard transition test: needs KeyboardVerified and KeyboardLiveVerified in the config"); return 2; }
        using var vendor = new ProcessPresence("DeviceDriver.exe");
        if (vendor.IsRunning) { Log.Error("keyboard transition test: DeviceDriver.exe (iLovbee software) is running; close it first"); return 3; }

        var cfg = new AppConfig { KeyboardVerified = true, KeyboardLiveVerified = true, KeyboardSleepChoice = owner.KeyboardSleepChoice };
        var rgb = cfg.Rgb;
        rgb.Enabled = false; rgb.Keyboard = true; rgb.Motherboard = rgb.DLedStrip = rgb.Ram = rgb.Mouse = false;
        rgb.Speed = 0.6; rgb.Brightness = 1.0; rgb.ColorB = Indigo.ToInt();
        bool liveOn = true;
        string? Own() => vendor.IsRunning ? "the iLovbee keyboard software (DeviceDriver.exe) is running" : null;
        var sink = new KeyboardSink(rgb, new ILovbeeTransport(() => cfg.KeyboardSleepChoice), () => true, Own, null, null, listenForDeviceChanges: true, liveVerified: () => liveOn);
        var engine = new RgbEngine(cfg, () => null);
        engine.Sinks.Add(sink);
        engine.Start();
        var ctl = sink.Controller;
        ctl.StoredWriteOwnership = () => vendor.CheckNow() ? "the iLovbee keyboard software (DeviceDriver.exe) is running" : null;
        Log.Info($"keyboard transition test: vendor detection {vendor.Mode}");
        var results = new List<object>();
        var sw = Stopwatch.StartNew();
        int failures = 0;
        using var prompt = new Prompt();

        object Counters() => new { storedWrites = ctl.Applies, liveFrames = ctl.LiveFrames, liveFailures = ctl.LiveFailures, storedFailures = ctl.Failures, reopens = ctl.Reopens, reconnects = ctl.Reconnects, ownershipChecks = ctl.OwnershipChecks, status = ctl.Status, link = ctl.LinkStatus };
        void Set(EffectKind e, Rgb a, bool enabled = true) { lock (cfg.Sync) { rgb.ColorA = a.ToInt(); rgb.Effect = e; rgb.Enabled = enabled; } }
        string? Step(string id, string what, double settle, string question, string expect = "Yes")
        {
            string head = $"Step {id}: {what}";
            Log.Info($"STEP {id} at t+{sw.Elapsed.TotalSeconds:0.0}s: {what}");
            prompt.Show(head, "Watch the keyboard...");
            Thread.Sleep(TimeSpan.FromSeconds(settle));
            var c0 = Counters();
            var ans = prompt.Ask(head, question, TimeSpan.FromSeconds(120));
            bool pass = ans == expect;
            if (!pass) failures++;
            Log.Info($"STEP {id}: '{question}' -> owner: {ans ?? "no answer"} (expected {expect}); {JsonSerializer.Serialize(c0)}");
            results.Add(new { step = id, what, question, expected = expect, answer = ans, pass, atSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1), counters = c0 });
            return ans;
        }

        try
        {
            prompt.Show("Keyboard transition test", "Starting in 5 s. Keep your eyes on the keyboard; answer each question here.");
            Thread.Sleep(5000);
            if (revertProbe)
            {
                // P1-P3: what live frames do over a stored brightness 0, and how long the keyboard holds the last live frame
                Set(EffectKind.Stealth, Orange);
                Step("P1", "Lights out: the stored brightness 0", 4, "Did the keyboard go DARK?");
                ctl.DeferStoredWrites = true;   // no stored fallback: live frames run over the stored brightness 0
                Set(EffectKind.Static, Orange);
                Step("P2", "Static ORANGE as live frames ONLY (the stored setting is still brightness 0)", 3, "Is the keyboard ORANGE?");
                Set(EffectKind.Static, Orange, enabled: false);   // frames stop; nothing stored
                var stopped = sw.Elapsed.TotalSeconds;
                Log.Info($"STEP P3 at t+{stopped:0.0}s: live frames stopped (stored setting still brightness 0)");
                var ans = prompt.Ask("Step P3: frames stopped", "Frames just STOPPED. Press the button the moment the keyboard changes by itself (e.g. goes dark).", TimeSpan.FromSeconds(45),
                    new[] { "It changed NOW", "Nothing changed (45 s)" });
                double held = sw.Elapsed.TotalSeconds - stopped;
                Log.Info($"STEP P3: owner: {ans ?? "no answer"} after {held:0.00} s");
                results.Add(new { step = "P3", what = "live frames stopped over stored brightness 0", answer = ans, secondsUntilAnswer = Math.Round(held, 2) });
                Step("P4", "after the change", 0, "Is the keyboard DARK now (its stored brightness 0)?");
                ctl.DeferStoredWrites = false;
                Set(EffectKind.Static, Orange);
                Step("P5", "Static ORANGE live frames with the stored fallback written again", 4, "Is the keyboard ORANGE?");
                Set(EffectKind.Static, Orange, enabled: false);
                var stopped2 = sw.Elapsed.TotalSeconds;
                var ans2 = prompt.Ask("Step P6: frames stopped", "Frames STOPPED again; the stored setting is now orange. Press the button if the keyboard changes (it should stay orange).", TimeSpan.FromSeconds(15),
                    new[] { "It changed", "Still orange" });
                results.Add(new { step = "P6", what = "frames stopped over the stored orange fallback", answer = ans2 ?? "no answer (15 s)", secondsUntilAnswer = Math.Round(sw.Elapsed.TotalSeconds - stopped2, 2) });
                Log.Info($"STEP P6: owner: {ans2 ?? "no answer (15 s, unchanged)"}");
                if (ans2 == "It changed") failures++;
                prompt.Show("Done", "Finishing: your own effect is restored (output off: the keyboard keeps its stored onboard look).");
                goto done;
            }
            Set(EffectKind.Static, Orange);
            Step("1", "Static ORANGE, sent as live frames", 3, "Is the whole keyboard solid ORANGE?");
            Set(EffectKind.Stealth, Orange);
            Step("2", "Lights out (the keyboard's stored brightness 0, written once after the 1.5 s settle time)", 4, "Did the keyboard go DARK?");
            Set(EffectKind.Static, Orange);
            Step("3", "Back to Static ORANGE: the SAME colour as step 1 (the reported bug)", 3, "Is the keyboard ORANGE again?");
            Set(EffectKind.Stealth, Orange);
            Step("4", "Lights out again", 4, "Did the keyboard go DARK?");
            Set(EffectKind.Breathing, Cyan);
            Step("5", "Breathing CYAN (live frames, about 2 s per breath)", 6, "Is the keyboard breathing CYAN (bright, dim, bright)?");
            Set(EffectKind.Stealth, Cyan);
            Step("6", "Lights out again", 4, "Did the keyboard go DARK?");
            Set(EffectKind.GradientWave, Cyan);
            Step("7", "Gradient Wave cyan/indigo (live frames)", 6, "Is a cyan/indigo gradient moving LEFT TO RIGHT across the keys?");
            liveOn = false;
            Set(EffectKind.Breathing, Magenta);
            Step("8", "Onboard only: the keyboard's own stored Breathing, MAGENTA (one stored write)", 5, "Is the keyboard breathing MAGENTA on its own?");
            liveOn = true;
            Set(EffectKind.Static, Magenta);
            Step("9", "Live again: Static MAGENTA as live frames", 3, "Is the keyboard now STEADY magenta (no breathing)?");
            Set(EffectKind.Static, Yellow);
            Thread.Sleep(1500);
            Set(EffectKind.Static, Yellow, enabled: false);
            Step("10", "Output OFF right after choosing YELLOW: the keyboard falls back to its stored setting, written at the stop", 6, "Is the keyboard steady YELLOW?");
            prompt.Show("Step 11: no frames for 30 s", "Keep watching: does the yellow stay, or does the keyboard go back to something else by itself?");
            Thread.Sleep(30000);
            Step("11", "30 s without any frames", 0, "After 30 s with nothing sent, is it STILL steady yellow?");
            Set(EffectKind.Breathing, Green);
            Step("12", "Output ON again: Breathing GREEN", 5, "Is the keyboard breathing GREEN?");
            if (withReplug)
            {
                Set(EffectKind.Static, Red);
                Thread.Sleep(2000);
                long rc0 = ctl.Reconnects, ro0 = ctl.Reopens;
                prompt.Show("Step 13: unplug and reconnect", "UNPLUG the keyboard's USB cable now, wait 3 s, then plug it back in.");
                Log.Info("STEP 13: waiting for the owner to unplug and reconnect the keyboard");
                var deadline = DateTime.UtcNow.AddSeconds(120);
                while (DateTime.UtcNow < deadline && ctl.Reconnects == rc0 && ctl.Reopens == ro0) Thread.Sleep(200);
                Log.Info($"STEP 13: reconnects {ctl.Reconnects - rc0}, reopens {ctl.Reopens - ro0} after {sw.Elapsed.TotalSeconds:0}s");
                Step("13", "Static RED, after the keyboard was unplugged and plugged back in", 4, "Is the keyboard solid RED again?");
            }
            prompt.Show("Done", "Finishing: output off, the keyboard is left on your own primary colour.");
        }
        catch (Exception ex) { Log.Error("keyboard transition test", ex); failures++; }
        finally
        {
            // end on the owner's own look: their effect and colours, stored as the keyboard's fallback, then output off
            ctl.DeferStoredWrites = false; liveOn = true;
            lock (cfg.Sync) { rgb.ColorA = owner.Rgb.ColorA; rgb.ColorB = owner.Rgb.ColorB; rgb.Speed = owner.Rgb.Speed; rgb.Brightness = owner.Rgb.Brightness; rgb.Effect = owner.Rgb.Effect == EffectKind.Stealth ? EffectKind.Static : owner.Rgb.Effect; rgb.Enabled = true; }
            Thread.Sleep(3500);
            lock (cfg.Sync) rgb.Enabled = false;
            Thread.Sleep(800);
            engine.Dispose();
        }
    done:
        var final = Counters();
        Log.Info($"keyboard transition test{(revertProbe ? " (revert probe)" : "")}: {results.Count} answers, {failures} not as expected; {JsonSerializer.Serialize(final)}");
        try
        {
            if (jsonOut != null)
                File.WriteAllText(jsonOut, JsonSerializer.Serialize(new { at = DateTime.Now, kind = "keyboard-transition", vendorDetection = vendor.Mode, results, final, failures }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Error("could not write " + jsonOut, ex); }
        return failures == 0 ? 0 : 1;
    }
}
