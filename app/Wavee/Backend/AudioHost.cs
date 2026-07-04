using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

// ── The AUDIO-HOST seam (the deferral boundary) ──────────────────────────────────────────────────────────────────────
// Everything UP TO this seam is in scope: Connect control plane, state projection, track resolution, audio-key fetch,
// storage-resolve. The seam receives a fully-resolved AudioStreamHandle (CDN + key + format) and reports a coalesced
// position clock + Ended. DEFERRED behind it (a separate design): AES-128-CTR decrypt → PCM, Ogg/Vorbis decode, mixer/DSP,
// WASAPI output, and the PlayPlay/x86_64 native key-derivation fallback. The default impl in this scope is SilentAudioHost.

public enum AudioFormat { OggVorbis96, OggVorbis160, OggVorbis320, Flac, Flac24 }

/// <summary>Pure POD crossing the seam. An EMPTY <see cref="Key"/> means the host must derive it (PlayPlay path).</summary>
public readonly record struct AudioStreamHandle(
    string TrackUri, string FileIdHex, string CdnUrl,
    ReadOnlyMemory<byte> Key, AudioFormat Format, long DurationMs, float NormalizationGainDb,
    string[]? CdnUrls = null, int HeadBoundary = 0);

/// <summary>Instant-start payload: clear head bytes cross the seam before the key exists.</summary>
public readonly record struct AudioFastStart(
    string TrackUri, string FileIdHex, AudioFormat Format, long DurationMs, float NormalizationGainDb,
    ReadOnlyMemory<byte> HeadBytes);

/// <summary>The output of a fast-first resolve: the clear head is ready NOW (play immediately); the encrypted body
/// (key + CDN) is still resolving in <see cref="Body"/> and is supplied to the host when it lands.</summary>
public readonly record struct FastStartPlan(AudioFastStart Start, System.Threading.Tasks.Task<AudioStreamHandle> Body);

/// <summary>Fast-first resolver seam: return the head (instant-start) while the key + CDN resolve in parallel. When set on
/// the controller it supersedes the plain <see cref="ITrackResolver"/> path for local play. Implemented live by
/// FastTrackPlayback (SpotifyLive); the controller stays portable via this Backend interface.</summary>
public interface IFastTrackResolver
{
    System.Threading.Tasks.Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default);
}

public enum AudioHostSignalKind { PositionTick, Ended, Buffering, Prebuffering, Playing, Paused, Error }

/// <summary>The boxing-free, coalesced report channel from the host. <see cref="AudioHostSignalKind.PositionTick"/> (~1 Hz
/// while playing) is the Driven wake; <see cref="AudioHostSignalKind.Ended"/> drives auto-advance.</summary>
public readonly record struct AudioHostSignal(AudioHostSignalKind Kind, long PositionMs, int ErrorCode = 0);

/// <summary>The reshaped audio seam (replaces the old <c>IAudioEngine</c> in Stage E). Takes a resolved handle, not a
/// bare Track — resolution lives in front of the seam (the controller), in scope.</summary>
public interface IAudioHost : IAsyncDisposable
{
    void Load(in AudioStreamHandle stream);
    void LoadFastStart(in AudioFastStart start);
    void SupplyBody(in AudioStreamHandle body);
    void Play();
    void Pause();
    void Stop();
    void Seek(long positionMs);
    void SetVolume(double volume01);                  // realtime, host-side (buffered-PCM-independent)
    long PositionMs { get; }
    bool IsPlaying { get; }
    bool IsBuffering { get; }
    IObservable<AudioHostSignal> Signals { get; }     // the clock + Ended report
}

/// <summary>The default in-scope host: a SILENT renderer that reports synthetic position/Ended with zero decrypt/decode/
/// output, so the whole control-plane → resolve → host → projection → UI pipeline runs and is testable headlessly today.
/// Position uses the same wall-clock anchor math the UI seekbar uses (AnchorPos + (now − AnchorWall)). Ticks fire only
/// while playing, honouring the engine's zero-frames-when-paused guardrail.</summary>
public sealed class SilentAudioHost : IAudioHost
{
    readonly Func<long> _now;
    readonly SimpleSubject<AudioHostSignal> _signals = new();
    readonly object _gate = new();
    long _anchorWall, _anchorPos, _durationMs;
    bool _playing, _buffering;
    Timer? _ticker;

    public SilentAudioHost(Func<long>? clock = null) => _now = clock ?? (() => Environment.TickCount64);

    public long PositionMs { get { lock (_gate) return Pos(); } }
    public bool IsPlaying { get { lock (_gate) return _playing; } }
    public bool IsBuffering { get { lock (_gate) return _buffering; } }
    public IObservable<AudioHostSignal> Signals => _signals;

    long Pos() => _playing ? Math.Min(_durationMs <= 0 ? long.MaxValue : _durationMs, _anchorPos + (_now() - _anchorWall)) : _anchorPos;

    public void Load(in AudioStreamHandle s)
    {
        lock (_gate) { _anchorPos = 0; _anchorWall = _now(); _durationMs = s.DurationMs; _playing = false; _buffering = true; }
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, 0));
        lock (_gate) _buffering = false;
    }

    public void LoadFastStart(in AudioFastStart s)
    {
        lock (_gate) { _anchorPos = 0; _anchorWall = _now(); _durationMs = s.DurationMs; _playing = false; _buffering = false; }
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Prebuffering, 0));
    }

    public void SupplyBody(in AudioStreamHandle body)
    {
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Buffering, 0));
        lock (_gate) _buffering = false;
    }

    public void Play()
    {
        lock (_gate) { _anchorWall = _now(); _playing = true; }
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Playing, PositionMs));
        StartTicker();
    }

    public void Pause()
    {
        lock (_gate) { _anchorPos = Pos(); _playing = false; }
        StopTicker();
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Paused, PositionMs));
    }

    public void Stop()
    {
        lock (_gate) { _anchorPos = 0; _playing = false; }
        StopTicker();
    }

    public void Seek(long ms)
    {
        lock (_gate) { _anchorPos = _durationMs > 0 ? Math.Clamp(ms, 0, _durationMs) : Math.Max(0, ms); _anchorWall = _now(); }
        _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.PositionTick, PositionMs));
    }

    public void SetVolume(double volume01) { /* silent host: real host-side volume lands with the real host */ }

    void StartTicker() { StopTicker(); _ticker = new Timer(_ => Tick(), null, 1000, 1000); }
    void StopTicker() { _ticker?.Dispose(); _ticker = null; }

    void Tick()
    {
        long pos; bool playing, ended;
        lock (_gate) { pos = Pos(); playing = _playing; ended = _durationMs > 0 && pos >= _durationMs; }
        if (!playing) return;
        if (ended)
        {
            lock (_gate) { _playing = false; _anchorPos = _durationMs; }
            StopTicker();
            _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Ended, pos));
        }
        else _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.PositionTick, pos));
    }

    public ValueTask DisposeAsync() { StopTicker(); return ValueTask.CompletedTask; }
}
