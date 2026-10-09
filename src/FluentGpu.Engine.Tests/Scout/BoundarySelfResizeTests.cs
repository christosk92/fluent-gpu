using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A layout boundary (fixed Width+Height, no flex, ClipToBounds) is the scoped-relayout firewall: a change INSIDE it
/// relayouts only it. A change to the boundary's OWN size is not inside it — the parent places it, so the parent must
/// re-solve. Before the fix the bound Width/Height effects and the re-render column write marked only the node, the
/// invalidator stopped at the node itself, and RunSubtree grew it in its old slot: the next sibling kept its X and
/// the grown tile overlapped it. Driven through the shipping reconciler + <see cref="LayoutInvalidator"/> path.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class BoundarySelfResizeTests
{
    private static readonly Size2 Window = new(800f, 600f);

    private sealed class Rig
    {
        public readonly StringTable Strings = new();
        public readonly SceneStore Scene = new();
        public readonly TreeReconciler Recon;
        public readonly FlexLayout Layout;
        public readonly LayoutInvalidator Invalidator;

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Recon = new TreeReconciler(Scene, Strings);
            Layout = new FlexLayout(Scene, new HeadlessFontSystem(Strings));
            Invalidator = new LayoutInvalidator(Scene, Layout);
        }

        /// <summary>The host's first frame: a full solve, then the worklist is consumed.</summary>
        public void FullLayout() { Layout.Run(Scene.Root, Window); Scene.ClearLayoutDirty(); }

        /// <summary>A later frame: flush the reactive writes, then the scoped relayout the host runs.</summary>
        public void ScopedFrame() { Recon.Runtime.Flush(); Invalidator.RunDirty(Window); Scene.ClearLayoutDirty(); }
    }

    [Fact]
    public void ABoundSizeOnABoundaryReflowsItsSiblings()
    {
        var rig = new Rig();
        var size = new Signal<float>(120f);
        NodeHandle tile = default, next = default;
        rig.Recon.ReconcileRoot(new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl { Width = Prop.Of(() => size.Value), Height = Prop.Of(() => size.Value), ClipToBounds = true, OnRealized = n => tile = n },
                new BoxEl { Width = 50f, Height = 50f, OnRealized = n => next = n },
            ],
        }, null);
        rig.FullLayout();
        Assert.Equal(120f, rig.Scene.Bounds(next).X);

        size.Value = 160f;
        rig.ScopedFrame();

        Assert.Equal(160f, rig.Scene.Bounds(tile).W);
        Assert.Equal(160f, rig.Scene.Bounds(next).X);   // was 120: the grown tile overlapped its sibling by 40
    }

    private sealed class Tile : Component
    {
        public readonly Signal<float> Size = new(120f);
        public NodeHandle Node;

        public override Element Render()
        {
            float s = Size.Value;
            return new BoxEl { Width = s, Height = s, ClipToBounds = true, OnRealized = n => Node = n };
        }
    }

    [Fact]
    public void ARerenderedBoundaryRootReflowsItsSiblings()
    {
        // The re-render path: the boundary is the component's rendered root, so RunComponent's own mark lands on the
        // same node and cannot reach the parent either.
        var rig = new Rig();
        var comp = new Tile();
        NodeHandle next = default;
        rig.Recon.ReconcileRoot(new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Start,
            Children = [Embed.Comp(() => comp), new BoxEl { Width = 50f, Height = 50f, OnRealized = n => next = n }],
        }, null);
        rig.FullLayout();
        Assert.Equal(120f, rig.Scene.Bounds(next).X);

        comp.Size.Value = 160f;
        rig.ScopedFrame();

        Assert.Equal(160f, rig.Scene.Bounds(comp.Node).W);
        Assert.Equal(160f, rig.Scene.Bounds(rig.Scene.Parent(comp.Node)).W);   // the component anchor grows with it
        Assert.Equal(160f, rig.Scene.Bounds(next).X);
    }

    [Fact]
    public void AChangeInsideABoundaryStillStaysInsideIt()
    {
        // The firewall itself is unchanged: a child resize under a fixed boundary marks nothing above the boundary.
        var rig = new Rig();
        var inner = new Signal<float>(20f);
        NodeHandle tile = default, next = default;
        rig.Recon.ReconcileRoot(new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl
                {
                    Width = 120f, Height = 120f, ClipToBounds = true, OnRealized = n => tile = n,
                    Children = [new BoxEl { Width = Prop.Of(() => inner.Value), Height = 10f }],
                },
                new BoxEl { Width = 50f, Height = 50f, OnRealized = n => next = n },
            ],
        }, null);
        rig.FullLayout();

        inner.Value = 80f;
        rig.ScopedFrame();

        Assert.Equal(0, rig.Invalidator.EscapesThisFrame);   // the walk stopped at the tile, never reached the root
        Assert.Equal(120f, rig.Scene.Bounds(tile).W);
        Assert.Equal(120f, rig.Scene.Bounds(next).X);
    }
}
