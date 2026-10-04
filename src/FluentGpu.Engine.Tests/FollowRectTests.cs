using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// F169 (follow-rect): a node that follows another takes the target's laid-out SIZE and PAINTED origin in the same frame,
/// including a move the target made with no layout change (a rail slide is a paint translation on an ancestor), and stands
/// down to its own bindings the moment the target is gone. These drive the two post-layout phases the host runs
/// (<see cref="SceneStore.SizeFollowRects"/>, then <see cref="SceneStore.PlaceFollowRects"/> after the dirty scopes re-solve)
/// against a hand-built tree, so the "layout" is the Bounds the test writes. With a render-owned compositor (the default
/// Async host) the target's ancestor slide is a render-thread row the UI never advances between feedbacks; the anchor tests
/// pin that the animation engine keeps such rows on the UI tick while a follower follows (and hands them back after), and
/// the reconciler test pins that a follower's own bound Width/Height/Transform stand down while it follows.
/// </summary>
public sealed class FollowRectTests
{
    private sealed class Tree
    {
        public readonly SceneStore Scene = new();
        public NodeHandle Root, Rail, Reservation, Follower;

        public Tree()
        {
            Root = Scene.CreateNode(1);
            Scene.Root = Root;
            Scene.Bounds(Root) = new RectF(0f, 0f, 1000f, 600f);
            Rail = Scene.CreateNode(1);
            Scene.AppendChild(Root, Rail);
            Scene.Bounds(Rail) = new RectF(700f, 0f, 300f, 600f);
            Reservation = Scene.CreateNode(1);
            Scene.AppendChild(Rail, Reservation);
            Scene.Bounds(Reservation) = new RectF(0f, 48f, 300f, 170f);
            Follower = Scene.CreateNode(1);
            Scene.AppendChild(Root, Follower);
            Scene.Bounds(Follower) = new RectF(0f, 0f, 10f, 10f);
        }

        /// <summary>The host's frame order: size, re-solve what it dirtied (here: the Bounds layout would write), place.</summary>
        public bool Frame()
        {
            bool sized = Scene.SizeFollowRects();
            if (sized)
            {
                ref RectF b = ref Scene.Bounds(Follower);
                b = new RectF(b.X, b.Y, Scene.Layout(Follower).Width, Scene.Layout(Follower).Height);
                Scene.ClearLayoutDirty();
            }
            Scene.PlaceFollowRects();
            return sized;
        }
    }

    [Fact]
    public void FollowerTakesTheTargetsSizeAndPaintedOrigin()
    {
        var t = new Tree();
        t.Scene.SetFollowRect(t.Follower, () => t.Reservation);

        Assert.True(t.Frame());

        Assert.Equal(300f, t.Scene.Layout(t.Follower).Width);
        Assert.Equal(170f, t.Scene.Layout(t.Follower).Height);
        var rect = t.Scene.AbsoluteRect(t.Follower);
        Assert.Equal(new RectF(700f, 48f, 300f, 170f), rect);
    }

    [Fact]
    public void APaintOnlyMoveOfAnAncestorMovesTheFollowerInTheSameFrame()
    {
        // The rail opens with a paint translation: no bounds change, so no OnBoundsChanged edge ever fires. The follower
        // must still land on the reservation's painted rect in the frame the animation wrote it.
        var t = new Tree();
        t.Scene.SetFollowRect(t.Follower, () => t.Reservation);
        t.Frame();

        t.Scene.Paint(t.Rail).LocalTransform = Affine2D.Translation(120f, 0f);
        Assert.False(t.Frame());                       // nothing resized: no re-solve is requested

        Assert.Equal(new RectF(820f, 48f, 300f, 170f), t.Scene.AbsoluteRect(t.Follower));
        Assert.Equal(t.Scene.AbsoluteRect(t.Reservation), t.Scene.AbsoluteRect(t.Follower));
    }

