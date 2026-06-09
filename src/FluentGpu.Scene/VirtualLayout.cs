using FluentGpu.Foundation;

namespace FluentGpu.Scene;

/// <summary>
/// A pluggable virtualizing layout: PURE, ALLOCATION-FREE arithmetic mapping (itemCount, viewport, scrollOffset) to
/// the realize window + per-item content-space rects. The engine calls it ONLY on realize/arrange frames (a steady
/// in-window scroll is transform-only and never touches it), and the methods return structs / via out-params — so a
/// custom layout costs zero per-frame managed allocation, honoring the engine's core contract. Implement this to make
/// any virtualized layout (lists, card grids, staggered walls, …). Built-ins: <see cref="StackVirtualLayout"/>,
/// <see cref="GridVirtualLayout"/>. (Variable-height/measured layouts use the Fenwick extent-table path instead.)
/// </summary>
public interface IVirtualLayout
{
    /// <summary>Total scroll-axis extent (the published ContentSize) for <paramref name="itemCount"/> items.</summary>
    float ContentExtent(int itemCount, float crossSize);

    /// <summary>The [first,last) item range to realize for <paramref name="scrollOffset"/> (incl. <paramref name="overscan"/>).</summary>
    void Window(int itemCount, float crossSize, float viewportExtent, float scrollOffset, int overscan, out int first, out int last);

    /// <summary>Content-space rect of item <paramref name="index"/> (LOCAL to the scroll content origin).</summary>
    RectF ItemRect(int index, float crossSize);
}

/// <summary>Decides whether a scroll offset needs a virtual-window refresh, using the realized overscan as a guard band.</summary>
public static class VirtualWindowing
{
    public static bool NeedsRealize(in ScrollState sc, int visibleFirst, int visibleLast)
    {
        if (sc.ItemCount <= 0) return false;
        visibleFirst = Math.Clamp(visibleFirst, 0, sc.ItemCount);
        visibleLast = Math.Clamp(visibleLast, visibleFirst, sc.ItemCount);

        if (sc.LastRealized <= sc.FirstRealized) return true;
        if (visibleFirst < sc.FirstRealized || visibleLast > sc.LastRealized) return true;

        int guard = Math.Max(1, sc.Overscan / 2);
        if (sc.FirstRealized > 0 && visibleFirst < sc.FirstRealized + guard) return true;
        if (sc.LastRealized < sc.ItemCount && visibleLast > sc.LastRealized - guard) return true;
        return false;
    }
}

/// <summary>Uniform 1-D stack (the WaveeMusic track-list shape) — O(1) windowing. <paramref name="horizontal"/> scrolls X.</summary>
public sealed class StackVirtualLayout : IVirtualLayout
{
    public readonly float Extent;
    public readonly bool Horizontal;
    public StackVirtualLayout(float extent, bool horizontal = false) { Extent = extent <= 0 ? 1f : extent; Horizontal = horizontal; }

    public float ContentExtent(int n, float cross) => n * Extent;

    public void Window(int n, float cross, float viewport, float offset, int overscan, out int first, out int last)
    {
        first = Math.Max(0, (int)MathF.Floor(offset / Extent) - overscan);
        last = Math.Min(n, (int)MathF.Ceiling((offset + viewport) / Extent) + overscan);
        if (last < first) last = first;
    }

    public RectF ItemRect(int i, float cross)
        => Horizontal ? new RectF(i * Extent, 0f, Extent, cross) : new RectF(0f, i * Extent, cross, Extent);
}

/// <summary>Uniform 2-D grid (album/artist card shelves) — virtualizes by ROW: realizes visible-rows × columns,
/// recycles identically. Columns share the cross size; this is the virtualized UniformGridLayout.</summary>
public sealed class GridVirtualLayout : IVirtualLayout
{
    public readonly int Columns;
    public readonly float ItemHeight, Gap;
    public GridVirtualLayout(int columns, float itemHeight, float gap = 0f)
    { Columns = Math.Max(1, columns); ItemHeight = itemHeight <= 0 ? 1f : itemHeight; Gap = gap; }

    private float RowStride => ItemHeight + Gap;
    private float ColWidth(float cross) => (cross - (Columns - 1) * Gap) / Columns;
    private int RowCount(int n) => (n + Columns - 1) / Columns;

    public float ContentExtent(int n, float cross)
    {
        int rows = RowCount(n);
        return rows <= 0 ? 0f : rows * ItemHeight + (rows - 1) * Gap;
    }

    public void Window(int n, float cross, float viewport, float offset, int overscan, out int first, out int last)
    {
        int firstRow = Math.Max(0, (int)MathF.Floor(offset / RowStride) - overscan);
        int lastRow = (int)MathF.Ceiling((offset + viewport) / RowStride) + overscan;
        first = Math.Min(n, firstRow * Columns);
        last = Math.Min(n, lastRow * Columns);
        if (last < first) last = first;
    }

    public RectF ItemRect(int i, float cross)
    {
        int row = i / Columns, col = i % Columns;
        float cw = ColWidth(cross);
        return new RectF(col * (cw + Gap), row * RowStride, cw, ItemHeight);
    }
}
