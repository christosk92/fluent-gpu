using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Scroll;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu.Controls;

/// <summary>The page-scroll offset, published once by the page that owns the outer <c>ScrollView</c> (wire its
/// <c>OnScrollGeometryChanged</c> to a <see cref="Signal{T}"/> and provide it here). A <see cref="LazyGrid"/> deeper in the
/// page reads it to window its rows against the live scroll — the SwiftUI <c>LazyVGrid</c>-in-<c>ScrollView</c> model.</summary>
public static class LazyScroll
{
    public static readonly Context<IReadSignal<float>?> Slot = new(null);
}

/// <summary>Column geometry handed to a <see cref="LazyGrid"/> inline-drawer builder so it can visually connect itself to
/// the expanded cell (e.g. an accent connector under that card). <see cref="Left"/> is the expanded card's left edge in the
/// grid's content space (x=0 == the first column), so a drawer overlay placed at <c>Left</c> lines up exactly with it.</summary>
public readonly record struct GridDrawerInfo(int Columns, int Column, float CellWidth, float Gap, float Left);

/// <summary>The exact, non-overscanned item window currently intersecting a <see cref="LazyGrid"/> viewport.</summary>
public readonly record struct LazyGridVisibleRange(int FirstIndex, int LastIndexExclusive, int Columns);

/// <summary>Pure windowing math for <see cref="LazyGrid"/> — separated so it can be tested headlessly. Maps a scroll band
/// to the visible row range and the spacer heights that reserve the WHOLE collection's extent (so the page scrollbar and
/// everything below the grid are correct even though only a window is realized). An optional inline drawer of height
/// <c>drawerH</c> inserted after <c>expandedRow</c> is accounted for in the extent and spacers.</summary>
public static class LazyGridMath
{
    public readonly record struct View(int FirstRow, int LastRow, float TopPad, float BottomPad, bool DrawerVisible);

    public static View Compute(float scrollInSection, float viewportH, float rowH, int totalRows, int overscanRows,
                               int expandedRow, float drawerH)
    {
        if (totalRows <= 0 || rowH <= 0f || viewportH <= 0f) return new View(0, -1, 0f, 0f, false);
        float drawer = expandedRow >= 0 ? MathF.Max(0f, drawerH) : 0f;
        float contentH = totalRows * rowH + drawer;
        float top = Math.Clamp(scrollInSection, 0f, MathF.Max(0f, contentH - viewportH));
        // Row guess ignores the drawer step (≤ a couple of rows of skew, absorbed by overscan).
        int first = (int)MathF.Floor(top / rowH) - overscanRows;
        int last = (int)MathF.Floor((top + viewportH) / rowH) + overscanRows;
        first = Math.Clamp(first, 0, totalRows - 1);
        last = Math.Clamp(last, first, totalRows - 1);
        // Exact extent bookkeeping: top pad = rows above (+ the drawer if it sits above the window); bottom pad by
        // subtraction so topPad + block + bottomPad == contentH ALWAYS — no scroll-extent drift regardless of the guess.
        float topPad = first * rowH + (expandedRow >= 0 && expandedRow < first ? drawer : 0f);
        bool drawerVisible = expandedRow >= first && expandedRow <= last;
        float blockH = (last - first + 1) * rowH + (drawerVisible ? drawer : 0f);
        float bottomPad = MathF.Max(0f, contentH - topPad - blockH);
        return new View(first, last, topPad, bottomPad, drawerVisible);
    }

