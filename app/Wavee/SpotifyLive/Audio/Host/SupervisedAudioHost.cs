using System.Text.Json;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee.SpotifyLive.Audio.Host;

/// <summary>Parent-side audio host: self-relaunches Wavee.exe --audio-host, caches state, respawns, then breaks to in-proc.</summary>
public sealed class SupervisedAudioHost : IAudioHost, IAudioDspControl, IPlayPlayKeyDeriver
{
    const int CrashLimit = 3;
    static readonly TimeSpan CrashWindow = TimeSpan.FromSeconds(60);

    readonly AudioHostProcess _process;
    readonly InProcessAudioHost _fallback;
    readonly Func<IPlayPlayKeyDeriver?> _fallbackDeriver;
    readonly SimpleSubject<AudioHostSignal> _signals = new();
    readonly Action<string>? _log;
    readonly object _gate = new();
    readonly Queue<long> _faults = new();
    readonly Timer _heartbeat;
    readonly IDisposable _fallbackSub;
    readonly bool _preferRemote;

    RuntimeAsset? _asset;
    EqualizerSettings _equalizer = EqualizerSettings.Flat;
    CrossfadeSettings _crossfade = CrossfadeSettings.Off;
    AudioFastStart? _lastStart;
    AudioStreamHandle? _lastBody;
    string _activeFileIdHex = "";
    long _generation;
    long _positionMs;
    bool _playing;
    bool _buffering;
    bool _prebuffering;
    bool _wantPlaying;
    bool _remoteDisabled;
    bool _usingFallback;
    bool _disposed;
    bool _circuitRaised;
    double _volume = 1.0;

    public event Action? CircuitBroken;

