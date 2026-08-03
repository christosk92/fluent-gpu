using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;

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
        // No dispatcher (the CLI demo / tests) ⇒ run the posted action inline, the same fallback the bridges use.
        Action<Action> postUi = uiPost ?? (static a => a());
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
            onCredentialAcquired: () => report.Report(new LoginSnapshot(LoginPhase.Finalizing, Step: LoginStep.Connecting)),
            allowBrowser: interactive && useBrowser, language: svc.Locale.SpotifyLanguage).ConfigureAwait(false);
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
        // §G go-live marks: everything from here to `stack.state` is ONE synchronous block on the apresolve continuation —
        // no awaits — so any cost in it lands directly on the login splash. It was 5–27 s in the field and invisible,
        // because the only two log lines bracket the whole region. These per-step marks are permanent regression
        // detectors, not scaffolding: they are what names the next offender without a repro.
        long goLiveStart = Environment.TickCount64;
        report.Report(new LoginSnapshot(LoginPhase.Finalizing, Step: LoginStep.Metadata));

        var connectivity = new Connectivity();
        var transport = new LiveDealerTransport(dealerHosts, live.TokenProvider, live.Pipeline, () => live.BaseUrl, dealerLog, connectivity,
            forceRefreshToken: live.ForceTokenProvider);   // G6 — force-mint after a failed wss handshake
        long transportMs = Environment.TickCount64 - goLiveStart;

        // Context resolution (inbound Connect play + UI play) needs the metadata stack to hydrate the resolved order, so
        // build it up front — over the SAME store the catalog reads — and hand the controller a unified context resolver.
        // (extendedMetadata + metadata are reused below for the on-open fetcher + now-playing enrichment → one cache.)
        Wavee.Backend.Metadata.ExtendedMetadataSource? extendedMetadata = null;
        Wavee.Backend.Metadata.ExtensionEtagCache? extensionCache = null;
        Wavee.Backend.Metadata.MetadataService? metadata = null;
        IContextResolver? contexts = null;
        long extMetaMs = -1, extCacheMs = -1, metadataMs = -1, contextsMs = -1;
        if (svc.RealStore is { } mdStore)
        {
            long t = Environment.TickCount64;
            extendedMetadata = new Wavee.Backend.Metadata.ExtendedMetadataSource(live.Pipeline, () => live.BaseUrl, () => live.Session);
            extMetaMs = Environment.TickCount64 - t; t = Environment.TickCount64;
            // O(1) since the bulk seed was deleted — the cold tier is now point-read per miss (HydrateFromCold).
            extensionCache = new Wavee.Backend.Metadata.ExtensionEtagCache(extendedMetadata, () => live.Session, connectLog,
                persistent: svc.RealCold);
            extCacheMs = Environment.TickCount64 - t; t = Environment.TickCount64;
            metadata = new Wavee.Backend.Metadata.MetadataService(extendedMetadata, mdStore, () => live.Session, extensionCache: extensionCache);
            metadataMs = Environment.TickCount64 - t; t = Environment.TickCount64;
            contexts = new LiveContextResolver(transport, metadata, mdStore, () => live.Session, connectLog);
            contextsMs = Environment.TickCount64 - t;
        }

        // Local audio (Stage H): wire the in-process decode/output stack when extended metadata can resolve file IDs.
        // PlayPlay is optional and supplied by the ignored Wavee.PlayPlay project when present.
        // Dedicated "audio" log category — persisted Info+ to wavee.log (WaveeLog special-cases it) so the whole
        // fetch→key→derive→decrypt pipeline is tailable/diagnosable in a windowed/AOT build with no console.
        var audioLog = new WaveeLogger(svc.Log, "audio");
        report.Report(new LoginSnapshot(LoginPhase.Finalizing, Step: LoginStep.Audio));
        long audioStart = Environment.TickCount64;
        AudioPlaybackStack? audio = extendedMetadata is not null
            ? new AudioPlaybackStack(transport, live.Pipeline, () => live.ApChannel, () => live.Session, extendedMetadata, svc.Settings, audioLog)
            : null;
        long audioMs = Environment.TickCount64 - audioStart;
        audioLog.Info(audio is not null
            ? "local-audio stack active in-process (file IDs via extended-metadata TRACK_V4/AUDIO_FILES)"
            : "local-audio stack OFF - no metadata store; playback stays remote-only");
        svc.Log.Event(WaveeLogLevel.Info, "audio", "stack.state", audio is not null ? "Local audio stack active" : "Local audio stack off",
            operationId: op,
            fields: [WaveeLogField.Of("active", audio is not null), WaveeLogField.Of("metadata", extendedMetadata is not null)]);
        // The go-live block's own budget. `elapsed` here IS the hosts.resolved → stack.state gap that used to have to be
        // reconstructed from two timestamps; the per-step fields say WHICH construction owns it.
        svc.Log.Event(WaveeLogLevel.Info, "connect", "golive.stack", "Go-live stack built",
            operationId: op, elapsedMs: Environment.TickCount64 - goLiveStart, fields:
        [
            WaveeLogField.Of("golive.transport_ms", transportMs),
            WaveeLogField.Of("golive.extmeta_ms", extMetaMs),
            WaveeLogField.Of("golive.extcache_ms", extCacheMs),
            WaveeLogField.Of("golive.metadata_ms", metadataMs),
            WaveeLogField.Of("golive.contexts_ms", contextsMs),
            WaveeLogField.Of("golive.audio_ms", audioMs),
        ]);
        // Remember-volume: seed the device's announced/local volume from the persisted setting (0.7 default when off).
        double initialVolume = svc.Settings.Get(WaveeSettings.RememberVolume)
            ? Math.Clamp(svc.Settings.Get(WaveeSettings.SavedVolume), 0f, 1f) : 0.7;
        var connect = new LiveConnect(transport, live.DeviceId, live.ApChannel, contexts, log: connectLog, audio: audio,
            initialVolume01: initialVolume, refreshTokens: live.TokenProvider);
        connect.Controller.AutoplayEnabled = () => svc.Settings.Get(WaveeSettings.AutoplayEnabled);
        // M0 — "one media, one host, one player": hand the controller the app-level video hooks (the per-track video predicate,
        // the async PopOutVideoSource handoff onto the player-owning FluentVideoMediaHost, the PlayerChanged → surface relay,
        // and the mid-track kind re-evaluation). All of it lives in LiveConnect.WireVideoMedia, wired unconditionally.
        // svc.Playback.ResolveVideoSource is wired later in GoLive — the hooks read it late-bound, at invoke time.
        // The user's local video-override curation rides the same hooks (open-failure recovery + the mp4-authoritative
        // duration); null on a backend built without a store, which leaves every override path unreachable.
        connect.WireVideoMedia(svc.Playback, svc.VideoOverrides);
        transport.Start();
        // Profile (name + avatar) fetched before go-live so CurrentUser is complete on the first render (no refresh hook).
        // Best-effort — a failure just omits that field.
        report.Report(new LoginSnapshot(LoginPhase.Finalizing, Step: LoginStep.Profile));
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
                svc.Playback.NotifyPlaybackError(e.UserMessage, Loc.Get(Strings.Common.Retry),
                    () =>
                    {
                        if (e.Reason != AudioKeyFailureReason.Network) audio?.StartProvisioning(cts.Token);
                        _ = connect.Controller.RetryCurrentAsync();
                    });
            }
        };
        // Output-device control + local-output picker (Phase A/B/C). Only when the audio stack is wired (local playback is
        // real): seed the persisted output BEFORE first play, surface device notices as toasts, reflect Windows session
        // volume/mute, and stand up the main-process picker service (its own device monitor, separate from the child's).
        // Through LiveConnect.OutputDeviceControl, never audio.Host directly: that composite is what also carries mute to the
        // video host, so muting the app keeps a music video muted too (and a video that starts later opens muted).
        if (audio is not null && connect.OutputDeviceControl is { } odc)
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
            // …and let the CONNECT wire name the endpoint we render to (desktop publishes it on every PutState).
            connect.AudioOutputDeviceName = localOutputs.CurrentOutputDeviceName;
            localOutputs.Activate(postUi);
        }
        // The picker's local rows are truthful/enabled iff local playback is actually supported (an audio stack exists) —
        // fixes the stale unconditional "Unavailable" (OnLocalPlaybackRejected is only wired when audio is null).
        postUi(() => svc.Playback.LocalPlaybackSupported.Value = audio is not null);
        // The last step lands BEFORE GoLive: GoLive flips AuthStatus.Authenticated, which unmounts the splash, so a report
        // after it would never be seen. This one gets the checkmarks on screen for the frame before the shell takes over.
        report.Report(new LoginSnapshot(LoginPhase.Finalizing, Step: LoginStep.Done));
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
                void ShowSetupToast() => Toast.Show(
                    Loc.Get(Strings.Playback.Runtime.Missing),
                    new ToastOptions
                    {
                        Severity = InfoBarSeverity.Warning,
                        ActionLabel = Loc.Get(Strings.Playback.Runtime.SetUp),
                        OnAction = () => svc.Playback.OpenPlaybackRuntimeSetup.Value++,
                    });
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
                (uris, c) => md.SyncAllAsync(uris, c),   // SyncAllAsync closes blank AlbumRefs / thin tracks itself (S2)
                (s, u) => svc.RealMutations!.HasPending(s, u));
            var signalClient = new Wavee.Backend.Playlists.PlaylistSignalsClient(
                live.Pipeline, () => live.BaseUrl, () => live.Session.Locale);
            var sync = new Wavee.Backend.Sync.LibrarySync(store, fetcher, collections, svc.RealMutations!, svc.MutTransport!,
                () => svc.RealSessionHost!.Current, () => live.Username, syncLog, cts.Token, svc.EchoRing, signalClient);
            var router = new Wavee.Backend.Realtime.DealerRouter(transport, sync);
            svc.RealSync = sync;
            svc.PlaylistTuning.Value = sync;
            sync.Enqueue(new Wavee.Backend.Sync.SyncCommand(Wavee.Backend.Sync.SyncKind.DrainWrites));      // replay writes queued while logged out
            var hydrated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Addendum A7 — InitialHydrate waits for the cold tier's WARM pass. It rewrites the saved sets and the
            // rootlist wholesale; running it against a still-empty hot tier makes every fold a cache MISS and refetches a
            // library that is already on disk. DrainWrites deliberately does NOT wait: local intent must send promptly,
            // and it touches the outbox, not the entity cache. WarmComplete is guaranteed to complete even when the warm
            // pass FAILS (Wave B's try/finally), so no timeout is needed to avoid a wedge.
            var hydrate = new Wavee.Backend.Sync.SyncCommand(Wavee.Backend.Sync.SyncKind.InitialHydrate, Done: hydrated);
            if (store is Wavee.Backend.Persistence.CachedStore warmStore && !warmStore.WarmComplete.IsCompleted)
                _ = Task.Run(async () =>
                {
                    try { await warmStore.WarmComplete.ConfigureAwait(false); } catch (Exception) { }
                    sync.Enqueue(hydrate);
                });
            else
                sync.Enqueue(hydrate);
            // Aggressive discography prefetch (artists → album cards → tracks) AFTER the saved sets land. Off the sync loop
            // (it must never block OpenPlaylist), cts-gated (logout cancels), SWR-skip makes re-login cheap.
            _ = Task.Run(async () =>
            {
                try
                {
                    await hydrated.Task.ConfigureAwait(false);
                    // Paged liked-member hydrate (S2): closes thin album/track refs for rows cached by an earlier session
                    // without one giant SyncAll over 10k members (CollectionFetcher page size = 300).
                    _ = PagedHydrateAsync(md, store.SavedUris("liked"), cts.Token);
                    await Wavee.Backend.Metadata.DiscographyPrefetcher.RunAsync(md, store, syncLog, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { syncLog.Info("discography prefetch failed: " + ex.Message); }
            });
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
                new PathfinderHeadersMiddleware(_ => Task.FromResult(live.ClientToken), live.Session.Locale));
            var pathfinder = new PathfinderClient(pathfinderExchange, spclientLog);
            var pathfinderResource = new PathfinderResource(pathfinder, () => live.Session, spclientLog);
            // Concert discovery (artist schedules, hub feed, location controls) — the live Pathfinder adapter over the same
            // resource, installed into the switchable the concert pages hold. Reset to the Null service on GoOffline.
            svc.Concerts.SetInner(new SpotifyConcertService(pathfinderResource));
            // Browse: the category directory + category pages, cached by the shared Pathfinder resource TTLs.
            svc.Browse.SetInner(new SpotifyBrowseService(pathfinderResource, spclientLog));
            // Expanded-row drawer data (kinds 98/99 associations + kind 5 audio formats), fetched on expand only.
            svc.TrackExpansion.SetInner(new SpotifyTrackExpansionService(em, store, metadataLog));
            // The cover-colour plane's universal feed. Everything that shows art — grids, shelves, heroes, editorial
            // cards, track rows — resolves its colour from the plane, and a miss enqueues the IMAGE here, so no surface
            // has to remember to prefetch its own tints. Kind 179 fills the same plane for free from the row bundle.
            CoverColorPlane.Current.Filler = CoverColorFiller.Create(pathfinderResource, spclientLog);
            var homeCache = new LiveHomeCache(pathfinderResource, () => svc.HomeFacet.Peek());
            // Featured-card hover peek: the batched feedBaselineLookup preview-track cache (display-only, no Store).
            HomeBaselinePreviews.Install(pathfinderResource);
            // "What's New" feed (queryWhatsNewFeed) — display-only, rides the PathfinderResource TTL. Seeded now so the
            // notification bell badge is correct before the first open; installed into the switchable the panel binds to.
            var whatsNew = new SpotifyWhatsNewService(pathfinderResource, notificationsLog);
            svc.WhatsNew.SetInner(whatsNew);
            host.AttachWhatsNew(whatsNew);
            whatsNew.EnsureFresh();
            // Below-the-fold album enrichment (about-artist / merch / similar via Pathfinder; recommended playlists via the
            // SAME extended-metadata source, kinds 151→205) — installed into the switchable service the album pages hold.
            svc.AlbumEnrichment.SetInner(new SpotifyAlbumEnrichmentService(pathfinderResource, em, store, metadataLog, extensionCache));
            // Standalone artist-page header stats (queryArtistOverview) — lazy, page-scoped; the Library artist surface
            // never reads it (100% V4). The discography itself is served from V4, not this.
            var artistLog = spclientLog.With("artist.popular");
            svc.ArtistStats.SetInner(new SpotifyArtistStatsService(pathfinderResource, store, artistLog));
            // Step two of the same chart: the SpClient artist-top-tracks-extensions list (~50 uris) hydrated over the SHARED
            // metadata service and merged onto that overview seed. Every dep is required — a half-wired go-live throws here
            // rather than pinning the chart at 10 rows forever.
            var popularTracks = new SpotifyArtistPopularTracksService(live.Pipeline, () => live.BaseUrl, md, store, artistLog);
            svc.ArtistPopularTracks.SetInner(popularTracks);
            svc.PlaylistPopcount.SetInner(new SpotifyPlaylistPopcountService(live.Pipeline, () => live.BaseUrl, artistLog));
            svc.ContentFilters.SetInner(new SpotifyContentFilterService(live.Pipeline, () => live.BaseUrl, artistLog));
            // Upcoming-release identity (kind 138) — shared extended-metadata source + etag cache, like the video detector.
            // Resolves prerelease↔album for the artist masthead, prerelease: routing, and the pre-save write.
            svc.PreRelease.SetInner(new SpotifyPreReleaseService(em, metadataLog, extensionCache));
            // Music-video detection + the video↔audio file-id map over the SAME extended-metadata source (etag-cached).
            var videoSvc = new SpotifyVideoService(em, store, metadataLog, extensionCache);
            svc.Video.SetInner(videoSvc);
            // Wire the pop-out/inline video resolver: track uri → Spotify manifest → a playable PopOutVideoSource
            // (PlayReady via the native CDM, or null when the account isn't served a PlayReady mp4). Over the live transport.
            // Through the composite so the tiered walk has ONE home: tier 1 is the user's attached local file (it always
            // wins, for ANY playable), tier 2 is this Spotify source tier, and a null answer falls through to the
            // controller's audio fallback. This REPLACES the overrides-only composite the pre-login bootstrap installed.
            svc.Playback.ResolveVideoSource =
                new CompositeVideoResolver((uri, ct) => videoSvc.ResolvePlayableAsync(uri, transport, ct), svc.VideoOverrides).ResolveAsync;
            var userProfiles = new SpotifyUserProfileService(em, live.Pipeline, () => live.BaseUrl, socialLog, extensionCache);
            if (profileFetched)
                userProfiles.Seed(live.Username, new Owner(
                    UserProfileIds.BareId(UserProfileIds.Normalize(live.Username) ?? live.Username),
                    displayName,
                    avatarUrl is { Length: > 0 } ? new Image(avatarUrl) : null));
            svc.UserProfiles.SetInner(userProfiles);
            // Let the player bar reflect the now-playing track's (async-detected) video via the store change stream.
            svc.Playback.AttachStore(store);
            // …and let the CONNECT wire reflect it too: the gid the state builder stamps as `associated_video_id` +
            // `switch-to-video`, and the one extra PutState a mid-track association land needs (no playback event fires
            // for a badge-only land, so nothing else would re-publish it).
            connect.AssociatedVideoGid = uri => store.GetVideoAssociation(uri)?.VideoGidHex;
            svc.Playback.RepublishConnectState = () => connect.RepublishPlayerState();
            // The detection hooks the surfaces that never route through OnDemandFetch fire: the artist chart, Liked Songs,
            // the queue, online search rows and live (sync-loop) playlist opens. Each is fire-and-forget so a hook never
            // sits on a read/render path; the batch itself is etag-cached and ≤300 uris per request inside the service.
            // Row adornments: the cover tint (kind 179) that stops track lists painting blank grey squares, plus
            // tempo/key (kind 222) for the track-row column. Shares the extended-metadata source + etag cache with the
            // video detector, and rides the same hooks below.
            var adornments = new SpotifyTrackAdornmentService(em, store, metadataLog, extensionCache);
            svc.TrackAdornments = adornments;
            // One hook per SURFACE (they differ only by the diagnostic tag): the association-plane log is only readable as
            // a coverage map if a batch names who asked for it.
            popularTracks.DetectVideos = DetectHook(svc.Video, adornments, cts.Token, "artist.popular", metadataLog);
            svc.Playback.DetectVideos = DetectHook(svc.Video, adornments, cts.Token, "queue", metadataLog);
            var detectSearch = DetectHook(svc.Video, adornments, cts.Token, "search", metadataLog);
            sync.OnPlaylistHydrated = uri => DetectContainerVideos(svc.Video, adornments, store, uri, cts.Token, metadataLog);
            if (svc.RealLibrarySource is { } libSrc)
            {
                libSrc.Sync = sync;   // on-open SWR: playlists route through the loop (blocking first fetch / background revalidate)
                libSrc.OnDemandFetch = async (uri, c) =>
                {
                    if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
                    {
                        await fetcher.FetchPlaylistAsync(uri, c).ConfigureAwait(false);
                    }
                    else if (uri.StartsWith("spotify:album:", StringComparison.Ordinal)) await EnsureAlbumAsync(md, pathfinderResource, store, uri, c).ConfigureAwait(false);
                    else if (uri.StartsWith("spotify:artist:", StringComparison.Ordinal))
                        await Wavee.Backend.Metadata.ArtistDiscography.EnsureAsync(md, store, uri, c, hydrateAppearsOn: true).ConfigureAwait(false);   // V4 ensure; appears-on hydrated lazily on open
                    else if (uri.StartsWith("spotify:show:", StringComparison.Ordinal))
                        await md.SyncAllAsync(new[] { uri }, c).ConfigureAwait(false);   // ShowV4 → membership + episode rows
                    // Detect music videos for the just-hydrated tracklist (batch, off the critical path → the movie icons fill in).
                    DetectContainerVideos(svc.Video, adornments, store, uri, c, metadataLog);
                };
                // Liked Songs never routes through OnDemandFetch. NOTE: GetDiscographyAsync fires this same hook with
                // ALBUM uris — DetectAsync drops every one of them (`notTrackUri` in the request line), only the adornment
                // pass consumes them. That is by design, not a coverage hole.
                libSrc.DetectVideos = DetectHook(svc.Video, adornments, cts.Token, "library", metadataLog);
                libSrc.HydrateMembers = uris => PagedHydrateAsync(md, uris, cts.Token);
                libSrc.LiveHomeFetch = c => homeCache.GetAsync(c);   // cached editorial home + separately refreshed recents
                libSrc.LiveSearch = async (q, facet, offset, limit, c) =>
                {
                    // Online search rows are transient mapper output (never store joins), so warm their associations at
                    // read time: the badge comes from the mapped totalCount, but PLAY-time correctness needs the cache.
                    var results = await FetchSearchAsync(pathfinder, q, facet, offset, limit, c).ConfigureAwait(false);
                    if (results is { Tracks.Count: > 0 })
                    {
                        var trackUris = new List<string>(results.Tracks.Count);
                        foreach (var t in results.Tracks) trackUris.Add(t.Uri);
                        _ = detectSearch(trackUris);
                    }
                    return results;
                };   // paged online search
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
        var notifications = new SpotifyNotificationsService(live.Pipeline, () => live.BaseUrl, notificationsLog,
            language: live.Session.Locale);
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

    // The shared detect hook handed to the surfaces that own their own track lists (artist chart, Liked, queue, search).
    // It returns a COMPLETED task on purpose: the callers sit on read/render paths, so the batch runs off-thread and its
    // failures die here rather than surfacing as an unobserved exception.
    // Adornments (kind 179 tint + kind 222 tempo/key) ride the SAME hook: every surface that already detects videos
    // for its rows needs the same rows tinted, and both services batch ≤300 uris with their own etag caching.
    /// <summary>Page SyncAllAsync at CollectionFetcher's page size (300) so a 10k liked set never becomes one
    /// giant ProjectCachedExtensions ToByteArray. Fire-and-forget from liked-open / post-InitialHydrate.</summary>
    static Task PagedHydrateAsync(Wavee.Backend.Metadata.MetadataService md, IReadOnlyList<string> uris, CancellationToken ct)
    {
        if (uris.Count == 0) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            const int page = 300;
            for (int i = 0; i < uris.Count; i += page)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(page, uris.Count - i);
                var batch = new string[n];
                for (int j = 0; j < n; j++) batch[j] = uris[i + j];
                try { await md.SyncAllAsync(batch, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { /* best-effort — next open retries */ }
            }
        }, ct);
    }

    // `surface` names WHICH list asked, so the association-plane log can be read as a coverage map (which surfaces ever
    // request kind 99, and with which uri sets) rather than an undifferentiated stream of batches.
    static Func<IReadOnlyList<string>, Task> DetectHook(IVideoService video, SpotifyTrackAdornmentService? adorn,
                                                       CancellationToken ct, string surface, WaveeLogger log)
        => uris =>
        {
            if (uris.Count > 0)
                _ = Task.Run(async () =>
                {
                    LogDetectSurface(log, surface, null, uris);
                    try { await video.DetectAsync(uris, ct).ConfigureAwait(false); } catch { }
                    if (adorn is not null)
                    {
                        // Tracks only. Album/playlist/artist covers no longer need a hook here at all: their colour is
                        // image-keyed in CoverColorPlane and the art slot itself asks for a grading when it renders.
                        try { await adorn.EnsureAsync(uris, ct).ConfigureAwait(false); } catch { }
                    }
                }, ct);
            return Task.CompletedTask;
        };

    /// <summary>Who asked for an association batch, and with WHICH uris. The bounded uri sample is the whole point: the
    /// "playlist shows no video but search does" report is only decidable by comparing the uri a playlist row carries with
    /// the uri the search response carried for the same song, and these two lines are where both are written down.</summary>
    static void LogDetectSurface(WaveeLogger log, string surface, string? contextUri, IReadOnlyList<string> uris)
    {
        if (!log.IsEnabled(WaveeLogLevel.Debug)) return;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < uris.Count && i < 6; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(uris[i].StartsWith("spotify:track:", StringComparison.Ordinal) ? uris[i]["spotify:track:".Length..] : uris[i]);
        }
        log.Event(WaveeLogLevel.Debug, "video.assoc.surface", "surface requested music-video associations",
            fields:
            [
                WaveeLogField.Of("surface", surface), WaveeLogField.Of("contextUri", contextUri ?? "-"),
                WaveeLogField.Of("uris", uris.Count), WaveeLogField.Of("sample", sb.Length == 0 ? "-" : sb.ToString()),
            ]);
    }

    // After a container's tracklist hydrates, batch-detect which of its tracks have a music video (fills the row indicator).
    // Fire-and-forget off the open path — best-effort, etag-cached, and a no-op when the container has no resident tracks yet.
    static void DetectContainerVideos(IVideoService video, SpotifyTrackAdornmentService? adorn, IStore store,
                                      string uri, CancellationToken ct, WaveeLogger log = default)
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
        else if (uri.StartsWith("spotify:artist:", StringComparison.Ordinal))
        {
            // The artist page's popular chart (the discography's albums detect on their own open).
            if (store.GetArtist(uri)?.TopTracks is { Count: > 0 } top)
            {
                uris = new List<string>(top.Count);
                foreach (var t in top) uris.Add(t.Uri);
            }
        }
        if (uris is not { Count: > 0 })
        {
            // A container whose tracklist is not resident yet detects NOTHING and is never retried for this open — worth a
            // line, because it is the one way an on-open detect can silently cover zero rows.
            log.Event(WaveeLogLevel.Debug, "video.assoc.surface", "container open detected no resident tracks",
                fields: [WaveeLogField.Of("surface", "container"), WaveeLogField.Of("contextUri", uri), WaveeLogField.Of("uris", 0)]);
            return;
        }
        var list = uris;
        _ = Task.Run(async () =>
        {
            LogDetectSurface(log, "container", uri, list);
            try { await video.DetectAsync(list, ct).ConfigureAwait(false); } catch { }
            if (adorn is not null)
                try { await adorn.EnsureAsync(list, ct).ConfigureAwait(false); } catch { }
        }, ct);
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

        // Audiobooks is the ONE facet whose op sends includePreReleases:true (wire-verified, omg.saz sid 0671).
        void VarsAudiobooks(Utf8JsonWriter w)
        {
            w.WriteBoolean("includePreReleases", true);
            w.WriteBoolean("includeAlbumPreReleases", true);
            w.WriteNumber("numberOfTopResults", limit);
            w.WriteString("searchTerm", query);
            w.WriteNumber("offset", offset);
            w.WriteNumber("limit", limit);
            w.WriteBoolean("includeAudiobooks", true);
            w.WriteBoolean("includeAuthors", true);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
        }

        // searchFullEpisodes takes a MINIMAL shape — sending the shared one would not match the persisted query.
        void VarsEpisodes(Utf8JsonWriter w)
        {
            w.WriteString("searchTerm", query);
            w.WriteNumber("offset", offset);
            w.WriteNumber("limit", limit);
            w.WriteBoolean("includeEpisodeContentRatingsV2", true);
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
                SearchFacet.Podcasts => (PathfinderOps.SearchPodcasts, PathfinderOps.SearchPodcastsHash),
                SearchFacet.Audiobooks => (PathfinderOps.SearchAudiobooks, PathfinderOps.SearchAudiobooksHash),
                SearchFacet.Episodes => (PathfinderOps.SearchFullEpisodes, PathfinderOps.SearchFullEpisodesHash),
                SearchFacet.Profiles => (PathfinderOps.SearchUsers, PathfinderOps.SearchUsersHash),
                // Unreachable: every SearchFacet member is mapped above. Kept as a loud failure so a NEW enum member
                // added without an operation fails at the call instead of silently returning empty results.
                _ => throw new NotSupportedException($"Search facet '{facet}' is not wired to a Pathfinder operation."),
            };

            // Two ops do NOT take the shared variable shape:
            //   searchAudiobooks  — the only op sending includePreReleases:TRUE
            //   searchFullEpisodes — a completely different, minimal shape (no numberOfTopResults / include* flags)
            Action<Utf8JsonWriter> vars = facet switch
            {
                SearchFacet.Audiobooks => VarsAudiobooks,
                SearchFacet.Episodes => VarsEpisodes,
                _ => Vars,
            };

            using var doc = await pf.QueryAsync(op, hash, vars, PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
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

        var lyricsLog = new WaveeLogger(WaveeLog.Instance, "lyrics");
        return new Wavee.Backend.Lyrics.AggregatingLyricsProvider(
            sources, Resolve, opt, referenceSourceId: "spotify",
            log: lyricsLog,
            // %LOCALAPPDATA%\Wavee\lyrics — read-through before the fan-out, so a track played in an earlier session
            // has its words with no network at all.
            diskCache: new Wavee.Backend.Lyrics.LyricsDiskCache(log: lyricsLog));
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
    // The desktop query embeds recently-played inline, so the composer builds the recents shelf too — no extra call.
    // facet: a homeChips[].id ("music-chip", "podcasts-following-chip", …) or null/"" for the unfiltered feed.
    static async Task<LiveHomeResult> FetchHomeAsync(PathfinderResource pf, string? facet, CancellationToken ct)
    {
        // The real local zone, as IANA. "Etc/UTC" used to be hardcoded here, which asked Spotify for someone else's
        // afternoon: the zone drives the greeting bucket and the time-of-day shelves.
        string tz = Wavee.Backend.Spotify.SpotifyTimeZone.LocalIana;
        using var doc = await pf.UseQueryAsync(PathfinderOps.Home, PathfinderOps.HomeHash,
            w =>
            {
                w.WriteString("homeEndUserIntegration", "INTEGRATION_DESKTOP");
                w.WriteString("timeZone", tz);
                w.WriteString("sp_t", "");
                w.WriteString("facet", facet ?? "");
                w.WriteNumber("sectionItemsLimit", 10);
                w.WriteBoolean("includeEpisodeContentRatingsV2", true);
            }, PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);
        if (doc is null) return LiveHomeResult.Empty;
        var homeRoot = Wavee.Core.SpotifyExportMapper.Dig(doc.RootElement, "data", "home");
        var contribution = Wavee.Core.SpotifyHomeComposer.Compose(homeRoot, System.Array.Empty<Wavee.Core.PlaylistSummary>(),
            Loc.Get(Strings.Home.MadeForYou), Loc.Get(Strings.Home.MoreForYou), Loc.Get(Strings.Home.RecentlyPlayed));
        return new LiveHomeResult(contribution.Groups, contribution.Chips);
    }

    sealed class LiveHomeCache
    {
        readonly PathfinderResource _pf;
        readonly Func<string?> _facet;

        public LiveHomeCache(PathfinderResource pf, Func<string?> facet) { _pf = pf; _facet = facet; }

        // The facet is read at FETCH time, not at construction: the chip row writes Services.HomeFacet and asks for a
        // refresh, and PathfinderResource keys its TTL cache on the request body — so a facet change is a distinct
        // cache entry rather than a stale hit.
        public Task<LiveHomeResult> GetAsync(CancellationToken ct) => FetchHomeAsync(_pf, _facet(), ct);
    }

    // V4-first album ensure: AlbumV4 (usually already resident from the prefetch) + TrackV4 enrichment for gid-only rows,
    // followed by Pathfinder getAlbum. The latter is required even for a named V4 list because V4 has no play-count field;
    // it also supplies the remaining Full release envelope. Already-Full cached albums return without another query.
    static async Task EnsureAlbumAsync(Wavee.Backend.Metadata.MetadataService md, PathfinderResource pf, IStore store, string uri, CancellationToken ct)
    {
        // Do not trust Hydration.Full alone. A partial getAlbum response can carry the complete envelope but no usable
        // rows; treating that as warm made the empty result permanent because every later repair request returned here.
        if (Wavee.Backend.Library.StoreLibrarySource.IsAlbumComplete(store.GetAlbum(uri))) return;
        try { await md.SyncAllAsync(new[] { uri }, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to getAlbum */ }
        var album = store.GetAlbum(uri);
        if (album?.Tracks is { Count: > 0 } tracks)
        {
            var missing = new List<string>();
            foreach (var t in tracks) if (t.Title.Length == 0) missing.Add(t.Uri);
            if (missing.Count > 0)
            {
                try
                {
                    await md.SyncAllAsync(missing, ct).ConfigureAwait(false);
                    var rebuilt = new List<Track>(tracks.Count);
                    foreach (var t in tracks) rebuilt.Add(store.GetTrack(t.Uri) ?? t);
                    store.UpsertAlbum(album with { Tracks = rebuilt });
                }
                catch (OperationCanceledException) { throw; }
                catch { /* TrackV4 batch failed → getAlbum below */ }
            }
        }
        await FetchAlbumAsync(pf, store, uri, ct).ConfigureAwait(false);   // play counts + complete envelope → Hydration.Full
    }

    // Fetch the album (metadata + tracklist) via Pathfinder getAlbum → map (data.albumUnion.tracksV2) → store. The
    // getAlbum fallback for the V4-empty-disc case + the below-the-fold "About this release" Full upgrade both call this.
    internal static async Task FetchAlbumAsync(PathfinderResource pf, IStore store, string uri, CancellationToken ct)
    {
        using var doc = await pf.QueryAsync(PathfinderOps.GetAlbum, PathfinderOps.GetAlbumHash,
            // locale is EMPTY on the wire (omg.saz/browe.saz): the captured client sends "" and lets the account's
            // market/language headers decide. Sending pf.Locale here diverged from every captured request.
            w => { w.WriteString("uri", uri); w.WriteString("locale", ""); w.WriteNumber("offset", 0); w.WriteNumber("limit", 50); },
            // getAlbum rides the web-player bundle in the capture, exactly like queryArtistOverview.
            PathfinderClient.Platform.WebPlayer, ct).ConfigureAwait(false);
        if (doc is null) return;
        if (Wavee.Core.SpotifyExportMapper.AlbumFromUnion(doc.RootElement) is { } album)
        {
            if (album.ArtistsDetailed is { Count: > 0 })
                foreach (var artist in album.ArtistsDetailed)
                    store.UpsertArtist(artist);
            // Fan the tracklist out as ENTITIES before the album write. CachedStore.PersistAlbum strips Tracks, so a
            // verdict carried only on the in-memory album is forgotten across a restart; routing each row through
            // UpsertTrack puts it on StoreEntityMerge.Track + PersistTrack, inheriting the same merge and pin rules
            // every other adornment uses.
            //
            // getAlbum is not the ONLY source of this — TrackV4 carries earliest_live_timestamp on every payload
            // (10,472/10,472 in the capture) and that is what ExtendedMetadataSource derives availability from. This
            // write is the getAlbum half; the two agree because both flow through the same nullable-merge rule.
            if (album.Tracks is { Count: > 0 } albumTracks)
                foreach (var t in albumTracks)
                    if (t.Uri.Length > 0) store.UpsertTrack(t);
            store.UpsertAlbum(album);
        }
    }

    // Connect's player_state can be thin. Resolve the full TrackV4 through extended-metadata; TrackV4's album ref carries
    // cover_group, and StoreEntityMerge keeps that richer image if a later thin cluster/store write arrives.
    //
    // The "already good enough" early-outs test all THREE fields the player bar renders — art, artists, AND the album
    // NAME. The album name used to be missing from the test, which made a row whose album ref carries a uri but no title
    // (a name-less TrackV4 album sub-message, or a row seeded by the artist-overview chart) count as fully resolved: the
    // getTrack upgrade below — the one source that always carries albumOfTrack.name — was never reached, so the bar and
    // the now-playing surfaces stayed album-less for the whole track. Both sites must agree, or the second one re-admits
    // the bug the first one just rejected.
    static async Task<Track?> ResolveNowPlayingTrackAsync(string uri, Wavee.Backend.Metadata.MetadataService metadata,
        PathfinderResource pathfinder, IStore store, CancellationToken ct)
    {
        var track = store.GetTrack(uri);
        if (StoreEntityGaps.NowPlayingReady(track)) return track;

        await metadata.SyncAllAsync(new[] { uri }, ct).ConfigureAwait(false);
        track = store.GetTrack(uri);
        if (StoreEntityGaps.NowPlayingReady(track)) return track;

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
    public static async Task<int> RunAsync(WaveeLogger log, CancellationToken ct, string language = "en")
    {
        log.Info("Wavee live Connect probe — building the real backend + going live...");
        language = SpotifyHeaders.NormalizeLanguage(language);
        var svc = Services.CreateReal(appLocale: new AppLocale(language == "en" ? "en-US" : language, language));
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
