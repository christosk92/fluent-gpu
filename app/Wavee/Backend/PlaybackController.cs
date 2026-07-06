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
    readonly QueueCore _queue = new();
    readonly IAudioHost _host;
    readonly ITrackResolver _resolver;
    readonly IFastTrackResolver? _fast;   // when set, local play uses instant-start (head before key); else the plain resolve
    readonly NowPlayingProjection _projection;
    readonly IContextResolver _contexts;
    readonly IOutboundControl? _outbound;
    readonly IReadOnlyList<IPlaybackProjection> _extra;
    readonly string _ourDeviceId;
    readonly string _featureVersion;
    readonly Action<string>? _log;
    readonly IDisposable _hostSub;
    readonly IDisposable _projSub;
    readonly SemaphoreSlim _lock = new(1, 1);
    readonly object _ownershipGate = new();
    static readonly TimeSpan FastStartBodySupplyGrace = TimeSpan.FromMilliseconds(250);
    string _lastActive = "";
    double _lastVolume = -1;
    bool _ownsActivePlayback;

    public PlaybackController(IAudioHost host, ITrackResolver resolver, NowPlayingProjection projection,
        IContextResolver contexts,
        string ourDeviceId, IOutboundControl? outbound = null, IReadOnlyList<IPlaybackProjection>? extraProjections = null, Action<string>? log = null,
        string? playFeatureVersion = null, IFastTrackResolver? fast = null)
    {
        _host = host;
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
        _projSub = projection.Changes.Subscribe(Observers.From<IPlaybackState>(OnProjectionChanged));
    }

    public IPlaybackState State => _projection;

    /// <summary>When set, LOCAL playback is rejected at every point that would start/seed the (silent) local host: the hook
    /// fires (the app shows the "playback on this device isn't supported yet — choose a remote device" toast) and the
    /// operation aborts. Null (the default — unit tests, and a future real-audio build) leaves local playback enabled.
    /// Remote forwarding is never affected. Wired by the live bootstrap to <c>PlaybackBridge.NotifyLocalPlaybackUnsupported</c>.</summary>
    public Action? OnLocalPlaybackRejected { get; set; }

    bool RejectLocalPlay()
    {
        if (OnLocalPlaybackRejected is not { } reject) return false;
        _log?.Invoke("local playback unsupported — rejecting local play intent (choose a remote device)");
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
        _log?.Invoke("local playback error: " + detail);
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
                    _log?.Invoke($"fast-start body ready early track={expectedTrackUri} file={h.FileIdHex}; deferring supply {remaining.TotalMilliseconds:0}ms so clear-head decode can queue first PCM");
                    await Task.Delay(remaining).ConfigureAwait(false);
                }
            }

            var current = _queue.Current?.Uri ?? "";
            if (string.Equals(current, expectedTrackUri, StringComparison.Ordinal))
            {
                _log?.Invoke($"fast-start body ready track={expectedTrackUri} file={h.FileIdHex}; supplying to audio host");
                _host.SupplyBody(h);
            }
            else
            {
                _log?.Invoke($"fast-start body ignored as stale expected={expectedTrackUri} current={current} bodyTrack={h.TrackUri} file={h.FileIdHex}");
            }
        }
        catch (OperationCanceledException)
        {
            _log?.Invoke($"fast-start body task canceled expected={expectedTrackUri}");
        }
        catch (Exception ex)
        {
            var current = _queue.Current?.Uri ?? "";
            if (string.Equals(current, expectedTrackUri, StringComparison.Ordinal))
            {
                _log?.Invoke($"fast-start body failed for active track={expectedTrackUri}; stopping audio host to unblock head stream: {ex.GetType().Name}: {ex.Message}");
                _host.Stop();
            }
            else
            {
                _log?.Invoke($"fast-start body failed for stale track expected={expectedTrackUri} current={current}: {ex.GetType().Name}: {ex.Message}");
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
        try { if (_queue.Current is not null) await LoadAndPlayCurrentAsync(EvKind.Started, ct).ConfigureAwait(false); }
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
        _log?.Invoke(message);
        _host.Stop();
    }

    void OnProjectionChanged(IPlaybackState s)
    {
        // Apply a volume change (incl. one a remote controller made to the active device) to the local host when WE are
        // active. Silent host = no-op today, but correct once real audio lands; never loops (the host has no readback).
        double vol = s.Volume;
        if (Math.Abs(vol - _lastVolume) > 0.0009) { _lastVolume = vol; if (RouteLocal()) _host.SetVolume(vol); }

        var aid = s.ActiveDeviceId ?? "";
        if (aid == _ourDeviceId) SetActiveOwner(true);
        if (aid == _lastActive) return;
        var previousActive = _lastActive;
        _lastActive = aid;
        if (aid != _ourDeviceId && (previousActive == _ourDeviceId || IsActiveOwner()))
        {
            _log?.Invoke("another device became active — stopping local playback");
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
        => RouteLocal() ? Local(() => { _projection.NoteLocalCommand(); _host.Seek(positionMs); EmitState(EvKind.Seeked); })
                        : Forward("seek_to", ct, ("value", positionMs));

    public Task SetVolumeAsync(double volume01, CancellationToken ct = default)
    {
        _projection.NoteLocalCommand();          // optimistic: a stale cluster echo won't snap the slider back
        _projection.SetLocalVolume(volume01);    // move the slider immediately (it follows the active device's volume)
        if (RouteLocal()) return Local(() => { _host.SetVolume(volume01); EmitState(EvKind.VolumeChanged); });
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return Done;
        return ForwardVolumeAsync(target, volume01, ct);   // remote: the dedicated connect/volume PUT, not player/command
    }

    async Task ForwardVolumeAsync(string target, double volume01, CancellationToken ct)
    {
        int vol = (int)Math.Round(Math.Clamp(volume01, 0, 1) * 65535);
        var r = await _outbound!.SetVolumeAsync(target, vol, ct).ConfigureAwait(false);
        if (!r.Ok) _log?.Invoke($"outbound volume → {target}: failed ({r.Status})");
    }

    public Task SetShuffleAsync(bool on, CancellationToken ct = default)
        => RouteLocal() ? Local(() => { _queue.SetShuffle(on); PushOptions(); EmitState(EvKind.OptionsChanged); }) : Forward("set_shuffling_context", ct, ("value", on));

    public async Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default)
    {
        if (RouteLocal()) { _queue.SetRepeat(mode); PushOptions(); EmitState(EvKind.OptionsChanged); return; }
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
            if (_queue.Current is null)   // add-to-queue while idle → start playing it (rule §3)
            {
                if (RejectLocalPlay()) return;   // can't start local playback → toast + abort (don't seed a phantom local queue)
                _queue.SetContext(queued.Track.Uri, new[] { queued }, 0);
                await LoadAndPlayCurrentAsync(EvKind.Started, ct).ConfigureAwait(false);
            }
            else { _queue.EnqueueUser(queued.Track); PushQueueAndPublish(); }
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
                for (int i = hydrated.Count - 1; i >= 0; i--) _queue.EnqueueNext(hydrated[i]);
                if (hydrated.Count > 0) WarmFastTrack(hydrated[0].Track, "play-next");
                PushQueueAndPublish();
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
        foreach (var t in clusterPrev) prev.Add(new QueueWireEntry(t.Uri, t.Uid, t.Provider == "queue"));
        var next = new List<QueueWireEntry>(refs.Length + clusterNext.Count);
        foreach (var r in refs)        next.Add(new QueueWireEntry(r.Uri, r.Uid, true));                 // inserted play-next → queue
        foreach (var t in clusterNext) next.Add(new QueueWireEntry(t.Uri, t.Uid, t.Provider == "queue"));// the device's queue, verbatim
        var json = OutboundEnvelope.SetQueue(_ourDeviceId, ParseRevision(), prev, next, NewId(), NewId(), Now());
        var r2 = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r2.Ok) _log?.Invoke($"outbound set_queue → {target}: failed ({r2.Status})");
    }

    public async Task MoveQueueAsync(string entryId, int toIndex, CancellationToken ct = default)
    {
        if (!RouteLocal()) { _log?.Invoke("queue move ignored — another device is active"); return; }   // the active device owns its queue
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { if (_queue.Move(entryId, toIndex)) PushQueueAndPublish(); }
        finally { _lock.Release(); }
    }

    public async Task RemoveFromQueueAsync(string entryId, CancellationToken ct = default)
    {
        if (!RouteLocal()) { _log?.Invoke("queue remove ignored — another device is active"); return; }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { if (_queue.Remove(entryId)) PushQueueAndPublish(); }
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
            try { if (_queue.Current is not null) { _host.Play(); EmitState(EvKind.Resumed); } else await GhostResumeAsync(ct).ConfigureAwait(false); }
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
            case ConnectCmd.SkipNext: _ = LocalNextAsync(); break;
            case ConnectCmd.SkipPrev: _ = LocalPrevAsync(); break;
            case ConnectCmd.SeekTo: _host.Seek(cmd.SeekToMs); EmitState(EvKind.Seeked); break;
            case ConnectCmd.SetShufflingContext: _queue.SetShuffle(cmd.BoolArg); PushOptions(); EmitState(EvKind.OptionsChanged); break;
            case ConnectCmd.SetRepeatingContext: _queue.SetRepeat(cmd.BoolArg ? RepeatMode.Context : RepeatMode.Off); PushOptions(); EmitState(EvKind.OptionsChanged); break;
            case ConnectCmd.SetRepeatingTrack: _queue.SetRepeat(cmd.BoolArg ? RepeatMode.Track : RepeatMode.Off); PushOptions(); EmitState(EvKind.OptionsChanged); break;
            case ConnectCmd.Play:
            case ConnectCmd.Transfer: _ = HandleInboundPlayOrTransferAsync(cmd); break;
            case ConnectCmd.AddToQueue: _ = HandleAddToQueueAsync(cmd); break;
            case ConnectCmd.SetQueue: _ = HandleSetQueueAsync(cmd); break;
            case ConnectCmd.UpdateContext: _ = HandleUpdateContextAsync(cmd); break;
            case ConnectCmd.SetOptions: HandleSetOptions(cmd); break;
            default: _log?.Invoke("controller: unhandled remote command " + cmd.Kind); break;
        }
    }

    async Task HandleInboundPlayOrTransferAsync(ConnectCommand cmd)
    {
        try
        {
            if (ExtractPlaySpec(cmd.Payload) is { } spec) await LocalPlaySpecAsync(spec, default).ConfigureAwait(false);
            else await LocalResumeAsync(default).ConfigureAwait(false);   // bare transfer → ghost-resume the cluster state here
        }
        catch (Exception ex) { _log?.Invoke("controller inbound play/transfer error: " + ex.Message); }
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
                if (_queue.Current is null)
                {
                    if (RejectLocalPlay()) return;   // inbound add-to-queue while idle would start local playback → toast + abort
                    _queue.SetContext(hydrated[0].Uri, hydrated, 0);
                    await LoadAndPlayCurrentAsync(EvKind.Started, default).ConfigureAwait(false);
                }
                else { _queue.EnqueueUser(hydrated[0]); PushQueueAndPublish(); }
            }
            finally { _lock.Release(); }
        }
        catch (Exception ex) { _log?.Invoke("controller add_to_queue error: " + ex.Message); }
    }

    // set_queue: replace the up-next (the user queue) with the command's next_tracks.
    async Task HandleSetQueueAsync(ConnectCommand cmd)
    {
        try
        {
            var refs = ParseQueueTracks(cmd.Payload, "next_tracks");
            if (refs.Count == 0) return;
            var hydrated = await _contexts.HydrateAsync(refs, default).ConfigureAwait(false);
            await _lock.WaitAsync().ConfigureAwait(false);
            try { _queue.ReplaceNextUp(hydrated); PushQueueAndPublish(); }
            finally { _lock.Release(); }
        }
        catch (Exception ex) { _log?.Invoke("controller set_queue error: " + ex.Message); }
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
                string? curUri = _queue.Current?.Uri;
                int found = curUri is null ? -1 : IndexOfUri(resolved.Tracks, curUri);
                _queue.SetContext(spec.Uri, resolved.Tracks, found < 0 ? 0 : found);
                PushQueue();
            }
            finally { _lock.Release(); }
        }
        catch (Exception ex) { _log?.Invoke("controller update_context error: " + ex.Message); }
    }

    // set_options: apply shuffle + repeat (the desktop sends explicit shuffling_context / repeating_context / repeating_track).
    void HandleSetOptions(in ConnectCommand cmd)
    {
        try
        {
            using var doc = JsonDocument.Parse(cmd.Payload);
            if (!doc.RootElement.TryGetProperty("command", out var c)) return;
            if (c.TryGetProperty("shuffling_context", out var sh) && sh.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _queue.SetShuffle(sh.GetBoolean());
            bool hasRepTrack = c.TryGetProperty("repeating_track", out var rt);
            bool hasRepCtx = c.TryGetProperty("repeating_context", out var rc);
            if (hasRepTrack || hasRepCtx)
            {
                bool repTrack = hasRepTrack && rt.ValueKind == JsonValueKind.True;
                bool repCtx = hasRepCtx && rc.ValueKind == JsonValueKind.True;
                _queue.SetRepeat(repTrack ? RepeatMode.Track : repCtx ? RepeatMode.Context : RepeatMode.Off);
            }
            PushOptions(); EmitState(EvKind.OptionsChanged);
        }
        catch (Exception ex) { _log?.Invoke("controller set_options error: " + ex.Message); }
    }

    // ── local execution primitives (shared by the public verbs + inbound handling) ───────────────────────────────────
    async Task LocalPlaySpecAsync(ContextSpec spec, CancellationToken ct)
    {
        var resolved = await _contexts.ResolveAsync(spec, ct).ConfigureAwait(false);
        if (resolved.Count == 0) { _log?.Invoke("play: context resolved to 0 tracks: " + spec.Uri); return; }
        await LocalPlayTracksAsync(spec.Uri, resolved.Tracks, resolved.StartIndex, ct).ConfigureAwait(false);
    }

    async Task LocalPlayTracksAsync(string contextUri, IReadOnlyList<QueuedTrack> tracks, int startIndex, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _queue.SetContext(contextUri, tracks, startIndex);
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
        catch (Exception ex) { _log?.Invoke("track hydrate failed; falling back to uri placeholder: " + ex.Message); }
        return new QueuedTrack(SyntheticTrack(uri), "");
    }

    async Task LocalResumeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_queue.Current is not null) { if (RejectLocalPlay()) return; _host.Play(); EmitState(EvKind.Resumed); }   // we have a local context → normal resume
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
            var t = _queue.Next();
            if (t is not null) await LoadAndPlayCurrentAsync(EvKind.TrackChanged, ct).ConfigureAwait(false);
            else { _host.Stop(); Emit(new PlaybackEvent(EvKind.Ended, null, 0)); }   // end-of-context
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
            _queue.Prev();
            await LoadAndPlayCurrentAsync(EvKind.TrackChanged, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    // Ghost resume: the play button while nothing is loaded locally → seed context/track/position + the next-up queue
    // from the cluster snapshot, then play locally (we take over). Caller holds _lock.
    async Task GhostResumeAsync(CancellationToken ct)
    {
        if (RejectLocalPlay()) return;   // local audio unsupported → toast + abort (covers cold resume / self-transfer / bare inbound transfer)
        var track = _projection.CurrentTrack;
        if (track is null) { _log?.Invoke("ghost resume: nothing in the cluster to resume"); return; }
        var ctxUri = _projection.ContextUri ?? track.Uri;
        var tracks = new List<Track>(1 + _projection.Queue.Count) { track };
        foreach (var qe in _projection.Queue) if (qe.Bucket == QueueBucket.NextUp) tracks.Add(qe.Track);
        _queue.SetContext(ctxUri, tracks, 0);
        AudioStreamHandle handle;
        try { handle = await _resolver.ResolveAsync(track, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ReportPlaybackError(ex); return; }   // no silent drop — surface a typed reason
        _host.Load(handle);
        long pos = _projection.PositionMs;
        if (pos > 0) _host.Seek(pos);
        _host.Play();
        PushQueue();                                              // queue first, so the published snapshot carries it
        Emit(new PlaybackEvent(EvKind.Started, track, pos));
    }

    async Task LoadAndPlayCurrentAsync(EvKind kind, CancellationToken ct)
    {
        if (RejectLocalPlay()) return;   // local audio unsupported → toast + abort (covers play / next / prev / enqueue-idle / inbound)
        var cur = _queue.Current;
        if (cur is null) { _host.Stop(); return; }

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
            PushQueue();
            WarmUpcomingFastTrack("after-start");
            Emit(new PlaybackEvent(kind, cur, 0));
            _ = SupplyBodyWhenReadyAsync(plan.Body, cur.Uri, loadStartedTicks, plan.Start.HeadBytes.Length);
            return;
        }

        AudioStreamHandle handle;
        try { handle = await _resolver.ResolveAsync(cur, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ReportPlaybackError(ex); return; }   // no silent drop — surface a typed reason
        _host.Load(handle);
        _host.Play();
        PushQueue();                                              // queue first, so the published snapshot carries it
        WarmUpcomingFastTrack("after-start");
        Emit(new PlaybackEvent(kind, cur, 0));
    }

    // Fan the event log out to the now-playing projection + every extra projection (telemetry, the PutState publisher).
    void Emit(in PlaybackEvent e)
    {
        if (e.Kind is EvKind.Started or EvKind.Resumed or EvKind.TrackChanged && e.Track is not null) SetActiveOwner(true);
        else if (e.Kind is EvKind.Ended or EvKind.BecameInactive) SetActiveOwner(false);
        _projection.OnEvent(e);
        for (int i = 0; i < _extra.Count; i++) _extra[i].OnEvent(e);
    }

    // Emit a state event carrying the current track + position (drives the projection slab + the PutState publish).
    void EmitState(EvKind kind) => Emit(new PlaybackEvent(kind, _queue.Current, _host.PositionMs));

    // Surface QueueCore's snapshot to the projection so IPlaybackState.Queue (+ the PutState next-up) reflect our local
    // queue while WE are the active device. Called after every local queue mutation.
    void PushQueue() => _projection.SetLocalQueue(_queue.Snapshot());

    void WarmUpcomingFastTrack(string reason)
    {
        if (_queue.PeekNext() is { } next)
            WarmFastTrack(next, reason);
    }

    void WarmFastTrack(Track track, string reason)
    {
        if (_fast is not IFastTrackWarmer warmer) return;
        try { warmer.Warm(track, reason); }
        catch (Exception ex) { _log?.Invoke($"fast-warm dispatch failed {track.Uri}: {ex.Message}"); }
    }

    // Push the local shuffle/repeat to the projection (so PutState carries them while we're active).
    void PushOptions() => _projection.SetLocalOptions(_queue.Shuffle, _queue.Repeat);

    // A queue mutation: surface it to the projection AND publish (QueueChanged) so a controller sees the new up-next.
    void PushQueueAndPublish() { PushQueue(); EmitState(EvKind.QueueChanged); }

    void OnHostSignal(AudioHostSignal s)
    {
        _projection.OnHostSignal(s);
        if (s.Kind == AudioHostSignalKind.Ended) _ = AutoAdvanceAsync();
        else if (s.Kind == AudioHostSignalKind.Error) ReportPlaybackError(new AudioPlaybackException(AudioKeyFailureReason.EmulationFault, "host playback error"));
    }

    async Task AutoAdvanceAsync() { try { await LocalNextAsync(default).ConfigureAwait(false); } catch (Exception ex) { _log?.Invoke("auto-advance error: " + ex.Message); } }

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
        if (request.OrderedTracks is { Count: > 0 })
        {
            var spec = new ContextSpec(request.ContextUri, null, request.OrderedTracks,
                request.SkipTrackUri, request.SkipTrackUid, request.StartIndex);
            await LocalPlaySpecAsync(spec, ct).ConfigureAwait(false);
            return;
        }
        await LocalPlaySpecAsync(ContextSpec.ForUri(request.ContextUri, request.StartIndex), ct).ConfigureAwait(false);
    }

    async Task Forward(string endpoint, CancellationToken ct, params (string Key, object Value)[] args)
    {
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return;
        var json = OutboundEnvelope.Command(_ourDeviceId, endpoint, args, NewId(), NewId(), Now());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) _log?.Invoke($"outbound {endpoint} → {target}: failed ({r.Status})");
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
            _queue.Shuffle, FeatureOf(request.ContextUri), _featureVersion, NewId(), NewId(), Now());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) { _log?.Invoke($"outbound play → {target}: failed ({r.Status})"); OnRemoteCommandFailed?.Invoke(); }
    }

    // add_to_queue: a single track as command.track {uri,uid,metadata} + options — NOT the flat command.uri Forward verb.
    async Task ForwardAddToQueueAsync(string trackUri, CancellationToken ct)
    {
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return;
        var json = OutboundEnvelope.AddToQueue(_ourDeviceId, trackUri, "", false, false, false, NewId(), NewId(), Now());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) _log?.Invoke($"outbound add_to_queue → {target}: failed ({r.Status})");
    }

    async Task<bool> TryForwardTransferAsync(string target, CancellationToken ct)
    {
        if (_outbound is null) { _log?.Invoke($"transfer to {target} ignored - no outbound control"); return false; }
        var from = string.IsNullOrEmpty(_projection.ActiveDeviceId) ? _ourDeviceId : _projection.ActiveDeviceId;
        var r = await _outbound.TransferAsync(from, target, ct).ConfigureAwait(false);
        if (r.Ok) { _log?.Invoke($"connect transfer {from} -> {target}: ok ({r.Status})"); return true; }
        _log?.Invoke($"connect transfer {from} -> {target}: failed ({r.Status})");
        OnRemoteCommandFailed?.Invoke();
        return false;
    }

    async Task ForwardTransferAsync(string target, CancellationToken ct)
    {
        if (_outbound is null) { _log?.Invoke($"transfer → {target} ignored — no outbound control"); return; }
        var json = OutboundEnvelope.Command(_ourDeviceId, "transfer", Array.Empty<(string, object)>(), NewId(), NewId(), Now());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (r.Ok) { _log?.Invoke($"outbound transfer → {target}: ok (ack {r.AckId})"); return; }
        _log?.Invoke($"outbound transfer → {target}: failed ({r.Status})");   // parity with the other forwards (was silent)
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
        for (int i = 0; i < tracks.Count; i++) refs[i] = new QueuedRef(tracks[i].Uri, tracks[i].Uid ?? "");
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
                            (pages ??= new List<QueuedRef>()).Add(new QueuedRef(tu, tid));
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
                return new QueuedRef(u, t.TryGetProperty("uid", out var d) ? d.GetString() ?? "" : "");
            if (c.TryGetProperty("uri", out var flat) && flat.ValueKind == JsonValueKind.String && flat.GetString() is { Length: > 0 } fu)
                return new QueuedRef(fu, "");
            return null;
        }
        catch { return null; }
    }

    // set_queue: the USER-QUEUE rows under command.next_tracks. Spotify's next_tracks is the user queue (provider:"queue")
    // FOLLOWED BY the context continuation (provider:"context"); only the queue rows belong in up-next. Context rows and
    // spotify:delimiter markers are dropped — the context continuation is served by the resident context, not the user
    // queue. A missing provider is treated as "queue" (our own/legacy outbound and older payloads omit it).
    static IReadOnlyList<QueuedRef> ParseQueueTracks(byte[] payload, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("command", out var c) ||
                !c.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<QueuedRef>();
            var list = new List<QueuedRef>(arr.GetArrayLength());
            foreach (var t in arr.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("uri", out var u) || u.GetString() is not { Length: > 0 } uri) continue;
                if (uri == "spotify:delimiter") continue;                                            // queue/context boundary marker
                if (t.TryGetProperty("provider", out var p) && p.GetString() == "context") continue; // context continuation, not user queue
                list.Add(new QueuedRef(uri, t.TryGetProperty("uid", out var d) ? d.GetString() ?? "" : ""));
            }
            return list;
        }
        catch { return Array.Empty<QueuedRef>(); }
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

    public void Dispose() { _hostSub.Dispose(); _projSub.Dispose(); _lock.Dispose(); }
}
