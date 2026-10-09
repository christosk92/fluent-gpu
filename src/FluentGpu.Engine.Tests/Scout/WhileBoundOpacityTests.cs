using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A While* edge seeds only the channels its targets declare. It used to seed every gesture channel over the
/// stashed rest pose, and a BOUND Opacity rests at 1 there (its bind effect owns the channel). So a press-only Scale
/// card dimmed to 0.4 by its binding sprang to full opacity on the first hover, kept it after release, and swallowed
/// any bind change made while the row was in flight.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class WhileBoundOpacityTests
{
    private static (SceneStore Scene, AnimEngine Anim, TreeReconciler Recon, NodeHandle Node) Mount(Func<Action<NodeHandle>, BoxEl> make)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var anim = new AnimEngine(scene);
        var recon = new TreeReconciler(scene, new StringTable()) { Anim = anim };
        NodeHandle node = default;
        recon.ReconcileRoot(new BoxEl { Children = [make(n => node = n)] }, null);
        scene.Bounds(node) = new RectF(0f, 0f, 100f, 40f);
        return (scene, anim, recon, node);
    }

    private static void Settle(AnimEngine anim)
    {
        for (int i = 0; i < 400 && anim.HasUiWork; i++) anim.Tick(16f);
        Assert.False(anim.HasUiWork);
    }

    [Fact]
    public void APressOnlyCardKeepsItsBoundOpacityThroughHoverPressAndRelease()
    {
        var available = new Signal<bool>(false);
        var (scene, anim, _, row) = Mount(realized => new BoxEl
        {
            Width = 100f, Height = 40f,
            Opacity = Prop.Of(() => available.Value ? 1f : 0.4f),
            WhilePressed = new MotionTarget { Scale = 0.985f },
            OnRealized = realized,
        });
        Assert.Equal(0.4f, scene.Paint(row).Opacity, 3);

        anim.ApplyInteractionEdge(row, AnimEngine.InteractKind.Hover, true);
        Settle(anim);
        Assert.Equal(0.4f, scene.Paint(row).Opacity, 3);   // was 1: the hover edge seeded Opacity toward rest 1

        anim.ApplyInteractionEdge(row, AnimEngine.InteractKind.Press, true);
        Settle(anim);
        Assert.Equal(0.985f, scene.Paint(row).LocalTransform.M11, 3);
        Assert.Equal(0.4f, scene.Paint(row).Opacity, 3);

        anim.ApplyInteractionEdge(row, AnimEngine.InteractKind.Press, false);
        anim.ApplyInteractionEdge(row, AnimEngine.InteractKind.Hover, false);
        Settle(anim);
        Assert.Equal(1f, scene.Paint(row).LocalTransform.M11, 3);
        Assert.Equal(0.4f, scene.Paint(row).Opacity, 3);   // was 1 until the signal next changed
    }

    [Fact]
    public void ABindChangeDuringAHoverScaleLandsAndSurvivesTheSettle()
    {
        var opacity = new Signal<float>(0.4f);
        var (scene, anim, recon, row) = Mount(realized => new BoxEl
        {
            Width = 100f, Height = 40f,
            Opacity = Prop.Of(() => opacity.Value),
            WhileHover = new MotionTarget { Scale = 1.02f },
            OnRealized = realized,
        });

        anim.ApplyInteractionEdge(row, AnimEngine.InteractKind.Hover, true);
        anim.Tick(16f); anim.Tick(16f);                     // the scale is in flight
        opacity.Value = 0.6f;
        recon.Runtime.Flush();
        Settle(anim);
        Assert.Equal(1.02f, scene.Paint(row).LocalTransform.M11, 3);
        Assert.Equal(0.6f, scene.Paint(row).Opacity, 3);   // was 1: the in-flight Opacity row replace-folded over the bind
    }
}
