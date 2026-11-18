using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HwSuite.Hardware.Games;

/// <summary>One scored match from the Deadlock API's match history.</summary>
public sealed record DeadlockMatch(long MatchId, int HeroId, string Hero, int Kills, int Deaths, int Assists, int Outcome, DateTime StartUtc, int DurationS)
{
    public bool Won => Outcome == 1;
    public bool Lost => Outcome == 2;
}

/// <summary>
/// What the AL80 panel shows: rank plus the newest <see cref="DeadlockParse.Take"/> scored matches. The numbers come from the
/// Deadlock API (api.deadlock-api.com), the community data that Statlocker is built on, so they can differ slightly from what
/// statlocker.gg shows on its own page.
/// </summary>
public sealed record DeadlockSummary(int Tier, int Subrank, string TierName, int TierColor, int Wins, int Losses,
    double AvgKills, double AvgDeaths, double AvgAssists, IReadOnlyList<int> Form, DeadlockMatch? Last, DateTime FetchedUtc)
{
    public double WinPct => Wins + Losses == 0 ? 0 : 100.0 * Wins / (Wins + Losses);
    public double Kda => (AvgKills + AvgAssists) / Math.Max(1.0, AvgDeaths);
    public string Rank => Tier <= 0 ? "Unranked" : Subrank > 0 ? $"{TierName} {Subrank}" : TierName;
    /// <summary>Changes only when something the panel draws changes (not with the fetch time), so a refresh with the same data sends no picture.</summary>
    public string Key => $"{Tier}.{Subrank}|{Wins}-{Losses}|{AvgKills:0.0}/{AvgDeaths:0.0}/{AvgAssists:0.0}|{string.Join("", Form)}|{Last?.MatchId}";
}

/// <summary>Everything the screen needs: the newest data, why it could not be fetched, and whether the first fetch is still running.</summary>
public sealed record DeadlockPanel(DeadlockSummary? Summary, string? Error, bool Loading)
{
    public string Key => $"{Summary?.Key}|{(Error != null ? "e" : "")}|{(Loading && Summary == null ? "l" : "")}";
}

public sealed class DeadlockApiException : Exception
{
    public DeadlockApiException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Pure parsing and arithmetic for the Deadlock panel (no network), so it can be tested with fixed JSON.</summary>
public static class DeadlockParse
{
    /// <summary>How many of the newest scored matches the averages and the win/loss count use.</summary>
    public const int Take = 20;
    /// <summary>How many form squares the panel draws.</summary>
    public const int FormLength = 10;

