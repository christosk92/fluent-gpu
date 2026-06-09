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

    /// <summary>A vertically-virtualized list of variable-height rows (Fenwick extent table + scroll anchoring) —
    /// the legacy built-in variable path (<c>Layout = null</c>). The seam-shaped equivalent is
    /// <see cref="Measured"/> with a <see cref="MeasuredStackVirtualLayout"/> (E11-L0), which custom layouts mirror.</summary>
    public static VirtualListEl VariableList(int itemCount, float estimatedExtent, Func<int, Element> renderItem,
                                             Func<int, string>? keyOf = null, int overscan = 4)
        => new()
        {
            ItemCount = itemCount, Layout = null, EstimatedExtent = estimatedExtent, RenderItem = renderItem,
            KeyOf = keyOf, Overscan = overscan, Grow = 1f,
        };

    /// <summary>A virtualized collection over ANY variable-extent <see cref="IMeasuredVirtualLayout"/> (E11-L0): rows
    /// realize at the layout's estimate, correct to their measured extent on arrange, and the engine re-pins the
    /// scroll anchor across corrections. The layout is STATEFUL — create it once (hoist in a <c>UseMemo</c>) and
    /// reuse it across renders.</summary>
    public static VirtualListEl Measured(int itemCount, IMeasuredVirtualLayout layout, Func<int, Element> renderItem,
                                         Func<int, string>? keyOf = null, int overscan = 4, bool horizontal = false)
        => new()
        {
            ItemCount = itemCount, Layout = layout, RenderItem = renderItem,
            KeyOf = keyOf, Overscan = overscan, Horizontal = horizontal, Grow = 1f,
        };

    /// <summary>The WinUI <c>LinedFlowLayout</c> photo-wall (ItemsView's signature layout): uniform-height lines,
    /// per-item width = aspect × lineHeight, wrapping at the viewport edge. The layout instance is stateful — reuse
    /// the returned element's <see cref="VirtualListEl.Layout"/> or hoist your own <see cref="LinedFlowLayout"/>.</summary>
    public static VirtualListEl LinedFlow(int itemCount, float lineHeight, Func<int, Element> renderItem,
                                          Func<int, float>? aspectRatio = null, float lineSpacing = 0f, float minItemSpacing = 0f,
                                          Func<int, string>? keyOf = null, int overscan = 8)
        => new()
        {
            ItemCount = itemCount,
            Layout = new LinedFlowLayout(lineHeight, aspectRatio, lineSpacing, minItemSpacing),
            RenderItem = renderItem, KeyOf = keyOf, Overscan = overscan, Grow = 1f,
        };

    /// <summary>A grouped flat list with measured rows + sticky-header hook (E11-L0 grouping): headers occupy flat
    /// indices of their own (<paramref name="headerIndices"/>, sorted ascending) and are rendered by the same
    /// <paramref name="renderItem"/> — a group header is just a measured item KIND. Keep a reference to the
    /// <see cref="GroupedListVirtualLayout"/> (via <paramref name="layout"/>) for <c>StickyHeaderIndexAt</c>.</summary>
    public static VirtualListEl GroupedList(int itemCount, int[] headerIndices, float headerExtent, float itemEstimate,
                                            Func<int, Element> renderItem, out GroupedListVirtualLayout layout,
                                            Func<int, string>? keyOf = null, int overscan = 4)
    {
        layout = new GroupedListVirtualLayout(headerIndices, headerExtent, itemEstimate);
        return new VirtualListEl
        {
            ItemCount = itemCount, Layout = layout, RenderItem = renderItem,
            KeyOf = keyOf, Overscan = overscan, Grow = 1f,
        };
    }

    /// <summary>Uniform-row grid with ITEM SPANNING (hero-as-first-row): <paramref name="spanOf"/> returns each item's
    /// column span (clamped 1..columns); items pack row-major and wrap when a span doesn't fit.</summary>
    public static VirtualListEl SpanGrid(int itemCount, int columns, float rowHeight, float gap, Func<int, int> spanOf,
                                         Func<int, Element> renderItem, Func<int, string>? keyOf = null, int overscan = 2)
        => new()
        {
            ItemCount = itemCount,
            Layout = new SpanningGridVirtualLayout(columns, rowHeight, gap, spanOf),
            RenderItem = renderItem, KeyOf = keyOf, Overscan = overscan, Grow = 1f,
        };

    /// <summary>A HORIZONTALLY-scrolling uniform card grid (a shelf <paramref name="rows"/> cells tall) — the
    /// horizontal variant of <see cref="Grid"/>; virtualizes by column.</summary>
    public static VirtualListEl HorizontalGrid(int itemCount, int rows, float itemWidth, float gap, Func<int, Element> renderItem,
                                               Func<int, string>? keyOf = null, int overscan = 2)
        => new()
        {
            ItemCount = itemCount,
            Layout = new HorizontalGridVirtualLayout(rows, itemWidth, gap),
            RenderItem = renderItem, KeyOf = keyOf, Overscan = overscan, Horizontal = true, Grow = 1f,
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
