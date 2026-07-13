using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee.Backend;

// ── The AUDIO-HOST seam (the deferral boundary) ──────────────────────────────────────────────────────────────────────
// Everything UP TO this seam is in scope: Connect control plane, state projection, track resolution, audio-key fetch,
// storage-resolve. The seam receives a fully-resolved AudioStreamHandle (CDN + key + format) and reports a coalesced
// position clock + Ended. Implementations handle AES/native CDN decrypt, PCM decode, mixer/DSP, WASAPI output, and
// optional PlayPlay key derivation. The default impl in this scope is SilentAudioHost.

public enum AudioFormat { OggVorbis96, OggVorbis160, OggVorbis320, Flac, Flac24, Mp3 }

/// <summary>How the host fetches/decrypts a body: the Spotify encrypted CDN path (AES-CTR / native PlayPlay) vs an
/// external plain-HTTP source (RSS/podcast, no decrypt). Explicit so we never overload an empty <c>Key</c> as a
/// discriminator (empty Key still means "derive the PlayPlay key" on the Spotify path).</summary>
public enum AudioSourceKind { SpotifyEncrypted = 0, ExternalPlain = 1 }

/// <summary>The user-facing streaming-quality preference (persisted as <c>playback.quality</c>) — the Spotify tier
/// ladder. The resolver aims at the chosen rung and falls back to the nearest available file (lower first), never to
/// silence. <see cref="Lossless"/> is reserved: the picker shows it disabled ("Coming soon") and nothing selects it yet.</summary>
public enum AudioQualityPreference { Normal96 = 0, High160 = 1, VeryHigh320 = 2, Lossless = 3 }

/// <summary>Pure POD crossing the seam. An EMPTY <see cref="Key"/> means the host must derive it (PlayPlay path).</summary>
public readonly record struct AudioStreamHandle(
    string TrackUri, string FileIdHex, string CdnUrl,
    ReadOnlyMemory<byte> Key, AudioFormat Format, long DurationMs, float NormalizationGainDb,
    string[]? CdnUrls = null, int HeadBoundary = 0, ReadOnlyMemory<byte> NativeCdnSeed = default,
    AudioSourceKind SourceKind = AudioSourceKind.SpotifyEncrypted);

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

public interface IFastTrackWarmer
{
    void Warm(Track track, string reason = "");
}

public enum AudioHostSignalKind { PositionTick, Ended, Buffering, Prebuffering, Playing, Paused, Recovering, Error }

/// <summary>The boxing-free, coalesced report channel from the host. State flags are explicit so play intent can remain
/// true while buffering, and a network-recovery state can coexist with either audible queued audio or a drained output.
/// The two-argument constructor preserves the old inferred-state call sites used by simple hosts and tests.</summary>
public readonly record struct AudioHostSignal
{
    public AudioHostSignalKind Kind { get; init; }
    public long PositionMs { get; init; }
    public bool IsPlaying { get; init; }
    public bool IsBuffering { get; init; }
    public bool IsPrebuffering { get; init; }
    public PlaybackRecoveryKind RecoveryKind { get; init; }
    public AudioKeyFailureReason FailureReason { get; init; }
    public string? Detail { get; init; }

    public AudioHostSignal(AudioHostSignalKind kind, long positionMs)
    {
        Kind = kind;
        PositionMs = positionMs;
        IsPlaying = kind is AudioHostSignalKind.Playing or AudioHostSignalKind.PositionTick or AudioHostSignalKind.Recovering;
        IsBuffering = kind == AudioHostSignalKind.Buffering;
        IsPrebuffering = kind == AudioHostSignalKind.Prebuffering;
        RecoveryKind = kind == AudioHostSignalKind.Recovering ? PlaybackRecoveryKind.Network : PlaybackRecoveryKind.None;
        FailureReason = AudioKeyFailureReason.None;
        Detail = null;
    }

    public AudioHostSignal(AudioHostSignalKind kind, long positionMs, bool isPlaying, bool isBuffering,
        bool isPrebuffering, PlaybackRecoveryKind recoveryKind = PlaybackRecoveryKind.None,
        AudioKeyFailureReason failureReason = AudioKeyFailureReason.None, string? detail = null)
    {
        Kind = kind;
        PositionMs = positionMs;
        IsPlaying = isPlaying;
        IsBuffering = isBuffering;
        IsPrebuffering = isPrebuffering;
        RecoveryKind = recoveryKind;
        FailureReason = failureReason;
        Detail = detail;
    }

    public static AudioHostSignal Fault(long positionMs, AudioKeyFailureReason reason, string? detail = null) =>
        new(AudioHostSignalKind.Error, positionMs, false, false, false, PlaybackRecoveryKind.None, reason, detail);
}

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

public interface IAudioDspControl
{
    void SetEqualizer(bool enabled, ReadOnlySpan<float> gainsDb, float preampDb = 0f);
    void SetCrossfade(bool enabled, int durationMs);
}

/// <summary>A stable, controller-minted description of the exact session item prepared after the active track.</summary>
public readonly record struct AudioPrepareRequest(
    string Token,
    AudioFastStart Start,
    bool AllowOverlap);

public enum AudioTransitionKind { Started, Completed, Missed }

/// <summary>Host-to-controller hand-off notification. Tokens make stale async resolves harmless after queue edits.</summary>
public readonly record struct AudioTransitionSignal(
    AudioTransitionKind Kind,
    string Token,
    string TrackUri,
    long PositionMs,
    int EffectiveFadeMs = 0,
    string? Reason = null);

public enum AudioPrepareCancelResult { Cancelled, AlreadyStarted, NotFound }

/// <summary>
/// Optional prepared-next capability. Manual next/row-click continues to use the active load API and stays immediate;
/// this seam is consumed only for a natural-end hand-off.
/// </summary>
public interface IPreparedAudioHost
{
    Task PrepareNextAsync(AudioPrepareRequest request, CancellationToken ct = default);
    Task SupplyNextBodyAsync(string token, AudioStreamHandle body, CancellationToken ct = default);
    Task<AudioPrepareCancelResult> CancelPreparedAsync(string token, CancellationToken ct = default);
    IObservable<AudioTransitionSignal> Transitions { get; }
}

// ── Output-device control (Phase A/B) — an OPTIONAL host capability discovered by interface (the IAudioDspControl
//    precedent): implemented by both real hosts, NOT by SilentAudioHost. Keeps the core IAudioHost seam untouched. ──────
public enum OutputDeviceNoticeKind { DeviceLost, SwitchedToDefault, DeviceRestored, OutputFailed }

/// <summary>A user-facing device event (toast). <see cref="DeviceName"/> is best-effort (the device may be gone).</summary>
public readonly record struct OutputDeviceNotice(OutputDeviceNoticeKind Kind, string DeviceId, string DeviceName, bool WasExplicit);

/// <summary>Optional host capability: choose the WASAPI output endpoint + reflect Windows session volume/mute. The audio
/// stack routes/persists/toasts through this; hosts without it (SilentAudioHost / fake backends) simply don't expose it,
/// and the UI hides the affordances.</summary>
public interface IAudioOutputDeviceControl
{
    void SetOutputDevice(string? deviceId);              // null or empty = system default
    void SetOutputMuted(bool muted);                     // Phase B
    event Action<OutputDeviceNotice>? OutputDeviceNotice;
    event Action<double, bool>? ExternalVolumeChanged;   // Phase B (slider01, muted)
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
