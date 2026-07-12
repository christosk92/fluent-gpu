using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Localization;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;

namespace Wavee.SpotifyLive;

// Disambiguate from the UI type Wavee.DiscographyPage (a Component) that is otherwise in scope under the Wavee.* namespace.
// (Declared inside the namespace so it wins over the enclosing-namespace member.)
using DiscographyPage = Wavee.Core.DiscographyPage;

// ── Live session bootstrap — bring up Connect + playback and swap it into the running app ─────────────────────────────
// Logs in, opens the dealer + the persistent AP channel, builds the full LiveConnect stack, and calls svc.GoLive so the
// UI's PlaybackBridge (bound to the switchable facades) starts reflecting + controlling live playback — with NO UI rebuild.
// Returns null if login/dealer aren't available (the app keeps the in-memory fake backend).
public sealed class LiveSessionHost : IAsyncDisposable
{
    readonly LiveDealerTransport _transport;
    readonly LiveConnect _connect;
    readonly CancellationTokenSource _cts;
    Wavee.Backend.Realtime.DealerRouter? _router;
    Wavee.Backend.Sync.LibrarySync? _sync;
    IDisposable? _connSub;
    SpotifyFriendActivityService? _friends;
    SpotifyNotificationsService? _notifications;
    SpotifyWhatsNewService? _whatsNew;

    LiveSessionHost(LiveDealerTransport transport, LiveConnect connect, CancellationTokenSource cts)
    { _transport = transport; _connect = connect; _cts = cts; }

    /// <summary>Register the sync-loop teardown handles (router → sync → connectivity subscription), disposed on logout
    /// BEFORE the transport so the loop stops recording against a torn-down socket.</summary>
    internal void AttachSync(Wavee.Backend.Realtime.DealerRouter router, Wavee.Backend.Sync.LibrarySync sync, IDisposable? connSub)
    { _router = router; _sync = sync; _connSub = connSub; }

    /// <summary>Register the session-scoped friend-activity (presence) feed, disposed on logout so its dealer/HTTP
    /// subscriptions + watchdog stop with the transport.</summary>
    internal void AttachFriends(SpotifyFriendActivityService friends) => _friends = friends;

    /// <summary>Register the session-scoped notification feeds (gander social + what's-new), disposed on logout so their
    /// in-flight fetches stop with the transport.</summary>
    internal void AttachNotifications(SpotifyNotificationsService notifications) => _notifications = notifications;
    internal void AttachWhatsNew(SpotifyWhatsNewService whatsNew) => _whatsNew = whatsNew;

    public LiveConnect Connect => _connect;

    /// <summary>Cancelled on dispose (logout) — gates the background hydration / fetch tasks so they stop instead of
    /// running against the store after the user signed out.</summary>
    public CancellationToken Token => _cts.Token;

