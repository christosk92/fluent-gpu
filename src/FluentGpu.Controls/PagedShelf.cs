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

// The two PURE decisions this control's card-mount budget rests on — the progressive probe progression
// (ShelfProbeMath) and the viewport band (ShelfViewportBand) — live in ShelfProbeMath.cs, System-only, so a headless
// test compiles them without the engine (the SortableMath / SplitterMath / ToastCoalescing pattern).

/// <summary>Non-generic cache for the probe cells' keys. A <c>static</c> inside <c>PagedShelfCore&lt;T&gt;</c> would be
/// one array per closed generic type; these strings are identical for every shelf, and building them per render per
/// cell was a string concat on the measured shelf's hot path.</summary>
static class ShelfProbeKeys
{
    static readonly string[] Keys = Build();

    static string[] Build()
    {
        var keys = new string[ShelfProbeMath.SampleCap];
        for (int i = 0; i < keys.Length; i++)
            keys[i] = "mshelf-probe:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return keys;
    }

    internal static string Of(int index)
        => (uint)index < (uint)Keys.Length
            ? Keys[index]
            : "mshelf-probe:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
    /// The immutable item snapshot and chrome are re-pushed to the retained shelf; metadata changes preserve its pager and rows.</summary>
    public static Element Create<T>(
        IReadOnlyList<T> items,
        Func<T, int, float, Element> cardAt,
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
        Func<T, int, string>? keyOf = null,
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
        ShelfSnap snap = ShelfSnap.None,
        int maxItems = int.MaxValue,
        Action<int, int>? onVisibleRange = null)
        => Embed.Comp(new ShelfProps<T>(items, cardAt, title, header, customPager, keyOf, maxItems, onVisibleRange),
                      () => new PagedShelfCore<T>(cardHeight, pager,
                                               minCardW, maxCardW, gap, rows, perPageOverride, fixedCardW,
                                               headerGap, edgeFade, prevGlyph, nextGlyph, parts, overscan, measured,
                                               cardWidthAgnostic, maxColumns, snap))
           // SkeletonProxy: the deriver can't see into this component, so hand it the header + a few real cards to derive
           // — the shelf shimmers as real cards instead of one default bar. The cards are fitted to the MEASURED slot
           // exactly as the live strip fits them (the same Fit, through Responsive's rendered-output proxy idiom): handing
           // them maxCardW made a shelf with an uncapped max ("let maxColumns decide") shimmer one sentinel-wide card,
           // which reflowed into the real columns the moment the data landed.
           with
           {
               SkeletonProxy = () => Embed.Comp(new ResponsiveBox.Props(
                       w => ShelfProxy(items, cardAt, header, title,
                           FillRowVirtualLayout.Fit(w, minCardW, maxCardW, gap, perPageOverride, fixedCardW, maxColumns).CardW,
                           gap, headerGap, maxItems), 0f, 0f),
                   static () => new ResponsiveBox()) with { DeriveRenderedOutput = true },
           };

    static Element ShelfProxy<T>(IReadOnlyList<T> items, Func<T, int, float, Element> cardAt, Element? header, string? title, float cardW, float gap, float headerGap, int maxItems)
    {
        int n = Math.Clamp(Math.Min(items.Count, maxItems), 0, 6);
        var cards = new Element[n];
        for (int i = 0; i < n; i++) cards[i] = cardAt(items[i], i, cardW);
        Element head = header ?? (title is { Length: > 0 } t ? new TextEl(t) { Size = 20f, Weight = 700 } : new BoxEl());
        return new BoxEl
        {
            Direction = 1, Gap = headerGap,
            Children = [head, new BoxEl { Direction = 0, Gap = gap, ClipToBounds = true, Children = cards }],
        };
    }
}

/// <summary>The re-pushed shelf props. <b>Equality IS the reconciler's re-render gate</b> (the props signal coalesces an
/// equal write), so it is defined on DATA ONLY — see <c>docs/design/subsystems/component-props-contract.md</c>
/// "Retained shelf authoring":
/// <list type="bullet">
/// <item><description><see cref="Items"/> — reference first, else the CLAMPED prefix (<c>min(Count, MaxItems)</c>)
/// element-by-element through <c>EqualityComparer&lt;T&gt;.Default</c>. Immutable domain records therefore compare by
/// VALUE, so a parent that rebuilds its array on every publication still gates. Items past <see cref="MaxItems"/> are
/// never rendered and are never compared.</description></item>
/// <item><description><see cref="MaxItems"/> — by value (it changes what is rendered).</description></item>
/// <item><description><see cref="CardAt"/>, <see cref="KeyOf"/>, <see cref="CustomPager"/>,
/// <see cref="OnVisibleRange"/> — <b>IGNORED</b>. A fresh closure over the same lambda is allocated on every parent
/// render and can never compare equal, so gating on one means never gating at all. The contract that buys this: <b>what
/// a card renders must be a function of its item</b> (plus stable behaviour the closure captures — a navigate/play
/// callback). State the card PAINTS ("saved", "playing") belongs IN the item, or on a signal the card itself reads —
/// never captured by the closure. The shelf always invokes the NEWEST delegates the parent pushed (see
/// <c>PagedShelfCore._latest</c>); a delegate change alone schedules no render, and a changed delegate <i>Method</i>
/// trips a DEBUG <c>ReuseGuard</c> note.</description></item>
/// <item><description><see cref="Title"/>, <see cref="Header"/> — chrome, NOT part of this gate. They ride
/// <see cref="ShelfChrome"/> on a separate signal, so a rebuilt header re-renders the shelf's own header row WITHOUT
/// rebuilding a single card.</description></item>
/// </list></summary>
internal sealed record ShelfProps<T>(IReadOnlyList<T> Items, Func<T, int, float, Element> CardAt,
    string? Title, Element? Header, Func<ShelfPagerContext, Element>? CustomPager,
    Func<T, int, string>? KeyOf, int MaxItems, Action<int, int>? OnVisibleRange)
{
    /// <summary>The number of items this shelf actually renders (the <see cref="MaxItems"/> clamp).</summary>
    internal int VisibleCount => Math.Min(Items.Count, Math.Max(0, MaxItems));

    public bool Equals(ShelfProps<T>? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || MaxItems != other.MaxItems) return false;
        if (ReferenceEquals(Items, other.Items)) return true;
        int n = VisibleCount;
        if (n != other.VisibleCount) return false;
        var cmp = EqualityComparer<T>.Default;
        for (int i = 0; i < n; i++) if (!cmp.Equals(Items[i], other.Items[i])) return false;
        return true;
    }

    // Count + MaxItems only: a hash must agree with Equals above and the per-element walk is the expensive half. These
    // props are never hashed on a hot path (Signal<T> only ever calls Equals) — this exists so the pair stays legal.
    public override int GetHashCode() => HashCode.Combine(VisibleCount, MaxItems);
}

