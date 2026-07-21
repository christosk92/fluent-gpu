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

/// <summary>Which pager affordances a <see cref="PagedShelf"/> shows (combinable — e.g. <c>Chevrons | Pips</c>).</summary>
[Flags]
public enum ShelfPager : byte
{
    None = 0,
    /// <summary>Prev/next circular chevron buttons in the header.</summary>
    Chevrons = 1,
    /// <summary>A WinUI <see cref="PipsPager"/> (dots) in the header showing the page position.</summary>
    Pips = 2,
    /// <summary>Buttons overlaid at the left/right edges of the strip (the FlipView affordance).</summary>
    HoverEdge = 4,
}

/// <summary>State handed to a custom pager builder (<c>customPager</c>).</summary>
public readonly record struct ShelfPagerContext(
    int Page, int PageCount, bool CanPrev, bool CanNext, Action Prev, Action Next, Action<int> GoTo);

/// <summary>
/// A SIZE-REACTIVE, virtualized, paged horizontal card shelf (the Spotify "Made for you" / "Popular artists" rail). It
/// fits as many EQUAL cards as the available width allows — each sized to fill exactly within <c>[minCardW, maxCardW]</c>
/// (never ballooned when items are few) — via the engine's <see cref="FillRowVirtualLayout"/> over the
/// <see cref="IViewportVirtualLayout"/> seam, so cards re-fit live on resize with NO app-side width broker. Cards
/// virtualize/recycle (scales to thousands); the pager glides between pages through the <see cref="ItemsViewController"/>
/// (animated <see cref="ItemsViewController.StartBringItemIntoView"/>). Every pager affordance is available and
/// combinable (chevrons, pips, hover-edge, or a fully custom builder), each independently stylable via
/// <see cref="TemplateParts"/> (the <c>::part</c> convention).
///
/// <para>Because shelf cards are WIDTH-driven (a square cover sized to the card width), the control must know each card's
/// HEIGHT for the fitted width to size the (cross-axis) viewport — supply <c>cardHeight(cardW)</c>. It returns the full
/// card height for a given card width; the shelf sizes the strip to it.</para>
/// </summary>
public static class PagedShelf
{
    // ── Template parts (::part). Each part's doc lists props the control OWNS (re-asserted after a modifier). ──
    /// <summary>The shelf root (header + strip column). Owned: Direction, Children, OnBoundsChanged (self-measure).</summary>
    public const string PartRoot = "Root";
    /// <summary>The title box in the header row (only when a <c>title</c>, not a custom <c>header</c>, is used).</summary>
    public const string PartHeader = "Header";
    /// <summary>The previous-page chevron button.</summary>
    public const string PartChevronPrev = "ChevronPrev";
    /// <summary>The next-page chevron button.</summary>
    public const string PartChevronNext = "ChevronNext";
    /// <summary>The left-edge hover button (HoverEdge mode).</summary>
    public const string PartEdgePrev = "EdgePrev";
    /// <summary>The right-edge hover button (HoverEdge mode).</summary>
    public const string PartEdgeNext = "EdgeNext";
    /// <summary>The clipped, edge-faded viewport box that hosts the virtualized strip. Owned: Height, ClipToBounds, EdgeFade.</summary>
    public const string PartViewport = "Viewport";

    /// <summary>Build a paged shelf. <paramref name="cardAt"/> builds card <c>index</c> at the fitted card width.
    /// <para>Two sizing modes. The default VIRTUALIZED strip (recycles, scales to thousands) needs
    /// <paramref name="cardHeight"/> — the card's full height for a given card width — to size the (cross-axis) viewport
    /// up front, since only the visible page is realized. Pass <paramref name="measured"/><c> = true</c> for a content
    /// shelf of a handful of cards: it lays them ALL out in a measured row so the engine measures each card and sizes
    /// the row to the TALLEST (the card sizes itself — exact, no <paramref name="cardHeight"/>, no estimate);
    /// single-row, no recycling.</para>
    /// The data should be stable at mount (mount after async load / key to remount on change), like every items control.</summary>
    public static Element Create(
        int count,
        Func<int, float, Element> cardAt,
        Func<float, float>? cardHeight = null,
        string? title = null,
        Element? header = null,
        ShelfPager pager = ShelfPager.Chevrons,
        Func<ShelfPagerContext, Element>? customPager = null,
        float minCardW = 150f, float maxCardW = 200f, float gap = 12f,
        int rows = 1, int perPageOverride = 0, float fixedCardW = 0f,
        float headerGap = 12f, float edgeFade = 36f,
        string prevGlyph = "", string nextGlyph = Icons.ChevronRight,
        TemplateParts? parts = null,
        Func<int, string>? keyOf = null,
        int overscan = 2,
        bool measured = false,
        // The card subtree derives its dimensions from its arranged cell (aspect ratio/stretch) and ignores cardAt's
        // width hint. This keeps realized item components subscribed only to their data: a container resize re-fits the
        // retained cells in layout without scheduling every card component through _cardW.
        bool cardWidthAgnostic = false,
        // 0 = unlimited. Clamp the auto-fit column count: a wide viewport stops adding columns and grows each card
        // instead (see FillRowVirtualLayout.Fit) — the editorial "a few large cards" shelf that still adapts to width.
        int maxColumns = 0)
        => Embed.Comp(() => new PagedShelfCore(count, cardAt, cardHeight, title, header, pager, customPager,
                                               minCardW, maxCardW, gap, rows, perPageOverride, fixedCardW,
                                               headerGap, edgeFade, prevGlyph, nextGlyph, parts, keyOf, overscan, measured,
                                               cardWidthAgnostic, maxColumns))
           // SkeletonProxy: the deriver can't see into this component, so hand it the header + a few real cards (at a
           // representative width) to derive — the shelf shimmers as real cards instead of one default bar.
           with { SkeletonProxy = () => ShelfProxy(count, cardAt, header, title, maxCardW, gap, headerGap) };

