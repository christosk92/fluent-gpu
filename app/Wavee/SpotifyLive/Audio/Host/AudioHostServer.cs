using System.Text.Json;
using Wavee.Backend;
using Wavee.Backend.Audio;
#if WAVEE_PLAYPLAY_LOCAL
using Wavee.PlayPlay;
#endif

namespace Wavee.SpotifyLive.Audio.Host;

/// <summary>Headless child-side audio service. The pipe reader never blocks on native derive or network-adjacent work.</summary>
internal sealed class AudioHostServer : IDisposable
{
    readonly IpcPipeTransport _ipc;
    readonly string? _launchToken;
    readonly Action<string> _log;
    readonly AudioPlayEngine _engine;
    readonly SemaphoreSlim _deriveGate = new(1, 1);
    readonly object _runtimeGate = new();

    long _generation;
    long _pendingPlayGeneration = -1;
    string _trackUri = "";
    string _fileIdHex = "";
    bool _disposed;

#if WAVEE_PLAYPLAY_LOCAL
    RuntimeAsset? _asset;
    PlayPlayRuntime? _runtime;
#endif

    public AudioHostServer(IpcPipeTransport ipc, string? launchToken, Action<string> log)
    {
        _ipc = ipc;
        _launchToken = launchToken;
        _log = log;
        _engine = new AudioPlayEngine(LogAndNotify, (_, seed) => CreateCdnDecryptor(seed));
        _engine.State += OnEngineState;
        _engine.TrackFinished += OnTrackFinished;
    }