    /// <summary>Returns the exact visible items rather than the larger realization band. While the viewport crosses an
    /// inline drawer, its owning row remains the contextual row.</summary>
    public static LazyGridVisibleRange VisibleRange(float scrollInSection, float viewportH, float rowH,
                                                     int itemCount, int columns, int expandedRow, float drawerH)
    {
        int cols = Math.Max(1, columns);
        int totalRows = itemCount <= 0 ? 0 : (itemCount + cols - 1) / cols;
        if (totalRows == 0 || rowH <= 0f || viewportH <= 0f)
            return new LazyGridVisibleRange(0, 0, cols);

        float drawer = expandedRow >= 0 ? MathF.Max(0f, drawerH) : 0f;
        float contentH = totalRows * rowH + drawer;
        float top = Math.Clamp(scrollInSection, 0f, MathF.Max(0f, contentH - viewportH));
        float bottom = MathF.Min(contentH, top + viewportH);

        int RowAt(float y)
        {
            float sample = Math.Clamp(y, 0f, MathF.Max(0f, contentH - 0.001f));
            if (expandedRow < 0 || drawer <= 0f) return (int)MathF.Floor(sample / rowH);
            float drawerTop = (expandedRow + 1) * rowH;
            if (sample < drawerTop) return (int)MathF.Floor(sample / rowH);
            if (sample < drawerTop + drawer) return expandedRow;
            return (int)MathF.Floor((sample - drawer) / rowH);
        }

        int firstRow = Math.Clamp(RowAt(top), 0, totalRows - 1);
        int lastRow = Math.Clamp(RowAt(MathF.Max(top, bottom - 0.001f)), firstRow, totalRows - 1);
        return new LazyGridVisibleRange(firstRow * cols, Math.Min(itemCount, (lastRow + 1) * cols), cols);
    }

    /// <summary>Stable selection anchor for an expanded row. The target depends only on the owning row, not the drawer's
    /// track count, so switching albums in the same row never moves the viewport. The base (drawer-less) extent is used
    /// for the clamp so the target remains valid throughout the drawer's 0→full reflow.</summary>
    public static float ExpandedTarget(float viewportH, float contentH, float rowStart, float drawerH, float topInset = 28f)
    {
        if (viewportH <= 1f || contentH <= viewportH) return 0f;

        float baseContentH = MathF.Max(0f, contentH - MathF.Max(0f, drawerH));
        return Math.Clamp(rowStart - MathF.Max(0f, topInset), 0f, MathF.Max(0f, baseContentH - viewportH));
    }

}

/// <summary>
/// An IN-PAGE, data-virtualized responsive grid: it lives as a normal section inside a page <c>ScrollView</c> (NOT its own
/// scroller), reserves the full extent for a KNOWN total, and realizes only the rows intersecting the live scroll window —
/// rendering a placeholder for any cell whose data hasn't arrived (pairs with <see cref="VirtualCollection{T}"/>). It calls
/// <c>ensureRange</c> with the visible item range so the data layer pages in. The column COUNT derives from the measured
/// width (responsive); each row then lays its cells out at <c>Grow=1, Basis=0</c> (CSS-grid <c>1fr</c>) so the ENGINE sizes
/// the card widths — nothing is hand-sized. Rows are a uniform height so the windowing is exact. An optional inline drawer
/// expands a cell in place (iTunes-style), its height reserved in the extent so the page scroll never jumps.
///
/// Reactive inputs are read through delegates/signals (count, cell, expanded, the data version), so the autonomous reused
/// component always sees live state. Zero engine changes — it rides the existing <c>OnScrollGeometryChanged</c> + scene
/// geometry. Allocations are per-window-move (the realized slice), never per-frame while still.
/// </summary>
public sealed class LazyGrid : Component
{
    readonly Action<LazyGridVisibleRange>? _visibleRangeChanged;
    readonly float _expandedTopInset;
    readonly Func<int> _count;                       // total item count (reads the collection's version/count → reactive)
    readonly Func<int, float, Element> _cell;        // (index, cellWidth) → card or placeholder
    readonly Action<int, int> _ensureRange;          // (firstIndex, lastIndexExclusive) → page the data in
    readonly float _minColW, _gap, _rowExtra;        // rowH = cellWidth + _rowExtra (cover square + text/padding)
    readonly int _overscanRows;
    readonly Signal<int>? _expanded;                 // expanded ITEM index (-1/none); null ⇒ no inline drawer
    readonly Func<int, GridDrawerInfo, Element>? _drawer;  // (index, column geometry) → the inline drawer subtree
    readonly Func<int, float>? _drawerHeight;        // (index) → the drawer's exact height (so the extent is exact)

