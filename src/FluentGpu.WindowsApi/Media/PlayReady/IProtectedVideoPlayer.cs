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
/// <see cref="Pump()"/>, which reads one native snapshot and writes value-gated signals. There is no timer anywhere in
/// this contract: transport verbs return completed tasks because their acknowledgement IS the next event.</para>
/// <para><b>Two halves (F132).</b> <see cref="Pump()"/> is the STATE half and needs no element: it reads the snapshot and
/// publishes state, position, duration, natural size, errors, the seek landing and the DRM phase. <see cref="Bind"/> is the
/// SURFACE half: it hands the swap-chain handle the last <see cref="Pump()"/> saw to a presenting element's binding. A
/// pump with no element therefore never touches a binding, and an element that arrives later binds the handle already
/// known. <see cref="Pump(in VideoBinding)"/> is the two in one call.</para>
/// <para><b>Threading.</b> The signals are written only inside <see cref="Pump()"/> (the UI thread). The transport verbs
/// are callable from any thread and never block.</para>
/// </summary>
public interface IProtectedVideoPlayer : IDisposable
{
    /// <summary>The lifecycle state (written by <see cref="Pump()"/>).</summary>
    IReadSignal<ProtectedVideoState> State { get; }
    /// <summary>The position at the last snapshot, in ms. Extrapolate between pumps from <see cref="PositionQpc"/>.</summary>
    IReadSignal<long> PositionMs { get; }
    /// <summary>The presentation duration in ms (0 until metadata).</summary>
    IReadSignal<long> DurationMs { get; }
    /// <summary>The natural frame size (empty until metadata).</summary>
    IReadSignal<Size2> NaturalSize { get; }
    /// <summary>The terminal error text, or null.</summary>
    IReadSignal<string?> Error { get; }
    /// <summary>The HRESULT behind <see cref="Error"/> (0 = none known), as of the last <see cref="Pump()"/> that published it.</summary>
    int ErrorHr { get; }
    /// <summary>True when <see cref="Error"/> is one a fresh protected runtime cures: the runtime this session lived on was
    /// replaced (bring-up failed, a device removed/reset, a hardware-DRM context reset), or could not be brought up. The owner
    /// surfaces it as retryable and reopens the source rather than reporting a license failure.</summary>
    bool ErrorNeedsRuntimeRebuild { get; }
    /// <summary>Whether a swap-chain handle has been produced for the attached source. Drops the moment the native session
    /// is detached (by <see cref="Stop"/> or by another session's attach), so a stale slot is never kept alive.</summary>
    bool HasSurface { get; }
    /// <summary>True once FIRSTFRAMEREADY of THIS attach has landed; false before it and after <see cref="Stop"/>. Unlike
    /// <see cref="FirstFrameEpoch"/> (which only ever grows) it says whether the swap chain holds this attach's picture
    /// rather than the previous source's last frame, so a surface is shown on <c>HasFirstFrame &amp;&amp; HasSurface</c>.</summary>
    bool HasFirstFrame { get; }

    /// <summary>Raised from ANY thread when native state changed and one coalesced UI-thread pump is due. Never raised
    /// per video frame; position-only samples do not raise it.</summary>
    event Action? PumpRequested;

