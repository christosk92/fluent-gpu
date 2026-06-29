using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
}

/// <summary>POSTs /connect-state/v1/player/command/from/{us}/to/{target} with the command JSON envelope, and parses the
/// server's ack_id from the response (best-effort).</summary>
public sealed class LiveOutboundControl : IOutboundControl
{
    readonly ITransport _transport;
    readonly string _ourDeviceId;
    public LiveOutboundControl(ITransport transport, string ourDeviceId) { _transport = transport; _ourDeviceId = ourDeviceId; }

    public async Task<OutboundResult> SendAsync(string targetDeviceId, string commandJson, CancellationToken ct = default)
    {
        var route = $"/connect-state/v1/player/command/from/{_ourDeviceId}/to/{targetDeviceId}";
        var resp = await _transport.Request(Channel.Spclient, route, Encoding.UTF8.GetBytes(commandJson), ct).ConfigureAwait(false);
        return new OutboundResult(resp.Ok, ParseAckId(resp), resp.Status);
    }

    public async Task<OutboundResult> SetVolumeAsync(string targetDeviceId, int volume0_65535, CancellationToken ct = default)
    {
        var route = $"/connect-state/v1/connect/volume/from/{_ourDeviceId}/to/{targetDeviceId}";
        var resp = await _transport.Request(Channel.Spclient, route, OutboundEnvelope.ConnectVolumeBody(volume0_65535), ct, "PUT").ConfigureAwait(false);
        return new OutboundResult(resp.Ok, ParseAckId(resp), resp.Status);
    }

    static string? ParseAckId(Resp resp)
    {
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0) return null;
        try { using var doc = JsonDocument.Parse(resp.Body); return doc.RootElement.TryGetProperty("ack_id", out var a) ? a.GetString() : null; }
        catch { return null; }
    }
}

public sealed class PlaybackController : IPlaybackPlayer, IDisposable
{
    readonly QueueCore _queue = new();
    readonly IAudioHost _host;
    readonly ITrackResolver _resolver;
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
    string _lastActive = "";
    double _lastVolume = -1;

