using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using Wavee.Backend;
using Wavee.Backend.Library;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// ── Artist discography pagination (docs/discography-pagination-fix-proposal.md) ─────────────────────────────────────
// Coverage for the data-layer fix that made the artist page shimmer-and-fill to the TRUE facet total instead of the
// ~10-item overview slice. Split across the boundaries it touches:
//   • the JSON ACL (SpotifyExportMapper) — MapArtist now carries discography.<facet>.totalCount, and the new
//     DiscographyPageFromResponse flattens a live paged response;
//   • the source seam (AggregateCatalog routing + ICatalogSource DIM probe semantics + StoreLibrarySource's deliverable
//     total / no-clamp / fast-path / live-failure rules); and
//   • the virtualization primitive (VirtualCollection<T>'s provisional-seed reconciliation — the Phase-2 regression).

public class DiscographyMapperTests
{
    static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    // The captured artist-maroon5.json overview (data.artistUnion.discography.<facet>.{items,totalCount}). Located
    // relative to THIS source file so it resolves regardless of the test host's working directory.
    static string FixturePath([CallerFilePath] string? here = null)
        => Path.Combine(Path.GetDirectoryName(here!)!, "..", "Wavee", "assets", "spotify", "artist-maroon5.json");

    // ── 1. MapArtist carries the per-facet totals (the number the pre-fix mapper dropped) ──
    [Fact]
    public void MapArtist_Fixture_CarriesPerFacetTotals()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        var au = doc.RootElement.GetProperty("data").GetProperty("artistUnion");

        var artist = SpotifyExportMapper.MapArtist(au);

        // The fixture's own discography.<facet>.totalCount values (verified against the JSON): 18 / 46 / 2.
        Assert.Equal(18, artist.AlbumsTotal);
        Assert.Equal(46, artist.SinglesTotal);
        Assert.Equal(2, artist.CompilationsTotal);
        // …while the overview still only carries the first window (≤10 albums, ≤10 singles) — the total ≠ the slice.
        Assert.Equal(18, artist.FacetTotal(DiscographyKind.Albums));
        Assert.Equal(46, artist.FacetTotal(DiscographyKind.Singles));
        Assert.Equal(2, artist.FacetTotal(DiscographyKind.Compilations));
    }

    // ── 2. DiscographyPageFromResponse: flatten one release-per-group + carry totalCount ──
    [Fact]
    public void DiscographyPageFromResponse_FlattensOnePerGroup_AndUsesTotalCount()
    {
        var page = SpotifyExportMapper.DiscographyPageFromResponse(Root("""
        { "data": { "artistUnion": { "discography": { "albums": {
            "totalCount": 18,
            "items": [
              { "releases": { "items": [
                  { "uri": "spotify:album:a1", "name": "First", "type": "ALBUM", "date": { "year": 2020 }, "tracks": { "totalCount": 10 } } ] } },
              { "releases": { "items": [
                  { "uri": "spotify:album:a2", "name": "Second (deluxe group head)", "type": "ALBUM", "date": { "year": 2021 }, "tracks": { "totalCount": 12 } },
                  { "uri": "spotify:album:a2b", "name": "Second (alt edition — must NOT surface)", "type": "ALBUM" } ] } }
            ] } } } } }
        """), DiscographyKind.Albums);

        Assert.Equal(2, page.Items.Count);                       // one row per release-GROUP (not per release)
        Assert.Equal("spotify:album:a1", page.Items[0].Uri);
        Assert.Equal("spotify:album:a2", page.Items[1].Uri);     // the group's FIRST release; the alt edition is dropped
        Assert.Equal(18, page.Total);                            // the facet total flows through, not the 2-item slice count
    }

    [Fact]
    public void DiscographyPageFromResponse_MissingTotalCount_FallsBackToItemCount()
    {
        var page = SpotifyExportMapper.DiscographyPageFromResponse(Root("""
        { "data": { "artistUnion": { "discography": { "singles": {
            "items": [
              { "releases": { "items": [ { "uri": "spotify:album:s1", "name": "S1", "type": "SINGLE" } ] } },
              { "releases": { "items": [ { "uri": "spotify:album:s2", "name": "S2", "type": "SINGLE" } ] } }
            ] } } } } }
        """), DiscographyKind.Singles);

        // No totalCount sibling → Total is clamped up to the delivered item count (never under-report what we handed back).
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(page.Items.Count, page.Total);
    }

    [Fact]
    public void DiscographyPageFromResponse_CarriesPreciseReleaseDateAndTrackCount()
    {
        var page = SpotifyExportMapper.DiscographyPageFromResponse(Root("""
        { "data": { "artistUnion": { "discography": { "albums": {
            "totalCount": 1,
            "items": [ { "releases": { "items": [ {
                "uri": "spotify:album:3QITXlmmt93E176jzVqKUb",
                "name": "Nurture",
                "type": "ALBUM",
                "date": { "day": 23, "month": 4, "precision": "DAY", "year": 2021 },
                "tracks": { "totalCount": 14 }
            } ] } } ]
        } } } } }
        """), DiscographyKind.Albums);

        var album = Assert.Single(page.Items);
        Assert.Equal(14, album.TrackCount);
        Assert.Equal(2021, album.Year);
        Assert.Equal("2021-04-23", album.ReleaseDate);
        Assert.Equal("DAY", album.ReleaseDatePrecision);
    }

    [Fact]
    public void DiscographyPageFromResponse_SmallerTotalCount_ClampsUpToItemCount()
    {
        var page = SpotifyExportMapper.DiscographyPageFromResponse(Root("""
        { "data": { "artistUnion": { "discography": { "albums": {
            "totalCount": 1,
            "items": [
              { "releases": { "items": [ { "uri": "spotify:album:a1", "name": "A1", "type": "ALBUM" } ] } },
              { "releases": { "items": [ { "uri": "spotify:album:a2", "name": "A2", "type": "ALBUM" } ] } }
            ] } } } } }
        """), DiscographyKind.Albums);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2, page.Total);   // max(totalCount=1, items=2) == 2
    }
}

