using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Playlist edits as durable, op-capable outbox intents: optimistic membership apply, replay via /changes, and a
// snapshot-based rollback on terminal failure. Distinct outbox rows per edit (append, never coalesced).
public class MutationOpRebaseTests
{
    sealed class ScriptedTransport(Func<string, Resp> respond) : ITransport
    {
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null) => Task.FromResult(respond(route));
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default) => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    static PlaylistMember M(string id) => new(id, "spotify:track:" + id, null, 0);
    static MutationEngine Engine(IStore store) => new(store, new IMutationStrategy[] { new SetReplayStrategy(), new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com") });

    [Fact]
    public async Task Edit_AppliesOptimistically_AndDrainConfirms()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, null);
        var eng = Engine(store);

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });
        Assert.Equal("spotify:track:b", Assert.Single(store.Membership("spotify:playlist:p")).ItemUri);   // optimistic

        string? route = null;
        var t = new ScriptedTransport(r => { route = r; return new Resp(true, Array.Empty<byte>(), 200); });
        await eng.Drain(t, SessionContext.LoggedOut);

        Assert.Equal(0, eng.Pending);
        Assert.Contains("/playlist/v2/playlist/p/changes", route);   // POSTed the change to the right route
        Assert.Single(store.Membership("spotify:playlist:p"));        // stays applied after confirm
    }

    [Fact]
    public async Task Edit_RollsBackMembership_ToSnapshot_OnTerminalFailure()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b") }, new byte[] { 1 });
        var clock = DateTime.UtcNow;
        var eng = new MutationEngine(store, new IMutationStrategy[] { new SetReplayStrategy(), new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com") }, null, () => clock);

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 2) });   // remove all
        Assert.Empty(store.Membership("spotify:playlist:p"));

        var t = new ScriptedTransport(_ => new Resp(false, Array.Empty<byte>(), 409));   // always fails
        for (int i = 0; i < 10; i++) { await eng.Drain(t, SessionContext.LoggedOut); clock = clock.AddSeconds(120); }   // advance past the §8.3 backoff → exhaust MaxAttempts

        Assert.Equal(0, eng.Pending);
        Assert.Single(eng.DeadLetter);
        var m = store.Membership("spotify:playlist:p");
        Assert.Equal(2, m.Count);                       // restored to the pre-edit snapshot
        Assert.Equal("spotify:track:a", m[0].ItemUri);
    }

    [Fact]
    public async Task Edits_AreAppended_NotCoalesced()
    {
        var store = new InMemoryStore();
        store.SetMembership("spotify:playlist:p", new[] { M("a"), M("b"), M("c") }, null);
        var eng = Engine(store);

        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });   // -> b,c
        eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) });   // -> c
        Assert.Equal(2, eng.Pending);   // two distinct rows, not one coalesced row
        Assert.Equal("spotify:track:c", Assert.Single(store.Membership("spotify:playlist:p")).ItemUri);

        await eng.Drain(new ScriptedTransport(_ => new Resp(true, Array.Empty<byte>(), 200)), SessionContext.LoggedOut);
        Assert.Equal(0, eng.Pending);
    }
}
