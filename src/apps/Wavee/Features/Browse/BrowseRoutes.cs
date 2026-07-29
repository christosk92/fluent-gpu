using System;

namespace Wavee.Features.Browse;

/// <summary>Route naming for Browse category pages.
///
/// The directory itself has NO route — it is Search's empty state (type to search, don't type and you're browsing),
/// which is why there is no "browse" name here. Only a category PAGE needs its own route, so that opening one is
/// back-navigable and gets ContentHost's keep-alive caching (returning from a category to the directory is instant).
///
/// Follows the ConcertRoutes / DiscographyRoute idiom: a prefix plus the entity uri, parsed back out on render.</summary>
public static class BrowseRoutes
{
    public const string Prefix = "browse:";

    /// <summary>Route name for a category page uri ("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy").</summary>
    public static string Page(string pageUri) => Prefix + pageUri;

    public static bool Is(string routeName)
        => routeName.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The page uri carried by a browse route, or "" when the route is not one.</summary>
    public static string UriOf(string routeName)
        => Is(routeName) ? routeName.Substring(Prefix.Length) : "";
}
