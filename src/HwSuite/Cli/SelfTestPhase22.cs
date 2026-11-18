using System.Net;
using System.Net.Http;
using System.Text.Json;
using HwSuite.Core;
using HwSuite.Hardware;
using HwSuite.Hardware.Al80;
using HwSuite.Hardware.Games;

namespace HwSuite.Cli;

/// <summary>
/// 1.22 checks: the Deadlock game profile. Settings and their defaults, the rank and match-history arithmetic on fixed JSON, the API
/// client against a fake HTTP handler (nothing touches the network), the process watcher with fake process lists (brightness raised
/// and restored, a brightness changed during the game kept, a crash restored at the next start, an unreadable process list changes
/// nothing), the AL80 service showing the panel and handing the screen back (simulated keyboard), and the panel drawing. No device
/// and no network are used.
/// </summary>
public static partial class SelfTest
{
    static void Phase22Tests()
    {
        Guarded("1.22 game settings", GameConfigTests);
        Guarded("1.22 Deadlock stats arithmetic", DeadlockParseTests);
        Guarded("1.22 Deadlock API client", DeadlockClientTests);
        Guarded("1.22 game profile watcher", GameServiceTests);
        Guarded("1.22 AL80 shows the Deadlock panel", DeadlockAl80Tests);
        Guarded("1.22 Deadlock panel drawing", DeadlockDrawTests);
    }

    // ------------------------------------------------------------------ fixtures
    const string HeroesJson = "[{\"id\":1,\"name\":\"Infernus\"},{\"id\":2,\"name\":\"Seven\"}]";
    const string RanksJson = "[{\"tier\":0,\"name\":\"Obscurus\",\"color\":\"#333333\"},{\"tier\":9,\"name\":\"Phantom\",\"color\":\"#9a7cff\"}]";
    const string RankJson = "{\"badge\":93,\"rank\":9,\"subrank\":3,\"last_match\":null}";

