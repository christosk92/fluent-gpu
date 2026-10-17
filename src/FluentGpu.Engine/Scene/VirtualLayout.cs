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
/// (group headers = a measured item kind + sticky-header hook), <see cref="GridVirtualLayout"/> when
/// <c>itemHeight ≤ 0</c> (measured rows — virtualization.md ItemExtent=0 ⇒ measure).
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
/// The STRUCTURAL-EDIT seam over a measured layout — the counterpart to <see cref="IMeasuredVirtualLayout.SetMeasured"/>
/// for a count change whose shape is KNOWN. A plain count change can only be resolved index-by-index (everything past
/// the old count is new), which is right for an append and wrong for an insert/removal in the MIDDLE: the tail's rows
/// survive at shifted indices, and re-seeding them to an estimate is what makes a mixed-extent list jump its content
/// extent and re-pin its scroll anchor against a stale offset (the disclosure/expander flicker). A composing control
/// that knows the disclosed band calls <see cref="Splice"/> BEFORE the new count reaches layout, so every surviving row
/// keeps its measured extent and the disclosure's travel distance is computed from real geometry.
/// Built-ins: <see cref="MeasuredStackVirtualLayout"/>, <see cref="GroupedListVirtualLayout"/>.
/// </summary>
/// <summary>A measured layout whose extents can be REWRITTEN out of band — a wholesale reseed back to its seeds — between
/// two layout passes. Such a rewrite moves every row below the first rewritten one with no plan frame shift, which on its
/// own is an unanchored jump (and the re-measure that follows shifts the plan by the correction, so the net is a jump of
/// the whole rewrite). The layout engine takes the rewrite's effect on the scroll anchor in the next pass and shifts the
/// plan frame by it, in the same call as that pass's measured corrections (<c>FlexLayout.ArrangeVirtual</c>): a reseed
/// keeps the anchor row where the user sees it, exactly like a measured correction above the viewport.</summary>
public interface IAnchoredReseedLayout : IMeasuredVirtualLayout
{
    /// <summary>Consumes the pending out-of-band rewrite (if any): <paramref name="delta"/> = how far it moved item
    /// <paramref name="anchorIndex"/>'s offset (new − old). False when nothing was rewritten since the last call.</summary>
    bool TakeReseedShift(int anchorIndex, out double delta);
}

public interface ISplicingVirtualLayout : IMeasuredVirtualLayout
{
    /// <summary>Items the layout currently holds extents for; −1 before its first geometry call.</summary>
    int ItemCount { get; }

    /// <summary>Remove <paramref name="removed"/> items at <paramref name="at"/> and insert <paramref name="inserted"/>
    /// fresh ones there, carrying every surviving item's corrected extent across. Allocation-free unless the table grows.</summary>
    void Splice(int at, int removed, int inserted);
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
/// recycles identically. Columns share the cross size; this is the virtualized UniformGridLayout.
/// <para><paramref name="itemHeight"/> &gt; 0 ⇒ fixed uniform rows (the fast path). <paramref name="itemHeight"/> ≤ 0 ⇒
/// <b>auto row height</b>: each row's extent is the max of its realized cell measures at the cell width
/// (estimate-then-correct via <see cref="IMeasuredVirtualLayout"/> — virtualization.md ItemExtent=0 ⇒ measure).
/// Pass <paramref name="minCellWidth"/> &gt; 0 (with <paramref name="columns"/> = 0) for responsive column count off the
/// cross size — callers never compute cell width or column count.</para></summary>
public sealed class GridVirtualLayout : IVirtualLayout, IMeasuredVirtualLayout, IAnchoredReseedLayout
{
    public readonly int Columns;           // fixed column count when MinCellWidth == 0
    /// <summary>&gt; 0 ⇒ responsive columns from cross (Columns ignored). LIVE through <see cref="SetMinCellWidth"/>: a host re-fits
    /// the mounted grid (a window-tier crossing changes the cell floor) and the viewport keeps its extent source and offset.</summary>
    public float MinCellWidth { get; private set; }
    public readonly float ItemHeight;      // ≤ 0 ⇒ measured rows
    /// <summary>The gap between two cells of a row (the column gap). Also the gap between rows unless a <see cref="RowGap"/> was given. LIVE through
    /// <see cref="SetFit"/> on a responsive grid.</summary>
    public float Gap { get; private set; }

    // The row gap when it differs from the column gap (NaN ⇒ the column gap: the single-gap grid every existing caller builds).
    private float _rowGap = float.NaN;

    /// <summary>The gap between two rows (vertical air). The column <see cref="Gap"/> unless the grid was built (or re-fitted) with its own.</summary>
    public float RowGap => float.IsNaN(_rowGap) ? Gap : _rowGap;
    public readonly float Estimate;        // initial row-height estimate in measured mode

    /// <summary>True when row heights come from cell measure (not a fixed <see cref="ItemHeight"/>).</summary>
    public bool IsMeasured => ItemHeight <= 0f;

    private ExtentTable? _rowTable;
    private float[]? _rowMax;
    private int _itemCount = -1;
    private int _cols = 1;
    private int _geomCols;
    private float _geomCross = float.NaN;

