using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Playlists;
using Wavee.Core;

namespace Wavee.Backend.Library;

// ── The catalog↔Store bridge ─────────────────────────────────────────────────────────────────────────────────────────
// A catalog source (the UI binds against ICatalogSource via AggregateCatalog) whose reads project the PERSISTENT Store:
// the unordered library sets (collection_items, via SavedUris) and the ordered playlist membership are JOINED at read to
// the shared entity rows. Heavy Track/Album/Artist/Show records live once in the Store; this never duplicates them — it
// joins by URI. Membership-scoped facts (added_by/added_at) come from the membership row, not the shared entity. The
// source also raises CollectionsChanged when a Store change lands, so the UI cache refreshes off-page without a reskeleton.
public sealed class StoreLibrarySource : ICatalogSource, IPodcastSource, ISourceCollectionEvents, IDisposable
{
    readonly IStore _store;
    readonly SimpleSubject<CollectionKind> _collections = new();
    readonly IDisposable _sub;

    /// <summary>Set by the live bootstrap: fetch a playlist's membership+tracks / an album's tracks on FIRST open (the
    /// rootlist + collection sync stores headers only). Null offline/in tests → reads stay pure store lookups.</summary>
    public Func<string, CancellationToken, Task>? OnDemandFetch { get; set; }

    public StoreLibrarySource(IStore store)
    {
        _store = store;
        _sub = _store.Changes.Subscribe(new ChangeObserver(this));
    }

    public string Id => "spotify-store";
    public bool Owns(string uri) => uri.StartsWith("spotify:", StringComparison.Ordinal);
    public SourceCapabilities Capabilities => SourceCapabilities.Catalog | SourceCapabilities.Podcasts;
    public IObservable<CollectionKind> CollectionsChanged => _collections;

    // ── single-item reads ──
    public async Task<Playlist?> GetPlaylistAsync(string uri, CancellationToken ct = default)
    {
        await EnsureFetchedAsync(uri, ct).ConfigureAwait(false);
        var header = _store.GetPlaylist(uri);
        if (header is null) return null;
        var tracks = JoinMembership(uri);
        Image? cover = header.Cover ?? MosaicCover(TilesFromTracks(tracks));   // cover-less → mosaic/single for the detail hero too
        return header with { Cover = cover, Tracks = tracks, TrackCount = tracks.Count };
    }

    // 4+ distinct album covers → a 2×2 mosaic Image (Url empty + tiles, detected by Surfaces.Artwork/Shelf); 1–3 → the
    // first as a single cover (Url set, renders everywhere); 0 → null (placeholder).
    static Image? MosaicCover(IReadOnlyList<string>? tiles)
        => tiles is not { Count: > 0 } ? null
         : tiles.Count >= 4 ? new Image("", MosaicTiles: tiles)
         : new Image(tiles[0]);

    static IReadOnlyList<string>? TilesFromTracks(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return null;
        var urls = new List<string>(4);
        var seen = new HashSet<string>();
        for (int i = 0; i < tracks.Count && urls.Count < 4; i++)
        {
            if (tracks[i].Image?.Url is not { Length: > 0 } u) continue;
            if (!seen.Add(tracks[i].Album?.Uri ?? u)) continue;
            urls.Add(u);
        }
        return urls.Count > 0 ? urls : null;
    }

    public async Task<Album?> GetAlbumAsync(string uri, CancellationToken ct = default)
    {
        await EnsureFetchedAsync(uri, ct).ConfigureAwait(false);
        return _store.GetAlbum(uri);
    }

    public Task<Artist?> GetArtistAsync(string uri, CancellationToken ct = default) => Task.FromResult(_store.GetArtist(uri));

    // First open of a playlist/album fetches its tracks (the rootlist/collection sync stores headers only). No-op offline.
    async Task EnsureFetchedAsync(string uri, CancellationToken ct)
    {
        var fetch = OnDemandFetch;
        if (fetch is null) return;
        bool need =
            uri.StartsWith("spotify:playlist:", StringComparison.Ordinal) ? _store.Membership(uri).Count == 0 :
            uri.StartsWith("spotify:album:", StringComparison.Ordinal) ? _store.GetAlbum(uri)?.Tracks is null or { Count: 0 } :
            false;
        if (need) { try { await fetch(uri, ct).ConfigureAwait(false); } catch { } }
    }

