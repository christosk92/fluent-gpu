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
/// A ZStack layer's own MinWidth/MaxWidth/MinHeight/MaxHeight at arrange. ArrangeZStack gave a Start/Stretch/Auto
/// layer the whole slot without the clamp a flex parent applies (ClampMain/ClampCross), so a capped overlay (the
/// MediaCaptionOverlay pill, MaxWidth 880) spanned the full stack and a MinWidth layer was squeezed below its floor.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ZStackLayerClampTests
{
    private static readonly Size2 Window = new(1200f, 800f);

    private static (SceneStore Scene, FlexLayout Layout, TreeReconciler Recon) Rig()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var strings = new StringTable();
        var scene = new SceneStore();
        return (scene, new FlexLayout(scene, new HeadlessFontSystem(strings)), new TreeReconciler(scene, strings));
    }

    private static (SceneStore Scene, NodeHandle Layer) ArrangeLayer(float stackW, float stackH, BoxEl layer)
    {
        var (scene, layout, recon) = Rig();
        NodeHandle n = default;
        recon.ReconcileRoot(new BoxEl
        {
            Direction = 1,
            AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl
                {
                    ZStack = true, Width = stackW, Height = stackH,
                    Children = [layer with { OnRealized = h => n = h }],
                },
            ],
        }, null);
        layout.Run(scene.Root, Window);
        return (scene, n);
    }

    [Fact]
    public void AFillLayerIsCappedByItsMaxWidth()
    {
        var (scene, n) = ArrangeLayer(1000f, 400f, new BoxEl { MaxWidth = 300f, Margin = new Edges4(24f, 0f, 24f, 0f) });
        Assert.Equal(300f, scene.Bounds(n).W);   // was 952: the whole slot
        Assert.Equal(400f, scene.Bounds(n).H);
    }

    [Fact]
    public void AFillLayerIsCappedByItsMaxHeight()
    {
        var (scene, n) = ArrangeLayer(1000f, 400f, new BoxEl { MaxHeight = 100f });
        Assert.Equal(100f, scene.Bounds(n).H);   // was 400
    }

    [Fact]
    public void AFillLayerKeepsItsMinWidthInANarrowerStack()
    {
        var (scene, n) = ArrangeLayer(100f, 50f, new BoxEl { MinWidth = 160f });
        Assert.Equal(160f, scene.Bounds(n).W);   // was 100: squeezed below its floor
    }

    [Fact]
    public void AVerticallyCenteredCaptionPillKeepsItsMaxWidth()
    {
        // The MediaCaptionOverlay shape: AlignSelf (vertical in a ZStack) set, JustifySelf left Auto ⇒ horizontal Start.
        var (scene, n) = ArrangeLayer(1000f, 400f, new BoxEl
        {
            AlignSelf = FlexAlign.Center, MaxWidth = 300f, Margin = new Edges4(24f, 0f, 24f, 0f),
            Children = [new BoxEl { Height = 20f }],
        });
        Assert.Equal(300f, scene.Bounds(n).W);   // was 952
        Assert.Equal(20f, scene.Bounds(n).H);
    }
}