/// <summary>The shelf's own chrome, carried on a SEPARATE signal from <see cref="ShelfProps{T}"/> so that a caller who
/// rebuilds its <c>header:</c> Element on every render re-renders the shelf's header row only — never its cards.
/// <para><see cref="Header"/> is compared by REFERENCE on purpose: an <c>Element</c> is a record whose <c>Children</c>
/// array compares by reference, so a rebuilt-but-identical header can never test equal by value, and a deep compare
/// would be both costly and wrong (it would have to compare delegates). A caller who wants the shelf gated end-to-end
/// passes <c>title:</c> (compared by value) or hands a stable header instance.</para></summary>
internal readonly record struct ShelfChrome(string? Title, Element? Header, bool HasCustomPager)
{
    public bool Equals(ShelfChrome other)
        => Title == other.Title && ReferenceEquals(Header, other.Header) && HasCustomPager == other.HasCustomPager;
    public override int GetHashCode() => HashCode.Combine(Title, Header is null, HasCustomPager);
}

/// <summary>The stateful core (self-measure → fit → virtualized strip + animated pager). See <see cref="PagedShelf"/>.</summary>

internal sealed class PagedShelfCore<T> : Component, IPropsHost
{
    // The sample size + per-pass budget live in ShelfProbeMath (pure, unit-tested). This alias keeps the array
    // dimensions and the existing call sites readable.
    const int MeasuredSampleCap = ShelfProbeMath.SampleCap;
    // Delay before a probe CONTINUATION widens the sample prefix. Wall-clock SCHEDULING only — it moves WHEN the next
    // chunk of cells mounts, never what is measured (see ShelfProbeMath). One host tick is enough to land the work in a
    // later frame; anything longer only makes a card-heavy page resolve its shelf heights more slowly.
    const float ProbeChunkMs = 1f;
    // A mounted sample that reports no bounds yet (nothing measurable) is retried a bounded number of times rather than
    // stalling the progression forever. In practice layout effects run AFTER layout (frame phase 6.5), so a freshly
    // realized cell already has bounds and this never fires.
    const int MaxProbeRetries = 8;
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

    // THE DATA signal — written only when ShelfProps' data gate says the items/cap actually moved. Every realized card
    // subscribes to it (ShelfCardSlot), so an equal-but-rebuilt props push must not reach it: that write is what used to
    // rebuild every card of every shelf on every parent publication.
    readonly Signal<ShelfProps<T>?> _props = new(null);
    // THE CHROME signal — title/header/pager presence. Written on every push (a rebuilt header is a new reference), so
    // the shelf's own header row stays live while the cards stay parked behind the data gate.
    readonly Signal<ShelfChrome> _chrome = new(default);
    // The NEWEST props object the parent pushed, delegates included — deliberately a plain field, not a signal: reading
    // it must never subscribe anything (the delegates are behaviour, not data). Every delegate invocation goes through
    // here so the shelf always calls the latest CardAt/KeyOf/CustomPager/OnVisibleRange even while the data gate holds.
    ShelfProps<T> _latest = null!;
    bool _delegateDriftReported;
    readonly Signal<long> _contentRevision = new(0);
    readonly BoundItemsSource<T> _items;
    readonly Signal<long> _measuredContentRevision = new(-1);
    int _count => _items.Count.Value;
    string? _title => _chrome.Value.Title;
    Element? _header => _chrome.Value.Header;
    Func<ShelfPagerContext, Element>? _customPager => _chrome.Value.HasCustomPager ? _latest.CustomPager : null;

    public void ApplyProps(object props)
    {
        var next = (ShelfProps<T>)props;
        var previous = _props.Peek();
        if (ReuseGuard.CompiledIn && ReuseGuard.Enabled && previous is not null) ReportDelegateDrift(previous, next);
        _latest = next;                       // delegates + chrome: always the newest, never gated
        _chrome.Value = new ShelfChrome(next.Title, next.Header, next.CustomPager is not null);
        // ONE compare decides both: the equality-gated write returns false for a rebuilt-but-equal snapshot, and the
        // content revision (which invalidates the measured-height lock) must move exactly when that write does. Call it
        // FIRST and unconditionally — a `previous is null ||` short-circuit here would skip the mount write entirely.
        if (_props.SetIfChanged(next)) _contentRevision.Value = _contentRevision.Peek() + 1;
    }

    /// <summary>DEBUG note (report-only): a re-pushed delegate whose <c>Method</c> differs from the mounted one is a real
    /// behaviour change, not the usual fresh closure over the same lambda — and delegates are IGNORED by the props gate,
    /// so it schedules no render (it takes effect at the next data/chrome change). Once per component.</summary>
    void ReportDelegateDrift(ShelfProps<T> previous, ShelfProps<T> next)
    {
        if (_delegateDriftReported) return;
        string? field =
            !SameMethod(previous.CardAt, next.CardAt) ? nameof(ShelfProps<T>.CardAt)
            : !SameMethod(previous.KeyOf, next.KeyOf) ? nameof(ShelfProps<T>.KeyOf)
            : !SameMethod(previous.CustomPager, next.CustomPager) ? nameof(ShelfProps<T>.CustomPager)
            : !SameMethod(previous.OnVisibleRange, next.OnVisibleRange) ? nameof(ShelfProps<T>.OnVisibleRange)
            : null;
        if (field is null) return;
        _delegateDriftReported = true;
        ReuseGuard.IgnoredDelegateChanged(this, field);
    }

