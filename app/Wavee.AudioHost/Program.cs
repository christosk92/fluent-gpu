using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Wavee.AudioHost.Audio;
using Wavee.Backend.Audio;
using Wavee.AudioHost.PlayPlay;

namespace Wavee.AudioHost;

/// <summary>x64 audio host: serial IPC command loop (derive key, fast-start load, decode/output).</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? pipeName = null, token = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--pipe") pipeName = args[i + 1];
            if (args[i] == "--token") token = args[i + 1];
        }
        if (string.IsNullOrEmpty(pipeName)) { Console.Error.WriteLine("missing --pipe"); return 2; }

        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync().ConfigureAwait(false);
        var transport = IpcPipeTransport.FromServerStream(server);
        var service = new AudioHostService(transport, token, msg => Console.Error.WriteLine(msg));
        await service.RunAsync().ConfigureAwait(false);
        return 0;
    }
}

sealed class AudioHostService
{
    readonly IpcPipeTransport _ipc;
    readonly string? _launchToken;
    readonly Action<string> _log;
    readonly Dictionary<string, PlayPlayKeyEmulator> _emulators = new();
    readonly AudioPlayEngine _engine;

    public AudioHostService(IpcPipeTransport ipc, string? launchToken, Action<string> log)
    {
        _ipc = ipc;
        _launchToken = launchToken;
        _log = log;
        _engine = new AudioPlayEngine(log);
        _engine.State += st => Notify(IpcMessageTypes.StateUpdate, st);
        _engine.TrackFinished += () => Notify(IpcMessageTypes.TrackFinished, new TrackFinishedMessage { Reason = "finished" });
    }

    public async Task RunAsync()
    {
        await _ipc.SendAsync(IpcMessageTypes.Ready, 0, new ReadyMessage { Ok = true }, CancellationToken.None).ConfigureAwait(false);
        while (true)
        {
            var (type, id, payload) = await _ipc.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                switch (type)
                {
                    case IpcMessageTypes.DerivePlayPlayKey:
                        await HandleDeriveAsync(id, payload).ConfigureAwait(false);
                        break;
                    case IpcMessageTypes.LoadFastStart:
                        var load = payload?.Deserialize(AudioIpcJsonContext.Default.LoadFastStartCommand);
                        if (load is not null) _engine.LoadFastStart(load);
                        await ReplyOk(id, load?.CorrelationId ?? "").ConfigureAwait(false);
                        break;
                    case IpcMessageTypes.SupplyBody:
                        var supply = payload?.Deserialize(AudioIpcJsonContext.Default.SupplyBodyCommand);
                        if (supply is not null) _engine.SupplyBody(supply);
                        await ReplyOk(id, supply?.CorrelationId ?? "").ConfigureAwait(false);
                        break;
                    case IpcMessageTypes.Play: _engine.Play(); break;
                    case IpcMessageTypes.Pause: _engine.Pause(); break;
                    case IpcMessageTypes.Stop: _engine.Stop(); break;
                    case IpcMessageTypes.Seek:
                        _engine.Seek(payload?.Deserialize(AudioIpcJsonContext.Default.SeekCommand)?.PositionMs ?? 0);
                        break;
                    case IpcMessageTypes.SetVolume:
                        _engine.SetVolume(payload?.Deserialize(AudioIpcJsonContext.Default.VolumeCommand)?.Volume ?? 1.0);
                        break;
                    case IpcMessageTypes.Shutdown:
                        _engine.Dispose();
                        return;
                    case IpcMessageTypes.Ping:
                        await _ipc.SendAsync(IpcMessageTypes.Pong, id, new EmptyPayload(), CancellationToken.None).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log("command failed: " + ex.Message);
                await _ipc.SendAsync(IpcMessageTypes.CommandResult, id,
                    new CommandResultMessage { CorrelationId = "", Ok = false, Detail = ex.Message }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    void Notify<T>(string type, T payload) => _ = _ipc.SendAsync(type, 0, payload, CancellationToken.None);

    async Task HandleDeriveAsync(long id, JsonElement? payload)
    {
        if (payload is null) throw new InvalidOperationException("missing payload");
        var cmd = payload.Value.Deserialize(AudioIpcJsonContext.Default.DerivePlayPlayKeyCommand)
                  ?? throw new InvalidOperationException("bad derive command");
        try
        {
            var emu = GetEmulator(cmd.SpotifyDllPath, cmd.Config);
            var obf = Convert.FromHexString(cmd.ObfuscatedKeyHex);
            var cid = Convert.FromHexString(cmd.ContentIdHex);
            var key = emu.DeriveAesKey(obf, cid);
            var result = new DerivePlayPlayKeyResult
            {
                CorrelationId = cmd.CorrelationId,
                AesKeyHex = Convert.ToHexStringLower(key),
            };
            await _ipc.SendAsync(IpcMessageTypes.DerivePlayPlayKey, id, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("never reached", StringComparison.OrdinalIgnoreCase))
        {
            await _ipc.SendAsync(IpcMessageTypes.DerivePlayPlayKey, id, new DerivePlayPlayKeyResult
            {
                CorrelationId = cmd.CorrelationId,
                Reason = AudioKeyFailureReason.RotationDrift,
                Detail = ex.Message,
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    PlayPlayKeyEmulator GetEmulator(string dllPath, PlayPlayConfig config)
    {
        var key = dllPath + "|" + config.Version;
        if (_emulators.TryGetValue(key, out var existing)) return existing;
        var emu = new PlayPlayKeyEmulator(dllPath, config, _log);
        _emulators[key] = emu;
        return emu;
    }

    async Task ReplyOk(long id, string correlationId)
    {
        await _ipc.SendAsync(IpcMessageTypes.CommandResult, id,
            new CommandResultMessage { CorrelationId = correlationId, Ok = true }, CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>Minimal pipe transport (mirrors UI-side framing).</summary>
sealed class IpcPipeTransport : IDisposable
{
    const int MaxFrameBytes = 4 * 1024 * 1024;
    readonly Stream _stream;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    public IpcPipeTransport(Stream stream) => _stream = stream;
    public static IpcPipeTransport FromServerStream(Stream stream) => new(stream);

    public async Task SendAsync<T>(string type, long id, T payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, typeof(T), AudioIpcJsonContext.Default);
        var envelope = $"{{\"type\":\"{type}\",\"id\":{id},\"payload\":{json}}}";
        var bytes = Encoding.UTF8.GetBytes(envelope);
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
        var body = new byte[len];
        await ReadExactAsync(_stream, body, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return (root.GetProperty("type").GetString() ?? "", root.GetProperty("id").GetInt64(),
            root.TryGetProperty("payload", out var p) ? p.Clone() : null);
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

    public void Dispose() => _stream.Dispose();
}
