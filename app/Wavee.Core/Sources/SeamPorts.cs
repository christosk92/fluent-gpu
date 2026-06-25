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

/// <summary>The Podcasts facet: shows + their episodes (docs/architecture.md §2). A capability-segregated read port kept
/// OFF <see cref="ICatalogSource"/> so music-only sources don't carry empty podcast reads; the aggregate routes to it via
/// <c>OfCapability(Podcasts)</c>. (The export has no podcast data, so the in-process source synthesizes it.)</summary>
public interface IPodcastSource : ISource
{
    Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct = default);
    Task<Show?> GetShowAsync(string uri, CancellationToken ct = default);
}

/// <summary>The Mutations facet: save / like / follow (saved-state) — optimistic local writes the UI gates on
/// <see cref="SourceCapabilities.Mutations"/> (and, for playlist item edits, the playlist's <see cref="PlaylistCapabilities"/>).
/// The set spans tracks (like), albums (save) and artists + playlists (follow); a real source reconciles via an outbox +
/// revision conflicts (docs/architecture.md §3). Playlist item edits + folders are the next Mutations increment (§9 seam).</summary>
public interface IMutationSource : ISource
{
    /// <summary>Snapshot of the currently saved / liked / followed uris.</summary>
    IReadOnlySet<string> Saved { get; }
    bool IsSaved(string uri);
    /// <summary>Emits the full saved-set on every change, so a bridge can mirror it into an engine Signal (§6).</summary>
    IObservable<IReadOnlySet<string>> SavedChanged { get; }
    /// <summary>Set the saved/followed state of a uri (idempotent) — optimistic + persisted in the in-process source.</summary>
    Task SetSavedAsync(string uri, bool saved, CancellationToken ct = default);
}
