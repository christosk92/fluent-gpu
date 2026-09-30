using System;

namespace FluentGpu.Scroll.Diag;

/// <summary>What moved the content of a viewport that was at rest (<see cref="ScrollJumpRules"/>).</summary>
public enum ScrollJumpCause : byte
{
    None = 0,
    /// <summary>Content above the anchor row changed size and the offset did not follow (an UNANCHORED extent change).</summary>
    Extent = 1,
    /// <summary>A plan frame shift was taken (<c>PlanSlots.Shift</c> — a measured correction or <c>ShiftFrame</c>) but the
    /// shown offset did not end up where the shift put the anchor row: the shift was lost, doubled or overwritten.</summary>
    ShiftFrame = 2,
    /// <summary>The shown offset was bounded away from the plan (the content extent / coverage clamp moved it).</summary>
    Coverage = 3,
    /// <summary>The plan was re-written (a new plan, not user input and not a programmatic move) to a different position.</summary>
    Plan = 4,
    /// <summary>None of the above: the offset changed with no extent change, no shift and no new plan.</summary>
    Unknown = 5,
}

/// <summary>One frame's facts about a viewport (UI thread, after layout and the UI-side pose — what that frame publishes).
/// Positions are content DIP in the plan's CURRENT frame.</summary>
/// <param name="NowSec">The frame's present time (plan clock).</param>
/// <param name="Shown">The offset this frame shows (<c>ScrollState.Offset</c>).</param>
/// <param name="PlanPos">The plan's own position at <paramref name="NowSec"/> before any clamp.</param>
/// <param name="AnchorIndex">The virtualizer's anchor row this frame (0 for a plain scroller).</param>
/// <param name="AnchorOffset">The anchor row's content offset this frame (<c>OffsetOf(AnchorIndex)</c>; 0 for a plain scroller).</param>
/// <param name="PrevAnchorOffsetNow">The PREVIOUS frame's anchor row's content offset in THIS frame's extent — where the
/// row the user was looking at now sits (equal to <paramref name="AnchorOffset"/> when the anchor did not change).</param>
/// <param name="Extent">The content extent (main axis).</param>
/// <param name="FrameShift">The plan binding's cumulative frame shift (<c>PlanSlots.FrameShiftOf</c>).</param>
/// <param name="PlanSeq">The plan's sequence number (every authored plan bumps it).</param>
/// <param name="PlanProgrammatic">The plan is an app move (<c>MotionKind.Programmatic</c>: ScrollTo / BringIntoView / Restore / key).</param>
/// <param name="UserMotion">User input is driving the viewport (a live contact, or an unsettled wheel / fling / thumb plan).</param>
/// <param name="Settled">The plan reports settled at <paramref name="NowSec"/>.</param>
/// <param name="ItemCount">The virtual item count (0 for a plain scroller). A frame whose count changed is a STRUCTURAL
/// change (an insert/remove, anchored by the control's own <c>ShiftFrame</c>): the anchor INDEX no longer names the same
/// item, so the frame only re-baselines.</param>
public readonly record struct ScrollJumpFrame(
    double NowSec, double Shown, double PlanPos, int AnchorIndex, double AnchorOffset, double PrevAnchorOffsetNow,
    double Extent, double FrameShift, ulong PlanSeq, bool PlanProgrammatic, bool UserMotion, bool Settled, int ItemCount = 0);

/// <summary>A detected jump: the anchor row's screen position moved by <see cref="Displacement"/> DIP while the viewport
/// was at rest; <see cref="From"/>/<see cref="To"/> are the shown offsets, the extents are before/after.</summary>
public readonly record struct ScrollJump(ScrollJumpCause Cause, double From, double To, double Displacement,
    double ExtentBefore, double ExtentAfter, int AnchorIndex, double FrameShiftDelta);

/// <summary>Per-viewport memory of <see cref="ScrollJumpRules"/> (POD; lives on the viewport's <c>ScrollHandle</c>).</summary>
public struct ScrollJumpWatch
{
    internal bool Valid, WasAtRest;
    internal double LastUserSec;
    internal double Shown, AnchorOffset, AnchorScreen, Extent, FrameShift;
    internal int AnchorIndex, ItemCount;
    internal ulong PlanSeq;

