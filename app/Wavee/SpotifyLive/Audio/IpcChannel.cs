using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.SpotifyLive.Audio;

/// <summary>A duplex message channel to the audio host. Abstracts the process+pipe so the manager's demux/recycle logic
/// is testable with an in-memory fake.</summary>
public interface IIpcChannel : IDisposable
{
    Task SendAsync<T>(string type, long id, T payload, CancellationToken ct);
    Task<(string Type, long Id, JsonElement? Payload)> ReadAsync(CancellationToken ct);
}

/// <summary>The real channel: spawns the x64 Wavee.AudioHost child over a per-launch named pipe and kills it on dispose.</summary>
public sealed class ProcessIpcChannel : IIpcChannel
{
    readonly Process _process;
    readonly IpcPipeTransport _ipc;

    ProcessIpcChannel(Process process, IpcPipeTransport ipc) { _process = process; _ipc = ipc; }

    public static async Task<IIpcChannel> SpawnAsync(Action<string>? log, CancellationToken ct)
    {
        var pipeName = $"WaveeAudio_{Environment.ProcessId}_{Guid.NewGuid():N}";
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var hostPath = Path.Combine(AppContext.BaseDirectory, "Wavee.AudioHost.exe");
        if (!File.Exists(hostPath)) throw new FileNotFoundException("Wavee.AudioHost.exe not found beside the app", hostPath);

        var psi = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "--pipe", pipeName, "--token", token, "--parent", Environment.ProcessId.ToString() },
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Wavee.AudioHost");
        try
        {
            var ipc = await IpcPipeTransport.ConnectClientAsync(pipeName, ct).ConfigureAwait(false);
            log?.Invoke($"AudioHost started pid={process.Id} pipe={pipeName}");
            return new ProcessIpcChannel(process, ipc);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw;
        }
    }

    public Task SendAsync<T>(string type, long id, T payload, CancellationToken ct) => _ipc.SendAsync(type, id, payload, ct);
    public Task<(string Type, long Id, JsonElement? Payload)> ReadAsync(CancellationToken ct) => _ipc.ReadAsync(ct);

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _process.Dispose(); } catch { }
        _ipc.Dispose();
    }
}
