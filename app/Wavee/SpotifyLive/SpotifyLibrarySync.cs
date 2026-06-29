using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Wavee.Backend.Realtime;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive;

// The end-to-end LIVE library sync into the REAL persistent store: connect → fetch the rootlist + every collection set
// (hydrating metadata) → persist to SQLite → open the hm:// dealer firehose and apply/mark-dirty real-time pushes. After
// this runs, a `--real-backend` app launch reads the library offline from disk. Needs creds + network, so the USER runs
// it: `--spotify-sync`. The pieces are all unit-tested; this is the live composition.
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

        var playlistFetcher = new PlaylistFetcher(live.Pipeline, () => live.BaseUrl, store, Hydrate);
        var collectionFetcher = new CollectionFetcher(live.Pipeline, () => live.BaseUrl, () => live.Username, store,
            s => cold.GetCollectionRevision(s), (s, r) => cold.SetCollectionRevision(s, r, 0), Hydrate);

        // 1) the rootlist (folder/playlist tree).
        log("Syncing rootlist...");
        try { await playlistFetcher.FetchRootlistAsync("spotify:user:" + live.Username + ":rootlist", ct).ConfigureAwait(false); }
        catch (Exception ex) { log("  rootlist sync failed: " + ex.Message); }
        var rootlist = store.Rootlist();
        log("  " + rootlist.Count(e => e.Kind == 0) + " playlists, " + rootlist.Count(e => e.Kind == 1) + " folders.");

        // 2) every collection set (token-gated delta when a sync token exists, else a full page snapshot).
        foreach (var set in Sets)
        {
            try { await collectionFetcher.FetchSetAsync(set, ct).ConfigureAwait(false); log("  " + set + ": " + store.SavedUris(set).Count + " items."); }
            catch (Exception ex) { log("  " + set + " sync failed: " + ex.Message); }
        }

        store.Flush();
        log("Library synced + persisted to " + dbPath);

        // 3) open the hm:// dealer firehose: parent-rev pushes apply in place, everything else marks dirty → lazy re-fetch.
        var dealerJson = await SharedHttp.Client.GetStringAsync("https://apresolve.spotify.com/?type=dealer", ct).ConfigureAwait(false);
        var dealerHosts = ApResolver.ParseHosts(dealerJson, "dealer");
        if (dealerHosts.Count == 0) { log("No dealer host — skipping the real-time listen."); return 0; }

        using var transport = new LiveDealerTransport(dealerHosts, _ => Task.FromResult(live.AccessToken), live.Pipeline, () => live.BaseUrl, log);
        using var router = new DealerRouter(transport, store,
            uri => { log("  push: " + uri + " (re-syncing)"); _ = playlistFetcher.FetchPlaylistAsync(uri, ct); },
            set => { foreach (var s in Sets) _ = collectionFetcher.FetchSetAsync(s, ct); });
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
        return 0;
    }
}
