using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// ── Live session bootstrap — bring up Connect + playback and swap it into the running app ─────────────────────────────
// Logs in, opens the dealer + the persistent AP channel, builds the full LiveConnect stack, and calls svc.GoLive so the
// UI's PlaybackBridge (bound to the switchable facades) starts reflecting + controlling live playback — with NO UI rebuild.
// Returns null if login/dealer aren't available (the app keeps the in-memory fake backend).
public sealed class LiveSessionHost : IAsyncDisposable
{
    readonly LiveDealerTransport _transport;
    readonly LiveConnect _connect;

    LiveSessionHost(LiveDealerTransport transport, LiveConnect connect) { _transport = transport; _connect = connect; }

    public LiveConnect Connect => _connect;

    public static async Task<LiveSessionHost?> StartAsync(Services svc, Action<string> log, CancellationToken ct)
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct, retainApChannel: true).ConfigureAwait(false);
        if (live is null) return null;

        var dealerJson = await SharedHttp.Client.GetStringAsync("https://apresolve.spotify.com/?type=dealer", ct).ConfigureAwait(false);
        var dealerHosts = ApResolver.ParseHosts(dealerJson, "dealer");
        if (dealerHosts.Count == 0) { log("no dealer host — live session not started"); live.ApChannel?.Dispose(); return null; }

        // The transport's token provider RE-MINTS on reconnect/expiry (not a captured constant).
        var transport = new LiveDealerTransport(dealerHosts[0].Split(':')[0], live.TokenProvider, live.Pipeline, () => live.BaseUrl, log);
        var connect = new LiveConnect(transport, live.DeviceId, live.ApChannel, resolveContext: null, log: log);
        transport.Start();
        var liveSession = new LiveSpotifySession(live.Username, live.Session.Tier == Tier.Premium);
        svc.GoLive(connect.Controller, connect.Devices, liveSession);
        log("Live Connect session active — Wavee is a controllable device, mirrors now-playing, and shows the live account.");

        // Live data wiring into the SAME store the catalog reads (InMemoryStore is lock-guarded → safe off-thread):
        if (svc.RealStore is { } store)
        {
            // (a) fetch playlist/album TRACKS the first time a detail page opens (the sync stored headers only). The real
            //     hydrator (MetadataService over the extended-metadata batch) replaces the no-op that left lists empty.
            var metadata = new Wavee.Backend.Metadata.MetadataService(
                new Wavee.Backend.Metadata.ExtendedMetadataSource(live.Pipeline, () => live.BaseUrl, () => live.Session),
                store, () => live.Session);
            var fetcher = new PlaylistFetcher(live.Pipeline, () => live.BaseUrl, store, (uris, c) => metadata.SyncAllAsync(uris, c));
            // Pathfinder (GraphQL) for rich catalog reads with no protobuf equivalent — the artist overview, on open.
            var pathfinder = new PathfinderClient(live.TokenProvider, _ => Task.FromResult(live.ClientToken), log);
            if (svc.RealLibrarySource is { } libSrc)
            {
                libSrc.OnDemandFetch = async (uri, c) =>
                {
                    if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) await fetcher.FetchPlaylistAsync(uri, c).ConfigureAwait(false);
                    else if (uri.StartsWith("spotify:album:", StringComparison.Ordinal)) await FetchAlbumAsync(pathfinder, store, uri, c).ConfigureAwait(false);
                    else if (uri.StartsWith("spotify:artist:", StringComparison.Ordinal)) await FetchArtistAsync(pathfinder, store, uri, c).ConfigureAwait(false);
                };
                libSrc.LiveHomeFetch = c => FetchHomeAsync(pathfinder, c);   // the editorial/personalized home (Pathfinder)
            }

            // (b) hydrate playlist HEADERS (name/cover) so the home + sidebar show names; for cover-less playlists also
            //     pull the tracklist so they render a 2×2 album mosaic.
            _ = Task.Run(() => HydratePlaylistHeadersAsync(fetcher, store, log, ct));
        }

        return new LiveSessionHost(transport, connect);
    }

    public ValueTask DisposeAsync()
    {
        _connect.Dispose();
        _transport.Dispose();
        return ValueTask.CompletedTask;
    }

    // Phase 1: hydrate each rootlist playlist's HEADER (name/cover) — fast, coalesced into one refresh. Phase 2: for the
    // cover-less ones, pull the TRACKLIST so SummaryOf can derive a 2×2 mosaic (progressive — each fills in as it lands).
    static async Task HydratePlaylistHeadersAsync(PlaylistFetcher fetcher, IStore store, Action<string> log, CancellationToken ct)
    {
        try
        {
            int headers = 0;
            using (store.BeginBulk())   // one store change → home/sidebar refresh once with all names
            {
                foreach (var e in store.Rootlist())
                {
                    if (ct.IsCancellationRequested) break;
                    if (e.Kind != 0 || !e.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) continue;
                    if (store.GetPlaylist(e.Uri) is not null) continue;   // header already present
                    try { await fetcher.FetchPlaylistHeaderAsync(e.Uri, ct).ConfigureAwait(false); headers++; }
                    catch { }
                }
            }
            if (headers > 0) log($"hydrated {headers} playlist headers (home + sidebar names)");

            // Cover-less playlists: pull the tracklist so the 2×2 mosaic can compose. NOT bulk-coalesced → each playlist's
            // mosaic fills in as its tracks land (progressive). Throttled implicitly by being sequential + best-effort.
            int mosaics = 0;
            foreach (var e in store.Rootlist())
            {
                if (ct.IsCancellationRequested) break;
                if (e.Kind != 0 || !e.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) continue;
                if (store.GetPlaylist(e.Uri)?.Cover is not null) continue;   // has a custom cover → no mosaic needed
                if (store.Membership(e.Uri).Count > 0) continue;            // already has a tracklist
                try { await fetcher.FetchPlaylistAsync(e.Uri, ct).ConfigureAwait(false); mosaics++; }
                catch { }
            }
            if (mosaics > 0) log($"hydrated {mosaics} cover-less playlist tracklists (mosaics)");
        }
        catch (Exception ex) { log("playlist hydration: " + ex.Message); }
    }

    // Fetch the rich artist overview via Pathfinder GraphQL → map (the export's artist-*.json IS this shape) → store.
    // Best-effort: a stale persisted-query hash or error leaves the identity-only artist in place.
    static async Task FetchArtistAsync(PathfinderClient pf, IStore store, string uri, CancellationToken ct)
    {
        using var doc = await pf.QueryAsync(PathfinderOps.QueryArtistOverview, PathfinderOps.QueryArtistOverviewHash,
            w => { w.WriteString("uri", uri); w.WriteString("locale", ""); w.WriteBoolean("preReleaseV2", false); },
            PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
        if (doc is null) return;
        if (Wavee.Core.SpotifyExportMapper.ArtistFromOverview(doc.RootElement) is { Uri.Length: > 0 } artist)
            store.UpsertArtist(artist);
    }

    // The editorial/personalized home via Pathfinder → the existing composer (data.home.sectionContainer.sections).
    static async Task<IReadOnlyList<HomeGroup>> FetchHomeAsync(PathfinderClient pf, CancellationToken ct)
    {
        using var doc = await pf.QueryAsync(PathfinderOps.Home, PathfinderOps.HomeHash,
            w =>
            {
                w.WriteString("homeEndUserIntegration", "INTEGRATION_WEB_PLAYER");
                w.WriteString("timeZone", "Etc/UTC");
                w.WriteString("sp_t", "");
                w.WriteString("facet", "");
                w.WriteNumber("sectionItemsLimit", 20);
                w.WriteBoolean("includeEpisodeContentRatingsV2", false);
            }, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
        if (doc is null) return System.Array.Empty<HomeGroup>();
        var homeRoot = Wavee.Core.SpotifyExportMapper.Dig(doc.RootElement, "data", "home");
        return Wavee.Core.SpotifyHomeComposer.Compose(homeRoot, System.Array.Empty<Wavee.Core.PlaylistSummary>()).Groups;
    }

    // Fetch the album (metadata + tracklist) via Pathfinder getAlbum → map (data.albumUnion.tracksV2) → store. The
    // spclient extended-metadata path was unreliable for some albums; getAlbum returns the full tracklist consistently.
    static async Task FetchAlbumAsync(PathfinderClient pf, IStore store, string uri, CancellationToken ct)
    {
        using var doc = await pf.QueryAsync(PathfinderOps.GetAlbum, PathfinderOps.GetAlbumHash,
            w => { w.WriteString("uri", uri); w.WriteString("locale", ""); w.WriteNumber("offset", 0); w.WriteNumber("limit", 50); },
            PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
        if (doc is null) return;
        if (Wavee.Core.SpotifyExportMapper.AlbumFromUnion(doc.RootElement) is { } album)
            store.UpsertAlbum(album);
    }

    /// <summary>CLI demo (`--connect-live`): bring up the live session over a REAL Services and log the now-playing the
    /// bridge sees THROUGH the switchable backend, for ~25 s — proving the fake→live swap end-to-end, headlessly.</summary>
    public static async Task<int> RunAsync(Action<string> log, CancellationToken ct)
    {
        log("Wavee live Connect probe — building the real backend + going live...");
        var svc = Services.CreateReal();
        await using var host = await StartAsync(svc, log, ct).ConfigureAwait(false);
        if (host is null) { log("Live session could not start."); return 1; }

        using var sub = svc.Player.State.Changes.Subscribe(Observers.From<Wavee.Core.IPlaybackState>(s =>
        {
            if (s.CurrentTrack is { } t)
                log("  bridge now-playing: " + t.Title + " — " + (s.IsPlaying ? "playing" : "paused") + " (active=" + (s.ActiveDeviceId ?? "") + ")");
        }));

        // Stage 1 verification: open the first rootlist playlist + an album through the catalog (fires OnDemandFetch).
        string? plUri = null, alUri = null, arUri = null;
        if (svc.RealStore is { } st)
        {
            foreach (var e in st.Rootlist())
                if (e.Kind == 0 && e.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) { plUri = e.Uri; break; }
            foreach (var u in st.SavedUris("albums")) { alUri = u; break; }
            foreach (var u in st.SavedUris("artists")) { arUri = u; break; }
        }
        if (plUri is not null)
        {
            var full = await svc.Library.GetPlaylistAsync(plUri, ct).ConfigureAwait(false);
            log($"  on-open playlist '{full?.Name}' → {full?.Tracks?.Count ?? 0} tracks");
        }
        if (alUri is not null)
        {
            var al = await svc.Library.GetAlbumAsync(alUri, ct).ConfigureAwait(false);
            var t0 = al?.Tracks is { Count: > 0 } tl ? $"{tl[0].Title} ({tl[0].DurationMs}ms)" : "—";
            log($"  on-open album '{al?.Name}' → {al?.Tracks?.Count ?? 0} tracks (first: {t0})");
        }
        if (arUri is not null)
        {
            var ar = await svc.Library.GetArtistAsync(arUri, ct).ConfigureAwait(false);
            log($"  on-open artist '{ar?.Name}' → {ar?.TopTracks?.Count ?? 0} top tracks, {ar?.TopAlbums?.Count ?? 0} releases, {ar?.MonthlyListeners ?? 0} listeners (Pathfinder)");
        }
        var home = await svc.Library.GetHomeAsync(ct).ConfigureAwait(false);
        log($"  home → {home.Groups.Count} groups (editorial Pathfinder + library)");

        log("Listening 25s (control Wavee from your phone's Connect picker to drive commands)...");
        try { await Task.Delay(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false); } catch { }
        return 0;
    }
}
