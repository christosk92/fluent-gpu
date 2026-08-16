using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentGpu.Animation;
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

/// <summary>A Home source section as a first-class page. Its first page is seeded by Home; further cards come from the
/// <c>homeSection</c> operation — the read the desktop client actually issues for this gesture, and the one that owns
/// <c>spotify:section:</c> URIs. (This page used to page them through <c>browseSection</c>, a BROWSE resource that was
/// only ever inferred to accept a Home section URI. There is no fallback back to it: a stale persisted hash answers 400,
/// which surfaces here as a visible failure rather than as a silent read of the wrong endpoint.)</summary>
sealed class HomeSectionPage : Component
{
    const float GridGap = Spacing.M;

    /// <summary>How far down the grid the wash may look for a card that can vouch for a colour. The wash is the TOP of
    /// the page, not a search over a fully-paged section — the same bound, for the same reason, as RecentsPage's.</summary>
    const int WashScan = 32;

    readonly Route _route;
    /// <summary>Identity for race-free last-writer-wins on <see cref="ShellMaterial"/> (see <c>ShellMaterialState</c>):
    /// a page clears the material only while it is still the owner.</summary>
    readonly object _washOwner = new();
    public HomeSectionPage(Route route) => _route = route;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var preview = UseContext(HomeSectionPreviewStore.Slot);
        var navPreview = UseContext(NavPreviewStore.Slot);
        var acts = UseContext(ActionServices.Slot);
        var overlay = UseContext(Overlay.Service);
        var shellMaterial = UseContext(ShellMaterial.Slot);
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
        // The server answered with nothing, or with a cursor that cannot advance, or with a page the dedup ate whole.
        // HomeSection carries no exhausted flag and TotalCount is the SERVER's number — not ours to rewrite — so the
        // latch lives here.
        var exhausted = UseSignal(false);
        // The server's own pagingInfo.nextOffset from the last page we read. Null means we have no cursor at all (a
        // section still showing Home's seed), which is the ONLY case where TotalCount gets to arm the button.
        var cursor = UseSignal<int?>(null);

        Context.UseSignalEffect(() =>
        {
            if (seeded is not null || expired || svc is null || sectionUri.Length == 0) return;
            _ = LoadInitialAsync(svc, sectionUri, _route.Arg, section, cursor, exhausted, post);
        });

        // ── the shell MATERIAL (Mica wash) ────────────────────────────────────────────────────────────────────────
        // The SAME one-leg publication and the SAME owner-gated lifecycle RecentsPage uses, deliberately not a second
        // parallel treatment: these are the app's two drill-in surfaces and they must sit on one ground. The colour is
        // this section's own first gradeable card, through the shared HomeWashSource resolution — payload accent first,
        // graded cover second, and a NULL leg when neither exists (an invented colour is a lie about the content).
        _ = AppearancePrefs.Epoch.Value;   // the Settings toggle applies LIVE (the DisableColorWashes idiom)
        bool washesDisabled = svc is null || svc.Settings.Get(WaveeSettings.DisableColorWashes);
        var washSource = section.Value.Value;   // subscribe: a landed page (or a paged one) re-picks the wash source
        var washCard = washesDisabled ? null : WashCard(washSource);
        // Watch exactly the ONE artwork whose grading the wash is still waiting on — never the plane's global epoch,
        // which every realized batch of this page's own grid would bump.
        if (HomeWashSource.PlaneUrl(washCard) is { Length: > 0 } planeUrl)
            _ = SpotifyLive.CoverColorPlane.Current.Watch(planeUrl).Value;
        var pick = washesDisabled ? null : HomeWashSource.Pick(washCard, Surfaces.ChromeSchemeFor);
        HomeWash? wash = pick is null ? null : new HomeWash(new WashLayer(pick.Value.Color, pick.Value.Key), null, null);

