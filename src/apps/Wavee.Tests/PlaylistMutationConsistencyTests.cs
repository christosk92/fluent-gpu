using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class PlaylistMutationConsistencyTests
{
    const string PlaylistUri = "spotify:playlist:p";
    static readonly SessionContext Ctx = new("alice", "NL", "premium", "en", Tier.Premium, false);

    sealed class FailingTransport : ITransport
    {
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
            => Task.FromResult(new Resp(false, Array.Empty<byte>(), 409));
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    static (InMemoryStore Store, MutationEngine Mutations, PlaylistMutationSource Source) Create(ITransport transport)
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("p", PlaylistUri, "New playlist", null, "alice", null, 0));
        store.SetMembership(PlaylistUri, Array.Empty<PlaylistMember>(), new byte[] { 1 });
        var mutations = new MutationEngine(store,
            new IMutationStrategy[] { new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com") });
        var http = new FakeExchange((_, _) => new HttpResp(500, new Dictionary<string, string>(), Array.Empty<byte>()));
        var source = new PlaylistMutationSource(mutations, transport, http, () => Ctx,
            () => "https://spclient.wg.spotify.com", new UserPlaylistSource(), store);
        return (store, mutations, source);
    }

    [Fact]
    public async Task AddRecommendedTrack_HydratesEntityBeforeOptimisticMembership()
    {
        var transport = new StubTransport();
        var (store, mutations, source) = Create(transport);
        var track = new Track("t", "spotify:track:t", "Recommended", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", ""), 123_000, false, null);

        await source.AddTracksAsync(PlaylistUri, new[] { track }, TestContext.Current.CancellationToken);

        Assert.Same(track, store.GetTrack(track.Uri));
        Assert.Equal(track.Uri, Assert.Single(store.Membership(PlaylistUri)).ItemUri);
        Assert.Equal(0, mutations.Pending);
        Assert.Equal("POST", transport.LastRequestMethod);
        Assert.Equal("/playlist/v2/playlist/p/changes", transport.LastRequestRoute);
    }

    [Fact]
    public async Task ScheduledDrain_IsAwaitedBeforeMutationReportsSuccess()
    {
        var transport = new StubTransport();
        var (store, mutations, source) = Create(transport);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ScheduleDrain = ct => release.Task.WaitAsync(ct);

        var save = source.UpdateDetailsAsync(PlaylistUri, "Renamed", null, null, TestContext.Current.CancellationToken);

        Assert.False(save.IsCompleted);
        Assert.Equal("Renamed", store.GetPlaylist(PlaylistUri)!.Name); // optimistic header is already live
        await mutations.Drain(transport, Ctx, TestContext.Current.CancellationToken);
        release.SetResult();
        await save;
        Assert.Equal(0, mutations.Pending);
    }

    [Fact]
    public async Task FailedServerAttempt_DoesNotReportConfirmedSuccess()
    {
        var (_, mutations, source) = Create(new FailingTransport());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.UpdateDetailsAsync(PlaylistUri, "Renamed", null, null, TestContext.Current.CancellationToken));

        Assert.Contains("could not be confirmed", error.Message);
        Assert.Equal(1, mutations.Pending); // durable retry remains queued
    }
}