    static Element ShelfProxy(int count, Func<int, float, Element> cardAt, Element? header, string? title, float cardW, float gap, float headerGap)
    {
        int n = Math.Clamp(count, 0, 6);
        var cards = new Element[n];
        for (int i = 0; i < n; i++) cards[i] = cardAt(i, cardW);
        Element head = header ?? (title is { Length: > 0 } t ? new TextEl(t) { Size = 20f, Weight = 700 } : new BoxEl());
        return new BoxEl
        {
            Direction = 1, Gap = headerGap,
            Children = [head, new BoxEl { Direction = 0, Gap = gap, ClipToBounds = true, Children = cards }],
        };
    }
}

/// <summary>The stateful core (self-measure → fit → virtualized strip + animated pager). See <see cref="PagedShelf"/>.</summary>
internal sealed class PagedShelfCore : Component
{
    const int MeasuredSampleCap = 24;
    // `measured: true` promises that the row is exactly as tall as its tallest card. App shelves are deliberately
    // bounded (Home 9–10, artist/detail sections ≤16), so realize that normal range in full. Sampling only the first
    // 8/12 made a later card with a wrapped title taller than the lock and its bottom text was scissored by the viewport.
    // Very large measured sets retain the virtual-probe fallback; callers with unbounded data should use cardHeight.
    const int MeasuredRealizeAllCapMin = 24;
    // Elevated cards paint a soft shadow (≈ OffsetY + Blur, ~6–10px) BELOW their layout box; a node's shadow draws
    // outside its OWN clip but is scissored by ANCESTOR clips at EXACT layout bounds (no outset). The strip clips twice
    // at the measured card height — the PartViewport box and the inner scroller viewport — so the clip chain needs
    // cross-axis headroom below the card or the halo is shaved. The pad lives in the item container's BOTTOM padding so
    // the card's own measure/height is unchanged (the shadow renders into the pad; both clip edges move below it).
    const float ShadowClearance = 12f;
    // Hover-lift headroom ABOVE the card — ShadowClearance's vertical mirror. Cards translate up on hover
    // (WhileHover OffsetY) and their hover halo blurs past the resting top edge, but the viewport clips exactly at
    // the strip's top, shaving both. The pad lives in the item container's TOP padding, and the root column's gap
    // between header and strip shrinks by the same amount (see Render) — so the on-screen rhythm is unchanged: the
    // former header gap simply moves INSIDE the clip, where the lift and halo can paint into it.
    const float LiftClearance = 12f;
    // Hover-halo headroom on the MAIN (horizontal) axis — LiftClearance's horizontal sibling. The first/last card's
    // elevation halo would hard-clip at the viewport's left/right edge (the viewport must keep clipping to page). The
    // fix is a MAIN-AXIS content gutter: the viewport is widened 2×HaloBleed (a negative horizontal margin, so the
    // shelf's own layout box is unmoved) and every card sits HaloBleed inside it (the FillRowVirtualLayout Lead/Trail
    // insets, or the non-virtual strip's L/R padding). Rest positions stay pixel-identical (inset +Bleed inside a
    // viewport shifted −Bleed cancels); the fitted cardW still uses the shelf width (Fit is fed the un-widened _w).
    // NOTE the gutter shows scrolled-out neighbor content ATTENUATED BY THE EDGE FADE at non-page-aligned rests, so a
    // shelf that enables the bleed should carry an edge fade ≥ the bleed (the fade is what keeps the gutter soft).
    const float HaloBleed = 12f;
    static readonly bool ShelfLog = Environment.GetEnvironmentVariable("FG_SHELFLOG") == "1";

