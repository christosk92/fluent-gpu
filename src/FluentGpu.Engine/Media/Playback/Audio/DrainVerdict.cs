namespace FluentGpu.Media;

/// <summary>
/// Pure decision (spec §7/§12) shared by <see cref="PcmAudioSession.RenderBlock"/> and the Playing-state Ended check
/// in <see cref="PcmAudioSession"/>'s pump (<c>Advance</c>): whether this tick should render/submit anything at all,
/// and whether the session has genuinely reached the end of content. A mixer with no live voices — and nothing about
/// to arrive — has nothing to mix; rendering it anyway keeps writing zero-filled blocks into a buffered sink that is
/// therefore never empty, so the sink-drained half of "Ended" can never observe true silence on a real device
/// (playback sits at the tail with the clock running forever — see docs/plans wavee "explain why playback is
/// indexed" diagnosis). <paramref name="cmdsPending"/> is the race guard (spec §12): a voice-add command may be
/// enqueued between <c>DrainMixerCmds()</c> and this check, or (on the RT feed path) a gapless/crossfade join may
/// already be scheduled but not yet drained through the render-thread SPSC — either way the mixer must NOT be
/// treated as drained, so a queued arrival is never mistaken for the end.
/// </summary>
public readonly struct DrainVerdict
{
    /// <summary>True when this tick should proceed to mix/render/submit. False means there is nothing to render,
    /// right now — the caller must not render or submit filler (a voiceless mixer with nothing in flight).</summary>
    public bool RenderAllowed { get; }

    /// <summary>True when the session has reached the true end: the mixer is drained, nothing is queued to arrive,
    /// no rendered-but-unsubmitted frames remain, and — for a buffered sink — the device has genuinely finished
    /// draining what was already submitted (never true while the sink still holds unplayed filler).</summary>
    public bool PublishEnded { get; }

    private DrainVerdict(bool renderAllowed, bool publishEnded)
    {
        RenderAllowed = renderAllowed;
        PublishEnded = publishEnded;
    }

    /// <summary>Decide both flags from the current mixer/command/sink state. Pure — no I/O, no allocation; safe to
    /// call from the render thread.</summary>
    /// <param name="mixerDrained">The mixer has no live voices (or every voice is finished) — caller-computed (RenderBlock
    /// reads <see cref="CrossfadeMixer.VoiceCount"/> directly; the RT-fed Advance path reads the RT-published
    /// <see cref="CrossfadeMixer.DrainedPublished"/> flag instead of touching the render-thread-owned voice list).</param>
    /// <param name="cmdsPending">A mixer command (e.g. a voice add) is enqueued but not yet drained/applied.</param>
    /// <param name="pendingFrames">Rendered-but-not-yet-submitted frames still held by the session.</param>
    /// <param name="writableFrames">The buffered sink's current writable frame count (ignored when <paramref name="bufferedSink"/> is false).</param>
    /// <param name="capacityFrames">The buffered sink's total capacity in frames (ignored when <paramref name="bufferedSink"/> is false).</param>
    /// <param name="bufferedSink">True when the output sink implements <c>IBufferedAudioSink</c> (a device with queued,
    /// draining-over-time capacity) — false for a synchronous/headless sink, which can never gate Ended on drain.</param>
    public static DrainVerdict Decide(bool mixerDrained, bool cmdsPending, int pendingFrames, int writableFrames,
        int capacityFrames, bool bufferedSink)
    {
        bool trulyDrained = mixerDrained && !cmdsPending;
        bool sinkEmpty = !bufferedSink || writableFrames >= capacityFrames;
        return new DrainVerdict(
            renderAllowed: !trulyDrained,
            publishEnded: trulyDrained && pendingFrames == 0 && sinkEmpty);
    }
}
