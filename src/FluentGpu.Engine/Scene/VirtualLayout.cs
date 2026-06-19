using FluentGpu.Foundation;

namespace FluentGpu.Scene;

/// <summary>
/// A pluggable virtualizing layout: PURE, ALLOCATION-FREE arithmetic mapping (itemCount, viewport, scrollOffset) to
/// the realize window + per-item content-space rects. The engine calls it ONLY on realize/arrange frames (a steady
/// in-window scroll is transform-only and never touches it), and the methods return structs / via out-params — so a
/// custom layout costs zero per-frame managed allocation, honoring the engine's core contract. Implement this to make
/// any virtualized layout (lists, card grids, staggered walls, …). Built-ins: <see cref="StackVirtualLayout"/>,
/// <see cref="GridVirtualLayout"/>, <see cref="HorizontalGridVirtualLayout"/>, <see cref="FillRowVirtualLayout"/>,
/// <see cref="LinedFlowLayout"/>, <see cref="SpanningGridVirtualLayout"/>. Variable-extent (measured) layouts implement
/// the widened seam <see cref="IMeasuredVirtualLayout"/> — same contract plus estimate-then-correct feedback; layouts
/// that size items to the viewport implement <see cref="IViewportVirtualLayout"/> — same contract plus a viewport feed.
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

/// <summary>
/// The variable-extent (measured) virtualization seam — E11-L0. Folds the Fenwick estimate-then-correct path behind
/// the SAME pluggable seam as the fixed-geometry layouts, so CUSTOM layouts can be variable/sliver-like: unmeasured
/// items report their estimate; at arrange time the layout engine measures each realized row and feeds the real
/// extent back through <see cref="SetMeasured"/> (O(log n) correction), then re-pins the scroll anchor via
/// <see cref="IndexAt"/>/<see cref="OffsetOf"/> so corrections above the viewport never jump the visible top
/// (virtualization.md §6.2 — estimate-then-correct + anchoring are seam behavior now, not a special case).
/// Built-ins: <see cref="MeasuredStackVirtualLayout"/> (the Fenwick variable list), <see cref="GroupedListVirtualLayout"/>
/// (group headers = a measured item kind + sticky-header hook).
/// </summary>
public interface IMeasuredVirtualLayout : IVirtualLayout
{
    /// <summary>Correct item <paramref name="index"/>'s main-axis extent to its measured value (estimate-then-correct).
    /// Called by the layout engine for every realized row at arrange time; O(log n), allocation-free.</summary>
    void SetMeasured(int index, float mainExtent, float crossSize);

    /// <summary>Content-space main-axis offset of item <paramref name="index"/> (prefix sum over corrected extents).</summary>
    float OffsetOf(int index, float crossSize);

    /// <summary>The item whose extent band contains <paramref name="offset"/> — the scroll-anchor candidate.</summary>
    int IndexAt(float offset, float crossSize);
}

/// <summary>
/// The viewport-aware virtualization seam — folds "size each item to the SCROLL-AXIS (main) viewport" behind the SAME
/// pluggable seam as the fixed-geometry layouts. The core <see cref="IVirtualLayout"/> methods only receive the CROSS
/// size (height for a horizontal viewport), so a "fill the width with N equal cards" layout cannot be expressed; this
/// adds <see cref="SetViewport"/>, which the layout engine calls with the main-axis viewport extent BEFORE every
/// geometry call (<see cref="IVirtualLayout.ContentExtent"/>/<see cref="IVirtualLayout.Window"/>/<see cref="IVirtualLayout.ItemRect"/>).
/// Stateful + allocation-free (cache the fit), exactly like <see cref="IMeasuredVirtualLayout"/>. The core contract is
/// unchanged, so existing layouts and custom implementations are unaffected. Built-in: <see cref="FillRowVirtualLayout"/>.
/// </summary>
public interface IViewportVirtualLayout : IVirtualLayout
{
    /// <summary>Feed the live viewport before geometry: <paramref name="mainExtent"/> is the scroll-axis viewport
    /// extent (width for a horizontal viewport), <paramref name="crossSize"/> the cross axis. O(1), allocation-free.</summary>
    void SetViewport(float mainExtent, float crossSize);
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

/// <summary>Uniform 2-D grid scrolling HORIZONTALLY (a shelf of fixed-width columns, <see cref="Rows"/> cells tall) —
/// virtualizes by COLUMN: realizes visible-columns × rows. The horizontal variant of <see cref="GridVirtualLayout"/>;
/// pair with <c>VirtualListEl.Horizontal = true</c>.</summary>
public sealed class HorizontalGridVirtualLayout : IVirtualLayout
{
    public readonly int Rows;
    public readonly float ItemWidth, Gap;
    public HorizontalGridVirtualLayout(int rows, float itemWidth, float gap = 0f)
    { Rows = Math.Max(1, rows); ItemWidth = itemWidth <= 0 ? 1f : itemWidth; Gap = gap; }

