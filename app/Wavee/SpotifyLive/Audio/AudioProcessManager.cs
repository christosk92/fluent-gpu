using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Manages the x64 audio host channel: owns the SINGLE demuxing read loop (replies matched by id vs
/// host-initiated notifications at id == 0), and self-heals — a dead pipe is dropped (lazy reconnect) and a wedged
/// request (timeout: a native derive can't be cancelled) recycles the channel and retries once. No silent hangs.</summary>
public sealed class AudioProcessManager : IAsyncDisposable
{
    readonly Action<string>? _log;
    readonly Func<CancellationToken, Task<IIpcChannel>> _connect;
    readonly SemaphoreSlim _startGate = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pending = new();
    IIpcChannel? _channel;
    CancellationTokenSource? _readCts;
    long _msgId;
    bool _disposed;

    /// <summary>Host-initiated frames (id == 0): StateUpdate, TrackFinished, Ready.</summary>
    public event Action<string, JsonElement?>? Notification;

    public AudioProcessManager(Action<string>? log = null)
        : this(log, ct => ProcessIpcChannel.SpawnAsync(log, ct)) { }

    /// <summary>Test seam: inject a channel factory (in-memory fake) instead of spawning the real process.</summary>
    public AudioProcessManager(Action<string>? log, Func<CancellationToken, Task<IIpcChannel>> connect)
    {
        _log = log;
        _connect = connect;
    }

    public bool IsRunning => _channel is not null;

    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_channel is not null) return;
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try { if (_channel is null && !_disposed) await ConnectLockedAsync(ct).ConfigureAwait(false); }
        finally { _startGate.Release(); }
    }

    async Task ConnectLockedAsync(CancellationToken ct)
    {
        var channel = await _connect(ct).ConfigureAwait(false);
        _channel = channel;
        _readCts = new CancellationTokenSource();
        var loopCt = _readCts.Token;
        _ = Task.Run(() => ReadLoopAsync(channel, loopCt));
    }

    /// <summary>Drop the given channel (if still current) so the next request reconnects. Fails all pending waiters.</summary>
    async Task RecycleAsync(IIpcChannel? bad)
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try { if (ReferenceEquals(_channel, bad)) TeardownLocked(); }
        finally { _startGate.Release(); }
    }

    void TeardownLocked()
    {
        try { _readCts?.Cancel(); } catch { }
        _readCts?.Dispose(); _readCts = null;
        var ch = _channel; _channel = null;
        FailAllPending(new IOException("audio host recycled"));
        try { ch?.Dispose(); } catch { }
    }

    async Task ReadLoopAsync(IIpcChannel channel, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (type, id, payload) = await channel.ReadAsync(ct).ConfigureAwait(false);
                if (id != 0 && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(payload);
                else Notification?.Invoke(type, payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log?.Invoke("audio host read loop ended: " + ex.Message);
            // The pipe died (child crashed / closed). Drop the channel so the next request reconnects lazily
            // (don't eagerly respawn — that would crash-loop a genuinely broken host).
            await RecycleAsync(channel).ConfigureAwait(false);
        }
    }

    void FailAllPending(Exception ex)
    {
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var tcs)) tcs.TrySetException(ex);
    }

    /// <summary>Fire-and-forget send (no reply): Play/Pause/Seek/Stop/SetVolume. id == 0.</summary>
    public async Task SendAsync<T>(string type, T payload, CancellationToken ct)
    {
        try
        {
            await EnsureStartedAsync(ct).ConfigureAwait(false);
            var channel = _channel;
            if (channel is not null) await channel.SendAsync(type, 0, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _log?.Invoke($"send {type} failed: {ex.Message}"); }
    }

    /// <summary>Send a request and await the host's reply (matched by id). Recycles + retries once on timeout / pipe death.</summary>
    public async Task<TResp> RequestAsync<TReq, TResp>(string type, TReq payload, Func<JsonElement?, TResp> parse, TimeSpan timeout, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await EnsureStartedAsync(ct).ConfigureAwait(false);
            var channel = _channel;
            if (channel is null) { last = new IOException("audio host unavailable"); continue; }

            long id = Interlocked.Increment(ref _msgId);
            var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                await channel.SendAsync(type, id, payload, ct).ConfigureAwait(false);
                var reply = await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
                return parse(reply);
            }
            catch (OperationCanceledException) { throw; }   // caller cancelled — not a host fault
            catch (TimeoutException ex) when (attempt == 0)
            {
                _log?.Invoke($"'{type}' timed out after {timeout.TotalSeconds:0}s — recycling audio host (native call may be wedged)");
                last = ex;
                await RecycleAsync(channel).ConfigureAwait(false);   // a wedged native derive can't be cancelled → kill+restart
            }
            catch (Exception ex) when (attempt == 0)
            {
                _log?.Invoke($"'{type}' failed ({ex.Message}) — recycling audio host");
                last = ex;
                await RecycleAsync(channel).ConfigureAwait(false);
            }
            finally { _pending.TryRemove(id, out _); }
        }
        throw last ?? new TimeoutException($"audio host request '{type}' failed after recycle");
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            var ch = _channel;
            if (ch is not null) await ch.SendAsync(IpcMessageTypes.Shutdown, 0, new EmptyPayload(), CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* best effort */ }
        await _startGate.WaitAsync().ConfigureAwait(false);
        try { TeardownLocked(); }
        finally { _startGate.Release(); }
    }
}
