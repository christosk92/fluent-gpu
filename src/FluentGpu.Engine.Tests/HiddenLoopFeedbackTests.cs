using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>A render-owned row whose node cannot reach a pixel (an always-mounted busy bar parked at opacity 0) feeds back a
/// HIDDEN pose: the UI imports its timing and composes nothing, so a frame that presents for any other reason (a playing
/// meter) does not re-record and re-raster the invisible node's slice. A Done edge still reports its final value.</summary>
public sealed class HiddenLoopFeedbackTests
{
    private static (SceneStore Scene, NodeHandle Root, NodeHandle Bar, AnimEngine Animation) Fixture(bool loop = true)
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
        animation.Keyframes(bar, AnimChannel.TranslateX, [new(0f, 0f), new(1f, 100f, Easing.Linear)], 1000f, loop: loop);
        return (scene, root, bar, animation);
    }

    private static (SceneRecordingSnapshot Snapshot, RenderCompositorAnimations Renderer, CompositorAnimationSnapshot Desired) Adopt(
        SceneStore scene, AnimEngine animation, double nowMs)
    {
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, nowMs);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, nowMs);
        return (snapshot, renderer, desired);
    }

    [Fact]
    public void AHiddenLoopFeedsBackAHiddenPose_SoTheUiSceneStaysUnchanged()
    {
        var (scene, root, bar, animation) = Fixture();
        scene.Paint(root).Opacity = 0f;
        var (snapshot, renderer, _) = Adopt(scene, animation, 0);
        renderer.Tick(snapshot, 100);
        renderer.Tick(snapshot, 250);

        Assert.False(renderer.ChangedThisTick);
        var pose = Assert.Single(renderer.Feedback.ToArray());
        Assert.True(pose.Hidden);
        Assert.Equal(250f, pose.ElapsedMs, 3);    // the timing still advances

        scene.ClearTransformDirty();
        float before = scene.Paint(bar).LocalTransform.Dx;
        animation.ApplyCompositorFeedback(renderer.Feedback);
        Assert.Equal(before, scene.Paint(bar).LocalTransform.Dx);
        Assert.False(scene.AnyTransformWrote);
        snapshot.ReleaseResources();
    }

    [Fact]
    public void AHiddenRowOverAnAuthoredTransform_LeavesThatTransformUntouched()
    {
        // The meter's bar authors Scale(1, rest) under its ScaleY track: a hidden pose must not fold the track's value in.
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore { DeviceScale = 1.5f };
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Bounds(root) = new(0, 0, 100, 100);
        scene.Paint(root).VisualKind = VisualKind.Box;
        scene.Paint(root).Opacity = 0f;
        var bar = scene.CreateNode(2);
        scene.AppendChild(root, bar);
        scene.Bounds(bar) = new(0, 0, 2.5f, 13f);
        scene.Paint(bar).VisualKind = VisualKind.Box;
        scene.Paint(bar).LocalTransform = Affine2D.Scale(1f, 0.4f);
        var animation = new AnimEngine(scene) { RenderOwnsCompositor = true };
        animation.Keyframes(bar, AnimChannel.ScaleY, [new(0f, 0.35f, Easing.Linear), new(1f, 0.95f, Easing.Linear)], 850f,
            loop: true, snapToDevicePixels: true);
        var (snapshot, renderer, _) = Adopt(scene, animation, 0);
        renderer.Tick(snapshot, 100);
        renderer.Tick(snapshot, 300);

        scene.ClearTransformDirty();
        animation.ApplyCompositorFeedback(renderer.Feedback);
        Assert.Equal(0.4f, scene.Paint(bar).LocalTransform.M22);
        Assert.False(scene.AnyTransformWrote);
        snapshot.ReleaseResources();
    }

    [Fact]
    public void AHiddenFiniteRow_FeedsBackHiddenPoses_ThenItsDoneEdgeReportsTheFinalValue()
    {
        var (scene, root, bar, animation) = Fixture(loop: false);
        scene.Paint(root).Opacity = 0f;
        var (snapshot, renderer, _) = Adopt(scene, animation, 0);

        renderer.Tick(snapshot, 400);
        var mid = Assert.Single(renderer.Feedback.ToArray());
        Assert.True(mid.Hidden && !mid.Done);
        Assert.True(renderer.HasActive);          // a finite row still ticks to its Done
        animation.ApplyCompositorFeedback(renderer.Feedback);
        Assert.Equal(0f, scene.Paint(bar).LocalTransform.Dx);

        renderer.Tick(snapshot, 1100);
        var done = Assert.Single(renderer.Feedback.ToArray());
        Assert.True(done.Done && !done.Hidden);
        Assert.Equal(100f, done.Value);
        Assert.True(renderer.ChangedThisTick);    // the Done edge completes a UI lifecycle
        animation.ApplyCompositorFeedback(renderer.Feedback);
        Assert.Equal(100f, scene.Paint(bar).LocalTransform.Dx);
        snapshot.ReleaseResources();
    }

    [Fact]
    public void ShownAgain_TheLoopPosesAndFeedsBackWhereTimePutsIt()
    {
        var (scene, root, bar, animation) = Fixture();
        scene.Paint(root).Opacity = 0f;
        var (snapshot, renderer, desired) = Adopt(scene, animation, 0);
        renderer.Tick(snapshot, 200);
        animation.ApplyCompositorFeedback(renderer.Feedback);

        scene.Paint(root).Opacity = 1f;   // the parent shows (a publication)
        snapshot.Capture(scene);
        animation.CaptureCompositorAnimations(desired, 300);
        renderer.Adopt(desired, snapshot, 300);

        Assert.True(renderer.ChangedThisTick);
        Assert.Equal(30f, snapshot.Paint(bar).LocalTransform.Dx, 2);
        var pose = Assert.Single(renderer.Feedback.ToArray());
        Assert.False(pose.Hidden);
        Assert.Equal(30f, pose.Value, 2);
        animation.ApplyCompositorFeedback(renderer.Feedback);
        Assert.Equal(30f, scene.Paint(bar).LocalTransform.Dx, 2);
        snapshot.ReleaseResources();
    }

    [Fact]
    public void AVisibleLoopStillFeedsBackItsAdvancingValue()
    {
        var (scene, _, bar, animation) = Fixture();
        var (snapshot, renderer, _) = Adopt(scene, animation, 0);
        renderer.Tick(snapshot, 100);
        renderer.Tick(snapshot, 250);

        Assert.True(renderer.ChangedThisTick);
        var pose = Assert.Single(renderer.Feedback.ToArray());
        Assert.False(pose.Hidden);
        Assert.Equal(25f, pose.Value, 2);
        animation.ApplyCompositorFeedback(renderer.Feedback);
        Assert.Equal(25f, scene.Paint(bar).LocalTransform.Dx, 2);
        snapshot.ReleaseResources();
    }
}
