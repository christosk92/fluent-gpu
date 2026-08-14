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
    // The user's local video-override curation (warm, synchronous). Wired by the bootstrap via AttachVideoOverrides; null
    // ⇒ every override path below is unreachable, which is the feature's kill switch.
    VideoOverrideService? _overrides;
    // The local "recently played" log (§C1.8.1). Wired by Services via AttachPlayLog; null ⇒ nothing is recorded, which
    // is the sidebar feature's kill switch. Appended ONLY at a real track boundary (see PushState).
    PlayLogStore? _playLog;
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
    TaskbarBridge? _taskbar;
    JumpListBridge? _jumpList;
    // ── playback session-snapshot seam (playback-restore fix §8) ────────────────────────────────────────────────────────
    // The concrete controller behind the switchable facade (re-resolved when the fake→live swap changes the inner player):
    // the WRITER gates + hints (HasLocalSession / RestoreWriterHints) and the READER hook (RestoreSnapshot) hang off it.
    IPlaybackPlayer? _restoreWiredTo;
    PlaybackController? _restoreController;
    bool _lastPushIsPlaying;   // pause-edge detector: play→pause is a snapshot write gate

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
    /// <summary>The published queue. Uses an ELEMENT-IDENTITY comparer, not the default reference comparer: the projection
    /// re-windows (<c>WindowQueue</c>) into a FRESH list of the SAME <see cref="QueueEntry"/> instances on every structural
    /// push — a seek, a pause, a volume nudge, a cluster heartbeat — so the default comparer notified every subscriber for a
    /// queue that had not changed, re-rendering the whole queue panel (50 rows, each a SwipeControl) for no visual change.</summary>
    public Signal<IReadOnlyList<QueueEntry>> Queue { get; } = new(Array.Empty<QueueEntry>(), QueueListIdentityComparer.Instance);
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
    /// has-video comes from <see cref="VideoPresence"/> (the association plane ∪ user attachments) or — for the
    /// now-playing track — from the async-detected <see cref="CurrentTrackHasVideo"/> signal. Never returns true once
    /// the user has closed/turned video
    /// off — that intent is off until they turn it back on, so the ✕ routes this track AND every later one back to audio.
    ///
    /// <para>DO NOT "simplify" the <see cref="PlacementCore.ResolveWith"/> call below into <see cref="VideoActive"/>: it
    /// deliberately OVERRIDES the state's own <c>Available</c> with THIS track's availability, which is exactly why the
    /// deferred-upgrade rule in <see cref="RecomputeHasVideo"/> cannot leak into playback. A mid-track association land
    /// leaves <c>state.Available</c> stale at <c>None</c> (the surface is deliberately not upgraded under a playing
    /// track), yet the NEXT track — evaluated here by uri before <see cref="CurrentTrack"/> moves — still needs only the
    /// standing <c>Requested</c> intent plus its own has-video to start as video. Reading <c>s.Available</c> instead
    /// would silently make every next video track start as audio.</para></summary>
    public bool ShouldPlayAsVideo(Track track)
    {
        // A ONE-PLAY intent (PrimeVideoIntentFor) belongs to exactly one uri. Any other track asking — including the
        // NEXT queue track, evaluated by uri before CurrentTrack moves and therefore before the scope expires in
        // PushState — gets a hard no, so a drawer "watch this one" can never leak video onto the rest of the queue.
        if (_videoIntentScopeUri is { } scope && !string.Equals(track.Uri, scope, StringComparison.Ordinal)) return false;
        // A user-attached local video counts as "this playable has a video" for EVERY playable — including the NEXT one,
        // evaluated by uri before CurrentTrack moves — which is why the lookup must be the warm dictionary and not a
        // signal (a signal-based check would ping-pong across the track boundary).
        // Proven-dead beats every availability input: the backend already told us this exact playable produced no video
        // media, so asking it to try again would just re-mount the never-resolving surface.
        if (VideoMediaLatch.IsDead(track.Uri, _videoDeadUri)) return false;
        bool hasVideo = VideoPresence.HasVideo(track.Uri)   // the association plane ∪ user attachments — one answer
            || (string.Equals(CurrentTrack.Peek()?.Uri, track.Uri, StringComparison.Ordinal) && CurrentTrackHasVideo.Peek());
        // Same state, same rules — only the AVAILABILITY input is swapped for that track's, so this asks "what would be
        // resolved if THIS track were playing?" without mutating anything.
        return PlacementCore.ResolveWith(VideoSurface.Peek(), AvailabilityFor(hasVideo)) != SurfacePlacement.None;
    }

    /// <summary>Content availability → the placement set. A track WITHOUT a video makes every placement unavailable, so
    /// the surface hides and the media stays audio through the exact same path a host limitation would take. (Host
    /// capability — "can a second window be opened at all?" — folds in here too once that seam exists.)</summary>
    static PlacementSet AvailabilityFor(bool hasVideo) => VideoUpgradeGate.AvailabilityFor(hasVideo);

    /// <summary>The two orthogonal flags a media-kind re-evaluation carries (see <see cref="RequestMediaKindRefresh"/>).</summary>
    public delegate void MediaKindRefreshRequest(bool forceReloadIfVideo, bool clearConnectAudioFirst);

    /// <summary>Ask the backend to re-evaluate the CURRENT playable's media kind right now (wired at composition to
    /// <c>PlaybackController.RefreshCurrentMediaKindAsync</c>; null on the fake backend / with the video-host kill switch off).
    /// Every writer of the video INTENT calls it, so "watch video" / "switch to audio" / the surface ✕ swap the media host for
    /// the track that is already playing instead of only taking effect at the next track boundary.
    /// <para><c>forceReloadIfVideo</c>: normally a re-evaluation that lands on the SAME kind is a no-op, but a
    /// mid-playback override attach/replace/remove changes the video SOURCE without changing the kind, so it must force
    /// the reload. The host's same-Key idempotence makes forcing safe (an unchanged source is still a no-op).</para>
    /// <para><c>clearConnectAudioFirst</c> is ORTHOGONAL to it and must never be folded into the same bool: it drops the
    /// controller's remote-playback ids (ending Connect's audio-first preference and the per-playable video recovery /
    /// audio-fallback latches). Only an EXPLICIT user media intent may do that; an availability edge that merely learned a
    /// track has a video must not, or a Connect-originated session silently loses its audio-first rule.</para></summary>
    public MediaKindRefreshRequest? RequestMediaKindRefresh;

    /// <summary>
    /// The ONE write path for <see cref="VideoSurface"/>. Publishes the new state and then does the two things a bare
    /// signal write cannot: when the surface turns ON or OFF it asks the backend to re-evaluate the current media kind
    /// (so "watch video" swaps what is PLAYING for the track already playing, instead of lighting a surface over a
    /// still-audio stream), and when it turns on it kicks the source resolve. Moving between placements is neither —
    /// the media and the source are already right, which is what keeps a move from restarting the video.
    /// <para><paramref name="clearConnectAudioFirst"/> travels with the refresh request: TRUE only from an explicit user
    /// media intent (toggle / picker / turn-off / surface ✕ / an override change), FALSE from an availability edge — see
    /// <see cref="RequestMediaKindRefresh"/> for why the two flags may not be merged. An explicit intent also PROMOTES the
    /// state to standing: it clears any one-play scope (<see cref="PrimeVideoIntentFor"/>), because a user who toggles has
    /// asked for the sticky rule, not the one-shot one.</para>
    /// <para><paramref name="refreshKind"/> is FALSE only when the very next thing the caller does re-evaluates the media
    /// kind anyway (a play-as-form request, or the scope expiring at a track boundary the new track has already resolved
    /// through). Refreshing there would reload the OUTGOING track — the observed flash of the old track's DRM video
    /// between a drawer play and the swap.</para>
    /// </summary>
    void CommitVideoSurface(in PlacementState after, bool clearConnectAudioFirst, bool refreshKind = true)
    {
        if (clearConnectAudioFirst) _videoIntentScopeUri = null;   // an explicit toggle makes the intent standing
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
        if (!refreshKind) return;
        // a real kind edge — never needs forcing
        if (isActive != wasActive) RequestKindRefresh(forceReloadIfVideo: false, clearConnectAudioFirst);
        if (isActive && !wasActive) RequestPopOutSource(CurrentTrack.Peek()?.Uri);
    }

    /// <summary>The ONE call site for <see cref="RequestMediaKindRefresh"/> — logs placement/has/latch state so a
    /// Video↔Audio thrash is one line, not archaeology.</summary>
    void RequestKindRefresh(bool forceReloadIfVideo, bool clearConnectAudioFirst)
    {
        if (RequestMediaKindRefresh is not { } refresh) return;
        var s = VideoSurface.Peek();
        string? uri = CurrentTrack.Peek()?.Uri;
        WaveeLog.Instance.Info("playback",
            $"media kind refresh requested force={forceReloadIfVideo} clearConnect={clearConnectAudioFirst} " +
            $"uri={uri ?? "-"} requested={s.Requested} available={s.Available} " +
            $"has={CurrentTrackHasVideo.Peek()} latched={_hasVideoLatchedUri ?? "-"} dead={_videoDeadUri ?? "-"}");
        refresh(forceReloadIfVideo, clearConnectAudioFirst);
    }

    /// <summary>The user's video INTENT, folded onto the CURRENT track's availability first. The fold is load-bearing:
    /// an association that lands mid-track updates the badge but deliberately leaves the surface (and therefore
    /// <c>Available</c>) alone — see <see cref="RecomputeHasVideo"/> — so a toggle read straight off the stale state
    /// would resolve to None and do nothing while the button sat lit.</summary>
    PlacementState IntentBase() => VideoUpgradeGate.FoldAvailability(VideoSurface.Peek(), CurrentTrackHasVideo.Peek());

    /// <summary>The PRIMARY video affordance, and it is symmetric: lit → off (from ANY placement), unlit → open at the
    /// user's preferred placement. Nothing else is needed to guarantee the toggle can always be turned off. After a
    /// deferred land it starts the video the badge is advertising — see <see cref="VideoUpgradeGate.PrimaryClick"/>.</summary>
    public void ToggleVideo() => CommitVideoSurface(
        VideoUpgradeGate.PrimaryClick(VideoSurface.Peek(), CurrentTrackHasVideo.Peek()), clearConnectAudioFirst: true);

    /// <summary>Show the video at a specific placement (the surface picker). Also clears a per-track dismiss and adopts
    /// the target as the preferred home, so the primary button and the next track follow the user there.</summary>
    public void ShowVideoAt(SurfacePlacement placement) => CommitVideoSurface(PlacementCore.OpenAt(IntentBase(), placement), clearConnectAudioFirst: true);

    // The uri a ONE-PLAY video intent is scoped to (null = the intent, whatever it is, is standing). Set only by
    // PrimeVideoIntentFor, cleared by any explicit toggle (which promotes to standing) or by the track moving on.
    string? _videoIntentScopeUri;

    /// <summary>Light the video intent for ONE upcoming play of <paramref name="uri"/> — "watch this one", not the
    /// standing toggle. The caller follows with the play command; <c>ShouldPlayAsVideo</c> then resolves video for
    /// exactly that uri and refuses every other, and the scope (surface included) expires at the next real track
    /// boundary. Only the explicit toggles (<see cref="ToggleVideo"/> / <see cref="ShowVideoAt"/> / turn-off / ✕)
    /// change the STANDING intent — a drawer "play the music video" must not leave video on for the rest of the queue.
    ///
    /// <para>Deliberately refresh-less: committing with a refresh would re-evaluate the CURRENT (outgoing) track and
    /// load ITS video for the ~150 ms until the swap — a wasted DRM license and a visible flash (seen in the log as
    /// <c>video-host loaded drm=True</c> for the old track right before the new one started).</para>
    ///
    /// <para>A no-op while video is already ACTIVE: the user is watching under a standing intent, and this gesture
    /// must not downgrade that to a one-shot.</para></summary>
    public void PrimeVideoIntentFor(string uri)
    {
        if (string.IsNullOrEmpty(uri) || VideoActive()) return;
        CommitVideoSurface(PlacementCore.OpenAt(IntentBase(), VideoSurface.Peek().Preferred), clearConnectAudioFirst: false, refreshKind: false);
        _videoIntentScopeUri = uri;   // AFTER the commit — an explicit-intent commit clears the scope, this one must set it
    }

    /// <summary>Turn video off entirely (the menu's "turn off video"): the surface goes away and the media swaps back to
    /// the song's own audio. STICKY — no subsequent track re-opens it. The preferred placement is remembered for the
    /// next time the user asks for video.</summary>
    public void TurnVideoOff() => CommitVideoSurface(PlacementCore.TurnOff(VideoSurface.Peek()), clearConnectAudioFirst: true);

    /// <summary>A surface reports that the USER closed it by its own chrome (the mini player's ✕, the pop-out's OS ✕ /
    /// Alt+F4). Closing an IN-APP surface turns video off globally and stickily — it used to be a per-song dismiss that
    /// expired on the next track, which is the "I closed the video and the next song opened it again" complaint (fixed
    /// 2026-07-26). Closing the DETACHED window still means "not in a separate window", not "stop watching", so it falls
    /// back to the mini player (closing THAT then turns video off). A close for a placement that is no longer resolved
    /// is stale and inert (see <see cref="PlacementCore.HostClosed"/>).</summary>
    public void NotifyVideoSurfaceClosed(SurfacePlacement closed) => CommitVideoSurface(PlacementCore.HostClosed(VideoSurface.Peek(), closed), clearConnectAudioFirst: true);

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

    // The playable the BACKEND proved has no live video media (see VideoMediaLatch). UI-thread written; read racily (and
    // benignly — a miss costs one extra video attempt) by ShouldPlayAsVideo on the playback thread, exactly like
    // _videoSourceUri. Cleared on a real track change in PushState.
    string? _videoDeadUri;

    /// <summary>Mirror a <c>FluentVideoMediaHost.PlayerChanged</c> notification onto <see cref="VideoPlayer"/>, marshalled to
    /// the UI thread. Safe to call from any thread; before <see cref="Activate"/> it writes directly (headless CLI).</summary>
    public void NotifyVideoPlayerChanged(FluentGpu.Media.MediaPlayer? player)
    {
        if (_post is not { } post) { VideoPlayer.Value = new VideoPlayerBinding(player, ++_videoPlayerGen); return; }
        post(() => VideoPlayer.Value = new VideoPlayerBinding(player, ++_videoPlayerGen));
    }

    /// <summary>The backend reports that VIDEO is NOT the current media any more (the host handed back no player and the
    /// controller's live media kind is not Video): a fallback to audio, an open failure, or the surface being closed while
    /// a load was still in flight. Without this, the placement model never learns — availability keeps saying "this track
    /// has a video", the surface stays mounted, and (having no source and no player) it shows its indeterminate "Loading"
    /// poster forever over audio that is already playing. Latching the PLAYABLE dead routes it through the ONE existing
    /// availability path, so both surfaces unmount together; the user's standing "watch video" INTENT is untouched, so the
    /// next video-bearing track opens exactly as before (the sticky-off semantics are unaffected — they live in
    /// <c>Requested</c>, which this never writes).</summary>
    public void NotifyVideoMediaEnded(string? playableUri)
    {
        if (_post is not { } post) { ApplyVideoMediaEnded(playableUri); return; }
        post(() => ApplyVideoMediaEnded(playableUri));
    }

    void ApplyVideoMediaEnded(string? playableUri)
    {
        var uri = string.IsNullOrEmpty(playableUri) ? CurrentTrack.Peek()?.Uri : playableUri;
        if (!VideoMediaLatch.MarkDead(uri, ref _videoDeadUri)) return;   // already latched → no republish
        if (string.Equals(_videoSourceUri, uri, StringComparison.Ordinal))
        {
            _videoSourceUri = null;
            PopOutVideoSource.Value = null;
            _videoResolveGen++;               // fence an in-flight resolve for the playable we just gave up on
            _videoResolveCts?.Cancel();
        }
        // A DOWNGRADE (availability → None) always commits, so both surfaces unmount through the one commit path.
        RecomputeHasVideo(commitUpgrade: true);
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
        catch (Exception ex)
        {
            // Was silently swallowed: a resolve that THREW looked identical to "this track has no video", so a broken
            // manifest / dead attachment fell back to audio with nothing in the log to explain it.
            WaveeLog.Instance.Info("playback", $"video source resolve failed for {trackUri}: {ex.GetType().Name}: {ex.Message}");
            return null;   // no source; the controller plays this track as audio
        }
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
        WireRestoreSeam();   // playback restore (§8): reader hook onto the live controller; re-checked on every push
        // Mirror the unified now-playing state onto the OS media surfaces (SMTC). UI-thread + the real top-level HWND
        // (FluentApp.WindowHandle); fail-soft if the platform refuses. Enabled for every backend (fake/offline included) —
        // it reflects whatever the bridge is showing, and transport buttons route back through _player like the on-screen ones.
        if (OperatingSystem.IsWindowsVersionAtLeast(8, 0))
        {
            _smtc = new SystemMediaControlsBridge(this, _player, post);
            _smtc.Activate(FluentApp.WindowHandle);
            _taskbar = new TaskbarBridge(this, _player, post);
            _taskbar.Activate(FluentApp.WindowHandle);
            _jumpList = new JumpListBridge(this, _player, post);
            _jumpList.Activate();
            WaveeNativeBoot.Install(post);
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

    /// <summary>Attach the user's local video-override curation (the warm, synchronous roster). Safe before or after
    /// <see cref="Activate"/>. The fake/pre-store bootstrap never calls it, so every override path stays unreachable.</summary>
    public void AttachVideoOverrides(VideoOverrideService overrides) => _overrides = overrides;

    /// <summary>The attached curation, for the menu/settings surfaces (null when the backend has none).</summary>
    public VideoOverrideService? VideoOverrides => _overrides;

    /// <summary>Attach the local play log, so the sidebar's "Recently played" feed has something to read. Safe before or
    /// after <see cref="Activate"/>; UI-thread only, like the store it appends to. Null (never attached) simply records
    /// nothing — the bridge holds no opinion about whether the feature exists.</summary>
    public void AttachPlayLog(PlayLogStore? playLog) => _playLog = playLog;

    /// <summary>The attached play log (null when the backend never called <see cref="AttachPlayLog"/>). Read by
    /// <see cref="JumpListBridge"/> on a track-boundary rebuild.</summary>
    internal PlayLogStore? PlayLog => _playLog;

    /// <summary>The Jump List sibling (null before <see cref="Activate"/> / non-Windows). The shell can
    /// <see cref="JumpListBridge.AttachHistory"/> once it has a <see cref="HistoryStore"/>.</summary>
    public JumpListBridge? JumpList => _jumpList;

    /// <summary>Opens the video-override management surface (Settings → Playback). Wired by the shell; null (e.g. a
    /// headless/test bridge) means the override toasts below simply carry no action button rather than a dead one.</summary>
    public Action<string>? OpenVideoOverrideManager;

    /// <summary>Monotonic "show me the video-override roster" request — the toasts' Manage action bumps it and navigates
    /// to Settings; <c>SettingsPage</c> watches it and selects the Playback tab. Settings has no route-arg tab
    /// deep-link, so this mirrors the proven <see cref="OpenPlaybackRuntimeSetup"/> counter pattern.</summary>
    public Signal<int> OpenVideoOverrides { get; } = new(0);

    /// <summary>The ONE mutation entry point for the override curation — post-marshalled, so it is safe to call from the
    /// menu, from Settings, or from a background repair. Side-effects are decided by
    /// <see cref="VideoOverrideMutationCore.Plan"/> (attach must not clear the has-video latch; force-reload only when
    /// the source key really changed under an already-active video surface).</summary>
    public void NotifyVideoOverrideChanged(string playableUri, OverrideMutationKind kind)
    {
        if (string.IsNullOrEmpty(playableUri)) return;
        if (_post is not { } post) { ApplyVideoOverrideChanged(playableUri, kind); return; }
        post(() => ApplyVideoOverrideChanged(playableUri, kind));
    }

    void ApplyVideoOverrideChanged(string playableUri, OverrideMutationKind kind)
    {
        bool isCurrent = string.Equals(CurrentTrack.Peek()?.Uri, playableUri, StringComparison.Ordinal);
        bool videoAlreadyActive = VideoActive();
        string? previousKey = string.Equals(_videoSourceUri, playableUri, StringComparison.Ordinal)
            ? PopOutVideoSource.Peek()?.Key
            : null;
        string? nextKey = null;
        if (kind != OverrideMutationKind.Remove && _overrides is { } ov && ov.TryGetActive(playableUri, out var o))
            nextKey = o.SourceKey;

        var plan = VideoOverrideMutationCore.Plan(kind, isCurrent, videoAlreadyActive, previousKey, nextKey);
        if (plan.ClearHasVideoLatch) HasVideoLatch.ClearFor(playableUri, ref _hasVideoLatchedUri);
        if (plan.ClearDeadVideoLatch) VideoMediaLatch.ClearFor(playableUri, ref _videoDeadUri);

        if (string.Equals(_videoSourceUri, playableUri, StringComparison.Ordinal))
        {
            _videoSourceUri = null;
            PopOutVideoSource.Value = null;
            _videoResolveGen++;               // fence any in-flight resolve carrying the superseded source
            _videoResolveCts?.Cancel();
        }
        // Explicit user action — commit upgrades (and downgrades) mid-track.
        RecomputeHasVideo(commitUpgrade: plan.CommitHasVideoUpgrade);

        // Reveal AFTER has-video is committed so OpenAt never runs against Available=None (that was the attach race that
        // opened intent with a stale None and then double-fired Audio→Video + forced same-kind reload).
        if (plan.RevealSurfaceIfCurrent
            && VideoOverrideMutationCore.CanReveal(isCurrent, CurrentTrackHasVideo.Peek(), VideoActive()))
            ShowVideoAt(VideoSurface.Peek().Preferred);

        // Force only when the plan says the source identity changed under an already-live video (replace). First attach
        // that flips Audio→Video is already handled by the availability edge above.
        if (plan.ForceReloadIfVideo)
            RequestKindRefresh(forceReloadIfVideo: true, clearConnectAudioFirst: true);
    }

    /// <summary>A playable's attached video file is missing at play time. One non-blocking WARNING per session per
    /// playable (the service gates it) — never the player-bar Error state: the music is already playing the original.</summary>
    public void NotifyVideoOverrideMissing(string playableUri)
    {
        if (_post is not { } post) return;
        post(() => Toast.Show(Loc.Get(Strings.VideoOverride.MissingToast), new ToastOptions
        {
            Severity = InfoBarSeverity.Warning,
            ActionLabel = OpenVideoOverrideManager is null ? null : Loc.Get(Strings.VideoOverride.Manage),
            OnAction = OpenVideoOverrideManager is null ? null : () => OpenVideoOverrideManager?.Invoke(playableUri),
        }));
    }

    /// <summary>A playable's attached video file exists but could NOT be opened/decoded. An Error toast with no retry CTA
    /// (retrying the same unplayable file is not a fix) — the fallback to the original has already been scheduled.</summary>
    public void NotifyVideoOverrideUnplayable(string playableUri)
    {
        if (_post is not { } post) return;
        post(() => Toast.Show(Loc.Get(Strings.VideoOverride.UnplayableToast), new ToastOptions
        {
            Severity = InfoBarSeverity.Error,
            ActionLabel = OpenVideoOverrideManager is null ? null : Loc.Get(Strings.VideoOverride.Manage),
            OnAction = OpenVideoOverrideManager is null ? null : () => OpenVideoOverrideManager?.Invoke(playableUri),
        }));
    }

    /// <summary>Drop the cached resolved video source for a playable so the next playback resolve goes back through the
    /// resolver (used after quarantining an unplayable attachment). The uri gate is cleared SYNCHRONOUSLY because it is
    /// what the playback-thread resolve reads; the published signal is cleared on the UI thread.</summary>
    public void InvalidateVideoSource(string playableUri)
    {
        if (!string.Equals(_videoSourceUri, playableUri, StringComparison.Ordinal)) return;
        _videoSourceUri = null;
        if (_post is { } post) post(() => { if (_videoSourceUri is null) PopOutVideoSource.Value = null; });
        else PopOutVideoSource.Value = null;
    }

    // Observe store changes for the CURRENT track's uri (or a bulk sync) and recompute the has-video signal. Detection is
    // fire-and-forget, so the association lands after the track is already playing — this is what lights the button up.
    void WireStore()
    {
        if (_storeWired || _store is not { } store || _post is not { } post) return;
        _storeWired = true;
        _subs.Add(store.Changes.Subscribe(c => post(() =>
        {
            // commitUpgrade: false is THE no-mid-track-auto-swap rule — an association landing under a playing track
            // lights the badge and nothing else (see RecomputeHasVideo).
            if (c.IsBulk || (CurrentTrack.Value is { } t && c.Uri == t.Uri)) RecomputeHasVideo(commitUpgrade: false);
        })));
        post(() => RecomputeHasVideo(commitUpgrade: false));   // initial compute for whatever is playing now
    }

    // The has-video LATCH: once a track uri is known to have a video, a later transient false for the SAME track must
    // not commit an availability downgrade. The association store is never evicted, so true→false for an unchanged uri
    // is always a read glitch (CurrentTrack momentarily null/other mid-push) — and committing it costs a full
    // Video→Audio→Video media-kind round trip that tears down and rebuilds the whole DRM session (the observed
    // ping-pong). Cleared only on a real track change (PushState).
    string? _hasVideoLatchedUri;

    /// <summary>Re-publish the Connect player state (a PLAYER_STATE_CHANGED PutState with NO host/kind change). Wired at
    /// go-live to the device-state publisher; null on the fake backend. Invoked when a music-video association lands under
    /// the already-playing track, which is the one wire-visible change that produces no playback event of its own.</summary>
    public Action? RepublishConnectState;
    readonly ConnectVideoFacts _connectVideoFacts = new();

    /// <summary>Recompute the now-playing track's has-video badge and (conditionally) fold it into the surface state.
    /// <para><paramref name="commitUpgrade"/> is the no-mid-track-auto-swap rule. An UPGRADE — an inactive surface
    /// becoming active because this track turned out to have a video — is committed only at a real boundary (a track
    /// change) or on an explicit user action (the override paths). When an association merely LANDS asynchronously under
    /// a track that is already playing, the badge lights but the surface is left alone, because committing it would swap
    /// the media host and restart the song at position 0 — which is what the user experienced as "it jumped". DOWNGRADES
    /// always commit regardless, so a video-less track and the proven-dead latch still unmount the surface immediately.</para></summary>
    void RecomputeHasVideo(bool commitUpgrade)
    {
        var uri = CurrentTrack.Value?.Uri;
        bool has = false;
        if (!string.IsNullOrEmpty(uri))
        {
            if (_store is { } store) has = store.GetVideoAssociation(uri)?.HasVideo ?? false;
            // A user attachment makes ANY playable a video playable — including one the source serves no video for, and
            // including on a backend with no store at all (overrides work without Spotify).
            if (!has && _overrides is { } ov) has = ov.Has(uri);
        }
        bool rawHas = has;
        has = HasVideoLatch.Apply(has, uri, ref _hasVideoLatchedUri);
        // A PROVEN "no video media for this playable" beats the glitch-suppression latch above (which exists only to
        // absorb a transient read). This is what unmounts a surface the backend has already stopped feeding.
        has = VideoMediaLatch.Apply(has, uri, _videoDeadUri);
        LogVideoAffordance(uri, rawHas, has);
        CurrentTrackHasVideo.SetIfChanged(has);   // the badge ALWAYS updates — the player-bar affordance lights immediately
        // …and so does the CONNECT wire. This sits ABOVE the deferred-upgrade return on purpose: a badge-only land is
        // precisely the case where no host swap (and therefore no playback event, and therefore no PutState) happens, yet
        // the state a remote controller sees changed — it gained an associated_video_id + a switch-to-video offer.
        if (!string.IsNullOrEmpty(uri)
            && _connectVideoFacts.Observe(uri, has, _store?.GetVideoAssociation(uri)?.VideoGidHex))
            RepublishConnectState?.Invoke();
        // AVAILABILITY is the one channel through which "this track has no video" reaches the surfaces: it hides them and
        // routes the media back to audio WITHOUT touching the user's standing intent, so the next track that DOES have a
        // video returns to exactly the placement they had. CommitVideoSurface then does the edge work.
        var target = PlacementCore.WithAvailability(VideoSurface.Peek(), AvailabilityFor(has));
        // DEFERRED UPGRADE: the association landed under a track that is already playing. Committing here would swap the
        // media host mid-track and restart the song from 0 — the badge is the whole notification, and the user's click on
        // it (ToggleVideo, which re-folds fresh availability) is what actually starts the video.
        if (VideoUpgradeGate.DeferUpgrade(VideoSurface.Peek(), target, commitUpgrade)) return;
        CommitVideoSurface(target, clearConnectAudioFirst: false);   // an availability edge is never an explicit media intent
        // Already-active but source-less (e.g. the track changed under a live surface): kick a resolve so the surface has
        // something to play. Gen-fenced at publish, so this is safe to call redundantly (last request wins).
        if (has && VideoActive() && PopOutVideoSource.Peek() is null)
            RequestPopOutSource(uri);
    }

    // ── the now-playing video affordance, as a decision record ────────────────────────────────────────────────────────
    // RecomputeHasVideo runs on EVERY store change relevant to the current track (and on every bulk — which is what a
    // detect slice produces), so this logs only on a real edge: a new uri, or the same uri's verdict flipping. That keeps
    // one line per track change plus one when an association lands late and heals the badge.
    string? _videoDiagUri;
    bool _videoDiagHas;
    bool _videoDiagSeen;

    void LogVideoAffordance(string? uri, bool planeAnswer, bool afterLatches)
    {
        if (_videoDiagSeen
            && string.Equals(_videoDiagUri, uri, StringComparison.Ordinal)
            && _videoDiagHas == afterLatches) return;
        _videoDiagUri = uri; _videoDiagHas = afterLatches; _videoDiagSeen = true;
        if (!WaveeLog.Instance.IsEnabled(WaveeLogLevel.Debug)) return;
        var assoc = string.IsNullOrEmpty(uri) ? null : _store?.GetVideoAssociation(uri);
        WaveeLog.Instance.Event(WaveeLogLevel.Debug, "playback", "video.assoc.nowplaying",
            "now-playing video affordance evaluated",
            fields:
            [
                WaveeLogField.Of("uri", uri ?? "-"),
                // The three inputs, separately, so a "no video" verdict names its own cause: no association row at all,
                // a row that says no, a user attachment, or a latch/proven-dead override of a true plane answer.
                WaveeLogField.Of("assoc", assoc is null ? "none" : assoc.HasVideo ? "hasVideo" : "noVideo"),
                WaveeLogField.Of("gid", assoc?.VideoGidHex ?? "-"),
                WaveeLogField.Of("counterpart", assoc?.CounterpartUri ?? "-"),
                WaveeLogField.Of("override", !string.IsNullOrEmpty(uri) && _overrides is { } o && o.Has(uri)),
                WaveeLogField.Of("plane", planeAnswer), WaveeLogField.Of("final", afterLatches),
                WaveeLogField.Of("latched", _hasVideoLatchedUri ?? "-"), WaveeLogField.Of("dead", _videoDeadUri ?? "-"),
            ]);
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
        WireRestoreSeam();   // the inner player can swap fake→live under the switchable — cheap reference check
        bool wasPlaying = _lastPushIsPlaying;
        _lastPushIsPlaying = s.IsPlaying;
        var prevUri = CurrentTrack.Value?.Uri;
        CurrentTrack.Value = s.CurrentTrack;
        // Null/empty next uri is a mid-push glitch — NOT a track boundary. Clearing the has-video latch here would
        // defeat HasVideoLatch's null-suppression and cost a Video→Audio→Video round trip (the observed custom-video thrash).
        bool trackBoundary = VideoOverrideMutationCore.IsRealTrackBoundary(prevUri, s.CurrentTrack?.Uri);
        if (trackBoundary)
        {
            // A new track. The video INTENT is sticky in BOTH directions (on carries video across tracks; off keeps
            // every later track on audio until the user turns it back on) — do NOT touch it. Fence any
            // in-flight resolve for the previous track, cancel it, and clear that track's resolved source.
            // RecomputeHasVideo() (below) then re-resolves for the new track iff it is active — so a video-less track
            // just goes inactive while a video track auto-continues.
            _videoResolveGen++;
            _videoResolveCts?.Cancel();
            _hasVideoLatchedUri = null;   // a REAL track change ends the has-video latch (see RecomputeHasVideo)
            _videoDeadUri = null;         // …and the proven-no-video latch: it is scoped to ONE playable, never sticky
            // A ONE-PLAY video intent dies with its track: moving to any other uri clears the scope AND takes the
            // surface down (refresh-less — the new track already resolved audio through the scope gate in
            // ShouldPlayAsVideo, so there is no media kind left to change). The standing intent — the "do NOT touch it"
            // note above — is exactly the null-scope case and stays untouched.
            if (_videoIntentScopeUri is { } scopedUri && !string.Equals(scopedUri, s.CurrentTrack?.Uri, StringComparison.Ordinal))
            {
                _videoIntentScopeUri = null;
                CommitVideoSurface(PlacementCore.TurnOff(VideoSurface.Peek()), clearConnectAudioFirst: false, refreshKind: false);
            }
            // NOTE: a track change no longer touches the placement state at all. It used to bump a CONTENT generation
            // that expired a per-track dismiss — the machinery that re-opened a video the user had closed. Closing is
            // now plain off (PlacementCore.TurnOff), so there is nothing per-track left to expire, and the only edge a
            // new track produces is the availability recompute below (RecomputeHasVideo → CommitVideoSurface), which is
            // what swaps the media for a still-on intent.
            // Keep a source the PLAYBACK path already resolved+published for THIS (new) track: the controller resolves the
            // video BEFORE it publishes the track change, so clearing here would unmount the live surface for a frame and
            // force a second resolve for a source we already have. Anything else (a stale source) is cleared as before.
            if (!string.Equals(_videoSourceUri, s.CurrentTrack?.Uri, StringComparison.Ordinal))
            {
                _videoSourceUri = null;
                PopOutVideoSource.Value = null;
            }
        }
        // Reflect the new track's cached video state (+ re-resolve if VideoActive). ONLY a real track boundary may commit
        // an upgrade — PushState also fires on every pause/volume/heartbeat push, and passing true unconditionally would
        // re-enable the mid-track auto-swap one push after the association landed.
        RecomputeHasVideo(commitUpgrade: trackBoundary);
        CurrentContext.Value = s.ContextUri;
        Identity.Value = new PlaybackIdentity(s.ContextUri, s.CurrentTrack);
        // The local play log (§C1.8.1) — the sidebar's "Recently played" source. ONLY at a real track boundary: PushState
        // also fires on every pause/volume/heartbeat push, and the store's own 1 s (track, context) idempotence is a
        // second line of defence, not the gate. Null-safe: an unattached log records nothing.
        if (trackBoundary && s.CurrentTrack is { } played)
            _playLog?.Append(played.Uri, s.ContextUri, contextTitle: JumpListBridge.FromTrack(played, s.ContextUri));
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
        Queue.Value = s.Queue;
        bool queueChanged = BumpQueueRevision(s.Queue);
        PlaybackBucketDiagnostics.QueueIfChanged(ref _lastQueueDiagSig, "bridge.ui.push-state",
            s.Queue, s.ContextUri, s.CurrentTrack?.Uri);
        // Session-snapshot write gates (§8): a real track boundary, a pause edge, or a queue-content change — never a
        // position tick (PushPosition doesn't come through here) and never a volume/heartbeat republish (no gate trips).
        MaybeWritePlaybackSnapshot(s, trackBoundary, pauseEdge: wasPlaying && !s.IsPlaying, queueChanged);
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
        _taskbar?.OnStateChanged();
        _jumpList?.OnStateChanged();
    }

    /// <summary>Element-IDENTITY equality for the <see cref="Queue"/> signal (see its remarks). Deliberately NOT the
    /// <see cref="BumpQueueRevision"/> fold: that one folds SET identity only (id/bucket/provider) and would wrongly
    /// coalesce a metadata enrichment, freezing stale titles/art in the panel. Reference-comparing the entries is instead
    /// conservative by construction — <c>QueueEntry</c> is a record, so any re-mapped or enriched row is a NEW instance
    /// and still publishes; only a pure re-window (same instances, new list) is coalesced.</summary>
    sealed class QueueListIdentityComparer : IEqualityComparer<IReadOnlyList<QueueEntry>>
    {
        public static readonly QueueListIdentityComparer Instance = new();

        public bool Equals(IReadOnlyList<QueueEntry>? a, IReadOnlyList<QueueEntry>? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        public int GetHashCode(IReadOnlyList<QueueEntry> value) => value.Count;   // never keyed on; Equals is the contract
    }

    // Fold the queue's SET identity (count + per-row id/bucket/provider — QueueContentFold, unit-tested in the Backend)
    // and bump the revision only on a real change. Returns whether the content changed (a snapshot-write gate, §8).
    bool BumpQueueRevision(IReadOnlyList<QueueEntry> queue)
    {
        ulong fold = QueueContentFold.Fold(queue);
        if (_haveQueueFold && fold == _queueContentFold) return false;
        _haveQueueFold = true;
        _queueContentFold = fold;
        QueueRevision.Value = ++_queueRev;
        // The queue is the one surface whose rows come from the player, not from a container read, so nothing else ever
        // detects them. Firing off the content-CHANGED branch (PushState calls this on every push) is the dedupe.
        if (DetectVideos is { } detect && queue.Count > 0)
        {
            var uris = new List<string>(queue.Count);
            for (int i = 0; i < queue.Count; i++)
                if (queue[i].Track?.Uri is { Length: > 0 } u) uris.Add(u);
            if (uris.Count > 0) { try { _ = detect(uris); } catch { } }
        }
        return true;
    }

    /// <summary>Set at go-live: batch music-video detection for the queue's tracks, fired when the queue's CONTENT fold
    /// changes. Fire-and-forget (it runs on the UI push path); null on the fake backend → no detection.</summary>
    public Func<IReadOnlyList<string>, Task>? DetectVideos;

    // ── playback session snapshot: reader wiring + the debounced writer (playback-restore fix §8) ───────────────────────

    // Wire the restore READER onto the concrete controller behind the switchable facade. Idempotent per inner instance;
    // re-run on every push because the fake→live swap replaces the inner player without re-pointing this bridge.
    void WireRestoreSeam()
    {
        var inner = (_player as SwitchablePlayer)?.Inner ?? _player;
        if (ReferenceEquals(inner, _restoreWiredTo)) return;
        _restoreWiredTo = inner;
        _restoreController = inner as PlaybackController;
        if (_restoreController is { } pc && pc.RestoreSnapshot is null)
            pc.RestoreSnapshot = static () => MapRestoreSnapshot(SessionSnapshotStore.Active?.PlaybackSection);
    }

    // SessionPlaybackDto (the persisted session.json shape) → the proto-free Backend record the controller consumes.
    static PlaybackSessionSnapshot? MapRestoreSnapshot(SessionPlaybackDto? dto)
    {
        if (dto is null || dto.TrackUri is not { Length: > 0 } uri) return null;
        IReadOnlyList<QueuedRef> queue = Array.Empty<QueuedRef>();
        if (dto.UserQueueUris is { Length: > 0 } uris)
        {
            var refs = new QueuedRef[uris.Length];
            for (int i = 0; i < uris.Length; i++) refs[i] = new QueuedRef(uris[i], "", "queue");
            queue = refs;
        }
        var repeat = dto.RepeatMode switch { "context" => RepeatMode.Context, "track" => RepeatMode.Track, _ => RepeatMode.Off };
        return new PlaybackSessionSnapshot(dto.ContextUri ?? "", uri, dto.TrackUid ?? "", dto.TrackIndex,
            Math.Max(0, dto.PositionMs), dto.Shuffle, repeat, queue, dto.AutoplayActive, dto.AutoplayContextUri);
    }

    // The WRITER (§8): track boundary / pause edge / queue-content change — never a position tick (the store's own 2 s
    // debounce coalesces bursts on top). Gated on a live LOCAL session so viewer-mode pushes can't overwrite the persisted
    // local one, and always written with paused=true semantics (a restored session never autoplays — crash-safety).
    void MaybeWritePlaybackSnapshot(IPlaybackState s, bool trackBoundary, bool pauseEdge, bool queueChanged)
    {
        if (!trackBoundary && !pauseEdge && !queueChanged) return;
        if (SessionSnapshotStore.Active is not { } store || store.WritesBlocked) return;
        if (_restoreController is not { } pc || !pc.HasLocalSession) return;
        if (s.CurrentTrack is not { } track || string.IsNullOrEmpty(track.Uri)) return;

        var hints = pc.RestoreWriterHints;
        string uid = "";
        List<string>? queueUris = null;
        bool autoplay = false;
        var rows = s.Queue;
        for (int i = 0; i < rows.Count; i++)
        {
            var e = rows[i];
            if (e.Bucket == QueueBucket.NowPlaying) uid = e.Uid;
            else if (e.Bucket == QueueBucket.UserQueue && e.Track.Uri is { Length: > 0 } qu)
                (queueUris ??= new List<string>()).Add(qu);
            if (e.IsAutoplay) autoplay = true;
        }
        store.UpdatePlayback(new SessionPlaybackDto
        {
            ContextUri = s.ContextUri,
            ContextKind = ContextKindOf(s.ContextUri),
            TrackUri = track.Uri,
            TrackUid = uid,
            TrackIndex = hints.ContextIndex,
            PositionMs = Math.Max(0, s.PositionMs),
            Paused = true,   // §8 — always written paused: a crash/relaunch restores silent, never mid-song audio
            Shuffle = s.IsShuffle,
            RepeatMode = s.Repeat switch { RepeatMode.Context => "context", RepeatMode.Track => "track", _ => "off" },
            UserQueueUris = queueUris?.ToArray(),
            AutoplayActive = autoplay,
            AutoplayContextUri = hints.AutoplayContextUri,
            CapturedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    static string? ContextKindOf(string? contextUri)
    {
        if (string.IsNullOrEmpty(contextUri)) return null;
        int first = contextUri.IndexOf(':');
        if (first < 0 || first + 1 >= contextUri.Length) return null;
        int second = contextUri.IndexOf(':', first + 1);
        return second > first + 1 ? contextUri[(first + 1)..second] : null;
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
        _taskbar?.OnPositionChanged(ms);
    }
}