    readonly int _count;
    readonly Func<int, float, Element> _cardAt;
    readonly Func<float, float>? _cardHeight;     // null in measured mode (the engine measures instead)
    readonly bool _measured;
    readonly string? _title;
    readonly Element? _header;
    readonly ShelfPager _pager;
    readonly Func<ShelfPagerContext, Element>? _customPager;
    readonly float _minCardW, _maxCardW, _gap;
    readonly int _rows, _perPageOverride, _maxColumns;
    readonly float _fixedCardW, _headerGap, _edgeFade;
    readonly string _prevGlyph, _nextGlyph;
    readonly TemplateParts? _parts;
    readonly Func<int, string>? _keyOf;
    readonly int _overscan;
    readonly bool _cardWidthAgnostic;

    readonly Signal<float> _w = new(0f);              // self-measured available width (no app broker)
    readonly Signal<int> _page = new(0);              // current page (chevrons/pips; re-synced from the settled offset)
    readonly Signal<int> _pageNav = new(0);           // pager NAV intent — only chevron/pip navigation re-arms the glide,
                                                      // a free-scroll page re-sync must NOT snap the strip to the grid
    readonly Signal<float> _measuredH = new(0f);      // probe-locked card height (measured-virtual mode)
    readonly Signal<float> _cardW = new(0f);          // fitted width consumed by the mounted ItemsView template
    readonly ItemsViewController _ctl = new();
    FillRowVirtualLayout? _layout;                    // stateful — hoisted once, reused across renders
    // SIGNAL (not a field): the re-probe completes by WRITING this — when the re-measured height happens to equal the
    // already-locked value, the equality-gated _measuredH write alone would never re-render us out of probe mode.
    readonly Signal<float> _measuredForCardW = new(float.NaN);
    readonly NodeHandle[] _probeNodes = new NodeHandle[MeasuredSampleCap];
    NodeHandle _probeHostNode = NodeHandle.Null;   // the invisible probe layer's root — RECORD-culled when not probing
    int _probeSample;
    int _lastMeasuredNav = -1;
    int _lastVirtualNav = -1;

    public PagedShelfCore(int count, Func<int, float, Element> cardAt, Func<float, float>? cardHeight, string? title,
                          Element? header, ShelfPager pager, Func<ShelfPagerContext, Element>? customPager,
                          float minCardW, float maxCardW, float gap, int rows, int perPageOverride, float fixedCardW,
                          float headerGap, float edgeFade, string prevGlyph, string nextGlyph, TemplateParts? parts,
                          Func<int, string>? keyOf, int overscan, bool measured, bool cardWidthAgnostic, int maxColumns = 0)
    {
        _count = count; _cardAt = cardAt; _cardHeight = cardHeight; _measured = measured; _title = title; _header = header;
        _pager = pager; _customPager = customPager; _minCardW = minCardW; _maxCardW = maxCardW; _gap = gap;
        _rows = Math.Max(1, rows); _perPageOverride = perPageOverride; _fixedCardW = fixedCardW;
        _headerGap = headerGap; _edgeFade = edgeFade; _prevGlyph = prevGlyph; _nextGlyph = nextGlyph;
        _parts = parts; _keyOf = keyOf; _overscan = overscan;
        _cardWidthAgnostic = cardWidthAgnostic;
        _maxColumns = Math.Max(0, maxColumns);
    }