    public SupervisedAudioHost(
        Func<IPlayPlayKeyDeriver?> fallbackDeriver,
        Func<IPlayPlayCdnDecryptorFactory?> fallbackDecryptors,
        Action<string>? log = null,
        AudioBodyDiskCache? bodyDisk = null)
    {
        _fallbackDeriver = fallbackDeriver;
        _log = log;
        _preferRemote = Environment.GetEnvironmentVariable("WAVEE_AUDIO_INPROC") != "1" &&
                        Environment.GetEnvironmentVariable("WAVEE_AUDIO_OOP") != "0";
        _fallback = new InProcessAudioHost(fallbackDecryptors, log, bodyDisk);
        _fallbackSub = _fallback.Signals.Subscribe(new SignalObserver(OnFallbackSignal));
        _process = new AudioHostProcess(log);
        _process.Notification += OnNotification;
        _process.Faulted += OnFaulted;
        _heartbeat = new Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public bool IsRemoteActive => RemoteAvailable && !_usingFallback;
    public long PositionMs => _usingFallback ? _fallback.PositionMs : Interlocked.Read(ref _positionMs);
    public bool IsPlaying => _usingFallback ? _fallback.IsPlaying : _playing;
    public bool IsBuffering => _usingFallback ? _fallback.IsBuffering : (_buffering || _prebuffering);
    public IObservable<AudioHostSignal> Signals => _signals;

    bool RemoteAvailable => _preferRemote && !_remoteDisabled;

    public void SetRuntimeAsset(RuntimeAsset? asset)
    {
        bool changed;
        lock (_gate)
        {
            changed = !SameAsset(_asset, asset);
            _asset = asset;
        }

        ConfigureProcess();
        if (changed && _process.IsRunning)
            _ = RestartRemoteAsync(new InvalidOperationException("audio runtime pack changed"), planned: true);
    }

    public void RebindFallbackDecryptors(Func<IPlayPlayCdnDecryptorFactory?> decryptors) =>
        _fallback.RebindPlayPlay(decryptors);

    public void Load(in AudioStreamHandle stream)
    {
        var s = stream;
        long generation = NextGeneration(s.FileIdHex);
        var start = new AudioFastStart(s.TrackUri, s.FileIdHex, s.Format, s.DurationMs, s.NormalizationGainDb, default);
        lock (_gate)
        {
            _lastStart = start;
            _lastBody = s;
            _wantPlaying = false;
            _positionMs = 0;
        }

        if (!RemoteAvailable)
        {
            UseFallback();
            _fallback.Load(s);
            return;
        }

        _ = RunRemoteAsync(async () =>
        {
            await SendLoadFastStartAsync(start, generation).ConfigureAwait(false);
            await SendSupplyBodyAsync(s, generation).ConfigureAwait(false);
            _usingFallback = false;
        }, () => _fallback.Load(s));
    }

    public void LoadFastStart(in AudioFastStart start)
    {
        var s = start;
        long generation = NextGeneration(s.FileIdHex);
        lock (_gate)
        {
            _lastStart = s;
            _lastBody = null;
            _wantPlaying = false;
            _positionMs = 0;
        }

        if (!RemoteAvailable)
        {
            UseFallback();
            _fallback.LoadFastStart(s);
            return;
        }

        _ = RunRemoteAsync(async () =>
        {
            await SendLoadFastStartAsync(s, generation).ConfigureAwait(false);
            _usingFallback = false;
        }, () => _fallback.LoadFastStart(s));
    }

    public void SupplyBody(in AudioStreamHandle body)
    {
        var b = body;
        long generation;
        lock (_gate)
        {
            if (_activeFileIdHex.Length > 0 && !string.Equals(_activeFileIdHex, b.FileIdHex, StringComparison.OrdinalIgnoreCase))
            {
                _log?.Invoke("remote audio stale body ignored file=" + b.FileIdHex + " active=" + _activeFileIdHex);
                return;
            }

            _lastBody = b;
            generation = _generation;
        }

        if (!RemoteAvailable)
        {
            UseFallback();
            _fallback.SupplyBody(b);
            return;
        }

        _ = RunRemoteAsync(async () => await SendSupplyBodyAsync(b, generation).ConfigureAwait(false),
            () => _fallback.SupplyBody(b));
    }

    public void Play()
    {
        _wantPlaying = true;
        if (!RemoteAvailable)
        {
            UseFallback();
            _fallback.Play();
            return;
        }

        long generation = Interlocked.Read(ref _generation);
        _ = RunRemoteAsync(async () =>
        {
            _log?.Invoke($"audio host send play generation={generation} file={_activeFileIdHex}");
            await _process.SendAsync(IpcMessageTypes.Play, new EmptyPayload { Generation = generation }, CancellationToken.None).ConfigureAwait(false);
        }, () => _fallback.Play());
    }

    public void Pause()
    {
        _wantPlaying = false;
        if (!RemoteAvailable)
        {
            _fallback.Pause();
            return;
        }

        long generation = Interlocked.Read(ref _generation);
        _ = RunRemoteAsync(() => _process.SendAsync(IpcMessageTypes.Pause, new EmptyPayload { Generation = generation }, CancellationToken.None),
            () => _fallback.Pause());
    }

    public void Stop()
    {
        _wantPlaying = false;
        if (!RemoteAvailable)
        {
            _fallback.Stop();
            return;
        }

        long generation = Interlocked.Read(ref _generation);
        _ = RunRemoteAsync(() => _process.SendAsync(IpcMessageTypes.Stop, new EmptyPayload { Generation = generation }, CancellationToken.None),
            () => _fallback.Stop());
    }

    public void Seek(long positionMs)
    {
        Interlocked.Exchange(ref _positionMs, Math.Max(0, positionMs));
        if (!RemoteAvailable)
        {
            _fallback.Seek(positionMs);
            return;
        }

        long generation = Interlocked.Read(ref _generation);
        _ = RunRemoteAsync(() => _process.SendAsync(IpcMessageTypes.Seek,
            new SeekCommand { Generation = generation, PositionMs = Math.Max(0, positionMs) }, CancellationToken.None),
            () => _fallback.Seek(positionMs));
    }

    public void SetVolume(double volume01)
    {
        _volume = Math.Clamp(volume01, 0.0, 1.0);
        ConfigureProcess();
        _fallback.SetVolume(_volume);
        if (!RemoteAvailable) return;

        long generation = Interlocked.Read(ref _generation);
        _ = RunRemoteAsync(() => _process.SendAsync(IpcMessageTypes.SetVolume,
            new VolumeCommand { Generation = generation, Volume = _volume }, CancellationToken.None),
            () => { });
    }

    public void SetEqualizer(bool enabled, ReadOnlySpan<float> gainsDb, float preampDb = 0f)
    {
        var gains = new float[10];
        gainsDb[..Math.Min(gainsDb.Length, gains.Length)].CopyTo(gains);
        var next = new EqualizerSettings { Enabled = enabled, GainsDb = gains, PreampDb = preampDb };
        if (SameEqualizer(_equalizer, next)) return;
        _equalizer = next;
        ConfigureProcess();
        _fallback.SetEqualizer(enabled, gains, preampDb);
        if (!RemoteAvailable) return;

        long generation = Interlocked.Read(ref _generation);
        _ = RunRemoteAsync(() => _process.SendAsync(IpcMessageTypes.SetEqualizer,
            new SetEqualizerCommand { Generation = generation, Settings = _equalizer }, CancellationToken.None),
            () => { });
    }

    public void SetCrossfade(bool enabled, int durationMs)
    {
        var next = new CrossfadeSettings { Enabled = enabled, DurationMs = Math.Clamp(durationMs, 0, 12_000) };
        if (SameCrossfade(_crossfade, next)) return;
        _crossfade = next;
        ConfigureProcess();
        _fallback.SetCrossfade(_crossfade.Enabled, _crossfade.DurationMs);
        if (!RemoteAvailable) return;

        long generation = Interlocked.Read(ref _generation);
        _ = RunRemoteAsync(() => _process.SendAsync(IpcMessageTypes.SetCrossfade,
            new SetCrossfadeCommand { Generation = generation, Settings = _crossfade }, CancellationToken.None),
            () => { });
    }

    public Task<PlayPlayDeriveResult> DeriveAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, CancellationToken ct = default)
        => DeriveAsync(obfuscatedKey, contentId, config, spotifyDllPath, correlationId, default, default, default, ct);

