using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// BoxEl.AspectRatio on a ZStack box. Measure routed a ZStack to MeasureZStack before the aspect block, which sized the
/// stack from its tallest layer and never read the ratio: a stretched square tile (Wavee's Concert BrowseAllCard, whose
/// layers are padded icons) measured to a short strip and the column stacked the next sibling right under it.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ZStackAspectRatioTests
{
    private static readonly Size2 Window = new(800f, 600f);

    private static (SceneStore Scene, FlexLayout Layout, TreeReconciler Recon) Rig()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var strings = new StringTable();
        var scene = new SceneStore();
        return (scene, new FlexLayout(scene, new HeadlessFontSystem(strings)), new TreeReconciler(scene, strings));
    }

    [Fact]
    public void AStretchedFluidZStackDerivesItsHeightFromTheOfferedWidth()
    {
        var (scene, layout, recon) = Rig();
        NodeHandle tile = default, next = default;
        recon.ReconcileRoot(new BoxEl
        {
            Direction = 1,
            AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl
                {
                    Width = 200f, Direction = 1,
                    Children =
                    [
                        new BoxEl
                        {
                            ZStack = true, AspectRatio = 1f, AlignSelf = FlexAlign.Stretch, OnRealized = n => tile = n,
                            Children = [new BoxEl { Width = 30f, Height = 30f }],
                        },
                        new BoxEl { Width = 10f, Height = 10f, OnRealized = n => next = n },
                    ],
                },
            ],
        }, null);
        layout.Run(scene.Root, Window);

        Assert.Equal(200f, scene.Bounds(tile).W);
        Assert.Equal(200f, scene.Bounds(tile).H);   // was 30: the tallest layer
        Assert.Equal(200f, scene.Bounds(next).Y);   // was 30: the sibling sat under the strip
    }

    [Fact]
    public void AnExplicitWidthZStackDerivesItsHeight()
    {
        var (scene, layout, recon) = Rig();
        NodeHandle tile = default;
        recon.ReconcileRoot(new BoxEl
        {
            Direction = 1,
            AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl
                {
                    ZStack = true, Width = 160f, AspectRatio = 16f / 9f, OnRealized = n => tile = n,
                    Children = [new BoxEl { Width = 20f, Height = 20f }],
                },
            ],
        }, null);
        layout.Run(scene.Root, Window);

        Assert.Equal(160f, scene.Bounds(tile).W);
        Assert.Equal(90f, scene.Bounds(tile).H, 3);   // was 20
    }
}
