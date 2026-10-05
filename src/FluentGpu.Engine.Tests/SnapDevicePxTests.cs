using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary><see cref="AnimFlags.SnapDevicePx"/>: a ScaleX/ScaleY keyframe track poses whole device pixels of its node's own
/// extent — on the render thread, through the compositor feedback and on the UI tick alike — so a looping meter steps a
/// pixel at a time and a frame in which no bar crosses a pixel is no change at all.</summary>
public sealed class SnapDevicePxTests
{
    const float HeightDip = 13f, Scale = 1.5f;   // 19.5 device px

    static float Snapped(float v) => MathF.Round(v * HeightDip * Scale) / (HeightDip * Scale);

    private static (SceneStore Scene, NodeHandle Bar, AnimEngine Animation) Fixture(bool renderOwns)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore { DeviceScale = Scale };
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Bounds(root) = new(0, 0, 100, 100);
        scene.Paint(root).VisualKind = VisualKind.Box;
        var bar = scene.CreateNode(2);
        scene.AppendChild(root, bar);
        scene.Bounds(bar) = new(0, 0, 2.5f, HeightDip);
        scene.Paint(bar).VisualKind = VisualKind.Box;
        var animation = new AnimEngine(scene) { RenderOwnsCompositor = renderOwns };
        // 0 → 1 over 1000 ms, linear: 19.5 px of travel, one pixel every ~51 ms.
        animation.Keyframes(bar, AnimChannel.ScaleY, [new(0f, 0f, Easing.Linear), new(1f, 1f, Easing.Linear)], 1000f,
            loop: true, snapToDevicePixels: true);
        return (scene, bar, animation);
    }

    [Theory]
    [InlineData(AnimChannel.ScaleY, 0.37f, 13f, 1.5f, 7f / 19.5f)]
    [InlineData(AnimChannel.ScaleX, 0.5f, 2.5f, 1.25f, 2f / 3.125f)]
    [InlineData(AnimChannel.ScaleY, 0.37f, 0.5f, 1f, 0.37f)]          // an extent of a pixel or less: unsnapped
    [InlineData(AnimChannel.TranslateX, 3.3f, 13f, 1.5f, 3.3f)]       // not a scale channel: unsnapped
    public void SnapToDevicePixels_RoundsTheScaledExtentToWholeDevicePixels(AnimChannel channel, float value, float extent,
        float scale, float expected)
        => Assert.Equal(expected, AnimEngine.SnapToDevicePixels(channel, value, extent, scale), 5);

    [Fact]
    public void TheRenderThreadPosesWholePixels_AndASubPixelStepIsNoChange()
    {
        var (scene, bar, animation) = Fixture(renderOwns: true);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);

        renderer.Tick(snapshot, 300);   // 0.300 → 5.85 px → 6 px
        Assert.True(renderer.ChangedThisTick);
        Assert.Equal(Snapped(0.3f), snapshot.Paint(bar).LocalTransform.M22, 5);
        renderer.Tick(snapshot, 310);   // 0.310 → 6.045 px → still 6 px: the same pose
        Assert.False(renderer.ChangedThisTick);
        Assert.True(renderer.HasActive);
        renderer.Tick(snapshot, 340);   // 0.340 → 6.63 px → 7 px
        Assert.True(renderer.ChangedThisTick);
        Assert.Equal(7f / 19.5f, snapshot.Paint(bar).LocalTransform.M22, 5);

        // The feedback carries the raw track value; the UI composes the same pixel the render thread posed.
        animation.ApplyCompositorFeedback(renderer.Feedback);
        Assert.Equal(7f / 19.5f, scene.Paint(bar).LocalTransform.M22, 5);
        snapshot.ReleaseResources();
    }

    [Fact]
    public void TheUiTickPosesTheSameWholePixels()
    {
        var (scene, bar, animation) = Fixture(renderOwns: false);
        animation.Tick(16f);    // the seed frame holds t = 0
        for (int i = 0; i < 20; i++)
        {
            animation.Tick(16f);
            Assert.True(animation.TryGetTrackValue(bar, AnimChannel.ScaleY, out float raw));
            Assert.Equal(Snapped(raw), scene.Paint(bar).LocalTransform.M22, 5);
        }
    }

    [Fact]
    public void WithoutTheFlag_TheTrackPosesItsExactValue()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore { DeviceScale = Scale };
        var bar = scene.CreateNode(1);
        scene.Root = bar;
        scene.Bounds(bar) = new(0, 0, 2.5f, HeightDip);
        var animation = new AnimEngine(scene);
        animation.Keyframes(bar, AnimChannel.ScaleY, [new(0f, 0f, Easing.Linear), new(1f, 1f, Easing.Linear)], 1000f, loop: true);
        animation.Tick(16f);
        animation.Tick(16f);
        Assert.True(animation.TryGetTrackValue(bar, AnimChannel.ScaleY, out float raw));
        Assert.Equal(raw, scene.Paint(bar).LocalTransform.M22, 6);
    }
}