    readonly Signal<float> _w = new(0f);             // own measured width → column count
    readonly Signal<long> _win = new(long.MinValue); // coarse row-window key — re-render only when (first,last) changes
    NodeHandle _node;                                // captured at realize; for content-space position via the scene
    readonly int _initialIndex;                      // >0 ⇒ on first valid layout, scroll the page so this item is at the top
    bool _didInitialScroll;
    int _lastCols;
    float _lastRowH, _lastSectionTop;
    // The kernel-owned Restore latch (ScrollBody.RestoreX/Y) isn't a readable SceneStore column — Geometry() used to
    // read ScrollState.RestorePending/RestoreY directly (synchronous) for the one/two frames between posting the
    // restore and the kernel's Reclamp landing it. Mirror the same value locally instead: set on post, cleared once
    // the live offset actually reaches it (kernel caught up).
    float? _pendingRestoreY;

    static long PackKey(in LazyGridMath.View v)
        => ((long)(uint)(v.FirstRow + 1) << 40) ^ ((long)(uint)(v.LastRow + 1) << 8) ^ (v.DrawerVisible ? 1L : 0L);

    // Pre-layout, Geometry() reports the 1e9 "viewport unknown yet" sentinel. Windowing against THAT realizes the entire
    // collection on the mount frame — every card, its cover, its overlay and its shimmer in one reconcile. That is the
    // measured page-activation avalanche (a 120.8 ms flush, 198 component renders on the artist page), and the cells past
    // the real viewport are unmounted again one frame later, so nearly all of it is pure waste.
    //
    // Capping the FIRST band is structurally free: the scroll extent is reserved by the spacers (totalRows × rowH), never
    // by the realized cells, so the page scrollbar and everything below the grid are correct either way. Real geometry
    // lands on the very next frame and widens the window through the normal path. Deliberately generous — under-realizing
    // only defers a cell by one frame, while over-realizing is exactly the cost being removed. The raw sentinel is left
    // intact for callers that legitimately test it (the one-shot initial scroll waits for real geometry).
    static float RealizeWindowH(float viewportH, float rowH) => viewportH > 1e8f ? rowH : viewportH;

    public LazyGrid(Func<int> count, Func<int, float, Element> cell, Action<int, int> ensureRange,
                    float minColWidth = 180f, float gap = 12f, float rowExtra = 56f, int overscanRows = 2,
                    Signal<int>? expanded = null, Func<int, GridDrawerInfo, Element>? drawer = null, Func<int, float>? drawerHeight = null,
                    int initialIndex = 0, Action<LazyGridVisibleRange>? onVisibleRangeChanged = null,
                    float expandedTopInset = 28f)
    {
        _count = count; _cell = cell; _ensureRange = ensureRange;
        _minColW = minColWidth; _gap = gap; _rowExtra = rowExtra; _overscanRows = Math.Max(0, overscanRows);
        _expanded = expanded; _drawer = drawer; _drawerHeight = drawerHeight;
        _initialIndex = Math.Max(0, initialIndex);
        _visibleRangeChanged = onVisibleRangeChanged;
        _expandedTopInset = MathF.Max(0f, expandedTopInset);
    }