    public override Element Render()
    {
        float w = _w.Value;                            // subscribe → re-fit on resize
        int page = _page.Value;                        // subscribe → pager state + glide retarget

        // Compute the fit the layout will land at (count-independent), to size the strip + page math.
        var (perPageColumns, cardW) = FillRowVirtualLayout.Fit(w, _minCardW, _maxCardW, _gap, _perPageOverride, _fixedCardW, _maxColumns);
        UseLayoutEffect(() =>
        {
            if (!_cardWidthAgnostic && MathF.Abs(_cardW.Peek() - cardW) > 0.25f) _cardW.Value = cardW;
        }, cardW);
        int perPageItems = Math.Max(1, perPageColumns * (_measured ? 1 : _rows));
        int pageCount = Math.Max(1, (_count + perPageItems - 1) / perPageItems);
        int maxPage = pageCount - 1;
        int p = Math.Clamp(page, 0, maxPage);
        bool canPrev = p > 0, canNext = p < maxPage;

        // Keep the stored page in range when a resize shrinks the page count (effect — never write a signal in render).
        UseEffect(() => { if (_page.Peek() > maxPage) _page.Value = maxPage; }, maxPage);

        // GoTo bumps the NAV intent even when the clamped page is unchanged — clicking ‹ at a free-scrolled fractional
        // offset within page 0 re-arms the glide back to the page boundary instead of silently doing nothing.
        void GoTo(int to) { _page.Value = Math.Clamp(to, 0, maxPage); _pageNav.Value = _pageNav.Peek() + 1; }
        int nav = _pageNav.Value;   // subscribe — a nav bump re-arms the bring-into-view effect below

        // Edge fades are OFFSET-driven (the engine's scroller AutoEdgeFade), not page-derived: the strip is a real
        // scroller the user can free-pan (touchpad/tilt-wheel), and a page-derived mask goes stale the moment the
        // offset diverges from the page grid — the "left fade dead while visibly mid-scroll" bug. The engine reads the
        // LIVE ScrollState per frame, so each edge fades exactly when content extends past it.
        bool fade = _edgeFade > 0f;

        // Stable hook surface — MeasuredBody (UseRef+effect) vs MeasuredVirtualBody (probe + bring-into-view) used to
        // branch on count, which reordered hook cells → InvalidCastException (EffectCell vs RefHolderCell).
        // Pick the structural mode from stable data only. A breakpoint must never replace an ItemsView with a flex strip.
        bool measuredRealizeAll = _measured && _count <= MeasuredRealizeAllCapMin;
        if (ShelfLog)
            Console.Error.WriteLine($"[shelf] count={_count} w={w:0} cardW={cardW:0} cols={perPageColumns} measured={_measured} realizeAll={measuredRealizeAll} mH={_measuredH.Peek():0.#} mFor={_measuredForCardW.Peek():0.#} sample={_probeSample}");
        // SUBSCRIBED reads (not Peek): the probe effect's height/for-width lock writes are what re-render us out of
        // probe mode — a Peek here leaves the shelf stuck on the invisible probe host forever.
        float measuredHLock = _measuredH.Value;
        bool needProbe = _measured && !measuredRealizeAll
            && (measuredHLock <= 0f || MathF.Abs(_measuredForCardW.Value - cardW) > 0.5f);
        if (needProbe) _probeSample = Math.Min(_count, MeasuredSampleCap);

        var viewport = UseRef(NodeHandle.Null);

        UseLayoutEffect(() =>
        {
            if (!measuredRealizeAll) return;
            bool animate = nav != _lastMeasuredNav;
            _lastMeasuredNav = nav;
            ScrollMeasuredViewport(viewport.Value, _page.Peek(), perPageColumns, cardW, animate);
        }, DepKey.From(HashCode.Combine(nav, perPageColumns, cardW, _count)));

        UseLayoutEffect(() =>
        {
            if (!needProbe) return;
            if (Context.Scene is not { } scene) return;
            float maxH = 0f;
            for (int i = 0; i < _probeSample; i++)
            {
                var h = _probeNodes[i];
                if (h.IsNull || !scene.IsLive(h)) continue;
                float ch = scene.Bounds(h).H;
                if (ch > maxH) maxH = ch;
            }
            if (maxH > 0.5f)
            {
                // REPLACE, not Max: the lock is per-cardW (mFor invalidates it on a width change), and a shelf that
                // re-fits narrower must not keep the taller old height as dead bottom padding.
                _measuredH.Value = maxH;
                _measuredForCardW.Value = cardW;
            }
        }, DepKey.From(HashCode.Combine(cardW, _probeSample)));

        // RECORD-cull the permanently-mounted probe layer when it isn't measuring. Opacity=0 alone does NOT stop the
        // recorder walking the subtree (SceneRecorder early-outs only on a cleared NodeFlags.Visible), so a settled
        // shelf would record its dozen phantom cards every frame. Clearing Visible skips the walk; layout still runs
        // (it ignores the flag), so a re-probe measures without a remount. The layer stays MOUNTED (see the probe-cell
        // contract below) — only its record-visibility toggles. Every needProbe transition coincides with this effect's
        // dep, and the only structural remount (measuredH crossing 0) flips needProbe too, so _probeHostNode is current.
        UseLayoutEffect(() =>
        {
            if (Context.Scene is not { } scene) return;
            var host = _probeHostNode;
            if (host.IsNull || !scene.IsLive(host)) return;
            if (needProbe) scene.Mark(host, NodeFlags.Visible);
            else { scene.Unmark(host, NodeFlags.Visible); scene.Mark(host, NodeFlags.PaintDirty); }
        }, needProbe);

        UseLayoutEffect(() =>
        {
            if (measuredRealizeAll || needProbe) return;
            bool animate = nav != _lastVirtualNav;
            _lastVirtualNav = nav;
            if (w > 1f) _ctl.StartBringItemIntoView(_page.Peek() * perPageItems, 0f, animate);
        }, DepKey.From(HashCode.Combine(nav, needProbe)));

        Element body = _measured
            ? (measuredRealizeAll
                ? MeasuredBody(perPageColumns, cardW, fade, viewport)
                : MeasuredVirtualBody(perPageItems, cardW, fade, needProbe))
            : VirtualBody(perPageItems, cardW, fade);
        if ((_pager & ShelfPager.HoverEdge) != 0)
            body = ZStack(body, new BoxEl
            {
                Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
                Children = [ EdgeButton(true, canPrev, () => GoTo(p - 1)), EdgeButton(false, canNext, () => GoTo(p + 1)) ],
            });

        // ── header (title + chevrons/pips/custom) ────────────────────────────────────────────────────────
        Element? headerEl = BuildHeader(p, pageCount, canPrev, canNext, GoTo);

        Element[] children = headerEl is null ? [ body ] : [ headerEl, body ];
        return _parts.Apply(PagedShelf.PartRoot, new BoxEl
        {
            // LiftClearance of the header gap lives INSIDE the strip's clip (the item container's top pad), so the
            // header→card distance on screen stays _headerGap while the clip gains hover-lift headroom.
            Direction = 1, Gap = MathF.Max(0f, _headerGap - LiftClearance),
            // No explicit width: the parent sizes us, so OnBoundsChanged reports the real available width (which the
            // strip's viewport then fills → FillRowVirtualLayout fits the same cardW).
            OnBoundsChanged = r => { if (r.W > 0f && MathF.Abs(r.W - _w.Peek()) > 0.5f) _w.Value = r.W; },
            Children = children,
        });
    }

