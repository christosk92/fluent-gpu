using System;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Signals;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>Signals-first protected-video player contract implemented by the in-process desktop PlayReady backend
/// (<see cref="DesktopProtectedVideoPlayer"/>).</summary>
public interface IProtectedVideoPlayer : IDisposable
{
    IReadSignal<ProtectedVideoState> State { get; }
    IReadSignal<long> PositionMs { get; }
    IReadSignal<long> DurationMs { get; }
    IReadSignal<Size2> NaturalSize { get; }
    IReadSignal<string?> Error { get; }
    bool HasSurface { get; }

    /// <summary>Begin a protected session for <paramref name="request"/> (source descriptor + the app license relay).
    /// Non-blocking: the native CDM/decode loop runs on a background MTA thread; state surfaces through the signals.</summary>
    void Start(ProtectedVideoRequest request);
    ValueTask PlayAsync();
    ValueTask PauseAsync();
    ValueTask SeekAsync(long positionMs);
    /// <summary>Request a video representation switch. Implementations apply it at the next segment/keyframe boundary.</summary>
    ValueTask SelectVideoRepresentationAsync(string representationId) => ValueTask.CompletedTask;
    /// <summary>Request a selectable audio/video track switch.</summary>
    ValueTask SelectTrackAsync(int trackId) => ValueTask.CompletedTask;
    /// <summary>The representation currently feeding the decoder, or null on legacy native backends.</summary>
    string? ActiveVideoRepresentationId => null;
    /// <summary>Bytes downloaded since open, for bounded-cadence ABR throughput estimation.</summary>
    long BytesDownloaded => 0;
    /// <summary>Forward buffered media in milliseconds, or zero when a legacy backend cannot report it.</summary>
    long ForwardBufferedMs => 0;
    void SetVolume(float volume);
    void SetRate(float rate);
    void Stop();
    /// <summary>Read one native snapshot and write value-gated surface intents. Called only for a coalesced session
    /// request (not once per host frame).</summary>
    void Pump(in VideoBinding binding);
}
