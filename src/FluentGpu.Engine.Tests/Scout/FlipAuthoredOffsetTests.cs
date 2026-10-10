using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A position FLIP settles on the node's AUTHORED static offset, not 0, and a structural snap lands it back on
/// its authored pose, not identity. The FLIP rows replace-fold TranslateX/Y over paint and a settle leaves their last
/// value there, so a moved OffsetY = -16 row jumped 16px on the first frame and then rested 16px off until its next
/// reconcile; a resize mid-FLIP dropped an authored Rotation the same way.</summary>
public sealed class FlipAuthoredOffsetTests
{
    private static (SceneStore Scene, AnimEngine Anim, NodeHandle Node, LayoutTransition Spec) Mount(TransitionDynamics dyn, float rotation = 0f)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var anim = new AnimEngine(scene);
        var recon = new TreeReconciler(scene, new StringTable()) { Anim = anim };
        var spec = new LayoutTransition(TransitionChannels.Position, dyn);
        NodeHandle node = default;
        recon.ReconcileRoot(new BoxEl
        {
            Children =
            [
                new BoxEl
                {
                    Width = 100f, Height = 40f, OffsetY = -16f, Rotation = rotation, Animate = spec,
                    OnRealized = n => node = n,
                },
            ],
        }, null);
        scene.Bounds(node) = new RectF(0f, 0f, 100f, 40f);
        return (scene, anim, node, spec);
    }

    // The node moved up 100px in its parent. The host's RelRect folds the authored Dy into both rects (ApplyProjections).
    private static void MoveUp100(AnimEngine anim, NodeHandle node, in LayoutTransition spec)
        => anim.AnimateBounds(node, new RectF(0f, 84f, 100f, 40f), new RectF(0f, -16f, 100f, 40f), spec);

    private static void Settle(AnimEngine anim)
    {
        for (int i = 0; i < 400 && anim.HasUiWork; i++) anim.Tick(16f);
        Assert.False(anim.HasUiWork);
    }

    private static float RotOf(in Affine2D m) => MathF.Atan2(m.M12, m.M11) * (180f / MathF.PI);

    [Fact]
    public void ASpringFlipStartsWhereTheNodeWasAndSettlesOnTheAuthoredOffset()
    {
        var (scene, anim, row, spec) = Mount(TransitionDynamics.Spring());
        MoveUp100(anim, row, spec);

        anim.Tick(16f);   // the seed frame shows the old presented spot: old slot + the authored offset
        Assert.Equal(84f, scene.Paint(row).LocalTransform.Dy, 1);   // was 100 (the offset was dropped)

        Settle(anim);
        Assert.Equal(-16f, scene.Paint(row).LocalTransform.Dy, 1);  // was 0
    }

    [Fact]
    public void ATweenFlipSettlesOnTheAuthoredOffset()
    {
        var (scene, anim, row, spec) = Mount(TransitionDynamics.Tween(200f, Easing.Linear));
        MoveUp100(anim, row, spec);
        anim.Tick(16f);
        Assert.Equal(84f, scene.Paint(row).LocalTransform.Dy, 1);

        Settle(anim);
        Assert.Equal(-16f, scene.Paint(row).LocalTransform.Dy, 1);  // was 0
    }

    [Fact]
    public void AStructuralSnapMidFlipLandsOnTheAuthoredPose()
    {
        var (scene, anim, row, spec) = Mount(TransitionDynamics.Spring(), rotation: 10f);
        MoveUp100(anim, row, spec);
        for (int i = 0; i < 3; i++) anim.Tick(16f);

        anim.SnapStructuralToLayout(row);   // an interactive resize lands mid-FLIP
        Affine2D tf = scene.Paint(row).LocalTransform;
        Assert.Equal(-16f, tf.Dy, 1);       // was 0 (identity)
        Assert.Equal(0f, tf.Dx, 1);
        Assert.Equal(10f, RotOf(tf), 1);    // was 0: the authored rotation was dropped
    }

    [Fact]
    public void AReflowShoveSnapLandsOnTheAuthoredOffset()
    {
        var (scene, anim, row, spec) = Mount(TransitionDynamics.Spring());
        MoveUp100(anim, row, spec);
        for (int i = 0; i < 3; i++) anim.Tick(16f);

        anim.SnapPositionToLayout(row);
        Assert.Equal(-16f, scene.Paint(row).LocalTransform.Dy, 1);  // was 0
    }
}
