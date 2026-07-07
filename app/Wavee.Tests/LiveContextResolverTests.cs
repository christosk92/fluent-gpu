using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

public class LiveContextResolverTests
{
    static byte[] ArtistListBody()
    {
        var slc = new Pl.SelectedListContent { Length = 3 };
        var contents = new Pl.ListItems();
        for (int i = 0; i < 3; i++)
            contents.Items.Add(new Pl.Item
            {
                Uri = $"spotify:track:t{i}",
                Attributes = new Pl.ItemAttributes { ItemId = ByteString.CopyFrom((byte)i, (byte)i, (byte)i, (byte)i) },
            });
        slc.Contents = contents;
        return slc.ToByteArray();
    }

    [Fact]
    public async Task ArtistResolve_SecondPlay_OneWireFetch_SkipParamsStillApply()
    {
        int wireCalls = 0;
        var transport = new ArtistCountingTransport(() =>
        {
            Interlocked.Increment(ref wireCalls);
            return new Resp(true, ArtistListBody(), 200);
        });
        var store = new InMemoryStore();
        var metadata = new MetadataService(new NullMetadataSource(), store, () => SessionContext.LoggedOut);
        var resolver = new LiveContextResolver(transport, metadata, store, () => SessionContext.LoggedOut);

        var r1 = await resolver.ResolveAsync(ContextSpec.ForUri("spotify:artist:abc", 0));
        var r2 = await resolver.ResolveAsync(ContextSpec.ForUri("spotify:artist:abc", 2));

        Assert.Equal(1, wireCalls);
        Assert.Equal(3, r1.Tracks.Count);
        Assert.Equal(3, r2.Tracks.Count);
        Assert.Equal(0, r1.StartIndex);
        Assert.Equal(2, r2.StartIndex);
    }

    sealed class NullMetadataSource : IMetadataSource
    {
        public Task FetchAsync(IReadOnlyList<EntityRef> entities, IStore store, CancellationToken ct) => Task.CompletedTask;
    }

    sealed class ArtistCountingTransport(Func<Resp> respond) : ITransport
    {
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            if (route.Contains("popular-release-segments", StringComparison.Ordinal))
                return Task.FromResult(respond());
            return Task.FromResult(new Resp(false, Array.Empty<byte>(), 404));
        }

        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }
}
