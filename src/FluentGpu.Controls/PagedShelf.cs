using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Scroll;
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

/// <summary>Whether a <see cref="PagedShelf"/> RESTS on page boundaries or free-pans (default <see cref="None"/> —
/// byte-identical to the pre-snap shelf).</summary>
public enum ShelfSnap : byte
{
    /// <summary>Free-pan: the strip rests wherever the gesture left it (today's behavior).</summary>
    None = 0,
    /// <summary>Page-mandatory: every rest lands on a page boundary. Three cooperating mechanisms, one opt-in —
    /// (a) the viewport's snap interval becomes the live page stride, so a touch/touchpad FLING retargets its decay and
    /// lands exactly on a boundary (the engine's own physics, untouched); (b) a wheel/keyboard settle at a fractional
    /// offset glides to the nearest boundary afterwards, since the engine hard-clamps those by contract and never snaps
    /// them; (c) a chevron/pip activation on the CURRENT page re-arms that same glide, so the affordance is never a
    /// no-op while the strip rests mid-page.</summary>
    Page = 1,
}

/// <summary>State handed to a custom pager builder (<c>customPager</c>). The three ACTION slots are REFERENCE-STABLE for
/// the shelf's lifetime (they read the live page + grid, never a render's locals) — so a pager that packs this context
/// into a props record for its own component gets value equality across renders and its subtree short-circuits instead of
/// re-rendering on every shelf render. Only the four value slots change.</summary>
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
        float headerGap = 12f,
        // Auto-edge-fade feather WIDTH in DIP (0 = no fade). Both the on/off bit AND the width: it reaches the viewport as
        // ScrollEl/VirtualListEl.AutoEdgeFadeBand, so a shelf whose trailing cell must stay crisp narrows the band rather
        // than dropping the cue. Keep it ≥ the 12 DIP halo-bleed gutter — the fade is what keeps a scrolled-out neighbour
        // in that gutter soft at a non-page-aligned rest.
        float edgeFade = 36f,
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
        int maxColumns = 0,
        // Opt-in page-mandatory snapping (see ShelfSnap.Page). Default None keeps every existing shelf free-panning.
        ShelfSnap snap = ShelfSnap.None)
        => Embed.Comp(() => new PagedShelfCore(count, cardAt, cardHeight, title, header, pager, customPager,
                                               minCardW, maxCardW, gap, rows, perPageOverride, fixedCardW,
                                               headerGap, edgeFade, prevGlyph, nextGlyph, parts, keyOf, overscan, measured,
                                               cardWidthAgnostic, maxColumns, snap))
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
    // Probe-lock granularity: two measured values this close are the SAME lock (see _measuredH/_measuredForCardW). Used
    // by both the re-probe predicate (Render) and the signals' equality comparer, so they can never disagree.
    const float MeasureTolerance = 0.5f;
    // Page-snap deadband: an offset this close to a page boundary IS on it. The programmatic glide lands on its target
    // exactly (the integrator writes the target verbatim on settle) and a snap fling lands within SnapLandEpsPx, so this
    // only has to absorb sub-pixel layout remainder — and it is what makes the post-settle re-snap idempotent (the glide's
    // own settle re-enters the same callback and must find nothing to do).
    const float SettleSnapEpsPx = 0.5f;
    // Page-key quantum (DIP) for the settled-offset observer's projection. The projection MUST change when a settle
    // lands somewhere new even if the ROUNDED page is unchanged — a wheel notch shorter than half a stride leaves the
    // page identical, the key identical, and the change-only observer therefore never fires the re-snap that is the
    // whole point of ShelfSnap.Page. Coarse on purpose: fine enough that any real settle moves it, coarse enough that a
    // sub-pixel layout remainder cannot. ReSnapSettled's SettleSnapEpsPx idempotence makes the extra fires free.
    const float PageKeyQuantumPx = 8f;
    // Bucket ceiling for that quantum term, so the packed key can never carry offset bits into the page field.
    // 2^20 buckets × 8 DIP ≈ 8.4M DIP of scrollable content — orders past any real shelf.
    const long PageKeyQuantumCap = (1L << 20) - 1L;
    // ── LIFT DEBOUNCE (ShelfSnap.Page). Grace window (ms) between a settle that WANTS a re-snap and the moment the glide
    // is actually armed. This is WALL-CLOCK SCHEDULING ONLY: it delays WHEN the one programmatic seam is called, never the
    // glide itself (which stays the exact closed-form Driven chase the kernel runs — dt-deterministic, untouched).
    //
    // Why it exists: a live two-finger pan is NOT one continuous motion. The OS segments it, and the kernel's contact
    // resampler clamps at the newest sample during a micro-pause — so no offset moves, and UserScrollActive drops
    // ~14–20 ms into any pause while Activity is still Drag. Arming ScrollIntoView.ScrollTo there would post a Driven
    // chase INTO the live gesture and kill the pan just as surely as the old integrator's phase overwrite did — a
    // fresh contact sample arriving mid-glide is not what "resumed panning" means to the kernel. The ACTIVITY GATE
    // (in the observer action) is the STRUCTURAL fix for that. This window covers the other half: the OS also
    // segments one continuous scroll into several complete gesture cycles, and between two segments Activity
    // genuinely IS Idle — no gate can tell that rest from a real one, only elapsed time can. A resumed pan pushes the
    // deadline out (both gesture edges bump _snapTick), so a segmented pan is never snapped mid-flight.
    //
    // 180 ms is past every observed inter-segment gap and still under the ~250 ms at which a deliberate rest starts to feel
    // unanswered. It is deliberately SHORTER than InputDispatcher.StickyAxisMs (400 ms), which is that heuristic's generous
    // upper bound on the same segmentation window — waiting 400 ms to answer a lift reads as a broken control, and the
    // phase gate already covers the common case. This is the tuning lever if a device shows longer gaps.
    // It applies to a wheel settle too (one path, no branch): a burst of notches is COALESCED into a single snap instead of
    // each notch's settle arming a glide the next notch has to fight.
    const float SnapGraceMs = 180f;
    // ── DIRECTIONAL COMMIT threshold, as a fraction of one page (the FlipView MandatorySingle model). A lift whose
    // PROJECTED resting offset has travelled at least this far from the page the gesture STARTED on commits to the next
    // page; anything shorter springs back to the start page. WinUI's implicit 50%-nearest rule is simply unreachable by
    // panning on a real shelf page (≈612 DIP on the artist chart — the strip runs out of finger first), which is why a
    // pan used to be answered by a yank back to where it started. 0.25 is the touch convention.
    const float CommitFraction = 0.25f;
    static readonly bool ShelfLog = Environment.GetEnvironmentVariable("FG_SHELFLOG") == "1";

    /// <summary>Tolerance equality for the probe-lock signals: equal within <see cref="MeasureTolerance"/>, NaN-aware
    /// (NaN equals NaN — the "never probed" sentinel must not notify itself; NaN never equals a real measurement).
    /// A singleton — the shelf allocates no comparer per instance.</summary>
    sealed class MeasureTolerantComparer : IEqualityComparer<float>
    {
        internal static readonly MeasureTolerantComparer Instance = new();
        public bool Equals(float a, float b)
            => float.IsNaN(a) || float.IsNaN(b) ? float.IsNaN(a) && float.IsNaN(b) : MathF.Abs(a - b) <= MeasureTolerance;
        // Buckets are deliberately coarse-free: these signals are never hashed (Signal<T> only calls Equals), and a
        // tolerance relation has no consistent hash. Constant 0 keeps the contract honest if one ever is.
        public int GetHashCode(float v) => 0;
    }

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
    readonly ShelfSnap _snap;

    readonly Signal<float> _w = new(0f);              // self-measured available width (no app broker)
    readonly Signal<int> _page = new(0);              // current page (chevrons/pips; re-synced from the settled offset)
    readonly Signal<int> _pageNav = new(0);           // pager NAV intent — only chevron/pip navigation re-arms the glide,
                                                      // a free-scroll page re-sync must NOT snap the strip to the grid
    // ±0.5px TOLERANCE (not default equality): the probe writes these from MEASURED bounds, and Render subscribes them
    // (:235-237) — so a sub-pixel re-measure (a glyph metric that lands 0.02px taller after a font/atlas refresh) would
    // notify → re-render → re-probe → write again, a measure→write→re-render loop that rebuilds every card of every
    // shelf for as long as a page keeps filling. Half a pixel is below the layout's own snapping granularity, so a
    // difference that small can never move a card; coalescing it costs nothing and cuts the loop at the source.
    readonly Signal<float> _measuredH = new(0f, MeasureTolerantComparer.Instance);   // probe-locked card height (measured-virtual mode)
    readonly Signal<float> _cardW = new(0f);          // fitted width consumed by the mounted ItemsView template
    readonly ItemsViewController _ctl = new();
    FillRowVirtualLayout? _layout;                    // stateful — hoisted once, reused across renders
    // SIGNAL (not a field): the re-probe completes by WRITING this — when the re-measured height happens to equal the
    // already-locked value, the equality-gated _measuredH write alone would never re-render us out of probe mode.
    // Same ±0.5px tolerance, and it is EXACTLY the needProbe threshold (:237 re-probes when |mFor − cardW| > 0.5f), so the
    // two stay complementary: whenever a re-probe is warranted the lock write is BY DEFINITION outside tolerance and still
    // notifies — the exit-probe-mode contract above is preserved — while a within-tolerance re-write is silent.
    readonly Signal<float> _measuredForCardW = new(float.NaN, MeasureTolerantComparer.Instance);
    readonly NodeHandle[] _probeNodes = new NodeHandle[MeasuredSampleCap];
    NodeHandle _probeHostNode = NodeHandle.Null;   // the invisible probe layer's root — RECORD-culled when not probing
    NodeHandle _measuredVp = NodeHandle.Null;      // the measured realize-all body's own ScrollEl (see ShelfViewport)
    int _probeSample;
    int _lastMeasuredNav = -1;
    int _lastVirtualNav = -1;

    // ── Snap-feel state (ShelfSnap.Page). Plain UI-thread scalars: written by the settled-offset observer (which the host
    // runs after the scroll kernel's tick) and read by the one debounce callback. None of it is scene state, and none of
    // it is physics — the offset stays single-writer (the kernel), reached only through ScrollIntoView.ScrollTo.
    float _pendingSnapTarget = float.NaN;   // the offset the debounce will glide to when it fires; NaN = nothing armed
    float _gestureAnchorX = float.NaN;      // the offset the CURRENT user gesture STARTED at; NaN = no gesture in flight
    bool _userScrollWas;                    // last observed UserScrollActive — the rising-edge detector for that anchor
    // The kernel's live Driven-chase Target isn't a readable SceneStore column (ScrollState.TargetX is deleted,
    // kernel-internal only) — mirror the last POSTED destination locally so ScrollMeasuredViewport can still tell
    // "already chasing this exact target" from "a fresh destination" without re-arming/re-latching every effect
    // re-fire. Cleared the moment the body isn't Driven any more (settled, or a user gesture took the offset back).
    float? _lastProgrammaticTargetX;
    // Bumped on BOTH gesture edges: the rising edge pushes a pending snap's deadline out (a resumed pan cancels it), the
    // falling edge arms a fresh one. Render subscribes it, so a bump re-renders us and the UseTimeout below re-arms.
    readonly Signal<long> _snapTick = new(0);
    readonly Action _commitPendingSnap;     // hoisted: the per-render UseTimeout re-arm must allocate no delegate

    // ── Pager delegates, CACHED for the lifetime of the shelf. A custom pager receives them inside a ShelfPagerContext
    // that a component (ArtistPopular's ChartPager) turns into a props RECORD: a freshly-allocated closure per render
    // makes that record compare UNEQUAL every time, so the reconciler's props channel can never short-circuit and the
    // whole pips subtree re-renders on every single shelf render. These three capture NOTHING render-scoped — they read
    // the live page + the live grid through _page/PageGrid() — so one instance each serves forever.
    readonly Action<int> _pagerGoTo;
    readonly Action _pagerPrev, _pagerNext;

    /// <summary>The live scroll viewport of whichever body is mounted — the virtualized bodies' ItemsView viewport (via the
    /// controller seam) or the measured realize-all body's own ScrollEl. One accessor so the snap/glide writes never have to
    /// know which structural mode is up. Null before the body realizes.</summary>
    NodeHandle ShelfViewport { get { var v = _ctl.Viewport; return v.IsNull ? _measuredVp : v; } }

    /// <summary>Whether this shelf arms the hover-elevate PARK+HOIST pair (the flagged cell + the flagged clip root). It
    /// earns its keep only for LIFT-AND-HALO cards — a single row of MediaCards that translate up on hover and blur a
    /// soft halo past their resting box, which the strip's own clip would otherwise shave. A MULTI-ROW grid is a dense
    /// CHART: nothing lifts, no halo needs headroom (which is why the multi-row cell carries no lift/shadow pad either),
    /// and arming the hoist there only buys a defect — the hovered cell escapes the viewport clip and paints over
    /// whatever sits beside the band. Measured bodies are single-row by construction, so they always qualify.</summary>
    bool HoverElevate => _measured || _rows == 1;

    public PagedShelfCore(int count, Func<int, float, Element> cardAt, Func<float, float>? cardHeight, string? title,
                          Element? header, ShelfPager pager, Func<ShelfPagerContext, Element>? customPager,
                          float minCardW, float maxCardW, float gap, int rows, int perPageOverride, float fixedCardW,
                          float headerGap, float edgeFade, string prevGlyph, string nextGlyph, TemplateParts? parts,
                          Func<int, string>? keyOf, int overscan, bool measured, bool cardWidthAgnostic, int maxColumns = 0,
                          ShelfSnap snap = ShelfSnap.None)
    {
        _count = count; _cardAt = cardAt; _cardHeight = cardHeight; _measured = measured; _title = title; _header = header;
        _pager = pager; _customPager = customPager; _minCardW = minCardW; _maxCardW = maxCardW; _gap = gap;
        _rows = Math.Max(1, rows); _perPageOverride = perPageOverride; _fixedCardW = fixedCardW;
        _headerGap = headerGap; _edgeFade = edgeFade; _prevGlyph = prevGlyph; _nextGlyph = nextGlyph;
        _parts = parts; _keyOf = keyOf; _overscan = overscan;
        _cardWidthAgnostic = cardWidthAgnostic;
        _maxColumns = Math.Max(0, maxColumns);
        _snap = snap;
        _commitPendingSnap = CommitPendingSnap;
        _pagerGoTo = GoToPage;
        _pagerPrev = () => StepPage(-1);
        _pagerNext = () => StepPage(+1);
    }

    // ── The ONE pager navigation entry point (the cached delegates + the stock chevrons/pips all route here). Bumps the
    // NAV intent even when the clamped page is unchanged — clicking ‹ at a free-scrolled fractional offset within page 0
    // re-arms the glide back to the boundary instead of silently doing nothing. Reads the LIVE grid (PageGrid, which is
    // the single page↔offset authority) rather than a captured render local, which is what lets the delegate be cached.
    void GoToPage(int to)
    {
        int maxPage = Math.Max(0, PageGrid().PageCount - 1);
        _page.Value = Math.Clamp(to, 0, maxPage);
        _pageNav.Value = _pageNav.Peek() + 1;
    }

    /// <summary>Relative navigation (±1 page) from the CLAMPED current page — the same value the header renders, so a
    /// transiently out-of-range _page (a resize that shrank the page count, before the clamp effect runs) still steps
    /// exactly one page from what the user can see.</summary>
    void StepPage(int delta)
    {
        int maxPage = Math.Max(0, PageGrid().PageCount - 1);
        GoToPage(Math.Clamp(_page.Peek(), 0, maxPage) + delta);
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

        // ── PAGE SNAP (opt-in) — the interval is the LIVE page stride, so it re-fits on every resize. That is exactly why
        // it is a SCENE write and not a declarative ScrollEl/ScrollOptions.Snap: the options record is unpacked and frozen
        // at ItemsView mount (the component-props contract), so a declaration could only ever carry the mount-time fit and
        // would be wrong for the rest of the shelf's life. The reconciler's snap patch is declaration-gated — it never
        // touches the snap fields of a non-declaring viewport — so this write survives every reconcile. Layout effect: the
        // viewport node exists only after the body realizes, and the fit is only real once the shelf has measured a width.
        UseLayoutEffect(() =>
        {
            if (_snap != ShelfSnap.Page) return;
            if (Context.Scene is not { } scene) return;
            var vp = ShelfViewport;
            if (vp.IsNull || !scene.IsLive(vp) || !scene.HasScroll(vp)) return;
            float pageW = perPageColumns * (cardW + _gap);
            ref ScrollState snapState = ref scene.ScrollRef(vp);
            ApplySnapGrid(ref snapState, pageW, snapState.ContentW - snapState.ViewportW);
            // ApplySnapGrid only writes ScrollState's own snap columns (SnapSpec.ApplyTo's contract) — the kernel body
            // caches its OWN copy of the snap grid (ScrollBody.Frame, set only by SetFrame) and a snap-only change is
            // not itself layout-affecting, so nothing else would re-post it. Without this the kernel keeps flinging
            // against whatever grid (or none) was live at the last real layout pass (scroll-v3-plan §2 kernel-side gap).
            FluentGpu.Layout.FlexLayout.RepostFrame(scene, vp);
        }, DepKey.From(HashCode.Combine(perPageColumns, cardW, _count, ShelfViewport.IsNull)));

        // ── The LIFT-DEBOUNCE timer (ShelfSnap.Page). Every gesture edge bumps _snapTick; reading it here subscribes us,
        // so the bump re-renders and this one-shot RE-ARMS from now — a resumed pan therefore pushes the pending snap out
        // instead of letting it fire into a live gesture. The callback re-validates against the LIVE scroll state before
        // it touches anything (see CommitPendingSnap). Wall-clock SCHEDULING only; the glide itself is untouched physics.
        // Unconditional (never behind `if (_snap == …)`) so the hook surface is identical for every shelf; the callback
        // no-ops when nothing is pending, which is the steady state for a free-panning shelf.
        long snapTick = _snapTick.Value;
        UseTimeout(_commitPendingSnap, SnapGraceMs, DepKey.From(snapTick));

        int nav = _pageNav.Value;   // subscribe — a nav bump re-arms the bring-into-view effect below

        // Edge fades are OFFSET-driven (the engine's scroller AutoEdgeFade), not page-derived: the strip is a real
        // scroller the user can free-pan (touchpad/tilt-wheel), and a page-derived mask goes stale the moment the
        // offset diverges from the page grid — the "left fade dead while visibly mid-scroll" bug. The engine reads the
        // LIVE ScrollState per frame, so each edge fades exactly when content extends past it.
        // `fade` is only the ON/OFF bit; the caller's DIP value rides alongside it as AutoEdgeFadeBand at each viewport
        // (a shelf whose trailing cell must stay crisp asks for a narrow band and now actually gets one).
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
            && (measuredHLock <= 0f || MathF.Abs(_measuredForCardW.Value - cardW) > MeasureTolerance);
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
                // Already locked on this measurement for this cardW ⇒ write NOTHING. The signals' tolerance comparer
                // would coalesce these writes anyway; returning first also skips the two BackwardsWriteGuard checks and
                // keeps the "probe wrote" intent readable — the loop this cuts is measure→write→re-render→re-probe.
                if (MathF.Abs(_measuredH.Peek() - maxH) <= MeasureTolerance
                    && MathF.Abs(_measuredForCardW.Peek() - cardW) <= MeasureTolerance) return;
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

        // Bring-into-view: page NAV *and* FIT CHANGE. The fit belongs in the dep because a resize that breaks a column
        // (3 → 2) moves every page boundary: the offset that was page 2's boundary is now mid-page, and without a re-seat
        // the strip rests off the new grid until the user scrolls again. Only a NAV animates (nav != _lastVirtualNav) —
        // a re-fit is a correction, not a navigation, so it snaps the offset onto the new grid with no glide.
        UseLayoutEffect(() =>
        {
            if (measuredRealizeAll || needProbe) return;
            bool animate = nav != _lastVirtualNav;
            _lastVirtualNav = nav;
            if (w > 1f) _ctl.StartBringItemIntoView(_page.Peek() * perPageItems, 0f, animate);
        }, DepKey.From(HashCode.Combine(nav, needProbe, perPageColumns, cardW)));

        Element body = _measured
            ? (measuredRealizeAll
                ? MeasuredBody(perPageColumns, cardW, fade, viewport)
                : MeasuredVirtualBody(perPageItems, cardW, fade, needProbe))
            : VirtualBody(perPageItems, cardW, fade);
        if ((_pager & ShelfPager.HoverEdge) != 0)
            body = ZStack(body, new BoxEl
            {
                Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
                Children = [ EdgeButton(true, canPrev, _pagerPrev), EdgeButton(false, canNext, _pagerNext) ],
            });

        // ── header (title + chevrons/pips/custom) ────────────────────────────────────────────────────────
        Element? headerEl = BuildHeader(p, pageCount, canPrev, canNext);

        Element[] children = headerEl is null ? [ body ] : [ headerEl, body ];
        return _parts.Apply(PagedShelf.PartRoot, new BoxEl
        {
            // LiftClearance of the header gap lives INSIDE the strip's clip (the item container's top pad), so the
            // header→card distance on screen stays _headerGap while the clip gains hover-lift headroom — but only where
            // that pad is actually re-added (measured bodies and the single-row virtual strip). The multi-row grid keeps
            // its tight clip with NO top pad, so its header gap must not spend clearance it never gets back.
            Direction = 1, Gap = _measured || _rows == 1 ? MathF.Max(0f, _headerGap - LiftClearance) : _headerGap,
            // No explicit width: the parent sizes us, so OnBoundsChanged reports the real available width (which the
            // strip's viewport then fills → FillRowVirtualLayout fits the same cardW).
            OnBoundsChanged = r => { if (r.W > 0f && MathF.Abs(r.W - _w.Peek()) > 0.5f) _w.Value = r.W; },
            Children = children,
        });
    }

    // ── The settled-offset → page re-sync: the strip is a real scroller, so ANY scroll source (chevron glide, touchpad
    // pan, tilt-wheel) can move it — the page state must follow the truth or the chevrons/pips (and anything derived
    // from them) go stale. Change-only (the long projection) and settle-only (a mid-glide write would retarget the
    // glide it's reporting on). The re-sync writes _page but NOT _pageNav, so it never re-arms a bring-into-view.
    //
    // The CHANGE-DETECTION bit is UserScrollActive, NOT ScrollFlags.MovingNowBit: ScrollFlags is computed only for
    // viewports that own a ScrollBind row (ScrollBindEval.ApplyPinAndFlagPass), and a shelf viewport owns none — so the bit
    // read 0 on every frame and a gate written against it was inert. UserScrollActive is maintained per-tick for every
    // armed viewport and is false on the settle tick, and RunObservers runs after the integrator, so both gesture edges are
    // observable here. It is NOT the settle GATE, though: it is a per-frame MOTION bit that goes false during any
    // micro-pause of a live pan (see the phase gate in the action — that is what "settled" means).
    //
    // The projection also carries a COARSE OFFSET term. Keyed on the rounded page alone, a settle that does not change
    // the page produces no key change and the change-only observer never fires — which is precisely the wheel notch
    // shorter than half a stride, the case ShelfSnap.Page exists to fix. Quantized so a sub-pixel remainder cannot
    // pulse it, and ReSnapSettled is idempotent within SettleSnapEpsPx, so the extra fires cost nothing. ──
    (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action) PageScrollSync() =>
    (
        g => ((long)PageFromOffset(g.OffsetX) << 21)
             | (Math.Clamp((long)(MathF.Max(0f, g.OffsetX) / PageKeyQuantumPx), 0L, PageKeyQuantumCap) << 1)
             | (g.UserScrollActive ? 1L : 0L),
        g =>
        {
            // The LIVE activity is what "settled" means (see below), so read it once up front — both branches need it.
            if (Context.Scene is not { } scene) return;
            var vp = ShelfViewport;
            if (vp.IsNull || !scene.IsLive(vp) || !scene.HasScroll(vp)) return;
            ScrollActivity activity;
            {
                ref ScrollState liveState = ref scene.ScrollRef(vp);
                activity = liveState.Activity;
            }

            // ── (1) LIVE GESTURE. UserScrollActive keeps its change-detection role (it is in the key, so BOTH edges of a
            // gesture fire this action), and its RISING edge is where the directional commit's anchor is latched: the
            // offset the gesture started from, which is the only thing that can tell a forward flick from a backward one.
            // A micro-pause mid-pan drops this bit but NOT the activity, so the anchor can never be re-latched mid-gesture
            // (gate 2 below returns first and leaves _userScrollWas set) — it stays the true gesture start.
            if (g.UserScrollActive)
            {
                if (!_userScrollWas)
                {
                    _userScrollWas = true;
                    // CONTACT pans only (ScrollActivity.Drag — overscroll is now a property (Band ≠ 0) of that same
                    // activity, not a separate phase). A mouse-wheel notch is ALSO user scroll (Driven|Wheel), but it is a
                    // DISCRETE request with no release velocity and its contract is the plain nearest-boundary re-snap —
                    // anchoring it would turn a sub-half-stride notch into a page advance. No anchor ⇒ the nearest rule,
                    // unchanged.
                    if (activity == ScrollActivity.Drag)
                        _gestureAnchorX = g.OffsetX;
                    // A gesture RESUMING on top of an armed snap pushes its deadline out (scheduling only — see
                    // SnapGraceMs). Bumped only when something is actually pending, so a normal pan costs no re-render.
                    if (!float.IsNaN(_pendingSnapTarget)) _snapTick.Value = _snapTick.Peek() + 1;
                }
                return;
            }

            // ── (2) THE ACTIVITY GATE — the root fix. UserScrollActive is a per-frame MOTION bit: the resampler clamps
            // at the newest sample during any micro-pause of a live two-finger pan, so no offset is written, movingNow
            // goes false, and this bit reads false ~14–20 ms into the pause WHILE Activity is still Drag. Acting there
            // re-snapped INTO the live gesture and killed it. Activity == Idle is reached ONLY through a real gesture end
            // or a settled fling/glide, so this one test subsumes Drag / Ballistic / Driven (Wheel or Programmatic) — a
            // chase mid-flight is not a rest either, and re-snapping there glides back where it came from.
            if (activity != ScrollActivity.Idle) return;

            // ── (3) A REAL REST. Consume the gesture anchor (a wheel notch / keyboard / chevron re-arm has none, which is
            // what degenerates the commit below to today's plain nearest rule) and let the commit decide the page.
            _userScrollWas = false;
            float anchorX = _gestureAnchorX;
            _gestureAnchorX = float.NaN;
            int page = ReSnapSettled(in g, PageFromOffset(g.OffsetX), anchorX);
            if (page != _page.Peek()) _page.Value = page;
        }
    );

    // ── The page-mandatory snap grid for a viewport: interval = the live page stride, BOUNDED at the last WHOLE page
    // boundary. Open-ended, the repeated-snap zone keeps emitting multiples past the content clamp, so a fling into a
    // PARTIAL last page (an odd trailing column) retargets to a boundary the offset can never reach and lands at the clamp
    // — off-grid, and every later flip then starts from a fractional offset. SnapEnd ≤ SnapStart means "open", which is
    // also the only honest value while maxX is still 0 (the strip has not published its extent yet).
    //
    // TWO writers, deliberately: the fit-keyed layout effect (the interval must track the live fit, which is why this is a
    // scene write and not a frozen ScrollOptions.Snap declaration), and the settle path — the first moment the published
    // content extent is guaranteed real, since the fit dep cannot observe an extent that lands a frame later. Idempotent
    // and alloc-free, so re-asserting on every settle costs nothing. Snap columns are CONFIGURATION, never the offset:
    // the phase-7 integrator remains the single writer of ScrollState.Offset*.
    static void ApplySnapGrid(ref ScrollState sc, float pageW, float maxX)
    {
        bool paged = pageW > 1f;
        float end = paged ? MathF.Floor(MathF.Max(0f, maxX) / pageW) * pageW : 0f;
        SnapSpec.Every(paged ? pageW : 0f, start: 0f, end: end).ApplyTo(ref sc);
    }

    // ── The LIVE page grid at the current fit: columns per page, the page stride in OFFSET space, and the page count.
    // Every page↔offset conversion goes through this one place, so the pager, the settled-offset re-sync, the snap interval
    // and the re-snap target can never disagree. Reads _w.Peek() (never subscribes): the callers are effects and scroll
    // callbacks whose closures freeze at ItemsView mount, so they must NOT capture a render-time fit.
    (int Cols, float PageW, int PageCount) PageGrid()
    {
        var (cols, cw) = FillRowVirtualLayout.Fit(_w.Peek(), _minCardW, _maxCardW, _gap, _perPageOverride, _fixedCardW, _maxColumns);
        int perPage = Math.Max(1, cols * (_measured ? 1 : _rows));
        return (cols, cols * (cw + _gap), Math.Max(1, (_count + perPage - 1) / perPage));
    }

    // The page the settled offset actually shows. Mirrors the glide target math: page ⇒ page·cols·stride px.
    int PageFromOffset(float offX)
    {
        var grid = PageGrid();
        if (grid.PageW <= 1f) return 0;
        return Math.Clamp((int)MathF.Round(offX / grid.PageW), 0, grid.PageCount - 1);
    }

    // ── POST-SETTLE re-snap + page COMMIT (ShelfSnap.Page only). The engine snaps FLINGS ONLY — a wheel notch, a tilt
    // wheel and a keyboard page are hard-clamped by contract and never snapped — so a detented wheel leaves the strip
    // resting mid-page. This closes that gap in the CONTROL layer instead of relaxing the engine rule: on the settle edge
    // (the caller's gate) with a fractional offset, STASH the boundary to glide to and let the lift-debounce arm the same
    // programmatic path the chevrons use. Idempotent: the glide's own settle re-enters here already on the boundary and
    // finds nothing to do. A touch/touchpad fling needs none of this — SnapInterval IS the page stride, so its retargeted
    // decay already lands here exactly.
    //
    // Returns the page the settle COMMITS to. With no gesture anchor (<paramref name="anchorX"/> NaN — a wheel notch, a
    // keyboard page, a chevron re-arm, the glide's own settle) that is byte-identically <paramref name="nearestPage"/> and
    // the whole directional block below is skipped: this path must stay exactly what it always was.
    int ReSnapSettled(in ScrollGeometry g, int nearestPage, float anchorX)
    {
        if (_snap != ShelfSnap.Page) return nearestPage;
        if (Context.Scene is not { } scene) return nearestPage;
        var vp = ShelfViewport;
        if (vp.IsNull || !scene.IsLive(vp) || !scene.HasScroll(vp)) return nearestPage;
        var grid = PageGrid();
        if (grid.PageW <= 1f) return nearestPage;
        float maxX = MathF.Max(0f, g.ContentW - g.ViewportW);
        // Re-assert the bounded grid from the PUBLISHED extent (see ApplySnapGrid): the fit-keyed layout effect can only
        // read whatever ContentW existed at that fit, and on a fresh mount that is 0 — which leaves the grid open-ended
        // for the rest of the shelf's life unless the fit happens to change again.
        ApplySnapGrid(ref scene.ScrollRef(vp), grid.PageW, maxX);
        // Same kernel-side gap as the fit-keyed layout effect above: a scene-column-only write is invisible to the
        // kernel's cached Frame until a real layout pass reposts it, so a later fling would retarget onto the STALE
        // (open-ended, or pre-bound) grid without this.
        FluentGpu.Layout.FlexLayout.RepostFrame(scene, vp);

        // Already PARKED — on the nearest boundary, or at the content end. Nothing to re-snap, whatever the commit rule
        // below would have chosen. This is the SettleSnapEpsPx idempotence that makes re-entering on every settle free
        // (the glide's own settle lands here), and it is also what keeps the ±1 rail from pulling back a multi-page snap
        // FLING the engine legitimately carried: a snapped fling rests exactly on a boundary, so it exits here.
        // The content END is a legitimate rest, but ONLY when the strip is ALREADY parked there. A partial last page (an
        // odd trailing column) puts maxX at a fractional grid position, so letting "whichever of the two is nearer" pick
        // it parks the strip half a page off-grid — and every later flip then starts from that fractional offset. The GRID
        // wins the choice; the end stays reachable because a wheel/fling into it is hard-clamped to maxX exactly, which
        // lands inside this deadband.
        float nearestBoundary = Math.Clamp(nearestPage * grid.PageW, 0f, maxX);
        if (MathF.Abs(g.OffsetX - nearestBoundary) <= SettleSnapEpsPx
            || MathF.Abs(g.OffsetX - maxX) <= SettleSnapEpsPx) return nearestPage;

        int page = CommitPage(g.OffsetX, anchorX, scene.ScrollRef(vp).LastReleaseVelocity,
                              grid.PageW, grid.PageCount, nearestPage);

        // page·stride clamped to maxX is itself the last whole boundary whenever the committed page's boundary lies past
        // the clamp (the bounded-grid rule above, in target form).
        float target = Math.Clamp(page * grid.PageW, 0f, maxX);
        if (MathF.Abs(g.OffsetX - target) <= SettleSnapEpsPx) return page;
        // STASH, don't glide: the lift-debounce owns the arming instant (see SnapGraceMs + CommitPendingSnap). The bump is
        // what re-arms the grace timer; _pendingSnapTarget must be written first so a fire can never read a stale target.
        _pendingSnapTarget = target;
        _snapTick.Value = _snapTick.Peek() + 1;
        return page;
    }

    /// <summary>The page a settled gesture COMMITS to — the FlipView MandatorySingle rule ported to offset space, as pure
    /// math (so the gates can pin it without a dispatcher, and so there is exactly one copy of it).
    /// <para>A pan is answered by "how far did you get from the page you STARTED on, projected forward by how fast you let
    /// go" — never by "which boundary is closest NOW". The nearest rule is what made a touchpad pan feel broken: on a real
    /// page (≈612 DIP on the artist chart) 50% is unreachable by panning, so every pan was yanked back to its start page.
    /// <paramref name="releaseVelocity"/> is <see cref="ScrollState.LastReleaseVelocity"/> (px/s, signed in offset space,
    /// recorded at lift; 0 for an OS-momentum gesture — which degenerates this to a pure "did the finger physically pass
    /// <see cref="CommitFraction"/>" rule), projected over the bounded settle window by the shared kernel divisor
    /// <c>ScrollFeel.Shipping.FlickProjectK</c>. The result is RAILED to ±1 page: one gesture never skips a page.</para>
    /// <para><paramref name="anchorX"/> NaN = no gesture anchor (a wheel notch, a keyboard page, a chevron re-arm, a
    /// glide's own settle) ⇒ returns <paramref name="nearestPage"/> verbatim. That is the degenerate contract this whole
    /// feature rests on: every non-gesture settle keeps exactly the behaviour it had before the directional commit.</para></summary>
    internal static int CommitPage(float offsetX, float anchorX, float releaseVelocity,
                                  float pageW, int pageCount, int nearestPage)
    {
        if (float.IsNaN(anchorX) || pageW <= 1f) return nearestPage;
        int maxPage = Math.Max(0, pageCount - 1);
        // The anchor PAGE, not the raw anchor offset: "the page this gesture started on". Rounding is what makes a pan the
        // OS split into several ScrollBegin/End segments still accumulate correctly — each segment measures progress from
        // the page it is basically on, so two 40% segments still commit one page forward.
        int anchorPage = Math.Clamp((int)MathF.Round(anchorX / pageW), 0, maxPage);
        float projected = offsetX + releaseVelocity / ScrollFeel.Shipping.FlickProjectK;
        float progress = (projected - anchorPage * pageW) / pageW;
        // The step must also agree with the gesture's NET TRAVEL. Without this, a tiny nudge that ends just past a page
        // midpoint (so the anchor page rounded UP) reads as "0.4 pages backwards from the anchor page" and would commit
        // BACKWARD even though the finger went nowhere — the one place rounding the anchor bites.
        float travel = projected - anchorX;
        int dir = progress > 0f ? 1 : -1;
        int step = MathF.Abs(progress) >= CommitFraction && travel * dir > 0f ? dir : 0;
        return Math.Clamp(anchorPage + step, 0, maxPage);
    }

    // ── The debounced commit: the ONE place a settled shelf reaches the programmatic seam. Runs on the host timer queue
    // SnapGraceMs after the last gesture edge, and re-validates everything, because the world may have moved on: the user
    // may have resumed panning (Activity back to Drag), a chevron may have armed its own glide (Activity Driven), the
    // body may have swapped viewports, or the strip may already be on the target. Reduced motion is read as a VALUE at
    // seed (a direct write instead of a glide), never as a branch in the authoring path.
    void CommitPendingSnap()
    {
        float target = _pendingSnapTarget;
        _pendingSnapTarget = float.NaN;   // one-shot: a fire consumes the intent whether or not it survives validation
        if (float.IsNaN(target) || _snap != ShelfSnap.Page) return;
        if (Context.Scene is not { } scene) return;
        var vp = ShelfViewport;
        if (vp.IsNull || !scene.IsLive(vp) || !scene.HasScroll(vp)) return;
        // Copy the fields out before the call: ScrollTo takes its own ref, and holding one across it would alias.
        ScrollActivity activity;
        float offset;
        {
            ref ScrollState sc = ref scene.ScrollRef(vp);
            activity = sc.Activity; offset = sc.OffsetX;
        }
        if (activity != ScrollActivity.Idle) return;                          // a gesture/chase owns the offset again
        if (MathF.Abs(offset - target) <= SettleSnapEpsPx) return;             // already there (idempotent)
        ScrollIntoView.ScrollTo(Context, vp, target, animate: !Motion.ReducedMotion);
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
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.None,
                Controller = _ctl,
                Overscan = shelfOverscan,
                KeyOf = _keyOf,
                Grow = 1f,
                Scroll = new ScrollOptions { SuppressScrollBar = true, AutoEdgeFade = fade, AutoEdgeFadeBand = _edgeFade, OnScrollGeometryChanged = PageScrollSync() },
                // Bottom padding absorbs the shadow clearance: FillRowVirtualLayout stretches this container to the full
                // viewport cross size (measuredH + clearance), and the card's Grow=1 fills the container's CONTENT box — so
                // the card stays measuredH tall and its shadow renders into the pad below both clip edges.
                // HoverElevatePaint on the CELL (not just the card inside): the deferral is a direct-sibling mechanism, and
                // at the strip level the siblings are these cells — the flag makes the cell hover-within-aware (see
                // InputDispatcher.UpdateHoverWithin), so the hovered card's cell paints above its neighbors' halo-overlap.
                ContainerFactory = (i, content, state, onInteraction, onFocusChanged) =>
                    new BoxEl { Direction = 1, Padding = new Edges4(0f, LiftClearance, 0f, ShadowClearance), HoverElevatePaint = HoverElevate, Children = [content] },
            });

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
                // PAIRED with the cell flag above: park and hoist arm together or not at all (see HoverElevate).
                HoverElevateClipRoot = HoverElevate,
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
            cells[i] = new BoxEl { Direction = 1, Width = cardW, HoverElevatePaint = HoverElevate, Children = [ _cardAt(idx, cardW) ] };
        }
        // Top/bottom padding sits the content ScrollEl's clip edges beyond the card's lift + shadow; it is OUTSIDE the
        // row's cross stretch, so cells still stretch to the tallest CARD (the pads do not inflate card height). L/R
        // HaloBleed padding is the MAIN-AXIS content gutter (the non-virtual sibling of the FillRowVirtualLayout insets):
        // cards sit HaloBleed inside the scroll content so the first/last card's elevation halo has room, while the
        // ScrollEl's negative horizontal margin widens the clip 2×HaloBleed into the surrounding gutters (rest positions
        // cancel: gutter +Bleed inside a viewport shifted −Bleed). Page targets anchor to page·cols·stride, so they cancel.
        Element strip = new BoxEl { Direction = 0, Gap = _gap, Padding = new Edges4(HaloBleed, LiftClearance, HaloBleed, ShadowClearance), Children = cells };
        // scroll-v3 §7.1: ScrollEl's own HoverElevateClipRoot is deleted (the escape-root flag now lives only on
        // BoxEl). The scroller still owns the clip + edge-fade scope the hovered cell must escape, so a thin
        // Direction=1 wrapper carries the flag instead — Direction=1 (a COLUMN) avoids the single-child wrapper's
        // main-axis shrink-to-content collapse (fluentgpu skill rule #11: a default ROW wrapper would shrink this
        // Grow=0 viewport to width 0), while still handing the ScrollEl the SAME cross-axis (width) stretch and
        // main-axis (height) auto-size it got as a direct column child before. The ScrollEl's own negative Margin
        // keeps widening ITS box (unchanged) — the wrapper only adds the escape-root paint-order flag.
        var scroller = _parts.Apply(PagedShelf.PartViewport, new ScrollEl
        {
            Horizontal = true,
            Grow = 0f,
            SuppressScrollBar = true,
            AutoEdgeFade = fade,
            AutoEdgeFadeBand = _edgeFade,
            Margin = new Edges4(-HaloBleed, 0f, -HaloBleed, 0f),
            OnScrollGeometryChanged = PageScrollSync(),
            Content = strip,
            // Both sinks: the Ref is what the page-glide effect already reads; the FIELD backs ShelfViewport, which the
            // snap-interval write and the post-settle re-snap use so neither has to know which body is mounted (a stale
            // handle after a body swap simply fails the IsLive guard).
            OnRealized = h => { viewport.Value = h; _measuredVp = h; },
        });
        return new BoxEl
        {
            Direction = 1,
            // PAIRED with the cell flag in MeasuredBody: park and hoist arm together or not at all (see HoverElevate).
            HoverElevateClipRoot = HoverElevate,
            Children = [ scroller ],
        };
    }

    void ScrollMeasuredViewport(NodeHandle vp, int page, int perPageColumns, float cardW, bool animate)
    {
        if (Context.Scene is not { } scene || vp.IsNull || !scene.IsLive(vp) || !scene.HasScroll(vp)) return;

        ref ScrollState sc = ref scene.ScrollRef(vp);
        float stride = cardW + _gap;
        float maxX = MathF.Max(0f, sc.ContentW - sc.ViewportW);
        float target = Math.Clamp(page * Math.Max(1, perPageColumns) * stride, 0f, maxX);
        // Already at rest on this target, or already chasing it ⇒ don't re-arm. Kept HERE rather than delegated:
        // ScrollTo only compares against the live offset, so mid-glide it would re-post every effect re-fire and keep
        // re-latching the half-life. The kernel's live Target isn't a readable SceneStore column any more
        // (ScrollState.TargetX is deleted, kernel-internal only) — _lastProgrammaticTargetX mirrors the last posted
        // destination locally instead, self-clearing the moment the body isn't Driven (settled, or a user gesture
        // took the offset back).
        if (sc.Activity != ScrollActivity.Driven) _lastProgrammaticTargetX = null;
        bool alreadyChasingIt = _lastProgrammaticTargetX is { } lastTarget && MathF.Abs(lastTarget - target) < 0.5f;
        if (alreadyChasingIt || MathF.Abs(sc.OffsetX - target) < 0.5f) return;

        // The ONE programmatic seam (ScrollIntoView): animate ⇒ posts a Driven chase to the kernel (distance-derived
        // half-life, velocity-continuous retarget); reduced motion / !animate ⇒ its immediate path posts a snap that
        // arrests any in-flight chase. Reduced motion is read as a VALUE at seed, never a branch in the authoring path.
        _lastProgrammaticTargetX = target;
        ScrollIntoView.ScrollTo(Context, vp, target, animate && !Motion.ReducedMotion);
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
        // FillRowVirtualLayout.Window measures Overscan in COLUMNS (firstCol -= overscan), not items — passing items on
        // a multi-row grid realizes rows× too much (5 rows ⇒ the whole chart resident on both sides of the window).
        int shelfOverscan = Math.Max(_overscan, Math.Max(1, perPageItems / _rows));
        Element items = ItemsView.Create(
            _count,
            i => _cardAt(i, _cardWidthAgnostic
                ? _maxCardW
                : (_cardW.Value > 0f ? _cardW.Value : layout.CardW)),
            RepeatLayout.Custom(layout, horizontal: true),
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.None,
                Controller = _ctl,
                Overscan = shelfOverscan,
                KeyOf = _keyOf,
                Grow = 1f,
                // paged: navigate by the chevron/pips pager, not a draggable scrollbar
                Scroll = new ScrollOptions { SuppressScrollBar = true, AutoEdgeFade = fade, AutoEdgeFadeBand = _edgeFade, OnScrollGeometryChanged = PageScrollSync() },
                // bare passthrough cell, COLUMN so the card cross-stretches to the cell's live width (fills it even mid-resize);
                // the card carries its own visuals (no ItemContainer selection chrome around it). Single-row only: a bottom
                // pad absorbs the card's shadow clearance (card stays shelfH, halo paints into the pad below the clip).
                // Multi-row keeps the old clip — RowHeight(cross) would spread the pad across rows and distort every card,
                // and interior rows occlude their own shadows against the row below anyway.
                ContainerFactory = (i, content, state, onInteraction, onFocusChanged) =>
                    new BoxEl { Direction = 1, Padding = _rows == 1 ? new Edges4(0f, LiftClearance, 0f, ShadowClearance) : default, HoverElevatePaint = HoverElevate, Children = [content] },
            });

        float vpH = shelfH > 0f ? (_rows == 1 ? shelfH + ShadowClearance + LiftClearance : shelfH) : float.NaN;
        return _parts.Apply(PagedShelf.PartViewport, new BoxEl
        {
            Height = vpH,
            MinHeight = vpH,
            ClipToBounds = true,
            // Clip-ESCAPE root: the hover-elevated cell hoists out of this clip + the inner scroller's edge fade — its
            // lift/halo paint into the page while resting content stays exactly clipped. SINGLE-ROW ONLY (HoverElevate):
            // a multi-row grid has no lift and no halo to make room for, so the hoist would only let the hovered row
            // paint outside the band. PAIRED with the cell flag in the ContainerFactory above.
            HoverElevateClipRoot = HoverElevate,
            // Widen the clip 2×HaloBleed into the surrounding gutters WITHOUT moving the shelf's layout box: a negative
            // horizontal margin on a cross-STRETCH child resolves to width = availCross − crossMargin (= _w + 2·Bleed)
            // at x = −Bleed (FlexLayout arrange). The ItemsView (grow:1) fills it, so SetViewport is fed _w+2·Bleed and
            // the layout subtracts the gutters back to _w for the fit — cards keep their width and rest positions.
            Margin = new Edges4(-HaloBleed, 0f, -HaloBleed, 0f),
            Children = [ items ],
        });
    }

    Element? BuildHeader(int p, int pageCount, bool canPrev, bool canNext)
    {
        Element? titleEl = _header
            ?? (_title is null ? null : _parts.Apply(PagedShelf.PartHeader, new BoxEl { Children = [ Heading(_title) ] }));

        var row = new List<Element>(4);
        if (titleEl is not null) row.Add(titleEl);
        row.Add(new BoxEl { Grow = 1f });   // spacer pushes the pager to the trailing edge

        if (_customPager is not null)
            // CACHED delegates (see the fields): the three action slots must be REFERENCE-STABLE across renders or a
            // custom pager that packs this context into a props record re-renders its whole subtree every shelf render.
            // Only the four VALUE slots (page/count/canPrev/canNext) change, which is exactly what should re-render it.
            row.Add(_customPager(new ShelfPagerContext(p, pageCount, canPrev, canNext,
                _pagerPrev, _pagerNext, _pagerGoTo)));
        else
        {
            if ((_pager & ShelfPager.Pips) != 0 && pageCount > 1)
                // Pass the page signal directly; onChange re-arms the bring-into-view glide (GoToPage's _pageNav bump).
                // onReselect closes the same-index hole: a pip click that does NOT change the page is swallowed by the
                // pager's value channel (WinUI semantics), yet after a partial pan the strip rests BETWEEN pages while the
                // pip still reads that page — the re-click is the request to be put back on the boundary, and GoToPage's
                // unconditional _pageNav bump is exactly the re-arm that does it.
                // Both channels take the CACHED Action<int> (see the fields) — stable instances, so the pips' own props
                // channel short-circuits on a shelf render that changed nothing about the pager.
                row.Add(PipsPager.Create(pageCount, _page, onChange: _pagerGoTo, onReselect: _pagerGoTo));
            if ((_pager & ShelfPager.Chevrons) != 0)
            {
                row.Add(Chevron(_prevGlyph, canPrev, _pagerPrev, PagedShelf.PartChevronPrev));
                row.Add(Chevron(_nextGlyph, canNext, _pagerNext, PagedShelf.PartChevronNext));
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
