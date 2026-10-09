using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>An additive (CompositeOp.Add) row folded onto the node's CURRENT paint, which already held last frame's
/// additive contribution: with no Replace row rewriting the channel it compounded every frame (a lone 0→20 slide ended
/// near 70, a lone additive fade collapsed to 0, a wobble over a settled one-shot base wandered off). An additive row
/// now composes onto the base it composed onto last frame.</summary>
public sealed class AdditiveBaseTests
{
    private const float Frame = 16.67f;

    private static (SceneStore Scene, NodeHandle Node, AnimEngine Anim) Engine()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new(0, 0, 100, 100);
        return (scene, node, new AnimEngine(scene));
    }

    private static void Run(AnimEngine anim, int frames)
    {
        for (int i = 0; i < frames; i++) anim.Tick(Frame);   // the first tick is the seed frame's hold
    }

    private static Keyframe[] Wobble() => [new(0f, -6f), new(0.5f, 6f, Easing.Linear), new(1f, -6f, Easing.Linear)];

    [Fact]
    public void ALoneAdditiveTranslateEndsAtItsTarget()
    {
        var (scene, node, anim) = Engine();
        anim.Animate(node, AnimChannel.TranslateX, 0f, 20f, 100f, Easing.Linear, CompositeOp.Add);
        Run(anim, 12);
        Assert.Equal(20f, scene.Paint(node).LocalTransform.Dx, 2);
    }

    [Fact]
    public void ALoneAdditiveTranslateComposesOntoTheStaticTransform()
    {
        var (scene, node, anim) = Engine();
        scene.Paint(node).LocalTransform = Affine2D.Translation(40f, 0f);
        anim.Animate(node, AnimChannel.TranslateX, 0f, 20f, 100f, Easing.Linear, CompositeOp.Add);
        Run(anim, 4);                                     // seed hold + 3 frames: 50 ms in → +10
        Assert.Equal(50f, scene.Paint(node).LocalTransform.Dx, 1);
        Run(anim, 8);
        Assert.Equal(60f, scene.Paint(node).LocalTransform.Dx, 2);
    }

    [Fact]
    public void ALoneAdditiveOpacityMultipliesTheBaseNotItsOwnLastFrame()
    {
        var (scene, node, anim) = Engine();
        anim.Animate(node, AnimChannel.Opacity, 0f, 1f, 100f, Easing.Linear, CompositeOp.Add);
        Run(anim, 12);
        Assert.Equal(1f, scene.Paint(node).Opacity, 3);
    }

    [Fact]
    public void AWobbleOverASettledOneShotBaseStaysAroundTheBase()
    {
        var (scene, node, anim) = Engine();
        anim.Animate(node, AnimChannel.TranslateX, 0f, 30f, 50f, Easing.Linear);   // Replace, settles and is freed
        anim.Keyframes(node, AnimChannel.TranslateX, Wobble(), 260f, loop: true, composite: CompositeOp.Add);
        Run(anim, 5);
        for (int i = 0; i < 120; i++)
        {
            anim.Tick(Frame);
            Assert.InRange(scene.Paint(node).LocalTransform.Dx, 23.99f, 36.01f);
        }
    }

    [Fact]
    public void ALaterAdditiveRowComposesOntoTheSameBaseAndItsEndValueHolds()
    {
        var (scene, node, anim) = Engine();
        anim.Keyframes(node, AnimChannel.TranslateX, Wobble(), 260f, loop: true, composite: CompositeOp.Add);
        Run(anim, 10);
        anim.Animate(node, AnimChannel.TranslateX, 0f, 10f, 100f, Easing.Linear, CompositeOp.Add);   // prepended on the chain
        Run(anim, 10);                                    // the nudge settles and is freed
        for (int i = 0; i < 60; i++)
        {
            anim.Tick(Frame);
            Assert.InRange(scene.Paint(node).LocalTransform.Dx, 3.99f, 16.01f);   // 10 + wobble
        }
    }
}
