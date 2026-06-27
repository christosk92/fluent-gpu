using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive;

// ── Stage F — the persistent AP channel (audio-key fetch over the long-lived Shannon socket) ──────────────────────────
// Spotify's login socket is discarded after APWelcome; the audio-key path (0x0c/0x0d) needs a long-lived encrypted channel.
// ApConnection opens its own AP socket (re-using the reusable credential), runs a read-pump that answers pings + routes
// 0x0d/0x0e to the proto-free AudioKeyDispatcher, and sends 0x0c requests. The dispatcher is shared with LiveAudioKeySource.
public sealed class ApConnection : IDisposable
{
    const byte CmdPing = 0x04, CmdPong = 0x49, CmdAesKeyRequest = 0x0c, CmdAesKey = 0x0d, CmdAesKeyError = 0x0e;
    static readonly TimeSpan IdleReadTimeout = TimeSpan.FromSeconds(90);   // > the AP ping interval so idle reads survive

    readonly IDuplexStream _stream;
    readonly ApCodec _codec;
    readonly AudioKeyDispatcher _keys;
    readonly Action<string>? _log;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    Task? _pump;

    ApConnection(IDuplexStream stream, ApCodec codec, AudioKeyDispatcher keys, Action<string>? log)
    {
        _stream = stream;
        _codec = codec;
        _keys = keys;
        _log = log;
    }

    public static async Task<ApConnection> ConnectAsync(string apHost, int apPort, Credential cred, string deviceId,
        AudioKeyDispatcher keys, Action<string>? log, CancellationToken ct)
    {
        var tcp = await TcpDuplexStream.ConnectAsync(apHost, apPort, ct, IdleReadTimeout).ConfigureAwait(false);
        try
        {
            var (_, codec) = await SpotifyConnection.HandshakeRetainAsync(tcp, cred, deviceId, ct).ConfigureAwait(false);
            var conn = new ApConnection(tcp, codec, keys, log);
            conn._pump = Task.Run(() => conn.PumpAsync(conn._cts.Token));
            log?.Invoke("AP channel open (" + apHost + ") — audio-key path ready");
            return conn;
        }
        catch { tcp.Dispose(); throw; }
    }

    public Task SendAudioKeyRequestAsync(byte[] body, CancellationToken ct = default) => SendAsync(CmdAesKeyRequest, body, ct);

    async Task PumpAsync(CancellationToken ct)
    {
        var header = new byte[3];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _stream.ReadExactAsync(header, ct).ConfigureAwait(false);
                var (cmd, len) = _codec.BeginDecode(header);
                var rest = new byte[len + 4];
                await _stream.ReadExactAsync(rest, ct).ConfigureAwait(false);
                var payload = _codec.EndDecode(rest.AsSpan(0, len), rest.AsSpan(len, 4));
                switch (cmd)
                {
                    case CmdAesKey: _keys.OnAesKey(payload); break;
                    case CmdAesKeyError: _keys.OnAesKeyError(payload); break;
                    case CmdPing: await SendAsync(CmdPong, payload, ct).ConfigureAwait(false); break;
                    // Mercury (0xb2-0xb6), country/product trailers, legacy packets: ignored for now.
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* disposed */ }
        catch (Exception ex)
        {
            _log?.Invoke("AP channel dropped: " + ex.Message);
            _keys.FailAll(ex);   // pending key waiters fail → callers retry on the next connection
        }
    }

    async Task SendAsync(byte cmd, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _stream.WriteAsync(_codec.Encode(cmd, payload.Span), ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { (_stream as IDisposable)?.Dispose(); } catch { }
        _sendLock.Dispose();
        _cts.Dispose();
    }
}

// IAudioKeySource over the persistent AP channel + an in-memory key cache. The connection is fetched via a delegate so it
// can be (re)established by the session owner (Stage 0) without re-creating this source.
public sealed class LiveAudioKeySource : IAudioKeySource
{
    readonly AudioKeyDispatcher _dispatcher;
    readonly Func<ApConnection?> _connection;
    readonly Dictionary<string, byte[]> _cache = new();
    readonly object _cacheGate = new();

    public LiveAudioKeySource(AudioKeyDispatcher dispatcher, Func<ApConnection?> connection)
    {
        _dispatcher = dispatcher;
        _connection = connection;
    }

    public async Task<ReadOnlyMemory<byte>> GetKeyAsync(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid, CancellationToken ct = default)
    {
        string hex = Convert.ToHexStringLower(fileId.Span);
        lock (_cacheGate) if (_cache.TryGetValue(hex, out var cached)) return cached;

        var conn = _connection() ?? throw new InvalidOperationException("AP channel not connected");
        var (body, keyTask) = _dispatcher.Begin(fileId.Span, trackGid.Span);
        await conn.SendAudioKeyRequestAsync(body, ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var key = await keyTask.WaitAsync(timeout.Token).ConfigureAwait(false);
        lock (_cacheGate) _cache[hex] = key;
        return key;
    }
}
