using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
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

    /// <summary>Minimal scroll keeping the expanded CARD fully visible below <paramref name="topInset"/> with at least
    /// <paramref name="drawerPeek"/> of its drawer showing above the viewport bottom. Returns
    /// <paramref name="currentOffset"/> unchanged whenever both already hold — so a mid-viewport expand, and a same-row
    /// switch to a taller drawer, do not move the page at all.
    ///
    /// It deliberately does NOT try to reveal the WHOLE drawer: a full <c>RowCap</c> drawer (header + 10 rows + gap) is
    /// taller than the band left below a sticky header, so "reveal it all" has no satisfying offset and degenerates to
    /// pin-to-top — the exact jump this replaces. Guaranteeing card + a fixed peek always has a solution, and the user
    /// scrolls on to see the rest.
    ///
    /// No extent clamp here beyond a 0 floor: the viewport's ScrollHandle owns the <c>[0, content − viewport]</c> clamp
    /// and re-derives it as the drawer's reflow grows the content, so clamping against a stale contentH would fight it.
    /// A <paramref name="viewportH"/> of 0 means geometry is not resolved yet and returns
    /// <paramref name="currentOffset"/> — never 0, which would read as a jump to the top.</summary>
    public static float MinRevealTarget(float currentOffset, float viewportH,
                                        float cardTop, float cardH, float drawerH,
                                        float topInset = 28f, float drawerPeek = 144f)
    {
        if (viewportH <= 1f) return currentOffset;   // geometry unknown → never move
        float inset = MathF.Max(0f, topInset);
        float h = MathF.Max(0f, cardH);
        float peek = MathF.Min(MathF.Max(0f, drawerPeek), MathF.Max(0f, drawerH));
        float maxOff = cardTop - inset;                 // past this the card slides under the sticky band
        float minOff = cardTop + h + peek - viewportH;  // this much must clear the viewport bottom
        if (minOff > maxOff) return MathF.Max(0f, maxOff);   // no slack → card leading wins (the ScrollHandle.BringIntoView rule)
        return MathF.Max(0f, Math.Clamp(currentOffset, minOff, maxOff));
    }

    /// <summary>ALIGN-TOP scroll target for a just-expanded row: pin its top under the sticky band at
    /// <paramref name="topInset"/> — unlike <see cref="MinRevealTarget"/>'s minimal-motion rule (stay put whenever
    /// already visible), this always lands the row in the SAME place on every open, which is exactly what a caller
    /// opting into <see cref="ExpandedReveal.AlignTop"/> wants.
    ///
    /// No extent clamp here beyond the 0 floor, same reasoning as <see cref="MinRevealTarget"/>: <c>ScrollHandle.ScrollTo</c>
    /// lands a target past today's extent at today's max and keeps the RAW target latched until the drawer's own reflow
    /// grows the content to hold it, so clamping against a stale contentH here would cut the move short.
    ///
    /// A <paramref name="viewportH"/> of ≤1 means geometry is not resolved yet — returns
    /// <paramref name="currentOffset"/> unchanged, never 0 (which would read as a jump to the top).</summary>
    public static float AlignRowTarget(float currentOffset, float viewportH, float cardTop, float topInset)
        => viewportH <= 1f ? currentOffset : MathF.Max(0f, cardTop - MathF.Max(0f, topInset));

}

/// <summary>How <see cref="LazyGrid"/> scrolls the page when a row expands its inline drawer. <see cref="Minimal"/>
/// (the default) moves only as much as needed to keep the card + a peek of its drawer visible, and does nothing at
/// all when both already hold (<see cref="LazyGridMath.MinRevealTarget"/>) — unchanged for every existing caller.
/// <see cref="AlignTop"/> instead always lands the clicked row's top at the SAME place under the sticky band on
/// every open (<see cref="LazyGridMath.AlignRowTarget"/>) — the artist page's album drawer.</summary>
public enum ExpandedReveal { Minimal, AlignTop }

/// <summary>A <see cref="LazyGrid"/>'s column geometry as LIVE props: mount the grid with
/// <c>Embed.Comp(new LazyGridFit(…), () =&gt; new LazyGrid(…))</c> and a later render's fit (a window-tier crossing changes the column
/// floor or the gap) re-fits the SAME grid — its realized cells, its expanded drawer and the page's scroll survive — instead of the
/// caller keying a remount on the fit. Without these props the constructor's values stand (frozen at mount, as before).</summary>
/// <param name="MinColWidth">The narrowest column.</param>
/// <param name="Gap">The gap between columns and rows.</param>
/// <param name="RowExtra">The row height over the cell width (the cover square + its text).</param>
public sealed record LazyGridFit(float MinColWidth, float Gap, float RowExtra);

