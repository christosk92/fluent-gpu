using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Reconciler;

// P2 (Operation ultra-fast GPU engine, text.md rtb-01 "bound spans"): SpanTextEl.Spans : Prop<TextSpans> — the bound
// span-paragraph channel. Mirrors TextEl.Text's bound effect (Reconciler.cs BindNode) exactly: one BindEffect wired at
// mount and re-wired in place when a re-render binds a new thunk/signal (Reconciler.Rewire.cs), equality/shaping-gated
// through the SAME WriteSpanText the static WriteColumns path uses (Reconciler.cs), counted by NodeBindingFireCount/WriteCount (the P0 counters). A row template that refills its SpanBuffer with
// content identical to the last fire (a recycle onto a row whose visible content happens not to have changed, or a
// re-render that produced value-equal spans) re-shapes NOTHING — WriteSpanText's shaping gate is the same one the
// static path relies on (gate.spans.shaping-gate-keeps-run).
public sealed partial class TreeReconciler
{
    /// <summary>Bound Spans wiring — called from BindNode for a SpanTextEl whose <c>Spans</c> channel is bound
    /// (<c>Prop.Of(() =&gt; buffer.Current)</c> or the P3 <c>item.Spans(...)</c> sugar). Every fire re-resolves the
    /// current <see cref="TextSpans"/> view, runs it through <see cref="TreeReconciler.WriteSpanText"/> (same
    /// shaping-gate + run-mint + scene-copy discipline as the static path), and folds the resolved run id back into
    /// <see cref="LayoutInput.TextStyle"/> — the only column this bind owns; every other TextStyle axis (size/weight/
    /// wrap/…) is <c>SpanTextEl</c>'s STATIC properties, already written once by WriteColumns before BindNode runs.</summary>
    private void BindSpanText(NodeHandle node, SpanTextEl st)
    {
        if (!st.Spans.IsBound) return;
        // WritesLayout: a re-wire's re-run happens inside a render scope, so a re-shape it causes is promoted to a
        // layout-shape change exactly like the static path's inRenderScope:true (RewireBinds).
        var fx = new BindEffect<TextSpans>(Runtime, st, static e => e is SpanTextEl x ? x.Spans : default) { WritesLayout = true };
        AddBinding(node, fx.Start(() =>
        {
            NodeBindingFireCount++;
            if (!_scene.IsLive(node)) return;
            var current = fx.Read();
            // OverflowSuffix is a static companion — read off the element this node was LAST reconciled against.
            int runId = WriteSpanText(node, current.AsSpan(), ((SpanTextEl)fx.El).OverflowSuffix, inRenderScope: false);
            ref LayoutInput li = ref _scene.Layout(node);
            if (li.TextStyle.SpanRunId != runId)
            {
                li.TextStyle = li.TextStyle with { SpanRunId = runId };
                NodeBindingWriteCount++;
            }
        }));
    }
}
