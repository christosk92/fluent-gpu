using System;
using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Hooks;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  Measured-bounds hooks — expose a component's own laid-out size as a signal, without an OnBoundsChanged closure.
//
//  Node semantics: the OBSERVED node is HostNode — this component's RENDERED ROOT (FirstChild of the layout-transparent
//  anchor), the node the animation hooks target. So UseMeasuredBounds/Width reports "the bounds of what *I* rendered",
//  NOT the parent slot the anchor occupies. (The anchor is layout-transparent and mirrors the child's participation, so
//  there is no separate "my host slot" rect to report — the rendered root IS the measurable box.)
//
//  Timing: registration is a MOUNT-ONCE layout effect (phase 6.5, after layout, when HostNode's Bounds are resolved). It
//  seeds the signal from the live bounds THEN, so a value written during the LAYOUT phase only MarksStale — the consumer
//  re-renders NEXT frame (never re-entrant). ⇒ a same-frame layout-effect reading the signal sees the PREVIOUS value.
//
//  Composition: the handler lives in SceneStore's SEPARATE hook slot (AddBoundsChangedHook), so an element's own
//  Element.OnBoundsChanged on the same node still fires — the reconciler-clobbered author slot and the hook slot are
//  dispatched independently by FlexLayout. Zero steady-state alloc: register once; the edge-triggered handler writes an
//  equality-gated signal (no write, hence no notify, when the value is unchanged).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Backs <see cref="RenderContext.UseMeasuredBounds"/> / <see cref="RenderContext.UseMeasuredWidth"/>: holds the
/// output signal + a stable handler installed in the host node's hook bounds-changed slot, removed on unmount.</summary>
internal sealed class MeasuredBoundsCell : HookCell, IDisposableCell
{
    private readonly RenderContext _ctx;
    public Signal<RectF>? BoundsSig;
    public FloatSignal? WidthSig;
    public float Quantum;

    public readonly Action Register;              // mount-once layout-effect body: seed + install (HostNode valid at 6.5)
    private readonly Action<RectF> _onBounds;     // stable handler in the SceneStore hook slot
    private NodeHandle _node;
    private bool _installed;

    // DEBUG oscillation tripwire (feedback-loop hint): count consecutive frame-times on which the measured value changed.
    private HostTimerQueue? _clock;
    private double _lastChangeMs;
    private int _run;
    private bool _warned;

    public MeasuredBoundsCell(RenderContext ctx)
    {
        _ctx = ctx;
        Register = DoRegister;
        _onBounds = OnBounds;
    }

    private void DoRegister()
    {
        var node = _ctx.HostNode;
        var scene = _ctx.Scene;
        if (scene is null || node.IsNull || !scene.IsLive(node)) return;
        _node = node;
        if (Diag.CompiledIn) _clock = _ctx.ResolveFrameClockForMeasure();   // frame clock — only read by the DEBUG tripwire
        RectF cur = scene.Bounds(node);                 // the arranged LOCAL rect (valid post-layout)
        scene.BoundsDeliveredRef(node) = cur;           // align the edge baseline so the next arrange doesn't re-fire the seed
        Write(cur);                                     // seed the initial value (MarkStale → consumer re-renders NEXT frame)
        scene.AddBoundsChangedHook(node, _onBounds);
        _installed = true;
    }

    private void OnBounds(RectF r) => Write(r);

    private void Write(RectF r)
    {
        bool changed;
        if (WidthSig is { } ws)
        {
            float w = r.W;
            if (Quantum > 0f) w = MathF.Round(w / Quantum) * Quantum;   // round to the quantum; the signal's exact-compare then coalesces sub-quantum churn
            changed = ws.SetIfChanged(w);
        }
        else changed = BoundsSig!.SetIfChanged(r);     // RectF is a record struct ⇒ value-equality coalesces no-change writes
        if (Diag.CompiledIn && changed) Tripwire();
    }

    private void Tripwire()
    {
        if (!Diag.Enabled || _warned) return;
        double now = _clock?.NowMs ?? 0.0;
        _run = (now - _lastChangeMs) <= 34.0 ? _run + 1 : 1;   // ~2 frame-times @60fps apart ⇒ "consecutive frames"
        _lastChangeMs = now;
        if (_run > 8)
        {
            _warned = true;
            Diag.Event("layout", $"measured-bounds feedback loop? node #{_node.Raw.Index} changed its measured bounds {_run} frames running " +
                "— a layout that reads its own measured size and resizes itself. Break the cycle (measure an OUTER fixed node) or add a quantum.");
        }
    }

    public void DisposeCell()
    {
        if (_installed && _ctx.Scene is { } s) s.RemoveBoundsChangedHook(_node, _onBounds);
        _installed = false;
    }
}

public sealed partial class RenderContext
{
    internal HostTimerQueue? ResolveFrameClockForMeasure() => ResolveTimers();

    /// <summary>The arranged LOCAL bounds of this component's rendered root (<see cref="HostNode"/>) as a read signal —
    /// updated whenever layout re-arranges it. Wiring: a mount-once layout-effect registers on the node's bounds-changed
    /// column (composed with any element-declared <c>OnBoundsChanged</c>, not clobbering it). The value is written during
    /// the layout phase, so a same-frame layout-effect sees the PREVIOUS value and the consumer re-renders NEXT frame (no
    /// re-entrancy). Equality-gated: no re-render while the bounds are stable. Prefer <see cref="UseMeasuredWidth"/> when
    /// only the width matters (a scalar signal + quantum kills sub-pixel churn).</summary>
    public IReadSignal<RectF> UseMeasuredBounds([CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        MeasuredBoundsCell cell;
        if (idx < 0)
        {
            cell = new MeasuredBoundsCell(this) { BoundsSig = new Signal<RectF>(default) };
            RegisterCell(__k, cell, cleanupCapable: true);
            EnqueueEffect(PendingLayoutEffects, cell.Register);   // runs once at phase 6.5 (HostNode valid) then is cleared
        }
        else cell = (MeasuredBoundsCell)_cells[idx];
        return cell.BoundsSig!;
    }

    /// <summary>The arranged width of this component's rendered root (<see cref="HostNode"/>) as a read signal — the
    /// width-only, hot-path form of <see cref="UseMeasuredBounds"/>. When <paramref name="quantum"/> &gt; 0 the width is
    /// rounded to that grid before the (exact-compare) signal write, so sub-quantum layout jitter never re-renders the
    /// consumer (e.g. quantum 4 ⇒ a &lt;4px wobble is absorbed). Same timing contract as <see cref="UseMeasuredBounds"/>:
    /// written during layout, consumer re-renders NEXT frame; a same-frame layout-effect sees the previous value.</summary>
    public IReadSignal<float> UseMeasuredWidth(float quantum = 0f, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        MeasuredBoundsCell cell;
        if (idx < 0)
        {
            cell = new MeasuredBoundsCell(this) { WidthSig = new FloatSignal(0f), Quantum = MathF.Max(quantum, 0f) };
            RegisterCell(__k, cell, cleanupCapable: true);
            EnqueueEffect(PendingLayoutEffects, cell.Register);
        }
        else cell = (MeasuredBoundsCell)_cells[idx];
        return cell.WidthSig!;
    }
}
