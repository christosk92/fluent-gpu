using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Wavee.Backend.Realtime;
using Wavee.Backend.Spotify;
using Wavee.Backend.Sync;

namespace Wavee.SpotifyLive;

// The end-to-end LIVE library sync into the REAL persistent store, driven by the SAME LibrarySync orchestrator the app
// runs (§11): connect → InitialHydrate through the loop (rootlist + fold + every collection set, hydrating metadata) →
// persist to SQLite → open the hm:// dealer firehose and route pushes through the loop. After this runs, a
// `--real-backend` app launch reads the library offline from disk. Needs creds + network, so the USER runs it:
// `--spotify-sync`. This one-shot doubles as the integration probe of the real orchestrator — no divergent hand-wiring.
public static class SpotifyLibrarySync
{
    static readonly string[] Sets = { "liked", "albums", "artists", "shows", "episodes" };

    public static async Task<int> RunAsync(Action<string> log, CancellationToken ct)
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct, retainApChannel: true).ConfigureAwait(false);
        if (live is null) return 1;

        string dbPath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Wavee", "library.db");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dbPath)!);
        using var cold = new SqliteColdStore(dbPath);
        using var store = new CachedStore(cold);

        var metadata = new MetadataService(new ExtendedMetadataSource(live.Pipeline, () => live.BaseUrl, () => live.Session), store, () => live.Session);
        Task Hydrate(IReadOnlyList<string> uris, CancellationToken c) => metadata.SyncAllAsync(uris, c);

        var mutEngine = new MutationEngine(store, new IMutationStrategy[] { new SetReplayStrategy(), new OpRebaseStrategy(store, () => live.BaseUrl), new RootlistFollowStrategy(store) }, cold);
        var sessionHost = new SessionContextHost(new SessionContext(live.Username, "US", "premium", "en", Tier.Premium, false));
        var playlistFetcher = new PlaylistFetcher(live.Pipeline, () => live.BaseUrl, store, Hydrate, () => live.Username);
        var collectionFetcher = new CollectionFetcher(live.Pipeline, () => live.BaseUrl, () => live.Username, store,
            s => cold.GetCollectionRevision(s), (s, r) => cold.SetCollectionRevision(s, r, DateTimeOffset.UtcNow.ToUnixTimeSeconds()), Hydrate,
            (s, u) => mutEngine.HasPending(s, u));

        // The dealer transport doubles as the mutation transport here (the CLI drains any restart-reloaded outbox intents
        // over the live socket during InitialHydrate — same as the app's go-live drain).
        var dealerJson = await SharedHttp.Client.GetStringAsync("https://apresolve.spotify.com/?type=dealer", ct).ConfigureAwait(false);
        var dealerHosts = ApResolver.ParseHosts(dealerJson, "dealer");
        using var transport = new LiveDealerTransport(dealerHosts, live.TokenProvider, live.Pipeline, () => live.BaseUrl, log,
            forceRefreshToken: live.ForceTokenProvider);

        // 1) InitialHydrate through the real orchestrator: drain → rootlist + "playlists" fold → every set (token-gated
        //    delta, else full paging + mark-and-sweep), per-set failures isolated — exactly the app's go-live pass.
        await using var sync = new LibrarySync(store, playlistFetcher, collectionFetcher, mutEngine, transport,
            () => sessionHost.Current, () => live.Username, log, ct);
        using var router = new DealerRouter(transport, sync);

        log("Syncing library (rootlist + collection sets, via LibrarySync.InitialHydrate)...");
        var hydrated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sync.Enqueue(new SyncCommand(SyncKind.InitialHydrate, Done: hydrated));
        await hydrated.Task.WaitAsync(ct).ConfigureAwait(false);

        var rootlist = store.Rootlist();
        log("  " + rootlist.Count(e => e.Kind == 0) + " playlists, " + rootlist.Count(e => e.Kind == 1) + " folders.");
        foreach (var set in Sets) log("  " + set + ": " + store.SavedUris(set).Count + " items.");
        store.Flush();
        log("Library synced + persisted to " + dbPath);

        // 2) the hm:// firehose: pushes decode-and-enqueue into the SAME loop (parent-rev in-place apply / dirty / delta).
        if (dealerHosts.Count == 0) { log("No dealer host — skipping the real-time listen."); return 0; }
        // Stage B — register this device on Spotify Connect: ConnectService captures the dealer connection_id (the pusher
        // hello header) and PUTs /connect-state/v1/devices/{id}, so the device APPEARS in the Connect picker. Created BEFORE
        // Start() so the first connection_id hello isn't missed. Later stages add inbound command handling + the projection.
        // Stages 0+B+C+D+E+F+H — the full live Connect+playback composition: device announce, cluster projection, inbound
        // command routing -> controller, outbound forwarding, the silent local host, and the persistent AP key channel.
        // The persistent AP channel is the LOGIN socket (retained above), reused for audio keys — no second handshake.
        using var liveConnect = new LiveConnect(transport, live.DeviceId, live.ApChannel, log: log);
        using var npSub = liveConnect.Projection.Changes.Subscribe(Observers.From<Wavee.Core.IPlaybackState>(s =>
        {
            if (s.CurrentTrack is { } tk)
                log("  now-playing: " + tk.Title + " — " + (s.IsPlaying ? "playing" : "paused") + " (active=" + liveConnect.Projection.ActiveDeviceId + ")");
        }));
        transport.Start();

        log("Dealer firehose open + Connect device announced; listening for live updates for 20s...");
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); } catch { }
        store.Flush();
        log("Sync counters: pushApplied=" + sync.PushApplied + " dirty=" + sync.PushMarkedDirty + " directApplied=" + sync.PushDirectApplied
            + " echoDropped=" + sync.EchoDropped + " setFetches=" + sync.SetFetches
            + " diff(applied/upToDate/full)=" + sync.DiffApplied + "/" + sync.DiffUpToDate + "/" + sync.DiffFellBack);
        return 0;
    }
}
