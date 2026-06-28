namespace Wavee.Core;

/// <summary>A connected source of music (a Spotify account, a local-files library, the synthetic fallback). It owns a
/// URI namespace and declares which facets it supports. Facets are SEGREGATED into narrow ports (this file is the
/// Catalog facet; see SeamPorts.cs for the playback/remote/session/lyrics ports) so a source implements only what it
/// can — no god-interface (docs/architecture.md §4).</summary>
public interface ISource
{
    /// <summary>Stable id, e.g. "spotify", "local", "fake".</summary>
    string Id { get; }

    /// <summary>True if this source owns/can-resolve the given URI (by scheme/namespace). The aggregate routes
    /// single-item reads to the first owning source.</summary>
    bool Owns(string uri);

    /// <summary>The facets this source supports.</summary>
    SourceCapabilities Capabilities { get; }
}

/// <summary>The Catalog facet: the read surface for one source. Single-item reads return null when the source has no
/// data for the URI (the aggregate then tries the next source / fallback). Collection reads return EMPTY when the
/// source has nothing to contribute (so the aggregate's concat-merge yields clean, non-duplicated lists).</summary>
public interface ICatalogSource : ISource
{
    // ── single-item reads (the owning source answers; null = not mine / no data) ──
    Task<Playlist?> GetPlaylistAsync(string uri, CancellationToken ct = default);
    Task<Album?> GetAlbumAsync(string uri, CancellationToken ct = default);
    Task<Artist?> GetArtistAsync(string uri, CancellationToken ct = default);

    /// <summary>Stream a context's tracks in pages (skeleton-then-stream). Yields nothing for a context it doesn't own.</summary>
    IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, CancellationToken ct = default);

    // ── collection contributions (merged by the aggregate; EMPTY when this source has none) ──
    Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetLikedSongsAsync(CancellationToken ct = default);
    Task<SearchResults> SearchAsync(string query, CancellationToken ct = default);
    Task<SearchResults> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default)
        => SearchAsync(query, ct);
    /// <summary>As-you-type search suggestions (the omnibar dropdown). Default empty — only an online source provides them.</summary>
    Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(System.Array.Empty<string>());
    async Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default)
        => new(await SuggestAsync(query, ct).ConfigureAwait(false), System.Array.Empty<SearchSuggestionItem>());
    Task<HomeContribution> GetHomeAsync(CancellationToken ct = default);
    Task<LibraryStats> GetStatsAsync(CancellationToken ct = default);
}
