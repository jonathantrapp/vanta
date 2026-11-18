using HwSuite.Core;
using HwSuite.Lighting;
using HwSuite.Lighting.Devices;

namespace HwSuite.Cli;

/// <summary>
/// 1.15 checks: the case layout (three front fans, the rear fan and the logo on the D_LED chain), the rain family (Drip,
/// Rain Ripples, Downpour) on every kind of device, and a save format that VANTA 1.13/1.14 can still read. Pure functions
/// and in-memory configurations only; nothing here opens a device.
/// </summary>
public static partial class SelfTest
{
    static void Phase15Tests()
    {
        Guarded("1.15 save format", RainSaveTests);
        Guarded("1.15 case layout", CaseLayoutTests);
        Guarded("1.15 rain effects", RainEffectTests);
        Guarded("1.15.1 splitter hub", MirroredHubTests);
        Guarded("1.15.2 colour match", ColourBalanceTests);
        Guarded("1.15.3 radiator fans", () =>
        {
            var six = new CaseLayout { TopFans = 2, LedsPerFan = 8, LogoLeds = 3 };
            var a = six.Allocate(51);
            Check("1.15.3 case: two radiator fans join the layout after the rear fan (6 x 8 + logo 3 = 51 LEDs)",
                six.KnownTotal == 51 && a.Select(p => p.Kind).SequenceEqual(new[] { CasePartKind.FrontTop, CasePartKind.FrontMiddle, CasePartKind.FrontBottom, CasePartKind.Rear, CasePartKind.TopFront, CasePartKind.TopRear, CasePartKind.Logo }) && a.Sum(p => p.Count) == 51,
                string.Join(", ", a.Select(p => $"{p.Kind} {p.Count}")));
            var bad = new CaseLayout { TopFans = 5 }; bad.Normalize();
            var g = new LayoutTransform(null, 1, six).GeometryFor(DeviceRole.Strip, 51);
            Check("1.15.3 case: top fans sit under the roof between the front and rear fans; at most 2", bad.TopFans == 2
                && Enumerable.Range(32, 16).All(i => g.Y[i] < 0.1 && g.X[i] > 0.25 && g.X[i] < 0.7)
                && CaseLayout.CheckColor(CasePartKind.TopFront).Color != CaseLayout.CheckColor(CasePartKind.Logo).Color && !CaseLayout.Name(CasePartKind.TopRear).Contains("logo"));
            var back = ConfigStore.Parse(ConfigStore.ToJson(new AppConfig { Rgb = { Case = six } }))!;
            Check("1.15.3 case: the radiator fans round-trip", back.Rgb.Case?.TopFans == 2);
        });
    }

    static void ColourBalanceTests()
    {
        var b = new ColorBalance { R = 0.55 };
        var violet = Rgb.FromInt(0x8B00FF);
        Check("1.15.2 balance: turning the memory's red down makes its violet bluer, other channels untouched", b.Apply(violet) == new Rgb(76, 0, 255));
        var same = new Rgb[] { violet };
        Check("1.15.2 balance: 100% everywhere sends frames exactly as rendered (same array, no copy)", new ColorBalance().IsIdentity && ReferenceEquals(new ColorBalance().Apply(same), same));
        var wild = new ColorBalance { R = 7, G = -1, B = double.NaN }; wild.Normalize();
        Check("1.15.2 balance: values are repaired to 20-100% (never a boost, never off)", wild is { R: 1, G: 0.2, B: 1 });
        var cfg = new AppConfig(); cfg.Rgb.Balance.Ram.R = 0.55; cfg.Rgb.Balance.Strip.B = 0.8;
        var back = ConfigStore.Parse(ConfigStore.ToJson(cfg))!;
        var old = ConfigStore.Parse("{\"Version\":4,\"Rgb\":{\"Effect\":\"Static\"}}")!;
        Check("1.15.2 balance: saved per device; settings without it send everything as rendered",
            Math.Abs(back.Rgb.Balance.Ram.R - 0.55) < 1e-9 && Math.Abs(back.Rgb.Balance.Strip.B - 0.8) < 1e-9 && back.Rgb.Balance.Board.IsIdentity
            && old.Rgb.Balance.Ram.IsIdentity && old.Rgb.Balance.Strip.IsIdentity);
        var frame = Enumerable.Repeat(violet, 4).ToArray();
        var balanced = b.Apply(frame);
        Check("1.15.2 balance: a balanced frame is a copy; the rendered frame (preview) keeps the chosen colour", !ReferenceEquals(balanced, frame) && frame[0] == violet && balanced[0].R == 76);
    }

