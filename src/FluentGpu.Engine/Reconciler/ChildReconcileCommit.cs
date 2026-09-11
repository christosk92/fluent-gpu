using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Reconciler;

public sealed partial class TreeReconciler : IChildReconcileCommitter
{
    private readonly Stack<ChildReconcilePlan> _childPlans = new();
    private ulong _childReconcileRevision;

    private void ReconcilePlannedChildren(NodeHandle parent, ReadOnlySpan<Element> desired, ReadOnlySpan<Element> previous)
    {
        // Nested Update/Mount can reconcile another large container: each active invocation leases a separate plan.
        var plan = _childPlans.Count == 0 ? new ChildReconcilePlan() : _childPlans.Pop();
        try
        {
            plan.Begin(_scene, parent, desired, previous, _childReconcileRevision);
            plan.Step(long.MaxValue);
            if (!plan.Commit(this)) throw new InvalidOperationException("Committed child topology changed during isolated planning.");
        }
        finally { plan.Reset(); _childPlans.Push(plan); }
    }

    bool IChildReconcileCommitter.Validate(NodeHandle parent, ulong revision, ReadOnlySpan<NodeHandle> originalNodes)
    {
        if (revision != _childReconcileRevision || !_scene.IsLive(parent)) return false;
        // The O(n) sibling re-walk below exists to catch topology drift between Begin/Step and Commit — a real
        // hazard ONLY for a caller that yields Step across frames (something else runs on the UI thread meanwhile).
        // The one production caller, ReconcilePlannedChildren, never yields: Begin/Step(long.MaxValue)/Commit run
        // back to back with no interleaving point, so the revision check above is already exhaustive there and the
        // walk is pure re-verification cost on every large-container render. Gated like ReuseGuard/BindContract
        // (Diag.CompiledIn — DEBUG or FLUENTGPU_DIAG): dead-code eliminated from the Release binary; CI (Debug) still
        // exercises the full check, which is where a future budgeted caller's regression would be caught.
#pragma warning disable CS0162 // const CompiledIn folds: exactly one of these arms is live per build config
        if (!Diag.CompiledIn) return true;
        var child = _scene.FirstChild(parent);
        foreach (var expected in originalNodes)
        {
            if (expected.IsNull || child != expected || !_scene.IsLive(child)) return false;
            child = _scene.NextSibling(child);
        }
        return child.IsNull;
#pragma warning restore CS0162
    }
    void IChildReconcileCommitter.Update(NodeHandle node, Element desired, Element previous) => Update(node, desired, previous);
    void IChildReconcileCommitter.SaveDeparting(NodeHandle node) => PreSaveScroll(node);
    NodeHandle IChildReconcileCommitter.Mount(NodeHandle parent, Element desired)
    {
        var child = _scene.CreateNode(desired.ElementTypeId);
        _scene.AppendChild(parent, child); // Context resolution requires logical ancestry before an indivisible mount.
        Mount(child, desired);
        return child;
    }
    void IChildReconcileCommitter.Remove(NodeHandle node) => Remove(node);
    void IChildReconcileCommitter.PublishOrder(NodeHandle parent, ReadOnlySpan<NodeHandle> children, bool changed)
    {
        foreach (var child in children) { _scene.Detach(child); _scene.AppendChild(parent, child); }
        if (changed) MarkLayoutShape(parent);
    }
}