        // Owner-gated exactly like HomePage/DetailShell/RecentsPage: a page clears the material only while it is still
        // the owner, so a "park this page + activate the destination" nav lands on the destination's material whichever
        // effect fires first. Disabled ⇒ this page still CLAIMS ownership with a null wash, which is what clears the
        // previous page's material and leaves only the deterministic ground.
        void SetWash(HomeWash? w)
        {
            if (shellMaterial is not null) shellMaterial.Value = new ShellMaterialState(_washOwner, null, w);
        }
        void ClearWash()
        {
            if (shellMaterial is not null && ReferenceEquals(shellMaterial.Peek().Owner, _washOwner))
                shellMaterial.Value = default;
        }
        UseEffect(() => SetWash(wash),
            DepKey.From(HashCode.Combine(washesDisabled, pick?.Key, pick?.Color.R, pick?.Color.G, pick?.Color.B)));
        // A KeepAlive-cached page does not re-run its mount effect, so reactivation re-publishes…
        UseActivation(onActivated: () => SetWash(wash), onDeactivated: ClearWash);
        // …and UNMOUNT clears too, because onDeactivated fires only on PARK: a nav that evicts this page without
        // parking it would otherwise leave a wash owned by a gone page. Owner-gated, so it can never clobber the next.
        UseEffect(() => (Action?)ClearWash, DepKey.Empty);

        void PlayTrack(string uri) { if (svc is not null) _ = svc.Player.PlayTrackAsync(uri); }
        void Open(HomeCard card) => HomeCardNav.Open(card, navPreview, go, PlayTrack);

        void LoadMore(HomeSection current)
        {
            if (svc is null || !CanPage(current) || loadingMore.Peek()) return;
            loadingMore.Value = true;
            _ = LoadMoreAsync(svc, current, section, loadingMore, cursor, exhausted, post);
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
                    canLoadMore: CanPage(current) && !exhausted.Value && HomeSectionPaging.HasMore(current, cursor.Value),
                    loadMore: () => LoadMore(current)),
                // grow:1 is LOAD-BEARING, not decoration. ResponsiveBox renders `BoxEl { Direction = 1, Grow = grow }`
                // and grow defaults to 0 — which in this COLUMN parent sizes it to its CONTENT. Its content is a
                // VirtualListEl, whose natural height is 0 because a virtualized scroller expects to be GIVEN a height,
                // so the grid's own Grow=1 was being measured against a zero-height wrapper and the section rendered
                // header-only with an empty body. Every other Responsive.Of caller wraps something content-sized (a
                // hero, a card), which is why this is the only site that needs it.
                Responsive.Of(width => Grid(current, width, Open, svc, acts, overlay), fallback: 1100f, grow: 1f),
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

