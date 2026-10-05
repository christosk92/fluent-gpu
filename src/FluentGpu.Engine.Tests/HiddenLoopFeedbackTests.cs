using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>A render-owned loop whose node cannot reach a pixel (an always-mounted busy bar parked at opacity 0) feeds back
/// the value its node already shows: applied on the UI, that feedback writes NOTHING new into the scene, so a frame that
/// presents for any other reason (a playing meter) does not re-record and re-raster the invisible bar's slice.</summary>
public sealed class HiddenLoopFeedbackTests
{
    private static (SceneStore Scene, NodeHandle Root, NodeHandle Bar, AnimEngine Animation) Fixture()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Bounds(root) = new(0, 0, 1000, 1000);
        scene.Paint(root).VisualKind = VisualKind.Box;
        var bar = scene.CreateNode(2);
        scene.AppendChild(root, bar);
        scene.Bounds(bar) = new(0, 0, 8, 8);
        scene.Paint(bar).VisualKind = VisualKind.Box;
        var animation = new AnimEngine(scene) { RenderOwnsCompositor = true };
        animation.Keyframes(bar, AnimChannel.TranslateX, [new(0f, 0f), new(1f, 100f, Easing.Linear)], 1000f, loop: true);
        return (scene, root, bar, animation);
    }

    [Fact]
    public void AHiddenLoopFeedsBackTheValueItsNodeShows_SoTheUiSceneStaysUnchanged()
    {
        var (scene, root, bar, animation) = Fixture();
        scene.Paint(root).Opacity = 0f;
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        renderer.Tick(snapshot, 100);
        renderer.Tick(snapshot, 250);

        Assert.False(renderer.ChangedThisTick);
        var pose = Assert.Single(renderer.Feedback.ToArray());
        Assert.Equal(0f, pose.Value);             // what the node shows, not where time is (25)
        Assert.Equal(250f, pose.ElapsedMs, 3);    // the timing still advances

        float before = scene.Paint(bar).LocalTransform.Dx;
        animation.ApplyCompositorFeedback(renderer.Feedback);
        Assert.Equal(before, scene.Paint(bar).LocalTransform.Dx);
        snapshot.ReleaseResources();
    }

    [Fact]
    public void ShownAgain_TheLoopPosesAndFeedsBackWhereTimePutsIt()
    {
        var (scene, root, bar, animation) = Fixture();
        scene.Paint(root).Opacity = 0f;
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        renderer.Tick(snapshot, 200);
        animation.ApplyCompositorFeedback(renderer.Feedback);

        scene.Paint(root).Opacity = 1f;   // the parent shows (a publication)
        snapshot.Capture(scene);
        animation.CaptureCompositorAnimations(desired, 300);
        renderer.Adopt(desired, snapshot, 300);

        Assert.True(renderer.ChangedThisTick);
        Assert.Equal(30f, snapshot.Paint(bar).LocalTransform.Dx, 2);
        Assert.Equal(30f, Assert.Single(renderer.Feedback.ToArray()).Value, 2);
        animation.ApplyCompositorFeedback(renderer.Feedback);
        Assert.Equal(30f, scene.Paint(bar).LocalTransform.Dx, 2);
        snapshot.ReleaseResources();
    }

    [Fact]
    public void AVisibleLoopStillFeedsBackItsAdvancingValue()
    {
        var (scene, _, bar, animation) = Fixture();
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        renderer.Tick(snapshot, 100);
        renderer.Tick(snapshot, 250);

        Assert.True(renderer.ChangedThisTick);
        Assert.Equal(25f, Assert.Single(renderer.Feedback.ToArray()).Value, 2);
        animation.ApplyCompositorFeedback(renderer.Feedback);
        Assert.Equal(25f, scene.Paint(bar).LocalTransform.Dx, 2);
        snapshot.ReleaseResources();
    }
}