    public async Task RunAsync()
    {
        while (!_disposed)
        {
            var (type, id, payload) = await _ipc.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                switch (type)
                {
                    case IpcMessageTypes.Hello:
                        await HandleHelloAsync(id, payload).ConfigureAwait(false);
                        break;
                    case IpcMessageTypes.DerivePlayPlayKey:
                        DispatchDerive(id, payload);
                        break;
                    case IpcMessageTypes.LoadFastStart:
                        HandleLoadFastStart(payload);
                        await ReplyOk(id, GenerationFrom(payload), CorrelationFrom(payload)).ConfigureAwait(false);
                        break;
                    case IpcMessageTypes.SupplyBody:
                        HandleSupplyBody(payload);
                        await ReplyOk(id, GenerationFrom(payload), CorrelationFrom(payload)).ConfigureAwait(false);
                        break;
                    case IpcMessageTypes.Play:
                        HandlePlay(payload);
                        break;
                    case IpcMessageTypes.Pause:
                        HandlePause(payload);
                        break;
                    case IpcMessageTypes.Stop:
                        HandleStop(payload);
                        break;
                    case IpcMessageTypes.Seek:
                        HandleSeek(payload);
                        break;
                    case IpcMessageTypes.SetVolume:
                        HandleVolume(payload);
                        break;
                    case IpcMessageTypes.SetEqualizer:
                        var eq = payload?.Deserialize(AudioIpcJsonContext.Default.SetEqualizerCommand);
                        if (eq is not null && IsCurrent(payload))
                            _engine.SetEqualizer(eq.Settings);
                        await Notify(IpcMessageTypes.EqualizerApplied, new DiagnosticMessage
                        {
                            Generation = GenerationFrom(payload),
                            Kind = "accepted",
                            Detail = "equalizer settings received",
                        }).ConfigureAwait(false);
                        break;
                    case IpcMessageTypes.SetCrossfade:
                        await Notify(IpcMessageTypes.CrossfadeMissed, new DiagnosticMessage
                        {
                            Generation = GenerationFrom(payload),
                            Kind = "not_active",
                            Detail = "single-pipeline host has no prepared next track yet",
                        }).ConfigureAwait(false);
                        break;
                    case IpcMessageTypes.Ping:
                        var ping = payload?.Deserialize(AudioIpcJsonContext.Default.PingMessage) ?? new PingMessage();
                        await _ipc.SendAsync(IpcMessageTypes.Pong, id, new PongMessage
                        {
                            SentUnixMs = ping.SentUnixMs,
                            HostUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        }, CancellationToken.None).ConfigureAwait(false);
                        break;
                    case IpcMessageTypes.Shutdown:
                        Dispose();
                        return;
                }
            }
            catch (Exception ex)
            {
                _log("audio host command failed type=" + type + " detail=" + ex.Message);
                if (id != 0)
                    await _ipc.SendAsync(IpcMessageTypes.CommandResult, id, new CommandResultMessage
                    {
                        Generation = GenerationFrom(payload),
                        CorrelationId = CorrelationFrom(payload),
                        Ok = false,
                        Detail = ex.Message,
                    }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    async Task HandleHelloAsync(long id, JsonElement? payload)
    {
        var hello = payload?.Deserialize(AudioIpcJsonContext.Default.HelloCommand);
        if (hello is null)
        {
            await Ready(id, false, "bad hello").ConfigureAwait(false);
            return;
        }

        if (hello.ContractVersion != AudioIpcContract.Version)
        {
            await Ready(id, false, "contract mismatch").ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(_launchToken) && !string.Equals(_launchToken, hello.LaunchToken, StringComparison.Ordinal))
        {
            await Ready(id, false, "launch token mismatch").ConfigureAwait(false);
            return;
        }

        if (hello.Pack is not null)
            BindPack(hello.Pack.ToAsset());

        _engine.SetVolume(hello.Volume);
        await Ready(id, true, null).ConfigureAwait(false);
    }

    Task Ready(long id, bool ok, string? detail) =>
        _ipc.SendAsync(IpcMessageTypes.Ready, id, new ReadyMessage
        {
            Ok = ok,
            ContractVersion = AudioIpcContract.Version,
            Detail = detail,
            Pid = Environment.ProcessId,
        }, CancellationToken.None);

    void HandleLoadFastStart(JsonElement? payload)
    {
        var cmd = payload?.Deserialize(AudioIpcJsonContext.Default.LoadFastStartCommand)
                  ?? throw new InvalidOperationException("bad load_fast_start payload");
        if (!AcceptGeneration(cmd.Generation))
        {
            LogAndNotify($"load-fast-start ignored stale generation={cmd.Generation} active={Interlocked.Read(ref _generation)} file={cmd.FileIdHex}");
            return;
        }

        if (!Enum.TryParse<AudioFormat>(cmd.Format, out var format))
            throw new InvalidOperationException("unknown audio format " + cmd.Format);

        _trackUri = cmd.TrackUri;
        _fileIdHex = cmd.FileIdHex;
        var head = DecodeBase64(cmd.HeadBytesBase64);
        LogAndNotify($"load-fast-start received generation={cmd.Generation} track={cmd.TrackUri} file={cmd.FileIdHex} fmt={cmd.Format} head={head.Length}B dur={cmd.DurationMs}ms");
        var start = new AudioFastStart(cmd.TrackUri, cmd.FileIdHex, format, cmd.DurationMs, cmd.NormalizationGainDb, head);
        _engine.LoadFastStart(start);
        if (Interlocked.CompareExchange(ref _pendingPlayGeneration, -1, cmd.Generation) == cmd.Generation)
        {
            LogAndNotify($"applying queued play generation={cmd.Generation} file={cmd.FileIdHex}");
            _engine.Play();
        }
    }

    void HandleSupplyBody(JsonElement? payload)
    {
        var cmd = payload?.Deserialize(AudioIpcJsonContext.Default.SupplyBodyCommand)
                  ?? throw new InvalidOperationException("bad supply_body payload");
        if (cmd.Generation != Interlocked.Read(ref _generation))
        {
            LogAndNotify($"supply-body ignored stale generation={cmd.Generation} active={Interlocked.Read(ref _generation)} file={cmd.FileIdHex}");
            return;
        }
        if (_fileIdHex.Length > 0 && !string.Equals(_fileIdHex, cmd.FileIdHex, StringComparison.OrdinalIgnoreCase))
        {
            LogAndNotify($"supply-body ignored stale file={cmd.FileIdHex} active={_fileIdHex} generation={cmd.Generation}");
            return;
        }

        if (!Enum.TryParse<AudioFormat>(cmd.Format, out var format))
            throw new InvalidOperationException("unknown audio format " + cmd.Format);

        var key = Convert.FromHexString(cmd.AesKeyHex);
        var nativeSeed = DecodeBase64(cmd.NativeCdnSeedBase64);
        LogAndNotify($"supply-body received generation={cmd.Generation} track={cmd.TrackUri} file={cmd.FileIdHex} fmt={cmd.Format} urls={cmd.CdnUrls.Length} headBoundary={cmd.HeadBoundary}B key={key.Length}B nativeSeed={nativeSeed.Length}B");
        var body = new AudioStreamHandle(
            cmd.TrackUri,
            cmd.FileIdHex,
            cmd.CdnUrls.Length > 0 ? cmd.CdnUrls[0] : "",
            key,
            format,
            cmd.DurationMs,
            cmd.NormalizationGainDb,
            cmd.CdnUrls,
            cmd.HeadBoundary,
            nativeSeed,
            (AudioSourceKind)cmd.SourceKind);
        _engine.SupplyBody(body);
    }

    void HandlePlay(JsonElement? payload)
    {
        var generation = GenerationFrom(payload);
        var current = Interlocked.Read(ref _generation);
        if (generation == 0 || generation == current)
        {
            LogAndNotify("play received generation=" + generation + " file=" + _fileIdHex);
            _engine.Play();
            return;
        }

        if (generation > current)
        {
            Interlocked.Exchange(ref _pendingPlayGeneration, generation);
            LogAndNotify($"play queued until load generation={generation} current={current}");
            return;
        }

        LogAndNotify($"play ignored stale generation={generation} current={current} file={_fileIdHex}");
    }

    void HandlePause(JsonElement? payload)
    {
        var generation = GenerationFrom(payload);
        var current = Interlocked.Read(ref _generation);
        if (generation > current)
        {
            Interlocked.Exchange(ref _pendingPlayGeneration, -1);
            LogAndNotify($"pause cleared queued play generation={generation} current={current}");
            return;
        }
        if (generation != 0 && generation != current) return;
        Interlocked.Exchange(ref _pendingPlayGeneration, -1);
        LogAndNotify("pause received generation=" + generation + " file=" + _fileIdHex);
        _engine.Pause();
    }

    void HandleStop(JsonElement? payload)
    {
        var generation = GenerationFrom(payload);
        var current = Interlocked.Read(ref _generation);
        if (generation > current)
        {
            Interlocked.Exchange(ref _pendingPlayGeneration, -1);
            LogAndNotify($"stop cleared queued play generation={generation} current={current}");
            return;
        }
        if (generation != 0 && generation != current) return;
        Interlocked.Exchange(ref _pendingPlayGeneration, -1);
        LogAndNotify("stop received generation=" + generation + " file=" + _fileIdHex);
        _engine.Stop();
    }

    void HandleSeek(JsonElement? payload)
    {
        var cmd = payload?.Deserialize(AudioIpcJsonContext.Default.SeekCommand);
        if (cmd is null || cmd.Generation != Interlocked.Read(ref _generation)) return;
        LogAndNotify($"seek received generation={cmd.Generation} position={cmd.PositionMs}ms file={_fileIdHex}");
        _engine.Seek(cmd.PositionMs);
    }

    void HandleVolume(JsonElement? payload)
    {
        var cmd = payload?.Deserialize(AudioIpcJsonContext.Default.VolumeCommand);
        if (cmd is null) return;
        _engine.SetVolume(cmd.Volume);
    }

    bool AcceptGeneration(long generation)
    {
        while (true)
        {
            long current = Interlocked.Read(ref _generation);
            if (generation < current) return false;
            if (generation == current) return true;
            if (Interlocked.CompareExchange(ref _generation, generation, current) == current) return true;
        }
    }

    bool IsCurrent(JsonElement? payload)
    {
        long generation = GenerationFrom(payload);
        return generation == 0 || generation == Interlocked.Read(ref _generation);
    }

    void DispatchDerive(long id, JsonElement? payload)
    {
        var cmd = payload?.Deserialize(AudioIpcJsonContext.Default.DerivePlayPlayKeyCommand)
                  ?? throw new InvalidOperationException("bad derive payload");
        _ = Task.Run(() => HandleDeriveAsync(id, cmd));
    }

    async Task HandleDeriveAsync(long id, DerivePlayPlayKeyCommand cmd)
    {
        await _deriveGate.WaitAsync().ConfigureAwait(false);
        try
        {
#if WAVEE_PLAYPLAY_LOCAL
            var runtime = EnsureRuntime(new RuntimeAsset(cmd.SpotifyDllPath, cmd.Config, cmd.PackId ?? cmd.Config.Version));
            var result = runtime.Derive(
                Convert.FromHexString(cmd.ObfuscatedKeyHex),
                Convert.FromHexString(cmd.ContentIdHex),
                cmd.CorrelationId,
                DecodeBase64(cmd.PlayPlayAuxBase64),
                DecodeBase64(cmd.LicenseRawBase64),
                DecodeBase64(cmd.LicenseRequestBase64));

            await _ipc.SendAsync(IpcMessageTypes.DerivePlayPlayKey, id, new DerivePlayPlayKeyResult
            {
                Generation = cmd.Generation,
                CorrelationId = cmd.CorrelationId,
                AesKeyHex = result.Ok ? Convert.ToHexStringLower(result.Key.Span) : null,
                NativeCdnSeedBase64 = result.NativeCdnSeed.IsEmpty ? null : Convert.ToBase64String(result.NativeCdnSeed.Span),
                DerivedSlabBase64 = result.DerivedSlab.IsEmpty ? null : Convert.ToBase64String(result.DerivedSlab.Span),
                Reason = result.Reason,
                Detail = result.Detail,
            }, CancellationToken.None).ConfigureAwait(false);
#else
            await _ipc.SendAsync(IpcMessageTypes.DerivePlayPlayKey, id, new DerivePlayPlayKeyResult
            {
                Generation = cmd.Generation,
                CorrelationId = cmd.CorrelationId,
                Reason = AudioKeyFailureReason.ProvisioningUnavailable,
                Detail = "Wavee was built without WAVEE_PLAYPLAY_LOCAL",
            }, CancellationToken.None).ConfigureAwait(false);
#endif
        }
        catch (Exception ex)
        {
            await _ipc.SendAsync(IpcMessageTypes.DerivePlayPlayKey, id, new DerivePlayPlayKeyResult
            {
                Generation = cmd.Generation,
                CorrelationId = cmd.CorrelationId,
                Reason = ex.Message.Contains("Architecture mismatch", StringComparison.OrdinalIgnoreCase)
                    ? AudioKeyFailureReason.ArchUnsupported
                    : AudioKeyFailureReason.EmulationFault,
                Detail = ex.Message,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _deriveGate.Release();
        }
    }

#if WAVEE_PLAYPLAY_LOCAL
    void BindPack(RuntimeAsset asset)
    {
        lock (_runtimeGate)
        {
            if (_asset is not null &&
                string.Equals(_asset.PackPath, asset.PackPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_asset.Config.Version, asset.Config.Version, StringComparison.Ordinal) &&
                _runtime is not null)
                return;

            _runtime?.Dispose();
            _runtime = null;
            _asset = asset;
            if (!PlayPlayRuntime.TryCreate(asset, out _runtime, _log) || _runtime is null)
                _log("audio host failed to bind PlayPlay runtime pack=" + asset.PackId);
            else
                _log("audio host bound PlayPlay runtime pack=" + asset.PackId + " arch=" + asset.Config.Arch);
        }
    }

    PlayPlayRuntime EnsureRuntime(RuntimeAsset asset)
    {
        BindPack(asset);
        lock (_runtimeGate)
            return _runtime ?? throw new InvalidOperationException("PlayPlay runtime unavailable");
    }
#else
    void BindPack(RuntimeAsset asset) { }
#endif

    CdnDecryptor? CreateCdnDecryptor(ReadOnlyMemory<byte> seed)
    {
#if WAVEE_PLAYPLAY_LOCAL
        lock (_runtimeGate)
            return _runtime?.CreateCdnDecryptor(seed);
#else
        return null;
#endif
    }

    async Task ReplyOk(long id, long generation, string correlationId)
    {
        if (id == 0) return;
        await _ipc.SendAsync(IpcMessageTypes.CommandResult, id, new CommandResultMessage
        {
            Generation = generation,
            CorrelationId = correlationId,
            Ok = true,
        }, CancellationToken.None).ConfigureAwait(false);
    }

    void OnEngineState(AudioHostSignal signal)
    {
        var update = new HostStateUpdate
        {
            Generation = Interlocked.Read(ref _generation),
            IsPlaying = signal.Kind is AudioHostSignalKind.Playing or AudioHostSignalKind.PositionTick,
            IsBuffering = signal.Kind == AudioHostSignalKind.Buffering,
            IsPrebuffering = signal.Kind == AudioHostSignalKind.Prebuffering,
            PositionMs = signal.PositionMs,
        };
        _ = Notify(IpcMessageTypes.StateUpdate, update);
    }

    void OnTrackFinished()
    {
        _ = Notify(IpcMessageTypes.TrackFinished, new TrackFinishedMessage
        {
            Generation = Interlocked.Read(ref _generation),
            TrackUri = _trackUri,
            Reason = "finished",
        });
    }

    Task Notify<T>(string type, T payload) => _ipc.SendAsync(type, 0, payload, CancellationToken.None);

    void LogAndNotify(string message)
    {
        _log(message);
        try
        {
            var task = Notify(IpcMessageTypes.Diagnostic, new DiagnosticMessage
            {
                Generation = Interlocked.Read(ref _generation),
                Kind = "engine",
                Detail = message,
            });
            _ = task.ContinueWith(static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch { }
    }

    static long GenerationFrom(JsonElement? payload)
    {
        if (payload is null) return 0;
        return payload.Value.TryGetProperty("generation", out var g) && g.TryGetInt64(out long value) ? value : 0;
    }

    static string CorrelationFrom(JsonElement? payload)
    {
        if (payload is null) return "";
        return payload.Value.TryGetProperty("correlationId", out var c) ? c.GetString() ?? "" : "";
    }

    static byte[] DecodeBase64(string? value) =>
        string.IsNullOrEmpty(value) ? Array.Empty<byte>() : Convert.FromBase64String(value);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.State -= OnEngineState;
        _engine.TrackFinished -= OnTrackFinished;
        _engine.Dispose();
        _deriveGate.Dispose();
#if WAVEE_PLAYPLAY_LOCAL
        lock (_runtimeGate)
        {
            _runtime?.Dispose();
            _runtime = null;
        }
#endif
    }
}