    public override Element Render()
    {
        int count = _count();
        float w = _w.Value;
        var scrollSig = UseContext(LazyScroll.Slot);
        float publishedScrollOffset = scrollSig?.Peek() ?? 0f;
        _ = _win.Value;
        int expandedIndex = _expanded?.Value ?? -1;

        UseSignalEffect(() =>
        {
            float off = scrollSig?.Value ?? 0f;
            Reactive.Untrack(() => UpdateWindowKey(off));
        });

        System.Diagnostics.Debug.Assert(_overscanRows >= 1,
            "LazyGrid overscanRows must be >= 1 for the 24px offset floor");

        bool widthKnown = w > 1f;
        int cols = widthKnown ? Math.Max(1, (int)((w + _gap) / (_minColW + _gap))) : 1;
        float cellW = widthKnown ? MathF.Max(_minColW * 0.5f, (w - (cols - 1) * _gap) / cols) : 0f;
        float rowH = widthKnown ? cellW + _rowExtra : 0f;
        int totalRows = count <= 0 ? 0 : (count + cols - 1) / cols;

        (float sectionTop, float viewportH, float scrollOffset) = Geometry(publishedScrollOffset);
        int expandedRow = expandedIndex >= 0 ? expandedIndex / cols : -1;
        float drawerH = expandedIndex >= 0 && _drawerHeight is { } dh ? dh(expandedIndex) : 0f;
        bool hasViewport = viewportH < 1e8f;
        float scrollInSection = scrollOffset - sectionTop;
        float contentH = widthKnown ? totalRows * rowH + MathF.Max(0f, drawerH) : 1f;
        bool intersects = widthKnown && (!hasViewport ||
                          (scrollInSection + viewportH > 0f && scrollInSection < contentH));

        // One structural shape at every scroll position. Compute clamps an offscreen grid to its first/last realization
        // window while the exact spacers keep its total extent invariant; there is no alternate "empty spacer" subtree
        // at the section boundary for the page scrollbar to remeasure.
        var view = widthKnown
            ? LazyGridMath.Compute(scrollInSection, RealizeWindowH(viewportH, rowH), rowH, totalRows,
                                   _overscanRows, expandedRow, drawerH)
            : new LazyGridMath.View(0, -1, 0f, 0f, false);
        var visible = hasViewport && intersects
            ? LazyGridMath.VisibleRange(scrollInSection, viewportH, rowH, count, cols, expandedRow, drawerH)
            : new LazyGridVisibleRange(0, 0, cols);
        UseEffect(() =>
        {
            if (hasViewport && intersects) _visibleRangeChanged?.Invoke(visible);
        }, DepKey.From(hasViewport && intersects ? 1 : 0,
                       visible.FirstIndex, visible.LastIndexExclusive, visible.Columns));

        bool hasRows = view.LastRow >= view.FirstRow;
        int ensureFirst = count <= 0 || !hasRows ? 0 : view.FirstRow * cols;
        int ensureLastExclusive = count <= 0 ? 1 : !hasRows ? 0 : Math.Min(count, (view.LastRow + 1) * cols);
        UseEffect(() =>
        {
            if (ensureLastExclusive > ensureFirst) _ensureRange(ensureFirst, ensureLastExclusive);
        }, DepKey.From(ensureFirst, ensureLastExclusive));

        int oldCols = _lastCols;
        float oldRowH = _lastRowH;
        float oldSectionTop = _lastSectionTop;
        bool animateRefit = oldCols > 0 && (oldCols != cols || MathF.Abs(oldRowH - rowH) > 0.5f);
        UseLayoutEffect(() =>
        {
            float newTop = Geometry().sectionTop;
            if (expandedIndex < 0 && oldCols > 0 && oldCols != cols)
                PreserveColumnAnchor(oldCols, oldRowH, oldSectionTop, cols, rowH, newTop);
            _lastCols = cols;
            _lastRowH = rowH;
            _lastSectionTop = newTop;
        }, DepKey.From(BitConverter.SingleToInt32Bits(rowH), cols));

        UseLayoutEffect(() =>
        {
            if (expandedIndex >= 0)
                BringExpandedIntoView(sectionTop, expandedRow, rowH, drawerH);
        }, DepKey.From(expandedIndex));

        if (_initialIndex > 0 && !_didInitialScroll && count > _initialIndex && widthKnown && hasViewport)
            MaybeInitialScroll(sectionTop, rowH, cols);

        if (!widthKnown || totalRows == 0)
            return Root(new BoxEl { Height = 1f });

        List<Element> children = FlatChildren(view, cols, cellW, rowH, count, expandedIndex, expandedRow, animateRefit);
        return Root(new BoxEl { Direction = 1, Gap = 0f, Children = children.ToArray() });
    }

    void UpdateWindowKey(float offset)
    {
        int count = _count();
        float w = _w.Peek();
        if (w <= 1f) { _win.Value = long.MinValue + 1; return; }

        int cols = Math.Max(1, (int)((w + _gap) / (_minColW + _gap)));
        float cellW = MathF.Max(_minColW * 0.5f, (w - (cols - 1) * _gap) / cols);
        float rowH = cellW + _rowExtra;
        int totalRows = count <= 0 ? 0 : (count + cols - 1) / cols;
        int expanded = _expanded?.Peek() ?? -1;
        float drawerH = expanded >= 0 && _drawerHeight is { } height ? height(expanded) : 0f;
        (float sectionTop, float viewportH, float sceneOffset) = Geometry(offset);
        offset = sceneOffset;
        float scrollInSection = offset - sectionTop;
        int expandedRow = expanded >= 0 ? expanded / cols : -1;
        var view = LazyGridMath.Compute(scrollInSection, RealizeWindowH(viewportH, rowH), rowH, totalRows,
                                        _overscanRows, expandedRow, drawerH);
        _win.Value = PackKey(view);
    }