    static int Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
    static long Long(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : 0;

    /// <summary><c>/v1/assets/heroes</c>: id and name of every hero.</summary>
    public static Dictionary<int, string> Heroes(JsonElement list)
    {
        var d = new Dictionary<int, string>();
        if (list.ValueKind != JsonValueKind.Array) return d;
        foreach (var h in list.EnumerateArray())
            if (h.ValueKind == JsonValueKind.Object && h.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) d[Int(h, "id")] = n.GetString() ?? "";
        return d;
    }

    /// <summary><c>/v1/assets/ranks</c>: tier number to name and colour (0xRRGGBB, -1 when the API gives none).</summary>
    public static Dictionary<int, (string Name, int Color)> Tiers(JsonElement list)
    {
        var d = new Dictionary<int, (string, int)>();
        if (list.ValueKind != JsonValueKind.Array) return d;
        foreach (var t in list.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
            int color = -1;
            if (t.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String
                && Regex.IsMatch(c.GetString() ?? "", "^#[0-9a-fA-F]{6}$")) color = int.Parse(c.GetString()![1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            d[Int(t, "tier")] = (n.GetString() ?? "", color);
        }
        return d;
    }

    /// <summary>
    /// Builds the summary from <c>/v1/players/{id}/rank</c> and <c>/v1/players/{id}/match-history</c>. Only matches the game scored for
    /// the player count (outcome 1 = win, 2 = loss); abandoned, penalized and unscored games are skipped.
    /// </summary>
    public static DeadlockSummary Summarize(JsonElement rank, JsonElement history, IReadOnlyDictionary<int, string> heroes,
        IReadOnlyDictionary<int, (string Name, int Color)> tiers, DateTime nowUtc)
    {
        int tier = Int(rank, "rank"), sub = Int(rank, "subrank");
        if (tier == 0 && Int(rank, "badge") >= 10) { tier = Int(rank, "badge") / 10; sub = Int(rank, "badge") % 10; }   // badge = tier * 10 + subrank
        var ms = new List<DeadlockMatch>();
        if (history.ValueKind == JsonValueKind.Array)
            foreach (var e in history.EnumerateArray())
            {
                int outcome = Int(e, "player_match_outcome");
                if (outcome is not (1 or 2)) continue;
                int hero = Int(e, "hero_id");
                ms.Add(new DeadlockMatch(Long(e, "match_id"), hero, heroes.TryGetValue(hero, out var hn) ? hn : $"Hero {hero}",
                    Int(e, "player_kills"), Int(e, "player_deaths"), Int(e, "player_assists"), outcome,
                    DateTimeOffset.FromUnixTimeSeconds(Int(e, "start_time")).UtcDateTime, Int(e, "match_duration_s")));
            }
        ms.Sort((a, b) => b.StartUtc.CompareTo(a.StartUtc));
        var recent = ms.Take(Take).ToList();
        int wins = recent.Count(m => m.Won), losses = recent.Count(m => m.Lost);
        double n = Math.Max(1, recent.Count);
        string name = tiers.TryGetValue(tier, out var t) ? t.Name : tier == 0 ? "Unranked" : $"Tier {tier}";
        int color = tiers.TryGetValue(tier, out var t2) ? t2.Color : -1;
        return new DeadlockSummary(tier, sub, name, color, wins, losses,
            recent.Sum(m => m.Kills) / n, recent.Sum(m => m.Deaths) / n, recent.Sum(m => m.Assists) / n,
            recent.Take(FormLength).Select(m => m.Outcome).ToList(), recent.FirstOrDefault(), nowUtc);
    }
}

/// <summary>
/// Reads the player's rank and match history from api.deadlock-api.com (free, no key; the player endpoints answer from stored history,
/// limited to 100 requests/s per IP). One refresh is two small requests; the hero and rank name lists are fetched once per run.
/// The only personal value sent is the Steam account number in the URL, which is what the lookup is for.
/// </summary>
public sealed class DeadlockClient : IDisposable
{
    public const string BaseUrl = "https://api.deadlock-api.com";
    readonly HttpClient _http;
    Dictionary<int, string>? _heroes;
    Dictionary<int, (string Name, int Color)>? _tiers;

    /// <summary>Tests pass a fake handler so nothing touches the network.</summary>
    public DeadlockClient(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(12);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VANTA-hardware-suite/1.22");
    }

    async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(BaseUrl + path, ct);
            if (!resp.IsSuccessStatusCode) throw new DeadlockApiException($"the Deadlock API answered {(int)resp.StatusCode} for {path.Split('?')[0]}");
            var text = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(text);
        }
        catch (DeadlockApiException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new DeadlockApiException("could not reach the Deadlock API: " + ex.Message, ex); }
    }

    public async Task<DeadlockSummary> FetchAsync(long accountId, CancellationToken ct, Func<DateTime>? now = null)
    {
        if (_heroes == null) { using var h = await GetAsync("/v1/assets/heroes", ct); _heroes = DeadlockParse.Heroes(h.RootElement); }
        if (_tiers == null) { using var r = await GetAsync("/v1/assets/ranks", ct); _tiers = DeadlockParse.Tiers(r.RootElement); }
        using var rank = await GetAsync($"/v1/players/{accountId}/rank", ct);
        using var hist = await GetAsync($"/v1/players/{accountId}/match-history", ct);
        return DeadlockParse.Summarize(rank.RootElement, hist.RootElement, _heroes, _tiers, (now ?? (() => DateTime.UtcNow))());
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Finds the Steam account to look up without asking: the one signed in most recently on this PC.</summary>
public static class SteamAccount
{
    const long Steam64Base = 76561197960265728L;

    public static long AccountIdFromSteam64(long steam64) => steam64 - Steam64Base;

    /// <summary>The account number (Steam32) of the "MostRecent" entry in Steam's loginusers.vdf text, or null.</summary>
    public static long? MostRecentAccountId(string vdf)
    {
        foreach (Match m in Regex.Matches(vdf, "\"(7656\\d{13})\"\\s*\\{([^{}]*)\\}"))
            if (Regex.IsMatch(m.Groups[2].Value, "\"MostRecent\"\\s+\"1\"") && long.TryParse(m.Groups[1].Value, out var id64))
                return AccountIdFromSteam64(id64);
        return null;
    }

    /// <summary>Reads Steam's own login list on this PC (account numbers and names only; no passwords or tokens are stored there).</summary>
    public static long? FindLocal()
    {
        try
        {
            var root = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ?? @"C:\Program Files (x86)\Steam";
            var path = Path.Combine(root, "config", "loginusers.vdf");
            return File.Exists(path) ? MostRecentAccountId(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }
}