/// <summary>
/// An IN-PAGE, data-virtualized responsive grid: it lives as a normal section inside a page <c>ScrollView</c> (NOT its own
/// scroller), reserves the full extent for a KNOWN total, and realizes only the rows intersecting the live scroll window —
/// rendering a placeholder for any cell whose data hasn't arrived (pairs with <see cref="VirtualCollection{T}"/>). It calls
/// <c>ensureRange</c> with the visible item range so the data layer pages in. The column COUNT derives from the measured
/// width (responsive); every column is the same share of it (CSS-grid <c>1fr</c>). Rows are a uniform height so the windowing
/// is exact. An optional inline drawer expands a cell in place (iTunes-style), its height reserved in the extent so the page
/// scroll never jumps.
///
/// ONE PLACED BODY. Every realized cell and every realized row's drawer slot is a child of one z-stack body, placed at its
/// grid position. A cell keeps the same parent whatever row is expanded and whatever the column count, so it is never
/// remounted by a drawer moving and every move it makes is a position FLIP from where it was presented. (The grid used to cut
/// the rows into a slice above the drawer and a slice below it: a cell whose row crossed the drawer was remounted at its new
/// place at once, and a cell that stayed in the lower slice FLIPped by its move WITHIN that slice, so on a switch to another
/// row two card rows drew over each other while the page glided, and a close let the rows snap up under the collapsing
/// drawer.) Each realized row owns a drawer slot keyed by the row, 0 tall while the row is shut: opening a row grows its slot,
/// closing it (or opening another row) shrinks it, both on the same spring the cells move on, so the old drawer collapses
/// while the new one opens and the rows between ride in lockstep. The panel inside a slot is keyed by the item: a switch
/// within one row cross-fades the panels while the slot resizes; a closing slot keeps its panel as a fading exit until shut.
///
/// Reactive inputs are read through delegates/signals (count, cell, expanded, the data version), so the autonomous reused
/// component always sees live state. Zero engine changes — it rides the existing <c>OnScrollGeometryChanged</c> + scene
/// geometry. Allocations are per-window-move (the realized slice), never per-frame while still.
/// </summary>
public sealed class LazyGrid : Component
{
    readonly Action<LazyGridVisibleRange>? _visibleRangeChanged;
    readonly float _expandedTopInset;
    readonly float _expandedRevealPeek;
    readonly ExpandedReveal _reveal;
    // Exactly one of the two is set. The Func form subscribes the grid's render to WHATEVER the caller reads inside it
    // (a table's Changed counter ⇒ every publication re-renders the grid); the signal form subscribes to that one
    // value-gated signal only — the churn-free form (the caller runs its count derivation in a UseComputed/Memo and
    // only a changed COUNT reaches the grid).
    readonly Func<int>? _count;                      // legacy: total item count as a tracked read
    readonly IReadSignal<int>? _countSignal;         // churn-free: total item count, gated by value at the caller
    readonly Func<int, float, Element> _cell;        // (index, cellWidth) → card or placeholder
    readonly Action<int, int> _ensureRange;          // (firstIndex, lastIndexExclusive) → page the data in
    float _minColW, _gap, _rowExtra;                 // rowH = cellWidth + _rowExtra (cover square + text/padding); live via LazyGridFit
    readonly int _overscanRows;
    readonly Signal<int>? _expanded;                 // expanded ITEM index (-1/none); null ⇒ no inline drawer
    readonly Func<int, GridDrawerInfo, Element>? _drawer;  // (index, column geometry) → the inline drawer subtree
    readonly Func<int, float>? _drawerHeight;        // (index) → the drawer's exact height (so the extent is exact)