    public GridVirtualLayout(int columns, float itemHeight, float gap = 0f, float minCellWidth = 0f, float estimate = 120f, float rowGap = float.NaN)
    {
        Columns = Math.Max(0, columns);
        MinCellWidth = minCellWidth < 0f ? 0f : minCellWidth;
        ItemHeight = itemHeight;
        Gap = gap;
        _rowGap = rowGap;
        Estimate = estimate <= 0f ? 120f : estimate;
        _cols = Math.Max(1, Columns > 0 ? Columns : 1);
    }

    // A fit (cell floor, column gap, row gap) set by SetFit and not applied yet (NaN = none). It is applied in TakeReseedShift, the layout pass's
    // out-of-band-rewrite seam, so the column or row change re-anchors the scroll exactly like a measured correction: the pass takes the
    // anchor item's offset under the old fit, applies the new one, and shifts the plan frame by the difference.
    private float _pendingMinCell = float.NaN, _pendingGap = float.NaN, _pendingRowGap = float.NaN;
    private bool _pending;

    /// <summary>Re-fit the responsive grid to a new cell floor, keeping its gaps (see <see cref="SetFit"/>).</summary>
    public bool SetMinCellWidth(float minCellWidth) => SetFit(minCellWidth, PendingOrLiveGap, PendingOrLiveRowGap);

    private float PendingOrLiveGap => _pending && !float.IsNaN(_pendingGap) ? _pendingGap : Gap;
    private float PendingOrLiveRowGap => _pending && !float.IsNaN(_pendingRowGap) ? _pendingRowGap : RowGap;

    /// <summary>Re-fit the responsive grid to a new cell floor, column gap and row gap (a responsive grid only: <see cref="MinCellWidth"/> &gt; 0 on both
    /// sides). The fit lands on the next layout pass (<see cref="TakeReseedShift"/>), which keeps the anchor row where the user
    /// sees it. False when nothing changes. The caller marks its viewport layout-dirty on true.</summary>
    public bool SetFit(float minCellWidth, float gap, float rowGap)
    {
        float v = minCellWidth < 0f ? 0f : minCellWidth;
        if (v <= 0f || MinCellWidth <= 0f) return false;   // fixed-column grids keep their shape (a different layout, not a re-fit)
        float curMin = _pending ? _pendingMinCell : MinCellWidth, curGap = _pending ? _pendingGap : Gap, curRow = _pending ? _pendingRowGap : RowGap;
        if (v == curMin && gap == curGap && rowGap == curRow) return false;
        if (v == MinCellWidth && gap == Gap && rowGap == RowGap) { _pending = false; _pendingMinCell = _pendingGap = _pendingRowGap = float.NaN; return true; }
        _pending = true;
        _pendingMinCell = v; _pendingGap = gap; _pendingRowGap = rowGap;
        return true;
    }

    /// <summary>Applies a pending <see cref="SetFit"/>: <paramref name="delta"/> is how far it moved item
    /// <paramref name="anchorIndex"/>'s offset (new − old) at the cross size last laid out. False when nothing was pending.</summary>
    public bool TakeReseedShift(int anchorIndex, out double delta)
    {
        delta = 0.0;
        if (!_pending) return false;
        float cross = float.IsNaN(_geomCross) ? 0f : _geomCross;
        bool anchored = cross > 0f && anchorIndex > 0 && _itemCount > anchorIndex;
        float before = anchored ? OffsetOf(anchorIndex, cross) : 0f;
        MinCellWidth = _pendingMinCell;
        Gap = _pendingGap;
        _rowGap = _pendingRowGap;
        _pending = false;
        _pendingMinCell = _pendingGap = _pendingRowGap = float.NaN;
        if (anchored) delta = OffsetOf(anchorIndex, cross) - before;
        return true;
    }

    /// <summary>Column count at the given cross size (for keyboard nav when columns are responsive).</summary>
    public int EffectiveColumns(float cross) => ColsOf(cross);

    private int ColsOf(float cross)
    {
        if (MinCellWidth > 0f)
        {
            float c = cross <= 0f ? MinCellWidth : cross;
            return Math.Max(1, (int)((c + Gap) / (MinCellWidth + Gap)));
        }
        return Math.Max(1, Columns > 0 ? Columns : 1);
    }

    private static int RowCount(int n, int cols) => n <= 0 ? 0 : (n + cols - 1) / cols;
    private float ColWidth(float cross, int cols) => MathF.Max(1f, (cross - (cols - 1) * Gap) / cols);
    private float FixedRowStride => ItemHeight + RowGap;

    private void Ensure(int n, float cross)
    {
        int cols = ColsOf(cross);
        int rows = RowCount(n, cols);
        bool geom = cols != _geomCols || MathF.Abs(cross - _geomCross) > 0.5f || n != _itemCount;
        _cols = cols;
        if (!IsMeasured) { _itemCount = n; _geomCols = cols; _geomCross = cross; return; }
        if (geom || _rowTable is null || _rowTable.Count != rows)
        {
            _itemCount = n;
            _geomCols = cols;
            _geomCross = cross;
            // RESIZE, never Reset (see MeasuredStackVirtualLayout.Ensure / ExtentTable.Resize): a width change (every frame
            // of a resize drag) or an append keeps the height every surviving row measured. A reset re-seeded the rows
            // ABOVE the anchor to the estimate outside SetMeasured, so no anchor delta shifted the plan and a scrolled grid
            // jumped to another row. A row still re-shrinks at a new width: its max restarts below and in ResetMeasurePass.
            if (_rowTable is null) _rowTable = new ExtentTable(rows, Estimate);
            else _rowTable.Resize(rows, Estimate);
            if (_rowMax is null || _rowMax.Length < rows) _rowMax = new float[rows];   // grows only, never per frame
            else Array.Clear(_rowMax);
        }
    }

