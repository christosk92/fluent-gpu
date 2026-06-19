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

    static string SlotKey(PageSlot s) => s.TabId.ToString() + "\u001F" + s.Route.Name + "\u001F" + (s.Route.Arg ?? "");

    static Element DetailHost(Route r, DetailKind kind, string? id) => new BoxEl
    {
        Key = "page:" + r.Name, Grow = 1f, Direction = 1,
        Children = [ Embed.Comp(() => new DetailPage(kind, id)) ],
    };

    static Element PageFor(Route r)
    {
        if (r.Name == "home")
            return new BoxEl { Key = "page:home", Grow = 1f, Direction = 1,
                Children = [ Embed.Comp(() => new HomePage()) ] };

        if (r.Name == "history")
            return new BoxEl { Key = "page:history", Grow = 1f, Direction = 1,
                Children = [ Embed.Comp(() => new HistoryPage()) ] };

        // The shared detail page — playlist / album / single / liked (single resolved post-load by track count).
        // The Key folds in the route arg (the uri) so KeepAlive never shares a cached page across two albums.
        if (r.Name.StartsWith("album:", StringComparison.Ordinal))
            return DetailHost(r, DetailKind.Album, r.Name["album:".Length..]);
        if (r.Name.StartsWith("pl:", StringComparison.Ordinal))
            return DetailHost(r, DetailKind.Playlist, r.Name["pl:".Length..]);
        if (r.Name == "liked")
            return DetailHost(r, DetailKind.Liked, null);

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