    // newest first once sorted: 600 (win), 500 (loss), 400 (penalized: ignored), 300 (win), 200 (invalid: ignored), 100 (loss)
    const string HistoryJson =
        "[{\"match_id\":4,\"hero_id\":1,\"start_time\":1700000300,\"player_kills\":6,\"player_deaths\":4,\"player_assists\":7,\"player_match_outcome\":1,\"match_duration_s\":1700}," +
        "{\"match_id\":1,\"hero_id\":1,\"start_time\":1700000600,\"player_kills\":8,\"player_deaths\":2,\"player_assists\":5,\"player_match_outcome\":1,\"match_duration_s\":1800}," +
        "{\"match_id\":6,\"hero_id\":2,\"start_time\":1700000100,\"player_kills\":2,\"player_deaths\":8,\"player_assists\":1,\"player_match_outcome\":2,\"match_duration_s\":1500}," +
        "{\"match_id\":2,\"hero_id\":2,\"start_time\":1700000500,\"player_kills\":4,\"player_deaths\":6,\"player_assists\":3,\"player_match_outcome\":2,\"match_duration_s\":1600}," +
        "{\"match_id\":3,\"hero_id\":1,\"start_time\":1700000400,\"player_kills\":9,\"player_deaths\":0,\"player_assists\":9,\"player_match_outcome\":3,\"match_duration_s\":600}," +
        "{\"match_id\":5,\"hero_id\":1,\"start_time\":1700000200,\"player_kills\":9,\"player_deaths\":0,\"player_assists\":9,\"player_match_outcome\":0,\"match_duration_s\":0}]";

    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    sealed class FakeDeadlockHandler : HttpMessageHandler
    {
        public readonly List<string> Paths = new();
        public int Status = 200;
        public bool Throw;
        public string History = HistoryJson;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            string p = req.RequestUri!.AbsolutePath;
            lock (Paths) Paths.Add(p);
            if (Throw) throw new HttpRequestException("no network (test)");
            string body = p == "/v1/assets/heroes" ? HeroesJson : p == "/v1/assets/ranks" ? RanksJson
                : p.EndsWith("/rank") ? RankJson : p.EndsWith("/match-history") ? History : "{}";
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(body) });
        }
    }

    // ------------------------------------------------------------------ settings
    static void GameConfigTests()
    {
        var c = new AppConfig();
        var g = c.Games;
        Check("1.22: the Deadlock profile is on by default, with full brightness and stats", g.DeadlockEnabled && g.DeadlockStats && g.DeadlockBrightness == 1.0);
        Check("1.22: the process names cover the installed game and the older project8.exe", g.DeadlockExes.Contains("deadlock.exe") && g.DeadlockExes.Contains("project8.exe"));
        var back = ConfigStore.Parse(ConfigStore.ToJson(c))!;
        Check("1.22: game settings survive save and load", back.Games.DeadlockExes.SequenceEqual(g.DeadlockExes) && back.Games.DeadlockRefreshMin == g.DeadlockRefreshMin && back.Games.DeadlockAccountId == null);
        var older = ConfigStore.Parse("{\"Version\":4}")!;
        Check("1.22: a config saved before 1.22 loads with the defaults", older.Games.DeadlockEnabled && older.Games.DeadlockExes.Count == 2);
        var set = ConfigStore.Parse("{\"Games\":{\"DeadlockAccountId\":39734274,\"DeadlockBrightness\":0.8,\"DeadlockEnabled\":false}}")!;
        Check("1.22: an account number and brightness in the file are read", set.Games.DeadlockAccountId == 39734274 && set.Games.DeadlockBrightness == 0.8 && !set.Games.DeadlockEnabled);
    }

    // ------------------------------------------------------------------ arithmetic
    static void DeadlockParseTests()
    {
        var heroes = DeadlockParse.Heroes(J(HeroesJson));
        var tiers = DeadlockParse.Tiers(J(RanksJson));
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var s = DeadlockParse.Summarize(J(RankJson), J(HistoryJson), heroes, tiers, now);
        Check("1.22: rank 9 sub-rank 3 reads as Phantom 3", s.Rank == "Phantom 3" && s.Tier == 9 && s.Subrank == 3 && s.TierColor == 0x9A7CFF, s.Rank);
        Check("1.22: only scored matches count (penalized and invalid games are skipped)", s.Wins == 2 && s.Losses == 2, $"{s.Wins}W {s.Losses}L");
        Check("1.22: average kills, deaths and assists over those four", Math.Abs(s.AvgKills - 5.0) < 1e-9 && Math.Abs(s.AvgDeaths - 5.0) < 1e-9 && Math.Abs(s.AvgAssists - 4.0) < 1e-9);
        Check("1.22: KDA is (kills + assists) / deaths and the win rate is 50%", Math.Abs(s.Kda - 1.8) < 1e-9 && Math.Abs(s.WinPct - 50) < 1e-9);
        Check("1.22: form is newest first, even though the list arrived out of order", s.Form.SequenceEqual(new[] { 1, 2, 1, 2 }));
        Check("1.22: the last match is the newest one, with the hero's name", s.Last is { MatchId: 1, Hero: "Infernus", Kills: 8, Deaths: 2, Assists: 5 } && s.Last.Won);
        Check("1.22: the key ignores the fetch time, so an unchanged refresh sends no picture",
            s.Key == DeadlockParse.Summarize(J(RankJson), J(HistoryJson), heroes, tiers, now.AddMinutes(3)).Key);

        var many = "[" + string.Join(",", Enumerable.Range(0, 25).Select(i => $"{{\"match_id\":{i},\"hero_id\":1,\"start_time\":{1700001000 - i * 60},\"player_kills\":1,\"player_deaths\":1,\"player_assists\":1,\"player_match_outcome\":1,\"match_duration_s\":1}}")) + "]";
        var m = DeadlockParse.Summarize(J(RankJson), J(many), heroes, tiers, now);
        Check($"1.22: only the newest {DeadlockParse.Take} scored matches count and the form shows {DeadlockParse.FormLength}", m.Wins == DeadlockParse.Take && m.Form.Count == DeadlockParse.FormLength && m.Last?.MatchId == 0);

        var none = DeadlockParse.Summarize(J("{\"badge\":0,\"rank\":0,\"subrank\":0,\"last_match\":null}"), J("[]"), heroes, tiers, now);
        Check("1.22: no matches and no rank is Unranked with zeros, not an error", none.Rank == "Unranked" && none.Wins == 0 && none.Losses == 0 && none.Last == null && none.WinPct == 0 && none.AvgKills == 0);
        var badge = DeadlockParse.Summarize(J("{\"badge\":93,\"rank\":0,\"subrank\":0}"), J("[]"), heroes, tiers, now);
        Check("1.22: a badge (tier * 10 + sub-rank) is used when rank and sub-rank are missing", badge.Tier == 9 && badge.Subrank == 3);
        var odd = DeadlockParse.Summarize(J("{\"rank\":9,\"subrank\":0}"), J("{}"), new Dictionary<int, string>(), new Dictionary<int, (string, int)>(), now);
        Check("1.22: unknown tier names and a history that is not a list do not throw", odd.Rank == "Tier 9" && odd.Wins == 0);
        var unknownHero = DeadlockParse.Summarize(J(RankJson), J(HistoryJson), new Dictionary<int, string>(), tiers, now);
        Check("1.22: a hero missing from the hero list is shown by number", unknownHero.Last?.Hero == "Hero 1");

        const long b = 76561197960265728L;
        string vdf = "\"users\"\n{\n\t\"76561198000000001\"\n\t{\n\t\t\"AccountName\"\t\t\"a\"\n\t\t\"MostRecent\"\t\t\"0\"\n\t}\n\t\"76561198000000002\"\n\t{\n\t\t\"AccountName\"\t\t\"b\"\n\t\t\"MostRecent\"\t\t\"1\"\n\t}\n}";
        Check("1.22: the Steam account used is the one signed in most recently, as a 32-bit account number", SteamAccount.MostRecentAccountId(vdf) == 76561198000000002L - b);
        Check("1.22: no most-recent account means none, not a guess", SteamAccount.MostRecentAccountId(vdf.Replace("\"MostRecent\"\t\t\"1\"", "\"MostRecent\"\t\t\"0\"")) == null);
    }

    // ------------------------------------------------------------------ client
    static void DeadlockClientTests()
    {
        var h = new FakeDeadlockHandler();
        using var c = new DeadlockClient(h);
        var s = c.FetchAsync(12345, CancellationToken.None).GetAwaiter().GetResult();
        Check("1.22: the client builds the summary from the API answers", s.Rank == "Phantom 3" && s.Wins == 2 && s.Losses == 2);
        Check("1.22: it asks for this player's rank and history only", h.Paths.Contains("/v1/players/12345/rank") && h.Paths.Contains("/v1/players/12345/match-history"));
        int first = h.Paths.Count;
        c.FetchAsync(12345, CancellationToken.None).GetAwaiter().GetResult();
        Check("1.22: the hero and rank name lists are fetched once, then reused", h.Paths.Count - first == 2 && h.Paths.Count(p => p == "/v1/assets/heroes") == 1);

        h.Status = 500;
        string msg = "";
        try { using var c2 = new DeadlockClient(h); c2.FetchAsync(1, CancellationToken.None).GetAwaiter().GetResult(); } catch (DeadlockApiException ex) { msg = ex.Message; }
        Check("1.22: an HTTP error becomes a short readable message", msg.Contains("500"), msg);
        h.Status = 200; h.Throw = true; msg = "";
        try { using var c3 = new DeadlockClient(h); c3.FetchAsync(1, CancellationToken.None).GetAwaiter().GetResult(); } catch (DeadlockApiException ex) { msg = ex.Message; }
        Check("1.22: a network failure becomes a short readable message", msg.Contains("could not reach"), msg);
    }

    // ------------------------------------------------------------------ watcher
    static void GameServiceTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vanta-game-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            bool running = false, fail = false; int saves = 0;
            ProcessPresence Fake(string n) => new(n, startTimer: false)
            {
                Lister = () => { if (fail) throw new InvalidOperationException("process list failed (test)"); return running && n.StartsWith("deadlock", StringComparison.OrdinalIgnoreCase); }
            };
            var cfg = new AppConfig(); cfg.Games.DeadlockStats = false; cfg.Rgb.Brightness = 0.4;
            string state = Path.Combine(dir, "game-profile-state.json");
            using (var svc = new GameProfileService(cfg, () => null, () => saves++, dir, presence: Fake))
            {
                svc.Tick();
                Check("1.22 watcher: nothing changes while the game is closed", !svc.Active && cfg.Rgb.Brightness == 0.4 && saves == 0 && svc.Status.StartsWith("Waiting"), svc.Status);
                running = true; svc.Tick();
                Check("1.22 watcher: the game starting raises the lighting brightness to the profile's level", svc.Active && cfg.Rgb.Brightness == 1.0 && File.Exists(state) && saves == 1, $"{cfg.Rgb.Brightness}");
                svc.Tick();
                Check("1.22 watcher: a second look while it runs changes nothing", saves == 1 && cfg.Rgb.Brightness == 1.0);
                fail = true; svc.Tick();
                Check("1.22 watcher: an unreadable process list keeps the current state", svc.Active && cfg.Rgb.Brightness == 1.0);
                fail = false; running = false; svc.Tick();
                Check("1.22 watcher: the game closing puts the brightness back and removes the state file", !svc.Active && cfg.Rgb.Brightness == 0.4 && !File.Exists(state) && saves == 2, $"{cfg.Rgb.Brightness}");

                running = true; svc.Tick(); cfg.Rgb.Brightness = 0.7; running = false; svc.Tick();
                Check("1.22 watcher: a brightness the owner changed during the game is kept", cfg.Rgb.Brightness == 0.7);

                cfg.Rgb.Brightness = 1.0; running = true; svc.Tick(); running = false; svc.Tick();
                Check("1.22 watcher: a brightness that was already at full is left alone both ways", cfg.Rgb.Brightness == 1.0);

                cfg.Rgb.Brightness = 0.4; running = true; svc.Tick();
                cfg.Games.DeadlockEnabled = false; svc.Tick();
                Check("1.22 watcher: switching the profile off while the game runs restores the brightness", !svc.Active && cfg.Rgb.Brightness == 0.4);
                cfg.Games.DeadlockEnabled = true; svc.Tick();
                Check("1.22 watcher: switching it back on while the game runs starts it again", svc.Active && cfg.Rgb.Brightness == 1.0);
                cfg.Games.DeadlockBrightness = 0.8; running = false; svc.Tick(); cfg.Rgb.Brightness = 0.2; running = true; svc.Tick();
                Check("1.22 watcher: the profile's own brightness level is used", cfg.Rgb.Brightness == 0.8);
                running = false; svc.Tick();
            }

            // a run that ended during a game: the next start puts the old brightness back
            var crash = new AppConfig(); crash.Games.DeadlockStats = false; crash.Rgb.Brightness = 0.3;
            running = true;
            var dead = new GameProfileService(crash, () => null, () => { }, dir, presence: Fake);
            dead.Tick();
            Check("1.22 crash safety: the previous brightness is written to the state file while the game runs", crash.Rgb.Brightness == 1.0 && File.Exists(state));
            // (no Dispose: the process "crashed"); the config on disk would hold 1.0
            using (var again = new GameProfileService(crash, () => null, () => { }, dir, presence: Fake))
                Check("1.22 crash safety: the next start restores it and removes the state file", again.RecoveredAtStart && crash.Rgb.Brightness == 0.3 && !File.Exists(state), $"{crash.Rgb.Brightness}");
            dead.Dispose();

            var edited = new AppConfig(); edited.Games.DeadlockStats = false; edited.Rgb.Brightness = 0.3; running = true;
            var d2 = new GameProfileService(edited, () => null, () => { }, dir, presence: Fake); d2.Tick(); edited.Rgb.Brightness = 0.55;
            using (var again2 = new GameProfileService(edited, () => null, () => { }, dir, presence: Fake))
                Check("1.22 crash safety: a brightness changed since is not overwritten at the next start", !again2.RecoveredAtStart && edited.Rgb.Brightness == 0.55);
            d2.Dispose();

            // stats without a keyboard
            var h = new FakeDeadlockHandler();
            var sc = new AppConfig(); sc.Games.DeadlockAccountId = 12345;
            using (var sv = new GameProfileService(sc, () => null, () => { }, dir, client: () => new DeadlockClient(h), accountFinder: () => 999, presence: Fake))
            {
                sv.FetchOnce(CancellationToken.None).GetAwaiter().GetResult();
                Check("1.22 stats: the configured account number is used, not the Steam login", sv.Summary?.Rank == "Phantom 3" && h.Paths.Contains("/v1/players/12345/rank") && !h.Paths.Any(p => p.Contains("999")));
                h.Status = 503;
                sv.FetchOnce(CancellationToken.None).GetAwaiter().GetResult();
                Check("1.22 stats: a failed refresh keeps the last data and records why", sv.Summary != null && sv.Error != null && sv.Error.Contains("503"), sv.Error ?? "");
            }
            var nacc = new AppConfig();
            using (var sv2 = new GameProfileService(nacc, () => null, () => { }, dir, client: () => new DeadlockClient(h), accountFinder: () => null, presence: Fake))
            {
                sv2.FetchOnce(CancellationToken.None).GetAwaiter().GetResult();
                Check("1.22 stats: with no Steam account the panel says so instead of guessing", sv2.Summary == null && (sv2.Error ?? "").Contains("no Steam account"));
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------------ keyboard
    static void DeadlockAl80Tests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vanta-game-al80-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cfg = new AppConfig();
            cfg.Al80.Enabled = true; cfg.Al80.Mode = "home"; cfg.Al80.FlashSlotsFilled = true; cfg.Games.DeadlockAccountId = 777; cfg.Rgb.Brightness = 0.3;
            var snap = new TelemetrySnapshot();
            var fake = new FakeAl80Transport();
            using var al = new Al80Service(cfg, () => snap, () => new Al80Device(fake));
            al.Start();
            bool Wait(Func<bool> cond, int ms) { var end = Environment.TickCount64 + ms; while (Environment.TickCount64 < end) { if (cond()) return true; Thread.Sleep(50); } return cond(); }
            bool IsCmd(byte[] r, byte kind) => r[7] == 0xA5 && r[8] == 0x5A && r[9] == kind;
            Check("1.22 AL80: the simulated keyboard connects", Wait(() => al.Connected, 4000));
            Check("1.22 AL80: home mode uploads no picture before the game", fake.Picture == null);

            bool running = false; var h = new FakeDeadlockHandler();
            using var gp = new GameProfileService(cfg, () => al, () => { }, dir, client: () => new DeadlockClient(h),
                presence: n => new ProcessPresence(n, startTimer: false) { Lister = () => running && n.StartsWith("deadlock", StringComparison.OrdinalIgnoreCase) });
            running = true; gp.Tick();
            Check("1.22 AL80: the game starting switches the screen to the Deadlock panel and raises the lights", al.PanelOverrideActive && cfg.Rgb.Brightness == 1.0);
            Check("1.22 AL80: the stats arrive", Wait(() => gp.Summary != null, 6000), gp.Status);
            Check("1.22 AL80: the panel is sent as a picture (the flash slots are confirmed full)", Wait(() => fake.Picture != null, 8000), al.Status);
            Check("1.22 AL80: the saved screen mode is untouched", cfg.Al80.Mode == "home");
            Wait(() => al.SessionPictures >= 1, 3000);        // the upload counts itself just after the last packet
            int pictures = al.SessionPictures;
            Thread.Sleep(1500);
            Check("1.22 AL80: an unchanged panel is not sent again", al.SessionPictures == pictures, $"({pictures} -> {al.SessionPictures})");

            running = false; gp.Tick();
            Check("1.22 AL80: the game closing ends the override and restores the lights", !al.PanelOverrideActive && cfg.Rgb.Brightness == 0.3);
            Check("1.22 AL80: the keyboard's own home screen comes back", Wait(() => fake.Snapshot().Any(r => IsCmd(r, Al80Protocol.PkGoHome)), 4000));
            Check("1.22 AL80: the saved mode is still home and nothing was saved", cfg.Al80.Mode == "home");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // without the flash-slot confirmation only a preview is made
        var cfg2 = new AppConfig();
        cfg2.Al80.Enabled = true; cfg2.Al80.Mode = "home"; cfg2.Al80.FlashSlotsFilled = false;
        var fake2 = new FakeAl80Transport();
        using var al2 = new Al80Service(cfg2, () => new TelemetrySnapshot(), () => new Al80Device(fake2));
        al2.Start();
        var until = Environment.TickCount64 + 4000; while (Environment.TickCount64 < until && !al2.Connected) Thread.Sleep(50);
        al2.SetPanelOverride(() => new DeadlockPanel(null, null, true));
        Thread.Sleep(1800);
        Check("1.22 AL80: without the flash-slot confirmation the panel is previewed, never sent", fake2.Picture == null && al2.Preview != null && al2.Status.Contains("preview only"), al2.Status);
    }

    // ------------------------------------------------------------------ drawing
    static byte[] RenderSta(Func<byte[]> draw)
    {
        byte[]? result = null; Exception? error = null;
        var t = new Thread(() => { try { result = draw(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (error != null) throw error;
        return result!;
    }

    static void DeadlockDrawTests()
    {
        const int W = Al80Protocol.Width, H = Al80Protocol.Height;
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var heroes = DeadlockParse.Heroes(J(HeroesJson)); var tiers = DeadlockParse.Tiers(J(RanksJson));
        var sum = DeadlockParse.Summarize(J(RankJson), J(HistoryJson), heroes, tiers, now);
        var px = RenderSta(() => Al80Imaging.RenderDeadlock(new DeadlockPanel(sum, null, false), now));
        Check("1.22 panel: a 96x160 BGRA picture", px.Length == W * H * 4);
        int Px(int x, int y, int ch) => px[(y * W + x) * 4 + ch];                // 0 = B, 1 = G, 2 = R
        Check("1.22 panel: not blank", Enumerable.Range(0, W * H).Count(i => px[i * 4 + 2] > 120 || px[i * 4 + 1] > 120) > 200);
        // form squares for [1,2,1,2]: x0 = (96 - (4*8 + 3*1.5)) / 2 = 29.75, squares 9.5 apart, y 122..130
        Check("1.22 panel: a win square is green", Px(34, 126, 1) > Px(34, 126, 2) + 40, $"R{Px(34, 126, 2)} G{Px(34, 126, 1)}");
        Check("1.22 panel: a loss square is red", Px(43, 126, 2) > Px(43, 126, 1) + 40, $"R{Px(43, 126, 2)} G{Px(43, 126, 1)}");
        var loading = RenderSta(() => Al80Imaging.RenderDeadlock(new DeadlockPanel(null, null, true), now));
        var failed = RenderSta(() => Al80Imaging.RenderDeadlock(new DeadlockPanel(null, "could not reach the Deadlock API: timeout", false), now));
        var stale = RenderSta(() => Al80Imaging.RenderDeadlock(new DeadlockPanel(sum, "the Deadlock API answered 503", false), now));
        Check("1.22 panel: loading, failed and stale states all draw and differ from each other", loading.Length == px.Length && failed.Length == px.Length && !loading.AsSpan().SequenceEqual(failed) && !stale.AsSpan().SequenceEqual(px));
        var empty = RenderSta(() => Al80Imaging.RenderDeadlock(new DeadlockPanel(DeadlockParse.Summarize(J("{}"), J("[]"), heroes, tiers, now), null, false), now));
        Check("1.22 panel: a player with no matches draws without error", empty.Length == px.Length);
        Check("1.22 panel: the panel key changes with the data and not with the clock", new DeadlockPanel(sum, null, false).Key == new DeadlockPanel(sum with { FetchedUtc = now.AddHours(1) }, null, false).Key
            && new DeadlockPanel(sum, null, false).Key != new DeadlockPanel(sum, "error", false).Key);
    }
}
