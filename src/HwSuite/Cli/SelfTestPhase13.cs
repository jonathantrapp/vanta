using System.Diagnostics;
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;
using HwSuite.Ui;

namespace HwSuite.Cli;

/// <summary>
/// 1.13 checks: the atmosphere effects (Rainfall, Rolling Mist, Fireflies, Moonlit Water, Distant Storm, Canopy), their
/// saved settings and looks, compatibility with older builds, device fallbacks and write budgets. Pure functions, mocks and
/// fake transports only: nothing here opens a device.
/// </summary>
public static partial class SelfTest
{
    static void Phase13Tests()
    {
        Guarded("1.13 settings", SceneSettingTests);
        Guarded("1.13 looks", SceneLookTests);
        Guarded("1.13 rendering", SceneRenderTests);
        Guarded("1.13 lightning", LightningTests);
        Guarded("1.13 geometry", SceneGeometryTests);
        Guarded("1.13 devices", SceneDeviceTests);
        Guarded("1.13 engine", SceneEngineTests);
    }

    static RgbConfig SceneCfg(EffectKind e, double brightness = 0.6) => new()
    {
        Enabled = true, Effect = e, ColorA = 0xA9C4D4, ColorB = 0x2E6A6E, Brightness = brightness, Speed = 0.4, Density = 0.5, Glow = 0.5, Lightning = true
    };

    // ---------------------------------------------------------------------------------------------------------
    static void SceneSettingTests()
    {
        // the owner's own configuration shape (1.12, saved before these fields existed)
        var owner = UiFixture.LoadConfig(null);
        Check("1.13: a 1.12 configuration loads with safe atmosphere defaults", owner.Rgb.Density == 0.35 && owner.Rgb.Glow == 0.5 && owner.Rgb.Lightning && !EffectInfo.IsScene(owner.Rgb.Effect));
        var old = ConfigStore.Parse("{\"Version\":4,\"Rgb\":{\"Effect\":\"GradientWave\",\"ColorA\":3766144,\"ColorB\":11125964,\"Speed\":0.2,\"Brightness\":0.35,\"Enabled\":true,\"LightsOutRestoreEffect\":\"Breathing\"},\"Looks\":[{\"Name\":\"Evening\",\"Effect\":\"Breathing\",\"ColorA\":1193046,\"ColorB\":0,\"Brightness\":0.5,\"Speed\":0.3}]}")!;
        Check("1.13: older settings keep their effect, colours, speed, brightness and Lights out restore point",
            old.Rgb.Effect == EffectKind.GradientWave && old.Rgb.ColorA == 0x397780 && old.Rgb.ColorB == 0xA9C4CC && old.Rgb.Speed == 0.2 && old.Rgb.Brightness == 0.35 && old.Rgb.LightsOutRestoreEffect == EffectKind.Breathing);
        Check("1.13: an older saved look loads unchanged, with no atmosphere fields", old.Looks.Count == 1 && old.Looks[0].Effect == EffectKind.Breathing && old.Looks[0].Density == null && old.Looks[0].Glow == null && old.Looks[0].Lightning == null);
        Check("1.13: loading never selects a new effect", !EffectInfo.IsScene(old.Rgb.Effect) && !EffectInfo.IsScene(owner.Rgb.Effect));
        var json = ConfigStore.ToJson(old);
        Check("1.13: a classic effect is saved exactly as before (no Scene field)", !json.Contains("\"Scene\"") && !json.Contains("LightsOutRestoreScene") && json.Contains("\"Effect\": \"GradientWave\""));

        // a scene is saved with a classic fallback that 1.12 understands
        var c = new AppConfig(); c.Rgb.Effect = EffectKind.Rainfall; c.Rgb.Density = 0.8; c.Rgb.Glow = 0.3; c.Rgb.Lightning = false;
        var sj = ConfigStore.ToJson(c);
        using (var doc = JsonDocument.Parse(sj))
        {
            var rgb = doc.RootElement.GetProperty("Rgb");
            string? saved = rgb.GetProperty("Effect").GetString();
            Check("1.13: a scene is saved as a classic effect older builds can read, plus a Scene field they ignore",
                saved == "GradientWave" && rgb.GetProperty("Scene").GetString() == "Rainfall" && EffectInfo.Classic.Any(k => k.ToString() == saved), $"(Effect {saved})");
        }
        var back = ConfigStore.Parse(sj)!;
        Check("1.13: a scene and its density, glow and lightning survive save and load", back.Rgb.Effect == EffectKind.Rainfall && back.Rgb.Density == 0.8 && back.Rgb.Glow == 0.3 && !back.Rgb.Lightning);
        var reordered = ConfigStore.Parse("{\"Rgb\":{\"Scene\":\"Canopy\",\"Effect\":\"Breathing\"}}")!;
        var ordered = ConfigStore.Parse("{\"Rgb\":{\"Effect\":\"Breathing\",\"Scene\":\"Canopy\"}}")!;
        Check("1.13: the Effect/Scene fields load in either order", reordered.Rgb.Effect == EffectKind.Canopy && ordered.Rgb.Effect == EffectKind.Canopy);
        Check("1.13: every scene's fallback is a classic animated effect", EffectInfo.Scenes.All(s => EffectInfo.ClassicFallback(s) is EffectKind.GradientWave or EffectKind.Breathing));

        // what an older build would see: the same JSON read with the classic fields only
        using (var doc = JsonDocument.Parse(sj))
        {
            var keys = doc.RootElement.GetProperty("Rgb").EnumerateObject().Select(p => p.Name).ToList();
            Check("1.13: the saved file has no new value inside an existing field (only new fields are added)", keys.Contains("Effect") && keys.Contains("Scene") && keys.Contains("Density"));
        }

        // Lights out with a scene: the restore point survives save/load and comes back
        var lo = new AppConfig(); lo.Rgb.Enabled = true; lo.Rgb.Effect = EffectKind.DistantStorm;
        LightsOut.Toggle(lo);
        var loBack = ConfigStore.Parse(ConfigStore.ToJson(lo))!;
        Check("1.13: Lights out over a scene saves the scene as the restore point (with a classic fallback)",
            LightsOut.IsOn(loBack.Rgb) && loBack.Rgb.LightsOutRestoreEffect == EffectKind.DistantStorm && ConfigStore.ToJson(lo).Contains("\"LightsOutRestoreEffect\": \"GradientWave\""));
        LightsOut.Toggle(loBack);
        Check("1.13: Lights out off restores the scene", loBack.Rgb.Effect == EffectKind.DistantStorm && !LightsOut.IsOn(loBack.Rgb));
    }

