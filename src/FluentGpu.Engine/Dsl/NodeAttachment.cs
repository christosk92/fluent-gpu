using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Dsl;

/// <summary>
/// A per-node behaviour a <see cref="BoxEl"/> carries WITHOUT a component (<see cref="BoxEl.Attachment"/>; PERF r3 UI). A component
/// costs a scene anchor node, a render context, a reactive scope and render effect, closures and hook cells per instance; a
/// behaviour that only needs the box's own input handlers and some state that exists only once the user touches it (a tooltip,
/// which is idle on every element it wraps but the one under the pointer) is cheaper as an attachment: an immutable spec on the
/// element, and per node one small <see cref="NodeAttachmentState"/> the reconciler keeps for as long as the node lives.
/// <para>Lifecycle (reconciler): when a box with an attachment is written (mount, or an update that changed the box), the spec's
/// <see cref="Bind"/> gets the node and the state an earlier spec of the SAME type left there (null on mount); it returns the
/// state to keep. The state's handler fields are the box's effective handlers wherever the box sets none of its own, so the
/// handler-mask bits and the pointer flags come out exactly as they would for a box declaring them. A box that drops the
/// attachment, takes one of another type, or leaves the tree detaches the state (<see cref="NodeAttachmentState.Detach"/>).</para>
/// <para>Specs are records: a re-render that builds an EQUAL spec leaves the box unchanged and nothing is re-bound.</para>
/// </summary>
public abstract record NodeAttachment
{
    /// <summary>Bind this spec to <paramref name="node"/>; <paramref name="state"/> is the state an earlier spec of the same type
    /// left on it (null on mount). Return the state the node keeps (usually <paramref name="state"/> updated, or a new one).</summary>
    protected internal abstract NodeAttachmentState Bind(NodeHandle node, NodeAttachmentState? state, IAttachmentHost host);
}

/// <summary>The per-node half of a <see cref="NodeAttachment"/>: the handlers the box gets from it, and whatever state the
/// behaviour keeps. Created by <see cref="NodeAttachment.Bind"/>; detached by the reconciler.</summary>
public abstract class NodeAttachmentState
{
    /// <summary>Handlers merged into the box (each used only where the box declares none of its own). Set them in
    /// <see cref="NodeAttachment.Bind"/>; the reconciler reads them right after it returns.</summary>
    public Action<Point2>? OnHoverMove;
    public Action? OnPointerExit;
    public Action<PointerEventArgs>? OnPointerPressed;
    public Action<bool>? OnFocusChanged;

    /// <summary>The node is leaving the tree, or its box dropped this attachment or took one of another type. UI thread, during
    /// the reconcile that unmounts it (where a component's unmount cleanups run).</summary>
    protected internal virtual void Detach() { }
}

/// <summary>What the reconciler offers an attachment (UI thread only).</summary>
public interface IAttachmentHost
{
    SceneStore Scene { get; }
    ReactiveRuntime Runtime { get; }
    /// <summary>The value the nearest provider of <paramref name="context"/> above <paramref name="node"/> holds now - what
    /// <c>UseContext</c> reads for a component anchored at the node's place - without subscribing anything.</summary>
    T PeekContext<T>(NodeHandle node, Context<T> context);
    /// <summary>Run <paramref name="action"/> with this frame's passive effects (phase 12: where a component's <c>UseEffect</c> runs).</summary>
    void EnqueuePassive(Action action);
}