    private float ColStride => ItemWidth + Gap;
    private float RowHeight(float cross) => (cross - (Rows - 1) * Gap) / Rows;
    private int ColCount(int n) => (n + Rows - 1) / Rows;

    public float ContentExtent(int n, float cross)
    {
        int cols = ColCount(n);
        return cols <= 0 ? 0f : cols * ItemWidth + (cols - 1) * Gap;
    }

    public void Window(int n, float cross, float viewport, float offset, int overscan, out int first, out int last)
    {
        int firstCol = Math.Max(0, (int)MathF.Floor(offset / ColStride) - overscan);
        int lastCol = (int)MathF.Ceiling((offset + viewport) / ColStride) + overscan;
        first = Math.Min(n, firstCol * Rows);
        last = Math.Min(n, lastCol * Rows);
        if (last < first) last = first;
    }

    public RectF ItemRect(int i, float cross)
    {
        int col = i / Rows, row = i % Rows;
        float rh = RowHeight(cross);
        return new RectF(col * ColStride, row * (rh + Gap), ItemWidth, rh);
    }
}

/// <summary>
/// Horizontal "fill the viewport with EQUAL cards" layout (the size-reactive shelf / Spotify rail) — the viewport-aware
/// sibling of <see cref="HorizontalGridVirtualLayout"/>: instead of a FIXED item width it fits as many equal columns as
/// the main-axis viewport width allows, each sized to fill exactly within <c>[minCardW, maxCardW]</c>, <see cref="Rows"/>
/// cells tall. The engine feeds the live viewport via <see cref="SetViewport"/> before each geometry call, so cards
/// re-fit on resize with NO app-side width broker. The fit is COUNT-INDEPENDENT (a handful of items render at the normal
/// fitted width, left-aligned with trailing space — never ballooned), which makes the card width strictly
/// <c>≤ maxCardW</c> by construction. STATEFUL (caches the fit) — create once and reuse (hoist in a <c>UseMemo</c>);
/// pair with <c>VirtualListEl.Horizontal = true</c>. Override knobs: <see cref="PerPageOverride"/> pins the columns-per-
/// page; <see cref="FixedCardW"/> pins the card width (both bypass the auto-fit).
/// </summary>
public sealed class FillRowVirtualLayout : IViewportVirtualLayout
{
    public readonly float MinCardW, MaxCardW, Gap;
    public readonly int Rows;
    public readonly int PerPageOverride;   // 0 ⇒ auto-fit
    public readonly float FixedCardW;      // 0 ⇒ auto-fit

    private float _main, _cross;           // last viewport fed by the engine (main = scroll-axis width)
    private int _perPage = 1;
    private float _cardW;

    public FillRowVirtualLayout(float minCardW = 150f, float maxCardW = 200f, float gap = 0f, int rows = 1,
                                int perPageOverride = 0, float fixedCardW = 0f)
    {
        MinCardW = minCardW <= 0 ? 1f : minCardW;
        MaxCardW = maxCardW < MinCardW ? MinCardW : maxCardW;
        Gap = gap < 0 ? 0f : gap;
        Rows = Math.Max(1, rows);
        PerPageOverride = Math.Max(0, perPageOverride);
        FixedCardW = fixedCardW < 0 ? 0f : fixedCardW;
        _cardW = MinCardW;
    }

