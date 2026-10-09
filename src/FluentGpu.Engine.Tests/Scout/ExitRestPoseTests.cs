using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>An Exit's terminal is relative to the node's AUTHORED pose (offset/blur add, scale/opacity multiply), the
/// mirror of the Enter. The exit rows replace-fold over paint, so a raw terminal was absolute: a dimmed row under a
/// slide-only Exit brightened to opacity 1 as it left, an OffsetX 24 node with Exit Dx 24 did not move, and an
/// authored ScaleX 1.2 with Exit Sx 0.95 shrank to 0.95.</summary>
public sealed class ExitRestPoseTests
{
    private static readonly TransitionDynamics Linear200 = TransitionDynamics.Tween(200f, Easing.Linear);

    private static (SceneStore Scene, AnimEngine Anim, NodeHandle Node) MountThenRemove(Func<Action<NodeHandle>, BoxEl> make)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var anim = new AnimEngine(scene);
        var recon = new TreeReconciler(scene, new StringTable()) { Anim = anim };
        NodeHandle node = default;
        var first = new BoxEl { Children = [make(n => node = n)] };
        recon.ReconcileRoot(first, null);
        scene.Bounds(node) = new RectF(0f, 0f, 100f, 40f);
        recon.ReconcileRoot(new BoxEl(), first);   // Remove → orphan + SeedExit
        Assert.True(scene.IsLive(node));
        return (scene, anim, node);
    }

    [Fact]
    public void ASlideOnlyExitKeepsTheAuthoredOpacityAndMovesRelativeToTheAuthoredOffset()
    {
        var (scene, anim, row) = MountThenRemove(realized => new BoxEl
        {
            Width = 100f, Height = 40f, Opacity = 0.6f, OffsetX = 24f, OffsetY = 10f,
            Animate = new LayoutTransition(TransitionChannels.Opacity, Linear200,
                Exit: new EnterExit(Dx: 24f, Dy: -14f, Active: true)),
            OnRealized = realized,
        });

        float maxOpacity = 0f;
        for (int i = 0; i < 400 && anim.HasUiWork; i++)
        {
            anim.Tick(16f);
            maxOpacity = MathF.Max(maxOpacity, scene.Paint(row).Opacity);
        }
        Assert.False(anim.HasUiWork);
        Assert.True(maxOpacity <= 0.601f, $"exit brightened to {maxOpacity:0.000}");   // was 1
        Assert.Equal(0.6f, scene.Paint(row).Opacity, 3);
        Assert.Equal(48f, scene.Paint(row).LocalTransform.Dx, 1);   // was 24: never moved
        Assert.Equal(-4f, scene.Paint(row).LocalTransform.Dy, 1);   // was -14: travelled 24 DIP
    }

    [Fact]
    public void AScaleExitShrinksRelativeToTheAuthoredScale()
    {
        var (scene, anim, card) = MountThenRemove(realized => new BoxEl
        {
            Width = 100f, Height = 40f, ScaleX = 1.2f, ScaleY = 1.2f,
            Animate = new LayoutTransition(TransitionChannels.Opacity, Linear200,
                Exit: new EnterExit(Sx: 0.95f, Sy: 0.95f, Opacity: 0f, Active: true)),
            OnRealized = realized,
        });

        for (int i = 0; i < 400 && anim.HasUiWork; i++) anim.Tick(16f);
        Assert.False(anim.HasUiWork);
        Affine2D tf = scene.Paint(card).LocalTransform;
        Assert.Equal(1.14f, tf.M11, 3);   // was 0.95
        Assert.Equal(1.14f, tf.M22, 3);
    }
}
