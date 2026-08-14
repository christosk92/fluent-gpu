using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.MediaSources;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;
using Wavee.SpotifyLive.Gabo;

namespace Wavee.SpotifyLive;

// ── Stage 0 + H — the live Connect+playback composition (the session glue) ────────────────────────────────────────────
// Wires the whole control plane + bidirectional projection + controller + silent host + resolver onto a single dealer
// transport, and exposes the Wavee.Core seams (IPlaybackPlayer / IPlaybackState / IConnectDevices) the UI binds to. This is
// the "LiveSession owner": it captures the connection_id + announces the device, ingests cluster state, routes inbound
// commands to the controller, forwards outbound commands when another device is active, drives a SILENT host for local
// playback (real audio is the deferred host behind the same IAudioHost seam), and resolves tracks (CDN + key).
//
// ONE AP connection: the persistent AP channel for audio keys is the LOGIN socket, adopted by SpotifyLiveSpclient and
// passed in here — there is NO second handshake. If it's null (couldn't be retained), the resolver falls back gracefully.
public sealed class LiveConnect : IDisposable
{
    public NowPlayingProjection Projection { get; }
    public LiveConnectDevices Devices { get; }
    public PlaybackController Controller { get; }
    public AudioPlaybackStack? Audio => _audio;
    /// <summary>The dealer connection id (null until the first pusher hello / after a reconnect drop). Replays last value.</summary>
    public IObservable<string?> ConnectionId => _connect.ConnectionId;
    /// <summary>The current dealer connection id, or null if none has been captured yet.</summary>
    public string? CurrentConnectionId => _connect.CurrentConnectionId;
    /// <summary>Track uri → associated music-video gid, for the connect-state video parity (see
    /// <see cref="ConnectStateBuilder.AssociatedVideoGid"/>). Wired at go-live to the video-association store.</summary>
    public Func<string, string?>? AssociatedVideoGid
    {
        get => _stateBuilder.AssociatedVideoGid;
        set => _stateBuilder.AssociatedVideoGid = value;
    }
    /// <summary>The current OS render-endpoint friendly name for <c>DeviceInfo.audio_output_device_info</c> (see
    /// <see cref="ConnectStateBuilder.AudioOutputDeviceName"/>). Wired at go-live to the local-output picker service.</summary>
    public Func<string?>? AudioOutputDeviceName
    {
        get => _stateBuilder.AudioOutputDeviceName;
        set => _stateBuilder.AudioOutputDeviceName = value;
    }
    /// <summary>The output-device control for the LOCAL media stack — the audio host's own control with mute additionally
    /// fanned out to the video host (see <see cref="LocalMediaOutputControl"/>). Null when the wired audio host exposes no
    /// device control (the silent/fake backend), which is exactly the case where the picker/mute affordances stay hidden.
    /// The composition root wires the picker service and the notice/volume subscriptions to THIS, never to the audio host
    /// directly, so a mute can never be lost at a video boundary.</summary>
    public IAudioOutputDeviceControl? OutputDeviceControl { get; }
    /// <summary>Re-publish the player state for a wire-visible change that produced no playback event (a music-video
    /// association landing under the playing track). One PutState, no host/kind change.</summary>
    public void RepublishPlayerState() => _publisher.PublishStateChanged();
    /// <summary>Re-announce this device to the cluster (a <c>NewConnection</c> PutState) after an OS resume — a sleeping
    /// machine's device registration is gone even when the socket looks alive. Not the same as
    /// <see cref="RepublishPlayerState"/>, which only reports transport state.</summary>
    public void AnnounceNewConnection() => _publisher.AnnounceNewConnection();
    readonly ConnectService _connect;
    readonly DeviceStatePublisher _publisher;
    readonly ConnectStateBuilder _stateBuilder;
    readonly ClusterIngest _ingest;
    readonly ConnectCommandRouter _commands;
    readonly IAudioHost _host;
    readonly FluentVideoMediaHost _videoHost;   // the VIDEO half of the ONE current media (Milestone B) — OWNS the player
    readonly WaveeLogger _playbackLog;
    Action<FluentGpu.Media.MediaPlayer?>? _onVideoPlayerChanged;   // the wired PlayerChanged relay (detached on Dispose)
    Action<string, long>? _onVideoDurationKnown;                   // the wired DurationKnown relay (detached on Dispose)
    readonly SpotifyServerClock _clock;   // server-clock skew estimator → corrects remote-position aging
    readonly ApConnection? _apChannel;   // owned: the adopted login socket
    readonly AudioPlaybackStack? _audio; // optional local-audio stack (null = silent/stub resolver)
    readonly RawCoreStreamProjection? _gabo;
    readonly ResumePointProjection? _resume;
    readonly GaboBatcher? _gaboBatcher;

