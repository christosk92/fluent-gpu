using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Wavee.Sdk;

namespace Wavee.Backend.Modules;

// ── MODULE FACTS → THE NOW-PLAYING PROJECTION ───────────────────────────────────────────────────────────────────────
// Two things only a module knows reach the player bar: whether what is playing is a LIVE broadcast, and what song a
// live broadcast is currently on. Both are per-playable overrides on the projection (the SetDurationOverride pattern),
// and both are wired identically pre-login and live — one relay, called from both composition points, so the two can
// never drift.

/// <summary>Wires a module host's live/metadata facts onto one <see cref="NowPlayingProjection"/>.</summary>
public sealed class ModuleProjectionRelay : IDisposable
{
    readonly NowPlayingProjection _projection;
    readonly ModuleHost? _host;
    readonly IDisposable? _changesSub;
    readonly Action<string, MetadataUpdate>? _onMetadata;
    readonly Action<string>? _onExpired;
    string? _lastUri;
    int _disposed;

    ModuleProjectionRelay(NowPlayingProjection projection, ModuleHost? host)
    {
        _projection = projection;
        _host = host;

        // LIVE-ness follows the CURRENT track, and only changes when the track does — the guard is what keeps
        // SetLiveOverride's own Changes fire from re-entering this subscription.
        _changesSub = projection.Changes.Subscribe(Observers.From<IPlaybackState>(OnProjectionChanged));

        if (host is null) return;
        _onMetadata = OnModuleMetadata;
        _onExpired = OnModuleExpired;
        host.MetadataChanged += _onMetadata;
        host.PlayableExpired += _onExpired;
    }

    /// <summary>Attach the relay. The caller owns the returned handle and disposes it with the session.</summary>
    /// <param name="projection">The projection to publish onto.</param>
    /// <param name="host">The module host whose notifications to relay; null wires the live-ness half only.</param>
    public static ModuleProjectionRelay Attach(NowPlayingProjection projection, ModuleHost? host)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return new ModuleProjectionRelay(projection, host);
    }

    /// <summary>Fired when a module says one of its locators expired AND that playable is the one playing — the app's
    /// cue to re-resolve and reload it (a YouTube url is IP-bound and dies after ~6 h; a Twitch token expires sooner).
    /// Argument: the playable uri.</summary>
    public event Action<string>? CurrentPlayableExpired;

    /// <summary>A live station's in-band "now playing" line, split and published for the CURRENT playable. Called by the
    /// audio host's <see cref="ILiveMetadataSource"/> relay; ignored unless the current playable really is live, so an
    /// ICY title can never overwrite the title of an ordinary track.</summary>
    /// <param name="rawStreamTitle">The station's raw <c>StreamTitle</c> value.</param>
    /// <param name="stationName">The station's own name, used as the attribution fallback.</param>
    public void OnLiveStreamTitle(string rawStreamTitle, string? stationName)
    {
        if (_projection.CurrentTrack?.Uri is not { Length: > 0 } uri) return;
        if (!_projection.IsLive && !ModulePlayables.IsLive(uri)) return;
        (string title, string? artist) = Wavee.Backend.Audio.IcyMetadata.SplitStreamTitle(rawStreamTitle);
        _projection.SetMetadataOverride(uri, title, artist ?? stationName);
    }

    void OnProjectionChanged(IPlaybackState state)
    {
        string? uri = state.CurrentTrack?.Uri;
        if (string.Equals(uri, _lastUri, StringComparison.Ordinal)) return;
        _lastUri = uri;
        _projection.SetLiveOverride(uri, ModulePlayables.IsLive(uri));
    }

    void OnModuleMetadata(string playableUri, MetadataUpdate update)
    {
        if (!string.Equals(_projection.CurrentTrack?.Uri, playableUri, StringComparison.Ordinal)) return;
        string? artist = update.Artists is { Length: > 0 } a ? string.Join(", ", a) : null;
        _projection.SetMetadataOverride(playableUri, update.Title, artist);
    }

    void OnModuleExpired(string playableUri)
    {
        if (!string.Equals(_projection.CurrentTrack?.Uri, playableUri, StringComparison.Ordinal)) return;
        CurrentPlayableExpired?.Invoke(playableUri);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_host is not null)
        {
            if (_onMetadata is { } m) _host.MetadataChanged -= m;
            if (_onExpired is { } e) _host.PlayableExpired -= e;
        }

        _changesSub?.Dispose();
    }
}

/// <summary>The bounded re-resolve policy for a module locator that died mid-play (an expired YouTube url, a rotated
/// Twitch token). Pure decision state so the retry ladder is testable without a media engine.</summary>
public sealed class ModuleReloadPolicy
{
    /// <summary>How many re-resolves one playable gets before the app stops trying and faults honestly.</summary>
    public const int MaxAttempts = 3;

    readonly Lock _gate = new();
    string? _uri;
    int _attempts;

    /// <summary>May this playable be re-resolved again right now? Counts the attempt when it answers true; a new uri
    /// resets the ladder (a different broadcast starts from zero).</summary>
    /// <param name="playableUri">The playable that failed.</param>
    public bool TryTakeAttempt(string? playableUri)
    {
        if (playableUri is not { Length: > 0 }) return false;
        lock (_gate)
        {
            if (!string.Equals(_uri, playableUri, StringComparison.Ordinal)) { _uri = playableUri; _attempts = 0; }
            if (_attempts >= MaxAttempts) return false;
            _attempts++;
            return true;
        }
    }

    /// <summary>How long to wait before the nth attempt: 0 s, 1 s, 4 s.</summary>
    /// <param name="attempt">1-based attempt number.</param>
    public static TimeSpan BackoffFor(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.Zero,
        2 => TimeSpan.FromSeconds(1),
        _ => TimeSpan.FromSeconds(4),
    };

    /// <summary>Attempts taken for the playable currently being retried.</summary>
    public int Attempts { get { lock (_gate) return _attempts; } }

    /// <summary>Forget the ladder (the playable started cleanly, or playback moved on).</summary>
    public void Reset() { lock (_gate) { _uri = null; _attempts = 0; } }

    /// <summary>Re-resolve one module playable and drop its cached video source, bounded by this policy.</summary>
    /// <param name="host">The module host.</param>
    /// <param name="playableUri">The playable to re-resolve.</param>
    /// <param name="ct">Cancels the wait and the resolve.</param>
    /// <returns>True when a fresh locator was obtained and the caller should reload.</returns>
    public async Task<bool> TryReResolveAsync(ModuleHost host, string playableUri, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!TryTakeAttempt(playableUri)) return false;
        TimeSpan wait = BackoffFor(Attempts);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        try
        {
            await host.ResolveAsync(playableUri, force: true, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }
}
