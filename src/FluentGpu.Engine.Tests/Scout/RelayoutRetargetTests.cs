using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A SizeMode.Relayout row pins LayoutInput.Width to its interp every tick (AppHost.RunIncrementalLayout). A
/// commit that re-solves the parent without re-rendering the node — it was only shoved sideways — hands that pinned
/// interp back to AnimateBounds as both the captured and the solved width. That echo used to restart a zero-distance
/// tween (the resize froze mid-way) and stash the interp as the declared width, which settle then wrote back for good
/// (Wavee's Follow toggle stuck half-way between "Follow" and "Following").</summary>
public sealed class RelayoutRetargetTests
{
    private static readonly LayoutTransition Relayout260 = new(TransitionChannels.Size,
        TransitionDynamics.Tween(260f, Easing.Linear), SizeMode.Relayout);

    private static (SceneStore Scene, NodeHandle Node, AnimEngine Anim) Engine(float declaredW, float solvedW)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        var node = scene.CreateNode(2);
        scene.AppendChild(root, node);
        scene.Layout(node).Width = declaredW;
        scene.Bounds(node) = new(0, 0, solvedW, 32);
        return (scene, node, new AnimEngine(scene));
    }

    // One frame of the host loop for a Relayouting node: tick, then pin LayoutInput.Width to the presented interp as
    // RunIncrementalLayout does (the subtree re-solve then lays the node out at exactly that width).
    private static void Frame(SceneStore scene, NodeHandle node, AnimEngine anim)
    {
        anim.Tick(16.67f);
        float pw = scene.Paint(node).PresentedW;
        if (!float.IsNaN(pw))
        {
            scene.Layout(node).Width = pw;
            scene.Bounds(node) = scene.Bounds(node) with { W = pw };
        }
        anim.IncrementalRoots.Clear();
    }

    [Fact]
    public void AShoveMidFlightKeepsResizingAndSettlesBackToTheDeclaredAutoWidth()
    {
        var (scene, node, anim) = Engine(declaredW: float.NaN, solvedW: 160f);
        anim.AnimateBounds(node, new RectF(0, 0, 100, 32), new RectF(0, 0, 160, 32), Relayout260);
        for (int i = 0; i < 6; i++) Frame(scene, node, anim);
        float mid = scene.Paint(node).PresentedW;
        Assert.InRange(mid, 101f, 159f);
        Assert.Equal(mid, scene.Layout(node).Width);   // pinned by the host, as in the real frame loop

        // The parent re-solves without re-rendering the node: it moves 12px, its width is the pinned interp.
        anim.AnimateBounds(node, new RectF(0, 0, mid, 32), new RectF(12, 0, mid, 32), Relayout260);
        Frame(scene, node, anim);
        Frame(scene, node, anim);
        Assert.True(scene.Paint(node).PresentedW > mid + 1f, "the resize froze at the shove");

        for (int i = 0; i < 40; i++) Frame(scene, node, anim);
        Assert.True(float.IsNaN(scene.Paint(node).PresentedW));
        Assert.True(float.IsNaN(scene.Layout(node).Width), $"settle pinned Width={scene.Layout(node).Width}");
    }

    [Fact]
    public void AReDeclarationMidFlightIsWhatSettleRestores()
    {
        var (scene, node, anim) = Engine(declaredW: 160f, solvedW: 160f);
        anim.AnimateBounds(node, new RectF(0, 0, 100, 32), new RectF(0, 0, 160, 32), Relayout260);
        for (int i = 0; i < 6; i++) Frame(scene, node, anim);
        float mid = scene.Paint(node).PresentedW;

        // A re-render declares Width=140 (the reconciler's WriteColumns hand-off), and layout solves it.
        if (!anim.RecordDeclaredSize(node, AnimChannel.LayoutW, 140f)) scene.Layout(node).Width = 140f;
        anim.AnimateBounds(node, new RectF(0, 0, mid, 32), new RectF(0, 0, 140, 32), Relayout260);

        for (int i = 0; i < 40; i++) Frame(scene, node, anim);
        Assert.Equal(140f, scene.Layout(node).Width);
    }
}
