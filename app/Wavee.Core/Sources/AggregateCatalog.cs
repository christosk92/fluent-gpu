using System.Linq;
using System.Runtime.CompilerServices;

namespace Wavee.Core;

/// <summary>The source-agnostic façade the UI binds against (docs/architecture.md §4.3). Implements the UI-facing
/// <see cref="IMusicLibrary"/> by federating over a <see cref="SourceRegistry"/>: single-item reads route to the first
/// owning source; collection reads MERGE (concat) across catalog sources — each source contributes only what it has,
/// so the union is clean. Provider-mappings / dedup / fallback chains are the documented extension point (trivial with
/// one real source today). This is the layer Connect/playback federation (FederatedPlayback/Remote) will sit beside.</summary>
public sealed class AggregateCatalog : IMusicLibrary, ICollectionEvents
{
    readonly SourceRegistry _reg;
    readonly SimpleSubject<CollectionKind> _collections = new();

    public AggregateCatalog(SourceRegistry registry)
    {
        _reg = registry;
        // Fan-in: any source that emits its own collection deltas forwards into the ONE aggregate stream the cache
        // subscribes to (off-page library freshness, docs/architecture.md §6). No source raises it today → neutral seam.
        foreach (var s in registry.All.OfType<ISourceCollectionEvents>())
            s.CollectionsChanged.Subscribe(new ActionObserver<CollectionKind>(k => _collections.OnNext(k)));
    }

    /// <summary>The aggregated library-delta stream — the cache refreshes the named collection in place, even off-page.</summary>
    public IObservable<CollectionKind> CollectionsChanged => _collections;

    // ── single-item reads: first owning source that returns non-null wins; else a minimal empty shape ──
    public async Task<Playlist> GetPlaylistAsync(string id, CancellationToken ct = default)
    {
        foreach (var s in _reg.CatalogSources)
            if (s.Owns(id) && await s.GetPlaylistAsync(id, ct).ConfigureAwait(false) is { } p) return p;
        return new Playlist(id, id, "", null, "", null, 0, System.Array.Empty<Track>());
    }

    public async Task<Album> GetAlbumAsync(string id, CancellationToken ct = default)
    {
        foreach (var s in _reg.CatalogSources)
            if (s.Owns(id) && await s.GetAlbumAsync(id, ct).ConfigureAwait(false) is { } a) return a;
        return new Album(id, id, "", null, System.Array.Empty<ArtistRef>(), 0, 0, System.Array.Empty<Track>());
    }

    public async Task<Artist> GetArtistAsync(string id, CancellationToken ct = default)
    {
        foreach (var s in _reg.CatalogSources)
            if (s.Owns(id) && await s.GetArtistAsync(id, ct).ConfigureAwait(false) is { } a) return a;
        return new Artist(id, id, "", null);
    }

