using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A spring seeded on a channel whose row was last a scroll-driven <c>Drive()</c> kept <c>AnimFlags.Driven</c>:
/// the census read it as signal-woken (never timer-due) and the compositor refused it, so on an idle page the spring froze
/// mid-flight and stayed UI-owned.</summary>
public sealed class SpringAfterDriveTests
{
    private static (SceneStore Scene, NodeHandle Node, AnimEngine Anim) Engine(bool renderOwns = false)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new(0, 0, 100, 100);
        return (scene, node, new AnimEngine(scene) { RenderOwnsCompositor = renderOwns });
    }

    private static void DriveOpacity(AnimEngine anim, NodeHandle node)
        => anim.Drive(node, AnimChannel.Opacity, [new Keyframe(0f, 0f), new Keyframe(1f, 1f)],
                      anim.Clocks.Register(() => 0f), 0f, 100f);

    [Fact]
    public void ASpringOverADrivenRow_IsTimerDueUntilItSettles()
    {
        var (_, node, anim) = Engine();
        DriveOpacity(anim, node);
        anim.Tick(16.67f);
        Assert.True(float.IsPositiveInfinity(anim.NextDueMs(0d)));   // the driven row alone is woken by its signal

        anim.Spring(node, AnimChannel.Opacity, 1f, SpringParams.Default, initial: 0f);
        Assert.Equal(0f, anim.NextDueMs(0d));                        // the spring owes its frames to the clock
        anim.Tick(16.67f);                                           // seed frame
        anim.Tick(16.67f);
        Assert.Equal(0f, anim.NextDueMs(0d));                        // still in flight: still timer-due
    }

    [Fact]
    public void ASpringOverADrivenRow_IsHandedToTheCompositor()
    {
        var (_, node, anim) = Engine(renderOwns: true);
        DriveOpacity(anim, node);
        Assert.True(anim.HasUiWork);                                 // a driven row stays UI-owned

        anim.Spring(node, AnimChannel.Opacity, 1f, SpringParams.Default, initial: 0f);
        Assert.False(anim.HasUiWork);                                // a plain opacity spring is render-thread owned
    }
}