    public PlaybackController(IAudioHost host, ITrackResolver resolver, NowPlayingProjection projection,
        IContextResolver contexts,
        string ourDeviceId, IOutboundControl? outbound = null, IReadOnlyList<IPlaybackProjection>? extraProjections = null, Action<string>? log = null,
        string? playFeatureVersion = null)
    {
        _host = host;
        _resolver = resolver;
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

    // The routing spine: local iff nobody is active or we are. (No _localActive flag — the cluster is the truth.)
    bool RouteLocal()
    {
        var aid = _projection.ActiveDeviceId;
        return string.IsNullOrEmpty(aid) || aid == _ourDeviceId;
    }

    // "Another device became active" → stop our local host so we don't double-play.
    void OnProjectionChanged(IPlaybackState s)
    {
        // Apply a volume change (incl. one a remote controller made to the active device) to the local host when WE are
        // active. Silent host = no-op today, but correct once real audio lands; never loops (the host has no readback).
        double vol = s.Volume;
        if (Math.Abs(vol - _lastVolume) > 0.0009) { _lastVolume = vol; if (RouteLocal()) _host.SetVolume(vol); }

        var aid = s.ActiveDeviceId ?? "";
        if (aid == _lastActive) return;
        _lastActive = aid;
        if (!string.IsNullOrEmpty(aid) && aid != _ourDeviceId && _host.IsPlaying)
        {
            _log?.Invoke("another device became active — stopping local playback");
            _host.Stop();
        }
    }

    // ── IPlaybackPlayer (UI intents) — each verb routes local vs. forward ─────────────────────────────────────────────
    public async Task PlayAsync(string contextUri, int startIndex = 0, CancellationToken ct = default)
    {
        if (!RouteLocal()) { await ForwardPlayAsync(contextUri, startIndex, ct).ConfigureAwait(false); return; }
        await LocalPlaySpecAsync(ContextSpec.ForUri(contextUri, startIndex), ct).ConfigureAwait(false);
    }

    public Task PlayTrackAsync(string trackUri, CancellationToken ct = default)
    {
        if (!RouteLocal()) return ForwardPlayAsync(trackUri, 0, ct);
        return LocalPlayTracksAsync(trackUri, new[] { new QueuedTrack(SyntheticTrack(trackUri), "") }, 0, ct);
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
        if (!RouteLocal()) { await Forward("add_to_queue", ct, ("uri", trackUri)).ConfigureAwait(false); return; }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_queue.Current is null)   // add-to-queue while idle → start playing it (rule §3)
            {
                _queue.SetContext(trackUri, new[] { SyntheticTrack(trackUri) }, 0);
                await LoadAndPlayCurrentAsync(EvKind.Started, ct).ConfigureAwait(false);
            }
            else { _queue.EnqueueUser(SyntheticTrack(trackUri)); PushQueueAndPublish(); }
        }
        finally { _lock.Release(); }
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
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try { if (_queue.Current is not null) _host.Play(); else await GhostResumeAsync(ct).ConfigureAwait(false); }
            finally { _lock.Release(); }
            return;
        }
        await ForwardTransferAsync(targetDeviceId, ct).ConfigureAwait(false);
        _host.Stop();
        EmitState(EvKind.BecameInactive);   // publish a clean is_active=false hand-off
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
        try { _queue.SetContext(contextUri, tracks, startIndex); await LoadAndPlayCurrentAsync(EvKind.Started, ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    async Task LocalResumeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_queue.Current is not null) { _host.Play(); EmitState(EvKind.Resumed); }   // we have a local context → normal resume
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
        var track = _projection.CurrentTrack;
        if (track is null) { _log?.Invoke("ghost resume: nothing in the cluster to resume"); return; }
        var ctxUri = _projection.ContextUri ?? track.Uri;
        var tracks = new List<Track>(1 + _projection.Queue.Count) { track };
        foreach (var qe in _projection.Queue) if (qe.Bucket == QueueBucket.NextUp) tracks.Add(qe.Track);
        _queue.SetContext(ctxUri, tracks, 0);
        var handle = await _resolver.ResolveAsync(track, ct).ConfigureAwait(false);
        _host.Load(handle);
        long pos = _projection.PositionMs;
        if (pos > 0) _host.Seek(pos);
        _host.Play();
        PushQueue();                                              // queue first, so the published snapshot carries it
        Emit(new PlaybackEvent(EvKind.Started, track, pos));
    }

    async Task LoadAndPlayCurrentAsync(EvKind kind, CancellationToken ct)
    {
        var cur = _queue.Current;
        if (cur is null) { _host.Stop(); return; }
        var handle = await _resolver.ResolveAsync(cur, ct).ConfigureAwait(false);
        _host.Load(handle);
        _host.Play();
        PushQueue();                                              // queue first, so the published snapshot carries it
        Emit(new PlaybackEvent(kind, cur, 0));
    }

    // Fan the event log out to the now-playing projection + every extra projection (telemetry, the PutState publisher).
    void Emit(in PlaybackEvent e) { _projection.OnEvent(e); for (int i = 0; i < _extra.Count; i++) _extra[i].OnEvent(e); }

    // Emit a state event carrying the current track + position (drives the projection slab + the PutState publish).
    void EmitState(EvKind kind) => Emit(new PlaybackEvent(kind, _queue.Current, _host.PositionMs));

    // Surface QueueCore's snapshot to the projection so IPlaybackState.Queue (+ the PutState next-up) reflect our local
    // queue while WE are the active device. Called after every local queue mutation.
    void PushQueue() => _projection.SetLocalQueue(_queue.Snapshot());

    // Push the local shuffle/repeat to the projection (so PutState carries them while we're active).
    void PushOptions() => _projection.SetLocalOptions(_queue.Shuffle, _queue.Repeat);

    // A queue mutation: surface it to the projection AND publish (QueueChanged) so a controller sees the new up-next.
    void PushQueueAndPublish() { PushQueue(); EmitState(EvKind.QueueChanged); }

    void OnHostSignal(AudioHostSignal s)
    {
        _projection.OnHostSignal(s);
        if (s.Kind == AudioHostSignalKind.Ended) _ = AutoAdvanceAsync();
    }

    async Task AutoAdvanceAsync() { try { await LocalNextAsync(default).ConfigureAwait(false); } catch (Exception ex) { _log?.Invoke("auto-advance error: " + ex.Message); } }

    // ── forwarding (we are the controller of another device) — the real desktop envelope; ack_id parsed, not block-waited ─
    static Task Done => Task.CompletedTask;
    Task Local(Action a) { a(); return Done; }

    async Task Forward(string endpoint, CancellationToken ct, params (string Key, object Value)[] args)
    {
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return;
        var json = OutboundEnvelope.Command(_ourDeviceId, endpoint, args, NewId(), NewId(), Now());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) _log?.Invoke($"outbound {endpoint} → {target}: failed ({r.Status})");
    }

    async Task ForwardPlayAsync(string contextUri, int startIndex, CancellationToken ct)
    {
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return;
        // Outbound carries the OPAQUE context uri — the TARGET resolves the tracks (the desktop full-envelope shape).
        var json = OutboundEnvelope.Play(_ourDeviceId, contextUri, null, startIndex > 0 ? startIndex : null, null, null,
            null, _queue.Shuffle, FeatureOf(contextUri), _featureVersion, NewId(), NewId(), Now());
        var r = await _outbound.SendAsync(target, json, ct).ConfigureAwait(false);
        if (!r.Ok) _log?.Invoke($"outbound play → {target}: failed ({r.Status})");
    }

    Task ForwardTransferAsync(string target, CancellationToken ct)
        => _outbound is null ? Done : _outbound.SendAsync(target,
            OutboundEnvelope.Command(_ourDeviceId, "transfer", Array.Empty<(string, object)>(), NewId(), NewId(), Now()), ct);

    static string NewId() => Guid.NewGuid().ToString("N");
    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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

    // set_queue: an array of track refs under command.<field> (e.g. next_tracks), each {uri,uid}.
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
