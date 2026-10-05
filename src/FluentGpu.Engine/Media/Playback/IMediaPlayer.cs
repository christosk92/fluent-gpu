using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>An explicit preroll token (spec §8.4) returned by <see cref="IMediaPlayer.PrepareNext"/>. Carries the epoch it
/// was issued under so a stale late Prepare (defeated by a Seek/queue-edit bumping the epoch) is unambiguous.</summary>
public readonly record struct PrepareToken(long Id, uint Epoch)
{
    /// <summary>The "nothing prepared" token.</summary>
    public static PrepareToken None => default;
    /// <summary>True when this token references a real prepared slot.</summary>
    public bool IsValid => Id != 0;
}

/// <summary>
/// The ONE headless playback contract both backends implement (spec §4.2). State is exposed as signals (the backend is
/// the SOLE writer; the UI binds read-only); transport is idempotent, coalescing <see cref="ValueTask"/>s that COMPLETE
/// (never throw) on supersession. Usable with zero UI attached (harness-drivable, macOS-portable).
/// </summary>
public interface IMediaPlayer : IAsyncDisposable
{
    // ── reactive state (all IReadSignal<> — backend-written, UI binds read-only) ──────────────────────────────────────

    /// <summary>The exhaustive playback state.</summary>
    IReadSignal<PlaybackState> State { get; }
    /// <summary>Intent: the user asked to play.</summary>
    IReadSignal<bool> IsPlayRequested { get; }
    /// <summary>Why-not: why playback is suppressed despite the intent.</summary>
    IReadSignal<SuppressionReason> Suppression { get; }
    /// <summary>DERIVED: <c>State==Playing &amp;&amp; Suppression==None</c>.</summary>
    IReadSignal<bool> IsPlaying { get; }
    /// <summary>DERIVED: opening / initial-buffering / stalled.</summary>
    IReadSignal<bool> IsBuffering { get; }
    /// <summary>Hot path: the clock-sampled position in seconds, alloc-free, node-bindable straight to a seekbar.</summary>
    FloatSignal PositionSeconds { get; }
    /// <summary>A <see cref="TimeSpan"/> view of the same position value.</summary>
    IReadSignal<TimeSpan> Position { get; }
    /// <summary>The media duration; <see cref="TimeSpan.MinValue"/> == unknown (live/streaming).</summary>
    IReadSignal<TimeSpan> Duration { get; }
    /// <summary>Buffer health (ranges in time + forward seconds + stall policy).</summary>
    IReadSignal<BufferHealth> Buffer { get; }
    /// <summary>Detailed buffering reason/progress for professional chrome and telemetry.</summary>
    IReadSignal<BufferingInfo> Buffering { get; }
    /// <summary>VOD/live/DVR window and chapters.</summary>
    IReadSignal<TimelineInfo> Timeline { get; }
    /// <summary>The video natural size in px; <c>(0,0)</c> = audio-only.</summary>
    IReadSignal<SizeI> NaturalSize { get; }
    /// <summary>Display geometry including clean aperture, sample aspect ratio, and rotation.</summary>
    IReadSignal<VideoGeometry> VideoGeometry { get; }
    /// <summary>Colorimetry/HDR metadata.</summary>
    IReadSignal<VideoColorInfo> VideoColor { get; }
    /// <summary>The composited PLACEMENT geometry the backend last realized — the decoded size, the size it is
    /// rendered at inside the backend's own swap chain, and the rect the compositor visual was placed at. This is
    /// what a host reads (and logs) to answer "why is there a black bar" without guessing from pixels.</summary>
    IReadSignal<VideoSurfaceGeometry> SurfaceGeometry { get; }
    /// <summary>Bounded-cadence playback diagnostics.</summary>
    IReadSignal<PlaybackStatistics> Statistics { get; }
    /// <summary>The selected subtitle/caption cue at the authoritative media position.</summary>
    IReadSignal<TimedCue?> ActiveCue { get; }
    /// <summary>The single typed error (null = no error).</summary>
    IReadSignal<MediaError?> Error { get; }

    /// <summary>Read-WRITE master volume 0..1 (smoothed).</summary>
    FloatSignal Volume { get; }
    /// <summary>Whether muted.</summary>
    IReadSignal<bool> Muted { get; }
    /// <summary>Read-WRITE playback rate; pitch-preserved by default.</summary>
    FloatSignal Rate { get; }

    // ── tracks, queue, effects, now-playing, capabilities ─────────────────────────────────────────────────────────────

    /// <summary>The observable track collections (spec §6).</summary>
    TrackSet Tracks { get; }
    /// <summary>The first-class play queue (spec §8).</summary>
    PlayQueue Queue { get; }
    /// <summary>EQ/crossfade/normalization (spec §7); the MF video backend returns an inert null-object.</summary>
    IAudioEffects Effects { get; }
    /// <summary>SMTC-shaped now-playing (spec §10), opt-in.</summary>
    NowPlaying NowPlaying { get; }
    /// <summary>The capability bitset (spec §10).</summary>
    MediaCommands Commands { get; }
    /// <summary>Adaptive representations and acknowledged quality choice.</summary>
    QualitySet Qualities { get; }