    /// <summary>Where the switch is (the poster/spinner discriminator), as of the last <see cref="Pump()"/>.</summary>
    ProtectedVideoPhase Phase { get; }
    /// <summary>Bumps once per source the moment FIRSTFRAMEREADY lands (0 until then) — the surface drops its poster on
    /// a change of this value, never on a state guess.</summary>
    long FirstFrameEpoch { get; }
    /// <summary>The <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> value at which <see cref="PositionMs"/> was
    /// sampled natively (0 = unknown). The UI extrapolates <c>pos + Δt·rate</c> while playing.</summary>
    long PositionQpc { get; }
    /// <summary>The <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> (QPC) value at which the engine reported this attach's
    /// FIRSTFRAMEREADY, as the native runtime stamped it (F215), or 0 until it lands. Unlike the moment a UI observer notices
    /// <see cref="FirstFrameEpoch"/> change (which a stage that unmounted the pump can delay by seconds), it is when the frame was
    /// actually ready, so a switch's first-frame time can be computed from it. Default 0: a player with no native clock.</summary>
    long FirstFrameQpc => 0;
    /// <summary>F066: frames the native engine's renderer PRESENTED for the attached source (IMFMediaEngineEx FRAMES_RENDERED, polled
    /// natively while it plays and accumulated across the engine's post-flush resets), as of the last <see cref="Pump()"/>. 0 until the
    /// first poll and for a player with no native engine (the default).</summary>
    long FramesRendered => 0;
    /// <summary>F066: frames the native renderer DROPPED for the attached source (FRAMES_DROPPED, same accumulation as
    /// <see cref="FramesRendered"/>), as of the last <see cref="Pump()"/>. Counts only the renderer's own drops.</summary>
    long FramesDropped => 0;
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
    /// <summary>The representation ON SCREEN: the one whose first sample the decoder has been handed, or null when the
    /// source is not adaptive (or has not switched yet). Follows a switch only when its picture does.</summary>
    string? ActiveVideoRepresentationId { get; }
    /// <summary>The representation the downloader is feeding the buffer from: the last one queued, which runs ahead of
    /// <see cref="ActiveVideoRepresentationId"/> by however much old-representation video was still buffered when the
    /// switch was spliced in. It is what the ABR judges against (Media3 compares with the last QUEUED chunk). Null falls
    /// back to the active one.</summary>
    string? DownloadingVideoRepresentationId { get; }
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
    /// <summary>How <see cref="SelectVideoRepresentationAsync"/> lands a switch: append the new representation after the
    /// last buffered segment, discarding nothing (what every ABR decrease and cap change wants).</summary>
    public const int AppendAtBufferEnd = -1;
    /// <summary>Request a video representation switch. <paramref name="retainMs"/> is how much forward buffer to KEEP
    /// before the new representation takes over: <see cref="AppendAtBufferEnd"/> keeps all of it, 0 lands at the segment
    /// boundary right after the playhead (the old playhead-adjacent splice: a manual pin), and a positive value lands that
    /// many ms ahead of the playhead (an upswitch that should show soon, e.g. entering fullscreen), discarding the old
    /// representation's buffer past it.</summary>
    ValueTask SelectVideoRepresentationAsync(string representationId, int retainMs = AppendAtBufferEnd);
    /// <summary>Set the soundtrack volume (0..1) on the engine's own audio renderer.</summary>
    void SetVolume(float volume);
    /// <summary>Set the playback rate.</summary>
    void SetRate(float rate);
    /// <summary>Size the engine's video stream to what the destination can show (device px); empty restores natural.</summary>
    void SetStreamSize(SizeI size);
    /// <summary>The attributed form of <see cref="SetStreamSize(SizeI)"/>: the same request, tagged with the registry
    /// <paramref name="token"/> of the slot asking and the <paramref name="host"/> ordinal of its window (0 main, 1.. a pop-out) so the
    /// <c>[video] stream.size</c> line names which window and slot wrote it (F235; no native ABI change: the tag only rides the
    /// managed log). The default drops the tag.</summary>
    void SetStreamSize(SizeI size, int token, int host) => SetStreamSize(size);
    /// <summary>The stream size (device px) the native engine has APPLIED, as of the last <see cref="Pump()"/>: the echo of
    /// <see cref="SetStreamSize"/> (or the size the swap chain was created at), empty while none has been applied or the
    /// session is detached. The owner keeps the compositor's content size at the previous value until this equals the size it
    /// asked for, so DirectComposition never scales a buffer still at the old size by the new size's factor.</summary>
    SizeI AppliedStreamSize { get; }
    /// <summary>Keep the protected runtime's hidden output-protection (OPM) window over this source's video, so HDCP and image
    /// constriction are attested against the monitor the picture is really on. <paramref name="hostWindow"/> is the presenting
    /// window's native handle (an HWND: the main window or a pop-out) and the rect the video's client-area rect in device
    /// pixels. Implementations value-gate it (a steady placement costs nothing) and ignore it while detached. The default does
    /// nothing: a player with no native runtime has no window to move.</summary>
    void PlaceOutputProtectionWindow(nuint hostWindow, int left, int top, int right, int bottom) { }
    /// <summary>Detach from the engine. The session, its store and its keyframe table survive for a re-attach.</summary>
    void Stop();
    /// <summary>Append one lifecycle diagnostic line to the ONE <c>[video]</c> timeline (always on; never per frame).</summary>
    void LogDiagnostic(string message);
    /// <summary>The STATE half: read one native snapshot and write value-gated signals (state, phase, position, duration,
    /// natural size, errors, the seek landing, the applied stream size) and remember the swap-chain handle the snapshot
    /// reported (<see cref="HasSurface"/>). Touches no binding and needs no element. UI thread only; called for a coalesced
    /// request, never per host frame.</summary>
    void Pump();
    /// <summary>The SURFACE half: bind the swap-chain handle the last <see cref="Pump()"/> saw through
    /// <paramref name="binding"/>, while the session is attached and <see cref="HasSurface"/> (bound every time: a placement
    /// move targets a NEW registry token that must receive the same handle). Does nothing for an inert binding. UI thread only.</summary>
    void Bind(in VideoBinding binding);
    /// <summary>Both halves in order: <see cref="Pump()"/>, then <see cref="Bind"/>.</summary>
    void Pump(in VideoBinding binding)
    {
        Pump();
        Bind(binding);
    }
}
