using System.Text.Json;
using HwSuite.Core;
using HwSuite.Hardware.Al80;

namespace HwSuite.Hardware.Games;

/// <summary>
/// 1.22: while Deadlock runs, the lighting brightness goes to the profile's level and the AL80 screen shows the player's rank and
/// recent matches. When the game exits everything goes back.
///
/// Rules:
/// - Opt-in per profile (<see cref="GameProfilesConfig.DeadlockEnabled"/>). Nothing is fetched and nothing changes while the game is closed.
/// - Brightness: raised only if it is lower than the profile's level. On exit it is restored only if it is still the level this
///   profile set, so a brightness the owner changed during the game is kept. The change goes through the same configuration the
///   Lighting page edits (and is saved), so a crash mid-game must not leave it raised: the previous value is kept in a small
///   state file and restored at the next start.
/// - The AL80 screen uses a runtime-only override of the screen mode (never saved); every picture still follows the existing
///   rules (control switch on, flash slots confirmed full, minimum gap between pictures).
/// - Stats come from the Deadlock API (the data Statlocker is built on) every <see cref="GameProfilesConfig.DeadlockRefreshMin"/> minutes
///   while the game runs. A failed refresh keeps the last data and shows the reason.
/// - An unreadable process list is "unknown", which keeps the current state instead of flipping it.
/// </summary>
public sealed class GameProfileService : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    readonly AppConfig _cfg;
    readonly Func<Al80Service?> _al80;
    readonly Action _save;
    readonly string _statePath;
    readonly bool _useAppData;          // the owner's real data folder: test runs (ConfigStore.ReadOnly) leave it alone
    readonly Func<DeadlockClient> _clientFactory;
    readonly Func<long?> _accountFinder;
    readonly Func<string, ProcessPresence> _presenceFactory;
    readonly Dictionary<string, ProcessPresence> _presence = new(StringComparer.OrdinalIgnoreCase);
    Timer? _timer;
    CancellationTokenSource? _cts;
    DeadlockClient? _client;
    double _prevBrightness, _gameBrightness;
    bool _statsOn;
    volatile DeadlockSummary? _summary;
    volatile string? _error;
    volatile bool _loading;
    int _busy;

    public bool Active { get; private set; }
    public DeadlockSummary? Summary => _summary;
    public string? Error => _error;
    public event Action? Changed;
    /// <summary>The previous brightness was restored at start-up after a run that ended during a game (for the log and the tests).</summary>
    public bool RecoveredAtStart { get; private set; }

    public GameProfileService(AppConfig cfg, Func<Al80Service?> al80, Action save, string? stateDir = null,
        Func<DeadlockClient>? client = null, Func<long?>? accountFinder = null, Func<string, ProcessPresence>? presence = null)
    {
        _cfg = cfg; _al80 = al80; _save = save;
        _useAppData = stateDir == null;
        _statePath = Path.Combine(stateDir ?? AppPaths.Data, "game-profile-state.json");
        _clientFactory = client ?? (() => new DeadlockClient());
        _accountFinder = accountFinder ?? SteamAccount.FindLocal;
        _presenceFactory = presence ?? (n => new ProcessPresence(n, startTimer: false));
        Recover();
    }

    public string Status
    {
        get
        {
            var g = _cfg.Games;
            if (!g.DeadlockEnabled) return "Deadlock profile is off";
            if (!Active) return "Waiting for Deadlock (" + string.Join(" / ", g.DeadlockExes) + ") to start";
            string s = $"Deadlock is running · lights at {_cfg.Rgb.Brightness * 100:0}%";
            if (!g.DeadlockStats) return s;
            if (_summary is { } d)
            {
                s += $" · {d.Rank}, {d.Wins}W {d.Losses}L, updated {d.FetchedUtc.ToLocalTime():HH:mm}";
                if (_error != null) s += " (last refresh failed: " + _error + ")";
            }
            else s += _error != null ? " · stats: " + _error : " · loading stats…";
            return s;
        }
    }

    public void Start()
    {
        if (_timer != null) return;
        _timer = new Timer(_ => { try { Tick(); } catch (Exception ex) { Log.Warn("game profile: " + ex.Message); } }, null, TimeSpan.Zero, PollInterval);
    }

    ProcessPresence PresenceFor(string exe)
    {
        lock (_presence)
        {
            if (!_presence.TryGetValue(exe, out var p)) _presence[exe] = p = _presenceFactory(exe);
            return p;
        }
    }

    /// <summary>One look at the process list (the timer calls this every <see cref="PollInterval"/>; tests call it directly).</summary>
    public void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var g = _cfg.Games;
            bool running = false, unknown = false;
            if (g.DeadlockEnabled)
                foreach (var exe in g.DeadlockExes.Where(e => !string.IsNullOrWhiteSpace(e)))
                {
                    var p = PresenceFor(exe);
                    p.CheckNow();
                    if (p.LastState == ProcessPresence.State.Running) running = true;
                    else if (p.LastState == ProcessPresence.State.Unknown) unknown = true;
                }
            if (running && !Active) Activate();
            else if (!running && Active && !unknown) Deactivate();
            bool wantStats = Active && g.DeadlockStats;
            if (wantStats && !_statsOn) StartStats();
            else if (!wantStats && _statsOn) StopStats();
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    void Activate()
    {
        double target = Math.Clamp(_cfg.Games.DeadlockBrightness, 0.05, 1.0);
        lock (_cfg.Sync)
        {
            _prevBrightness = _cfg.Rgb.Brightness;
            _gameBrightness = Math.Max(_prevBrightness, target);
            _cfg.Rgb.Brightness = _gameBrightness;
        }
        WriteState();
        _save();
        Active = true;
        Log.Info($"game profile: Deadlock started, lighting brightness {_prevBrightness * 100:0}% -> {_gameBrightness * 100:0}%");
        Changed?.Invoke();
    }

    void Deactivate()
    {
        Active = false;
        StopStats();
        bool restore;
        lock (_cfg.Sync)
        {
            restore = Math.Abs(_cfg.Rgb.Brightness - _gameBrightness) < 0.005;
            if (restore) _cfg.Rgb.Brightness = _prevBrightness;
        }
        DeleteState();
        _save();
        Log.Info("game profile: Deadlock closed, " + (restore ? $"lighting brightness back to {_prevBrightness * 100:0}%" : "lighting brightness was changed during the game, so it is left as it is"));
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ stats
    void StartStats()
    {
        _statsOn = true;
        _loading = true;
        _cts = new CancellationTokenSource();
        _al80()?.SetPanelOverride(() => new DeadlockPanel(_summary, _error, _loading));
        var ct = _cts.Token;
        _ = Task.Run(() => RefreshLoop(ct));
        Changed?.Invoke();
    }

    void StopStats()
    {
        if (!_statsOn) return;
        _statsOn = false;
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _al80()?.SetPanelOverride(null);
        _summary = null; _error = null; _loading = false;
        Changed?.Invoke();
    }

    async Task RefreshLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await FetchOnce(ct);
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _cfg.Games.DeadlockRefreshMin)), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One refresh. Public so tests can run it without the timer.</summary>
    public async Task FetchOnce(CancellationToken ct)
    {
        long? id = _cfg.Games.DeadlockAccountId ?? _accountFinder();
        if (id == null) { _error = "no Steam account found on this PC: set the account number in the config"; _loading = false; Changed?.Invoke(); return; }
        try
        {
            _client ??= _clientFactory();
            var s = await _client.FetchAsync(id.Value, ct);
            _summary = s; _error = null;
            Log.Info($"game profile: Deadlock stats {s.Rank}, {s.Wins}W {s.Losses}L over the last {s.Wins + s.Losses} scored matches");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            _error = ex.Message;
            Log.Warn("game profile: Deadlock stats failed: " + ex.Message);
        }
        _loading = false;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ crash safety
    sealed record State(double Prev, double Game);

    void WriteState()
    {
        if (_useAppData && ConfigStore.ReadOnly) return;
        try { File.WriteAllText(_statePath, JsonSerializer.Serialize(new State(_prevBrightness, _gameBrightness))); } catch { }
    }

    void DeleteState() { try { if (File.Exists(_statePath)) File.Delete(_statePath); } catch { } }

    /// <summary>A previous run ended while the game profile held the brightness up: put the earlier value back, unless it was changed since.</summary>
    void Recover()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var st = JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath));
            if (st != null)
            {
                bool restore;
                lock (_cfg.Sync)
                {
                    restore = Math.Abs(_cfg.Rgb.Brightness - st.Game) < 0.005 && st.Prev < st.Game;
                    if (restore) _cfg.Rgb.Brightness = st.Prev;
                }
                if (restore) { _save(); RecoveredAtStart = true; Log.Warn($"game profile: the last run ended during a game; lighting brightness restored to {st.Prev * 100:0}%"); }
            }
            DeleteState();
        }
        catch (Exception ex) { Log.Warn("game profile: could not read the saved state: " + ex.Message); }
    }

    public void Dispose()
    {
        try { _timer?.Dispose(); } catch { }
        _timer = null;
        if (Active) { try { Deactivate(); } catch (Exception ex) { Log.Warn("game profile: " + ex.Message); } }
        else StopStats();
        lock (_presence) { foreach (var p in _presence.Values) p.Dispose(); _presence.Clear(); }
        try { _client?.Dispose(); } catch { }
    }
}
