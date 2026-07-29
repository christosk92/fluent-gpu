using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

// queryArtistOverview is the ONLY source of track play counts. These cover the two properties that make them stick,
// both of which were missing and produced the same visible symptom — an artist chart that shows "3:11" but no plays,
// permanently, for exactly the artists the sign-in prefetch had touched.
public class ArtistStatsPlayCountTests
{
    const string ArtistUri = "spotify:artist:a1";
    const string TrackUri = "spotify:track:t1";

    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    // The artistUnion shape the mapper reads: discography.topTracks.items[].track.{uri,name,playcount,…} plus the
    // release facets the freshness gate also requires.
    static string Overview(long playcount) => $$"""
    { "data": { "artistUnion": {
        "uri": "{{ArtistUri}}",
        "profile": { "name": "A1" },
        "stats": { "monthlyListeners": 1000, "followers": 10, "worldRank": 0 },
        "discography": {
          "topTracks": { "items": [ { "track": {
              "uri": "{{TrackUri}}", "name": "T1", "playcount": "{{playcount}}",
              "duration": { "totalMilliseconds": 200000 },
              "albumOfTrack": { "uri": "spotify:album:al1" }
          } } ] },
          "latest": { "uri": "spotify:album:al1", "name": "Latest", "type": "SINGLE",
                      "date": { "isoString": "2026-01-01T00:00:00Z" } }
        }
    } } }
    """;

    static (SpotifyArtistStatsService Svc, InMemoryStore Store, FakeExchange Http) Build(long playcount = 20491)
    {
        var store = new InMemoryStore();
        int calls = 0;
        var http = new FakeExchange((req, n) =>
        {
            calls++;
            return new HttpResp(200, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Encoding.UTF8.GetBytes(Overview(playcount)));
        });
        var pf = new PathfinderResource(new PathfinderClient(http), () => Ctx);
        return (new SpotifyArtistStatsService(pf, store), store, http);
    }

    [Fact]
    public async Task Overview_LandsPlayCountsOnTheSharedTrackRow_NotOnlyInTheArtistProjection()
    {
        var (svc, store, _) = Build();

        var artist = await svc.EnsureStatsAsync(ArtistUri);

        Assert.NotNull(artist);
        Assert.Equal(20491, artist!.TopTracks![0].PlayCount);
        // The point of the fix: the count is ALSO on the track plane, which is what every later merge, the cold
        // persist and ArtistSplit.Refatten all read. Without this the projection was the only copy and a thin V4
        // write re-persisted a zero over it.
        Assert.Equal(20491, store.GetTrack(TrackUri)?.PlayCount);
    }

    [Fact]
    public async Task PlayCount_SurvivesALaterThinTrackWrite()
    {
        var (svc, store, _) = Build();
        await svc.EnsureStatsAsync(ArtistUri);

        // What the sign-in discography prefetch does moments later: a TrackV4 row that knows nothing about plays.
        store.UpsertTrack(new Track("t1", TrackUri, "T1", [], new AlbumRef("al1", "spotify:album:al1", ""),
            200000, false, null));

        Assert.Equal(20491, store.GetTrack(TrackUri)?.PlayCount);
    }

    [Fact]
    public async Task WarmArtistWithPlayCounts_IsServedFromTheStore()
    {
        var (svc, store, http) = Build();
        await svc.EnsureStatsAsync(ArtistUri);
        int afterFirst = http.Calls;

        var again = await svc.EnsureStatsAsync(ArtistUri);

        Assert.Equal(afterFirst, http.Calls);                          // no second request — the gate held
        Assert.Equal(20491, again?.TopTracks?[0].PlayCount);
    }
}

// The durable half: the chart's play counts persist WITH the chart, so re-fattening cannot lose them to another
// writer of the shared track row. This is the round-trip the artist page actually performs across a restart.
public class ArtistOverviewPlayCountRoundTripTests
{
    const string TrackUri = "spotify:track:t1";

    static Track Row(long plays) =>
        new("t1", TrackUri, "T1", [], new AlbumRef("al1", "spotify:album:al1", ""), 200000, false, null, PlayCount: plays);

    static Artist WithChart(Track top) =>
        new("a1", "spotify:artist:a1", "A1", null, Array.Empty<Album>(), TopTracks: [top]);

    [Fact]
    public void PlayCounts_SurviveTheProjectRefattenRoundTrip_EvenWhenTheTrackRowWasZeroed()
    {
        var doc = ArtistSplit.Project(WithChart(Row(20491)));

        // What actually happens between the two halves: a TrackV4 prefetch / cluster projection rewrites the SHARED
        // row knowing nothing about plays. Re-fatten must not take its zero.
        var refattened = ArtistSplit.Refatten(ArtistSplit.Core(WithChart(Row(20491))), doc, _ => Row(0));

        Assert.Equal(20491, refattened.TopTracks![0].PlayCount);
        Assert.Equal("T1", refattened.TopTracks[0].Title);          // …while the body still comes from the row
    }

    [Fact]
    public void ADocumentWrittenBeforeCountsWereStored_StillLoads_AndKeepsWhateverTheRowHas()
    {
        // The legacy shape: TopTracks as bare uri strings. It must deserialize (not throw away the whole document),
        // and with no stored count the row's own value stands.
        var legacy = JsonSerializer.Deserialize<ArtistOverviewDoc>(
            $$"""{"TopTracks":["{{TrackUri}}"]}""", EntityJson.Default.Options);

        Assert.NotNull(legacy);
        Assert.Equal(TrackUri, legacy!.TopTracks![0].Uri);
        Assert.Equal(0, legacy.TopTracks[0].Plays);

        var refattened = ArtistSplit.Refatten(ArtistSplit.Core(WithChart(Row(0))), legacy, _ => Row(777));
        Assert.Equal(777, refattened.TopTracks![0].PlayCount);
    }

    [Fact]
    public void ProjectedCounts_RoundTripThroughJson()
    {
        var doc = ArtistSplit.Project(WithChart(Row(20491)));
        var json = JsonSerializer.Serialize(doc, EntityJson.Default.ArtistOverviewDoc);
        var back = JsonSerializer.Deserialize<ArtistOverviewDoc>(json, EntityJson.Default.Options);

        Assert.Equal(20491, back!.TopTracks![0].Plays);
        Assert.Equal(TrackUri, back.TopTracks[0].Uri);
    }
}
