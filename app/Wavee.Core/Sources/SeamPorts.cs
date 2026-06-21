namespace Wavee.Core;

// Seam-only facet ports (docs/architecture.md §4.2, §9). These are DEFINED now so the federation has stable contracts,
// but are NOT implemented in the first pass — playback/remote/session/lyrics stay served by the existing in-process
// fakes wired through Services. When a real Spotify account source or a local-files source lands, it implements the
// facets it supports and declares them via SourceCapabilities; the UI and the aggregate do not change shape.

/// <summary>The Playback facet: a source that owns a player for the contexts it owns (routes play(contextUri) by URI).
/// Mirrors <see cref="IPlaybackPlayer"/> scoped to one source; a future FederatedPlayback surfaces a unified state.</summary>
public interface IPlaybackSource : ISource
{
    IPlaybackState State { get; }
    Task PlayAsync(string contextUri, int startIndex = 0, CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    Task NextAsync(CancellationToken ct = default);
    Task PreviousAsync(CancellationToken ct = default);
    Task SeekAsync(long positionMs, CancellationToken ct = default);
    Task SetShuffleAsync(bool on, CancellationToken ct = default);
    Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default);
    Task SetVolumeAsync(double volume01, CancellationToken ct = default);
}

/// <summary>The Remote/Connect facet: device list + transfer + the live remote state stream. A future FederatedRemote
/// merges device lists across sources and routes transfer to the active one.</summary>
public interface IRemoteSource : ISource
{
    IReadOnlyList<PlaybackDevice> Devices { get; }
    IObservable<IReadOnlyList<PlaybackDevice>> DevicesChanged { get; }
    Task TransferAsync(string deviceId, CancellationToken ct = default);
}

/// <summary>The Session/account facet: auth + current user + the gating context (tier / market / locale) a real
/// source carries. Availability checks at queue time consult this.</summary>
public interface ISessionSource : ISource
{
    AuthStatus Status { get; }
    WaveeUser? CurrentUser { get; }
    IObservable<AuthStatus> StatusChanged { get; }
}

/// <summary>The Lyrics facet.</summary>
public interface ILyricsSource : ISource
{
    Task<LyricsDocument?> GetLyricsAsync(string trackId, CancellationToken ct = default);
}

/// <summary>The Mutations facet: save/follow + playlist edits + folders (optimistic + outbox). Gated by the playlist's
/// <see cref="PlaylistCapabilities"/> and this source's <see cref="SourceCapabilities.Mutations"/> flag.</summary>
public interface IMutationSource : ISource
{
    Task SaveAsync(string uri, CancellationToken ct = default);
    Task RemoveAsync(string uri, CancellationToken ct = default);
}
