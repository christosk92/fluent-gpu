using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Reconciler;

/// <summary>
/// A virtualized collection viewport: ONE retained node + a window of keyed children. Each frame the visible range is
/// dirty, the reconciler asks the <see cref="Layout"/> for <c>[first,last)</c>, calls <see cref="RenderItem"/> for just
/// that window, keys each row, and feeds it to the existing keyed-LIS diff — so recycling IS CreateNode/FreeNode over
/// the slab free-list (no second pool). Scroll is layout-free (the <c>-ScrollOffset</c> is the content's transform).
///
/// <see cref="Layout"/> is a pluggable <see cref="IVirtualLayout"/> (stack / grid / custom — pure, allocation-free).
/// When it is null, the variable-height Fenwick extent-table path runs (measured rows + scroll anchoring).
/// The user-facing <c>Virtual.List/Grid/Custom/VariableList</c> factories live in FluentGpu.Controls; this record is the
/// engine primitive the reconciler diffs directly (ElementTypeId 6). See <c>design/subsystems/virtualization.md</c>.
/// </summary>
public sealed record VirtualListEl : Element
{
    public override ushort ElementTypeId => 6;

    public int ItemCount { get; init; }
    public Func<int, Element> RenderItem { get; init; } = static _ => new BoxEl();
    public Func<int, string>? KeyOf { get; init; }
    public IVirtualLayout? Layout { get; init; }      // fixed-geometry (stack/grid/custom); null ⇒ variable Fenwick
    public float EstimatedExtent { get; init; } = 48f;// variable path: seed extent for unmeasured rows
    public int Overscan { get; init; } = 4;
    public bool Horizontal { get; init; }

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
}
