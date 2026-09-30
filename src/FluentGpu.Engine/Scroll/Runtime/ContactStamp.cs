namespace FluentGpu.Scroll.Runtime;

/// <summary>
/// The plan-time stamp of a COMPOSITION-TIMED contact event (<see cref="ScrollInputEvent.PresentTimed"/>, a contact on
/// <see cref="FluentGpu.Scroll.Motion.ContactClock.Present"/> — DirectManipulation's touchpad output and the headless
/// stand-in for it) produced on a UI frame whose clock is <c>clock</c>: the present time of the FIRST render turn
/// guaranteed to see it — the NEXT tick's present, <c>PresentQpc + RefreshQpc</c> (the <c>RenderPresentSec</c> law,
/// <c>tick + (1 + maxFrameLatency)·refresh</c>, one refresh on). ONE rule in ONE place: the DirectManipulation producer
/// (<c>Win32DirectManipulation.UpdateFrame</c>) and the headless producer (<c>HeadlessWindow.PumpScroll</c>) both call it.
///
/// <para><b>Why the next tick, not this one.</b> The UI thread writes frame k's sample into <c>PlanSlots</c> within a
/// fraction of a millisecond of the render thread reading them for the SAME compositor tick. Stamped for the tick that
/// reads it (the old <c>now + clamp(PresentQpc − now, 0, refresh)</c> rule, ≈ tick + one refresh, which the render pose
/// at tick + (1 + depth)·refresh is always past), the ring held its newest sample and each frame showed whichever side
/// won the race — sample k when the write landed first, sample k−1 when the read did (2026-09-29 RCA: +2/0 sample steps
/// on ~14 % of fast drag frames, 8 DIP at 1000 DIP/s). Stamped one tick on, render tick k poses at exactly the stamp of
/// sample k−1, so it shows sample k−1 whether or not sample k has been written yet, and between lattice points the ring
/// interpolates two REAL samples: the shown position is a function of time, never of thread order. This is Gecko APZ's
/// "one frame delay between computing the async transform and compositing it" expressed as a timestamp (Flutter's
/// pointer resampler samples at present minus an offset the same way). Latency equals the old rule's typical case — 93 %
/// of frames already showed sample k−1; only the same-tick tail is gone.</para>
///
/// <para>The stamp is a function of the frame's TICK, never of when the UI ran (<c>nowQpc</c> is not read on a paced
/// clock). DirectManipulation's own latency compensation (the frame-info hint <c>CompositionDeltaMs</c> — what DM
/// predicts TO) is a separate knob and unchanged: the hint says what DM computes, the stamp says when that sample is
/// shown. Nothing about <c>ContactRing</c>, <c>ContactClock.Present</c> or the plan author changes — past the newest
/// sample the ring still holds (only when the UI stalls longer than a refresh).</para>
///
/// <para>Without a known refresh (<c>RefreshQpc</c> or <c>PresentQpc</c> ≤ 0 — no clock at all) the stamp is
/// <c>nowQpc</c>: the ring's <c>PlaceContactTime</c> keeps such a stamp monotone behind the newest sample. Pure,
/// allocation-free, frequency-agnostic (QPC ticks in, QPC ticks out).</para>
/// </summary>
public static class ContactStamp
{
    /// <summary>The stamp (QPC ticks, the frame clock's domain) a composition-timed contact event produced on the frame
    /// whose clock is <paramref name="clock"/> carries: <c>PresentQpc + RefreshQpc</c> when the clock knows its refresh
    /// and present, else <paramref name="nowQpc"/> (the producer's own read of the time).</summary>
    public static long ForFrame(in FluentGpu.Pal.FrameClock clock, long nowQpc)
        => clock.RefreshQpc > 0 && clock.PresentQpc > 0 ? clock.PresentQpc + clock.RefreshQpc : nowQpc;
}
