using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── Home module shells ───────────────────────────────────────────────────────────────────────────────────────────────
// Each shell arranges ONE card kind from HomeCards into the layout the prototype gives it, and nothing else: no data
// access, no hooks, no state. HomePage stays the dispatcher; these are pure functions of (group, callbacks, width).
//
// Every shell is width-adaptive through Responsive.Of rather than a fixed breakpoint table, because Home lives inside a
// measured virtual list whose row width is only known at arrange time. The column counts come from HomeModuleLayout,
// which the ESTIMATOR reads too — one source of truth, so a row's estimated height and its rendered height agree and the
// scroll anchor cannot flap.
static class HomeModules
{
    /// <summary>Head + content, with the module gap the prototype's `.content` uses between modules.</summary>
    static Element Module(HomeGroup group, string? sub, Element? tools, Element content,
                          Action<HomeGroup>? openSection = null) => new BoxEl
    {
        Direction = 1, Gap = HomeModuleLayout.HeadGap, MinWidth = 0f,
        Children = group.Title is { Length: > 0 }
            ? [ModuleHeader(group, sub, tools, openSection), content]
            : [content],
    };

    /// <summary>The `home-section:` drill-in header. The affordance is GATED on the group naming something the section
    /// page can actually open — a URI, a TotalCount it can page, or cards already in hand — because that page reads the
    /// home document and a group with none of those would drill into an empty surface.</summary>
    static Element ModuleHeader(HomeGroup group, string? subtitle, Element? tools, Action<HomeGroup>? openSection)
    {
        if (group.Title is not { Length: > 0 } title) return new BoxEl();
        Action? open = openSection is null
            || (group.Uri is not { Length: > 0 } && group.TotalCount <= 0 && group.Cards.Count == 0)
            ? null
            : () => openSection(group);
        return ModuleHeader(title, subtitle, tools, open);
    }

