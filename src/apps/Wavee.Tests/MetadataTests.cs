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
using Xunit;

namespace Wavee.Tests;

public class EntityRefTests
{
    [Theory]
    [InlineData("spotify:track:abc", EntityKind.Track)]
    [InlineData("spotify:album:xyz", EntityKind.Album)]
    [InlineData("spotify:artist:a1", EntityKind.Artist)]
    [InlineData("spotify:playlist:p1", EntityKind.Playlist)]
    [InlineData("spotify:episode:e1", EntityKind.Episode)]
    [InlineData("spotify:show:s1", EntityKind.Show)]
    [InlineData("spotify:wibble:x", EntityKind.Unknown)]
    [InlineData("not-a-uri", EntityKind.Unknown)]
    public void Parse_RecognizesKind(string uri, EntityKind kind)
    {
        var e = EntityRef.Parse(uri);
        Assert.Equal(kind, e.Kind);
        Assert.Equal(uri, e.Uri);
    }

    [Fact]
    public void Parse_IsAllocationFree()   // the repo's zero-alloc discipline — String.Split would allocate ~4 objects/call
    {
        var uris = Enumerable.Range(0, 1000).Select(i => $"spotify:track:t{i}").ToArray();
        foreach (var u in uris) _ = EntityRef.Parse(u);   // warm up JIT

        long before = GC.GetAllocatedBytesForCurrentThread();
        var acc = EntityKind.Unknown;
        foreach (var u in uris) acc |= EntityRef.Parse(u).Kind;
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(EntityKind.Track, acc);   // keep the result live
        Assert.True(delta < 200, $"EntityRef.Parse allocated {delta} bytes for 1000 parses (expected ~0)");
    }
}

public class MetadataChunkingTests
{
    static IReadOnlyList<EntityRef> Tracks(int n) =>
        Enumerable.Range(0, n).Select(i => EntityRef.Parse($"spotify:track:t{i}")).ToArray();

    [Fact]
    public void Ranges_Packs10kTracks_IntoOneRequest()
    {
        var ranges = MetadataChunking.Ranges(Tracks(10_000)).ToList();
        Assert.Single(ranges);                     // ~30 bytes x 10k = ~300 KB < 4 MB → ONE request, not 20
        Assert.Equal((0, 10_000), ranges[0]);
    }

    [Fact]
    public void Ranges_RespectsBodyBudget_AndCoversAllContiguously()
    {
        var entities = Tracks(1000);
        var ranges = MetadataChunking.Ranges(entities, maxBodyBytes: 2000, headerBytes: 0).ToList();

        Assert.True(ranges.Count > 1);             // small budget → multiple chunks
        foreach (var (start, count) in ranges)
        {
            int size = 0;
            for (int i = start; i < start + count; i++) size += MetadataChunking.EstimateBytes(entities[i]);
            Assert.True(count == 1 || size <= 2000, $"chunk {start}+{count} estimated {size} > 2000");
        }
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(1000, ranges.Sum(r => r.Count));
        for (int i = 1; i < ranges.Count; i++)
            Assert.Equal(ranges[i - 1].Start + ranges[i - 1].Count, ranges[i].Start);   // contiguous
    }

    [Fact]
    public void Ranges_OversizedEntity_GetsItsOwnChunk()
    {
        var entities = new[]
        {
            EntityRef.Parse("spotify:track:a"),
            EntityRef.Parse("spotify:track:" + new string('x', 5000)),
            EntityRef.Parse("spotify:track:b"),
        };
        Assert.Equal(3, MetadataChunking.Ranges(entities, maxBodyBytes: 100, headerBytes: 0).Count());
    }
}

public class HttpCompressionTests
{
    [Fact]
    public void Gzip_RoundTrips_AndShrinksRepetitiveBody()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("spotify:track:abcdef ", 1000)));
        var z = HttpCompression.Gzip(data);
        Assert.True(z.Length < data.Length);                 // a batched request body compresses well
        Assert.Equal(data, HttpCompression.Gunzip(z));
    }
}

public class MetadataServiceTests
{
    static SessionContext Ctx => new("me", "US", "premium", "en", Tier.Premium, false);

    sealed class FakeBatchSource : IMetadataSource
    {
        public int Calls { get; private set; }
        public int TotalEntities { get; private set; }
        readonly Action<EntityRef, IStore>? _project;
        public FakeBatchSource(Action<EntityRef, IStore>? project = null) => _project = project;
        public Task FetchAsync(IReadOnlyList<EntityRef> entities, IStore store, CancellationToken ct)
        {
            Calls++;
            TotalEntities += entities.Count;
            if (_project is not null) foreach (var e in entities) _project(e, store);
            return Task.CompletedTask;
        }
    }