    [Fact]
    public void ALaidOutOffsetOfTheFollowerIsCompensated()
    {
        var t = new Tree();
        t.Scene.Bounds(t.Follower) = new RectF(10f, 20f, 10f, 10f);
        t.Scene.SetFollowRect(t.Follower, () => t.Reservation);

        t.Frame();

        Assert.Equal(700f, t.Scene.AbsoluteRect(t.Follower).X);
        Assert.Equal(48f, t.Scene.AbsoluteRect(t.Follower).Y);
        Assert.Equal(690f, t.Scene.Paint(t.Follower).LocalTransform.Dx);
        Assert.Equal(28f, t.Scene.Paint(t.Follower).LocalTransform.Dy);
    }

    [Fact]
    public void ASettledFrameWritesNothing()
    {
        var t = new Tree();
        t.Scene.SetFollowRect(t.Follower, () => t.Reservation);
        t.Frame();
        t.Scene.ClearTransformDirty();
        t.Scene.ClearLayoutDirty();

        Assert.False(t.Frame());

        Assert.False(t.Scene.AnyTransformWrote);
        Assert.False(t.Scene.AnyLayoutDirty);
    }

    [Fact]
    public void ANullAnswerLeavesTheNodeToItsOwnBindings()
    {
        var t = new Tree();
        NodeHandle target = default;
        t.Scene.SetFollowRect(t.Follower, () => target);
        var before = t.Scene.Paint(t.Follower).LocalTransform;

        Assert.False(t.Scene.IsFollowing(t.Follower));
        Assert.False(t.Frame());

        Assert.Equal(before, t.Scene.Paint(t.Follower).LocalTransform);
        Assert.True(float.IsNaN(t.Scene.Layout(t.Follower).Width));

        target = t.Reservation;                        // the answer is read live every pass
        Assert.True(t.Scene.IsFollowing(t.Follower));
        Assert.True(t.Frame());
    }

    [Fact]
    public void AFreedTargetStopsFollowingAndKeepsTheLastGeometry()
    {
        // The reservation remounts (a re-dock): the follower must not collapse to the dead node's zeros.
        var t = new Tree();
        t.Scene.SetFollowRect(t.Follower, () => t.Reservation);
        t.Frame();
        var placed = t.Scene.AbsoluteRect(t.Follower);

        t.Scene.FreeSubtree(t.Reservation);

        Assert.False(t.Scene.IsFollowing(t.Follower));
        Assert.False(t.Frame());
        Assert.Equal(placed, t.Scene.AbsoluteRect(t.Follower));
    }

    [Fact]
    public void AFreedFollowerIsPrunedAndNoFollowerIsReported()
    {
        var t = new Tree();
        t.Scene.SetFollowRect(t.Follower, () => t.Reservation);
        Assert.True(t.Scene.HasFollowers);

        t.Scene.FreeSubtree(t.Follower);
        Assert.False(t.Scene.SizeFollowRects());

        Assert.False(t.Scene.HasFollowers);
    }

    [Fact]
    public void ATargetInsideTheFollowerIsRefused()
    {
        var t = new Tree();
        var inner = t.Scene.CreateNode(1);
        t.Scene.AppendChild(t.Follower, inner);
        t.Scene.Bounds(inner) = new RectF(0f, 0f, 50f, 50f);
        t.Scene.SetFollowRect(t.Follower, () => inner);

        Assert.False(t.Scene.IsFollowing(t.Follower));
        Assert.False(t.Scene.SizeFollowRects());
    }

    // The host's 7.15 anchor publication (AppHost): the follow pass hands its anchor chains to the animation engine.
    private static void PublishAnchors(SceneStore scene, AnimEngine anim, HashSet<NodeHandle> scratch)
    {
        bool has = scene.HasFollowers;
        if (!has && !anim.HasFollowAnchors) return;
        scratch.Clear();
        if (has) scene.CollectFollowAnchors(scratch);
        anim.SetFollowAnchors(scratch);
    }