    List<Element> FlatChildren(in LazyGridMath.View view, int cols, float cellW, float rowH, int count,
                               int expandedIndex, int expandedRow, bool animateRefit)
    {
        var children = new List<Element>(5);
        if (view.TopPad > 0.5f) children.Add(new BoxEl { Key = "lazy-top", Height = view.TopPad });
        bool hasRows = view.LastRow >= view.FirstRow;
        if (!view.DrawerVisible && hasRows)
            children.Add(GridSlice(view.FirstRow, view.LastRow, cols, cellW, rowH, count, false, animateRefit));
        else if (_drawer is { } drawer)
        {
            children.Add(GridSlice(view.FirstRow, expandedRow, cols, cellW, rowH, count, false, animateRefit));
            int column = expandedIndex - expandedRow * cols;
            children.Add(drawer(expandedIndex,
                new GridDrawerInfo(cols, column, cellW, _gap, column * (cellW + _gap))));
            if (expandedRow < view.LastRow)
                children.Add(GridSlice(expandedRow + 1, view.LastRow, cols, cellW, rowH, count, true, animateRefit));
        }
        if (view.BottomPad > 0.5f) children.Add(new BoxEl { Key = "lazy-bottom", Height = view.BottomPad });
        return children;
    }

    Element Root(Element inner) => new BoxEl
    {
        Direction = 1,
        OnRealized = h => _node = h,
        OnBoundsChanged = r => { if (r.W > 0f && MathF.Abs(r.W - _w.Peek()) > 0.5f) _w.Value = r.W; },
        Children = [inner],
    };

    Element GridSlice(int firstRow, int lastRow, int cols, float cellW, float rowH, int count, bool isBelow, bool animateRefit)
    {
        int start = firstRow * cols;
        int end = Math.Min(count, (lastRow + 1) * cols);
        var cells = new Element[Math.Max(0, end - start)];
        for (int idx = start; idx < end; idx++)
        {
            cells[idx - start] = new BoxEl
            {
                Key = "lazy-cell:" + idx,
                Direction = 1,
                Animate = animateRefit ? MotionRecipes.CardRefit : null,
                Children = [_cell(idx, cellW)],
            };
        }
        var tracks = new TrackSize[cols];
        Array.Fill(tracks, TrackSize.Star());
        return new GridEl
        {
            Key = isBelow ? "lazy-grid:below" : "lazy-grid",
            Columns = tracks,
            ColGap = _gap,
            RowGap = 0f,
            RowHeight = rowH,
            Children = cells,
        };
    }

    void PreserveColumnAnchor(int oldCols, float oldRowH, float oldTop, int cols, float rowH, float sectionTop)
    {
        var scene = Context.Scene;
        if (scene is null || _node.IsNull || !scene.IsLive(_node) || oldRowH <= 0f) return;
        var vp = _node;
        for (vp = scene.Parent(vp); !vp.IsNull && !scene.HasScroll(vp); vp = scene.Parent(vp)) { }
        if (vp.IsNull) return;
        ref ScrollState sc = ref scene.ScrollRef(vp);
        if (sc.UserScrollActive) return;
        float rel = sc.OffsetY - oldTop;
        if (rel <= 0f) return;
        int oldRow = Math.Max(0, (int)MathF.Floor(rel / oldRowH));
        int anchorIndex = oldRow * oldCols;
        float within = rel - oldRow * oldRowH;
        float target = sectionTop + (anchorIndex / Math.Max(1, cols)) * rowH + within * (rowH / oldRowH);
        float delta = target - sc.OffsetY;
        if (delta == 0f) return;
        // A coordinate-frame rebase, not a motion — AnchorShift moves with every other live intent instead of
        // restarting/interrupting one (the kernel clamps to [0, content − viewport] on its own Reclamp).
        scene.ScrollPort!.Post(ScrollInput.AnchorShift((int)vp.Raw.Index, delta));
    }

