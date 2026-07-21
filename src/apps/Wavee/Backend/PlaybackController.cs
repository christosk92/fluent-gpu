using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.Backend;

// ── Stage E — the PlaybackController (the live IPlaybackPlayer orchestrator + Connect command arbitration) ────────────
// Routing spine (see docs/plans/wavee-playback-arbitration-rules.md): for EVERY verb,
//   LOCAL  ⇔ cluster.ActiveDeviceId is empty OR == us   (we take over / we are the player)
//   REMOTE ⇔ another device is active                   (forward the command to it)
// The cluster is the single source of truth for "who is active" — no local flag. Ghost resume seeds local playback from
// the cluster snapshot when the play button is pressed while nothing is loaded locally. Inbound REQUEST commands (we are
// the target) always execute LOCALLY regardless of the routing rule (the dealer only routes them to us when we're active).

public interface ITrackResolver
{
    Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default);
}

public readonly record struct PlaybackTrackMeta(byte[] MediaId, byte[] FileId, int BitrateKbps, string AudioFormat, long DurationMs);

public sealed class StubTrackResolver : ITrackResolver
{
    public Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default)
        => Task.FromResult(new AudioStreamHandle(track.Uri, "", "", default, AudioFormat.OggVorbis320, track.DurationMs, 0f));
}

/// <summary>The result of an outbound player command: HTTP ok + the ack_id the server echoes (optimistic correlation —
/// we do not block-wait; a failure surfaces via the cluster, and the status/ack_id are surfaced for logging).</summary>
public readonly record struct OutboundResult(bool Ok, string? AckId, int Status);

/// <summary>Sends an outbound player command to the active device (we are the controller). Proto-free JSON over spclient.</summary>
public interface IOutboundControl
{
    Task<OutboundResult> SendAsync(string targetDeviceId, string commandJson, CancellationToken ct = default);
    /// <summary>Set a remote device's volume via the dedicated PUT /connect-state/v1/connect/volume endpoint (NOT a
    /// player/command verb). <paramref name="volume0_65535"/> is Spotify's 0..65535 scale.</summary>
    Task<OutboundResult> SetVolumeAsync(string targetDeviceId, int volume0_65535, CancellationToken ct = default);
    Task<OutboundResult> TransferAsync(string fromDeviceId, string targetDeviceId, CancellationToken ct = default);
}

/// <summary>POSTs /connect-state/v1/player/command/from/{us}/to/{target} with the command JSON envelope, and parses the
/// server's ack_id from the response (best-effort).</summary>
public sealed class LiveOutboundControl : IOutboundControl
{
    readonly ITransport _transport;
    readonly string _ourDeviceId;
    readonly Func<string?>? _connectionId;
    public LiveOutboundControl(ITransport transport, string ourDeviceId, Func<string?>? connectionId = null)
    { _transport = transport; _ourDeviceId = ourDeviceId; _connectionId = connectionId; }

    public async Task<OutboundResult> SendAsync(string targetDeviceId, string commandJson, CancellationToken ct = default)
    {
        var route = $"/connect-state/v1/player/command/from/{_ourDeviceId}/to/{targetDeviceId}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
            ["X-Transfer-Encoding"] = "gzip",
        };
        if (_connectionId?.Invoke() is { Length: > 0 } connId) headers["X-Spotify-Connection-Id"] = connId;
        var resp = await _transport.Request(Channel.Spclient, route,
            HttpCompression.Gzip(Encoding.UTF8.GetBytes(commandJson)), ct, headers: headers).ConfigureAwait(false);
        return new OutboundResult(resp.Ok, ParseAckId(resp), resp.Status);
    }

    public async Task<OutboundResult> SetVolumeAsync(string targetDeviceId, int volume0_65535, CancellationToken ct = default)
    {
        var route = $"/connect-state/v1/connect/volume/from/{_ourDeviceId}/to/{targetDeviceId}";
        var resp = await _transport.Request(Channel.Spclient, route, OutboundEnvelope.ConnectVolumeBody(volume0_65535), ct, "PUT").ConfigureAwait(false);
        return new OutboundResult(resp.Ok, ParseAckId(resp), resp.Status);
    }

    public async Task<OutboundResult> TransferAsync(string fromDeviceId, string targetDeviceId, CancellationToken ct = default)
    {
        var route = $"/connect-state/v1/connect/transfer/from/{fromDeviceId}/to/{targetDeviceId}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
        };
        if (_connectionId?.Invoke() is { Length: > 0 } connId) headers["X-Spotify-Connection-Id"] = connId;
        var body = Encoding.UTF8.GetBytes(OutboundEnvelope.Transfer(NewId(), NewId(), Guid.NewGuid().ToString(), "premium"));
        var resp = await _transport.Request(Channel.Spclient, route, body, ct, headers: headers).ConfigureAwait(false);
        return new OutboundResult(resp.Ok, ParseAckId(resp), resp.Status);
    }

    static string? ParseAckId(Resp resp)
    {
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0) return null;
        try { using var doc = JsonDocument.Parse(resp.Body); return doc.RootElement.TryGetProperty("ack_id", out var a) ? a.GetString() : null; }
        catch { return null; }
    }

    static string NewId() => Guid.NewGuid().ToString("N");
}

public sealed class PlaybackController : IPlaybackPlayer, IDisposable
{
    readonly PlaybackSession _session = new();
    QueueSnapshot _snap;                   // the latest atomic snapshot (published via ApplyLocalSnapshot); the ONE truth
    readonly IAudioHost _host;
    readonly ITrackResolver _resolver;
    readonly IFastTrackResolver? _fast;   // when set, local play uses instant-start (head before key); else the plain resolve
    readonly NowPlayingProjection _projection;
    readonly IContextResolver _contexts;
    readonly IOutboundControl? _outbound;
    readonly IReadOnlyList<IPlaybackProjection> _extra;
    readonly string _ourDeviceId;
    readonly string _featureVersion;
    readonly WaveeLogger _log;
    readonly IDisposable _hostSub;
    readonly IPreparedAudioHost? _preparedHost;
    readonly IDisposable? _transitionSub;
    readonly IDisposable _projSub;
    readonly SemaphoreSlim _lock = new(1, 1);
    readonly object _ownershipGate = new();
    static readonly TimeSpan FastStartBodySupplyGrace = TimeSpan.FromMilliseconds(250);
    string _lastActive = "";
    double _lastVolume = -1;
    double _lastIntentVolume = double.NaN;
    readonly TrailingCoalescer _remoteVolumeTx = new(400);
    bool _ownsActivePlayback;
    string? _nextPageUrl;
    bool _contextIsInfinite;
    string? _autoplayLatchedFor;
    Task<ResolvedContext>? _continuationFetch;
    string _commandIdHex = "";
    PlaybackIds? _currentIds;
    string? _idsSessionContext;
    string _reasonStart = "clickrow";
    string? _lastControllerQueueDiagSig;
    readonly object _prepareGate = new();
    CancellationTokenSource? _prepareCts;
    string? _preparedToken;
    string? _preparedSignature;
    QueueItemId _preparedItemId;
    long _prepareSequence;
    PlaybackFailureCheckpoint? _failureCheckpoint;

    readonly record struct PlaybackFailureCheckpoint(string TrackUri, long PositionMs);

    public PlaybackController(IAudioHost host, ITrackResolver resolver, NowPlayingProjection projection,
        IContextResolver contexts,
        string ourDeviceId, IOutboundControl? outbound = null, IReadOnlyList<IPlaybackProjection>? extraProjections = null, WaveeLogger log = default,
        string? playFeatureVersion = null, IFastTrackResolver? fast = null)
    {
        _host = host;
        _snap = _session.Snapshot();
        _resolver = resolver;
        _fast = fast;
        _projection = projection;
        _contexts = contexts;
        _ourDeviceId = ourDeviceId;
        _outbound = outbound;
        _extra = extraProjections ?? Array.Empty<IPlaybackProjection>();
        _log = log;
        _featureVersion = playFeatureVersion ?? OutboundEnvelope.DefaultFeatureVersion;
        _hostSub = host.Signals.Subscribe(Observers.From<AudioHostSignal>(OnHostSignal));
        _preparedHost = host as IPreparedAudioHost;
        _transitionSub = _preparedHost?.Transitions.Subscribe(Observers.From<AudioTransitionSignal>(OnAudioTransition));
        _projSub = projection.Changes.Subscribe(Observers.From<IPlaybackState>(OnProjectionChanged));
        PlaybackBucketDiagnostics.Startup("controller", "created",
            WaveeLogField.Of("device", ourDeviceId),
            WaveeLogField.Of("outbound", outbound is not null),
            WaveeLogField.Of("fast", fast is not null),
            WaveeLogField.Of("extraProjections", _extra.Count));
    }

    public IPlaybackState State => _projection;

    /// <summary>When set, LOCAL playback is rejected at every point that would start/seed the (silent) local host: the hook
    /// fires (the app shows the "playback on this device isn't supported yet — choose a remote device" toast) and the
    /// operation aborts. Null (the default — unit tests, and a future real-audio build) leaves local playback enabled.
    /// Remote forwarding is never affected. Wired by the live bootstrap to <c>PlaybackBridge.NotifyLocalPlaybackUnsupported</c>.</summary>
    public Action? OnLocalPlaybackRejected { get; set; }
    public Func<bool>? AutoplayEnabled { get; set; }
    public Func<Track, CancellationToken, Task<PlaybackTrackMeta?>>? MetaResolver { get; set; }
    public Func<string, CancellationToken, Task<long>>? EpisodeResumeMicros { get; set; }

    bool RejectLocalPlay()
    {
        if (OnLocalPlaybackRejected is not { } reject) return false;
        _log.Info("local playback unsupported — rejecting local play intent (choose a remote device)");
        reject();
        return true;
    }

    /// <summary>When set, an outbound command to the active remote device that FAILS (transfer / play) surfaces to the app
    /// (a "couldn't reach that device" toast) instead of failing silently. Null (unit tests) = log-only.</summary>
    public Action? OnRemoteCommandFailed { get; set; }

    /// <summary>When set, a LOCAL playback attempt that fails to resolve/decrypt/decode surfaces a typed
    /// <see cref="PlaybackErrorInfo"/> (reason + technical detail + user message) instead of a silently-dropped
    /// fire-and-forget Task. The live bootstrap logs the detail at Error and toasts the user message.</summary>
    public Action<PlaybackErrorInfo>? OnPlaybackError { get; set; }

    void ReportPlaybackError(Exception ex)
    {
        var reason = ex is AudioPlaybackException ape ? ape.Reason : AudioKeyFailureReason.None;
        string userMsg = reason != AudioKeyFailureReason.None ? reason.ToUserMessage() : "Couldn't play this track.";
        string detail = ex is AudioPlaybackException a ? (a.Message == reason.ToString() ? reason.ToString() : $"{reason}: {a.Message}") : ex.ToString();
        _log.Info("local playback error: " + detail);
        OnPlaybackError?.Invoke(new PlaybackErrorInfo(reason, userMsg, detail));
    }

