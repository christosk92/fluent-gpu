using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Text;

namespace FluentGpu.Layout;

/// <summary>
/// Flexbox layout over the SoA scene columns. Two descents: Measure (bottom-up — content/basis base sizes, honoring
/// explicit size + min/max + text content) then Arrange (top-down — distribute free space by flex-grow/shrink,
/// position by justify-content, align the cross axis by align-items/align-self incl. stretch, applying margins).
/// Direction 0 = row (main = X), 1 = column (main = Y). Wrap / grid / absolute positioning are the remaining layout work.
/// </summary>
public sealed class FlexLayout
{
    private readonly SceneStore _scene;
    private readonly IFontSystem _fonts;

    public FlexLayout(SceneStore scene, IFontSystem fonts)
    {
        _scene = scene;
        _fonts = fonts;
    }

    public void Run(NodeHandle root)
    {
        if (root.IsNull) return;
        var size = Measure(root);
        Arrange(root, 0f, 0f, size.Width, size.Height);
    }

    /// <summary>Lay out the root to FILL the window (the conventional top-level behavior) — an auto-sized root takes
    /// the full client area; an explicitly-sized root keeps its size. The slice's direct <see cref="Run(NodeHandle)"/>
    /// keeps content-sizing for golden flexbox checks.</summary>
    public void Run(NodeHandle root, Size2 window)
    {
        if (root.IsNull) return;
        Measure(root, window.Width);
        ref LayoutInput li = ref _scene.Layout(root);
        float w = float.IsNaN(li.Width) ? window.Width : li.Width;
        float h = float.IsNaN(li.Height) ? window.Height : li.Height;
        Arrange(root, 0f, 0f, w, h);
    }

    /// <summary>Re-solve ONLY the subtree rooted at <paramref name="node"/> against its current Bounds (or its
    /// LayoutInput size if set — a SizeMode.Relayout animation writes the interpolated width there each tick). The parent
    /// already placed this node, so this cannot propagate upward — a scoped, per-frame-affordable relayout for live reflow.</summary>
    public void RunSubtree(NodeHandle node)
    {
        if (node.IsNull) return;
        ref LayoutInput li = ref _scene.Layout(node);
        ref RectF b = ref _scene.Bounds(node);
        float w = float.IsNaN(li.Width) ? b.W : li.Width;
        float h = float.IsNaN(li.Height) ? b.H : li.Height;
        Measure(node, w);
        Arrange(node, b.X, b.Y, w, h);
    }

    private static bool Row(in LayoutInput li) => li.Direction == 0;
    private static float Clamp(float v, float min, float max)
    {
        if (!float.IsNaN(min) && v < min) v = min;
        if (!float.IsNaN(max) && v > max) v = max;
        return v;
    }

    private static float DefiniteWidth(in LayoutInput li, float availW)
    {
        float w = !float.IsNaN(li.Width) ? li.Width : availW;
        if (!float.IsNaN(li.MaxW) && !float.IsInfinity(w)) w = MathF.Min(w, li.MaxW);
        else if (!float.IsNaN(li.MaxW) && float.IsInfinity(w)) w = li.MaxW;
        if (!float.IsNaN(li.MinW) && !float.IsInfinity(w)) w = MathF.Max(w, li.MinW);
        return w;
    }

