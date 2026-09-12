using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using static FluentGpu.VerticalSlice.Harness.Gate;

static class CompositorAnimationChecks
{
    public static void Run()
    {
        IndependentProgress();
        CompletedFlipRowsAwaitUiFeedback();
        SpringRetarget();
        ParkAndCancel();
        PublisherReuseAndVisibility();
        MultiAxisComposition();
        UiPartition();
        ParkedRowsDoNotCostSteadyStateCapture();
        RowPoolIsSparseAtSceneHighWater();
        RowPoolCapacityBoundsAndFallback();
        RowPoolTicksAllocateNothing();
    }

    private static (SceneStore Scene, NodeHandle Node, AnimEngine Animation, SceneRecordingSnapshot Snapshot) Fixture()
    {
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new(0, 0, 100, 100);
        scene.Paint(node).VisualKind = VisualKind.Box;
        var animation = new AnimEngine(scene) { RenderOwnsCompositor = true };
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        return (scene, node, animation, snapshot);
    }

    private static void IndependentProgress()
    {
        var (scene, node, animation, snapshot) = Fixture();
        animation.Animate(node, AnimChannel.Opacity, 0, 1, 250, Easing.Linear);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        bool seeded = snapshot.Paint(node).Opacity == 0;
        animation.Tick(16); // Delegated rows must not advance or write the live UI scene.
        renderer.Tick(snapshot, 200);
        bool progressed = MathF.Abs(snapshot.Paint(node).Opacity - .8f) < .0001f;
        bool uiIdle = !animation.HasUiWork && animation.HasTracks(node) && scene.Paint(node).Opacity == 1;
        var subsequent = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(subsequent, 200);
        renderer.Adopt(subsequent, snapshot, 200);
        bool noRestart = MathF.Abs(snapshot.Paint(node).Opacity - .8f) < .0001f;
        renderer.Tick(snapshot, 250);
        bool completed = snapshot.Paint(node).Opacity == 1 && !renderer.HasActive && renderer.Feedback[0].Done;
        animation.ApplyCompositorFeedback(renderer.Feedback);
        Check("gate.compositor-ui-stall", seeded && progressed && uiIdle && noRestart && completed && !animation.HasTracks(node),
            "A 250ms animation progresses through a 200ms UI stall, survives unchanged publication, and settles via UI feedback.");
        snapshot.BeginCompositorOverlay();
        Check("gate.compositor-authored-isolation", snapshot.Paint(node).Opacity == 1,
            "Rendered animation poses never mutate the snapshot's authored paint columns.");

        animation.Keyframes(node, AnimChannel.Opacity, [new(0, 0), new(.5f, 1, Easing.Linear), new(1, 0, Easing.Linear)], 100, loop: true);
        animation.CaptureCompositorAnimations(desired, 300);
        renderer.Adopt(desired, snapshot, 300);
        for (int i = 0; i < 32; i++) renderer.Tick(snapshot, 300 + i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) renderer.Tick(snapshot, 400 + i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.compositor-alloc", allocated == 0 && renderer.HasActive,
            $"Independent looping animation allocated {allocated} bytes during steady render ticks.");
    }

    private static void CompletedFlipRowsAwaitUiFeedback()
    {
        var (scene, _, nodes, animation) = Fan(2);
        var outgoing = nodes[0];
        var incoming = nodes[1];
        animation.SeedExit(outgoing, new EnterExit(Dy: -14f, Opacity: 0f, Active: true), MotionTok.ControlFast);
        scene.Orphan(outgoing);
        animation.SeedEnter(incoming, new EnterExit(Dy: 14f, Opacity: 0f, Active: true), MotionTok.ControlFast);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0); // structural pending starts hold their first presented pose
        renderer.Tick(snapshot, 16);
        bool progressing = renderer.HasActive && desired.Count == 4;
        renderer.Tick(snapshot, 250); // both 150ms pairs have completed, without any UI tick or feedback drain