    // Instant-start body supply: await the (parallel) key+CDN resolve and hand it to the host; a body failure surfaces
    // as a typed playback error (the head already started, so this is the "couldn't continue" case).
    async Task SupplyBodyWhenReadyAsync(Task<AudioStreamHandle> body, string expectedTrackUri, long loadStartedTicks, int clearHeadBytes)
    {
        try
        {
            var h = await body.ConfigureAwait(false);
            if (clearHeadBytes > 0)
            {
                var elapsed = ElapsedSince(loadStartedTicks);
                if (elapsed < FastStartBodySupplyGrace)
                {
                    var remaining = FastStartBodySupplyGrace - elapsed;
                    _log.Info($"fast-start body ready early track={expectedTrackUri} file={h.FileIdHex}; deferring supply {remaining.TotalMilliseconds:0}ms so clear-head decode can queue first PCM");
                    await Task.Delay(remaining).ConfigureAwait(false);
                }
            }

            var current = _session.Current?.Uri ?? "";
            if (string.Equals(current, expectedTrackUri, StringComparison.Ordinal))
            {
                _log.Info($"fast-start body ready track={expectedTrackUri} file={h.FileIdHex}; supplying to audio host");
                _host.SupplyBody(h);
            }
            else
            {
                _log.Info($"fast-start body ignored as stale expected={expectedTrackUri} current={current} bodyTrack={h.TrackUri} file={h.FileIdHex}");
            }
        }
        catch (OperationCanceledException)
        {
            _log.Info($"fast-start body task canceled expected={expectedTrackUri}");
        }
        catch (Exception ex)
        {
            var current = _session.Current?.Uri ?? "";
            if (string.Equals(current, expectedTrackUri, StringComparison.Ordinal))
            {
                _log.Info($"fast-start body failed for active track={expectedTrackUri}; stopping audio host to unblock head stream: {ex.GetType().Name}: {ex.Message}");
                _host.Stop();
            }
            else
            {
                _log.Info($"fast-start body failed for stale track expected={expectedTrackUri} current={current}: {ex.GetType().Name}: {ex.Message}");
            }
            ReportPlaybackError(ex);
        }
    }

