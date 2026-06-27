using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

// ── Stage E — the PlaybackController (the live IPlaybackPlayer orchestrator) ──────────────────────────────────────────
// Owns QueueCore + IAudioHost + ITrackResolver + the NowPlayingProjection, and is the single serialization point for UI
// intents, inbound remote commands, and host Ended/position signals. Two arms, keyed on the active device:
//   • WE are active (or a fresh local play)  → drive the local IAudioHost (resolve -> Load+Play / Pause / Seek / Next).
//   • another device is active               → forward the intent as an OUTBOUND remote command (IOutboundControl).
// Replaces FakePlaybackProvider on the live path. Resolution lives in front of the host seam (in scope); the host renders
// (deferred → SilentAudioHost for now).

/// <summary>Resolves a domain Track to a playable handle (CDN + key + format). Real impl in Stage F; stub for headless.</summary>
public interface ITrackResolver
{
    Task<AudioStreamHandle> ResolveAsync(Track track, CancellationToken ct = default);
}

/// <summary>A synthetic resolver for headless tests — no CDN/key, duration carried from the Track.</summary>
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
    readonly string _ourDeviceId;
    readonly Action<string>? _log;
    readonly IDisposable _hostSub;
    readonly SemaphoreSlim _lock = new(1, 1);
    bool _localActive;

    public PlaybackController(IAudioHost host, ITrackResolver resolver, NowPlayingProjection projection,
        Func<string, CancellationToken, Task<IReadOnlyList<Track>>> resolveContext,
        string ourDeviceId, IOutboundControl? outbound = null, Action<string>? log = null)
    {
        _host = host;
        _resolver = resolver;
        _projection = projection;
        _resolveContext = resolveContext;
        _ourDeviceId = ourDeviceId;
        _outbound = outbound;
        _log = log;
        _hostSub = host.Signals.Subscribe(Observers.From<AudioHostSignal>(OnHostSignal));
    }

    public IPlaybackState State => _projection;

    // Active when the cluster names us (authoritative when present) or we just started a local play (no cluster yet).
    bool Active()
    {
        var aid = _projection.ActiveDeviceId;
        return string.IsNullOrEmpty(aid) ? _localActive : aid == _ourDeviceId;
    }

    // ── IPlaybackPlayer (UI intents) ──────────────────────────────────────────────────────────────────────────────────
    public async Task PlayAsync(string contextUri, int startIndex = 0, CancellationToken ct = default)
    {
        var tracks = await _resolveContext(contextUri, ct).ConfigureAwait(false);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _localActive = true;   // a user-initiated play makes us the active device
            _queue.SetContext(contextUri, tracks, startIndex);
            await LoadAndPlayCurrentAsync(EvKind.Started, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    public Task PlayTrackAsync(string trackUri, CancellationToken ct = default)
    {
        // A single-track play: treat the track as a one-item context (full metadata fills in via resolve/projection).
        var t = SyntheticTrack(trackUri);
        return PlayListAsync(trackUri, new[] { t }, 0, ct);
    }

    async Task PlayListAsync(string contextUri, IReadOnlyList<Track> tracks, int startIndex, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { _localActive = true; _queue.SetContext(contextUri, tracks, startIndex); await LoadAndPlayCurrentAsync(EvKind.Started, ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    public Task PauseAsync(CancellationToken ct = default) => SimpleAsync("pause", () => _host.Pause(), ct);
    public Task ResumeAsync(CancellationToken ct = default) => SimpleAsync("resume", () => _host.Play(), ct);

    public async Task NextAsync(CancellationToken ct = default)
    {
        if (!Active()) { await ForwardAsync("skip_next", ct).ConfigureAwait(false); return; }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _projection.NoteLocalCommand();
            var t = _queue.Next();
            if (t is not null) await LoadAndPlayCurrentAsync(EvKind.TrackChanged, ct).ConfigureAwait(false);
            else { _host.Stop(); _projection.OnEvent(new PlaybackEvent(EvKind.Ended, null, 0)); }
        }
        finally { _lock.Release(); }
    }

    public async Task PreviousAsync(CancellationToken ct = default)
    {
        if (!Active()) { await ForwardAsync("skip_prev", ct).ConfigureAwait(false); return; }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { _projection.NoteLocalCommand(); _queue.Prev(); await LoadAndPlayCurrentAsync(EvKind.TrackChanged, ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    public async Task SeekAsync(long positionMs, CancellationToken ct = default)
    {
        if (!Active()) { await ForwardAsync("seek_to", ct, positionMs).ConfigureAwait(false); return; }
        _projection.NoteLocalCommand();
        _host.Seek(positionMs);
    }

    public Task SetVolumeAsync(double volume01, CancellationToken ct = default) { _host.SetVolume(volume01); return Task.CompletedTask; }
    public Task SetShuffleAsync(bool on, CancellationToken ct = default) { _queue.SetShuffle(on); return Task.CompletedTask; }
    public Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default) { _queue.SetRepeat(mode); return Task.CompletedTask; }
    public Task EnqueueAsync(string trackUri, CancellationToken ct = default) { _queue.EnqueueUser(SyntheticTrack(trackUri)); return Task.CompletedTask; }
    public Task MoveQueueAsync(string entryId, int toIndex, CancellationToken ct = default) => Task.CompletedTask;     // QueueCore widening: Stage G
    public Task RemoveFromQueueAsync(string entryId, CancellationToken ct = default) => Task.CompletedTask;           // QueueCore widening: Stage G

    // ── Inbound remote commands (we are the Connect target) ──────────────────────────────────────────────────────────
    /// <summary>Translate a parsed inbound ConnectCommand into a controller action (the router calls this when WE are the
    /// target). Synchronous-friendly: fires the async op and returns; the ack is ack-on-dispatch.</summary>
    public void HandleRemoteCommand(in ConnectCommand cmd)
    {
        switch (cmd.Kind)
        {
            case ConnectCmd.Pause: _ = PauseAsync(); break;
            case ConnectCmd.Resume: _ = ResumeAsync(); break;
            case ConnectCmd.SkipNext: _ = NextAsync(); break;
            case ConnectCmd.SkipPrev: _ = PreviousAsync(); break;
            case ConnectCmd.SeekTo: _ = SeekAsync(cmd.SeekToMs); break;
            case ConnectCmd.SetShufflingContext: _ = SetShuffleAsync(cmd.BoolArg); break;
            case ConnectCmd.SetRepeatingContext: _ = SetRepeatAsync(cmd.BoolArg ? RepeatMode.Context : RepeatMode.Off); break;
            case ConnectCmd.SetRepeatingTrack: _ = SetRepeatAsync(cmd.BoolArg ? RepeatMode.Track : RepeatMode.Off); break;
            case ConnectCmd.Play:
            case ConnectCmd.Transfer: _localActive = true; _ = HandlePlayOrTransferAsync(cmd); break;
            default: _log?.Invoke("controller: unhandled remote command " + cmd.Kind); break;
        }
    }

    async Task HandlePlayOrTransferAsync(ConnectCommand cmd)
    {
        // Best-effort: extract the context uri from the command payload and start it locally. (Full play_origin / page /
        // seek-into parsing is layered in with the real resolver; transfer resumes the cluster's current context.)
        try
        {
            string? ctxUri = ExtractContextUri(cmd.Payload);
            if (!string.IsNullOrEmpty(ctxUri)) await PlayAsync(ctxUri!, 0).ConfigureAwait(false);
            else _host.Play();   // bare transfer with no context → resume
        }
        catch (Exception ex) { _log?.Invoke("controller play/transfer error: " + ex.Message); }
    }

    // ── transfer OUT (the device picker → make {target} active) ──────────────────────────────────────────────────────
    public Task TransferToAsync(string targetDeviceId, CancellationToken ct = default)
    {
        if (targetDeviceId == _ourDeviceId) return Task.CompletedTask;
        _localActive = false;
        return _outbound?.SendAsync(targetDeviceId, BuildCommand("transfer"), ct) ?? Task.CompletedTask;
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────────────────────────
    async Task SimpleAsync(string endpoint, Action local, CancellationToken ct)
    {
        if (!Active()) { await ForwardAsync(endpoint, ct).ConfigureAwait(false); return; }
        _projection.NoteLocalCommand();
        local();
    }

    Task ForwardAsync(string endpoint, CancellationToken ct, long seekMs = 0)
    {
        var target = _projection.ActiveDeviceId;
        if (_outbound is null || string.IsNullOrEmpty(target)) return Task.CompletedTask;
        return _outbound.SendAsync(target, BuildCommand(endpoint, seekMs), ct);
    }

    async Task LoadAndPlayCurrentAsync(EvKind kind, CancellationToken ct)
    {
        var cur = _queue.Current;
        if (cur is null) { _host.Stop(); return; }
        var handle = await _resolver.ResolveAsync(cur, ct).ConfigureAwait(false);
        _host.Load(handle);
        _host.Play();
        _projection.OnEvent(new PlaybackEvent(kind, cur, 0));
    }

    void OnHostSignal(AudioHostSignal s)
    {
        _projection.OnHostSignal(s);
        if (s.Kind == AudioHostSignalKind.Ended) _ = AutoAdvanceAsync();
    }

    async Task AutoAdvanceAsync() { try { await NextAsync().ConfigureAwait(false); } catch (Exception ex) { _log?.Invoke("auto-advance error: " + ex.Message); } }

    static string BuildCommand(string endpoint, long seekMs = 0)
        => endpoint == "seek_to"
            ? $"{{\"command\":{{\"endpoint\":\"seek_to\",\"value\":{seekMs}}}}}"
            : $"{{\"command\":{{\"endpoint\":\"{endpoint}\"}}}}";

    static string? ExtractContextUri(byte[] payload)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("command", out var c))
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

    public void Dispose() { _hostSub.Dispose(); _lock.Dispose(); }
}