    readonly Signal<float> _w = new(0f);             // own measured width → column count
    // Two windows, two signals, two readers — deliberately NOT one tuple. The REALIZED window (visible rows + overscan,
    // packed by PackKey) is what the render subscribes to: it moves only when a row enters or leaves the realized band,
    // so wheel-speed scrolling inside the band never re-renders the grid. The exact VISIBLE range moves on every row
    // crossing (≈ every frame at wheel speed) and is read by the onVisibleRangeChanged effect alone. Both writes are
    // equality-gated (the Signal<T> setter is SetIfChanged), so an unchanged window is silent. The initial value IS the
    // width-unknown sentinel UpdateWindowKey writes on the mount frame, so that eager first write is silent too (it
    // used to differ from the seed and cost every grid a second, identical 1px render at mount).
    readonly Signal<long> _realized = new(long.MinValue + 1);
    readonly Signal<LazyGridVisibleRange> _visible = new(default);
    // The scroll/geometry effects hand UpdateWindowKey its offset through this field so the untracked call is one
    // cached delegate, not a closure per scroll event; the callback effect does the same for the range it publishes.
    float _windowOffset;
    LazyGridVisibleRange _publishedVisible;
    readonly Action _applyWindow;
    readonly Action _invokeVisibleRangeChanged;
    // Render scratch. The reconciler retains the previous Element tree (Update/RecordChanged diff old against new), so
    // every Children ARRAY handed to a BoxEl must be fresh — the list is only the builder behind ToArray().
    List<Element>? _children;
    // The motion the placed cells and drawer slots move on, chosen by WHAT moved them and kept until something else moves
    // them (a commit can render twice — the grid re-measures its width after the layout — and a choice made only on the
    // render that saw the change would be gone before the commit's FLIPs are measured). A re-fit (column count or row
    // height) glides on the house card re-fit; a drawer opening, closing, switching or re-sizing moves on the reveal spring,
    // the same one its slot resizes on, so the rows ride the drawer's edge exactly.
    bool _refitMotion;
    int _motionCols, _motionExpanded = -1;
    float _motionRowH, _motionDrawerH;
    // The expanded item of the last render: a panel mounting into a row whose slot was ALREADY open (a switch within one row)
    // fades in over the outgoing one; a panel opening a shut slot is revealed by the slot's edge alone.
    int _lastExpandedIndex = -1, _lastExpandedRow = -1;
    // The open drawer's panel, built at the top of the render (before its height is read) and placed in its row's slot.
    Element? _openPanel;
    NodeHandle _node;                                // captured at realize; for content-space position via the scene
    readonly int _initialIndex;                      // >0 ⇒ on first valid layout, scroll the page so this item is at the top
    bool _didInitialScroll;
    int _lastCols;
    float _lastRowH, _lastSectionTop;
    // The restore target this grid posted (ScrollHandle.Restore holds it latched until the extent can hold it); Geometry()
    // windows against it for the frames between the post and the shown offset reaching it. Set on post, cleared once the
    // live offset actually reaches it.
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

    /// <summary>The tracked-count form: <paramref name="count"/> runs inside the grid's render, so the grid re-renders
    /// whenever anything it reads changes — fine for a count derived from one collection version (the gallery's
    /// <c>VirtualCollection</c> pages), a re-render-per-publication trap when it reads shared table counters. Prefer the
    /// <see cref="IReadSignal{T}"/> overload for the latter.</summary>
    public LazyGrid(Func<int> count, Func<int, float, Element> cell, Action<int, int> ensureRange,
                    float minColWidth = 180f, float gap = 12f, float rowExtra = 56f, int overscanRows = 2,
                    Signal<int>? expanded = null, Func<int, GridDrawerInfo, Element>? drawer = null, Func<int, float>? drawerHeight = null,
                    int initialIndex = 0, Action<LazyGridVisibleRange>? onVisibleRangeChanged = null,
                    float expandedTopInset = 28f, float expandedRevealPeek = 144f,
                    ExpandedReveal reveal = ExpandedReveal.Minimal)
        : this(count, null, cell, ensureRange, minColWidth, gap, rowExtra, overscanRows, expanded, drawer, drawerHeight,
               initialIndex, onVisibleRangeChanged, expandedTopInset, expandedRevealPeek, reveal)
    { }

