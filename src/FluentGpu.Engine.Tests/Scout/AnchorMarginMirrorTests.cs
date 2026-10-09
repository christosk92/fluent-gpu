using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A transparent anchor takes its parent's slot as its rendered root's MARGIN box: the anchor's column places the root
/// at its leading margin inside itself, so a declared Width/Height/Min/Max/Basis must grow by that margin. Before the fix
/// the anchor mirrored the bare border-box extent: a 32-wide root with an 8+8 margin got a 32-wide anchor, sat at x=8 in
/// it and overlapped the next sibling (Wavee's empty Pinned drop zone lost its 4-DIP trailing gap).
/// </summary>
public sealed class AnchorMarginMirrorTests
{
    private static readonly Edges4 SideMargin = new(8f, 0f, 8f, 0f);

    private sealed class Root(Element root) : Component
    {
        public override Element Render() => root;
    }

    private sealed class Outer(Element root) : Component
    {
        public override Element Render() => Embed.Comp(() => new Root(root));
    }

    private sealed class Harness
    {
        public readonly SceneStore Scene = new();
        public readonly TreeReconciler Recon;
        public readonly FlexLayout Layout;
        public NodeHandle Last;

        // A row (800x40, gap 10) of [20-wide box, middle, 20-wide probe] — or a column (200x200, no gap) of [middle, probe].
        public Harness(Element middle, bool column = false)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            var strings = new StringTable();
            Recon = new TreeReconciler(Scene, strings);
            Layout = new FlexLayout(Scene, new HeadlessFontSystem(strings));
            var probe = new BoxEl { Width = 20f, Height = 20f, OnRealized = n => Last = n };
            Recon.ReconcileRoot(column
                ? new BoxEl { Direction = 1, Width = 200f, Height = 200f, Children = [middle, probe] }
                : new BoxEl { Direction = 0, Gap = 10f, Width = 800f, Height = 40f, Children = [new BoxEl { Width = 20f, Height = 20f }, middle, probe] }, null);
            Recon.Runtime.Flush();
            Layout.Run(Scene.Root, column ? new Size2(200f, 200f) : new Size2(800f, 40f));
        }

        public RectF LastBox => Scene.Bounds(Last);
    }

    [Fact]
    public void AnEmbedWhoseRootHasAWidthAndASideMarginReservesTheMarginBox()
    {
        NodeHandle root = default;
        var h = new Harness(Embed.Comp(() => new Root(new BoxEl { Width = 32f, Height = 20f, Margin = SideMargin, OnRealized = n => root = n })));
        Assert.Equal(20f + 10f + 8f + 32f + 8f + 10f, h.LastBox.X);
        Assert.Equal(48f, h.Scene.Bounds(h.Scene.Parent(root)).W);   // the anchor is the margin box ...
        Assert.Equal(8f, h.Scene.Bounds(root).X);                     // ... and the root sits at its margin inside it
        Assert.Equal(32f, h.Scene.Bounds(root).W);
    }

    [Fact]
    public void AnEmbedWhoseRootHasAHeightAndATrailingGapKeepsTheGapInAColumn()
    {
        // Wavee's empty Pinned section: Height = 56, Margin = (0, 0, 0, 4); the next row must start below the gap.
        var h = new Harness(Embed.Comp(() => new Root(new BoxEl { Height = 56f, Margin = new Edges4(0f, 0f, 0f, 4f) })), column: true);
        Assert.Equal(60f, h.LastBox.Y);
    }

    [Fact]
    public void AShowWhoseBranchHasAWidthAndASideMarginReservesTheMarginBox()
    {
        var h = new Harness(Flow.Show(() => true, new BoxEl { Width = 32f, Height = 20f, Margin = SideMargin }));
        Assert.Equal(20f + 10f + 8f + 32f + 8f + 10f, h.LastBox.X);
    }

    [Fact]
    public void AMaxWidthOnTheRootClampsTheAnchorAtTheMarginBox()
    {
        var h = new Harness(Embed.Comp(() => new Root(new BoxEl
        {
            MaxWidth = 50f, Height = 20f, Margin = SideMargin,
            Children = [new BoxEl { Width = 100f, Height = 20f }],
        })));
        Assert.Equal(20f + 10f + 8f + 50f + 8f + 10f, h.LastBox.X);
    }

    [Fact]
    public void ABasisOnTheRootTakesTheRowMarginEvenThroughANestedAnchor()
    {
        var el = new BoxEl { Basis = 40f, Width = 40f, Height = 20f, Margin = SideMargin };
        var direct = new Harness(Embed.Comp(() => new Root(el)));
        Assert.Equal(20f + 10f + 8f + 40f + 8f + 10f, direct.LastBox.X);
        var nested = new Harness(Embed.Comp(() => new Outer(el)));
        Assert.Equal(20f + 10f + 8f + 40f + 8f + 10f, nested.LastBox.X);
    }
}
