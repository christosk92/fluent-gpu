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
//  Timing: registration is a layout effect (phase 6.5, after layout, when HostNode's Bounds are resolved) that runs at mount and again whenever a root-type change remounts HostNode (RenderContext.SetHostNode). It
//  seeds the signal from the live bounds THEN, so a value written during the LAYOUT phase only MarksStale — the consumer
//  re-renders NEXT frame (never re-entrant). ⇒ a same-frame layout-effect reading the signal sees the PREVIOUS value.
//
//  Composition: the handler lives in SceneStore's SEPARATE hook slot (AddBoundsChangedHook), so an element's own
//  Element.OnBoundsChanged on the same node still fires — the reconciler-clobbered author slot and the hook slot are
//  dispatched independently by FlexLayout. Zero steady-state alloc: register once; the edge-triggered handler writes an
//  equality-gated signal (no write, hence no notify, when the value is unchanged).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Backs <see cref="RenderContext.UseMeasuredBounds(string?,int)"/> / <see cref="RenderContext.UseMeasuredWidth(float,string?,int)"/> (and their keyed overloads): holds the
/// output signal + a stable handler installed in the host node's hook bounds-changed slot, removed on unmount.</summary>
internal sealed class MeasuredBoundsCell : HookCell, IDisposableCell
{
    private readonly RenderContext _ctx;
    public Signal<RectF>? BoundsSig;
    public FloatSignal? WidthSig;
    public float Quantum;
    // E17c: the full MeasureMemory key of a keyed hook (null = unkeyed), and the call site that names an unkeyed one in a
    // settle's journal (the caller-info literal: no allocation).
    public string? MemoryKey;
    public string? SiteFile;
    public int SiteLine;

