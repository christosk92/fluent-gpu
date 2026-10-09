using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A render-owned row interrupted mid-flight is re-seeded by the UI from ITS view of the row - the last feedback
/// pose it imported - while the render thread has kept advancing it. A re-seed that starts from that view (a hover fade,
/// an eased from-current move, a FLIP retarget) must continue from the pose on screen, never step back to the older one;
/// a re-seed from an explicit start still restarts there.</summary>
public sealed class RelativeReseedTests
{
    private static readonly MotionTokenDef Linear1s = MotionTokenDef.Eased(1000f, Easing.Linear, ReducedMotionPolicy.Exempt);

    private sealed class Rig
    {
        public readonly SceneStore Scene = new();
        public readonly NodeHandle Node;
        public readonly AnimEngine Animation;
        public readonly SceneRecordingSnapshot Snapshot = new();
        public readonly CompositorAnimationSnapshot Desired = new();
        public readonly RenderCompositorAnimations Renderer = new();

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            var root = Scene.CreateNode(1);
            Scene.Root = root;
            Scene.Bounds(root) = new(0, 0, 1000, 1000);
            Scene.Paint(root).VisualKind = VisualKind.Box;
            Node = Scene.CreateNode(2);
            Scene.AppendChild(root, Node);
            Scene.Bounds(Node) = new(0, 0, 8, 8);
            Scene.Paint(Node).VisualKind = VisualKind.Box;
            Animation = new AnimEngine(Scene) { RenderOwnsCompositor = true };
        }

        /// <summary>One UI publication adopted by the render thread at <paramref name="nowMs"/>.</summary>
        public void Publish(double nowMs)
        {
            Snapshot.Capture(Scene);
            Animation.CaptureCompositorAnimations(Desired, nowMs);
            Renderer.Adopt(Desired, Snapshot, nowMs);
        }

        public float PosedDx => Snapshot.Paint(Node).LocalTransform.Dx;
    }

    [Fact]
    public void AnEasedFromCurrentRetarget_ContinuesFromTheRenderPose()
    {
        var rig = new Rig();
        rig.Animation.SeedValue(rig.Node, AnimChannel.TranslateX, 100f, in Linear1s, from: 0f);
        rig.Publish(0);
        rig.Renderer.Tick(rig.Snapshot, 100);
        rig.Animation.ApplyCompositorFeedback(rig.Renderer.Feedback);   // the UI's view: 10
        rig.Renderer.Tick(rig.Snapshot, 200);                           // on screen: 20, not imported yet
        Assert.Equal(20f, rig.PosedDx, 3);

        rig.Animation.SeedValue(rig.Node, AnimChannel.TranslateX, 200f, in Linear1s);   // from current
        rig.Publish(200);
        Assert.Equal(20f, rig.PosedDx, 3);

        // Re-seeded again before any pose of that revision came back: still continuous.
        rig.Renderer.Tick(rig.Snapshot, 300);
        float shown = rig.PosedDx;
        rig.Animation.SeedValue(rig.Node, AnimChannel.TranslateX, 300f, in Linear1s);
        rig.Publish(300);
        Assert.Equal(shown, rig.PosedDx, 3);
        rig.Renderer.Tick(rig.Snapshot, 2000);
        Assert.Equal(300f, rig.PosedDx, 3);
        rig.Snapshot.ReleaseResources();
    }

    [Fact]
    public void AHoverOut_FadesFromTheRenderPose()
    {
        var rig = new Rig();
        ref var interaction = ref rig.Scene.InteractRef(rig.Node);
        interaction = InteractionAnim.Default;
        interaction.HoverDurationMs = 1000f;
        interaction.HoverEasing = Easing.Linear;
        rig.Animation.SetHover(rig.Node, true);
        rig.Publish(0);
        rig.Renderer.Tick(rig.Snapshot, 100);
        rig.Animation.ApplyCompositorFeedback(rig.Renderer.Feedback);   // HoverT 0.1
        rig.Renderer.Tick(rig.Snapshot, 200);                           // on screen 0.2
        Assert.Equal(0.2f, Assert.Single(rig.Renderer.Feedback.ToArray()).Value, 4);

        rig.Animation.SetHover(rig.Node, false);
        rig.Publish(200);
        Assert.Equal(0.2f, Assert.Single(rig.Renderer.Feedback.ToArray()).Value, 4);
        rig.Snapshot.ReleaseResources();
    }

    [Fact]
    public void AFlipTweenRetarget_ShiftsThePoseOnScreen()
    {
        var rig = new Rig();
        var spec = new LayoutTransition(TransitionChannels.Position, TransitionDynamics.Tween(1000f, Easing.Linear));
        rig.Animation.AnimateBounds(rig.Node, new RectF(100, 0, 8, 8), new RectF(0, 0, 8, 8), spec);
        rig.Publish(0);
        rig.Renderer.Tick(rig.Snapshot, 100);
        rig.Animation.ApplyCompositorFeedback(rig.Renderer.Feedback);   // the UI's view: 90
        rig.Renderer.Tick(rig.Snapshot, 300);                           // on screen: 70
        Assert.Equal(70f, rig.PosedDx, 3);

        rig.Animation.AnimateBounds(rig.Node, new RectF(50, 0, 8, 8), new RectF(0, 0, 8, 8), spec);   // moved 50 more
        rig.Publish(300);
        Assert.Equal(120f, rig.PosedDx, 3);
        rig.Snapshot.ReleaseResources();
    }

    [Fact]
    public void ASpringFlipRebase_ShiftsThePoseOnScreen()
    {
        var rig = new Rig();
        var spec = new LayoutTransition(TransitionChannels.Position, TransitionDynamics.Spring(0.5f, 1f));
        rig.Animation.AnimateBounds(rig.Node, new RectF(100, 0, 8, 8), new RectF(0, 0, 8, 8), spec);
        rig.Publish(0);
        rig.Renderer.Tick(rig.Snapshot, 100);
        rig.Animation.ApplyCompositorFeedback(rig.Renderer.Feedback);
        rig.Renderer.Tick(rig.Snapshot, 200);
        float shown = rig.PosedDx;

        rig.Animation.AnimateBounds(rig.Node, new RectF(50, 0, 8, 8), new RectF(0, 0, 8, 8), spec);
        rig.Publish(200);
        Assert.Equal(shown + 50f, rig.PosedDx, 3);
        rig.Snapshot.ReleaseResources();
    }

    [Fact]
    public void AnExplicitFromReseed_StillRestartsThere()
    {
        var rig = new Rig();
        rig.Animation.Animate(rig.Node, AnimChannel.TranslateX, 0f, 100f, 1000f, Easing.Linear);
        rig.Publish(0);
        rig.Renderer.Tick(rig.Snapshot, 100);
        rig.Animation.ApplyCompositorFeedback(rig.Renderer.Feedback);
        rig.Renderer.Tick(rig.Snapshot, 200);

        rig.Animation.Animate(rig.Node, AnimChannel.TranslateX, 0f, 100f, 1000f, Easing.Linear);
        rig.Publish(200);
        Assert.Equal(0f, rig.PosedDx, 3);
        rig.Snapshot.ReleaseResources();
    }
}