    // This grid's top within the page scroll's CONTENT space (stable across scroll: my abs Y and the content's abs Y both
    // shift by the same -offset), plus the viewport height — read from the nearest ancestor scroll. Defaults before layout.
    (float sectionTop, float viewportH, float offsetY) Geometry(float fallbackOffset = 0f)
    {
        var scene = Context.Scene;
        if (scene is null || _node.IsNull || !scene.IsLive(_node)) return (0f, 1e9f, fallbackOffset);
        var vp = _node;
        for (vp = scene.Parent(vp); !vp.IsNull && !scene.HasScroll(vp); vp = scene.Parent(vp)) { }
        if (vp.IsNull) return (0f, 1e9f, fallbackOffset);
        ref ScrollState sc = ref scene.ScrollRef(vp);
        var content = sc.ContentNode;
        if (content.IsNull || !scene.IsLive(content)) return (0f, 1e9f, fallbackOffset);
        float top = scene.AbsoluteRect(_node).Y - scene.AbsoluteRect(content).Y;
        float vh = sc.ViewportH > 1f ? sc.ViewportH : scene.AbsoluteRect(vp).H;
        // Scroll observers publish after layout/animation. During route restoration the scene therefore holds the
        // authoritative offset (or pending target) one frame before the throttled context signal catches up. Window
        // against it so a restored viewport never paints only the old top-window spacers — the kernel's own
        // ScrollBody.RestoreX/Y latch isn't a readable SceneStore column, so mirror the posted target locally
        // (_pendingRestoreY, cleared once the live offset actually reaches it — see MaybeInitialScroll).
        float effectiveOffset = sc.OffsetY;
        if (_pendingRestoreY is { } py)
        {
            if (MathF.Abs(sc.OffsetY - py) < 1f) _pendingRestoreY = null;
            else effectiveOffset = py;
        }
        return (top, vh > 1f ? vh : 1e9f, effectiveOffset);
    }

    // One-time scroll so item _initialIndex sits at the page-scroll's top — its content-Y = this grid's top + its row * rowH,
    // seeded via the kernel's Restore command (applied verbatim while geometry is still resolving, retried each Reclamp —
    // the same path the engine's own scroll-restore uses). Runs once geometry is real.
    void MaybeInitialScroll(float sectionTop, float rowH, int cols)
    {
        var scene = Context.Scene;
        if (scene is null || _node.IsNull || !scene.IsLive(_node)) return;
        var vp = _node;
        for (vp = scene.Parent(vp); !vp.IsNull && !scene.HasScroll(vp); vp = scene.Parent(vp)) { }
        if (vp.IsNull) return;
        ref ScrollState sc = ref scene.ScrollRef(vp);
        float targetY = sectionTop + (_initialIndex / Math.Max(1, cols)) * rowH;
        float clamped = Math.Clamp(targetY, 0f, MathF.Max(0f, sc.ContentH - sc.ViewportH));
        _pendingRestoreY = clamped;
        scene.ScrollPort!.Post(ScrollInput.Restore((int)vp.Raw.Index, sc.OffsetX, clamped));
        scene.Mark(vp, NodeFlags.LayoutDirty);
        _didInitialScroll = true;
    }

    void BringExpandedIntoView(float sectionTop, int expandedRow, float rowH, float drawerH)
    {
        var scene = Context.Scene;
        if (scene is null || _node.IsNull || !scene.IsLive(_node) || expandedRow < 0) return;
        var vp = _node;
        for (vp = scene.Parent(vp); !vp.IsNull && !scene.HasScroll(vp); vp = scene.Parent(vp)) { }
        if (vp.IsNull) return;

        ref ScrollState sc = ref scene.ScrollRef(vp);
        float rowStart = sectionTop + expandedRow * rowH;
        float target = LazyGridMath.ExpandedTarget(sc.ViewportH, sc.ContentH, rowStart, drawerH, _expandedTopInset);
        // Posts a Driven glide through the kernel (ScrollIntoView already no-ops within 0.5 DIP and wakes the frame).
        ScrollIntoView.ScrollTo(Context, vp, target, animate: true);
    }
}