    /// <summary>Columns shown per page at the current viewport (≥1) — valid after the engine's first <see cref="SetViewport"/>.</summary>
    public int PerPage => _perPage;
    /// <summary>The fitted card width at the current viewport.</summary>
    public float CardW => _cardW;

    public void SetViewport(float mainExtent, float crossSize)
    {
        if (mainExtent == _main && crossSize == _cross) return;
        _main = mainExtent; _cross = crossSize;
        (_perPage, _cardW) = Fit(_main, MinCardW, MaxCardW, Gap, PerPageOverride, FixedCardW);
    }

    /// <summary>The viewport→(perPage, cardW) fit — COUNT-INDEPENDENT, cardW capped at <paramref name="maxCardW"/>.
    /// Exposed static so a host control can compute the SAME geometry it will be laid out at (e.g. to size a
    /// width-driven card's height before it knows the realized cell). Allocation-free.</summary>
    public static (int PerPage, float CardW) Fit(float main, float minCardW, float maxCardW, float gap,
                                                 int perPageOverride = 0, float fixedCardW = 0f)
    {
        minCardW = minCardW <= 0 ? 1f : minCardW;
        maxCardW = maxCardW < minCardW ? minCardW : maxCardW;
        gap = gap < 0 ? 0f : gap;
        if (fixedCardW > 0f)
            return (main <= 1f ? 1 : Math.Max(1, (int)MathF.Floor((main + gap) / (fixedCardW + gap))), fixedCardW);
        if (main <= 1f) return (1, minCardW);
        // Max columns that fit at the MIN card width, then grow columns to pull each card down to ≤ maxCardW.
        int perPage = perPageOverride > 0 ? perPageOverride : Math.Max(1, (int)MathF.Floor((main + gap) / (minCardW + gap)));
        float cardW = (main - (perPage - 1) * gap) / perPage;
        while (perPageOverride == 0 && cardW > maxCardW) { perPage++; cardW = (main - (perPage - 1) * gap) / perPage; }
        cardW = MathF.Min(cardW, maxCardW);   // belt-and-suspenders (and the perPageOverride path)
        return (Math.Max(1, perPage), cardW <= 0f ? minCardW : cardW);
    }

    private float ColStride => _cardW + Gap;
    private float RowHeight(float cross) => (cross - (Rows - 1) * Gap) / Rows;
    private int ColCount(int n) => (n + Rows - 1) / Rows;

    public float ContentExtent(int n, float cross)
    {
        int cols = ColCount(n);
        return cols <= 0 ? 0f : cols * _cardW + (cols - 1) * Gap;
    }

    public void Window(int n, float cross, float viewport, float offset, int overscan, out int first, out int last)
    {
        float stride = ColStride;
        int firstCol = Math.Max(0, (int)MathF.Floor(offset / stride) - overscan);
        int lastCol = (int)MathF.Ceiling((offset + viewport) / stride) + overscan;
        first = Math.Min(n, firstCol * Rows);
        last = Math.Min(n, lastCol * Rows);
        if (last < first) last = first;
    }

    public RectF ItemRect(int i, float cross)
    {
        int col = i / Rows, row = i % Rows;
        float rh = RowHeight(cross);
        return new RectF(col * ColStride, row * (rh + Gap), _cardW, rh);
    }
}

/// <summary>
/// The Fenwick variable-extent list behind the <see cref="IMeasuredVirtualLayout"/> seam (E11-L0): every row starts at
/// <see cref="Estimate"/>, gets corrected to its measured extent on realize, and the layout engine re-pins the scroll
/// anchor across corrections. Functionally the engine's legacy <c>Layout = null</c> variable path (kept for
/// compatibility), but USER-REACHABLE: compose it, subclass the idea, or build your own measured layout on the same
/// contract. STATEFUL (owns an <see cref="ExtentTable"/>) — create ONCE and reuse across renders (hoist in a
/// <c>UseMemo</c>); the table self-rebuilds only on item-count change.
/// </summary>
public sealed class MeasuredStackVirtualLayout : IMeasuredVirtualLayout
{
    public readonly float Estimate;
    public readonly bool Horizontal;
    private ExtentTable? _table;