    public LiveConnect(ITransport transport, string deviceId, ApConnection? apChannel,
        IContextResolver? contexts = null, WaveeLogger log = default,
        AudioPlaybackStack? audio = null, double initialVolume01 = 0.7,
        Func<CancellationToken, Task<string>>? refreshTokens = null, IAppSettings? settings = null)
    {
        _apChannel = apChannel;
        _audio = audio;
        var playbackLog = log.With("playback");
        _playbackLog = playbackLog;
        var telemetryLog = log.With("telemetry");

        // Server-clock estimator: probes GET /melody/v1/time over the authenticated spclient pipeline; its corrected
        // "server now" feeds the projection's remote-position aging (the offset-dependent transit term).
        _clock = new SpotifyServerClock(ct => FetchServerTimeMs(transport, ct), log);
        Projection = new NowPlayingProjection(deviceId, serverNowUnixMs: _clock.ServerNowUnixMs, initialVolume01: initialVolume01);
        Devices = new LiveConnectDevices();
        _ingest = new ClusterIngest(transport, Projection, Devices, deviceId, log, _clock.ObservePassive);

        var builder = new ConnectStateBuilder(deviceId, "Wavee", isPrivateSession: () => Projection.IsPrivateSession);
        _stateBuilder = builder;
        _connect = new ConnectService(transport);   // connection-id capture only
        // bug 6 / M0: the current track's Connect `track_player` must come from the controller's LIVE media kind (the ONE
        // truth about which host is playing), not from a bare has-video flag on the wire snapshot. The controller does not
        // exist yet here, so the publisher's build delegate reads it through this late-bound thunk (Audio until then).
        Func<PlayableKind>? currentMediaKind = null;
        // The SINGLE PutState writer: NewConnection announce on the connection-id + our local player_state on playback
        // changes (so other devices/controllers see us as the active player). Re-injects the response cluster.
        _publisher = new DeviceStatePublisher(transport, deviceId, Projection, _connect.ConnectionId, () => _connect.CurrentConnectionId,
            (reason, snap, mid, isActive, attribution) => builder.BuildPutState(reason, snap, mid, isActive,
                currentKind: currentMediaKind is null ? PlayableKind.Audio : currentMediaKind(),
                lastCommandSentByDeviceId: attribution.SenderDeviceId,
                lastCommandMessageId: attribution.MessageId),
            onCluster: _ingest.OnAnnounceResponse, log: log);

        _host = audio is not null ? audio.Host : new SilentAudioHost();
        // The video-media host: the VIDEO half of the ONE current media. Constructed regardless of the audio backend (it is
        // self-contained — a resolved PopOutVideoSource carries its own descriptor/relay), so the SilentAudioHost path still
        // has a real video host available for the swap.
        _videoHost = new FluentVideoMediaHost(playbackLog);
        // Mute is a property of the CURRENT MEDIA, not of its audio half: the player bar / picker set it through
        // IAudioOutputDeviceControl, which only the audio host implements, so a mute set while (or before) a music video is
        // the current media used to be dropped on the floor. The composite routes it to both hosts; everything else is the
        // audio host's control verbatim. Null on the silent/fake backend, where no device affordances are shown at all.
        OutputDeviceControl = _host is IAudioOutputDeviceControl audioDeviceControl
            ? new LocalMediaOutputControl(audioDeviceControl, _videoHost)
            : null;
        var resolver = audio?.TrackResolver ?? (ITrackResolver)new StubTrackResolver();
        // Instant-start: when the local-audio stack is present, resolve head+key in parallel and start on the clear head.
        var fast = audio is not null
            ? new FastTrackPlayback(audio.TrackResolver, audio.HeadClient, playbackLog, audio.TrackResolver.InvalidateCdn)
            : null;
        // Source-agnostic playable seams: resolve / warm / wire-meta all dispatch through the provider registry, which routes
        // by uri ownership — no code between play-intent and the host inspects the scheme any more. The fake/silent bootstrap
        // has no live resolver: it keeps the stub resolver wired directly and leaves every registry-driven hook null
        // (= today's behavior).
        //
        // REGISTRATION ORDER IS THE ROUTING TABLE (first Owns wins): Spotify first because it is every hot playable, then the
        // two engine-free local sources. Those two are constructed UNCONDITIONALLY — they need no session, no network and no
        // credentials, which is precisely what makes them the validation cases for the seam.
        MediaProviderRegistry? media = audio?.TrackResolver is { } liveResolver && fast is not null
            ? new MediaProviderRegistry(
                new SpotifyMediaProvider(liveResolver, fast),
                new LocalFileMediaProvider(probeDurationMs: LocalAudioDurationProbe.Probe),
                new GenericMediaProvider(probeDurationMs: LocalAudioDurationProbe.Probe))
            : null;
        var outbound = new LiveOutboundControl(transport, deviceId, () => _connect.CurrentConnectionId);
        var gaboCtx = GaboContextFactory.Create();
        settings ??= AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        var gaboSeq = settings.Get(WaveeSettings.GaboGlobalSequence);
        _gaboBatcher = new GaboBatcher(transport, gaboCtx, initialSequenceNumber: gaboSeq, refreshTokens: refreshTokens,
            persistSequence: seq => settings.Set(WaveeSettings.GaboGlobalSequence, seq), log: telemetryLog);
        _gabo = new RawCoreStreamProjection(_gaboBatcher, () => Projection.ContextUri, () => true, log: telemetryLog);
        var herodotus = new HerodotusClient(transport, telemetryLog);
        _resume = new ResumePointProjection(herodotus, () => Projection.IsPrivateSession, telemetryLog);
        Controller = new PlaybackController(_host, media ?? resolver, Projection,
            contexts ?? EmptyContextResolver.Instance,
            deviceId, outbound, new IPlaybackProjection[] { _gabo, _resume, _publisher }, playbackLog,
            SpotifyClientIdentity.XpuiSnapshotVersion,   // play_origin.feature_version
            fast: (IFastTrackResolver?)media ?? fast, videoHost: _videoHost,
            transferDecoder: new ProtoTransferStateDecoder());
        currentMediaKind = () => Controller.CurrentMediaKind;   // close the late-bound `track_player` loop (see above)
        Controller.EpisodeResumeMicros = (uri, ct) => herodotus.TryGetEpisodeResumeMicrosAsync(uri, ct);
        if (media is not null)
        {
            Controller.MetaResolver = media.ResolveWireMetaAsync;
            Controller.CanPrepareNext = t => media.SupportsPreparedNext(t.Uri);
            // CONNECT MASKING ON (P4). Now that non-Spotify playables exist, the publisher must stop putting uris on the
            // cluster that no remote controller can resolve: publishable ones (Spotify's) go verbatim, everything else is
            // rewritten into Spotify's own self-describing local-file namespace. The mask touches ONLY the uri field —
            // the QueueEntry uid is untouched, so skip_to/remove from a controller still address the right row.
            _publisher.PublishUriMask = ConnectUriMask.For(media);
        }

        _commands = new ConnectCommandRouter(
            transport,
            (cmd, ct) => Controller.HandleRemoteCommandAsync(cmd, ct),
            (volume, ct) => Controller.HandleInboundVolumeAsync(volume, ct),
            log);
        Devices.TransferHandler = (id, c) => Controller.TransferToAsync(id, c);
        _clock.Start();
    }

