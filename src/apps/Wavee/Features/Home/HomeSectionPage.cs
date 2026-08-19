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
using Wavee.Features.Browse;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>One page class for two drill-in kinds — a Home source section and a Browse section — because both render
/// the identical grid-of-cards shape. Which read the mount effect issues is decided by the ROUTE PREFIX the caller
/// built (<see cref="HomeSectionRoutes"/>'s <c>home-section:</c> vs <see cref="BrowseSectionRoutes"/>'s
/// <c>browse-section:</c>), never by sniffing the section's own <c>spotify:section:</c> URI: some hardcoded browse
/// sections (<c>Wavee.Features.Browse.BrowseTaxonomy.ChartSections</c>) carry a URI that is textually indistinguishable
/// from a Home section URI, so a URI-shaped discriminator silently sent those reads to <c>homeSection</c> instead of
/// <c>browseSection</c> — the bug this split exists to make structurally impossible. A <c>home-section:</c> route pages
/// through <c>homeSection</c> ONLY; a <c>browse-section:</c> route pages through <c>browseSection</c> ONLY. There is no
/// fallback from one to the other in either direction: a stale persisted hash or a 400 surfaces here as a visible
/// failure rather than as a silent read of the wrong endpoint.</summary>
sealed class HomeSectionPage : Component
{
    /// <summary>How far down the grid the wash may look for a card that can vouch for a colour. The wash is the TOP of
    /// the page, not a search over a fully-paged section — the same bound, for the same reason, as RecentsPage's.</summary>
    const int WashScan = 32;

    readonly Route _route;
    /// <summary>Identity for race-free last-writer-wins on <see cref="ShellMaterial"/> (see <c>ShellMaterialState</c>):
    /// a page clears the material only while it is still the owner.</summary>
    readonly object _washOwner = new();
    /// <summary>Identity for race-free last-writer-wins on <see cref="ShellMasthead"/> (see <c>ShellMastheadState</c>)
    /// — the same owner-token contract as <see cref="_washOwner"/>, one channel over.</summary>
    readonly object _mastheadOwner = new();
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
        var mastheadStore = UseContext(ShellMasthead.Slot);
        var post = UsePost();
        // The PREFIX selects the API — never the uri, which a hardcoded browse chart section can share the shape of
        // with a Home section (see the class doc-comment). An unrecognised route is a routing bug, not a data problem,
        // so it throws here rather than falling through to either endpoint by default.
        bool browse = BrowseSectionRoutes.Is(_route.Name);
        if (!browse && !HomeSectionRoutes.Is(_route.Name))
            throw new InvalidOperationException("HomeSectionPage got " + _route.Name);
        string sectionUri = browse ? BrowseSectionRoutes.UriOf(_route.Name) : HomeSectionRoutes.UriOf(_route.Name);
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
        // T12 (refinement A, verbatim): "since we know the group title we will navigate to, just load it
        // immediately". `placeholder`'s own Title is already the route's arg (see above) — reading it here, before
        // the resource resolves, is what lets the masthead below render as REAL content from frame one instead of
        // waiting on Skel.Region's Ready branch.
        var currentSection = section.Value.Value;
        var loadingMore = UseSignal(false);
        // The server answered with nothing, or with a cursor that cannot advance, or with a page the dedup ate whole.
        // HomeSection carries no exhausted flag and TotalCount is the SERVER's number — not ours to rewrite — so the
        // latch lives here.
        var exhausted = UseSignal(false);
        // The server's own pagingInfo.nextOffset from the last page we read. Null means we have no cursor at all (a
        // section still showing Home's seed), which is the ONLY case where TotalCount gets to arm the button.
        var cursor = UseSignal<int?>(null);
        // Infinite-scroll proximity gate for the grid below (HomeSectionAppendPreloader) — ConcertAppendPreloader's
        // same three-gate idiom (near-tail + arm-debounce + single-in-flight), driven off the SELF-SCROLLING grid's
        // own scroll geometry rather than a page-level ScrollView (see HomeModules.SectionGrid's OnScrollGeometryChanged
        // wiring below — this page has no outer ScrollView; the grid owns its own scroll, T8-style). Seeded true so a
        // first page shorter than the viewport still fills the screen once; dropped after every append (LoadMoreAsync)
        // so only a fresh scroll-geometry event continues the chain.
        var nearTail = UseSignal(true);