    public static async Task<LiveSessionHost?> StartAsync(Services svc, WaveeLogger log, CancellationToken ct,
        ILoginProgress? progress = null, bool interactive = true, bool useBrowser = false, bool quietPhases = false,
        Action<Action>? uiPost = null)
    {
        var report = progress ?? NullLoginProgress.Instance;
        var adapter = new AuthStateAdapter(report, interactive, useBrowser, quietPhases);
        string op = "live-" + Guid.NewGuid().ToString("N")[..8];
        var connectLog = log.Sink is null ? new WaveeLogger(svc.Log, "connect") : log;
        var spclientLog = new WaveeLogger(svc.Log, "spclient");
        var metadataLog = new WaveeLogger(svc.Log, "metadata");
        var socialLog = new WaveeLogger(svc.Log, "social");
        var notificationsLog = new WaveeLogger(svc.Log, "notifications");
        var syncLog = new WaveeLogger(svc.Log, "sync");
        var dealerLog = new WaveeLogger(svc.Log, "dealer");
        svc.Log.Event(WaveeLogLevel.Info, "connect", "session.start", "Live session bootstrap starting",
            operationId: op,
            fields:
            [
                WaveeLogField.Of("interactive", interactive),
                WaveeLogField.Of("browser", useBrowser),
                WaveeLogField.Of("quiet", quietPhases),
            ]);

        // Silent resume with NO stored credential → Welcome, never the Error card (a null login is ambiguous between "no
        // credential" and "handshake failed"; this pre-check disambiguates the common first-run path).
        if (!interactive && !SpotifyLiveLogin.HasStoredCredential())
        {
            svc.Log.Event(WaveeLogLevel.Info, "auth", "silent.no_credential", "Silent resume skipped; no stored credential",
                operationId: op);
            report.Report(new LoginSnapshot(LoginPhase.LoggedOut));
            return null;
        }
        // quietPhases: a racing sibling (the browser button alongside the device code) stays silent on the intermediate
        // states so it can't replace the two-pane — it surfaces only Finalizing/Authenticated/PremiumRequired on success.
        if (!quietPhases) report.Report(new LoginSnapshot(!interactive ? LoginPhase.SilentResume : useBrowser ? LoginPhase.AwaitingBrowser : LoginPhase.RequestingCode));

        var live = await SpotifyLiveSpclient.ConnectAsync(connectLog, ct, retainApChannel: true,
            allowDeviceCode: interactive && !useBrowser, authObserver: adapter,
            onCredentialAcquired: () => report.Report(new LoginSnapshot(LoginPhase.Finalizing)),
            allowBrowser: interactive && useBrowser).ConfigureAwait(false);
        if (live is null)
        {
            if (ct.IsCancellationRequested || quietPhases) return null;   // superseded / cancelled / a quiet racing sibling → stay silent
            // Welcome on a silent miss (no / rejected-and-cleared credential); Failed/Expired on a genuine error or lapsed code.
            svc.Log.Event(WaveeLogLevel.Warning, "connect", "session.login_failed", "Live login did not produce a session",
                operationId: op,
                fields: [WaveeLogField.Of("storedCredential", SpotifyLiveLogin.HasStoredCredential())]);
            report.Report(adapter.Terminal(credExisted: SpotifyLiveLogin.HasStoredCredential()));
            return null;
        }

        // Premium gate IN-APP (replaces the pre-window MessageBox): refuse a Free account here, and wipe the reusable blob
        // LoginAsync already persisted so the next launch can't silent-resume straight back into the wall.
        if (live.Session.Tier != Tier.Premium)
        {
            svc.Log.Event(WaveeLogLevel.Warning, "auth", "premium.required", "Signed-in account is not Premium",
                operationId: op,
                fields: [WaveeLogField.Of("tier", live.Session.Tier.ToString())]);
            live.CredStore?.Clear();
            report.Report(new LoginSnapshot(LoginPhase.PremiumRequired));
            live.ApChannel?.Dispose();
            return null;
        }

        var dealerJson = await SharedHttp.Client.GetStringAsync("https://apresolve.spotify.com/?type=dealer", ct).ConfigureAwait(false);
        var dealerHosts = ApResolver.ParseHosts(dealerJson, "dealer");
        if (dealerHosts.Count > 0)
            svc.Log.Event(WaveeLogLevel.Info, "dealer", "hosts.resolved", "Dealer access points resolved",
                operationId: op,
                fields: [WaveeLogField.Of("count", dealerHosts.Count), WaveeLogField.Of("first", dealerHosts[0])]);
        if (dealerHosts.Count == 0) { connectLog.Warn("no dealer host — live session not started"); if (!ct.IsCancellationRequested) report.Report(adapter.Terminal(credExisted: true)); live.ApChannel?.Dispose(); return null; }

        // The transport's token provider RE-MINTS on reconnect/expiry (not a captured constant). The WHOLE dealer host
        // list is passed (failover across hosts), and a Connectivity signal is driven by the socket lifecycle so a drop
        // shows in the UI as "Reconnecting…" (not silent stale playback) — surfaced via svc.Connectivity on go-live.
        var connectivity = new Connectivity();
        var transport = new LiveDealerTransport(dealerHosts, live.TokenProvider, live.Pipeline, () => live.BaseUrl, dealerLog, connectivity,
            forceRefreshToken: live.ForceTokenProvider);   // G6 — force-mint after a failed wss handshake

        // Context resolution (inbound Connect play + UI play) needs the metadata stack to hydrate the resolved order, so
        // build it up front — over the SAME store the catalog reads — and hand the controller a unified context resolver.
        // (extendedMetadata + metadata are reused below for the on-open fetcher + now-playing enrichment → one cache.)
        Wavee.Backend.Metadata.ExtendedMetadataSource? extendedMetadata = null;
        Wavee.Backend.Metadata.ExtensionEtagCache? extensionCache = null;
        Wavee.Backend.Metadata.MetadataService? metadata = null;
        IContextResolver? contexts = null;
        if (svc.RealStore is { } mdStore)
        {
            extendedMetadata = new Wavee.Backend.Metadata.ExtendedMetadataSource(live.Pipeline, () => live.BaseUrl, () => live.Session);
            extensionCache = new Wavee.Backend.Metadata.ExtensionEtagCache(extendedMetadata, () => live.Session, connectLog);
            metadata = new Wavee.Backend.Metadata.MetadataService(extendedMetadata, mdStore, () => live.Session, extensionCache: extensionCache);
            contexts = new LiveContextResolver(transport, metadata, mdStore, () => live.Session, connectLog);
        }

        // Local audio (Stage H): wire the in-process decode/output stack when extended metadata can resolve file IDs.
        // PlayPlay is optional and supplied by the ignored Wavee.PlayPlay project when present.
        // Dedicated "audio" log category — persisted Info+ to wavee.log (WaveeLog special-cases it) so the whole
        // fetch→key→derive→decrypt pipeline is tailable/diagnosable in a windowed/AOT build with no console.
        var audioLog = new WaveeLogger(svc.Log, "audio");
        AudioPlaybackStack? audio = extendedMetadata is not null
            ? new AudioPlaybackStack(transport, live.Pipeline, () => live.ApChannel, () => live.Session, extendedMetadata, svc.Settings, audioLog)
            : null;
        audioLog.Info(audio is not null
            ? "local-audio stack active in-process (file IDs via extended-metadata TRACK_V4/AUDIO_FILES)"
            : "local-audio stack OFF - no metadata store; playback stays remote-only");
        svc.Log.Event(WaveeLogLevel.Info, "audio", "stack.state", audio is not null ? "Local audio stack active" : "Local audio stack off",
            operationId: op,
            fields: [WaveeLogField.Of("active", audio is not null), WaveeLogField.Of("metadata", extendedMetadata is not null)]);
        // Remember-volume: seed the device's announced/local volume from the persisted setting (0.7 default when off).
        double initialVolume = svc.Settings.Get(WaveeSettings.RememberVolume)
            ? Math.Clamp(svc.Settings.Get(WaveeSettings.SavedVolume), 0f, 1f) : 0.7;
        var connect = new LiveConnect(transport, live.DeviceId, live.ApChannel, contexts, log: connectLog, audio: audio,
            initialVolume01: initialVolume, refreshTokens: live.TokenProvider);
        connect.Controller.AutoplayEnabled = () => svc.Settings.Get(WaveeSettings.AutoplayEnabled);
        transport.Start();
        // Profile (name + avatar) fetched before go-live so CurrentUser is complete on the first render (no refresh hook).
        // Best-effort — a failure just omits that field.
        var (displayName, avatarUrl, profileFetched) = await FetchProfileAsync(live.Pipeline, live.BaseUrl, live.Username, ct).ConfigureAwait(false);
        var liveSession = new LiveSpotifySession(live.Username, displayName, avatarUrl, live.Session.Tier == Tier.Premium);

        // Owned CTS — INDEPENDENT of the bootstrap ct (a racing-sibling cancel must not kill hydration); cancelled on logout.
        var cts = new CancellationTokenSource();
        var host = new LiveSessionHost(transport, connect, cts);
        audio?.StartProvisioning(cts.Token);   // background PlayPlay pack provision — off the play path, owned CTS

        if (audio is not null)
        {
            svc.PlayPlayProvisioner = audio.Provisioner;
            svc.AudioBodyCache = audio.BodyDiskCache;
            svc.AudioLicenseCache = audio.LicenseDiskCache;
            if (audio.BodyDiskCache is not null)
                svc.Residency.Register(3, "audio-body-disk", () => audio.BodyDiskCache.Trim());
            void PushRuntime() => svc.Playback.UpdateRuntimeStatus(audio.Provisioner.GetSnapshot(), uiPost);
            audio.Status.Changed += () => PushRuntime();
            PushRuntime();
        }

        // Supersede check: a newer login cancels THIS bootstrap's ct. Bail (disposing what we built) so a stale flow can't
        // AttachLive/GoLive over the winner. No await between here and GoLive → effectively atomic. AttachLive runs BEFORE
        // GoLive so a logout fired in the go-live window still tears the host down (not a no-op).
        if (ct.IsCancellationRequested) { await host.DisposeAsync().ConfigureAwait(false); return null; }
        svc.AttachLive(host, live.CredStore!);
        svc.LiveHttp = live.Pipeline;
        // Point the switchable mutation transport at the live dealer BEFORE go-live so a write in the go-live window networks;
        // set the real username into the ambient session so write bodies carry a valid account (§3).
        svc.MutTransport?.SetInner(transport);
        if (svc.RealSessionHost is { } sh) sh.Set(sh.Current with { Account = live.Username });
        var lyrics = BuildLiveLyrics(() => live.BaseUrl, connect.Controller, live.TokenProvider, () => connect.Projection?.TrackResolver);
        // Local (silent) playback is unsupported: any play that routes to THIS device shows the standard "choose a remote
        // device" toast instead of pretending to play. The hook can fire from a dealer thread — NotifyLocalPlaybackUnsupported
        // posts to the UI thread. (The --connect-live CLI demo never Activates the bridge, so the notify no-ops there.)
        // Reject local playback ONLY when there's no local-audio stack (remote-only). With the stack wired, a play routed
        // to THIS device actually decodes/outputs in process instead of showing the "choose a remote device" toast.
        if (audio is null)
            connect.Controller.OnLocalPlaybackRejected = () => svc.Playback.NotifyLocalPlaybackUnsupported();
        // A failing transfer / play to the active remote device surfaces as a toast (was silent) — so "switching doesn't work"
        // shows a reason instead of nothing. The controller also logs the HTTP status (grep "outbound transfer"/"outbound play").
        connect.Controller.OnRemoteCommandFailed = () => svc.Playback.NotifyRemoteCommandFailed();
        // A LOCAL play that fails (key/CDN/decode/provisioning) surfaces a typed toast + player-bar error with a Retry that
        // re-provisions the pack (if needed) and replays the current track — instead of a silently-dropped fire-and-forget.
        connect.Controller.OnPlaybackError = e =>
        {
            svc.Log.Event(WaveeLogLevel.Error, "audio", "playback.failed", "Local playback failed",
                operationId: op,
                fields:
                [
                    WaveeLogField.Of("reason", e.Reason.ToString()),
                    WaveeLogField.Of("detail", e.Detail ?? ""),
                ]);   // the structured Event above reaches ring + file (no plain-text duplicate)
            // When the failure is "no local runtime" (nothing to retry into), route the toast action to the one-click
            // SETUP flow instead of a Retry that would just replay and fail again. Also surface the persistent banner by
            // pushing the RuntimeUnavailable status (so the offer isn't a one-shot toast the user can miss).
            bool needsSetup = e.Reason is AudioKeyFailureReason.NeverProvisioned
                or AudioKeyFailureReason.ProvisioningUnavailable
                or AudioKeyFailureReason.ArchUnsupported;
            if (needsSetup && audio is not null)
            {
                var snap = audio.Provisioner.GetSnapshot();
                if (snap.Outcome is ProvisioningOutcome.Ready or ProvisioningOutcome.NeverAttempted)
                    snap = new PlaybackRuntimeStatus(ProvisioningOutcome.RuntimeUnavailable);
                svc.Playback.UpdateRuntimeStatus(snap, uiPost);
                svc.Settings.Set(WaveeSettings.PlaybackRuntimeSetupDismissed, false);   // re-offer after an explicit play attempt
                svc.Playback.NotifyPlaybackError(e.UserMessage, Loc.Get(Strings.Playback.Runtime.SetUp),
                    () => svc.Playback.OpenPlaybackRuntimeSetup.Value++);
            }
            else
            {
                svc.Playback.NotifyPlaybackError(e.UserMessage, "Retry",
                    () => { audio?.StartProvisioning(cts.Token); _ = connect.Controller.RetryCurrentAsync(); });
            }
        };
        // Output-device control + local-output picker (Phase A/B/C). Only when the audio stack is wired (local playback is
        // real): seed the persisted output BEFORE first play, surface device notices as toasts, reflect Windows session
        // volume/mute, and stand up the main-process picker service (its own device monitor, separate from the child's).
        if (audio is not null && audio.Host is IAudioOutputDeviceControl odc)
        {
            var persistedId = svc.Settings.Get(WaveeSettings.OutputDeviceId);
            string? seedId = string.IsNullOrEmpty(persistedId) ? null : persistedId;
            odc.SetOutputDevice(seedId);   // seed the selected endpoint before the first play (Hello carries it OOP)
            odc.OutputDeviceNotice += n => svc.Playback.NotifyOutputDeviceNotice(n);
            odc.ExternalVolumeChanged += (v, muted) =>
            {
                connect.Controller.OnExternalVolumeChanged(v);
                svc.Playback.NotifyOutputMuted(muted);
            };
            var localOutputs = new LocalAudioDeviceService(
                new Wavee.SpotifyLive.Audio.WasapiAudioDeviceMonitor(audioLog),
                odc,
                (id, ct) => connect.Controller.TransferToAsync(id, ct),
                live.DeviceId,
                () => connect.Controller.State.ActiveDeviceId,
                (id, name) =>
                {
                    svc.Settings.Set(WaveeSettings.OutputDeviceId, id ?? "");
                    svc.Settings.Set(WaveeSettings.OutputDeviceName, name ?? "");
                },
                seedId);
            svc.Playback.AttachLocalOutputs(localOutputs);
            localOutputs.Activate(uiPost);
        }
        // The picker's local rows are truthful/enabled iff local playback is actually supported (an audio stack exists) —
        // fixes the stale unconditional "Unavailable" (OnLocalPlaybackRejected is only wired when audio is null).
        uiPost(() => svc.Playback.LocalPlaybackSupported.Value = audio is not null);
        svc.GoLive(connect.Controller, connect.Devices, liveSession, connectivity, lyrics);
        // Diagnostic one-shot: WAVEE_PLAYPLAY_PROBE=1 (or a file-id hex) fetches that file's PlayPlay obf+aes on the LIVE
        // session and compares to the reference ogg-vorbis-160 golden vector — isolates "is our live obf the vector's value".
        if (audio is not null && Environment.GetEnvironmentVariable("WAVEE_PLAYPLAY_PROBE") is { Length: > 0 } probe)
            _ = ProbePlayPlayAsync(audio, probe, audioLog, cts.Token);
        // Diagnostic one-shot: WAVEE_AUDIO_FORMAT_PROBE=1 plus WAVEE_AUDIO_FORMAT_PROBE_TRACK=<track-uri-or-base62>
        // resolves exactly one track and lets AudioFormatProbe log every exposed audio candidate, CDN prefix, preview MP3,
        // and music-video DRM manifest without requiring a UI play action.
        if (audio is not null && Environment.GetEnvironmentVariable("WAVEE_AUDIO_FORMAT_PROBE_TRACK") is { Length: > 0 } formatProbe)
            _ = ProbeAudioFormatsAsync(audio, formatProbe, audioLog, cts.Token);
        report.Report(new LoginSnapshot(LoginPhase.Authenticated, User: liveSession.CurrentUser));
        if (audio is not null && !svc.Settings.Get(WaveeSettings.PlaybackRuntimeSetupDismissed))
        {
            var snap = audio.Provisioner.GetSnapshot();
            if (snap.Outcome == ProvisioningOutcome.RuntimeUnavailable)
            {
                void ShowSetupToast() => Toasts.Show(
                    Loc.Get(Strings.Playback.Runtime.Missing),
                    ToastSeverity.Caution,
                    Loc.Get(Strings.Playback.Runtime.SetUp),
                    () => svc.Playback.OpenPlaybackRuntimeSetup.Value++);
                if (uiPost is { } post) post(ShowSetupToast);
            }
        }
        log.Info("Live Connect session active — Wavee is a controllable device, mirrors now-playing, and shows the live account.");

        // Live data wiring into the SAME store the catalog reads (InMemoryStore is lock-guarded → safe off-thread):
        if (svc.RealStore is { } store && metadata is { } md && extendedMetadata is { } em)
        {
            // (a) fetch playlist/album TRACKS the first time a detail page opens (the sync stored headers only). The real
            //     hydrator (MetadataService over the extended-metadata batch) replaces the no-op that left lists empty.
            //     em + md were built above for the context resolver — reuse them so the whole session shares one cache.
            var fetcher = new PlaylistFetcher(live.Pipeline, () => live.BaseUrl, store, (uris, c) => md.SyncAllAsync(uris, c), () => live.Username);

            // The single library-sync writer loop (RC1): the collection fetcher (revision get/set → the SQLite cold tier,
            // mark-and-sweep shielded by the mutation outbox), the loop itself, and the dealer router that decode-and-enqueues
            // into it. The DealerRouter no longer writes the store — the in-place apply / mark-dirty / refetch policy is the loop's.
            var cold = svc.RealCold!;
            var collections = new Wavee.Backend.Collections.CollectionFetcher(live.Pipeline, () => live.BaseUrl, () => live.Username, store,
                s => cold.GetCollectionRevision(s),
                (s, r) => cold.SetCollectionRevision(s, r, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                (uris, c) => md.SyncAllAsync(uris, c),
                (s, u) => svc.RealMutations!.HasPending(s, u));
            var sync = new Wavee.Backend.Sync.LibrarySync(store, fetcher, collections, svc.RealMutations!, svc.MutTransport!,
                () => svc.RealSessionHost!.Current, () => live.Username, syncLog, cts.Token, svc.EchoRing);
            var router = new Wavee.Backend.Realtime.DealerRouter(transport, sync);
            svc.RealSync = sync;
            sync.Enqueue(new Wavee.Backend.Sync.SyncCommand(Wavee.Backend.Sync.SyncKind.DrainWrites));      // replay writes queued while logged out
            sync.Enqueue(new Wavee.Backend.Sync.SyncCommand(Wavee.Backend.Sync.SyncKind.InitialHydrate));
            // Reconnect resync (§6.2): on a transition back to Online from a drop, run the ordered convergence pass —
            // drain the outbox FIRST (a like made during the gap sends), then rootlist + token-gated deltas + /diff for
            // the open/dirty resident playlists. Pushes during the gap died with the socket; this pass is the recovery.
            var prevStatus = connectivity.Status;
            IDisposable connSub = connectivity.StatusChanged.Subscribe(Observers.From<Wavee.Core.ConnectionStatus>(s =>
            {
                var prev = prevStatus; prevStatus = s;
                if (s == Wavee.Core.ConnectionStatus.Online && (prev == Wavee.Core.ConnectionStatus.Reconnecting || prev == Wavee.Core.ConnectionStatus.Offline))
                    sync.Enqueue(new Wavee.Backend.Sync.SyncCommand(Wavee.Backend.Sync.SyncKind.ReconnectResync));
            }));
            host.AttachSync(router, sync, connSub);
            // Post-write drains route through the loop (§6 hardening): replay/reconcile serializes with inbound diffs
            // instead of racing them from the caller's thread. GoOffline resets this to inline-drain.
            if (svc.RealMutationSource is { } mutSrc) mutSrc.ScheduleDrain = () => sync.Enqueue(new Wavee.Backend.Sync.SyncCommand(Wavee.Backend.Sync.SyncKind.DrainWrites));
            if (svc.RealSpclientBaseUrl is { } baseUrl) baseUrl.Value = live.BaseUrl;
            if (svc.RealPlaylistMutations is { } pmSrc)
            {
                pmSrc.SetHttp(live.Pipeline);
                pmSrc.ScheduleDrain = ct => sync.DrainWritesAsync(ct);
            }

            // Pathfinder (GraphQL) for rich catalog reads with no protobuf equivalent — the artist overview, on open.
            var pathfinderExchange = new HttpPipeline(
                new HttpClientExchange(HttpPools.Get(HttpPool.ControlPlane)),
                new AuthMiddleware((force, c) => force && live.ForceTokenProvider is { } refresh
                    ? refresh(c)
                    : live.TokenProvider(c)),
                new RateLimitMiddleware(),
                new PathfinderHeadersMiddleware(_ => Task.FromResult(live.ClientToken)));
            var pathfinder = new PathfinderClient(pathfinderExchange, spclientLog);
            var pathfinderResource = new PathfinderResource(pathfinder, () => live.Session, spclientLog);
            // Playlist page tint parity with albums: albums carry cover colors inline (getAlbum); playlists come over the
            // Mercury proto with none, so resolve them via fetchExtractedColors on the cover, cached persistently (colors
            // are immutable per image → ~half-year), and merged into the resident header.
            var playlistPalette = new PlaylistPaletteEnricher(pathfinderResource, store, new ExtractedColorCache(), spclientLog);
            var homeCache = new LiveHomeCache(pathfinderResource);
            // "What's New" feed (queryWhatsNewFeed) — display-only, rides the PathfinderResource TTL. Seeded now so the
            // notification bell badge is correct before the first open; installed into the switchable the panel binds to.
            var whatsNew = new SpotifyWhatsNewService(pathfinderResource, notificationsLog);
            svc.WhatsNew.SetInner(whatsNew);
            host.AttachWhatsNew(whatsNew);
            whatsNew.EnsureFresh();
            // Below-the-fold album enrichment (about-artist / merch / similar via Pathfinder; recommended playlists via the
            // SAME extended-metadata source, kinds 151→205) — installed into the switchable service the album pages hold.
            svc.AlbumEnrichment.SetInner(new SpotifyAlbumEnrichmentService(pathfinderResource, em, store, metadataLog, extensionCache));
            // Music-video detection + the video↔audio file-id map over the SAME extended-metadata source (etag-cached).
            svc.Video.SetInner(new SpotifyVideoService(em, store, metadataLog, extensionCache));
            var userProfiles = new SpotifyUserProfileService(em, live.Pipeline, () => live.BaseUrl, socialLog, extensionCache);
            if (profileFetched)
                userProfiles.Seed(live.Username, new Owner(
                    UserProfileIds.BareId(UserProfileIds.Normalize(live.Username) ?? live.Username),
                    displayName,
                    avatarUrl is { Length: > 0 } ? new Image(avatarUrl) : null));
            svc.UserProfiles.SetInner(userProfiles);
            // Let the player bar reflect the now-playing track's (async-detected) video via the store change stream.
            svc.Playback.AttachStore(store);
            if (svc.RealLibrarySource is { } libSrc)
            {
                libSrc.Sync = sync;   // on-open SWR: playlists route through the loop (blocking first fetch / background revalidate)
                libSrc.EnsurePlaylistPalette = playlistPalette.EnsureAsync;
                libSrc.OnDemandFetch = async (uri, c) =>
                {
                    if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
                    {
                        await fetcher.FetchPlaylistAsync(uri, c).ConfigureAwait(false);
                    }
                    else if (uri.StartsWith("spotify:album:", StringComparison.Ordinal)) await FetchAlbumAsync(pathfinderResource, store, uri, c).ConfigureAwait(false);
                    else if (uri.StartsWith("spotify:artist:", StringComparison.Ordinal)) await FetchArtistAsync(pathfinderResource, store, uri, c).ConfigureAwait(false);
                    // Detect music videos for the just-hydrated tracklist (batch, off the critical path → the movie icons fill in).
                    DetectContainerVideos(svc.Video, store, uri, c);
                };
                libSrc.LiveHomeFetch = c => homeCache.GetAsync(c);   // cached editorial home + separately refreshed recents
                libSrc.LiveSearch = (q, facet, offset, limit, c) => FetchSearchAsync(pathfinder, q, facet, offset, limit, c);   // paged online search
                libSrc.LiveDiscography = (uri, kind, off, lim, c) => FetchDiscographyPageAsync(pathfinder, uri, kind, off, lim, c);   // paged artist discography
                libSrc.LiveSuggest = async (q, c) => (await FetchSuggestRichAsync(pathfinder, q, c).ConfigureAwait(false)).Queries;   // omnibar as-you-type suggestions
                libSrc.LiveSuggestRich = (q, c) => FetchSuggestRichAsync(pathfinder, q, c);
            }

            // Now-playing enrichment: the cluster's player_state metadata is thin (often no artist / no album art), so
            // resolve the full track by uri over the extended-metadata transport + fold artist/album/art into the bar.
            connect.Projection.TrackResolver = async (uri, c) =>
            {
                if (!uri.StartsWith("spotify:track:", StringComparison.Ordinal)) return null;
                _ = svc.Video.GetAsync(uri, c);   // warm the current track's video↔audio mapping (best-effort, fire-and-forget)
                return await ResolveNowPlayingTrackAsync(uri, md, pathfinderResource, store, c).ConfigureAwait(false);
            };

            // (b) hydrate playlist HEADERS (name/cover) so the home + sidebar show names; for cover-less playlists also
            //     pull the tracklist so they render a 2×2 album mosaic.
            _ = Task.Run(() => HydratePlaylistHeadersAsync(fetcher, store, syncLog, cts.Token));
        }

        // Friend-activity (presence) feed — session-scoped, display-only (never touches the Store). Seeds on the dealer
        // connection id + applies hm://presence2/user/ deltas; installed into the switchable service the friends panel
        // binds to (go-live → live provider; logout → back to the Null service via GoOffline).
        var friends = new SpotifyFriendActivityService(transport, live.Pipeline, () => live.BaseUrl,
            connect.ConnectionId, () => connect.CurrentConnectionId, socialLog);
        svc.Friends.SetInner(friends);
        host.AttachFriends(friends);

        // Social notifications (gander) — session-scoped, display-only. One authed GET; seeds itself at construction so the
        // bell badge is right before the first open. Installed into the switchable the notification panel binds to.
        var notifications = new SpotifyNotificationsService(live.Pipeline, () => live.BaseUrl, notificationsLog);
        svc.SpotifyNotifications.SetInner(notifications);
        host.AttachNotifications(notifications);

        return host;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();           // stop background hydration / in-flight fetches before tearing the transport down
        _connSub?.Dispose();     // stop reconnect-resync triggers
        _friends?.Dispose();     // stop presence seed/deltas + watchdog
        _notifications?.Dispose();   // stop the gander in-flight fetch
        _whatsNew?.Dispose();        // stop the what's-new in-flight fetch
        _router?.Dispose();      // stop decoding pushes
        if (_sync is not null) await _sync.DisposeAsync().ConfigureAwait(false);   // drain the loop to a stop before the transport
        _connect.Dispose();
        _transport.Dispose();
        _cts.Dispose();
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

    // Hydrate each rootlist playlist's HEADER (name/cover) — fast, coalesced into one refresh — so the home + sidebar show
    // names on cold start. The mosaic-tracklist half was RETIRED (§3): LibrarySync.InitialHydrate is now the authoritative
    // rootlist consumer, and a cover-less playlist's mosaic derives from its tracklist which lands on first OPEN (the on-open
    // SWR path) rather than eagerly pulling every playlist's tracks here (the herd this design avoids).
    static async Task HydratePlaylistHeadersAsync(PlaylistFetcher fetcher, IStore store, WaveeLogger log, CancellationToken ct)
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
            if (headers > 0) log.Info($"hydrated {headers} playlist headers (home + sidebar names)");
        }
        catch (Exception ex) { log.Info("playlist hydration: " + ex.Message); }
    }

