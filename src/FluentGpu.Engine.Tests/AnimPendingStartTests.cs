using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Visual-continuity audit S2 #5: an enter transition seeded during a commit started its clock at the commit,
/// so when the incoming page's mount made the frame 40-60 ms long, its first PRESENTED frame already showed a big slice
/// of the slide/fade. The start now resolves at the first presented frame (Web Animations' pending play task — Gecko
/// Animation::Tick/TryTriggerNow; Flutter's Ticker takes the first vsync after start() as elapsed 0), on both the UI
/// scheduler and the render-thread compositor.</summary>
public sealed class AnimPendingStartTests
{
    private static readonly LayoutTransition Fade200 = new(TransitionChannels.Opacity,
        TransitionDynamics.Tween(200f, Easing.Linear), Enter: new EnterExit(Opacity: 0f, Active: true));

    [Theory]
    [InlineData(16.67f, 16.67f, 16.67f)]   // steady frames: unchanged
    [InlineData(60f, 16.67f, 16.67f)]      // the held (commit) frame ran long: one steady frame
    [InlineData(30f, 16.67f, 30f)]         // under two frames: taken as presented time
    [InlineData(60f, 0f, 60f)]             // no reference (a fast-forward hold): raw step
    [InlineData(500f, 8.33f, 8.33f)]       // 120 Hz
    [InlineData(60f, 50f, 60f)]            // a hold step past the clock clamp is no steady reference
    public void PendingStartStep_CapsOnlyASpanThatOutlastsTwoSteadyFrames(float step, float held, float expected)
        => Assert.Equal(expected, AnimEngine.PendingStartStep(step, held), 3);

    private static (SceneStore Scene, NodeHandle Node, AnimEngine Anim) Engine(bool renderOwns = false)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new(0, 0, 100, 100);
        return (scene, node, new AnimEngine(scene) { RenderOwnsCompositor = renderOwns });
    }

    [Fact]
    public void UiScheduler_AnEntranceSeededInALongCommitFrameStartsAtItsBeginning()
    {
        var (scene, node, anim) = Engine();
        anim.SeedEnter(node, Fade200.Enter, Fade200);
        anim.Tick(16.67f);                              // the commit frame: held at t=0 — the first presented pose
        Assert.Equal(0f, scene.Paint(node).Opacity, 3);
        anim.Tick(60f);                                 // the long frame the mount caused
        Assert.Equal(16.67f / 200f, scene.Paint(node).Opacity, 3);
        anim.Tick(16.67f);                              // afterwards: ordinary steps
        Assert.Equal(33.34f / 200f, scene.Paint(node).Opacity, 3);
    }

    [Fact]
    public void UiScheduler_APlainAnimateKeepsRawDt()
    {
        var (scene, node, anim) = Engine();
        anim.Animate(node, AnimChannel.Opacity, 0f, 1f, 200f, Easing.Linear);
        anim.Tick(16.67f);
        anim.Tick(60f);
        Assert.Equal(60f / 200f, scene.Paint(node).Opacity, 3);
    }

    [Fact]
    public void RenderCompositor_HoldsTheFirstPoseThenResolvesTheStartFromIt()
    {
        var (scene, node, anim) = Engine(renderOwns: true);
        anim.SeedEnter(node, Fade200.Enter, Fade200);
        var desired = new CompositorAnimationSnapshot();
        anim.CaptureCompositorAnimations(desired, 0);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var renderer = new RenderCompositorAnimations();

        renderer.Adopt(desired, snapshot, 0);           // the render frame that first poses the entrance: t=0
        Assert.Equal(0f, snapshot.Paint(node).Opacity, 3);
        renderer.Tick(snapshot, 60);                    // that frame recorded/presented for 60 ms: still one steady step
        Assert.Equal(AnimClock.DefaultDeltaMs / 200f, snapshot.Paint(node).Opacity, 3);
        renderer.Tick(snapshot, 60 + 16.67);
        Assert.Equal((AnimClock.DefaultDeltaMs + 16.67f) / 200f, snapshot.Paint(node).Opacity, 3);
    }

    [Fact]
    public void RenderCompositor_AQuickFirstFrameIsPresentedTime()
    {
        var (scene, node, anim) = Engine(renderOwns: true);
        anim.SeedEnter(node, Fade200.Enter, Fade200);
        var desired = new CompositorAnimationSnapshot();
        anim.CaptureCompositorAnimations(desired, 0);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        renderer.Tick(snapshot, 10);
        Assert.Equal(10f / 200f, snapshot.Paint(node).Opacity, 3);
    }
}
