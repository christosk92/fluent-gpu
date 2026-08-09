using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Features.Browse;

/// <summary>One browse category page: an editorial header tinted by the server's own accent, then the page's sections
/// as shelves of cards, with the related-category blocks rendered in the SAME text-link vocabulary as the directory
/// so the two surfaces read as one system.
///
/// Three wire behaviours are handled explicitly rather than assumed away, all observed in <c>browe.saz</c>:
///  • a page can resolve to a 200 with no header and no sections — a normal state, not an error;
///  • <c>header.color</c> is null on some pages, so the accent wash is optional;
///  • sections page independently of the items INSIDE a section (two cursors, never conflated).</summary>
sealed class BrowsePage : Component
{
    internal sealed record Model(
        string PageUri,
        Action<string, string> OnOpenCategory,
        Action<string> OnOpenFeature,
        Action<string, string?> Go,
        Action<string> Play,
        Action OnExploreAll);
    internal static readonly Context<Model?> Props = new(null);

    // Tall enough for a two-line category title inside its colour wash, short enough that the first shelf is on screen
    // without scrolling. 168 was a slab that pushed every card below the fold.
    const float HeaderHeight = 116f;

    // Section uris whose "Show all" is in flight, so a second click cannot double-fetch or double-append.
    readonly HashSet<string> _expanding = new(StringComparer.Ordinal);
    // The live page resource, so "Show all" republishes INTO it rather than owning a second copy of the model.
    Loadable<BrowsePageModel?>? _pageRes;
    // Resolved per render, read by the card factories at drag PROMOTION (the payload factory is cold) — the same
    // late-binding shape ArtistPage uses for its own card menus.
    ActionServices? _acts;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        _acts = UseContext(ActionServices.Slot);
        var model = UseContext(Props);
        var post = UsePost();

        // The engine owns Pending / Ready / Empty / Failed (SkeletonRegion.cs) — this component never branches on load
        // state by hand. That is not a style preference: the previous hand-rolled `_loading` signal was flipped only
        // from inside an effect that bailed when `svc` was null, while its dep key carried ONLY the page uri. A
        // Services.Slot that resolved after first render therefore never re-ran the effect, the fetch never started,
        // and the shimmer stayed up forever with no log line to explain it. Folding svc-readiness into the deps (as
        // BrowseDirectory already did) makes that state unreachable, and UseResource owns cancellation on top.
        var page = UseResource(
            async ct => svc is null || model is null
                ? null
                : await svc.Browse.GetPageAsync(model.PageUri, 0, ct).ConfigureAwait(false),
            seed: (BrowsePageModel?)null,
            deps: (model?.PageUri ?? "", svc is null ? 0 : 1)).Loadable;
        _pageRes = page;