    /// <summary>The masthead — the SAME one RecentsPage established, because these are the app's two drill-in surfaces
    /// and two mastheads would read as two designs: <see cref="WaveeType.SurfaceDisplay"/>'s 40/52/400 display cut over
    /// one thin metadata line, the stagger on the CONTAINER and the enter on each line (the engine's own idiom).
    ///
    /// <para>The bordered, filled 116-DIP band this replaced was authored before the page had a Mica wash under it: a
    /// FillLayerDefault card with its own contour sits ON TOP of the wash and hides exactly the part of it the eye
    /// reads first. The "show all" button keeps its place and its behaviour — only the chrome around it is gone.</para></summary>
    static Element Header(HomeSection section, bool loading, bool canLoadMore, Action loadMore)
    {
        string title = SectionTitle(section);
        string meta = section.Subtitle is { Length: > 0 } sub
            ? sub
            : section.TotalCount > 0 ? Strings.Home.SectionItems(section.TotalCount) : "";

        var lines = new List<Element>(2)
        {
            WaveeType.SurfaceDisplay(title) with
            {
                MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                Enter = new EnterExit(Dy: 10f, Opacity: 0f, Active: true),
                Transition = MotionTok.StandardEnter,
            },
        };
        if (meta.Length > 0)
            lines.Add(Caption(meta) with
            {
                Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                Enter = new EnterExit(Dy: 10f, Opacity: 0f, Active: true),
                Transition = MotionTok.StandardEnter,
            });

        var masthead = new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Stagger = Motion.ReducedMotion ? 0f : WaveeMotion.MastheadStaggerMs,
            Children = lines.ToArray(),
        };
        // The row is UNCONDITIONAL — `tools ?? new BoxEl()`, exactly as before. Returning the bare masthead when there
        // is nothing left to page would change this subtree's shape the moment the section exhausts, remounting the
        // masthead and replaying its entrance in the middle of a read.
        var tools = canLoadMore
            ? Button.Create(Loc.Get(Strings.Browse.ShowAll),
                loadMore, ButtonAppearance.Subtle, ControlSize.Small, isEnabled: !loading)
            : null;
        // The paging control rides the masthead's LAST baseline rather than a card corner, so a one-line and a two-line
        // section title both leave it in the same place relative to the copy.
        return new BoxEl
        {
            Direction = 0, MinWidth = 0f, Gap = Spacing.M, AlignItems = FlexAlign.End,
            Children = [masthead, tools ?? new BoxEl()],
        };
    }

    /// <summary>The wash's source card: the first card that can actually vouch for a colour — a payload accent, or a
    /// cover the plane can grade. Null until one can, so the page never publishes an invented tint (the skeleton cards
    /// carry neither, which is exactly why a loading section shows the bare deterministic ground).</summary>
    static HomeCard? WashCard(HomeSection section)
    {
        var cards = section.Cards;
        int scan = Math.Min(cards.Count, WashScan);
        for (int i = 0; i < scan; i++)
        {
            var c = cards[i];
            if (c.Meta is { Accent: not 0u } || c.Image?.Url is { Length: > 0 }) return c;
        }
        return null;
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

    /// <summary>A section URI the HOME document owns, and therefore one <c>homeSection</c> answers. Everything else
    /// (browse-minted section URIs reached through this page) keeps the browse read; <c>wavee:local:</c> never gets
    /// here at all — <see cref="CanPage"/> and the mount effect both stop it.</summary>
    static bool IsHomeSection(string? uri) =>
        uri is not null && uri.StartsWith("spotify:section:", StringComparison.Ordinal);

    static async Task LoadInitialAsync(Services svc, string uri, string? routeTitle, Loadable<HomeSection> target,
                                       Signal<int?> cursor, Signal<bool> exhausted, Action<Action> post)
    {
        try
        {
            if (IsHomeSection(uri))
            {
                // No browseSection fallback on failure — a null here is a 400 on a stale persisted hash (PathfinderClient
                // logs it as such) or a dead session, and quietly re-asking the wrong endpoint is what hid this for so
                // long. Fail loudly: Skel.Region paints ErrorState from the SetFailed below.
                var result = await svc.HomeSections.GetHomeSectionAsync(uri, 0).ConfigureAwait(false);
                if (result is null) throw new InvalidOperationException("homeSection returned no section for " + uri + ".");
                var first = Identify(result.Section, uri, routeTitle);
                bool more = HomeSectionPaging.CanAdvance(0, result.NextOffset);
                post(() =>
                {
                    if (more) cursor.Value = result.NextOffset; else exhausted.Value = true;
                    target.SetReady(first);
                });
                return;
            }

            var page = await svc.Browse.GetSectionAsync(uri, 0).ConfigureAwait(false);
            if (page is null) throw new InvalidOperationException("Home section paging returned no section.");
            var mapped = FromBrowse(page, routeTitle);
            post(() => target.SetReady(mapped));
        }
        catch (Exception ex) { post(() => target.SetFailed(ex)); }
    }

    static async Task LoadMoreAsync(Services svc, HomeSection current, Loadable<HomeSection> target,
                                    Signal<bool> loading, Signal<int?> cursor, Signal<bool> exhausted,
                                    Action<Action> post)
    {
        // The RAW server cursor, never the deduped card count — see HomeSectionPaging for why the two differ and what
        // paging by the deduped one did (re-fetching dropped items; an all-duplicate page looping on one offset).
        int offset = HomeSectionPaging.NextOffset(current);
        string uri = current.Uri ?? "";
        bool home = IsHomeSection(uri);
        IReadOnlyList<HomeCard>? cards = null;
        int total = current.TotalCount;
        int? nextOffset = null;
        Exception? error = null;
        try
        {
            if (home)
            {
                if (await svc.HomeSections.GetHomeSectionAsync(uri, offset).ConfigureAwait(false) is { } result)
                {
                    cards = result.Section.Cards;
                    total = result.Section.TotalCount;
                    nextOffset = result.NextOffset;
                }
            }
            else if (await svc.Browse.GetSectionAsync(uri, offset).ConfigureAwait(false) is { } page)
            {
                var mapped = new HomeCard[page.Cards.Count];
                for (int i = 0; i < mapped.Length; i++) mapped[i] = FromBrowse(page.Cards[i]);
                cards = mapped;
                total = page.Total;
            }
        }
        catch (Exception ex) { error = ex; }
        post(() =>
        {
            loading.Value = false;
            if (cards is null || cards.Count == 0)
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

            var next = HomeSectionPaging.Append(current, cards, total);
            // Three independent ways this was the last useful page, and totalCount is deliberately none of them:
            //  • the server's cursor is null (complete) or points at/behind the offset we just asked for — a COMPLETE
            //    section answers nextOffset: 0, so honouring it as a cursor loops on page one forever;
            //  • the whole page was already-seen URIs, so another click can only produce the same nothing.
            // The browse path carries no cursor of its own, so only the dedup guard applies to it.
            bool advances = (!home || HomeSectionPaging.CanAdvance(offset, nextOffset))
                            && HomeSectionPaging.Progressed(current, next);
            if (advances) cursor.Value = nextOffset; else exhausted.Value = true;
            target.SetReady(next);
        });
    }

    /// <summary>Give a fetched page the identity the route already knows. <c>homeSection</c> echoes the section's own
    /// uri/title, but a response that omits either must not turn a titled drill-in into an anonymous one — the route is
    /// the authority we arrived with.</summary>
    static HomeSection Identify(HomeSection section, string uri, string? routeTitle) => section with
    {
        Uri = section.Uri is { Length: > 0 } ? section.Uri : uri,
        Title = section.Title is { Length: > 0 } ? section.Title : routeTitle,
    };

    static HomeSection FromBrowse(BrowseSection section, string? routeTitle) => new(
        section.Uri, section.Title ?? routeTitle, null, section.Cards.Select(FromBrowse).ToArray(),
        section.Total, section.Cards.Count);

    static HomeCard FromBrowse(BrowseCard card) => new(card.Uri, card.Title, card.Subtitle, card.Image, KindOf(card.Uri),
        Meta: new HomeCardMeta(Accent: card.Accent ?? 0));

    // The card's uri names its kind through the ONE parser (hydration-facade-design.md §1.1); everything the browse
    // feed can carry that is not one of these five still reads as a Playlist card, exactly as before.
    static HomeCardKind KindOf(string uri) => EntityUri.KindOf(uri) switch
    {
        EntityKind.Artist => HomeCardKind.Artist,
        EntityKind.Album => HomeCardKind.Album,
        EntityKind.Show => HomeCardKind.Podcast,
        EntityKind.Episode => HomeCardKind.Episode,
        EntityKind.Track => HomeCardKind.Track,
        _ => HomeCardKind.Playlist,
    };

    static HomeCard[] BlankCards()
    {
        var cards = new HomeCard[8];
        for (int i = 0; i < cards.Length; i++)
            cards[i] = new HomeCard("wavee:skeleton:home-section:" + i, "", "", null, HomeCardKind.Playlist);
        return cards;
    }
}