    /// <summary>Clears per-row measure accumulators at the start of an arrange pass (rows re-shrink after resize).</summary>
    public void ResetMeasurePass(int itemCount, float cross)
    {
        Ensure(itemCount, cross);
        if (_rowMax is not null) Array.Clear(_rowMax);
    }

    private float RowTop(int row) => IsMeasured
        ? (_rowTable?.OffsetOf(row) ?? row * Estimate) + row * RowGap
        : row * FixedRowStride;

    private float RowHeightAt(int row) => IsMeasured ? (_rowTable?.ExtentAt(row) ?? Estimate) : ItemHeight;

    private int RowAtOffset(float offset)
    {
        if (!IsMeasured || _rowTable is null || _rowTable.Count == 0) return 0;
        int lo = 0, hi = _rowTable.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (RowTop(mid) > offset) hi = mid - 1;
            else lo = mid;
        }
        return lo;
    }

    public float ContentExtent(int n, float cross)
    {
        Ensure(n, cross);
        if (!IsMeasured)
        {
            int rows = RowCount(n, _cols);
            return rows <= 0 ? 0f : rows * ItemHeight + (rows - 1) * RowGap;
        }
        int rc = RowCount(n, _cols);
        return rc <= 0 ? 0f : RowTop(rc - 1) + RowHeightAt(rc - 1);
    }

    public void Window(int n, float cross, float viewport, float offset, int overscan, out int first, out int last)
    {
        Ensure(n, cross);
        if (!IsMeasured)
        {
            int firstRow = Math.Max(0, (int)MathF.Floor(offset / FixedRowStride) - overscan);
            int lastRow = (int)MathF.Ceiling((offset + viewport) / FixedRowStride) + overscan;
            first = Math.Min(n, firstRow * _cols);
            last = Math.Min(n, lastRow * _cols);
            if (last < first) last = first;
            return;
        }
        if (_rowTable is null || _rowTable.Count == 0) { first = last = 0; return; }
        int row0 = RowAtOffset(offset);
        int row1 = RowAtOffset(offset + viewport);
        int fr = Math.Max(0, row0 - overscan);
        int lr = Math.Min(_rowTable.Count - 1, row1 + overscan);
        first = fr * _cols;
        last = Math.Min(n, (lr + 1) * _cols);
        if (last < first) last = first;
    }

    public RectF ItemRect(int i, float cross)
    {
        Ensure(Math.Max(_itemCount, i + 1), cross);
        int row = i / _cols, col = i % _cols;
        float cw = ColWidth(cross, _cols);
        float x = col * (cw + Gap);
        if (x + cw > cross + 0.5f) cw = MathF.Max(1f, cross - x);   // float/resize safety — never spill past cross
        return new RectF(x, RowTop(row), cw, RowHeightAt(row));
    }

    public void SetMeasured(int index, float mainExtent, float crossSize)
    {
        if (!IsMeasured) return;
        Ensure(Math.Max(_itemCount, index + 1), crossSize);
        if (_rowTable is null || _rowMax is null) return;
        int row = index / _cols;
        if ((uint)row >= (uint)_rowMax.Length) return;
        _rowMax[row] = MathF.Max(_rowMax[row], mainExtent);
        _rowTable.SetExtent(row, _rowMax[row]);
    }

    public float OffsetOf(int index, float crossSize)
    {
        Ensure(Math.Max(_itemCount, index + 1), crossSize);
        return RowTop(index / _cols);
    }

    public int IndexAt(float offset, float crossSize)
    {
        Ensure(_itemCount > 0 ? _itemCount : 1, crossSize);
        return RowAtOffset(offset) * _cols;
    }
}

/// <summary>A uniform card grid whose ROW HEIGHT is DERIVED from the (responsive) cell width — height =
/// cellWidth·<see cref="Aspect"/> + <see cref="ExtraHeight"/>. For "square cover + a couple of text lines" cards, the
/// caller passes <c>aspect = 1</c> and a text allowance and never computes a cell width itself: the layout owns BOTH
/// axes off its own cross size, so the cover is always square and the row never under/over-budgets the text (the bug
/// when the app guessed the cell width off a different measurement — scrollbar gutter, resize). Fixed-geometry (the
/// height is a pure function of cross), so it rides the same arrange path as <see cref="GridVirtualLayout"/>.</summary>
public sealed class AspectGridVirtualLayout : IVirtualLayout
{
    public readonly int Columns;
    public readonly float Aspect, ExtraHeight, Gap;
    public AspectGridVirtualLayout(int columns, float aspect, float extraHeight, float gap = 0f)
    { Columns = Math.Max(1, columns); Aspect = aspect <= 0f ? 1f : aspect; ExtraHeight = Math.Max(0f, extraHeight); Gap = gap; }

    private float ColWidth(float cross) => MathF.Max(1f, (cross - (Columns - 1) * Gap) / Columns);
    private float RowHeight(float cross) => ColWidth(cross) * Aspect + ExtraHeight;
    private int RowCount(int n) => (n + Columns - 1) / Columns;