    /// <summary>The anchor row the watch last saw (the host resolves its offset in the next frame's extent).</summary>
    public readonly int LastAnchorIndex => AnchorIndex;
    /// <summary>True once a frame has been observed.</summary>
    public readonly bool HasBaseline => Valid;
}

/// <summary>
/// The unrequested-jump detector's decision (pure, <c>ScrollJumpRulesTests</c>). A viewport AT REST — no user input for
/// <see cref="AtRestS"/>, a settled plan, on this frame and the previous one — must keep the row the user is looking at
/// where it was on screen: its screen position is <c>OffsetOf(anchor) − shown</c>. A legitimate measured correction
/// above the viewport moves BOTH terms by the same amount (the anchored frame shift) and is no jump; a programmatic move
/// (a new <c>Programmatic</c> plan) is requested and is no jump. Anything else that moves that row by more than
/// <see cref="ThresholdDip"/> between two frames is a jump, attributed to what changed between them.
/// </summary>
public static class ScrollJumpRules
{
    /// <summary>A displacement at or under this (DIP) is sub-pixel noise, never a jump.</summary>
    public const double ThresholdDip = 1.0;

    /// <summary>How long after the last user input a viewport counts as at rest.</summary>
    public const double AtRestS = 0.25;

    /// <summary>Folds one frame into <paramref name="w"/>. Returns true (and the jump) when the anchor row moved on screen
    /// while the viewport was at rest on both frames with no programmatic move in between.</summary>
    public static bool Step(ref ScrollJumpWatch w, in ScrollJumpFrame f, out ScrollJump jump)
    {
        jump = default;
        if (f.UserMotion) w.LastUserSec = f.NowSec;
        bool atRest = !f.UserMotion && f.Settled && f.NowSec - w.LastUserSec >= AtRestS;
        bool fired = false;
        if (w.Valid && atRest && w.WasAtRest && f.ItemCount == w.ItemCount)
        {
            bool newPlan = f.PlanSeq != w.PlanSeq;
            bool programmatic = newPlan && f.PlanProgrammatic;
            double screenNow = f.PrevAnchorOffsetNow - f.Shown;
            double displacement = screenNow - w.AnchorScreen;
            if (!programmatic && Math.Abs(displacement) > ThresholdDip)
            {
                double dContent = f.PrevAnchorOffsetNow - w.AnchorOffset;
                double dShown = f.Shown - w.Shown;
                double dFrame = f.FrameShift - w.FrameShift;
                ScrollJumpCause cause;
                if (dFrame != 0.0) cause = ScrollJumpCause.ShiftFrame;
                else if (Math.Abs(dContent) > ThresholdDip && Math.Abs(dShown) <= ThresholdDip) cause = ScrollJumpCause.Extent;
                else if (Math.Abs(f.Shown - f.PlanPos) > ThresholdDip) cause = ScrollJumpCause.Coverage;
                else if (newPlan) cause = ScrollJumpCause.Plan;
                else cause = ScrollJumpCause.Unknown;
                jump = new ScrollJump(cause, w.Shown, f.Shown, displacement, w.Extent, f.Extent, f.AnchorIndex, dFrame);
                fired = true;
            }
        }
        w.Valid = true;
        w.WasAtRest = atRest;
        w.Shown = f.Shown;
        w.AnchorIndex = f.AnchorIndex;
        w.AnchorOffset = f.AnchorOffset;
        w.AnchorScreen = f.AnchorOffset - f.Shown;
        w.Extent = f.Extent;
        w.FrameShift = f.FrameShift;
        w.PlanSeq = f.PlanSeq;
        w.ItemCount = f.ItemCount;
        return fired;
    }

    /// <summary>The log token of a cause (<c>[scroll.jump] cause=</c>).</summary>
    public static string CauseName(ScrollJumpCause c) => c switch
    {
        ScrollJumpCause.Extent => "extent",
        ScrollJumpCause.ShiftFrame => "shiftframe",
        ScrollJumpCause.Coverage => "coverage",
        ScrollJumpCause.Plan => "plan",
        ScrollJumpCause.Unknown => "unknown",
        _ => "none",
    };
}
