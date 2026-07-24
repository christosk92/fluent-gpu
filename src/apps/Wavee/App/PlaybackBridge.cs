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
    /// <summary>STICKY "watch video" intent: the user prefers video for playback. It carries ACROSS track changes (a new
    /// track that also has a video keeps playing video) and is cleared ONLY by an explicit "switch to audio" / close-video
    /// (see <see cref="PlayerBar"/>). A placement change (PiP ↔ detached) never clears it. This is one of the two inputs to
    /// <see cref="VideoActive"/>; NEVER read it alone to decide whether a video surface should be live — read
    /// <see cref="VideoActive"/>, which also honors has-video and the per-track PiP dismiss.</summary>
    public Signal<bool> PreferVideo { get; } = new(false);

    /// <summary>The SINGLE owned placement state: where the now-playing video plays when <see cref="VideoActive"/> is true.
    /// Defaults to <see cref="VideoPlacement.Detached"/> — the primary "watch video" action pops out. The one owner of the
    /// surfaces (<c>VideoPlacementHost</c> for the detached window; <c>InWindowVideoPip</c> self-gates on it) derives from
    /// this + <see cref="VideoActive"/>; the player-bar button highlights derive from it too, so the toggle can never get
    /// out of sync with the actual surface state.</summary>
    public Signal<VideoPlacement> VideoPlacement { get; } = new(Wavee.VideoPlacement.Detached);

    // ── video placement plumbing (single-owner model) ──────────────────────────────────────────────────────────────
    // Monotonic per-track generation, bumped on every track change (PushState). Used both to expire the PiP-dismiss
    // (below) and to fence stale async video resolves (see _videoResolveGen).
    long _trackGen;
    // The track generation the user DISMISSED the video for (PiP ✕). While it equals _trackGen, VideoActive() is false
    // for this track WITHOUT clearing the sticky PreferVideo — audio keeps playing, the surface hides, and the next track
    // (a higher _trackGen) or an explicit RestoreVideo() re-activates. A signal so VideoActive() re-evaluates reactively.
    // -1 = "not dismissed" (_trackGen is never negative).
    readonly Signal<long> _dismissedForTrackGen = new(-1L);

    /// <summary>The one predicate every video surface + player-bar highlight reads: a video should be live iff the user
    /// prefers video, the current track HAS a video, and the video is not dismissed for this track. Reads signals, so a
    /// caller in <c>Render</c> subscribes and re-derives automatically — this is what keeps the button toggle, the PiP,
    /// and the detached window from desyncing (they all derive from this ONE truth, not three separate flags).</summary>
    public bool VideoActive()
        => VideoPlacementLogic.VideoActive(PreferVideo.Value, CurrentTrackHasVideo.Value, _trackGen, _dismissedForTrackGen.Value);

    /// <summary>Dismiss the video for the CURRENT track (the PiP ✕): audio keeps playing and the surface hides until the
    /// track changes or <see cref="RestoreVideo"/> is called. Does NOT clear the sticky <see cref="PreferVideo"/>.</summary>
    public void DismissVideoForCurrentTrack() => _dismissedForTrackGen.Value = _trackGen;

    /// <summary>Clear a per-track PiP dismiss so <see cref="VideoActive"/> can light up again for the current track (used by
    /// the player-bar "watch video" intents, so re-clicking video after dismissing the PiP restores it).</summary>
    public void RestoreVideo() => _dismissedForTrackGen.Value = -1L;

    /// <summary>The resolved video source for the now-playing track (null = none resolved yet): a clear/Canvas URL or a
    /// PlayReady DRM descriptor + license relay. The pop-out / inline video surface plays it (clear on the MF backend,
    /// DRM via the native CDM). The Spotify video-resolution layer (Canvas from the feed; PlayReady once the probe
    /// confirms it) populates it. Reset to null on every track change.</summary>
    public Signal<Wavee.SpotifyLive.PopOutVideoSource?> PopOutVideoSource { get; } = new(null);

    /// <summary>The video-resolution delegate (track uri → a playable <c>PopOutVideoSource</c>), wired by the live
    /// bootstrap to <c>SpotifyVideoService.ResolvePlayableAsync</c>; null on the fake/offline backend. Off the UI thread.</summary>
    public System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<Wavee.SpotifyLive.PopOutVideoSource?>>? ResolveVideoSource;

    // Monotonic resolve generation (bug 4 guard): each RequestPopOutSource captures ++_videoResolveGen; a track change
    // (PushState) also bumps it. An async resolve only publishes if its captured gen is still current — a resolve for a
    // superseded track is dropped instead of overwriting the current track's source with a stale video. The CTS cancels
    // the previous in-flight resolve so a dropped one also stops early. UI-thread only (every writer is post-marshalled).
    long _videoResolveGen;
    System.Threading.CancellationTokenSource? _videoResolveCts;

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
        post(() => { if (VideoPlacementLogic.ShouldPublishResolve(gen, _videoResolveGen)) PopOutVideoSource.Value = src; });   // drop a stale (superseded-track) resolve
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

    void RecomputeHasVideo()
    {
        var uri = CurrentTrack.Value?.Uri;
        bool has = false;
        if (!string.IsNullOrEmpty(uri) && _store is { } store)
            has = (store.GetVideoAssociation(uri)?.HasVideo ?? false) || (store.GetTrack(uri)?.HasVideo ?? false);
        CurrentTrackHasVideo.Value = has;
        // Sticky video: if the user prefers video and this track has one (whether known at the track change or arriving
        // asynchronously after it) but no source is resolved yet, kick a resolve so the surface has something to play.
        // Gen-fenced at publish, so this is safe to call redundantly (last request wins).
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
            // A new track. PreferVideo is STICKY now (video carries across tracks) — do NOT clear it. Bump the track
            // generation (expires any PiP-dismiss AND fences an in-flight resolve for the previous track), cancel that
            // resolve, and clear the previous track's resolved source. RecomputeHasVideo() (next line) re-resolves for
            // the new track iff VideoActive() — so a video-less track just goes inactive, a video track auto-continues.
            _trackGen++;
            _videoResolveGen++;
            _videoResolveCts?.Cancel();
            _dismissedForTrackGen.Value = -1L;   // the new track is not dismissed
            PopOutVideoSource.Value = null;
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
