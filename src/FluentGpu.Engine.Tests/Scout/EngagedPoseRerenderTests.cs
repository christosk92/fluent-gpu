using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A re-render whose props changed re-asserts the authored rest transform, opacity and blur in WriteColumns.
/// Under a gesture still engaged whose While* rows had settled and been freed, nothing re-posed the node: a hovered Fold
/// cover snapped back to its stacked rest (and a While* dim or blur to its authored value) under a pointer that never
/// left, until the next hover edge. The engaged target now re-lands over the re-written rest.</summary>
public sealed class EngagedPoseRerenderTests
{
    private const float Frame = 16f;

    private sealed class Harness
    {
        public readonly SceneStore Scene = new();
        public readonly AnimEngine Anim;
        public readonly TreeReconciler Recon;
        private Element? _last;

        public Harness()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Anim = new AnimEngine(Scene);
            Recon = new TreeReconciler(Scene, new StringTable()) { Anim = Anim };
        }

        public NodeHandle Node => Scene.Root;

        public void Render(Element el) { Recon.ReconcileRoot(el, _last); _last = el; }

        public void Settle() { for (int i = 0; i < 30 && Anim.HasActive; i++) Anim.Tick(Frame); }
    }

    // A Fold-cover shape: an authored rest pose plus a hover delta on every channel the static write re-asserts. `alt`
    // flips the fill (a theme switch), so the re-render really changes a prop and WriteColumns runs.
    private static BoxEl Cover(float offsetX = 100f, bool alt = false) => new()
    {
        Width = 40f, Height = 40f,
        Fill = alt ? ColorF.FromRgba(0x30, 0x30, 0x30, 0xFF) : ColorF.FromRgba(0x20, 0x20, 0x20, 0xFF),
        OffsetX = offsetX, Rotation = -11f, Opacity = 0.8f, Blur = 1f,
        WhileHover = new MotionTarget { OffsetX = -10f, Rotation = -5f, Opacity = 0.5f, Blur = 3f },
        Transition = MotionTok.ControlFaster,
    };

    private static float RotOf(in Affine2D tf)
        => (tf.M11 != 0f || tf.M12 != 0f) ? MathF.Atan2(tf.M12, tf.M11) * (180f / MathF.PI) : 0f;

    [Fact]
    public void AChangedRerenderKeepsTheSettledHoverPose()
    {
        var h = new Harness();
        h.Render(Cover());
        h.Anim.ApplyInteractionEdge(h.Node, AnimEngine.InteractKind.Hover, true);
        h.Settle();
        Assert.False(h.Anim.HasTracks(h.Node));   // settled and freed: only paint holds the pose

        h.Render(Cover(alt: true));
        h.Settle();

        NodePaint p = h.Scene.Paint(h.Node);
        Assert.Equal(90f, p.LocalTransform.Dx, 2);
        Assert.Equal(-16f, RotOf(p.LocalTransform), 1);
        Assert.Equal(0.4f, p.Opacity, 3);
        Assert.Equal(4f, p.BlurSigma, 3);
    }

    [Fact]
    public void ARerenderThatMovesTheRestLandsTheNewRestPlusTheDelta()
    {
        var h = new Harness();
        h.Render(Cover());
        h.Anim.ApplyInteractionEdge(h.Node, AnimEngine.InteractKind.Hover, true);
        h.Settle();

        h.Render(Cover(offsetX: 60f));
        h.Settle();

        Assert.Equal(50f, h.Scene.Paint(h.Node).LocalTransform.Dx, 2);
    }

    [Fact]
    public void AChangedRerenderKeepsTheSettledPressedOpacity()
    {
        var h = new Harness();
        BoxEl Button(bool alt) => new()
        {
            Width = 40f, Height = 40f,
            Fill = alt ? ColorF.FromRgba(0x30, 0x30, 0x30, 0xFF) : ColorF.FromRgba(0x20, 0x20, 0x20, 0xFF),
            WhilePressed = new MotionTarget { Scale = 0.96f, Opacity = 0.6f },
            Transition = MotionTok.ControlFaster,
        };
        h.Render(Button(false));
        h.Anim.ApplyInteractionEdge(h.Node, AnimEngine.InteractKind.Press, true);
        h.Settle();

        h.Render(Button(true));
        h.Settle();

        Assert.Equal(0.6f, h.Scene.Paint(h.Node).Opacity, 3);
        Assert.Equal(0.96f, h.Scene.Paint(h.Node).LocalTransform.M11, 3);
    }

    [Fact]
    public void ReleasingAfterTheRerenderReturnsToTheAuthoredRest()
    {
        var h = new Harness();
        h.Render(Cover());
        h.Anim.ApplyInteractionEdge(h.Node, AnimEngine.InteractKind.Hover, true);
        h.Settle();
        h.Render(Cover(alt: true));
        h.Anim.ApplyInteractionEdge(h.Node, AnimEngine.InteractKind.Hover, false);
        h.Settle();

        NodePaint p = h.Scene.Paint(h.Node);
        Assert.Equal(100f, p.LocalTransform.Dx, 2);
        Assert.Equal(-11f, RotOf(p.LocalTransform), 1);
        Assert.Equal(0.8f, p.Opacity, 3);
        Assert.Equal(1f, p.BlurSigma, 3);
    }

    [Fact]
    public void AnUnengagedRerenderStillLandsTheAuthoredRest()
    {
        var h = new Harness();
        h.Render(Cover());
        h.Render(Cover(offsetX: 60f));

        NodePaint p = h.Scene.Paint(h.Node);
        Assert.Equal(60f, p.LocalTransform.Dx, 2);
        Assert.Equal(0.8f, p.Opacity, 3);
        Assert.Equal(1f, p.BlurSigma, 3);
    }
}
