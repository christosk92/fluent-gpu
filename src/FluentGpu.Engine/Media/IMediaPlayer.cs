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
    /// <summary>The video natural size in px; <c>(0,0)</c> = audio-only.</summary>
    IReadSignal<SizeI> NaturalSize { get; }
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

    // ── video surface binding (for the control; spec §10) ─────────────────────────────────────────────────────────────

    /// <summary>The composited-video child-visual id; <see cref="VideoSurfaceId.IsNone"/> until the first video frame.</summary>
    IReadSignal<VideoSurfaceId> VideoSurface { get; }

    /// <summary>Drive one UI-thread video pump: the routed backend (when it produces a composited surface) translates its
    /// engine state into the player signals, binds the produced DirectComposition handle into <paramref name="binding"/>
    /// (the hole the control draws), and sizes/places the video child at <paramref name="videoRect"/> (DIP) ×
    /// <paramref name="scale"/> (device px). A no-op for audio-only / headless players. The control (<c>MediaPlayerElement</c>)
    /// calls this every frame it is mounted; it is cheap and idempotent once steady-state.</summary>
    void PumpVideo(VideoBinding binding, RectF videoRect, float scale);

    // ── transport: idempotent, coalescing — complete (never throw) on supersession ────────────────────────────────────

    /// <summary>Resume the current source.</summary>
    ValueTask PlayAsync();
    /// <summary>Pause.</summary>
    ValueTask PauseAsync();
    /// <summary>Stop (idempotent; → Idle, releases decode residency).</summary>
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

    // ── source + queue + preroll ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Open a source (the general form; multiple concurrent voices, not a single SetSource).</summary>
    ValueTask OpenAsync(MediaSource source, CancellationToken ct = default);
    /// <summary>Enqueue a source to play after the current one.</summary>
    void Enqueue(MediaSource next);
    /// <summary>Explicitly preroll the next source (spec §8.4).</summary>
    PrepareToken PrepareNext(MediaSource next);
}