    // ── Measure: fill Bounds.W/H with each node's base (hypothetical) border-box size ──
    private Size2 Measure(NodeHandle node, float availW = float.PositiveInfinity)
    {
        ref LayoutInput li = ref _scene.Layout(node);
        ref NodePaint paint = ref _scene.Paint(node);

        // A scroll/virtual viewport is a layout boundary: its size is its own box (explicit/flex), independent of
        // content — content overflow is what scrolls. (layout.md §4.3/§6.)
        if (_scene.HasScroll(node)) return MeasureViewport(node, in li, availW);
        if (_scene.HasGrid(node)) return MeasureGrid(node, in li, availW);
        if ((_scene.Flags(node) & NodeFlags.ZStack) != 0) return MeasureZStack(node, in li, availW);

        float w, h;
        if (paint.VisualKind == VisualKind.Text)
        {
            float measureW = DefiniteWidth(in li, availW);
            float maxW = li.TextStyle.Wrap != Foundation.TextWrap.NoWrap && !float.IsInfinity(measureW) ? MathF.Max(0f, measureW) : float.PositiveInfinity;
            // Measure cache: skip re-shaping when (text, style, availWidth) are unchanged (the §2.3 down-rule win on a
            // scoped relayout). Pure-function key ⇒ self-invalidating; helps the real shaping path, neutral headless.
            ref TextMeasureCache mc = ref _scene.MeasureCacheRef(node);
            if (mc.Valid && mc.Text == paint.Text && mc.MaxW == maxW && mc.Style == li.TextStyle)
            {
                w = mc.Size.Width; h = mc.Size.Height;
            }
            else
            {
                var m = _fonts.Measure(paint.Text, li.TextStyle, maxW);
                w = m.Size.Width; h = m.Size.Height;
                mc = new TextMeasureCache { Valid = true, Text = paint.Text, Style = li.TextStyle, MaxW = maxW, Size = new Size2(w, h) };
            }
        }
        else
        {
            bool row = Row(li);
            // The width children may occupy (content box). A stretched child in a column gets the full content width
            // (so wrapped text knows where to break); a row's children share it (an upper bound is fine for wrapping).
            float measureW = DefiniteWidth(in li, availW);
            float childAvail = float.IsInfinity(measureW) ? measureW : MathF.Max(0f, measureW - li.Padding.Horizontal);
            if (li.Wrap && TryWrapMainLimit(in li, row, availW, out float wrapMainLimit))
            {
                (w, h) = MeasureWrap(node, in li, row, wrapMainLimit);   // multi-line: main is fixed, cross grows with line count
            }
            else
            {
                // In a ROW with a definite width, a flex-grow child's real width is (childAvail − the fixed siblings),
                // not the whole row. Measure its (possibly wrapping) content against THAT — otherwise a fixed pane +
                // grow content wraps to the entire window and overflows. (A column already stretches children to its
                // full width, so this only matters for the row's main axis.) NoWrap content ignores the bound, so this
                // is a no-op except where it's needed.
                float growAvail = childAvail;
                if (row && !float.IsInfinity(childAvail))
                {
                    float fixedMain = 0f; int cc = 0; bool anyGrow = false;
                    for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
                    {
                        ref LayoutInput cli2 = ref _scene.Layout(c);
                        cc++;
                        if (cli2.FlexGrow > 0f) { anyGrow = true; continue; }
                        float cm = !float.IsNaN(cli2.FlexBasis) ? cli2.FlexBasis : Measure(c, childAvail).Width;
                        fixedMain += cm + MarginMain(cli2, row);
                    }
                    if (anyGrow) growAvail = MathF.Max(0f, childAvail - fixedMain - (cc > 1 ? li.Gap * (cc - 1) : 0f));
                }

                float main = 0f, cross = 0f;
                int n = 0;
                for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
                {
                    ref LayoutInput cli = ref _scene.Layout(c);
                    var cs = Measure(c, row && cli.FlexGrow > 0f ? growAvail : childAvail);
                    float cMain = row ? cs.Width : cs.Height;
                    float cCross = row ? cs.Height : cs.Width;
                    if (!float.IsNaN(cli.FlexBasis)) cMain = cli.FlexBasis;
                    cMain += MarginMain(cli, row);
                    cCross += MarginCross(cli, row);
                    main += cMain;
                    cross = MathF.Max(cross, cCross);
                    n++;
                }
                if (n > 1) main += li.Gap * (n - 1);
                main += row ? li.Padding.Horizontal : li.Padding.Vertical;
                cross += row ? li.Padding.Vertical : li.Padding.Horizontal;
                w = row ? main : cross;
                h = row ? cross : main;
            }
        }

        if (!float.IsNaN(li.Width)) w = li.Width;
        if (!float.IsNaN(li.Height)) h = li.Height;
        w = Clamp(w, li.MinW, li.MaxW);
        h = Clamp(h, li.MinH, li.MaxH);

        ref RectF b = ref _scene.Bounds(node);
        b = new RectF(b.X, b.Y, w, h);
        return new Size2(w, h);
    }

