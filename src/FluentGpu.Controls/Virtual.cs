using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Reconciler;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>Fluent factories for virtualized collections. All recycle over the slab free-list and keep a 0-alloc
/// in-window scroll. <see cref="Custom"/> takes any <see cref="IVirtualLayout"/> you implement. The returned
/// <see cref="VirtualListEl"/> record lives in FluentGpu.Reconciler (the reconciler diffs it directly).</summary>
public static class Virtual
{
    /// <summary>A vertically-virtualized uniform list (the WaveeMusic track-list shape).</summary>
    public static VirtualListEl List(int itemCount, float itemExtent, Func<int, Element> renderItem,
                                     Func<int, string>? keyOf = null, int overscan = 4)
        => new()
        {
            ItemCount = itemCount, Layout = new StackVirtualLayout(itemExtent), RenderItem = renderItem,
            KeyOf = keyOf, Overscan = overscan, Grow = 1f,
        };

    /// <summary>A vertically-virtualized uniform card GRID (album/artist shelves) — virtualizes by row.</summary>
    public static VirtualListEl Grid(int itemCount, int columns, float itemHeight, float gap, Func<int, Element> renderItem,
                                     Func<int, string>? keyOf = null, int overscan = 2)
        => new()
        {
            ItemCount = itemCount, Layout = new GridVirtualLayout(columns, itemHeight, gap), RenderItem = renderItem,
            KeyOf = keyOf, Overscan = overscan, Grow = 1f,
        };

    /// <summary>A virtualized collection with ANY custom <see cref="IVirtualLayout"/> you supply.</summary>
    public static VirtualListEl Custom(int itemCount, IVirtualLayout layout, Func<int, Element> renderItem,
                                       Func<int, string>? keyOf = null, int overscan = 4, bool horizontal = false)
        => new()
        {
            ItemCount = itemCount, Layout = layout, RenderItem = renderItem,
            KeyOf = keyOf, Overscan = overscan, Horizontal = horizontal, Grow = 1f,
        };

    /// <summary>A vertically-virtualized list of variable-height rows (Fenwick extent table + scroll anchoring).</summary>
    public static VirtualListEl VariableList(int itemCount, float estimatedExtent, Func<int, Element> renderItem,
                                             Func<int, string>? keyOf = null, int overscan = 4)
        => new()
        {
            ItemCount = itemCount, Layout = null, EstimatedExtent = estimatedExtent, RenderItem = renderItem,
            KeyOf = keyOf, Overscan = overscan, Grow = 1f,
        };

    /// <summary>Signals-first BOUND list (the recycler fast path): <paramref name="row"/> runs ONCE per visible slot
    /// with an index SIGNAL; scrolling rebinds a slot by writing its signal — zero element rebuild, zero reconcile,
    /// zero keys. Express anything that varies by index as a reactive bind (<c>TextBind</c>/<c>FillBind</c>/
    /// <c>SourceBind</c>/<c>PlaceholderBind</c>), never a captured value. The fastest path for huge uniform lists
    /// (the WaveeMusic 100k track list under a scrollbar thumb-drag).</summary>
    public static VirtualListEl ListBound(int itemCount, float itemExtent, Func<IReadSignal<int>, Element> row, int overscan = 4)
        => new() { ItemCount = itemCount, Layout = new StackVirtualLayout(itemExtent), RowBind = row, Overscan = overscan, Grow = 1f };

    /// <summary>Signals-first BOUND uniform card grid — <see cref="ListBound"/> semantics over <see cref="GridVirtualLayout"/>.</summary>
    public static VirtualListEl GridBound(int itemCount, int columns, float itemHeight, float gap, Func<IReadSignal<int>, Element> row, int overscan = 2)
        => new() { ItemCount = itemCount, Layout = new GridVirtualLayout(columns, itemHeight, gap), RowBind = row, Overscan = overscan, Grow = 1f };
}
