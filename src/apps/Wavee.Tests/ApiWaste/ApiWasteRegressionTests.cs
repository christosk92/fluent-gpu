using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Backend.Lyrics;
using Wavee.Backend.Lyrics.Sources;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;
using Pb = Wavee.Protocol.Metadata;

namespace Wavee.Tests.ApiWaste;

public class PathfinderResourceTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    [Fact]
    public async Task SameOperationAndVariables_CoalescesParallelCalls()
    {
        var http = new FakeExchange((_, _) =>
            new HttpResp(200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("""{"data":{"ok":true}}""")));
        var resource = new PathfinderResource(new PathfinderClient(http), () => Ctx);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => resource.QueryAsync("home", "hash",
                w => w.WriteString("uri", "spotify:album:A"),
                PathfinderClient.Platform.WebPlayer,
                TestContext.Current.CancellationToken))
            .ToArray();

        var docs = await Task.WhenAll(tasks);

        Assert.All(docs, d =>
        {
            Assert.NotNull(d);
            Assert.True(d!.RootElement.GetProperty("data").GetProperty("ok").GetBoolean());
            d.Dispose();
        });
        Assert.Equal(1, http.Calls);
    }
}

public class ExtensionEtagCacheTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);
    static SessionContext DutchCtx => Ctx with { Locale = "nl" };

    [Fact]
    public async Task SecondStaleFetch_SendsEtag_AndKeepsPayloadOn304()
    {
        const string uri = "spotify:album:A";
        string? secondEtag = null;
        var http = new FakeExchange((req, call) =>
        {
            var body = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
            if (call == 2) secondEtag = Assert.Single(Assert.Single(body.EntityRequest).Query).Etag;
            return new HttpResp(200, new Dictionary<string, string>(),
                call == 1
                    ? ExtensionResponse(uri, Xm.ExtensionKind.RecommendedPlaylists, 200, "v1", ByteString.CopyFromUtf8("payload"))
                    : ExtensionResponse(uri, Xm.ExtensionKind.RecommendedPlaylists, 304, "v1", null));
        });
        var source = new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx);
        var cache = new ExtensionEtagCache(source, () => Ctx);

        var first = await cache.GetPayloadAsync(uri, Xm.ExtensionKind.RecommendedPlaylists, TestContext.Current.CancellationToken);
        cache.MarkStale(uri, Xm.ExtensionKind.RecommendedPlaylists);
        var second = await cache.GetPayloadAsync(uri, Xm.ExtensionKind.RecommendedPlaylists, TestContext.Current.CancellationToken);

        Assert.Equal("payload", first!.ToStringUtf8());
        Assert.Equal("payload", second!.ToStringUtf8());
        Assert.Equal("v1", secondEtag);
        Assert.Equal(2, http.Calls);
    }

    [Fact]
    public async Task Restart_RestoresExactLocalePayloadAndEtag_ForConditionalRevalidation()
    {
        const string uri = "spotify:album:persistent";
        string path = Path.Combine(Path.GetTempPath(), "wavee-extension-test-" + Guid.NewGuid().ToString("N") + ".db");
        static void DeleteDb(string p)
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(p + suffix); } catch { }
        }

        try
        {
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "nl-NL"))
            {
                var firstHttp = new FakeExchange((req, _) =>
                {
                    Assert.Equal("nl", req.Headers["Accept-Language"]);
                    return new HttpResp(200, new Dictionary<string, string>(),
                        ExtensionResponse(uri, Xm.ExtensionKind.RecommendedPlaylists, 200, "persistent-v1", ByteString.CopyFromUtf8("disk payload")));
                });
                var source = new ExtendedMetadataSource(firstHttp, () => "https://spclient.test", () => DutchCtx);
                var cache = new ExtensionEtagCache(source, () => DutchCtx, persistent: cold);
                Assert.Equal("disk payload", (await cache.GetPayloadAsync(uri, Xm.ExtensionKind.RecommendedPlaylists,
                    TestContext.Current.CancellationToken))!.ToStringUtf8());
                cold.Flush();
            }

            string? sentEtag = null;
            using (var reopened = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, "nl"))
            {
                var secondHttp = new FakeExchange((req, _) =>
                {
                    var body = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
                    sentEtag = Assert.Single(Assert.Single(body.EntityRequest).Query).Etag;
                    return new HttpResp(200, new Dictionary<string, string>(),
                        ExtensionResponse(uri, Xm.ExtensionKind.RecommendedPlaylists, 304, "persistent-v1", null));
                });
                var source = new ExtendedMetadataSource(secondHttp, () => "https://spclient.test", () => DutchCtx);
                var cache = new ExtensionEtagCache(source, () => DutchCtx, persistent: reopened);
                cache.MarkStale(uri, Xm.ExtensionKind.RecommendedPlaylists);

                var restored = await cache.GetPayloadAsync(uri, Xm.ExtensionKind.RecommendedPlaylists,
                    TestContext.Current.CancellationToken);

                Assert.Equal("disk payload", restored!.ToStringUtf8());
                Assert.Equal("persistent-v1", sentEtag);
                Assert.Equal(1, secondHttp.Calls);
            }
        }
        finally { DeleteDb(path); }
    }

    internal static byte[] ExtensionResponse(string uri, Xm.ExtensionKind kind, int status, string? etag, ByteString? payload)
    {
        var hdr = new Xm.EntityExtensionDataHeader { StatusCode = status, OfflineTtlInSeconds = 60 };
        if (etag is not null) hdr.Etag = etag;
        var data = new Xm.EntityExtensionData { EntityUri = uri, Header = hdr };
        if (payload is not null) data.ExtensionData = new Any { Value = payload };
        var array = new Xm.EntityExtensionDataArray { ExtensionKind = kind };
        array.ExtensionData.Add(data);
        var response = new Xm.BatchedExtensionResponse();
        response.ExtendedMetadata.Add(array);
        return response.ToByteArray();
    }
}