    public MeasuredStackVirtualLayout(float estimatedExtent, bool horizontal = false)
    { Estimate = estimatedExtent <= 0 ? 1f : estimatedExtent; Horizontal = horizontal; }

    private ExtentTable Ensure(int n)
    {
        if (_table is null) _table = new ExtentTable(n, Estimate);
        else if (_table.Count != n) _table.Reset(n, Estimate);
        return _table;
    }

    public float ContentExtent(int n, float cross) => (float)Ensure(n).Total;

    public void Window(int n, float cross, float viewport, float offset, int overscan, out int first, out int last)
    {
        var t = Ensure(n);
        first = Math.Max(0, t.IndexAt(offset) - overscan);
        last = Math.Min(n, t.IndexAt(offset + viewport) + 1 + overscan);
        if (last < first) last = first;
    }

    public RectF ItemRect(int i, float cross)
    {
        float pos = _table?.OffsetOf(i) ?? i * Estimate;
        float ext = _table?.ExtentAt(i) ?? Estimate;
        return Horizontal ? new RectF(pos, 0f, ext, cross) : new RectF(0f, pos, cross, ext);
    }

    public void SetMeasured(int index, float mainExtent, float crossSize) => _table?.SetExtent(index, mainExtent);
    public float OffsetOf(int index, float crossSize) => _table?.OffsetOf(index) ?? index * Estimate;
    public int IndexAt(float offset, float crossSize) => _table?.IndexAt(offset) ?? Math.Max(0, (int)(offset / Estimate));
}

/// <summary>
/// Grouped flat list with measured rows (E11-L0 grouping hook): the items are a FLATTENED projection — group headers
/// occupy indices of their own (a header is just a measured item KIND, the app-requirements "Liked Songs" shape).
/// <see cref="HeaderIndices"/> must be sorted ascending. Headers seed at <see cref="HeaderEstimate"/>, items at
/// <see cref="ItemEstimate"/>; both correct to measured extents. <see cref="StickyHeaderIndexAt"/> is the
/// sticky-header support hook: the header that should pin for a given scroll offset (binary search, O(log groups)) —
/// consumers (ItemsView / a custom list) translate that header's realized node by the pin delta at phase 7
/// (<c>NodeFlags.StickyPinned</c>). Vertical scroll. STATEFUL — create once and reuse (see MeasuredStackVirtualLayout).
/// </summary>
public sealed class GroupedListVirtualLayout : IMeasuredVirtualLayout
{
    public readonly float HeaderEstimate, ItemEstimate;
    private readonly int[] _headers;   // sorted flat indices of group headers
    private ExtentTable? _table;

    public GroupedListVirtualLayout(int[] headerIndices, float headerExtent, float itemEstimate)
    {
        _headers = headerIndices ?? [];
        HeaderEstimate = headerExtent <= 0 ? 1f : headerExtent;
        ItemEstimate = itemEstimate <= 0 ? 1f : itemEstimate;
    }

    public ReadOnlySpan<int> HeaderIndices => _headers;

    /// <summary>True when flat index <paramref name="index"/> is a group header (binary search, allocation-free).</summary>
    public bool IsHeader(int index) => System.Array.BinarySearch(_headers, index) >= 0;

    /// <summary>The header that owns the item band at <paramref name="offset"/> — the one a sticky-header presenter
    /// pins to the viewport top. −1 when the offset is above the first header.</summary>
    public int StickyHeaderIndexAt(float offset)
    {
        if (_table is null || _headers.Length == 0) return -1;
        int at = _table.IndexAt(offset);
        // Last header index ≤ at (binary search over the sorted header list).
        int lo = 0, hi = _headers.Length - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_headers[mid] <= at) { best = _headers[mid]; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }

    private ExtentTable Ensure(int n)
    {
        if (_table is null || _table.Count != n)
        {
            (_table ??= new ExtentTable(n, ItemEstimate)).Reset(n, ItemEstimate);
            for (int h = 0; h < _headers.Length; h++)
                if ((uint)_headers[h] < (uint)n) _table.SetExtent(_headers[h], HeaderEstimate);
        }
        return _table;
    }