    // ── The settled-offset → page re-sync: the strip is a real scroller, so ANY scroll source (chevron glide, touchpad
    // pan, tilt-wheel) can move it — the page state must follow the truth or the chevrons/pips (and anything derived
    // from them) go stale. Change-only (the long projection) and settle-only (a mid-glide write would retarget the
    // glide it's reporting on). The re-sync writes _page but NOT _pageNav, so it never re-arms a bring-into-view. ──
    (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action) PageScrollSync() =>
    (
        g => ((long)PageFromOffset(g.OffsetX) << 1) | ((g.Flags & ScrollState.MovingNowBit) != 0 ? 1L : 0L),
        g =>
        {
            if ((g.Flags & ScrollState.MovingNowBit) != 0) return;
            int page = PageFromOffset(g.OffsetX);
            if (page != _page.Peek()) _page.Value = page;
        }
    );

    // The page the settled offset actually shows (live fit — the callback closure freezes at ItemsView mount, so it
    // must not capture render-time cols/cardW). Mirrors ScrollMeasuredViewport's target math: page ⇒ cols·stride px.
    int PageFromOffset(float offX)
    {
        var (cols, cw) = FillRowVirtualLayout.Fit(_w.Peek(), _minCardW, _maxCardW, _gap, _perPageOverride, _fixedCardW, _maxColumns);
        float pageW = cols * (cw + _gap);
        if (pageW <= 1f) return 0;
        int perPage = Math.Max(1, cols * (_measured ? 1 : _rows));
        int pageCount = Math.Max(1, (_count + perPage - 1) / perPage);
        return Math.Clamp((int)MathF.Round(offX / pageW), 0, pageCount - 1);
    }