public class BulkMetadataEtagTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    [Fact]
    public async Task SyncAll_UsesConditionalExtensionCache_ForCatalogHydration()
    {
        const string uri = "spotify:track:x";
        string? secondEtag = null;
        var http = new FakeExchange((req, call) =>
        {
            var body = Xm.BatchedEntityRequest.Parser.ParseFrom(HttpCompression.Gunzip(req.Body!));
            if (call == 2) secondEtag = Assert.Single(Assert.Single(body.EntityRequest).Query).Etag;
            return new HttpResp(200, new Dictionary<string, string>(),
                call == 1
                    ? ExtensionEtagCacheTests.ExtensionResponse(uri, Xm.ExtensionKind.TrackV4, 200, "track-etag", TrackPayload())
                    : ExtensionEtagCacheTests.ExtensionResponse(uri, Xm.ExtensionKind.TrackV4, 304, "track-etag", null));
        });
        var store = new InMemoryStore();
        var source = new ExtendedMetadataSource(http, () => "https://spclient.test", () => Ctx);
        var cache = new ExtensionEtagCache(source, () => Ctx);
        var metadata = new MetadataService(source, store, () => Ctx, ttl: TimeSpan.Zero, extensionCache: cache);

        await metadata.SyncAllAsync([uri], TestContext.Current.CancellationToken);
        cache.MarkStale(uri, Xm.ExtensionKind.TrackV4);
        await metadata.SyncAllAsync([uri], TestContext.Current.CancellationToken);

        Assert.Equal("track-etag", secondEtag);
        Assert.Equal(2, http.Calls);
        Assert.Equal("Waste Track", Assert.Single(store.QueryTracks()).Title);
    }

    static ByteString TrackPayload()
    {
        var gid = ByteString.CopyFrom(Enumerable.Repeat((byte)0x11, 16).ToArray());
        var track = new Pb.Track { Gid = gid, Name = "Waste Track", Duration = 123000 };
        track.Artist.Add(new Pb.Artist { Gid = ByteString.CopyFrom(Enumerable.Repeat((byte)0x22, 16).ToArray()), Name = "Artist" });
        track.Album = new Pb.Album { Gid = ByteString.CopyFrom(Enumerable.Repeat((byte)0x33, 16).ToArray()), Name = "Album" };
        return track.ToByteString();
    }
}

public class LyricsNegativeCacheTests
{
    [Fact]
    public async Task AmllSource_SkipsHttp_WhenSpotifyLyricsKnown()
    {
        var http = new CountingLyricHttp();
        var source = new AmllTtmlDbSource(http);
        var req = new LyricsRequest("t1", "spotify:track:t1", "Song", ["Artist"], "Album", 1000, HasSpotifyLyrics: true);

        var result = await source.FetchAsync(req, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(0, http.Calls);
    }

    sealed class CountingLyricHttp : ILyricHttpWithStatus
    {
        public int Calls;

        public Task<string?> GetStringAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<string?>(null);
        }

        public Task<LyricHttpResult> GetAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LyricHttpResult(404, null));
        }
    }
}