    /// <summary>1.15.1: the LANCOOL 215 hub copies one signal to every fan (owner's layout check: all four fans red).</summary>
    static void MirroredHubTests()
    {
        var m = new CaseLayout { Mirrored = true, LedsPerFan = 8, LogoLeds = 3 };
        var a = m.Allocate(35);
        Check("1.15.1 hub: a splitter hub is one 8-LED ring (every fan shows it), and needs only 8 LEDs", a.Count == 1 && a[0] is { Start: 0, Count: 8 } && m.CountsKnown && m.KnownTotal == 8);
        var pat = m.CheckPattern(8);
        Check("1.15.1 hub: its layout check is a rainbow around the ring starting with red at LED 1", pat[0] == new Rgb(255, 0, 0) && pat.Distinct().Count() == 8);
        var g = new LayoutTransform(null, 1, m).GeometryFor(DeviceRole.Strip, 8);
        int top = Enumerable.Range(0, 8).OrderBy(i => g.Y[i]).First(), bottom = Enumerable.Range(0, 8).OrderBy(i => g.Y[i]).Last();
        var turned = new CaseLayout { Mirrored = true, LedsPerFan = 8, TopLed = 2, CounterClockwise = true };
        var gt = new LayoutTransform(null, 1, turned).GeometryFor(DeviceRole.Strip, 8);
        int topT = Enumerable.Range(0, 8).OrderBy(i => gt.Y[i]).First();
        Check("1.15.1 hub: the ring runs top to bottom of the case; the top LED and direction can be set",
            g.Ring && top == 0 && bottom == 4 && g.Y[bottom] - g.Y[top] > 0.8 && topT == 2 && gt.X[3] < gt.X[2] && g.X[1] > g.X[0], $"(top {top}, bottom {bottom}, turned top {topT})");
        // a drip gathers at the top LED, runs down one side and reaches the bottom LED
        var cfg = RainCfg(EffectKind.Drip); cfg.Density = 0.3;
        var f = new Rgb[8];
        double restG = Rgb.FromInt(cfg.ColorB).G * Atmosphere.RestLevel(cfg.Glow) * cfg.Brightness;
        double? bead = null, side = null, low = null;
        for (double t = 0; t < 120 && low == null; t += 0.02)
        {
            Effects.Render(EffectKind.Drip, EffectContext.From(cfg, t, 45), g, f);
            if (bead == null && f[0].G > restG + 80) bead = t;
            else if (bead != null && side == null && (f[2].G > restG + 40 || f[6].G > restG + 40)) side = t;
            else if (side != null && f[4].G > restG + 40) low = t;
        }
        Check("1.15.1 hub: a drip gathers at the top LED, runs down a side of the ring and splashes at the bottom LED", bead != null && side > bead && low > side, $"(top {bead:0.00}, side {side:0.00}, bottom {low:0.00})");
        var saved = new AppConfig(); saved.Rgb.Case = turned;
        var back = ConfigStore.Parse(ConfigStore.ToJson(saved))!;
        Check("1.15.1 hub: the hub setting, top LED and direction round-trip", back.Rgb.Case is { Mirrored: true, TopLed: 2, CounterClockwise: true, LedsPerFan: 8 });
        var n = new CaseLayout { LedsPerFan = 8, TopLed = 99 }; n.Normalize();
        Check("1.15.1 hub: a top LED past the ring is repaired", n.TopLed == 7);
    }

    static RgbConfig RainCfg(EffectKind e) => new()
    {
        Enabled = true, Effect = e, ColorA = 0xBFE6D2, ColorB = 0x1E4A3A, Brightness = 0.6, Speed = 0.4, Density = 0.5, Glow = 0.45, Lightning = true
    };

