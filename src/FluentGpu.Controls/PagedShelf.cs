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
        bool measured = false)
        => Embed.Comp(() => new PagedShelfCore(count, cardAt, cardHeight, title, header, pager, customPager,
                                               minCardW, maxCardW, gap, rows, perPageOverride, fixedCardW,
                                               headerGap, edgeFade, prevGlyph, nextGlyph, parts, keyOf, overscan, measured))
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
    readonly int _count;
    readonly Func<int, float, Element> _cardAt;
    readonly Func<float, float>? _cardHeight;     // null in measured mode (the engine measures instead)
    readonly bool _measured;
    readonly string? _title;
    readonly Element? _header;
    readonly ShelfPager _pager;
    readonly Func<ShelfPagerContext, Element>? _customPager;
    readonly float _minCardW, _maxCardW, _gap;
    readonly int _rows, _perPageOverride;
    readonly float _fixedCardW, _headerGap, _edgeFade;
    readonly string _prevGlyph, _nextGlyph;
    readonly TemplateParts? _parts;
    readonly Func<int, string>? _keyOf;
    readonly int _overscan;

    readonly Signal<float> _w = new(0f);              // self-measured available width (no app broker)
    readonly Signal<int> _page = new(0);              // current page (pager intent)
    readonly ItemsViewController _ctl = new();
    FillRowVirtualLayout? _layout;                    // stateful — hoisted once, reused across renders

    public PagedShelfCore(int count, Func<int, float, Element> cardAt, Func<float, float>? cardHeight, string? title,
                          Element? header, ShelfPager pager, Func<ShelfPagerContext, Element>? customPager,
                          float minCardW, float maxCardW, float gap, int rows, int perPageOverride, float fixedCardW,
                          float headerGap, float edgeFade, string prevGlyph, string nextGlyph, TemplateParts? parts,
                          Func<int, string>? keyOf, int overscan, bool measured)
    {
        _count = count; _cardAt = cardAt; _cardHeight = cardHeight; _measured = measured; _title = title; _header = header;
        _pager = pager; _customPager = customPager; _minCardW = minCardW; _maxCardW = maxCardW; _gap = gap;
        _rows = Math.Max(1, rows); _perPageOverride = perPageOverride; _fixedCardW = fixedCardW;
        _headerGap = headerGap; _edgeFade = edgeFade; _prevGlyph = prevGlyph; _nextGlyph = nextGlyph;
        _parts = parts; _keyOf = keyOf; _overscan = overscan;
    }

    public override Element Render()
    {
        float w = _w.Value;                            // subscribe → re-fit on resize
        int page = _page.Value;                        // subscribe → pager state + glide retarget

        // Compute the fit the layout will land at (count-independent), to size the strip + page math.
        var (perPageColumns, cardW) = FillRowVirtualLayout.Fit(w, _minCardW, _maxCardW, _gap, _perPageOverride, _fixedCardW);
        int perPageItems = Math.Max(1, perPageColumns * (_measured ? 1 : _rows));
        int pageCount = Math.Max(1, (_count + perPageItems - 1) / perPageItems);
        int maxPage = pageCount - 1;
        int p = Math.Clamp(page, 0, maxPage);
        bool canPrev = p > 0, canNext = p < maxPage;

        // Keep the stored page in range when a resize shrinks the page count (effect — never write a signal in render).
        UseEffect(() => { if (_page.Peek() > maxPage) _page.Value = maxPage; }, maxPage);

        void GoTo(int to) => _page.Value = Math.Clamp(to, 0, maxPage);

        EdgeMask mask = (canPrev, canNext) switch
        {
            (true, true)  => EdgeMask.Horizontal,
            (true, false) => EdgeMask.Left,
            (false, true) => EdgeMask.Right,
            _             => EdgeMask.None,
        };
        EdgeFadeSpec? fade = mask == EdgeMask.None || _edgeFade <= 0f ? null : new EdgeFadeSpec(mask, _edgeFade);

        Element body = _measured
            ? MeasuredBody(perPageColumns, cardW, p, fade)
            : VirtualBody(perPageItems, cardW, p, w, fade);
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
            Direction = 1, Gap = _headerGap,
            // No explicit width: the parent sizes us, so OnBoundsChanged reports the real available width (which the
            // strip's viewport then fills → FillRowVirtualLayout fits the same cardW).
            OnBoundsChanged = r => { if (r.W > 0f && MathF.Abs(r.W - _w.Peek()) > 0.5f) _w.Value = r.W; },
            Children = children,
        });
    }

    // ── Measured body (auto-height): NOT virtualized. Lays ALL cards in one flex row; the engine measures each card's
    // natural height and the row's default cross-stretch (FlexAlign.Stretch) makes every card the height of the TALLEST
    // — uniform, EXACT, and computed by the layout engine (no cardHeight() estimate; the card sizes itself). For the
    // handful of cards a content shelf holds, laying them all out beats the machinery to avoid it; paging slides the
    // row (animated OffsetX) rather than virtualizing. Single-row (Rows == 1) — the content-shelf shape. ──
    Element MeasuredBody(int perPageColumns, float cardW, int p, EdgeFadeSpec? fade)
    {
        var viewport = UseRef(NodeHandle.Null);
        UseLayoutEffect(() => ScrollMeasuredViewport(viewport.Value, p, perPageColumns, cardW),
            p, perPageColumns, cardW, _count);

        var cells = new Element[Math.Max(0, _count)];
        for (int i = 0; i < _count; i++)
        {
            int idx = i;
            // COLUMN cell at the fitted width so the card's own Grow=1 fills the cell's (stretched) HEIGHT — not the
            // row's width — and the card cross-stretches to cardW. Mirrors the virtualized cell, minus the recycler.
            cells[i] = new BoxEl { Direction = 1, Width = cardW, Children = [ _cardAt(idx, cardW) ] };
        }
        Element strip = new BoxEl { Direction = 0, Gap = _gap, Children = cells };
        return _parts.Apply(PagedShelf.PartViewport, new ScrollEl
        {
            Horizontal = true,
            Grow = 0f,
            SuppressScrollBar = true,
            EdgeFade = fade,
            Content = strip,
            OnRealized = h => viewport.Value = h,
        });
    }

    void ScrollMeasuredViewport(NodeHandle vp, int page, int perPageColumns, float cardW)
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

        if (Motion.ReducedMotion)
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
    Element VirtualBody(int perPageItems, float cardW, int p, float w, EdgeFadeSpec? fade)
    {
        // The SAME stateful layout instance the engine drives via SetViewport; hoisted so its fit cache survives renders.
        var layout = _layout ??= new FillRowVirtualLayout(_minCardW, _maxCardW, _gap, _rows, _perPageOverride, _fixedCardW);

        // Glide to the current page once the viewport is measured + the controller is wired (post-layout). Retargets on
        // page change AND width change (a resize keeps the same page aligned to its new offset).
        UseLayoutEffect(() => { if (w > 1f) _ctl.StartBringItemIntoView(p * perPageItems, 0f, animate: true); }, p, w);

        float shelfH = _cardHeight is null ? float.NaN : _rows * _cardHeight(cardW) + (_rows - 1) * _gap;

        // ItemsView is an Embed.Comp → its template closure FREEZES at first mount (when width was 0 ⇒ cardW=min). Read
        // the layout's LIVE fitted width at realize time (the engine sets it via SetViewport every arrange) so the card
        // always matches its cell — otherwise cards stay min-width inside full-width cells (huge gaps + short cards).
        Element items = ItemsView.Create(
            _count,
            i => _cardAt(i, layout.CardW),
            RepeatLayout.Custom(layout, horizontal: true),
            selectionMode: ItemsSelectionMode.None,
            controller: _ctl,
            overscan: _overscan,
            keyOf: _keyOf,
            grow: 1f,
            suppressScrollBar: true,   // paged: navigate by the chevron/pips pager, not a draggable scrollbar
            // bare passthrough cell, COLUMN so the card cross-stretches to the cell's live width (fills it even mid-resize);
            // the card carries its own visuals (no ItemContainer selection chrome around it).
            containerFactory: (i, content, state, onInteraction, onFocusChanged) => new BoxEl { Direction = 1, Children = [content] });

        return _parts.Apply(PagedShelf.PartViewport, new BoxEl
        {
            Height = shelfH > 0f ? shelfH : float.NaN,
            ClipToBounds = true,
            EdgeFade = fade,
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
                row.Add(PipsPager.Create(pageCount, p, goTo));
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