    public async Task<PlayPlayDeriveResult> DeriveAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, ReadOnlyMemory<byte> playPlayAux,
        ReadOnlyMemory<byte> licenseRaw = default, ReadOnlyMemory<byte> licenseRequest = default, CancellationToken ct = default)
    {
        if (!RemoteAvailable)
            return await DeriveFallbackAsync(obfuscatedKey, contentId, config, spotifyDllPath, correlationId, playPlayAux, licenseRaw, licenseRequest, ct).ConfigureAwait(false);

        try
        {
            ConfigureProcess();
            var cmd = new DerivePlayPlayKeyCommand
            {
                Generation = Interlocked.Read(ref _generation),
                CorrelationId = correlationId,
                ObfuscatedKeyHex = Convert.ToHexStringLower(obfuscatedKey.Span),
                ContentIdHex = Convert.ToHexStringLower(contentId.Span),
                SpotifyDllPath = spotifyDllPath,
                PackId = _asset?.PackId,
                Config = config,
                PlayPlayAuxBase64 = playPlayAux.IsEmpty ? null : Convert.ToBase64String(playPlayAux.Span),
                LicenseRawBase64 = licenseRaw.IsEmpty ? null : Convert.ToBase64String(licenseRaw.Span),
                LicenseRequestBase64 = licenseRequest.IsEmpty ? null : Convert.ToBase64String(licenseRequest.Span),
            };
            var result = await _process.RequestAsync(IpcMessageTypes.DerivePlayPlayKey, cmd, ParseDeriveResult,
                TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
            if (result.Reason is not null and not AudioKeyFailureReason.None)
                return new PlayPlayDeriveResult(default, result.Reason.Value, result.Detail);

            if (string.IsNullOrEmpty(result.AesKeyHex) || result.AesKeyHex.Length != 32)
                return new PlayPlayDeriveResult(default, AudioKeyFailureReason.RotationDrift, result.Detail);

            return new PlayPlayDeriveResult(
                Convert.FromHexString(result.AesKeyHex),
                AudioKeyFailureReason.None,
                result.Detail,
                DecodeBase64Memory(result.DerivedSlabBase64),
                DecodeBase64Memory(result.NativeCdnSeedBase64));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RegisterFault(ex);
            return await DeriveFallbackAsync(obfuscatedKey, contentId, config, spotifyDllPath, correlationId, playPlayAux, licenseRaw, licenseRequest, ct).ConfigureAwait(false);
        }
    }

    static DerivePlayPlayKeyResult ParseDeriveResult(JsonElement? payload) =>
        payload is null
            ? new DerivePlayPlayKeyResult { CorrelationId = "", Reason = AudioKeyFailureReason.EmulationFault, Detail = "missing derive reply" }
            : payload.Value.Deserialize(AudioIpcJsonContext.Default.DerivePlayPlayKeyResult)
              ?? new DerivePlayPlayKeyResult { CorrelationId = "", Reason = AudioKeyFailureReason.EmulationFault, Detail = "bad derive reply" };

    async Task<PlayPlayDeriveResult> DeriveFallbackAsync(ReadOnlyMemory<byte> obfuscatedKey, ReadOnlyMemory<byte> contentId,
        PlayPlayConfig config, string spotifyDllPath, string correlationId, ReadOnlyMemory<byte> playPlayAux,
        ReadOnlyMemory<byte> licenseRaw, ReadOnlyMemory<byte> licenseRequest, CancellationToken ct)
    {
        var fallback = _fallbackDeriver();
        if (fallback is null)
            return new PlayPlayDeriveResult(default, AudioKeyFailureReason.NeverProvisioned, "audio host unavailable and no in-process fallback is bound");
        return await fallback.DeriveAsync(obfuscatedKey, contentId, config, spotifyDllPath, correlationId,
            playPlayAux, licenseRaw, licenseRequest, ct).ConfigureAwait(false);
    }

    async Task SendLoadFastStartAsync(AudioFastStart start, long generation)
    {
        ConfigureProcess();
        var cmd = new LoadFastStartCommand
        {
            Generation = generation,
            CorrelationId = Guid.NewGuid().ToString("N"),
            TrackUri = start.TrackUri,
            FileIdHex = start.FileIdHex,
            Format = start.Format.ToString(),
            DurationMs = start.DurationMs,
            NormalizationGainDb = start.NormalizationGainDb,
            HeadBytesBase64 = Convert.ToBase64String(start.HeadBytes.Span),
        };
        _log?.Invoke($"audio host send load_fast_start generation={generation} track={start.TrackUri} file={start.FileIdHex} fmt={start.Format} head={start.HeadBytes.Length}B dur={start.DurationMs}ms");
        await _process.RequestAsync(IpcMessageTypes.LoadFastStart, cmd, ParseCommandResult,
            TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
        _log?.Invoke($"audio host ack load_fast_start generation={generation} file={start.FileIdHex}");
    }

    async Task SendSupplyBodyAsync(AudioStreamHandle body, long generation)
    {
        ConfigureProcess();
        var urls = body.CdnUrls ?? (string.IsNullOrEmpty(body.CdnUrl) ? Array.Empty<string>() : [body.CdnUrl]);
        var cmd = new SupplyBodyCommand
        {
            Generation = generation,
            CorrelationId = Guid.NewGuid().ToString("N"),
            TrackUri = body.TrackUri,
            FileIdHex = body.FileIdHex,
            Format = body.Format.ToString(),
            DurationMs = body.DurationMs,
            NormalizationGainDb = body.NormalizationGainDb,
            AesKeyHex = Convert.ToHexStringLower(body.Key.Span),
            NativeCdnSeedBase64 = body.NativeCdnSeed.IsEmpty ? null : Convert.ToBase64String(body.NativeCdnSeed.Span),
            CdnUrls = urls,
            HeadBoundary = body.HeadBoundary,
            SourceKind = (int)body.SourceKind,
        };
        _log?.Invoke($"audio host send supply_body generation={generation} track={body.TrackUri} file={body.FileIdHex} fmt={body.Format} urls={urls.Length} headBoundary={body.HeadBoundary}B key={body.Key.Length}B nativeSeed={body.NativeCdnSeed.Length}B");
        await _process.RequestAsync(IpcMessageTypes.SupplyBody, cmd, ParseCommandResult,
            TimeSpan.FromSeconds(60), CancellationToken.None).ConfigureAwait(false);
        _log?.Invoke($"audio host ack supply_body generation={generation} file={body.FileIdHex}");
    }

    static CommandResultMessage ParseCommandResult(JsonElement? payload)
    {
        var result = payload?.Deserialize(AudioIpcJsonContext.Default.CommandResultMessage);
        if (result is null) throw new InvalidOperationException("bad command reply");
        if (!result.Ok) throw new InvalidOperationException(result.Detail ?? result.Reason?.ToString() ?? "audio command failed");
        return result;
    }

    async Task RunRemoteAsync(Func<Task> remote, Action fallback)
    {
        try
        {
            ConfigureProcess();
            await remote().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RegisterFault(ex);
            UseFallback();
            fallback();
        }
    }

    void ConfigureProcess() => _process.Configure(_asset, _equalizer, _crossfade, _volume);

    long NextGeneration(string fileIdHex)
    {
        lock (_gate)
        {
            _activeFileIdHex = fileIdHex;
            _playing = false;
            _buffering = true;
            _prebuffering = false;
            _generation++;
            return _generation;
        }
    }

    void OnNotification(string type, JsonElement? payload)
    {
        try
        {
            switch (type)
            {
                case IpcMessageTypes.StateUpdate:
                    var st = payload?.Deserialize(AudioIpcJsonContext.Default.HostStateUpdate);
                    if (st is null || st.Generation != Interlocked.Read(ref _generation)) return;
                    _usingFallback = false;
                    var wasPlaying = _playing;
                    var wasBuffering = _buffering || _prebuffering;
                    _playing = st.IsPlaying;
                    _buffering = st.IsBuffering;
                    _prebuffering = st.IsPrebuffering;
                    Interlocked.Exchange(ref _positionMs, st.PositionMs);
                    var kind = st.IsPrebuffering ? AudioHostSignalKind.Prebuffering
                        : st.IsBuffering ? AudioHostSignalKind.Buffering
                        : st.IsPlaying ? (wasPlaying && !wasBuffering ? AudioHostSignalKind.PositionTick : AudioHostSignalKind.Playing)
                        : AudioHostSignalKind.Paused;
                    _signals.OnNext(new AudioHostSignal(kind, st.PositionMs));
                    break;
                case IpcMessageTypes.TrackFinished:
                    var fin = payload?.Deserialize(AudioIpcJsonContext.Default.TrackFinishedMessage);
                    if (fin is not null && fin.Generation != Interlocked.Read(ref _generation)) return;
                    _playing = false;
                    _wantPlaying = false;
                    _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Ended, PositionMs));
                    break;
                case IpcMessageTypes.Diagnostic:
                case IpcMessageTypes.EqualizerApplied:
                case IpcMessageTypes.CrossfadeMissed:
                    var diag = payload?.Deserialize(AudioIpcJsonContext.Default.DiagnosticMessage);
                    if (diag is not null) _log?.Invoke("audio host " + type + ": " + diag.Detail);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("audio host notification parse failed: " + ex.Message);
        }
    }

    void OnFallbackSignal(AudioHostSignal signal)
    {
        if (!_usingFallback) return;
        Interlocked.Exchange(ref _positionMs, signal.PositionMs);
        _playing = signal.Kind is AudioHostSignalKind.Playing or AudioHostSignalKind.PositionTick;
        _buffering = signal.Kind == AudioHostSignalKind.Buffering;
        _prebuffering = signal.Kind == AudioHostSignalKind.Prebuffering;
        _signals.OnNext(signal);
    }

    void OnFaulted(Exception ex)
    {
        if (_disposed) return;
        RegisterFault(ex);
        if (RemoteAvailable && _wantPlaying)
            _ = RestartRemoteAsync(ex);
    }

    async Task RestartRemoteAsync(Exception reason, bool planned = false)
    {
        try
        {
            await _process.RecycleAsync(null, reason, notifyFault: !planned).ConfigureAwait(false);
            if (!RemoteAvailable) return;
            await ReplayRemoteStateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RegisterFault(ex);
            SwitchToFallback();
        }
    }

    async Task ReplayRemoteStateAsync()
    {
        AudioFastStart? start;
        AudioStreamHandle? body;
        bool wantPlaying;
        long position;
        long generation;
        lock (_gate)
        {
            start = _lastStart;
            body = _lastBody;
            wantPlaying = _wantPlaying;
            position = _positionMs;
            generation = _generation;
        }

        if (start is { } s) await SendLoadFastStartAsync(s, generation).ConfigureAwait(false);
        if (body is { } b) await SendSupplyBodyAsync(b, generation).ConfigureAwait(false);
        if (position > 0)
            await _process.SendAsync(IpcMessageTypes.Seek, new SeekCommand { Generation = generation, PositionMs = position }, CancellationToken.None).ConfigureAwait(false);
        if (wantPlaying)
            await _process.SendAsync(IpcMessageTypes.Play, new EmptyPayload { Generation = generation }, CancellationToken.None).ConfigureAwait(false);
        _usingFallback = false;
    }

    void RegisterFault(Exception ex)
    {
        if (!_preferRemote || _remoteDisabled) return;
        long now = Environment.TickCount64;
        bool breakCircuit = false;
        lock (_faults)
        {
            _faults.Enqueue(now);
            while (_faults.Count > 0 && now - _faults.Peek() > (long)CrashWindow.TotalMilliseconds)
                _faults.Dequeue();
            breakCircuit = _faults.Count >= CrashLimit;
        }

        _log?.Invoke("audio host fault: " + ex);
        if (breakCircuit)
        {
            _remoteDisabled = true;
            _log?.Invoke("audio host circuit breaker tripped; switching to in-process audio");
            if (!_circuitRaised)
            {
                _circuitRaised = true;
                CircuitBroken?.Invoke();
            }
            SwitchToFallback();
        }
    }

    void SwitchToFallback()
    {
        UseFallback();
        AudioFastStart? start;
        AudioStreamHandle? body;
        bool wantPlaying;
        long position;
        lock (_gate)
        {
            start = _lastStart;
            body = _lastBody;
            wantPlaying = _wantPlaying;
            position = _positionMs;
        }

        if (start is { } s) _fallback.LoadFastStart(s);
        if (body is { } b) _fallback.SupplyBody(b);
        _fallback.SetVolume(_volume);
        _fallback.SetEqualizer(_equalizer.Enabled, _equalizer.GainsDb, _equalizer.PreampDb);
        _fallback.SetCrossfade(_crossfade.Enabled, _crossfade.DurationMs);
        if (position > 0) _fallback.Seek(position);
        if (wantPlaying) _fallback.Play();
    }

    void UseFallback() => _usingFallback = true;

    void Heartbeat()
    {
        if (_disposed || !RemoteAvailable || !_process.IsRunning) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _process.RequestAsync(IpcMessageTypes.Ping,
                    new PingMessage { SentUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    p => p?.Deserialize(AudioIpcJsonContext.Default.PongMessage) ?? new PongMessage(),
                    TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RegisterFault(ex);
            }
        });
    }

    static bool SameAsset(RuntimeAsset? a, RuntimeAsset? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return string.Equals(a.PackPath, b.PackPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.PackId, b.PackId, StringComparison.Ordinal)
            && string.Equals(a.Config.Version, b.Config.Version, StringComparison.Ordinal);
    }

    static bool SameEqualizer(EqualizerSettings a, EqualizerSettings b)
    {
        if (a.Enabled != b.Enabled || Math.Abs(a.PreampDb - b.PreampDb) > 0.0001f) return false;
        var ag = a.GainsDb ?? Array.Empty<float>();
        var bg = b.GainsDb ?? Array.Empty<float>();
        for (int i = 0; i < 10; i++)
        {
            var av = i < ag.Length ? ag[i] : 0f;
            var bv = i < bg.Length ? bg[i] : 0f;
            if (Math.Abs(av - bv) > 0.0001f) return false;
        }

        return true;
    }

    static bool SameCrossfade(CrossfadeSettings a, CrossfadeSettings b) =>
        a.Enabled == b.Enabled && a.DurationMs == b.DurationMs;

    static ReadOnlyMemory<byte> DecodeBase64Memory(string? value) =>
        string.IsNullOrEmpty(value) ? default : Convert.FromBase64String(value);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _heartbeat.Dispose();
        _fallbackSub.Dispose();
        _process.Notification -= OnNotification;
        _process.Faulted -= OnFaulted;
        await _process.DisposeAsync().ConfigureAwait(false);
        await _fallback.DisposeAsync().ConfigureAwait(false);
    }

    sealed class SignalObserver(Action<AudioHostSignal> onNext) : IObserver<AudioHostSignal>
    {
        public void OnNext(AudioHostSignal value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
