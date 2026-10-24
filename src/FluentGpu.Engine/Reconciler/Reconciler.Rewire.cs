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
//   • RE-RENDER, bound→static FLIP — the channel is static on the new element: WriteColumns writes the static value
//     through the normal column write (with the layout-shape promotion any static write gets), then RewireBinds UNLINKS
//     the channel's effect from the node's chain and disposes it (unsubscribed; a queued fire is skipped as Disposed).
//   • RE-RENDER, static→bound FLIP — the generated {T}Diff.AnyBindGained finds a channel the new element binds and the
//     previous one carried static; WireGainedBinds wires it through Mount's own BindNode code (filtered to just those
//     channels, no OnRealized replay), runs it once inside the render scope and promotes a layout channel's write.
//   Both flips sit inside the RecordChanged gate (a bound and a static Prop<T> are never equal), so an unchanged element
//   still costs nothing; a changed element without a flip pays one generated IsBound sweep (AnyBindGained).
// Before the re-wire, wiring was mount-only and a reused node kept evaluating its MOUNT closure forever: ProgressBar.Create's
// indicator (`Width = () => clamp(value.Value) * width`) first rendered at a fallback width kept `value × fallback`
// after the parent re-rendered it at the real width, while its static track followed (Wavee's daylist timeline, 86 %).
// Before the flips were handled (2026-10-20), a static↔bound flip silently lost: Wavee's playlist side column mounted at a
// static Width 0, re-rendered with a Width bound to the insights toggle, and stayed 0 (a Debug ShellTests run counted
// ~900 such flips — Fill, Visible, BorderColor, HoverFill, Opacity, Width, Height, TextEl.Color).
public sealed partial class TreeReconciler
{
    /// <summary>Point every bound-channel effect of the reused <paramref name="node"/> at <paramref name="next"/> — the
    /// element it was just reconciled against — re-running exactly the channels whose bind payload changed, and UNWIRING
    /// every channel <paramref name="next"/> carries static (the bound→static flip). Called from <see cref="Update"/>
    /// (after WriteColumns, mirroring Mount's WriteColumns-then-BindNode order; gated by the same DiffProps
    /// <c>RecordChanged</c> that decides the column rewrite, so an unchanged element costs nothing) and from
    /// <see cref="WriteAnchorColumns"/> for a reused anchor's bound <c>Visible</c>. Zero managed allocation: one
    /// dictionary probe, a type test per entry, a static-selector call and two reference compares per binding; a re-run
    /// is the flush's own RunComputation. A layout-writing channel (Width/Height/Text/Spans) whose re-run WROTE is
    /// promoted to a reconcile-time layout-shape change — exactly what the static write of the same channel does via
    /// <see cref="MarkLayoutShape"/> — because this runs INSIDE a render scope, unlike a signal fire.
    /// <para>bound→static: WriteColumns (which ran first) already wrote the static value through the normal column write,
    /// with its own layout-shape promotion; here the effect is unlinked from the node's chain and disposed, which
    /// unsubscribes it from every source. A disposed effect still sitting in a flush queue is skipped by the scheduler
    /// (<c>ReactiveRuntime.Flush</c> and <c>RunComputation*</c> test <c>Disposed</c>), so it can never write again, and
    /// because it left the chain, <see cref="DisposeNodeBindings"/> at unmount never sees it a second time.</para>
    /// Returns true when at least one channel was unwired (the caller's DEBUG <see cref="BindContract"/> cross-check).</summary>
    private bool RewireBinds(NodeHandle node, Element next)
    {
        int idx = (int)node.Raw.Index;
        if (!_nodeBindings.TryGetValue(idx, out var head)) return false;
        bool unwired = false;
        Computation? before = null;
        for (Computation? c = head; c is not null;)
        {
            Computation? after = c.NextNodeBinding;
            if (c is BindEffect bind)   // Show/For/skeleton boundary effects share the table
            {
                int writes = NodeBindingWriteCount;
                BindRewire r = bind.Rewire(next);
                if (r == BindRewire.Reran)
                {
                    if (bind.WritesLayout && NodeBindingWriteCount != writes) _layoutShapeMutated = true;
                }
                else if (r == BindRewire.NowStatic)
                {
                    if (before is null) { if (after is null) _nodeBindings.Remove(idx); else _nodeBindings[idx] = after; }
                    else before.NextNodeBinding = after;
                    c.NextNodeBinding = null;
                    c.Dispose();
                    NodeBindingUnwireCount++;
                    unwired = true;
                    c = after;
                    continue;   // `before` stays: the unlinked effect is no longer in the chain
                }
            }
            before = c;
            c = after;
        }
        return unwired;
    }