    // Method, not the delegate itself: a lambda allocates a NEW closure instance every render (different Target, equal
    // Method) — that is the normal case and must stay silent. A different Method is a different lambda.
    static bool SameMethod(Delegate? a, Delegate? b)
        => a is null ? b is null : b is not null && a.Method.Equals(b.Method);

    Element CardAt(int index, float width)
    {
        var p = _props.Value!;                            // DATA (subscribes) — the gated snapshot
        return (uint)index < (uint)p.VisibleCount
            ? _latest.CardAt(p.Items[index], index, width) : new BoxEl();   // BEHAVIOUR — always the newest builder
    }

    string ItemKey(int index)
    {
        var p = _props.Peek()!;
        return (uint)index < (uint)p.Items.Count
            ? _latest.KeyOf?.Invoke(p.Items[index], index) ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    void VisibleRange(int first, int last) => _latest.OnVisibleRange?.Invoke(first, last);
    Element BindCard(BoundItemScope<T> scope) => new BoxEl
    {
        Direction = 1,
        Padding = _measured || _rows == 1 ? new Edges4(0f, LiftClearance, 0f, ShadowClearance) : default,
        HoverElevatePaint = HoverElevate,
        Children = [Embed.Comp(() => new ShelfCardSlot(this, scope))],
    };

    sealed class ShelfCardSlot(PagedShelfCore<T> owner, BoundItemScope<T> scope) : Component
    {
        public override Element Render()
        {
            // DATA read (subscribes): the gated snapshot is what re-renders this card. The BUILDERS come off _latest —
            // never gated, never subscribed (see ShelfProps' equality contract).
            var p = owner._props.Value!;
            var d = owner._latest;
            int index = scope.Index.Value;
            var item = scope.Item.Value;
            if ((uint)index >= (uint)p.VisibleCount) return new BoxEl();
            float width = owner._cardWidthAgnostic ? owner._maxCardW : owner._cardW.Value;
            if (width <= 0f) width = owner._layout?.CardW ?? owner._maxCardW;
            string key = d.KeyOf?.Invoke(item, index) ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new BoxEl { Direction = 1, Grow = 1f,
                Children = [d.CardAt(item, index, width) with { Key = key }] };
        }
    }
    readonly Func<float, float>? _cardHeight;     // null in measured mode (the engine measures instead)
    readonly bool _measured;
    readonly ShelfPager _pager;
    readonly float _minCardW, _maxCardW, _gap;
    readonly int _rows, _perPageOverride, _maxColumns;
    readonly float _fixedCardW, _headerGap, _edgeFade;
    readonly string _prevGlyph, _nextGlyph;
    readonly TemplateParts? _parts;
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
    // ── PROGRESSIVE PROBE state (measured mode). Plain UI-thread scalars; the ONE signal is the continuation's wake.
    // _probeSample is the KEYED prefix currently EMITTED — 0 means the probe layer holds no cells at all, which is the
    // steady state of a settled measured shelf. It used to be latched at min(count, cap) by the first probe and never
    // cleared, so every later render rebuilt (and kept mounted) up to 24 full card subtrees for the shelf's lifetime.
    int _probeSample;
    // The (fit, revision) the in-flight progression is measuring. Anything else moving RESTARTS it from the first
    // chunk; only the continuation widens the prefix, so a mid-pass re-render can never skip a chunk ahead.
    float _probeForCardW = float.NaN;
    long _probeForRevision = -1;
    float _probeMaxH;          // running max over the measured prefix — the provisional lock between passes
    int _probeRetries;
    readonly Signal<long> _probeTick = new(0);   // bumped by the continuation → Render re-emits the wider prefix
    readonly Action _advanceProbe;               // hoisted: the per-render UseTimeout re-arm allocates no delegate
    readonly Action<NodeHandle> _captureProbeHost;
    readonly Action<NodeHandle> _captureRoot;
    readonly Action<NodeHandle>?[] _probeRealized = new Action<NodeHandle>?[MeasuredSampleCap];
    // The sample cells' built Elements, cached FOR THE PASS. A pass is a fixed (fit, revision), so cell i's subtree is
    // the same Element every render of that pass — handing back the SAME instance lets the reconciler's ReferenceEquals
    // short-circuit skip it entirely, so a continuation that widens the prefix from 8 to 12 builds FOUR card subtrees,
    // not twelve, and re-renders none of the eight already mounted. Cleared whenever the pass resets.
    readonly Element?[] _probeCells = new Element?[MeasuredSampleCap];
    // ── VIEWPORT GATE (see ShelfViewportBand). _stripLatched is one-way: a shelf that has mounted its cards keeps
    // them. _rootNode is this shelf's own box, the anchor the band test measures from.
    NodeHandle _rootNode = NodeHandle.Null;
    bool _stripLatched;
    readonly Signal<long> _bandTick = new(0);    // the latch's wake (Render subscribes it)
    IReadSignal<float>? _pageScrollSig;          // the page's published offset (LazyScroll.Slot), refreshed per render
    readonly Action _tryLatchStripGated, _watchPageScroll;   // hoisted: no per-render delegates
    readonly Action<RectF> _onRootBounds;
    // The ItemsView element, built ONCE. ItemsView is a PROPLESS Embed.Comp, so every field it is handed FREEZES at
    // mount: rebuilding this element on later renders could not change a thing, it only allocated a ListOptions, a
    // ScrollOptions, the geometry-observer delegate pair and a RepeatLayout per render, per shelf, forever. Re-pushing
    // the SAME instance also lets the reconciler's ReferenceEquals short-circuit skip the whole strip subtree.
    Element? _liveStrip;
    int _lastVirtualNav = -1;
    int _lastVirtualColumns;
    float _lastVirtualCardW = float.NaN;

    // ── Snap-feel state (ShelfSnap.Page). Plain UI-thread scalars: written by the settled-offset observer (which the host
    // runs after the scroll kernel's tick) and read by the one debounce callback. None of it is scene state, and none of
    // it is physics — the offset stays single-writer (the kernel), reached only through ScrollIntoView.ScrollTo.
    float _pendingSnapTarget = float.NaN;   // the offset the debounce will glide to when it fires; NaN = nothing armed
    float _gestureAnchorX = float.NaN;      // the offset the CURRENT user gesture STARTED at; NaN = no gesture in flight
    bool _userScrollWas;                    // last observed UserScrollActive — the rising-edge detector for that anchor
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

    /// <summary>The retained ItemsView viewport. Null before the body realizes.</summary>
    NodeHandle ShelfViewport => _ctl.Viewport;

    /// <summary>Whether this shelf arms the hover-elevate PARK+HOIST pair (the flagged cell + the flagged clip root). It
    /// earns its keep only for LIFT-AND-HALO cards — a single row of MediaCards that translate up on hover and blur a
    /// soft halo past their resting box, which the strip's own clip would otherwise shave. A MULTI-ROW grid is a dense
    /// CHART: nothing lifts, no halo needs headroom (which is why the multi-row cell carries no lift/shadow pad either),
    /// and arming the hoist there only buys a defect — the hovered cell escapes the viewport clip and paints over
    /// whatever sits beside the band. Measured bodies are single-row by construction, so they always qualify.</summary>
    bool HoverElevate => _measured || _rows == 1;

    public PagedShelfCore(Func<float, float>? cardHeight, ShelfPager pager,
                          float minCardW, float maxCardW, float gap, int rows, int perPageOverride, float fixedCardW,
                          float headerGap, float edgeFade, string prevGlyph, string nextGlyph, TemplateParts? parts,
                          int overscan, bool measured, bool cardWidthAgnostic, int maxColumns = 0,
                          ShelfSnap snap = ShelfSnap.None)
    {
        _items = BoundItems.Project(_props, static p => p is null ? 0 : Math.Min(p.Items.Count, Math.Max(0, p.MaxItems)),
            static (p, i) => p!.Items[i], default!);
        _cardHeight = cardHeight; _measured = measured;
        _pager = pager; _minCardW = minCardW; _maxCardW = maxCardW; _gap = gap;
        _rows = Math.Max(1, rows); _perPageOverride = perPageOverride; _fixedCardW = fixedCardW;
        _headerGap = headerGap; _edgeFade = edgeFade; _prevGlyph = prevGlyph; _nextGlyph = nextGlyph;
        _parts = parts; _overscan = overscan;
        _cardWidthAgnostic = cardWidthAgnostic;
        _maxColumns = Math.Max(0, maxColumns);
        _snap = snap;
        _commitPendingSnap = CommitPendingSnap;
        _advanceProbe = AdvanceProbe;
        _captureProbeHost = h => _probeHostNode = h;
        _captureRoot = h => { _rootNode = h; };
        _tryLatchStripGated = TryLatchStripGated;
        _watchPageScroll = WatchPageScroll;
        _onRootBounds = r =>
        {
            if (r.W > 0f && MathF.Abs(r.W - _w.Peek()) > 0.5f) _w.Value = r.W;
            TryLatchStripGated();
        };
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
        // ── THE VIEWPORT GATE's data source. A page that owns the outer ScrollView publishes its live offset here
        // (LazyScroll.Slot — the same channel LazyGrid windows against). NO provider ⇒ no gate: the shelf mounts its
        // cards immediately, byte-identically to before, so a shelf on a page without a published page scroll (Home's
        // rails) is untouched.
        var pageScrollSig = UseContext(LazyScroll.Slot);
        _pageScrollSig = pageScrollSig;                 // the hoisted watcher/latch delegates read the live slot
        _ = _bandTick.Value;                           // subscribe → the latch's wake re-renders us with the strip
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

        // One bound viewport for every collection size; item revisions only invalidate measurement.
        long contentRevision = _contentRevision.Value;
        if (ShelfLog)
            Console.Error.WriteLine($"[shelf] count={_count} w={w:0} cardW={cardW:0} cols={perPageColumns} measured={_measured} mH={_measuredH.Peek():0.#} mFor={_measuredForCardW.Peek():0.#} sample={_probeSample}");
        // SUBSCRIBED reads (not Peek): the probe effect's height/for-width lock writes are what re-render us out of
        // probe mode — a Peek here leaves the shelf stuck on the invisible probe host forever.
        float measuredHLock = _measuredH.Value;
        long probeTick = _probeTick.Value;      // subscribe → a continuation's bump re-emits the wider sample prefix
        int probeTarget = _measured ? ShelfProbeMath.Target(_count) : 0;
        bool lockStale = measuredHLock <= 0f || _measuredContentRevision.Value != contentRevision
                      || MathF.Abs(_measuredForCardW.Value - cardW) > MeasureTolerance;
        // A PASS is identified by the (fit, revision) it measures. When either moves — a resize re-fit, a metadata
        // publication — the progression restarts from the first chunk at the new fit; the already-realized cells are
        // KEYED so the reconciler keeps them and only re-renders their content. When neither moved, _probeSample is
        // whatever the last completed pass left (0) or whatever the continuation advanced it to: NEVER recomputed here,
        // which is what keeps a mid-pass re-render (a chrome push, a page nav, a hover) from skipping a chunk ahead.
        bool passStale = float.IsNaN(_probeForCardW)
                      || MathF.Abs(_probeForCardW - cardW) > MeasureTolerance
                      || _probeForRevision != contentRevision;
        if (_measured)
        {
            if (probeTarget <= 0)
            {
                if (_probeSample > 0) ResetProbePass();
                _probeForCardW = float.NaN;
                _probeForRevision = -1;
            }
            else if (passStale || (lockStale && _probeSample <= 0))
            {
                ResetProbePass();
                _probeForCardW = cardW;
                _probeForRevision = contentRevision;
                _probeSample = ShelfProbeMath.FirstSample(probeTarget);
            }
            else if (_probeSample > probeTarget) _probeSample = probeTarget;
        }
        // "The probe layer is measuring" == "it has cells". A settled shelf has none, so the whole phantom-card layer
        // (up to 24 ShelfCard/overlay/shimmer subtrees per shelf, which layout kept measuring because it does NOT
        // honour NodeFlags.Visible) is simply absent for the rest of the page's life.
        bool needProbe = _probeSample > 0;

        int probeSample = _probeSample;
        UseLayoutEffect(() =>
        {
            if (!needProbe) return;
            if (Context.Scene is not { } scene) return;
            // GROW the running max: earlier passes' cells may already have unmounted-and-remounted, and a chunked
            // progression must end at the same answer an eager whole-sample pass would have produced.
            float maxH = _probeMaxH;
            for (int i = 0; i < probeSample; i++)
            {
                var h = _probeNodes[i];
                if (h.IsNull || !scene.IsLive(h)) continue;
                float ch = scene.Bounds(h).H;
                if (ch > maxH) maxH = ch;
            }
            if (maxH <= 0.5f) { ArmProbeRetry(); return; }   // nothing measurable yet — bounded retry, never a stall
            _probeMaxH = maxH;
            if (!ShelfProbeMath.IsComplete(probeSample, probeTarget))
            {
                // PROVISIONAL lock — but ONLY while there is no lock at all. A shelf that has never measured wants its
                // strip as soon as the first chunk knows a height (uniform cards, the common measured shelf, make that
                // byte-identical to the final value, so nothing moves when the later chunks land). A RE-probe must NOT
                // publish a partial max: its contract is "keep the last good strip visible and interactive; the lock is
                // replaced only after layout reports a COMPLETE new measurement", and a partial max can be shorter.
                if (_measuredH.Peek() <= 0f) _measuredH.Value = maxH;
                return;                                       // the continuation (UseTimeout below) widens the prefix
            }
            // COMPLETE: drop the cells (the next render emits none) and clear both the handles they wrote and the
            // cached cell elements — the cells are transient now, so anything left behind would point at a node (or a
            // subtree) that is about to be unmounted.
            ResetProbePass();
            // Already locked on this measurement for this cardW ⇒ write NOTHING. The signals' tolerance comparer would
            // coalesce these writes anyway; skipping them also skips the two BackwardsWriteGuard checks and keeps the
            // "probe wrote" intent readable — the loop this cuts is measure→write→re-render→re-probe.
            if (MathF.Abs(_measuredH.Peek() - maxH) > MeasureTolerance
                || MathF.Abs(_measuredForCardW.Peek() - cardW) > MeasureTolerance
                || _measuredContentRevision.Peek() != contentRevision)
            {
                _measuredContentRevision.Value = contentRevision;
                // REPLACE, not Max: the lock is per-cardW (mFor invalidates it on a width change), and a shelf that
                // re-fits narrower must not keep the taller old height as dead bottom padding.
                _measuredH.Value = maxH;
                _measuredForCardW.Value = cardW;
            }
            // UNCONDITIONAL wake: _probeSample is a plain field, so nothing else guarantees the render that actually
            // unmounts the sample cells — and every lock write above may legitimately coalesce to nothing.
            _probeTick.Value = _probeTick.Peek() + 1;
        }, DepKey.From(HashCode.Combine(cardW, probeSample, contentRevision, probeTick)));

        // The probe CONTINUATION. One shot on the host timer queue per pass: it widens the keyed prefix by one chunk
        // and bumps _probeTick, which re-renders us — so the next chunk of cells mounts in a LATER frame. Armed
        // unconditionally (identical hook surface for every shelf); the callback no-ops when no pass is in flight,
        // which is the steady state of a settled shelf and of every non-measured shelf.
        UseTimeout(_advanceProbe, ProbeChunkMs, DepKey.From(HashCode.Combine(probeSample, probeTick)));

        // ── THE VIEWPORT GATE (ShelfViewportBand). A measured shelf below the fold reserves EXACTLY the box it will
        // occupy — its height is the probe's lock, published whether or not the strip is mounted — and mounts no cards
        // until it comes within one viewport of the page's scroll window. That is rendering virtualization, the same
        // contract LazyGrid holds: the model is whole, the extent is honest, only the realized cards wait. Latching is
        // ONE-WAY, so scrolling past a shelf and back never unmounts and remounts a card.
        //
        // Two edges reach the latch. (1) The page OFFSET moving — a signal effect, untracked inside so the scene walk
        // never subscribes anything. (2) GEOMETRY changing (mount, resize, this shelf's own height landing) — the
        // layout effect below, which is also the first-frame evaluation: the top shelves must mount without waiting
        // for a scroll that may never come.
        UseSignalEffect(_watchPageScroll);
        UseLayoutEffect(_tryLatchStripGated,
            DepKey.From(HashCode.Combine(BitConverter.SingleToInt32Bits(w),
                                         BitConverter.SingleToInt32Bits(measuredHLock), probeTick)));
        // The gate may only ever withhold cards from a shelf whose BOX is reserved without them: a measured shelf's
        // probe lock, or a caller-supplied cardHeight. A non-measured shelf with NO cardHeight is sized by its
        // ItemsView, so withholding it would collapse the strip to nothing instead of reserving it — never gate that.
        bool stripMounted = _stripLatched || pageScrollSig is null || (!_measured && _cardHeight is null);

        // RECORD-cull the probe HOST when it isn't measuring. Opacity=0 alone does NOT stop the recorder walking the
        // subtree (SceneRecorder early-outs only on a cleared NodeFlags.Visible). The host itself stays mounted for the
        // shelf's lifetime — that is what makes a re-probe a pure update with the live ItemsView as an unchanged
        // sibling — while its CELLS now exist only for the frames a pass is in flight, so between passes there is
        // nothing under it to record OR to lay out. This cull is what keeps the in-flight sample off the recorder.
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
            if (needProbe || w <= 1f) return;
            bool animate = nav != _lastVirtualNav;
            bool refit = perPageColumns != _lastVirtualColumns || MathF.Abs(cardW - _lastVirtualCardW) > 0.25f;
            if (!animate && !refit) return; // metadata remeasurement preserves a free-panned offset
            _lastVirtualNav = nav;
            _lastVirtualColumns = perPageColumns;
            _lastVirtualCardW = cardW;
            _ctl.StartBringItemIntoView(_page.Peek() * perPageItems, 0f, animate && !Motion.ReducedMotion);
        }, DepKey.From(HashCode.Combine(nav, perPageColumns, cardW, needProbe)));