    /// <summary>The churn-free form: the render subscribes to <paramref name="count"/> and nothing else of the caller's.
    /// The caller gates the count BY VALUE — typically <c>UseComputed(() => { …read the tables…; return total; })</c>
    /// (a <see cref="Memo{T}"/> re-evaluates on every publication but notifies only when the total moved) or a
    /// <see cref="Signal{T}"/> written with <c>SetIfChanged</c> — so a publication that leaves the total unchanged never
    /// reaches the grid. Anything a cell must react to besides the count (a late row landing behind a placeholder) needs
    /// its own signal path; the grid does not re-render for it.</summary>
    public LazyGrid(IReadSignal<int> count, Func<int, float, Element> cell, Action<int, int> ensureRange,
                    float minColWidth = 180f, float gap = 12f, float rowExtra = 56f, int overscanRows = 2,
                    Signal<int>? expanded = null, Func<int, GridDrawerInfo, Element>? drawer = null, Func<int, float>? drawerHeight = null,
                    int initialIndex = 0, Action<LazyGridVisibleRange>? onVisibleRangeChanged = null,
                    float expandedTopInset = 28f, float expandedRevealPeek = 144f,
                    ExpandedReveal reveal = ExpandedReveal.Minimal)
        : this(null, count, cell, ensureRange, minColWidth, gap, rowExtra, overscanRows, expanded, drawer, drawerHeight,
               initialIndex, onVisibleRangeChanged, expandedTopInset, expandedRevealPeek, reveal)
    { }

    LazyGrid(Func<int>? count, IReadSignal<int>? countSignal, Func<int, float, Element> cell, Action<int, int> ensureRange,
             float minColWidth, float gap, float rowExtra, int overscanRows,
             Signal<int>? expanded, Func<int, GridDrawerInfo, Element>? drawer, Func<int, float>? drawerHeight,
             int initialIndex, Action<LazyGridVisibleRange>? onVisibleRangeChanged,
             float expandedTopInset, float expandedRevealPeek, ExpandedReveal reveal)
    {
        _count = count; _countSignal = countSignal; _cell = cell; _ensureRange = ensureRange;
        _minColW = minColWidth; _gap = gap; _rowExtra = rowExtra; _overscanRows = Math.Max(0, overscanRows);
        _expanded = expanded; _drawer = drawer; _drawerHeight = drawerHeight;
        _initialIndex = Math.Max(0, initialIndex);
        _visibleRangeChanged = onVisibleRangeChanged;
        _expandedTopInset = MathF.Max(0f, expandedTopInset);
        _expandedRevealPeek = MathF.Max(0f, expandedRevealPeek);
        _reveal = reveal;
        _applyWindow = () => UpdateWindowKey(_windowOffset);
        _invokeVisibleRangeChanged = () => _visibleRangeChanged?.Invoke(_publishedVisible);
    }

    /// <summary>The total item count as the RENDER reads it (tracked: the signal form subscribes to the one signal, the
    /// Func form to whatever the caller reads).</summary>
    int ReadCount() => _countSignal is { } sig ? sig.Value : _count!();

    /// <summary>The total item count outside a render (never subscribes).</summary>
    int PeekCount() => _countSignal is { } sig ? sig.Peek() : _count!();