    /// <summary>The static→bound flip: <paramref name="next"/> binds channels that <paramref name="prev"/> (the element the
    /// reused <paramref name="node"/> was last reconciled against) carried static, so the node has no effect for them yet.
    /// Wires exactly those channels through Mount's own <see cref="BindNode"/> code (the <paramref name="prev"/> filter
    /// skips every channel that already has an effect and every mount-only callback such as <c>OnRealized</c>); each new
    /// effect runs once, now, inside the render scope. A layout channel (Width/Height/Text/Spans) that wrote is promoted
    /// to a layout-shape change, as <see cref="RewireBinds"/> does for a re-run. Allocates what mount allocates for those
    /// channels; reached only when the generated <c>{T}Diff.AnyBindGained</c> found such a channel.</summary>
    private void WireGainedBinds(NodeHandle node, Element next, Element prev)
    {
        int writes = NodeBindingWriteCount;
        BindNode(node, next, prev);
        NodeBindingWireCount++;
        if (NodeBindingWriteCount != writes && GainsLayoutChannel(next, prev)) _layoutShapeMutated = true;
    }

    /// <summary>Did <paramref name="next"/> bind a LAYOUT channel <paramref name="prev"/> carried static?</summary>
    private static bool GainsLayoutChannel(Element next, Element prev) => next switch
    {
        BoxEl b => prev is BoxEl p && ((b.Width.IsBound && !p.Width.IsBound) || (b.Height.IsBound && !p.Height.IsBound)),
        TextEl t => prev is TextEl p && t.Text.IsBound && !p.Text.IsBound,
        SpanTextEl s => prev is SpanTextEl p && s.Spans.IsBound && !p.Spans.IsBound,
        _ => false,
    };

    /// <summary>Generated static→bound detector per leaf element type (<c>{T}Diff.AnyBindGained</c>); a type mismatch is a
    /// replace, never a flip. Kinds without a generated diff (Scroll/VirtualList/anchors) test their only channel,
    /// <c>Visible</c>, at their own Update branch.</summary>
    private static bool AnyBindGained(Element next, Element prev) => next switch
    {
        BoxEl x => prev is BoxEl y && BoxElDiff.AnyBindGained(x, y),
        TextEl x => prev is TextEl y && TextElDiff.AnyBindGained(x, y),
        GridEl x => prev is GridEl y && GridElDiff.AnyBindGained(x, y),
        ImageEl x => prev is ImageEl y && ImageElDiff.AnyBindGained(x, y),
        IconLayerEl x => prev is IconLayerEl y && IconLayerElDiff.AnyBindGained(x, y),
        SpanTextEl x => prev is SpanTextEl y && SpanTextElDiff.AnyBindGained(x, y),
        ListRowEl x => prev is ListRowEl y && ListRowElDiff.AnyBindGained(x, y),
        PolylineStrokeEl x => prev is PolylineStrokeEl y && PolylineStrokeElDiff.AnyBindGained(x, y),
        PathEl x => prev is PathEl y && PathElDiff.AnyBindGained(x, y),
        SeriesEl x => prev is SeriesEl y && SeriesElDiff.AnyBindGained(x, y),
        SpriteFieldEl x => prev is SpriteFieldEl y && SpriteFieldElDiff.AnyBindGained(x, y),
        _ => next.Visible.IsBound && !prev.Visible.IsBound,
    };

    /// <summary>DEBUG (<see cref="BindContract"/>): after a flip, the node must carry exactly one BindEffect per bound
    /// <c>Prop&lt;T&gt;</c> channel of <paramref name="el"/>. A mismatch means a channel the reconciler does not wire (or
    /// wired twice) — reported, never silently lost.</summary>
    private void CheckBindingShape(NodeHandle node, Element el)
    {
        int expected = el switch
        {
            BoxEl x => BoxElDiff.BoundChannelCount(x),
            TextEl x => TextElDiff.BoundChannelCount(x),
            GridEl x => GridElDiff.BoundChannelCount(x),
            ImageEl x => ImageElDiff.BoundChannelCount(x),
            IconLayerEl x => IconLayerElDiff.BoundChannelCount(x),
            SpanTextEl x => SpanTextElDiff.BoundChannelCount(x),
            ListRowEl x => ListRowElDiff.BoundChannelCount(x),
            PolylineStrokeEl x => PolylineStrokeElDiff.BoundChannelCount(x),
            PathEl x => PathElDiff.BoundChannelCount(x),
            SeriesEl x => SeriesElDiff.BoundChannelCount(x),
            SpriteFieldEl x => SpriteFieldElDiff.BoundChannelCount(x),
            _ => -1,
        };
        if (expected < 0) return;
        int actual = BindEffectCount(node);
        if (actual != expected) BindContract.Unwired(el.GetType().Name, expected, actual);
    }