    public float ContentExtent(int n, float cross)
    {
        int rows = RowCount(n);
        return rows <= 0 ? 0f : rows * RowHeight(cross) + (rows - 1) * Gap;
    }

    public void Window(int n, float cross, float viewport, float offset, int overscan, out int first, out int last)
    {
        float stride = RowHeight(cross) + Gap;
        int firstRow = Math.Max(0, (int)MathF.Floor(offset / stride) - overscan);
        int lastRow = (int)MathF.Ceiling((offset + viewport) / stride) + overscan;
        first = Math.Min(n, firstRow * Columns);
        last = Math.Min(n, lastRow * Columns);
        if (last < first) last = first;
    }

    public RectF ItemRect(int i, float cross)
    {
        int row = i / Columns, col = i % Columns;
        float cw = ColWidth(cross), ih = RowHeight(cross);
        return new RectF(col * (cw + Gap), row * (ih + Gap), cw, ih);
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
    // The card band (min / max / gap / fixed width / column cap / lead minimum) is LIVE: a host re-fits a mounted layout through
    // SetFit (a window-tier crossing changes the band) instead of building a new instance, so the viewport's extent source, its
    // scroll offset and every realized card survive the change.
    public float MinCardW { get; private set; }
    public float MaxCardW { get; private set; }
    public float Gap { get; private set; }
    // Rows is LIVE between two multi-row counts (SetRows): a 5-row chart re-fits to 4 rows in place. 1 against more stays the
    // layout's SHAPE (the lead span and the cell math are single-row only), so it never changes on a mounted layout.
    public int Rows { get; private set; }
    public readonly int PerPageOverride;   // 0 ⇒ auto-fit
    public float FixedCardW { get; private set; }      // 0 ⇒ auto-fit
    public int MaxColumns { get; private set; }        // 0 ⇒ unlimited; else the auto-fit never exceeds this many columns
    // Main-axis CONTENT INSETS (halo-bleed gutters, virtualization.md §fill-row): a leading/trailing gutter carved
    // INSIDE the (widened) viewport so the first/last card's elevation halo paints into the gutter instead of hard-
    // clipping at the viewport edge. The engine feeds a viewport already widened by Lead+Trail; the fit subtracts the
    // gutters (cards stay sized to the SHELF width), item i's main position shifts by LeadInset, and ContentExtent
    // carries both. Default 0 ⇒ no gutter (byte-identical to the pre-inset geometry). Allocation-free arithmetic.
    public readonly float LeadInset, TrailInset;
    // E22 — per-shelf lead minimum: the column count a page must reach before a lead span is honoured. 0 (the
    // default) keeps E11's half-row rule (2*span+1 — byte-identical to a layout built before this field existed);
    // a positive value replaces it with max(span+1, LeadMinColumns) — see MinColsFor, the ONE place the rule lives.
    public int LeadMinColumns { get; private set; }

    private float _main, _cross;           // last viewport fed by the engine (main = scroll-axis width)
    private int _perPage = 1;
    private float _cardW;
    // E7 — lead-item span (a wide "hero" first card, PagedShelf's CoverShelf/MixedCovers lead). Plain field, LIVE: a
    // caller flips it via SetLeadSpan on any render with no remount — the next geometry query (ContentExtent/Window/
    // ItemRect) simply sees the new value, since this layout is already stateful-and-reused (create once, hoisted).
    private int _leadSpan = 1;

    public FillRowVirtualLayout(float minCardW = 150f, float maxCardW = 200f, float gap = 0f, int rows = 1,
                                int perPageOverride = 0, float fixedCardW = 0f, int maxColumns = 0,
                                float leadInset = 0f, float trailInset = 0f, int leadMinColumns = 0)
    {
        MinCardW = minCardW <= 0 ? 1f : minCardW;
        MaxCardW = maxCardW < MinCardW ? MinCardW : maxCardW;
        Gap = gap < 0 ? 0f : gap;
        Rows = Math.Max(1, rows);
        PerPageOverride = Math.Max(0, perPageOverride);
        FixedCardW = fixedCardW < 0 ? 0f : fixedCardW;
        MaxColumns = Math.Max(0, maxColumns);
        LeadInset = leadInset < 0f ? 0f : leadInset;
        TrailInset = trailInset < 0f ? 0f : trailInset;
        LeadMinColumns = Math.Max(0, leadMinColumns);
        _cardW = MinCardW;
    }

    /// <summary>Re-fit this mounted layout to a new card band (a host's tier crossing): the band is normalized exactly as the
    /// constructor normalizes it, and the fit is re-derived at once from the last viewport the engine fed, so the next geometry
    /// query already answers for the new band. False (nothing written) when the band is unchanged. The caller marks its viewport
    /// layout-dirty on true: nothing else tells the engine a band moved. Allocation-free.</summary>
    public bool SetFit(float minCardW, float maxCardW, float gap, float fixedCardW, int maxColumns, int leadMinColumns)
    {
        float min = minCardW <= 0 ? 1f : minCardW;
        float max = maxCardW < min ? min : maxCardW;
        float g = gap < 0 ? 0f : gap;
        float fixedW = fixedCardW < 0 ? 0f : fixedCardW;
        int cols = Math.Max(0, maxColumns);
        int leadMin = Math.Max(0, leadMinColumns);
        if (min == MinCardW && max == MaxCardW && g == Gap && fixedW == FixedCardW && cols == MaxColumns && leadMin == LeadMinColumns)
            return false;
        MinCardW = min; MaxCardW = max; Gap = g; FixedCardW = fixedW; MaxColumns = cols; LeadMinColumns = leadMin;
        if (_main > 0f)
        {
            float inner = MathF.Max(0f, _main - LeadInset - TrailInset);
            (_perPage, _cardW) = Fit(inner, MinCardW, MaxCardW, Gap, PerPageOverride, FixedCardW, MaxColumns);
        }
        else _cardW = MinCardW;
        return true;
    }

    /// <summary>Re-fit this mounted layout to a new row count (a host's tier crossing: a chart of five rows per column that takes four
    /// past a rung). Only BETWEEN TWO MULTI-ROW COUNTS: a single-row layout is a different shape (the lead span, the cell math), so
    /// <c>rows</c> of 1, or a layout mounted with 1, writes nothing. The column fit does not depend on the rows; the row height follows
    /// the cross size the engine feeds next. False when nothing changed. The caller marks its viewport layout-dirty on true.
    /// Allocation-free.</summary>
    public bool SetRows(int rows)
    {
        int r = Math.Max(1, rows);
        if (r == Rows || r == 1 || Rows == 1) return false;
        Rows = r;
        return true;
    }

    /// <summary>The page that holds page <paramref name="page"/>'s first item once a page of a MULTI-ROW strip holds
    /// <paramref name="newPerPage"/> items (columns x rows) instead of <paramref name="oldPerPage"/>: <c>floor(page * old / new)</c>, since
    /// a multi-row page starts at item <c>page * cols * rows</c>. A host keeps its place across a live re-fit with it (a five-row page of three
    /// columns on page 2 starts at item 30; at four by four that item is on page 1). Pure; the caller clamps to its live page count.</summary>
    public static int PageKeepingFirstItem(int page, int oldPerPage, int newPerPage)
        => page <= 0 || oldPerPage <= 0 || newPerPage <= 0 || oldPerPage == newPerPage ? Math.Max(0, page) : page * oldPerPage / newPerPage;

    /// <summary>The MINIMUM columns per page at which a lead span of <paramref name="leadSpan"/> cells is honoured
    /// (E22) — the ONE place this rule lives, shared by <see cref="EffectiveLeadSpan"/> (the geometry) and a host's
    /// page-count math (<c>PagedShelfCore.PageCountFor</c>) so the two can never drift. <paramref name="leadMinColumns"/>
    /// <c>== 0</c> ⇒ E11's half-row rule, <c>2*span + 1</c> (at least <c>span + 1</c> ordinary cells beside the
    /// lead — the pre-E22 <c>PerPage &gt; 2*span</c> test, byte for byte); <c>&gt; 0</c> ⇒
    /// <c>max(span + 1, leadMinColumns)</c>: the caller's own threshold, floored so at least ONE ordinary cell still
    /// sits beside the lead (a lead that IS the whole row is never a lead). Pure, allocation-free.</summary>
    public static int MinColsFor(int leadSpan, int leadMinColumns)
        => leadMinColumns > 0 ? Math.Max(leadSpan + 1, leadMinColumns) : 2 * leadSpan + 1;

    /// <summary>Columns shown per page at the current viewport (≥1) — valid after the engine's first <see cref="SetViewport"/>.</summary>
    public int PerPage => _perPage;
    /// <summary>The fitted card width at the current viewport.</summary>
    public float CardW => _cardW;

    /// <summary>The RAW lead span last set via <see cref="SetLeadSpan"/> (unclamped; 1 = no lead item — byte-identical
    /// to a plain fill-row layout). Use <see cref="EffectiveLeadSpan"/> for the value geometry actually applies.</summary>
    public int LeadSpan => _leadSpan;

    /// <summary>Set the lead item's (item 0's) span in columns — the number of cells it occupies, wide-hero style
    /// (E7). LIVE: takes effect on the very next geometry call, no remount, no re-<c>SetViewport</c> required.
    /// Clamped to ≥ 1 here; the value geometry actually uses is <see cref="EffectiveLeadSpan"/>, which additionally
    /// clamps to the current page width and zeroes out on a multi-row grid. Allocation-free (a field write).</summary>
    public void SetLeadSpan(int span) => _leadSpan = Math.Max(1, span);

    /// <summary>The span geometry actually uses (E11 — the "half-row" rule): <c>Rows == 1 &amp;&amp; LeadSpan &gt; 1
    /// &amp;&amp; PerPage &gt; 2*LeadSpan ? LeadSpan : 1</c>. A multi-row grid never spans its lead item (a wide hero
    /// has no meaning stacked across rows — see <see cref="ItemRect"/>'s row-major packing). On a single-row shelf, a
    /// lead that would take HALF the visible row or more reads as a broken shelf, not a lead — so the raw
    /// <see cref="LeadSpan"/> is honoured only while at least <c>LeadSpan + 1</c> ordinary cells remain beside it
    /// (<c>PerPage &gt; 2*LeadSpan</c>, i.e. <c>PerPage - LeadSpan &gt;= LeadSpan + 1</c>); once the viewport narrows
    /// past that point the span collapses to 1 (an ordinary, unspanned lead item) rather than clamping down to
    /// whatever column count remains, which the earlier <c>Math.Clamp(LeadSpan, 1, PerPage)</c> rule allowed all the
    /// way down to a lead that WAS the entire (single-column) row. <c>LeadSpan == 1</c> is already the identity, so
    /// this collapse is invisible to the byte-identical <c>leadSpan: 1</c> fallback either way.
    /// <para>E22 — the threshold itself is <see cref="MinColsFor"/><c>(LeadSpan, LeadMinColumns)</c>: with the default
    /// <see cref="LeadMinColumns"/> of 0 that IS the half-row rule above (<c>PerPage &gt;= 2*LeadSpan + 1</c>); a
    /// per-shelf minimum replaces it with <c>max(LeadSpan + 1, LeadMinColumns)</c>.</para></summary>
    public int EffectiveLeadSpan
        => Rows == 1 && _leadSpan > 1 && _perPage >= MinColsFor(_leadSpan, LeadMinColumns) ? _leadSpan : 1;

    /// <summary>Cells occupied by the first <paramref name="n"/> items at the CURRENT <see cref="EffectiveLeadSpan"/>
    /// <c>s</c>: item 0 occupies <c>s</c> cells, every later item occupies exactly one — so <c>n</c> items occupy
    /// <c>n + s - 1</c> cells (<c>n &lt;= 0</c> ⇒ 0). <c>s == 1</c> makes this identity (<c>CellsOf(n) == n</c>), the
    /// byte-identical fallback. Allocation-free; O(1).</summary>
    public int CellsOf(int n) => n <= 0 ? 0 : n + EffectiveLeadSpan - 1;

    /// <summary>The item index a CELL column belongs to, at the current <see cref="EffectiveLeadSpan"/> <c>s</c>:
    /// cells <c>[0, s)</c> all belong to item 0 (the lead's span), cell <c>c &gt;= s</c> belongs to item
    /// <c>c - s + 1</c>. Inverse of the cell a non-lead item <paramref name="i"/> sits at (<c>i + s - 1</c>).</summary>
    private int ItemAtCell(int cell)
    {
        int s = EffectiveLeadSpan;
        if (s <= 1) return Math.Max(0, cell);
        return cell < s ? 0 : cell - s + 1;
    }

    /// <summary>The first item index realized on page <paramref name="page"/> at the CURRENT <see cref="PerPage"/>
    /// column count and <see cref="EffectiveLeadSpan"/> <c>s</c>: page 0 always starts at item 0 (the lead item, cells
    /// <c>[0, s)</c>); every later page starts <c>s - 1</c> items EARLIER than a plain <c>page * PerPage</c> would,
    /// because the lead item ate <c>s - 1</c> extra cells out of page 0 that a non-spanning grid would have given to
    /// page-0 items — so the page BOUNDARY (in pixels, <c>page * PerPage * (cardW + gap)</c>, the page stride) still
    /// lands on a card edge, but the ITEM it lands on shifts back by that same <c>s - 1</c>. <c>s == 1</c> makes this
    /// identity (<c>FirstItemOfPage(p) == p * PerPage</c>), the byte-identical fallback. Allocation-free; O(1).</summary>
    public int FirstItemOfPage(int page)
    {
        if (page <= 0) return 0;
        return Math.Max(0, page * _perPage - (EffectiveLeadSpan - 1));
    }

    public void SetViewport(float mainExtent, float crossSize)
    {
        if (mainExtent == _main && crossSize == _cross) return;
        _main = mainExtent; _cross = crossSize;
        // The fed viewport is widened by the halo-bleed gutters; fit cards to the INNER (shelf) width so the fitted
        // cardW is unchanged by the insets — the gutters are pure paint headroom, not card space.
        float inner = MathF.Max(0f, mainExtent - LeadInset - TrailInset);
        (_perPage, _cardW) = Fit(inner, MinCardW, MaxCardW, Gap, PerPageOverride, FixedCardW, MaxColumns);
    }

    /// <summary>The viewport→(perPage, cardW) fit — COUNT-INDEPENDENT, cardW capped at <paramref name="maxCardW"/>.
    /// Exposed static so a host control can compute the SAME geometry it will be laid out at (e.g. to size a
    /// width-driven card's height before it knows the realized cell). Allocation-free.
    /// <paramref name="maxColumns"/> (0 = unlimited) clamps the AUTO-FIT column count: a wide viewport stops adding
    /// columns and lets each card grow past <paramref name="minCardW"/> instead (up to <paramref name="maxCardW"/> —
    /// pass an uncapped max for a row that always fills). Ignored by the override/fixed-width paths.</summary>
    public static (int PerPage, float CardW) Fit(float main, float minCardW, float maxCardW, float gap,
                                                 int perPageOverride = 0, float fixedCardW = 0f, int maxColumns = 0)
    {
        minCardW = minCardW <= 0 ? 1f : minCardW;
        maxCardW = maxCardW < minCardW ? minCardW : maxCardW;
        gap = gap < 0 ? 0f : gap;
        if (fixedCardW > 0f)
            return (main <= 1f ? 1 : Math.Max(1, (int)MathF.Floor((main + gap) / (fixedCardW + gap))), fixedCardW);
        if (main <= 1f) return (1, minCardW);
        // Max columns that fit at the MIN card width, then grow columns to pull each card down to ≤ maxCardW.
        int perPage = perPageOverride > 0 ? perPageOverride : Math.Max(1, (int)MathF.Floor((main + gap) / (minCardW + gap)));
        if (perPageOverride == 0 && maxColumns > 0 && perPage > maxColumns) perPage = maxColumns;
        float cardW = (main - (perPage - 1) * gap) / perPage;
        while (perPageOverride == 0 && cardW > maxCardW && (maxColumns <= 0 || perPage < maxColumns))
        { perPage++; cardW = (main - (perPage - 1) * gap) / perPage; }
        cardW = MathF.Min(cardW, maxCardW);   // belt-and-suspenders (and the perPageOverride/maxColumns paths)
        return (Math.Max(1, perPage), cardW <= 0f ? minCardW : cardW);
    }

    private float ColStride => _cardW + Gap;
    private float RowHeight(float cross) => (cross - (Rows - 1) * Gap) / Rows;
    private int ColCount(int n) => (n + Rows - 1) / Rows;

    public float ContentExtent(int n, float cross)
    {
        // Rows == 1 ⇒ one column per cell, so CellsOf(n) IS the column count (identity n+s-1, s==1 ⇒ n == ColCount(n)
        // — the byte-identical fallback). Rows > 1 forces EffectiveLeadSpan back to 1 anyway, but ColCount's row-major
        // grouping (cols = ceil(n/Rows)) is a DIFFERENT shape than CellsOf and must stay the untouched multi-row path.
        int cols = Rows == 1 ? CellsOf(n) : ColCount(n);
        float inner = cols <= 0 ? 0f : cols * _cardW + (cols - 1) * Gap;
        return inner + LeadInset + TrailInset;   // both gutters live in the scroll extent (the widened viewport holds them)
    }

    public void Window(int n, float cross, float viewport, float offset, int overscan, out int first, out int last)
    {
        float stride = ColStride;
        float o = offset - LeadInset;   // items live in INNER content space (item 0 starts at LeadInset); shift the query
        int firstCell = Math.Max(0, (int)MathF.Floor(o / stride) - overscan);
        int lastCellExcl = (int)MathF.Ceiling((o + viewport) / stride) + overscan;
        if (Rows == 1)
        {
            // Cell range → item range through the span-aware inverse (ItemAtCell; identity when s == 1).
            first = Math.Min(n, ItemAtCell(firstCell));
            last = Math.Min(n, lastCellExcl <= 0 ? 0 : ItemAtCell(lastCellExcl - 1) + 1);
        }
        else
        {
            first = Math.Min(n, firstCell * Rows);
            last = Math.Min(n, lastCellExcl * Rows);
        }
        if (last < first) last = first;
    }

    public RectF ItemRect(int i, float cross)
    {
        float rh = RowHeight(cross);
        if (Rows == 1)
        {
            int s = EffectiveLeadSpan;
            if (s > 1)
            {
                // Item 0 spans s cells from the lead inset; every later item i sits at cell (i + s - 1) — see
                // CellsOf/FirstItemOfPage. s == 1 never reaches this branch (falls through to the identical formula
                // below), so a plain shelf's geometry is byte-for-byte unchanged.
                if (i <= 0) return new RectF(LeadInset, 0f, s * _cardW + (s - 1) * Gap, rh);
                return new RectF(LeadInset + (i + s - 1) * ColStride, 0f, _cardW, rh);
            }
            return new RectF(LeadInset + i * ColStride, 0f, _cardW, rh);
        }
        int col = i / Rows, row = i % Rows;
        return new RectF(LeadInset + col * ColStride, row * (rh + Gap), _cardW, rh);
    }
}

/// <summary>
/// The Fenwick variable-extent list behind the <see cref="IMeasuredVirtualLayout"/> seam (E11-L0): every row starts at
/// <see cref="Estimate"/>, gets corrected to its measured extent on realize, and the layout engine re-pins the scroll
/// anchor across corrections. Functionally the engine's legacy <c>ItemLayout = null</c> variable path (kept for
/// compatibility), but USER-REACHABLE: compose it, subclass the idea, or build your own measured layout on the same
/// contract. STATEFUL (owns an <see cref="ExtentTable"/>) — create ONCE and reuse across renders (hoist in a
/// <c>UseMemo</c>); the table self-rebuilds only on item-count change.
/// </summary>
public sealed class MeasuredStackVirtualLayout : IMeasuredVirtualLayout, ISplicingVirtualLayout, IAnchoredReseedLayout
{
    public readonly float Estimate;
    public readonly bool Horizontal;
    /// <summary>Optional ANALYTIC seed: a host whose row heights are a pure function of its model (a planned sidebar,
    /// a fixed ladder of row kinds) supplies the height per index here, so an unmeasured row starts at its REAL extent
    /// instead of one global estimate. Purely a seed — measure feedback still corrects it, so a row the function cannot
    /// predict exactly (a wrapped text band, a photo strip) degrades to the estimate-then-correct path it has today.
    /// Called only on a seed/resize/splice, never per frame.</summary>
    private readonly Func<int, float>? _extentOf;
    private ExtentTable? _table;