    static TimeSpan ElapsedSince(long startTicks) =>
        startTicks == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - startTicks) / (double)Stopwatch.Frequency);

    /// <summary>Re-attempt the current track after a surfaced playback error (the toast/player-bar "Retry" action).</summary>
    public async Task RetryCurrentAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session.Current is { } current)
            {
                long resume = _failureCheckpoint is { } checkpoint
                    && string.Equals(checkpoint.TrackUri, current.Uri, StringComparison.Ordinal)
                    ? checkpoint.PositionMs
                    : -1;
                await LoadAndPlayCurrentAsync(EvKind.Started, ct, resume).ConfigureAwait(false);
                _failureCheckpoint = null;
            }
        }
        finally { _lock.Release(); }
    }

    // The routing spine: local iff nobody is active or we are. (No _localActive flag — the cluster is the truth.)
    bool RouteLocal()
    {
        var aid = _projection.ActiveDeviceId;
        return string.IsNullOrEmpty(aid) || aid == _ourDeviceId;
    }

    // "Another device became active" → stop our local host so we don't double-play.
    bool IsActiveOwner()
    {
        lock (_ownershipGate)
        {
            if (_ownsActivePlayback) return true;
            if (_projection.ActiveDeviceId != _ourDeviceId) return false;
            _ownsActivePlayback = true;
            return true;
        }
    }

    void SetActiveOwner(bool value)
    {
        lock (_ownershipGate) _ownsActivePlayback = value;
    }

    public void DeactivateIfActiveOwner()
    {
        bool deactivate;
        lock (_ownershipGate)
        {
            deactivate = _ownsActivePlayback;
            if (deactivate) _ownsActivePlayback = false;
        }
        if (!deactivate) return;
        _host.Stop();
        EmitState(EvKind.BecameInactive);
    }

    void StopStrayLocalHost(string message)
    {
        if (!_host.IsPlaying) return;
        _log.Info(message);
        _host.Stop();
    }

    void OnProjectionChanged(IPlaybackState s)
    {
        // Apply a volume change (incl. one a remote controller made to the active device) to the local host when WE are
        // active. Silent host = no-op today, but correct once real audio lands; never loops (the host has no readback).
        double vol = s.Volume;
        if (Math.Abs(vol - _lastVolume) > 0.0009) { _lastVolume = vol; _lastIntentVolume = vol; if (RouteLocal()) _host.SetVolume(vol); }

        var aid = s.ActiveDeviceId ?? "";
        if (aid == _ourDeviceId) SetActiveOwner(true);
        if (aid == _lastActive) return;
        var previousActive = _lastActive;
        _lastActive = aid;
        if (aid != _ourDeviceId && (previousActive == _ourDeviceId || IsActiveOwner()))
        {
            _log.Info("another device became active — stopping local playback");
            DeactivateIfActiveOwner();
        }
        else if (!string.IsNullOrEmpty(aid) && aid != _ourDeviceId)
        {
            StopStrayLocalHost("another device became active - stopping stray local playback");
        }
    }

    // ── IPlaybackPlayer (UI intents) — each verb routes local vs. forward ─────────────────────────────────────────────
    public async Task PlayAsync(string contextUri, int startIndex = 0, CancellationToken ct = default)
    {
        await ExecutePlayAsync(PlayRequest.Default(contextUri, startIndex), ct).ConfigureAwait(false);
    }

    public async Task PlayContextTrackAsync(string contextUri, PlaybackContextTrack track, int fallbackIndex = 0, CancellationToken ct = default)
    {
        await ExecutePlayAsync(new PlayRequest(
            contextUri,
            Math.Max(0, fallbackIndex),
            null,
            string.IsNullOrEmpty(track.Uri) ? null : track.Uri,
            string.IsNullOrEmpty(track.Uid) ? null : track.Uid), ct).ConfigureAwait(false);
    }

    public async Task PlayOrderedAsync(string contextUri, IReadOnlyList<PlaybackContextTrack> tracks, int startIndex = 0, CancellationToken ct = default)
    {
        if (tracks.Count == 0)
        {
            await PlayAsync(contextUri, startIndex, ct).ConfigureAwait(false);
            return;
        }

        var refs = ToQueuedRefs(tracks);
        int start = Math.Clamp(startIndex, 0, refs.Length - 1);
        var selected = refs[start];
        await ExecutePlayAsync(new PlayRequest(contextUri, start, refs, selected.Uri, selected.Uid), ct).ConfigureAwait(false);
    }

    public async Task PlayTrackAsync(string trackUri, CancellationToken ct = default)
    {
        if (!RouteLocal())
        {
            await ExecutePlayAsync(PlayRequest.Default(trackUri, 0), ct).ConfigureAwait(false);
            return;
        }

        var track = await HydrateOneAsync(trackUri, ct).ConfigureAwait(false);
        await LocalPlayTracksAsync(trackUri, new[] { track }, 0, ct).ConfigureAwait(false);
    }

    public Task PlayTrackAsync(Track track, CancellationToken ct = default)
    {
        if (!RouteLocal()) return ExecutePlayAsync(PlayRequest.Default(track.Uri, 0), ct);
        return LocalPlayTracksAsync(track.Uri, new[] { new QueuedTrack(track, "") }, 0, ct);
    }

    // Apple-Music-style "Start radio" (radio-inspiredby-mix-design §5.3): resolve the seed → a radio playlist, then park
    // it as the new context so the current track finishes first (playback flows into the radio via the existing Ended →
    // AutoAdvance → Next() path — no new end-of-track logic). Nothing playing (or a remote device is active) → play the
    // radio playlist through the normal routed play path instead. Returns the radio playlist uri (for the "Open playlist"
    // toast at the caller — this controller is UI-free), or null when no radio is available / nothing changed.
    public async Task<string?> StartRadioAsync(string seedUri, string? displayName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(seedUri)) return null;
        var playlistUri = await _contexts.ResolveRadioSeedAsync(seedUri, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(playlistUri)) { _log.Info("radio: seed resolved to no playlist: " + seedUri); return null; }

        // Idle here, or a remote device owns playback → play the radio playlist via the normal routed play path (which
        // forwards to the active device / honors the local-unsupported reject). "Park after current" is a LOCAL-session op
        // that only applies when WE are the active local player with a track already playing.
        if (!RouteLocal() || _session.Current is null)
        {
            await PlayAsync(playlistUri!, 0, ct).ConfigureAwait(false);
            return playlistUri;
        }

        // A track is playing locally → resolve the radio playlist and park it WITHOUT touching the audio host (§5.4).
        var resolved = await _contexts.ResolveAsync(ContextSpec.ForUri(playlistUri!), ct).ConfigureAwait(false);
        if (resolved.Count == 0) { _log.Info("radio: playlist resolved to 0 tracks: " + playlistUri); return null; }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snap = _session.SwitchContextAfterCurrent(resolved.ContextUri ?? playlistUri!, resolved.Tracks);
            // Re-point the controller's continuation tracking at the radio context. A stale in-flight prefetch for the OLD
            // context is dropped by the ReferenceEquals guard in EagerApplyContinuationAsync once _continuationFetch is nulled.
            _nextPageUrl = string.IsNullOrEmpty(resolved.NextPageUrl) ? null : resolved.NextPageUrl;
            _contextIsInfinite = resolved.IsInfinite || ContextResolve.IsInfinite(resolved.ContextUri ?? playlistUri!);
            _autoplayLatchedFor = null;
            _continuationFetch = null;
            _projection.SetContextMetadata(resolved.Metadata);          // "Playing from …" line (mirrors SetQueueContext)
            EmitSnap(snap, EvKind.QueueChanged);                        // publish the parked up-next; current track untouched
        }
        finally { _lock.Release(); }
        _log.Info($"radio: parked {playlistUri} after current ({resolved.Count} tracks)");
        return playlistUri;
    }

    public Task PauseAsync(CancellationToken ct = default)
        => RouteLocal() ? Local(() => { _host.Pause(); EmitState(EvKind.Paused); }) : Forward("pause", ct);

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        if (!RouteLocal()) { await Forward("resume", ct).ConfigureAwait(false); return; }
        await LocalResumeAsync(ct).ConfigureAwait(false);
    }

    public async Task NextAsync(CancellationToken ct = default)
    {
        if (!RouteLocal()) { await Forward("skip_next", ct).ConfigureAwait(false); return; }
        await LocalNextAsync(ct).ConfigureAwait(false);
    }

    public async Task PreviousAsync(CancellationToken ct = default)
    {
        if (!RouteLocal()) { await Forward("skip_prev", ct).ConfigureAwait(false); return; }
        await LocalPrevAsync(ct).ConfigureAwait(false);
    }

    public Task SeekAsync(long positionMs, CancellationToken ct = default)
        => RouteLocal() ? Local(() => EmitSeeked(positionMs))
                        : Forward("seek_to", ct, ("value", positionMs));

    public Task SetVolumeAsync(double volume01, CancellationToken ct = default)
    {
        volume01 = Math.Clamp(volume01, 0, 1);
        if (!double.IsNaN(_lastIntentVolume) && Math.Abs(volume01 - _lastIntentVolume) < 0.0005)
            return Done;
        _lastIntentVolume = volume01;

        _projection.NoteLocalCommand();          // optimistic: a stale cluster echo won't snap the slider back
        _projection.SetLocalVolume(volume01);    // move the slider immediately (it follows the active device's volume)
        if (RouteLocal()) return Local(() => { _host.SetVolume(volume01); EmitState(EvKind.VolumeChanged); });
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return Done;
        _remoteVolumeTx.Post(() => _ = ForwardVolumeAsync(target, volume01, CancellationToken.None));
        return Done;
    }

    async Task ForwardVolumeAsync(string target, double volume01, CancellationToken ct)
    {
        int vol = (int)Math.Round(Math.Clamp(volume01, 0, 1) * 65535);
        var r = await _outbound!.SetVolumeAsync(target, vol, ct).ConfigureAwait(false);
        if (!r.Ok) _log.Info($"outbound volume → {target}: failed ({r.Status})");
    }

    /// <summary>An EXTERNAL Windows session-volume change (SndVol / another app) reflected onto OUR device (Phase B3). We
    /// are the active output, so this only ANNOUNCES the new volume (coalesced PutState via DeviceStatePublisher) — it is
    /// NOT forwarded as a Connect volume PUT (that path controls a REMOTE device). Two independent echo-guards keep it from
    /// looping: the OnProjectionChanged epsilon guard (we set _lastVolume first) and the engine's context-GUID sink filter.</summary>
    public void OnExternalVolumeChanged(double slider01)
    {
        slider01 = Math.Clamp(slider01, 0, 1);
        _lastVolume = slider01;                  // suppress the OnProjectionChanged echo-down to the host
        _lastIntentVolume = slider01;
        _projection.NoteLocalCommand();          // a stale cluster echo must not snap the slider back (LocalCmdWindow)
        _projection.SetLocalVolume(slider01);    // move the bridge slider (Volume signal via PushState)
        EmitState(EvKind.VolumeChanged);         // announce our device volume (coalesced PutState) — no outbound PUT
    }

    public async Task SetShuffleAsync(bool on, CancellationToken ct = default)
    {
        if (!RouteLocal()) { await Forward("set_shuffling_context", ct, ("value", on)).ConfigureAwait(false); return; }
        await _lock.WaitAsync(ct).ConfigureAwait(false);   // SetShuffle rebuilds the context list — one lock per mutation
        try { EmitSnap(_session.SetShuffle(on), EvKind.OptionsChanged); }
        finally { _lock.Release(); }
    }

    public async Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default)
    {
        if (RouteLocal())
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try { EmitSnap(_session.SetRepeat(mode), EvKind.OptionsChanged); }
            finally { _lock.Release(); }
            return;
        }
        // Remote: split + always send BOTH explicit modes so Track->Off / Track->Context can't leave the target stuck.
        await Forward("set_repeating_track", ct, ("value", mode == RepeatMode.Track)).ConfigureAwait(false);
        await Forward("set_repeating_context", ct, ("value", mode == RepeatMode.Context)).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(string trackUri, CancellationToken ct = default)
    {
        if (!RouteLocal()) { await ForwardAddToQueueAsync(trackUri, ct).ConfigureAwait(false); return; }
        var queued = await HydrateOneAsync(trackUri, ct).ConfigureAwait(false);
        await EnqueueLocalAsync(queued, ct).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(Track track, CancellationToken ct = default)
    {
        if (!RouteLocal()) { await ForwardAddToQueueAsync(track.Uri, ct).ConfigureAwait(false); return; }
        await EnqueueLocalAsync(new QueuedTrack(track, ""), ct).ConfigureAwait(false);
    }

    async Task EnqueueLocalAsync(QueuedTrack queued, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session.Current is null)   // add-to-queue while idle → start playing it (rule §3)
            {
                if (RejectLocalPlay()) return;   // can't start local playback → toast + abort (don't seed a phantom local queue)
                SetQueueContext(queued.Track.Uri, new[] { queued }, 0);
                await LoadAndPlayCurrentAsync(EvKind.Started, ct).ConfigureAwait(false);
            }
            else EmitSnap(_session.EnqueueUser(new[] { queued }), EvKind.QueueChanged);   // active device mints the q-uid (§7.4)
            WarmFastTrack(queued.Track, "enqueue");
        }
        finally { _lock.Release(); }
    }

    // play-next: insert at the FRONT of the user queue. LOCAL → EnqueueNext (head-insert, before existing queue). REMOTE →
    // a full set_queue snapshot: the inserted tracks + the existing user queue as provider:"queue", then the resident
    // context continuation as provider:"context". prev_tracks is empty (no history model); queue_revision echoes the cluster.
    public async Task PlayNextAsync(IReadOnlyList<PlaybackContextTrack> tracks, CancellationToken ct = default)
    {
        var refs = ToQueuedRefs(tracks);
        if (refs.Length == 0) return;
        if (RouteLocal())
        {
            if (RejectLocalPlay()) return;   // local play-next would seed a local queue that can never play → toast + abort
            var hydrated = await _contexts.HydrateAsync(refs, ct).ConfigureAwait(false);
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var snap = _session.EnqueueNextUser(hydrated);
                if (hydrated.Count > 0) WarmFastTrack(hydrated[0].Track, "play-next");
                EmitSnap(snap, EvKind.QueueChanged);
            }
            finally { _lock.Release(); }
            return;
        }
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return;
        // Rewrite the ACTIVE device's queue as the cluster reports it (its real prev/next, uid+provider preserved) with our
        // tracks inserted at the FRONT of the up-next — NOT our local QueueCore, which is stale/empty when we're a viewer.
        // prev_tracks + the context continuation are echoed verbatim so the remote queue isn't clobbered, and queue_revision
        // comes from the same cluster snapshot (so it matches the server's; remote routing can't happen without a cluster).
        var clusterPrev = _projection.ClusterPrevTracks;
        var clusterNext = _projection.ClusterNextTracks;
        var prev = new List<QueueWireEntry>(clusterPrev.Count);
        foreach (var t in clusterPrev) prev.Add(new QueueWireEntry(t.Uri, t.Uid, t.Provider == "queue", t.Metadata));
        var next = new List<QueueWireEntry>(refs.Length + clusterNext.Count);
        foreach (var r in refs)        next.Add(new QueueWireEntry(r.Uri, r.Uid, true, r.Metadata));                 // inserted play-next → queue
        foreach (var t in clusterNext) next.Add(new QueueWireEntry(t.Uri, t.Uid, t.Provider == "queue", t.Metadata));// the device's queue, verbatim
        var json = OutboundEnvelope.SetQueue(_ourDeviceId, ParseRevision(), prev, next, NewId(), NewId(), Now(), NewId());
        var r2 = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r2.Ok) _log.Info($"outbound set_queue → {target}: failed ({r2.Status})");
    }

    // Skip-in-place to a queue/history row (§6). Active: session cursor move + fast-start (never a rebuild). Viewer: forward
    // next_track with the target row (FIXTURE-B — uid-first, no play/skip_to). Idle: no-op (the id resolves to nothing).
    public async Task SkipToQueueItemAsync(QueueItemId id, CancellationToken ct = default)
    {
        if (id.IsNone) return;
        if (RouteLocal())
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _projection.NoteLocalCommand();
                if (_session.SkipToItem(id) is { } snap)
                {
                    _snap = snap;
                    await LoadAndPlayCurrentAsync(EvKind.TrackChanged, ct).ConfigureAwait(false);
                }
            }
            finally { _lock.Release(); }
            return;
        }
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return;
        if (!_projection.TryGetViewerRow(id, out var row)) { _log.Info("skip-to: viewer row not found for id " + id.Value); return; }
        var json = OutboundEnvelope.NextTrack(row, _ourDeviceId, NewId(), NewId(), Now());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) { _log.Info($"outbound next_track → {target}: failed ({r.Status})"); OnRemoteCommandFailed?.Invoke(); }
    }

    public async Task MoveQueueItemAsync(QueueItemId id, int newPos, CancellationToken ct = default)
    {
        if (!RouteLocal()) { _log.Info("queue move ignored — another device is active"); return; }   // the active device owns its queue
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { if (_session.MoveUserItem(id, newPos) is { } snap) EmitSnap(snap, EvKind.QueueChanged); }
        finally { _lock.Release(); }
    }

    public async Task RemoveQueueItemAsync(QueueItemId id, CancellationToken ct = default)
    {
        if (!RouteLocal()) { _log.Info("queue remove ignored — another device is active"); return; }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { if (_session.RemoveItem(id) is { } snap) EmitSnap(snap, EvKind.QueueChanged); }
        finally { _lock.Release(); }
    }

    // Clear the user queue / history (§10.1) — active-device local session ops (one revision bump, atomic publish). Viewer:
    // no-op (no wire verb; the panel hides the button in viewer mode).
    public async Task ClearQueueAsync(CancellationToken ct = default)
    {
        if (!RouteLocal()) { _log.Info("queue clear ignored — another device is active"); return; }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { EmitSnap(_session.ClearUserQueue(), EvKind.QueueChanged); }
        finally { _lock.Release(); }
    }

    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        if (!RouteLocal()) { _log.Info("history clear ignored — another device is active"); return; }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { EmitSnap(_session.ClearHistory(), EvKind.QueueChanged); }
        finally { _lock.Release(); }
    }

    /// <summary>Device-picker hand-off. Self = ghost-resume (the HTTP transfer endpoint 400s for self); another = forward
    /// the transfer + stop our local host so we don't double-play.</summary>
    public async Task TransferToAsync(string targetDeviceId, CancellationToken ct = default)
    {
        if (targetDeviceId == _ourDeviceId)
        {
            if (RejectLocalPlay()) return;   // transfer-to-this-device = local playback, which is unsupported → toast + abort
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try { if (_session.Current is not null) { _host.Play(); EmitState(EvKind.Resumed); } else await GhostResumeAsync(ct).ConfigureAwait(false); }
            finally { _lock.Release(); }
            return;
        }
        bool wasActiveOwner = IsActiveOwner();
        bool ok = await TryForwardTransferAsync(targetDeviceId, ct).ConfigureAwait(false);
        if (!ok) return;
        if (wasActiveOwner) DeactivateIfActiveOwner();
        else StopStrayLocalHost("remote transfer accepted while Wavee was not active - stopping stray local playback");
    }

    // ── Inbound remote commands (WE are the target) — ALWAYS local, regardless of the routing rule ───────────────────
    public void HandleRemoteCommand(in ConnectCommand cmd)
    {
        switch (cmd.Kind)
        {
            case ConnectCmd.Pause: _host.Pause(); EmitState(EvKind.Paused); break;
            case ConnectCmd.Resume: _ = LocalResumeAsync(); break;
            case ConnectCmd.SkipNext: _ = HandleInboundSkipNextAsync(cmd); break;   // next_track w/ a row payload → skip-to-uid; bare → advance one (F7)
            case ConnectCmd.SkipPrev: _ = LocalPrevAsync(); break;
            case ConnectCmd.SeekTo: EmitSeeked(cmd.SeekToMs); break;
            // Session mutations off the dealer thread MUST take _lock (they rebuild the context list; a bare toggle would
            // race a lock-holding local AutoAdvance) — route through the locked async helpers (F: one lock per mutation).
            case ConnectCmd.SetShufflingContext: { bool on = cmd.BoolArg; _ = RemoteSetShuffleAsync(on); } break;
            case ConnectCmd.SetRepeatingContext: { var m = cmd.BoolArg ? RepeatMode.Context : RepeatMode.Off; _ = RemoteSetRepeatAsync(m); } break;
            case ConnectCmd.SetRepeatingTrack: { var m = cmd.BoolArg ? RepeatMode.Track : RepeatMode.Off; _ = RemoteSetRepeatAsync(m); } break;
            case ConnectCmd.Play:
            case ConnectCmd.Transfer: _ = HandleInboundPlayOrTransferAsync(cmd); break;
            case ConnectCmd.AddToQueue: _ = HandleAddToQueueAsync(cmd); break;
            case ConnectCmd.SetQueue: _ = HandleSetQueueAsync(cmd); break;
            case ConnectCmd.UpdateContext: _ = HandleUpdateContextAsync(cmd); break;
            case ConnectCmd.SetOptions: { var payload = cmd.Payload; _ = HandleSetOptionsAsync(payload); } break;
            default: _log.Info("controller: unhandled remote command " + cmd.Kind); break;
        }
    }

    // Inbound next_track / skip_next (F7): a payload (command.track {uri,uid}) is a row-jump → skip-to-uid + play; a bare
    // skip_next advances one exactly as before. skip_prev never carries a payload (unchanged).
    async Task HandleInboundSkipNextAsync(ConnectCommand cmd)
    {
        if (string.IsNullOrEmpty(cmd.TrackUri) && string.IsNullOrEmpty(cmd.TrackUid)) { await LocalNextAsync().ConfigureAwait(false); return; }
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _projection.NoteLocalCommand();
            if (_session.SkipToUid(cmd.TrackUid, cmd.TrackUri) is { } snap)
            {
                _snap = snap;
                await LoadAndPlayCurrentAsync(EvKind.TrackChanged, default).ConfigureAwait(false);
                return;
            }
        }
        finally { _lock.Release(); }
        // identity miss (the target isn't in our resolved session) → fall back to a plain advance.
        _log.Info($"inbound next_track: row uid={cmd.TrackUid} uri={cmd.TrackUri} not found in session — advancing one");
        await LocalNextAsync().ConfigureAwait(false);
    }

    async Task HandleInboundPlayOrTransferAsync(ConnectCommand cmd)
    {
        try
        {
            if (ExtractPlaySpec(cmd.Payload) is { } spec) await LocalPlaySpecAsync(spec, default).ConfigureAwait(false);
            else await LocalResumeAsync(default).ConfigureAwait(false);   // bare transfer → ghost-resume the cluster state here
        }
        catch (Exception ex) { _log.Info("controller inbound play/transfer error: " + ex.Message); }
    }

    // add_to_queue: append one track to the user queue — or, if nothing is loaded, start playing it (the idle-start rule).
    async Task HandleAddToQueueAsync(ConnectCommand cmd)
    {
        try
        {
            if (ParseQueueTrack(cmd.Payload) is not { } qref) return;
            var hydrated = await _contexts.HydrateAsync(new[] { qref }, default).ConfigureAwait(false);
            if (hydrated.Count == 0) return;
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session.Current is null)
                {
                    if (RejectLocalPlay()) return;   // inbound add-to-queue while idle would start local playback → toast + abort
                    SetQueueContext(hydrated[0].Uri, hydrated, 0);
                    await LoadAndPlayCurrentAsync(EvKind.Started, default).ConfigureAwait(false);
                }
                else EmitSnap(_session.EnqueueUser(hydrated), EvKind.QueueChanged);   // active device mints q-uids for uid:"" rows (§7.4)
            }
            finally { _lock.Release(); }
        }
        catch (Exception ex) { _log.Info("controller add_to_queue error: " + ex.Message); }
    }

    // set_queue (F8): full reconcile of ALL of next_tracks (queue rows → user queue by uid, context rows → Upcoming, autoplay
    // tail + delimiter/meta markers preserved). The current track is untouched (set_queue never changes what's playing).
    async Task HandleSetQueueAsync(ConnectCommand cmd)
    {
        try
        {
            var prev = ParseWireEntries(cmd.Payload, "prev_tracks");
            var next = ParseWireEntries(cmd.Payload, "next_tracks");
            if (next.Count == 0 && prev.Count == 0) return;
            string revision = ParseQueueRevisionString(cmd.Payload);
            await _lock.WaitAsync().ConfigureAwait(false);
            try { EmitSnap(_session.ApplySetQueue(prev, next, revision), EvKind.QueueChanged); }
            finally { _lock.Release(); }
        }
        catch (Exception ex) { _log.Info("controller set_queue error: " + ex.Message); }
    }

    // update_context: the context's tracks changed (e.g. the playlist was edited) — re-resolve and keep playing the same
    // track (reposition the cursor to it in the new order); if it's gone, start the new context from the top.
    async Task HandleUpdateContextAsync(ConnectCommand cmd)
    {
        try
        {
            if (ExtractPlaySpec(cmd.Payload) is not { } spec) return;
            var resolved = await _contexts.ResolveAsync(spec, default).ConfigureAwait(false);
            if (resolved.Count == 0) return;
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                string? curUri = _session.Current?.Uri;
                int found = curUri is null ? -1 : IndexOfUri(resolved.Tracks, curUri);
                SetQueueContext(resolved.ContextUri ?? spec.Uri, resolved.Tracks, found < 0 ? 0 : found,
                    resolved.NextPageUrl, resolved.IsInfinite, resolved.Metadata);
                EmitSnap(_snap, EvKind.QueueChanged);
            }
            finally { _lock.Release(); }
        }
        catch (Exception ex) { _log.Info("controller update_context error: " + ex.Message); }
    }

    // set_options: apply shuffle + repeat (the desktop sends explicit shuffling_context / repeating_context / repeating_track).
    // Parse off-lock (immutable JSON), then apply the session mutations under _lock (they rebuild the context list, F7).
    async Task HandleSetOptionsAsync(byte[] payload)
    {
        try
        {
            bool? shuffle = null; RepeatMode? repeat = null;
            using (var doc = JsonDocument.Parse(payload))
            {
                if (!doc.RootElement.TryGetProperty("command", out var c)) return;
                if (c.TryGetProperty("shuffling_context", out var sh) && sh.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    shuffle = sh.GetBoolean();
                bool hasRepTrack = c.TryGetProperty("repeating_track", out var rt);
                bool hasRepCtx = c.TryGetProperty("repeating_context", out var rc);
                if (hasRepTrack || hasRepCtx)
                {
                    bool repTrack = hasRepTrack && rt.ValueKind == JsonValueKind.True;
                    bool repCtx = hasRepCtx && rc.ValueKind == JsonValueKind.True;
                    repeat = repTrack ? RepeatMode.Track : repCtx ? RepeatMode.Context : RepeatMode.Off;
                }
            }
            if (shuffle is null && repeat is null) return;
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                QueueSnapshot snap = _snap;
                if (shuffle is { } s) snap = _session.SetShuffle(s);
                if (repeat is { } r) snap = _session.SetRepeat(r);
                EmitSnap(snap, EvKind.OptionsChanged);
            }
            finally { _lock.Release(); }
        }
        catch (Exception ex) { _log.Info("controller set_options error: " + ex.Message); }
    }

    // Inbound shuffle/repeat off the dealer thread — take _lock (SetShuffle/SetRepeat rebuild the context list, F7).
    async Task RemoteSetShuffleAsync(bool on)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { EmitSnap(_session.SetShuffle(on), EvKind.OptionsChanged); }
        finally { _lock.Release(); }
    }

    async Task RemoteSetRepeatAsync(RepeatMode mode)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { EmitSnap(_session.SetRepeat(mode), EvKind.OptionsChanged); }
        finally { _lock.Release(); }
    }

    // ── local execution primitives (shared by the public verbs + inbound handling) ───────────────────────────────────
    // Seed the session with a resolved context. keepUserQueue is always true (§4.7 — Spotify parity: a new context keeps the
    // user queue). The context display metadata rides alongside via SetContextMetadata; the atomic publish happens at the
    // caller's LoadAndPlayCurrent / EmitSnap, never here (F3: no split publish).
    void SetQueueContext(string uri, IReadOnlyList<QueuedTrack> tracks, int startIndex,
        string? nextPageUrl = null, bool isInfinite = false, IReadOnlyDictionary<string, string>? metadata = null)
    {
        _snap = _session.SetContext(uri, tracks, startIndex);
        _nextPageUrl = string.IsNullOrEmpty(nextPageUrl) ? null : nextPageUrl;
        _contextIsInfinite = isInfinite || ContextResolve.IsInfinite(uri);
        _autoplayLatchedFor = null;
        _continuationFetch = null;
        _projection.SetContextMetadata(metadata);
        DiagnoseQueue("controller.set-context");
    }

    // §7.3: resolve, then honor an identity-strict skip target. ResolveAsync returns StartIndex = -1 on identity miss (the
    // blind index fallback is gone, F2). While hunting the skip target we page deeper than MaxEagerPages (bounded); on a
    // final miss (a regenerated dynamic context) we patch the clicked track in as current rather than play an unrelated row.
    const int SkipHuntMaxPages = 40;
    async Task LocalPlaySpecAsync(ContextSpec spec, CancellationToken ct)
    {
        var resolved = await _contexts.ResolveAsync(spec, ct).ConfigureAwait(false);
        if (resolved.Count == 0) { _log.Info("play: context resolved to 0 tracks: " + spec.Uri); return; }

        IReadOnlyList<QueuedTrack> tracks = resolved.Tracks;
        int start = resolved.StartIndex;
        string? nextPage = resolved.NextPageUrl;
        bool hasSkipTarget = !string.IsNullOrEmpty(spec.SkipToTrackUid) || !string.IsNullOrEmpty(spec.SkipToTrackUri);

        if (start < 0 && hasSkipTarget && !resolved.IsInfinite && !string.IsNullOrEmpty(nextPage))
        {
            var acc = new List<QueuedTrack>(tracks);
            int pages = 0;
            while (start < 0 && !string.IsNullOrEmpty(nextPage) && pages < SkipHuntMaxPages)
            {
                var page = await _contexts.LoadMoreAsync(nextPage!, ct).ConfigureAwait(false);
                if (page.Tracks.Count > 0)
                {
                    acc.AddRange(page.Tracks);
                    start = ContextResolve.FindStartIndex(acc, spec.SkipToTrackUri, spec.SkipToTrackUid);
                }
                nextPage = page.NextPageUrl;
                pages++;
            }
            tracks = acc;
            if (start >= 0) _log.Info($"skip target found after paging {pages} extra pages ({tracks.Count} tracks)");
        }

        if (start < 0 && hasSkipTarget)
        {
            // §7.3.2: identity miss — patch the clicked track as current (context_patched), never a blind index.
            var patched = await BuildPatchedTrackAsync(spec, ct).ConfigureAwait(false);
            var list = new List<QueuedTrack>(tracks.Count + 1) { patched };
            list.AddRange(tracks);
            tracks = list;
            start = 0;
            _log.Info($"queue.skip-miss: patched {spec.SkipToTrackUri ?? spec.SkipToTrackUid} as current over {resolved.ContextUri ?? spec.Uri}");
            PlaybackBucketDiagnostics.Continuation("queue.skip-miss", "skip target not resolved; patched clicked track as current",
                WaveeLogField.Of("target", spec.SkipToTrackUri ?? spec.SkipToTrackUid ?? ""),
                WaveeLogField.Of("ctx", resolved.ContextUri ?? spec.Uri));
        }

        if (start < 0) start = 0;   // no skip target at all → start at the top
        await LocalPlayTracksAsync(resolved.ContextUri ?? spec.Uri, tracks, start, ct,
            nextPage, resolved.IsInfinite, resolved.Metadata).ConfigureAwait(false);
    }

    // Build the clicked track as a context row patched in as current (§7.3.2): hydrate for display, tag context_patched.
    async Task<QueuedTrack> BuildPatchedTrackAsync(ContextSpec spec, CancellationToken ct)
    {
        string uri = spec.SkipToTrackUri ?? "";
        var meta = new Dictionary<string, string>(StringComparer.Ordinal) { ["context_patched"] = "true" };
        if (string.IsNullOrEmpty(uri)) return new QueuedTrack(ContextResolve.Synthetic(spec.SkipToTrackUid ?? ""), spec.SkipToTrackUid ?? "", "context", meta);
        var q = await HydrateOneAsync(uri, ct).ConfigureAwait(false);
        return new QueuedTrack(q.Track, spec.SkipToTrackUid ?? "", "context", meta);
    }

    async Task LocalPlayTracksAsync(string contextUri, IReadOnlyList<QueuedTrack> tracks, int startIndex, CancellationToken ct,
        string? nextPageUrl = null, bool isInfinite = false, IReadOnlyDictionary<string, string>? metadata = null)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SetQueueContext(contextUri, tracks, startIndex, nextPageUrl, isInfinite, metadata);
            await LoadAndPlayCurrentAsync(EvKind.Started, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    async Task<QueuedTrack> HydrateOneAsync(string uri, CancellationToken ct)
    {
        try
        {
            var hydrated = await _contexts.HydrateAsync(new[] { new QueuedRef(uri, "") }, ct).ConfigureAwait(false);
            if (hydrated.Count > 0) return hydrated[0];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.Info("track hydrate failed; falling back to uri placeholder: " + ex.Message); }
        return new QueuedTrack(SyntheticTrack(uri), "");
    }

    async Task LocalResumeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session.Current is not null) { if (RejectLocalPlay()) return; _host.Play(); EmitState(EvKind.Resumed); }   // we have a local context → normal resume
            else await GhostResumeAsync(ct).ConfigureAwait(false);   // cold/ghost → seed from the cluster snapshot
        }
        finally { _lock.Release(); }
    }

    async Task LocalNextAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _projection.NoteLocalCommand();
            _snap = _session.Next();
            if (_snap.Current is not null) await LoadAndPlayCurrentAsync(EvKind.TrackChanged, ct).ConfigureAwait(false);
            else if (await TryContinueContextAsync(ct).ConfigureAwait(false)) { }
            else { _host.Stop(); Emit(BuildEvent(EvKind.Ended, null, 0, reasonEnd: "endplay")); }   // end-of-context
        }
        finally { _lock.Release(); }
    }

    async Task LocalPrevAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _projection.NoteLocalCommand();
            // Desktop semantics: >3 s into the track, "previous" restarts the current track instead of stepping back.
            if (_host.PositionMs > 3000) { _host.Seek(0); return; }
            if (_session.Prev() is { } snap) { _snap = snap; await LoadAndPlayCurrentAsync(EvKind.TrackChanged, ct).ConfigureAwait(false); }
        }
        finally { _lock.Release(); }
    }

    // Ghost resume: the play button while nothing is loaded locally → seed context/track/position + the next-up queue
    // from the cluster snapshot, then play locally (we take over). Caller holds _lock.
    async Task GhostResumeAsync(CancellationToken ct)
    {
        if (RejectLocalPlay()) return;   // local audio unsupported → toast + abort (covers cold resume / self-transfer / bare inbound transfer)
        var track = _projection.CurrentTrack;
        if (track is null) { _log.Info("ghost resume: nothing in the cluster to resume"); return; }
        var ctxUri = _projection.ContextUri ?? track.Uri;
        SeedSessionFromCluster(track, ctxUri);
        AudioStreamHandle handle;
        try { handle = await _resolver.ResolveAsync(track, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ReportPlaybackError(ex); return; }   // no silent drop — surface a typed reason
        _host.Load(handle);
        long pos = _projection.PositionMs;
        if (track.Uri.StartsWith("spotify:episode:", StringComparison.Ordinal) && EpisodeResumeMicros is { } resumeFn)
        {
            try
            {
                long micros = await resumeFn(track.Uri, ct).ConfigureAwait(false);
                if (micros > 0) pos = micros / 1000;
            }
            catch (Exception ex) { _log.Info("episode resume lookup failed: " + ex.Message); }
        }
        if (pos > 0) _host.Seek(pos);
        _host.Play();
        MintCommand("playbtn");
        _currentIds = MintPlaybackIds(track);
        Emit(BuildEvent(EvKind.Started, track, pos));   // the atomic publish carries the session snapshot seeded above
    }

    // Full session recovery from the last cluster (§8, F9): replay the raw cluster rows through ReplaceFromCluster so the
    // user queue is filed into _userQueue IN WIRE ORDER (drain-first preserved), the context continuation + autoplay tail
    // land in Upcoming (AutoplayContextUri set), and prev_tracks restore History — NOT SetContext over _projection.Queue,
    // which relabels queue rows as context, drops drain-first + the autoplay context, and (when we're the active device)
    // reads an empty windowed queue. Falls back to a single-track context when no cluster has been folded. Caller holds _lock.
    void SeedSessionFromCluster(Track current, string ctxUri)
    {
        if (_projection.LastCluster is { HasTrack: true } c)
            _snap = _session.ReplaceFromCluster(c, current);
        else
            _snap = _session.SetContext(ctxUri, new[] { new QueuedTrack(current, "") }, 0);
        _nextPageUrl = null;
        _contextIsInfinite = ContextResolve.IsInfinite(_session.ContextUri ?? ctxUri);
        _autoplayLatchedFor = null;
        _continuationFetch = null;
        _projection.SetContextMetadata(null);
        DiagnoseQueue("controller.recover-from-cluster");
    }

    async Task MaybeSeekEpisodeResumeAsync(Track track, CancellationToken ct)
    {
        if (!track.Uri.StartsWith("spotify:episode:", StringComparison.Ordinal) || EpisodeResumeMicros is not { } fn)
            return;
        try
        {
            long micros = await fn(track.Uri, ct).ConfigureAwait(false);
            if (micros > 0) _host.Seek(micros / 1000);
        }
        catch (Exception ex) { _log.Info("episode resume lookup failed: " + ex.Message); }
    }

    void MintCommand(string reasonStart = "clickrow")
    {
        _commandIdHex = PlaybackIds.MintCommandId();
        _reasonStart = reasonStart;
    }

    PlaybackIds MintPlaybackIds(Track track, byte[]? mediaId = null)
    {
        var ctx = _session.ContextUri;
        if (ctx != _idsSessionContext)
        {
            _idsSessionContext = ctx;
        }
        return PlaybackIds.Mint(_commandIdHex, mediaId);
    }

    PlaybackEvent BuildEvent(EvKind kind, Track? track, long atMs, byte[]? mediaId = null,
        int bitrateKbps = 0, string audioFormat = "", long durationMs = 0, byte[]? fileId = null, string reasonEnd = "",
        long seekToMs = -1)
    {
        // F6: read the provider straight off the atomic snapshot's current (the dead ternary + row scan are gone).
        var provider = (_snap.Current?.Provider ?? QueueProvider.Context).ToWire();
        return new PlaybackEvent(kind, track, atMs, _currentIds, _reasonStart, reasonEnd, ParseContextKind(_snap.ContextUri),
            mediaId, bitrateKbps, audioFormat, durationMs, fileId, provider, true, seekToMs);
    }

    void EmitSeeked(long targetMs)
    {
        _projection.NoteLocalCommand();
        long fromMs = _host.PositionMs;
        _host.Seek(targetMs);
        Emit(BuildEvent(EvKind.Seeked, _snap.Current?.Track, fromMs, seekToMs: targetMs));
    }

    static string ParseContextKind(string? contextUri)
    {
        if (string.IsNullOrEmpty(contextUri)) return "playlist";
        var parts = contextUri.Split(':');
        return parts.Length >= 3 ? parts[1] : "playlist";
    }

    async Task LoadAndPlayCurrentAsync(EvKind kind, CancellationToken ct, long resumePositionMs = -1)
    {
        if (RejectLocalPlay()) return;   // local audio unsupported → toast + abort (covers play / next / prev / enqueue-idle / inbound)
        var cur = _session.Current;
        if (cur is null) { _host.Stop(); return; }

        byte[]? mediaId = null;
        byte[]? fileId = null;
        int bitrateKbps = 160;
        string audioFormat = "";
        long durationMs = cur.DurationMs;
        if (MetaResolver is { } metaFn)
        {
            try
            {
                if (await metaFn(cur, ct).ConfigureAwait(false) is { } meta)
                {
                    mediaId = meta.MediaId;
                    fileId = meta.FileId;
                    bitrateKbps = meta.BitrateKbps;
                    audioFormat = meta.AudioFormat;
                    durationMs = meta.DurationMs > 0 ? meta.DurationMs : durationMs;
                }
            }
            catch { }
        }
        if (string.IsNullOrEmpty(_commandIdHex)) MintCommand(kind == EvKind.TrackChanged ? "trackdone" : "playbtn");
        _currentIds = MintPlaybackIds(cur, mediaId);

        if (_fast is not null)
        {
            // Instant-start: play the clear head immediately; the encrypted body (key + CDN) resolves in parallel and is
            // supplied to the host when ready — hiding key/derive latency behind the head's ~3 s of audio.
            FastStartPlan plan;
            try { plan = await _fast.ResolveFastAsync(cur, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ReportPlaybackError(ex); return; }
            var loadStartedTicks = Stopwatch.GetTimestamp();
            _host.LoadFastStart(plan.Start);
            _host.Play();
            if (resumePositionMs > 0) _host.Seek(resumePositionMs);
            else await MaybeSeekEpisodeResumeAsync(cur, ct).ConfigureAwait(false);
            WarmUpcomingFastTrack("after-start");
            Emit(BuildEvent(kind, cur, Math.Max(0, resumePositionMs), mediaId, bitrateKbps, audioFormat, durationMs, fileId));
            SchedulePreparedNext("after-start");
            MaybeStartContinuationFetch();
            _ = SupplyBodyWhenReadyAsync(plan.Body, cur.Uri, loadStartedTicks, plan.Start.HeadBytes.Length);
            return;
        }

        AudioStreamHandle handle;
        try { handle = await _resolver.ResolveAsync(cur, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ReportPlaybackError(ex); return; }   // no silent drop — surface a typed reason
        _host.Load(handle);
        _host.Play();
        if (resumePositionMs > 0) _host.Seek(resumePositionMs);
        else await MaybeSeekEpisodeResumeAsync(cur, ct).ConfigureAwait(false);
        WarmUpcomingFastTrack("after-start");
        Emit(BuildEvent(kind, cur, Math.Max(0, resumePositionMs), mediaId, bitrateKbps, audioFormat, durationMs, fileId));
        SchedulePreparedNext("after-start");
        MaybeStartContinuationFetch();
    }

    void MaybeStartContinuationFetch()
    {
        if (_session.Current is null)
        {
            PlaybackBucketDiagnostics.Continuation("continuation.skip", "no current track");
            return;
        }
        if (_session.RemainingInContext > 5)
        {
            PlaybackBucketDiagnostics.Continuation("continuation.skip", "context still has enough upcoming tracks",
                WaveeLogField.Of("remainingContext", _session.RemainingInContext),
                WaveeLogField.Of("ctx", _session.ContextUri ?? ""),
                WaveeLogField.Of("current", _session.Current.Uri));
            return;
        }
        if (_continuationFetch is { } existing && !existing.IsCompleted)
        {
            PlaybackBucketDiagnostics.Continuation("continuation.skip", "continuation fetch already running",
                WaveeLogField.Of("remainingContext", _session.RemainingInContext),
                WaveeLogField.Of("ctx", _session.ContextUri ?? ""),
                WaveeLogField.Of("current", _session.Current.Uri));
            return;
        }
        if (_continuationFetch is { IsCompleted: true })
        {
            PlaybackBucketDiagnostics.Continuation("continuation.skip", "completed continuation fetch waiting for track-end consumer",
                WaveeLogField.Of("remainingContext", _session.RemainingInContext),
                WaveeLogField.Of("ctx", _session.ContextUri ?? ""),
                WaveeLogField.Of("current", _session.Current.Uri));
            return;
        }
        var fetch = StartContinuationFetch(forceAutoplay: false);
        if (fetch is not null) _ = EagerApplyContinuationAsync(fetch);
    }

    // Append the prefetched continuation (next context page / autoplay station) to the queue AS SOON AS it resolves —
    // not deferred to track-end — so the up-next list shows the upcoming tracks while the current one still plays (the
    // "Autoplaying similar music" preview). Append-only: the cursor doesn't move, so nothing changes what's playing.
    // ReferenceEquals-guarded so it never double-applies with the track-end TryContinueContextAsync path.
    async Task EagerApplyContinuationAsync(Task<ResolvedContext> fetch)
    {
        ResolvedContext result;
        try { result = await fetch.ConfigureAwait(false); }
        catch (Exception ex)
        {
            PlaybackBucketDiagnostics.Continuation("continuation.eager-fault", "eager continuation fetch faulted",
                WaveeLogField.Of("error", ex.GetType().Name),
                WaveeLogField.Of("detail", ex.Message));
            return;   // a fault surfaces on the track-end path's own await instead
        }

        bool held = false;
        try
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            held = true;
            if (!ReferenceEquals(_continuationFetch, fetch))
            {
                PlaybackBucketDiagnostics.Continuation("continuation.eager-skip", "prefetch was already consumed or superseded",
                    WaveeLogField.Of("resultCount", result.Count));
                return;   // already consumed/superseded by track-end
            }
            _continuationFetch = null;
            PlaybackBucketDiagnostics.Continuation("continuation.eager-apply", "applying prefetched continuation before track-end",
                WaveeLogField.Of("resultCount", result.Count),
                WaveeLogField.Of("resultContext", result.ContextUri ?? ""),
                WaveeLogField.Of("nextPage", result.NextPageUrl ?? ""),
                WaveeLogField.Of("isInfinite", result.IsInfinite));
            ApplyContinuation(result);
        }
        catch (Exception ex)
        {
            PlaybackBucketDiagnostics.Continuation("continuation.eager-error", "eager continuation apply failed",
                WaveeLogField.Of("error", ex.GetType().Name),
                WaveeLogField.Of("detail", ex.Message));
        }
        finally { if (held) _lock.Release(); }
    }

    Task<ResolvedContext>? StartContinuationFetch(bool forceAutoplay)
    {
        var ctx = _session.ContextUri;
        if (string.IsNullOrEmpty(ctx))
        {
            PlaybackBucketDiagnostics.Continuation("continuation.none", "no context uri; cannot fetch continuation");
            return null;
        }

        if (!forceAutoplay && !string.IsNullOrEmpty(_nextPageUrl))
        {
            var page = _nextPageUrl!;
            _log.Info("continuation: prefetching next context page " + page);
            PlaybackBucketDiagnostics.Continuation("continuation.fetch-page", "prefetching next context page",
                WaveeLogField.Of("ctx", ctx),
                WaveeLogField.Of("page", page),
                WaveeLogField.Of("remainingContext", _session.RemainingInContext),
                WaveeLogField.Of("current", _session.Current?.Uri ?? ""));
            return _continuationFetch = FetchNextPageAsync(page, _contextIsInfinite);
        }

        if (!CanAutoplay(ctx))
        {
            PlaybackBucketDiagnostics.Continuation("continuation.none", "autoplay not eligible",
                WaveeLogField.Of("ctx", ctx),
                WaveeLogField.Of("remainingContext", _session.RemainingInContext),
                WaveeLogField.Of("isInfinite", _contextIsInfinite || ContextResolve.IsInfinite(ctx)),
                WaveeLogField.Of("latchedFor", _autoplayLatchedFor ?? ""),
                WaveeLogField.Of("enabled", AutoplayEnabled?.Invoke() ?? true));
            return null;
        }
        _autoplayLatchedFor = ctx;
        var recent = _session.RecentUris(5);
        _log.Info("continuation: prefetching autoplay for " + ctx);
        PlaybackBucketDiagnostics.Continuation("continuation.fetch-autoplay", "prefetching autoplay",
            WaveeLogField.Of("ctx", ctx),
            WaveeLogField.Of("remainingContext", _session.RemainingInContext),
            WaveeLogField.Of("recent", string.Join(",", recent)),
            WaveeLogField.Of("current", _session.Current?.Uri ?? ""));
        return _continuationFetch = FetchAutoplayAsync(ctx, recent);
    }

    bool CanAutoplay(string contextUri)
    {
        if (_contextIsInfinite || ContextResolve.IsInfinite(contextUri)) return false;
        if (_autoplayLatchedFor == contextUri) return false;
        return AutoplayEnabled?.Invoke() ?? true;
    }

    async Task<ResolvedContext> FetchNextPageAsync(string nextPageUrl, bool isInfinite)
    {
        try
        {
            var page = await _contexts.LoadMoreAsync(nextPageUrl).ConfigureAwait(false);
            PlaybackBucketDiagnostics.Continuation("continuation.page-result", "next context page resolved",
                WaveeLogField.Of("count", page.Tracks.Count),
                WaveeLogField.Of("nextPage", page.NextPageUrl ?? ""),
                WaveeLogField.Of("isInfinite", isInfinite));
            return new ResolvedContext(page.Tracks, 0, null, page.NextPageUrl, isInfinite);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Info("continuation page fetch failed: " + ex.Message);
            PlaybackBucketDiagnostics.Continuation("continuation.page-error", "next context page fetch failed",
                WaveeLogField.Of("error", ex.GetType().Name),
                WaveeLogField.Of("detail", ex.Message));
            return ResolvedContext.Empty;
        }
    }

    async Task<ResolvedContext> FetchAutoplayAsync(string contextUri, IReadOnlyList<string> recent)
    {
        try
        {
            var result = await _contexts.ResolveAutoplayAsync(contextUri, recent).ConfigureAwait(false);
            if (result.Count == 0) _log.Info("autoplay returned no tracks for " + contextUri);
            PlaybackBucketDiagnostics.Continuation("continuation.autoplay-result", "autoplay resolved",
                WaveeLogField.Of("ctx", contextUri),
                WaveeLogField.Of("count", result.Count),
                WaveeLogField.Of("resultContext", result.ContextUri ?? ""),
                WaveeLogField.Of("nextPage", result.NextPageUrl ?? ""));
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Info("autoplay fetch failed for " + contextUri + ": " + ex.Message);
            PlaybackBucketDiagnostics.Continuation("continuation.autoplay-error", "autoplay fetch failed",
                WaveeLogField.Of("ctx", contextUri),
                WaveeLogField.Of("error", ex.GetType().Name),
                WaveeLogField.Of("detail", ex.Message));
            return ResolvedContext.Empty;
        }
    }

    bool ApplyContinuation(in ResolvedContext result)
    {
        _nextPageUrl = string.IsNullOrEmpty(result.NextPageUrl) ? null : result.NextPageUrl;
        _contextIsInfinite = _contextIsInfinite || result.IsInfinite;
        if (result.Count == 0)
        {
            PlaybackBucketDiagnostics.Continuation("continuation.apply-empty", "continuation result had no tracks",
                WaveeLogField.Of("nextPage", _nextPageUrl ?? ""),
                WaveeLogField.Of("isInfinite", _contextIsInfinite));
            return false;
        }

        bool autoplay = result.Tracks[0].Provider == "autoplay";
        string? sourceContextUri = null;
        if (!string.IsNullOrEmpty(result.ContextUri) && result.ContextUri != _session.ContextUri)
        {
            _snap = _session.RelabelContext(result.ContextUri);
            _projection.SetContextMetadata(result.Metadata);
            sourceContextUri = result.ContextUri;
            autoplay = true;
        }

        if (!autoplay && _session.ContextUri is { } ctx && ContextResolve.IsInfinite(ctx)) autoplay = true;
        var prov = autoplay ? QueueProvider.Autoplay : QueueProvider.Context;
        _snap = _session.AppendContextPage(result.Tracks, prov, sourceContextUri ?? _session.ContextUri);
        EmitSnap(_snap, EvKind.QueueChanged);
        _log.Info("continuation: appended " + result.Count + " tracks"
            + (autoplay ? " (autoplay)" : "")
            + (_nextPageUrl is null ? "" : " with next page"));
        PlaybackBucketDiagnostics.Continuation("continuation.applied", "continuation appended to queue core",
            WaveeLogField.Of("count", result.Count),
            WaveeLogField.Of("provider", autoplay ? "autoplay" : "context"),
            WaveeLogField.Of("ctx", _session.ContextUri ?? ""),
            WaveeLogField.Of("nextPage", _nextPageUrl ?? ""),
            WaveeLogField.Of("remainingContext", _session.RemainingInContext));
        return true;
    }

    async Task<bool> TryContinueContextAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var fetch = _continuationFetch ?? StartContinuationFetch(forceAutoplay: attempt > 0);
            if (fetch is null)
            {
                PlaybackBucketDiagnostics.Continuation("continuation.trackend-none", "no continuation available at track-end",
                    WaveeLogField.Of("attempt", attempt),
                    WaveeLogField.Of("ctx", _session.ContextUri ?? ""),
                    WaveeLogField.Of("remainingContext", _session.RemainingInContext));
                return false;
            }

            Task completed;
            try
            {
                completed = await Task.WhenAny(fetch, Task.Delay(TimeSpan.FromSeconds(3), ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            if (completed != fetch)
            {
                _log.Info("continuation: fetch exceeded 3s grace timeout");
                PlaybackBucketDiagnostics.Continuation("continuation.trackend-timeout", "fetch exceeded track-end grace timeout",
                    WaveeLogField.Of("attempt", attempt),
                    WaveeLogField.Of("ctx", _session.ContextUri ?? ""));
                return false;
            }

            ResolvedContext result;
            try { result = await fetch.ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Info("continuation: fetch faulted: " + ex.Message);
                PlaybackBucketDiagnostics.Continuation("continuation.trackend-fault", "fetch faulted at track-end",
                    WaveeLogField.Of("attempt", attempt),
                    WaveeLogField.Of("error", ex.GetType().Name),
                    WaveeLogField.Of("detail", ex.Message));
                result = ResolvedContext.Empty;
            }
            _continuationFetch = null;

            if (!ApplyContinuation(result))
            {
                if (attempt == 0 && string.IsNullOrEmpty(_nextPageUrl))
                {
                    PlaybackBucketDiagnostics.Continuation("continuation.trackend-retry", "first continuation was empty; retrying forced autoplay",
                        WaveeLogField.Of("attempt", attempt),
                        WaveeLogField.Of("ctx", _session.ContextUri ?? ""));
                    continue;
                }
                return false;
            }

            _snap = _session.Next();
            var next = _snap.Current;
            if (next is null)
            {
                PlaybackBucketDiagnostics.Continuation("continuation.trackend-no-next", "continuation appended but queue had no playable next track",
                    WaveeLogField.Of("ctx", _session.ContextUri ?? ""));
                return false;
            }
            PlaybackBucketDiagnostics.Continuation("continuation.trackend-next", "advancing into continuation track",
                WaveeLogField.Of("track", next.Track.Uri),
                WaveeLogField.Of("ctx", _session.ContextUri ?? ""),
                WaveeLogField.Of("remainingContext", _session.RemainingInContext));
            await LoadAndPlayCurrentAsync(EvKind.TrackChanged, ct).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    // The ONE atomic publish (§5): the current snapshot + the event fold into the projection under a single lock / single
    // FireChanges (ApplyLocalSnapshot), then the same event fans out to the extra projections (the PutState publisher) — the
    // queue can never publish out of step with the track. Ownership is derived from the event kind, as before.
    void Publish(QueueSnapshot snap, in PlaybackEvent e)
    {
        _snap = snap;
        if (e.Kind is EvKind.Started or EvKind.Resumed or EvKind.TrackChanged && e.Track is not null) SetActiveOwner(true);
        else if (e.Kind is EvKind.Ended or EvKind.BecameInactive) SetActiveOwner(false);
        DiagnoseQueue("controller.publish." + e.Kind);
        _projection.ApplyLocalSnapshot(snap, e);
        for (int i = 0; i < _extra.Count; i++) _extra[i].OnEvent(e);
    }

    void Emit(in PlaybackEvent e) => Publish(_snap, e);

    // Publish a session snapshot with a state event carrying its current track (queue mutations + options changes).
    void EmitSnap(QueueSnapshot snap, EvKind kind)
    {
        _snap = snap;
        Emit(BuildEvent(kind, snap.Current?.Track, _host.PositionMs));
        SchedulePreparedNext("session-changed");
    }

    // Emit a state event carrying the current track + position (drives the projection slab + the PutState publish). Reads
    // the current off the immutable _snap (never the live _session) so it is safe on the unlocked inbound paths (F7).
    void EmitState(EvKind kind) => Emit(BuildEvent(kind, _snap.Current?.Track, _host.PositionMs));

    // queue.snapshot diagnostics for the current atomic snapshot (rev + itemId columns, §9). Dedup-guarded.
    void DiagnoseQueue(string reason)
    {
        var rows = new List<QueueEntry>(1 + _snap.UserQueue.Length + _snap.Upcoming.Length + _snap.History.Length);
        if (_snap.Current is { } c) rows.Add(c);
        rows.AddRange(_snap.UserQueue);
        rows.AddRange(_snap.Upcoming);
        rows.AddRange(_snap.History);
        PlaybackBucketDiagnostics.QueueIfChanged(ref _lastControllerQueueDiagSig, reason,
            rows, _snap.ContextUri, _snap.Current?.Track.Uri, _snap.Upcoming.Length, _snap.Revision);   // _snap only (no live-session read) → safe off-lock (F7)
    }

    void WarmUpcomingFastTrack(string reason)
    {
        if (_session.PeekNext() is { } next)
            WarmFastTrack(next, reason);
    }

    void WarmFastTrack(Track track, string reason)
    {
        if (_fast is not IFastTrackWarmer warmer) return;
        try { warmer.Warm(track, reason); }
        catch (Exception ex) { _log.Info($"fast-warm dispatch failed {track.Uri}: {ex.Message}"); }
    }

    void SchedulePreparedNext(string reason)
    {
        if (_preparedHost is null) return;

        var current = _snap.Current;
        var next = current is null ? null : _session.PreviewNext();
        bool allowOverlap = current is not null && next is not null && _snap.Repeat != RepeatMode.Track
            && IsMusic(current.Track) && IsMusic(next.Track);
        string? signature = next is null ? null
            : $"{current!.ItemId.Value:x}:{next.ItemId.Value:x}:{(allowOverlap ? 1 : 0)}";

        CancellationTokenSource? priorCts;
        string? priorToken;
        string? token = null;
        CancellationTokenSource? cts = null;
        lock (_prepareGate)
        {
            if (signature is not null && string.Equals(signature, _preparedSignature, StringComparison.Ordinal)) return;
            priorCts = _prepareCts;
            priorToken = _preparedToken;
            _prepareCts = null;
            _preparedToken = null;
            _preparedSignature = null;
            _preparedItemId = QueueItemId.None;

            if (next is not null)
            {
                token = $"p{Interlocked.Increment(ref _prepareSequence):x}-{next.ItemId.Value:x}";
                cts = new CancellationTokenSource();
                _prepareCts = cts;
                _preparedToken = token;
                _preparedSignature = signature;
                _preparedItemId = next.ItemId;
            }
        }

        try { priorCts?.Cancel(); } catch { }
        priorCts?.Dispose();
        if (!string.IsNullOrEmpty(priorToken))
            _ = _preparedHost.CancelPreparedAsync(priorToken, CancellationToken.None);

        if (next is not null && token is not null && cts is not null)
        {
            _log.Info($"audio prepare scheduled token={token} item={next.ItemId.Value} track={next.Track.Uri} overlap={allowOverlap} reason={reason}");
            _ = ResolvePreparedNextAsync(next, token, allowOverlap, cts.Token);
        }
    }

    async Task ResolvePreparedNextAsync(QueueEntry next, string token, bool allowOverlap, CancellationToken ct)
    {
        try
        {
            AudioFastStart start;
            Task<AudioStreamHandle>? pendingBody = null;
            AudioStreamHandle resolvedBody = default;
            if (_fast is not null)
            {
                var plan = await _fast.ResolveFastAsync(next.Track, ct).ConfigureAwait(false);
                start = plan.Start;
                pendingBody = plan.Body;
            }
            else
            {
                resolvedBody = await _resolver.ResolveAsync(next.Track, ct).ConfigureAwait(false);
                start = new AudioFastStart(resolvedBody.TrackUri, resolvedBody.FileIdHex, resolvedBody.Format,
                    resolvedBody.DurationMs, resolvedBody.NormalizationGainDb, default);
            }

            if (!IsPreparedTokenCurrent(token)) return;
            await _preparedHost!.PrepareNextAsync(new AudioPrepareRequest(token, start, allowOverlap), ct).ConfigureAwait(false);
            if (!IsPreparedTokenCurrent(token))
            {
                await _preparedHost.CancelPreparedAsync(token, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var body = pendingBody is null ? resolvedBody : await pendingBody.ConfigureAwait(false);
            if (!IsPreparedTokenCurrent(token))
            {
                await _preparedHost.CancelPreparedAsync(token, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            await _preparedHost.SupplyNextBodyAsync(token, body, ct).ConfigureAwait(false);
            _log.Info($"audio prepare ready token={token} item={next.ItemId.Value} track={next.Track.Uri}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Info($"audio prepare failed token={token} item={next.ItemId.Value} track={next.Track.Uri}: {ex.GetType().Name}: {ex.Message}");
            ClearPreparedToken(token);
            try { await _preparedHost!.CancelPreparedAsync(token, CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    bool IsPreparedTokenCurrent(string token)
    {
        lock (_prepareGate) return string.Equals(_preparedToken, token, StringComparison.Ordinal);
    }

    void ClearPreparedToken(string token)
    {
        CancellationTokenSource? cts = null;
        lock (_prepareGate)
        {
            if (!string.Equals(_preparedToken, token, StringComparison.Ordinal)) return;
            cts = _prepareCts;
            _prepareCts = null;
            _preparedToken = null;
            _preparedSignature = null;
            _preparedItemId = QueueItemId.None;
        }
        cts?.Dispose();
    }

    static bool IsMusic(Track track)
    {
        if (track.Uri.StartsWith("spotify:episode:", StringComparison.OrdinalIgnoreCase)
            || track.Uri.Contains(":episode:", StringComparison.OrdinalIgnoreCase)
            || track.Uri.Contains(":podcast:", StringComparison.OrdinalIgnoreCase)) return false;
        return track.Source?.Contains("podcast", StringComparison.OrdinalIgnoreCase) != true;
    }

    void OnAudioTransition(AudioTransitionSignal signal)
    {
        if (signal.Kind == AudioTransitionKind.Started)
            _ = CommitPreparedTransitionAsync(signal);
        else if (signal.Kind == AudioTransitionKind.Missed)
        {
            ClearPreparedToken(signal.Token);
            _log.Info($"audio transition missed token={signal.Token} track={signal.TrackUri} reason={signal.Reason ?? "unknown"}");
            // A miss while the current track is still playing (a recycled OOP host lost the prepared stream, or its
            // decoder wasn't prebuffered in time) leaves the upcoming hand-off unprepared. Re-resolve so a fresh prepare
            // is attempted for the same next item instead of silently degrading that one boundary to a hard cut. If the
            // track already ended, the Ended fallback advances first and this just previews the following item.
            SchedulePreparedNext("transition-missed");
        }
        else
            _log.Info($"audio transition completed token={signal.Token} track={signal.TrackUri} fade={signal.EffectiveFadeMs}ms");
    }

    async Task CommitPreparedTransitionAsync(AudioTransitionSignal signal)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            QueueItemId expectedItem;
            lock (_prepareGate)
            {
                if (!string.Equals(_preparedToken, signal.Token, StringComparison.Ordinal))
                {
                    _log.Info($"audio transition rejected stale token={signal.Token} track={signal.TrackUri}");
                    return;
                }
                expectedItem = _preparedItemId;
            }

            var preview = _session.PreviewNext();
            if (preview is null || preview.ItemId != expectedItem
                || !string.Equals(preview.Track.Uri, signal.TrackUri, StringComparison.Ordinal))
            {
                _log.Info($"audio transition identity mismatch token={signal.Token} expectedItem={expectedItem.Value} " +
                    $"previewItem={preview?.ItemId.Value ?? 0} hostTrack={signal.TrackUri} previewTrack={preview?.Track.Uri ?? "(none)"}; reloading current");
                ClearPreparedToken(signal.Token);
                await LoadAndPlayCurrentAsync(EvKind.TrackChanged, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var advanced = _session.Next();
            if (advanced?.Current is not { } current || current.ItemId != expectedItem)
            {
                _log.Info($"audio transition advance mismatch token={signal.Token} expectedItem={expectedItem.Value}");
                ClearPreparedToken(signal.Token);
                await LoadAndPlayCurrentAsync(EvKind.TrackChanged, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            _snap = advanced;
            ClearPreparedToken(signal.Token);
            _projection.NoteLocalCommand();
            MintCommand("trackdone");
            _currentIds = MintPlaybackIds(current.Track);
            Emit(BuildEvent(EvKind.TrackChanged, current.Track, Math.Max(0, signal.PositionMs),
                durationMs: current.Track.DurationMs));
            WarmUpcomingFastTrack("after-handoff");
            SchedulePreparedNext("after-handoff");
            MaybeStartContinuationFetch();
        }
        catch (Exception ex)
        {
            _log.Info("audio transition commit failed: " + ex.Message);
        }
        finally { _lock.Release(); }
    }

    void OnHostSignal(AudioHostSignal s)
    {
        _projection.OnHostSignal(s);
        if (s.Kind == AudioHostSignalKind.Ended)
        {
            _failureCheckpoint = null;
            _ = AutoAdvanceAsync();
        }
        else if (s.Kind == AudioHostSignalKind.Error)
        {
            var reason = s.FailureReason == AudioKeyFailureReason.None
                ? AudioKeyFailureReason.EmulationFault
                : s.FailureReason;
            if (reason == AudioKeyFailureReason.Network && _session.Current is { } current)
                _failureCheckpoint = new PlaybackFailureCheckpoint(current.Uri, Math.Max(0, s.PositionMs));
            ReportPlaybackError(new AudioPlaybackException(reason, s.Detail ?? "audio host playback error"));
        }
    }

    async Task AutoAdvanceAsync() { try { await LocalNextAsync(default).ConfigureAwait(false); } catch (Exception ex) { _log.Info("auto-advance error: " + ex.Message); } }

    // ── forwarding (we are the controller of another device) — the real desktop envelope; ack_id parsed, not block-waited ─
    static Task Done => Task.CompletedTask;
    Task Local(Action a) { a(); return Done; }

    readonly record struct PlayRequest(
        string ContextUri, int StartIndex, IReadOnlyList<QueuedRef>? OrderedTracks,
        string? SkipTrackUri, string? SkipTrackUid)
    {
        public static PlayRequest Default(string contextUri, int startIndex) =>
            new(contextUri, Math.Max(0, startIndex), null, null, null);
    }

    async Task ExecutePlayAsync(PlayRequest request, CancellationToken ct)
    {
        if (!RouteLocal()) { await ForwardPlayAsync(request, ct).ConfigureAwait(false); return; }
        MintCommand("playbtn");
        if (request.OrderedTracks is { Count: > 0 })
        {
            var spec = new ContextSpec(request.ContextUri, null, request.OrderedTracks,
                request.SkipTrackUri, request.SkipTrackUid, request.StartIndex);
            await LocalPlaySpecAsync(spec, ct).ConfigureAwait(false);
            return;
        }
        await LocalPlaySpecAsync(new ContextSpec(
            request.ContextUri,
            null,
            null,
            request.SkipTrackUri,
            request.SkipTrackUid,
            request.StartIndex), ct).ConfigureAwait(false);
    }

    async Task Forward(string endpoint, CancellationToken ct, params (string Key, object Value)[] args)
    {
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return;
        var json = OutboundEnvelope.Command(_ourDeviceId, endpoint, args, NewId(), NewId(), Now(), NewId());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) _log.Info($"outbound {endpoint} → {target}: failed ({r.Status})");
    }

    async Task ForwardPlayAsync(PlayRequest request, CancellationToken ct)
    {
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return;
        // Outbound carries the OPAQUE context uri — the TARGET resolves the tracks (the desktop full-envelope shape).
        int? skipIndex = request.OrderedTracks is { Count: > 0 } ? request.StartIndex
            : request.StartIndex > 0 ? request.StartIndex : null;
        string? skipUid = string.IsNullOrEmpty(request.SkipTrackUid) ? null : request.SkipTrackUid;
        var json = OutboundEnvelope.Play(_ourDeviceId, request.ContextUri, null,
            skipIndex, request.SkipTrackUri, skipUid, request.OrderedTracks,
            _session.Shuffle, FeatureOf(request.ContextUri), _featureVersion, NewId(), NewId(), Now());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) { _log.Info($"outbound play → {target}: failed ({r.Status})"); OnRemoteCommandFailed?.Invoke(); }
    }

    // add_to_queue: a single track as command.track {uri,uid,metadata} + options — NOT the flat command.uri Forward verb.
    async Task ForwardAddToQueueAsync(string trackUri, CancellationToken ct)
    {
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return;
        var json = OutboundEnvelope.AddToQueue(_ourDeviceId, trackUri, "", false, false, false, NewId(), NewId(), Now(), NewId());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) _log.Info($"outbound add_to_queue → {target}: failed ({r.Status})");
    }

    async Task<bool> TryForwardTransferAsync(string target, CancellationToken ct)
    {
        if (_outbound is null) { _log.Info($"transfer to {target} ignored - no outbound control"); return false; }
        var from = string.IsNullOrEmpty(_projection.ActiveDeviceId) ? _ourDeviceId : _projection.ActiveDeviceId;
        var r = await _outbound.TransferAsync(from, target, ct).ConfigureAwait(false);
        if (r.Ok) { _log.Info($"connect transfer {from} -> {target}: ok ({r.Status})"); return true; }
        _log.Info($"connect transfer {from} -> {target}: failed ({r.Status})");
        OnRemoteCommandFailed?.Invoke();
        return false;
    }

    async Task ForwardTransferAsync(string target, CancellationToken ct)
    {
        if (_outbound is null) { _log.Info($"transfer → {target} ignored — no outbound control"); return; }
        var json = OutboundEnvelope.Command(_ourDeviceId, "transfer", Array.Empty<(string, object)>(), NewId(), NewId(), Now(), NewId());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (r.Ok) { _log.Info($"outbound transfer → {target}: ok (ack {r.AckId})"); return; }
        _log.Info($"outbound transfer → {target}: failed ({r.Status})");   // parity with the other forwards (was silent)
        OnRemoteCommandFailed?.Invoke();
    }

    static string NewId() => Guid.NewGuid().ToString("N");
    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    // The queue_revision to echo on an outbound set_queue — the last revision the cluster reported (held by the projection;
    // "" until the first cluster). It can exceed Int64, so parse as ulong; an unparseable/absent value sends 0 (best-effort).
    ulong ParseRevision() => ulong.TryParse(_projection.QueueRevision, out var r) ? r : 0UL;
    static QueuedRef[] ToQueuedRefs(IReadOnlyList<PlaybackContextTrack> tracks)
    {
        var refs = new QueuedRef[tracks.Count];
        for (int i = 0; i < tracks.Count; i++) refs[i] = new QueuedRef(tracks[i].Uri, tracks[i].Uid ?? "", Metadata: tracks[i].Metadata);
        return refs;
    }

    // play_origin.feature_identifier — the source surface, derived from the context type (matches the desktop captures).
    static string FeatureOf(string uri) =>
        uri.Contains(":album:", StringComparison.Ordinal) ? "album"
        : uri.Contains(":artist", StringComparison.Ordinal) ? "artist"
        : uri.Contains(":playlist:", StringComparison.Ordinal) ? "playlist"
        : uri.Contains(":collection", StringComparison.Ordinal) ? "collection"
        : "harmony";

    // Parse the inbound play/transfer command payload into a ContextSpec (proto-free). The command payload is small (it
    // carries an opaque context uri + skip_to, NOT a track list — that's resolved server-side), so JsonDocument is fine
    // here; the LARGE context-resolve RESPONSE is streamed via Utf8JsonReader in LiveContextResolver. Returns null when
    // there's no context to play (a bare transfer → the caller ghost-resumes the cluster snapshot instead).
    static ContextSpec? ExtractPlaySpec(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("command", out var c)) return null;

            string? uri = null, url = null;
            List<QueuedRef>? pages = null;
            if (c.TryGetProperty("context", out var ctx))
            {
                if (ctx.TryGetProperty("uri", out var u)) uri = u.GetString();
                if (ctx.TryGetProperty("url", out var ur)) url = ur.GetString();
                if (ctx.TryGetProperty("pages", out var pg) && pg.ValueKind == JsonValueKind.Array)
                {
                    foreach (var page in pg.EnumerateArray())
                    {
                        if (!page.TryGetProperty("tracks", out var trks) || trks.ValueKind != JsonValueKind.Array) continue;
                        foreach (var t in trks.EnumerateArray())
                        {
                            string tu = t.TryGetProperty("uri", out var tuv) ? tuv.GetString() ?? "" : "";
                            if (tu.Length == 0) continue;
                            string tid = t.TryGetProperty("uid", out var tidv) ? tidv.GetString() ?? "" : "";
                            (pages ??= new List<QueuedRef>()).Add(new QueuedRef(tu, tid,
                                TrackProvider(t, "context"), TrackMetadata(t)));
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(uri) && c.TryGetProperty("context_uri", out var cu)) uri = cu.GetString();
            if (string.IsNullOrEmpty(uri)) return null;

            string? skUri = null, skUid = null; int? skIdx = null;
            if (TryGetSkipTo(c, out var skipTo))
            {
                if (skipTo.TryGetProperty("track_uri", out var su)) skUri = su.GetString();
                if (skipTo.TryGetProperty("track_uid", out var sd)) skUid = sd.GetString();
                if (skipTo.TryGetProperty("track_index", out var si) && si.TryGetInt32(out var sidx)) skIdx = sidx;
            }
            return new ContextSpec(uri!, url, pages, skUri, skUid, skIdx);
        }
        catch { return null; }
    }

    // skip_to lives under prepare_play_options.skip_to (desktop envelope) or options.skip_to (legacy/bare form).
    static bool TryGetSkipTo(JsonElement command, out JsonElement skipTo)
    {
        if (command.TryGetProperty("prepare_play_options", out var ppo) && ppo.TryGetProperty("skip_to", out skipTo)) return true;
        if (command.TryGetProperty("options", out var opt) && opt.TryGetProperty("skip_to", out skipTo)) return true;
        skipTo = default;
        return false;
    }

    // add_to_queue: a single track ref. Real desktop sends command.track {uri,uid}; our own/legacy outbound sends a flat
    // command.uri string — accept both.
    static QueuedRef? ParseQueueTrack(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("command", out var c)) return null;
            if (c.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object &&
                t.TryGetProperty("uri", out var tu) && tu.GetString() is { Length: > 0 } u)
                return new QueuedRef(u, t.TryGetProperty("uid", out var d) ? d.GetString() ?? "" : "",
                    TrackProvider(t, "queue"), TrackMetadata(t));
            if (c.TryGetProperty("uri", out var flat) && flat.ValueKind == JsonValueKind.String && flat.GetString() is { Length: > 0 } fu)
                return new QueuedRef(fu, "", "queue");
            return null;
        }
        catch { return null; }
    }

    // set_queue full reconcile (F8): parse ALL of command.{field} into wire entries, preserving EVERY row — queue rows,
    // context continuation, autoplay tail AND the delimiter / meta:page markers (the session classifies them by uri/kind).
    // IsQueued keys on provider:"queue" or metadata is_queued:"true" (a missing provider is treated as context).
    static IReadOnlyList<QueueWireEntry> ParseWireEntries(byte[] payload, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("command", out var c) ||
                !c.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<QueueWireEntry>();
            var list = new List<QueueWireEntry>(arr.GetArrayLength());
            foreach (var t in arr.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("uri", out var u) || u.GetString() is not { Length: > 0 } uri) continue;
                var meta = TrackMetadata(t);
                bool queued = string.Equals(TrackProvider(t, ""), "queue", StringComparison.Ordinal)
                    || (meta is not null && meta.TryGetValue("is_queued", out var iq) && iq == "true");
                list.Add(new QueueWireEntry(uri, t.TryGetProperty("uid", out var d) ? d.GetString() ?? "" : "", queued, meta));
            }
            return list;
        }
        catch { return Array.Empty<QueueWireEntry>(); }
    }

    // command.queue_revision — a bare unsigned number that can exceed Int64; kept as a string (echoed on an outbound set_queue).
    static string ParseQueueRevisionString(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("command", out var c) && c.TryGetProperty("queue_revision", out var r))
                return r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : r.GetRawText();
            return "";
        }
        catch { return ""; }
    }

    static string TrackProvider(JsonElement track, string fallback) =>
        track.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? fallback
            : fallback;

    static IReadOnlyDictionary<string, string>? TrackMetadata(JsonElement track)
    {
        if (!track.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object) return null;
        Dictionary<string, string>? result = null;
        foreach (var p in metadata.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            (result ??= new Dictionary<string, string>(StringComparer.Ordinal))[p.Name] = p.Value.GetString() ?? "";
        }
        return result;
    }

    static int IndexOfUri(IReadOnlyList<QueuedTrack> tracks, string uri)
    {
        for (int i = 0; i < tracks.Count; i++) if (tracks[i].Uri == uri) return i;
        return -1;
    }

    static Track SyntheticTrack(string uri)
    {
        string id = uri.LastIndexOf(':') is var i && i >= 0 && i + 1 < uri.Length ? uri[(i + 1)..] : uri;
        return new Track(id, uri, uri, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null);
    }

    public void Dispose()
    {
        CancellationTokenSource? prepareCts;
        string? preparedToken;
        lock (_prepareGate)
        {
            prepareCts = _prepareCts;
            preparedToken = _preparedToken;
            _prepareCts = null;
            _preparedToken = null;
        }
        try { prepareCts?.Cancel(); } catch { }
        prepareCts?.Dispose();
        if (_preparedHost is not null && preparedToken is not null)
            _ = _preparedHost.CancelPreparedAsync(preparedToken, CancellationToken.None);
        _remoteVolumeTx.Dispose();
        _transitionSub?.Dispose();
        _hostSub.Dispose();
        _projSub.Dispose();
        _lock.Dispose();
    }
}
