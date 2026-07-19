using System;
using System.Threading;
using System.Threading.Tasks;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Cross-backend queue preparation (spec §8.4 + the M3 plan requirement). The queue/scheduler's preroll is BACKEND-AGNOSTIC:
// it prepares the next item on whichever backend the router resolves it to. For an audio→audio next that is the concrete
// PreparedSlot voice preroll (the PcmAudioPlayer implements this here). For an audio→video(DRM) next the MF/DRM session is
// spun up + first-frame-readied ahead of the boundary (M5 fills the MF side; the seam + the audio impl + a fake-video test
// live here). The A→B cross-backend transition is a clean declicked HARD CUT — the two engines never co-mix (spec §1).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The context a backend needs to preroll the next item into the ACTIVE session's fixed mix format / normalization
/// (spec §8.4). Passed to <see cref="IPreparableBackend.PrepareAsync"/> so the prepared audio voice arrives already
/// resampled + ReplayGain-resolved and can butt-join / crossfade sample-accurately.</summary>
public readonly record struct PrepareContext(MixFormat Format, NormMode Norm, float ReferenceLufs, int MixRate)
{
    /// <summary>A context for <paramref name="format"/> under <paramref name="norm"/>/<paramref name="referenceLufs"/>.</summary>
    public static PrepareContext For(MixFormat format, NormMode norm, float referenceLufs)
        => new(format, norm, referenceLufs, format.SampleRate);
}

/// <summary>
/// A backend that can PREROLL the next queue item ahead of the join (spec §8.4 / M3 cross-backend). The
/// <see cref="QueuePlaybackCoordinator"/> calls <see cref="PrepareAsync"/> off the block path (worker pool) once the active
/// track fires <c>ending-soon</c>; the audio backend opens the byte-source, resolves/caches the key OUT of the read path,
/// primes the decoder, prefills opening samples and resolves <see cref="GaplessInfo"/>. A video/DRM backend spins up its MF
/// session + first-frame-readies. The two backends never co-mix — a cross-backend join is a declicked hard cut.
/// </summary>
public interface IPreparableBackend
{
    /// <summary>The concrete kind this backend prepares (drives the same-kind crossfade vs cross-backend hard-cut decision).</summary>
    MediaKind Kind { get; }

    /// <summary>Preroll <paramref name="next"/> ahead of the boundary. Runs OFF the RT block path (worker pool); the returned
    /// handle is <see cref="IPreparedItem.IsReady"/> when the join may consume it. Cancellation (a Seek/queue-edit
    /// invalidating the slot) completes the task without corrupting anything.</summary>
    ValueTask<IPreparedItem> PrepareAsync(MediaSource next, PrepareContext ctx, CancellationToken ct);
}

/// <summary>
/// A pre-rolled next item (spec §8.4). For an audio backend it exposes the ready-to-mix voice (<see cref="AudioVoice"/>)
/// plus its trim + loudness; for a video/DRM backend <see cref="AudioVoice"/> is null and the handle carries the spun-up
/// session the coordinator hard-cuts to. Disposal releases the preroll (a Seek/queue-edit that drops the slot disposes it).
/// </summary>
public interface IPreparedItem : IAsyncDisposable
{
    /// <summary>The backend that produced this (its <see cref="IPreparableBackend.Kind"/>).</summary>
    MediaKind Kind { get; }
    /// <summary>True once the preroll has covered worst-case decode+decrypt+seek latency (safe to consume at the join).</summary>
    bool IsReady { get; }
    /// <summary>The prepared audio voice in the active mix format (null for a non-audio backend).</summary>
    IAudioSource? AudioVoice { get; }
    /// <summary>The prepared voice's sample-accurate trim (spec §8.3); default for non-audio.</summary>
    GaplessInfo Gapless { get; }
    /// <summary>The prepared voice's loudness metadata for the per-source ReplayGain scalar (spec §7.7).</summary>
    ReplayGainInfo Loudness { get; }
    /// <summary>The prepared item's total length in mix-domain frames (−1 unknown/streaming).</summary>
    long TotalFrames { get; }
    /// <summary>The prepared item's duration.</summary>
    TimeSpan Duration { get; }
    /// <summary>An opaque backend session/handle for a cross-backend hard-cut hand-off (e.g. the spun-up MF session), or null.</summary>
    object? BackendHandle { get; }
}

/// <summary>The audio-backend prepared item (spec §8.4): a ready voice already decoded/resampled/trimmed into the active mix
/// format, with its loudness for the per-voice ReplayGain scalar. Produced by <see cref="PcmAudioPlayer.PrepareAsync"/>.</summary>
public sealed class AudioPreparedItem : IPreparedItem
{
    /// <summary>Create a ready audio preroll over <paramref name="voice"/>.</summary>
    public AudioPreparedItem(IAudioSource voice, GaplessInfo gapless, ReplayGainInfo loudness, long totalFrames, TimeSpan duration)
    {
        AudioVoice = voice;
        Gapless = gapless;
        Loudness = loudness;
        TotalFrames = totalFrames;
        Duration = duration;
        IsReady = true;
    }

    /// <inheritdoc/>
    public MediaKind Kind => MediaKind.PcmAudio;
    /// <inheritdoc/>
    public bool IsReady { get; }
    /// <inheritdoc/>
    public IAudioSource? AudioVoice { get; }
    /// <inheritdoc/>
    public GaplessInfo Gapless { get; }
    /// <inheritdoc/>
    public ReplayGainInfo Loudness { get; }
    /// <inheritdoc/>
    public long TotalFrames { get; }
    /// <inheritdoc/>
    public TimeSpan Duration { get; }
    /// <inheritdoc/>
    public object? BackendHandle => null;

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        (AudioVoice as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }
}
