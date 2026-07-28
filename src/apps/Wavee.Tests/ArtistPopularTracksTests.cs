using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

// The artist chart's step two (SpClient artist-top-tracks-extensions → TrackV4 → merge). The pure fold is pinned first
// (it owns the play-count contract), then the live service through the IHttpExchange + IMetadataSource seams — no
// network, no clock.
public class ArtistPopularMergeTests
{
    static Track T(string id, long plays = 0) =>
        new(id, "spotify:track:" + id, "T" + id, [], new AlbumRef("", "", ""), 1000, false, null, PlayCount: plays);

    [Fact]
    public void Merge_KeepsTheSeedHeadAndItsPlayCounts()
    {
        // The extension endpoint carries uris only — every play count in the chart comes from the overview seed. If the
        // extension copy of a shared uri won, the top rows would silently lose their "N plays" subline.
        var seed = new[] { T("a", 500), T("b", 400) };
        var ext = new[] { T("b"), T("a"), T("c") };

        var merged = ArtistPopularTracks.Merge(seed, ext);

        Assert.Equal(["spotify:track:a", "spotify:track:b", "spotify:track:c"], merged.Select(t => t.Uri));
        Assert.Equal(500, merged[0].PlayCount);
        Assert.Equal(400, merged[1].PlayCount);
        Assert.Equal(0, merged[2].PlayCount);   // no invented play count for tracks 11+
    }

    [Fact]
    public void Merge_AppendsExtensionOnlyTracksInExtensionOrder()
    {
        var merged = ArtistPopularTracks.Merge([T("a")], [T("z"), T("y")]);
        Assert.Equal(["spotify:track:a", "spotify:track:z", "spotify:track:y"], merged.Select(t => t.Uri));
    }

    [Fact]
    public void Merge_EmptyExtension_ReturnsTheSeedUntouched()
    {
        var seed = new[] { T("a", 9), T("b") };
        Assert.Same(seed, ArtistPopularTracks.Merge(seed, Array.Empty<Track>()));
        Assert.Same(seed, ArtistPopularTracks.Merge(seed, null));
    }

    [Fact]
    public void Merge_DropsDuplicateAndUriLessEntries()
    {
        var blank = new Track("x", "", "X", [], new AlbumRef("", "", ""), 0, false, null);
        var merged = ArtistPopularTracks.Merge([T("a")], [T("a"), blank, T("a"), T("b")]);
        Assert.Equal(["spotify:track:a", "spotify:track:b"], merged.Select(t => t.Uri));
    }

    [Fact]
    public void Merge_CapsAtTheExtendedCeiling()
    {
        var ext = Enumerable.Range(0, 200).Select(i => T("e" + i)).ToArray();
        var merged = ArtistPopularTracks.Merge([T("a")], ext);
        Assert.Equal(ArtistPopularTracks.ExtendedCap, merged.Count);
        Assert.Equal("spotify:track:a", merged[0].Uri);   // the seed head survives the cap
    }

    [Fact]
    public async Task NullService_HandsTheSeedBack()
    {
        var seed = new[] { T("a") };
        Assert.Same(seed, await new NullArtistPopularTracksService().EnsureExtendedAsync("spotify:artist:x", seed));
    }
}

public class SpotifyArtistPopularTracksServiceTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);
    const string ArtistUri = "spotify:artist:a1";

    static Track T(string id, long plays = 0) =>
        new(id, "spotify:track:" + id, "T" + id, [], new AlbumRef("", "", ""), 1000, false, null, PlayCount: plays);

    static HttpResp Json(string body)
        => new(200, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Encoding.UTF8.GetBytes(body));

    static string TracksJson(params string[] ids)
        => "{\"tracks\":[" + string.Join(",", ids.Select(i => $"{{\"uri\":\"spotify:track:{i}\"}}")) + "]}";

    /// <summary>Projects every requested uri into the store as a real Track — what TrackV4 hydration does.</summary>
    sealed class ProjectingSource : IMetadataSource
    {
        public int Calls;
        public Task FetchAsync(IReadOnlyList<EntityRef> entities, IStore store, CancellationToken ct)
        {
            Calls++;
            foreach (var e in entities)
                store.UpsertTrack(new Track(e.Uri, e.Uri, "hydrated", [], new AlbumRef("", "", ""), 1000, false, null));
            return Task.CompletedTask;
        }
    }

    static (SpotifyArtistPopularTracksService Svc, InMemoryStore Store, FakeExchange Http) Build(
        Func<HttpReq, int, HttpResp> responder, IMetadataSource? source = null)
    {
        var store = new InMemoryStore();
        var http = new FakeExchange(responder);
        var metadata = new MetadataService(source ?? new ProjectingSource(), store, () => Ctx);
        return (new SpotifyArtistPopularTracksService(http, () => "https://spclient", metadata, store, default), store, http);
    }

    static SpotifyArtistPopularTracksService Build(IHttpExchange http, out InMemoryStore store)
    {
        store = new InMemoryStore();
        var metadata = new MetadataService(new ProjectingSource(), store, () => Ctx);
        return new SpotifyArtistPopularTracksService(http, () => "https://spclient", metadata, store, default);
    }

    static void SeedArtist(InMemoryStore store, IReadOnlyList<Track> top, DateTimeOffset? fetchedAt = null)
    {
        foreach (var t in top) store.UpsertTrack(t);
        store.UpsertArtist(new Artist("a1", ArtistUri, "A", null)
        {
            TopTracks = top,
            FetchedAt = fetchedAt ?? DateTimeOffset.UtcNow,
        });
    }

    [Fact]
    public async Task Ensure_FetchesEnrichesAndMergesIntoTheStore()
    {
        var (svc, store, http) = Build((_, _) => Json(TracksJson("a", "b", "c", "d")));
        var seed = new[] { T("a", 900), T("b", 800) };
        SeedArtist(store, seed);

        var merged = await svc.EnsureExtendedAsync(ArtistUri, seed, TestContext.Current.CancellationToken);

        Assert.Equal(4, merged.Count);
        Assert.Equal(900, merged[0].PlayCount);                 // seed head kept, play counts intact
        Assert.Equal("hydrated", merged[3].Title);              // tail came from the metadata projection
        Assert.Equal(4, store.GetArtist(ArtistUri)!.TopTracks!.Count);   // and it landed on the store artist
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task Ensure_UsesTheRequestedUrl()
    {
        string? seen = null;
        var (svc, store, _) = Build((r, _) => { seen = r.Url; return Json(TracksJson("a")); });
        SeedArtist(store, [T("a")]);

        await svc.EnsureExtendedAsync(ArtistUri, [T("a")], TestContext.Current.CancellationToken);

        Assert.Equal("https://spclient/artistplaycontext/v1/page/spotify/artist-top-tracks-extensions/"
            + Uri.EscapeDataString(ArtistUri), seen);
    }

    [Fact]
    public async Task Ensure_AlreadyExtendedAndFresh_SkipsTheNetwork()
    {
        var (svc, store, http) = Build((_, _) => Json(TracksJson("z")));
        var extended = Enumerable.Range(0, ArtistPopularTracks.OverviewSeedCap + 1).Select(i => T("t" + i)).ToArray();
        SeedArtist(store, extended);

        var result = await svc.EnsureExtendedAsync(ArtistUri, extended, TestContext.Current.CancellationToken);

        Assert.Equal(extended.Length, result.Count);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task Ensure_ExtendedButStale_RefetchesOnTheStatsStamp()
    {
        // The stats overview write rewrites TopTracks back to the ~10 seed on its own 12h tick, so a stale stamp must
        // re-open this path rather than pinning yesterday's list forever.
        var (svc, store, http) = Build((_, _) => Json(TracksJson("z")));
        var extended = Enumerable.Range(0, ArtistPopularTracks.OverviewSeedCap + 1).Select(i => T("t" + i)).ToArray();
        SeedArtist(store, extended, DateTimeOffset.UtcNow - TimeSpan.FromHours(13));

        await svc.EnsureExtendedAsync(ArtistUri, extended, TestContext.Current.CancellationToken);

        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task Ensure_NonSpotifyArtist_NeverCallsOut()
    {
        var (svc, _, http) = Build((_, _) => Json(TracksJson("a")));
        var seed = new[] { T("a") };

        Assert.Same(seed, await svc.EnsureExtendedAsync("local:artist:mine", seed, TestContext.Current.CancellationToken));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task Ensure_HttpFailure_KeepsTheSeedAndTheStoredList()
    {
        var (svc, store, _) = Build((_, _) => new HttpResp(503, new Dictionary<string, string>(), Array.Empty<byte>()));
        var seed = new[] { T("a", 5), T("b") };
        SeedArtist(store, seed);

        var result = await svc.EnsureExtendedAsync(ArtistUri, seed, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal(5, result[0].PlayCount);
        Assert.Equal(2, store.GetArtist(ArtistUri)!.TopTracks!.Count);   // a failed step two never blanks the chart
    }

    [Fact]
    public async Task Ensure_MalformedBody_DegradesToTheSeed()
    {
        var (svc, store, _) = Build((_, _) => Json("{\"nope\":1}"));
        var seed = new[] { T("a") };
        SeedArtist(store, seed);

        Assert.Single(await svc.EnsureExtendedAsync(ArtistUri, seed, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ensure_UnresolvableUris_AreSkippedNotPlaceheld()
    {
        // A source that hydrates nothing: the extension uris must drop out rather than becoming uri-titled rows.
        var (svc, store, _) = Build((_, _) => Json(TracksJson("a", "ghost")), new NoopSource());
        var seed = new[] { T("a", 3) };
        SeedArtist(store, seed);

        var result = await svc.EnsureExtendedAsync(ArtistUri, seed, TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("spotify:track:a", result[0].Uri);
    }

    [Fact]
    public async Task Ensure_ConcurrentCalls_ShareOneRequest()
    {
        var http = new GatedExchange(Json(TracksJson("a", "b")));
        var svc = Build(http, out var store);
        var seed = new[] { T("a") };
        SeedArtist(store, seed);

        var first = svc.EnsureExtendedAsync(ArtistUri, seed);
        var second = svc.EnsureExtendedAsync(ArtistUri, seed);
        http.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, http.Calls);
        Assert.All(results, r => Assert.Equal(2, r.Count));
    }

    [Fact]
    public async Task Ensure_CancelledCaller_Throws_WithoutKillingTheSharedLoad()
    {
        // Cancellation cancels the caller's AWAIT, not the shared in-flight load — a second page joined to the same
        // artist must still get its list, and the (uri-keyed) store write stays correct whenever it lands.
        var http = new GatedExchange(Json(TracksJson("a", "b")));
        var svc = Build(http, out var store);
        SeedArtist(store, [T("a")]);

        using var cts = new CancellationTokenSource();
        var cancelled = svc.EnsureExtendedAsync(ArtistUri, [T("a")], cts.Token);
        var joined = svc.EnsureExtendedAsync(ArtistUri, [T("a")]);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        http.Release();
        Assert.Equal(2, (await joined).Count);
    }

    /// <summary>An exchange whose response completes only when the test says so — so an in-flight load is observable
    /// without blocking a thread.</summary>
    sealed class GatedExchange : IHttpExchange
    {
        readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly HttpResp _resp;
        public int Calls;
        public GatedExchange(HttpResp resp) => _resp = resp;
        public void Release() => _gate.TrySetResult();
        public async Task<HttpResp> SendAsync(HttpReq req, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await _gate.Task.ConfigureAwait(false);
            return _resp;
        }
    }

    sealed class NoopSource : IMetadataSource
    {
        public Task FetchAsync(IReadOnlyList<EntityRef> entities, IStore store, CancellationToken ct) => Task.CompletedTask;
    }
}