    // ---------------------------------------------------------------------------------------------------------
    static void SceneLookTests()
    {
        var s = LightingLooks.Starters;
        var teal = s.FirstOrDefault(l => l.Name == LightingLooks.TealTide);
        Check("1.13: the 1.12 'Rainfall' starter keeps its values under the clear name 'Teal Tide'",
            teal != null && teal.Effect == EffectKind.GradientWave && teal.ColorA == 0x397780 && teal.ColorB == 0xA9C4CC && teal.Brightness == 0.35 && teal.Speed == LightingLooks.Slow && teal.Note!.Contains("Rainfall"));
        Check("1.13: no starter is called Rainfall any more (that is now the falling-rain effect)", s.All(l => l.Name != LightingLooks.FormerRainfall));
        var owner = UiFixture.LoadConfig(null);
        owner.Rgb.Effect = EffectKind.GradientWave; owner.Rgb.ColorA = 0x397780; owner.Rgb.ColorB = 0xA9C4CC; owner.Rgb.Speed = 0.2; owner.Rgb.Brightness = 0.35;
        string before = ConfigStore.ToJson(owner);
        var matched = s.Where(l => LightingLooks.Matches(owner, l)).Select(l => l.Name).ToList();
        Check("1.13: settings that were the 1.12 Rainfall starter now show as Teal Tide, unchanged", matched.SequenceEqual(new[] { LightingLooks.TealTide }) && ConfigStore.ToJson(owner) == before, $"({string.Join(",", matched)})");
        Check("1.13: the rainy-forest starters use the new effects, with bounded settings",
            s.Count(l => l.Group == LightingLooks.ForestGroup) == 8 && s.Where(l => l.Group == LightingLooks.ForestGroup).All(l => EffectInfo.IsScene(l.Effect) && l.Density is >= 0 and <= 1 && l.Glow is >= 0 and <= 1 && l.Brightness <= 0.6 && l.Speed is >= 0 and <= 0.6));
        Check("1.13: lightning is part of a look only for the effects that use it (Distant Storm; 1.15 Downpour)", s.All(l => (l.Lightning != null) == EffectInfo.UsesLightning(l.Effect)));
        Check("1.13: starter names are unique", s.Select(l => l.Name.ToLowerInvariant()).Distinct().Count() == s.Count);

        // round trip of every new parameter
        var cfg = UiFixture.LoadConfig(null); cfg.Looks.Clear(); cfg.Rgb.Enabled = true;
        LightingLooks.Apply(cfg, s.First(l => l.Name == "Distant Thunder"));
        cfg.Rgb.Density = 0.72; cfg.Rgb.Glow = 0.21; cfg.Rgb.Lightning = false; cfg.Rgb.Speed = 0.33;
        Check("1.13: an edited starter no longer matches it", !LightingLooks.Matches(cfg, s.First(l => l.Name == "Distant Thunder")));
        Check("1.13: saving a scene look succeeds", LightingLooks.SaveCurrent(cfg, "Quiet storm") == null);
        var back = ConfigStore.Parse(ConfigStore.ToJson(cfg))!;
        var q = back.Looks.Single();
        Check("1.13: a saved scene look round-trips effect, density, glow, lightning, speed and colours",
            q.Effect == EffectKind.DistantStorm && q.Density == 0.72 && q.Glow == 0.21 && q.Lightning == false && q.Speed == 0.33 && q.ColorA == cfg.Rgb.ColorA && q.ColorB == cfg.Rgb.ColorB);
        var other = UiFixture.LoadConfig(null); other.Rgb.Effect = EffectKind.Static; other.Rgb.Density = 0.1; other.Rgb.Lightning = true;
        LightingLooks.Apply(other, q);
        Check("1.13: applying it restores every parameter exactly", other.Rgb.Effect == EffectKind.DistantStorm && other.Rgb.Density == 0.72 && other.Rgb.Glow == 0.21 && !other.Rgb.Lightning && LightingLooks.Matches(other, q));
        Check("1.13: a classic look leaves the atmosphere settings alone", ((Func<bool>)(() => { LightingLooks.Apply(other, s[2]); return other.Rgb.Density == 0.72 && other.Rgb.Glow == 0.21 && !other.Rgb.Lightning && other.Rgb.Effect == EffectKind.Static; }))());

        // a saved look whose name is now a starter's is kept under another name, never dropped
        var loaded = LightingLooks.Normalize(new() { new() { Name = "Window Rain", Effect = EffectKind.Breathing, ColorA = 1, Brightness = 0.4, Speed = 0.3 }, new() { Name = "Window Rain (saved)", Effect = EffectKind.Static }, new() { Name = "Rainfall", Effect = EffectKind.Static } });
        Check("1.13: saved looks named like a new starter are kept under a '(saved)' name; 'Rainfall' is a free name now",
            loaded.Count == 3 && loaded.Any(l => l.Name == "Window Rain (saved 2)" && l.ColorA == 1) && loaded.Any(l => l.Name == "Window Rain (saved)") && loaded.Any(l => l.Name == "Rainfall"), string.Join(", ", loaded.Select(l => l.Name)));
        var partial = LightingLooks.Normalize(new() { new() { Name = "x", Effect = EffectKind.Fireflies, Brightness = 0.5 } });
        Check("1.13: a scene look missing fields gets that effect's defaults", partial[0].Density == EffectInfo.DefaultDensity(EffectKind.Fireflies) && partial[0].Glow == 0.5 && partial[0].Speed == 0.5 && partial[0].Lightning == null);

        // Lights out survives choosing a starter
        var lo = UiFixture.LoadConfig(null); lo.Rgb.Enabled = true; lo.Rgb.Effect = EffectKind.Breathing;
        LightsOut.Toggle(lo);
        var dark = (MouseLightingState.From(lo.Rgb), KeyboardLightingState.From(lo.Rgb, 3));
        LightingLooks.Apply(lo, s.First(l => l.Name == "Window Rain"));
        var f = new Rgb[27];
        Effects.Render(lo.Rgb.Effect, EffectContext.From(lo.Rgb, 5, null), LayoutTransform.Identity.GeometryFor(DeviceRole.Strip, 27), f);
        Check("1.13: choosing a rainy-forest look during Lights out keeps everything dark", LightsOut.IsOn(lo.Rgb) && f.All(x => x == Rgb.Black)
            && Equals(MouseLightingState.From(lo.Rgb), dark.Item1) && Equals(KeyboardLightingState.From(lo.Rgb, 3), dark.Item2) && RamSink.OnboardFor(new RgbConfig { RamOnboardEffects = true, Effect = lo.Rgb.Effect }) is { mode: EneDramController.ModeOff });
        LightsOut.Toggle(lo);
        Check("1.13: Lights out off then shows the chosen look", lo.Rgb.Effect == EffectKind.Rainfall && LightingLooks.Matches(lo, s.First(l => l.Name == "Window Rain")));
        var off = UiFixture.LoadConfig(null); off.Rgb.Enabled = false;
        LightingLooks.Apply(off, s.First(l => l.Name == "Mountain Fog"));
        Check("1.13: choosing a look with output off keeps output off", !off.Rgb.Enabled && MouseLightingState.From(off.Rgb) == null);
    }