    // ── Arrange: position + size children within the node's final box ──
    private void Arrange(NodeHandle node, float x, float y, float finalW, float finalH)
    {
        ref RectF b = ref _scene.Bounds(node);
        b = new RectF(x, y, finalW, finalH);

        ref LayoutInput li = ref _scene.Layout(node);
        if (_scene.HasScroll(node)) { ArrangeViewport(node, finalW, finalH, in li); return; }
        if (_scene.HasGrid(node)) { ArrangeGrid(node, finalW, finalH, in li); return; }
        if ((_scene.Flags(node) & NodeFlags.ZStack) != 0) { ArrangeZStack(node, finalW, finalH, in li); return; }
        if (_scene.FirstChild(node).IsNull) return;
        bool row = Row(li);

        if (li.Wrap) { ArrangeWrap(node, finalW, finalH, in li, row); return; }

        float availMain = (row ? finalW : finalH) - (row ? li.Padding.Horizontal : li.Padding.Vertical);
        float availCross = (row ? finalH : finalW) - (row ? li.Padding.Vertical : li.Padding.Horizontal);
        float padMainStart = row ? li.Padding.Left : li.Padding.Top;
        float padCrossStart = row ? li.Padding.Top : li.Padding.Left;

        // A column's final cross-size is often only known during arrange (for example, a NavigationView content
        // frame after its fixed pane has consumed 320px). Re-measure stretch children against that final width before
        // computing main sizes, otherwise wrapped text can keep its single-line measured height/width and drag the
        // page wider than the actual frame.
        if (!row && !float.IsInfinity(availCross))
        {
            for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
            {
                ref LayoutInput cli = ref _scene.Layout(c);
                FlexAlign align = cli.AlignSelf == FlexAlign.Auto ? li.AlignItems : cli.AlignSelf;
                float crossMargin = MarginCross(cli, row);
                bool hasExplicitCross = !float.IsNaN(cli.Width);
                float childW = (align == FlexAlign.Stretch && !hasExplicitCross)
                    ? MathF.Max(0f, availCross - crossMargin)
                    : (!float.IsNaN(cli.Width) ? cli.Width : MathF.Max(0f, availCross - crossMargin));
                Measure(c, childW);
            }
        }

        // First pass: base main sizes + counts.
        int n = 0; float usedMain = 0f, totalGrow = 0f, totalShrinkScaled = 0f;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            ref LayoutInput cli = ref _scene.Layout(c);
            ref RectF cb = ref _scene.Bounds(c);
            float baseMain = !float.IsNaN(cli.FlexBasis) ? cli.FlexBasis : (row ? cb.W : cb.H);
            baseMain = ClampMain(cli, row, baseMain);
            usedMain += baseMain + MarginMain(cli, row);
            totalGrow += cli.FlexGrow;
            totalShrinkScaled += cli.FlexShrink * baseMain;
            n++;
        }
        if (n > 1) usedMain += li.Gap * (n - 1);
        float free = availMain - usedMain;