    // ── M0 — "one media, one host, one player": the app-level video hooks ─────────────────────────────────────────────────
    // This is the wiring the old TODO(B-wire) described. It lives here (not in Backend/) precisely because the controller's
    // hooks are DELEGATES: PlaybackController never references PlaybackBridge, the SpotifyLive video types, or FluentGpu.
    //
    //   ShouldPlayAsVideo      → the bridge's derived per-track predicate (the ONE VideoPlacementLogic.VideoActive rule, read
    //                            without subscribing), so turning video off / dismissing it routes the media back to audio.
    //   LoadCurrentVideoAsync  → the bridge's existing resolve path, then FluentVideoMediaHost.LoadVideo — the HOST builds and
    //                            owns the engine MediaPlayer; the mounted surface only presents it.
    //   PlayerChanged          → mirrored onto PlaybackBridge.VideoPlayer (UI-thread marshalled) so the surface re-binds.
    //   RequestMediaKindRefresh→ the controller's re-evaluation entry point, so a mid-track "watch video" takes effect NOW.
    /// <summary>Hand the controller the app-level video hooks (M0). Called ONCE by the live composition root
    /// (<c>LiveSessionHost</c>); idempotent.
    /// <para>KNOWN GAP, not a switch: a DRM music video has no audio track yet — the native CENC source demuxes video only
    /// (the manifest parser drops every audio profile; <c>ProtectedMediaBackend</c> advertises no audio voice), so the swap
    /// stops the song and the video plays silent. The milestone that demuxes the video's OWN audio is what makes this fully
    /// correct (<c>docs/plans/wavee-surfaces-placement-design.md</c>, M7).</para></summary>
    public void WireVideoMedia(PlaybackBridge bridge, VideoOverrideService? overrides = null)
    {
        if (_onVideoPlayerChanged is not null) return;

        // 1. The per-playable video decision. The bridge folds the sticky intent × this track's has-video × the per-content
        //    dismissal through the one pure rule; a throwing predicate degrades to audio inside the controller.
        Controller.ShouldPlayAsVideo = track => bridge.ShouldPlayAsVideo(track);

        // 2. The async source handoff. Reuses the bridge's resolve path (same resolver, same publish onto PopOutVideoSource so
        //    the surfaces key their content on the very source the host plays). Returns FALSE on "no playable video", which is
        //    the controller's signal to fall back to audio for this playable instead of leaving the user in silence.
        Controller.LoadCurrentVideoAsync = async (track, ct) =>
        {
            var src = await bridge.ResolveVideoSourceForPlaybackAsync(track.Uri, ct).ConfigureAwait(false);
            if (src is null)
            {
                _playbackLog.Info($"no playable video source resolved for {track.Uri} - the controller will play it as audio");
                return false;
            }
            _videoHost.LoadVideo(src);   // the HOST builds/owns the player and raises PlayerChanged (relayed below)
            return true;
        };

        // 3. The player reaches the surfaces REACTIVELY (never a frozen field): the host raises PlayerChanged on its own
        //    thread, the bridge marshals it onto the UI thread as one atomic (player, generation) value, and the mounted
        //    MediaPlayerElement is keyed on that generation so it re-binds to the new instance.
        _onVideoPlayerChanged = p => bridge.NotifyVideoPlayerChanged(p);
        // 3b. THE DEAD-VIDEO EDGE. The controller is the only place that KNOWS video did not happen for a playable (it
        //     fell back to audio because nothing was playable, or the open failed and the recovery hook declined). Without
        //     this the placement model never learns: availability still says "this track has a video", so the mounted
        //     surface keeps showing its indeterminate loading poster over audio that is already playing, forever. The app
        //     latches THAT PLAYABLE (never the intent), so the next video-bearing track opens exactly as before.
        Controller.OnVideoMediaUnavailable = track => bridge.NotifyVideoMediaEnded(track.Uri);
        _videoHost.PlayerChanged += _onVideoPlayerChanged;
        // Seed only if a player somehow already exists (re-wire after a logout/login); the default binding is already "none",
        // so seeding null would bump the generation and re-render the surfaces for nothing.
        if (_videoHost.CurrentPlayer is { } existing) bridge.NotifyVideoPlayerChanged(existing);

        // 4. Every writer of the video INTENT asks the controller to re-evaluate the CURRENT playable's kind, so "watch video",
        //    "switch to audio", and the surface ✕ swap the media host for the track already playing — not only at the next
        //    track boundary. Fire-and-forget: it is a locked backend operation, never awaited from a UI handler.
        //    forceReloadIfVideo: an override attach/replace/remove changes the video SOURCE without changing the KIND, so the
        //    same-kind early return has to be overridden for that one case. clearConnectAudioFirst is ORTHOGONAL and travels
        //    separately: only an explicit user media intent may drop the remote playback ids (see the controller's remarks).
        bridge.RequestMediaKindRefresh = (forced, clearConnect) => _ = RefreshMediaKindAsync(forced, clearConnect);

        // 5. MP4-AUTHORITATIVE DURATION. A user-attached local video is a different edit with its own length; the moment the
        //    media engine knows it, it becomes the projection's truth for that playable (seek bar + the PutState duration
        //    follow automatically). Scoped to `local:video:` keys ONLY — a Spotify music video keeps publishing the catalog
        //    duration byte-for-byte as before.
        _onVideoDurationKnown = (key, ms) =>
        {
            if (!key.StartsWith(Wavee.Backend.VideoOverride.SourceKeyPrefix, StringComparison.Ordinal)) return;
            if (Projection.CurrentTrack?.Uri is not { Length: > 0 } uri) return;
            Projection.SetDurationOverride(uri, ms);
            overrides?.NoteDuration(uri, ms);
            _playbackLog.Info($"local video duration adopted for {uri}: {ms} ms");
        };
        _videoHost.DurationKnown += _onVideoDurationKnown;

        // 6. OPEN-FAILURE RECOVERY. A local attachment that exists but cannot be decoded must not become a dead end: peek the
        //    source the host was playing, and if it is an override, quarantine that exact (uri, key) for the session, drop the
        //    cached source, tell the user once, and answer TRUE so the controller re-runs the load — which now walks past the
        //    attachment to the official video, or to audio. Anything else answers FALSE, i.e. today's behavior byte-for-byte.
        Controller.TryRecoverVideoAsync = (track, _) =>
        {
            if (overrides is null) return Task.FromResult(false);
            var key = bridge.PopOutVideoSource.Peek()?.Key;
            if (key is not { Length: > 0 } || !key.StartsWith(Wavee.Backend.VideoOverride.SourceKeyPrefix, StringComparison.Ordinal))
                return Task.FromResult(false);
            overrides.Quarantine(track.Uri, key);
            bridge.InvalidateVideoSource(track.Uri);   // else the reload would hand the same broken source straight back
            bridge.NotifyVideoOverrideUnplayable(track.Uri);
            _playbackLog.Info($"attached video {key} failed to open for {track.Uri} — quarantined for this session; falling back");
            return Task.FromResult(true);
        };
    }

