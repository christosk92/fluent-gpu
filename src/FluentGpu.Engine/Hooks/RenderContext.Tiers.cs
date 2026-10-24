using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Tiers;
using FluentGpu.Signals;

namespace FluentGpu.Hooks;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  UseTier — a size tier with hysteresis whose signal changes only on a crossing.
//
//  Timing contract (technical-plan §3.3 rules 1 and 3):
//    • FIRST MOUNT resolves in the hook's own frame: the cell resolves the source (untracked by the render) while it is
//      created, so the first render already reads the final tier — no deferral, no corrective frame.
//    • A CROSSING FOUND AFTER MOUNT (the source moved) is NOT written to the signal. It is stored as a PENDING value in the
//      host-owned TierRegistry and a frame is requested at once; AppHost.CommitPendingTiers() writes it on the first
//      frame that is not resized, after the scroll frame step and before the reconcile decision, so the write makes the
//      runtime pending and FLIP capture runs with post-resize rects as "before". A plain write here would be reconciled
//      by an in-frame global flush with no capture and snap.
//    • The source is read by a standalone eager Effect, never by the owning component's render: a viewport-driven tier
//      does not re-render the component on every resized frame, only when the tier actually crosses (and its consumers
//      read the returned signal).
//  Zero steady-state allocation: a resolve is a pure walk over the ladder's edge array, the effect body is one stable
//  delegate, and an unchanged tier stores nothing.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Backs <see cref="RenderContext.UseTier"/>: the committed/pending slot, the source reader and the eager effect
/// that re-resolves the ladder when the source changes.</summary>
internal sealed class TierCell : HookCell, IDisposableCell
{
    private readonly TierLadder _ladder;
    private readonly TierSource _source;
    private readonly TierRegistry? _registry;
    private readonly IReadSignal<Size2>? _viewport;
    private bool _resolved;

    public readonly TierSlot Slot;
    public Effect? Effect;
    public readonly Action Run;

    public TierCell(RenderContext ctx, TierLadder ladder, TierSource source, TierRegistry? registry)
    {
        _ladder = ladder;
        _source = source;
        _registry = registry;
        Slot = registry?.Create() ?? new TierSlot();
        if (source.Kind is TierSourceKind.ViewportWidth or TierSourceKind.ViewportHeight)
            _viewport = new RenderContext.ContextReadSignal<Size2>(ctx, Viewport.Size);
        Run = RunBody;
    }

    private float ReadSource(out float aspect)
    {
        aspect = float.NaN;
        switch (_source.Kind)
        {
            case TierSourceKind.ViewportWidth:
            case TierSourceKind.ViewportHeight:
            {
                Size2 s = _viewport!.Value;
                if (s.Height > 0f) aspect = s.Width / s.Height;
                return _source.Kind == TierSourceKind.ViewportWidth ? s.Width : s.Height;
            }
            case TierSourceKind.Measured:
            {
                float v = _source.Slot!.Value;
                if (v <= 0.5f && _source.Thunk is { } seed) v = seed();
                return v;
            }
            default:
                return _source.Thunk!();
        }
    }

    private void RunBody()
    {
        float value = ReadSource(out float aspect);
        if (!_resolved)
        {
            // Mount: resolve fresh into the committed signal. Nothing has read it yet, so this is the hook's own frame.
            _resolved = true;
            Slot.Committed.SetIfChanged(_ladder.Resolve(value, aspect, -1));
            return;
        }
        if (Slot.Disposed) return;
        int previous = Slot.Logical;                         // the hysteresis state: pending if a crossing is waiting
        int next = _ladder.Resolve(value, aspect, previous);
        if (next == previous) return;
        if (_registry is null) { Slot.Committed.SetIfChanged(next); return; }   // no host: nothing to defer against
        if (next == Slot.Committed.Peek()) _registry.Cancel(Slot);               // crossed back before the commit: nothing to write
        else _registry.Store(Slot, next, measured: _source.Kind == TierSourceKind.Measured);
    }

    public void DisposeCell()
    {
        Effect?.Dispose();
        if (_registry is not null) _registry.Release(Slot);
        else Slot.Disposed = true;
    }
}

public sealed partial class RenderContext
{
    /// <summary>A size tier of <paramref name="source"/> on <paramref name="ladder"/>, as a read signal that changes ONLY on
    /// a crossing (the ladder's hysteresis decides when). The tier index is the caller's own enum value.
    /// <para><b>First mount</b> resolves in its own frame (the first render already reads the final tier).
    /// <b>A crossing after mount</b> is stored as a pending value in the host and committed by
    /// <c>AppHost.CommitPendingTiers</c> on the first non-resized frame, right before FLIP capture, so a resize lands one
    /// animated crossing and never a snap. Read the returned signal's <c>.Value</c> in render (or bind it) to follow the
    /// tier; the owning component is NOT re-rendered by the source itself.</para>
    /// The ladder and the source freeze at mount (a hook's inputs, like every propless factory).</summary>
    public IReadSignal<int> UseTier(TierLadder ladder, TierSource source, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        TierCell cell;
        if (idx < 0)
        {
            var registry = ResolveContextSignal?.Invoke(AnchorNode, TierHost.Registry)?.Peek() as TierRegistry;
            cell = new TierCell(this, ladder, source, registry);
            RegisterCell(__k, cell, cleanupCapable: true);
            cell.Effect = Own(new Effect(Rt, cell.Run));   // runs now: the fresh resolve; re-runs when the source changes
        }
        else cell = (TierCell)_cells[idx];
        return cell.Slot.Committed;
    }
}