    private static void AssertFollowerOnTarget(Tree t)
    {
        RectF want = t.Scene.AbsoluteRect(t.Reservation), got = t.Scene.AbsoluteRect(t.Follower);
        Assert.Equal(want.X, got.X, 2);
        Assert.Equal(want.Y, got.Y, 2);
        Assert.Equal(want.W, got.W, 2);
        Assert.Equal(want.H, got.H, 2);
    }

    [Fact]
    public void ARenderOwnedSlideOfTheTargetsAncestorIsTickedOnTheUiWhileFollowedAndTheFollowerRidesItEveryFrame()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var t = new Tree();
        var anim = new AnimEngine(t.Scene) { RenderOwnsCompositor = true };
        var anchors = new HashSet<NodeHandle>();
        anim.Animate(t.Rail, AnimChannel.TranslateX, 0f, 300f, 200f, Easing.EaseInOut);
        var desired = new CompositorAnimationSnapshot();

        // Nothing follows: the slide is the compositor's, the UI has no tick debt.
        anim.CaptureCompositorAnimations(desired, 0);
        Assert.Equal(1, desired.Count);
        Assert.False(anim.HasUiWork);

        // A follower attaches: the chain under it is held on the UI tick and out of the render thread's desired set.
        t.Scene.SetFollowRect(t.Follower, () => t.Reservation);
        t.Frame();
        PublishAnchors(t.Scene, anim, anchors);
        Assert.True(anim.HasFollowAnchors);
        Assert.True(anim.HasUiWork);
        anim.CaptureCompositorAnimations(desired, 16);
        Assert.Equal(0, desired.Count);

