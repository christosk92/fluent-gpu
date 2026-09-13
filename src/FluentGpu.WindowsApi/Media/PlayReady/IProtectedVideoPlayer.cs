using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Signals;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// One protected source, as <see cref="ProtectedMediaSession"/> drives it. Implemented in production by
/// <see cref="ProtectedVideoSession"/> (a handle on the process <see cref="ProtectedVideoRuntime"/>) and in tests by a
/// fake — so the whole session state machine runs with no CDM, no GPU, no license server and no window.
/// <para><b>Events, not polls.</b> Every state change the native runtime observes (metadata, CANPLAY,
/// FIRSTFRAMEREADY, seeking/seeked, playing/paused, the swap-chain handle, errors, license usable) raises
/// <see cref="PumpRequested"/> from whatever thread saw it. The owner then runs ONE coalesced UI-thread
/// <see cref="Pump"/>, which reads one native snapshot and writes value-gated signals. There is no timer anywhere in
/// this contract: transport verbs return completed tasks because their acknowledgement IS the next event.</para>
/// <para><b>Threading.</b> The signals are written only inside <see cref="Pump"/> (the UI thread). The transport verbs
/// are callable from any thread and never block.</para>
/// </summary>
public interface IProtectedVideoPlayer : IDisposable
{
    /// <summary>The lifecycle state (written by <see cref="Pump"/>).</summary>
    IReadSignal<ProtectedVideoState> State { get; }
    /// <summary>The position at the last snapshot, in ms. Extrapolate between pumps from <see cref="PositionQpc"/>.</summary>
    IReadSignal<long> PositionMs { get; }
    /// <summary>The presentation duration in ms (0 until metadata).</summary>
    IReadSignal<long> DurationMs { get; }
    /// <summary>The natural frame size (empty until metadata).</summary>
    IReadSignal<Size2> NaturalSize { get; }
    /// <summary>The terminal error text, or null.</summary>
    IReadSignal<string?> Error { get; }
    /// <summary>Whether a swap-chain handle has been produced for the attached source.</summary>
    bool HasSurface { get; }

    /// <summary>Raised from ANY thread when native state changed and one coalesced UI-thread pump is due. Never raised
    /// per video frame; position-only samples do not raise it.</summary>
    event Action? PumpRequested;

    /// <summary>Where the switch is (the poster/spinner discriminator), as of the last <see cref="Pump"/>.</summary>
    ProtectedVideoPhase Phase { get; }
    /// <summary>Bumps once per source the moment FIRSTFRAMEREADY lands (0 until then) — the surface drops its poster on
    /// a change of this value, never on a state guess.</summary>
    long FirstFrameEpoch { get; }
    /// <summary>The <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> value at which <see cref="PositionMs"/> was
    /// sampled natively (0 = unknown). The UI extrapolates <c>pos + Δt·rate</c> while playing.</summary>
    long PositionQpc { get; }
    /// <summary>True while a seek is in flight (a JOINING state: the previous frame stays on screen).</summary>
    bool IsSeeking { get; }
    /// <summary>The last seek's landed position in ms, or -1 while none has landed since the last seek was issued.</summary>
    long LastSeekLandedMs { get; }
    /// <summary>Forward buffered media in ms.</summary>
    long ForwardBufferedMs { get; }
    /// <summary>Media retained behind the playhead in ms (a backward seek inside it never fetches).</summary>
    long RetainedBehindMs { get; }
    /// <summary>Bumps whenever the buffered ranges or the keyframe table grew — refill the caller's fixed index buffers
    /// with <see cref="GetBuffered"/> / <see cref="GetKeyframes"/> on a change, never per frame.</summary>
    int IndexEpoch { get; }
    /// <summary>Bytes downloaded since the session was created (for bounded-cadence ABR throughput estimation).</summary>
    long BytesDownloaded { get; }
    /// <summary>Cumulative time spent transferring those bytes, excluding idle gaps between fetches.</summary>
    long DownloadElapsedMs { get; }
    /// <summary>The representation currently feeding the decoder, or null when the source is not adaptive.</summary>
    string? ActiveVideoRepresentationId { get; }
    /// <summary>Whether representation changes apply without reopening (true whenever a catalog exists).</summary>
    bool SupportsAdaptiveSelection { get; }

    /// <summary>Fetch the init segments and <paramref name="segments"/> media segments around the request's start
    /// position — video and audio in parallel — with NO engine call. Completes when media landed (or on cancel).
    /// The license should already be in flight (<see cref="ProtectedVideoRuntime.EnsureLicense"/>).</summary>
    Task PrefetchAsync(int segments, CancellationToken ct);
    /// <summary>THE SWITCH: attach this source to the warm engine (one <c>SetSource</c>) at the request's start position.
    /// Non-blocking; metadata → CANPLAY → first frame arrive as events. Calling it again after <see cref="Stop"/>
    /// re-attaches without refetching.</summary>
    void Start(ProtectedVideoRequest request);
    /// <summary>Play. Completes at once — <see cref="ProtectedVideoState.Playing"/> arriving is the acknowledgement.</summary>
    ValueTask PlayAsync();
    /// <summary>Pause. Completes at once — <see cref="ProtectedVideoState.Paused"/> arriving is the acknowledgement.</summary>
    ValueTask PauseAsync();
    /// <summary>Seek with the native side searching its own keyframe table. <see cref="SeekMode.Keyframe"/> ⇒ present
    /// the keyframe at or before the target (a scrub preview); <see cref="SeekMode.Accurate"/> ⇒ decode to the exact PTS
    /// (the commit). Completes at once; Seeking/Seeked arrive as events. Latest-wins.</summary>
    ValueTask SeekAsync(long positionMs, SeekMode mode);
    /// <summary>Seek with the planner's keyframe answer (<paramref name="keyframeMs"/>, or -1 for "native decides"), so
    /// the native side does not repeat the search.</summary>
    ValueTask SeekAsync(long positionMs, SeekMode mode, long keyframeMs);
    /// <summary>Copy the keyframe table (ascending ms) into <paramref name="into"/>; returns the TOTAL count, which may
    /// exceed the span (re-ask with a larger buffer). Never allocates.</summary>
    int GetKeyframes(Span<long> into);
    /// <summary>Copy the buffered ranges as ascending (start, end) ms pairs into <paramref name="pairs"/> (2 longs per
    /// pair); returns the TOTAL number of pairs. Never allocates.</summary>
    int GetBuffered(Span<long> pairs);
    /// <summary>Request a video representation switch, applied at the next segment boundary.</summary>
    ValueTask SelectVideoRepresentationAsync(string representationId);
    /// <summary>Set the soundtrack volume (0..1) on the engine's own audio renderer.</summary>
    void SetVolume(float volume);
    /// <summary>Set the playback rate.</summary>
    void SetRate(float rate);
    /// <summary>Size the engine's video stream to what the destination can show (device px); empty restores natural.</summary>
    void SetStreamSize(SizeI size);
    /// <summary>Detach from the engine. The session, its store and its keyframe table survive for a re-attach.</summary>
    void Stop();
    /// <summary>Append one lifecycle diagnostic line to the ONE <c>[video]</c> timeline (always on; never per frame).</summary>
    void LogDiagnostic(string message);
    /// <summary>Read one native snapshot, write value-gated signals, and bind the swap-chain handle through
    /// <paramref name="binding"/> (bound every pump: a placement move targets a NEW registry token that must receive
    /// the same handle). UI thread only; called for a coalesced request, never per host frame.</summary>
    void Pump(in VideoBinding binding);
}
