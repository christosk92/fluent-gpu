using System;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

// ── The Spotify Connect control-plane service (proto-free, unit-testable) ─────────────────────────────────────────────
// Captures the dealer connection_id from the pusher hello HEADER (not the URI tail), and on each NEW connection_id PUTs the
// device announce via ITransport.Publish — re-injecting the returned Cluster bytes so the announce-response and the live
// cluster pushes share one ingest path (Stage D). The proto PutStateRequest is built by an injected delegate (SpotifyLive's
// ConnectStateBuilder), so this orchestration stays protobuf-free and testable against StubTransport.
public sealed class ConnectService : IDisposable
{
    readonly ITransport _transport;
    readonly string _deviceId;
    readonly Func<uint, byte[]> _buildPutState;
    readonly Action<byte[]>? _onClusterBytes;
    readonly Action<string>? _log;
    readonly SimpleSubject<string?> _connectionId = new(null);
    readonly IDisposable _pusherSub;
    readonly object _gate = new();
    string? _connId;
    string? _announcedFor;
    uint _messageId;

    public ConnectService(ITransport transport, string deviceId, Func<uint, byte[]> buildPutState,
        Action<byte[]>? onClusterBytes = null, Action<string>? log = null)
    {
        _transport = transport;
        _deviceId = deviceId;
        _buildPutState = buildPutState;
        _onClusterBytes = onClusterBytes;
        _log = log;
        // ONE subscription on the pusher topic; the announce gate fires off the connection-id header.
        _pusherSub = transport.Events("hm://pusher/v1/connections/").Subscribe(Observers.From<WireEvent>(OnPusher));
    }

    /// <summary>The current dealer connection id (null until the first pusher hello / after a reconnect drop).</summary>
    public IObservable<string?> ConnectionId => _connectionId;
    public string? CurrentConnectionId { get { lock (_gate) return _connId; } }

    void OnPusher(WireEvent e)
    {
        if (e.Headers is null || !e.Headers.TryGetValue("Spotify-Connection-Id", out var id) || string.IsNullOrEmpty(id))
            return;
        bool announce;
        lock (_gate)
        {
            _connId = id;
            announce = _announcedFor != id;   // a NEW connection id (first hello, or post-reconnect) re-announces
            if (announce) _announcedFor = id;
        }
        _connectionId.OnNext(id);
        if (announce) _ = AnnounceAsync(id);
    }

    async Task AnnounceAsync(string connId)
    {
        try
        {
            uint mid;
            lock (_gate) mid = ++_messageId;
            var bytes = _buildPutState(mid);
            var resp = await _transport.Publish(_deviceId, connId, bytes).ConfigureAwait(false);
            _log?.Invoke(resp.Ok ? "connect-state device announced (now visible in Spotify Connect)"
                                 : $"connect-state announce failed ({resp.Status})");
            if (resp.Ok && resp.Body.Length > 0) _onClusterBytes?.Invoke(resp.Body);
        }
        catch (Exception ex) { _log?.Invoke("connect-state announce error: " + ex.Message); }
    }

    public void Dispose() => _pusherSub.Dispose();
}
