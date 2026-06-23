namespace Wavee.Core;

public enum LibraryItemKind { Track, Album, Artist, Playlist }

public sealed record LibraryItem(string Uri, string Title, string? Subtitle, Image? Image, LibraryItemKind Kind);

public sealed record SearchResults(
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<Album> Albums,
    IReadOnlyList<Artist> Artists,
    IReadOnlyList<Playlist> Playlists)
{
    public static readonly SearchResults Empty = new(
        System.Array.Empty<Track>(), System.Array.Empty<Album>(), System.Array.Empty<Artist>(), System.Array.Empty<Playlist>());
}

/// <summary>One page of a streamed track list (skeleton-then-stream — see docs/architecture.md §3/§6): the tracks
/// resolved so far, the running loaded count, and the known total (so the UI can size a progress cue up front).</summary>
public sealed record TrackPage(IReadOnlyList<Track> Tracks, int Loaded, int Total);

/// <summary>The read-API facade. Collapses WaveeMusic's library / album / artist / search read
/// paths (Pathfinder + SpClient) behind one async surface the UI binds against.</summary>
public interface IMusicLibrary
{
    Task<Playlist> GetPlaylistAsync(string id, CancellationToken ct = default);
    Task<Album> GetAlbumAsync(string id, CancellationToken ct = default);
    Task<Artist> GetArtistAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default);
    Task<SearchResults> SearchAsync(string query, CancellationToken ct = default);

    // Per-collection read paths — the sidebar's "Your Library" rows route to their own page, each loading its own slice.
    Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetLikedSongsAsync(CancellationToken ct = default);

    // Sidebar IA read paths — the "Your Library" badge counts and the (flat) playlist list. Async so the shell can
    // skeleton-load them like everything else (the folder-capable tree is a later enhancement).
    Task<LibraryStats> GetStatsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default);

    // Streamed track loading (skeleton-then-stream): the detail header loads via GetPlaylist/GetAlbum, then the rows
    // page in from here so a big context fills progressively instead of blocking on the whole list.
    IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, CancellationToken ct = default);

    // The condensed, grouped home feed (replaces the four-separate-collection-calls home). Merged across sources.
    Task<HomeFeed> GetHomeAsync(CancellationToken ct = default);

    // Podcasts (federated to the Podcasts-capable sources): the library grid of shows + a single show's episodes.
    Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct = default);
    Task<Show?> GetShowAsync(string uri, CancellationToken ct = default);
}
