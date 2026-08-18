using System.Diagnostics;

namespace FluentGpu.Hosting;

/// <summary>
/// scroll-v3 (docs/plans/scroll-v3-plan-2026-08-17.md §3.3 item 6 / §4): the UI-thread motion-slice budget that
/// bounds realize + image-apply work to a small window of the frame while a viewport is actively Dragging or
/// coasting Ballistic, so a fling never queues a full realize pass or a burst of decode applies behind it. Owed
/// work carries its own dirty bit forward instead (<c>VirtualRangeDirty</c> for realize, a still-queued decode for
/// image apply) — a later frame picks it back up. Every steady frame (no viewport Drag/Ballistic) is disarmed, so
/// the budgeted call sites (<c>TreeReconciler.ReRealizeVirtuals(long)</c>, <c>DecodeScheduler.Pump(...,long)</c>)
/// never even read the clock — this is not a general-purpose scheduler, just the one seam the plan needed.
/// </summary>
public sealed class FrameBudget
{
    /// <summary>The UI-thread slice (ms) realize + image-apply may spend while a viewport is Drag/Ballistic. One
    /// const, no env knob — a host-loop pacing budget, not a physics feel constant (those live in
    /// <c>FluentGpu.Scroll.ScrollFeel</c>).</summary>
    public const float MotionUiSliceMs = 3f;

    private long _deadlineTicks = long.MaxValue;

    /// <summary>The Stopwatch-tick deadline the budgeted call sites must stop draining past.
    /// <see cref="long.MaxValue"/> (i.e. unbounded) whenever the budget is disarmed — every steady frame.</summary>
    public long DeadlineTicks => _deadlineTicks;

    /// <summary>Arm the budget for this frame: the deadline is <paramref name="nowTicks"/> + <see cref="MotionUiSliceMs"/>.</summary>
    public void Arm(long nowTicks) => _deadlineTicks = nowTicks + (long)(MotionUiSliceMs * Stopwatch.Frequency / 1000.0);

    /// <summary>Disarm — steady frames pass <see cref="long.MaxValue"/> (unbounded) to the budgeted call sites.</summary>
    public void Disarm() => _deadlineTicks = long.MaxValue;
}
