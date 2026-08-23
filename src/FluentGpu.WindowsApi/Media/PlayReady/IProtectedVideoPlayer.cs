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
    /// <summary>Seek. <paramref name="mode"/> maps onto the native seek entry point's mode argument:
    /// <see cref="SeekMode.Keyframe"/> ⇒ APPROXIMATE (snap to the nearest keyframe — the fast scrub used while the
    /// scrubber is being dragged), <see cref="SeekMode.Accurate"/> ⇒ EXACT (decode to the requested PTS — the commit
    /// on release).</summary>
    ValueTask SeekAsync(long positionMs, SeekMode mode);
    /// <summary>Request a video representation switch. Implementations apply it at the next segment/keyframe boundary.</summary>
    ValueTask SelectVideoRepresentationAsync(string representationId) => ValueTask.CompletedTask;
    /// <summary>Request a selectable audio/video track switch.</summary>
    ValueTask SelectTrackAsync(int trackId) => ValueTask.CompletedTask;
    /// <summary>The representation currently feeding the decoder, or null on legacy native backends.</summary>
    string? ActiveVideoRepresentationId => null;
    /// <summary>Bytes downloaded since open, for bounded-cadence ABR throughput estimation.</summary>
    long BytesDownloaded => 0;
    /// <summary>Cumulative time spent transferring those bytes, excluding feeder backpressure waits.</summary>
    long DownloadElapsedMs => 0;
    /// <summary>Forward buffered media in milliseconds, or zero when a legacy backend cannot report it.</summary>
    long ForwardBufferedMs => 0;
    /// <summary>Whether the loaded native ABI can apply video-representation changes without reopening.</summary>
    bool SupportsAdaptiveSelection => false;
    /// <summary>Whether the loaded native ABI can apply alternate audio/video track changes without reopening.</summary>
    bool SupportsTrackSelection => false;
    void SetVolume(float volume);
    void SetRate(float rate);
    void Stop();
    /// <summary>Append one bounded-cadence diagnostic line to the backend's own log timeline, so managed decisions
    /// (seek intents, ABR rung changes, watchdog fires) interleave with the native CDM/decode lines in ONE file.
    /// ALWAYS ON — never behind a switch: a field report has to carry the trail without a repro. Never called per
    /// frame; the ABR line is capped at one per second and the rest are one-shot lifecycle events.</summary>
    void LogDiagnostic(string message) { }
    /// <summary>Read one native snapshot and write value-gated surface intents. Called only for a coalesced session
    /// request (not once per host frame).</summary>
    void Pump(in VideoBinding binding);
}