public class DiscographyRoutingTests
{
    // A minimal catalog source: it answers GetArtistAsync (so the ICatalogSource default GetDiscographyAsync can serve
    // the overview slice) and declares ownership of one URI; everything else is out of scope for these tests.
    sealed class FakeArtistSource(string ownedUri, Artist artist) : ICatalogSource
    {
        public string Id => "fake";
        public bool Owns(string uri) => uri == ownedUri;
        public SourceCapabilities Capabilities => SourceCapabilities.Catalog;

        public Task<Artist?> GetArtistAsync(string uri, CancellationToken ct = default)
            => Task.FromResult<Artist?>(uri == ownedUri ? artist : null);

        // Unused by discography routing / DIM probe — deliberately unsupported so a stray call is loud.
        public Task<Playlist?> GetPlaylistAsync(string uri, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Album?> GetAlbumAsync(string uri, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Track>> GetLikedSongsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HomeContribution> GetHomeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    static Album Alb(string id, AlbumKind kind = AlbumKind.Album)
        => new(id, "spotify:album:" + id, "N" + id, null, Array.Empty<ArtistRef>(), 2020, 10, null, kind);

    static Artist ArtistWith(string uri, params Album[] topAlbums)
        => new(uri.Substring(uri.LastIndexOf(':') + 1), uri, "Name", null, topAlbums);

    // ── 3. AggregateCatalog routes discography to the owning source; a non-owned URI → (empty, 0) ──
    [Fact]
    public async Task Aggregate_RoutesToOwningSource_AndEmptyForUnowned()
    {
        const string owned = "spotify:artist:owned";
        var artist = ArtistWith(owned, Alb("a1"), Alb("a2"), Alb("a3"));
        var reg = new SourceRegistry(new ISource[] { new FakeArtistSource(owned, artist) });
        var cat = new AggregateCatalog(reg);

        var mine = await cat.GetDiscographyAsync(owned, DiscographyKind.Albums, 0, 60);
        Assert.Equal(3, mine.Total);                 // served via the owning source's DIM over the 3-album slice
        Assert.Equal(3, mine.Items.Count);

        var theirs = await cat.GetDiscographyAsync("spotify:artist:nobody", DiscographyKind.Albums, 0, 60);
        Assert.Empty(theirs.Items);                  // no owner → clean empty, total 0
        Assert.Equal(0, theirs.Total);
    }

    // ── 4a. Probe (limit <= 0) through the ICatalogSource DIM → (empty, total), no window materialized ──
    [Fact]
    public async Task Dim_Probe_ReturnsEmptyWindow_WithTotal()
    {
        const string uri = "spotify:artist:x";
        var artist = ArtistWith(uri, Alb("a1"), Alb("a2"), Alb("a3"), Alb("a4"), Alb("a5"));
        ICatalogSource src = new FakeArtistSource(uri, artist);   // does NOT override GetDiscographyAsync → the DIM runs

        var probe = await src.GetDiscographyAsync(uri, DiscographyKind.Albums, 0, 0);

        Assert.Empty(probe.Items);   // limit <= 0 → no window materialized (never the whole list as a bogus page)
        Assert.Equal(5, probe.Total);
    }

    // ── 9. The shared kind filter groups Singles with EPs (matches Spotify's `singles` facet) ──
    [Fact]
    public void KindMatches_SinglesFacet_IncludesSingleAndEp_ExcludesAlbum()
    {
        Assert.True(AggregateCatalog.KindMatches(AlbumKind.Single, DiscographyKind.Singles));
        Assert.True(AggregateCatalog.KindMatches(AlbumKind.EP, DiscographyKind.Singles));
        Assert.False(AggregateCatalog.KindMatches(AlbumKind.Album, DiscographyKind.Singles));
        Assert.False(AggregateCatalog.KindMatches(AlbumKind.Compilation, DiscographyKind.Singles));

        Assert.True(AggregateCatalog.KindMatches(AlbumKind.Album, DiscographyKind.Albums));
        Assert.True(AggregateCatalog.KindMatches(AlbumKind.Compilation, DiscographyKind.Compilations));
    }

    [Fact]
    public async Task Dim_SinglesFacet_SurfacesSinglesAndEps_NotAlbums()
    {
        const string uri = "spotify:artist:y";
        var artist = ArtistWith(uri,
            Alb("single", AlbumKind.Single), Alb("ep", AlbumKind.EP),
            Alb("album", AlbumKind.Album), Alb("comp", AlbumKind.Compilation));
        ICatalogSource src = new FakeArtistSource(uri, artist);

        var page = await src.GetDiscographyAsync(uri, DiscographyKind.Singles, 0, 60);

        Assert.Equal(2, page.Total);                                  // the Single + the EP (offline count == Spotify grouping)
        var uris = new HashSet<string>();
        foreach (var a in page.Items) uris.Add(a.Uri);
        Assert.Contains("spotify:album:single", uris);
        Assert.Contains("spotify:album:ep", uris);
        Assert.DoesNotContain("spotify:album:album", uris);
    }
}

public class StoreLibraryDiscographyTests
{
    const string ArtistUri = "spotify:artist:ar";

    static Album Alb(string id, AlbumKind kind = AlbumKind.Album)
        => new(id, "spotify:album:" + id, "N" + id, null, Array.Empty<ArtistRef>(), 2020, 10, null, kind);

    // Seed an artist into an InMemoryStore with a chosen AlbumsTotal + a set of Album-kind TopAlbums (the overview slice).
    static StoreLibrarySource SourceWith(int albumsTotal, params Album[] topAlbums)
    {
        var store = new InMemoryStore();
        store.UpsertArtist(new Artist("ar", ArtistUri, "Ar", null, topAlbums, AlbumsTotal: albumsTotal));
        return new StoreLibrarySource(store);
    }

    // ── 4b. Probe through StoreLibrarySource with no live delegate → total == in-memory filtered count (deliverability) ──
    [Fact]
    public async Task Probe_OfflineNoLiveDelegate_TotalIsInMemoryCount()
    {
        var src = SourceWith(albumsTotal: 18, Alb("a1"), Alb("a2"), Alb("a3"));   // cached facet says 18…
        // LiveDiscography left null → the source can only promise what it holds (3), no permanent trailing shimmer offline.

        var probe = await src.GetDiscographyAsync(ArtistUri, DiscographyKind.Albums, 0, 0);

        Assert.Empty(probe.Items);   // limit <= 0 → total-only probe, no network
        Assert.Equal(3, probe.Total);
    }

    // ── 5. No-clamp rule: a live page's own Total is returned verbatim, never Math.Max(pageTotal, cachedFacet) ──
    [Fact]
    public async Task LivePage_TotalReturnedVerbatim_NeverClampedToCachedFacet()
    {
        var src = SourceWith(albumsTotal: 18, Alb("a1"), Alb("a2"), Alb("a3"));   // cached estimate N = 18
        const int liveTotal = 12;                                                  // the fresh authoritative total M ≠ N (and < N)
        src.LiveDiscography = (uri, kind, offset, limit, ct)
            => Task.FromResult<DiscographyPage?>(new DiscographyPage(new[] { Alb("live1"), Alb("live2") }, liveTotal));

        var page = await src.GetDiscographyAsync(ArtistUri, DiscographyKind.Albums, 0, 60);

        Assert.Equal(liveTotal, page.Total);   // exactly M — NOT Math.Max(12, 18); a shrunk facet must not freeze trailing shimmer
    }

    // ── 6. Fast-path: when the overview slice already covers the facet, no live call is made ──
    [Fact]
    public async Task FastPath_OverviewCoversFacet_NoLiveCall()
    {
        var src = SourceWith(albumsTotal: 3, Alb("a1"), Alb("a2"), Alb("a3"));   // filtered.Count (3) >= total (3)
        int liveCalls = 0;
        src.LiveDiscography = (uri, kind, offset, limit, ct) =>
        {
            liveCalls++;
            return Task.FromResult<DiscographyPage?>(new DiscographyPage(Array.Empty<Album>(), 999));
        };

        var page = await src.GetDiscographyAsync(ArtistUri, DiscographyKind.Albums, 0, 60);

        Assert.Equal(0, liveCalls);        // served from memory, zero network
        Assert.Equal(3, page.Total);
        Assert.Equal(3, page.Items.Count);
    }

    // ── 7. Live failure: LiveDiscography returning null → InvalidOperationException (VC clears its guard, retries later) ──
    [Fact]
    public async Task LiveFailure_NullResponse_Throws()
    {
        var src = SourceWith(albumsTotal: 18, Alb("a1"), Alb("a2"), Alb("a3"));   // filtered (3) < total (18) → the live path
        src.LiveDiscography = (uri, kind, offset, limit, ct) => Task.FromResult<DiscographyPage?>(null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => src.GetDiscographyAsync(ArtistUri, DiscographyKind.Albums, 0, 60));
    }
}

// ── 8. VirtualCollection<T> provisional-seed reconciliation (the Phase-2 regression, both directions) ──
public class VirtualCollectionSeedTests
{
    // A synchronous fetch that reports a fixed total and fills each page with ascending ints (item value == index).
    static VirtualCollection<int>.Fetch AscendingWithTotal(int total) => (offset, count, ct) =>
    {
        var items = new int[count];
        for (int i = 0; i < count; i++) items[i] = offset + i;
        return new ValueTask<PageResult<int>>(new PageResult<int>(total, items));
    };

    static VirtualCollection<int> Vc(int total, int pageSize = 10)
        => new(AscendingWithTotal(total), pageSize: pageSize);   // post == null → inline (synchronous) fill

    // (a) seed-too-HIGH: Seed(N, provisional) then a real page reporting M < N → converge DOWN to M.
    [Fact]
    public void ProvisionalSeed_HigherThanLivePage_ConvergesDown()
    {
        var vc = Vc(total: 30);
        vc.Seed(100, ReadOnlySpan<int>.Empty, provisional: true);
        Assert.Equal(100, vc.CountOr0);   // renders as N up front (shimmer-to-N)

        vc.EnsureRange(0, 9);             // page 0 lands with the authoritative total 30

        Assert.Equal(30, vc.CountOr0);    // corrected DOWN — no permanent trailing shimmer
        for (int i = 0; i < 10; i++) { Assert.True(vc.IsLoaded(i)); Assert.Equal(i, vc[i]); }   // no null slots in the loaded range
        Assert.False(vc.IsLoaded(35));    // an index beyond M is never exposed as a ghost slot
        Assert.Equal(default, vc[35]);
    }

    // (b) seed-too-LOW: Seed(N, provisional) then a real page reporting M > N → grow to M, [N, M) reachable.
    [Fact]
    public void ProvisionalSeed_LowerThanLivePage_GrowsAndKeepsUpperItemsReachable()
    {
        var vc = Vc(total: 50);
        vc.Seed(10, ReadOnlySpan<int>.Empty, provisional: true);
        Assert.Equal(10, vc.CountOr0);

        vc.EnsureRange(0, 9);             // page 0 → learns the true total 50 (grows _chunks, un-truncates)
        Assert.Equal(50, vc.CountOr0);

        vc.EnsureRange(40, 49);           // a page well above the seeded N must be loadable (the seed didn't truncate it)
        Assert.True(vc.IsLoaded(45));
        Assert.Equal(45, vc[45]);
    }

    // (c) Seed AFTER a real page already set the count → no-op on the count.
    [Fact]
    public void Seed_AfterRealPage_DoesNotOverrideCount()
    {
        var vc = Vc(total: 40);
        vc.EnsureRange(0, 9);             // a real (non-provisional) page sets count = 40
        Assert.Equal(40, vc.CountOr0);

        vc.Seed(999, ReadOnlySpan<int>.Empty, provisional: true);   // too late — a real total already spoke

        Assert.Equal(40, vc.CountOr0);
    }

    // (d) non-provisional Seed keeps existing semantics: the seeded count sticks even when a live page disagrees.
    [Fact]
    public void NonProvisionalSeed_CountSticks_EvenIfLivePageDiffers()
    {
        var vc = Vc(total: 99);
        vc.Seed(20, ReadOnlySpan<int>.Empty, provisional: false);   // firm seed
        Assert.Equal(20, vc.CountOr0);

        vc.EnsureRange(0, 9);             // page 0 reports 99 — but a firm (non-provisional) count is NOT corrected
        Assert.Equal(20, vc.CountOr0);
        Assert.True(vc.IsLoaded(5));
        Assert.Equal(5, vc[5]);
    }

    // (e) seed corrected DOWN so far the arriving page falls out of range (live facet actually EMPTY): no chunk is stored
    // (cap == 0), so the correction itself MUST bump Version — a grid watching it re-windows away from the seeded shimmer
    // slots instead of showing them forever.
    [Fact]
    public void ProvisionalSeed_CorrectedToEmpty_StillBumpsVersion()
    {
        var vc = Vc(total: 0);
        vc.Seed(46, ReadOnlySpan<int>.Empty, provisional: true);
        Assert.Equal(46, vc.CountOr0);    // shimmer-to-N up front
        int before = vc.Version.Value;

        vc.EnsureRange(0, 9);             // page 0 reports total 0 → count corrects to 0, page 0 now out of range

        Assert.Equal(0, vc.CountOr0);     // corrected to empty
        Assert.True(vc.Version.Value > before);   // and the grid was told — no permanent shimmer
    }
}
