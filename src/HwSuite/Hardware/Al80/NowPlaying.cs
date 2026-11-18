using HwSuite.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace HwSuite.Hardware.Al80;

/// <summary>What a player reports through Windows' media controls (the same source as the volume flyout).</summary>
public sealed record NowPlayingInfo(string App, string Title, string Artist, string Album, bool Playing, byte[]? Art, string ArtHash = "")
{
    /// <summary>Changes only when what the screen draws changes (title, artist, play/pause, which cover).</summary>
    public string Key => $"{Title}\u0001{Artist}\u0001{Playing}\u0001{ArtHash}";
}

/// <summary>
/// 1.21: reads the current Spotify track from Windows' Global System Media Transport Controls (verified 2026-11-17: Spotify.exe
/// publishes title, artist, album and a cover thumbnail there). Polls on the thread pool every 1.5 s while started; nothing
/// is sent anywhere, the cover is read from Windows, not downloaded. <see cref="Latest"/> is null when Spotify has no session.
/// 1.21.1: on 2026-11-17/18 about 1 track in 13 never got its cover in VANTA (40-min diagnostic could not reproduce it), so a
/// failed cover read is logged with its reason, retried every poll, and after 3 failures the media-controls connection is
/// reopened. A cover identical to the previous track's (different album) is held back for up to 3 s as possibly stale.
/// </summary>
public sealed class NowPlayingWatcher : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan StaleHold = TimeSpan.FromSeconds(3);
    readonly Func<string, bool> _match;
    Timer? _timer;
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    int _busy;
    // all below: only touched inside Poll (guarded by _busy)
    string? _artFor, _artHash, _trackKey, _prevTrackHash, _prevAlbum;
    byte[]? _art;
    int _artFails;
    DateTime _trackSince;

    /// <summary><paramref name="match"/> picks the session by its app id (default: Spotify).</summary>
    public NowPlayingWatcher(Func<string, bool>? match = null)
    {
        _match = match ?? (id => id.Contains("spotify", StringComparison.OrdinalIgnoreCase));
    }

    public volatile NowPlayingInfo? Latest;
    public volatile string? Error;
    public bool Running => _timer != null;
    public long CoverFailures;
    public long Reconnects;

    public void Start()
    {
        if (_timer != null) return;
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, Interval);
    }

    public void Stop()
    {
        _timer?.Dispose(); _timer = null;
        Latest = null;
    }

    void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var t = ReadAsync();
            if (!t.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("media controls did not answer in 10 s");
            Latest = t.Result; Error = null;
        }
        catch (Exception ex) { Error = (ex as AggregateException)?.InnerException?.Message ?? ex.Message; Latest = null; _mgr = null; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    async Task<NowPlayingInfo?> ReadAsync()
    {
        _mgr ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var s = _mgr.GetSessions().FirstOrDefault(x => _match(x.SourceAppUserModelId ?? ""));
        if (s == null) return null;
        var p = await s.TryGetMediaPropertiesAsync();
        if (p == null || string.IsNullOrWhiteSpace(p.Title)) return null;
        var status = s.GetPlaybackInfo()?.PlaybackStatus;
        string track = p.Title + "\u0001" + p.Artist + "\u0001" + p.AlbumTitle;
        if (track != _trackKey)
        {
            // remember the outgoing track's cover for the stale check
            if (_trackKey != null) { _prevTrackHash = _artHash; _prevAlbum = _trackKey.Split('\u0001')[2]; }
            _trackKey = track; _trackSince = DateTime.UtcNow; _artFails = 0;
        }
        if (_artFor != track || _art == null)
        {
            var (a, why) = await ReadThumb(p.Thumbnail);
            string? h = a != null ? Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(a))[..12] : null;
            if (a != null && h == _prevTrackHash && (p.AlbumTitle ?? "") != (_prevAlbum ?? "") && DateTime.UtcNow - _trackSince < StaleHold)
            { a = null; h = null; why = "same cover as the previous track (held back as possibly stale)"; }
            if (a == null)
            {
                _artFails++;
                if (_artFails == 1 || _artFails == 3 || _artFails % 20 == 0)
                    Log.Warn($"AL80 now playing: no cover for '{p.Title}' (attempt {_artFails}): {why}");
                if (_artFails % 3 == 0 && why != null && !why.StartsWith("same cover")) { _mgr = null; Reconnects++; }   // fresh connection on the next poll
                Interlocked.Increment(ref CoverFailures);
            }
            else if (_artFails > 0) Log.Info($"AL80 now playing: cover for '{p.Title}' arrived after {_artFails} failed attempt(s)");
            _art = a; _artHash = h; _artFor = a != null ? track : null;
        }
        return new NowPlayingInfo(s.SourceAppUserModelId ?? "", p.Title, p.Artist ?? "", p.AlbumTitle ?? "",
            status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing, _art, _artHash ?? "");
    }

    static async Task<(byte[]?, string?)> ReadThumb(IRandomAccessStreamReference? r)
    {
        if (r == null) return (null, "Spotify reported no thumbnail");
        try
        {
            using var st = await r.OpenReadAsync();
            if (st.Size == 0) return (null, "thumbnail stream is empty");
            if (st.Size > 8 * 1024 * 1024) return (null, $"thumbnail too large ({st.Size} bytes)");
            using var ms = new MemoryStream();
            await st.AsStreamForRead().CopyToAsync(ms);
            if ((ulong)ms.Length != st.Size) return (null, $"thumbnail read short ({ms.Length} of {st.Size} bytes)");
            return (ms.ToArray(), null);
        }
        catch (Exception ex) { return (null, "thumbnail read failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    public void Dispose() => Stop();
}
