using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A fresh scale row seeded with no explicit start reads the node's live scale. It used to read the matrix
/// diagonal, which on a rotated node is scale·cos(rotation): a tilted card (Rotation -11) that grows on hover first
/// popped down to 0.982 and only then grew, on every hover edge.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class RotatedScaleSeedTests
{
    private static readonly MotionTokenDef Linear250 = MotionTokenDef.Eased(250f, Easing.Linear, ReducedMotionPolicy.Exempt);

    private static (SceneStore Scene, AnimEngine Anim, NodeHandle Node) Mount(MotionTarget? whileHover)
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
                    Width = 160f, Height = 160f, Rotation = -11f,
                    WhileHover = whileHover, Transition = Linear250,
                    OnRealized = n => node = n,
                },
            ],
        }, null);
        scene.Bounds(node) = new RectF(0f, 0f, 160f, 160f);
        return (scene, anim, node);
    }

    // Compose builds Translation * Rotation * Scale, so each scale is its matrix column's length.
    private static float ScaleXOf(in Affine2D m) => MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
    private static float ScaleYOf(in Affine2D m) => MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22);

    [Fact]
    public void AHoverGrowOnATiltedCardNeverShrinksFirst()
    {
        var (scene, anim, card) = Mount(new MotionTarget { Scale = 1.05f });
        Assert.Equal(1f, ScaleXOf(scene.Paint(card).LocalTransform), 3);

        anim.ApplyInteractionEdge(card, AnimEngine.InteractKind.Hover, true);
        float min = float.MaxValue;
        for (int i = 0; i < 400 && anim.HasUiWork; i++)
        {
            anim.Tick(16f);
            min = MathF.Min(min, MathF.Min(ScaleXOf(scene.Paint(card).LocalTransform), ScaleYOf(scene.Paint(card).LocalTransform)));
        }
        Assert.True(min > 0.999f, $"the hover dipped to {min:0.0000} before growing");   // was 0.982 = cos 11°
        Assert.Equal(1.05f, ScaleXOf(scene.Paint(card).LocalTransform), 3);
        Assert.Equal(-11f, MathF.Atan2(scene.Paint(card).LocalTransform.M12, scene.Paint(card).LocalTransform.M11) * (180f / MathF.PI), 2);
    }

    [Fact]
    public void ASpringSeedWithNoFromStartsAtTheTiltedCardsRealScale()
    {
        var (scene, anim, card) = Mount(whileHover: null);
        anim.SeedValue(card, AnimChannel.ScaleY, 1f, MotionTokenDef.SpringOf(SpringParams.FromResponse(0.3f, 1f), ReducedMotionPolicy.Exempt));
        anim.Tick(16f);
        Assert.Equal(1f, ScaleYOf(scene.Paint(card).LocalTransform), 3);   // was 0.982: sprang back up from cos 11°
    }
}
