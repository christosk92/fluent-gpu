using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A retarget that lands while a row is still in its start delay (an enter's Stagger / EnterExit.DelayMs) must
/// keep what is left of that delay. RetargetReflow re-seeded through Spring (whose retarget zeroes the delay) or Animate
/// (no delay), and ReframePosition's tween branch passed no delay either, so async content landing during the stagger
/// opened a reflow slot ahead of its own fade, and a sibling's commit slid a staggered enter in while it was invisible.</summary>
public sealed class RetargetKeepsStartDelayTests
{
    private const float Frame = 16f;

    private static (SceneStore Scene, AnimEngine Anim, NodeHandle Node) Mount(float w, float h)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        var node = scene.CreateNode(2);
        scene.AppendChild(root, node);
        scene.Bounds(node) = new RectF(0f, 0f, w, h);
        return (scene, new AnimEngine(scene), node);
    }

    public static TheoryData<bool> Dynamics => new() { false, true };

    [Theory]
    [MemberData(nameof(Dynamics))]
    public void ANaturalExtentRetargetDuringTheStaggerOpensTheSlotWithItsFade(bool spring)
    {
        var (scene, anim, node) = Mount(300f, 120f);
        scene.Layout(node).Height = float.NaN;               // declared auto
        var spec = new LayoutTransition(TransitionChannels.Bounds | TransitionChannels.Opacity,
            spring ? TransitionDynamics.Spring() : TransitionDynamics.Tween(300f, Easing.Linear), SizeMode.Reflow,
            Enter: new EnterExit(Opacity: 0f, Active: true, DelayMs: 200f), Axes: SizeAxes.Height);
        anim.SetTransition(node, spec);
        anim.SeedEnter(node, spec.Enter, spec);
        anim.SeedEnterReflow(node, horizontal: false, 300f, 120f);
        for (int i = 0; i < 3; i++) { anim.Tick(Frame); anim.ReflowRoots.Clear(); }

        // Async content lands 48ms into the 200ms stagger: the host re-aims the row at the new natural extent.
        anim.RetargetReflow(node, AnimChannel.LayoutH, 240f);

        int opened = -1, faded = -1;
        for (int i = 4; i <= 60; i++)
        {
            anim.Tick(Frame); anim.ReflowRoots.Clear();
            if (opened < 0 && scene.Layout(node).Height > 0.01f) opened = i;
            if (faded < 0 && scene.Paint(node).Opacity > 0.001f) faded = i;
        }
        Assert.True(faded > 0, "the delayed fade never started");
        Assert.Equal(faded, opened);                        // was 4/5: the slot opened ~150ms ahead of its fade
    }

    [Fact]
    public void ATweenFlipDuringTheStaggerStartsWithTheDelayedFade()
    {
        var (scene, anim, node) = Mount(100f, 40f);
        var spec = new LayoutTransition(TransitionChannels.Position, TransitionDynamics.Tween(300f, Easing.Linear));
        anim.SetTransition(node, spec);
        anim.SeedEnter(node, new EnterExit(Dy: 20f, Opacity: 0f, Active: true, DelayMs: 200f), spec);
        for (int i = 0; i < 3; i++) anim.Tick(Frame);
        Assert.Equal(20f, scene.Paint(node).LocalTransform.Dy, 1);

        // A sibling above shrank 24px mid-stagger: the node FLIPs from 24px lower, on top of its held enter offset.
        anim.AnimateBounds(node, new RectF(0f, 24f, 100f, 40f), new RectF(0f, 0f, 100f, 40f), spec);

        int moved = -1, faded = -1;
        for (int i = 4; i <= 60; i++)
        {
            anim.Tick(Frame);
            if (moved < 0 && scene.Paint(node).LocalTransform.Dy < 43.99f) moved = i;
            if (faded < 0 && scene.Paint(node).Opacity > 0.001f) faded = i;
        }
        Assert.True(faded > 0, "the delayed fade never started");
        Assert.Equal(faded, moved);                         // was 4: the slide ran out while the node was still invisible
        Assert.Equal(0f, scene.Paint(node).LocalTransform.Dy, 1);
    }
}
