using FluentGpu.Scroll.Diag;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The unrequested-jump detector's decision (<see cref="ScrollJumpRules"/>): a viewport at rest keeps the row the user is
/// looking at where it was on screen. Frames below are shaped after the owner's 2026-09-25 Library V3 rail evidence
/// (<c>sidebar.v3.rail</c>: extent 3893, anchor row 67, offset ~3107, the head above growing by 186 DIP on selection).
/// </summary>
public sealed class ScrollJumpRulesTests
{
    private const double Refresh = 1.0 / 120.0;

    /// <summary>A frame at rest at <paramref name="shown"/>, anchor 67 at <paramref name="anchorOffset"/>.</summary>
    private static ScrollJumpFrame Rest(double t, double shown, double anchorOffset, double prevAnchorOffsetNow = double.NaN,
        double extent = 3893.0, double frameShift = 0.0, ulong seq = 7, bool programmatic = false, double planPos = double.NaN)
        => new(t, shown, double.IsNaN(planPos) ? shown : planPos, 67, anchorOffset,
            double.IsNaN(prevAnchorOffsetNow) ? anchorOffset : prevAnchorOffsetNow, extent, frameShift, seq, programmatic,
            UserMotion: false, Settled: true);

    private static ScrollJumpWatch Settled(double t0, double shown, double anchorOffset, out double t)
    {
        var w = default(ScrollJumpWatch);
        t = t0;
        for (int i = 0; i < 3; i++, t += Refresh)
            Assert.False(ScrollJumpRules.Step(ref w, Rest(t, shown, anchorOffset), out _));
        return w;
    }

    [Fact]
    public void AnAnchoredCorrectionAboveTheViewport_IsNoJump()
    {
        // The head grows 186 DIP: the anchor row's offset and the shown offset move together (PlanSlots.Shift).
        var w = Settled(10.0, 3107.36, 3280.0, out double t);
        var f = Rest(t, 3107.36 + 186.0, 3280.0 + 186.0, extent: 3893.0 + 186.0, frameShift: 186.0);
        Assert.False(ScrollJumpRules.Step(ref w, in f, out _));
    }

    [Fact]
    public void TheShiftLostOnALaterFrame_IsAJump_AttributedToThePlanWrite()
    {
        // Frame A: the anchored shift (no jump). Frame B: the plan is re-written at the PRE-shift offset (a new plan that is
        // neither user input nor a programmatic move) — the row under the user moves 186 DIP down the screen.
        var w = Settled(10.0, 3107.36, 3280.0, out double t);
        Assert.False(ScrollJumpRules.Step(ref w, Rest(t, 3293.36, 3466.0, extent: 4079.0, frameShift: 186.0), out _));
        t += Refresh;
        Assert.True(ScrollJumpRules.Step(ref w, Rest(t, 3107.36, 3466.0, extent: 4079.0, frameShift: 186.0, seq: 8), out var jump));
        Assert.Equal(ScrollJumpCause.Plan, jump.Cause);
        Assert.Equal(186.0, jump.Displacement, 6);
        Assert.Equal(3293.36, jump.From, 6);
        Assert.Equal(3107.36, jump.To, 6);
    }

    [Fact]
    public void AnUnanchoredExtentChangeAboveTheViewport_IsAJump_CauseExtent()
    {
        var w = Settled(10.0, 3107.36, 3280.0, out double t);
        Assert.True(ScrollJumpRules.Step(ref w, Rest(t, 3107.36, 3466.0, extent: 4079.0), out var jump));
        Assert.Equal(ScrollJumpCause.Extent, jump.Cause);
        Assert.Equal(186.0, jump.Displacement, 6);
        Assert.Equal(3893.0, jump.ExtentBefore);
        Assert.Equal(4079.0, jump.ExtentAfter);
    }

    [Fact]
    public void AShiftThatTheShownOffsetDoesNotFollow_IsAJump_CauseShiftFrame()
    {
        var w = Settled(10.0, 3107.36, 3280.0, out double t);
        Assert.True(ScrollJumpRules.Step(ref w, Rest(t, 3107.36, 3466.0, extent: 4079.0, frameShift: 186.0), out var jump));
        Assert.Equal(ScrollJumpCause.ShiftFrame, jump.Cause);
        Assert.Equal(186.0, jump.FrameShiftDelta, 6);
    }

