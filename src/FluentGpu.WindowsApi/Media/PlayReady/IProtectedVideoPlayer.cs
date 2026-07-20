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
    void SetVolume(float volume);
    void SetRate(float rate);
    void Stop();
    void Pump(in VideoBinding binding);
}