    // Diagnostic one-shot (WAVEE_PLAYPLAY_PROBE): fetch a file's PlayPlay obf+aes on the LIVE session and compare to the
    // reference ogg-vorbis-160 golden vector. Confirms whether the obf Spotify returns for OUR (bumped-version) request is
    // the vector's value — i.e. whether the existing 1.2.88.483 emulator derives the right key on a non-403 request.
    static readonly (string File, string HarObf)[] PlayPlayHarVectors =
    [
        ("5989137781b15a3275f8e312bceb096b7ef8f0a0", "4cc24d16068d90fe18c4e2e2cd2691d0"),
        ("1e90abc9cde41338a87c8da5be203218ac84a82c", "a7545790cfe4cae70dd5f51712df35a8"),
    ];

    static async Task ProbePlayPlayAsync(Wavee.SpotifyLive.Audio.AudioPlaybackStack audio, string probe, WaveeLogger log, CancellationToken ct)
    {
        try
        {
            if (audio.RuntimeAsset is null)
            {
                for (int i = 0; i < 10 && audio.RuntimeAsset is null && !ct.IsCancellationRequested; i++)
                    await Task.Delay(200, ct).ConfigureAwait(false);
            }
            if (audio.RuntimeAsset is null) { log.Info("PROBE: runtime not ready"); return; }

            IEnumerable<string> files = probe is "har" or "all" or "1" or "true"
                ? PlayPlayHarVectors.Select(h => h.File)
                : [probe.Trim().ToLowerInvariant()];

            foreach (var fileHex in files)
            {
                log.Info($"PROBE: full PlayPlay path for {fileHex}");
                var key = await audio.KeyResolver.GetKeyAsync(Convert.FromHexString(fileHex), new byte[16], ct).ConfigureAwait(false);
                log.Info($"PROBE RESULT {fileHex[..8]}...: aes={key.Length}B redacted");
            }
        }
        catch (Exception ex) { log.Info("PROBE failed: " + ex.Message); }
    }