        int retainedBeforeFeedback = animation.TrackCount;
        int completed = 0;
        foreach (ref readonly var row in renderer.Feedback)
            if (row.Done) completed++;
        bool settledRender = !renderer.HasActive && completed == 4
            && snapshot.Paint(outgoing).Opacity == 0f && snapshot.Paint(incoming).Opacity == 1f;
        bool retainedUi = retainedBeforeFeedback == 4 && !animation.HasUiWork
            && animation.HasTracks(outgoing) && animation.HasTracks(incoming)
            && scene.OrphanCount == 1 && !scene.IsOrphan(incoming);
        animation.ApplyCompositorFeedback(renderer.Feedback);

        Check("gate.compositor-flip-four-rows-await-feedback the UI census retains both completed enter and exit pairs until feedback drains",
            progressing && settledRender && retainedUi && animation.TrackCount == 0
            && !animation.HasTracks(outgoing) && !animation.HasTracks(incoming) && scene.OrphanCount == 1,
            $"beforeFeedback={retainedBeforeFeedback} renderDone={completed} renderActive={renderer.HasActive} "
            + $"afterFeedback={animation.TrackCount} orphans={scene.OrphanCount}; UI track membership is not render activity.");
        snapshot.ReleaseResources();
    }

    private static void SpringRetarget()
    {
        var (scene, node, animation, snapshot) = Fixture();
        var spring = SpringParams.FromResponse(.4f, .8f);
        animation.Spring(node, AnimChannel.TranslateX, 100, spring, initial: 0);
        var first = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(first, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(first, snapshot, 0);
        renderer.Tick(snapshot, 100);
        var oldPose = renderer.Feedback[0];
        float current = snapshot.Paint(node).LocalTransform.Dx;
        animation.Spring(node, AnimChannel.TranslateX, 200, spring);
        var retargeted = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(retargeted, 100);
        renderer.Adopt(retargeted, snapshot, 100);
        bool continuous = MathF.Abs(snapshot.Paint(node).LocalTransform.Dx - current) < .0001f
            && MathF.Abs(renderer.Feedback[0].Velocity - oldPose.Velocity) < .0001f;
        animation.ApplyCompositorFeedback([oldPose]);
        bool rejected = scene.Paint(node).LocalTransform.Dx == 0;
        renderer.Tick(snapshot, 200);
        var generator = Generators.BakeSpring(in spring, current - 200, oldPose.Velocity);
        var expected = Generators.EvalSpring(in generator, 200, 100, Generators.RestDelta, Generators.RestSpeed, out _);
        Check("gate.compositor-spring-retarget", continuous && rejected
            && MathF.Abs(snapshot.Paint(node).LocalTransform.Dx - expected.Value) < .0001f,
            "Retarget carries render value/velocity rather than stale UI state, and old-revision feedback is rejected.");
    }

    private static void ParkAndCancel()
    {
        var (scene, node, animation, snapshot) = Fixture();
        animation.Animate(node, AnimChannel.Opacity, 0, 1, 1000, Easing.Linear);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        renderer.Tick(snapshot, 100);
        animation.SetNodeParked(node, true);
        var parked = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(parked, 100);
        renderer.Adopt(parked, snapshot, 300); // busy renderer adopts well after the UI's 100ms park edge
        renderer.Tick(snapshot, 1000);
        bool quiescent = !renderer.HasActive && renderer.Feedback[0].ElapsedMs == 100;
        animation.SetNodeParked(node, false);
        var resumed = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(resumed, 1000);
        renderer.Adopt(resumed, snapshot, 1050);
        renderer.Tick(snapshot, 1100);
        bool pausedClock = MathF.Abs(snapshot.Paint(node).Opacity - .2f) < .0001f;
        var stale = renderer.Feedback[0];
        animation.Cancel(node, AnimChannel.Opacity);
        animation.Animate(node, AnimChannel.Opacity, .5f, 1, 1000, Easing.Linear);
        animation.ApplyCompositorFeedback([stale]);
        bool staleIgnored = scene.Paint(node).Opacity == 1;
        var replacement = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(replacement, 1100);
        renderer.Adopt(replacement, snapshot, 1100);
        bool newInstance = renderer.Feedback[0].InstanceId != stale.InstanceId && snapshot.Paint(node).Opacity == .5f;
        animation.Cancel(node, AnimChannel.Opacity);
        animation.CaptureCompositorAnimations(desired, 1200);
        renderer.Adopt(desired, snapshot, 1200);
        Check("gate.compositor-park-cancel", quiescent && pausedClock && staleIgnored && newInstance
            && !renderer.HasActive && renderer.Feedback.IsEmpty,
            "Parking excludes paused time; cancellation and slot reuse reject stale feedback; empty desired membership cancels rendering.");
    }

    private static void UiPartition()
    {
        var (_, node, animation, _) = Fixture();
        animation.Animate(node, AnimChannel.LayoutW, 10, 20, 100);
        animation.Animate(node, AnimChannel.Opacity, 0, 1, 100, composite: CompositeOp.Add);
        int samples = 0;
        int driver = animation.Clocks.Register(() => { samples++; return 0; });
        animation.Drive(node, AnimChannel.TranslateX, [new(0, 0), new(1, 10)], driver, 0, 1);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        Check("gate.compositor-ui-partition", desired.Count == 0 && animation.HasUiWork && samples == 0,
            "Layout, additive and delegate-driven channels stay UI-owned; capture never executes a driving callback.");
    }

    private static void PublisherReuseAndVisibility()
    {
        var (_, node, animation, snapshot) = Fixture();
        animation.Keyframes(node, AnimChannel.Opacity, [new(0, 0), new(1, 1, Easing.Linear)], 1000);
        var slot = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(slot, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(slot, snapshot, 0);
        animation.Keyframes(node, AnimChannel.Opacity, [new(0, 1), new(1, 0, Easing.Linear)], 1000);
        animation.CaptureCompositorAnimations(slot, 100); // UI recycles a released publication slot, before adoption.
        renderer.Tick(snapshot, 200);
        Check("gate.compositor-key-storage-owned", MathF.Abs(snapshot.Paint(node).Opacity - .2f) < .0001f,
            "Renderer-owned retained keys remain stable when the UI recaptures the previous publisher buffer.");

        renderer.Adopt(slot, snapshot, 100);
        renderer.Pause(200);
        renderer.Tick(snapshot, 900);
        bool paused = !renderer.HasActive && MathF.Abs(snapshot.Paint(node).Opacity - .9f) < .0001f;
        animation.CaptureCompositorAnimations(slot, 900);
        renderer.Adopt(slot, snapshot, 900);
        renderer.Resume(1000);
        renderer.Tick(snapshot, 1100);
        Check("gate.compositor-visibility-pause", paused && renderer.HasActive
            && MathF.Abs(snapshot.Paint(node).Opacity - .8f) < .0001f,
            "Whole-renderer visibility parking excludes hidden elapsed time, including publications adopted while parked.");
    }

    private static void MultiAxisComposition()
    {
        var (scene, node, animation, snapshot) = Fixture();
        animation.Animate(node, AnimChannel.TranslateX, 0, 100, 1000, Easing.Linear);
        animation.Animate(node, AnimChannel.Rotation, 0, 180, 1000, Easing.Linear);
        animation.Animate(node, AnimChannel.ScaleX, 1, 2, 1000, Easing.Linear);
        animation.Animate(node, AnimChannel.ScaleY, 1, 0, 1000, Easing.Linear);
        animation.Animate(node, AnimChannel.ClipL, 0, 10, 1000, Easing.Linear);
        animation.Animate(node, AnimChannel.ClipR, 100, 50, 1000, Easing.Linear);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        renderer.Tick(snapshot, 200);
        var expected = Affine2D.Translation(20, 0).Multiply(Affine2D.Rotation(36 * (MathF.PI / 180)))
            .Multiply(Affine2D.Scale(1.2f, .8f));
        bool rendered = snapshot.Paint(node).LocalTransform == expected && snapshot.Paint(node).ClipRect == new RectF(2, 0, 88, 100);
        animation.ApplyCompositorFeedback(renderer.Feedback);
        Check("gate.compositor-multi-axis-compose", rendered && scene.Paint(node).LocalTransform == expected
            && scene.Paint(node).ClipRect == new RectF(2, 0, 88, 100),
            "Rotation, non-uniform scale, translation and multiple clip edges fold once from one base on both threads.");
    }

    // perf plan item 4: HasUiWork/CaptureCompositorAnimations must cost O(compositor-candidate rows), not O(active
    // rows) — 10,000 parked, structurally-non-compositor (Additive) rows sitting on their own nodes must not make
    // steady-state (no further Add/Free — the compositor-candidate list stays cached) capture calls scale up.
    private static void ParkedRowsDoNotCostSteadyStateCapture()
    {
        long Steady(AnimEngine animation, int iterations)
        {
            var desired = new CompositorAnimationSnapshot();
            animation.CaptureCompositorAnimations(desired, 0); // warm: pays the one post-mutation candidate rebuild
            _ = animation.HasUiWork;
            long best = long.MaxValue;
            for (int i = 0; i < iterations; i++)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                animation.CaptureCompositorAnimations(desired, i);
                _ = animation.HasUiWork;
                best = Math.Min(best, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            }
            return best;
        }

        var (_, quietNode, quietAnim, _) = Fixture();
        quietAnim.Animate(quietNode, AnimChannel.Opacity, 0, 1, 1000, Easing.Linear);
        long quietTicks = Steady(quietAnim, 50);

        var (busyScene, busyNode, busyAnim, _) = Fixture();
        busyAnim.Animate(busyNode, AnimChannel.Opacity, 0, 1, 1000, Easing.Linear);
        const int ParkedCount = 10_000;
        for (int i = 0; i < ParkedCount; i++)
        {
            var extra = busyScene.CreateNode(2);
            busyScene.AppendChild(busyNode, extra);
            busyScene.Paint(extra).VisualKind = VisualKind.Box;
            // Additive is structurally non-compositor-eligible even before Parked is considered (a mixed
            // additive/replace axis must stay one UI-owned composition — IsCompositorRowStatic).
            busyAnim.Animate(extra, AnimChannel.Opacity, 0, 1, 1000, Easing.Linear, composite: CompositeOp.Add);
            busyAnim.SetNodeParked(extra, true);
        }
        long busyTicks = Steady(busyAnim, 50);

        Check("gate.compositor-parked-rows-free", busyTicks <= Math.Max(quietTicks * 5, 1),
            $"10,000 parked/non-compositor rows cost {busyTicks} ticks/call vs {quietTicks} with none " +
            "(steady-state capture must not scale with them).");
    }

    // ── the compositor overlay's memory bound (threading-render-seam.md 3.5) ────────────────────────────────────────
    // The overlay used to be three DENSE columns sized at the scene's node high-water: 508 B/node/snapshot across
    // three publisher slots, i.e. 47.6 MiB at the 32 768 nodes the native ARM64 tour reached, for a workload where
    // a few dozen nodes animate at once. It is a bounded ROW POOL now, and the bound is what these three gates pin:
    // the backing is O(reserved rows) and NOT O(scene capacity); the pool's ceiling degrades deterministically and
    // self-heals at the next capture; and a tick over the sparse path still allocates nothing.

    private static (SceneStore Scene, NodeHandle Root, NodeHandle[] Nodes, AnimEngine Animation) Fan(int children)
    {
        var scene = new SceneStore();
        var root = scene.CreateNode(1);
        scene.Root = root;
        scene.Bounds(root) = new(0, 0, 1000, 1000);
        scene.Paint(root).VisualKind = VisualKind.Box;
        var nodes = new NodeHandle[children];
        for (int i = 0; i < children; i++)
        {
            var child = scene.CreateNode(2);
            scene.AppendChild(root, child);
            scene.Bounds(child) = new(0, 0, 8, 8);
            scene.Paint(child).VisualKind = VisualKind.Box;
            nodes[i] = child;
        }
        return (scene, root, nodes, new AnimEngine(scene) { RenderOwnsCompositor = true });
    }

    private static void RowPoolIsSparseAtSceneHighWater()
    {
        const int Nodes = 32_768;   // the measured scene high-water of the native ARM64 tour
        var (scene, _, nodes, animation) = Fan(Nodes);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        int capacity = snapshot.Capacity;

        for (int i = 0; i < 3; i++) animation.Animate(nodes[i], AnimChannel.Opacity, 0, 1, 1000, Easing.Linear);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        // The publisher's sequence: capture the scene, capture the animation descriptions, THEN reserve rows for the
        // nodes those descriptions name (SceneRenderFrame.Capture does exactly this).
        snapshot.ReserveCompositorRows(desired.DistinctNodeCount);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 500);

        long sparse = snapshot.CompositorOverlayBytes;
        long dense = SceneRecordingSnapshot.DenseCompositorOverlayBytes(capacity);
        bool bound = capacity >= Nodes
            && snapshot.CompositorRowCapacity == SceneRecordingSnapshot.MinCompositorRows   // O(rows), not O(capacity)
            && snapshot.CompositorRowsInUse == 3
            && snapshot.CompositorRowOverflows == 0
            // 640 KiB, re-derived for the THIRD 4-B/node side array §13.1 added (the self epoch, beside the row map and
            // the dirty trail): at this fixture's 32 770 slots that array alone is 131 KB, which took the old 512 KiB
            // literal from ~25 % headroom to under 1 % — passing, but one column away from a false failure. The bound
            // still says the same thing it always did: 256 rows plus three per-node int columns, never the dense
            // overlay's 508 B/node.
            && sparse < 640L * 1024
            && sparse * 30 < dense
            && MathF.Abs(snapshot.Paint(nodes[0]).Opacity - .5f) < .0001f
            && snapshot.Paint(nodes[3]).Opacity == 1;
        Check("gate.compositor-row-sparse", bound,
            $"At {capacity} scene slots with 3 animating nodes the overlay holds {sparse} B in "
            + $"{snapshot.CompositorRowCapacity} reserved rows of {SceneRecordingSnapshot.CompositorRowBytes} B "
            + $"(the deleted dense overlay: {dense} B) — the backing must scale with rows, not scene capacity.");
        snapshot.ReleaseResources();
    }

    private static void RowPoolCapacityBoundsAndFallback()
    {
        const int Animated = SceneRecordingSnapshot.MinCompositorRows + 64;
        const int Spare = 8;   // never animated — proves the dirty marks below are the OVERLAY's, not the capture's
        var (scene, root, nodes, animation) = Fan(Animated + Spare);
        scene.ClearRecordDirty();   // so every dirty bit read below can only have come from the overlay
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);   // no animations exist yet, so this slot reserves only the floor
        int reserved = snapshot.CompositorRowCapacity;

        for (int i = 0; i < Animated; i++) animation.Animate(nodes[i], AnimChannel.Opacity, 0, 1, 1000, Easing.Linear);
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        var renderer = new RenderCompositorAnimations();
        // 320 nodes want a row from a 256-row pool. The RENDER side must not grow it, must not allocate and must not
        // throw: the surplus writes go to the discard sink, the nodes stay record-dirty, and the demand is recorded.
        // (Adoption itself warms the renderer's own buffers, so the overflow tick is measured on its own.)
        renderer.Adopt(desired, snapshot, 500);
        long beforeOverflow = GC.GetAllocatedBytesForCurrentThread();
        renderer.Tick(snapshot, 500);
        long overflowAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeOverflow;

        int posed = 0, deferred = 0, deferredButDirty = 0, spareClean = 0;
        for (int i = 0; i < Animated; i++)
        {
            float opacity = snapshot.Paint(nodes[i]).Opacity;
            // A posed node reads its POSED opacity; a deferred one still reads its authored 1. The self-content bit is
            // no longer the proxy for "got a row": since §13.1 it means "this pose CHANGED", and this fixture ticks
            // twice at the same instant (Adopt ends in Tick, then the measured Tick re-runs at t=500), so the second
            // tick legitimately re-poses identical values and stamps nothing. The pose itself is the evidence here;
            // the bound assertions below still pin the row pool directly.
            if (MathF.Abs(opacity - .5f) < .0001f) posed++;
            else if (opacity == 1 && snapshot.RecordDirtySelfBits(nodes[i]) == 0)
            {
                deferred++;
                // ...and a deferred one has no row, yet is still marked dirty so it re-records this frame.
                if (snapshot.RecordDirtyBits(nodes[i]) != 0) deferredButDirty++;
            }
        }
        for (int i = Animated; i < Animated + Spare; i++)
            if (snapshot.RecordDirtyBits(nodes[i]) == 0) spareClean++;
        int overflowed = Animated - reserved;
        bool bounded = snapshot.CompositorRowCapacity == reserved   // never grown during a tick
            && snapshot.CompositorRowsInUse == reserved
            && snapshot.CompositorRowOverflows == overflowed
            && snapshot.CompositorRowDemand >= Animated
            && posed == reserved && deferred == overflowed && deferredButDirty == overflowed
            && spareClean == Spare                                        // marks are the overlay's, not the capture's
            && snapshot.RecordDirtyDescendantBits(root) != 0;             // ancestor propagation still fires

        // The documented fallback: the PUBLISHER honours that demand at the next capture, and from then on every
        // value lands. Nothing was lost — the trajectories live in the renderer, not in the overlay.
        snapshot.Capture(scene);
        bool grew = snapshot.CompositorRowCapacity >= Animated;
        renderer.Tick(snapshot, 500);
        int posedAfter = 0;
        for (int i = 0; i < Animated; i++)
            if (MathF.Abs(snapshot.Paint(nodes[i]).Opacity - .5f) < .0001f) posedAfter++;
        bool healed = grew && posedAfter == Animated
            && snapshot.CompositorRowOverflows == 0 && snapshot.CompositorRowsInUse == Animated;

        Check("gate.compositor-row-overflow", bounded && healed && overflowAlloc == 0,
            $"{Animated} animating nodes against a {reserved}-row pool posed {posed} and deferred {deferred} "
            + $"({deferredButDirty} still record-dirty), allocated {overflowAlloc} B; the next capture reserved "
            + $"{snapshot.CompositorRowCapacity} rows and posed {posedAfter}.");
        snapshot.ReleaseResources();
    }

    private static void RowPoolTicksAllocateNothing()
    {
        const int Animated = 200;
        var (scene, _, nodes, animation) = Fan(Animated);
        var snapshot = new SceneRecordingSnapshot();
        for (int i = 0; i < Animated; i++)
        {
            animation.Keyframes(nodes[i], AnimChannel.Opacity,
                [new(0, 0), new(.5f, 1, Easing.Linear), new(1, 0, Easing.Linear)], 100, loop: true);
            animation.Spring(nodes[i], AnimChannel.TranslateX, 100, SpringParams.FromResponse(.4f, .8f), initial: 0);
        }
        var desired = new CompositorAnimationSnapshot();
        animation.CaptureCompositorAnimations(desired, 0);
        snapshot.Capture(scene);
        snapshot.ReserveCompositorRows(desired.DistinctNodeCount);
        var renderer = new RenderCompositorAnimations();
        renderer.Adopt(desired, snapshot, 0);
        for (int i = 0; i < 32; i++) renderer.Tick(snapshot, 100 + i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) renderer.Tick(snapshot, 200 + i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.compositor-row-alloc", allocated == 0 && renderer.HasActive
            && snapshot.CompositorRowOverflows == 0 && snapshot.CompositorRowsInUse == Animated
            && desired.DistinctNodeCount == Animated,
            $"{Animated} nodes × 2 channels over the sparse row pool allocated {allocated} bytes across 64 render "
            + $"ticks ({snapshot.CompositorRowsInUse} rows bound, {snapshot.CompositorRowCapacity} reserved).");
        snapshot.ReleaseResources();
    }
}
