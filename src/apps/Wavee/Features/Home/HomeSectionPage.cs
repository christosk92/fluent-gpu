using System;
using System.Collections.Generic;
using System.Linq;
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

namespace Wavee;

/// <summary>A Home source section as a first-class page. Its first page is seeded by Home; further cards use the existing
/// browse-section cursor. The seed remains visible if that inferred endpoint rejects a Home section URI.</summary>
sealed class HomeSectionPage : Component
{
    // The established BrowsePage header: 116 DIPs keeps a two-line PageHero above the fold. The section page is the same
    // directory-depth surface, so it deliberately reuses that measured band instead of inventing a third page header.
    const float HeaderHeight = 116f;
    const float GridGap = Spacing.M;

    readonly Route _route;
    public HomeSectionPage(Route route) => _route = route;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var preview = UseContext(HomeSectionPreviewStore.Slot);
        var navPreview = UseContext(NavPreviewStore.Slot);
        var acts = UseContext(ActionServices.Slot);
        var overlay = UseContext(Overlay.Service);
        var post = UsePost();
        string sectionUri = HomeSectionRoutes.UriOf(_route.Name);
        var seeded = UseMemo(() => preview?.Get(_route.Name), _route.Name);
        // A section Home minted a LOCAL identity for (the server published none) exists ONLY as its seed: the preview
        // store is a bounded FIFO and is the sole copy. Once that entry is evicted — 32 drill-ins later, or after a
        // process restart — the route is a dead link. `wavee:local:<hash>` is not a browse resource, so asking the
        // server would turn a stale route into a hard ErrorState for a section Home was quite happily rendering. Fall
        // through to the page's ordinary empty state instead, and never issue the request.
        bool expired = seeded is null && HomeSectionRoutes.IsLocal(sectionUri);
        var placeholder = seeded ?? (expired
            ? new HomeSection(null, _route.Arg, null, Array.Empty<HomeCard>(), 0, 0)
            : new HomeSection(sectionUri, _route.Arg ?? " ", null, BlankCards(), 8, 8));
        var section = UseLoadable(seeded is null && !expired
            ? Loadable<HomeSection>.Pending(placeholder)
            : Loadable<HomeSection>.Ready(placeholder));
        var loadingMore = UseSignal(false);
        // The server answered with nothing (an empty page, or a section it will not page at all). HomeSection carries
        // no exhausted flag and TotalCount is the SERVER's number — not ours to rewrite — so the latch lives here.
        var exhausted = UseSignal(false);

        Context.UseSignalEffect(() =>
        {
            if (seeded is not null || expired || svc is null || sectionUri.Length == 0) return;
            _ = LoadInitialAsync(svc, sectionUri, _route.Arg, section, post);
        });

        void PlayTrack(string uri) { if (svc is not null) _ = svc.Player.PlayTrackAsync(uri); }
        void Open(HomeCard card) => HomeCardNav.Open(card, navPreview, go, PlayTrack);

        void LoadMore(HomeSection current)
        {
            if (svc is null || !CanPage(current) || loadingMore.Peek()) return;
            loadingMore.Value = true;
            _ = LoadMoreAsync(svc, current, section, loadingMore, exhausted, post);
        }

