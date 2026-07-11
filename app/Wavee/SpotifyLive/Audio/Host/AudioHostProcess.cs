using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio.Host;

/// <summary>Owns the audio-child channel: one demuxing read loop, request correlation, hello/version gate and recycle.</summary>
internal sealed class AudioHostProcess : IAsyncDisposable
{
    readonly WaveeLogger _log;
    readonly Func<string, CancellationToken, Task<IIpcChannel>> _connect;
    readonly SemaphoreSlim _startGate = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pending = new();
    readonly string _launchToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());

    IIpcChannel? _channel;
    CancellationTokenSource? _readCts;
    RuntimeAssetDescriptor? _pack;
    EqualizerSettings _equalizer = EqualizerSettings.Flat;
    CrossfadeSettings _crossfade = CrossfadeSettings.Off;
    double _volume = 1.0;
    string? _outputDeviceId;
    long _msgId;
    bool _disposed;

    public event Action<string, JsonElement?>? Notification;
    public event Action<Exception>? Faulted;

    public AudioHostProcess(WaveeLogger log = default)
        : this(log, (token, ct) => ProcessIpcChannel.SpawnAsync(token, log, ct)) { }

    public AudioHostProcess(WaveeLogger log, Func<string, CancellationToken, Task<IIpcChannel>> connect)
    {
        _log = log;
        _connect = connect;
    }

    public bool IsRunning => _channel is not null;

    public void Configure(RuntimeAsset? asset, EqualizerSettings equalizer, CrossfadeSettings crossfade, double volume,
        string? outputDeviceId = null)
    {
        _pack = asset is null ? null : RuntimeAssetDescriptor.FromAsset(asset);
        _equalizer = equalizer;
        _crossfade = crossfade;
        _volume = volume;
        _outputDeviceId = outputDeviceId;
    }

    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_channel is not null) return;
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_channel is null && !_disposed)
                await ConnectLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    async Task ConnectLockedAsync(CancellationToken ct)
    {
        var channel = await _connect(_launchToken, ct).ConfigureAwait(false);
        _channel = channel;
        _readCts = new CancellationTokenSource();
        var loopCt = _readCts.Token;
        _ = Task.Run(() => ReadLoopAsync(channel, loopCt));

        try
        {
            var ready = await SendRequestOnChannelAsync(channel, IpcMessageTypes.Hello, new HelloCommand
            {
                ContractVersion = AudioIpcContract.Version,
                LaunchToken = _launchToken,
                ParentPid = Environment.ProcessId,
                Pack = _pack,
                Equalizer = _equalizer,
                Crossfade = _crossfade,
                Volume = _volume,
                OutputDeviceId = _outputDeviceId,
            }, p => p is null
                ? new ReadyMessage { Ok = false, ContractVersion = 0, Detail = "missing ready", Pid = 0 }
                : p.Value.Deserialize(AudioIpcJsonContext.Default.ReadyMessage)
                  ?? new ReadyMessage { Ok = false, ContractVersion = 0, Detail = "bad ready", Pid = 0 },
                TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

            if (!ready.Ok || ready.ContractVersion != AudioIpcContract.Version)
                throw new InvalidOperationException("audio host contract mismatch: host=" + ready.ContractVersion +
                    " app=" + AudioIpcContract.Version + " detail=" + (ready.Detail ?? ""));

            _log.Info("audio host ready pid=" + ready.Pid + " contract=" + ready.ContractVersion);
        }
        catch (Exception ex)
        {
            _log.Warn("audio host hello/spawn failed: " + ex.Message, ex);
            TeardownLocked(new IOException("audio host failed during hello"), notifyFault: true);
            throw;
        }
    }

    async Task ReadLoopAsync(IIpcChannel channel, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (type, id, payload) = await channel.ReadAsync(ct).ConfigureAwait(false);
                if (id != 0 && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(payload);
                else
                    Notification?.Invoke(type, payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Info("audio host read loop ended: " + ex.Message);
            await RecycleAsync(channel, ex).ConfigureAwait(false);
        }
    }

    public async Task RecycleAsync(IIpcChannel? bad, Exception reason, bool notifyFault = true)
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (bad is null || ReferenceEquals(_channel, bad))
                TeardownLocked(reason, notifyFault);
        }
        finally
        {
            _startGate.Release();
        }
    }

    void TeardownLocked(Exception reason, bool notifyFault)
    {
        try { _readCts?.Cancel(); } catch { }
        _readCts?.Dispose();
        _readCts = null;

        var channel = _channel;
        _channel = null;
        FailAllPending(reason);
        try { channel?.Dispose(); } catch { }
        if (notifyFault)
            Faulted?.Invoke(reason);
    }

    void FailAllPending(Exception ex)
    {
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(ex);
        }
    }

    public async Task SendAsync<T>(string type, T payload, CancellationToken ct)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        var channel = _channel ?? throw new IOException("audio host unavailable");
        await channel.SendAsync(type, 0, payload, ct).ConfigureAwait(false);
    }

    public async Task<TResp> RequestAsync<TReq, TResp>(
        string type,
        TReq payload,
        Func<JsonElement?, TResp> parse,
        TimeSpan timeout,
        CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await EnsureStartedAsync(ct).ConfigureAwait(false);
            var channel = _channel;
            if (channel is null)
            {
                last = new IOException("audio host unavailable");
                continue;
            }

            try
            {
                return await SendRequestOnChannelAsync(channel, type, payload, parse, timeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException ex) when (attempt == 0)
            {
                _log.Info("'" + type + "' timed out after " + timeout.TotalSeconds.ToString("0") + "s; recycling audio host");
                last = ex;
                await RecycleAsync(channel, ex).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 0)
            {
                _log.Info("'" + type + "' failed (" + ex.Message + "); recycling audio host");
                last = ex;
                await RecycleAsync(channel, ex).ConfigureAwait(false);
            }
        }

        throw last ?? new TimeoutException("audio host request '" + type + "' failed after recycle");
    }

    async Task<TResp> SendRequestOnChannelAsync<TReq, TResp>(
        IIpcChannel channel,
        string type,
        TReq payload,
        Func<JsonElement?, TResp> parse,
        TimeSpan timeout,
        CancellationToken ct)
    {
        long id = Interlocked.Increment(ref _msgId);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await channel.SendAsync(type, id, payload, ct).ConfigureAwait(false);
            var reply = await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            return parse(reply);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            var channel = _channel;
            if (channel is not null)
                await channel.SendAsync(IpcMessageTypes.Shutdown, 0, new EmptyPayload(), CancellationToken.None).ConfigureAwait(false);
        }
        catch { }

        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            TeardownLocked(new ObjectDisposedException(nameof(AudioHostProcess)), notifyFault: true);
        }
        finally
        {
            _startGate.Release();
            _startGate.Dispose();
        }
    }
}