        // Distribute free space → each child's final main size.
        Span<float> finalMain = n <= 64 ? stackalloc float[n] : new float[n];
        {
            int i = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), i++)
            {
                ref LayoutInput cli = ref _scene.Layout(c);
                ref RectF cb = ref _scene.Bounds(c);
                float baseMain = !float.IsNaN(cli.FlexBasis) ? cli.FlexBasis : (row ? cb.W : cb.H);
                baseMain = ClampMain(cli, row, baseMain);
                float fm = baseMain;
                if (free > 0f && totalGrow > 0f) fm = baseMain + free * (cli.FlexGrow / totalGrow);
                else if (free < 0f && totalShrinkScaled > 0f) fm = baseMain + free * (cli.FlexShrink * baseMain / totalShrinkScaled);
                finalMain[i] = MathF.Max(0f, ClampMain(cli, row, fm));
            }
        }

        // Leftover after sizing → justify-content spacing.
        float consumed = 0f; for (int i = 0; i < n; i++) consumed += finalMain[i];
        { int i = 0; for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), i++) consumed += MarginMain(_scene.Layout(c), row); }
        if (n > 1) consumed += li.Gap * (n - 1);
        float leftover = MathF.Max(0f, availMain - consumed);
        (float lead, float between) = Distribute(li.Justify, leftover, n);

        // Place children.
        float cursor = padMainStart + lead;
        int idx = 0;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), idx++)
        {
            ref LayoutInput cli = ref _scene.Layout(c);
            ref RectF cb = ref _scene.Bounds(c);

            float fMain = finalMain[idx];
            if (row && fMain > 0f && !float.IsInfinity(fMain))
                Measure(c, fMain);

            FlexAlign align = cli.AlignSelf == FlexAlign.Auto ? li.AlignItems : cli.AlignSelf;
            float crossMargin = MarginCross(cli, row);
            float baseCross = row ? cb.H : cb.W;
            bool hasExplicitCross = !float.IsNaN(row ? cli.Height : cli.Width);
            float fCross = (align == FlexAlign.Stretch && !hasExplicitCross)
                ? ClampCross(cli, row, availCross - crossMargin)
                : baseCross;

            float crossFree = availCross - (fCross + crossMargin);
            float crossOff = align switch
            {
                FlexAlign.Center => crossFree / 2f,
                FlexAlign.End => crossFree,
                _ => 0f,   // Start / Stretch
            };

            float mainStart = cursor + MarginMainStart(cli, row);
            float crossStart = padCrossStart + crossOff + MarginCrossStart(cli, row);

            float cx = row ? mainStart : crossStart;
            float cy = row ? crossStart : mainStart;
            float cw = row ? fMain : fCross;
            float ch = row ? fCross : fMain;

            Arrange(c, cx, cy, cw, ch);
            cursor += MarginMain(cli, row) + fMain + li.Gap + between;
        }
    }

    // ── Scroll / virtual viewport (layout.md §6: layout-free scroll; content arranged at content-box origin) ──

    private Size2 MeasureViewport(NodeHandle node, in LayoutInput li, float availW)
    {
        // Default ScrollView is a hard viewport boundary: it should take the size assigned by parent flex/layout and
        // publish overflow to the scroll system, not make the page/nav/sidebar grow to its full content height.
        // Popup list presenters opt into ContentSized: auto-size to rows, then clamp by MaxHeight and scroll overflow.
        _scene.TryGetScroll(node, out var sc);
        var content = sc.ContentNode;
        bool horizontal = sc.Orientation == 1;
        float w = float.IsNaN(li.Width) ? float.NaN : li.Width;
        float h = float.IsNaN(li.Height) ? float.NaN : li.Height;

        if (!sc.ContentSized)
        {
            if (float.IsNaN(w)) w = 0f;
            if (float.IsNaN(h)) h = 0f;
            w = Clamp(w, li.MinW, li.MaxW);
            h = Clamp(h, li.MinH, li.MaxH);
            ref RectF vb = ref _scene.Bounds(node);
            vb = new RectF(vb.X, vb.Y, w, h);
            return new Size2(w, h);
        }

        if (content.IsNull || !_scene.IsLive(content))
        {
            if (float.IsNaN(w)) w = 0f;
            if (float.IsNaN(h)) h = 0f;
        }
        else if (float.IsNaN(w) || float.IsNaN(h))
        {
            float outerW = DefiniteWidth(in li, availW);
            float contentAvailW = horizontal
                ? float.PositiveInfinity
                : (!float.IsNaN(w) ? MathF.Max(0f, w - li.Padding.Horizontal)
                   : !float.IsInfinity(outerW) ? MathF.Max(0f, outerW - li.Padding.Horizontal)
                   : float.PositiveInfinity);
            var cs = Measure(content, contentAvailW);
            if (float.IsNaN(w)) w = cs.Width + li.Padding.Horizontal;
            if (float.IsNaN(h)) h = cs.Height + li.Padding.Vertical;
        }
        w = Clamp(w, li.MinW, li.MaxW);
        h = Clamp(h, li.MinH, li.MaxH);
        ref RectF b = ref _scene.Bounds(node);
        b = new RectF(b.X, b.Y, w, h);
        return new Size2(w, h);
    }

    private void ArrangeViewport(NodeHandle node, float finalW, float finalH, in LayoutInput li)
    {
        // Snapshot by value: arranging content may add nested-viewport rows to the scroll table and relocate refs.
        _scene.TryGetScroll(node, out var sc0);
        var content = sc0.ContentNode;
        bool horizontal = sc0.Orientation == 1;
        float innerW = finalW - li.Padding.Horizontal;
        float innerH = finalH - li.Padding.Vertical;
        float padL = li.Padding.Left, padT = li.Padding.Top;

        (float contentW, float contentH) =
              sc0.ItemCount > 0 && sc0.Layout is not null ? ArrangeVirtualLayout(in sc0, content, innerW, innerH, padL, padT, horizontal)
            : sc0.ItemCount > 0                           ? ArrangeVirtualVariable(node, in sc0, content, innerW, innerH, padL, padT, horizontal)
            :                                               ArrangePlainScroll(content, innerW, innerH, padL, padT, horizontal);

        // Publish ContentSize + viewport extent (Layout-owned fields) via a fresh ref (post-recursion).
        ref ScrollState sc = ref _scene.ScrollRef(node);
        sc.ContentW = contentW; sc.ContentH = contentH;
        sc.ViewportW = innerW; sc.ViewportH = innerH;

        // Re-clamp the scroll position to the (possibly changed) content on resize/relayout, and reflect it in the
        // content's -offset transform, so a smaller content / wrapped reflow doesn't leave the view scrolled past the end.
        float maxX = MathF.Max(0f, contentW - innerW), maxY = MathF.Max(0f, contentH - innerH);
        sc.OffsetX = Clamp(sc.OffsetX, 0f, maxX); sc.TargetX = Clamp(sc.TargetX, 0f, maxX);
        sc.OffsetY = Clamp(sc.OffsetY, 0f, maxY); sc.TargetY = Clamp(sc.TargetY, 0f, maxY);
        if (!content.IsNull && _scene.IsLive(content))
        {
            ref NodePaint cp = ref _scene.Paint(content);
            cp.LocalTransform = Affine2D.Translation(horizontal ? -sc.OffsetX : 0f, horizontal ? 0f : -sc.OffsetY);
        }
    }

    private (float w, float h) ArrangePlainScroll(NodeHandle content, float innerW, float innerH, float padL, float padT, bool horizontal)
    {
        if (content.IsNull) return (0f, 0f);
        // Fill the cross axis to the viewport during ARRANGE; do not write LayoutInput.Width/Height here. LayoutInput is
        // the reconciled model, not mutable layout scratch. Mutating it poisoned content-sized popup lists: the first
        // arrange wrote the 96px menu minimum into the column, so the next measure clipped long menu labels to 96px.
        var cs = Measure(content, horizontal ? float.PositiveInfinity : innerW);   // vertical scroll: wrap text to the viewport width
        float contentW = horizontal ? cs.Width : innerW;
        float contentH = horizontal ? innerH : cs.Height;
        Arrange(content, padL, padT, contentW, contentH);   // content-box origin; Input adds -ScrollOffset
        return (contentW, contentH);
    }

    // Pluggable fixed-geometry virtualization: the IVirtualLayout publishes ContentSize and places each realized item
    // by ItemRect (stack / grid / custom — all the same code path). Allocation-free (struct rects). virtualization.md §8.7.
    private (float w, float h) ArrangeVirtualLayout(in ScrollState sc, NodeHandle content, float innerW, float innerH, float padL, float padT, bool horizontal)
    {
        var layout = sc.Layout;
        if (content.IsNull || layout is null) return (0f, 0f);
        int first = sc.FirstRealized;
        float cross = horizontal ? innerH : innerW;
        float mainContent = layout.ContentExtent(sc.ItemCount, cross);
        float contentW = horizontal ? mainContent : innerW;
        float contentH = horizontal ? innerH : mainContent;
        _scene.Bounds(content) = new RectF(padL, padT, contentW, contentH);

        int ord = 0;
        for (var rc = _scene.FirstChild(content); !rc.IsNull; rc = _scene.NextSibling(rc), ord++)
        {
            var rect = layout.ItemRect(first + ord, cross);   // children are in window (index) order; content-space rect
            Measure(rc);                                       // base sizes for the cell's own internal flex
            Arrange(rc, rect.X, rect.Y, rect.W, rect.H);
        }
        return (contentW, contentH);
    }

    // Variable virtualization: rows positioned by the Fenwick extent table (OffsetOf), measured-then-corrected,
    // with scroll-anchoring so an above-viewport extent correction doesn't jump the visible top (virtualization.md §6.2).
    private (float w, float h) ArrangeVirtualVariable(NodeHandle node, in ScrollState sc, NodeHandle content,
                                                      float innerW, float innerH, float padL, float padT, bool horizontal)
    {
        if (content.IsNull || !_scene.TryGetExtents(node, out var table) || table is null) return (0f, 0f);
        int first = sc.FirstRealized;

        // Anchor: the topmost-visible item + its sub-item offset, captured BEFORE this frame's corrections.
        float offset = horizontal ? sc.OffsetX : sc.OffsetY;
        int anchorIndex = table.IndexAt(offset);
        float anchorWithin = offset - table.OffsetOf(anchorIndex);

        int ord = 0;
        for (var rc = _scene.FirstChild(content); !rc.IsNull; rc = _scene.NextSibling(rc), ord++)
        {
            int index = first + ord;
            var cs = Measure(rc);                                  // the row's natural main extent
            float pos = table.OffsetOf(index);                    // content-space position (corrections so far applied)
            float main = horizontal ? cs.Width : cs.Height;
            if (horizontal) Arrange(rc, pos, 0f, main, innerH);
            else            Arrange(rc, 0f, pos, innerW, main);
            table.SetExtent(index, main);                         // correct this row's extent (O(log n))
        }

        float mainContent = (float)table.Total;
        float contentW = horizontal ? mainContent : innerW;
        float contentH = horizontal ? innerH : mainContent;
        _scene.Bounds(content) = new RectF(padL, padT, contentW, contentH);

        // Re-pin the anchor so corrections to rows above the visible top do not shift the viewport.
        float pinned = table.OffsetOf(anchorIndex) + anchorWithin;
        float maxOff = MathF.Max(0f, mainContent - (horizontal ? innerW : innerH));
        pinned = Math.Clamp(pinned, 0f, maxOff);
        ref ScrollState scw = ref _scene.ScrollRef(node);
        if (horizontal) scw.OffsetX = pinned; else scw.OffsetY = pinned;
        scw.AnchorIndex = anchorIndex;
        ref NodePaint cp = ref _scene.Paint(content);
        cp.LocalTransform = Affine2D.Translation(horizontal ? -pinned : 0f, horizontal ? 0f : -pinned);
        return (contentW, contentH);
    }

    // ── Z-stack: children overlay at the origin (each filling the box unless explicitly sized), painted in order ──

    private Size2 MeasureZStack(NodeHandle node, in LayoutInput li, float availW)
    {
        float childAvail = DefiniteWidth(in li, availW);
        if (!float.IsInfinity(childAvail)) childAvail = MathF.Max(0f, childAvail - li.Padding.Horizontal);
        float maxW = 0f, maxH = 0f;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            var cs = Measure(c, childAvail);
            maxW = MathF.Max(maxW, cs.Width); maxH = MathF.Max(maxH, cs.Height);
        }
        float w = float.IsNaN(li.Width) ? maxW + li.Padding.Horizontal : li.Width;
        float h = float.IsNaN(li.Height) ? maxH + li.Padding.Vertical : li.Height;
        w = Clamp(w, li.MinW, li.MaxW); h = Clamp(h, li.MinH, li.MaxH);
        ref RectF b = ref _scene.Bounds(node);
        b = new RectF(b.X, b.Y, w, h);
        return new Size2(w, h);
    }

    private void ArrangeZStack(NodeHandle node, float finalW, float finalH, in LayoutInput li)
    {
        float innerW = finalW - li.Padding.Horizontal, innerH = finalH - li.Padding.Vertical;
        float padL = li.Padding.Left, padT = li.Padding.Top;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            ref LayoutInput cli = ref _scene.Layout(c);
            float mL = cli.Margin.Left, mT = cli.Margin.Top, mR = cli.Margin.Right, mB = cli.Margin.Bottom;
            float cw = float.IsNaN(cli.Width) ? MathF.Max(0f, innerW - mL - mR) : cli.Width;   // explicit child size, else fill the stack (minus margin)
            float ch = float.IsNaN(cli.Height) ? MathF.Max(0f, innerH - mT - mB) : cli.Height;
            // A ZStack has no main axis; honor AlignSelf as the child's VERTICAL placement (overlay VerticalAlignment)
            // and its leading Margin as the offset. Start/Stretch/Auto keep the legacy top-left origin (no regression).
            FlexAlign align = cli.AlignSelf == FlexAlign.Auto ? li.AlignItems : cli.AlignSelf;
            float freeV = MathF.Max(0f, innerH - ch - mT - mB);
            float oy = align == FlexAlign.Center ? freeV * 0.5f : align == FlexAlign.End ? freeV : 0f;
            Arrange(c, padL + mL, padT + mT + oy, cw, ch);   // overlay at the aligned origin (recorder paints in order)
        }
    }

    // ── CSS Grid — distinct true-tracks (Pixel/Star/Auto) + row-major auto-flow (layout.md §7) ──

    private Size2 MeasureGrid(NodeHandle node, in LayoutInput li, float availW)
    {
        _scene.TryGetGrid(node, out var g);
        float padH = li.Padding.Horizontal, padV = li.Padding.Vertical;
        // Border-box width: explicit, else the width the parent will stretch us to (availW). A CSS grid is block-level —
        // it fills the available inline size, and star tracks NEED that concrete width to divide. Without availW a
        // stretch-width grid measured to height 0, so the parent column stacked the next sibling over its overflow.
        float w = !float.IsNaN(li.Width) ? li.Width
                : float.IsInfinity(availW) ? 0f
                : MathF.Max(0f, availW);
        int count = GridColCount(in g, w - padH);   // auto-fill resolves the count from the (now known) width
        float h;
        if (w > 0f && count > 0)
        {
            Span<float> colW = count <= 64 ? stackalloc float[count] : new float[count];
            ResolveColumns(node, in g, count, w - padH, colW);
            h = GridContentHeight(node, in g, count) + padV;
        }
        else h = float.IsNaN(li.Height) ? 0f : li.Height;
        w = Clamp(w, li.MinW, li.MaxW);
        h = Clamp(h, li.MinH, li.MaxH);
        ref RectF b = ref _scene.Bounds(node);
        b = new RectF(b.X, b.Y, w, h);
        return new Size2(w, h);
    }

    private void ArrangeGrid(NodeHandle node, float finalW, float finalH, in LayoutInput li)
    {
        _scene.TryGetGrid(node, out var g);
        float padL = li.Padding.Left, padT = li.Padding.Top;
        float innerW = finalW - li.Padding.Horizontal;
        int count = GridColCount(in g, innerW);   // same width Measure saw → same count, so rows/height stay consistent
        if (count == 0 || _scene.FirstChild(node).IsNull) return;

        Span<float> colW = count <= 64 ? stackalloc float[count] : new float[count];
        Span<float> colX = count <= 64 ? stackalloc float[count] : new float[count];
        Span<NodeHandle> rowKids = count <= 64 ? stackalloc NodeHandle[count] : new NodeHandle[count];
        ResolveColumns(node, in g, count, innerW, colW);
        float cx = padL;
        for (int j = 0; j < count; j++) { colX[j] = cx; cx += colW[j] + g.ColGap; }

        bool autoRow = float.IsNaN(g.RowHeight);
        float rowTop = padT;
        var child = _scene.FirstChild(node);
        while (!child.IsNull)
        {
            int n = 0;
            var c = child;
            for (; n < count && !c.IsNull; n++, c = _scene.NextSibling(c)) rowKids[n] = c;

            float rowH = autoRow ? 0f : g.RowHeight;
            if (autoRow)
                for (int j = 0; j < n; j++) { var cs = Measure(rowKids[j]); rowH = MathF.Max(rowH, cs.Height); }
            for (int j = 0; j < n; j++)
            {
                if (!autoRow) Measure(rowKids[j]);   // base sizes for the cell's own flex
                Arrange(rowKids[j], colX[j], rowTop, colW[j], rowH);
            }
            rowTop += rowH + g.RowGap;
            child = c;
        }
    }

    // Effective column count. Fixed grids use their declared track list; an auto-fill grid (MinColWidth > 0) packs as
    // many equal 1fr columns as fit at >= MinColWidth, so the tracks always fill the width and the count reflows with it
    // (CSS repeat(auto-fill, minmax(MinColWidth, 1fr))). Width unknown (0 / ∞) ⇒ assume a single column.
    private static int GridColCount(in GridSpec g, float innerW)
    {
        if (g.MinColWidth > 0f)
            return innerW > 0f && !float.IsInfinity(innerW)
                ? Math.Max(1, (int)((innerW + g.ColGap) / (g.MinColWidth + g.ColGap)))
                : 1;
        return g.Columns?.Length ?? 0;
    }

    private void ResolveColumns(NodeHandle node, in GridSpec g, int count, float availW, Span<float> colW)
    {
        if (g.MinColWidth > 0f)   // auto-fill: 'count' equal (1fr) tracks share the width evenly → flush fill, no ragged edge
        {
            float starGaps = count > 1 ? (count - 1) * g.ColGap : 0f;
            float each = count > 0 ? MathF.Max(0f, (availW - starGaps) / count) : 0f;
            for (int j = 0; j < count; j++) colW[j] = each;
            return;
        }

        Span<float> autoW = count <= 64 ? stackalloc float[count] : new float[count];
        bool anyAuto = false;
        for (int j = 0; j < count; j++) if (g.Columns[j].Kind == TrackKind.Auto) { anyAuto = true; break; }
        if (anyAuto)
        {
            int k = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), k++)
            {
                int col = k % count;
                if (g.Columns[col].Kind == TrackKind.Auto) { var cs = Measure(c); autoW[col] = MathF.Max(autoW[col], cs.Width); }
            }
        }

        float fixedW = 0f, starTotal = 0f;
        for (int j = 0; j < count; j++)
        {
            var t = g.Columns[j];
            if (t.Kind == TrackKind.Pixel) fixedW += t.Value;
            else if (t.Kind == TrackKind.Auto) fixedW += autoW[j];
            else starTotal += MathF.Max(0f, t.Value);
        }
        float gaps = count > 1 ? (count - 1) * g.ColGap : 0f;
        float remaining = MathF.Max(0f, availW - fixedW - gaps);
        for (int j = 0; j < count; j++)
        {
            var t = g.Columns[j];
            colW[j] = t.Kind switch
            {
                TrackKind.Pixel => t.Value,
                TrackKind.Auto => autoW[j],
                _ => starTotal > 0f ? remaining * MathF.Max(0f, t.Value) / starTotal : 0f,
            };
        }
    }

    private float GridContentHeight(NodeHandle node, in GridSpec g, int count)
    {
        int childCount = _scene.ChildCount(node);
        if (childCount == 0) return 0f;
        int rows = (childCount + count - 1) / count;
        if (!float.IsNaN(g.RowHeight)) return rows * g.RowHeight + (rows - 1) * g.RowGap;

        float sumRowH = 0f, rowH = 0f; int k = 0;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), k++)
        {
            var cs = Measure(c);
            rowH = MathF.Max(rowH, cs.Height);
            if (k % count == count - 1) { sumRowH += rowH; rowH = 0f; }
        }
        if (k % count != 0) sumRowH += rowH;   // trailing partial row
        return sumRowH + (rows - 1) * g.RowGap;
    }

    // Wrap: main axis is finite (explicit size or parent-provided row width); children flow onto multiple lines.
    private static bool TryWrapMainLimit(in LayoutInput li, bool row, float availW, out float mainLimit)
    {
        float explicitMain = row ? li.Width : li.Height;
        if (!float.IsNaN(explicitMain))
        {
            mainLimit = MathF.Max(0f, explicitMain);
            return true;
        }

        if (row && !float.IsInfinity(availW))
        {
            mainLimit = MathF.Max(0f, availW);
            return true;
        }

        mainLimit = 0f;
        return false;
    }

    private (float w, float h) MeasureWrap(NodeHandle node, in LayoutInput li, bool row, float mainLimit)
    {
        float availMain = MathF.Max(0f, mainLimit - (row ? li.Padding.Horizontal : li.Padding.Vertical));
        float cursor = 0f, lineCross = 0f, totalCross = 0f;
        bool first = true, any = false;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            var cs = Measure(c);
            ref LayoutInput cli = ref _scene.Layout(c);
            float oMain = (row ? cs.Width : cs.Height) + MarginMain(cli, row);
            float oCross = (row ? cs.Height : cs.Width) + MarginCross(cli, row);
            if (!first && cursor + li.Gap + oMain > availMain + 0.01f)
            {
                totalCross += lineCross + li.Gap;   // close the line + a cross-axis gap
                cursor = oMain; lineCross = oCross;
            }
            else
            {
                cursor += first ? oMain : li.Gap + oMain;
                lineCross = MathF.Max(lineCross, oCross);
            }
            first = false; any = true;
        }
        if (any) totalCross += lineCross;
        float crossSize = totalCross + (row ? li.Padding.Vertical : li.Padding.Horizontal);
        float mainSize = mainLimit;
        return row ? (mainSize, crossSize) : (crossSize, mainSize);
    }

    private void ArrangeWrap(NodeHandle node, float finalW, float finalH, in LayoutInput li, bool row)
    {
        float padMainStart = row ? li.Padding.Left : li.Padding.Top;
        float padCrossStart = row ? li.Padding.Top : li.Padding.Left;
        float availMain = (row ? finalW : finalH) - (row ? li.Padding.Horizontal : li.Padding.Vertical);

        float cursor = padMainStart, lineTop = padCrossStart, lineCross = 0f;
        bool first = true;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            ref LayoutInput cli = ref _scene.Layout(c);
            ref RectF cb = ref _scene.Bounds(c);
            float baseMain = row ? cb.W : cb.H, baseCross = row ? cb.H : cb.W;
            float oMain = baseMain + MarginMain(cli, row), oCross = baseCross + MarginCross(cli, row);

            if (!first && (cursor - padMainStart) + li.Gap + oMain > availMain + 0.01f)
            {
                lineTop += lineCross + li.Gap;   // wrap to the next line
                cursor = padMainStart; lineCross = 0f; first = true;
            }
            if (!first) cursor += li.Gap;

            float childMainPos = cursor + MarginMainStart(cli, row);
            float childCrossPos = lineTop + MarginCrossStart(cli, row);
            float cx = row ? childMainPos : childCrossPos;
            float cy = row ? childCrossPos : childMainPos;
            Arrange(c, cx, cy, row ? baseMain : baseCross, row ? baseCross : baseMain);

            cursor += oMain;
            lineCross = MathF.Max(lineCross, oCross);
            first = false;
        }
    }

    private static (float lead, float between) Distribute(FlexJustify j, float leftover, int n) => j switch
    {
        FlexJustify.Center => (leftover / 2f, 0f),
        FlexJustify.End => (leftover, 0f),
        FlexJustify.SpaceBetween => (0f, n > 1 ? leftover / (n - 1) : 0f),
        FlexJustify.SpaceAround => (n > 0 ? leftover / n / 2f : 0f, n > 0 ? leftover / n : 0f),
        FlexJustify.SpaceEvenly => (leftover / (n + 1), leftover / (n + 1)),
        _ => (0f, 0f),   // Start
    };

    private static float MarginMain(in LayoutInput li, bool row) => row ? li.Margin.Horizontal : li.Margin.Vertical;
    private static float MarginCross(in LayoutInput li, bool row) => row ? li.Margin.Vertical : li.Margin.Horizontal;
    private static float MarginMainStart(in LayoutInput li, bool row) => row ? li.Margin.Left : li.Margin.Top;
    private static float MarginCrossStart(in LayoutInput li, bool row) => row ? li.Margin.Top : li.Margin.Left;
    private static float ClampMain(in LayoutInput li, bool row, float v) => row ? Clamp(v, li.MinW, li.MaxW) : Clamp(v, li.MinH, li.MaxH);
    private static float ClampCross(in LayoutInput li, bool row, float v) => row ? Clamp(v, li.MinH, li.MaxH) : Clamp(v, li.MinW, li.MaxW);
}
