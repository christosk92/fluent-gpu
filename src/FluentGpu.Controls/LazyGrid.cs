using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
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

    /// <summary>Stable selection anchor for an expanded row. The target depends only on the owning row, not the drawer's
    /// track count, so switching albums in the same row never moves the viewport. The base (drawer-less) extent is used
    /// for the clamp so the target remains valid throughout the drawer's 0→full reflow.</summary>
    public static float ExpandedTarget(float viewportH, float contentH, float rowStart, float drawerH)
    {
        if (viewportH <= 1f || contentH <= viewportH) return 0f;

        const float topInset = 28f;
        float baseContentH = MathF.Max(0f, contentH - MathF.Max(0f, drawerH));
        return Math.Clamp(rowStart - topInset, 0f, MathF.Max(0f, baseContentH - viewportH));
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

    static long PackKey(in LazyGridMath.View v)
        => ((long)(uint)(v.FirstRow + 1) << 40) ^ ((long)(uint)(v.LastRow + 1) << 8) ^ (v.DrawerVisible ? 1L : 0L);

    public LazyGrid(Func<int> count, Func<int, float, Element> cell, Action<int, int> ensureRange,
                    float minColWidth = 180f, float gap = 12f, float rowExtra = 56f, int overscanRows = 2,
                    Signal<int>? expanded = null, Func<int, GridDrawerInfo, Element>? drawer = null, Func<int, float>? drawerHeight = null,
                    int initialIndex = 0)
    {
        _count = count; _cell = cell; _ensureRange = ensureRange;
        _minColW = minColWidth; _gap = gap; _rowExtra = rowExtra; _overscanRows = Math.Max(0, overscanRows);
        _expanded = expanded; _drawer = drawer; _drawerHeight = drawerHeight;
        _initialIndex = Math.Max(0, initialIndex);
    }

    public override Element Render()
    {
        int count = _count();                                  // subscribe → re-render when the total / a page lands
        float w = _w.Value;                                      // subscribe → re-render on resize
        var scrollSig = UseContext(LazyScroll.Slot);
        float scrollOffset = scrollSig?.Peek() ?? 0f;            // hot offset — subscribed only by the bridge effect below
        _ = _win.Value;                                          // subscribe → re-render when the row window changes
        int expandedIndex = _expanded?.Value ?? -1;              // subscribe → re-render on expand/collapse

        // Bridge: the page-scroll offset (24px-throttled) → a value-gated row-window key (~per rowH, not per 24px).
        UseSignalEffect(() =>
        {
            float off = scrollSig?.Value ?? 0f;
            Reactive.Untrack(() =>
            {
                int c = _count();
                float ww = _w.Peek();
                float eff = ww > 1f ? ww : 900f;
                int cols = Math.Max(1, (int)((eff + _gap) / (_minColW + _gap)));
                float cellW = MathF.Max(_minColW * 0.5f, (eff - (cols - 1) * _gap) / cols);
                float rowH = cellW + _rowExtra;
                int totalRows = c <= 0 ? 0 : (c + cols - 1) / cols;
                int expIdx = _expanded?.Peek() ?? -1;
                int expRow = expIdx >= 0 ? expIdx / cols : -1;
                float drawerH = expIdx >= 0 && _drawerHeight is { } dh ? dh(expIdx) : 0f;
                (float sectionTop, float viewportH) = Geometry();
                var v = LazyGridMath.Compute(off - sectionTop, viewportH, rowH, totalRows, _overscanRows, expRow, drawerH);
                _win.Value = PackKey(v);
            });
        });

        System.Diagnostics.Debug.Assert(_overscanRows >= 1, "LazyGrid overscanRows must be >= 1 for the 24px offset floor");

        float eff = w > 1f ? w : 900f;
        int cols = Math.Max(1, (int)((eff + _gap) / (_minColW + _gap)));
        float cellW = MathF.Max(_minColW * 0.5f, (eff - (cols - 1) * _gap) / cols);
        float rowH = cellW + _rowExtra;
        int totalRows = count <= 0 ? 0 : (count + cols - 1) / cols;

        // Where this grid sits within the page scroll's content, and the viewport height — from the live scene geometry.
        (float sectionTop, float viewportH) = Geometry();
        int expandedRow = expandedIndex >= 0 ? expandedIndex / cols : -1;
        float drawerH = expandedIndex >= 0 && _drawerHeight is { } dh ? dh(expandedIndex) : 0f;

        var view = LazyGridMath.Compute(scrollOffset - sectionTop, viewportH, rowH, totalRows, _overscanRows, expandedRow, drawerH);

        // Opening is one coordinated interaction: after final geometry is known, minimally reveal the selected row and
        // drawer through the engine-owned smooth-scroll path. Closing deliberately leaves the viewport alone.
        UseLayoutEffect(() =>
        {
            if (expandedIndex >= 0) BringExpandedIntoView(sectionTop, expandedRow, rowH, drawerH);
        }, DepKey.From(expandedIndex));

        // One-time "skip to item N": once the real geometry is known, scroll the page so item _initialIndex sits at the top
        // (the facet page skips the items already previewed on the artist page). Done once per instance.
        if (_initialIndex > 0 && !_didInitialScroll && count > _initialIndex && rowH > 0f && viewportH < 1e8f)
            MaybeInitialScroll(sectionTop, rowH, cols);

        if (totalRows == 0)
        {
            _ensureRange(0, 1);                        // bootstrap: pull page 0 so the total is learned (dedup'd thereafter)
            return Root(new BoxEl { Height = 1f });    // measure-only stub until the count lands
        }

        // Page the data in for the realized window (+ overscan already folded into first/last).
        _ensureRange(view.FirstRow * cols, Math.Min(count, (view.LastRow + 1) * cols));

        var children = new List<Element>((view.LastRow - view.FirstRow + 1) + 3);
        if (view.TopPad > 0.5f) children.Add(new BoxEl { Key = "lazy-top", Height = view.TopPad });
        for (int r = view.FirstRow; r <= view.LastRow; r++)
        {
            children.Add(Row(r, cols, cellW, rowH, count));
            if (r == expandedRow && view.DrawerVisible && _drawer is { } d)
            {
                int col = expandedIndex - expandedRow * cols;       // expanded card's column → drawer connects to it
                children.Add(d(expandedIndex, new GridDrawerInfo(cols, col, cellW, _gap, col * (cellW + _gap))));
            }
        }
        if (view.BottomPad > 0.5f) children.Add(new BoxEl { Key = "lazy-bottom", Height = view.BottomPad });

        return Root(new BoxEl { Direction = 1, Gap = 0f, Children = children.ToArray() });
    }

    Element Root(Element inner) => new BoxEl
    {
        Direction = 1,
        OnRealized = h => _node = h,
        OnBoundsChanged = r => { if (r.W > 0f && MathF.Abs(r.W - _w.Peek()) > 0.5f) _w.Value = r.W; },
        Children = [inner],
    };

    Element Row(int row, int cols, float cellW, float rowH, int count)
    {
        int start = row * cols;
        var cells = new Element[cols];
        for (int c = 0; c < cols; c++)
        {
            int idx = start + c;
            // 1fr cells: the ENGINE distributes the row width equally (like AutoGrid / CSS-grid `1fr`) — NO hand-set card
            // widths; the card's cover self-sizes (aspect-ratio) into whatever width flex gives it. Content-height +
            // top-aligned, so the leftover (rowH − cardHeight) is real vertical breathing room, not a stretched card.
            cells[c] = idx < count
                ? new BoxEl { Grow = 1f, Basis = 0f, Direction = 1, Children = [_cell(idx, cellW)] }
                : new BoxEl { Grow = 1f, Basis = 0f };   // empty track keeps the column widths uniform on the last row
        }
        // Fixed row height keeps the windowing math exact; AlignItems=Start stops cards from stretching to fill it.
        // (cellW is the engine's own per-column result — passed to the cell only as a sizing HINT, never to set a width.)
        return new BoxEl
        {
            Key = "lazy-row:" + row,
            Direction = 0, Gap = _gap, Height = rowH, AlignItems = FlexAlign.Start, Children = cells,
        };
    }

    // This grid's top within the page scroll's CONTENT space (stable across scroll: my abs Y and the content's abs Y both
    // shift by the same -offset), plus the viewport height — read from the nearest ancestor scroll. Defaults before layout.
    (float sectionTop, float viewportH) Geometry()
    {
        var scene = Context.Scene;
        if (scene is null || _node.IsNull || !scene.IsLive(_node)) return (0f, 1e9f);   // pre-layout: window everything once
        var vp = _node;
        for (vp = scene.Parent(vp); !vp.IsNull && !scene.HasScroll(vp); vp = scene.Parent(vp)) { }
        if (vp.IsNull) return (0f, 1e9f);
        ref ScrollState sc = ref scene.ScrollRef(vp);
        var content = sc.ContentNode;
        if (content.IsNull || !scene.IsLive(content)) return (0f, 1e9f);
        float top = scene.AbsoluteRect(_node).Y - scene.AbsoluteRect(content).Y;
        float vh = sc.ViewportH > 1f ? sc.ViewportH : scene.AbsoluteRect(vp).H;
        return (top, vh > 1f ? vh : 1e9f);
    }

    // One-time scroll so item _initialIndex sits at the page-scroll's top — its content-Y = this grid's top + its row * rowH,
    // seeded via the restore latch + a layout mark (the same path the scroll-restore uses). Runs once geometry is real.
    void MaybeInitialScroll(float sectionTop, float rowH, int cols)
    {
        var scene = Context.Scene;
        if (scene is null || _node.IsNull || !scene.IsLive(_node)) return;
        var vp = _node;
        for (vp = scene.Parent(vp); !vp.IsNull && !scene.HasScroll(vp); vp = scene.Parent(vp)) { }
        if (vp.IsNull) return;
        ref ScrollState sc = ref scene.ScrollRef(vp);
        float targetY = sectionTop + (_initialIndex / Math.Max(1, cols)) * rowH;
        sc.RestoreX = sc.OffsetX;
        sc.RestoreY = Math.Clamp(targetY, 0f, MathF.Max(0f, sc.ContentH - sc.ViewportH));
        sc.RestorePending = true;
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
        float target = LazyGridMath.ExpandedTarget(sc.ViewportH, sc.ContentH, rowStart, drawerH);
        if (MathF.Abs(target - sc.OffsetY) < 1f) return;

        sc.Phase = ScrollIntegrator.WheelAnimating;
        sc.PhaseFlags = ScrollState.PhaseProgrammatic;
        sc.FlingVelocity = 0f;
        sc.PendingTargetY = target;
        Context.ArmScroll?.Invoke(vp);
        Context.RequestRerender();
    }
}
