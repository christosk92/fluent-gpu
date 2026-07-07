using System;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee.SpotifyLive.Audio;

/// <summary>In-process local audio host: CDN fetch/decrypt, decode, and WASAPI output without an IPC sidecar.</summary>
public sealed class InProcessAudioHost : IAudioHost, IAudioDspControl
{
    readonly SimpleSubject<AudioHostSignal> _signals = new();
    readonly AudioPlayEngine _engine;
    bool _playing;
    bool _buffering;

    Func<IPlayPlayCdnDecryptorFactory?> _decryptors;

    public InProcessAudioHost(Func<IPlayPlayCdnDecryptorFactory?> decryptors, Action<string>? log = null,
        AudioBodyDiskCache? bodyDisk = null)
    {
        _decryptors = decryptors;
        _engine = new AudioPlayEngine(log ?? (_ => { }), (_, seed) => _decryptors()?.CreateCdnDecryptor(seed), bodyDisk);
        _engine.State += OnState;
        _engine.TrackFinished += OnTrackFinished;
    }

    public void RebindPlayPlay(Func<IPlayPlayCdnDecryptorFactory?> decryptors) => _decryptors = decryptors;

    public long PositionMs => _engine.PositionMs;
    public bool IsPlaying => _playing;
    public bool IsBuffering => _buffering;
    public IObservable<AudioHostSignal> Signals => _signals;

    public void Load(in AudioStreamHandle stream)
    {
        LoadFastStart(new AudioFastStart(stream.TrackUri, stream.FileIdHex, stream.Format, stream.DurationMs, stream.NormalizationGainDb, default));
        SupplyBody(stream);
    }

    public void LoadFastStart(in AudioFastStart start) => _engine.LoadFastStart(start);
    public void SupplyBody(in AudioStreamHandle body) => _engine.SupplyBody(body);
    public void Play() { _playing = true; _engine.Play(); }
    public void Pause() { _playing = false; _engine.Pause(); }
    public void Stop() { _playing = false; _engine.Stop(); }
    public void Seek(long positionMs) => _engine.Seek(positionMs);
    public void SetVolume(double volume01) => _engine.SetVolume(volume01);
    public void SetEqualizer(bool enabled, ReadOnlySpan<float> gainsDb, float preampDb = 0f)
    {
        var gains = new float[10];
        gainsDb[..Math.Min(gainsDb.Length, gains.Length)].CopyTo(gains);
        _engine.SetEqualizer(new EqualizerSettings { Enabled = enabled, GainsDb = gains, PreampDb = preampDb });
    }

    public void SetCrossfade(bool enabled, int durationMs) { }

    void OnState(AudioHostSignal signal)
    {
        _playing = signal.Kind is AudioHostSignalKind.Playing or AudioHostSignalKind.PositionTick;
        _buffering = signal.Kind is AudioHostSignalKind.Buffering or AudioHostSignalKind.Prebuffering;
        _signals.OnNext(signal);
    }

    void OnTrackFinished()
    {
        _playing = false;
        _buffering = false;
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Ended, PositionMs));
    }

    public ValueTask DisposeAsync()
    {
        _engine.State -= OnState;
        _engine.TrackFinished -= OnTrackFinished;
        _engine.Dispose();
        return ValueTask.CompletedTask;
    }
}