    public MeasuredStackVirtualLayout(float estimatedExtent, bool horizontal = false, Func<int, float>? extentOf = null)
    { Estimate = estimatedExtent <= 0 ? 1f : estimatedExtent; Horizontal = horizontal; _extentOf = extentOf; }

    /// <inheritdoc/>
    public int ItemCount => _table?.Count ?? -1;

    private float SeedAt(int index)
    {
        if (_extentOf is null) return Estimate;
        float e = _extentOf(index);
        return float.IsFinite(e) && e >= 0f ? e : Estimate;   // 0 is a real answer (a row the host draws as nothing)
    }

    private void SeedRange(ExtentTable t, int first, int count)
    {
        if (_extentOf is null) return;                       // Resize/Splice already seeded the flat estimate
        int end = System.Math.Min(t.Count, first + count);
        for (int i = System.Math.Max(0, first); i < end; i++) t.SetExtent(i, SeedAt(i));
    }

    private ExtentTable Ensure(int n)
    {
        var t = _table;
        if (t is null) { _table = t = new ExtentTable(n, Estimate); SeedRange(t, 0, n); }
        else if (t.Count != n)
        {
            // RESIZE, never Reset: a count change must not discard the extents every surviving row already measured
            // (ExtentTable.Resize carries the reasoning). Only the appended tail seeds.
            int old = t.Count;
            t.Resize(n, Estimate);
            if (n > old) SeedRange(t, old, n - old);
        }
        return t;
    }

