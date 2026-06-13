using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Reconciler;

/// <summary>
/// A virtualized collection viewport: ONE retained node + a window of keyed children. Each frame the visible range is
/// dirty, the reconciler asks the <see cref="Layout"/> for <c>[first,last)</c>, calls <see cref="RenderItem"/> for just
/// that window, keys each row, and feeds it to the existing keyed-LIS diff — so recycling IS CreateNode/FreeNode over
/// the slab free-list (no second pool). Scroll is layout-free (the <c>-ScrollOffset</c> is the content's transform).
///
/// <see cref="Layout"/> is a pluggable <see cref="IVirtualLayout"/> (stack / grid / custom — pure, allocation-free).
/// E11-L1: the viewport consumes BOTH seam kinds — an <see cref="IMeasuredVirtualLayout"/> gets the variable-extent
/// estimate-then-correct arrange (measured rows + scroll anchoring) through the SAME property; when the layout is
/// null, the legacy built-in Fenwick extent-table path runs (kept source/behavior-compatible).
/// The user-facing <c>Virtual.List/Grid/Custom/Measured/VariableList/…</c> factories live in FluentGpu.Controls; this
/// record is the engine primitive the reconciler diffs directly (ElementTypeId 6). See <c>design/subsystems/virtualization.md</c>.
/// </summary>
public sealed record VirtualListEl : Element
{
    public override ushort ElementTypeId => 6;

    public int ItemCount { get; init; }
    public Func<int, Element> RenderItem { get; init; } = static _ => new BoxEl();
    public Func<int, string>? KeyOf { get; init; }
    /// <summary>Signals-first BOUND row template (the recycler fast path): the template runs ONCE per visible slot
    /// with an index SIGNAL; scrolling rebinds a slot by writing its signal, so only the row's reactive binds
    /// (TextBind/FillBind/SourceBind/…) re-run — zero element rebuild, zero reconcile, zero keys. When set,
    /// <see cref="RenderItem"/>/<see cref="KeyOf"/> are ignored.</summary>
    public Func<IReadSignal<int>, Element>? RowBind { get; init; }
    public IVirtualLayout? Layout { get; init; }      // fixed OR measured (IMeasuredVirtualLayout) seam; null ⇒ legacy variable Fenwick
    public float EstimatedExtent { get; init; } = 48f;// legacy variable path: seed extent for unmeasured rows
    public int Overscan { get; init; } = 4;
    public bool Horizontal { get; init; }

    // ── E11-L2 item lifecycle (the WinUI ItemsRepeater ElementPrepared/ElementClearing/ElementIndexChanged trio +
    //    the UseVisibleRange prefetch hook). Fired by the reconciler at realize time (cold realize edge, never on a
    //    steady transform-only scroll frame):
    //    • Prepared  — index entered the realized window (fresh mount OR a recycled node rebound to it).
    //    • Clearing  — index left the realized window (its node was recycled to another index or freed).
    //      Recycling therefore fires Clearing(old) + Prepared(new) — exactly WinUI's recycle event order.
    //    • IndexChanged — a BOUND slot (RowBind path) was rebound from one index to another (the slot's element
    //      identity persists; only its index signal moved): (oldIndex, newIndex).
    /// <summary>WinUI <c>ItemsRepeater.ElementPrepared</c> — fired with the item index when it is realized.</summary>
    public Action<int>? OnItemPrepared { get; init; }
    /// <summary>WinUI <c>ItemsRepeater.ElementClearing</c> — fired with the item index when it leaves the window.</summary>
    public Action<int>? OnItemClearing { get; init; }
    /// <summary>WinUI <c>ItemsRepeater.ElementIndexChanged</c> — a persistent bound slot rebound (oldIndex, newIndex).</summary>
    public Action<int, int>? OnItemIndexChanged { get; init; }
    /// <summary>Prefetch hook: the realized window changed → (first, last) exclusive. Use to warm images/data just
    /// outside the viewport (the plan's <c>UseVisibleRange</c> surface). Fired only when the range actually moved.</summary>
    public Action<int, int>? OnVisibleRange { get; init; }
    /// <summary>Called once when this viewport is realized into the scene, with its node handle — the escape hatch a
    /// composing control (ItemsView) uses to drive <c>ScrollState</c> (StartBringItemIntoView, sticky pinning).</summary>
    public Action<NodeHandle>? OnRealized { get; init; }

    // The viewport participates in its parent's layout like a box (size + flex + margin + a backing fill).
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
    public ColorF Fill { get; init; }

    /// <summary>Scroll-edge cues (controls.md §8.3): a surface-colour gradient fade at an overflowing edge so a clipped
    /// list signals there is more below the fold. <see cref="ScrollEdgeCues.Auto"/> (default) resolves to
    /// <see cref="ScrollEdgeCuesDefaults.Default"/> (ON, fade-only); <see cref="ScrollEdgeCues.None"/> opts out.</summary>
    public ScrollEdgeCues EdgeCues { get; init; } = ScrollEdgeCues.Auto;
}