    public override Element Render()
    {
        // The live fit (LazyGridFit props), when the caller mounts the grid with one: a re-fit lands on the fields every render reads.
        if (UsePropsOrDefault<LazyGridFit>() is { } fit) { _minColW = fit.MinColWidth; _gap = fit.Gap; _rowExtra = fit.RowExtra; }
        int count = ReadCount();
        float w = _w.Value;
        var scrollSig = UseContext(LazyScroll.Slot);
        float publishedScrollOffset = scrollSig?.Peek() ?? 0f;
        // Subscribe to the REALIZED window only. The exact visible range is the effect's business below; reading it here
        // was what re-rendered the grid on every row crossing (and rebuilt the whole realized slice) at wheel speed.
        _ = _realized.Value;
        int expandedIndex = _expanded?.Value ?? -1;

        UseSignalEffect(() =>
        {
            _windowOffset = scrollSig?.Value ?? 0f;
            Reactive.Untrack(_applyWindow);
        });

        // The visible-range callback follows the _visible signal, not the render: a row crossing inside the realized
        // band costs one callback and no re-render. The callback runs untracked (as the keyed effect it replaces did),
        // so whatever the host reads while handling it never subscribes this effect.
        UseEffect(() =>
        {
            _publishedVisible = _visible.Value;
            Reactive.Untrack(_invokeVisibleRangeChanged);
        });

        System.Diagnostics.Debug.Assert(_overscanRows >= 1,
            "LazyGrid overscanRows must be >= 1 for the 24px offset floor");

        bool widthKnown = w > 1f;
        int cols = widthKnown ? Math.Max(1, (int)((w + _gap) / (_minColW + _gap))) : 1;
        float cellW = widthKnown ? MathF.Max(_minColW * 0.5f, (w - (cols - 1) * _gap) / cols) : 0f;
        float rowH = widthKnown ? cellW + _rowExtra : 0f;
        int totalRows = count <= 0 ? 0 : (count + cols - 1) / cols;

        (float sectionTop, float viewportH, float scrollOffset, bool pageScrollLive) = Geometry(publishedScrollOffset);
        int expandedRow = expandedIndex >= 0 ? expandedIndex / cols : -1;
        // The open drawer's panel is built FIRST, before its height is read: the builder is where the caller derives the
        // height it then reports (a verdict off the item it opens), so a height read before it is the PREVIOUS item's (or
        // none on a first open) and the slot would open to the wrong height, or not at all.
        _openPanel = expandedIndex >= 0 && widthKnown && _drawer is { } build
            ? build(expandedIndex, new GridDrawerInfo(cols, expandedIndex - expandedRow * cols, cellW, _gap,
                                                      (expandedIndex - expandedRow * cols) * (cellW + _gap)))
            : null;
        float drawerH = expandedIndex >= 0 && _drawerHeight is { } dh ? dh(expandedIndex) : 0f;
        bool hasViewport = viewportH < 1e8f;
        float scrollInSection = scrollOffset - sectionTop;

        // One structural shape at every scroll position. Compute clamps an offscreen grid to its first/last realization
        // window while the exact spacers keep its total extent invariant; there is no alternate "empty spacer" subtree
        // at the section boundary for the page scrollbar to remeasure.
        // Unknown geometry keeps a one-row probe window (RealizeWindowH) and ZERO overscan. DiscoGrid's
        // overscanRows: 4 on top of that probe used to realize 5 rows × 3 facets on the artist mount frame.
        int realizeOverscan = hasViewport ? _overscanRows : 0;
        var view = widthKnown
            ? LazyGridMath.Compute(scrollInSection, RealizeWindowH(viewportH, rowH), rowH, totalRows,
                                   realizeOverscan, expandedRow, drawerH)
            : new LazyGridMath.View(0, -1, 0f, 0f, false);

        // The windows also move WITHOUT a scroll — a page of data landing (count), the width refitting (cols/rowH), a
        // drawer opening or resolving its height. Re-derive both keys post-layout when any of those change; the scroll
        // effect above covers every other move. Equality-gated writes: a window that did not move stays silent, and a
        // realized key that did (the first real-width layout) re-renders once through the normal path.
        UseLayoutEffect(() =>
        {
            _windowOffset = scrollSig?.Peek() ?? 0f;
            Reactive.Untrack(_applyWindow);
        }, DepKey.Combine(DepKey.From(count, cols, expandedIndex, BitConverter.SingleToInt32Bits(rowH)),
                          DepKey.From(drawerH)));

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
        UseLayoutEffect(() =>
        {
            float newTop = Geometry().sectionTop;
            if (expandedIndex < 0 && oldCols > 0 && oldCols != cols)
                PreserveColumnAnchor(oldCols, oldRowH, oldSectionTop, cols, rowH, newTop);
            _lastCols = cols;
            _lastRowH = rowH;
            _lastSectionTop = newTop;
        }, DepKey.From(BitConverter.SingleToInt32Bits(rowH), cols));

        // Keyed on expandedIndex ALONE, not drawerH: the host now reserves the drawer's final height on the click
        // frame (a pure verdict derived from the card's advertised track count), so re-firing this on every height
        // change chased the shimmer -> real-rows transition with a second scroll glide. The effect still reads the
        // CURRENT drawerH/rowH when it runs, it just doesn't re-fire because they changed.
        UseLayoutEffect(() =>
        {
            if (expandedIndex >= 0)
                BringExpandedIntoView(expandedRow, rowH, drawerH);
        }, DepKey.From(expandedIndex));

        if (_initialIndex > 0 && !_didInitialScroll && count > _initialIndex && widthKnown && hasViewport)
            MaybeInitialScroll(sectionTop, rowH, cols);

        if (!widthKnown || totalRows == 0)
            return Root(new BoxEl { Height = 1f });

        ChooseMotion(cols, rowH, expandedIndex, drawerH);
        float drawer = expandedRow >= 0 ? MathF.Max(0f, drawerH) : 0f;
        List<Element> children = BodyChildren(view, cols, cellW, rowH, count, expandedIndex, expandedRow, drawer);
        _lastExpandedIndex = expandedIndex;
        _lastExpandedRow = expandedRow;
        // The body reserves the whole extent (every row plus the open drawer); its height change rides out to everything
        // below the grid on the same reveal spring, so the next section never jumps while the rows above it glide.
        return Root(new BoxEl
        {
            Key = "lazy-body", ZStack = true, Height = totalRows * rowH + drawer, Animate = s_bodyMotion,
            Children = children.ToArray(),
        });
    }

