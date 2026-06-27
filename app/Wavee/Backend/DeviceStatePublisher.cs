using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

// ── Parity: the outbound half of the projection — the SINGLE PutState writer ─────────────────────────────────────────
// When WE play locally, the cloud must see us as the active player (otherwise other devices/controllers don't show our
// playback and a phone can't "transfer from" us). This publishes our player_state to /connect-state/v1/devices/{id} on
// local playback changes: is_active=true + the current track/context/position/options/queue, with stable session/playback
// ids + has_been_playing_for_ms (the Recently-Played-relevant fields). Proto-building is delegated (SpotifyLive), so the
// publish discipline (when, dedup, ids, reason) stays proto-free + unit-testable. WaveeMusic does this inside its god-class;
// here it's one focused IPlaybackProjection that also owns the NewConnection announce — one writer, one message_id sequence.

public enum PutStateReasonKind { NewConnection, PlayerStateChanged }

/// <summary>Proto-free snapshot of OUR local playback, handed to the builder to populate the player_state.</summary>
public readonly record struct LocalPlaybackSnapshot(
    string TrackUri, string TrackUid, string? ContextUri, long PositionMs, long DurationMs,
    bool IsPlaying, bool IsPaused, bool Shuffle, RepeatMode Repeat, IReadOnlyList<string> NextUris,
    string SessionId, string PlaybackId, long HasBeenPlayingForMs, long StartedPlayingAtMs);

public sealed class DeviceStatePublisher : IPlaybackProjection, IDisposable
{
    readonly ITransport _transport;
    readonly string _deviceId;
    readonly IPlaybackState _state;
    readonly Func<string?> _connectionId;
    readonly Func<PutStateReasonKind, LocalPlaybackSnapshot?, uint, bool, byte[]> _build;
    readonly Action<byte[]>? _onCluster;
    readonly Action<string>? _log;
    readonly Func<long> _now;
    readonly IDisposable _connSub;
    readonly object _gate = new();
    uint _messageId;
    string _sessionId = "";
    string _playbackId = "";
    string? _sessionContextUri;
    long _startedPlayingAtMs;
    string _lastPublishKey = "";

    public DeviceStatePublisher(
        ITransport transport, string deviceId, IPlaybackState state,
        IObservable<string?> connectionId, Func<string?> currentConnectionId,
        Func<PutStateReasonKind, LocalPlaybackSnapshot?, uint, bool, byte[]> build,
        Action<byte[]>? onCluster = null, Action<string>? log = null, Func<long>? clock = null)
    {
        _transport = transport;
        _deviceId = deviceId;
        _state = state;
        _connectionId = currentConnectionId;
        _build = build;
        _onCluster = onCluster;
        _log = log;
        _now = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _connSub = connectionId.Subscribe(Observers.From<string?>(OnConnectionId));
    }

    /// <summary>On a new connection id → the device announce (NewConnection). is_active reflects whether we're already
    /// playing locally (e.g. after a reconnect mid-playback).</summary>
    void OnConnectionId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _ = PublishAsync(PutStateReasonKind.NewConnection, IsLocallyPlaying());
    }

    public void OnEvent(in PlaybackEvent e)
    {
        if (e.Kind is EvKind.Started or EvKind.TrackChanged)
        {
            lock (_gate)
            {
                var ctx = _state.ContextUri;
                if (ctx != _sessionContextUri) { _sessionId = NewId(); _sessionContextUri = ctx; }   // new context → new session
                _playbackId = NewId();                                                                // each track → new playback id
                if (_startedPlayingAtMs == 0) _startedPlayingAtMs = _now();
            }
        }
        else if (e.Kind == EvKind.Ended)
        {
            lock (_gate) _startedPlayingAtMs = 0;
        }
        bool isActive = _state.CurrentTrack is not null && e.Kind != EvKind.Ended;
        _ = PublishAsync(PutStateReasonKind.PlayerStateChanged, isActive);
    }

    bool IsLocallyPlaying() => _state.CurrentTrack is not null && _state.IsPlaying;

    async Task PublishAsync(PutStateReasonKind reason, bool isActive)
    {
        var connId = _connectionId();
        if (string.IsNullOrEmpty(connId)) return;   // can't PUT before the dealer connection id arrives

        var snap = BuildSnapshot();
        uint mid;
        lock (_gate)
        {
            // Dedup repeat player-state publishes with identical salient fields (avoid PutState spam on no-op events).
            string key = reason + "|" + isActive + "|" + (snap?.TrackUri ?? "") + "|" + (snap?.IsPlaying ?? false);
            if (reason == PutStateReasonKind.PlayerStateChanged && key == _lastPublishKey) return;
            _lastPublishKey = key;
            mid = ++_messageId;
        }
        try
        {
            var bytes = _build(reason, snap, mid, isActive);
            var resp = await _transport.Publish(_deviceId, connId!, bytes).ConfigureAwait(false);
            _log?.Invoke(resp.Ok ? $"put-state ({reason}, active={isActive}, track={snap?.TrackUri ?? "-"})"
                                 : $"put-state failed ({resp.Status})");
            if (resp.Ok && resp.Body.Length > 0) _onCluster?.Invoke(resp.Body);
        }
        catch (Exception ex) { _log?.Invoke("put-state error: " + ex.Message); }
    }

    LocalPlaybackSnapshot? BuildSnapshot()
    {
        var t = _state.CurrentTrack;
        if (t is null) return null;
        var next = new List<string>();
        foreach (var qe in _state.Queue) if (qe.Bucket == QueueBucket.NextUp) next.Add(qe.Track.Uri);
        long started, hasBeen; string sid, pid;
        lock (_gate)
        {
            started = _startedPlayingAtMs; sid = _sessionId; pid = _playbackId;
            hasBeen = started > 0 && _state.IsPlaying ? Math.Max(0, _now() - started) : 0;
        }
        return new LocalPlaybackSnapshot(t.Uri, "", _state.ContextUri, _state.PositionMs, _state.DurationMs,
            _state.IsPlaying, !_state.IsPlaying, _state.IsShuffle, _state.Repeat, next, sid, pid, hasBeen, started);
    }

    static string NewId() => Guid.NewGuid().ToString("N");

    public void Dispose() => _connSub.Dispose();
}