        // A failed fetch and an empty-but-successful page get the SAME calm empty state: from the user's side both
        // mean "there is nothing to show here", and a category that quietly carries no sections is not an error.
        return Skel.Region(page, Skeleton, p => Body(p!, model, svc, post),
            isEmpty: p => p is null || p.IsEmpty,
            onEmpty: () => EmptyBody(model),
            onFailed: () => EmptyBody(model));
    }

    Element Body(BrowsePageModel page, Model? model, Services? svc, Action<Action> post)
    {
        var children = new List<Element>(page.Sections.Count + 3) { Header(page) };
        foreach (var section in page.Sections)
            if (Section(section, model, svc, post) is { } el)
                children.Add(el);
        children.Add(ExploreAllButton(model));

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.L, MinWidth = 0f,
            Padding = new Edges4(0f, 0f, 0f, Spacing.XL),
            Children = children.ToArray(),
        };
    }

    static Element EmptyBody(Model? model) => new BoxEl
    {
        Direction = 1, Gap = Spacing.L, MinWidth = 0f,
        Children =
        [
            EmptyState.Build(Loc.Get(Strings.Browse.Unavailable)),
            ExploreAllButton(model),
        ],
    };

    // ── header ───────────────────────────────────────────────────────────────────────────────────────────────────
    // The category title over a soft wash of the category's own colour. Falls back to a neutral layer fill when the
    // server sends no colour, which it genuinely does on some pages.
    //
    // NO eyebrow. It used to print "BROWSE ALL" above every category, so the Pop page read "BROWSE ALL / Pop" — the
    // eyebrow named the section the user had already left rather than telling them anything about where they are. The
    // string still earns its place on the home destination card (HomePage), where "Browse all" IS the destination.
    static Element Header(BrowsePageModel page)
    {
        ColorF baseFill = Tok.FillLayerDefault;
        ColorF wash = page.Accent is { } argb ? ColorF.Lerp(baseFill, WaveePalette.ToColor(argb), 0.55f) : baseFill;

        // The colour band is BEHIND the title, not a slab above it. It used to be a 168px block holding one line of text
        // at its bottom edge — most of the page's first screen was an empty gradient, and the category's own colour was
        // spent on nothing. Now it is a short wash that the title sits inside and that fades into the page, so the
        // colour reads as this category's identity rather than as a placeholder for missing artwork.
        return new BoxEl
        {
            Direction = 1, Justify = FlexJustify.End, MinHeight = HeaderHeight, MinWidth = 0f,
            Padding = new Edges4(Spacing.PageWide, Spacing.XL, Spacing.PageWide, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
            Gradient = GradientDown(new GradientStop(0f, wash), new GradientStop(1f, baseFill)),
            Children =
            [
                WaveeType.PageHero(page.Title ?? "") with { MaxLines = 2 },
            ],
        };
    }

    // ── sections ─────────────────────────────────────────────────────────────────────────────────────────────────
    Element? Section(BrowseSection s, Model? model, Services? svc, Action<Action> post)
    {
        if (s.Kind is BrowseSectionKind.CategoryGrid or BrowseSectionKind.Related)
            return s.Categories.Count == 0 ? null : CategoryBlock(s, model);
        return s.Cards.Count == 0 ? null : Shelf(s, model, svc, post);
    }

    // A shelf of entity cards — the SAME PagedShelf + MediaCard the home feed uses, so a browse shelf and a home shelf
    // are indistinguishable in behaviour (chevron paging, edge fade, hover play).
    Element Shelf(BrowseSection s, Model? model, Services? svc, Action<Action> post)
    {
        var cards = s.Cards;
        return PagedShelf.Create(
            cards.Count,
            cardAt: (i, w) =>
            {
                var c = cards[i];
                return MediaCard.Shelf(c.Image, c.Title, c.Subtitle ?? "", c.Uri,
                    onClick: () => model?.Go(RouteFor(c.Uri), null),
                    onPlay: () => model?.Play(c.Uri),
                    cardW: w,
                    // A BrowseCard carries no kind field; the uri decides — the same discrimination RouteFor uses for
                    // the click, so the drag and the navigation can never name different entities.
                    drag: Drag.Source(WaveeDragKinds.Resource,
                        () => WaveeResourceDragPayload.ForEntity(WaveeDragKindMap.OfUri(c.Uri), c.Uri, c.Title,
                                                                 c.Image, _acts)));
            },
            header: ShelfHeader(s, svc, post),
            pager: ShelfPager.Chevrons,
            measured: true);
    }

    // Shelf heading + the "Show all" affordance, present ONLY when the server says there is more than it returned.
    // Total is frequently far larger than the returned page (a section came back 10 of 1000). It used to render that
    // as a bare "10 / 47", which reads as debug output: it states a fact the user cannot act on and competes with the
    // heading for attention. It is now an actual control that pages the SECTION cursor (browseSection) — never the
    // page cursor; the two are independent and conflating them re-fetches the wrong axis.
    Element ShelfHeader(BrowseSection s, Services? svc, Action<Action> post)
    {
        var parts = new List<Element>(2)
        {
            Subtitle(s.Title ?? "") with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Grow = 1f },
        };
        if (s.Total > s.Cards.Count && s.Uri is { Length: > 0 } sectionUri && svc is not null)
            parts.Add(new BoxEl
            {
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
                Padding = new Edges4(Spacing.S, 4f, Spacing.S, 4f),
                Corners = CornerRadius4.All(Radii.Control),
                Fill = ColorF.Transparent, HoverFill = Tok.FillControlSecondary,
                OnClick = () => ShowAll(svc, sectionUri, s.Cards.Count, post),
                Children = [Caption(Loc.Get(Strings.Browse.ShowAll)) with { Color = Tok.AccentTextPrimary, Weight = 600 }],
            });
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Children = parts.ToArray() };
    }

    void ShowAll(Services svc, string sectionUri, int offset, Action<Action> post)
    {
        if (!_expanding.Add(sectionUri)) return;   // already in flight
        _ = LoadSectionAsync(svc, sectionUri, offset, post);
    }

    async Task LoadSectionAsync(Services svc, string sectionUri, int offset, Action<Action> post)
    {
        BrowseSection? more = null;
        // A failed "Show all" leaves the shelf exactly as it was — logged so a control that appears to do nothing is
        // diagnosable rather than mysterious.
        try { more = await svc.Browse.GetSectionAsync(sectionUri, offset).ConfigureAwait(false); }
        catch (Exception ex)
        {
            svc.Log?.Event(WaveeLogLevel.Warning, "browse", "browse.section.fail", "browse section paging failed",
                sectionUri, ex: ex, fields: [WaveeLogField.Of("offset", offset)]);
        }
        post(() =>
        {
            _expanding.Remove(sectionUri);
            if (more is null || more.Cards.Count == 0 || _pageRes is not { } res) return;
            if (res.Value.Peek() is not { } page) return;
            // Ready → Ready in place: the appended cards must not send the region back through Pending, which would
            // flash the whole page's shimmer for what is a single shelf growing.
            res.SetReady(page.WithSectionCardsAppended(sectionUri, more.Cards));
        });
    }

    // A grid/related section: further categories, rendered as text links — the same vocabulary as the directory, so
    // descending the browse TREE never changes the visual language.
    static Element CategoryBlock(BrowseSection s, Model? model)
    {
        var links = new Element[s.Categories.Count];
        for (int i = 0; i < s.Categories.Count; i++)
        {
            var c = s.Categories[i];
            links[i] = new BoxEl
            {
                Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
                FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
                // A QUIET chip, and deliberately NOT a SelectorBar item. These look like a facet row and are not one:
                // there is no selection model here at all — every chip NAVIGATES (Role = Hyperlink, onClick opens
                // another page), so a selection pill would advertise a state that can never be true. What they lose in
                // this wave is the accent: an accent HOVER BORDER plus an accent hover LABEL made every one of ~20
                // links a candidate for the page's accent, which is accent on structure (hard rule 2 of the accent
                // budget in WaveeTokens). The pill shape, the fixed height and the hover motion — Wavee identity —
                // stay; the state ladder is now the neutral subtle-fill rungs the rest of the app's quiet chips use.
                // Fixed height + Shrink 0: on a non-wrapping rail, flex would otherwise compress every pill to fit and
                // ellipsise the labels instead of letting the rail overflow.
                Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                Corners = Radii.FullAll,
                Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary,
                PressedFill = Tok.FillControlTertiary,
                BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                HoverBorderColor = Tok.StrokeControlStrongDefault,
                HoverScale = WaveeMotion.ScaleSubtle.Hover,
                HoverDurationMs = WaveeMotion.Fast, HoverEasing = Easing.FluentDecelerate,
                PressScale = WaveeMotion.ScaleSubtle.Press,
                OnClick = model is null ? null : () =>
                {
                    if (c.IsClientFeature) model.OnOpenFeature(c.Uri);
                    else model.OnOpenCategory(c.Uri, c.Title);
                },
                Children = [Ui.Body(c.Title) with { Color = Tok.TextPrimary, MaxLines = 1 }],
            };
        }

        // ONE scrolling rail, no eyebrow. "RELATED CONTENT" over a wrapped block of pills was two rows of chrome
        // explaining a row of chips that already explain themselves — and on a page like Decades the wrap pushed the
        // first shelf further down than the chips were worth. Same rail the Liked content filters use: single line,
        // edge fade as the overflow cue, no chip ever squashed.
        return ScrollView(new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = links,
        }, horizontal: true) with
        {
            Grow = 0f, Height = 44f, AutoEdgeFade = true, SuppressScrollBar = true,
            ScrollKey = "browse-related:" + (s.Uri ?? s.Title ?? ""),
        };
    }

    // The page foot: one full-width route back to the directory. This is what makes a browse TREE navigable without a
    // breadcrumb — from any depth, one click returns to the table of contents.
    static Element ExploreAllButton(Model? model) => new BoxEl
    {
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
        Corners = CornerRadius4.All(Radii.Card),
        BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
        Fill = Tok.FillLayerDefault, HoverFill = Tok.FillControlSecondary,
        FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
        OnClick = model is null ? null : () => model.OnExploreAll(),
        Children = [BodyStrong(Loc.Get(Strings.Browse.ExploreAll))],
    };

    // Browse cards mix playlists, albums, shows, episodes and audiobooks. The nav route is derived from the uri's own
    // type segment, so a new entity type degrades to "open by uri" instead of navigating somewhere wrong.
    /// <summary>A card's entity uri → the app's route key. This was a stub returning the uri unchanged, so every card on
    /// a browse page navigated to a route name ContentHost has no case for and landed on the generic "arrives in a
    /// later pass" stub — every playlist, album and artist on the whole surface. ContentHost matches on the PREFIXED
    /// form ("pl:", "album:", "artist:"), which is exactly what RichText.RouteForUri already builds for rich-text links.
    ///
    /// An unroutable uri (a show, an episode, a concept) falls back to the raw uri rather than swallowing the click —
    /// the stub page at least names where the user asked to go.</summary>
    static string RouteFor(string uri) => RichText.RouteForUri(uri) ?? uri;

    static Element Skeleton()
    {
        var rows = new List<Element>(4)
        {
            new BoxEl { Height = HeaderHeight, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary },
        };
        for (int i = 0; i < 3; i++)
        {
            var cards = new List<Element>(5);
            for (int c = 0; c < 5; c++)
                cards.Add(new BoxEl { Width = 170f, Height = 200f, Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary });
            rows.Add(new BoxEl
            {
                Direction = 1, Gap = Spacing.S,
                Children =
                [
                    new BoxEl { Width = 160f, Height = 16f, Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary },
                    new BoxEl { Direction = 0, Gap = Spacing.M, Children = cards.ToArray() },
                ],
            });
        }
        return new BoxEl { Direction = 1, Gap = Spacing.L, Children = rows.ToArray() }.Skeletonized(true);
    }
}