    static readonly TransitionDynamics s_revealDynamics = MotionTok.Reveal.ToDynamics();
    static readonly LayoutTransition s_bodyMotion = new(TransitionChannels.Size, s_revealDynamics, SizeMode.FlowReveal);
    static readonly LayoutTransition s_cellDrawerMotion = MotionRecipes.CardRefit with { Dynamics = s_revealDynamics };
    // A slot moves with its row and clips its panel to the spring's presented height; its panel lays out at the slot's FULL
    // height (the panel's own size), so a closing slot wipes up over a panel that stays put.
    static readonly LayoutTransition s_slotDrawerMotion = new(TransitionChannels.Bounds, s_revealDynamics, SizeMode.Reveal, Axes: SizeAxes.Height);
    static readonly LayoutTransition s_slotRefitMotion = s_slotDrawerMotion with { Dynamics = MotionRecipes.CardRefit.Dynamics };
    // A panel leaving its slot (closed, or replaced within the row) drops out fast (a WinUI ControlFast fade on the
    // decelerating curve) while the slot's edge closes over it, so a switch within a row never shows two panels' text over
    // each other; one arriving in an already-open slot fades in on WinUI's entrance (ControlNormal, FluentDecelerate) just
    // after the outgoing one has mostly gone.
    const float PanelOutMs = 150f, PanelInMs = 250f, PanelInDelayMs = 50f;
    static readonly LayoutTransition s_panelSwap = new(TransitionChannels.Opacity,
        TransitionDynamics.Tween(PanelInMs, Easing.FluentDecelerate),
        Enter: new EnterExit(Opacity: 0f, Active: true, DelayMs: PanelInDelayMs), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(PanelOutMs, Easing.FluentDecelerate));
    static readonly LayoutTransition s_panelExit = s_panelSwap with { Enter = default };

    /// <summary>Which motion the placed children move on this commit (see <see cref="_refitMotion"/>): a re-fit when the
    /// column count or the row height moved, the drawer's spring when the expanded item or its height moved, else the last.</summary>
    void ChooseMotion(int cols, float rowH, int expandedIndex, float drawerH)
    {
        bool refit = _motionCols > 0 && (cols != _motionCols || MathF.Abs(rowH - _motionRowH) > 0.5f);
        bool drawerMoved = expandedIndex != _motionExpanded || MathF.Abs(drawerH - _motionDrawerH) > 0.5f;
        if (refit) _refitMotion = true;
        else if (drawerMoved) _refitMotion = false;
        _motionCols = cols; _motionRowH = rowH; _motionExpanded = expandedIndex; _motionDrawerH = drawerH;
    }

    /// <summary>Derive both window keys from the live scene geometry at <paramref name="offset"/> and write each signal
    /// only when its value moved. Always called untracked (the scroll effect and the geometry layout-effect wrap it), so
    /// neither the count derivation nor the drawer-height callback subscribes anything here.</summary>
    void UpdateWindowKey(float offset)
    {
        int count = PeekCount();
        float w = _w.Peek();
        if (w <= 1f) { _realized.Value = long.MinValue + 1; _visible.Value = default; return; }

        int cols = Math.Max(1, (int)((w + _gap) / (_minColW + _gap)));
        float cellW = MathF.Max(_minColW * 0.5f, (w - (cols - 1) * _gap) / cols);
        float rowH = cellW + _rowExtra;
        int totalRows = count <= 0 ? 0 : (count + cols - 1) / cols;
        int expanded = _expanded?.Peek() ?? -1;
        float drawerH = expanded >= 0 && _drawerHeight is { } height ? height(expanded) : 0f;
        (float sectionTop, float viewportH, float sceneOffset, _) = Geometry(offset);
        offset = sceneOffset;
        float scrollInSection = offset - sectionTop;
        int expandedRow = expanded >= 0 ? expanded / cols : -1;
        int realizeOverscan = viewportH > 1e8f ? 0 : _overscanRows;
        var view = LazyGridMath.Compute(scrollInSection, RealizeWindowH(viewportH, rowH), rowH, totalRows,
                                        realizeOverscan, expandedRow, drawerH);
        bool intersects = viewportH < 1e8f && scrollInSection + viewportH > 0f
            && scrollInSection < totalRows * rowH + MathF.Max(0f, drawerH);
        var visible = intersects
            ? LazyGridMath.VisibleRange(scrollInSection, viewportH, rowH, count, cols, expandedRow, drawerH)
            : new LazyGridVisibleRange(0, 0, cols);
        // Two independent equality-gated writes: a row crossing inside the realized band moves _visible alone (one
        // callback, no re-render); a row entering/leaving the band moves _realized (one re-render).
        _realized.Value = PackKey(view);
        _visible.Value = visible;
    }

