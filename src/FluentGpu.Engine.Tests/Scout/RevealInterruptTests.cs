using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>The host captures a bounds-animated node's LAYOUT size (AppHost.RelRect), which mid-flight is the previous
/// target, not what is on screen. A tween Reveal / ScaleCorrect interrupted by a new commit restarted from that size: a
/// SplitView pane reversed 100ms into opening popped to the full 320px for a frame before closing, and the ToggleSwitch
/// knob's hover-grow jumped to the hover size when pressed. The restart must depart from the presented extent.</summary>
public sealed class RevealInterruptTests
{
    private static readonly LayoutTransition Reveal200 = new(TransitionChannels.Size,
        TransitionDynamics.Tween(200f, Easing.Linear), SizeMode.Reveal);
    private static readonly LayoutTransition Scale300 = new(TransitionChannels.Size,
        TransitionDynamics.Tween(300f, Easing.Linear), SizeMode.ScaleCorrect);

    private static (SceneStore Scene, NodeHandle Node, AnimEngine Anim) Engine(float w, float h)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        var node = scene.CreateNode(2);
        scene.AppendChild(root, node);
        scene.Bounds(node) = new(0, 0, w, h);
        return (scene, node, new AnimEngine(scene));
    }

    [Fact]
    public void AReversedRevealTweenRestartsFromThePresentedWidth()
    {
        var (scene, node, anim) = Engine(320f, 40f);
        anim.AnimateBounds(node, new RectF(0, 0, 48, 40), new RectF(0, 0, 320, 40), Reveal200);
        for (int i = 0; i < 6; i++) anim.Tick(16.67f);
        float mid = scene.Paint(node).PresentedW;
        Assert.InRange(mid, 60f, 300f);

        // Close again: layout snaps to 48 and the host hands AnimateBounds the captured LAYOUT width (320).
        scene.Bounds(node) = new(0, 0, 48, 40);
        anim.AnimateBounds(node, new RectF(0, 0, 320, 40), new RectF(0, 0, 48, 40), Reveal200);
        anim.Tick(16.67f);   // the seed frame poses the restart's from value
        Assert.InRange(scene.Paint(node).PresentedW, mid - 0.5f, mid + 0.5f);

        float prev = scene.Paint(node).PresentedW;
        for (int i = 0; i < 20; i++)
        {
            anim.Tick(16.67f);
            float pw = scene.Paint(node).PresentedW;
            if (float.IsNaN(pw)) break;
            Assert.True(pw <= prev + 0.01f, $"the close grew back to {pw} from {prev}");
            prev = pw;
        }
    }

    [Fact]
    public void AnInterruptedScaleCorrectTweenRestartsFromThePresentedExtent()
    {
        var (scene, node, anim) = Engine(200f, 100f);
        anim.AnimateBounds(node, new RectF(0, 0, 100, 100), new RectF(0, 0, 200, 100), Scale300);
        for (int i = 0; i < 6; i++) anim.Tick(16.67f);
        float midW = 200f * scene.Paint(node).LocalTransform.M11;
        Assert.InRange(midW, 105f, 195f);

        // A second grow lands mid-flight (hover → press): the host captures the previous LAYOUT width (200).
        scene.Bounds(node) = new(0, 0, 300, 100);
        anim.AnimateBounds(node, new RectF(0, 0, 200, 100), new RectF(0, 0, 300, 100), Scale300);
        anim.Tick(16.67f);   // seed frame
        Assert.InRange(300f * scene.Paint(node).LocalTransform.M11, midW - 0.5f, midW + 0.5f);
    }
}