    /// <inheritdoc/>
    public void Splice(int at, int removed, int inserted)
    {
        var t = _table;
        if (t is null) return;
        t.Splice(at, removed, inserted, Estimate);
        SeedRange(t, at, inserted);
    }

    /// <summary>Re-seed EVERY extent from the analytic provider (or the flat estimate when there is none) for a
    /// wholesale model change — a new document, a new query — where index-preserving carry-over would be stale. ANCHORED:
    /// the extents it replaces are kept until the next layout pass takes the rewrite's effect on the scroll anchor
    /// (<see cref="TakeReseedShift"/>) and shifts the plan frame by it, so a reseed never moves the rows on screen (a
    /// seed that disagrees with a row's real size is then corrected by the ordinary anchored re-measure). Allocates the
    /// snapshot — a wholesale model change, never per frame.</summary>
    public void Reseed(int n)
    {
        var t = Ensure(n);
        if (_reseedFrom is null)
        {
            var from = new float[t.Count];
            for (int i = 0; i < from.Length; i++) from[i] = t.ExtentAt(i);
            _reseedFrom = from;   // a second reseed before the next pass keeps the OLDEST extents: one shift covers both
        }
        if (_extentOf is null) t.Reset(n, Estimate);
        else SeedRange(t, 0, n);
    }