    // ── video surface binding (for the control; spec §10) ─────────────────────────────────────────────────────────────

    /// <summary>The composited-video child-visual id; <see cref="VideoSurfaceId.IsNone"/> until the first video frame.</summary>
    IReadSignal<VideoSurfaceId> VideoSurface { get; }

    /// <summary>Drive one UI-thread video pump for the presenting control: the routed backend (when it produces a composited
    /// surface) binds the produced DirectComposition handle into <paramref name="binding"/> (the hole the control draws), and
    /// sizes/places the video child at <paramref name="videoRect"/> (DIP) × <paramref name="scale"/> (device px). A no-op for
    /// audio-only / headless players. The control (<c>MediaPlayerElement</c>) calls this for an initial hand-off and
    /// coalesced native/geometry/transport requests; it is not a per-frame repaint path.
    /// <para>A player that owns its control plane (<see cref="MediaPlayer"/>) publishes state, position, duration, natural
    /// size and errors on its own, from its session's pump requests, whether or not any control is mounted, so this call is
    /// the surface half only. A player that does not (the headless scripted player) publishes them from inside this call
    /// as well.</para></summary>
    void PumpVideo(VideoBinding binding, RectF videoRect, float scale);

    // ── transport: idempotent, coalescing — complete (never throw) on supersession ────────────────────────────────────

    /// <summary>Resume the current source.</summary>
    ValueTask PlayAsync();
    /// <summary>Pause.</summary>
    ValueTask PauseAsync();
    /// <summary>Stop (idempotent): cancels an open still in flight, pauses and RELEASES the current session (decode, clock,
    /// network and native handles — its disposal finishes in the background and <c>DisposeAsync</c> awaits it), and goes
    /// <see cref="PlaybackState.Idle"/> with no play intent. Playback needs a new open afterwards; a later
    /// <see cref="PlayAsync"/> with nothing open only records the intent.</summary>
    void Stop();
    /// <summary>Seek to <paramref name="to"/>.</summary>
    ValueTask SeekAsync(TimeSpan to, SeekMode mode = SeekMode.Accurate);
    /// <summary>Step a single frame (+1 / −1).</summary>
    ValueTask StepFrame(int delta);
    /// <summary>Set the playback rate.</summary>
    void SetRate(double rate);
    /// <summary>Set the volume (0..1).</summary>
    void SetVolume(double volume);
    /// <summary>Mute/unmute.</summary>
    void SetMuted(bool muted);
    /// <summary>Select a discovered audio/video/text track; null disables text.</summary>
    ValueTask SelectTrackAsync(MediaTrack? track);
    /// <summary>Enable automatic ABR or pin a representation.</summary>
    ValueTask SelectQualityAsync(QualitySelection selection);
    /// <summary>Update the laid-out video height used to cap automatic ABR. Manual pins intentionally ignore this cap.</summary>
    void SetAdaptiveViewportHeight(int height) { }
    /// <summary>Update the policy/network height cap used by automatic ABR. Zero means unlimited.</summary>
    void SetAdaptiveMaxHeight(int height) { }
    /// <summary>Seek to the current live edge.</summary>
    ValueTask GoLiveAsync();
    /// <summary>Seek to the previous chapter marker.</summary>
    ValueTask PreviousChapterAsync();
    /// <summary>Seek to the next chapter marker.</summary>
    ValueTask NextChapterAsync();

    // ── source + queue + preroll ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Open a source (the general form; multiple concurrent voices, not a single SetSource).</summary>
    ValueTask OpenAsync(MediaSource source, CancellationToken ct = default);
    /// <summary>Open a source with the caller's open options — chiefly WHERE it opens
    /// (<see cref="MediaOpenOptions.StartPosition"/>) and whether it starts paused. A backend that honours the start
    /// position fetches and presents from it directly (the protected path never shows 0:00 first); options the caller
    /// leaves unset (buffering, network, ABR, license relay) fall back to the player's own. An implementation that cannot
    /// honour a start position opens at zero.</summary>
    ValueTask OpenAsync(MediaSource source, MediaOpenOptions options, CancellationToken ct = default) => OpenAsync(source, ct);
    /// <summary>Append a source to <see cref="Queue"/>. It only queues: nothing is opened, prefetched or prepared ahead of
    /// time (a host that wants a warm next item prepares it itself).</summary>
    void Enqueue(MediaSource next);
    /// <summary>Append the next source to <see cref="Queue"/> and return a token naming the queued item (spec §8.4). The
    /// <c>MediaPlayer</c> facade does not preroll it: the token marks the slot, it is not a prepared session.</summary>
    PrepareToken PrepareNext(MediaSource next);
}