    public float ContentExtent(int n, float cross) => (float)Ensure(n).Total;

    public void Window(int n, float cross, float viewport, float offset, int overscan, out int first, out int last)
    {
        var t = Ensure(n);
        first = Math.Max(0, t.IndexAt(offset) - overscan);
        last = Math.Min(n, t.IndexAt(offset + viewport) + 1 + overscan);
        if (last < first) last = first;
    }

    public RectF ItemRect(int i, float cross)
    {
        float pos = _table?.OffsetOf(i) ?? 0f;
        float ext = _table?.ExtentAt(i) ?? (IsHeader(i) ? HeaderEstimate : ItemEstimate);
        return new RectF(0f, pos, cross, ext);
    }

    public void SetMeasured(int index, float mainExtent, float crossSize) => _table?.SetExtent(index, mainExtent);
    public float OffsetOf(int index, float crossSize) => _table?.OffsetOf(index) ?? 0f;
    public int IndexAt(float offset, float crossSize) => _table?.IndexAt(offset) ?? 0;
}

/// <summary>
/// WinUI <c>LinedFlowLayout</c> — the ItemsView photo-wall: items flow left-to-right into uniform-height LINES, each
/// item's width = its aspect ratio × <see cref="LineHeight"/>; a line wraps when the next item would overflow the
/// cross size. Uniform line stride keeps windowing O(1); the per-item line table is (re)built lazily ONLY when
/// (itemCount, crossSize) change — realize/arrange calls stay allocation-free per the seam contract.
/// WinUI defaults: LineSpacing 0, MinItemSpacing 0 (LinedFlowLayout.h:25,27 s_defaultLineSpacing/s_defaultMinItemSpacing);
/// LineHeight has no auto here (WinUI s_defaultLineHeight = NaN = auto-from-first-item; the engine takes it explicitly).
/// Items wider than the cross size clamp to it (single-item line). STATEFUL — create once and reuse.
/// </summary>
public sealed class LinedFlowLayout : IVirtualLayout
{
    public readonly float LineHeight, LineSpacing, MinItemSpacing;
    private readonly Func<int, float>? _aspect;   // desired width/height per item; null = square (1.0)

    private int _count = -1;
    private float _cross = float.NaN;
    private int _lineCount;
    private int[] _lineOf = [];      // item → line
    private float[] _xOf = [];       // item → x
    private float[] _wOf = [];       // item → width
    private int[] _lineStart = [];   // line → first item index

    public LinedFlowLayout(float lineHeight, Func<int, float>? aspectRatio = null, float lineSpacing = 0f, float minItemSpacing = 0f)
    {
        LineHeight = lineHeight <= 0 ? 1f : lineHeight;
        LineSpacing = lineSpacing;
        MinItemSpacing = minItemSpacing;
        _aspect = aspectRatio;
    }

    private float LineStride => LineHeight + LineSpacing;

    private void Ensure(int n, float cross)
    {
        if (n == _count && cross == _cross) return;
        _count = n; _cross = cross <= 0 ? 1f : cross;
        if (_lineOf.Length < n) { _lineOf = new int[n]; _xOf = new float[n]; _wOf = new float[n]; }
        int line = 0; float x = 0f;
        var starts = new List<int>(Math.Max(1, n / 4));
        if (n > 0) starts.Add(0);
        for (int i = 0; i < n; i++)
        {
            float a = _aspect?.Invoke(i) ?? 1f;
            if (!(a > 0f)) a = 1f;
            float w = MathF.Min(_cross, a * LineHeight);
            if (x > 0f && x + MinItemSpacing + w > _cross) { line++; x = 0f; starts.Add(i); }
            else if (x > 0f) x += MinItemSpacing;
            _lineOf[i] = line; _xOf[i] = x; _wOf[i] = w;
            x += w;
        }
        _lineCount = n == 0 ? 0 : line + 1;
        _lineStart = starts.ToArray();
    }

