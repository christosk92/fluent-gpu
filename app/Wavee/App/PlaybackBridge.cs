using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Backend;
using Wavee.Core;

namespace Wavee;

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

    // ── UI signals (read by components) ─────────────────────────────────────────────────────────────────────────────
    public Signal<Track?> CurrentTrack { get; } = new(null);
    /// <summary>The currently-playing context uri (playlist/album/liked) — content cards compare their own uri to this
    /// to show the now-playing equalizer.</summary>
    public Signal<string?> CurrentContext { get; } = new(null);
    public Signal<bool> IsPlaying { get; } = new(false);
    public Signal<bool> IsBuffering { get; } = new(false);
    // Player-bar display states the IPlaybackState snapshot doesn't carry yet (the real provider drives these; default
    // off, so the fake/in-process path is unchanged). Loading = the initial track resolve before audio; Error = a
    // non-null user-facing message (the bar shows it + offers retry on the primary). See PlayerBar.PlayerState.
    public Signal<bool> IsLoading { get; } = new(false);
    public Signal<string?> Error { get; } = new(null);
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
    public Signal<IReadOnlyList<PlaybackDevice>> Devices { get; } = new(Array.Empty<PlaybackDevice>());
    public Signal<AuthStatus> Auth { get; } = new(AuthStatus.LoggedOut);
    public Signal<WaveeUser?> User { get; } = new(null);
    /// <summary>The rich login projection driving the full-screen login takeover (device-code / QR / phase). Fed by the
    /// live bootstrap through <see cref="Progress"/>; the coarse <see cref="Auth"/> still gates shell ↔ takeover.</summary>
    public Signal<LoginSnapshot> Login { get; } = new(new(LoginPhase.LoggedOut));

    /// <summary>UI-only: the full now-playing view is open. The player-bar expand button toggles it; the shell renders the
    /// panel as a top layer. Lives on the bridge so any component under the playback context can open/close it.</summary>
    public Signal<bool> Expanded { get; } = new(false);

    /// <summary>The now-playing track has an accompanying music video (the <c>VideoService</c> association, detected
    /// asynchronously after the track resolves). Drives the player-bar video button's visibility. Fed by the optional
    /// store probe (<see cref="AttachStore"/>); the fake backend has none, so it stays false.</summary>
    public Signal<bool> CurrentTrackHasVideo { get; } = new(false);
    /// <summary>UI-only swap intent: the user picked "video" for the now-playing track. The actual video surface/host is a
    /// follow-up — for now this is the seam the player-bar button toggles (reset on every track change).</summary>
    public Signal<bool> PreferVideo { get; } = new(false);

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
        if (s.CurrentTrack?.Uri != prevUri) PreferVideo.Value = false;   // a new track resets the swap toggle
        RecomputeHasVideo();                                            // reflect the new track's cached video state (if any)
        CurrentContext.Value = s.ContextUri;
        IsPlaying.Value = s.IsPlaying;
        IsBuffering.Value = s.IsBuffering;
        IsShuffle.Value = s.IsShuffle;
        Repeat.Value = s.Repeat;
        Volume.Value = (float)s.Volume;
        DurationMs.Value = s.DurationMs;
        TrackPalette.Value = s.Palette;
        Queue.Value = s.Queue;
        IsLoading.Value = s.IsLoading;
        Error.Value = s.Error;
        CanSkipNext.Value = s.CanSkipNext;
        CanSkipPrev.Value = s.CanSkipPrev;
        CanSeek.Value = s.CanSeek;
        ActiveDeviceId.Value = s.ActiveDeviceId;
        PushPosition(s.PositionMs);
    }

    void PushPosition(long ms)
    {
        PositionMs.Value = ms;
        long dur = DurationMs.Value;
        PositionFrac.Value = dur > 0 ? Math.Clamp(ms / (float)dur, 0f, 1f) : 0f;
    }
}
