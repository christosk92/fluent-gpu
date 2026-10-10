using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>Moving the pointer from a hovered card onto its own nested button fires two hover edges on the card (the
/// HoverWithin on edge, then the leaf off edge the effective-hover guard keeps on), and moving back up fires two more.
/// All of them resolve to the target the card's rows are already easing toward. Each one re-seeded those rows, which
/// restarted every eased lift and fade from where it stood with its full duration (the lift stalled and landed up to a
/// whole duration late) and stamped a fresh compositor revision per edge. A same-target edge now leaves them running.</summary>
public sealed class RedundantHoverEdgeTests
{
    private const float Frame = 16f;

    private sealed class Harness
    {
        public readonly SceneStore Scene = new();
        public readonly AnimEngine Anim;

        public Harness(bool renderOwned = false)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Anim = new AnimEngine(Scene) { RenderOwnsCompositor = renderOwned };
            new TreeReconciler(Scene, new StringTable()) { Anim = Anim }.ReconcileRoot(Card(), null);
        }

        public NodeHandle Node => Scene.Root;

        public float Lift => Scene.Paint(Node).LocalTransform.Dy;

        public float HoverT => Scene.TryGetInteract(Node, out var ia) ? ia.HoverT : float.NaN;

        // One hover edge as AppHost forwards the dispatcher's OnHoverChanged.
        public void Edge(bool on)
        {
            Anim.SetHover(Node, on);
            Anim.ApplyInteractionEdge(Node, AnimEngine.InteractKind.Hover, on);
        }

        public void Enter() { Scene.SetFlagBits(Node, NodeFlags.Hovered); Edge(true); }

        // InputDispatcher.SetState for the card -> nested button move: the leaf flag moves, UpdateHoverWithin sets the
        // card's HoverWithin and fires its on edge, then Notify fires the card's leaf off edge.
        public void MoveOntoNestedButton()
        {
            Scene.ClearFlagBits(Node, NodeFlags.Hovered);
            Scene.SetFlagBits(Node, NodeFlags.HoverWithin);
            Edge(true);
            Edge(false);
        }

        // ...and back up: the leaf flag returns, UpdateHoverWithin clears HoverWithin and fires its off edge, then Notify
        // fires the card's leaf on edge.
        public void MoveBackOntoCard()
        {
            Scene.SetFlagBits(Node, NodeFlags.Hovered);
            Scene.ClearFlagBits(Node, NodeFlags.HoverWithin);
            Edge(false);
            Edge(true);
        }
    }

    // A clickable card with an eased WhileHover lift and a hover scale (its HoverFade row).
    private static BoxEl Card() => new()
    {
        Width = 40f, Height = 40f, OnClick = static () => { },
        HoverScale = 1.04f, HoverDurationMs = 250f,
        WhileHover = new MotionTarget { OffsetY = -8f },
        Transition = MotionTok.ControlNormal,
    };

    private static void TickBoth(Harness a, Harness b) { a.Anim.Tick(Frame); b.Anim.Tick(Frame); }

    [Fact]
    public void CrossingOntoANestedButtonAndBackKeepsTheLiftOnItsCourse()
    {
        var crossed = new Harness();
        var straight = new Harness();
        crossed.Enter();
        straight.Enter();
        for (int i = 0; i < 4; i++) TickBoth(crossed, straight);

        crossed.MoveOntoNestedButton();
        for (int i = 0; i < 3; i++)
        {
            TickBoth(crossed, straight);
            Assert.Equal(straight.Lift, crossed.Lift, 3);
            Assert.Equal(straight.HoverT, crossed.HoverT, 3);
        }

        crossed.MoveBackOntoCard();
        for (int i = 0; i < 4; i++)
        {
            TickBoth(crossed, straight);
            Assert.Equal(straight.Lift, crossed.Lift, 3);
            Assert.Equal(straight.HoverT, crossed.HoverT, 3);
        }
    }

    [Fact]
    public void ARedundantEdgeStampsNoCompositorRevision()
    {
        var h = new Harness(renderOwned: true);
        h.Enter();
        ulong before = h.Anim.CompositorCaptureFingerprint();

        h.MoveOntoNestedButton();
        Assert.Equal(before, h.Anim.CompositorCaptureFingerprint());

        h.MoveBackOntoCard();
        Assert.Equal(before, h.Anim.CompositorCaptureFingerprint());
    }

    [Fact]
    public void LeavingTheCardAfterACrossingStillReturnsToRest()
    {
        var h = new Harness();
        h.Enter();
        for (int i = 0; i < 4; i++) h.Anim.Tick(Frame);
        h.MoveOntoNestedButton();
        h.Anim.Tick(Frame);

        h.Scene.ClearFlagBits(h.Node, NodeFlags.HoverWithin);   // the pointer leaves the card from its button
        h.Edge(false);
        for (int i = 0; i < 40 && h.Anim.HasActive; i++) h.Anim.Tick(Frame);

        Assert.Equal(0f, h.Lift, 3);
        Assert.Equal(0f, h.HoverT, 3);
    }
}
