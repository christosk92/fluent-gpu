using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// The poser's feedback slots are indexed by coverage row. A viewport that drops out of the coverage (a KeepAlive page
/// parked behind a page with fewer scrollers) or whose plan slot is gone must stop reporting feedback: otherwise the
/// unpark frame reads its pre-park pose (old offset, an unsettled Fling) before the render thread adopts a coverage
/// that covers it again, and a grab in that frame re-plans from the stale position.
/// </summary>
public sealed class ScrollPoserFeedbackRetireTests
{
    private static readonly ScrollViewportId Shell = new(1, 1);
    private static readonly ScrollViewportId List = new(2, 1);

    private sealed class NullSink : IScrollPoseSink
    {
        public void PoseViewport(int vpNode, double shown) { }
        public void PoseContent(int node, bool horizontal, float trans, bool changed) { }
        public void PoseEffect(int node, EffectChannel channel, float value, bool changed) { }
        public void PoseTransform(int node, in EffectTransform transform, bool changed) { }
    }

    private static ScrollCoverageRow Row(ScrollViewportId vp, int content) =>
        new(vp.Node, vp.Gen, content, 0.0, 0.0, 5000.0, 400.0, 5000.0, false, 0, 0, 0.0);

    private static ScrollCoverageTable Coverage(params ScrollViewportId[] vps)
    {
        var cov = new ScrollCoverageTable();
        for (int i = 0; i < vps.Length; i++) cov.AddRow(Row(vps[i], 100 + i));
        return cov;
    }

    [Fact]
    public void AViewportLeftOutOfTheAdoptedCoverageReportsNoFeedback()
    {
        var slots = new PlanSlots();
        slots.Allocate(Shell, ScrollPlan.Idle(Shell.Node, 0.0, 0.0, 4600.0));
        slots.Allocate(List, ScrollPlan.Idle(List.Node, 300.0, 0.0, 4600.0));
        var poser = new ScrollPoser();
        var sink = new NullSink();

        poser.Adopt(Coverage(Shell, List));   // the list is row 1
        poser.Tick(slots, 1.0, 1f, sink);
        Assert.True(poser.TryGetFeedback(List, out var before));
        Assert.Equal(300.0, before.Shown);

        // The list's page parks; the page shown now has fewer scrollers, so row 1 is never re-posed. The list's plan
        // keeps moving while parked (a coast settling elsewhere).
        poser.Adopt(Coverage(Shell));
        slots.Write(List, ScrollPlan.Idle(List.Node, 900.0, 0.0, 4600.0));
        poser.Tick(slots, 2.0, 1f, sink);

        Assert.False(poser.TryGetFeedback(List, out var stale),
            $"an uncovered viewport still reports the pose from before it left the coverage (Shown={stale.Shown})");
        Assert.True(poser.TryGetFeedback(Shell, out _));

        // Covered again: fresh feedback at the plan's current position.
        poser.Adopt(Coverage(Shell, List));
        poser.Tick(slots, 3.0, 1f, sink);
        Assert.True(poser.TryGetFeedback(List, out var after));
        Assert.Equal(900.0, after.Shown);
    }

    [Fact]
    public void ACoveredRowWhosePlanSlotIsGoneReportsNoFeedback()
    {
        var slots = new PlanSlots();
        slots.Allocate(List, ScrollPlan.Idle(List.Node, 300.0, 0.0, 4600.0));
        var poser = new ScrollPoser();
        var sink = new NullSink();
        poser.Adopt(Coverage(List));
        poser.Tick(slots, 1.0, 1f, sink);
        Assert.True(poser.TryGetFeedback(List, out _));

        slots.Release(List);
        poser.Tick(slots, 2.0, 1f, sink);
        Assert.False(poser.TryGetFeedback(List, out var stale),
            $"a row the poser could not pose still reports its last pose (Shown={stale.Shown})");
    }
}
