using System.IO;
using System.Net.Sockets;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive;

// The real socket behind the backend's IDuplexStream seam — a TCP connection to a Spotify access point. The backend's
// SpotifyApChannel/ApCodec are socket-agnostic; this is the live wire.
public sealed class TcpDuplexStream : IDuplexStream, IDisposable
{
    static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(30);   // per-op backstop: a half-open socket can't hang forever

    readonly Socket _socket;
    readonly NetworkStream _stream;

    TcpDuplexStream(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
    }

    public static async Task<TcpDuplexStream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(OpTimeout);
        await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        return new TcpDuplexStream(socket);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(OpTimeout);
        await _stream.WriteAsync(data, cts.Token).ConfigureAwait(false);
        await _stream.FlushAsync(cts.Token).ConfigureAwait(false);
    }

    public async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(OpTimeout);   // even under CancellationToken.None, a stalled read times out
            int n = await _stream.ReadAsync(buffer.Slice(filled), cts.Token).ConfigureAwait(false);
            if (n == 0) throw new IOException("connection closed by peer");
            filled += n;
        }
    }

    public void Dispose() { _stream.Dispose(); _socket.Dispose(); }
}
