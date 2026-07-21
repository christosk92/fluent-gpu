using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Localization;
using Wavee.Backend.Playlists;
using Wavee.Core;

namespace Wavee.Backend.Library;

// Disambiguate from the UI type Wavee.DiscographyPage (a Component) that is otherwise in scope under the Wavee.* namespace.
// (Declared inside the namespace so it wins over the enclosing-namespace member.)
using DiscographyPage = Wavee.Core.DiscographyPage;

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
    readonly object _profileGate = new();
    readonly Dictionary<string, HashSet<string>> _profilePlaylistDeps = new(StringComparer.Ordinal);
    readonly HashSet<string> _profilePlaylistCollectionDeps = new(StringComparer.Ordinal);
    IUserProfileService? _userProfiles;
    IDisposable? _profileSub;

    /// <summary>Set by the live bootstrap: fetch a playlist's membership+tracks / an album's tracks on FIRST open (the
    /// rootlist + collection sync stores headers only). Null offline/in tests → reads stay pure store lookups.</summary>
    public Func<string, CancellationToken, Task>? OnDemandFetch { get; set; }

    /// <summary>Best-effort cover-palette hydration for playlist reads. Unlike <see cref="OnDemandFetch"/>, this runs
    /// for warm/resident playlists too; the live implementation is fire-and-forget safe and publishes through Store.</summary>
    public Func<string, CancellationToken, Task>? EnsurePlaylistPalette { get; set; }

    /// <summary>Set by the go-live block: the single library-sync loop. When present, playlist opens route through it (SWR —
    /// blocking first fetch / background revalidate); null offline/in tests → the OnDemandFetch path (album/artist unchanged).</summary>
    public Wavee.Backend.Sync.LibrarySync? Sync { get; set; }

    /// <summary>Set by the live bootstrap: the editorial/personalized Pathfinder home groups, inserted after the pinned
    /// quick-pick matrix and above the store-derived library shelves. Null offline → only the library-derived home.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<HomeGroup>>>? LiveHomeFetch { get; set; }

    /// <summary>Set by the live bootstrap: full-catalog online search (Pathfinder). Primary when present; the offline
    /// store track search is the fallback. Returns null on failure → caller degrades to offline.</summary>
    public Func<string, SearchFacet, int, int, CancellationToken, Task<SearchResults?>>? LiveSearch { get; set; }

    /// <summary>Set by the live bootstrap: as-you-type search suggestions (Pathfinder searchSuggestions). Empty offline.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<string>>>? LiveSuggest { get; set; }
    public Func<string, CancellationToken, Task<SearchSuggestions>>? LiveSuggestRich { get; set; }

    /// <summary>Optional live user profile overlay for playlist owners / added-by contributors. Null offline/in tests.</summary>
    public IUserProfileService? UserProfiles
    {
        get => _userProfiles;
        set
        {
            if (ReferenceEquals(_userProfiles, value)) return;
            _profileSub?.Dispose();
            _userProfiles = value;
            lock (_profileGate)
            {
                _profilePlaylistDeps.Clear();
                _profilePlaylistCollectionDeps.Clear();
            }
            _profileSub = value?.Changed.Subscribe(Wavee.Backend.Observers.From<string>(OnProfileChanged));
        }
    }

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
        // Palette hydration is independent of membership freshness. The old placement inside OnDemandFetch never ran
        // when LibrarySync served a warm playlist, which is why no fetchExtractedColors request appeared at all.
        // This enrichment outlives the foreground read. A route/read token is commonly cancelled as soon as the
        // ready model is published; forwarding it could abort Pathfinder before fetchExtractedColors reaches the wire.
        if (EnsurePlaylistPalette is { } ensurePalette) _ = ensurePalette(uri, CancellationToken.None);
        var header = _store.GetPlaylist(uri);
        if (header is null) return null;
        var members = _store.Membership(uri);
        PrefetchPlaylistUsers(uri, header, members);
        var owner = OverlayOwner(uri, header, collectionDependency: false);
        var tracks = JoinMembership(uri, members);
        Image? cover = header.Cover ?? MosaicCover(TilesFromTracks(tracks));   // cover-less → mosaic/single for the detail hero too
        return header with
        {
            OwnerName = owner?.Name ?? header.Owner?.Name ?? header.OwnerName,
            Owner = owner ?? header.Owner,
            Collaborators = BuildCollaborators(header, owner, members),
            Cover = cover,
            Tracks = tracks,
            TrackCount = tracks.Count,
        };
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

    public async Task<Artist?> GetArtistAsync(string uri, CancellationToken ct = default)
    {
        await EnsureFetchedAsync(uri, ct).ConfigureAwait(false);
        return _store.GetArtist(uri);
    }

    // Discography paging — now a pure in-memory slice. TopAlbums holds the WHOLE discography (ArtistV4 groups → stubs,
    // upgraded to resident cards by ArtistDiscography.Assemble), so paging is client-side and needs no network beyond the
    // V4 ensure GetArtistAsync already triggers. The facet total is simply the filtered count (what we actually hold).
    public async Task<DiscographyPage> GetDiscographyAsync(string uri, DiscographyKind kind, int offset, int limit, CancellationToken ct = default)
    {
        var artist = await GetArtistAsync(uri, ct).ConfigureAwait(false);   // triggers the V4 ensure when cold
        var all = artist?.TopAlbums ?? Array.Empty<Album>();
        var filtered = new List<Album>();
        foreach (var a in all) if (AggregateCatalog.KindMatches(a.Kind, kind)) filtered.Add(a);   // shared kind filter (Singles ⇒ Single/EP)
        if (limit <= 0) return new DiscographyPage(Array.Empty<Album>(), filtered.Count);   // total-only probe
        var window = new List<Album>();
        for (int i = offset; i < filtered.Count && window.Count < limit; i++) window.Add(filtered[i]);
        return new DiscographyPage(window, filtered.Count);
    }

    // First open fetches the detail envelope over the V4 pipeline (ArtistDiscography for artists, EnsureAlbumAsync for
    // albums). The gates below decide when a read is cold enough to fetch. Album rows expose play counts, which are not
    // present in AlbumV4/TrackV4, so an album read is complete only after the Pathfinder Full envelope has landed.
    async Task EnsureFetchedAsync(string uri, CancellationToken ct)
    {
        // Playlist on-open routing through the sync loop (SWR, §2.6): a MISSING membership baseline → blocking first
        // fetch; a KNOWN baseline (including a valid empty, newly-created playlist) → fire-and-forget revalidate and
        // serve cache now. Count==0 cannot distinguish those states and used to let an early fetch overwrite optimistic
        // title/description/cover edits on new empty playlists.
        if (Sync is { } sync && uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
        {
            if (!_store.HasMembership(uri)) await sync.OpenPlaylistAsync(uri, ct).ConfigureAwait(false);
            else sync.Enqueue(new Wavee.Backend.Sync.SyncCommand(Wavee.Backend.Sync.SyncKind.OpenPlaylist, uri));
            return;
        }

        var fetch = OnDemandFetch;
        if (fetch is null) return;
        bool need = false;
        if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
            need = !_store.HasMembership(uri);
        else if (uri.StartsWith("spotify:album:", StringComparison.Ordinal))
        {
            // V4 supplies the fast tracklist, but not play counts. Require Full hydration so EnsureAlbumAsync also lands
            // the getAlbum envelope before the detail model is published. A cached Full album remains a zero-network read.
            // A tracklist with UNNAMED rows is still cold: AlbumV4 disc tracks are frequently gid-only on the wire (the
            // prefetch's Wave 2 lands them like that before Wave 3 enriches), so "has tracks" alone is not hydrated —
            // without this clause the open path never runs the TrackV4 enrichment and the page renders empty titles.
            need = !IsAlbumComplete(_store.GetAlbum(uri));
        }
        else if (uri.StartsWith("spotify:artist:", StringComparison.Ordinal))
        {
            // V4 ensure is SWR-cheap (etag/resident-skip), so no TTL clause — re-run whenever the discography is missing or
            // still a bare stub. TopAlbums[0].Name empty ⇒ the ArtistV4 stubs haven't been upgraded to resident cards yet.
            var a = _store.GetArtist(uri);
            need = a is null || a.TopAlbums is null or { Count: 0 } || a.TopAlbums[0].Name.Length == 0;
        }
        if (need) { try { await fetch(uri, ct).ConfigureAwait(false); } catch { } }
    }

    // Shared with the live EnsureAlbumAsync callback. `Full` describes the detail envelope that was mapped; it does not
    // prove that a usable track list landed. A transient/partial Pathfinder response can otherwise seal an empty album as
    // Full forever: StoreLibrarySource asks for repair, while the callback returns before doing the repair.
    internal static bool IsAlbumComplete(Album? album)
        => album is { Hydration: AlbumHydrationLevel.Full, Tracks: { Count: > 0 } tracks }
           && !HasUnnamedTrack(tracks)
           // The Full flag alone is not proof: a partial/transient getAlbum response can carry named tracks but NEITHER
           // play counts NOR inline colors, and marking that Full sealed the album "Full forever" (0 plays + no wash +
           // stub artist — every later open early-returned here). A COMPLETE envelope always delivers inline colors and/or
           // play counts, so require at least one; a payload-less "Full" is treated as cold and re-fetched. (Play counts
           // are legitimately 0 on brand-new releases, hence OR — colors alone still count as complete, and vice versa.)
           && (album.Palette is not null || HasAnyPlayCount(tracks));

    static bool HasUnnamedTrack(IReadOnlyList<Track> tracks)
    {
        for (int i = 0; i < tracks.Count; i++) if (tracks[i].Title.Length == 0) return true;
        return false;
    }

    static bool HasAnyPlayCount(IReadOnlyList<Track> tracks)
    {
        for (int i = 0; i < tracks.Count; i++) if (tracks[i].PlayCount > 0) return true;
        return false;
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

    // Liked Songs is an ADD-ORDERED collection (newest first — the Spotify default) with the add date a first-class,
    // sortable column: join the timestamped set and stamp AddedAt onto the read-model copy (same shape JoinMembership
    // gives playlist rows), so the detail surface derives the Date-added column + default sort from the data itself.
    public Task<IReadOnlyList<Track>> GetLikedSongsAsync(CancellationToken ct = default)
    {
        var items = SortedByAddedDesc(_store.SavedItems("liked"));
        var list = new List<Track>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var t = _store.GetTrack(items[i].Uri);
            if (t is null) continue;   // offline-first inner join: a not-yet-hydrated member has no row until it lands
            list.Add(items[i].AddedAtMs > 0 ? t with { AddedAt = DateTimeOffset.FromUnixTimeMilliseconds(items[i].AddedAtMs) } : t);
        }
        return Task.FromResult<IReadOnlyList<Track>>(list);
    }

    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default)
        => SearchAsync(query, SearchFacet.All, 0, 30, ct);

    public async Task<SearchResults> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return SearchResults.Empty;
        // Online catalog search (Pathfinder) is primary — the WHOLE Spotify catalog (tracks/albums/artists/playlists).
        // The Store's offline track index (the cached library) is the fallback when the live session isn't up.
        if (LiveSearch is { } live)
        {
            var online = await live(q, facet, offset, limit, ct).ConfigureAwait(false);
            return online ?? throw new InvalidOperationException("Spotify search returned no response.");
        }
        var tracks = facet is SearchFacet.All or SearchFacet.Tracks ? _store.QueryTracks(q) : Array.Empty<Track>();
        return new SearchResults(tracks, Array.Empty<Album>(), Array.Empty<Artist>(), Array.Empty<Playlist>(),
            TracksTotal: tracks.Count);
    }

    // Offline, cache-only full-text library search (the library page's left search box). Scans the RESIDENT store only —
    // never the network — so it stays instant; ranked+grouped by LibrarySearchIndex. Off the UI thread (Store reads are
    // lock-safe); an empty store / empty query → Empty.
    public Task<LibrarySearchResults> SearchLibraryAsync(string query, LibrarySearchScope scope, CancellationToken ct = default)
        => query.Trim().Length == 0
            ? Task.FromResult(LibrarySearchResults.Empty)
            : Task.Run(() => LibrarySearchIndex.Run(_store, scope, query), ct);

    public async Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default)
    {
        var s = await SuggestRichAsync(query, ct).ConfigureAwait(false);
        return s.Queries;
    }

    public async Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return SearchSuggestions.Empty;
        if (LiveSuggestRich is { } rich)
        {
            try { return await rich(q, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return SearchSuggestions.Empty; }
        }
        if (LiveSuggest is not { } live) return SearchSuggestions.Empty;
        try
        {
            var queries = await live(q, ct).ConfigureAwait(false);
            return new SearchSuggestions(queries, Array.Empty<SearchSuggestionItem>());
        }
        catch (OperationCanceledException) { throw; }
        catch { return SearchSuggestions.Empty; }
    }

    // A home built from the SYNCED library (no Spotify home-feed API needed): a pinned jump-back-in quick grid (Liked +
    // first playlists), then live editorial modules and "Your playlists" / "Your albums" / "Your artists" shelves.
    // Empty only on a truly empty store.
    public async Task<HomeContribution> GetHomeAsync(CancellationToken ct = default)
    {
        var playlists = await GetPlaylistsAsync(ct).ConfigureAwait(false);
        var albums = await GetAlbumsAsync(ct).ConfigureAwait(false);
        var artists = await GetArtistsAsync(ct).ConfigureAwait(false);
        int likedCount = _store.SavedUris("liked").Count;

        var groups = new List<HomeGroup>();

        var quick = new List<HomeCard>();
        if (likedCount > 0)
            quick.Add(new HomeCard("spotify:collection:tracks", Loc.Get(Strings.Detail.LikedSongs),
                Strings.Detail.SongCount(likedCount), null, HomeCardKind.Liked));
        for (int i = 0; i < playlists.Count && quick.Count < 9; i++)
            quick.Add(new HomeCard(playlists[i].Uri, playlists[i].Name, null, playlists[i].Cover, HomeCardKind.Playlist, playlists[i].MosaicTiles));
        if (quick.Count > 0)
            groups.Add(new HomeGroup(HomeGroupKind.QuickGrid, null, quick));

        // The personal quick matrix is the stable first home module. Pathfinder editorial/personalized groups follow
        // it, still above the larger library-derived shelves.
        if (LiveHomeFetch is { } liveFetch)
        {
            try { groups.AddRange(await liveFetch(ct).ConfigureAwait(false)); } catch { /* editorial home is best-effort */ }
        }

        if (playlists.Count > 0)
        {
            var cards = new List<HomeCard>(playlists.Count);
            foreach (var p in playlists) cards.Add(new HomeCard(p.Uri, p.Name, p.OwnerName, p.Cover, HomeCardKind.Playlist, p.MosaicTiles));
            groups.Add(new HomeGroup(HomeGroupKind.Shelf, Loc.Get(Strings.Home.YourPlaylists), cards));
        }
        if (albums.Count > 0)
        {
            var cards = new List<HomeCard>(albums.Count);
            foreach (var a in albums) cards.Add(new HomeCard(a.Uri, a.Name, "Album", a.Cover, HomeCardKind.Album));
            groups.Add(new HomeGroup(HomeGroupKind.Shelf, Loc.Get(Strings.Home.YourAlbums), cards));
        }
        if (artists.Count > 0)
        {
            var cards = new List<HomeCard>(artists.Count);
            foreach (var a in artists) cards.Add(new HomeCard(a.Uri, a.Name, "Artist", a.Image, HomeCardKind.Artist));
            groups.Add(new HomeGroup(HomeGroupKind.Shelf, Loc.Get(Strings.Home.YourArtists), cards));
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
    // Every library set reads in ADD order, newest first (the Spotify collection default); unknown timestamps (0) sink
    // to the end.
    IReadOnlyList<T> JoinSet<T>(string setId, Func<string, T?> get) where T : class
    {
        var items = SortedByAddedDesc(_store.SavedItems(setId));
        var list = new List<T>(items.Count);
        for (int i = 0; i < items.Count; i++) { var v = get(items[i].Uri); if (v is not null) list.Add(v); }   // inner join: skip not-yet-hydrated
        return list;
    }

    static List<SavedItem> SortedByAddedDesc(IReadOnlyList<SavedItem> items)
    {
        var list = new List<SavedItem>(items);
        list.Sort((a, b) => b.AddedAtMs.CompareTo(a.AddedAtMs));
        return list;
    }

    IReadOnlyList<Track> JoinMembership(string playlistUri) => JoinMembership(playlistUri, _store.Membership(playlistUri));

    IReadOnlyList<Track> JoinMembership(string playlistUri, IReadOnlyList<PlaylistMember> members)
    {
        var list = new List<Track>(members.Count);
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            var t = _store.GetTrack(m.ItemUri);
            if (t is null) continue;   // offline-first inner join: a not-yet-hydrated member has no row until it lands
            DateTimeOffset? at = m.AddedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(m.AddedAt) : null;
            list.Add(t with { AddedAt = at, AddedBy = m.AddedBy, ContextUid = m.ItemId });   // stamp membership facts (+ per-row uid) onto the read-model copy
        }
        return list;
    }

    PlaylistSummary SummaryOf(string uri)
    {
        var h = _store.GetPlaylist(uri);
        int count = _store.Membership(uri).Count;
        var tiles = h?.Cover is null ? MosaicTilesOf(uri) : null;   // no custom cover → a 2×2 mosaic (or single) from the tracks
        Image? cover = h?.Cover ?? MosaicCover(tiles);
        // Editability flags (feed the "Copy to playlist" picker): a playlist is editable when the user can edit items or
        // owns it; the h-is-null branch defaults both to false (PlaylistSummary defaults).
        bool canEdit = h is not null && (h.Capabilities.CanEditItems || h.Capabilities.IsOwner);
        bool isOwner = h is not null && h.Capabilities.IsOwner;
        return h is null
            ? new PlaylistSummary(uri, uri, "", count, cover, tiles)
            : new PlaylistSummary(uri, h.Name, OwnerDisplayName(uri, h, collectionDependency: true), count > 0 ? count : h.TrackCount, cover, tiles, CanEdit: canEdit, IsOwner: isOwner);
    }

    // Up to 4 DISTINCT album covers from the playlist's resident tracks — the mosaic source for a cover-less playlist.
    // Derived read-through (NOT memoized on the header), so it recomputes when the tracklist changes.
    string OwnerDisplayName(string playlistUri, Playlist header, bool collectionDependency)
    {
        var owner = OverlayOwner(playlistUri, header, collectionDependency);
        return owner?.Name ?? header.Owner?.Name ?? header.OwnerName;
    }

    Owner? OverlayOwner(string playlistUri, Playlist header, bool collectionDependency)
    {
        var raw = RawOwnerId(header);
        if (raw.Length == 0) return header.Owner;
        RegisterProfileDependency(raw, playlistUri, collectionDependency);
        _userProfiles?.Prefetch(new[] { raw });
        return _userProfiles?.Get(raw) ?? header.Owner;
    }

    static string RawOwnerId(Playlist header)
        => header.Owner?.Id is { Length: > 0 } id ? id : header.OwnerName;

    void PrefetchPlaylistUsers(string playlistUri, Playlist header, IReadOnlyList<PlaylistMember> members)
    {
        if (_userProfiles is null) return;
        var ids = new List<string>(1 + members.Count);
        var owner = RawOwnerId(header);
        if (owner.Length > 0)
        {
            ids.Add(owner);
            RegisterProfileDependency(owner, playlistUri, collectionDependency: false);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < members.Count; i++)
        {
            var id = members[i].AddedBy;
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
            ids.Add(id);
            RegisterProfileDependency(id, playlistUri, collectionDependency: false);
        }
        if (ids.Count > 0) _userProfiles.Prefetch(ids);
    }

    IReadOnlyList<Owner>? BuildCollaborators(Playlist header, Owner? resolvedOwner, IReadOnlyList<PlaylistMember> members)
    {
        var result = new List<Owner>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rawOwner = RawOwnerId(header);
        var owner = resolvedOwner ?? header.Owner;
        if (owner is not null) Add(owner);
        else if (rawOwner.Length > 0) Add(new Owner(ProfileId(rawOwner), header.OwnerName, null));

        for (int i = 0; i < members.Count; i++)
        {
            var raw = members[i].AddedBy;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var profile = _userProfiles?.Get(raw);
            Add(new Owner(profile?.Id ?? ProfileId(raw), profile?.Name ?? raw, profile?.Avatar));
        }

        return result.Count > 0 ? result : header.Collaborators;

        void Add(Owner value)
        {
            var key = UserProfileIds.Normalize(value.Id) ?? value.Id;
            if (!seen.Add(key)) return;
            result.Add(value);
        }
    }

    static string ProfileId(string raw)
        => UserProfileIds.BareId(UserProfileIds.Normalize(raw) ?? raw);

    void RegisterProfileDependency(string rawUserId, string playlistUri, bool collectionDependency)
    {
        var canonical = UserProfileIds.Normalize(rawUserId);
        if (canonical is null) return;
        lock (_profileGate)
        {
            if (!_profilePlaylistDeps.TryGetValue(canonical, out var playlists))
                _profilePlaylistDeps[canonical] = playlists = new HashSet<string>(StringComparer.Ordinal);
            playlists.Add(playlistUri);
            if (collectionDependency) _profilePlaylistCollectionDeps.Add(canonical);
        }
    }

    void OnProfileChanged(string userUri)
    {
        List<string>? playlists = null;
        bool collection;
        lock (_profileGate)
        {
            if (_profilePlaylistDeps.TryGetValue(userUri, out var deps)) playlists = new List<string>(deps);
            collection = _profilePlaylistCollectionDeps.Contains(userUri);
        }
        if (playlists is not null)
            for (int i = 0; i < playlists.Count; i++) _store.Bump(playlists[i]);
        if (collection) _collections.OnNext(CollectionKind.Playlists);
    }

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
        if (c.Kind is { } explicitKind) { _collections.OnNext(explicitKind); return; }
        if (KindOfUri(c.Uri) is { } kind) _collections.OnNext(kind);
    }

    static readonly CollectionKind[] AllKinds =
        { CollectionKind.Albums, CollectionKind.Artists, CollectionKind.Liked, CollectionKind.Shows, CollectionKind.Playlists };

    static CollectionKind? KindOfUri(string uri) =>
        uri.StartsWith("spotify:album:", StringComparison.Ordinal) ? CollectionKind.Albums :
        uri.StartsWith("spotify:artist:", StringComparison.Ordinal) ? CollectionKind.Artists :
        uri.StartsWith("spotify:show:", StringComparison.Ordinal) || uri.StartsWith("spotify:episode:", StringComparison.Ordinal) ? CollectionKind.Shows :
        uri.StartsWith("spotify:playlist:", StringComparison.Ordinal) || uri == "rootlist" ? CollectionKind.Playlists :
        null;

    sealed class ChangeObserver(StoreLibrarySource owner) : IObserver<StoreChange>
    {
        public void OnNext(StoreChange c) => owner.OnStoreChange(c);
        public void OnCompleted() { }
        public void OnError(Exception e) { }
    }

    public void Dispose()
    {
        _profileSub?.Dispose();
        _sub.Dispose();
    }
}
