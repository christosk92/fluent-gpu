using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary><see cref="AnimEngine.SetHeld"/> and <see cref="AnimEngine.SetPaused"/> are two independent owners of one row:
/// resuming a pause never releases a hold, and releasing a hold never resumes a pause — on the UI tick and on the render
/// thread alike.</summary>
public sealed class HoldPauseTests
{
    const AnimChannel Ch = AnimChannel.TranslateX;

    private static (SceneStore Scene, NodeHandle Node, AnimEngine Animation) Fixture(bool renderOwns)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Bounds(root) = new(0, 0, 200, 100);
        scene.Paint(root).VisualKind = VisualKind.Box;
        var node = scene.CreateNode(2);
        scene.AppendChild(root, node);
        scene.Bounds(node) = new(0, 0, 10, 10);
        scene.Paint(node).VisualKind = VisualKind.Box;
        var animation = new AnimEngine(scene) { RenderOwnsCompositor = renderOwns };
        // 0 → 100 over 1000 ms, linear, looping: the value is a tenth of the phase in ms.
        animation.Keyframes(node, Ch, [new(0f, 0f, Easing.Linear), new(1f, 100f, Easing.Linear)], 1000f, loop: true);
        return (scene, node, animation);
    }

    [Fact]
    public void ResumingAPause_KeepsASeparateHold_OnTheUiTick()
    {
        var (_, node, animation) = Fixture(renderOwns: false);
        animation.Tick(16f);   // the seed frame holds t = 0
        animation.Tick(16f);
        animation.Tick(16f);
        Assert.True(animation.TryGetTrackValue(node, Ch, out float frozen));
        animation.SetHeld(node, Ch, true);     // owner A freezes the loop
        animation.SetPaused(node, Ch, true);   // owner B pauses it…
        animation.SetPaused(node, Ch, false);  // …and resumes: A's hold still stands
        for (int i = 0; i < 5; i++) animation.Tick(16f);
        Assert.True(animation.TryGetTrackValue(node, Ch, out float after));
        Assert.Equal(frozen, after);
    }

    [Fact]
    public void ResumingAPause_KeepsASeparateHold_OnTheRenderThread()
    {
        var (scene, node, animation) = Fixture(renderOwns: true);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        renderer.Tick(snapshot, 300);
        animation.ApplyCompositorFeedback(renderer.Feedback);
        float shown = snapshot.Paint(node).LocalTransform.Dx;
        Assert.Equal(30f, shown, 3);

        animation.SetHeld(node, Ch, true);
        animation.SetPaused(node, Ch, true);
        snapshot.Capture(scene);
        animation.CaptureCompositorAnimations(desired, 300);
        renderer.Adopt(desired, snapshot, 300);
        animation.SetPaused(node, Ch, false);
        snapshot.Capture(scene);
        animation.CaptureCompositorAnimations(desired, 2550);
        renderer.Adopt(desired, snapshot, 2550);
        Assert.Equal(shown, snapshot.Paint(node).LocalTransform.Dx);
        Assert.False(renderer.HasActive);   // still held: no frames
        renderer.Tick(snapshot, 2800);
        Assert.Equal(shown, snapshot.Paint(node).LocalTransform.Dx);
        snapshot.ReleaseResources();
    }

    [Fact]
    public void ReleasingAHoldNobodySet_KeepsThePause_OnTheRenderThread()
    {
        var (scene, node, animation) = Fixture(renderOwns: true);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        renderer.Tick(snapshot, 300);
        animation.ApplyCompositorFeedback(renderer.Feedback);
        float shown = snapshot.Paint(node).LocalTransform.Dx;

        animation.SetPaused(node, Ch, true);
        snapshot.Capture(scene);
        animation.CaptureCompositorAnimations(desired, 300);
        renderer.Adopt(desired, snapshot, 300);
        // A hold release that matches no SetHeld: the pause stands, its value and its phase both still.
        animation.SetHeld(node, Ch, false);
        snapshot.Capture(scene);
        animation.CaptureCompositorAnimations(desired, 2550);
        renderer.Adopt(desired, snapshot, 2550);
        Assert.Equal(shown, snapshot.Paint(node).LocalTransform.Dx);
        Assert.False(renderer.HasActive);
        renderer.Tick(snapshot, 2800);
        Assert.Equal(shown, snapshot.Paint(node).LocalTransform.Dx);

        // Resumed, it continues from the phase it stopped at (300 ms), not from the wall clock (2900 ms).
        animation.SetPaused(node, Ch, false);
        snapshot.Capture(scene);
        animation.CaptureCompositorAnimations(desired, 2900);
        renderer.Adopt(desired, snapshot, 2900);
        renderer.Tick(snapshot, 3000);
        Assert.Equal(40f, snapshot.Paint(node).LocalTransform.Dx, 3);
        snapshot.ReleaseResources();
    }
}
