using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Reconciler;

// ── BoxEl.Attachment: per-node behaviours without a component (PERF r3 UI; Dsl/NodeAttachment.cs) ──────────────────────────
//
// WriteColumns binds a box's attachment spec to its node before it writes the handler columns (Reconciler.cs, the BoxEl handler
// block): the per-node state the spec returns is kept here, keyed by the node's index and checked against its generation, and its
// handlers are merged into the box's. UnmountSubtreeCore detaches it with the node's other index-keyed rows. A box that drops its
// attachment, or takes one of another type, detaches the old state first.
public sealed partial class TreeReconciler : IAttachmentHost
{
    private readonly Dictionary<int, (NodeHandle Node, Type Spec, NodeAttachmentState State)> _attachments = new();
    private RenderContext? _attachmentEffects;   // the passive-effect queue attachments share (registered like a component's)

    private NodeAttachmentState? BindAttachment(NodeHandle node, NodeAttachment? spec)
    {
        int idx = (int)node.Raw.Index;
        NodeAttachmentState? state = null;
        if (_attachments.TryGetValue(idx, out var row))
        {
            // A recycled index (another node), no spec any more, or a spec of another type: the old state is detached first.
            if (row.Node != node || spec is null || row.Spec != spec.GetType()) DetachAttachment(idx);
            else state = row.State;
        }
        if (spec is null) return null;
        var bound = spec.Bind(node, state, this);
        if (state is not null && !ReferenceEquals(bound, state)) state.Detach();
        _attachments[idx] = (node, spec.GetType(), bound);
        return bound;
    }

    private void DetachAttachment(int idx)
    {
        if (_attachments.Remove(idx, out var row)) row.State.Detach();
    }

    SceneStore IAttachmentHost.Scene => _scene;
    ReactiveRuntime IAttachmentHost.Runtime => Runtime;

    T IAttachmentHost.PeekContext<T>(NodeHandle node, Context<T> context)
        => ResolveContext(node, context)?.Peek() is T value ? value : context.Default;

    void IAttachmentHost.EnqueuePassive(Action action)
    {
        var ctx = _attachmentEffects ??= new RenderContext();
        if (ctx.PendingEffects.Count == 0) RegisterPendingEffectContext?.Invoke(ctx, false);
        ctx.PendingEffects.Add(action);
    }
}
