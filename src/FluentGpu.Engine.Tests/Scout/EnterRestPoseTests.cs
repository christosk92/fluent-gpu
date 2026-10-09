using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>An Enter animates FROM its terminal TO the node's AUTHORED static pose, not identity. The enter row
/// replace-folds the channel, and its settle leaves its last value in paint until the next reconcile. So the Queue's
/// dimmed Autoplay rows (Opacity 0.72 plus an Enter fade/slide) settled fully opaque, and an OffsetY card settled at 0.</summary>
public sealed class EnterRestPoseTests
{
    private static (SceneStore Scene, AnimEngine Anim, NodeHandle Node) Mount(Func<Action<NodeHandle>, BoxEl> make)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var anim = new AnimEngine(scene);
        var recon = new TreeReconciler(scene, new StringTable()) { Anim = anim };
        NodeHandle node = default;
        recon.ReconcileRoot(new BoxEl { Children = [make(n => node = n)] }, null);
        scene.Bounds(node) = new RectF(0f, 0f, 100f, 40f);
        return (scene, anim, node);
    }

    private static void Settle(AnimEngine anim)
    {
        for (int i = 0; i < 400 && anim.HasUiWork; i++) anim.Tick(16f);
        Assert.False(anim.HasUiWork);
    }

    [Fact]
    public void AFadeAndSlideEnterSettlesOnTheAuthoredOpacityAndOffset()
    {
        var (scene, anim, row) = Mount(realized => new BoxEl
        {
            Width = 100f, Height = 40f, Opacity = 0.72f, OffsetX = 20f, OffsetY = 10f,
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            OnRealized = realized,
        });

        anim.Tick(16f);   // the seed frame holds the terminal: transparent, 6px below the AUTHORED offset
        Assert.Equal(0f, scene.Paint(row).Opacity, 3);
        Assert.Equal(16f, scene.Paint(row).LocalTransform.Dy, 2);
        Assert.Equal(20f, scene.Paint(row).LocalTransform.Dx, 2);

        Settle(anim);
        Assert.Equal(0.72f, scene.Paint(row).Opacity, 3);          // was 1: the identity terminal outlived the settle
        Assert.Equal(10f, scene.Paint(row).LocalTransform.Dy, 2);  // was 0
        Assert.Equal(20f, scene.Paint(row).LocalTransform.Dx, 2);
    }

    [Fact]
    public void AScaleEnterSettlesOnTheAuthoredScale()
    {
        var (scene, anim, card) = Mount(realized => new BoxEl
        {
            Width = 100f, Height = 40f, ScaleX = 0.5f, ScaleY = 0.5f,
            Enter = new EnterExit(Sx: 0.9f, Sy: 0.9f, Active: true),
            OnRealized = realized,
        });

        Settle(anim);
        Affine2D tf = scene.Paint(card).LocalTransform;
        Assert.Equal(0.5f, tf.M11, 3);   // was 1
        Assert.Equal(0.5f, tf.M22, 3);
    }
}