    async Task RefreshMediaKindAsync(bool forceReloadIfVideo = false, bool clearConnectAudioFirst = true)
    {
        try { await Controller.RefreshCurrentMediaKindAsync(forceReloadIfVideo, clearConnectAudioFirst).ConfigureAwait(false); }
        catch (Exception ex) { _playbackLog.Info("media-kind refresh failed: " + ex.Message); }
    }

    // Fetch the server's wall clock (Unix ms) over the authenticated spclient pipeline. GET /melody/v1/time → {"timestamp": ms}.
    // Tolerates a seconds-resolution payload (scaled to ms) so a unit change on the endpoint can't silently corrupt the offset.
    static async Task<long> FetchServerTimeMs(ITransport transport, CancellationToken ct)
    {
        var resp = await transport.Request(Channel.Spclient, "/melody/v1/time", default, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0) return 0;
        using var doc = System.Text.Json.JsonDocument.Parse(resp.Body);
        if (!doc.RootElement.TryGetProperty("timestamp", out var t) || !t.TryGetInt64(out var ms) || ms <= 0) return 0;
        return ms < 100_000_000_000L ? ms * 1000 : ms;   // < ~1973 in ms ⇒ the payload was seconds; scale up
    }

    public void Dispose()
    {
        if (_onVideoPlayerChanged is { } relay) { try { _videoHost.PlayerChanged -= relay; } catch { } _onVideoPlayerChanged = null; }
        if (_onVideoDurationKnown is { } durRelay) { try { _videoHost.DurationKnown -= durRelay; } catch { } _onVideoDurationKnown = null; }
        try { Controller.DeactivateIfActiveOwner(); } catch { }   // best-effort clean is_active=false hand-off on logout
        _commands.Dispose();
        _publisher.Dispose();
        _resume?.Dispose();
        if (_gabo is not null) _ = _gabo.DisposeAsync().AsTask();
        if (_gaboBatcher is not null) _ = _gaboBatcher.DisposeAsync().AsTask();
        _connect.Dispose();
        _ingest.Dispose();
        Controller.Dispose();
        _clock.Dispose();
        _apChannel?.Dispose();
        Projection.Dispose();
        try { _videoHost.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _host.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _audio?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
    }
}
