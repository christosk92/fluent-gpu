using System;
using Wavee.Features.Concerts;

namespace Wavee.Features.Browse;

/// <summary>Route naming for Browse. <see cref="Home"/> is the directory (exact name — it must not collide with the
/// <see cref="Prefix"/> category pages). Category pages keep the prefix-plus-uri idiom (ConcertRoutes / DiscographyRoute).
/// </summary>
public static class BrowseRoutes
{
    /// <summary>The Browse directory. Exact name; <see cref="Is"/> is prefix-only and does not match this.</summary>
    public const string Home = "browse";

    public const string Prefix = "browse:";

    public static bool IsHome(string routeName)
        => string.Equals(routeName, Home, StringComparison.Ordinal);

    /// <summary>Route name for a category page uri ("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy").</summary>
    public static string Page(string pageUri) => Prefix + pageUri;

    public static bool Is(string routeName)
        => routeName.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The page uri carried by a browse route, or "" when the route is not one.</summary>
    public static string UriOf(string routeName)
        => Is(routeName) ? routeName.Substring(Prefix.Length) : "";

    /// <summary>Map a BrowseClientFeature uri onto the client surface that owns it. Only Spotify's Live Events tile is
    /// known to appear here; anything else falls back to the entity route so a new feature opens *something* rather
    /// than silently doing nothing.</summary>
    public static string FeatureRoute(string featureUri)
        => string.Equals(featureUri, "spotify:concerts", StringComparison.Ordinal)
            ? ConcertRoutes.Hub
            : featureUri;
}
