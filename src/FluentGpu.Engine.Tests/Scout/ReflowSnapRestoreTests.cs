using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>Under reduced motion ApplyProjections rewrites every projection to a 1ms tween, but a SizeMode.Reflow ENTER
/// row is seeded at its authored duration and writes its interp into LayoutInput.Height each tick. A commit that only
/// MOVED the node (a sibling above changed height) used to hit ReflowSize's snap branch, which dropped the row without
/// settle-restoring the declared size, so the node stayed frozen at its mid-reveal height until it was re-rendered.</summary>
public sealed class ReflowSnapRestoreTests
{
    private static readonly LayoutTransition Reflow300 = new(TransitionChannels.Bounds,
        TransitionDynamics.Tween(300f, Easing.Linear), SizeMode.Reflow, Axes: SizeAxes.Height);

    [Fact]
    public void AReducedMotionSnapOfAnInFlightEnterReflowRestoresTheDeclaredAutoHeight()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        var node = scene.CreateNode(2);
        scene.AppendChild(root, node);
        scene.Layout(node).Height = float.NaN;               // declared auto
        scene.Bounds(node) = new(0, 0, 300, 180);           // solved natural size
        var anim = new AnimEngine(scene);
        anim.SetTransition(node, Reflow300);
        anim.SeedEnterReflow(node, horizontal: false, 300f, 180f);
        for (int i = 0; i < 4; i++) { anim.Tick(16.67f); anim.ReflowRoots.Clear(); }
        float mid = scene.Layout(node).Height;
        Assert.InRange(mid, 1f, 179f);
        Assert.True(anim.HasEngineOwnedClip(node));
        anim.ConsumeReflowWrites();

        // A sibling's commit moves the node 24px; ApplyProjections under reduced motion hands AnimateBounds a 1ms tween.
        var reduced = Reflow300 with { Dynamics = TransitionDynamics.Tween(1f, Easing.Linear) };
        anim.AnimateBounds(node, new RectF(0, 0, 300, mid), new RectF(0, 24, 300, mid), reduced);

        Assert.True(float.IsNaN(scene.Layout(node).Height), $"snap froze Height={scene.Layout(node).Height}");
        Assert.False(anim.HasEngineOwnedClip(node));
        Assert.True(anim.ConsumeReflowWrites(), "the host must re-solve the restored size this frame");
        for (int i = 0; i < 20; i++) anim.Tick(16.67f);
        Assert.True(float.IsNaN(scene.Layout(node).Height));
    }
}
