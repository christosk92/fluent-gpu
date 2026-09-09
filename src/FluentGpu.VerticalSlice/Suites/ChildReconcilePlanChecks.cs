using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using static FluentGpu.VerticalSlice.Harness.Gate;

static class ChildReconcilePlanChecks
{
    internal static void Run()
    {
        var scene = new SceneStore();
        var parent = scene.CreateNode(1);
        scene.Root = parent;
        var previous = new Element[140];
        var desired = new Element[140];
        var nodes = new NodeHandle[140];
        for (int i = 0; i < previous.Length; i++)
        {
            previous[i] = new BoxEl { Key = "child-" + i, Width = 10, Height = 10 };
            nodes[i] = scene.CreateNode(previous[i].ElementTypeId);
            scene.AppendChild(parent, nodes[i]);
        }
        for (int i = 0; i < desired.Length - 1; i++) desired[i] = previous[139 - i];
        desired[^1] = new BoxEl { Key = "new-child", Width = 20, Height = 10 };
        long clock = 0;
        var plan = new ChildReconcilePlan(() => clock++);
        plan.Begin(scene, parent, desired, previous, 0);
        desired[0] = new TextEl("mutated caller buffer");
        bool untouched = true;
        int yields = 0;
        while (!plan.Step(clock + 8))
        {
            yields++;
            untouched &= scene.FirstChild(parent) == nodes[0] && scene.NextSibling(nodes[0]) == nodes[1]
                && scene.IsLive(nodes[0]);
        }
        var reconciler = new TreeReconciler(scene, new StringTable());
        bool committed = plan.Commit(reconciler);
        Check("gate.child-plan-yield-commit", yields > 10 && untouched && committed
            && plan.Phase == ChildReconcilePhase.Committed && scene.FirstChild(parent) == nodes[139]
            && !scene.IsLive(nodes[0]) && scene.NextSibling(nodes[139]) == nodes[138],
            "Yielded keyed matching retains the complete old topology; one validated adapter commit preserves matched identities and removes only departing nodes.");

        var existing = new Element[] { previous[139] };
        var empty = Array.Empty<Element>();
        plan.Begin(scene, parent, empty, existing, 0);
        plan.Step(long.MaxValue);
        var first = scene.FirstChild(parent);
        scene.Detach(first); // invalidate captured topology before commit
        bool commitSucceeded = plan.Commit(reconciler);
        // The sibling re-walk that catches this single-node detach is Diag.CompiledIn-gated (DEBUG/FLUENTGPU_DIAG
        // only — see ChildReconcileCommit.Validate): a stale-topology commit is caught in every dev/CI build, and in
        // Release the O(1) revision check alone does not see a same-revision in-place detach — by design, since the
        // one production caller never yields between Begin/Step and Commit (nothing else can run meanwhile to detach
        // a node), matching the ReuseGuard/BindContract "production safety == CI coverage" posture.
        bool cancelled = Diag.CompiledIn
            ? !commitSucceeded && plan.Phase == ChildReconcilePhase.Cancelled && scene.IsLive(first)
            : commitSucceeded && plan.Phase == ChildReconcilePhase.Committed;
        Check("gate.child-plan-stale-cancel", cancelled,
            $"A stale topology/generation plan cancels before any departing cleanup or prop delivery when the diag-only re-walk is compiled in (compiledIn={Diag.CompiledIn}).");

        plan.Begin(scene, parent, empty, empty, 0);
        plan.Cancel();
        // first's liveness here is whatever the PRECEDING gate legitimately left it as (see its Diag.CompiledIn
        // branch above) — this gate's own subject is the fresh Begin/Cancel pair on empty/empty just above, which
        // must mutate nothing regardless: first must be neither newly freed nor newly resurrected by it.
        Check("gate.child-plan-explicit-cancel",
            plan.Phase == ChildReconcilePhase.Cancelled && scene.IsLive(first) == Diag.CompiledIn,
            "Cancelling pure planning performs no mount, unmount, hook cleanup, or scene mutation.");
        plan.Reset();

        for (int i = 0; i < 8; i++) { plan.Begin(scene, parent, desired, previous, 0); plan.Step(long.MaxValue); plan.Reset(); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) { plan.Begin(scene, parent, desired, previous, 0); plan.Step(long.MaxValue); plan.Reset(); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.child-plan-alloc", allocated == 0,
            $"Retained identity planning allocated {allocated} bytes after warming its input/key buffers.");

        // The non-budgeted production call (TreeReconciler.ReconcilePlannedChildren always passes long.MaxValue):
        // must never sample the clock, on top of the existing zero-alloc bar above. Its own ChildReconcilePlan
        // instance counts calls through the injected Func<long> instead of reusing `plan`'s `clock` counter.
        var bigScene = new SceneStore();
        var bigParent = bigScene.CreateNode(1);
        bigScene.Root = bigParent;
        const int childCount = 128;
        var bigOld = new Element[childCount];
        var bigNew = new Element[childCount];
        for (int i = 0; i < childCount; i++)
        {
            bigOld[i] = new BoxEl { Key = "big-" + i, Width = 10, Height = 10 };
            bigScene.AppendChild(bigParent, bigScene.CreateNode(bigOld[i].ElementTypeId));
        }
        for (int i = 0; i < childCount; i++) bigNew[i] = bigOld[childCount - 1 - i]; // full reorder: worst-case matching work
        int timestampCalls = 0;
        var unbudgeted = new ChildReconcilePlan(() => { timestampCalls++; return 0; });
        for (int i = 0; i < 8; i++) { unbudgeted.Begin(bigScene, bigParent, bigNew, bigOld, 0); unbudgeted.Step(long.MaxValue); unbudgeted.Reset(); } // warm
        timestampCalls = 0;
        long bigBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) { unbudgeted.Begin(bigScene, bigParent, bigNew, bigOld, 0); unbudgeted.Step(long.MaxValue); unbudgeted.Reset(); }
        long bigAllocated = GC.GetAllocatedBytesForCurrentThread() - bigBefore;
        Check("gate.child-plan-nonbudgeted-no-clock",
            timestampCalls == 0 && bigAllocated == 0,
            $"A long.MaxValue deadline is the production, non-budgeted call: over 200 warm reconciles of a {childCount}-child container it must read the clock 0 times (got {timestampCalls}) and allocate 0 bytes (got {bigAllocated}B).");
    }
}
