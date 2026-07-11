using System.ComponentModel;

namespace Wavee.Core;

/// <summary>
/// The remote-only player: LOCAL audio playback is not supported yet. Every PLAY intent raises
/// <see cref="OnPlayIntentRejected"/> synchronously (the composition root wires it to the critical
/// "choose a remote device" toast + the device-picker action); every other verb no-ops. State is
/// permanently empty. This replaces the old in-process FakePlaybackProvider as the pre-login / offline
/// player — after a live login the switchable facade swaps in the real <c>PlaybackController</c>, which
/// forwards playback to the active Connect device (and rejects LOCAL play with the same toast).
/// </summary>
public sealed class UnsupportedPlaybackPlayer : IPlaybackPlayer, IPlaybackState
{
    readonly SimpleSubject<IPlaybackState> _changes = new();
    readonly SimpleSubject<long> _ticks = new();

    /// <summary>Raised (synchronously, on the caller's thread) whenever a play intent is attempted. The app layer wires
    /// this to the "playback on this device isn't supported yet — choose a remote device" toast; null = silent.</summary>
    public Action? OnPlayIntentRejected { get; set; }

    public IPlaybackState State => this;

    // ── IPlaybackState — permanently empty (nothing ever plays locally) ─────────────────────────────────────────────
    public Track? CurrentTrack => null;
    public string? ContextUri => null;
    public bool IsPlaying => false;
    public bool IsBuffering => false;
    public long PositionMs => 0;
    public long DurationMs => 0;
    public double Volume => 0.7;                     // matches the bridge's default so the volume slider doesn't jump
    public bool IsShuffle => false;
    public RepeatMode Repeat => RepeatMode.Off;
    public Palette? Palette => null;
    public IReadOnlyList<QueueEntry> Queue => Array.Empty<QueueEntry>();
    // No skipping / seeking when nothing can play (the widened surface; the player bar disables them via NoTrack anyway).
    public bool IsLoading => false;
    public string? Error => null;
    public bool CanSkipNext => false;
    public bool CanSkipPrev => false;
    public bool CanSeek => false;
    public string? ActiveDeviceId => null;
    public IObservable<IPlaybackState> Changes => _changes;
    public IObservable<long> PositionTicks => _ticks;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }   // consumers use Changes

    static readonly Task Done = Task.CompletedTask;
    Task Reject() { OnPlayIntentRejected?.Invoke(); return Done; }

    // ── IPlaybackPlayer — play intents reject; everything else no-ops ───────────────────────────────────────────────
    public Task PlayAsync(string contextUri, int startIndex = 0, CancellationToken ct = default) => Reject();
    public Task PlayContextTrackAsync(string contextUri, PlaybackContextTrack track, int fallbackIndex = 0, CancellationToken ct = default) => Reject();
    public Task PlayOrderedAsync(string contextUri, IReadOnlyList<PlaybackContextTrack> tracks, int startIndex = 0, CancellationToken ct = default) => Reject();
    public Task PlayTrackAsync(string trackUri, CancellationToken ct = default) => Reject();
    public Task PlayTrackAsync(Track track, CancellationToken ct = default) => Reject();
    public Task ResumeAsync(CancellationToken ct = default) => Reject();
    public Task EnqueueAsync(string trackUri, CancellationToken ct = default) => Reject();
    public Task EnqueueAsync(Track track, CancellationToken ct = default) => Reject();
    public Task PlayNextAsync(IReadOnlyList<PlaybackContextTrack> tracks, CancellationToken ct = default) => Reject();

    // Radio is a play intent → fire the "choose a remote device" prompt and report "no radio" (null) so the caller shows
    // the graceful "couldn't start radio" affordance rather than a phantom "Radio started".
    public Task<string?> StartRadioAsync(string seedUri, string? displayName = null, CancellationToken ct = default)
    { OnPlayIntentRejected?.Invoke(); return Task.FromResult<string?>(null); }

    public Task PauseAsync(CancellationToken ct = default) => Done;
    public Task NextAsync(CancellationToken ct = default) => Done;
    public Task PreviousAsync(CancellationToken ct = default) => Done;
    public Task SeekAsync(long positionMs, CancellationToken ct = default) => Done;
    public Task SetVolumeAsync(double volume01, CancellationToken ct = default) => Done;
    public Task SetShuffleAsync(bool on, CancellationToken ct = default) => Done;
    public Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default) => Done;
    public Task SkipToQueueItemAsync(QueueItemId id, CancellationToken ct = default) => Reject();
    public Task MoveQueueItemAsync(QueueItemId id, int newPos, CancellationToken ct = default) => Done;
    public Task RemoveQueueItemAsync(QueueItemId id, CancellationToken ct = default) => Done;
    public Task ClearQueueAsync(CancellationToken ct = default) => Done;
    public Task ClearHistoryAsync(CancellationToken ct = default) => Done;
}

/// <summary>The empty Connect roster (pre-login / logged out). Real devices arrive only from the live Connect cluster
/// after login — before that the device picker shows its empty state.</summary>
public sealed class NoConnectDevices : IConnectDevices
{
    readonly SimpleSubject<IReadOnlyList<PlaybackDevice>> _changed = new(Array.Empty<PlaybackDevice>());
    public IReadOnlyList<PlaybackDevice> Devices => Array.Empty<PlaybackDevice>();
    public IObservable<IReadOnlyList<PlaybackDevice>> DevicesChanged => _changed;
    public Task TransferAsync(string deviceId, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>No lyrics (replaces the old fake's Lyrics facet). The live <c>AggregatingLyricsProvider</c> swaps in on login.</summary>
public sealed class NoLyricsProvider : ILyricsProvider
{
    public Task<LyricsDocument?> GetLyricsAsync(string trackId, CancellationToken ct = default)
        => Task.FromResult<LyricsDocument?>(null);
}