        Element body = _measured
            ? MeasuredVirtualBody(perPageItems, cardW, fade, stripMounted)
            : VirtualBody(perPageItems, cardW, fade, stripMounted);
        if ((_pager & ShelfPager.HoverEdge) != 0)
            body = ZStack(body, new BoxEl
            {
                Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
                Children = [ EdgeButton(true, canPrev, _pagerPrev), EdgeButton(false, canNext, _pagerNext) ],
            });

        // ── header (title + chevrons/pips/custom) ────────────────────────────────────────────────────────
        Element? headerEl = BuildHeader(p, pageCount, canPrev, canNext);

        Element[] children = headerEl is null ? [ body with { Key = "body" } ]
            : [ headerEl with { Key = "header" }, body with { Key = "body" } ];
        return _parts.Apply(PagedShelf.PartRoot, new BoxEl
        {
            // LiftClearance of the header gap lives INSIDE the strip's clip (the item container's top pad), so the
            // header→card distance on screen stays _headerGap while the clip gains hover-lift headroom — but only where
            // that pad is actually re-added (measured bodies and the single-row virtual strip). The multi-row grid keeps
            // its tight clip with NO top pad, so its header gap must not spend clearance it never gets back.
            Direction = 1, Gap = _measured || _rows == 1 ? MathF.Max(0f, _headerGap - LiftClearance) : _headerGap,
            // No explicit width: the parent sizes us, so OnBoundsChanged reports the real available width (which the
            // strip's viewport then fills → FillRowVirtualLayout fits the same cardW).
            // Cached delegate (the Root part is re-emitted every render). It also re-evaluates the viewport gate: the
            // handler fires on any X/Y/W/H change, so a section ABOVE this shelf resolving or collapsing — which moves
            // this shelf into the band with no scroll and no re-render of our own — reaches the latch.
            OnBoundsChanged = _onRootBounds,
            // The viewport gate's anchor: this box IS the shelf, so its content-space top/height are what the band
            // test measures (see TryLatchStrip).
            OnRealized = _captureRoot,
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

    // Tear a probe pass down to nothing: no emitted cells, no cached cell subtrees, no stale realize handles, no
    // running max, no retry budget spent. Called on completion (the cells unmount) and on every restart (a new fit or a
    // new content revision measures new cells at a new width).
    void ResetProbePass()
    {
        _probeSample = 0;
        _probeMaxH = 0f;
        _probeRetries = 0;
        Array.Clear(_probeNodes, 0, _probeNodes.Length);
        Array.Clear(_probeCells, 0, _probeCells.Length);
    }

    // ── The probe CONTINUATION (see ShelfProbeMath). Runs on the host timer queue one tick after the pass that armed
    // it: widen the KEYED sample prefix by one chunk and wake a render, so each chunk of sample cells mounts in its own
    // frame. Wall-clock SCHEDULING only — the completed pass measures the max over exactly the cells an eager
    // whole-sample pass would have measured, so the locked height is unchanged.
    void AdvanceProbe()
    {
        int mounted = _probeSample;
        if (mounted <= 0) return;                       // no pass in flight — the steady state of a settled shelf
        var p = _props.Peek();
        int target = p is null ? 0 : ShelfProbeMath.Target(p.VisibleCount);
        if (target <= 0) return;
        int next = ShelfProbeMath.NextSample(mounted, target);
        // Already at the target but the pass has not completed ⇒ the sample reported no bounds. Retry (bounded) rather
        // than leaving the cells mounted and the height unresolved.
        if (next == mounted && !TakeProbeRetry()) return;
        _probeSample = next;
        _probeTick.Value = _probeTick.Peek() + 1;
    }

    // Wake one more measure attempt for a mounted sample that has no bounds yet. Unreachable in practice — layout
    // effects run AFTER layout (frame phase 6.5), so a freshly realized cell already measured — and bounded either way.
    void ArmProbeRetry()
    {
        if (!TakeProbeRetry()) return;
        _probeTick.Value = _probeTick.Peek() + 1;
    }

    bool TakeProbeRetry()
    {
        if (_probeRetries >= MaxProbeRetries) return false;
        _probeRetries++;
        return true;
    }

    // The GEOMETRY edge of the viewport gate (mount / resize / a section above resolving / this shelf's own height
    // landing) — and the first-frame evaluation, so a shelf already inside the band mounts its cards without waiting
    // for a scroll that may never come.
    void TryLatchStripGated()
    {
        if (_stripLatched || _pageScrollSig is null) return;
        // A MEASURED shelf decides with its real box or not at all: before its probe locks a height its box is just the
        // header, and every not-yet-measured shelf on the page is bunched at the same content Y — a decision taken
        // there would be taken against a layout that is not yet the page's layout.
        if (_measured && _measuredH.Peek() <= 0f) return;
        TryLatchStrip();
    }

    // The page-OFFSET edge. Subscribes to the published offset and does the scene walk UNTRACKED, so the walk never
    // adds a dependency (and a shelf that has latched unsubscribes on its next run — the effect re-links every run).
    void WatchPageScroll()
    {
        if (_stripLatched) return;
        var sig = _pageScrollSig;
        if (sig is null) return;
        _ = sig.Value;                                 // subscribe to the live page offset
        Reactive.Untrack(_tryLatchStripGated);
    }

    // ── THE VIEWPORT GATE's one decision point; ShelfViewportBand holds the arithmetic. Walk to the nearest ancestor
    // scroller (the page's ScrollView) and ask whether this shelf's box, in that scroller's CONTENT space, is within
    // one viewport of the scroll window. LAYOUT-only geometry (AbsoluteLayoutRect) for the same reason
    // LazyGrid.Geometry gives: AbsoluteRect would fold in an ancestor's mid-FLIP paint transform on exactly the frame
    // this runs. Unknown geometry LATCHES — a gate that cannot see where it is must never withhold content.
    void TryLatchStrip()
    {
        if (_stripLatched) return;
        if (Context.Scene is not { } scene) return;
        if (_rootNode.IsNull || !scene.IsLive(_rootNode)) return;
        var vp = _rootNode;
        for (vp = scene.Parent(vp); !vp.IsNull && !scene.HasScroll(vp); vp = scene.Parent(vp)) { }
        if (vp.IsNull) { LatchStrip(); return; }               // no page scroller above us ⇒ nothing to gate against
        // Copy every field out BEFORE latching: LatchStrip writes a signal, and holding a ScrollState ref across a
        // write that can flush effects would alias the store (the CommitPendingSnap rule).
        float offsetY, viewportH;
        NodeHandle content;
        {
            ref ScrollState sc = ref scene.ScrollRef(vp);
            offsetY = sc.OffsetY; viewportH = sc.ViewportH; content = sc.ContentNode;
        }
        if (content.IsNull || !scene.IsLive(content)) { LatchStrip(); return; }
        var box = scene.AbsoluteLayoutRect(_rootNode);
        float top = box.Y - scene.AbsoluteLayoutRect(content).Y;
        float vh = viewportH > 1f ? viewportH : scene.AbsoluteRect(vp).H;
        // Do not treat "content hasn't grown yet" (contentH ≈ viewportH on the first layout) as "nothing is
        // below the fold". A shelf whose reserved box is already past the band stays gated; a page that truly
        // fits still latches, because every box then intersects the window.
        if (ShelfViewportBand.ShouldLatch(top, box.H, offsetY, vh))
            LatchStrip();
    }

    // ONE-WAY: a shelf that has mounted its cards keeps them, so scrolling past it and back never unmounts and
    // remounts a card (and never re-runs a card's enter motion).
    void LatchStrip()
    {
        if (_stripLatched) return;
        _stripLatched = true;
        _bandTick.Value = _bandTick.Peek() + 1;   // Render subscribes it → the strip mounts on the next render
    }

    // ── The transient PROBE cells' realize sinks, cached per index. A fresh `h => _probeNodes[idx] = h` closure per
    // cell per render was one display-class + one delegate allocation for every sample cell of every measured shelf.
    Action<NodeHandle> ProbeRealized(int index)
    {
        var sink = _probeRealized[index];
        if (sink is null)
        {
            int idx = index;
            _probeRealized[index] = sink = h => _probeNodes[idx] = h;
        }
        return sink;
    }

    // The gated-off strip's stand-in. The SAME instance every render, so the reconciler's ReferenceEquals
    // short-circuit skips it entirely; it carries the strip's key so the swap to the real ItemsView is a keyed replace.
    static readonly Element StripPlaceholder = new BoxEl { Key = "mshelf-strip", Grow = 1f };

    /// <summary>The measured strip's ItemsView — built ONCE, on the render that actually mounts it (see
    /// <see cref="_liveStrip"/>: every field of a propless <c>Embed.Comp</c> freezes at mount, so a rebuild could only
    /// ever produce garbage). Deferring the build to the mount render is also what keeps the frozen overscan honest:
    /// it is the fit live at mount, exactly as before.</summary>
    Element MeasuredLiveStrip(int perPageItems, bool fade)
    {
        if (_liveStrip is { } cached) return cached;
        var layout = _layout ??= new FillRowVirtualLayout(_minCardW, _maxCardW, _gap, 1, _perPageOverride, _fixedCardW, _maxColumns,
            leadInset: HaloBleed, trailInset: HaloBleed);
        int shelfOverscan = Math.Max(_overscan, perPageItems);
        Element strip = ItemsView.CreateBound(
            _items,
            BindCard,
            RepeatLayout.Custom(layout, horizontal: true),
            new ListOptions<T>
            {
                SelectionMode = ItemsSelectionMode.None,
                Controller = _ctl,
                Overscan = shelfOverscan,
                Entrance = new EntranceOptions { StaggerColdRealize = true },
                KeyOf = ItemKey,
                OnVisibleRange = VisibleRange,
                Grow = 1f,
                Scroll = new ScrollOptions { SuppressScrollBar = true, AutoEdgeFade = fade, AutoEdgeFadeBand = _edgeFade, OnScrollGeometryChanged = PageScrollSync() },
            }) with { Key = "mshelf-strip" };
        _liveStrip = strip;
        return strip;
    }

    // ── Measured-virtual body: sample-measure a bounded card set, lock height, then virtualize the strip. ──
    Element MeasuredVirtualBody(int perPageItems, float cardW, bool fade, bool stripMounted)
    {
        float measuredH = _measuredH.Value;
        // Constrain the strip/probe overlay to the measured shelf width: the probe row's intrinsic width is N×cardW,
        // and letting that width size this ZStack makes the INNER ScrollEl believe the off-screen strip is its
        // viewport. The outer page then clips first, paging clamps after ~one click, and the scroller's right
        // edge-fade is emitted off-screen. Widen the pinned width by 2×HaloBleed and shift it −HaloBleed (Margin) so
        // the widened clip straddles both gutters exactly like the stretch path. The live ItemsView (grow:1) fills the
        // widened ZStack ⇒ SetViewport fed _w+2·Bleed ⇒ layout re-fits back to _w.
        float viewportW = _w.Value;
        float widenedW = viewportW > 0.5f ? viewportW + 2f * HaloBleed : float.NaN;

        // ── THE PROBE LAYER. Its HOST stays mounted for the shelf's lifetime — that is what makes a width re-probe a
        // pure update, with the live ItemsView the same sibling that never flashes out of the tree — but its CELLS
        // exist ONLY while a pass is in flight (_probeSample > 0). They used to be emitted on EVERY render forever
        // (_probeSample was latched by the first probe and never cleared): a settled measured shelf rebuilt up to 24
        // FULL card subtrees per render — each a ShelfCard + LazyNowPlayingOverlay + CoverShimmer + tooltip tree — and
        // kept them mounted. The record-cull below hides them from the RECORDER, but layout does not honour
        // NodeFlags.Visible, so every layout pass still measured all of them: eight measured shelves on the artist
        // page meant ~190 phantom cards resident and remeasured for the page's whole life.
        //
        // The cells are KEYED by index (ShelfProbeKeys), so a continuation that widens the prefix REUSES every cell it
        // already realized and only realizes the new ones — the progression preserves identity, it does not re-realize.
        // The handles are cleared when a pass COMPLETES (see the measure effect), because the cells then unmount.
        var sampleCells = _probeSample > 0 ? new Element[_probeSample] : Array.Empty<Element>();
        for (int i = 0; i < sampleCells.Length; i++)
        {
            Element? cell = _probeCells[i];
            if (cell is null)
                _probeCells[i] = cell = new BoxEl
                {
                    Key = ShelfProbeKeys.Of(i),
                    Direction = 1, Width = cardW,
                    OnRealized = ProbeRealized(i),
                    Children = [ CardAt(i, cardW) ],
                };
            sampleCells[i] = cell;
        }
        Element probeHost = new BoxEl
        {
            Key = "mshelf-probe-host",
            Opacity = 0f, HitTestVisible = false,
            // Unpadded measuredH — the probe host is invisible (its own clip cuts nothing on screen) and its cells
            // measure PURE card height; the shadow-clearance pad lives only on the live strip's container/viewport.
            // Probe cells keep their natural height even while the visible viewport retains its prior lock.
            // Stretching them to that lock makes every remeasure report the previous height forever.
            Direction = 0, AlignItems = FlexAlign.Start,
            OnRealized = _captureProbeHost,   // handle for the record-cull toggle (see the needProbe effect)
            Children = sampleCells,
        };

        // ONE structural shape at every stage — [strip slot, probe host], both keyed. The strip slot is the real
        // ItemsView once the height is locked AND the shelf is inside the viewport band (ShelfViewportBand), and a
        // reference-stable empty box until then. The shelf's BOX is unchanged either way (the Height below is the
        // probe's lock, published whether or not the strip is mounted), so gating moves no pixel of anything else.
        Element strip = measuredH > 0f && stripMounted ? MeasuredLiveStrip(perPageItems, fade) : StripPlaceholder;
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
            Children = [ ZStack(strip, probeHost) with { Width = widenedW } ],
        });
    }

