// Engine-free by construction (System + FluentGpu.Localization + Wavee.Features.Browse only — no Element/Component,
// no engine reference) so DrillTrailTests can source-include it into Wavee.Tests, exactly like ShellNav.cs above it.
using System;
using System.Collections.Generic;
using FluentGpu.Localization;
using Wavee.Features.Browse;

namespace Wavee;

/// One crumb of a drill trail. RouteName == null marks the CURRENT page — the last crumb, never clickable.
readonly record struct DrillCrumb(string Label, string? RouteName, string? RouteArg = null);

/// <summary>The breadcrumb as a pure function of the ROUTE — the IA answer ("where does this sit"), never the
/// history answer ("how did I get here", which is Back/Forward's job). Same route ⇒ same trail: restore, a new
/// tab, a sidebar jump and a Home shortcut all agree. Roots return empty — not-a-drill-prefix IS the root test,
/// so there is no allow-list to maintain. A blank current label also returns empty: a trail that cannot name
/// where you ARE is a lone chevron, not information.</summary>
static class DrillTrail
{
    public static IReadOnlyList<DrillCrumb> Of(string routeName, string? routeArg, string? liveTitle)
    {
        string? label = !string.IsNullOrWhiteSpace(liveTitle) ? liveTitle.Trim()
                       : !string.IsNullOrWhiteSpace(routeArg) ? routeArg.Trim()
                       : null;
        if (label is null) return [];

        if (HomeSectionRoutes.Is(routeName))
            return [new(Loc.Get(Strings.Nav.Home), "home"), new(label, null)];

        // A Home-minted section drills to Home (its trail arm above), but a BROWSE section/category drills to
        // Browse — even when the tile that opened it lived on the Home page (e.g. a Home Charts Fold). Home was
        // a shortcut into Browse's IA there, never an ancestor of it, so the parent crumb must say Browse.
        //
        // The Browse crumb routes to "search" because the browse directory is Search's empty state and has no
        // route of its own (see BrowseRoutes.cs) — "search" is the closest real, back-navigable destination.
        if (BrowseSectionRoutes.Is(routeName) || BrowseRoutes.Is(routeName))
            return [new(Loc.Get(Strings.Browse.HomeTitle), "search"), new(label, null)];

        return [];
    }
}