    // ── Measured-virtual body: sample-measure a bounded card set, lock height, then virtualize the strip. ──
    Element MeasuredVirtualBody(int perPageItems, float cardW, bool fade, bool needProbe)
    {
        var layout = _layout ??= new FillRowVirtualLayout(_minCardW, _maxCardW, _gap, 1, _perPageOverride, _fixedCardW, _maxColumns,
            leadInset: HaloBleed, trailInset: HaloBleed);
        int shelfOverscan = Math.Max(_overscan, perPageItems);
        float measuredH = _measuredH.Value;
        Element liveItems = ItemsView.Create(
            _count,
            i => _cardAt(i, _cardWidthAgnostic
                ? _maxCardW
                : (_cardW.Value > 0f ? _cardW.Value : layout.CardW)),
            RepeatLayout.Custom(layout, horizontal: true),
            selectionMode: ItemsSelectionMode.None,
            controller: _ctl,
            overscan: shelfOverscan,
            keyOf: _keyOf,
            grow: 1f,
            suppressScrollBar: true,
            autoEdgeFade: fade,
            onScrollGeometryChanged: PageScrollSync(),
            // Bottom padding absorbs the shadow clearance: FillRowVirtualLayout stretches this container to the full
            // viewport cross size (measuredH + clearance), and the card's Grow=1 fills the container's CONTENT box — so
            // the card stays measuredH tall and its shadow renders into the pad below both clip edges.
            // HoverElevatePaint on the CELL (not just the card inside): the deferral is a direct-sibling mechanism, and
            // at the strip level the siblings are these cells — the flag makes the cell hover-within-aware (see
            // InputDispatcher.UpdateHoverWithin), so the hovered card's cell paints above its neighbors' halo-overlap.
            containerFactory: (i, content, state, onInteraction, onFocusChanged) =>
                new BoxEl { Direction = 1, Padding = new Edges4(0f, LiftClearance, 0f, ShadowClearance), HoverElevatePaint = true, Children = [content] });

        // Keep this bounded probe layer mounted for the lifetime of a measured-virtual shelf. That makes a width
        // re-probe a pure update: the live ItemsView remains the same sibling and never flashes out of the tree.
        {
            // Do NOT clear _probeNodes on a RE-probe (cardW changed): the sample cells are KEYED, so the reconciler
            // reuses the realized nodes in place and OnRealized never re-fires — cleared handles would stay null, the
            // measure pass would see maxH=0, and the shelf would sit on the invisible probe host forever (the empty
            // "Fans also like"/"Appears on" bands). Reused handles stay live and re-measure at the new width.
            var sampleCells = new Element[_probeSample];
            for (int i = 0; i < _probeSample; i++)
            {
                int idx = i;
                sampleCells[i] = new BoxEl
                {
                    Key = "mshelf-probe:" + idx,
                    Direction = 1, Width = cardW,
                    OnRealized = h => _probeNodes[idx] = h,
                    Children = [ _cardAt(idx, cardW) ],
                };
            }
            Element probeHost = new BoxEl
            {
                Opacity = 0f, HitTestVisible = false,
                // Unpadded measuredH — the probe host is invisible (its own clip cuts nothing on screen) and its cells
                // measure PURE card height; the shadow-clearance pad lives only on the live strip's container/viewport.
                Height = measuredH > 0f ? measuredH : float.NaN,
                ClipToBounds = measuredH > 0f,
                OnRealized = h => _probeHostNode = h,   // handle for the record-cull toggle (see the needProbe effect)
                Children = sampleCells,
            };
            // On re-probe keep the last good strip visible and interactive. The invisible sample overlays it and the
            // height lock is replaced only after layout reports a complete new measurement. Constrain the overlay to
            // the measured shelf width: the probe row's intrinsic width is N×cardW, and letting that width size this
            // ZStack makes the INNER ScrollEl believe the off-screen strip is its viewport. The outer page then clips
            // first, paging clamps after ~one click, and the scroller's right edge-fade is emitted off-screen.
            float viewportW = _w.Value;
            // This path pins an EXPLICIT width (the probe row's intrinsic N×cardW must not size the ZStack), so the
            // negative-margin stretch trick can't apply — widen the pinned width by 2×HaloBleed instead and shift it
            // −HaloBleed (Margin) so the widened clip straddles both gutters exactly like the stretch path. The live
            // ItemsView (grow:1) fills the widened ZStack ⇒ SetViewport fed _w+2·Bleed ⇒ layout re-fits back to _w.
            float widenedW = viewportW > 0.5f ? viewportW + 2f * HaloBleed : float.NaN;
            Element probing = measuredH > 0f
                ? ZStack(liveItems, probeHost) with { Width = widenedW }
                : probeHost;
            return _parts.Apply(PagedShelf.PartViewport, new BoxEl
            {
                // + both clearances: the viewport (and the inner scroller it hosts) both clip at this height; the extra
                // headroom below AND above the card lets the soft shadow + hover lift paint (the pads are inside each
                // item container, so the card itself still measures/fills exactly measuredH).
                Width = widenedW,
                Margin = new Edges4(-HaloBleed, 0f, -HaloBleed, 0f),
                Height = measuredH > 0f ? measuredH + ShadowClearance + LiftClearance : float.NaN,
                ClipToBounds = true,
                // Clip-ESCAPE root: the hover-elevated cell hoists out of this viewport's clip AND the inner
                // scroller's edge fade, so the lifted card's halo paints into the page — resting content stays clipped.
                HoverElevateClipRoot = true,
                Animate = MotionRecipes.CardResizeHeight,
                Children = [ probing ],
            });
        }
    }

    // ── Measured body (auto-height): NOT virtualized. Lays ALL cards in one flex row; the engine measures each card's
    // natural height and the row's default cross-stretch (FlexAlign.Stretch) makes every card the height of the TALLEST
    // — uniform, EXACT, and computed by the layout engine (no cardHeight() estimate; the card sizes itself). For the
    // handful of cards a content shelf holds, laying them all out beats the machinery to avoid it; paging slides the
    // row (animated OffsetX) rather than virtualizing. Single-row (Rows == 1) — the content-shelf shape. ──
    Element MeasuredBody(int perPageColumns, float cardW, bool fade, Ref<NodeHandle> viewport)
    {
        var cells = new Element[Math.Max(0, _count)];
        for (int i = 0; i < _count; i++)
        {
            int idx = i;
            // COLUMN cell at the fitted width so the card's own Grow=1 fills the cell's (stretched) HEIGHT — not the
            // row's width — and the card cross-stretches to cardW. Mirrors the virtualized cell, minus the recycler.
            cells[i] = new BoxEl { Direction = 1, Width = cardW, HoverElevatePaint = true, Children = [ _cardAt(idx, cardW) ] };
        }
        // Top/bottom padding sits the content ScrollEl's clip edges beyond the card's lift + shadow; it is OUTSIDE the
        // row's cross stretch, so cells still stretch to the tallest CARD (the pads do not inflate card height). L/R
        // HaloBleed padding is the MAIN-AXIS content gutter (the non-virtual sibling of the FillRowVirtualLayout insets):
        // cards sit HaloBleed inside the scroll content so the first/last card's elevation halo has room, while the
        // ScrollEl's negative horizontal margin widens the clip 2×HaloBleed into the surrounding gutters (rest positions
        // cancel: gutter +Bleed inside a viewport shifted −Bleed). Page targets anchor to page·cols·stride, so they cancel.
        Element strip = new BoxEl { Direction = 0, Gap = _gap, Padding = new Edges4(HaloBleed, LiftClearance, HaloBleed, ShadowClearance), Children = cells };
        // The scroller owns the clip + edge-fade scope, so it is the native clip-ESCAPE root: the hovered cell parks
        // under it and hoists after both scopes close. Keeping the ScrollEl as the root column's DIRECT child also
        // preserves cross-axis stretch; an intervening default-row wrapper collapses this Grow=0 viewport to width 0.
        return _parts.Apply(PagedShelf.PartViewport, new ScrollEl
        {
            Horizontal = true,
            Grow = 0f,
            SuppressScrollBar = true,
            AutoEdgeFade = fade,
            HoverElevateClipRoot = true,
            Margin = new Edges4(-HaloBleed, 0f, -HaloBleed, 0f),
            OnScrollGeometryChanged = PageScrollSync(),
            Content = strip,
            OnRealized = h => viewport.Value = h,
        });
    }