    /// <summary>The caller-height strip's ItemsView — built ONCE (see <see cref="MeasuredLiveStrip"/> for why a rebuild
    /// can only ever produce garbage).</summary>
    Element VirtualLiveStrip(int perPageItems, bool fade)
    {
        if (_liveStrip is { } cached) return cached;
        // The SAME stateful layout instance the engine drives via SetViewport; hoisted so its fit cache survives renders.
        // Lead/Trail = HaloBleed carve the halo gutters INSIDE the viewport (widened below by the same amount).
        var layout = _layout ??= new FillRowVirtualLayout(_minCardW, _maxCardW, _gap, _rows, _perPageOverride, _fixedCardW, _maxColumns,
            leadInset: HaloBleed, trailInset: HaloBleed);
        // ItemsView is an Embed.Comp → its template closure FREEZES at first mount (when width was 0 ⇒ cardW=min). Read
        // the layout's LIVE fitted width at realize time (the engine sets it via SetViewport every arrange) so the card
        // always matches its cell — otherwise cards stay min-width inside full-width cells (huge gaps + short cards).
        // FillRowVirtualLayout.Window measures Overscan in COLUMNS (firstCol -= overscan), not items — passing items on
        // a multi-row grid realizes rows× too much (5 rows ⇒ the whole chart resident on both sides of the window).
        // Multi-row: one neighbor column is enough for the snap-glide fade. The old max(_overscan, cols) on a
        // 2-column × 5-row chart realized 2+2+2 columns = 30 ChartRows on the artist's first content frame.
        int cols = Math.Max(1, perPageItems / _rows);
        int shelfOverscan = _rows > 1 ? 1 : Math.Max(_overscan, cols);
        Element strip = ItemsView.CreateBound(
            _items,
            BindCard,
            RepeatLayout.Custom(layout, horizontal: true),
            new ListOptions<T>
            {
                SelectionMode = ItemsSelectionMode.None,
                Controller = _ctl,
                Overscan = shelfOverscan,
                Entrance = new EntranceOptions { StaggerColdRealize = true },
                KeyOf = ItemKey,
                OnVisibleRange = VisibleRange,
                Grow = 1f,
                // paged: navigate by the chevron/pips pager, not a draggable scrollbar
                Scroll = new ScrollOptions { SuppressScrollBar = true, AutoEdgeFade = fade, AutoEdgeFadeBand = _edgeFade, OnScrollGeometryChanged = PageScrollSync() },
            }) with { Key = "mshelf-strip" };
        _liveStrip = strip;
        return strip;
    }

