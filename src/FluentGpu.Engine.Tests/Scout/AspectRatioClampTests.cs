using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// CSS aspect-ratio transfers min/max through the ratio: the ratio-determining axis is clamped by its own min/max
/// BEFORE the other extent is derived. Before the fix FlexLayout.Measure derived the height from the unclamped width
/// and clamped each axis on its own afterwards, so a stretched Ui.AspectRatio(1, cover) { MaxWidth = 300 } in a
/// 1000-wide column was arranged as a 300x1000 strip and pushed every sibling below it down by 700. Driven headlessly
/// through the shipping Measure/Arrange path.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class AspectRatioClampTests
{
    private static (SceneStore Scene, FlexLayout Layout, NodeHandle Root) Rig()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var layout = new FlexLayout(scene, new HeadlessFontSystem(new StringTable()));
        var root = scene.CreateNode(1);
        scene.Layout(root).Direction = 1;   // column, AlignItems = Stretch (LayoutInput.Default)
        scene.Layout(root).Width = 1000f;
        scene.Layout(root).Height = 2000f;
        return (scene, layout, root);
    }

    [Fact]
    public void AFluidAspectBoxDerivesItsHeightFromTheMaxClampedWidth()
    {
        var (scene, layout, root) = Rig();
        var cover = scene.CreateNode(2);
        scene.AppendChild(root, cover);
        scene.Layout(cover).AspectRatio = 1f;
        scene.Layout(cover).MaxW = 300f;
        var below = scene.CreateNode(3);
        scene.AppendChild(root, below);
        scene.Layout(below).Height = 40f;

        layout.Run(root, new Size2(1000f, 2000f));

        Assert.Equal(300f, scene.Bounds(cover).W);
        Assert.Equal(300f, scene.Bounds(cover).H);   // was 1000: derived from the unclamped stretch width
        Assert.Equal(300f, scene.Bounds(below).Y);   // was 1000: the strip pushed the sibling down
    }

    [Fact]
    public void AnExplicitWidthAboveMaxWidthDerivesFromTheClampedWidth()
    {
        var (scene, layout, root) = Rig();
        var cover = scene.CreateNode(2);
        scene.AppendChild(root, cover);
        scene.Layout(cover).AspectRatio = 1f;
        scene.Layout(cover).Width = 500f;
        scene.Layout(cover).MaxW = 300f;

        layout.Run(root, new Size2(1000f, 2000f));

        Assert.Equal(300f, scene.Bounds(cover).W);
        Assert.Equal(300f, scene.Bounds(cover).H);   // was 500
    }

    [Fact]
    public void AnExplicitHeightBelowMinHeightDerivesFromTheClampedHeight()
    {
        var (scene, layout, root) = Rig();
        var box = scene.CreateNode(2);
        scene.AppendChild(root, box);
        scene.Layout(box).AlignSelf = FlexAlign.Start;   // keep the width content-derived, not stretched
        scene.Layout(box).AspectRatio = 2f;
        scene.Layout(box).Height = 50f;
        scene.Layout(box).MinH = 100f;

        layout.Run(root, new Size2(1000f, 2000f));

        Assert.Equal(100f, scene.Bounds(box).H);
        Assert.Equal(200f, scene.Bounds(box).W);   // was 100: derived from the unclamped 50
    }
}