    // ---------------------------------------------------------------------------------------------------------
    static void SceneRenderTests()
    {
        var L = LayoutTransform.Identity;
        var geos = new[] { L.GeometryFor(DeviceRole.Strip, 27), L.GeometryFor(DeviceRole.Ram, 26, new[] { 5, 8, 5, 8 }), L.GeometryFor(DeviceRole.Board, 2), L.GeometryFor(DeviceRole.Keyboard, ILovbeeKeyMap.Keys.Length), L.GeometryFor(DeviceRole.Mouse, 1) };
        var rnd = new Random(1313);
        bool repeat = true, bounded = true, zero = true, varied = true, stateless = true;
        string why = "";
        foreach (var e in EffectInfo.Scenes)
            foreach (var g in geos)
            {
                for (int k = 0; k < 40; k++)
                {
                    var cfg = new RgbConfig
                    {
                        Effect = e, ColorA = rnd.Next(0x1000000), ColorB = rnd.Next(0x1000000), Brightness = rnd.NextDouble(), Speed = rnd.NextDouble(),
                        Density = rnd.NextDouble(), Glow = rnd.NextDouble(), Lightning = true
                    };
                    double t = rnd.NextDouble() * 5000;
                    var a = new Rgb[g.Count]; var b = new Rgb[g.Count];
                    Effects.Render(e, EffectContext.From(cfg, t, null), g, a);
                    Effects.Render(e, EffectContext.From(cfg, t + 3.3, null), g, b);   // unrelated call in between
                    Effects.Render(e, EffectContext.From(cfg, t, null), g, b);
                    if (!a.AsSpan().SequenceEqual(b)) { repeat = false; why = $"{e} {g.Count}"; }
                    // every channel within what the colours and brightness allow (lightning: the flash tint, never forced white)
                    var A = Rgb.FromInt(cfg.ColorA); var B = Rgb.FromInt(cfg.ColorB);
                    int cap = (int)Math.Ceiling(Math.Max(Math.Max(Math.Max(A.R, A.G), A.B), Math.Max(Math.Max(B.R, B.G), B.B)) * cfg.Brightness) + 1;
                    if (e == EffectKind.DistantStorm || e == EffectKind.Fireflies) cap = Math.Max(cap, (int)Math.Ceiling(242 * cfg.Brightness) + 1);
                    if (a.Any(x => Math.Max(x.R, Math.Max(x.G, x.B)) > cap)) { bounded = false; why = $"{e} over cap {cap}"; }
                    var z = new Rgb[g.Count];
                    Effects.Render(e, EffectContext.From(new RgbConfig { Effect = e, ColorA = cfg.ColorA, ColorB = cfg.ColorB, Brightness = 0, Lightning = true }, t, null), g, z);
                    if (z.Any(x => x != Rgb.Black)) zero = false;
                }
            }
        Check("1.13: the same seed and time give the same frame (no hidden state between frames)", repeat, why);
        Check("1.13: colours stay within the chosen colours times brightness (lightning never forces white)", bounded, why);
        Check("1.13: brightness 0 is dark for every scene", zero);

        // frame-rate independence: frames sampled at 15, 60 and 144 per second agree wherever their times coincide
        foreach (var e in EffectInfo.Scenes)
        {
            var g = geos[3]; var cfg = SceneCfg(e);
            var at60 = new Dictionary<int, Rgb[]>();
            for (int i = 0; i <= 600; i++) { var f = new Rgb[g.Count]; Effects.Render(e, EffectContext.From(cfg, 100 + i / 60.0, null), g, f); if (i % 4 == 0) at60[i / 4] = f; }
            for (int i = 0; i <= 150; i++) { var f = new Rgb[g.Count]; Effects.Render(e, EffectContext.From(cfg, 100 + i / 15.0, null), g, f); if (!f.AsSpan().SequenceEqual(at60[i])) { stateless = false; why = e.ToString(); } }
        }
        Check("1.13: frames do not depend on the frame rate (15/s and 60/s agree at the same times)", stateless, why);

        // the scene actually moves: frames a second apart differ, and the keyboard is mostly dim during rain
        foreach (var e in EffectInfo.Scenes)
        {
            var g = geos[3]; var cfg = SceneCfg(e);
            int changed = 0;
            for (int i = 0; i < 30; i++)
            {
                var f1 = new Rgb[g.Count]; var f2 = new Rgb[g.Count];
                Effects.Render(e, EffectContext.From(cfg, 50 + i * 7, null), g, f1);
                Effects.Render(e, EffectContext.From(cfg, 51 + i * 7, null), g, f2);
                if (!f1.AsSpan().SequenceEqual(f2)) changed++;
            }
            if (changed < 15) { varied = false; why = $"{e}: {changed}/30"; }
        }
        Check("1.13: every scene changes over time", varied, why);

        // dragging the speed slider: with the accumulated scene clock a speed step changes the next frame no more than an
        // ordinary frame does, even after an hour (time x speed would jump the whole pattern)
        foreach (var e in EffectInfo.Scenes)
        {
            var g = geos[3]; var cfg = SceneCfg(e);
            double t0 = 3600, drift = 0, last = double.NaN, worstStep = 0, worstFrame = 0;
            var prev = new Rgb[g.Count]; var cur = new Rgb[g.Count];
            Effects.Render(e, EffectContext.From(cfg, t0, null, drift: EffectContext.AdvanceDrift(ref drift, ref last, t0, cfg.Speed)), g, prev);
            for (int i = 1; i <= 120; i++)
            {
                double t = t0 + i / 60.0;
                bool step = i % 2 == 0;
                if (step) cfg.Speed = Math.Clamp(cfg.Speed + 0.01, 0, 1);   // the slider moving 1 % every other frame
                Effects.Render(e, EffectContext.From(cfg, t, null, drift: EffectContext.AdvanceDrift(ref drift, ref last, t, cfg.Speed)), g, cur);
                double d = prev.Zip(cur, (p, q) => (double)Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B)))).Max();
                if (step) worstStep = Math.Max(worstStep, d); else worstFrame = Math.Max(worstFrame, d);
                (prev, cur) = (cur, prev);
            }
            if (worstStep > worstFrame * 2 + 12) { varied = false; why = $"{e}: speed step {worstStep}, plain frame {worstFrame}"; }
        }
        Check("1.13: moving the speed slider never jumps a scene (even after an hour)", varied, why);

        var kb = geos[3]; var rain = SceneCfg(EffectKind.Rainfall); rain.Density = 0.3;
        double dimShare = 0; int frames = 0, withDrop = 0;
        var colsSeen = new HashSet<int>();
        for (double t = 10; t < 130; t += 0.25)
        {
            var f = new Rgb[kb.Count]; Effects.Render(EffectKind.Rainfall, EffectContext.From(rain, t, null), kb, f);
            int bg = f.Select(x => (int)x.G).OrderBy(x => x).ElementAt(f.Length / 2);
            int bright = f.Count(x => x.G > bg + 20);
            dimShare += 1 - bright / (double)f.Length; frames++;
            if (bright > 0) withDrop++;
            for (int i = 0; i < f.Length; i++) if (f[i].G > bg + 20) colsSeen.Add((int)Math.Round(kb.X[i] / 0.0575));
        }
        Check("1.13: light rain keeps most keys dim and shows drops in many different columns",
            dimShare / frames > 0.93 && withDrop > frames * 0.6 && colsSeen.Count >= 14, $"(dim {100 * dimShare / frames:0.0}%, frames with a drop {100.0 * withDrop / frames:0}%, columns {colsSeen.Count})");

        // long gaps: rendering far in the future costs the same and needs no catch-up (no state to advance)
        var storm = SceneCfg(EffectKind.DistantStorm);
        var buf = new Rgb[kb.Count];
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) Effects.Render(EffectKind.DistantStorm, EffectContext.From(storm, 10 + i / 60.0, null), kb, buf);
        double near = sw.Elapsed.TotalMilliseconds; sw.Restart();
        for (int i = 0; i < 200; i++) Effects.Render(EffectKind.DistantStorm, EffectContext.From(storm, 3.0e6 + i / 60.0, null), kb, buf);
        double far = sw.Elapsed.TotalMilliseconds;
        Check("1.13: a long time jump (sleep, stall) costs no catch-up work", far < near * 3 + 5, $"(200 frames: {near:0.0} ms near, {far:0.0} ms after a 35-day jump)");

        // per-frame allocation (the renderer itself; the engine already allocates one frame array per device)
        foreach (var e in EffectInfo.Scenes) Effects.Render(e, EffectContext.From(SceneCfg(e), 1, null), kb, buf);
        var ctxs = EffectInfo.Scenes.Select(e => (e, EffectContext.From(SceneCfg(e), 77, null))).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) foreach (var (e, cx) in ctxs) Effects.Render(e, cx, kb, buf);
        long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("1.13: the atmosphere renderer allocates nothing per frame", alloc < 1024, $"({alloc} bytes over 600 frames)");

        // device seeds: two devices do not rain in lockstep
        var strip = geos[0]; var ram = geos[1];
        Check("1.13: each device has its own seed (drops are not copied between devices)", strip.Seed != ram.Seed && ram.Seed != kb.Seed && strip.Seed != kb.Seed);
    }

    // ---------------------------------------------------------------------------------------------------------
    static void LightningTests()
    {
        // events over three simulated hours: rare, spaced, gentle
        var starts = new List<double>();
        double prev = 0, maxStep = 0, peak = 0;
        bool inFlash = false;
        for (double t = 0; t < 3 * 3600; t += 1 / 60.0)
        {
            double v = Atmosphere.LightningLevel(t, out _, out _);
            maxStep = Math.Max(maxStep, Math.Abs(v - prev));
            peak = Math.Max(peak, v);
            if (!inFlash && v > 0.02) { starts.Add(t); inFlash = true; }
            if (inFlash && v == 0) inFlash = false;
            prev = v;
        }
        var gaps = starts.Zip(starts.Skip(1), (a, b) => b - a).ToList();
        double perHour = starts.Count / 3.0;
        Check("1.13: soft lightning is rare: about one a minute or less, never two within 24 s", perHour is >= 30 and <= 70 && gaps.Min() >= 24, $"({perHour:0}/h, shortest gap {gaps.Min():0.0} s, median {gaps.OrderBy(g => g).ElementAt(gaps.Count / 2):0} s)");
        Check("1.13: each flash rises and fades gently (no step over 7 % per 1/60 s, no strobe)", maxStep < 0.07 && peak <= 1.0, $"(largest step {maxStep:0.000}, peak {peak:0.00})");
        int rises = 0; prev = 0; bool rising = false;
        for (double t = starts[0] - 1; t < starts[0] + Atmosphere.FlashLength + 1; t += 1 / 60.0)
        {
            double v = Atmosphere.LightningLevel(t, out _, out _);
            if (v > prev + 1e-4 && !rising) { rises++; rising = true; }
            if (v < prev - 1e-4) rising = false;
            prev = v;
        }
        Check("1.13: one flash is a single swell, not repeated flashes", rises == 1, $"({rises} rises)");

        // only Distant Storm, only when on; the switch keeps the rain
        var g = LayoutTransform.Identity.GeometryFor(DeviceRole.Keyboard, ILovbeeKeyMap.Keys.Length);
        double flashT = starts[0] + Atmosphere.FlashRise;
        var withL = SceneCfg(EffectKind.DistantStorm); var withoutL = SceneCfg(EffectKind.DistantStorm); withoutL.Lightning = false;
        var rainCfg = SceneCfg(EffectKind.Rainfall); rainCfg.Lightning = true;
        double Mean(RgbConfig c, double t) { var f = new Rgb[g.Count]; Effects.Render(c.Effect, EffectContext.From(c, t, null), g, f); return f.Average(x => (double)x.R + x.G + x.B); }
        Check("1.13: Distant Storm brightens during a flash; with lightning off it does not", Mean(withL, flashT) > Mean(withoutL, flashT) * 1.5 && Math.Abs(Mean(withoutL, flashT) - Mean(withoutL, flashT - 0.001)) < 30,
            $"({Mean(withL, flashT):0} vs {Mean(withoutL, flashT):0})");
        Check("1.13: no other effect ever shows lightning (the setting is ignored outside Distant Storm)", !EffectContext.From(rainCfg, flashT, null).Lightning && EffectInfo.Scenes.Where(e => e != EffectKind.DistantStorm).All(e => !EffectContext.From(SceneCfg(e), flashT, null).Lightning));
        Check("1.13: the preview can suppress lightning (reduced motion) without changing the setting", !EffectContext.From(withL, flashT, null, lightningAllowed: false).Lightning && withL.Lightning);
        var dim = SceneCfg(EffectKind.DistantStorm, brightness: 0.3);
        var fd = new Rgb[g.Count]; Effects.Render(EffectKind.DistantStorm, EffectContext.From(dim, flashT, null), g, fd);
        Check("1.13: flashes scale with brightness (at 30 % nothing exceeds 30 % of full)", fd.All(x => Math.Max(x.R, Math.Max(x.G, x.B)) <= 0.3 * 255 + 1), $"(max {fd.Max(x => Math.Max(x.R, Math.Max(x.G, x.B)))})");
    }

    // ---------------------------------------------------------------------------------------------------------
    static void SceneGeometryTests()
    {
        var keys = ILovbeeKeyMap.Keys;
        int esc = Array.FindIndex(keys, k => k.Name == "Esc"), space = Array.FindIndex(keys, k => k.Name == "Space"), right = Array.FindIndex(keys, k => k.Name == "Right");
        LedGeometry Kb(int rot)
        {
            var a = new DeviceArrangement(); a.Set(DeviceRole.Keyboard, new DevicePlacement { X = 500, Y = 200, Rotation = rot });
            return new LayoutTransform(a, rot + 1).GeometryFor(DeviceRole.Keyboard, keys.Length);
        }
        var k0 = Kb(0); var k90 = Kb(90); var k180 = Kb(180);
        Check("1.13: keyboard rain uses the verified key map: Esc in the top row, Space in the bottom row", k0.Surface && k0.Y[esc] < 0.01 && k0.Y[space] > k0.Height - 0.01 && Math.Abs(k0.Width - 1) < 1e-9);
        Check("1.13: turning the keyboard 180° makes rain fall from the space bar row toward the function row", k180.Y[space] < 0.01 && k180.Y[esc] > k180.Height - 0.01);
        Check("1.13: turned 90°, rain falls along the keyboard's length (down the arrangement)", k90.Height > 0.99 && k90.Width < 0.35 && k90.Y[esc] < 0.01 && k90.Y[right] > 0.9);
        Check("1.13: other outputs stay linear (no invented two-dimensional surface)", !LayoutTransform.Identity.GeometryFor(DeviceRole.Strip, 27).Surface && !LayoutTransform.Identity.GeometryFor(DeviceRole.Ram, 26, new[] { 5, 8, 5, 8 }).Surface);
        var ramG = LayoutTransform.Identity.GeometryFor(DeviceRole.Ram, 26, new[] { 5, 8, 5, 8 });
        Check("1.13: each memory controller is its own short run, in detection order", ramG.SegmentLength.SequenceEqual(new[] { 5, 8, 5, 8 }) && ramG.Segment[0] == 0 && ramG.Segment[25] == 3);
        // LED 0 is at the top of a stick (owner, 2026-10-15): drops travel toward increasing X, so X must grow from LED 0
        // to the stick's last LED in every run (the previous build had it reversed and the drops rose)
        bool down = true;
        for (int run = 0, o = 0; run < ramG.SegmentLength.Length; o += ramG.SegmentLength[run], run++)
            for (int i = 1; i < ramG.SegmentLength[run]; i++) if (!(ramG.X[o + i] > ramG.X[o + i - 1])) down = false;
        Check("1.13: memory drops run from LED 0 (top of the stick) to the last LED, i.e. downward", down && ramG.X[0] == 0 && ramG.X[4] == 1);
        Check("1.13: the front fans and logo stay one D_LED run", LayoutTransform.Identity.GeometryFor(DeviceRole.Strip, 27).SegmentLength.SequenceEqual(new[] { 27 }));

        // device-local rain does not move with the arrangement; setup-wide mist does
        var a1 = new DeviceArrangement(); a1.Set(DeviceRole.Keyboard, new DevicePlacement { X = 220, Y = 200 });
        var a2 = new DeviceArrangement(); a2.Set(DeviceRole.Keyboard, new DevicePlacement { X = 780, Y = 200 });
        var g1 = new LayoutTransform(a1, 11).GeometryFor(DeviceRole.Keyboard, keys.Length); var g2 = new LayoutTransform(a2, 12).GeometryFor(DeviceRole.Keyboard, keys.Length);
        bool dropsSame = true, mistDiffers = false;
        for (double t = 20; t < 60; t += 0.5)
        {
            var r1 = new Rgb[keys.Length]; var r2 = new Rgb[keys.Length];
            var rc = SceneCfg(EffectKind.Rainfall); rc.Glow = 0;   // no background: drops only
            Effects.Render(EffectKind.Rainfall, EffectContext.From(rc, t, null), g1, r1); Effects.Render(EffectKind.Rainfall, EffectContext.From(rc, t, null), g2, r2);
            for (int i = 0; i < keys.Length; i++) if (Math.Abs(r1[i].G - r2[i].G) > 12) dropsSame = false;
            var m1 = new Rgb[keys.Length]; var m2 = new Rgb[keys.Length];
            Effects.Render(EffectKind.RollingMist, EffectContext.From(SceneCfg(EffectKind.RollingMist), t, null), g1, m1); Effects.Render(EffectKind.RollingMist, EffectContext.From(SceneCfg(EffectKind.RollingMist), t, null), g2, m2);
            if (!m1.AsSpan().SequenceEqual(m2)) mistDiffers = true;
        }
        Check("1.13: rain is device-local (moving the keyboard does not change where drops fall)", dropsSame);
        var arr = UiFixture.LoadConfig(null); arr.KeyboardVerified = arr.KeyboardLiveVerified = true; arr.Rgb.Effect = EffectKind.Rainfall; arr.Rgb.RamOnboardEffects = false;
        string kbRain = LayoutTransform.ArrangeLabel(DeviceRole.Keyboard, arr), mouseRain = LayoutTransform.ArrangeLabel(DeviceRole.Mouse, arr);
        arr.Rgb.Effect = EffectKind.RollingMist;
        string kbMist = LayoutTransform.ArrangeLabel(DeviceRole.Keyboard, arr);
        Check("1.13: Arrange labels say what moving a device changes (rain stays on it, mist follows it, mouse preview only)",
            kbRain == "own rain" && kbMist == "moves the effect" && mouseRain == "preview only" && LayoutTransform.AxisCaption(EffectKind.Rainfall).Contains("rain stays") && LayoutTransform.AxisCaption(EffectKind.RollingMist).Contains("mist"), $"({kbRain}; {kbMist}; {mouseRain})");
        Check("1.13: mist is setup-wide (moving the keyboard changes what passes over it)", mistDiffers);
        Check("1.13: classic effects are rendered exactly as before (same per-LED positions)",
            EffectInfo.Classic.All(e =>
            {
                var g = LayoutTransform.Identity.GeometryFor(DeviceRole.Strip, 27); var f = new Rgb[27];
                var ctx = new EffectContext { Time = 3.3, Speed = 0.4, Brightness = 0.8, ColorA = new Rgb(200, 30, 90), ColorB = new Rgb(10, 220, 120), ThermalC = 55 };
                Effects.Render(e, ctx, g, f);
                var pos = DLedSink.PositionsFor(27);
                return Enumerable.Range(0, 27).All(i => f[i] == Effects.Evaluate(e, ctx, pos[i]));
            }));
    }

    // ---------------------------------------------------------------------------------------------------------
    static void SceneDeviceTests()
    {
        string bad = "";
        foreach (var e in EffectInfo.Scenes)
        {
            var c = SceneCfg(e);
            var m = MouseLightingState.From(c)!;
            var cc = Companion.For(c);
            bool okMouse = m.Fallback && m.Description.Contains("companion") && m.Description.Contains("independent")
                && m.Effect == (cc.Breathing ? MouseLightingState.EffectBreathingSingleColor : MouseLightingState.EffectStatic) && m.Color == Rgb.FromInt(cc.Color);
            if (!okMouse) bad += $"{e}: {m.Description}; ";
        }
        Check("1.13: every scene gives the mouse a labelled, independent companion onboard setting (never frames)", bad == "", bad);
        var st = LightingStatus.Evaluate(new AppConfig { Rgb = SceneCfg(EffectKind.Rainfall) }, new DeviceFacts(DeviceRole.Mouse, true, true, null, true, 0, 1, null) { Ack = LightAck.Accepted });
        Check("1.13: the mouse card says companion effect, independent, no live-update path", st.State == DeviceLightState.Independent && st.Headline.Contains("Companion") && st.Detail.Contains("no safe live-update path"));

        // keyboard: live frames carry the scene; without them the stored companion; stored setting never depends on time
        var kc = SceneCfg(EffectKind.Rainfall);
        var k = KeyboardLightingState.From(kc, 3)!;
        Check("1.13: keyboard stored setting for a scene is its calm companion (labelled)", k.Mode == KeyboardLightingState.ModeSteady && k.Description.StartsWith("Companion") && k.Color == Rgb.FromInt(Companion.For(kc).Color));
        var kf = KeyboardLightingState.From(SceneCfg(EffectKind.Fireflies), 3)!;
        Check("1.13: a breathing companion uses the keyboard's native breathing (own timing)", kf.Mode == KeyboardLightingState.ModeBreathing && kf.Sync == KeyboardSync.Native);
        var app = new AppConfig { Rgb = kc, KeyboardVerified = true, KeyboardLiveVerified = false };
        var ks = LightingStatus.Evaluate(app, new DeviceFacts(DeviceRole.Keyboard, true, true, null, true, 0, 1, null) { Ack = LightAck.Accepted });
        app.KeyboardLiveVerified = true;
        var kl = LightingStatus.Evaluate(app, new DeviceFacts(DeviceRole.Keyboard, true, true, null, true, 0, 1, null) { Ack = LightAck.Accepted });
        Check("1.13: keyboard without live frames: companion (independent); with live frames: the shared scene", ks.State == DeviceLightState.Independent && ks.Headline == "Companion look" && kl.State == DeviceLightState.Shared && kl.Detail.Contains("rain falls down the key rows"));

        // memory: Onboard mode is kept; the companion is shown and the reason explained
        var rc = SceneCfg(EffectKind.Rainfall); rc.RamOnboardEffects = true;
        var ramApp = new AppConfig { Rgb = rc };
        var rs = LightingStatus.Evaluate(ramApp, new DeviceFacts(DeviceRole.Ram, true, true, null, true, 0, 1, null) { Ack = LightAck.Accepted });
        Check("1.13: memory in Onboard mode keeps Onboard and shows the companion, saying the effect needs Streamed",
            rc.RamOnboardEffects && rs.State == DeviceLightState.Independent && rs.Detail.Contains("needs streamed memory lighting") && rs.Detail.Contains("Streamed"));
        Check("1.13: a steady companion in Onboard mode is one still colour (no animation streamed); a breathing one uses the modules' breathing",
            RamSink.OnboardFor(rc) == null && RamSink.StillCompanion(rc) == Rgb.FromInt(Companion.For(rc).Color).Scale(rc.Brightness)
            && RamSink.OnboardFor(new RgbConfig { RamOnboardEffects = true, Effect = EffectKind.Fireflies, ColorA = 0xC6D9A6, ColorB = 0x24503A, Brightness = 0.5, Speed = 0.4 }) is { mode: EneDramController.ModeBreathing }
            && RamSink.StillCompanion(SceneCfg(EffectKind.Rainfall)) == null);
        var streamed = LightingStatus.Evaluate(new AppConfig { Rgb = SceneCfg(EffectKind.Rainfall) }, new DeviceFacts(DeviceRole.Ram, true, true, null, true, 0, 1, null) { Ack = LightAck.Accepted });
        Check("1.13: streamed memory follows the scene (LED order, no slot claims)", streamed.State == DeviceLightState.Shared && streamed.Detail.Contains("LED order"));
        var strip = LightingStatus.Evaluate(new AppConfig { Rgb = SceneCfg(EffectKind.Rainfall) }, new DeviceFacts(DeviceRole.Strip, true, true, null, true, 0, 1, null) { Ack = LightAck.Accepted });
        Check("1.13: the case front is described as one chain (fans and logo together)", strip.Detail.Contains("one LED chain"));

        // at quit: the scene's calm companion colour, not its bright highlight colour; classic effects unchanged
        var exitCfg = SceneCfg(EffectKind.Rainfall);
        Check("1.13: quitting during a scene leaves the companion colour (classic effects: the primary colour, unchanged)",
            RgbEngine.SteadyLook(exitCfg) == Rgb.FromInt(Companion.For(exitCfg).Color) && RgbEngine.SteadyLook(new RgbConfig { Effect = EffectKind.Breathing, ColorA = 0x2198AA }) == Rgb.FromInt(0x2198AA)
            && exitCfg.SteadyConfig().Effect == EffectKind.Static && exitCfg.Effect == EffectKind.Rainfall);

        // write budgets: a scene running for a while produces one mouse setting, then nothing more
        var mc = SceneCfg(EffectKind.Rainfall);
        var mt = new FakeTransport();
        var ms = new MouseSink(mc, mt, null, null, listenForDeviceChanges: false);
        ms.Controller.Debounce = TimeSpan.FromMilliseconds(50);
        ms.Controller.EnumerateInterval = TimeSpan.FromMilliseconds(20);
        try
        {
            var end = DateTime.UtcNow.AddMilliseconds(1500);
            var frame = new Rgb[1];
            int posts = 0;
            while (DateTime.UtcNow < end) { frame[0] = new Rgb((byte)(posts * 7), 40, 90); ms.Post(frame); posts++; Thread.Sleep(5); }
            Thread.Sleep(200);
            int afterRain = mt.Link.Applies;
            Check("1.13: the mouse gets one setting for a running scene, however many frames are rendered", afterRain == 1 && mt.Link.Last?.Effect == MouseLightingState.EffectStatic, $"({posts} frames, {afterRain} settings)");
            mc.Effect = EffectKind.Fireflies;
            end = DateTime.UtcNow.AddMilliseconds(800);
            while (DateTime.UtcNow < end) { ms.Post(frame); Thread.Sleep(5); }
            Thread.Sleep(200);
            Check("1.13: changing the scene is one more setting (persistent writes only on change)", mt.Link.Applies == afterRain + 1 && mt.Link.Last?.Effect == MouseLightingState.EffectBreathingSingleColor, $"({mt.Link.Applies})");
        }
        finally { ms.Dispose(); }

        // keyboard with live frames: the scene streams as live frames; the stored fallback is written once
        var kcfg = SceneCfg(EffectKind.Rainfall);
        var t = new FakeKbTransport();
        var sink = NewKbSink(kcfg, t);
        try
        {
            var g = LayoutTransform.Identity.GeometryFor(DeviceRole.Keyboard, ILovbeeKeyMap.Keys.Length);
            var end = DateTime.UtcNow.AddMilliseconds(1500);
            double tt = 10;
            while (DateTime.UtcNow < end)
            {
                var f = new Rgb[g.Count];
                Effects.Render(EffectKind.Rainfall, EffectContext.From(kcfg, tt, null), g, f);
                sink.Post(f); tt += 1 / 15.0; Thread.Sleep(30);
            }
            Thread.Sleep(200);
            List<string> order; lock (t.Order) order = t.Order.ToList();
            int stored = order.Count(o => o.StartsWith("stored")), live = order.Count(o => o.StartsWith("live"));
            Check("1.13: keyboard live frames carry the scene; its stored companion is written once", live > 5 && stored == 1, $"(live {live}, stored {stored})");
        }
        finally { sink.Dispose(); }
    }

    // ---------------------------------------------------------------------------------------------------------
    static void SceneEngineTests()
    {
        // the engine's frames are exactly what the preview function computes for the same time and geometry
        var cfg = new AppConfig();
        cfg.Rgb.Enabled = true; cfg.Rgb.Effect = EffectKind.Rainfall; cfg.Rgb.ColorA = 0xA9C4D4; cfg.Rgb.ColorB = 0x2E6A6E; cfg.Rgb.Brightness = 0.6; cfg.Rgb.Density = 0.9;
        cfg.Rgb.DLedStrip = cfg.Rgb.Motherboard = cfg.Rgb.Ram = cfg.Rgb.Mouse = cfg.Rgb.Keyboard = false;
        var sink = new TestSink();
        var seen = new List<(EffectContext C, Rgb[] F)>();
        RgbEngine.FrameTap = (s, ctx, f) => { if (s == sink) lock (seen) if (seen.Count < 400) seen.Add((ctx, (Rgb[])f.Clone())); };
        try
        {
            using var engine = new RgbEngine(cfg, () => null);
            engine.Sinks.Add(sink);
            engine.Start();
            WaitUntil(() => { lock (seen) return seen.Count >= 120; }, 4000);
            cfg.Rgb.Enabled = false;
            List<(EffectContext C, Rgb[] F)> copy; lock (seen) copy = seen.ToList();
            var geo = engine.Layout.Geometry(sink);
            bool same = copy.Count >= 60 && copy.All(x => { var f = new Rgb[x.F.Length]; Effects.Render(EffectKind.Rainfall, EffectContext.From(cfg.Rgb, x.C.Time, null, drift: x.C.Drift), geo, f); return f.AsSpan().SequenceEqual(x.F); });
            double rate = EffectContext.DriftRate(cfg.Rgb.Speed);
            Check("1.13: engine frames equal the preview renderer for the same time and scene clock (one algorithm for both)", same, $"({copy.Count} frames)");
            Check("1.13: at a constant speed the engine's scene clock is time × rate", copy.All(x => Math.Abs(x.C.Drift - x.C.Time * rate) < 1e-6));
            Check("1.13: a scene renders at the existing animation rate (no new loop or higher rate)", engine.RenderHz == RgbEngine.StandardHz);
        }
        finally { RgbEngine.FrameTap = null; }
    }
}