    private float[]? _reseedFrom;   // the extents the last Reseed replaced, until a layout pass anchors it

    /// <inheritdoc/>
    public bool TakeReseedShift(int anchorIndex, out double delta)
    {
        delta = 0.0;
        var from = _reseedFrom;
        var t = _table;
        if (from is null || t is null) return false;
        _reseedFrom = null;
        int end = System.Math.Min(System.Math.Max(0, anchorIndex), System.Math.Min(from.Length, t.Count));
        double before = 0.0, after = 0.0;
        for (int i = 0; i < end; i++) { before += from[i]; after += t.ExtentAt(i); }
        delta = after - before;
        return true;
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
public sealed class GroupedListVirtualLayout : IMeasuredVirtualLayout, ISplicingVirtualLayout
{
    public readonly float HeaderEstimate, ItemEstimate;
    private readonly int[] _headers;   // sorted flat indices of group headers
    private ExtentTable? _table;

    /// <summary>W1.2c: bumped by <see cref="SetMeasured"/> only when the corrected extent is an actual DELTA
    /// (compare-before-set) — a consumer can gate a re-render of derived layout (sticky-pin math, rail geometry) on
    /// this instead of guessing from unrelated render churn. Mirrors <c>ExtentTable.SetExtent</c>'s own internal
    /// delta gate rather than widening that shared type's signature with a bool return.</summary>
    public int MeasuredVersion { get; private set; }

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

    /// <inheritdoc/>
    public int ItemCount => _table?.Count ?? -1;

    private void SeedHeaders(ExtentTable t, int from, int toExclusive)
    {
        for (int h = 0; h < _headers.Length; h++)
        {
            int i = _headers[h];
            if (i >= from && i < toExclusive) t.SetExtent(i, HeaderEstimate);
        }
    }

    private ExtentTable Ensure(int n)
    {
        var t = _table;
        if (t is null) { _table = t = new ExtentTable(n, ItemEstimate); SeedHeaders(t, 0, n); }
        else if (t.Count != n)
        {
            // RESIZE, never Reset (see MeasuredStackVirtualLayout.Ensure / ExtentTable.Resize): the surviving rows keep
            // their measured extents and only the appended tail seeds — headers included, since the header index list
            // is fixed at construction and therefore never re-classifies a surviving index.
            int old = t.Count;
            t.Resize(n, ItemEstimate);
            if (n > old) SeedHeaders(t, old, n);
        }
        return t;
    }

    /// <inheritdoc/>
    public void Splice(int at, int removed, int inserted)
    {
        var t = _table;
        if (t is null) return;
        t.Splice(at, removed, inserted, ItemEstimate);
        SeedHeaders(t, at, at + inserted);
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

    public void SetMeasured(int index, float mainExtent, float crossSize)
    {
        if (_table is null || (uint)index >= (uint)_table.Count) return;
        if (_table.ExtentAt(index) != mainExtent) MeasuredVersion++;
        _table.SetExtent(index, mainExtent);
    }
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