    public IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, CancellationToken ct = default)
        => _reg.OwnerOf(contextUri)?.StreamTracksAsync(contextUri, ct) ?? EmptyPages(ct);

    // Paged discography window + facet total — served from the (live-fetched) artist overview's releases, split by kind.
    // No synthetic fallback: an artist with no releases in a facet returns an EMPTY page (the UI shows an empty state).
    public async Task<DiscographyPage> GetDiscographyAsync(string artistUri, DiscographyKind kind, int offset, int limit, CancellationToken ct = default)
    {
        var artist = await GetArtistAsync(artistUri, ct).ConfigureAwait(false);
        var all = artist?.TopAlbums ?? System.Array.Empty<Album>();
        var filtered = new List<Album>();
        foreach (var a in all) if (KindMatches(a.Kind, kind)) filtered.Add(a);
        var items = new List<Album>();
        for (int i = offset; i < filtered.Count && items.Count < limit; i++) items.Add(filtered[i]);
        return new DiscographyPage(items, filtered.Count);
    }

    static bool KindMatches(AlbumKind ak, DiscographyKind dk) => dk switch
    {
        DiscographyKind.Singles => ak is AlbumKind.Single or AlbumKind.EP,
        DiscographyKind.Compilations => ak == AlbumKind.Compilation,
        _ => ak == AlbumKind.Album,
    };

    // ── merged collections (each source returns EMPTY where it has no data → clean union, no dups) ──
    public async Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default)
    {
        var r = new List<LibraryItem>();
        foreach (var s in _reg.CatalogSources) r.AddRange(await s.GetLibraryAsync(ct).ConfigureAwait(false));
        return r;
    }

    public async Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        var r = new List<PlaylistSummary>();
        foreach (var s in _reg.CatalogSources) r.AddRange(await s.GetPlaylistsAsync(ct).ConfigureAwait(false));
        return r;
    }

    public async Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
    {
        var r = new List<Album>();
        foreach (var s in _reg.CatalogSources) r.AddRange(await s.GetAlbumsAsync(ct).ConfigureAwait(false));
        return r;
    }

    public async Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default)
    {
        var r = new List<Artist>();
        foreach (var s in _reg.CatalogSources) r.AddRange(await s.GetArtistsAsync(ct).ConfigureAwait(false));
        return r;
    }

    public async Task<IReadOnlyList<Track>> GetLikedSongsAsync(CancellationToken ct = default)
    {
        var r = new List<Track>();
        foreach (var s in _reg.CatalogSources) r.AddRange(await s.GetLikedSongsAsync(ct).ConfigureAwait(false));
        return r;
    }

    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default)
        => SearchAsync(query, SearchFacet.All, 0, 30, ct);

    public async Task<SearchResults> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default)
    {
        var t = new List<Track>(); var al = new List<Album>(); var ar = new List<Artist>(); var pl = new List<Playlist>();
        IReadOnlyList<SearchTopHit>? topHits = null;
        int tt = 0, at = 0, art = 0, pt = 0;
        foreach (var s in _reg.CatalogSources)
        {
            var x = await s.SearchAsync(query, facet, offset, limit, ct).ConfigureAwait(false);
            t.AddRange(x.Tracks); al.AddRange(x.Albums); ar.AddRange(x.Artists); pl.AddRange(x.Playlists);
            topHits ??= x.TopHits;
            tt += x.TracksTotal >= 0 ? x.TracksTotal : x.Tracks.Count;
            at += x.AlbumsTotal >= 0 ? x.AlbumsTotal : x.Albums.Count;
            art += x.ArtistsTotal >= 0 ? x.ArtistsTotal : x.Artists.Count;
            pt += x.PlaylistsTotal >= 0 ? x.PlaylistsTotal : x.Playlists.Count;
        }
        return new SearchResults(t, al, ar, pl, topHits, tt, at, art, pt);
    }

    public async Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default)
    {
        var x = await SuggestRichAsync(query, ct).ConfigureAwait(false);
        return x.Queries;
    }

    public async Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default)
    {
        // First source that returns suggestions wins (the online source); offline sources default to empty.
        foreach (var s in _reg.CatalogSources)
        {
            var x = await s.SuggestRichAsync(query, ct).ConfigureAwait(false);
            if (x.Queries.Count + x.Items.Count > 0) return x;
        }
        return SearchSuggestions.Empty;
    }

    public async Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
    {
        int al = 0, ar = 0, lk = 0, pod = 0;
        foreach (var s in _reg.CatalogSources)
        {
            var st = await s.GetStatsAsync(ct).ConfigureAwait(false);
            al += st.Albums; ar += st.Artists; lk += st.LikedSongs; pod += st.Podcasts;
        }
        return new LibraryStats(al, ar, lk, pod);
    }

    public async Task<HomeFeed> GetHomeAsync(CancellationToken ct = default)
    {
        var contribs = new List<HomeContribution>();
        foreach (var s in _reg.CatalogSources)
        {
            var c = await s.GetHomeAsync(ct).ConfigureAwait(false);
            if (c.Groups.Count > 0) contribs.Add(c);
        }
        contribs.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        var groups = contribs.SelectMany(c => c.Groups).ToList();
        return new HomeFeed(Greeting(), groups);
    }

    // ── podcasts: federated to the Podcasts-capable sources (route single-show reads to the owner; merge the grid) ──
    public async Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct = default)
    {
        var r = new List<Show>();
        foreach (var s in _reg.OfCapability(SourceCapabilities.Podcasts).OfType<IPodcastSource>())
            r.AddRange(await s.GetShowsAsync(ct).ConfigureAwait(false));
        return r;
    }

    public async Task<Show?> GetShowAsync(string uri, CancellationToken ct = default)
    {
        foreach (var s in _reg.OfCapability(SourceCapabilities.Podcasts).OfType<IPodcastSource>())
            if (s.Owns(uri) && await s.GetShowAsync(uri, ct).ConfigureAwait(false) is { } show) return show;
        return null;
    }

    /// <summary>A live, time-of-day greeting (the export's is a snapshot; compute it fresh so it's always right).</summary>
    static string Greeting()
    {
        int h = System.DateTime.Now.Hour;
        return h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
    }

    static async IAsyncEnumerable<TrackPage> EmptyPages([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
