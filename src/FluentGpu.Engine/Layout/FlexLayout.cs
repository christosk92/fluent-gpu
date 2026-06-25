using FluentGpu.Animation;
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

    // FG_LAYOUT_DIAG=1: per-Run layout-cost diagnostic — Measure/Arrange node-visit counts + text-shape hit/miss. A
    // regression guard for the measure-call explosion this memo cures: a healthy pass keeps measure≈O(nodes); a runaway
    // measure≫arrange flags a redundant-measure blow-up. Gated to a single bool check (zero work/alloc) when off.
    private static readonly bool s_layoutDiag = Diag.EnvFlag("FG_LAYOUT_DIAG");
    private int _dMeasure, _dTextHit, _dTextMiss, _dArrange;

    // Within-pass Measure memo. Measure(node, availW) is a PURE function of the node's subtree content + availW within
    // ONE layout solve (it has no external mutable input; its only side effect is writing the node's Bounds W/H). But the
    // flex algorithm calls it redundantly — a row's fixed-size pre-pass AND the main loop each measure a non-grow child
    // at the SAME width, and that doubling COMPOUNDS with depth (~45x per node measured here on the Wavee tree: 10k calls
    // for 227 nodes). Memoizing per (node, availW) for the current pass collapses the explosion — the box-level twin of
    // the per-leaf text measure cache. A hit re-asserts the Bounds W/H so Arrange (which reads Bounds for base sizes) is
    // byte-identical to the unmemoized result. Reset by bumping the generation at each top-level solve (cross-pass tree
    // mutations are thereby never reused; within a pass the tree is immutable, so the function stays pure).
    private struct MeasureMemo { public uint Gen; public float AvailW; public float W, H; }
    private MeasureMemo[] _memo = System.Array.Empty<MeasureMemo>();
    private uint _measureGen;

    private void BeginMeasurePass()
    {
        _measureGen++;
        int cap = _scene.Capacity;
        if (_memo.Length < cap) _memo = new MeasureMemo[cap];
    }

    private Size2 StoreMemo(NodeHandle node, float availW, Size2 size)
    {
        uint i = node.Raw.Index;
        if (i < (uint)_memo.Length) _memo[i] = new MeasureMemo { Gen = _measureGen, AvailW = availW, W = size.Width, H = size.Height };
        return size;
    }

    public FlexLayout(SceneStore scene, IFontSystem fonts)
    {
        _scene = scene;
        _fonts = fonts;
    }

    public void Run(NodeHandle root)
    {
        if (root.IsNull) return;
        BeginMeasurePass();
        var size = Measure(root);
        Arrange(root, 0f, 0f, size.Width, size.Height);
    }

    /// <summary>Lay out the root to FILL the window (the conventional top-level behavior) — an auto-sized root takes
    /// the full client area; an explicitly-sized root keeps its size. The slice's direct <see cref="Run(NodeHandle)"/>
    /// keeps content-sizing for golden flexbox checks.</summary>
    public void Run(NodeHandle root, Size2 window)
    {
        if (root.IsNull) return;
        if (s_layoutDiag) { _dMeasure = _dTextHit = _dTextMiss = _dArrange = 0; }
        BeginMeasurePass();
        Measure(root, window.Width);
        ref LayoutInput li = ref _scene.Layout(root);
        float w = float.IsNaN(li.Width) ? window.Width : li.Width;
        float h = float.IsNaN(li.Height) ? window.Height : li.Height;
        Arrange(root, 0f, 0f, w, h);
        if (s_layoutDiag) Console.Error.WriteLine($"[FG_LAYOUT_DIAG] measure={_dMeasure} arrange={_dArrange} textHit={_dTextHit} textMiss={_dTextMiss}");
    }

    /// <summary>Re-solve ONLY the subtree rooted at <paramref name="node"/> against its current Bounds (or its
    /// LayoutInput size if set — a SizeMode.Relayout animation writes the interpolated width there each tick). The parent
    /// already placed this node, so this cannot propagate upward — a scoped, per-frame-affordable relayout for live reflow.</summary>
    public void RunSubtree(NodeHandle node)
    {
        if (node.IsNull) return;
        BeginMeasurePass();
        ref LayoutInput li = ref _scene.Layout(node);
        ref RectF b = ref _scene.Bounds(node);
        float w = float.IsNaN(li.Width) ? b.W : li.Width;
        float h = float.IsNaN(li.Height) ? b.H : li.Height;
        // A scoped relayout roots HERE (this node is a layout boundary — IsolateLayout / scroll viewport / fixed-size),
        // so no ANCESTOR pass re-sizes this node's OUTER box. On a PARENT-DETERMINED axis (no explicit size — the node
        // grows/stretches/fills) the stored Bounds can be STALE and too LARGE: a sibling that reserved space after this
        // node was last arranged (a docked player bar shrinking the content region) shrinks the parent, but the firewall
        // keeps re-solving this subtree against the stale box — so the node bleeds past its parent (content paints under
        // the translucent player bar). Re-clamp each parent-determined axis to the parent's current inner content box
        // (minus this node's margin): a parent-determined boundary can never legitimately exceed its parent. Explicit
        // li.Width/Height (incl. a SizeMode.Relayout animation writing the interpolated size) are honoured untouched.
        var parent = _scene.Parent(node);
        if (!parent.IsNull)
        {
            ref RectF pb = ref _scene.Bounds(parent);
            ref LayoutInput pli = ref _scene.Layout(parent);
            if (float.IsNaN(li.Width))  w = MathF.Min(w, MathF.Max(0f, pb.W - pli.Padding.Horizontal - li.Margin.Horizontal));
            if (float.IsNaN(li.Height)) h = MathF.Min(h, MathF.Max(0f, pb.H - pli.Padding.Vertical   - li.Margin.Vertical));
        }
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

    private void SetArrangedBounds(NodeHandle node, in RectF next)
    {
        ref RectF b = ref _scene.Bounds(node);
        b = next;
        var handler = _scene.GetBoundsChangedHandler(node);
        if (handler is null) return;
        // Edge-trigger against the LAST DELIVERED arranged rect — NOT the live Bounds. Measure pre-writes Bounds to each
        // node's hypothetical size earlier in this pass, so for an unconstrained node (arranged == measured, e.g. the
        // marquee's Shrink=0 text box) a Bounds-vs-next compare is always false and the handler would fire only once via
        // the mount one-shot, never again on a real content-driven size change. Comparing against the delivered baseline
        // also stops a constrained node (arranged != measured) from firing spuriously every pass. Fire on a real change,
        // OR once when a freshly-installed handler is still pending its initial delivery; then advance the baseline.
        ref RectF delivered = ref _scene.BoundsDeliveredRef(node);
        bool pending = (_scene.Flags(node) & NodeFlags.BoundsChangedPending) != 0;
        bool changed = delivered.X != next.X || delivered.Y != next.Y || delivered.W != next.W || delivered.H != next.H;
        if (changed || pending)
        {
            if (pending) _scene.Unmark(node, NodeFlags.BoundsChangedPending);
            delivered = next;
            handler.Invoke(next);
        }
    }

    // ── Measure: fill Bounds.W/H with each node's base (hypothetical) border-box size ──
    private Size2 Measure(NodeHandle node, float availW = float.PositiveInfinity)
    {
        if (s_layoutDiag) _dMeasure++;
        // Within-pass memo: same (node, availW) already solved this pass ⇒ reuse it, re-asserting the Bounds W/H so the
        // Arrange pass (which reads Bounds for base main/cross sizes) sees exactly what an unmemoized recompute would.
        uint mi = node.Raw.Index;
        if (mi < (uint)_memo.Length)
        {
            ref MeasureMemo hit = ref _memo[mi];
            if (hit.Gen == _measureGen && hit.AvailW == availW)
            {
                ref RectF hb = ref _scene.Bounds(node);
                hb = new RectF(hb.X, hb.Y, hit.W, hit.H);
                return new Size2(hit.W, hit.H);
            }
        }
        ref LayoutInput li = ref _scene.Layout(node);
        ref NodePaint paint = ref _scene.Paint(node);

        // A scroll/virtual viewport is a layout boundary: its size is its own box (explicit/flex), independent of
        // content — content overflow is what scrolls. (layout.md §4.3/§6.)
        // NOTE: the viewport/grid/zstack measure paths are NOT memoized (only the pure general flex path below is) — they
        // recompute every call. Their SUBTREES still benefit (the general-path nodes inside them memoize). Conservative:
        // the flex-row pre-pass/main-loop redundancy that compounds is entirely in the general path.
        if (_scene.HasScroll(node)) return MeasureViewport(node, in li, availW);
        if (_scene.HasGrid(node)) return MeasureGrid(node, in li, availW);
        if ((_scene.Flags(node) & NodeFlags.ZStack) != 0) return MeasureZStack(node, in li, availW);

        float w, h;
        if (paint.VisualKind == VisualKind.Text)
        {
            float measureW = DefiniteWidth(in li, availW);
            // MaxLines/Trim need a finite wrap width even when Wrap=NoWrap — otherwise a long title measures at its
            // natural width, the grid cell's cross-size inflates, and glyphs bleed into the next column.
            bool widthConstrained = !float.IsInfinity(measureW);
            bool needsColumnBudget = li.TextStyle.Wrap != Foundation.TextWrap.NoWrap
                || li.TextStyle.Trim != Foundation.TextTrim.None;
            float maxW = widthConstrained && needsColumnBudget
                ? MathF.Max(0f, measureW)
                : float.PositiveInfinity;
            // Measure cache: skip re-shaping when (text, style, availWidth) are unchanged (the §2.3 down-rule win on a
            // scoped relayout). Pure-function key ⇒ self-invalidating; helps the real shaping path, neutral headless.
            ref TextMeasureCache mc = ref _scene.MeasureCacheRef(node);
            if (mc.Valid && mc.Text == paint.Text && mc.MaxW == maxW && mc.Style == li.TextStyle)
            {
                if (s_layoutDiag) _dTextHit++;
                w = mc.Size.Width; h = mc.Size.Height;
            }
            else
            {
                if (s_layoutDiag) _dTextMiss++;
                // Auto-fit (TextEl.MinSize / TextStyle.MinSizeDip): shrink the font so the run fits MaxLines at maxW.
                // Opt-in (MinSizeDip>0), so normal text skips this entirely. The chosen size feeds BOTH the measured box
                // and the recorder (stored as FitSize); 0 ⇒ no shrink (the recorder shapes at the authored SizeDip).
                float fit = 0f;
                TextStyle eff = li.TextStyle;
                if (li.TextStyle.MinSizeDip > 0f && li.TextStyle.MinSizeDip < li.TextStyle.SizeDip
                    && li.TextStyle.MaxLines > 0 && li.TextStyle.Wrap != Foundation.TextWrap.NoWrap && !float.IsInfinity(maxW))
                {
                    float chosen = FitTextSize(paint.Text, li.TextStyle, maxW);
                    if (chosen < li.TextStyle.SizeDip) { fit = chosen; eff = li.TextStyle with { SizeDip = chosen }; }
                }
                var m = _fonts.Measure(paint.Text, eff, maxW);
                w = m.Size.Width; h = m.Size.Height;
                // Retain the face's decoration metrics alongside the size: the recorder places underline/strikethrough
                // bars (NodePaint.TextDecorations) from this row at record time without re-touching the font seam.
                mc = new TextMeasureCache
                {
                    Valid = true, Text = paint.Text, Style = li.TextStyle, MaxW = maxW, Size = new Size2(w, h),
                    FitSize = fit,
                    UnderlineY = m.UnderlineY, UnderlineThickness = m.UnderlineThickness, StrikeY = m.StrikeY,
                };
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
                    // In an INDEFINITE column, a growable Basis=0 child must still contribute its content height while
                    // the parent determines its own height; otherwise the following sibling is stacked over that content.
                    // A row with a finite width is different: Basis=0 is the standard "flex: 1 1 0" contract and MUST
                    // suppress intrinsic width. Controls such as AutoSuggestBox depend on that to shrink before fixed
                    // toolbar siblings. Applying the column min-size rule to finite rows makes their stale/intrinsic
                    // content width become the flex base and lets it paint across later siblings.
                    if (!float.IsNaN(cli.FlexBasis))
                    {
                        bool indefiniteMain = row
                            ? float.IsInfinity(measureW)
                            : float.IsNaN(li.Height);
                        cMain = cli.FlexGrow > 0f && indefiniteMain
                            ? MathF.Max(cli.FlexBasis, cMain)
                            : cli.FlexBasis;
                    }
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

        // Aspect-ratio (CSS aspect-ratio): derive the missing extent for a fluid leaf. Explicit Width+Height both set
        // wins (aspect ignored). When both are fluid, take the offered width constraint as the box width and derive the
        // height — the parent's cross-stretch then arranges that same width, and this measured height rides along as the
        // main size (re-measured against the final cross size in Arrange, so measure↔arrange stay square).
        float ar = li.AspectRatio;
        if (!float.IsNaN(ar) && ar > 0f)
        {
            bool defW = !float.IsNaN(li.Width), defH = !float.IsNaN(li.Height);
            if (defW && !defH) h = w / ar;
            else if (defH && !defW) w = h * ar;
            else if (!defW && !defH && !float.IsInfinity(availW))
            {
                w = MathF.Max(0f, availW - li.Padding.Horizontal);
                h = w / ar;
            }
        }

        w = Clamp(w, li.MinW, li.MaxW);
        h = Clamp(h, li.MinH, li.MaxH);

        ref RectF b = ref _scene.Bounds(node);
        b = new RectF(b.X, b.Y, w, h);
        return StoreMemo(node, availW, new Size2(w, h));
    }

    // ── Arrange: position + size children within the node's final box ──
    private void Arrange(NodeHandle node, float x, float y, float finalW, float finalH)
    {
        if (s_layoutDiag) _dArrange++;
        SetArrangedBounds(node, new RectF(x, y, finalW, finalH));

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
            // A vertical viewport with a FINITE offered width ADOPTS it (CSS overflow-y: the content is width-
            // constrained and only the scroll axis overflows). Without this a wide child (e.g. a horizontal card
            // strip / Home shelf) hugged the content's natural width PAST the viewport, so the page could never shrink
            // below it on resize (it was measured at +Inf in the cross-hug below). Horizontal viewports and genuinely
            // unconstrained contexts (availW = +Inf) still take the natural-width hug below. (layout.md §6.)
            if (!horizontal && float.IsNaN(w) && !float.IsInfinity(availW))
                w = MathF.Max(0f, availW);

            // D1 — natural-size fallback for NON-FLEXING virtual viewports the parent does not size. WinUI's
            // ItemsView template is a ScrollView over an ItemsRepeater (ItemsView.xaml:19-37, VerticalAlignment=Top):
            // measured unconstrained it reports the repeater's natural extent — it does not collapse to 0 (the
            // gallery ListView/ItemsView empty-panel regression). Cross axis: an auto-width vertical list fills the
            // available width (block-level, the MeasureGrid rule); main axis: the layout's ContentExtent. Gated on
            // FlexGrow == 0 so a Grow viewport (every Virtual.* factory, app fill-lists) keeps its 0 base — a
            // 10k-row list must never inject a ~440000px flex basis; grow/stretch size it at arrange and
            // realize-after-layout (ArrangeViewport tail) re-windows against the published viewport.
            if (sc.ItemCount > 0 && li.FlexGrow == 0f)
            {
                if (!horizontal && float.IsNaN(w) && !float.IsInfinity(availW))
                    w = MathF.Max(0f, availW);
                if (horizontal ? float.IsNaN(w) : float.IsNaN(h))
                {
                    float cross = horizontal
                        ? (float.IsNaN(h) ? 0f : MathF.Max(0f, h - li.Padding.Vertical))
                        : (float.IsNaN(w) ? 0f : MathF.Max(0f, w - li.Padding.Horizontal));
                    // Viewport-aware layout: seed a best-known main estimate (the offered width for a horizontal shelf)
                    // so ContentExtent is reasonable this frame; arrange + realize-after-layout correct it.
                    if (sc.Layout is IViewportVirtualLayout dvl)
                        dvl.SetViewport(horizontal ? (float.IsInfinity(availW) ? 0f : MathF.Max(0f, availW)) : 0f, cross);
                    float main = sc.Layout is not null ? sc.Layout.ContentExtent(sc.ItemCount, cross)
                               : _scene.TryGetExtents(node, out var extents) && extents is not null ? (float)extents.Total
                               : 0f;
                    if (horizontal) w = main + li.Padding.Horizontal;
                    else h = main + li.Padding.Vertical;
                }
            }
            // Cross-axis hug (non-virtual content) — a viewport clips/scrolls ONLY its scroll axis; its cross axis has
            // no overflow, so size it to the content when the parent leaves it indefinite. Else a horizontal strip in an
            // auto-height column (the cross axis is the column's MAIN axis, which neither cross-stretch nor Grow fills)
            // collapses to 0. The scroll axis is never touched here — it stays parent-assigned (never self-grows).
            if (sc.ItemCount == 0 && !content.IsNull && _scene.IsLive(content)
                && (horizontal ? float.IsNaN(h) : float.IsNaN(w)))
            {
                float crossAvailW = horizontal ? float.PositiveInfinity
                                  : (!float.IsNaN(w) ? MathF.Max(0f, w - li.Padding.Horizontal) : float.PositiveInfinity);
                var cs = Measure(content, crossAvailW);
                if (horizontal) h = cs.Height + li.Padding.Vertical;
                else w = cs.Width + li.Padding.Horizontal;
            }
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
              sc0.ItemCount > 0 && sc0.Layout is IMeasuredVirtualLayout ml && UsesMeasuredExtent(sc0.Layout)
                ? ArrangeVirtualMeasured(node, ml, in sc0, content, innerW, innerH, padL, padT, horizontal)
            : sc0.ItemCount > 0 && sc0.Layout is not null ? ArrangeVirtualLayout(in sc0, content, innerW, innerH, padL, padT, horizontal)
            : sc0.ItemCount > 0                           ? ArrangeVirtualVariable(node, in sc0, content, innerW, innerH, padL, padT, horizontal)
            :                                               ArrangePlainScroll(content, innerW, innerH, padL, padT, horizontal);

        // Publish ContentSize + viewport extent (Layout-owned fields) via a fresh ref (post-recursion).
        ref ScrollState sc = ref _scene.ScrollRef(node);
        sc.ContentW = contentW; sc.ContentH = contentH;
        sc.ViewportW = innerW; sc.ViewportH = innerH;

        // Re-clamp the scroll position to the (possibly changed) content on resize/relayout, and reflect it in the
        // content's -offset transform, so a smaller content / wrapped reflow doesn't leave the view scrolled past the end.
        float maxX = MathF.Max(0f, contentW - innerW), maxY = MathF.Max(0f, contentH - innerH);
        // Scroll-position restoration latch: a revisit seeded RestoreX/Y (Reconciler.ApplyScrollKey). Re-assert it on EACH
        // layout until the real content extent can hold it — the loading skeleton is short, so the saved deep offset can't
        // apply until the taller real content lands; until then sit at the top rather than a clamped-mid skeleton position.
        // Because the seed is in place before the FIRST realize/layout, the first PRESENTED frame is already at the saved
        // row (no scroll-to-top flash). Released on satisfaction (here) or by a user scroll (InputDispatcher.SetScrollOffset).
        if (sc.RestorePending)
        {
            bool okX = maxX + 0.5f >= sc.RestoreX, okY = maxY + 0.5f >= sc.RestoreY;
            sc.OffsetX = sc.TargetX = okX ? sc.RestoreX : 0f;
            sc.OffsetY = sc.TargetY = okY ? sc.RestoreY : 0f;
            if (okX && okY) sc.RestorePending = false;
        }
        sc.OffsetX = Clamp(sc.OffsetX, 0f, maxX); sc.TargetX = Clamp(sc.TargetX, 0f, maxX);
        sc.OffsetY = Clamp(sc.OffsetY, 0f, maxY); sc.TargetY = Clamp(sc.TargetY, 0f, maxY);
        if (!content.IsNull && _scene.IsLive(content))
        {
            ref NodePaint cp = ref _scene.Paint(content);
            float off = horizontal ? sc.OffsetX : sc.OffsetY;
            float maxOff = horizontal ? MathF.Max(0f, sc.ContentW - innerW) : MathF.Max(0f, sc.ContentH - innerH);
            float band = OverscrollPhysics.GuardBandSign(sc.OverscrollPx, off, maxOff);
            if (sc.Overscrolling && band != sc.OverscrollPx) sc.OverscrollPx = band;
            OverscrollPhysics.WriteContentTransform(ref cp, in _scene.Bounds(content), horizontal, off, band,
                sc.ZoomFactor);
        }
        // (Re)bake geometry-dependent ranges (Content*/Bounds now known), then apply the generic scroll-driven bindings
        // in the SAME ArrangeViewport invocation — a resize frame must not paint a one-frame-stale bound transform.
        if (!content.IsNull && _scene.IsLive(content))
        {
            ScrollBindEval.BakeGeometry(_scene, node, in sc);
            ScrollBindEval.ApplyContinuous(_scene, node, ref sc);
        }

        // D1 realize-after-layout: the realize window was computed BEFORE this arrange published the real viewport
        // size (a mount realizes against the Height hint; a relayout can also grow the host). If the realized window
        // no longer covers the now-known viewport, flag the node — the host (AppHost.Paint) re-realizes + re-runs
        // scoped layout inside the SAME frame (bounded), so the first presented frame shows the real rows. Same
        // windowing idiom as the scroll paths (ScrollAnimator.Tick / InputDispatcher).
        if (sc.ItemCount > 0)
        {
            float vpExtent = horizontal ? sc.ViewportW : sc.ViewportH;
            float off = horizontal ? sc.OffsetX : sc.OffsetY;
            int visibleFirst, visibleLast;
            if (sc.Layout is not null)
            {
                float cross = horizontal ? sc.ViewportH : sc.ViewportW;
                sc.Layout.Window(sc.ItemCount, cross, vpExtent, off, 0, out visibleFirst, out visibleLast);
            }
            else if (_scene.TryGetExtents(node, out var extents) && extents is not null)
            {
                visibleFirst = extents.IndexAt(off);
                visibleLast = Math.Min(sc.ItemCount, extents.IndexAt(off + vpExtent) + 1);
            }
            else visibleFirst = visibleLast = 0;
            if (VirtualWindowing.NeedsRealize(in sc, visibleFirst, visibleLast))
                _scene.Mark(node, NodeFlags.VirtualRangeDirty);
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
        // Viewport-aware layouts (fill-the-width shelves) need the scroll-axis viewport before any geometry call.
        if (layout is IViewportVirtualLayout vl) vl.SetViewport(horizontal ? innerW : innerH, cross);
        float mainContent = layout.ContentExtent(sc.ItemCount, cross);
        float contentW = horizontal ? mainContent : innerW;
        float contentH = horizontal ? innerH : mainContent;
        _scene.Bounds(content) = new RectF(padL, padT, contentW, contentH);

        int ord = 0;
        for (var rc = _scene.FirstChild(content); !rc.IsNull; rc = _scene.NextSibling(rc), ord++)
        {
            var rect = layout.ItemRect(first + ord, cross);   // children are in window (index) order; content-space rect
            // Inset the item by its Margin within the slot, so a list item honors Margin like any stack child (the WinUI
            // ListViewItem backplate inset {4,2,4,2}). Without this the item filled the full slot — a margined row (an
            // inset highlight pill / backplate) then started its content at padding-only, drifting out of alignment with
            // a fixed header outside the list. The slot stride (ItemRect/extent) is unchanged; only the item insets.
            ref LayoutInput rli = ref _scene.Layout(rc);
            float mL = rli.Margin.Left, mT = rli.Margin.Top, mR = rli.Margin.Right, mB = rli.Margin.Bottom;
            // Measure at the slot's content WIDTH (its column), not unbounded — so a grid cell's text truncates/wraps to its
            // own column instead of measuring at its full natural width and bleeding into neighbours. A stack slot's
            // rect.W == the full cross width, so list rows are unaffected (they already filled it).
            Measure(rc, MathF.Max(0f, rect.W - mL - mR));
            Arrange(rc, rect.X + mL, rect.Y + mT, MathF.Max(0f, rect.W - mL - mR), MathF.Max(0f, rect.H - mT - mB));
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

    /// <summary>True when the layout participates in estimate-then-correct (not a fixed-geometry grid posing as measured).</summary>
    private static bool UsesMeasuredExtent(IVirtualLayout layout)
        => layout is not GridVirtualLayout gv || gv.IsMeasured;

    // Measured-seam virtualization (E11-L0): the same estimate-then-correct + scroll-anchoring contract as the
    // built-in Fenwick path (ArrangeVirtualVariable), but the extents/prefix sums live behind the user-implementable
    // IMeasuredVirtualLayout — custom layouts can be variable/sliver-like. virtualization.md §6.2 semantics.
    private (float w, float h) ArrangeVirtualMeasured(NodeHandle node, IMeasuredVirtualLayout layout, in ScrollState sc,
                                                      NodeHandle content, float innerW, float innerH, float padL, float padT, bool horizontal)
    {
        if (content.IsNull) return (0f, 0f);
        int first = sc.FirstRealized;
        float cross = horizontal ? innerH : innerW;

        // Anchor: the topmost-visible item + its sub-item offset, captured BEFORE this frame's corrections.
        float offset = horizontal ? sc.OffsetX : sc.OffsetY;
        int anchorIndex = layout.IndexAt(offset, cross);
        float anchorWithin = offset - layout.OffsetOf(anchorIndex, cross);

        if (layout is GridVirtualLayout { IsMeasured: true } grid)
            grid.ResetMeasurePass(sc.ItemCount, cross);

        // Pass 1 — measure every realized cell and feed extents (measured grids need the full row before heights lock).
        int ord = 0;
        for (var rc = _scene.FirstChild(content); !rc.IsNull; rc = _scene.NextSibling(rc), ord++)
        {
            int index = first + ord;
            var rect = layout.ItemRect(index, cross);
            ref LayoutInput rli = ref _scene.Layout(rc);
            float mL = rli.Margin.Left, mT = rli.Margin.Top, mR = rli.Margin.Right, mB = rli.Margin.Bottom;
            float measureW = horizontal ? MathF.Max(0f, rect.H - mT - mB) : MathF.Max(0f, rect.W - mL - mR);
            var cs = Measure(rc, measureW);
            float main = horizontal ? cs.Width : cs.Height;
            layout.SetMeasured(index, main, cross);
        }

        // Pass 2 — arrange at the corrected slots (row-synced for grids).
        ord = 0;
        for (var rc = _scene.FirstChild(content); !rc.IsNull; rc = _scene.NextSibling(rc), ord++)
        {
            int index = first + ord;
            var rect = layout.ItemRect(index, cross);
            ref LayoutInput rli = ref _scene.Layout(rc);
            float mL = rli.Margin.Left, mT = rli.Margin.Top, mR = rli.Margin.Right, mB = rli.Margin.Bottom;
            Arrange(rc, rect.X + mL, rect.Y + mT,
                MathF.Max(0f, rect.W - mL - mR), MathF.Max(0f, rect.H - mT - mB));
        }

        float mainContent = layout.ContentExtent(sc.ItemCount, cross);
        float contentW = horizontal ? mainContent : innerW;
        float contentH = horizontal ? innerH : mainContent;
        _scene.Bounds(content) = new RectF(padL, padT, contentW, contentH);

        // Re-pin the anchor so corrections to rows above the visible top do not shift the viewport.
        float pinned = layout.OffsetOf(anchorIndex, cross) + anchorWithin;
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

    // Largest font size in [MinSizeDip, SizeDip] whose run wraps to ≤ MaxLines at maxW (TextEl auto-fit). "Fits" is
    // monotonic in size (a bigger size never wraps fewer lines), so a short binary search converges. Each probe does
    // two cheap Measure calls (a single-line height reference + the wrapped box); this runs ONLY on a cache miss of an
    // opt-in (MinSizeDip>0) text node — never normal text, never a steady frame — so the extra measures are immaterial.
    private float FitTextSize(StringId text, TextStyle style, float maxW)
    {
        int maxLines = style.MaxLines;
        float maxS = style.SizeDip, minS = style.MinSizeDip;

        bool Fits(float s)
        {
            var probe = style with { SizeDip = s };
            float oneLine = _fonts.Measure(text, probe with { Wrap = Foundation.TextWrap.NoWrap, MaxLines = 0, Trim = Foundation.TextTrim.None }, float.PositiveInfinity).Size.Height;
            if (oneLine <= 0f) return true;
            float boxH = _fonts.Measure(text, probe with { MaxLines = 0, Trim = Foundation.TextTrim.None }, maxW).Size.Height;
            int lines = Math.Max(1, (int)MathF.Round(boxH / oneLine));
            return lines <= maxLines;
        }

        if (Fits(maxS)) return maxS;
        if (!Fits(minS)) return minS;
        float lo = minS, hi = maxS;
        for (int i = 0; i < 6; i++) { float mid = (lo + hi) * 0.5f; if (Fits(mid)) lo = mid; else hi = mid; }
        return lo;
    }

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
            h = GridContentHeight(node, in g, count, colW) + padV;
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
                for (int j = 0; j < n; j++) { var cs = Measure(rowKids[j], colW[j]); rowH = MathF.Max(rowH, cs.Height); }
            for (int j = 0; j < n; j++)
            {
                if (!autoRow) Measure(rowKids[j], colW[j]);   // base sizes for the cell's own flex, at the cell's width so text wraps to the track
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
        // Overflow guard (layout.md §7): when the FIXED (Px/Auto) tracks + gaps cannot fit a FINITE width, scale the
        // fixed tracks down proportionally so the row fits EXACTLY instead of spilling past the edge with overlapping
        // cells (Star tracks already resolve to 0). Grids don't scroll, so an over-wide fixed grid is a layout error we
        // degrade gracefully. No-op when it already fits (scale 1) or the width is unconstrained (the measure pass).
        float fixedScale = 1f;
        if (!float.IsInfinity(availW) && fixedW > 0f && fixedW + gaps > availW)
            fixedScale = Math.Clamp((availW - gaps) / fixedW, 0f, 1f);
        float remaining = MathF.Max(0f, availW - fixedW * fixedScale - gaps);
        for (int j = 0; j < count; j++)
        {
            var t = g.Columns[j];
            colW[j] = t.Kind switch
            {
                TrackKind.Pixel => t.Value * fixedScale,
                TrackKind.Auto => autoW[j] * fixedScale,
                _ => starTotal > 0f ? remaining * MathF.Max(0f, t.Value) / starTotal : 0f,
            };
        }
    }

    private float GridContentHeight(NodeHandle node, in GridSpec g, int count, ReadOnlySpan<float> colW)
    {
        int childCount = _scene.ChildCount(node);
        if (childCount == 0) return 0f;
        int rows = (childCount + count - 1) / count;
        if (!float.IsNaN(g.RowHeight)) return rows * g.RowHeight + (rows - 1) * g.RowGap;

        float sumRowH = 0f, rowH = 0f; int k = 0;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), k++)
        {
            var cs = Measure(c, colW[k % count]);   // at the track width, so wrapping text reports its wrapped height
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
