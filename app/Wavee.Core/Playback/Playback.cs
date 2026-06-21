namespace Wavee.Core;

public enum RepeatMode { Off, Context, Track }

/// <summary>Playback command surface. The real implementation marshals these to the out-of-process
/// x64 AudioHost over a named pipe; the fake implementation is in-process. State is observed via
/// <see cref="IPlaybackState"/>, never returned from commands.</summary>
public interface IPlaybackPlayer
{
    Task PlayAsync(string contextUri, int startIndex = 0, CancellationToken ct = default);
    Task PlayTrackAsync(string trackUri, CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    Task NextAsync(CancellationToken ct = default);
    Task PreviousAsync(CancellationToken ct = default);
    Task SeekAsync(long positionMs, CancellationToken ct = default);
    Task SetVolumeAsync(double volume01, CancellationToken ct = default);
    Task SetShuffleAsync(bool on, CancellationToken ct = default);
    Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default);
    Task MoveQueueAsync(string entryId, int toIndex, CancellationToken ct = default);
    Task RemoveFromQueueAsync(string entryId, CancellationToken ct = default);
    IPlaybackState State { get; }
}

/// <summary>Observable playback state. Position is authoritative-frame + 1 Hz interpolation
/// (mirrors the real IPC snapshot cadence — the UI interpolates per-frame between ticks).</summary>
public interface IPlaybackState : System.ComponentModel.INotifyPropertyChanged
{
    Track? CurrentTrack { get; }
    /// <summary>The URI of the context currently playing (the playlist/album/liked uri) — what a card compares its own
    /// uri against to show the now-playing equalizer. Null when nothing was started from a context.</summary>
    string? ContextUri { get; }
    bool IsPlaying { get; }
    bool IsBuffering { get; }
    long PositionMs { get; }
    long DurationMs { get; }
    double Volume { get; }
    bool IsShuffle { get; }
    RepeatMode Repeat { get; }
    Palette? Palette { get; }
    IReadOnlyList<QueueEntry> Queue { get; }

    /// <summary>Coarse "something changed" signal (track / play-state / queue / palette).</summary>
    IObservable<IPlaybackState> Changes { get; }

    /// <summary>Emits the current position in ms ~once per second while playing; re-anchors on track change.</summary>
    IObservable<long> PositionTicks { get; }
}

public enum DeviceKind { ThisDevice, Phone, Computer, Speaker, Tv }
public sealed record PlaybackDevice(string Id, string Name, DeviceKind Kind, bool IsActive, int VolumePercent);

/// <summary>Spotify Connect device list + transfer seam.</summary>
public interface IConnectDevices
{
    IReadOnlyList<PlaybackDevice> Devices { get; }
    IObservable<IReadOnlyList<PlaybackDevice>> DevicesChanged { get; }
    Task TransferAsync(string deviceId, CancellationToken ct = default);
}

public sealed record LyricSyllable(long StartMs, long EndMs, string Text);
public sealed record LyricLine(long StartMs, string Text, IReadOnlyList<LyricSyllable> Syllables);
public sealed record LyricsDocument(string TrackId, bool IsSynced, IReadOnlyList<LyricLine> Lines);

public interface ILyricsProvider
{
    Task<LyricsDocument?> GetLyricsAsync(string trackId, CancellationToken ct = default);
}