        // Same gutter, first-row inset and page measure as Home itself — this page is one drill-in from it, so the
        // content column must not jump to a different width and a different inset on the way.
        Element Body(HomeSection current) => new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Padding = new Edges4(Spacing.PageWide, Spacing.XXL, Spacing.PageWide, PlayerDock.Reserve + Spacing.L),
            Gap = Spacing.L,
            Children =
            [
                BreadcrumbBar.Create(
                    [Loc.Get(Strings.Nav.Home), SectionTitle(current)],
                    i => { if (i == 0) go("home", null); }),
                Header(current, loadingMore.Value,
                    canLoadMore: CanPage(current) && !exhausted.Value && HomeSectionPaging.HasMore(current),
                    loadMore: () => LoadMore(current)),
                Responsive.Of(width => Grid(current, width, Open, svc, acts, overlay), fallback: 1100f),
            ],
        };

        return Skel.Region(section,
            reveal: SkelReveal.StaggerRows,
            isEmpty: s => s.Cards.Count == 0 && s.UnsupportedCount == 0,
            onEmpty: () => new BoxEl { Grow = 1f, Children = [EmptyState.Default()] },
            onFailed: () => new BoxEl { Grow = 1f, Children = [ErrorState.Build(section.Error)] },
            content: Body);
    }

    /// <summary>A section is pageable only when it names a real server resource: a client-minted
    /// <c>wavee:local:</c> identity has no endpoint behind it (and the seed is all there will ever be).</summary>
    static bool CanPage(HomeSection section) =>
        section.Uri is { Length: > 0 } && !HomeSectionRoutes.IsLocal(section.Uri);

    static Element Header(HomeSection section, bool loading, bool canLoadMore, Action loadMore)
    {
        string title = SectionTitle(section);
        string meta = section.Subtitle is { Length: > 0 } sub
            ? sub
            : section.TotalCount > 0 ? Strings.Home.SectionItems(section.TotalCount) : "";
        var tools = canLoadMore
            ? Button.Create(Loc.Get(Strings.Browse.ShowAll),
                loadMore, ButtonAppearance.Subtle, ControlSize.Small, isEnabled: !loading)
            : null;

        return new BoxEl
        {
            Direction = 0, MinHeight = HeaderHeight, MinWidth = 0f, AlignItems = FlexAlign.End,
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.M),
            Corners = Radii.CardAll, ClipToBounds = true,
            Fill = Tok.FillLayerDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.XS, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children = [WaveeType.PageHero(title) with { MaxLines = 2 },
                        Caption(meta) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                },
                tools ?? new BoxEl(),
            ],
        };
    }

    static string SectionTitle(HomeSection section) => section.Title is { Length: > 0 } title
        ? title
        : section.Cards.FirstOrDefault()?.Title ?? Loc.Get(Strings.Browse.Title);

    static Element Grid(HomeSection section, float width, Action<HomeCard> open, Services? svc,
                        ActionServices? acts, IOverlayService overlay)
    {
        var (columns, cardW) = FillRowVirtualLayout.Fit(width,
            HomeModuleLayout.ShelfCardMin, HomeModuleLayout.ShelfCardMax, GridGap);
        columns = Math.Max(1, columns);
        int tier = columns;
        return Virtual.Grid(section.Cards.Count, columns, HomeModuleLayout.ShelfCardHeight(cardW), GridGap,
            i =>
            {
                var card = section.Cards[i];
                var menu = Menus.CardAttach(acts, overlay, card.Uri, card.Title, card.Image,
                    SpotifyExportMapper.ToPlainText(card.Subtitle), circular: card.Kind == HomeCardKind.Artist);
                var drag = card.Kind is HomeCardKind.Track or HomeCardKind.Episode ? null
                    : Drag.Source(WaveeDragKinds.Resource,
                        () => WaveeResourceDragPayload.ForEntity(WaveeDragKindMap.Of(card.Kind), card.Uri,
                            card.Title, card.Image, acts));
                var media = MediaCard.Shelf(card.Image, card.Title, SpotifyExportMapper.ToPlainText(card.Subtitle) ?? "",
                        card.Uri, () => open(card), () => { if (svc is not null) _ = svc.Player.PlayAsync(card.Uri, 0); },
                        cardW, circular: card.Kind == HomeCardKind.Artist, menu: menu, drag: drag) with
                    { Key = "home-section-card:" + tier + ":" + card.Uri };
                return new BoxEl { Direction = 1, Width = cardW, MinWidth = 0f, Children = [media] };
            },
            keyOf: i => section.Uri + "\u001F" + section.Cards[i].Uri,
            overscan: 2) with { MinHeight = 0f };
    }

    static async Task LoadInitialAsync(Services svc, string uri, string? routeTitle, Loadable<HomeSection> target,
                                       Action<Action> post)
    {
        try
        {
            var page = await svc.Browse.GetSectionAsync(uri, 0).ConfigureAwait(false);
            if (page is null) throw new InvalidOperationException("Home section paging returned no section.");
            var mapped = FromBrowse(page, routeTitle);
            post(() => target.SetReady(mapped));
        }
        catch (Exception ex) { post(() => target.SetFailed(ex)); }
    }

    static async Task LoadMoreAsync(Services svc, HomeSection current, Loadable<HomeSection> target,
                                    Signal<bool> loading, Signal<bool> exhausted, Action<Action> post)
    {
        // The RAW server cursor, never the deduped card count — see HomeSectionPaging for why the two differ and what
        // paging by the deduped one did (re-fetching dropped items; an all-duplicate page looping on one offset).
        int offset = HomeSectionPaging.NextOffset(current);
        BrowseSection? page = null;
        Exception? error = null;
        try { page = await svc.Browse.GetSectionAsync(current.Uri ?? "", offset).ConfigureAwait(false); }
        catch (Exception ex) { error = ex; }
        post(() =>
        {
            loading.Value = false;
            if (page is null || page.Cards.Count == 0)
            {
                if (error is not null)
                {
                    // Transient: what we have stays visible and the button stays armed, so the user can retry.
                    svc.Log?.Event(WaveeLogLevel.Warning, "home", "home.section.page.fail",
                        "Home section paging failed; the seeded page remains visible", current.Uri, ex: error,
                        fields: [WaveeLogField.Of("offset", offset)]);
                    return;
                }
                // The endpoint answered, with nothing: either the cursor is spent or it will not page this section at
                // all. Disarm — TotalCount can outrun what the server is actually willing to serve, and an armed button
                // that fetches nothing is a click that does nothing, forever.
                exhausted.Value = true;
                return;
            }
            var mapped = new HomeCard[page.Cards.Count];
            for (int i = 0; i < mapped.Length; i++) mapped[i] = FromBrowse(page.Cards[i]);
            target.SetReady(HomeSectionPaging.Append(current, mapped, page.Total));
        });
    }

    static HomeSection FromBrowse(BrowseSection section, string? routeTitle) => new(
        section.Uri, section.Title ?? routeTitle, null, section.Cards.Select(FromBrowse).ToArray(),
        section.Total, section.Cards.Count);

    static HomeCard FromBrowse(BrowseCard card) => new(card.Uri, card.Title, card.Subtitle, card.Image, KindOf(card.Uri),
        Meta: new HomeCardMeta(Accent: card.Accent ?? 0));

    static HomeCardKind KindOf(string uri) =>
        uri.StartsWith("spotify:artist:", StringComparison.Ordinal) ? HomeCardKind.Artist :
        uri.StartsWith("spotify:album:", StringComparison.Ordinal) ? HomeCardKind.Album :
        uri.StartsWith("spotify:show:", StringComparison.Ordinal) ? HomeCardKind.Podcast :
        uri.StartsWith("spotify:episode:", StringComparison.Ordinal) ? HomeCardKind.Episode :
        uri.StartsWith("spotify:track:", StringComparison.Ordinal) ? HomeCardKind.Track : HomeCardKind.Playlist;

    static HomeCard[] BlankCards()
    {
        var cards = new HomeCard[8];
        for (int i = 0; i < cards.Length; i++)
            cards[i] = new HomeCard("wavee:skeleton:home-section:" + i, "", "", null, HomeCardKind.Playlist);
        return cards;
    }
}
