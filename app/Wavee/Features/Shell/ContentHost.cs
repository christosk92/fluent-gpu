using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The content-card body opts routes into FluentGpu keep-alive caching. Route changes swap inside the boundary, scoped
// by the active browser tab, so same-route tabs never share page state.
sealed class ContentHost : Component
{
    readonly record struct PageSlot(int TabId, Route Route);

    readonly Signal<Route> _route;
    readonly Func<int> _activeTabId;
    public ContentHost(Signal<Route> route, Func<int> activeTabId) { _route = route; _activeTabId = activeTabId; }

    public override Element Render()
        => Flow.KeepAlive(() => new PageSlot(_activeTabId(), _route.Value), SlotKey, s => PageFor(s.Route), new KeepAliveOptions(MaxEntries: 8));

    // Detail routes collapse to ONE slot per tab (identity = tab × page-class; the route is content) so album→album
    // reconciles the mounted shell instead of cold-remounting. TabId stays in the key → tabs never share a detail slot.
    static string SlotKey(PageSlot s) => IsDetail(s.Route) ? s.TabId.ToString() + "\\u001Fdetail" : IsArtist(s.Route) ? s.TabId.ToString() + "\\u001Fartist" : s.Route.Name == "search" ? s.TabId.ToString() + "\\u001Fsearch" : s.TabId.ToString() + "\u001F" + s.Route.Name + "\u001F" + (s.Route.Arg ?? "");

    // The shared detail page (album / playlist / single / liked) reads the route reactively (via _route) and derives its
    // own kind/id, so ONE DetailPage instance serves successive detail routes in place. The Key is route-INDEPENDENT
    // ("page:detail") so KeepAlive reuses the mounted slot across albums — the swap reconciles + reloads in place rather
    // than tearing down and cold-remounting the rail / track-list / trailing.
    Element DetailHost() => new BoxEl
    {
        Key = "page:detail", Grow = 1f, Direction = 1,
        Children = [ Embed.Comp(() => new DetailPage(_route)) ],
    };

    // The artist page reads the route reactively so ONE instance serves successive artist routes (a fans-also-like hop,
    // a track's artist link) in place — route-independent Key so KeepAlive reuses the mounted slot across artists.
    Element ArtistHost() => new BoxEl
    {
        Key = "page:artist", Grow = 1f, Direction = 1,
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
            return new BoxEl { Key = "page:home", Grow = 1f, Direction = 1,
                Children = [ Embed.Comp(() => new HomePage()) ] };

        if (r.Name == "history")
            return new BoxEl { Key = "page:history", Grow = 1f, Direction = 1,
                Children = [ Embed.Comp(() => new HistoryPage()) ] };

        if (r.Name == "search")
            return new BoxEl { Key = "page:search", Grow = 1f, Direction = 1,
                Children = [ Embed.Comp(() => new SearchPage()) ] };

        if (r.Name == "albums" || r.Name == "artists" || r.Name == "podcasts")
            return new BoxEl { Key = "page:" + r.Name, Grow = 1f, Direction = 1,
                Children = [ Embed.Comp(() => new LibraryPage(r.Name)) ] };

        if (IsArtist(r)) return ArtistHost();
        if (IsDetail(r)) return DetailHost();

        var (title, glyph) = ShellNav.Dest(r);
        return new BoxEl
        {
            Key = "page:" + r.Name,
            Grow = 1f, Direction = 1, Gap = WaveeSpace.M,
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