    /// <summary>The header over a PLAIN open action, for a module whose drill-in is a fixed app ROUTE rather than a
    /// `home-section:` page (Recents → the "recents" destination). None of the group-shaped gating above applies to
    /// one of those: its page is backed by a different endpoint entirely, so the shelf's URI and counts have no say in
    /// whether it can be opened. Same rendered header either way — one affordance, two ways of naming its target.</summary>
    static Element ModuleHeader(string title, string? subtitle, Element? tools, Action? open)
    {
        if (open is null) return Surfaces.SectionHeader(title, subtitle, tools);

        Element label = subtitle is { Length: > 0 } sub
            ? WaveeType.ModuleHeader(title, sub)
            : WaveeType.ModuleHeader(title);
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                    Shrink = 1f, MinWidth = 0f, OnClick = open,
                    Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                    Children = [label, Icon(Icons.ChevronRight, 12f, Tok.TextTertiary)],
                },
                new BoxEl { Grow = 1f, MinWidth = 0f },
                tools ?? new BoxEl(),
            ],
        };
    }

    public static Element SourceModule(HomeGroup group, Element content, Action<HomeGroup>? openSection = null)
        => Module(group, group.Subtitle, null, content, openSection);

    // Key per URI so the measured virtual list rebinds a recycled row to the right subtree instead of positionally
    // patching one card's tree onto another's. Built ONCE — the obvious `build(c) is BoxEl b ? … : build(c)` shape
    // evaluates the builder twice, which doubles the element tree allocated per card on a scroll-hot path.
    static Element Keyed(Element el, HomeGroupKind kind, string uri, HomeCardChrome ch)
    {
        // Chrome is applied HERE rather than inside each skin: a card's draggability and its context menu are properties
        // of the ENTITY it stands for, identical across every skin, so threading two more parameters through ten card
        // signatures would put the same two lines in ten places.
        if (el is not BoxEl b) return el;
        var keyed = b with { Key = HomeModuleLayout.RowKey(kind, uri), Draggable = ch.Drag };
        return ch.Menu is null ? keyed : keyed.WithMenu(ch.Menu);
    }

    /// <summary>A uniform column grid, as a REAL <see cref="GridEl"/> of equal star tracks.
    ///
    /// <para>It must be a grid, not a flex row of <c>Grow = 1</c> cells. A row divides no space during MEASURE:
    /// <c>growAvail</c> is "available minus the FIXED siblings" (FlexLayout's row prepass), so with every child growable
    /// it stays the whole row width and each cell measures as if it had all of it. Wrapping text then reports a
    /// one-line height, that height becomes the row's cross size, and Arrange's re-measure at the true width is
    /// discarded by the default <c>AlignItems = Stretch</c>. The cell overflows and an ancestor's ClipToBounds cuts it
    /// mid-glyph — permanently, since nothing re-measures. <c>MeasureGrid</c> instead resolves star tracks from the
    /// concrete width and derives the row height from the resolved column widths, which is the whole fix.</para>
    ///
    /// <para><c>RowHeight = NaN</c> is the grid's "auto — tallest cell in the row", and a short final row simply leaves
    /// its trailing tracks empty rather than stretching the cards it does have.</para></summary>
    static Element Grid(int columns, float colGap, float rowGap, IReadOnlyList<Element> cards)
    {
        if (cards.Count == 0) return new BoxEl();
        var tracks = new TrackSize[Math.Max(1, columns)];
        for (int i = 0; i < tracks.Length; i++) tracks[i] = TrackSize.Star();
        // Stretch is load-bearing: a star grid measured against infinite/hug width collapses its tracks to content,
        // so a 1-column dial sits as a left-aligned list instead of filling the module (and the estimator, which
        // assumed full-width wrapping, under-sizes the Home row and clips the tail).
        return Ui.Grid(tracks, colGap, rowGap, float.NaN, [.. cards]) with { AlignSelf = FlexAlign.Stretch };
    }

    /// <summary>Two star tracks at explicit weights — the editorial `1.08fr / 1fr` and the even split. Same reason as
    /// <see cref="Grid"/>: a two-child flex row measures BOTH children at the full width.</summary>
    static Element TwoColumn(float leftWeight, float gap, Element left, Element right)
        => Ui.Grid([TrackSize.Star(leftWeight), TrackSize.Star(1f)], gap, gap, float.NaN, left, right);

    // ── A2 · the weekly pair ───────────────────────────────────────────────────────────────────────────────────
    public static Element WeeklyPair(HomeGroup g, Func<HomeCard, Action> nav, Func<HomeCard, HomeCardChrome> chrome,
                                     Action<HomeGroup>? openSection = null)
        => Responsive.Of(width =>
        {
            var cards = g.Cards.Select(c => Keyed(HomeCards.WeeklyCard(c, nav(c)), g.Kind, c.Uri, chrome(c))).ToArray();
            return Module(g, g.Subtitle, null,
                Grid(HomeModuleLayout.Columns(g.Kind, width), Spacing.M, Spacing.M, cards), openSection);
        }, fallback: HomeModuleLayout.FallbackWidth);

    // ── B · jump back in ───────────────────────────────────────────────────────────────────────────────────────
    public static Element Quick(HomeGroup g, Func<HomeCard, Action> nav, Func<HomeCard, Action> play,
                                Func<HomeCard, HomeCardChrome> chrome, Action<HomeGroup>? openSection = null)
        => Responsive.Of(width =>
        {
            int shown = Math.Min(g.Cards.Count, HomeModuleLayout.QuickShown);
            var cards = g.Cards.Take(shown).Select(c => Keyed(HomeCards.QuickTile(c, nav(c), play(c)), g.Kind, c.Uri, chrome(c))).ToArray();
            return Module(g, Strings.Home.MostOpenedOf(shown, g.Cards.Count),
                null, Grid(HomeModuleLayout.Columns(g.Kind, width), Spacing.M, Spacing.M, cards), openSection);
        }, fallback: HomeModuleLayout.FallbackWidth);

    // ── C · the recents rail ───────────────────────────────────────────────────────────────────────────────────
    /// <summary>A virtualized PagedShelf with the shared MediaCard height/physics. Artists keep circular artwork while
    /// every entity participates in the same chevron paging and 24-DIP edge fade as the other browse-y modules.
    ///
    /// <para><paramref name="openAll"/> is an <c>Action</c>, not the <c>Action&lt;HomeGroup&gt; openSection</c> every
    /// other module takes, and that difference is the point: this shelf's header drills into the app's OWN Recents page
    /// (<c>/playlist/v2/list/recents/page</c> — the whole grouped snapshot), not a <c>home-section:</c> page built from
    /// this group. There is no group to hand a callback, so it is not asked for one, and the affordance is armed
    /// UNCONDITIONALLY: the landing projection's Recents group carries a null Uri, and gating on that would hide a
    /// destination whose availability the shelf's payload has nothing to do with. Chevron paging stays on the strip.</para></summary>
    public static Element Recents(HomeGroup g, Func<HomeCard, Action> nav, Func<HomeCard, string> kindLabel,
                                  Func<HomeCard, HomeCardChrome> chrome, Action? openAll = null)
    {
        var shelf = PagedShelf.Create(g.Cards.Count,
            (i, cardW) =>
            {
                var c = g.Cards[i];
                var ch = chrome(c);
                return MediaCard.Shelf(c.Image, c.Title, kindLabel(c), c.Uri, nav(c), static () => { }, cardW,
                    circular: c.Kind == HomeCardKind.Artist, menu: ch.Menu, drag: ch.Drag);
            },
            cardHeight: HomeModuleLayout.ShelfCardHeight,
            header: g.Title is { Length: > 0 } title ? ModuleHeader(title, null, null, openAll) : new BoxEl(),
            minCardW: HomeModuleLayout.ShelfCardMin, maxCardW: HomeModuleLayout.ShelfCardMax,
            gap: Spacing.M, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            keyOf: i => HomeModuleLayout.SourceCardKey(g, g.Cards[i]));
        // No subtitle: the prototype's is "Shape shows the type — artists are round", which explains the design to a
        // reviewer rather than telling the user anything. The shape does the explaining on its own.
        return shelf;
    }

    // ── D · the daily-mix band ─────────────────────────────────────────────────────────────────────────────────
    /// <summary>Six cells in ONE surface: a single r-card container with `ClipToBounds`, per-cell leading dividers and a
    /// hairline between wrapped rows. That is what makes it read as a numbered series rather than six adjacent cards.</summary>
    public static Element MixBand(HomeGroup g, Func<HomeCard, Action> nav, Func<HomeCard, HomeCardChrome> chrome,
                                  Action<HomeGroup>? openSection = null)
        => Responsive.Of(width =>
        {
            int columns = HomeModuleLayout.Columns(g.Kind, width);
            // ONE grid, gap 0, dividers drawn INSIDE each cell. A grid cannot take a separator element between its rows,
            // and it must be a grid rather than stacked flex rows for the measurement reason in Grid() — a band of six
            // growable cells in a flex row is exactly the shape that reported a one-line height and clipped its seeds.
            var cells = new Element[g.Cards.Count];
            for (int i = 0; i < cells.Length; i++)
            {
                var c = g.Cards[i];
                // The ordinal is the card's POSITION, 1-based — never parsed out of "Daily Mix 3", which is localized and
                // would number the band wrongly in any other language.
                cells[i] = Keyed(
                    HomeCards.MixSegment(c, i + 1, nav(c), leading: i % columns != 0, above: i >= columns),
                    g.Kind, c.Uri, chrome(c));
            }
            var band = new BoxEl
            {
                Direction = 1, MinWidth = 0f, ClipToBounds = true,
                Corners = CornerRadius4.All(Radii.Card),
                Fill = Tok.FillCardDefault,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children = [Ui.Grid(StarTracks(columns), 0f, 0f, float.NaN, cells)],
            };
            return Module(g, Strings.Home.OneSeries(g.Cards.Count), null, band, openSection);
        }, fallback: HomeModuleLayout.FallbackWidth);

    static TrackSize[] StarTracks(int columns)
    {
        var tracks = new TrackSize[Math.Max(1, columns)];
        for (int i = 0; i < tracks.Length; i++) tracks[i] = TrackSize.Star();
        return tracks;
    }

    // ── F · chip cards ─────────────────────────────────────────────────────────────────────────────────────────
    public static Element ChipCards(HomeGroup g, Func<HomeCard, Action> nav, Func<HomeCard, HomeCardChrome> chrome,
                                    Action<string> onNavUri, Action<HomeGroup>? openSection = null)
        => Responsive.Of(width =>
        {
            int shown = Math.Min(g.Cards.Count, HomeModuleLayout.ChipCardsShown);
            var cards = g.Cards.Take(shown).Select(c => Keyed(HomeCards.ChipCard(c, nav(c), onNavUri), g.Kind, c.Uri, chrome(c))).ToArray();
            return Module(g, Strings.Home.MixesFromArtists(g.Cards.Count),
                null, Grid(HomeModuleLayout.Columns(g.Kind, width), Spacing.M, Spacing.M, cards), openSection);
        }, fallback: HomeModuleLayout.FallbackWidth);

    // ── G · the radio dial ─────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Two columns with a COLUMN gap only — no row gap. Twenty station rows read as one dial that happens to be
    /// folded in half, which a row gap would break into ten separate pairs.</summary>
    public static Element Radio(HomeGroup g, Func<HomeCard, Action> nav, Func<HomeCard, Action> play,
                                Func<HomeCard, HomeCardChrome> chrome, Action<HomeGroup>? openSection = null)
        => Responsive.Of(width =>
        {
            int shown = Math.Min(g.Cards.Count, HomeModuleLayout.RadioShown);
            var cards = g.Cards.Take(shown).Select(c => Keyed(HomeCards.RadioRow(c, nav(c), play(c)), g.Kind, c.Uri, chrome(c))).ToArray();
            int columns = HomeModuleLayout.Columns(g.Kind, width);
            return Module(g, Strings.Home.StationCount(g.Cards.Count),
                null, Grid(columns, Spacing.XXL, 0f, cards) with { Key = "radio-grid:" + columns }, openSection);
        }, fallback: HomeModuleLayout.FallbackWidth);

    // ── H1 · up next (episodes) ────────────────────────────────────────────────────────────────────────────────
    public static Element UpNext(HomeGroup g, Func<HomeCard, Action> nav, Func<HomeCard, HomeCardChrome> chrome,
                                 Action<HomeGroup>? openSection = null)
    {
        int shown = Math.Min(g.Cards.Count, HomeModuleLayout.QueueShown);
        long queued = 0;
        for (int i = 0; i < shown; i++) queued += g.Cards[i].Meta?.DurationMs ?? 0;
        var rows = new Element[shown];
        for (int i = 0; i < shown; i++)
        {
            var c = g.Cards[i];
            rows[i] = Keyed(HomeCards.QueueRow(c, nav(c), last: i == shown - 1), g.Kind, c.Uri, chrome(c));
        }
        return Module(g, Strings.Home.QueuedSuggestions(HomeCards.Duration(queued), g.Cards.Count),
            null, new BoxEl { Direction = 1, Gap = 0f, MinWidth = 0f, Children = rows }, openSection);
    }

    // ── H2 · audiobooks ───────────────────────────────────────────────────────────────────────────────────────
    public static Element Audiobooks(HomeGroup g, Func<HomeCard, Action> nav, Func<HomeCard, HomeCardChrome> chrome,
                                     Action<HomeGroup>? openSection = null)
    {
        int shown = Math.Min(g.Cards.Count, HomeModuleLayout.BooksShown);
        var rows = g.Cards.Take(shown).Select(c => Keyed(HomeCards.BookRow(c, nav(c)), g.Kind, c.Uri, chrome(c))).ToArray();
        // A DELIBERATE 2, not the 12 every other module grid now uses: the audiobook shelf is a dense TABULAR stack
        // (the same family as the queue's Gap = 0 + divider list), where a 12-DIP gap would break six rows into six
        // cards. 2 is the spacing scale's own smallest rung, so it is on the grid rather than off it.
        return Module(g, Strings.Home.IncludedWithPremium(g.Cards.Count),
            null, new BoxEl { Direction = 1, Gap = Spacing.XXS, MinWidth = 0f, Children = rows }, openSection);
    }

    /// <summary>The `split even` pairing: episodes and audiobooks SIDE BY SIDE at width, stacked below ~1020px. Two
    /// tabular modules of the same density read as a pair; stacked they read as two more shelves.</summary>
    public static Element SplitEven(Element left, Element right)
        => Responsive.Of(width => width >= HomeModuleLayout.SplitEvenMin
            ? TwoColumn(1f, Spacing.XXL, left, right)
            : new BoxEl { Direction = 1, Gap = HomeModuleLayout.Gap(width), MinWidth = 0f, Children = [left, right] },
            fallback: HomeModuleLayout.FallbackWidth);

    /// <summary>A degraded split keeps the surviving module in its original half-column above the split threshold.</summary>
    public static Element SplitSingle(Element survivor)
        => Responsive.Of(width => width >= HomeModuleLayout.SplitEvenMin
            ? TwoColumn(1f, Spacing.XXL, survivor, new BoxEl())
            : survivor,
            fallback: HomeModuleLayout.FallbackWidth);

    // ── J · editors' picks ─────────────────────────────────────────────────────────────────────────────────────
    /// <summary>One feature card beside a column of three compact companions, `1.08fr 1fr`. The feature is the card with
    /// something to say; the column keeps the module from being a single lonely hero.</summary>
    public static Element Editorial(HomeGroup g, Func<HomeCard, Action> nav, Func<HomeCard, Action> play,
                                    Func<HomeCard, string> meta, Func<HomeCard, HomeCardChrome> chrome,
                                    Action<string> onNavUri, Action<HomeGroup>? openSection = null)
        => Responsive.Of(width =>
        {
            var feature = g.Cards[0];
            // The prototype shows one feature and three companions. Everything editorial that home returns lands in this
            // bucket though — 18 cards on a real payload — so the rest is reachable through the header drill-in.
            int companions = HomeModuleLayout.EditorialCompanions;
            var rest = g.Cards.Skip(1).Take(companions).ToList();
            Element left = Keyed(HomeCards.FeatureCard(feature, meta(feature), nav(feature), play(feature), onNavUri), g.Kind, feature.Uri, chrome(feature));
            Element right = new BoxEl
            {
                Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                Children = [.. rest.Select(c => Keyed(HomeCards.CrowdRow(c, nav(c), play(c), onNavUri), g.Kind, c.Uri, chrome(c)))],
            };
            Element content = width >= HomeModuleLayout.EditorialMin && rest.Count > 0
                // 1.08 : 1 — the feature is given a hair more room so its 148px art and three lines of blurb are not
                // fighting the column beside it.
                ? TwoColumn(1.08f, Spacing.L, left, right)
                : new BoxEl { Direction = 1, Gap = Spacing.L, MinWidth = 0f, Children = rest.Count > 0 ? [left, right] : [left] };
            return Module(g, Loc.Get(Strings.Home.EditorsPicksSub), null, content, openSection);
        }, fallback: HomeModuleLayout.FallbackWidth);

    /// <summary>A source-owned show shelf. Podcasts are destinations, so the card and module title drill rather than
    /// being flattened into QuickGrid tiles.</summary>
    public static Element Podcasts(HomeGroup g, Func<HomeCard, Action> nav, Func<HomeCard, Action> play,
                                   Func<HomeCard, HomeCardChrome> chrome, Action<HomeGroup>? openSection = null)
        => PagedShelf.Create(g.Cards.Count,
            (i, cardW) =>
            {
                var c = g.Cards[i];
                var ch = chrome(c);
                return MediaCard.Shelf(c.Image, c.Title, c.Subtitle ?? "", c.Uri, nav(c), play(c), cardW,
                    menu: ch.Menu, drag: ch.Drag);
            },
            cardHeight: HomeModuleLayout.ShelfCardHeight,
            header: ModuleHeader(g, g.Subtitle, null, openSection),
            minCardW: HomeModuleLayout.ShelfCardMin, maxCardW: HomeModuleLayout.ShelfCardMax,
            gap: Spacing.M, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            keyOf: i => HomeModuleLayout.SourceCardKey(g, g.Cards[i]));

    /// <summary>The Zune-style directory layer: peer or mixed sections become a two-row deck, and each tile opens a
    /// full section page. No selection state lives on Home.</summary>
    public static Element SectionDeck(IReadOnlyList<HomeSection> sections, Action<HomeSection> openSection)
        => PagedShelf.Create(sections.Count,
            (i, cardW) => SectionTile(sections[i], cardW, openSection),
            cardHeight: static _ => HomeModuleLayout.SectionCardHeight,
            header: Surfaces.SectionHeader(Loc.Get(Strings.Home.Sections)),
            minCardW: HomeModuleLayout.SectionCardMin, maxCardW: HomeModuleLayout.SectionCardMax,
            gap: Spacing.M, rows: 2, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            keyOf: i => "home-section-tile:" + (sections[i].Uri ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
           with { Key = HomeModuleLayout.SectionSetKey(sections) };

    static Element SectionTile(HomeSection section, float cardW, Action<HomeSection> openSection)
    {
        var first = section.Cards.FirstOrDefault();
        string title = section.Title is { Length: > 0 } t ? t : first?.Title ?? Loc.Get(Strings.Home.Sections);
        string meta = section.Subtitle is { Length: > 0 } s
            ? s
            : Strings.Home.SectionItems(Math.Max(section.TotalCount, section.Cards.Count));
        var tile = new BoxEl
        {
            Direction = 0, Height = HomeModuleLayout.SectionCardHeight, MinWidth = 0f,
            Gap = Spacing.M, AlignItems = FlexAlign.Center, Padding = Edges4.All(Spacing.S),
            Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
            OnClick = () => openSection(section), Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink,
            Children =
            [
                Surfaces.Artwork(first?.Image, SpotifyExportMapper.Hash(section.Uri ?? title),
                    HomeModuleLayout.SectionArt, HomeModuleLayout.SectionArt, Radii.Control, decodePx: 128),
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.XS, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        WaveeType.TrackTitle(title) with
                        {
                            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        WaveeType.TrackMeta(meta) with
                        {
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                },
                Icon(Icons.ChevronRight, 14f, Tok.TextTertiary) with { Shrink = 0f },
            ],
        }.Interactive(Interaction.Card);
        return MediaCard.ApplyCardPhysics(tile) with
        {
            Key = "home-section-tile-body:" + cardW.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + section.Uri,
        };
    }

    /// <summary>The landing projection's unique baseline recommendations in one paged shelf. Source reasons remain in
    /// the section directory rather than becoming one chevron-bearing header per server section.</summary>
    public static Element Feed(HomeGroup group, Func<HomeCard, Action> nav, Func<HomeCard, Action> play,
                               Func<HomeCard, HomeCardChrome> chrome, Action<string> onNavUri,
                               Action<HomeGroup>? openSection = null)
    {
        return PagedShelf.Create(group.Cards.Count,
            (i, cardW) =>
            {
                var card = group.Cards[i];
                var ch = chrome(card);
                string subtitle = card.Subtitle ?? card.Eyebrow ?? "";
                return MediaCard.Shelf(card.Image, card.Title, subtitle, card.Uri, nav(card), play(card), cardW,
                    onNavUri: onNavUri, menu: ch.Menu, drag: ch.Drag);
            },
            cardHeight: HomeModuleLayout.ShelfCardHeight,
            header: ModuleHeader(group, Strings.Home.RecommendationsWithReason(group.Cards.Count), null, openSection),
            minCardW: HomeModuleLayout.ShelfCardMin, maxCardW: HomeModuleLayout.ShelfCardMax,
            gap: Spacing.M, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            keyOf: i => HomeModuleLayout.SourceCardKey(group, group.Cards[i]))
            with { Key = HomeModuleLayout.SourceGroupKey(group) + ":feed" };
    }

}

/// <summary>A card's entity-level chrome: whether it can be dragged out of Home, and the context menu it opens. Both are
/// properties of the entity rather than of the skin, so they are applied once by <c>Keyed</c> instead of being threaded
/// through every card signature.</summary>
readonly record struct HomeCardChrome(DragSource? Drag, MenuAttach? Menu);

// ── One source of truth for module geometry ──────────────────────────────────────────────────────────────────────────
// Both the RENDERER (HomeModules) and the ESTIMATOR (HomeFeedVirtualLayout) read these. Keeping them in one place is what
// stops the likeliest regression in this page: an estimate that disagrees with the rendered height makes the measured
// virtual list re-pin its scroll anchor mid-scroll, which reads as the feed jumping under the cursor.
//
// Every breakpoint is the prototype's own container query, converted from `cqi` to the row width the virtual list hands
// the module (they are the same measurement — the module IS the container).
static class HomeModuleLayout
{
    public const float FallbackWidth = 1100f;
    /// <summary>The rhythm that separates one module from the next — the app's SECTION gap, shared verbatim with every
    /// other page that stacks sections, so a module boundary on Home is the same distance as one on the artist page.
    /// (The prototype's `.content { gap: 40px }` / 32 under 1080 is exactly those two rungs.)</summary>
    public const float ModuleGap = WaveeSize.SectionGapWide;
    public const float ModuleGapNarrow = WaveeSize.SectionGap;
    /// <summary>`.mod-head { margin-bottom: 14px }` — on the 4-grid at 12.</summary>
    public const float HeadGap = Spacing.M;

    public const float SplitEvenMin = 1020f;
    public const float EditorialMin = 980f;

    public const float ShelfCardMin = 148f;
    public const float ShelfCardMax = 188f;
    public const float ShelfEdgeFade = 24f;
    public const float SectionCardMin = 256f;
    public const float SectionCardMax = 320f;
    public const float SectionCardHeight = 112f;
    public const float SectionArt = 80f;

    // Display counts — the landing preview. The rest of the section lives on the drill-in page.
    public const int QuickShown = 8;
    public const int ChipCardsShown = 6;
    public const int RadioShown = 12;
    public const int QueueShown = 6;
    public const int BooksShown = 6;
    /// <summary>Companions beside the editorial feature: `[f, ...rest] = editorial.slice(0, 4)`.</summary>
    public const int EditorialCompanions = 3;

    public static float Gap(float width) => width >= 1080f ? ModuleGap : ModuleGapNarrow;

    /// <summary>Minimum width of one radio-dial column: 32 art + row pad + gap to text + a BodyStrong run that can
    /// still read as a station name (three thumb rungs). Same arithmetic the estimator uses so a wrap cannot desync
    /// the Home row's measured height.</summary>
    public static float RadioColMin =>
        WaveeSize.Thumb32 + 2f * Spacing.S + Spacing.M + 3f * WaveeSize.Thumb64;

    /// <summary>1 or 2 columns for the radio dial at <paramref name="width"/>. Floor-fit, then cap at 2.</summary>
    public static int RadioColumns(float width)
    {
        float gap = Spacing.XXL;
        int n = (int)MathF.Floor((MathF.Max(0f, width) + gap) / (RadioColMin + gap));
        return Math.Clamp(n, 1, 2);
    }

    /// <summary>Column count per module at a given row width — the prototype's container queries, verbatim.</summary>
    public static int Columns(HomeGroupKind kind, float width) => kind switch
    {
        // `.band` repeat(6) / 3 ≤1080 / 2 ≤620. Six numbered cells only stay legible at ~200px each.
        HomeGroupKind.MixBand => width > 1080f ? 6 : width > 620f ? 3 : 2,
        // `.quick` repeat(4) / 3 ≤1120 / 2 ≤780.
        HomeGroupKind.QuickGrid => width > 1120f ? 4 : width > 780f ? 3 : 2,
        // `.chipcards` repeat(3) / 2 ≤1020 / 1 ≤680.
        HomeGroupKind.ChipCards => width > 1020f ? 3 : width > 680f ? 2 : 1,
        // Two 1fr tracks when each would be at least a station row; never 3 — the prototype dial is a fold, not a grid.
        HomeGroupKind.RadioDial => RadioColumns(width),
        // `.weekly` 1fr 1fr / 1 ≤760.
        HomeGroupKind.WeeklyPair => width > 760f ? 2 : 1,
        _ => 1,
    };

    /// <summary>Per-card row height, for the estimator. These are the heights the skins actually produce (content + the
    /// card's own vertical padding), not guesses.</summary>
    public static float HeroHeight(float width) => HomeHeroLayout.HeightFor(width);

    public static float ShelfCardHeight(float cardW) => MediaCard.ShelfHeight(cardW);

    /// <summary>32 chevron header + card + 12 lift clearance + 12 shadow clearance. PagedShelf consumes its 12-DIP
    /// header gap into the equal lift clearance, so it contributes no second gap.</summary>
    public static float ShelfExtent(float width)
    {
        var (_, cardW) = FillRowVirtualLayout.Fit(width, ShelfCardMin, ShelfCardMax, Spacing.M);
        return 32f + ShelfCardHeight(cardW) + 2f * Spacing.M;
    }

    /// <summary>32 header + 12 header gap + two 112-DIP cards + their 12-DIP row gap.</summary>
    public const float SectionDeckExtent = 32f + Spacing.M + 2f * SectionCardHeight + Spacing.M;

    // Every arm below is the SKIN's own arithmetic restated in the SKIN's own tokens — art rung + padding rungs + line
    // heights — never a measured guess. That is the only thing keeping the estimator and the renderer from disagreeing.
    public static float CardHeight(HomeGroupKind kind) => kind switch
    {
        // The tile is exactly its cover now (it used to leave a 2-DIP sliver).
        HomeGroupKind.QuickGrid => WaveeSize.Thumb56,
        // 56 art vs. (BodyLarge 24 + a two-line 12/16 blurb = 56) — the two legs are level, plus 16 of padding a side.
        HomeGroupKind.WeeklyPair => WaveeSize.Thumb56 + 2f * Spacing.L,
        // 16 pad + Title 28/36 + 4 + Caption 16 + three Caption lines + 2 spine clearance + two 2-DIP stack gaps + 16 pad.
        HomeGroupKind.MixBand => 2f * Spacing.L + 36f + Spacing.XS + 16f + 3f * 16f + 3f * Spacing.XXS,
        // The chip card's TEXT is now the taller leg: BodyStrong 20 + a Caption-height chip run + a 16 count, with
        // two 8-DIP stack gaps, over the 64 cover. Plus 12 of padding a side.
        HomeGroupKind.ChipCards => 20f + 20f + 16f + 2f * Spacing.S + 2f * Spacing.M,
        HomeGroupKind.RadioDial => 48f,
        // Likewise: BodyStrong 20 over Caption 16 out-measures the 32 cover, + 8 of padding a side + the 1px divider.
        HomeGroupKind.QueueList => 20f + 16f + 2f * Spacing.S + 1f,
        HomeGroupKind.RatedShelf => WaveeSize.Thumb48 + 2f * Spacing.S,
        _ => 56f,
    };

    /// <summary>ONE row gap for every wrapped module grid. The old table ran 8 / 12 / 10 for three grids that sit
    /// within a screen of each other, which is three different answers to one question. The audiobook stack keeps its
    /// dense 2 — see <c>HomeModules.Audiobooks</c> for why that one is a real distinction and not drift.</summary>
    static float RowGap(HomeGroupKind kind) => kind switch
    {
        HomeGroupKind.QuickGrid or HomeGroupKind.WeeklyPair or HomeGroupKind.ChipCards => Spacing.M,
        HomeGroupKind.RatedShelf => Spacing.XXS,
        _ => 0f,
    };

    /// <summary>Display count per module on the landing — the estimator must size what is SHOWN, not what the drill-in holds.</summary>
    public static int Shown(HomeGroupKind kind, int count) => kind switch
    {
        HomeGroupKind.Hero => Math.Min(count, 1),
        HomeGroupKind.QuickGrid => Math.Min(count, QuickShown),
        HomeGroupKind.ChipCards => Math.Min(count, ChipCardsShown),
        HomeGroupKind.RadioDial => Math.Min(count, RadioShown),
        HomeGroupKind.QueueList => Math.Min(count, QueueShown),
        HomeGroupKind.RatedShelf => Math.Min(count, BooksShown),
        HomeGroupKind.Featured => Math.Min(count, 1 + EditorialCompanions),
        _ => count,
    };

    /// <summary>The rendered height of a module's CONTENT (no head) at this width.</summary>
    public static float ContentExtent(HomeGroupKind kind, float width, int count)
    {
        int shown = Shown(kind, count);
        if (shown <= 0) return 0f;
        if (kind == HomeGroupKind.Hero) return HeroHeight(width);
        if (kind is HomeGroupKind.Recents or HomeGroupKind.PodcastShelf or HomeGroupKind.DiscoverFeed)
            return ShelfExtent(width);
        if (kind == HomeGroupKind.Featured) return FeaturedExtent(width, shown);
        int columns = Columns(kind, width);
        int rows = (shown + columns - 1) / columns;
        float gap = RowGap(kind);
        float h = rows * CardHeight(kind) + Math.Max(0, rows - 1) * gap;
        // The band draws a 1px hairline between wrapped rows and sits inside a 1px contour.
        if (kind == HomeGroupKind.MixBand) h += Math.Max(0, rows - 1) + 2f;
        // The feed appends a Show-more button or the endcap rule + line.
        return h;
    }

    static float FeaturedExtent(float width, int shown)
    {
        const float feature = 2f * Spacing.XL + 148f;
        int companions = Math.Max(0, shown - 1);
        // 2x12 padding + a 48 cover: byte-identical to the old 2x10 + 52, which is why the companion column still
        // lands level with the feature card.
        float companionColumn = companions == 0 ? 0f
            : companions * (2f * Spacing.M + WaveeSize.Thumb48) + (companions - 1) * Spacing.S;
        if (companions == 0) return feature;
        return width >= EditorialMin
            ? MathF.Max(feature, companionColumn)
            : feature + Spacing.L + companionColumn;
    }

    public static string RowKey(HomeGroupKind kind, string uri) => "home-" + kind + ":" + uri;
    public static string SourceCardKey(HomeGroup group, HomeCard card)
        => (group.Uri ?? group.Title ?? group.Kind.ToString()) + "\u001F" + card.Uri;

    // PagedShelf is a Component: its card factory/data are mount-time configuration. These stable fingerprints are the
    // responsive-key rule applied to data refreshes — unchanged groups retain pager position, while any rendered field
    // changing remounts the shelf instead of leaving frozen constructor data on screen.
    //
    // MEMOIZED on the group instance. Fingerprint(group) is a deep FNV over every card and every card's meta (seeds,
    // mosaic tiles, the lot), and SourceGroupKey is asked for on the scroll-hot path: HomePage's KeyAt runs it for every
    // realized row AND again for the virtual list's own key lookup, so a 200-card discover feed was being re-hashed
    // several times per realization. A HomeGroup is an immutable record, so the key it produces can never go stale for
    // that instance; the table holds only weak references, so a swapped-out feed's groups fall out of it with the feed.
    // (Reference identity is what a ConditionalWeakTable keys on — the record's value-based Equals is not consulted.)
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HomeGroup, string> SourceGroupKeys = new();

    public static string SourceGroupKey(HomeGroup group)
        => SourceGroupKeys.GetValue(group, static g =>
            "home-source:" + Fingerprint(g).ToString("X16", System.Globalization.CultureInfo.InvariantCulture));

    // Memoized for the same reason and on the same terms as SourceGroupKey: the section deck asks for this key on every
    // render of the Sections row, and the answer is a deep FNV over every section's every card. The landing projection
    // hands out ONE directory instance per feed, so the list reference is a stable, immutable cache key.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<HomeSection>, string> SectionSetKeys = new();

    public static string SectionSetKey(IReadOnlyList<HomeSection> sections)
        => SectionSetKeys.GetValue(sections, static s => ComputeSectionSetKey(s));

    static string ComputeSectionSetKey(IReadOnlyList<HomeSection> sections)
    {
        ulong h = Text(Offset, "sections");
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            h = Text(Text(Text(h, s.Uri), s.Title), s.Subtitle);
            h = Value(Value(h, unchecked((ulong)s.TotalCount)), unchecked((ulong)s.Cards.Count));
            for (int c = 0; c < s.Cards.Count; c++) h = Value(h, Fingerprint(s.Cards[c]));
        }
        return "home-section-set:" + h.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
    }

    const ulong Offset = 14695981039346656037UL;
    const ulong Prime = 1099511628211UL;

    static ulong Value(ulong h, ulong value)
    {
        for (int i = 0; i < 8; i++) { h ^= (byte)(value >> (i * 8)); h *= Prime; }
        return h;
    }

    static ulong Text(ulong h, string? value)
    {
        h = Value(h, unchecked((ulong)(value?.Length ?? -1)));
        if (value is null) return h;
        for (int i = 0; i < value.Length; i++) h = Value(h, value[i]);
        return h;
    }

    static ulong Fingerprint(HomeGroup group)
    {
        ulong h = Value(Offset, (ulong)group.Kind);
        h = Text(Text(Text(h, group.Title), group.Subtitle), group.Uri);
        h = Value(Value(h, unchecked((ulong)group.TotalCount)), unchecked((ulong)group.Cards.Count));
        for (int i = 0; i < group.Cards.Count; i++) h = Value(h, Fingerprint(group.Cards[i]));
        return h;
    }

    static ulong Fingerprint(HomeCard card)
    {
        ulong h = Value(Offset, (ulong)card.Kind);
        h = Text(Text(Text(Text(h, card.Uri), card.Title), card.Subtitle), card.Eyebrow);
        h = Text(Text(Text(h, card.Image?.Url), card.Image?.LargestUrl), card.Image?.BlurHash);
        if (card.MosaicTiles is { } mosaic)
            for (int i = 0; i < mosaic.Count; i++) h = Text(h, mosaic[i]);
        if (card.Meta is { } m)
        {
            h = Text(Text(Text(Text(Text(h, m.Format), m.OwnerName), m.Author), m.Signifier), m.GenericTitle);
            h = Value(Value(Value(Value(Value(h, m.Accent), unchecked((ulong)m.TrackCount)),
                unchecked((ulong)m.DurationMs)), unchecked((ulong)m.ResumeMs)), m.HasVideo ? 1UL : 0UL);
            h = Value(h, m.NeedsHydration ? 1UL : 0UL);
            h = Value(h, unchecked((ulong)BitConverter.DoubleToInt64Bits(m.Rating)));
            if (m.Seeds is { } seeds)
                for (int i = 0; i < seeds.Count; i++) h = Text(h, seeds[i]);
        }
        return h;
    }
}