    static async Task ProbeAudioFormatsAsync(AudioPlaybackStack audio, string probe, WaveeLogger log, CancellationToken ct)
    {
        try
        {
            var uri = probe.Trim();
            if (uri.Length == 22 && !uri.StartsWith("spotify:", StringComparison.Ordinal))
                uri = "spotify:track:" + uri;
            if (!uri.StartsWith("spotify:track:", StringComparison.Ordinal))
            {
                log.Info("AUDIO FORMAT PROBE: invalid track probe '" + probe + "' (expected spotify:track:<id> or 22-char id)");
                return;
            }

            var id = uri["spotify:track:".Length..];
            var track = new Track(
                id, uri, "probe",
                Array.Empty<ArtistRef>(),
                new AlbumRef("", "", ""),
                0, false, null);

            log.Info("AUDIO FORMAT PROBE: resolving " + uri);
            await audio.TrackResolver.ResolveMetaAsync(track, ct).ConfigureAwait(false);
            log.Info("AUDIO FORMAT PROBE: metadata resolved for " + uri + "; waiting for background probe logs");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { log.Info("AUDIO FORMAT PROBE failed: " + ex.Message); }
    }

    // After a container's tracklist hydrates, batch-detect which of its tracks have a music video (fills the row indicator).
    // Fire-and-forget off the open path — best-effort, etag-cached, and a no-op when the container has no resident tracks yet.
    static void DetectContainerVideos(IVideoService video, IStore store, string uri, CancellationToken ct)
    {
        List<string>? uris = null;
        if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
        {
            var m = store.Membership(uri);
            if (m.Count > 0)
            {
                uris = new List<string>(m.Count);
                foreach (var r in m) if (r.ItemUri.StartsWith("spotify:track:", StringComparison.Ordinal)) uris.Add(r.ItemUri);
            }
        }
        else if (uri.StartsWith("spotify:album:", StringComparison.Ordinal))
        {
            if (store.GetAlbum(uri)?.Tracks is { Count: > 0 } tracks)
            {
                uris = new List<string>(tracks.Count);
                foreach (var t in tracks) uris.Add(t.Uri);
            }
        }
        if (uris is not { Count: > 0 }) return;
        var list = uris;
        _ = Task.Run(async () => { try { await video.DetectAsync(list, ct).ConfigureAwait(false); } catch { } }, ct);
    }

    // Fetch the rich artist overview via Pathfinder GraphQL → map (the export's artist-*.json IS this shape) → store.
    // Best-effort: a stale persisted-query hash or error leaves the identity-only artist in place.
    static async Task FetchArtistAsync(PathfinderResource pf, IStore store, string uri, CancellationToken ct)
    {
        using var doc = await pf.QueryAsync(PathfinderOps.QueryArtistOverview, PathfinderOps.QueryArtistOverviewHash,
            w => { w.WriteString("uri", uri); w.WriteString("locale", ""); w.WriteBoolean("preReleaseV2", false); },
            PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
        if (doc is null) return;
        if (Wavee.Core.SpotifyExportMapper.ArtistFromOverview(doc.RootElement) is { Uri.Length: > 0 } artist)
            store.UpsertArtist(artist with { FetchedAt = DateTimeOffset.UtcNow });   // stamp SWR freshness on the full overview
    }

    // One window of an artist's discography facet. The three facet ops share one persisted hash; the server keys on the
    // operationName. Variables mirror the desktop client: uri/offset/limit + order DATE_DESC (no preReleaseV2 — that's
    // overview-only). null doc (HTTP/stale-hash/network) → null, so the source clears the VC page guard and retries.
    static async Task<DiscographyPage?> FetchDiscographyPageAsync(PathfinderClient pf, string artistUri, DiscographyKind kind, int offset, int limit, CancellationToken ct)
    {
        var op = kind switch
        {
            DiscographyKind.Singles => PathfinderOps.QueryArtistDiscographySingles,
            DiscographyKind.Compilations => PathfinderOps.QueryArtistDiscographyCompilations,
            _ => PathfinderOps.QueryArtistDiscographyAlbums,
        };
        using var doc = await pf.QueryAsync(op, PathfinderOps.QueryArtistDiscographyHash, w =>
        {
            w.WriteString("uri", artistUri);
            w.WriteNumber("offset", offset);
            w.WriteNumber("limit", limit);
            w.WriteString("order", "DATE_DESC");
        }, PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
        if (doc is null) return null;
        return Wavee.Core.SpotifyExportMapper.DiscographyPageFromResponse(doc.RootElement, kind);
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
        Func<string> baseUrl, IPlaybackPlayer controller, Func<CancellationToken, Task<string>> token,
        Func<Func<string, CancellationToken, Task<Track?>>?> trackResolver)
    {
        var http = new Wavee.Backend.Lyrics.SharedHttpLyricFetch();

        // Spotify color-lyrics auth — the proven WaveeMusic SpClient.GetLyricsAsync recipe: a raw bearer GET with
        // app-platform=ANDROID + spotify-app-version. The ANDROID platform is what lets the lyrics CDN serve WITHOUT a
        // client-token; WebPlayer/desktop platforms require a client-token and 403 without one. We must NOT route through
        // the shared spclient pipeline (it force-stamps App-Platform=Win32_x86_64 in ClientTokenMiddleware). The bearer is
        // the refreshing TokenProvider (survives the ~1h access-token expiry), so lyrics keep loading deep into a session.
        async Task<string?> SpotifyGet(string url, CancellationToken c)
        {
            try
            {
                string tok = await token(c).ConfigureAwait(false);
                if (string.IsNullOrEmpty(tok)) { Wavee.Backend.Lyrics.LyricsProbe.Note("spotify", "no access token (bearer refresh empty)"); return null; }
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tok);
                req.Headers.TryAddWithoutValidation("app-platform", "Android");
                req.Headers.TryAddWithoutValidation("spotify-app-version", SpotifyClientIdentity.AppVersionHeader);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var resp = await Wavee.Backend.Spotify.SharedHttp.Client.SendAsync(req, c).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) { Wavee.Backend.Lyrics.LyricsProbe.Note("spotify", $"color-lyrics HTTP {(int)resp.StatusCode}"); return null; }
                return await resp.Content.ReadAsStringAsync(c).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Wavee.Backend.Lyrics.LyricsProbe.Note("spotify", $"color-lyrics error: {e.GetType().Name}"); return null; }
        }

        var sources = new System.Collections.Generic.List<Wavee.Backend.Lyrics.ILyricCandidateSource>
        {
            new Wavee.Backend.Lyrics.Sources.AmllTtmlDbSource(http),
            new Wavee.Backend.Lyrics.Sources.SpotifyNativeLyricsSource(SpotifyGet, baseUrl),
            new Wavee.Backend.Lyrics.Sources.LrcLibSource(http),
        };
        // Grey providers (docs plan §6) — ENABLED: widen word/syllable coverage beyond AMLL with the reverse-engineered
        // CJK APIs (QQ QRC, NetEase YRC, Kugou KRC) + Musixmatch richsync. The reranker still validates each against the
        // Spotify reference, so a wrong/ mistimed grey candidate can't win.
        var opt = Wavee.Backend.Lyrics.LyricsOptions.Default with
        {
            EnableGreyProviders = true,
            PerSourceTimeoutMs = 30000,
            TotalTimeoutMs = 30000,
            FirstHitGraceMs = 1200,
        };
        if (opt.EnableGreyProviders)
        {
            sources.Add(new Wavee.Backend.Lyrics.Sources.MusixmatchSource());
            sources.Add(new Wavee.Backend.Lyrics.Sources.QqMusicSource());
            sources.Add(new Wavee.Backend.Lyrics.Sources.NeteaseSource());
            sources.Add(new Wavee.Backend.Lyrics.Sources.KugouSource());
        }

        async Task<Wavee.Backend.Lyrics.LyricsRequest?> Resolve(string trackId, CancellationToken c)
        {
            var t = controller.State.CurrentTrack;
            if (t is null || (t.Id != trackId && t.Uri != "spotify:track:" + trackId)) return null;

            string uri = "spotify:track:" + trackId;
            // The cluster's now-playing track is often THIN (no artist / no ISRC) and may not be enriched yet when the
            // lyrics view first asks — so resolve the FULL track ourselves (the same extended-metadata + Pathfinder
            // resolver the player bar uses). This makes the search's artist + ISRC independent of the now-playing
            // enrichment race; otherwise every provider searches title-only (e.g. "fade away" with no artist → no match).
            bool thin = t.Artists.Count == 0 || string.IsNullOrEmpty(t.Artists[0].Name) || string.IsNullOrEmpty(t.Isrc);
            if (thin && trackResolver() is { } resolve)
            {
                try
                {
                    var full = await resolve(uri, c).ConfigureAwait(false);
                    if (full is not null && (full.Uri == uri || full.Id == trackId)) t = full;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* best-effort — fall back to the thin track */ }
            }

            // Skip blank artist names (a thin cluster track carries a single empty ArtistRef) so the request gets [] not [""].
            var artists = new System.Collections.Generic.List<string>(t.Artists.Count);
            foreach (var a in t.Artists) if (!string.IsNullOrEmpty(a.Name)) artists.Add(a.Name);
            return new Wavee.Backend.Lyrics.LyricsRequest(
                trackId, uri, t.Title, artists, t.Album.Name, t.DurationMs,
                Isrc: t.Isrc, Market: "from_token", HasSpotifyLyrics: null);
        }

        return new Wavee.Backend.Lyrics.AggregatingLyricsProvider(
            sources, Resolve, opt, referenceSourceId: "spotify",
            log: new WaveeLogger(WaveeLog.Instance, "lyrics"));
    }

    // The signed-in user's profile (display name + avatar) via spclient user-profile-view — the cluster/login only give
    // the opaque username, so the account chip would otherwise show "31unjf…" with no photo. Best-effort: falls back to
    // the username on any failure. Fetched BEFORE go-live so CurrentUser is correct from the first render (no refresh hook).
    static async Task<(string displayName, string? avatarUrl, bool fetched)> FetchProfileAsync(
        Wavee.Backend.Spotify.IHttpExchange http, string baseUrl, string username, CancellationToken ct)
    {
        try
        {
            var url = baseUrl + "/user-profile-view/v3/profile/" + Uri.EscapeDataString(username) + "?market=from_token";
            var headers = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };
            using var resp = await http.SendAsync(new Wavee.Backend.Spotify.HttpReq("GET", url, headers, null), ct).ConfigureAwait(false);
            if (resp.Status != 200) return (username, null, false);
            using var doc = await JsonDocument.ParseAsync(resp.Body, default, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()! : username;
            string? avatar = root.TryGetProperty("image_url", out var im) && im.ValueKind == JsonValueKind.String && im.GetString() is { Length: > 0 } a
                ? a : null;
            return (name, avatar, true);
        }
        catch { return (username, null, false); }
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
    static async Task<IReadOnlyList<HomeGroup>> FetchHomeAsync(PathfinderResource pf, CancellationToken ct)
    {
        using var doc = await pf.UseQueryAsync(PathfinderOps.Home, PathfinderOps.HomeHash,
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
    static async Task<IReadOnlyList<HomeCard>> FetchRecentsAsync(PathfinderResource pf, CancellationToken ct)
    {
        using var doc = await pf.UseQueryAsync(PathfinderOps.Recents, PathfinderOps.RecentsHash,
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
        readonly PathfinderResource _pf;

        public LiveHomeCache(PathfinderResource pf) => _pf = pf;

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
                new(HomeGroupKind.QuickGrid, "Recently played", recents, SpotifyHomeComposer.GroupAccent(HomeGroupKind.QuickGrid, recents)),
            };
            groups.AddRange(home);
            return groups;
        }

        async Task<IReadOnlyList<HomeGroup>> GetHomeGroupsAsync(CancellationToken ct)
            => await FetchHomeAsync(_pf, ct).ConfigureAwait(false);

        async Task<IReadOnlyList<HomeCard>> GetRecentsAsync(CancellationToken ct)
            => await FetchRecentsAsync(_pf, ct).ConfigureAwait(false);
    }

    static async Task FetchAlbumAsync(PathfinderResource pf, IStore store, string uri, CancellationToken ct)
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
        PathfinderResource pathfinder, IStore store, CancellationToken ct)
    {
        var track = store.GetTrack(uri);
        if (track?.Image is not null && track.Artists.Count > 0) return track;

        await metadata.SyncAllAsync(new[] { uri }, ct).ConfigureAwait(false);
        track = store.GetTrack(uri);
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
    public static async Task<int> RunAsync(WaveeLogger log, CancellationToken ct)
    {
        log.Info("Wavee live Connect probe — building the real backend + going live...");
        var svc = Services.CreateReal();
        await using var host = await StartAsync(svc, log, ct).ConfigureAwait(false);
        if (host is null) { log.Info("Live session could not start."); return 1; }

        using var sub = svc.Player.State.Changes.Subscribe(Observers.From<Wavee.Core.IPlaybackState>(s =>
        {
            if (s.CurrentTrack is { } t)
                log.Info("  bridge now-playing: " + t.Title + " — " + (s.IsPlaying ? "playing" : "paused") + " (active=" + (s.ActiveDeviceId ?? "") + ")");
        }));
        // Observability proof: the realtime (dealer socket) link status — toggle your network to see Reconnecting → Online.
        using var connSub = svc.Connectivity.StatusChanged.Subscribe(Observers.From<Wavee.Core.ConnectionStatus>(
            s => log.Info("  realtime link: " + s)));
        log.Info("  realtime link: " + svc.Connectivity.Status);

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
            log.Info($"  on-open playlist '{full?.Name}' → {full?.Tracks?.Count ?? 0} tracks");
        }
        if (alUri is not null)
        {
            var al = await svc.Library.GetAlbumAsync(alUri, ct).ConfigureAwait(false);
            var t0 = al?.Tracks is { Count: > 0 } tl ? $"{tl[0].Title} ({tl[0].DurationMs}ms)" : "—";
            log.Info($"  on-open album '{al?.Name}' → {al?.Tracks?.Count ?? 0} tracks (first: {t0})");
        }
        if (arUri is not null)
        {
            var ar = await svc.Library.GetArtistAsync(arUri, ct).ConfigureAwait(false);
            log.Info($"  on-open artist '{ar?.Name}' → {ar?.TopTracks?.Count ?? 0} top tracks, {ar?.TopAlbums?.Count ?? 0} releases, {ar?.MonthlyListeners ?? 0} listeners (Pathfinder)");
        }
        var home = await svc.Library.GetHomeAsync(ct).ConfigureAwait(false);
        log.Info($"  home → {home.Groups.Count} groups (editorial Pathfinder + library)");
        var sr = await svc.Library.SearchAsync("paul kim", ct).ConfigureAwait(false);
        log.Info($"  search 'paul kim' → {sr.Tracks.Count} tracks, {sr.Albums.Count} albums, {sr.Artists.Count} artists, {sr.Playlists.Count} playlists");
        var sg = await svc.Library.SuggestAsync("aras", ct).ConfigureAwait(false);
        log.Info($"  suggest 'aras' → {sg.Count}: {string.Join(" | ", System.Linq.Enumerable.Take(sg, 6))}");

        log.Info("SMOKE TEST — Wavee is now a live Connect device. In the next 90s:");
        log.Info("  1) open Spotify on your phone/web → device picker → confirm \"Wavee\" appears;");
        log.Info("  2) transfer to Wavee + play a playlist/album/Liked Songs → watch now-playing + the queue below;");
        log.Info("  3) pause/seek/next/shuffle/repeat from the phone → each logs an inbound 'connect command' + a put-state;");
        log.Info("  4) (optional) toggle airplane mode briefly → watch 'realtime link: Reconnecting' then 'Online'.");
        try { await Task.Delay(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false); } catch { }
        return 0;
    }
}
