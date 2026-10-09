using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A SPRING ScaleCorrect resize retargeted mid-flight: the host hands AnimateBounds the previous LAYOUT size, so
/// the ratio it passes is old layout / new layout, while the live ScaleX row holds a ratio of the OLD box. Spring's
/// retarget ignored the new ratio and kept the old-basis value against the new box: a 240→480 cover reversed at ~360 DIP
/// showed ~180 DIP for a frame, then sprang back up to 240 instead of shrinking from what was on screen.</summary>
public sealed class ScaleCorrectSpringRetargetTests
{
    private static readonly LayoutTransition Cover = new(TransitionChannels.Size,
        TransitionDynamics.Spring(0.45f, 0.90f), SizeMode.ScaleCorrect);

    [Fact]
    public void AReversedSpringScaleCorrectContinuesFromThePresentedExtent()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        var node = scene.CreateNode(2);
        scene.AppendChild(root, node);
        scene.Bounds(node) = new(0, 0, 480, 480);
        var anim = new AnimEngine(scene);

        anim.AnimateBounds(node, new RectF(0, 0, 240, 240), new RectF(0, 0, 480, 480), Cover);
        for (int i = 0; i < 8; i++) anim.Tick(16.67f);
        float mid = 480f * scene.Paint(node).LocalTransform.M11;
        Assert.InRange(mid, 300f, 440f);

        // The layout switches back: layout snaps to 240 and the host captures the previous LAYOUT width (480).
        scene.Bounds(node) = new(0, 0, 240, 240);
        anim.AnimateBounds(node, new RectF(0, 0, 480, 480), new RectF(0, 0, 240, 240), Cover);
        anim.Tick(16.67f);
        float after = 240f * scene.Paint(node).LocalTransform.M11;
        // One frame on from the presented ~360 (the outward velocity carries a little further), not a pop to ~180.
        Assert.InRange(after, mid - 5f, mid + 30f);

        for (int i = 0; i < 400 && anim.HasUiWork; i++) anim.Tick(16.67f);
        Assert.Equal(1f, scene.Paint(node).LocalTransform.M11, 2);
    }
}
