using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
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
        => Flow.KeepAlive(
            () => new PageSlot(_activeTabId(), _route.Value, _motion.Value),
            SlotKey,
            s => PageFor(s.Route),
            new KeepAliveOptions(MaxEntries: 8, TransitionFor: PageTransition));

    // Detail and artist routes each collapse to ONE slot per tab (identity = tab × page-class; the route is content) so
    // album→album / artist→artist reconciles the mounted shell instead of cold-remounting. TabId stays in the key → tabs
    // never share a slot. Everything else (incl. discography) keys per name+arg for a fresh slot.
    static string SlotKey(PageSlot s)
    {
        if (IsDetail(s.Route)) return s.TabId + "\u001Fdetail";
        if (IsArtist(s.Route)) return s.TabId + "\u001Fartist";
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

    // The shared detail page (album / playlist / single / liked) reads the route reactively (via _route) and derives its
    // own kind/id, so ONE DetailPage instance serves successive detail routes in place. The Key is route-INDEPENDENT
    // ("page:detail") so KeepAlive reuses the mounted slot across albums — the swap reconciles + reloads in place rather
    // than tearing down and cold-remounting the rail / track-list / trailing.
    Element DetailHost() => new BoxEl
    {
        Key = "page:detail", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children = [ Embed.Comp(() => new DetailPage(_route)) ],
    };

    // The artist page reads the route reactively so ONE instance serves successive artist routes (a fans-also-like hop,
    // a track's artist link) in place — route-independent Key so KeepAlive reuses the mounted slot across artists.
    Element ArtistHost() => new BoxEl
    {
        Key = "page:artist", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children = [ Embed.Comp(() => new ArtistPage(_route)) ],
    };

    // album / playlist / liked / local / SHOW all flow through the one shared detail surface (DetailPage → DetailShell);
    // a show just renders Episodes instead of Tracks on the right (DetailConfig.Show.Content == Episodes).
    static bool IsDetail(Route r) =>
        r.Name.StartsWith("album:", StringComparison.Ordinal) || r.Name.StartsWith("pl:", StringComparison.Ordinal)
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

        if (r.Name == "search")
            return new BoxEl { Key = "page:search", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new SearchPage()) ] };

        if (r.Name == "albums" || r.Name == "artists" || r.Name == "podcasts")
            return new BoxEl { Key = "page:" + r.Name, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new LibraryPage(r.Name, _settings)) ] };

        if (DiscographyRoute.Is(r.Name))
            return new BoxEl { Key = "page:disco", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new DiscographyPage(new Signal<Route>(r))) ] };

        if (IsArtist(r)) return ArtistHost();
        if (IsDetail(r)) return DetailHost();

        var (title, glyph) = ShellNav.Dest(r);
        return new BoxEl
        {
            Key = "page:" + r.Name,
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Gap = WaveeSpace.M,
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
