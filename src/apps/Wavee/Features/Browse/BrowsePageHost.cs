using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using Wavee.Core;
using Wavee.Features.Concerts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Features.Browse;

/// <summary>Route host for a browse category page: resolves the page uri out of the route, wires navigation/playback
/// from the shell, and provides the model the page reads.
///
/// Mirrors <c>ConcertRoutePage</c> — the route-parsing and service-wiring live here so <see cref="BrowsePage"/> stays a
/// pure view over its model.</summary>
sealed class BrowsePageHost : Component
{
    readonly Route _route;

    public BrowsePageHost(Route route) => _route = route;

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var svc = UseContext(Services.Slot);

        string pageUri = BrowseRoutes.UriOf(_route.Name);
        if (pageUri.Length == 0) return new BoxEl { Grow = 1f };

        var model = new BrowsePage.Model(
            PageUri: pageUri,
            OnOpenCategory: (uri, title) => go(BrowseRoutes.Page(uri), title),
            // A client feature is not a browse page: Live Events carries featureUri "spotify:concerts" and routes into
            // the Concerts hub Wavee already has.
            OnOpenFeature: uri => go(FeatureRoute(uri), null),
            Go: go,
            Play: uri => { if (svc is not null) _ = svc.Player.PlayAsync(uri, 0); },
            OnExploreAll: () => go("search", null));

        return Ctx.Provide(BrowsePage.Props, model,
            ScrollView(new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                // 32 left/right to match the artist and Concerts page bodies — the category page previously sat at 16
                // while the directory it descends from sat at 36, so stepping into a category visibly shifted the
                // content leftward. One gutter across the browse tree and its neighbours.
                Padding = new Edges4(32f, Spacing.M, 32f, PlayerDock.Reserve + Spacing.XXL),
                Children = [Embed.Comp(() => new BrowsePage()) with { Key = "browse-page:" + pageUri }],
            }) with { Grow = 1f, MinHeight = 0f, ScrollKey = "browse:" + pageUri });
    }

    /// <summary>Map a BrowseClientFeature uri onto the client surface that owns it. Only Spotify's Live Events tile is
    /// known to appear here; anything else falls back to the entity route so a new feature opens *something* rather
    /// than silently doing nothing.</summary>
    static string FeatureRoute(string featureUri)
        => string.Equals(featureUri, "spotify:concerts", StringComparison.Ordinal)
            ? ConcertRoutes.Hub
            : featureUri;
}
