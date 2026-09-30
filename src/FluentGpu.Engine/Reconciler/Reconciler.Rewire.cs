using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Reconciler;

// The bind contract (reconciler-hooks.md §0bis "Fine-grained bindings", 2026-09-30 re-wire):
//   • MOUNT — BindNode wires every BOUND Prop<T> channel into one BindEffect<T> (the effect IS the binding: it replaces
//     the former `new Effect(...)` one-for-one, so mount allocates what it always did).
//   • RE-RENDER, bound→bound — a reused node reconciled against an element that binds the same channel again with a
//     DIFFERENT payload (a fresh lambda capturing new render-time values, or another signal instance) is RE-WIRED: the
//     effect swaps its source and re-runs once, and RunComputation's unlink/re-track re-subscribes it to whatever the new
//     thunk reads. An EQUAL payload (Prop<T> equality — the same delegate/signal, exactly what the generated
//     {T}Diff.AnyChanged already compares) re-runs nothing.
//   • RE-RENDER, static↔bound FLIP — unchanged and NOT handled here: the mount bind keeps owning the channel and the new
//     form silently loses. BindContract (DEBUG) flags it at the Update seam; keep a channel's shape stable or re-key.
// Before this, wiring was mount-only and a reused node kept evaluating its MOUNT closure forever: ProgressBar.Create's
// indicator (`Width = () => clamp(value.Value) * width`) first rendered at a fallback width kept `value × fallback`
// after the parent re-rendered it at the real width, while its static track followed (Wavee's daylist timeline, 86 %).
public sealed partial class TreeReconciler
{
    /// <summary>Point every bound-channel effect of the reused <paramref name="node"/> at <paramref name="next"/> — the
    /// element it was just reconciled against — re-running exactly the channels whose bind payload changed. Called from
    /// <see cref="Update"/> (after WriteColumns, mirroring Mount's WriteColumns-then-BindNode order; gated by the same
    /// DiffProps <c>RecordChanged</c> that decides the column rewrite, so an unchanged element costs nothing) and from
    /// <see cref="WriteAnchorColumns"/> for a reused component anchor's bound <c>Visible</c>. Zero managed allocation:
    /// one dictionary probe, a type test per entry, a static-selector call and two reference compares per binding; a
    /// re-run is the flush's own RunComputation. A layout-writing channel (Width/Height/Text/Spans) whose re-run WROTE is
    /// promoted to a reconcile-time layout-shape change — exactly what the static write of the same channel does via
    /// <see cref="MarkLayoutShape"/> — because this runs INSIDE a render scope, unlike a signal fire.</summary>
    private void RewireBinds(NodeHandle node, Element next)
    {
        if (!_nodeBindings.TryGetValue((int)node.Raw.Index, out var list)) return;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not BindEffect bind) continue;   // Show/For/skeleton boundary effects share the table
            int writes = NodeBindingWriteCount;
            if (bind.Rewire(next) && bind.WritesLayout && NodeBindingWriteCount != writes) _layoutShapeMutated = true;
        }
    }
}

/// <summary>One bound element channel's reactive effect — the unit <c>TreeReconciler.BindNode</c> wires per bound
/// <c>Prop&lt;T&gt;</c>. The body reads the channel's SOURCE through this object (<see cref="BindEffect{T}.Read"/>) and
/// the channel's STATIC companions through <see cref="El"/>, never through mount-captured locals, so the reconciler can
/// re-point a reused node's binding at a re-rendered element (<see cref="Rewire"/>) without re-allocating anything.</summary>
internal abstract class BindEffect : Computation
{
    private Action? _body;

    private protected BindEffect(ReactiveRuntime runtime, Element el) : base(runtime, owner: null) => El = el;

    /// <summary>The element this channel was last reconciled against (the mount element until the first re-wire). Bodies
    /// read static companions here — an ImageEl's decode target/BlurHash/effects, a SpanTextEl's OverflowSuffix, a
    /// ListRowEl's placeholder color and click flag, the declared Enter a bound Visible seeds — so a re-run (or a later
    /// signal fire) applies the CURRENT element's companions, never the mount element's stale ones. Updated by every
    /// <see cref="Rewire"/> whose element still binds the channel.</summary>
    internal Element El { get; private protected set; }

    /// <summary>The channel writes a LAYOUT column (Width/Height/Text/Spans): a re-wire that wrote raises the enclosing
    /// render scope's layout-shape bit (<c>TreeReconciler.RewireBinds</c>).</summary>
    internal bool WritesLayout { get; init; }

    /// <summary>Arm the body and run it once — the mount <c>runNow</c>. Separate from construction so the body closure can
    /// capture this effect (it reads <see cref="BindEffect{T}.Read"/> and <see cref="El"/> through it).</summary>
    internal BindEffect Start(Action body)
    {
        _body = body;
        RunStale();
        return this;
    }

    private protected sealed override void OnStale() => Runtime.Schedule(this);

    internal sealed override void RunStale() => RunComputation(_body!);

    /// <summary>Re-point this binding at the same channel of <paramref name="next"/>. A still-bound channel adopts
    /// <paramref name="next"/> as <see cref="El"/>; if its payload also differs (by <c>Prop&lt;T&gt;</c> equality) the
    /// source is swapped and the body re-runs once (re-tracking its dependencies) — returns true. A channel that is
    /// STATIC on <paramref name="next"/> (a bound→static flip) is left untouched: BindContract's domain.</summary>
    internal abstract bool Rewire(Element next);
}

/// <summary>The typed half of <see cref="BindEffect"/>: holds the channel's current thunk-or-signal payload and the
/// static selector that reads the same channel off a re-rendered element.</summary>
internal sealed class BindEffect<T> : BindEffect
{
    private readonly Func<Element, Prop<T>> _channel;   // a static lambda (compiler-cached) — never allocates per call
    private Func<T>? _thunk;
    private IReadSignal<T>? _signal;

    /// <param name="channel">Reads THIS channel off an element of the bound node's type, returning <c>default</c> (a
    /// static, unbound <c>Prop&lt;T&gt;</c>) for any other type — so a mismatched element can never re-wire.</param>
    internal BindEffect(ReactiveRuntime runtime, Element el, Func<Element, Prop<T>> channel) : base(runtime, el)
    {
        _channel = channel;
        Prop<T> p = channel(el);
        _thunk = p.Thunk;
        _signal = p.Signal;
    }

    /// <summary>The channel's current value. Read it INSIDE the body: invoking the thunk / reading the signal there is
    /// what subscribes this effect to the current source.</summary>
    internal T Read() => _thunk is { } thunk ? thunk() : _signal!.Value;

    internal override bool Rewire(Element next)
    {
        Prop<T> p = _channel(next);
        if (!p.IsBound) return false;
        El = next;
        Func<T>? thunk = p.Thunk;
        IReadSignal<T>? signal = p.Signal;
        if (object.Equals(thunk, _thunk) && object.Equals(signal, _signal)) return false;
        _thunk = thunk;
        _signal = signal;
        RunStale();
        return true;
    }
}