    static Track Trk(EntityRef e) => new(e.Uri, e.Uri, "T", [], new AlbumRef("", "", ""), 0, false, null);

    [Fact]
    public async Task SyncAll_HandsTheWholeBatchToTheSource()
    {
        var src = new FakeBatchSource();
        var svc = new MetadataService(src, new InMemoryStore(), () => Ctx);
        var uris = Enumerable.Range(0, 10_000).Select(i => $"spotify:track:t{i}").ToList();

        await svc.SyncAllAsync(uris, TestContext.Current.CancellationToken);

        Assert.Equal(1, src.Calls);              // one hand-off; the source packs by body size (see MetadataChunking)
        Assert.Equal(10_000, src.TotalEntities);
    }

    [Fact]
    public async Task SyncAll_ProjectsEveryEntity()
    {
        var store = new InMemoryStore();
        var svc = new MetadataService(new FakeBatchSource((e, st) => st.UpsertTrack(Trk(e))), store, () => Ctx);
        await svc.SyncAllAsync(["spotify:track:a", "spotify:track:b"], TestContext.Current.CancellationToken);
        Assert.NotNull(store.GetTrack("spotify:track:a"));
        Assert.NotNull(store.GetTrack("spotify:track:b"));
    }

    [Fact]
    public async Task SyncAll_PartialCache_OnlyFetchesTheMisses()
    {
        var store = new InMemoryStore();
        var src = new FakeBatchSource((e, st) => st.UpsertTrack(Trk(e)));
        var svc = new MetadataService(src, store, () => Ctx);
        var all = Enumerable.Range(0, 10_000).Select(i => $"spotify:track:t{i}").ToList();

        await svc.SyncAllAsync(all.Take(5000).ToList(), TestContext.Current.CancellationToken);   // warm 5k into cache
        Assert.Equal(5000, src.TotalEntities);

        await svc.SyncAllAsync(all, TestContext.Current.CancellationToken);   // first 5k fresh → only the new 5k fetch
        Assert.Equal(10_000, src.TotalEntities);   // cumulative — NOT 15000, which a refetch-all would give
    }

    [Fact]
    public async Task SyncAll_FullyCached_FetchesNothing()
    {
        var store = new InMemoryStore();
        var src = new FakeBatchSource((e, st) => st.UpsertTrack(Trk(e)));
        var svc = new MetadataService(src, store, () => Ctx);
        await svc.SyncAllAsync(["spotify:track:a"], TestContext.Current.CancellationToken);
        Assert.Equal(1, src.Calls);
        await svc.SyncAllAsync(["spotify:track:a"], TestContext.Current.CancellationToken);   // already fresh
        Assert.Equal(1, src.Calls);   // zero new requests
    }

    [Fact]
    public async Task Use_FetchesOnce_ThenServesFresh()
    {
        var store = new InMemoryStore();
        var src = new FakeBatchSource((e, st) => st.UpsertTrack(Trk(e)));
        var svc = new MetadataService(src, store, () => Ctx);

        await svc.EnsureAsync("spotify:track:t1");
        Assert.Equal(1, src.Calls);
        Assert.NotNull(store.GetTrack("spotify:track:t1"));
        Assert.True(svc.Use("spotify:track:t1").IsReady);
        Assert.Equal(1, src.Calls);   // fresh (1h TTL) → no refetch
    }
}

public class StoreBulkTests
{
    sealed class CountObserver : IObserver<StoreChange>
    {
        public int Count;
        public void OnNext(StoreChange v) => Count++;
        public void OnError(Exception e) { }
        public void OnCompleted() { }
    }

    [Fact]
    public void BeginBulk_CoalescesManyWrites_IntoOneSignal()
    {
        var store = new InMemoryStore();
        var obs = new CountObserver();
        using var sub = store.Changes.Subscribe(obs);
        obs.Count = 0;   // ignore the BehaviorSubject replay on subscribe
        using (store.BeginBulk())
            for (int i = 0; i < 1000; i++)
                store.UpsertTrack(new Track("t" + i, "spotify:track:t" + i, "T", [], new AlbumRef("", "", ""), 0, false, null));
        Assert.Equal(1, obs.Count);                                 // ONE signal, not 1000
        Assert.Equal(1000, store.QueryTracks(limit: 5000).Count);   // all the data is present
    }
}