    static void RainSaveTests()
    {
        foreach (var e in EffectInfo.RainScenes)
        {
            var cfg = new AppConfig(); cfg.Rgb.Effect = e;
            var json = ConfigStore.ToJson(cfg);
            var back = ConfigStore.Parse(json)!;
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!["Rgb"]!;
            string? saved = node["Effect"]?.GetValue<string>(), scene = node["Scene"]?.GetValue<string>(), scene2 = node["Scene2"]?.GetValue<string>();
            // what 1.13/1.14 see: they ignore "Scene2" and only know the classic effects and the six 1.13 scenes
            node.AsObject().Remove("Scene2");
            var older = ConfigStore.Parse(System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject().Let(o => { o["Rgb"]!.AsObject().Remove("Scene2"); return o.ToJsonString(); }))!;
            Check($"1.15 save: {e} round-trips, and older builds read {EffectInfo.SceneFallback(e)} (a scene they know) with a classic fallback",
                back.Rgb.Effect == e && scene2 == e.ToString() && scene == EffectInfo.SceneFallback(e).ToString() && EffectInfo.Scenes.Contains(EffectInfo.SceneFallback(e))
                && EffectInfo.Classic.Contains(Enum.Parse<EffectKind>(saved!)) && older.Rgb.Effect == EffectInfo.SceneFallback(e), $"(Effect {saved}, Scene {scene}, Scene2 {scene2})");
        }
        // field order does not matter (Scene2 before Scene and Effect)
        var reordered = ConfigStore.Parse("{\"Version\":4,\"Rgb\":{\"Scene2\":\"Downpour\",\"Scene\":\"DistantStorm\",\"Effect\":\"GradientWave\"}}")!;
        Check("1.15 save: the reading order of Effect, Scene and Scene2 does not matter", reordered.Rgb.Effect == EffectKind.Downpour);
        // Lights out restore point and saved looks use the same scheme
        var lo = new AppConfig(); lo.Rgb.LightsOutRestoreEffect = EffectKind.Drip; lo.Rgb.Effect = EffectKind.Stealth;
        var loBack = ConfigStore.Parse(ConfigStore.ToJson(lo))!;
        var loOld = ConfigStore.Parse(ConfigStore.ToJson(lo).Replace("\"LightsOutRestoreScene2\": \"Drip\",", ""))!;
        Check("1.15 save: the Lights out restore point keeps a 1.15 effect, and older builds restore its 1.13 neighbour",
            loBack.Rgb.LightsOutRestoreEffect == EffectKind.Drip && loOld.Rgb.LightsOutRestoreEffect == EffectKind.Rainfall, ConfigStore.ToJson(lo.Rgb).Length.ToString());
        var look = new LightingLook { Name = "x", Effect = EffectKind.RainRipples, ColorA = 1, ColorB = 2 };
        var lj = ConfigStore.ToJson(look);
        var lookBack = ConfigStore.FromJson<LightingLook>(lj)!;
        var lookOld = ConfigStore.FromJson<LightingLook>(lj.Replace("\"Scene2\": \"RainRipples\"", "\"Unused\": 0"))!;
        Check("1.15 save: a saved look with a 1.15 effect round-trips; older builds see Moonlit Water", lookBack.Effect == EffectKind.RainRipples && lookOld.Effect == EffectKind.MoonlitWater, lj.Replace("\n", " "));
        // the case layout is optional and ignored by older builds (unknown field)
        var plain = ConfigStore.Parse("{\"Version\":4,\"Rgb\":{\"Effect\":\"Static\"}}")!;
        Check("1.15 save: settings without a case layout get the default (3 front fans, rear fan, logo; LED split assumed)",
            plain.Rgb.Case == null && plain.Rgb.CaseOrDefault is { FrontFans: 3, RearFan: true, Logo: true, LedsPerFan: 0 });
        var withCase = new AppConfig(); withCase.Rgb.Case = new CaseLayout { FrontFans = 2, LedsPerFan = 9, LogoLeds = 4, Order = CaseChainOrder.RearFrontDownLogo };
        var wc = ConfigStore.Parse(ConfigStore.ToJson(withCase))!;
        Check("1.15 save: the case layout round-trips", wc.Rgb.Case is { FrontFans: 2, LedsPerFan: 9, LogoLeds: 4, Order: CaseChainOrder.RearFrontDownLogo });
    }