    public float ContentExtent(int n, float cross)
    {
        Ensure(n, cross);
        return _lineCount <= 0 ? 0f : _lineCount * LineHeight + (_lineCount - 1) * LineSpacing;
    }

    public void Window(int n, float cross, float viewport, float offset, int overscan, out int first, out int last)
    {
        Ensure(n, cross);
        if (_lineCount == 0) { first = last = 0; return; }
        int firstLine = Math.Clamp((int)MathF.Floor(offset / LineStride), 0, _lineCount - 1);
        int lastLine = Math.Clamp((int)MathF.Ceiling((offset + viewport) / LineStride), firstLine, _lineCount - 1);
        first = Math.Max(0, _lineStart[firstLine] - overscan);
        last = Math.Min(n, (lastLine + 1 < _lineCount ? _lineStart[lastLine + 1] : n) + overscan);
        if (last < first) last = first;
    }

    public RectF ItemRect(int i, float cross)
    {
        Ensure(Math.Max(_count, i + 1), cross);
        return new RectF(_xOf[i], _lineOf[i] * LineStride, _wOf[i], LineHeight);
    }
}

/// <summary>
/// Uniform-row grid with ITEM SPANNING (E11-L0): each item occupies <c>SpanOf(i)</c> columns (clamped 1..Columns) and
/// items pack row-major — a span that doesn't fit the current row wraps to the next (the "hero as the first full-width
/// row" shape: <c>SpanOf(0) == Columns</c>). Placement depends only on the item count (not cross size), so the table
/// rebuilds only on count change; windowing binary-searches the monotonic row column. STATEFUL — create once and reuse.
/// </summary>
public sealed class SpanningGridVirtualLayout : IVirtualLayout
{
    public readonly int Columns;
    public readonly float RowHeight, Gap;
    private readonly Func<int, int> _spanOf;

    private int _count = -1;
    private int _rowCount;
    private int[] _row = [], _col = [], _span = [];

    public SpanningGridVirtualLayout(int columns, float rowHeight, float gap, Func<int, int> spanOf)
    {
        Columns = Math.Max(1, columns);
        RowHeight = rowHeight <= 0 ? 1f : rowHeight;
        Gap = gap;
        _spanOf = spanOf;
    }

    private float RowStride => RowHeight + Gap;

    private void Ensure(int n)
    {
        if (n == _count) return;
        _count = n;
        if (_row.Length < n) { _row = new int[n]; _col = new int[n]; _span = new int[n]; }
        int row = 0, col = 0;
        for (int i = 0; i < n; i++)
        {
            int span = Math.Clamp(_spanOf(i), 1, Columns);
            if (col + span > Columns) { row++; col = 0; }
            _row[i] = row; _col[i] = col; _span[i] = span;
            col += span;
        }
        _rowCount = n == 0 ? 0 : row + 1;
    }

    public float ContentExtent(int n, float cross)
    {
        Ensure(n);
        return _rowCount <= 0 ? 0f : _rowCount * RowHeight + (_rowCount - 1) * Gap;
    }

    public void Window(int n, float cross, float viewport, float offset, int overscan, out int first, out int last)
    {
        Ensure(n);
        if (n == 0) { first = last = 0; return; }
        int firstRow = Math.Max(0, (int)MathF.Floor(offset / RowStride) - overscan);
        int lastRow = (int)MathF.Ceiling((offset + viewport) / RowStride) + overscan;
        first = LowerBoundRow(firstRow, n);
        last = LowerBoundRow(lastRow + 1, n);
        if (last < first) last = first;
    }

    /// <summary>First item index whose row ≥ <paramref name="row"/> (the row column is monotonic non-decreasing).</summary>
    private int LowerBoundRow(int row, int n)
    {
        int lo = 0, hi = n;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_row[mid] < row) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    public RectF ItemRect(int i, float cross)
    {
        Ensure(Math.Max(_count, i + 1));
        float cw = (cross - (Columns - 1) * Gap) / Columns;
        float w = _span[i] * cw + (_span[i] - 1) * Gap;
        return new RectF(_col[i] * (cw + Gap), _row[i] * RowStride, w, RowHeight);
    }
}
