using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Length-prefixed JSON framing over a named pipe: [4B BE length][UTF-8 JSON], 4 MB cap.</summary>
public sealed class IpcPipeTransport : IIpcChannel
{
    const int MaxFrameBytes = 4 * 1024 * 1024;
    readonly Stream _stream;
    readonly SemaphoreSlim _writeLock = new(1, 1);

    public IpcPipeTransport(Stream stream) => _stream = stream;

    public static async Task<IpcPipeTransport> ConnectClientAsync(string pipeName, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
        return new IpcPipeTransport(pipe);
    }

    public static IpcPipeTransport FromServerStream(Stream stream) => new(stream);

    public async Task SendAsync<T>(string type, long id, T payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, typeof(T), AudioIpcJsonContext.Default);
        var envelope = $"{{\"type\":\"{type}\",\"id\":{id},\"payload\":{json}}}";
        var bytes = Encoding.UTF8.GetBytes(envelope);
        if (bytes.Length > MaxFrameBytes) throw new InvalidOperationException("IPC frame too large");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, ct).ConfigureAwait(false);
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<(string Type, long Id, JsonElement? Payload)> ReadAsync(CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactAsync(_stream, header, ct).ConfigureAwait(false);
        int len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len <= 0 || len > MaxFrameBytes) throw new InvalidOperationException($"Invalid IPC frame length {len}");
        var body = new byte[len];
        await ReadExactAsync(_stream, body, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString() ?? "";
        var id = root.GetProperty("id").GetInt64();
        JsonElement? payload = root.TryGetProperty("payload", out var p) ? p.Clone() : null;
        return (type, id, payload);
    }

    static async Task ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(off, buf.Length - off), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
    }

    public void Dispose() => (_stream as IDisposable)?.Dispose();
}