    static void CaseLayoutTests()
    {
        var d = CaseLayout.Default;
        var a27 = d.Allocate(27);
        Check("1.15 case: the default layout splits 27 LEDs over three front fans, the rear fan and the logo, in chain order, with none lost",
            a27.Select(p => p.Kind).SequenceEqual(new[] { CasePartKind.FrontTop, CasePartKind.FrontMiddle, CasePartKind.FrontBottom, CasePartKind.Rear, CasePartKind.Logo })
            && a27.Sum(p => p.Count) == 27 && a27.Last().Count < a27[0].Count && a27.Zip(a27.Skip(1)).All(z => z.First.Start + z.First.Count == z.Second.Start),
            string.Join(", ", a27.Select(p => $"{p.Kind} {p.Count}")));
        var known = new CaseLayout { LedsPerFan = 12, LogoLeds = 6 };
        var a54 = known.Allocate(54);
        var a40 = known.Allocate(40);
        Check("1.15 case: known counts are used exactly; a short chain cuts the last parts", known.KnownTotal == 54 && a54.All(p => p.Count == (p.Kind == CasePartKind.Logo ? 6 : 12)) && a40.Sum(p => p.Count) == 40 && a40.All(p => p.Kind != CasePartKind.Logo),
            string.Join(", ", a40.Select(p => $"{p.Kind} {p.Count}")));
        var orders = Enum.GetValues<CaseChainOrder>().Select(o => new CaseLayout { Order = o }.Chain()).ToList();
        Check("1.15 case: every chain order lists each part once", orders.All(c => c.Distinct().Count() == 5 && c.Count == 5) && orders.Select(c => string.Join(",", c)).Distinct().Count() == orders.Count);
        var odd = new CaseLayout { FrontFans = 9, LedsPerFan = 400, LogoLeds = -3, RearFan = false, Logo = false }; odd.Normalize();
        var none = new CaseLayout { FrontFans = 0, RearFan = false, Logo = false }; none.Normalize();
        Check("1.15 case: invalid values are repaired, never trusted", odd is { FrontFans: 3, LedsPerFan: 60, LogoLeds: 0 } && none.FrontFans == 1);

        var g = new LayoutTransform(null, 1, known).GeometryFor(DeviceRole.Strip, 54);
        double Y(int part) => Enumerable.Range(0, 54).Where(i => g.Part[i] == part).Average(i => g.Y[i]);
        double X(int part) => Enumerable.Range(0, 54).Where(i => g.Part[i] == part).Average(i => g.X[i]);
        Check("1.15 case: the front fans are stacked top to bottom, the rear fan at the back near the top, the logo at the bottom",
            g.Case && !g.Surface && Y(0) < Y(1) && Y(1) < Y(2) && X(3) > X(0) + 0.4 && Math.Abs(Y(3) - Y(0)) < 0.05 && Y(4) > Y(2), $"(y {Y(0):0.00} {Y(1):0.00} {Y(2):0.00}, rear x {X(3):0.00})");
        var pat = known.CheckPattern(56);
        Check("1.15 case: the layout check lights each part in its own colour and leaves LEDs past the layout off",
            pat.Length == 56 && pat[0] == CaseLayout.CheckColor(CasePartKind.FrontTop).Color && pat[12] == CaseLayout.CheckColor(CasePartKind.FrontMiddle).Color
            && pat[36] == CaseLayout.CheckColor(CasePartKind.Rear).Color && pat[53] == CaseLayout.CheckColor(CasePartKind.Logo).Color && pat[54] == Rgb.Black && pat[55] == Rgb.Black
            && Enum.GetValues<CasePartKind>().Select(k => CaseLayout.CheckColor(k).Color).Distinct().Count() == Enum.GetValues<CasePartKind>().Length);
        var classic = LayoutTransform.Identity.GeometryFor(DeviceRole.Strip, 27);
        Check("1.15 case: classic effects keep the chain's setup-wide positions (Gradient Wave unchanged)", classic.Line.SequenceEqual(DLedSink.PositionsFor(27)));
    }