        float last = 0f;
        for (int frame = 0; frame < 20; frame++)
        {
            anim.Tick(16f);                            // the UI scheduler advances the rail's own transform
            t.Frame();
            PublishAnchors(t.Scene, anim, anchors);
            AssertFollowerOnTarget(t);                 // same frame, on every frame of the slide
            float dx = t.Scene.Paint(t.Rail).LocalTransform.Dx;
            Assert.True(dx >= last, $"rail went backwards at frame {frame}: {last} -> {dx}");
            last = dx;
        }
        Assert.Equal(300f, last, 2);                   // the slide completed on the UI and the follower sits on it
        Assert.False(anim.HasUiWork);
    }

    [Fact]
    public void AReleasedFollowerReturnsTheSlideToTheCompositor()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var t = new Tree();
        var anim = new AnimEngine(t.Scene) { RenderOwnsCompositor = true };
        var anchors = new HashSet<NodeHandle>();
        bool follow = true;
        anim.Animate(t.Rail, AnimChannel.TranslateX, 0f, 300f, 200f, Easing.EaseInOut);
        t.Scene.SetFollowRect(t.Follower, () => follow ? t.Reservation : default);
        t.Frame();
        PublishAnchors(t.Scene, anim, anchors);
        var desired = new CompositorAnimationSnapshot();
        anim.CaptureCompositorAnimations(desired, 0);
        Assert.Equal(0, desired.Count);
        Assert.True(anim.HasUiWork);

        follow = false;                                // the thunk answers Null: nothing is read any more
        t.Frame();
        PublishAnchors(t.Scene, anim, anchors);

        Assert.False(anim.HasFollowAnchors);
        Assert.False(anim.HasUiWork);
        anim.CaptureCompositorAnimations(desired, 32);
        Assert.Equal(1, desired.Count);

        // And a removed follower releases the anchors too (the host's HasFollowers==false branch).
        follow = true;
        t.Frame();
        PublishAnchors(t.Scene, anim, anchors);
        Assert.True(anim.HasFollowAnchors);
        t.Scene.SetFollowRect(t.Follower, null);
        PublishAnchors(t.Scene, anim, anchors);
        Assert.False(anim.HasFollowAnchors);
        anim.CaptureCompositorAnimations(desired, 48);
        Assert.Equal(1, desired.Count);
    }

    [Fact]
    public void ASlideAlreadyInFlightOnTheRenderThreadHandsOverToTheUiFromItsLastFeedbackPose()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var t = new Tree();
        var anim = new AnimEngine(t.Scene) { RenderOwnsCompositor = true };
        var anchors = new HashSet<NodeHandle>();
        anim.Animate(t.Rail, AnimChannel.TranslateX, 0f, 300f, 200f, Easing.Linear);
        var desired = new CompositorAnimationSnapshot();
        anim.CaptureCompositorAnimations(desired, 0);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(t.Scene);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        renderer.Tick(snapshot, 100);                  // the render thread is half way (linear: 150)
        anim.ApplyCompositorFeedback(renderer.Feedback);
        float fed = t.Scene.Paint(t.Rail).LocalTransform.Dx;
        Assert.InRange(fed, 1f, 299f);

        t.Scene.SetFollowRect(t.Follower, () => t.Reservation);
        t.Frame();
        PublishAnchors(t.Scene, anim, anchors);        // the reservation mounts mid-slide
        anim.Tick(16f);
        t.Frame();

        float handed = t.Scene.Paint(t.Rail).LocalTransform.Dx;
        Assert.True(handed >= fed, $"the hand-over jumped back: {fed} -> {handed}");   // continues from the feedback pose
        Assert.True(handed < 300f);
        AssertFollowerOnTarget(t);
    }

    [Fact]
    public void BoundSizeAndTransformStandDownWhileFollowingAndReapplyOnceFollowingStops()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, new StringTable());
        var w = new Signal<float>(100f);
        var h = new Signal<float>(50f);
        var tx = new Signal<float>(10f);
        bool following = false;
        NodeHandle follower = default, reservation = default;
        Func<NodeHandle> thunk = () => following ? reservation : default;
        recon.ReconcileRoot(new BoxEl
        {
            Children =
            [
                new BoxEl { OnRealized = n => reservation = n },
                new BoxEl
                {
                    Width = Prop.Of(() => w.Value),
                    Height = Prop.Of(() => h.Value),
                    Transform = Prop.Of(() => Affine2D.Translation(tx.Value, 0f)),
                    FollowRect = thunk,
                    OnRealized = n => follower = n,
                },
            ],
        }, null);
        scene.Bounds(reservation) = new RectF(0f, 48f, 300f, 170f);

        Assert.Equal(100f, scene.Layout(follower).Width);   // not following: the bindings own the box
        Assert.Equal(50f, scene.Layout(follower).Height);
        Assert.Equal(10f, scene.Paint(follower).LocalTransform.Dx);

        following = true;
        scene.SizeFollowRects();
        scene.PlaceFollowRects();
        Affine2D placed = scene.Paint(follower).LocalTransform;
        Assert.Equal(300f, scene.Layout(follower).Width);
        Assert.Equal(170f, scene.Layout(follower).Height);

        w.Value = 111f; h.Value = 61f; tx.Value = 20f;      // every bound source moves while following
        recon.Runtime.Flush();
        Assert.Equal(300f, scene.Layout(follower).Width);   // the follow pass keeps the box
        Assert.Equal(170f, scene.Layout(follower).Height);
        Assert.Equal(placed, scene.Paint(follower).LocalTransform);
        Assert.True(w.HasSubscribers && h.HasSubscribers && tx.HasSubscribers);   // the writers stayed subscribed

        following = false;                                  // the thunk answers Null; the next source change re-applies
        w.Value = 120f; h.Value = 70f; tx.Value = 30f;
        recon.Runtime.Flush();
        Assert.Equal(120f, scene.Layout(follower).Width);
        Assert.Equal(70f, scene.Layout(follower).Height);
        Assert.Equal(30f, scene.Paint(follower).LocalTransform.Dx);
    }

    [Fact]
    public void ClearingTheTargetRemovesTheFollower()
    {
        var t = new Tree();
        t.Scene.SetFollowRect(t.Follower, () => t.Reservation);
        t.Scene.SetFollowRect(t.Follower, null);

        Assert.False(t.Scene.HasFollowers);
        Assert.False(t.Scene.IsFollowing(t.Follower));
    }
}
