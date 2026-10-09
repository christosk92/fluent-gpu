using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A mirrored node (ScaleX/ScaleY = -1) must keep its mirror when any animation row composes it. The fold
/// decomposed |scale| + atan2 and so read Scale(-1,1) as a 180° turn: an opacity fade re-composed diag(-1,-1) and the
/// glyph stood upside down until the next re-render, on the UI scheduler and on the render-thread compositor alike.</summary>
public sealed class MirroredTransformFoldTests
{
    private static readonly LayoutTransition Fade200 = new(TransitionChannels.Opacity,
        TransitionDynamics.Tween(200f, Easing.Linear), Enter: new EnterExit(Opacity: 0f, Active: true));

    private static (SceneStore Scene, NodeHandle Node, AnimEngine Anim) Engine(in Affine2D tf, bool renderOwns = false)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new(0, 0, 100, 100);
        scene.Paint(node).LocalTransform = tf;
        return (scene, node, new AnimEngine(scene) { RenderOwnsCompositor = renderOwns });
    }

    private static void AssertLinear(in Affine2D expected, in Affine2D actual)
    {
        Assert.Equal(expected.M11, actual.M11, 4);
        Assert.Equal(expected.M12, actual.M12, 4);
        Assert.Equal(expected.M21, actual.M21, 4);
        Assert.Equal(expected.M22, actual.M22, 4);
    }

    [Theory]
    [InlineData(-1f, 1f)]
    [InlineData(1f, -1f)]
    [InlineData(-2f, 0.5f)]
    public void UiScheduler_AnOpacityFadeKeepsTheMirror(float sx, float sy)
    {
        var mirror = Affine2D.Scale(sx, sy);
        var (scene, node, anim) = Engine(in mirror);
        anim.Animate(node, AnimChannel.Opacity, 0f, 1f, 200f, Easing.Linear);
        anim.Tick(16.67f);
        anim.Tick(16.67f);
        AssertLinear(in mirror, in scene.Paint(node).LocalTransform);
    }

    [Fact]
    public void ApplyImmediate_KeepsARotatedMirror()
    {
        var mirror = Affine2D.Rotation(30f * MathF.PI / 180f).Multiply(Affine2D.Scale(-2f, 1f));
        var (scene, node, anim) = Engine(in mirror);
        anim.ApplyImmediate(node, AnimChannel.Opacity, 0.5f);
        AssertLinear(in mirror, in scene.Paint(node).LocalTransform);
    }

    [Fact]
    public void ApplyImmediate_ZeroRotationOnAnXMirrorLeavesItMirrored()
    {
        var mirror = Affine2D.Scale(-1f, 1f);
        var (scene, node, anim) = Engine(in mirror);
        anim.ApplyImmediate(node, AnimChannel.Rotation, 0f);   // the mirror carries no rotation: nothing to undo
        AssertLinear(in mirror, in scene.Paint(node).LocalTransform);
    }

    [Fact]
    public void RenderCompositor_AnEntranceFadeKeepsTheMirror()
    {
        var mirror = Affine2D.Scale(-1f, 1f);
        var (scene, node, anim) = Engine(in mirror, renderOwns: true);
        anim.SeedEnter(node, Fade200.Enter, Fade200);
        var desired = new CompositorAnimationSnapshot();
        anim.CaptureCompositorAnimations(desired, 0);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        renderer.Tick(snapshot, 16.67);
        AssertLinear(in mirror, in snapshot.Paint(node).LocalTransform);
    }
}
