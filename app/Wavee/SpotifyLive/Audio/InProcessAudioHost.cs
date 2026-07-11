using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee.SpotifyLive.Audio;

/// <summary>
/// In-process local audio host. A thin adapter over ONE long-lived <see cref="AudioPlayEngine"/>: the engine decodes the
/// active track plus (when prepared) an incoming track and equal-power mixes them per-sample in its single output loop, so
/// a crossfade is sample-accurate and pause-aware with no second WASAPI stream. The engine originates the Started/
/// Completed/Missed hand-off events; this host just forwards them. Manual loads cancel the prepared path and stay immediate.
/// </summary>
public sealed class InProcessAudioHost : IAudioHost, IAudioDspControl, IAudioOutputDeviceControl, IPreparedAudioHost
{
    const int MaxCrossfadeMs = 12_000;

    readonly SimpleSubject<AudioHostSignal> _signals = new();
    readonly SimpleSubject<AudioTransitionSignal> _transitions = new();
    readonly object _gate = new();
    readonly WaveeLogger _log;
    readonly AudioBodyDiskCache? _bodyDisk;

    Func<IPlayPlayCdnDecryptorFactory?> _decryptors;
    Func<string, byte[], CdnDecryptor?> _nativeDecryptorFactory;
    readonly AudioPlayEngine _active;

    bool _playing;
    bool _buffering;
    double _volume = 1.0;
    bool _muted;
    string? _outputDeviceId;
    EqualizerSettings _equalizer = EqualizerSettings.Flat;
    bool _crossfadeEnabled;
    int _crossfadeMs;
    bool _disposed;

    public event Action<OutputDeviceNotice>? OutputDeviceNotice;
    public event Action<double, bool>? ExternalVolumeChanged;

    public InProcessAudioHost(Func<IPlayPlayCdnDecryptorFactory?> decryptors, WaveeLogger log = default,
        AudioBodyDiskCache? bodyDisk = null)
        : this((_, seed) => decryptors()?.CreateCdnDecryptor(seed), log, bodyDisk)
    {
        _decryptors = decryptors;
    }

    internal InProcessAudioHost(Func<string, byte[], CdnDecryptor?> nativeDecryptorFactory, WaveeLogger log,
        AudioBodyDiskCache? bodyDisk)
    {
        _decryptors = static () => null;
        _nativeDecryptorFactory = nativeDecryptorFactory;
        _log = log;
        _bodyDisk = bodyDisk;
        _active = CreateEngine();
    }

    public void RebindPlayPlay(Func<IPlayPlayCdnDecryptorFactory?> decryptors)
    {
        _decryptors = decryptors;
        _nativeDecryptorFactory = (_, seed) => _decryptors()?.CreateCdnDecryptor(seed);
    }

    public long PositionMs { get { lock (_gate) return _active.PositionMs; } }
    public bool IsPlaying => Volatile.Read(ref _playing);
    public bool IsBuffering => Volatile.Read(ref _buffering);
    public IObservable<AudioHostSignal> Signals => _signals;
    public IObservable<AudioTransitionSignal> Transitions => _transitions;

    AudioPlayEngine CreateEngine()
    {
        var engine = new AudioPlayEngine(_log, (file, seed) => _nativeDecryptorFactory(file, seed), _bodyDisk);
        engine.State += OnEngineState;
        engine.TrackFinished += OnEngineFinished;
        engine.Transition += OnEngineTransition;
        engine.DeviceNotice += OnEngineDeviceNotice;
        engine.SessionVolumeChanged += OnEngineSessionVolume;
        engine.SetOutputDevice(_outputDeviceId);
        engine.SetOutputMuted(_muted);
        engine.SetVolume(_volume);
        engine.SetEqualizer(_equalizer);
        return engine;
    }

    public void Load(in AudioStreamHandle stream)
    {
        LoadFastStart(new AudioFastStart(stream.TrackUri, stream.FileIdHex, stream.Format, stream.DurationMs,
            stream.NormalizationGainDb, default));
        SupplyBody(stream);
    }

    public void LoadFastStart(in AudioFastStart start)
    {
        lock (_gate) _buffering = true;
        _active.LoadFastStart(start);
    }

    public void SupplyBody(in AudioStreamHandle body) => _active.SupplyBody(body);

    public void Play() { lock (_gate) _playing = true; _active.Play(); }
    public void Pause() { lock (_gate) _playing = false; _active.Pause(); }

    public void Stop()
    {
        lock (_gate) { _playing = false; _buffering = false; }
        _active.Stop();
    }

    public void Seek(long positionMs) => _active.Seek(positionMs);

    public void SetVolume(double volume01)
    {
        _volume = Math.Clamp(volume01, 0, 1);
        _active.SetVolume(_volume);
    }

    public void SetOutputDevice(string? deviceId)
    {
        _outputDeviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        _active.SetOutputDevice(_outputDeviceId);
    }

    public void SetOutputMuted(bool muted)
    {
        _muted = muted;
        _active.SetOutputMuted(muted);
    }

    public void SetEqualizer(bool enabled, ReadOnlySpan<float> gainsDb, float preampDb = 0f)
    {
        var gains = new float[10];
        gainsDb[..Math.Min(gainsDb.Length, gains.Length)].CopyTo(gains);
        _equalizer = new EqualizerSettings { Enabled = enabled, GainsDb = gains, PreampDb = preampDb };
        _active.SetEqualizer(_equalizer);
    }

    public void SetCrossfade(bool enabled, int durationMs)
    {
        _crossfadeMs = Math.Clamp(durationMs, 0, MaxCrossfadeMs);
        _crossfadeEnabled = enabled && _crossfadeMs > 0;
    }

    public Task PrepareNextAsync(AudioPrepareRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Token)) throw new ArgumentException("A prepared-next token is required.", nameof(request));
        lock (_gate) ThrowIfDisposed();
        _active.PrepareIncoming(request, EffectiveFadeMs(request));
        return Task.CompletedTask;
    }

    public Task SupplyNextBodyAsync(string token, AudioStreamHandle body, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _active.SupplyIncomingBody(token, body);
        return Task.CompletedTask;
    }

    public Task<AudioPrepareCancelResult> CancelPreparedAsync(string token, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_active.CancelIncoming(token));
    }

    int EffectiveFadeMs(AudioPrepareRequest request) => request.AllowOverlap && _crossfadeEnabled ? _crossfadeMs : 0;

    void OnEngineState(AudioHostSignal signal)
    {
        lock (_gate)
        {
            _playing = signal.Kind is AudioHostSignalKind.Playing or AudioHostSignalKind.PositionTick;
            _buffering = signal.Kind is AudioHostSignalKind.Buffering or AudioHostSignalKind.Prebuffering;
        }
        _signals.OnNext(signal);
    }

    void OnEngineTransition(AudioTransitionSignal signal) => _transitions.OnNext(signal);

    void OnEngineFinished()
    {
        long pos = _active.PositionMs;
        lock (_gate) { _playing = false; _buffering = false; }
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Ended, pos));
    }

    void OnEngineDeviceNotice(OutputDeviceNotice notice) => OutputDeviceNotice?.Invoke(notice);

    void OnEngineSessionVolume(double volume, bool muted) => ExternalVolumeChanged?.Invoke(volume, muted);

    void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InProcessAudioHost));
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { _active.Stop(); } catch { }
        try { _active.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
