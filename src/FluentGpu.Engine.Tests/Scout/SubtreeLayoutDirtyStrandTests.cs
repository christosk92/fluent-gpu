using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// A child that is marked LayoutDirty and then freed or detached (exit orphan, KeepAlive park) in the same frame used to
/// strand <see cref="SceneStore.AuxFlags.SubtreeLayoutDirty"/> on its former parent. <c>ClearLayoutDirty</c> walked up
/// from each worklist entry's CURRENT parent: a dead entry was skipped, an orphan's parent is 0, and the parent's own
/// mark clears from ITS parent upward. The next mark under that parent stopped at the stuck bit, the ancestors read
/// clean, and a clean, scroller-free ancestor's Arrange early-out skipped the change, so the sibling kept its old rect.
/// </summary>
public sealed class SubtreeLayoutDirtyStrandTests
{
    [Theory]
    [InlineData(false)]   // removed without exit animation: FreeSubtree
    [InlineData(true)]    // removed with exit animation: Orphan (live, detached)
    public void ASiblingChangeAfterADirtyChildIsRemovedIsStillArranged(bool orphan)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var layout = new FlexLayout(scene, new HeadlessFontSystem(new StringTable()));
        var window = new Size2(800f, 600f);

        // R -> A (fixed box, scroller-free: the early-out candidate) -> P (row) -> { C, D }
        var r = scene.CreateNode(1);
        scene.Layout(r).Direction = 1;
        var a = scene.CreateNode(2);
        scene.AppendChild(r, a);
        scene.Layout(a).Direction = 1;
        scene.Layout(a).Width = 400f;
        scene.Layout(a).Height = 40f;
        var p = scene.CreateNode(3);
        scene.AppendChild(a, p);
        scene.Layout(p).Direction = 0;
        scene.Layout(p).Height = 40f;
        scene.Layout(p).AlignItems = FlexAlign.Start;
        var c = scene.CreateNode(4);
        scene.AppendChild(p, c);
        scene.Layout(c).Width = 30f;
        scene.Layout(c).Height = 10f;
        var d = scene.CreateNode(5);
        scene.AppendChild(p, d);
        scene.Layout(d).Width = 50f;
        scene.Layout(d).Height = 10f;
        layout.Run(r, window);
        scene.ClearLayoutDirty();

        // Frame 1: C changes and leaves in the same flush; the reconciler marks the surviving parent.
        scene.Mark(c, NodeFlags.LayoutDirty);
        if (orphan) scene.Orphan(c); else scene.FreeSubtree(c);
        scene.Mark(p, NodeFlags.LayoutDirty);
        layout.Run(r, window);
        scene.ClearLayoutDirty();
        Assert.False(scene.IsSubtreeLayoutDirty(p));   // no bit may survive the frame boundary
        Assert.Equal(50f, scene.Bounds(d).W, 3);

        // Frame 2: the surviving sibling changes size.
        scene.Layout(d).Width = 120f;
        scene.Mark(d, NodeFlags.LayoutDirty);
        layout.Run(r, window);
        Assert.Equal(120f, scene.Bounds(d).W, 3);   // was 50: A's early-out skipped it
        scene.ClearLayoutDirty();
    }
}