    [Fact]
    public void AnOffsetMoveWithNoNewPlanAndNoShift_IsUnknown_AndAClampIsCoverage()
    {
        var w = Settled(10.0, 3107.36, 3280.0, out double t);
        Assert.True(ScrollJumpRules.Step(ref w, Rest(t, 3293.36, 3280.0), out var unknown));
        Assert.Equal(ScrollJumpCause.Unknown, unknown.Cause);

        var w2 = Settled(20.0, 3107.36, 3280.0, out double t2);
        Assert.True(ScrollJumpRules.Step(ref w2, Rest(t2, 3000.0, 3280.0, planPos: 3107.36), out var clamp));
        Assert.Equal(ScrollJumpCause.Coverage, clamp.Cause);
    }

    [Fact]
    public void AProgrammaticMove_IsRequested_NotAJump()
    {
        var w = Settled(10.0, 3107.36, 3280.0, out double t);
        Assert.False(ScrollJumpRules.Step(ref w, Rest(t, 2000.0, 3280.0, seq: 8, programmatic: true), out _));
    }

    [Fact]
    public void SubPixelMotion_IsNoJump()
    {
        var w = Settled(10.0, 3107.36, 3280.0, out double t);
        Assert.False(ScrollJumpRules.Step(ref w, Rest(t, 3107.36 + ScrollJumpRules.ThresholdDip * 0.9, 3280.0), out _));
    }

    [Fact]
    public void WhileTheUserScrolls_AndForTheAtRestWindowAfter_NothingFires()
    {
        var w = Settled(10.0, 3107.36, 3280.0, out double t);
        var moving = new ScrollJumpFrame(t, 3150.0, 3150.0, 67, 3280.0, 3280.0, 3893.0, 0.0, 9, false, UserMotion: true, Settled: false);
        Assert.False(ScrollJumpRules.Step(ref w, in moving, out _));
        t += Refresh;
        // Settled again but within the at-rest window: a correction landing now is not judged.
        Assert.False(ScrollJumpRules.Step(ref w, Rest(t, 3336.0, 3280.0, seq: 10), out _));
        t += ScrollJumpRules.AtRestS;
        Assert.False(ScrollJumpRules.Step(ref w, Rest(t, 3336.0, 3280.0, seq: 10), out _));   // first at-rest frame: baseline
        t += Refresh;
        Assert.True(ScrollJumpRules.Step(ref w, Rest(t, 3150.0, 3280.0, seq: 11), out var jump));
        Assert.Equal(ScrollJumpCause.Plan, jump.Cause);
    }

    [Fact]
    public void AStructuralChange_TheItemCountMoving_OnlyRebaselines()
    {
        // An insert above the viewport (ItemsView anchors it with ShiftFrame): the anchor INDEX now names another item,
        // so the frame is not judged — the next one is, against the new baseline.
        var w = default(ScrollJumpWatch);
        double t = 10.0;
        ScrollJumpFrame F(double shown, double anchorOffset, int count, double shift)
            => new(t, shown, shown, 67, anchorOffset, anchorOffset, 3893.0, shift, 7, false, false, true, count);
        for (int i = 0; i < 3; i++, t += Refresh) ScrollJumpRules.Step(ref w, F(3107.36, 3280.0, 78, 0.0), out _);
        Assert.False(ScrollJumpRules.Step(ref w, F(3153.36, 3290.0, 79, 46.0), out _));
        t += Refresh;
        Assert.False(ScrollJumpRules.Step(ref w, F(3153.36, 3290.0, 79, 46.0), out _));
        t += Refresh;
        Assert.True(ScrollJumpRules.Step(ref w, F(3107.36, 3290.0, 79, 46.0), out _));
    }

    [Fact]
    public void TheAnchorRowChanging_IsJudgedByWhereThePreviousAnchorRowNowSits()
    {
        // The virtualizer re-anchors on row 68 (offset 3320) while the content stays put: the previous anchor row 67 is still
        // at 3280 in this frame's extent, so nothing moved on screen.
        var w = Settled(10.0, 3107.36, 3280.0, out double t);
        var f = new ScrollJumpFrame(t, 3107.36, 3107.36, 68, 3320.0, 3280.0, 3893.0, 0.0, 7, false, false, true);
        Assert.False(ScrollJumpRules.Step(ref w, in f, out _));
        Assert.Equal(68, w.LastAnchorIndex);
    }
}
