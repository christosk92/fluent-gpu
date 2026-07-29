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

enum NavTransitionKind : byte { Forward, Back, Neutral }

// The content-card body opts routes into FluentGpu keep-alive caching. Route changes swap inside the boundary, scoped
// by the active browser tab, so same-route tabs never share page state.
sealed class ContentHost : Component
{
    readonly record struct PageSlot(int TabId, Route Route, NavTransitionKind Motion);

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
                Flow.KeepAlive(
                    () => new PageSlot(_activeTabId(), _route.Value, _motion.Value),
                    SlotKey,
                    s => PageFor(s.Route),
                    new KeepAliveOptions(
                        MaxEntries: 8,
                        TransitionFor: PageTransition,
                        SuppressLayoutTransitionsOnActivation: true)),
            ],
        };
    }

    // Every destination page gets its own slot inside the active tab, so ALL forward/back navigation uses the same
    // page-slide language. The prior detail/artist family keys made album→album and artist→artist mutate in place while
    // cross-family hops slid a new page in — two visibly different navigation systems for adjacent links. Search remains
    // one live workspace because its query changes in place as the omnibar is edited.
    static string SlotKey(PageSlot s)
    {
        if (s.Route.Name == "search") return s.TabId + "\u001Fsearch";
        return s.TabId + "\u001F" + s.Route.Name + "\u001F" + (s.Route.Arg ?? "");
    }

    static LayoutTransition? PageTransition(object oldToken, object newToken)
    {
        if (newToken is not PageSlot next) return null;
        var enter = next.Motion switch
            {
                NavTransitionKind.Back => MotionRecipes.PageSlideBack,
                NavTransitionKind.Neutral => MotionRecipes.PageFade,
                _ => MotionRecipes.PageSlideForward,
            };
        // KeepAlive pages are stateful, independently responsive layout roots. Overlapping the outgoing root makes both
        // participate in measurement during a window resize and destabilizes grids/scrollers. Park it immediately and
        // animate only the incoming page from the correct direction inside the already-bounded content card.
        return enter with { Exit = default };
    }

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
        if (r.Name == "home")
            return new BoxEl { Key = "page:home", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new HomePage()) ] };

        if (r.Name == "history")
            return new BoxEl { Key = "page:history", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new HistoryPage()) ] };

        if (r.Name == "settings")
            return new BoxEl { Key = "page:settings", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new SettingsPage()) ] };

        if (r.Name == "api-console")
            return new BoxEl { Key = "page:api-console", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new ApiConsolePage()) ] };

        if (r.Name == "search")
            return new BoxEl { Key = "page:search", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new SearchPage()) ] };

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
