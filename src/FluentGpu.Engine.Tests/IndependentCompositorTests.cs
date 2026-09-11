using System.Diagnostics;
using System.Threading;
using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

public sealed class IndependentCompositorTests
{
    [Fact]
    public void RenderThreadAdvancesTwoHundredMillisecondsWhileUiWaits()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new(0, 0, 100, 100);
        var animations = new AnimEngine(scene) { RenderOwnsCompositor = true };
        animations.Animate(node, AnimChannel.Opacity, 0, 1, 250, Easing.Linear);
        var desired = new CompositorAnimationSnapshot();
        animations.CaptureCompositorAnimations(desired, 0);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var renderer = new RenderCompositorAnimations();
        var publisher = new SceneFramePublisher();
        using var reached = new ManualResetEventSlim();
        int ticks = 0, adopted = 0;
        bool ticking = true;
        float opacity = 0;
        using var thread = new RenderThread(publisher, _ =>
        {
            renderer.Adopt(desired, snapshot, 0);
            adopted++;
        }, async: true,
        needsTick: () => adopted != 0 && ticking,
        tick: () =>
        {
            // A deterministic render clock, paced by actual render turns; the UI does no work here.
            renderer.Tick(snapshot, ++ticks * 10);
            if (ticks == 20)
            {
                opacity = snapshot.Paint(node).Opacity;
                ticking = false;
                reached.Set();
            }
        }, tickPeriod: () => Stopwatch.Frequency / 100);
        publisher.Publish([1], default, default);
        thread.WakeAsync();
        Assert.True(reached.Wait(TimeSpan.FromSeconds(5)), "Render clock stalled behind the waiting UI thread.");
        Assert.Equal(.8f, opacity, precision: 5);
        Assert.Equal(20, ticks);
        Assert.Equal(1, adopted);
        Assert.Equal(1UL, publisher.LastConsumedSeq); // motion is not a new scene consume/quarantine tick
        Assert.Equal(1f, scene.Paint(node).Opacity);  // render never writes the live scene
        Assert.False(animations.HasUiWork);
    }
}
