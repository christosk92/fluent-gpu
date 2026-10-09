using FluentGpu.Scroll.Motion;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>Quick keyboard steps accumulate onto the in-flight glide's destination, like wheel notches and ScrollBy.
/// Each press used to re-base on the displayed position, so a second PageDown 100 ms after the first landed about half a
/// page short and a held arrow key crawled at a quarter of its step rate.</summary>
public sealed class KeyStepAccumulateTests
{
    private static readonly MotionFeel Feel = FeelProfiles.Standard;
    private const double Viewport = 800.0;

    private static ScrollPlan Idle(double pos) => ScrollPlan.Idle(0, pos, 0.0, 20_000.0, Viewport, Feel.RubberBandC);

    [Fact]
    public void TwoQuickPageDowns_TravelTwoFullPages()
    {
        double page = Feel.PageFraction * Viewport;
        var first = PlanAuthor.Key(Idle(0.0), 0.0, KeyMove.PageDown, in Feel, Viewport);
        Assert.True(first.Eval(0.1, out _, out _) < page * 0.9);   // still mid-glide when the second press lands

        var second = PlanAuthor.Key(in first, 0.1, KeyMove.PageDown, in Feel, Viewport);

        Assert.Equal(2.0 * page, second.Dest, 6);
    }

    [Fact]
    public void HeldArrowAutoRepeat_TravelsEveryLine()
    {
        ScrollPlan plan = Idle(1_000.0);
        for (int i = 0; i < 10; i++)
            plan = PlanAuthor.Key(in plan, i * 0.033, KeyMove.LineDown, in Feel, Viewport);

        Assert.Equal(1_000.0 + 10.0 * Feel.KeyLineDip, plan.Dest, 6);
    }

    [Fact]
    public void Reversal_StepsFromTheDisplayedPosition()
    {
        double page = Feel.PageFraction * Viewport;
        var down = PlanAuthor.Key(Idle(5_000.0), 0.0, KeyMove.PageDown, in Feel, Viewport);
        double shown = down.Eval(0.1, out _, out _);

        var up = PlanAuthor.Key(in down, 0.1, KeyMove.PageUp, in Feel, Viewport);

        Assert.Equal(shown - page, up.Dest, 6);
        Assert.Equal(shown, up.Eval(0.1, out _, out _), 6);   // no jump at the re-plan
    }

    [Fact]
    public void StepOverAFling_DoesNotAddToItsAsymptote()
    {
        // A coasting fling's Dest is its decay asymptote: a key press over it steps from what is shown.
        var coast = new MotionSeg(SegKind.Decay, 0.0, double.PositiveInfinity, 1_000.0, 0.0, 3_000.0, 4.0);
        var fling = new ScrollPlan(coast, default, default, default, 1, 0, 0, 0, 0.0, 20_000.0, Viewport, Feel.RubberBandC,
            ContactClock.Device, OverpanPolicy.None, MotionKind.Fling, default);
        double shown = fling.Eval(0.05, out _, out _);

        var next = PlanAuthor.Key(in fling, 0.05, KeyMove.LineDown, in Feel, Viewport);

        Assert.Equal(shown + Feel.KeyLineDip, next.Dest, 6);
    }
}
