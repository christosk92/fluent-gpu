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
            if (svc.RealLibrarySource is { } libSrc)
                libSrc.OnDemandFetch = async (uri, c) =>
                {
                    if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) await fetcher.FetchPlaylistAsync(uri, c).ConfigureAwait(false);
                    else if (uri.StartsWith("spotify:album:", StringComparison.Ordinal)) await metadata.SyncAllAsync(new[] { uri }, c).ConfigureAwait(false);
                };

            // (b) hydrate playlist HEADERS (name/cover) in the background so the home + sidebar show names, not URIs.
            _ = Task.Run(() => HydratePlaylistHeadersAsync(live, store, log, ct));
        }

        return new LiveSessionHost(transport, connect);
    }

    public ValueTask DisposeAsync()
    {
        _connect.Dispose();
        _transport.Dispose();
        return ValueTask.CompletedTask;
    }

    // Hydrate each rootlist playlist's header (name/cover) — header-only (no member pull). Skips already-hydrated ones,
    // sequential + best-effort so a single failure never stops the rest. The store fires Changes → the UI refreshes.
    static async Task HydratePlaylistHeadersAsync(LiveSpclient live, IStore store, Action<string> log, CancellationToken ct)
    {
        try
        {
            var fetcher = new PlaylistFetcher(live.Pipeline, () => live.BaseUrl, store, (_, _) => Task.CompletedTask);
            // Coalesce the per-playlist upserts into ONE store change so the home/sidebar refresh once at the end, not N×.
            using var bulk = store.BeginBulk();
            int n = 0;
            foreach (var e in store.Rootlist())
            {
                if (ct.IsCancellationRequested) break;
                if (e.Kind != 0 || !e.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) continue;
                if (store.GetPlaylist(e.Uri) is { Cover: not null }) continue;   // fully hydrated (name + cover) — skip
                try { await fetcher.FetchPlaylistHeaderAsync(e.Uri, ct).ConfigureAwait(false); n++; }
                catch { /* skip a failed playlist, keep going */ }
            }
            if (n > 0) log($"hydrated {n} playlist headers (home + sidebar names)");
        }
        catch (Exception ex) { log("playlist header hydration: " + ex.Message); }
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
        string? plUri = null, alUri = null;
        if (svc.RealStore is { } st)
        {
            foreach (var e in st.Rootlist())
                if (e.Kind == 0 && e.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) { plUri = e.Uri; break; }
            foreach (var u in st.SavedUris("albums")) { alUri = u; break; }
        }
        if (plUri is not null)
        {
            var full = await svc.Library.GetPlaylistAsync(plUri, ct).ConfigureAwait(false);
            log($"  on-open playlist '{full?.Name}' → {full?.Tracks?.Count ?? 0} tracks");
        }
        if (alUri is not null)
        {
            var al = await svc.Library.GetAlbumAsync(alUri, ct).ConfigureAwait(false);
            log($"  on-open album '{al?.Name}' → {al?.Tracks?.Count ?? 0} tracks");
        }

        log("Listening 25s (control Wavee from your phone's Connect picker to drive commands)...");
        try { await Task.Delay(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false); } catch { }
        return 0;
    }
}
