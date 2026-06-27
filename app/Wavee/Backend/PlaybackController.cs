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

/// <summary>Sends an outbound player command to the active device (we are the controller). Proto-free JSON over spclient.</summary>
public interface IOutboundControl
{
    Task SendAsync(string targetDeviceId, string commandJson, CancellationToken ct = default);
}

/// <summary>POSTs /connect-state/v1/player/command/from/{us}/to/{target} with the command JSON envelope.</summary>
public sealed class LiveOutboundControl : IOutboundControl
{
    readonly ITransport _transport;
    readonly string _ourDeviceId;
    public LiveOutboundControl(ITransport transport, string ourDeviceId) { _transport = transport; _ourDeviceId = ourDeviceId; }

    public Task SendAsync(string targetDeviceId, string commandJson, CancellationToken ct = default)
    {
        var route = $"/connect-state/v1/player/command/from/{_ourDeviceId}/to/{targetDeviceId}";
        return _transport.Request(Channel.Spclient, route, Encoding.UTF8.GetBytes(commandJson), ct);
    }
}

public sealed class PlaybackController : IPlaybackPlayer, IDisposable
{
    readonly QueueCore _queue = new();
    readonly IAudioHost _host;
    readonly ITrackResolver _resolver;
    readonly NowPlayingProjection _projection;
    readonly Func<string, CancellationToken, Task<IReadOnlyList<Track>>> _resolveContext;
    readonly IOutboundControl? _outbound;
    readonly IPlaybackProjection? _telemetry;
    readonly string _ourDeviceId;
    readonly Action<string>? _log;
    readonly IDisposable _hostSub;
    readonly IDisposable _projSub;
    readonly SemaphoreSlim _lock = new(1, 1);
    string _lastActive = "";

    public PlaybackController(IAudioHost host, ITrackResolver resolver, NowPlayingProjection projection,
        Func<string, CancellationToken, Task<IReadOnlyList<Track>>> resolveContext,
        string ourDeviceId, IOutboundControl? outbound = null, IPlaybackProjection? telemetry = null, Action<string>? log = null)
    {
        _host = host;
        _resolver = resolver;
        _projection = projection;
        _resolveContext = resolveContext;
        _ourDeviceId = ourDeviceId;
        _outbound = outbound;
        _telemetry = telemetry;
        _log = log;
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
        await LocalPlayContextAsync(contextUri, startIndex, ct).ConfigureAwait(false);
    }

    public Task PlayTrackAsync(string trackUri, CancellationToken ct = default)
    {
        if (!RouteLocal()) return ForwardPlayAsync(trackUri, 0, ct);
        return LocalPlayTracksAsync(trackUri, new[] { SyntheticTrack(trackUri) }, 0, ct);
    }

    public Task PauseAsync(CancellationToken ct = default)
        => RouteLocal() ? Local(() => _host.Pause()) : Forward("pause", ct);

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
        => RouteLocal() ? Local(() => { _projection.NoteLocalCommand(); _host.Seek(positionMs); })
                        : Forward("seek_to", ct, ("value", positionMs));

    public Task SetVolumeAsync(double volume01, CancellationToken ct = default)
        => RouteLocal() ? Local(() => _host.SetVolume(volume01))
                        : Forward("set_volume", ct, ("value", (long)Math.Round(Math.Clamp(volume01, 0, 1) * 65535)));

    public Task SetShuffleAsync(bool on, CancellationToken ct = default)
        => RouteLocal() ? Local(() => _queue.SetShuffle(on)) : Forward("set_shuffling_context", ct, ("value", on));