    public readonly Action Register;              // layout-effect body (mount + root swap): seed + install (HostNode valid at 6.5)
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
        if (scene is null) return;
        if (_installed)
        {
            if (_node == node) return;                           // mount + the first SetHostNode both enqueue this
            scene.RemoveBoundsChangedHook(_node, _onBounds);     // root swapped: leave the old node (no-op once it is freed)
            _installed = false;
        }
        if (node.IsNull || !scene.IsLive(node)) return;
        _node = node;
        // Frame clock — read ONLY by the DEBUG tripwire below, which itself early-outs on !Diag.Enabled. Both operands
        // are deliberate: the const CompiledIn is what folds this away in release, the non-const Enabled is what keeps
        // the guard a non-constant expression (so the folded body is not reported as CS0162 unreachable code).
        if (Diag.CompiledIn && Diag.Enabled) _clock = _ctx.ResolveFrameClockForMeasure();
        RectF cur = scene.Bounds(node);                 // the arranged LOCAL rect (valid post-layout)
        scene.BoundsDeliveredRef(node) = cur;           // align the edge baseline so the next arrange doesn't re-fire the seed
        Write(cur);                                     // seed the initial value (MarkDirty → consumer re-renders NEXT frame)
        scene.AddBoundsChangedHook(node, _onBounds);
        _installed = true;
    }

    private void OnBounds(RectF r) => Write(r);

    /// <summary>The value a hook's signal holds for <paramref name="r"/> (the width hook rounds to its quantum).</summary>
    internal float QuantizeWidth(float w) => Quantum > 0f ? MathF.Round(w / Quantum) * Quantum : w;

    private void Write(RectF r)
    {
        bool changed;
        if (WidthSig is { } ws)
            changed = ws.SetIfChanged(QuantizeWidth(r.W));   // round to the quantum; the signal's exact-compare then coalesces sub-quantum churn
        else changed = BoundsSig!.SetIfChanged(r);     // RectF is a record struct ⇒ value-equality coalesces no-change writes
        // E17c: a keyed hook remembers its rect for the next mount with the same key; a changed decision (keyed or not) bumps
        // the measured-change serial a settle compares across a pass, and is journaled while one runs.
        if (MemoryKey is { } key) MeasureMemory.Remember(key, r);
        if (changed)
        {
            if (MemoryKey is { } k) MeasureMemory.NoteChanged(k);
            else MeasureMemory.NoteChangedUnkeyed(SiteFile, SiteLine);
        }
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
    /// updated whenever layout re-arranges it. Wiring: a layout-effect (mount, and again on a root-node swap) registers on the node's bounds-changed
    /// column (composed with any element-declared <c>OnBoundsChanged</c>, not clobbering it). The value is written during
    /// the layout phase, so a same-frame layout-effect sees the PREVIOUS value and the consumer re-renders NEXT frame (no
    /// re-entrancy). Equality-gated: no re-render while the bounds are stable. Prefer <see cref="UseMeasuredWidth(float,string?,int)"/> when
    /// only the width matters (a scalar signal + quantum kills sub-pixel churn). Unkeyed: the first render reads an empty rect;
    /// the keyed overload <see cref="UseMeasuredBounds(string,string?,int)"/> starts from the remembered one.</summary>
    [OverloadResolutionPriority(-1)]   // a string argument means the KEYED overload, never this one's caller-info slot
    public IReadSignal<RectF> UseMeasuredBounds([CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
        => MeasuredCell(null, bounds: true, 0f, __hf, __hl).BoundsSig!;

    /// <summary>The arranged width of this component's rendered root (<see cref="HostNode"/>) as a read signal — the
    /// width-only, hot-path form of <see cref="UseMeasuredBounds(string?,int)"/>. When <paramref name="quantum"/> &gt; 0 the width is
    /// rounded to that grid before the (exact-compare) signal write, so sub-quantum layout jitter never re-renders the
    /// consumer (e.g. quantum 4 ⇒ a &lt;4px wobble is absorbed). Same timing contract as <see cref="UseMeasuredBounds(string?,int)"/>:
    /// written during layout, consumer re-renders NEXT frame; a same-frame layout-effect sees the previous value.</summary>
    public IReadSignal<float> UseMeasuredWidth(float quantum = 0f, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
        => MeasuredCell(null, bounds: false, quantum, __hf, __hl).WidthSig!;

    /// <summary>E17c — the KEYED <see cref="UseMeasuredBounds(string?,int)"/>: the signal starts from the rect remembered for
    /// <paramref name="key"/> (<see cref="MeasureMemory"/>, composed onto the ambient <see cref="MeasureMemory.Scope"/>), and every
    /// arranged rect is remembered under it. A remount with the same key therefore renders its FIRST frame at its laid-out size
    /// and the measurement that follows changes nothing (no corrective frame). <paramref name="key"/> is a semantic region role
    /// (<c>"top-band"</c>); the scope supplies the route instance. Key and scope freeze at mount, like every hook input.</summary>
    public IReadSignal<RectF> UseMeasuredBounds(string key, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
        => MeasuredCell(key ?? throw new ArgumentNullException(nameof(key)), bounds: true, 0f, __hf, __hl).BoundsSig!;

    /// <summary>E17c — the KEYED <see cref="UseMeasuredWidth(float,string?,int)"/>: starts from the width remembered for
    /// <paramref name="key"/> (rounded to <paramref name="quantum"/>) and remembers every arranged width; see
    /// <see cref="UseMeasuredBounds(string,string?,int)"/>.</summary>
    public IReadSignal<float> UseMeasuredWidth(string key, float quantum = 0f, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
        => MeasuredCell(key ?? throw new ArgumentNullException(nameof(key)), bounds: false, quantum, __hf, __hl).WidthSig!;

    private MeasuredBoundsCell MeasuredCell(string? key, bool bounds, float quantum, string? file, int line)
    {
        int idx = LookupCell(file, line, out var __k);
        if (idx >= 0) return (MeasuredBoundsCell)_cells[idx];
        var cell = new MeasuredBoundsCell(this) { Quantum = MathF.Max(quantum, 0f), SiteFile = file, SiteLine = line };
        RectF seed = default;
        if (key is not null)
        {
            // The scope is read untracked: it is a mount input (the route instance), not something this hook follows.
            string scope = ResolveContextSignal?.Invoke(AnchorNode, MeasureMemory.Scope)?.Peek() as string ?? MeasureMemory.Scope.Default;
            cell.MemoryKey = MeasureMemory.Compose(scope, key);
            MeasureMemory.TryGet(cell.MemoryKey, out seed);
        }
        if (bounds) cell.BoundsSig = new Signal<RectF>(seed);
        else cell.WidthSig = new FloatSignal(cell.QuantizeWidth(seed.W));
        RegisterCell(__k, cell, cleanupCapable: true);
        AddHostBound(cell.Register);                          // re-run when a root-type change remounts HostNode
        EnqueueEffect(PendingLayoutEffects, cell.Register);   // runs at phase 6.5 (HostNode valid) then is cleared
        return cell;
    }
}