    // Persistent bound viewport with caller-supplied card height.
    Element VirtualBody(int perPageItems, float cardW, bool fade, bool stripMounted)
    {
        float shelfH = _cardHeight is null ? float.NaN : _rows * _cardHeight(cardW) + (_rows - 1) * _gap;
        // Build on the FIRST render even while the gate holds. ItemsView's fields freeze at MOUNT, and this shelf has
        // always built (and mounted) its strip on its first render — when _w is still 0, so the frozen overscan is the
        // caller's floor. Building here keeps that value byte-identical no matter which later render finally mounts it;
        // building it lazily instead would freeze the post-measure fit and silently WIDEN every gated shelf's realized
        // window. (The measured leg is the opposite: it has always mounted after the probe, so it builds lazily.)
        Element strip = VirtualLiveStrip(perPageItems, fade);
        // Viewport-gated (ShelfViewportBand): the caller supplied the height, so the reserved box below is exact from
        // the FIRST layout whether or not the cards are mounted — this leg of the gate never moves anything at all.
        Element items = stripMounted ? strip : StripPlaceholder;

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

        if (_customPager is { } customPager)
            // CACHED delegates (see the fields): the three action slots must be REFERENCE-STABLE across renders or a
            // custom pager that packs this context into a props record re-renders its whole subtree every shelf render.
            // Only the four VALUE slots (page/count/canPrev/canNext) change, which is exactly what should re-render it.
            row.Add(customPager(new ShelfPagerContext(p, pageCount, canPrev, canNext,
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