    static void RainEffectTests()
    {
        var kb = LayoutTransform.Identity.GeometryFor(DeviceRole.Keyboard, ILovbeeKeyMap.Keys.Length);
        var ram = LayoutTransform.Identity.GeometryFor(DeviceRole.Ram, 26, new[] { 5, 8, 5, 8 });
        var board = LayoutTransform.Identity.GeometryFor(DeviceRole.Board, 2);
        var caseG = LayoutTransform.Identity.GeometryFor(DeviceRole.Strip, 27);
        var geos = new[] { ("case", caseG), ("keyboard", kb), ("memory", ram), ("board", board) };
        foreach (var e in EffectInfo.RainScenes)
        {
            var cfg = RainCfg(e);
            bool repeat = true, finite = true, someLight = false; double meanLevel = 0; int frames = 0;
            foreach (var (_, g) in geos)
            {
                var f1 = new Rgb[g.Count]; var f2 = new Rgb[g.Count];
                for (double t = 3; t < 63; t += 0.25)
                {
                    Effects.Render(e, EffectContext.From(cfg, t, 45), g, f1);
                    Effects.Render(e, EffectContext.From(cfg, t, 45), g, f2);
                    repeat &= f1.AsSpan().SequenceEqual(f2);
                    double peak = f1.Max(c => (double)Math.Max(c.R, Math.Max(c.G, c.B)));
                    someLight |= peak > 90;
                    meanLevel += f1.Average(c => (c.R + c.G + c.B) / 3.0); frames++;
                }
                finite &= f1.All(c => c.R <= 255);
            }
            meanLevel /= frames;
            Check($"1.15 rain: {EffectInfo.Label(e)} is repeatable at any moment, shows its drops, and keeps most LEDs dim", repeat && finite && someLight && meanLevel < 75, $"(mean level {meanLevel:0})");
        }

        // a drip gathers at the top of the front stack before it falls through the middle and bottom fans
        var known = new CaseLayout { LedsPerFan = 12, LogoLeds = 6 };
        var cg = new LayoutTransform(null, 1, known).GeometryFor(DeviceRole.Strip, 54);
        var parts = known.Allocate(54);
        var dcfg = RainCfg(EffectKind.Drip); dcfg.Density = 0.2;
        double PartLevel(Rgb[] f, int p) => Enumerable.Range(parts[p].Start, parts[p].Count).Max(i => f[i].G);
        var fr = new Rgb[54];
        double? topAt = null, midAt = null, botAt = null;
        double restG = Rgb.FromInt(dcfg.ColorB).G * Atmosphere.RestLevel(dcfg.Glow) * dcfg.Brightness;
        for (double t = 0; t < 120 && botAt == null; t += 0.02)
        {
            Effects.Render(EffectKind.Drip, EffectContext.From(dcfg, t, 45), cg, fr);
            double top = PartLevel(fr, 0), mid = PartLevel(fr, 1), bot = PartLevel(fr, 2);
            if (topAt == null && top > restG + 60) topAt = t;
            else if (topAt != null && midAt == null && mid > restG + 40) midAt = t;
            else if (midAt != null && bot > restG + 40) botAt = t;
        }
        Check("1.15 rain: on the case a drip lights the top fan first, then the middle fan, then the bottom fan, speeding up as it falls",
            topAt != null && midAt > topAt && botAt > midAt && botAt - midAt < midAt - topAt + 0.6, $"(top {topAt:0.00} s, middle {midAt:0.00}, bottom {botAt:0.00})");

        // Downpour honours the lightning switch; Drip and Rain Ripples never flash
        double flashT = 1; for (double t = 1; t < 400; t += 0.05) if (Atmosphere.LightningLevel(t, out _, out _) > 0.6) { flashT = t; break; }
        var dp = RainCfg(EffectKind.Downpour); var dpOff = RainCfg(EffectKind.Downpour); dpOff.Lightning = false;
        Check("1.15 rain: Downpour shows lightning only when it is switched on; Drip and Rain Ripples never do",
            EffectContext.From(dp, flashT, null).Lightning && !EffectContext.From(dpOff, flashT, null).Lightning
            && !EffectContext.From(RainCfg(EffectKind.Drip), flashT, null).Lightning && !EffectContext.From(RainCfg(EffectKind.RainRipples), flashT, null).Lightning);

        // no allocation per frame (the renderer itself)
        var buf = new Rgb[kb.Count]; var cbuf = new Rgb[54];
        var ctxs = EffectInfo.RainScenes.Append(EffectKind.Rainfall).Append(EffectKind.DistantStorm).Select(e => (e, EffectContext.From(RainCfg(e), 77, null))).ToArray();
        foreach (var (e, cx) in ctxs) { Effects.Render(e, cx, kb, buf); Effects.Render(e, cx, cg, cbuf); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) foreach (var (e, cx) in ctxs) { Effects.Render(e, cx, kb, buf); Effects.Render(e, cx, cg, cbuf); }
        long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("1.15 rain: the rain renderers allocate nothing per frame (keyboard and case)", alloc < 1024, $"({alloc} bytes over 1000 frames)");

        // brightness 0 is dark; the companion and fallbacks are defined
        var dark = RainCfg(EffectKind.Downpour); dark.Brightness = 0;
        var df = new Rgb[54]; Effects.Render(EffectKind.Downpour, EffectContext.From(dark, flashT, null), cg, df);
        Check("1.15 rain: brightness 0 turns every LED off, lightning included", df.All(c => c.R == 0 && c.G == 0 && c.B == 0));
        Check("1.15 rain: each new effect has a calm companion and a classic fallback", EffectInfo.RainScenes.All(e => Companion.For(RainCfg(e)).Text.Length > 0 && EffectInfo.ClassicFallback(e) == EffectKind.GradientWave && EffectInfo.DensityLabel(e) != null));
        var starters = LightingLooks.Starters.Where(l => l.Group == LightingLooks.RainGroup).ToList();
        var applied = new AppConfig(); LightingLooks.Apply(applied, starters.First(l => l.Effect == EffectKind.Drip));
        Check("1.15 rain: four rain starter looks, and applying one selects its effect", starters.Count == 4 && EffectInfo.RainScenes.All(e => starters.Any(l => l.Effect == e)) && applied.Rgb.Effect == EffectKind.Drip);
    }
}

static class JsonLet
{
    public static TR Let<T, TR>(this T value, Func<T, TR> f) => f(value);
}