    public async IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureFetchedAsync(contextUri, ct).ConfigureAwait(false);
        IReadOnlyList<Track> tracks =
            contextUri.StartsWith("spotify:playlist:", StringComparison.Ordinal) ? JoinMembership(contextUri)
            : _store.GetAlbum(contextUri)?.Tracks ?? Array.Empty<Track>();
        if (tracks.Count > 0) yield return new TrackPage(tracks, tracks.Count, tracks.Count);
    }

    // ── collection contributions (empty when this source has nothing for a kind) ──
    public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LibraryItem>>(Array.Empty<LibraryItem>());

    public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        var list = new List<PlaylistSummary>();
        foreach (var e in _store.Rootlist())
            if (e.Kind == 0 && e.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
                list.Add(SummaryOf(e.Uri));
        return Task.FromResult<IReadOnlyList<PlaylistSummary>>(list);
    }

    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default) => Task.FromResult(JoinSet("albums", _store.GetAlbum));
    public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default) => Task.FromResult(JoinSet("artists", _store.GetArtist));
    public Task<IReadOnlyList<Track>> GetLikedSongsAsync(CancellationToken ct = default) => Task.FromResult(JoinSet("liked", _store.GetTrack));

    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return Task.FromResult(SearchResults.Empty);
        // The Store indexes tracks (offline search over the cached library); album/artist full-text search is the online
        // search endpoint's job (a separate source), so this contributes track hits only.
        var tracks = _store.QueryTracks(q);
        return Task.FromResult(new SearchResults(tracks, Array.Empty<Album>(), Array.Empty<Artist>(), Array.Empty<Playlist>()));
    }

    // A home built from the SYNCED library (no Spotify home-feed API needed): a jump-back-in quick grid (Liked +
    // first playlists), then "Your playlists" / "Your albums" / "Your artists" shelves. Empty only on a truly empty store.
    public async Task<HomeContribution> GetHomeAsync(CancellationToken ct = default)
    {
        var playlists = await GetPlaylistsAsync(ct).ConfigureAwait(false);
        var albums = await GetAlbumsAsync(ct).ConfigureAwait(false);
        var artists = await GetArtistsAsync(ct).ConfigureAwait(false);
        int likedCount = _store.SavedUris("liked").Count;

        var groups = new List<HomeGroup>();

        var quick = new List<HomeCard>();
        if (likedCount > 0)
            quick.Add(new HomeCard("spotify:collection:tracks", "Liked Songs", likedCount + " songs", null, HomeCardKind.Liked));
        for (int i = 0; i < playlists.Count && quick.Count < 8; i++)
            quick.Add(new HomeCard(playlists[i].Uri, playlists[i].Name, null, playlists[i].Cover, HomeCardKind.Playlist, playlists[i].MosaicTiles));
        if (quick.Count > 0)
            groups.Add(new HomeGroup(HomeGroupKind.QuickGrid, null, quick));

        if (playlists.Count > 0)
        {
            var cards = new List<HomeCard>(playlists.Count);
            foreach (var p in playlists) cards.Add(new HomeCard(p.Uri, p.Name, p.OwnerName, p.Cover, HomeCardKind.Playlist, p.MosaicTiles));
            groups.Add(new HomeGroup(HomeGroupKind.Shelf, "Your playlists", cards));
        }
        if (albums.Count > 0)
        {
            var cards = new List<HomeCard>(albums.Count);
            foreach (var a in albums) cards.Add(new HomeCard(a.Uri, a.Name, "Album", a.Cover, HomeCardKind.Album));
            groups.Add(new HomeGroup(HomeGroupKind.Shelf, "Your albums", cards));
        }
        if (artists.Count > 0)
        {
            var cards = new List<HomeCard>(artists.Count);
            foreach (var a in artists) cards.Add(new HomeCard(a.Uri, a.Name, "Artist", a.Image, HomeCardKind.Artist));
            groups.Add(new HomeGroup(HomeGroupKind.Shelf, "Your artists", cards));
        }

        return new HomeContribution(groups, Priority: 100);
    }

    public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
        => Task.FromResult(new LibraryStats(
            _store.SavedUris("albums").Count, _store.SavedUris("artists").Count,
            _store.SavedUris("liked").Count, _store.SavedUris("shows").Count));

    // ── IPodcastSource ──
    public Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct = default) => Task.FromResult(JoinSet("shows", _store.GetShow));
    public Task<Show?> GetShowAsync(string uri, CancellationToken ct = default) => Task.FromResult(_store.GetShow(uri));

    // ── joins ──
    IReadOnlyList<T> JoinSet<T>(string setId, Func<string, T?> get) where T : class
    {
        var uris = _store.SavedUris(setId);
        var list = new List<T>(uris.Count);
        for (int i = 0; i < uris.Count; i++) { var v = get(uris[i]); if (v is not null) list.Add(v); }   // inner join: skip not-yet-hydrated
        return list;
    }

    IReadOnlyList<Track> JoinMembership(string playlistUri)
    {
        var members = _store.Membership(playlistUri);
        var list = new List<Track>(members.Count);
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            var t = _store.GetTrack(m.ItemUri);
            if (t is null) continue;   // offline-first inner join: a not-yet-hydrated member has no row until it lands
            DateTimeOffset? at = m.AddedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(m.AddedAt) : null;
            list.Add(t with { AddedAt = at, AddedBy = m.AddedBy });   // stamp membership facts onto the read-model copy
        }
        return list;
    }

    PlaylistSummary SummaryOf(string uri)
    {
        var h = _store.GetPlaylist(uri);
        int count = _store.Membership(uri).Count;
        var tiles = h?.Cover is null ? MosaicTilesOf(uri) : null;   // no custom cover → a 2×2 mosaic (or single) from the tracks
        Image? cover = h?.Cover ?? MosaicCover(tiles);
        return h is null
            ? new PlaylistSummary(uri, uri, "", count, cover, tiles)
            : new PlaylistSummary(uri, h.Name, h.OwnerName, count > 0 ? count : h.TrackCount, cover, tiles);
    }

    // Up to 4 DISTINCT album covers from the playlist's resident tracks — the mosaic source for a cover-less playlist.
    // Derived read-through (NOT memoized on the header), so it recomputes when the tracklist changes.
    internal IReadOnlyList<string>? MosaicTilesOf(string uri)
    {
        var members = _store.Membership(uri);
        if (members.Count == 0) return null;
        var urls = new List<string>(4);
        var seen = new HashSet<string>();
        for (int i = 0; i < members.Count && urls.Count < 4; i++)
        {
            var t = _store.GetTrack(members[i].ItemUri);
            if (t?.Image?.Url is not { Length: > 0 } u) continue;
            if (!seen.Add(t.Album?.Uri ?? u)) continue;   // dedupe by album so a single-album playlist isn't 4× the same art
            urls.Add(u);
        }
        return urls.Count > 0 ? urls : null;
    }

    // ── change fan-out → CollectionsChanged ──
    void OnStoreChange(StoreChange c)
    {
        if (c.IsBulk) { foreach (var k in AllKinds) _collections.OnNext(k); return; }
        if (KindOfUri(c.Uri) is { } kind) _collections.OnNext(kind);
    }

    static readonly CollectionKind[] AllKinds =
        { CollectionKind.Albums, CollectionKind.Artists, CollectionKind.Liked, CollectionKind.Shows, CollectionKind.Playlists };

    static CollectionKind? KindOfUri(string uri) =>
        uri.StartsWith("spotify:album:", StringComparison.Ordinal) ? CollectionKind.Albums :
        uri.StartsWith("spotify:artist:", StringComparison.Ordinal) ? CollectionKind.Artists :
        uri.StartsWith("spotify:track:", StringComparison.Ordinal) ? CollectionKind.Liked :
        uri.StartsWith("spotify:show:", StringComparison.Ordinal) || uri.StartsWith("spotify:episode:", StringComparison.Ordinal) ? CollectionKind.Shows :
        uri.StartsWith("spotify:playlist:", StringComparison.Ordinal) || uri == "rootlist" ? CollectionKind.Playlists :
        null;

    sealed class ChangeObserver(StoreLibrarySource owner) : IObserver<StoreChange>
    {
        public void OnNext(StoreChange c) => owner.OnStoreChange(c);
        public void OnCompleted() { }
        public void OnError(Exception e) { }
    }

    public void Dispose() => _sub.Dispose();
}