        Context.UseSignalEffect(() =>
        {
            if (expired || svc is null || sectionUri.Length == 0) return;
            // A Fold-tile drill stashes the first page in the preview store, so LoadInitial is skipped. Arm the
            // paging cursor from that seed (Total vs raw count) or a 74-item Weekly section would show ten cards
            // with no way to get the rest.
            if (seeded is not null)
            {
                if (HomeSectionPaging.HasMore(seeded))
                    cursor.Value = HomeSectionPaging.NextOffset(seeded);
                else
                    exhausted.Value = true;
                return;
            }
            _ = LoadInitialAsync(svc, sectionUri, _route.Arg, browse, section, cursor, exhausted, post);
        });

        // ── the shell MATERIAL (Mica wash) ────────────────────────────────────────────────────────────────────────
        // The SAME one-leg publication and the SAME owner-gated lifecycle RecentsPage uses, deliberately not a second
        // parallel treatment: these are the app's two drill-in surfaces and they must sit on one ground. The colour is
        // this section's own first gradeable card, through the shared HomeWashSource resolution — payload accent first,
        // graded cover second, and a NULL leg when neither exists (an invented colour is a lie about the content).
        _ = AppearancePrefs.Epoch.Value;   // the Settings toggle applies LIVE (the DisableColorWashes idiom)
        bool washesDisabled = svc is null || svc.Settings.Get(WaveeSettings.DisableColorWashes);
        var washSource = currentSection;   // subscribe: a landed page (or a paged one) re-picks the wash source
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
            _ = LoadMoreAsync(svc, current, browse, section, loadingMore, cursor, exhausted, nearTail, post);
        }

        // The near-tail scroll-geometry watch for the grid below: fed straight from the SELF-SCROLLING Virtual.Custom
        // viewport (there is no page-level ScrollView here to observe instead — see the `nearTail` field doc comment).
        // ConcertHubPage's exact quantization: the 24px-floored offset + a content-height term, so the check re-fires
        // on append growth (an append pushes the tail away → nearness recomputes false until the user scrolls again).
        (Func<FluentGpu.Animation.ScrollGeometry, long> Project, Action<FluentGpu.Animation.ScrollGeometry> Action) GridScrollWatch()
            => (
                static g => ((long)(g.OffsetY / 24f) << 20) ^ (long)(g.ContentH / 48f),
                g => nearTail.Value = g.OffsetY + g.ViewportH >= g.ContentH - 1.5f * g.ViewportH);

        // T12: the grid-only body — GridBody used to be Body's inner list slot; the masthead is no longer a
        // sibling rendered in this tree at all (G2c-B: it is a PUBLICATION now — see the leg below). Recents' list
        // slot: Grow=1 + MinHeight=0 in a COLUMN whose
        // parent has a definite height. Without this the Responsive wrapper (and the Virtual.Grid inside it)
        // measure as content-sized; a virtual viewport's natural height is 0, so the grid is given a stub
        // cross-size, Fill/Grid item rects come out shorter than the square covers, and ClipToBounds shears them
        // while the page box still fills the pane (empty mica down to the player).
        Element GridBody(HomeSection current)
        {
            // Same arming rule the published masthead's own "Show all" tools triple uses (`canLoadMore` below) —
            // the preloader is just a second, silent trigger for the identical LoadMore call, so it must never be
            // armed when the manual affordance is not.
            bool canAutoPage = CanPage(current) && !exhausted.Value && HomeSectionPaging.HasMore(current, cursor.Value);
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Children =
                [
                    // grow:1 is LOAD-BEARING. ResponsiveBox defaults grow to 0, which in this COLUMN sizes it to
                    // content. The grid must inherit the slot height above, not hug a zero-height viewport.
                    Responsive.Of(width => HomeModules.SectionGrid(current.Cards, current.Uri, width, Open, svc, acts, overlay,
                        onScrollGeometryChanged: GridScrollWatch()), fallback: HomeModuleLayout.FallbackWidth, grow: 1f),
                    // The infinite-scroll trigger (no visual footprint — see HomeSectionAppendPreloader's own doc
                    // comment for why). Keyed on the CURSOR so a successful append remounts it with a fresh retry
                    // budget for the next page, mirroring ConcertAppendPreloader's per-page remount.
                    canAutoPage
                        ? Embed.Comp(() => new HomeSectionAppendPreloader
                          {
                              Loading = loadingMore,
                              NearTail = nearTail,
                              Start = () => LoadMore(section.Value.Peek()),
                          }) with { Key = "home-section-append:" + current.Uri + ":" + cursor.Value }
                        : new BoxEl(),
                ],
            };
        }

        // T12 (refinement A, verbatim): "for the shimmer, dont include the breadcrumb ... just load it
        // immediately". The title is known from `currentSection` (the seed while pending, the real section once
        // ready — see its own doc-comment above), so the PUBLISH below carries real content from frame one — the
        // shell's ShellMastheadBand renders it above the KeepAlive swap; only GridBody (via Skel.Region's own
        // content(seed) derivation) shimmers.
        string title = SectionTitle(currentSection);
        string meta = currentSection.Subtitle is { Length: > 0 } sub
            ? sub
            : currentSection.TotalCount > 0 ? Strings.Home.SectionItems(currentSection.TotalCount) : "";
        // Same arming rule GridBody's `canAutoPage` uses — the preloader is just a second, silent trigger for the
        // identical LoadMore call, so the published "Show all" tools triple and the preloader stay armed together.
        bool canLoadMore = CanPage(currentSection) && !exhausted.Value && HomeSectionPaging.HasMore(currentSection, cursor.Value);

        // ── the shell MASTHEAD publication ──────────────────────────────────────────────────────────────────────
        // Same three-leg publish/clear lifecycle the wash (_washOwner, above) uses: a deps-leg republishes whenever
        // title/meta/tools change, UseActivation covers a KeepAlive reactivation (which skips the mount effect), and
        // the unmount leg clears ownership so a parked/evicted page never leaves a stale masthead behind it.
        void SetMasthead()
        {
            if (mastheadStore is not null)
                mastheadStore.Value = new ShellMastheadState(_mastheadOwner, title, meta.Length > 0 ? meta : null,
                    canLoadMore, loadingMore.Value, canLoadMore ? () => LoadMore(currentSection) : null);
        }
        void ClearMasthead()
        {
            if (mastheadStore is not null && ReferenceEquals(mastheadStore.Peek()?.Owner, _mastheadOwner))
                mastheadStore.Value = null;
        }
        UseEffect(() => SetMasthead(),
            DepKey.From(HashCode.Combine(title, meta, canLoadMore, loadingMore.Value)));
        // A KeepAlive-cached page does not re-run its mount effect, so reactivation re-publishes…
        UseActivation(onActivated: () => SetMasthead(), onDeactivated: ClearMasthead);
        // …and UNMOUNT clears too, because onDeactivated fires only on PARK. Owner-gated, so it can never clobber
        // whatever the next page has already published.
        UseEffect(() => (Action?)ClearMasthead, DepKey.Empty);

        // ComponentEl has no layout props. Recents puts Grow=1 / MinHeight=0 on the RENDERED root so the host pane's
        // height actually reaches the content below. smoothResize:false: easing 0 → N rows of a virtual grid clips
        // the covers into a strip (SearchPage's facet-body comment — same shape).
        //
        // G2c-B: the masthead itself moved OUT to the shell's ShellMastheadBand (above); this Padding's top inset is
        // now Spacing.L (the band owns FrameTop) rather than BrowseLayout.Frame's FrameTop — the same net gap the
        // masthead's own bottom margin used to give, just moved from "under the masthead" to "under the band". The
        // FrameX gutters still match the directory / a category page, so a drill never jumps the content column.
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.L,
            Padding = new Edges4(BrowseLayout.FrameX, Spacing.L, BrowseLayout.FrameX, Spacing.L),
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                    Children =
                    [
                        Skel.Region(section,
                            reveal: SkelReveal.StaggerRows,
                            smoothResize: false,
                            isEmpty: s => s.Cards.Count == 0 && s.UnsupportedCount == 0,
                            onEmpty: () => new BoxEl { Grow = 1f, MinHeight = 0f, Children = [EmptyState.Default()] },
                            onFailed: () => new BoxEl { Grow = 1f, MinHeight = 0f, Children = [ErrorState.Build(section.Error)] },
                            content: GridBody),
                    ],
                },
            ],
        };
    }

    /// <summary>A section is pageable only when it names a real server resource: a client-minted
    /// <c>wavee:local:</c> identity has no endpoint behind it (and the seed is all there will ever be).</summary>
    static bool CanPage(HomeSection section) =>
        section.Uri is { Length: > 0 } && !HomeSectionRoutes.IsLocal(section.Uri);

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

    static async Task LoadInitialAsync(Services svc, string uri, string? routeTitle, bool browse,
                                       Loadable<HomeSection> target, Signal<int?> cursor, Signal<bool> exhausted,
                                       Action<Action> post)
    {
        try
        {
            if (!browse)
            {
                // No browseSection fallback on failure — a null here is a 400 on a stale persisted hash (PathfinderClient
                // logs it as such) or a dead session, and quietly re-asking the wrong endpoint is what hid this for so
                // long. Fail loudly: Skel.Region paints ErrorState from the SetFailed below.
                var result = await svc.HomeSections.GetHomeSectionAsync(uri, 0).ConfigureAwait(false);
                if (result is null) throw new InvalidOperationException("homeSection returned no section for " + uri + ".");
                var first = Identify(result.Section, uri, routeTitle);
                bool hasMore = HomeSectionPaging.CanAdvance(0, result.NextOffset);
                post(() =>
                {
                    if (hasMore) cursor.Value = result.NextOffset; else exhausted.Value = true;
                    target.SetReady(first);
                });
                return;
            }

            // Same rule, the other endpoint: no homeSection fallback on failure — a browse-routed section is never
            // legal input to homeSection, so a null here fails loudly instead of quietly reading the wrong endpoint.
            var page = await svc.Browse.GetSectionAsync(uri, 0).ConfigureAwait(false);
            if (page is null) throw new InvalidOperationException("browseSection returned no section for " + uri + ".");
            var mapped = HomeBrowseCards.Section(page, routeTitle);
            int? next = HomeSectionPaging.BrowseSectionNextOffset(0, page);
            bool pageable = HomeSectionPaging.CanAdvance(0, next);
            post(() =>
            {
                if (pageable) cursor.Value = next; else exhausted.Value = true;
                target.SetReady(mapped);
            });
        }
        catch (Exception ex) { post(() => target.SetFailed(ex)); }
    }

    static async Task LoadMoreAsync(Services svc, HomeSection current, bool browse, Loadable<HomeSection> target,
                                    Signal<bool> loading, Signal<int?> cursor, Signal<bool> exhausted,
                                    Signal<bool> nearTail, Action<Action> post)
    {
        // The RAW server cursor, never the deduped card count — see HomeSectionPaging for why the two differ and what
        // paging by the deduped one did (re-fetching dropped items; an all-duplicate page looping on one offset).
        int offset = HomeSectionPaging.NextOffset(current);
        string uri = current.Uri ?? "";
        IReadOnlyList<HomeCard>? cards = null;
        int total = current.TotalCount;
        int? nextOffset = null;
        Exception? error = null;
        try
        {
            if (!browse)
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
                for (int i = 0; i < mapped.Length; i++) mapped[i] = HomeBrowseCards.Card(page.Cards[i]);
                cards = mapped;
                total = page.Total;
                nextOffset = HomeSectionPaging.BrowseSectionNextOffset(offset, page);
            }
        }
        catch (Exception ex) { error = ex; }
        post(() =>
        {
            loading.Value = false;
            // Disarm until the next scroll-geometry event re-confirms the user is still heading down — this is what
            // stops the tail from chain-loading the whole section while the page sits still (ConcertAppendPreloader's
            // identical rule). Unconditional: whatever this fetch resolved to (more, exhausted, or a transient
            // failure that leaves the button armed), only a FRESH scroll continues the auto-page chain.
            nearTail.Value = false;
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
            // Browse resolves that cursor via BrowseSectionNextOffset (server cursor, explicit terminator, or the
            // synthesized offset+count-vs-total fallback — see its own doc comment).
            bool advances = HomeSectionPaging.CanAdvance(offset, nextOffset)
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

    static HomeCard[] BlankCards()
    {
        var cards = new HomeCard[8];
        for (int i = 0; i < cards.Length; i++)
            cards[i] = new HomeCard("wavee:skeleton:home-section:" + i, "", "", null, HomeCardKind.Playlist);
        return cards;
    }
}