    /// <summary>The placed body's children: the realized rows' cells, then one drawer slot per realized row (drawn over the
    /// cells, so a slot an interrupted motion briefly overlaps stays on top). A row's top is its index times the row height,
    /// plus the open drawer when the row is below it; a slot sits under its row's folded gap, the row's full pitch down.</summary>
    List<Element> BodyChildren(in LazyGridMath.View view, int cols, float cellW, float rowH, int count,
                               int expandedIndex, int expandedRow, float drawer)
    {
        var children = _children ??= new List<Element>(32);
        children.Clear();
        if (view.LastRow < view.FirstRow) return children;
        LayoutTransition cellMotion = _refitMotion ? MotionRecipes.CardRefit : s_cellDrawerMotion;
        float pitch = cellW + _gap;
        for (int row = view.FirstRow; row <= view.LastRow; row++)
        {
            float top = RowTop(row, rowH, expandedRow, drawer);
            int end = Math.Min(count, (row + 1) * cols);
            for (int idx = row * cols; idx < end; idx++)
            {
                children.Add(new BoxEl
                {
                    Key = "lazy-cell:" + idx,
                    Direction = 1, Width = cellW, Height = rowH,
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4((idx - row * cols) * pitch, top, 0f, 0f),
                    // The motion stays on every cell, moved or not: a cell whose move lands on a second render of the
                    // commit must still find it (see _refitMotion).
                    Animate = cellMotion,
                    Children = [_cell(idx, cellW)],
                });
            }
        }
        if (_drawer is not null)
        {
            LayoutTransition slotMotion = _refitMotion ? s_slotRefitMotion : s_slotDrawerMotion;
            for (int row = view.FirstRow; row <= view.LastRow; row++)
            {
                bool open = row == expandedRow && drawer > 0f && _openPanel is not null;
                Element[] panel = [];
                if (open)
                {
                    // A switch within an open row fades the new panel in over the outgoing one; an opening slot reveals it.
                    bool swap = _lastExpandedRow == row && _lastExpandedIndex >= 0 && _lastExpandedIndex != expandedIndex;
                    panel =
                    [
                        new BoxEl
                        {
                            Key = "lazy-panel:" + expandedIndex, Direction = 1,
                            Animate = swap ? s_panelSwap : s_panelExit,
                            Children = [_openPanel!],
                        },
                    ];
                }
                children.Add(new BoxEl
                {
                    Key = "lazy-drawer:" + row,
                    Direction = 1, Height = open ? drawer : 0f, ClipToBounds = true,
                    AlignSelf = FlexAlign.Start,
                    Margin = new Edges4(0f, RowTop(row, rowH, expandedRow, drawer) + rowH, 0f, 0f),
                    HitTestVisible = open,
                    Animate = slotMotion,
                    Children = panel,
                });
            }
        }
        return children;
    }

    static float RowTop(int row, float rowH, int expandedRow, float drawer)
        => row * rowH + (expandedRow >= 0 && row > expandedRow ? drawer : 0f);

