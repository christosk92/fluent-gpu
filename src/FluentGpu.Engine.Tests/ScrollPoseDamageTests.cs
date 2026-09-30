using System;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Since the retained-tile recorder partition (docs/plans/scroll-gpu-retained-tiles-implementation.md P1) a scroll pose
/// — the content translate, a sticky's folded transform — is a COMPOSITE parameter: the content and the sticky are
/// translation slices recorded pose-free, and the flatten/composite damages their motion (old ∪ new footprint) itself.
/// So the render poser writes the pose into the snapshot's compositor overlay (the node reads its posed paint) but never
/// marks the node dirty — neither on a fresh adoption nor on a re-pose — and a settled viewport re-posed every tick of
/// unrelated motion can never repaint its band.
/// </summary>
public sealed class ScrollPoseDamageTests
{
    private static readonly ScrollViewportId Vp = new(1, 1);

    private sealed class Fixture
    {
        internal readonly SceneStore Scene = new();
        internal readonly SceneRecordingSnapshot Snapshot = new();
        internal readonly NodeHandle Viewport, Content, Header;

        internal Fixture()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Scene.Root = Scene.CreateNode(1);
            Viewport = Scene.CreateNode(2);
            Content = Scene.CreateNode(3);
            Header = Scene.CreateNode(4);
            Scene.AppendChild(Scene.Root, Viewport);
            Scene.AppendChild(Viewport, Content);
            Scene.AppendChild(Content, Header);
            Scene.Bounds(Viewport) = new RectF(0, 0, 400, 400);
            Scene.Bounds(Content) = new RectF(0, 0, 400, 5000);
            Scene.Bounds(Header) = new RectF(0, 1000, 400, 40);
            Snapshot.Capture(Scene);
        }

        internal ScrollCoverageTable Coverage()
        {
            var cov = new ScrollCoverageTable();
            var fx = new[]
            {
                new ScrollEffectRow((int)Header.Raw.Index, ScrollEffect.Sticky(inset: 0f),
                    new EffectGeometry(1000.0, 40.0, 5000.0, 5000.0, 400.0, 0f, 0f)),
            };
            cov.AddRow(new ScrollCoverageRow((int)Viewport.Raw.Index, Viewport.Raw.Gen, (int)Content.Raw.Index,
                0.0, 0.0, 5000.0, 400.0, 5000.0, false, 0, 0, 0.0), fx);
            return cov;
        }
    }

    [Fact]
    public void ASettledViewportReposedWithAnUnchangedTranslateContributesNoRepaintBand()
    {
        var f = new Fixture();
        var slots = new PlanSlots();
        var vp = new ScrollViewportId((int)f.Viewport.Raw.Index, f.Viewport.Raw.Gen);
        slots.Allocate(vp, ScrollPlan.Idle(vp.Node, 1200.0, 0.0, 4600.0));   // settled, header pinned (sticky engaged)
        var poser = new ScrollPoser();
        var sink = new SnapshotScrollPoseSink();
        sink.Bind(f.Snapshot);
        poser.Adopt(f.Coverage());

        // Tick 1: a fresh adoption — the pose is written, but it is a composite parameter: nothing is marked dirty.
        f.Snapshot.BeginCompositorOverlay();
        byte capturedDirty = f.Snapshot.RecordDirtyBits(f.Content);   // the fixture's creation marks, from the capture
        poser.Tick(slots, 1.0, 1f, sink);
        Assert.Equal(-1200f, f.Snapshot.Paint(f.Content).LocalTransform.Dy);
        Assert.True((f.Snapshot.Flags(f.Content) & NodeFlags.TransformDirty) == 0);
        Assert.True(f.Snapshot.RecordDirtyBits(f.Content) == capturedDirty, "a scroll pose adds no dirty trail");
        Assert.False(sink.RecordRequired);

        // Tick 2: the same settled plan, the same translate. The pose must still be WRITTEN (the node reads its posed
        // paint) and claim no change — neither for the content nor for the pinned header's transform.
        f.Snapshot.BeginCompositorOverlay();
        poser.Tick(slots, 2.0, 1f, sink);
        Assert.Equal(-1200f, f.Snapshot.Paint(f.Content).LocalTransform.Dy);
        Assert.True((f.Snapshot.Flags(f.Content) & NodeFlags.TransformDirty) == 0,
            "an unchanged content translate was reported as a pose change");
        Assert.True((f.Snapshot.Flags(f.Header) & NodeFlags.TransformDirty) == 0,
            "an unchanged sticky effect value was reported as a pose change");
    }
}
