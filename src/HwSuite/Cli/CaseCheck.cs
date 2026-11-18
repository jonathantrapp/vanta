using HwSuite.Core;
using HwSuite.Lighting;

namespace HwSuite.Cli;

/// <summary>
/// --case-check [seconds]: shows the case layout check on the real fans while VANTA is closed (attended; run through the
/// hardware-test runner, which stops the Gigabyte services first). Only the D_LED strip is driven: the splitter-hub
/// rainbow (LED 1 red, then orange, yellow, green, cyan, blue, purple, pink) on the first 8 LEDs, the rest off. Reads the
/// owner's settings for the byte order and LED count; writes nothing to them. The strip is left on a dim neutral colour.
/// </summary>
public static class CaseCheck
{
    public static int Run(int seconds, bool two = false)
    {
        seconds = Math.Clamp(seconds, 5, 60);
        var owner = ConfigStore.Load();
        var cfg = new AppConfig { AutoStopGigabyte = false };   // the runner stops and restores the services
        var rgb = cfg.Rgb;
        rgb.Enabled = false; rgb.DLedStrip = true; rgb.Motherboard = false; rgb.Ram = false; rgb.Mouse = false; rgb.Keyboard = false;
        rgb.DLedCount = Math.Clamp(owner.Rgb.DLedCount, 8, 120); rgb.StripOrder = owner.Rgb.StripOrder; rgb.DLedMaxHz = 60;
        rgb.Effect = EffectKind.Static; rgb.ColorA = 0x101010; rgb.Brightness = 1;
        var layout = new CaseLayout { Mirrored = true, LedsPerFan = 8, LogoLeds = 3 };
        var pattern = layout.CheckPattern(rgb.DLedCount);
        if (two)
        {
            // easier to remember: LED 1 red, LED 2 blue, everything else off
            pattern = new Rgb[rgb.DLedCount];
            pattern[0] = new Rgb(255, 0, 0); pattern[1] = new Rgb(0, 0, 255);
        }
        Log.Info(two ? $"case check: {seconds} s, LED 1 red and LED 2 blue, the rest off ({rgb.StripOrder})"
            : $"case check: {seconds} s rainbow on LEDs 1-8 of {rgb.DLedCount} ({rgb.StripOrder}); expected order red, orange, yellow, green, cyan, blue, purple, pink");

        using var app = new AppServices(cfg);
        app.InitializeHardware();
        app.InitializeLighting(allowRamRemap: false);
        var strip = app.Rgb.Sinks.OfType<DLedSink>().FirstOrDefault();
        if (strip?.Available != true) { Log.Error("case check: D_LED controller unavailable"); return 3; }
        app.Rgb.Start();
        Thread.Sleep(2500);
        var own = app.Ownership!.Check(Hardware.HwResource.RgbUsb);
        if (!own.MayWrite) { Log.Error("case check: IT5701 not owned: " + own.Reason); return 4; }
        rgb.Enabled = true;
        Thread.Sleep(800);
        strip.ShowTestPattern(pattern, TimeSpan.FromSeconds(seconds));
        for (int s = 0; s < seconds; s++)
        {
            // a static effect sends one frame; repost so the pattern is held even if the controller drops one
            Thread.Sleep(1000);
            strip.Post(new Rgb[rgb.DLedCount]);
        }
        Log.Info($"case check: done; strip sent {strip.FramesSent} frames, {strip.Errors} errors");
        rgb.Enabled = false;
        Thread.Sleep(800);
        return strip.Errors == 0 ? 0 : 5;
    }
}
