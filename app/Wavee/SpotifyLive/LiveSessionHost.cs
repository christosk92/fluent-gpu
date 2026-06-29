using System;
using System.Collections.Generic;
using System.Text.Json;
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
    readonly CancellationTokenSource _cts;

    LiveSessionHost(LiveDealerTransport transport, LiveConnect connect, CancellationTokenSource cts)
    { _transport = transport; _connect = connect; _cts = cts; }

    public LiveConnect Connect => _connect;

    /// <summary>Cancelled on dispose (logout) — gates the background hydration / fetch tasks so they stop instead of
    /// running against the store after the user signed out.</summary>
    public CancellationToken Token => _cts.Token;

    public static async Task<LiveSessionHost?> StartAsync(Services svc, Action<string> log, CancellationToken ct,
        ILoginProgress? progress = null, bool interactive = true, bool useBrowser = false, bool quietPhases = false)
    {
        var report = progress ?? NullLoginProgress.Instance;
        var adapter = new AuthStateAdapter(report, interactive, useBrowser, quietPhases);

        // Silent resume with NO stored credential → Welcome, never the Error card (a null login is ambiguous between "no
        // credential" and "handshake failed"; this pre-check disambiguates the common first-run path).
        if (!interactive && !SpotifyLiveLogin.HasStoredCredential())
        {
            report.Report(new LoginSnapshot(LoginPhase.LoggedOut));
            return null;
        }
        // quietPhases: a racing sibling (the browser button alongside the device code) stays silent on the intermediate
        // states so it can't replace the two-pane — it surfaces only Finalizing/Authenticated/PremiumRequired on success.
        if (!quietPhases) report.Report(new LoginSnapshot(!interactive ? LoginPhase.SilentResume : useBrowser ? LoginPhase.AwaitingBrowser : LoginPhase.RequestingCode));

        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct, retainApChannel: true,
            allowDeviceCode: interactive && !useBrowser, authObserver: adapter,
            onCredentialAcquired: () => report.Report(new LoginSnapshot(LoginPhase.Finalizing)),
            allowBrowser: interactive && useBrowser).ConfigureAwait(false);
        if (live is null)
        {
            if (ct.IsCancellationRequested || quietPhases) return null;   // superseded / cancelled / a quiet racing sibling → stay silent
            // Welcome on a silent miss (no / rejected-and-cleared credential); Failed/Expired on a genuine error or lapsed code.
            report.Report(adapter.Terminal(credExisted: SpotifyLiveLogin.HasStoredCredential()));
            return null;
        }

        // Premium gate IN-APP (replaces the pre-window MessageBox): refuse a Free account here, and wipe the reusable blob
        // LoginAsync already persisted so the next launch can't silent-resume straight back into the wall.
        if (live.Session.Tier != Tier.Premium)
        {
            live.CredStore?.Clear();
            report.Report(new LoginSnapshot(LoginPhase.PremiumRequired));
            live.ApChannel?.Dispose();
            return null;
        }

        var dealerJson = await SharedHttp.Client.GetStringAsync("https://apresolve.spotify.com/?type=dealer", ct).ConfigureAwait(false);
        var dealerHosts = ApResolver.ParseHosts(dealerJson, "dealer");
        if (dealerHosts.Count == 0) { log("no dealer host — live session not started"); if (!ct.IsCancellationRequested) report.Report(adapter.Terminal(credExisted: true)); live.ApChannel?.Dispose(); return null; }

        // The transport's token provider RE-MINTS on reconnect/expiry (not a captured constant). The WHOLE dealer host
        // list is passed (failover across hosts), and a Connectivity signal is driven by the socket lifecycle so a drop
        // shows in the UI as "Reconnecting…" (not silent stale playback) — surfaced via svc.Connectivity on go-live.
        var connectivity = new Connectivity();
        var transport = new LiveDealerTransport(dealerHosts, live.TokenProvider, live.Pipeline, () => live.BaseUrl, log, connectivity);

        // Context resolution (inbound Connect play + UI play) needs the metadata stack to hydrate the resolved order, so
        // build it up front — over the SAME store the catalog reads — and hand the controller a unified context resolver.
        // (extendedMetadata + metadata are reused below for the on-open fetcher + now-playing enrichment → one cache.)
        Wavee.Backend.Metadata.ExtendedMetadataSource? extendedMetadata = null;
        Wavee.Backend.Metadata.MetadataService? metadata = null;
        IContextResolver? contexts = null;
        if (svc.RealStore is { } mdStore)
        {
            extendedMetadata = new Wavee.Backend.Metadata.ExtendedMetadataSource(live.Pipeline, () => live.BaseUrl, () => live.Session);
            metadata = new Wavee.Backend.Metadata.MetadataService(extendedMetadata, mdStore, () => live.Session);
            contexts = new LiveContextResolver(transport, metadata, mdStore, log);
        }

        var connect = new LiveConnect(transport, live.DeviceId, live.ApChannel, contexts, log: log);
        transport.Start();
        // Profile (name + avatar) and the account email fetched in PARALLEL before go-live, so CurrentUser is complete on
        // the first render (no refresh hook). Both are best-effort — a failure just omits that field.
        var profileTask = FetchProfileAsync(live.Pipeline, live.BaseUrl, live.Username, ct);
        var emailTask = FetchEmailAsync(live.AccessToken, ct);
        var (displayName, avatarUrl) = await profileTask.ConfigureAwait(false);
        var email = await emailTask.ConfigureAwait(false);
        var liveSession = new LiveSpotifySession(live.Username, displayName, avatarUrl, live.Session.Tier == Tier.Premium, email);

        // Owned CTS — INDEPENDENT of the bootstrap ct (a racing-sibling cancel must not kill hydration); cancelled on logout.
        var cts = new CancellationTokenSource();
        var host = new LiveSessionHost(transport, connect, cts);

        // Supersede check: a newer login cancels THIS bootstrap's ct. Bail (disposing what we built) so a stale flow can't
        // AttachLive/GoLive over the winner. No await between here and GoLive → effectively atomic. AttachLive runs BEFORE
        // GoLive so a logout fired in the go-live window still tears the host down (not a no-op).
        if (ct.IsCancellationRequested) { await host.DisposeAsync().ConfigureAwait(false); return null; }
        svc.AttachLive(host, live.CredStore!);
        var lyrics = BuildLiveLyrics(live.Pipeline, () => live.BaseUrl, connect.Controller);
        svc.GoLive(connect.Controller, connect.Devices, liveSession, connectivity, lyrics);
        report.Report(new LoginSnapshot(LoginPhase.Authenticated, User: liveSession.CurrentUser));
        log("Live Connect session active — Wavee is a controllable device, mirrors now-playing, and shows the live account.");

        // Live data wiring into the SAME store the catalog reads (InMemoryStore is lock-guarded → safe off-thread):
        if (svc.RealStore is { } store && metadata is { } md && extendedMetadata is { } em)
        {
            // (a) fetch playlist/album TRACKS the first time a detail page opens (the sync stored headers only). The real
            //     hydrator (MetadataService over the extended-metadata batch) replaces the no-op that left lists empty.
            //     em + md were built above for the context resolver — reuse them so the whole session shares one cache.
            var fetcher = new PlaylistFetcher(live.Pipeline, () => live.BaseUrl, store, (uris, c) => md.SyncAllAsync(uris, c));
            // Pathfinder (GraphQL) for rich catalog reads with no protobuf equivalent — the artist overview, on open.
            var pathfinder = new PathfinderClient(live.TokenProvider, _ => Task.FromResult(live.ClientToken), log);
            var homeCache = new LiveHomeCache(pathfinder);
            // Below-the-fold album enrichment (about-artist / merch / similar via Pathfinder; recommended playlists via the
            // SAME extended-metadata source, kinds 151→205) — installed into the switchable service the album pages hold.
            svc.AlbumEnrichment.SetInner(new SpotifyAlbumEnrichmentService(pathfinder, em, store, log));
            if (svc.RealLibrarySource is { } libSrc)
            {
                libSrc.OnDemandFetch = async (uri, c) =>
                {
                    if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) await fetcher.FetchPlaylistAsync(uri, c).ConfigureAwait(false);
                    else if (uri.StartsWith("spotify:album:", StringComparison.Ordinal)) await FetchAlbumAsync(pathfinder, store, uri, c).ConfigureAwait(false);
                    else if (uri.StartsWith("spotify:artist:", StringComparison.Ordinal)) await FetchArtistAsync(pathfinder, store, uri, c).ConfigureAwait(false);
                };
                libSrc.LiveHomeFetch = c => homeCache.GetAsync(c);   // cached editorial home + separately refreshed recents
                libSrc.LiveSearch = (q, facet, offset, limit, c) => FetchSearchAsync(pathfinder, q, facet, offset, limit, c);   // paged online search
                libSrc.LiveSuggest = async (q, c) => (await FetchSuggestRichAsync(pathfinder, q, c).ConfigureAwait(false)).Queries;   // omnibar as-you-type suggestions
                libSrc.LiveSuggestRich = (q, c) => FetchSuggestRichAsync(pathfinder, q, c);
            }

            // Now-playing enrichment: the cluster's player_state metadata is thin (often no artist / no album art), so
            // resolve the full track by uri over the extended-metadata transport + fold artist/album/art into the bar.
            connect.Projection.TrackResolver = async (uri, c) =>
            {
                if (!uri.StartsWith("spotify:track:", StringComparison.Ordinal)) return null;
                return await ResolveNowPlayingTrackAsync(uri, md, pathfinder, store, c).ConfigureAwait(false);
            };

            // (b) hydrate playlist HEADERS (name/cover) so the home + sidebar show names; for cover-less playlists also
            //     pull the tracklist so they render a 2×2 album mosaic.
            _ = Task.Run(() => HydratePlaylistHeadersAsync(fetcher, store, log, cts.Token));
        }

        return host;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();           // stop background hydration / in-flight fetches before tearing the transport down
        _connect.Dispose();
        _transport.Dispose();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── AuthState → LoginSnapshot projection ─────────────────────────────────────────────────────────────────────────
    /// <summary>Maps the backend reactive <see cref="AuthState"/> stream to UI <see cref="LoginSnapshot"/>s. The live
    /// AuthFlow only emits LoggedOut → AwaitingCredential → AwaitingUser(challenge) → ChallengeExpired; Finalizing /
    /// Authenticated / Failed / PremiumRequired are reported by the bootstrap (the AuthFlow never calls Connecting()).</summary>
    sealed class AuthStateAdapter(ILoginProgress progress, bool interactive, bool useBrowser, bool quiet = false) : IObserver<AuthState>
    {
        AuthPhase _last = AuthPhase.LoggedOut;

        public void OnNext(AuthState s)
        {
            _last = s.Phase;
            if (quiet) return;   // a racing sibling stays silent on the intermediate states (the two-pane owns them)
            switch (s.Phase)
            {
                case AuthPhase.AwaitingCredential:
                    progress.Report(new LoginSnapshot(!interactive ? LoginPhase.SilentResume : useBrowser ? LoginPhase.AwaitingBrowser : LoginPhase.RequestingCode));
                    break;
                case AuthPhase.AwaitingUser when s.Challenge is { } c:
                    progress.Report(new LoginSnapshot(LoginPhase.AwaitingApproval,
                        new LoginChallenge(c.UserCode, c.VerificationUri, c.VerificationUriComplete, c.Expiry)));
                    break;
                case AuthPhase.ChallengeExpired:
                    progress.Report(new LoginSnapshot(LoginPhase.ChallengeExpired));
                    break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }

        /// <summary>The phase to show when LoginAsync returned null: a lapsed code → ChallengeExpired; a silent resume that
        /// found no usable credential → Welcome (LoggedOut); otherwise a genuine network/AP failure → Failed.</summary>
        public LoginSnapshot Terminal(bool credExisted) =>
            _last == AuthPhase.ChallengeExpired ? new LoginSnapshot(LoginPhase.ChallengeExpired)
          : (!interactive && !credExisted)      ? new LoginSnapshot(LoginPhase.LoggedOut)
          :                                       new LoginSnapshot(LoginPhase.Failed, Error: "We couldn't reach Spotify. Check your connection and try again.");
    }

    sealed class NullLoginProgress : ILoginProgress
    {
        public static readonly NullLoginProgress Instance = new();
        public void Report(LoginSnapshot snapshot) { }
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

    // Full-catalog online search via Pathfinder — the per-facet ops (searchTracks/Albums/Artists/Playlists) fired in
    // parallel, each filling its own data.searchV2.<facet>, merged into one SearchResults. The query variable is
    // "searchTerm" (NOT "query"), matching the captured wire request exactly.
    static async Task<SearchResults?> FetchSearchAsync(PathfinderClient pf, string query, SearchFacet facet, int offset, int limit, CancellationToken ct)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 50);

        void Vars(Utf8JsonWriter w)
        {
            w.WriteBoolean("includePreReleases", false);
            w.WriteBoolean("includeAlbumPreReleases", true);
            w.WriteNumber("numberOfTopResults", limit);
            w.WriteString("searchTerm", query);
            w.WriteNumber("offset", offset);
            w.WriteNumber("limit", limit);
            w.WriteBoolean("includeAudiobooks", true);
            w.WriteBoolean("includeAuthors", true);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
        }
        // The unified top-results op (the "All" tab) declares a DIFFERENT variable set, keyed on "query" (not "searchTerm").
        void VarsTop(Utf8JsonWriter w)
        {
            w.WriteString("query", query);
            w.WriteNumber("limit", limit);
            w.WriteNumber("offset", offset);
            w.WriteNumber("numberOfTopResults", limit);
            w.WriteBoolean("includeArtistHasConcertsField", false);
            w.WriteBoolean("includeAudiobooks", true);
            w.WriteBoolean("includeAuthors", true);
            w.WriteBoolean("includePreReleases", true);
            w.WriteBoolean("includeAlbumPreReleases", true);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
            w.WriteNull("isPrefix");
            w.WriteStartArray("sectionFilters");
            w.WriteStringValue("GENERIC");
            w.WriteStringValue("VIDEO_CONTENT");
            w.WriteEndArray();
        }

        var callerCt = ct;
        using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        searchCts.CancelAfter(TimeSpan.FromSeconds(8));
        ct = searchCts.Token;

        try
        {
            if (facet == SearchFacet.All)
            {
                using var topd = await pf.QueryAsync(PathfinderOps.SearchTopResults, PathfinderOps.SearchTopResultsHash, VarsTop, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
                if (topd is null) throw new InvalidOperationException("Spotify top-results search failed.");
                var topHits = Wavee.Core.SpotifyExportMapper.TopHitsFromV2(topd.RootElement);
                var totals = Wavee.Core.SpotifyExportMapper.SearchFromV2(topd.RootElement);
                return totals with { TopHits = topHits };
            }

            var (op, hash) = facet switch
            {
                SearchFacet.Tracks => (PathfinderOps.SearchTracks, PathfinderOps.SearchTracksHash),
                SearchFacet.Albums => (PathfinderOps.SearchAlbums, PathfinderOps.SearchAlbumsHash),
                SearchFacet.Artists => (PathfinderOps.SearchArtists, PathfinderOps.SearchArtistsHash),
                SearchFacet.Playlists => (PathfinderOps.SearchPlaylists, PathfinderOps.SearchPlaylistsHash),
                _ => throw new NotSupportedException($"Search facet '{facet}' is not wired to a Pathfinder operation yet."),
            };

            using var doc = await pf.QueryAsync(op, hash, Vars, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
            if (doc is null) throw new InvalidOperationException($"Spotify {facet} search failed.");
            return Wavee.Core.SpotifyExportMapper.SearchFromV2(doc.RootElement);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            throw new TimeoutException($"Spotify {facet} search timed out.");
        }
    }

    // The real lyrics feed (docs/lyrics-aggregator-reranker-plan.md): fan out to AMLL (word-synced TTML by track id),
    // Spotify-native (the rerank reference + a line candidate, via the authed spclient), and LRCLIB (clean metadata
    // fallback); the reranker validates content/timing and picks the best. The request is resolved from the live
    // now-playing track (what the lyrics view asks for). Grey CJK/Musixmatch sources stay off by default (LyricsOptions).
    static Wavee.Backend.Lyrics.AggregatingLyricsProvider BuildLiveLyrics(
        Wavee.Backend.Spotify.IHttpExchange pipeline, Func<string> baseUrl, IPlaybackPlayer controller)
    {
        var http = new Wavee.Backend.Lyrics.SharedHttpLyricFetch();

        async Task<string?> SpotifyGet(string url, CancellationToken c)
        {
            try
            {
                var headers = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { ["Accept"] = "application/json", ["app-platform"] = "WebPlayer" };
                using var resp = await pipeline.SendAsync(new Wavee.Backend.Spotify.HttpReq("GET", url, headers, null), c).ConfigureAwait(false);
                if (resp.Status != 200) return null;
                using var reader = new System.IO.StreamReader(resp.Body);
                return await reader.ReadToEndAsync(c).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        var sources = new Wavee.Backend.Lyrics.ILyricCandidateSource[]
        {
            new Wavee.Backend.Lyrics.Sources.AmllTtmlDbSource(http),
            new Wavee.Backend.Lyrics.Sources.SpotifyNativeLyricsSource(SpotifyGet, baseUrl),
            new Wavee.Backend.Lyrics.Sources.LrcLibSource(http),
        };

        Task<Wavee.Backend.Lyrics.LyricsRequest?> Resolve(string trackId, CancellationToken c)
        {
            var t = controller.State.CurrentTrack;
            if (t is not null && (t.Id == trackId || t.Uri == "spotify:track:" + trackId))
            {
                var artists = new System.Collections.Generic.List<string>(t.Artists.Count);
                foreach (var a in t.Artists) artists.Add(a.Name);
                return Task.FromResult<Wavee.Backend.Lyrics.LyricsRequest?>(new Wavee.Backend.Lyrics.LyricsRequest(
                    trackId, "spotify:track:" + trackId, t.Title, artists, t.Album.Name, t.DurationMs,
                    Isrc: null, Market: "from_token", HasSpotifyLyrics: null));
            }
            return Task.FromResult<Wavee.Backend.Lyrics.LyricsRequest?>(null);
        }

        return new Wavee.Backend.Lyrics.AggregatingLyricsProvider(
            sources, Resolve, Wavee.Backend.Lyrics.LyricsOptions.Default, referenceSourceId: "spotify",
            log: s => WaveeLog.Instance.Info("lyrics", s));
    }

    // The signed-in user's profile (display name + avatar) via spclient user-profile-view — the cluster/login only give
    // the opaque username, so the account chip would otherwise show "31unjf…" with no photo. Best-effort: falls back to
    // the username on any failure. Fetched BEFORE go-live so CurrentUser is correct from the first render (no refresh hook).
    static async Task<(string displayName, string? avatarUrl)> FetchProfileAsync(
        Wavee.Backend.Spotify.IHttpExchange http, string baseUrl, string username, CancellationToken ct)
    {
        try
        {
            var url = baseUrl + "/user-profile-view/v3/profile/" + Uri.EscapeDataString(username) + "?market=from_token";
            var headers = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };
            using var resp = await http.SendAsync(new Wavee.Backend.Spotify.HttpReq("GET", url, headers, null), ct).ConfigureAwait(false);
            if (resp.Status != 200) return (username, null);
            using var doc = await JsonDocument.ParseAsync(resp.Body, default, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()! : username;
            string? avatar = root.TryGetProperty("image_url", out var im) && im.ValueKind == JsonValueKind.String && im.GetString() is { Length: > 0 } a
                ? a : null;
            return (name, avatar);
        }
        catch { return (username, null); }
    }

    // The account email via the Web API /v1/me (the user-read-email scope is requested). Best-effort: null on any failure
    // (the account chip just omits the email line). Bearer = the login5 access token — the same token the spclient calls use.
    static async Task<string?> FetchEmailAsync(string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(accessToken)) return null;
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://api.spotify.com/v1/me");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await SharedHttp.Client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(s, default, ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString())
                ? e.GetString() : null;
        }
        catch { return null; }
    }

    // As-you-type omnibar suggestions via Pathfinder searchSuggestions (variable "query", not "searchTerm").
    static async Task<IReadOnlyList<string>> FetchSuggestAsync(PathfinderClient pf, string query, CancellationToken ct)
    {
        var suggestions = await FetchSuggestRichAsync(pf, query, ct).ConfigureAwait(false);
        return suggestions.Queries;
    }

    static async Task<SearchSuggestions> FetchSuggestRichAsync(PathfinderClient pf, string query, CancellationToken ct)
    {
        using var doc = await pf.QueryAsync(PathfinderOps.SearchSuggestions, PathfinderOps.SearchSuggestionsHash,
            w =>
            {
                w.WriteString("query", query);
                w.WriteNumber("limit", 30);
                w.WriteNumber("numberOfTopResults", 30);
                w.WriteNumber("offset", 0);
                w.WriteBoolean("includeAuthors", true);
                w.WriteBoolean("includeAlbumPreReleases", true);
                w.WriteBoolean("includeEpisodeContentRatingsV2", true);
            }, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
        return doc is null ? SearchSuggestions.Empty : Wavee.Core.SpotifyExportMapper.SuggestionsFromV2(doc.RootElement);
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
    static async Task<IReadOnlyList<HomeCard>> FetchRecentsAsync(PathfinderClient pf, CancellationToken ct)
    {
        using var doc = await pf.QueryAsync(PathfinderOps.Recents, PathfinderOps.RecentsHash,
            w =>
            {
                w.WriteStartArray("uris");
                w.WriteStringValue("spotify:list:recents:page");
                w.WriteEndArray();
                w.WriteNumber("offset", 0);
                w.WriteNumber("limit", 100);
            }, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
        return doc is null ? System.Array.Empty<HomeCard>() : Wavee.Core.SpotifyExportMapper.RecentCards(doc.RootElement, 8);
    }

    sealed class LiveHomeCache
    {
        static readonly TimeSpan HomeTtl = TimeSpan.FromMinutes(15);
        static readonly TimeSpan RecentsTtl = TimeSpan.FromSeconds(60);

        readonly PathfinderClient _pf;
        readonly SemaphoreSlim _homeGate = new(1, 1);
        readonly SemaphoreSlim _recentsGate = new(1, 1);
        IReadOnlyList<HomeGroup> _home = System.Array.Empty<HomeGroup>();
        IReadOnlyList<HomeCard> _recents = System.Array.Empty<HomeCard>();
        DateTimeOffset _homeAt = DateTimeOffset.MinValue;
        DateTimeOffset _recentsAt = DateTimeOffset.MinValue;

        public LiveHomeCache(PathfinderClient pf) => _pf = pf;

        public async Task<IReadOnlyList<HomeGroup>> GetAsync(CancellationToken ct)
        {
            var homeTask = GetHomeGroupsAsync(ct);
            var recentsTask = GetRecentsAsync(ct);
            await Task.WhenAll(homeTask, recentsTask).ConfigureAwait(false);

            var home = await homeTask.ConfigureAwait(false);
            var recents = await recentsTask.ConfigureAwait(false);
            if (recents.Count == 0) return home;

            var groups = new List<HomeGroup>(home.Count + 1)
            {
                new(HomeGroupKind.QuickGrid, "Recently played", recents, 0xFFF59E0Bu),
            };
            groups.AddRange(home);
            return groups;
        }

        async Task<IReadOnlyList<HomeGroup>> GetHomeGroupsAsync(CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            if (_home.Count > 0 && now - _homeAt < HomeTtl) return _home;

            await _homeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                now = DateTimeOffset.UtcNow;
                if (_home.Count > 0 && now - _homeAt < HomeTtl) return _home;
                var fresh = await FetchHomeAsync(_pf, ct).ConfigureAwait(false);
                if (fresh.Count > 0)
                {
                    _home = fresh;
                    _homeAt = now;
                }
                return _home;
            }
            finally { _homeGate.Release(); }
        }

        async Task<IReadOnlyList<HomeCard>> GetRecentsAsync(CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            if (_recents.Count > 0 && now - _recentsAt < RecentsTtl) return _recents;

            await _recentsGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                now = DateTimeOffset.UtcNow;
                if (_recents.Count > 0 && now - _recentsAt < RecentsTtl) return _recents;
                var fresh = await FetchRecentsAsync(_pf, ct).ConfigureAwait(false);
                if (fresh.Count > 0)
                {
                    _recents = fresh;
                    _recentsAt = now;
                }
                return _recents;
            }
            finally { _recentsGate.Release(); }
        }
    }

    static async Task FetchAlbumAsync(PathfinderClient pf, IStore store, string uri, CancellationToken ct)
    {
        using var doc = await pf.QueryAsync(PathfinderOps.GetAlbum, PathfinderOps.GetAlbumHash,
            w => { w.WriteString("uri", uri); w.WriteString("locale", ""); w.WriteNumber("offset", 0); w.WriteNumber("limit", 50); },
            PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
        if (doc is null) return;
        if (Wavee.Core.SpotifyExportMapper.AlbumFromUnion(doc.RootElement) is { } album)
        {
            if (album.ArtistsDetailed is { Count: > 0 })
                foreach (var artist in album.ArtistsDetailed)
                    store.UpsertArtist(artist);
            store.UpsertAlbum(album);
        }
    }

    // Connect's player_state can be thin. Resolve the full TrackV4 through extended-metadata; TrackV4's album ref carries
    // cover_group, and StoreEntityMerge keeps that richer image if a later thin cluster/store write arrives.
    static async Task<Track?> ResolveNowPlayingTrackAsync(string uri, Wavee.Backend.Metadata.MetadataService metadata,
        PathfinderClient pathfinder, IStore store, CancellationToken ct)
    {
        await metadata.SyncAllAsync(new[] { uri }, ct).ConfigureAwait(false);
        var track = store.GetTrack(uri);
        if (track?.Image is not null && track.Artists.Count > 0) return track;

        using var doc = await pathfinder.QueryAsync(PathfinderOps.GetTrack, PathfinderOps.GetTrackHash,
            w => w.WriteString("uri", uri), PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
        if (doc is not null && SpotifyExportMapper.TrackFromUnion(doc.RootElement) is { } full)
        {
            store.UpsertTrack(full);
            track = store.GetTrack(uri) ?? full;
        }
        return track;
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
        // Observability proof: the realtime (dealer socket) link status — toggle your network to see Reconnecting → Online.
        using var connSub = svc.Connectivity.StatusChanged.Subscribe(Observers.From<Wavee.Core.ConnectionStatus>(
            s => log("  realtime link: " + s)));
        log("  realtime link: " + svc.Connectivity.Status);

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
        var sr = await svc.Library.SearchAsync("paul kim", ct).ConfigureAwait(false);
        log($"  search 'paul kim' → {sr.Tracks.Count} tracks, {sr.Albums.Count} albums, {sr.Artists.Count} artists, {sr.Playlists.Count} playlists");
        var sg = await svc.Library.SuggestAsync("aras", ct).ConfigureAwait(false);
        log($"  suggest 'aras' → {sg.Count}: {string.Join(" | ", System.Linq.Enumerable.Take(sg, 6))}");

        log("SMOKE TEST — Wavee is now a live Connect device. In the next 90s:");
        log("  1) open Spotify on your phone/web → device picker → confirm \"Wavee\" appears;");
        log("  2) transfer to Wavee + play a playlist/album/Liked Songs → watch now-playing + the queue below;");
        log("  3) pause/seek/next/shuffle/repeat from the phone → each logs an inbound 'connect command' + a put-state;");
        log("  4) (optional) toggle airplane mode briefly → watch 'realtime link: Reconnecting' then 'Online'.");
        try { await Task.Delay(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false); } catch { }
        return 0;
    }
}