    Element Root(Element inner) => new BoxEl
    {
        Direction = 1,
        OnRealized = h => _node = h,
        OnBoundsChanged = r => { if (r.W > 0f && MathF.Abs(r.W - _w.Peek()) > 0.5f) _w.Value = r.W; },
        Children = [inner],
    };

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
        // A coordinate-frame rebase, not a motion — the plan shift moves with every other live arc instead of
        // restarting/interrupting one (the plan clamps to [0, content − viewport]).
        scene.ScrollHandleFor(vp)?.ShiftFrame(delta);
    }

    // This grid's top within the page scroll's CONTENT space (stable across scroll: my abs Y and the content's abs Y both
    // shift by the same -offset), plus the viewport height — read from the nearest ancestor scroll. Defaults before layout.
    // pageLive: true while the page scroller is in motion (wheel/fling/drag/glide — ScrollState.Motion), regardless of
    // whether THIS render's offset happens to have moved.
    (float sectionTop, float viewportH, float offsetY, bool pageLive) Geometry(float fallbackOffset = 0f)
    {
        var scene = Context.Scene;
        if (scene is null || _node.IsNull || !scene.IsLive(_node)) return (0f, 1e9f, fallbackOffset, false);
        var vp = _node;
        for (vp = scene.Parent(vp); !vp.IsNull && !scene.HasScroll(vp); vp = scene.Parent(vp)) { }
        if (vp.IsNull) return (0f, 1e9f, fallbackOffset, false);
        ref ScrollState sc = ref scene.ScrollRef(vp);
        var content = sc.ContentNode;
        if (content.IsNull || !scene.IsLive(content)) return (0f, 1e9f, fallbackOffset, false);
        // Layout-only geometry: sectionTop/content feed a scroll TARGET (content space == layout space), and
        // AbsoluteRect would fold in an ancestor's mid-FLIP paint transform (an Expander host's Reflow, this grid's own
        // inline-drawer reflow) on exactly the frame this runs — see SceneStore.AbsoluteLayoutRect.
        float top = scene.AbsoluteLayoutRect(_node).Y - scene.AbsoluteLayoutRect(content).Y;
        float vh = sc.ViewportH > 1f ? sc.ViewportH : scene.AbsoluteRect(vp).H;
        // Scroll observers publish after layout/animation. During route restoration the scene therefore holds the
        // authoritative offset (or pending target) one frame before the throttled context signal catches up. Window
        // against it so a restored viewport never paints only the old top-window spacers — the posted target is mirrored
        // locally (_pendingRestoreY, cleared once the live offset actually reaches it — see MaybeInitialScroll).
        float effectiveOffset = sc.OffsetY;
        if (_pendingRestoreY is { } py)
        {
            if (MathF.Abs(sc.OffsetY - py) < 1f) _pendingRestoreY = null;
            else effectiveOffset = py;
        }
        bool pageLive = sc.Motion.IsMoving;
        return (top, vh > 1f ? vh : 1e9f, effectiveOffset, pageLive);
    }

    // One-time scroll so item _initialIndex sits at the page-scroll's top — its content-Y = this grid's top + its row * rowH,
    // seeded via ScrollHandle.Restore (latched until the extent can hold it — the same path a ScrollKey restore uses).
    // Runs once geometry is real.
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
        scene.ScrollHandleFor(vp)?.Restore(clamped);
        scene.Mark(vp, NodeFlags.LayoutDirty);
        _didInitialScroll = true;
    }

    void BringExpandedIntoView(int expandedRow, float rowH, float drawerH)
    {
        var scene = Context.Scene;
        if (scene is null || _node.IsNull || !scene.IsLive(_node) || expandedRow < 0) return;
        var vp = _node;
        for (vp = scene.Parent(vp); !vp.IsNull && !scene.HasScroll(vp); vp = scene.Parent(vp)) { }
        if (vp.IsNull) return;

        // Re-read sectionTop POST-layout (like the sibling refit effect) — pairing the render-time value the effect
        // closed over with the post-layout sc.OffsetY is a drift source.
        float rowStart = Geometry().sectionTop + expandedRow * rowH;
        ref ScrollState sc = ref scene.ScrollRef(vp);
        float target = _reveal == ExpandedReveal.AlignTop
            ? LazyGridMath.AlignRowTarget(sc.OffsetY, sc.ViewportH, rowStart, _expandedTopInset)
            : LazyGridMath.MinRevealTarget(sc.OffsetY, sc.ViewportH, rowStart, rowH, drawerH, _expandedTopInset, _expandedRevealPeek);
        if (MathF.Abs(target - sc.OffsetY) < 0.5f) return;
        scene.ScrollHandleFor(vp)?.ScrollTo(target, ScrollMove.Glide);
    }
}
