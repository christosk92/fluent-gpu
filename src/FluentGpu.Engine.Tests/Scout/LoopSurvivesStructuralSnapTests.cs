using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A looping row is authored continuous motion, not a stale projection. A KeepAlive reactivation runs
/// <c>SnapStructuralToLayout</c> over the page, which freed every Scale/Translate row including the render-thread loop
/// track of the now-playing meter: the three bars froze at the authored rest scale, or at whatever the last composed
/// value was, until something reseeded them. Both snaps skip <see cref="AnimFlags.Loop"/> rows; finite rows snap as
/// before.</summary>
public sealed class LoopSurvivesStructuralSnapTests
{
    private const float RestScale = 0.4f;
    private const float LoopScale = 0.9f;

    private static (SceneStore Scene, AnimEngine Anim, NodeHandle Node) Mount()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var anim = new AnimEngine(scene);
        var recon = new TreeReconciler(scene, new StringTable()) { Anim = anim };
        NodeHandle node = default;
        recon.ReconcileRoot(new BoxEl
        {
            Children =
            [
                new BoxEl
                {
                    Width = 2.5f, Height = 40f, Transform = Affine2D.Scale(1f, RestScale),
                    OnRealized = n => node = n,
                },
            ],
        }, null);
        scene.Bounds(node) = new RectF(0f, 0f, 2.5f, 40f);
        return (scene, anim, node);
    }

    // A flat track: every key is the same value, so the sampled scale does not depend on the loop phase.
    private static Keyframe[] Flat(float v) => [new(0f, v, Easing.Linear), new(1f, v, Easing.Linear)];

    [Fact]
    public void ALoopingScaleTrackSurvivesAStructuralSnap()
    {
        var (scene, anim, node) = Mount();
        anim.Keyframes(node, AnimChannel.ScaleY, Flat(LoopScale), 850f, loop: true);
        for (int i = 0; i < 4; i++) anim.Tick(16f);
        Assert.Equal(LoopScale, scene.Paint(node).LocalTransform.M22, 2);
        Assert.Equal(1, anim.LoopCount);

        anim.SnapStructuralToLayout(node);   // the keep-alive un-park

        Assert.Equal(1, anim.LoopCount);                                          // the track was not freed
        Assert.Equal(LoopScale, scene.Paint(node).LocalTransform.M22, 2);         // and the pose was not reset to rest
        anim.Tick(16f);
        Assert.Equal(LoopScale, scene.Paint(node).LocalTransform.M22, 2);         // it keeps running
    }

    [Fact]
    public void AFiniteScaleRowIsStillSnapped()
    {
        var (scene, anim, node) = Mount();
        anim.Keyframes(node, AnimChannel.ScaleY, Flat(LoopScale), 5000f);
        for (int i = 0; i < 4; i++) anim.Tick(16f);
        Assert.Equal(LoopScale, scene.Paint(node).LocalTransform.M22, 2);

        anim.SnapStructuralToLayout(node);

        Assert.False(anim.HasActive);                                             // the finite row was freed
        Assert.Equal(1f, scene.Paint(node).LocalTransform.M22, 2);                // reset to the node's rest pose (no rest registered here: identity)
    }

    [Fact]
    public void ALoopingTranslateSurvivesSnapPositionToLayout()
    {
        var (scene, anim, node) = Mount();
        anim.Keyframes(node, AnimChannel.TranslateY, Flat(7f), 850f, loop: true);
        for (int i = 0; i < 4; i++) anim.Tick(16f);
        Assert.Equal(7f, scene.Paint(node).LocalTransform.Dy, 1);

        anim.SnapPositionToLayout(node);

        Assert.Equal(1, anim.LoopCount);
        Assert.Equal(7f, scene.Paint(node).LocalTransform.Dy, 1);
    }
}
