using System;
using FluentGpu.Scene;

namespace FluentGpu.Scroll.Extent;

/// <summary>
/// An <see cref="IExtentSource"/> over a pluggable <see cref="IVirtualLayout"/> (grids, fill-row shelves, grouped lists,
/// custom layouts): <see cref="OffsetOf"/> is the item's row start on the scroll axis, <see cref="IndexAt"/> the first
/// item of the row at an offset. Rows of a grid share one offset, so the virtualizer's <c>[first, last]</c> is widened
/// to whole rows with <see cref="RowEnd"/>. Measured layouts (<see cref="IMeasuredVirtualLayout"/>) forward
/// <see cref="SetMeasured"/> and report the anchor delta the same way a Fenwick source does — by re-reading the
/// anchor's offset. Float geometry is the layout's own; a fixed-stride list uses <see cref="FixedExtent"/> instead so
/// deep offsets stay exact.
/// </summary>
public sealed class VirtualLayoutExtent : IExtentSource
{
    private readonly IVirtualLayout _layout;
    private readonly bool _horizontal;
    private int _count;

    public VirtualLayoutExtent(IVirtualLayout layout, int count, float cross, bool horizontal)
    {
        _layout = layout;
        _count = Math.Max(0, count);
        Cross = cross;
        _horizontal = horizontal;
    }

    public IVirtualLayout Layout => _layout;
    public bool Horizontal => _horizontal;

    /// <summary>The cross-axis size the layout is evaluated at (a viewport's inner cross) — refreshed by layout each frame.</summary>
    public float Cross { get; set; }

    public int Count => _count;

    public double Total => _layout.ContentExtent(_count, Cross);

    public double OffsetOf(int i)
    {
        // Item 0 answers its OWN rect like every other item: a layout with a leading pad (FillRowVirtualLayout's lead
        // inset, the halo gutter) starts its first item past 0, and a hard 0 here arranged item 0 one gutter before the
        // rest of its row/column (the artist Top tracks chart's first row 12 DIP left, 2026-09-25 item H). Negative
        // indices are the content start.
        if (_count == 0 || i < 0) return 0.0;
        if (i >= _count) return Total;
        var r = _layout.ItemRect(i, Cross);
        return _horizontal ? r.X : r.Y;
    }

    public int IndexAt(double off)
    {
        if (_count == 0) return 0;
        if (_layout is MeasuredStackVirtualLayout m) return Math.Clamp(m.IndexAt((float)off, Cross), 0, _count - 1);
        // Every other layout answers through its Window contract (viewport 0, no overscan ⇒ the item AT the offset);
        // a fixed grid's IMeasuredVirtualLayout.IndexAt only knows its measured row table.
        _layout.Window(_count, Cross, 0f, (float)Math.Max(0.0, off), 0, out int first, out _);
        return Math.Clamp(first, 0, _count - 1);
    }

    public double ExtentOf(int i)
    {
        if ((uint)i >= (uint)_count) return 0.0;
        var r = _layout.ItemRect(i, Cross);
        return _horizontal ? r.W : r.H;
    }

    public bool IsMeasured(int i) => true;

    public double SetMeasured(int i, double extent, int anchorIndex)
    {
        if (_layout is not IMeasuredVirtualLayout m || (uint)i >= (uint)_count) return 0.0;
        double before = OffsetOf(anchorIndex);
        m.SetMeasured(i, (float)extent, Cross);
        return i < anchorIndex ? OffsetOf(anchorIndex) - before : 0.0;
    }

    public void Resize(int newCount) => _count = Math.Max(0, newCount);

    /// <summary>The exclusive end of the row containing <paramref name="index"/>: the first later item whose row start
    /// differs (or <see cref="Count"/>). Widens a per-item window to whole grid rows.</summary>
    public int RowEnd(int index)
    {
        if (_count == 0) return 0;
        index = Math.Clamp(index, 0, _count - 1);
        double start = OffsetOf(index);
        int j = index + 1;
        while (j < _count && OffsetOf(j) == start) j++;
        return j;
    }

    /// <summary>The first item of the row containing <paramref name="index"/>.</summary>
    public int RowStart(int index)
    {
        if (_count == 0) return 0;
        index = Math.Clamp(index, 0, _count - 1);
        double start = OffsetOf(index);
        int j = index;
        while (j > 0 && OffsetOf(j - 1) == start) j--;
        return j;
    }
}