    /// <summary>The number of <see cref="BindEffect"/>s on <paramref name="node"/>'s binding chain (tests + the DEBUG
    /// cross-check; control-flow boundary effects sharing the chain are not counted).</summary>
    internal int BindEffectCount(NodeHandle node)
    {
        int n = 0;
        if (_nodeBindings.TryGetValue((int)node.Raw.Index, out var head))
            for (var c = head; c is not null; c = c.NextNodeBinding)
                if (c is BindEffect) n++;
        return n;
    }

    /// <summary>Total <see cref="BindEffect"/>s across every node (tests: a flip must never leak or stack an effect).</summary>
    internal int TotalBindEffectCount
    {
        get
        {
            int n = 0;
            foreach (var head in _nodeBindings.Values)
                for (var c = head; c is not null; c = c.NextNodeBinding)
                    if (c is BindEffect) n++;
            return n;
        }
    }

    /// <summary>Always-on counters: bound→static unwires (<see cref="RewireBinds"/>) and static→bound wire passes
    /// (<see cref="WireGainedBinds"/>) since construction. Plain increments, off the unchanged path.</summary>
    public int NodeBindingUnwireCount { get; private set; }
    /// <inheritdoc cref="NodeBindingUnwireCount"/>
    public int NodeBindingWireCount { get; private set; }
}

/// <summary>What <see cref="BindEffect.Rewire"/> did with a re-rendered element's channel.</summary>
internal enum BindRewire : byte
{
    /// <summary>Still bound with an equal payload (or adopted the new element only): nothing re-ran.</summary>
    Unchanged,
    /// <summary>Still bound with a different thunk/signal: the source was swapped and the body re-ran once.</summary>
    Reran,
    /// <summary>The channel is STATIC on the new element (a bound→static flip): the caller unlinks and disposes it.</summary>
    NowStatic,
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
        Started = true;
        return this;
    }

    /// <summary>The closure-free form: the effect carries its <paramref name="node"/> and <paramref name="rec"/>, and
    /// <paramref name="fire"/> is a STATIC lambda (compiler-cached) that calls the reconciler's channel method with this effect.
    /// One object per binding instead of three (effect, closure, delegate) — page-mount RCA, allocations.</summary>
    internal BindEffect Start(TreeReconciler rec, NodeHandle node, Action<BindEffect> fire)
    {
        Rec = rec;
        Node = node;
        _fire = fire;
        RunStale();
        Started = true;
        return this;
    }

    private Action<BindEffect>? _fire;
    /// <summary>The reconciler and node a closure-free binding fires into (<see cref="Start(TreeReconciler, NodeHandle, Action{BindEffect})"/>).</summary>
    internal TreeReconciler? Rec;
    internal NodeHandle Node;
    /// <summary>A channel's own first-write latch (the bound Width/Height "primed" state the closures used to capture).</summary>
    internal bool Primed;

    private protected sealed override void RunSelfBody() => _fire!(this);

    /// <summary>The mount run is over: every later run re-writes a channel that already shows a value (E20: a colour channel
    /// re-fired inside the live theme fade cross-fades from it; the mount run has nothing on screen to fade from).</summary>
    internal bool Started { get; private set; }

    private protected sealed override void OnStale() => Runtime.Schedule(this);

    internal sealed override void RunStale()
    {
        FluentGpu.Hosting.MountProf.Enter(FluentGpu.Hosting.MountProf.BindFire);
        try { if (_fire is null) RunComputation(_body!); else RunComputationSelf(); } finally { FluentGpu.Hosting.MountProf.Exit(); }
    }

    /// <summary>Re-point this binding at the same channel of <paramref name="next"/>. A still-bound channel adopts
    /// <paramref name="next"/> as <see cref="El"/>; if its payload also differs (by <c>Prop&lt;T&gt;</c> equality) the
    /// source is swapped and the body re-runs once (re-tracking its dependencies) — <see cref="BindRewire.Reran"/>. A
    /// channel that is STATIC on <paramref name="next"/> (a bound→static flip) is left untouched and reported as
    /// <see cref="BindRewire.NowStatic"/>: the reconciler unlinks and disposes it (<c>TreeReconciler.RewireBinds</c>).</summary>
    internal abstract BindRewire Rewire(Element next);
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

    internal override BindRewire Rewire(Element next)
    {
        Prop<T> p = _channel(next);
        if (!p.IsBound) return BindRewire.NowStatic;
        El = next;
        Func<T>? thunk = p.Thunk;
        IReadSignal<T>? signal = p.Signal;
        if (object.Equals(thunk, _thunk) && object.Equals(signal, _signal)) return BindRewire.Unchanged;
        _thunk = thunk;
        _signal = signal;
        RunStale();
        return BindRewire.Reran;
    }
}
