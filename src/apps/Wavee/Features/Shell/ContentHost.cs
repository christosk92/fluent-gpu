using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Features.Concerts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The content-card body opts routes into FluentGpu keep-alive caching. Route changes swap inside the boundary, scoped
// by the active browser tab, so same-route tabs never share page state. Slot identity + the direction→recipe map live in
// PageNavMotion (PageSlot / SlotKey / RecipeFor).
sealed class ContentHost : Component
{
    internal const string LegacyRecentsRoute = "home-section:spotify:list:recents:main";

    readonly Signal<Route> _route;
    readonly Signal<NavTransitionKind> _motion;
    readonly Func<int> _activeTabId;
    readonly IAppSettings? _settings;   // seeds LibraryPage's persisted per-kind state (widths/sort/view/selection)
    public ContentHost(Signal<Route> route, Signal<NavTransitionKind> motion, Func<int> activeTabId, IAppSettings? settings = null)
    { _route = route; _motion = motion; _activeTabId = activeTabId; _settings = settings; }

    public override Element Render()
    {
        // A floating surface (today: the video mini player) RESERVES bottom space while it sits at its default anchor,
        // so the page content simply ends above it instead of being covered. Dragging the surface releases the
        // reservation. The wrapper is UNCONDITIONAL — padding 0 when nothing is reserved — because appearing and
        // disappearing from the tree would remount the KeepAlive subtree and cold-restart every cached page.
        var bridge = UseContext(PlaybackBridge.Slot);
        float reserve = bridge?.FloatingSurfaceReserve.Value ?? 0f;   // subscribe → re-inset as the surface comes and goes
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
            Padding = new Edges4(0f, 0f, 0f, reserve),
            Children =
            [
                // The token reads the tab + the route ONLY. `_motion` is read untracked inside PageTransition (Peek), so
                // a direction write can never re-run this thunk and re-activate the page that is already active.
                Flow.KeepAlive(
                    () => new PageSlot(_activeTabId(), _route.Value),
                    PageNavMotion.SlotKey,
                    s => PageFor(s.Route),
                    new KeepAliveOptions(
                        MaxEntries: 8,
                        TransitionFor: PageTransition,
                        SuppressLayoutTransitionsOnActivation: true)),
            ],
        };
    }

    // The whole recipe — Enter AND Exit. The two pages OVERLAP on the boundary's ZStack for the length of the swap: the
    // reconciler keeps the outgoing root drawing (hit-test invisible) and parks it the moment its tracks settle, so the
    // card cross-fades/slides between two real pages instead of cutting to empty and then fading only the new one in.
    // Direction comes from the motion signal by PEEK: the shell writes it before the route in the same flush, so at
    // reconcile time it already IS the direction of the route being activated — and an untracked read keeps a
    // motion-only write from re-running the keep-alive thunk.
    LayoutTransition? PageTransition(object oldToken, object newToken)
        => newToken is PageSlot ? PageNavMotion.RecipeFor(_motion.Peek()) : null;

    // Detail/artist pages still use their existing signal-based internals, but each route owns its signal and cached
    // subtree. Returning via Back reactivates that destination's preserved page; opening another entity activates a new
    // slot and therefore receives the same PageTransition as every other page.
    static Element DetailHost(Route route) => new BoxEl
    {
        Key = "page:detail", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children = [ Embed.Comp(() => new DetailPage(new Signal<Route>(route))) ],
    };

    static Element ArtistHost(Route route) => new BoxEl
    {
        Key = "page:artist", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children = [ Embed.Comp(() => new ArtistPage(new Signal<Route>(route))) ],
    };

    // album / playlist / liked / local / SHOW all flow through the one shared detail surface (DetailPage → DetailShell);
    // a show just renders Episodes instead of Tracks on the right (DetailConfig.Show.Content == Episodes).
    // A `prerelease:` route IS the album detail surface: the prerelease uri is resolved to its album INSIDE DetailPage's
    // load (kind 138 — the ids differ, so nothing can map them earlier), so it needs no page class of its own, only its
    // own keep-alive slot.
    static bool IsDetail(Route r) =>
        r.Name.StartsWith("album:", StringComparison.Ordinal) || r.Name.StartsWith("pl:", StringComparison.Ordinal)
        || r.Name.StartsWith("prerelease:", StringComparison.Ordinal)
        || r.Name.StartsWith("show:", StringComparison.Ordinal) || r.Name == "liked" || r.Name == "local";

    static bool IsArtist(Route r) => r.Name.StartsWith("artist:", StringComparison.Ordinal);

    Element PageFor(Route r)
    {
        // Older Home documents and persisted navigation history can still carry the synthetic section route. It was
        // never page-able (spotify:list:recents:main is not a home-section resource); render the canonical playlist4
        // Recents destination before the generic home-section arm can claim it.
        if (string.Equals(r.Name, LegacyRecentsRoute, StringComparison.Ordinal))
            r = new Route("recents", r.Arg);

        if (r.Name == "home")
            return new BoxEl { Key = "page:home", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new HomePage()) ] };

        if (r.Name == HomeCustomizerPage.Route)
            return new BoxEl { Key = "page:home-customize", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => HomeCustomizerPage.Create()) ] };

        if (HomeSectionRoutes.Is(r.Name))
            return new BoxEl { Key = "page:home-section", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new HomeSectionPage(r)) ] };

        if (r.Name == "history")
            return new BoxEl { Key = "page:history", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new HistoryPage()) ] };

        // The full recently-PLAYED surface. Deliberately its own destination rather than a `home-section:` drill-in:
        // it is backed by `/playlist/v2/list/recents/page` (the whole grouped snapshot), not by the home document's
        // section paging, so nothing about the Home shelf's counts or URIs decides whether it can be reached.
        if (r.Name == "recents")
            return new BoxEl { Key = "page:recents", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new RecentsPage()) ] };

        if (r.Name == "settings")
            return new BoxEl { Key = "page:settings", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new SettingsPage()) ] };

        if (r.Name == "api-console")
            return new BoxEl { Key = "page:api-console", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new ApiConsolePage()) ] };

        if (r.Name == PlaybackRuntimeDiagnosticsPage.Route)
            return new BoxEl { Key = "page:" + PlaybackRuntimeDiagnosticsPage.Route, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new PlaybackRuntimeDiagnosticsPage()) ] };

        // The full-page sidebar customizer (§C4.1). An ordinary destination — tabs, back/forward, history and KeepAlive
        // all behave — because it edits the LIVE preference document instead of owning any state of its own.
        if (r.Name == SidebarLayoutMenu.CustomizeRoute)
            return new BoxEl { Key = "page:sidebar-customize:" + (r.Arg ?? ""), Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new SidebarCustomizerPage(r.Arg)) with { Key = "sidebar-customizer:" + (r.Arg ?? "") } ] };

        if (r.Name == "search")
            return new BoxEl { Key = "page:search", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new SearchPage(_route)) ] };

        if (r.Name == "albums" || r.Name == "artists" || r.Name == "podcasts")
            return new BoxEl { Key = "page:" + r.Name, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new LibraryPage(r.Name, _settings)) ] };

        if (DiscographyRoute.Is(r.Name))
            return new BoxEl { Key = "page:disco", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new DiscographyPage(new Signal<Route>(r))) ] };

        // A browse CATEGORY page. The directory itself has no route — it is Search's empty state — so only the page
        // needs one, which is what makes opening a category back-navigable and keep-alive cached.
        if (Wavee.Features.Browse.BrowseRoutes.Is(r.Name))
            return new BoxEl { Key = "page:browse", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new Wavee.Features.Browse.BrowsePageHost(r)) ] };

        if (ConcertRoutes.Is(r.Name))
            return new BoxEl { Key = "page:concert-route", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new ConcertRoutePage(r)) ] };

        if (IsArtist(r)) return ArtistHost(r);
        if (IsDetail(r)) return DetailHost(r);

        var (title, glyph) = ShellNav.Dest(r);
        return new BoxEl
        {
            Key = "page:" + r.Name,
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Gap = Spacing.M,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                Icon(glyph, 40f, Tok.TextTertiary),
                WaveeType.PageHero(title),
                Caption(Loc.Get(Strings.Nav.ComingSoon)).Secondary(),
            ],
        };
    }
}