    public async Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default)
    {
        if (RouteLocal()) { _queue.SetRepeat(mode); return; }
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
            else _queue.EnqueueUser(SyntheticTrack(trackUri));
        }
        finally { _lock.Release(); }
    }

    public Task MoveQueueAsync(string entryId, int toIndex, CancellationToken ct = default) => Task.CompletedTask;   // local-only; QueueCore widening: Stage G
    public Task RemoveFromQueueAsync(string entryId, CancellationToken ct = default) => Task.CompletedTask;

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
        if (_outbound is not null) await _outbound.SendAsync(targetDeviceId, BuildCommand("transfer"), ct).ConfigureAwait(false);
        _host.Stop();
    }

    // ── Inbound remote commands (WE are the target) — ALWAYS local, regardless of the routing rule ───────────────────
    public void HandleRemoteCommand(in ConnectCommand cmd)
    {
        switch (cmd.Kind)
        {
            case ConnectCmd.Pause: _host.Pause(); break;
            case ConnectCmd.Resume: _ = LocalResumeAsync(); break;
            case ConnectCmd.SkipNext: _ = LocalNextAsync(); break;
            case ConnectCmd.SkipPrev: _ = LocalPrevAsync(); break;
            case ConnectCmd.SeekTo: _host.Seek(cmd.SeekToMs); break;
            case ConnectCmd.SetShufflingContext: _queue.SetShuffle(cmd.BoolArg); break;
            case ConnectCmd.SetRepeatingContext: _queue.SetRepeat(cmd.BoolArg ? RepeatMode.Context : RepeatMode.Off); break;
            case ConnectCmd.SetRepeatingTrack: _queue.SetRepeat(cmd.BoolArg ? RepeatMode.Track : RepeatMode.Off); break;
            case ConnectCmd.Play:
            case ConnectCmd.Transfer: _ = HandleInboundPlayOrTransferAsync(cmd); break;
            default: _log?.Invoke("controller: unhandled remote command " + cmd.Kind); break;
        }
    }

    async Task HandleInboundPlayOrTransferAsync(ConnectCommand cmd)
    {
        try
        {
            string? ctxUri = ExtractContextUri(cmd.Payload);
            if (!string.IsNullOrEmpty(ctxUri)) await LocalPlayContextAsync(ctxUri!, 0, default).ConfigureAwait(false);
            else await LocalResumeAsync(default).ConfigureAwait(false);   // bare transfer → ghost-resume the cluster state here
        }
        catch (Exception ex) { _log?.Invoke("controller inbound play/transfer error: " + ex.Message); }
    }

    // ── local execution primitives (shared by the public verbs + inbound handling) ───────────────────────────────────
    async Task LocalPlayContextAsync(string contextUri, int startIndex, CancellationToken ct)
    {
        var tracks = await _resolveContext(contextUri, ct).ConfigureAwait(false);
        if (tracks.Count == 0) { _log?.Invoke("play: context resolved to 0 tracks: " + contextUri); return; }
        await LocalPlayTracksAsync(contextUri, tracks, startIndex, ct).ConfigureAwait(false);
    }

    async Task LocalPlayTracksAsync(string contextUri, IReadOnlyList<Track> tracks, int startIndex, CancellationToken ct)
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
            if (_queue.Current is not null) _host.Play();   // we have a local context → normal resume
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
        try { _projection.NoteLocalCommand(); _queue.Prev(); await LoadAndPlayCurrentAsync(EvKind.TrackChanged, ct).ConfigureAwait(false); }
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
        Emit(new PlaybackEvent(EvKind.Started, track, pos));
    }

    async Task LoadAndPlayCurrentAsync(EvKind kind, CancellationToken ct)
    {
        var cur = _queue.Current;
        if (cur is null) { _host.Stop(); return; }
        var handle = await _resolver.ResolveAsync(cur, ct).ConfigureAwait(false);
        _host.Load(handle);
        _host.Play();
        Emit(new PlaybackEvent(kind, cur, 0));
    }

    // Fan the event log out to the now-playing projection + the telemetry projection (Recently Played).
    void Emit(in PlaybackEvent e) { _projection.OnEvent(e); _telemetry?.OnEvent(e); }

    void OnHostSignal(AudioHostSignal s)
    {
        _projection.OnHostSignal(s);
        if (s.Kind == AudioHostSignalKind.Ended) _ = AutoAdvanceAsync();
    }

    async Task AutoAdvanceAsync() { try { await LocalNextAsync(default).ConfigureAwait(false); } catch (Exception ex) { _log?.Invoke("auto-advance error: " + ex.Message); } }

    // ── forwarding (we are the controller of another device) ─────────────────────────────────────────────────────────
    static Task Done => Task.CompletedTask;
    Task Local(Action a) { a(); return Done; }

    Task Forward(string endpoint, CancellationToken ct, params (string Key, object Value)[] args)
    {
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return Done;
        return _outbound.SendAsync(target, CommandJson(endpoint, args), ct);
    }

    Task ForwardPlayAsync(string contextUri, int startIndex, CancellationToken ct)
    {
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return Done;
        return _outbound.SendAsync(target, PlayJson(contextUri, startIndex), ct);
    }

    static string BuildCommand(string endpoint) => CommandJson(endpoint, Array.Empty<(string, object)>());

    // {"command":{"endpoint":"<ep>", <args>}}  — written with Utf8JsonWriter so values/keys are JSON-escaped.
    static string CommandJson(string endpoint, (string Key, object Value)[] args)
    {
        var buf = new ArrayBufferWriter<byte>(96);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", endpoint);
            foreach (var (k, v) in args)
            {
                switch (v)
                {
                    case bool b: w.WriteBoolean(k, b); break;
                    case long l: w.WriteNumber(k, l); break;
                    case int i: w.WriteNumber(k, i); break;
                    default: w.WriteString(k, v?.ToString() ?? ""); break;
                }
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    // {"command":{"endpoint":"play","context":{"uri":<ctx>},"options":{"skip_to":{"track_index":<i>}}}}
    static string PlayJson(string contextUri, int startIndex)
    {
        var buf = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", "play");
            w.WriteStartObject("context"); w.WriteString("uri", contextUri); w.WriteEndObject();
            if (startIndex > 0)
            {
                w.WriteStartObject("options");
                w.WriteStartObject("skip_to"); w.WriteNumber("track_index", startIndex); w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    static string? ExtractContextUri(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("command", out var c))
            {
                if (c.TryGetProperty("context", out var ctx) && ctx.TryGetProperty("uri", out var u)) return u.GetString();
                if (c.TryGetProperty("context_uri", out var cu)) return cu.GetString();
            }
            return null;
        }
        catch { return null; }
    }

    static Track SyntheticTrack(string uri)
    {
        string id = uri.LastIndexOf(':') is var i && i >= 0 && i + 1 < uri.Length ? uri[(i + 1)..] : uri;
        return new Track(id, uri, uri, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null);
    }

    public void Dispose() { _hostSub.Dispose(); _projSub.Dispose(); _lock.Dispose(); }
}
