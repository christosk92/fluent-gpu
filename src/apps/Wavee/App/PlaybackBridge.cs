using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee;

/// <summary>Atomic, equality-gated identity used by high-fanout playback matchers (rows/cards/lyrics). It retains the
/// rich track for album/artist matching but compares only the identity fields that can change a match, so metadata-only
/// refreshes do not invalidate every visible consumer.</summary>
public readonly struct PlaybackIdentity : IEquatable<PlaybackIdentity>
{
    public string? ContextUri { get; }
    public Track? Track { get; }
    readonly int _membershipHash;

    public PlaybackIdentity(string? contextUri, Track? track)
    {
        ContextUri = contextUri;
        Track = track;
        int h = 17;
        h = unchecked(h * 31 + StringComparer.Ordinal.GetHashCode(track?.Id ?? ""));
        h = unchecked(h * 31 + StringComparer.Ordinal.GetHashCode(track?.Album.Uri ?? ""));
        if (track is { } t)
            for (int i = 0; i < t.Artists.Count; i++)
                h = unchecked(h * 31 + StringComparer.Ordinal.GetHashCode(t.Artists[i].Uri ?? ""));
        _membershipHash = h;
    }

    public bool Equals(PlaybackIdentity other)
    {
        if (!StringComparer.Ordinal.Equals(ContextUri, other.ContextUri)
            || !StringComparer.Ordinal.Equals(Track?.Id, other.Track?.Id)
            || !StringComparer.Ordinal.Equals(Track?.Album.Uri, other.Track?.Album.Uri))
            return false;
        int count = Track?.Artists.Count ?? 0;
        if (count != (other.Track?.Artists.Count ?? 0)) return false;
        for (int i = 0; i < count; i++)
            if (!StringComparer.Ordinal.Equals(Track!.Artists[i].Uri, other.Track!.Artists[i].Uri))
                return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is PlaybackIdentity other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(
        ContextUri is null ? 0 : StringComparer.Ordinal.GetHashCode(ContextUri),
        _membershipHash);
    public static bool operator ==(PlaybackIdentity left, PlaybackIdentity right) => left.Equals(right);
    public static bool operator !=(PlaybackIdentity left, PlaybackIdentity right) => !left.Equals(right);
}

/// <summary>
/// THE single boundary between framework-neutral <c>Wavee.Core</c> (<see cref="IObservable{T}"/>) and the engine's
/// reactive <see cref="Signal{T}"/>. It subscribes to the Core observables and, marshaling every callback onto the UI
/// thread via the post delegate, writes the matching signal. Components read the signals; intents flow back as explicit
/// <see cref="IPlaybackPlayer"/> command calls (optimistic writes set the signal first, then the bridge reconciles).
/// No view ever holds authoritative state. Provided once at the app root via <see cref="Slot"/>.
/// </summary>
public sealed class PlaybackBridge
{
    /// <summary>Context slot — provide at the root, read with <c>UseContext(PlaybackBridge.Slot)</c>.</summary>
    public static readonly Context<PlaybackBridge?> Slot = new(null);

    readonly IPlaybackPlayer _player;
    readonly IPlaybackState _state;
    readonly IConnectDevices _devices;
    readonly ISpotifySession _session;
    readonly List<IDisposable> _subs = [];
    bool _active;
    // Optional store probe for async per-track enrichment (the music-video association lands AFTER the track resolves).
    // Wired by the live bootstrap via AttachStore; null on the fake backend → CurrentTrackHasVideo stays false.
    IStore? _store;
    Action<Action>? _post;
    bool _storeWired;
    string? _lastQueueDiagSig;
    // Queue-revision content fold (drives QueueRevision — see the signal). Bumps a monotonic counter only when the fold
    // changes, so the queue panel remounts iff its visible set actually differs (no thrash on volume/position/metadata).
    ulong _queueContentFold;
    bool _haveQueueFold;
    long _queueRev;
    Action? _playbackErrorAction;
    long _playbackErrorActionToken;
    // Seek latch (#2): a seek is applied by the engine ASYNCHRONOUSLY, so a stale pre-seek PositionTick can land between
    // the optimistic paint and the engine catching up — snapping the slider back to the old spot for a frame. While the
    // latch is live we drop incoming position ticks that are still far from the target, and release it once a tick lands
    // near the target (the seek took) or the window expires. UI-thread only (every writer is post-marshalled).
    long _seekLatchTargetMs = -1;
    long _seekLatchDeadlineTick;
    const long SeekLatchWindowMs = 1200;   // max time to suppress stale ticks after a seek
    const long SeekLatchToleranceMs = 750;  // a tick within this of the target = the seek landed → release
    // OS media surfaces (SMTC: lock screen, now-playing flyout, hardware media keys) mirrored from the unified state below.
    // Null when the platform refuses it or before Activate; every push is then a no-op.
    SystemMediaControlsBridge? _smtc;

    // ── UI signals (read by components) ─────────────────────────────────────────────────────────────────────────────
    public Signal<Track?> CurrentTrack { get; } = new(null);
    /// <summary>The currently-playing context uri (playlist/album/liked) — content cards compare their own uri to this
    /// to show the now-playing equalizer.</summary>
    public Signal<string?> CurrentContext { get; } = new(null);
    /// <summary>Atomic context + match-relevant track identity for high-fanout consumers. Prefer this over separately
    /// observing <see cref="CurrentTrack"/> and <see cref="CurrentContext"/> when only now-playing matching is needed.</summary>
    public Signal<PlaybackIdentity> Identity { get; } = new(default);
    /// <summary>Coarse "is anything playing?" gate for the very-high-fanout now-playing matchers. True iff there is an
    /// active context OR a current track — i.e. iff any card COULD match (see <c>NowPlayingOverlay.Matches</c>, which
    /// returns false when both are empty). While idle (the common case at page-load, when ~70 card overlays mount at
    /// once) a cold overlay subscribes to THIS one bool instead of the hot <see cref="Identity"/>, so it neither runs
    /// <c>Matches</c> nor joins Identity's fanout until playback actually starts. Equality-gated by the signal setter, so
    /// idle→idle refreshes never notify.</summary>
    public Signal<bool> HasActiveContext { get; } = new(false);
    public Signal<bool> IsPlaying { get; } = new(false);
    public Signal<bool> IsBuffering { get; } = new(false);
    public Signal<PlaybackRecoveryKind> RecoveryKind { get; } = new(PlaybackRecoveryKind.None);
    // Player-bar display states the IPlaybackState snapshot doesn't carry yet (the real provider drives these; default
    // off, so the fake/in-process path is unchanged). Loading = the initial track resolve before audio; Error = a
    // non-null user-facing message (the bar shows it + offers retry on the primary). See PlayerBar.PlayerState.
    public Signal<bool> IsLoading { get; } = new(false);
    public Signal<string?> Error { get; } = new(null);
    public Signal<bool> HasPlaybackErrorAction { get; } = new(false);
    // Stage G — skip gating + the active Connect device (drives the prev/next enable state + the "playing on X" label).
    public Signal<bool> CanSkipNext { get; } = new(true);
    public Signal<bool> CanSkipPrev { get; } = new(true);
    public Signal<bool> CanSeek { get; } = new(true);
    public Signal<string?> ActiveDeviceId { get; } = new(null);
    public Signal<bool> IsShuffle { get; } = new(false);
    public Signal<RepeatMode> Repeat { get; } = new(RepeatMode.Off);
    public FloatSignal PositionFrac { get; } = new(0f);
    public FloatSignal Volume { get; } = new(0.7f);
    public Signal<long> PositionMs { get; } = new(0L);
    public Signal<long> DurationMs { get; } = new(0L);
    public Signal<Palette?> TrackPalette { get; } = new(null);
    public Signal<IReadOnlyList<QueueEntry>> Queue { get; } = new(Array.Empty<QueueEntry>());
    /// <summary>A monotonic queue revision — bumped only when the published queue's CONTENT changes (count/identity/bucket/
    /// provider), not on metadata enrichment or unrelated state ticks. The queue-panel keys its bound-list remount on this
    /// (replaces the old UI-side content-hash): one value that changes iff the visible SET must be rebuilt. Covers both the
    /// active-session snapshot cadence and viewer-mode cluster folds.</summary>
    public Signal<long> QueueRevision { get; } = new(0L);
    public Signal<IReadOnlyList<PlaybackDevice>> Devices { get; } = new(Array.Empty<PlaybackDevice>());
    public Signal<AuthStatus> Auth { get; } = new(AuthStatus.LoggedOut);
    public Signal<WaveeUser?> User { get; } = new(null);
    /// <summary>The rich login projection driving the full-screen login takeover (device-code / QR / phase). Fed by the
    /// live bootstrap through <see cref="Progress"/>; the coarse <see cref="Auth"/> still gates shell ↔ takeover.</summary>
    public Signal<LoginSnapshot> Login { get; } = new(new(LoginPhase.LoggedOut));

    /// <summary>The now-playing track has an accompanying music video (the <c>VideoService</c> association, detected
    /// asynchronously after the track resolves). Drives the player-bar video button's visibility. Fed by the optional
    /// store probe (<see cref="AttachStore"/>); the fake backend has none, so it stays false.</summary>
    public Signal<bool> CurrentTrackHasVideo { get; } = new(false);
    /// <summary>
    /// THE complete state of the now-playing video surface — intent (<c>Requested</c>/<c>Preferred</c>), reality
    /// (<c>Live</c>), what is possible right now (<c>Available</c>), and the per-track dismiss — as ONE value behind ONE
    /// signal with ONE owner. Everything that used to be a separate flag (a sticky <c>PreferVideo</c> bool, a placement
    /// enum, a dismiss generation, and a view-local window handle in the player bar) is now a field of this state, which
    /// is precisely why the surfaces, the detached-window owner and the player-bar button can no longer disagree —
    /// a stuck toggle is not "fixed", it is unrepresentable. The rules live in the pure, unit-tested
    /// <see cref="PlacementCore"/>.
    ///
    /// <para>READ it through <see cref="VideoActive"/> / <see cref="VideoPlacementNow"/> (both subscribe, so a
    /// <c>Render</c> caller re-derives automatically). WRITE it ONLY through the intent methods below — never assign a
    /// placement directly, because the intents also swap what is actually PLAYING (a bare state write would light a
    /// surface while the song kept playing underneath it).</para>
    /// </summary>
    public Signal<PlacementState> VideoSurface { get; } = new(PlacementState.Initial(PlacementPolicy.Video));

    /// <summary>The settings store, wired at composition. Only the PREFERRED placement is persisted here — never whether
    /// a video is running (a launch must not resume one) and never Fullscreen (a mode, not a home).</summary>
    public IAppSettings? Settings;

    /// <summary>Seed the video surface from persisted settings. Call ONCE at composition, before the first frame, so the
    /// remembered placement is already in place rather than popping in after the shell mounts.</summary>
    public void SeedVideoSurfaceFromSettings(IAppSettings settings)
    {
        Settings = settings;
        var preferred = PlacementPersistence.LoadPlacement(settings.Get(WaveeSettings.VideoPreferredPlacement), PlacementPolicy.Video);
        var s = VideoSurface.Peek();
        if (s.Preferred != preferred) VideoSurface.Value = s with { Preferred = preferred };
    }

    /// <summary>The placement that should be mounted right now, or <see cref="SurfacePlacement.None"/> for "nothing".
    /// This is the ONE thing every video surface gates on (each mounts iff it is the resolved placement) and the ONE
    /// thing the player-bar affordance reflects.</summary>
    public SurfacePlacement VideoPlacementNow() => PlacementCore.Resolve(VideoSurface.Value);

    /// <summary>Whether the video is live at all: the media should be video and the primary affordance is lit. Derived
    /// from <see cref="VideoSurface"/> — never a standalone flag.</summary>
    public bool VideoActive() => PlacementCore.IsActive(VideoSurface.Value);

    /// <summary>How much bottom space the floating surface currently claims from the page content, in DIP (0 = none).
    /// A floating surface RESERVES layout while it sits at its default anchor — the content area shrinks by exactly its
    /// height, so it can never cover anything — and gives the reservation up the moment the user DRAGS it, because
    /// putting it somewhere specific is them saying they want it there. Written by the surface, read by
    /// <c>ContentHost</c>.</summary>
    public Signal<float> FloatingSurfaceReserve { get; } = new(0f);

    /// <summary>The PLAYBACK-side predicate the backend asks per playable (<c>PlaybackController.ShouldPlayAsVideo</c>): the
    /// SAME single rule as <see cref="VideoActive"/>, but scoped to <em>this</em> track and read WITHOUT subscribing (Peek),
    /// because the controller calls it from a playback/dealer thread where a reactive subscription would be meaningless.
    /// has-video comes from the hydrated <see cref="Track.HasVideo"/> or — for the now-playing track — from the
    /// async-detected <see cref="CurrentTrackHasVideo"/> signal (the association routinely lands after the track resolved;
    /// <see cref="RecomputeHasVideo"/> then asks the controller to re-evaluate, so the swap is not deferred to the next
    /// track). Never returns true for a track the user has dismissed video for, so the ✕ really does route back to audio.</summary>
    public bool ShouldPlayAsVideo(Track track)
    {
        bool hasVideo = track.HasVideo
            || (string.Equals(CurrentTrack.Peek()?.Uri, track.Uri, StringComparison.Ordinal) && CurrentTrackHasVideo.Peek());
        // Same state, same rules — only the AVAILABILITY input is swapped for that track's, so this asks "what would be
        // resolved if THIS track were playing?" without mutating anything.
        return PlacementCore.ResolveWith(VideoSurface.Peek(), AvailabilityFor(hasVideo)) != SurfacePlacement.None;
    }

    /// <summary>Content availability → the placement set. A track WITHOUT a video makes every placement unavailable, so
    /// the surface hides and the media stays audio through the exact same path a host limitation would take. (Host
    /// capability — "can a second window be opened at all?" — folds in here too once that seam exists.)</summary>
    static PlacementSet AvailabilityFor(bool hasVideo) => hasVideo ? PlacementPolicy.Video.Allowed : PlacementSet.None;

    /// <summary>Ask the backend to re-evaluate the CURRENT playable's media kind right now (wired at composition to
    /// <c>PlaybackController.RefreshCurrentMediaKindAsync</c>; null on the fake backend / with the video-host kill switch off).
    /// Every writer of the video INTENT calls it, so "watch video" / "switch to audio" / the surface ✕ swap the media host for
    /// the track that is already playing instead of only taking effect at the next track boundary.</summary>
    public Action? RequestMediaKindRefresh;

    /// <summary>
    /// The ONE write path for <see cref="VideoSurface"/>. Publishes the new state and then does the two things a bare
    /// signal write cannot: when the surface turns ON or OFF it asks the backend to re-evaluate the current media kind
    /// (so "watch video" swaps what is PLAYING for the track already playing, instead of lighting a surface over a
    /// still-audio stream), and when it turns on it kicks the source resolve. Moving between placements is neither —
    /// the media and the source are already right, which is what keeps a move from restarting the video.
    /// </summary>
    void CommitVideoSurface(in PlacementState after)
    {
        var before = VideoSurface.Peek();
        if (after.Equals(before)) return;
        bool wasActive = PlacementCore.IsActive(before), isActive = PlacementCore.IsActive(after);
        VideoSurface.Value = after;
        // Remember where the user likes to watch (only when it actually changed — this runs on availability edges and
        // track changes too, and those must not rewrite the preference).
        if (after.Preferred != before.Preferred && Settings is { } settings)
        {
            var stored = PlacementPersistence.SavePlacement(after.Preferred);
            if (stored.Length > 0) settings.Set(WaveeSettings.VideoPreferredPlacement, stored);
        }
        if (isActive != wasActive) RequestMediaKindRefresh?.Invoke();
        if (isActive && !wasActive) RequestPopOutSource(CurrentTrack.Peek()?.Uri);
    }

    /// <summary>The PRIMARY video affordance, and it is symmetric: lit → off (from ANY placement), unlit → open at the
    /// user's preferred placement. Nothing else is needed to guarantee the toggle can always be turned off.</summary>
    public void ToggleVideo() => CommitVideoSurface(PlacementCore.TogglePrimary(VideoSurface.Peek()));

    /// <summary>Show the video at a specific placement (the surface picker). Also clears a per-track dismiss and adopts
    /// the target as the preferred home, so the primary button and the next track follow the user there.</summary>
    public void ShowVideoAt(SurfacePlacement placement) => CommitVideoSurface(PlacementCore.OpenAt(VideoSurface.Peek(), placement));

    /// <summary>Turn video off entirely (the menu's "turn off video"): the surface goes away and the media swaps back to
    /// the song's own audio. The preferred placement is remembered for the next time.</summary>
    public void TurnVideoOff() => CommitVideoSurface(PlacementCore.TurnOff(VideoSurface.Peek()));

    /// <summary>Dismiss the video for the CURRENT track (a surface's own ✕): audio keeps playing, the surface hides, and
    /// the standing intent survives — the next track brings it straight back. Routes the media back to AUDIO for this
    /// track (otherwise the video's soundtrack would keep playing behind a hidden surface).</summary>
    public void DismissVideoForCurrentTrack() => CommitVideoSurface(PlacementCore.DismissForContent(VideoSurface.Peek()));

    /// <summary>Clear a per-track dismiss so the video can light up again for the current track.</summary>
    public void RestoreVideo() => CommitVideoSurface(PlacementCore.Restore(VideoSurface.Peek()));

    /// <summary>A surface reports that the USER closed it by its own chrome (the pop-out's OS ✕ / Alt+F4). Closing the
    /// detached window means "not in a separate window", not "stop watching", so it falls back to the mini player —
    /// the transition that used to be missing entirely, leaving the toggle lit with no surface behind it. A close for a
    /// placement that is no longer resolved is stale and inert (see <see cref="PlacementCore.HostClosed"/>).</summary>
    public void NotifyVideoSurfaceClosed(SurfacePlacement closed) => CommitVideoSurface(PlacementCore.HostClosed(VideoSurface.Peek(), closed));

    /// <summary>Surface-only: report whether <paramref name="surface"/> — the CALLER'S OWN placement — is mounted right
    /// now. Never changes intent; it maintains the reality half of the state so the model can tell "asked for" from
    /// "has". Scoped per surface (<see cref="PlacementCore.LiveAfterReport"/>): a surface can claim reality for itself
    /// and release only its own claim, so the two surfaces watching this state cannot overwrite each other's.</summary>
    public void SetVideoSurfaceLive(SurfacePlacement surface, bool mounted)
    {
        var s = VideoSurface.Peek();
        var live = PlacementCore.LiveAfterReport(s.Live, surface, mounted);
        if (live != s.Live) VideoSurface.Value = PlacementCore.WithLive(s, live);
    }

    /// <summary>The resolved video source for the now-playing track (null = none resolved yet): a clear/Canvas URL or a
    /// PlayReady DRM descriptor + license relay. It is the surfaces' CONTENT IDENTITY (they key their subtree on
    /// <c>Key</c>); the player itself is owned by <c>FluentVideoMediaHost</c> and reaches them via <see cref="VideoPlayer"/>.
    /// The Spotify video-resolution layer (Canvas from the feed; PlayReady once the probe confirms it) populates it.
    /// Reset to null on every track change, EXCEPT when the playback path already resolved+published it for the new track.</summary>
    public Signal<Wavee.SpotifyLive.PopOutVideoSource?> PopOutVideoSource { get; } = new(null);

    /// <summary>The video-resolution delegate (track uri → a playable <c>PopOutVideoSource</c>), wired by the live
    /// bootstrap to <c>SpotifyVideoService.ResolvePlayableAsync</c>; null on the fake/offline backend. Off the UI thread.</summary>
    public System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<Wavee.SpotifyLive.PopOutVideoSource?>>? ResolveVideoSource;

    // ── M0: the ONE player, owned by the backend host and PRESENTED by the surfaces ─────────────────────────────────
    /// <summary>The live video player + a monotonic generation, as ONE atomic equality-gated value. The player is built and
    /// owned by <c>FluentVideoMediaHost</c> (never by a surface — that was the "video restarts from 0 on every placement
    /// flip" defect); the surfaces bind a <c>MediaPlayerElement</c> to it and key that element on <c>Generation</c>,
    /// because <c>MediaPlayerElement.Player</c> is a frozen-at-mount prop. Two signals would allow a torn frame where the
    /// player changed but the generation had not, so this is deliberately one struct.</summary>
    public readonly record struct VideoPlayerBinding(FluentGpu.Media.MediaPlayer? Player, long Generation);

    /// <summary>The video player the mounted surface must present (see <see cref="VideoPlayerBinding"/>). UI-thread signal;
    /// written ONLY by <see cref="NotifyVideoPlayerChanged"/>, which marshals <c>FluentVideoMediaHost.PlayerChanged</c>
    /// (raised on a playback thread) onto the UI thread.</summary>
    public Signal<VideoPlayerBinding> VideoPlayer { get; } = new(default);

    long _videoPlayerGen;

    /// <summary>Mirror a <c>FluentVideoMediaHost.PlayerChanged</c> notification onto <see cref="VideoPlayer"/>, marshalled to
    /// the UI thread. Safe to call from any thread; before <see cref="Activate"/> it writes directly (headless CLI).</summary>
    public void NotifyVideoPlayerChanged(FluentGpu.Media.MediaPlayer? player)
    {
        if (_post is not { } post) { VideoPlayer.Value = new VideoPlayerBinding(player, ++_videoPlayerGen); return; }
        post(() => VideoPlayer.Value = new VideoPlayerBinding(player, ++_videoPlayerGen));
    }

    // Monotonic resolve generation (bug 4 guard): each RequestPopOutSource captures ++_videoResolveGen; a track change
    // (PushState) also bumps it. An async resolve only publishes if its captured gen is still current — a resolve for a
    // superseded track is dropped instead of overwriting the current track's source with a stale video. The CTS cancels
    // the previous in-flight resolve so a dropped one also stops early. UI-thread only (every writer is post-marshalled).
    long _videoResolveGen;
    System.Threading.CancellationTokenSource? _videoResolveCts;
    // The track uri PopOutVideoSource currently holds a source FOR (null = none). Written only inside the UI-thread publish
    // paths below; read (racily, benignly — a miss just costs one extra resolve) by the playback resolve. It exists so a
    // track change does NOT clear a source the PLAYBACK path already resolved and published for that very track — which would
    // unmount the surface for a frame and force a second network resolve.
    string? _videoSourceUri;

    /// <summary>Kick off (fire-and-forget) resolving the pop-out video source for <paramref name="trackUri"/> and publish
    /// it onto <see cref="PopOutVideoSource"/> on the UI thread. No-op before <see cref="Activate"/> / without a resolver
    /// (fake backend) — the pop-out then just shows the letterbox until a source arrives. A resolve superseded by the next
    /// request or a track change is dropped at publish (never overwrites the current track's source).</summary>
    public void RequestPopOutSource(string? trackUri)
    {
        if (ResolveVideoSource is not { } resolve || string.IsNullOrEmpty(trackUri) || _post is not { } post) return;
        _videoResolveCts?.Cancel();
        _videoResolveCts = new System.Threading.CancellationTokenSource();
        var gen = ++_videoResolveGen;
        _ = ResolveAndPublishAsync(resolve, trackUri!, post, gen, _videoResolveCts.Token);
    }

    async System.Threading.Tasks.Task ResolveAndPublishAsync(
        System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<Wavee.SpotifyLive.PopOutVideoSource?>> resolve,
        string uri, System.Action<System.Action> post, long gen, System.Threading.CancellationToken ct)
    {
        Wavee.SpotifyLive.PopOutVideoSource? src = null;
        try { src = await resolve(uri, ct).ConfigureAwait(false); } catch { /* resolution failure / cancellation → no source (pop-out stays letterbox) */ }
        post(() =>
        {
            if (!PlacementCore.IsCurrentGeneration(gen, _videoResolveGen)) return;   // drop a stale (superseded-track) resolve
            _videoSourceUri = src is null ? null : uri;
            PopOutVideoSource.Value = src;
        });
    }

    /// <summary>The AWAITABLE half of <see cref="RequestPopOutSource"/>, for the playback path
    /// (<c>PlaybackController.LoadCurrentVideoAsync</c>): resolve this track's video source with the SAME
    /// <see cref="ResolveVideoSource"/> resolver, publish it onto <see cref="PopOutVideoSource"/> (so the mounted surface keys
    /// its content on the very source the media host is about to play), and return it so the caller can hand it to
    /// <c>FluentVideoMediaHost.LoadVideo</c>. Returns null when there is no resolver (fake/offline backend) or the track has no
    /// playable video — the controller then falls back to audio rather than leaving the user in silence.
    ///
    /// Callable from any thread. It needs no generation fence of its own: the controller serializes its loads under one lock,
    /// so at most ONE playback resolve is ever in flight and it always belongs to the playable currently being loaded (a
    /// cancelled load throws out of <paramref name="ct"/> and publishes nothing).</summary>
    public async System.Threading.Tasks.Task<Wavee.SpotifyLive.PopOutVideoSource?> ResolveVideoSourceForPlaybackAsync(
        string? trackUri, System.Threading.CancellationToken ct)
    {
        if (ResolveVideoSource is not { } resolve || string.IsNullOrEmpty(trackUri)) return null;
        // Already resolved for this exact track (the player-bar intent pre-resolved it) → reuse it; re-resolving would only
        // republish an equal source and make the surface remount.
        if (string.Equals(_videoSourceUri, trackUri, StringComparison.Ordinal) && PopOutVideoSource.Peek() is { } cached)
            return cached;

        Wavee.SpotifyLive.PopOutVideoSource? src;
        try { src = await resolve(trackUri!, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        catch { return null; }   // resolution failure → no source; the controller plays this track as audio
        if (src is null) return null;

        if (_post is { } post) post(() => { _videoSourceUri = trackUri; PopOutVideoSource.Value = src; });
        else { _videoSourceUri = trackUri; PopOutVideoSource.Value = src; }
        return src;
    }

    /// <summary>Monotonic "open the device picker" request. The critical "playback unsupported" toast's <em>Choose device</em>
    /// action bumps it; the player-bar <c>DevicesButton</c> watches it and opens its flyout.</summary>
    public Signal<int> DevicePickerRequest { get; } = new(0);

    /// <summary>Local (this-computer) audio outputs — the picker's "This computer" section. Null on fake/pre-login backends
    /// that genuinely have no local audio stack (the UI hides the section, never fakes success). Wired via
    /// <see cref="AttachLocalOutputs"/> (the AttachStore precedent).</summary>
    public LocalAudioDeviceService? LocalOutputs { get; private set; }
    /// <summary>Whether local playback is actually supported (an audio stack is wired) — flips the picker's local rows from
    /// the stale unconditional "Unavailable" to truthful/enabled.</summary>
    public Signal<bool> LocalPlaybackSupported { get; } = new(false);
    /// <summary>The Windows session mute state (Phase B) — drives the volume-button mute glyph.</summary>
    public Signal<bool> OutputMuted { get; } = new(false);

    /// <summary>Attach the local-output picker service (live bootstrap only; null on fake backends).</summary>
    public void AttachLocalOutputs(LocalAudioDeviceService service) => LocalOutputs = service;

    /// <summary>A device-topology notice (loss / fallback / auto-return / output-failed) → a caution toast whose action
    /// opens the device picker. Marshalled to the UI thread; no-op before <see cref="Activate"/>.</summary>
    public void NotifyOutputDeviceNotice(OutputDeviceNotice n)
    {
        if (_post is not { } post) return;
        post(() =>
        {
            string name = string.IsNullOrEmpty(n.DeviceName) ? Loc.Get(Strings.Player.SystemDefault) : n.DeviceName;
            string msg = n.Kind switch
            {
                OutputDeviceNoticeKind.DeviceLost => Strings.Player.DeviceLost(name),
                OutputDeviceNoticeKind.SwitchedToDefault => Strings.Player.DeviceSwitched(name),
                OutputDeviceNoticeKind.DeviceRestored => Strings.Player.DeviceRestored(name),
                _ => Loc.Get(Strings.Player.OutputFailed),
            };
            Toast.Show(msg, new ToastOptions
            {
                Severity = InfoBarSeverity.Warning,
                ActionLabel = Loc.Get(Strings.Player.ChooseDevice),
                OnAction = () => DevicePickerRequest.Value = DevicePickerRequest.Peek() + 1,
            });
        });
    }

    /// <summary>Reflect the Windows session mute state (Phase B4). Marshalled to the UI thread; no-op before Activate.</summary>
    public void NotifyOutputMuted(bool muted)
    {
        if (_post is not { } post) { OutputMuted.Value = muted; return; }
        post(() => OutputMuted.Value = muted);
    }

    /// <summary>Monotonic "open playback runtime setup" request — banner/toast CTAs bump it; ProfileMenu Settings watches it.</summary>
    public Signal<int> OpenPlaybackRuntimeSetup { get; } = new(0);

    /// <summary>Local PlayPlay runtime provisioning status (banner + setup modal).</summary>
    public Signal<PlaybackRuntimeStatus> RuntimeStatus { get; } = new(PlaybackRuntimeStatus.NotApplicable);

    // ── intents (UI → Core) ─────────────────────────────────────────────────────────────────────────────────────────
    public IPlaybackPlayer Player => _player;
    public IConnectDevices DeviceControl => _devices;
    public ISpotifySession Session => _session;

    public PlaybackBridge(IPlaybackPlayer player, IConnectDevices devices, ISpotifySession session)
    {
        _player = player;
        _state = player.State;
        _devices = devices;
        _session = session;
    }

    /// <summary>Subscribe Core observables → signals. Idempotent. Call once from a mount effect with <c>Context.UsePost()</c>.</summary>
    public void Activate(Action<Action> post)
    {
        if (_active) return;
        _active = true;
        _post = post;
        _subs.Add(_state.Changes.Subscribe(s => post(() => PushState(s))));
        _subs.Add(_state.PositionTicks.Subscribe(ms => post(() => PushPosition(ms))));
        _subs.Add(_devices.DevicesChanged.Subscribe(d => post(() => Devices.Value = d)));
        _subs.Add(_session.StatusChanged.Subscribe(st => post(() =>
        {
            Auth.Value = st;
            User.Value = _session.CurrentUser;            // profile chip (name/avatar) follows the session
        })));
        WireStore();   // if a store was attached before mount, start observing it now
        // Mirror the unified now-playing state onto the OS media surfaces (SMTC). UI-thread + the real top-level HWND
        // (FluentApp.WindowHandle); fail-soft if the platform refuses. Enabled for every backend (fake/offline included) —
        // it reflects whatever the bridge is showing, and transport buttons route back through _player like the on-screen ones.
        if (OperatingSystem.IsWindowsVersionAtLeast(8, 0))
        {
            _smtc = new SystemMediaControlsBridge(this, _player, post);
            _smtc.Activate(FluentApp.WindowHandle);
        }
        PlaybackBucketDiagnostics.Startup("bridge", "activated");
        PlaybackBucketDiagnostics.QueueIfChanged(ref _lastQueueDiagSig, "bridge.activate.initial",
            _state.Queue, _state.ContextUri, _state.CurrentTrack?.Uri);
    }

    /// <summary>Surface the standard "local playback isn't supported yet — choose a remote device" notice: a critical toast
    /// whose <em>Choose device</em> action opens the device picker. Marshalled onto the UI thread via the post delegate, so
    /// it is safe to call from a dealer/background thread (the live <c>PlaybackController</c> rejection hook) or from a UI
    /// intent (the pre-login <see cref="UnsupportedPlaybackPlayer"/>). No-op before <see cref="Activate"/> (headless CLI).</summary>
    public void NotifyLocalPlaybackUnsupported()
    {
        if (_post is not { } post) return;
        post(() => Toast.Show(
            Loc.Get(Strings.Player.LocalPlaybackUnsupported),
            new ToastOptions
            {
                Severity = InfoBarSeverity.Error,
                ActionLabel = Loc.Get(Strings.Player.ChooseDevice),
                OnAction = () => DevicePickerRequest.Value = DevicePickerRequest.Peek() + 1,
            }));
    }

    /// <summary>An outbound Connect command (transfer / play) to the active remote device failed — surface it as a critical
    /// toast instead of failing silently. Marshalled to the UI thread; no-op before <see cref="Activate"/>.</summary>
    public void NotifyRemoteCommandFailed()
    {
        if (_post is not { } post) return;
        post(() => Toast.Show(Loc.Get(Strings.Player.RemoteCommandFailed), new ToastOptions { Severity = InfoBarSeverity.Error }));
    }

    /// <summary>A LOCAL playback attempt failed (key/CDN/decode/provisioning) — surface a typed, user-facing message as a
    /// critical toast AND drive the player-bar into its Error state (retry offered on the primary). Marshalled to the UI
    /// thread; no-op before <see cref="Activate"/>. The optional retry action (e.g. re-provision + reset latch) becomes the
    /// toast's CTA. Cleared automatically when a track next plays (see <see cref="PushState"/>).</summary>
    public void NotifyPlaybackError(string message, string? retryLabel = null, Action? retry = null)
    {
        if (_post is not { } post) return;
        post(() =>
        {
            Error.Value = message;      // → PlayerBar PlayerState.Error (primary becomes Play/retry)
            IsLoading.Value = false;
            var token = ++_playbackErrorActionToken;
            _playbackErrorAction = retry;
            HasPlaybackErrorAction.Value = retry is not null;
            Toast.Show(message, new ToastOptions
            {
                Severity = InfoBarSeverity.Error,
                ActionLabel = retryLabel,
                OnAction = retry is null ? null : () => InvokePlaybackErrorAction(token),
            });
        });
    }

    public void InvokePlaybackErrorAction() => InvokePlaybackErrorAction(_playbackErrorActionToken);

    void InvokePlaybackErrorAction(long token)
    {
        if (token != _playbackErrorActionToken || _playbackErrorAction is not { } action) return;
        _playbackErrorAction = null;
        HasPlaybackErrorAction.Value = false;
        _playbackErrorActionToken++;
        Error.Value = null;
        IsLoading.Value = true;
        action();
    }

    /// <summary>Clear a surfaced playback error (e.g. the user picked a working device / a retry succeeded).</summary>
    public void ClearPlaybackError()
    {
        if (_post is not { } post) return;
        post(() =>
        {
            Error.Value = null;
            _playbackErrorAction = null;
            HasPlaybackErrorAction.Value = false;
            _playbackErrorActionToken++;
        });
    }

    /// <summary>Push runtime provisioning status onto the UI thread (no-op before <see cref="Activate"/>).</summary>
    public void UpdateRuntimeStatus(PlaybackRuntimeStatus status, Action<Action>? postOverride = null)
    {
        var post = postOverride ?? _post;
        if (post is null) { RuntimeStatus.Value = status; return; }
        post(() => RuntimeStatus.Value = status);
    }

    /// <summary>Attach the persistent store so the bridge can reflect async per-track enrichment (music video). Wired by
    /// the live bootstrap; safe to call before or after <see cref="Activate"/> (the store subscription is added once the
    /// post delegate is known). The fake backend never calls this, so the video signal stays false.</summary>
    public void AttachStore(IStore store)
    {
        _store = store;
        WireStore();
    }

    // Observe store changes for the CURRENT track's uri (or a bulk sync) and recompute the has-video signal. Detection is
    // fire-and-forget, so the association lands after the track is already playing — this is what lights the button up.
    void WireStore()
    {
        if (_storeWired || _store is not { } store || _post is not { } post) return;
        _storeWired = true;
        _subs.Add(store.Changes.Subscribe(c => post(() =>
        {
            if (c.IsBulk || (CurrentTrack.Value is { } t && c.Uri == t.Uri)) RecomputeHasVideo();
        })));
        post(RecomputeHasVideo);   // initial compute for whatever is playing now
    }

    // The has-video LATCH: once a track uri is known to have a video, a later transient false for the SAME track must
    // not commit an availability downgrade. The association store is never evicted, so true→false for an unchanged uri
    // is always a read glitch (CurrentTrack momentarily null/other mid-push) — and committing it costs a full
    // Video→Audio→Video media-kind round trip that tears down and rebuilds the whole DRM session (the observed
    // ping-pong). Cleared only on a real track change (PushState).
    string? _hasVideoLatchedUri;

    void RecomputeHasVideo()
    {
        var uri = CurrentTrack.Value?.Uri;
        bool has = false;
        if (!string.IsNullOrEmpty(uri) && _store is { } store)
            has = (store.GetVideoAssociation(uri)?.HasVideo ?? false) || (store.GetTrack(uri)?.HasVideo ?? false);
        if (has) _hasVideoLatchedUri = uri;
        else if (_hasVideoLatchedUri is not null && (uri is null || string.Equals(uri, _hasVideoLatchedUri, StringComparison.Ordinal)))
            has = true;   // transient glitch on the latched track — suppress the downgrade (see the latch comment)
        CurrentTrackHasVideo.SetIfChanged(has);
        // AVAILABILITY is the one channel through which "this track has no video" reaches the surfaces: it hides them and
        // routes the media back to audio WITHOUT touching the user's standing intent, so the next track that DOES have a
        // video returns to exactly the placement they had. CommitVideoSurface then does the edge work — and because the
        // music-video association is detected ASYNCHRONOUSLY (a video track routinely starts playing as AUDIO and only
        // then becomes known to have a video), that edge is also what swaps the media for the track already playing
        // instead of deferring the swap to the next track boundary.
        CommitVideoSurface(PlacementCore.WithAvailability(VideoSurface.Peek(), AvailabilityFor(has)));
        // Already-active but source-less (e.g. the track changed under a live surface): kick a resolve so the surface has
        // something to play. Gen-fenced at publish, so this is safe to call redundantly (last request wins).
        if (has && VideoActive() && PopOutVideoSource.Peek() is null)
            RequestPopOutSource(uri);
    }

    /// <summary>An <see cref="ILoginProgress"/> the live-login bootstrap reports to off the UI thread; each snapshot is
    /// marshalled onto the UI thread via <paramref name="post"/> and written to <see cref="Login"/>.</summary>
    public ILoginProgress Progress(Action<Action> post) => new SignalProgress(this, post);

    sealed class SignalProgress(PlaybackBridge bridge, Action<Action> post) : ILoginProgress
    {
        public void Report(LoginSnapshot snapshot) => post(() => bridge.Login.Value = snapshot);
    }

    void PushState(IPlaybackState s)
    {
        var prevUri = CurrentTrack.Value?.Uri;
        CurrentTrack.Value = s.CurrentTrack;
        if (s.CurrentTrack?.Uri != prevUri)
        {
            // A new track. The video INTENT is sticky (video carries across tracks) — do NOT clear it. Fence any
            // in-flight resolve for the previous track, cancel it, and clear that track's resolved source.
            // RecomputeHasVideo() (below) then re-resolves for the new track iff it is active — so a video-less track
            // just goes inactive while a video track auto-continues.
            _videoResolveGen++;
            _videoResolveCts?.Cancel();
            _hasVideoLatchedUri = null;   // a REAL track change ends the has-video latch (see RecomputeHasVideo)
            // Bump the CONTENT generation. That alone expires a per-track dismiss (it is compared against this
            // generation, never cleared), and routing it through the commit path means the resulting off→on edge also
            // swaps the media back to video for the new track.
            var vs = VideoSurface.Peek();
            CommitVideoSurface(PlacementCore.ContentChanged(vs, vs.ContentGen + 1));
            // Keep a source the PLAYBACK path already resolved+published for THIS (new) track: the controller resolves the
            // video BEFORE it publishes the track change, so clearing here would unmount the live surface for a frame and
            // force a second resolve for a source we already have. Anything else (a stale source) is cleared as before.
            if (!string.Equals(_videoSourceUri, s.CurrentTrack?.Uri, StringComparison.Ordinal))
            {
                _videoSourceUri = null;
                PopOutVideoSource.Value = null;
            }
        }
        RecomputeHasVideo();                                            // reflect the new track's cached video state (+ re-resolve if VideoActive)
        CurrentContext.Value = s.ContextUri;
        Identity.Value = new PlaybackIdentity(s.ContextUri, s.CurrentTrack);
        // Coarse gate for the now-playing card overlays: true iff any card COULD match (mirrors NowPlayingOverlay.Matches,
        // which is false when both context and track are empty). Equality-gated by the setter, so an idle→idle push is free.
        HasActiveContext.Value = !string.IsNullOrEmpty(s.ContextUri) || s.CurrentTrack is not null;
        IsPlaying.Value = s.IsPlaying;
        IsBuffering.Value = s.IsBuffering;
        RecoveryKind.Value = s.RecoveryKind;
        IsShuffle.Value = s.IsShuffle;
        Repeat.Value = s.Repeat;
        Volume.Value = (float)s.Volume;
        DurationMs.Value = s.DurationMs;
        TrackPalette.Value = s.Palette;
        Queue.Value = s.Queue;
        BumpQueueRevision(s.Queue);
        PlaybackBucketDiagnostics.QueueIfChanged(ref _lastQueueDiagSig, "bridge.ui.push-state",
            s.Queue, s.ContextUri, s.CurrentTrack?.Uri);
        IsLoading.Value = s.IsLoading;
        // A surfaced local-playback error (set via NotifyPlaybackError) is owned by the bridge, not the projection (whose
        // Error is inert). Don't clobber it on every structural tick — clear it only once a track is actually playing again.
        if (s.IsPlaying && !s.IsBuffering && s.RecoveryKind == PlaybackRecoveryKind.None && s.CurrentTrack is not null)
        {
            Error.Value = null;
            _playbackErrorAction = null;
            HasPlaybackErrorAction.Value = false;
        }
        CanSkipNext.Value = s.CanSkipNext;
        CanSkipPrev.Value = s.CanSkipPrev;
        CanSeek.Value = s.CanSeek && s.RecoveryKind == PlaybackRecoveryKind.None;
        ActiveDeviceId.Value = s.ActiveDeviceId;
        PushPosition(s.PositionMs);
        _smtc?.OnStateChanged();   // metadata / play-status / prev-next availability → OS media surface
    }

    // Fold the queue's SET identity (count + per-row id/bucket/provider) and bump the revision only on a real change.
    void BumpQueueRevision(IReadOnlyList<QueueEntry> queue)
    {
        ulong fold = 1469598103934665603UL;   // FNV-ish, order-sensitive
        fold = (fold ^ (ulong)queue.Count) * 1099511628211UL;
        for (int i = 0; i < queue.Count; i++)
        {
            var e = queue[i];
            fold = (fold ^ e.ItemId.Value) * 1099511628211UL;
            fold = (fold ^ (uint)e.Bucket) * 1099511628211UL;
            fold = (fold ^ (uint)e.Provider) * 1099511628211UL;
            if (e.ItemId.IsNone)   // degenerate/fake ids collide → mix the derived EntryId so the set still distinguishes
                fold = (fold ^ (ulong)(uint)e.EntryId.GetHashCode(StringComparison.Ordinal)) * 1099511628211UL;
        }
        if (_haveQueueFold && fold == _queueContentFold) return;
        _haveQueueFold = true;
        _queueContentFold = fold;
        QueueRevision.Value = ++_queueRev;
    }

    /// <summary>Arm the seek latch (#2): call the instant a seek is issued from the UI so stale pre-seek position ticks are
    /// suppressed until the engine catches up. UI-thread only. The caller also optimistically writes PositionMs/Frac.</summary>
    public void NoteSeek(long targetMs)
    {
        _seekLatchTargetMs = targetMs;
        _seekLatchDeadlineTick = Environment.TickCount64 + SeekLatchWindowMs;
    }

    void PushPosition(long ms)
    {
        if (_seekLatchTargetMs >= 0)
        {
            bool landed = Math.Abs(ms - _seekLatchTargetMs) <= SeekLatchToleranceMs;
            bool expired = Environment.TickCount64 >= _seekLatchDeadlineTick;
            if (!landed && !expired) return;   // stale pre-seek tick → keep the optimistic target on screen
            _seekLatchTargetMs = -1;           // seek took (or gave up waiting) → resume normal position flow
        }
        PositionMs.Value = ms;
        long dur = DurationMs.Value;
        PositionFrac.Value = dur > 0 ? Math.Clamp(ms / (float)dur, 0f, 1f) : 0f;
        _smtc?.OnPositionChanged(ms);   // ~1 Hz timeline scrub → OS media surface (throttled inside)
    }
}