    void ScrollMeasuredViewport(NodeHandle vp, int page, int perPageColumns, float cardW, bool animate)
    {
        if (Context.Scene is not { } scene || vp.IsNull || !scene.IsLive(vp) || !scene.HasScroll(vp)) return;

        ref ScrollState sc = ref scene.ScrollRef(vp);
        float stride = cardW + _gap;
        float maxX = MathF.Max(0f, sc.ContentW - sc.ViewportW);
        float target = Math.Clamp(page * Math.Max(1, perPageColumns) * stride, 0f, maxX);
        // Already at (idle) or already chasing this target ⇒ don't re-arm.
        float pendCur = sc.PendingTargetX;
        if ((!float.IsNaN(pendCur) && MathF.Abs(pendCur - target) < 0.5f) ||
            (float.IsNaN(pendCur) && MathF.Abs(sc.OffsetX - target) < 0.5f)) return;

        if (Motion.ReducedMotion || !animate)
        {
            sc.OffsetX = sc.TargetX = target;
            NodeHandle content = sc.ContentNode;
            if (!content.IsNull && scene.IsLive(content))
            {
                scene.Paint(content).LocalTransform = Affine2D.Translation(-target, 0f);
                scene.Mark(content, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            }
            Context.RequestRerender();
            return;
        }

        sc.Phase = ScrollIntegrator.WheelAnimating;
        sc.PhaseFlags = ScrollState.PhaseProgrammatic;
        sc.FlingVelocity = 0f;
        sc.FlingRetargeted = false;
        sc.FlingSnapTarget = float.NaN;
        sc.PendingTargetX = target;
        Context.ArmScroll?.Invoke(vp);
        Context.RequestRerender();
    }

    // ── Virtualized body: the size-reactive, recycling strip (scales to thousands). Needs cardHeight(cardW) to size
    // the (cross-axis) viewport up front, since only the visible page is realized. ──
    Element VirtualBody(int perPageItems, float cardW, bool fade)
    {
        // The SAME stateful layout instance the engine drives via SetViewport; hoisted so its fit cache survives renders.
        // Lead/Trail = HaloBleed carve the halo gutters INSIDE the viewport (widened below by the same amount).
        var layout = _layout ??= new FillRowVirtualLayout(_minCardW, _maxCardW, _gap, _rows, _perPageOverride, _fixedCardW, _maxColumns,
            leadInset: HaloBleed, trailInset: HaloBleed);

        float shelfH = _cardHeight is null ? float.NaN : _rows * _cardHeight(cardW) + (_rows - 1) * _gap;

        // ItemsView is an Embed.Comp → its template closure FREEZES at first mount (when width was 0 ⇒ cardW=min). Read
        // the layout's LIVE fitted width at realize time (the engine sets it via SetViewport every arrange) so the card
        // always matches its cell — otherwise cards stay min-width inside full-width cells (huge gaps + short cards).
        int shelfOverscan = Math.Max(_overscan, perPageItems);
        Element items = ItemsView.Create(
            _count,
            i => _cardAt(i, _cardWidthAgnostic
                ? _maxCardW
                : (_cardW.Value > 0f ? _cardW.Value : layout.CardW)),
            RepeatLayout.Custom(layout, horizontal: true),
            selectionMode: ItemsSelectionMode.None,
            controller: _ctl,
            overscan: shelfOverscan,
            keyOf: _keyOf,
            grow: 1f,
            suppressScrollBar: true,   // paged: navigate by the chevron/pips pager, not a draggable scrollbar
            autoEdgeFade: fade,
            onScrollGeometryChanged: PageScrollSync(),
            // bare passthrough cell, COLUMN so the card cross-stretches to the cell's live width (fills it even mid-resize);
            // the card carries its own visuals (no ItemContainer selection chrome around it). Single-row only: a bottom
            // pad absorbs the card's shadow clearance (card stays shelfH, halo paints into the pad below the clip).
            // Multi-row keeps the old clip — RowHeight(cross) would spread the pad across rows and distort every card,
            // and interior rows occlude their own shadows against the row below anyway.
            containerFactory: (i, content, state, onInteraction, onFocusChanged) =>
                new BoxEl { Direction = 1, Padding = _rows == 1 ? new Edges4(0f, LiftClearance, 0f, ShadowClearance) : default, HoverElevatePaint = true, Children = [content] });

        return _parts.Apply(PagedShelf.PartViewport, new BoxEl
        {
            Height = shelfH > 0f ? (_rows == 1 ? shelfH + ShadowClearance + LiftClearance : shelfH) : float.NaN,
            ClipToBounds = true,
            // Clip-ESCAPE root: the hover-elevated cell hoists out of this clip + the inner scroller's edge fade —
            // its lift/halo paint into the page while resting content stays exactly clipped (multi-row included:
            // the grid keeps its tight clip, only the hovered cell escapes).
            HoverElevateClipRoot = true,
            // Widen the clip 2×HaloBleed into the surrounding gutters WITHOUT moving the shelf's layout box: a negative
            // horizontal margin on a cross-STRETCH child resolves to width = availCross − crossMargin (= _w + 2·Bleed)
            // at x = −Bleed (FlexLayout arrange). The ItemsView (grow:1) fills it, so SetViewport is fed _w+2·Bleed and
            // the layout subtracts the gutters back to _w for the fit — cards keep their width and rest positions.
            Margin = new Edges4(-HaloBleed, 0f, -HaloBleed, 0f),
            Children = [ items ],
        });
    }

    Element? BuildHeader(int p, int pageCount, bool canPrev, bool canNext, Action<int> goTo)
    {
        Element? titleEl = _header
            ?? (_title is null ? null : _parts.Apply(PagedShelf.PartHeader, new BoxEl { Children = [ Heading(_title) ] }));

        var row = new List<Element>(4);
        if (titleEl is not null) row.Add(titleEl);
        row.Add(new BoxEl { Grow = 1f });   // spacer pushes the pager to the trailing edge

        if (_customPager is not null)
            row.Add(_customPager(new ShelfPagerContext(p, pageCount, canPrev, canNext,
                () => goTo(p - 1), () => goTo(p + 1), goTo)));
        else
        {
            if ((_pager & ShelfPager.Pips) != 0 && pageCount > 1)
                // Pass the page signal directly; onChange=goTo re-arms the bring-into-view glide (_pageNav bump).
                row.Add(PipsPager.Create(pageCount, _page, onChange: goTo));
            if ((_pager & ShelfPager.Chevrons) != 0)
            {
                row.Add(Chevron(_prevGlyph, canPrev, () => goTo(p - 1), PagedShelf.PartChevronPrev));
                row.Add(Chevron(_nextGlyph, canNext, () => goTo(p + 1), PagedShelf.PartChevronNext));
            }
        }

        // Nothing to show (no title, no pager controls) → no header row.
        bool hasPager = _customPager is not null
            || ((_pager & ShelfPager.Pips) != 0 && pageCount > 1)
            || (_pager & ShelfPager.Chevrons) != 0;
        if (titleEl is null && !hasPager) return null;

        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = HeaderItemGap, Children = row.ToArray() };
    }

    const float HeaderItemGap = 8f;

    Element Chevron(string glyph, bool enabled, Action onClick, string part) => _parts.Apply(part, new BoxEl
    {
        Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(16f), Fill = Tok.FillControlDefault,
        HoverFill = enabled ? Tok.FillControlSecondary : Tok.FillControlDefault,
        Opacity = enabled ? 1f : 0.35f, OnClick = enabled ? onClick : null,
        Children = [ Icon(glyph, 13f, Tok.TextSecondary) ],
    });

    Element EdgeButton(bool left, bool enabled, Action onClick) => _parts.Apply(left ? PagedShelf.PartEdgePrev : PagedShelf.PartEdgeNext, new BoxEl
    {
        Width = 36f, Height = 36f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Margin = new Edges4(left ? 4f : 0f, 0f, left ? 0f : 4f, 0f),
        Corners = CornerRadius4.All(18f), Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary,
        Shadow = Elevation.Card, Opacity = enabled ? 1f : 0f, OnClick = enabled ? onClick : null,
        Children = [ Icon(left ? _prevGlyph : _nextGlyph, 14f, Tok.TextSecondary) ],
    });
}
