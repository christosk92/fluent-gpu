using System;
using System.IO;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The outbox is durable: pending intents persist to SQLite and a fresh engine over the same store replays them — so an
// offline save/edit survives a restart.
public class DurableOutboxTests
{
    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-test-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { File.Delete(f); } catch { } } }

    [Fact]
    public async Task SetSaves_SurviveRestart_AndReplay()
    {
        var path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path))
            {
                var store = new CachedStore(cold);
                var eng = new MutationEngine(store, new IMutationStrategy[] { new SetReplayStrategy() }, cold);
                eng.Save("liked", "spotify:track:a", true);
                eng.Save("albums", "spotify:album:b", true);
                Assert.Equal(2, eng.Pending);
            }   // dispose → outbox already durable (synchronous)

            using (var cold2 = new SqliteColdStore(path))
            {
                var store2 = new CachedStore(cold2);
                var eng2 = new MutationEngine(store2, new IMutationStrategy[] { new SetReplayStrategy() }, cold2);
                Assert.Equal(2, eng2.Pending);   // restored from disk on construction

                await eng2.Drain(new StubTransport(), SessionContext.LoggedOut);
                Assert.Equal(0, eng2.Pending);   // replayed + cleared (and removed from the durable outbox)
            }

            using (var cold3 = new SqliteColdStore(path))
            {
                var eng3 = new MutationEngine(new CachedStore(cold3), new IMutationStrategy[] { new SetReplayStrategy() }, cold3);
                Assert.Equal(0, eng3.Pending);   // the drained outbox stays empty across the next restart
            }
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task PlaylistEdit_SurvivesRestart_WithOpsAndBaseRev()
    {
        var path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path))
            {
                var store = new CachedStore(cold);
                store.SetMembership("spotify:playlist:p", new[] { new PlaylistMember("a", "spotify:track:a", null, 0), new PlaylistMember("b", "spotify:track:b", null, 0) }, new byte[] { 7 });
                var eng = new MutationEngine(store, new IMutationStrategy[] { new SetReplayStrategy(), new OpRebaseStrategy(store, () => "https://spclient.wg.spotify.com") }, cold);
                eng.Edit("spotify:playlist:p", new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1) }, new byte[] { 7 });
                Assert.Equal(1, eng.Pending);
            }

            using (var cold2 = new SqliteColdStore(path))
            {
                var store2 = new CachedStore(cold2);
                var eng2 = new MutationEngine(store2, new IMutationStrategy[] { new SetReplayStrategy(), new OpRebaseStrategy(store2, () => "https://spclient.wg.spotify.com") }, cold2);
                Assert.Equal(1, eng2.Pending);   // the op-rebase edit (ops + base_rev) round-tripped through SQLite
                await eng2.Drain(new StubTransport(), SessionContext.LoggedOut);
                Assert.Equal(0, eng2.Pending);
            }
        }
        finally { TryDelete(path); }
    }
}
