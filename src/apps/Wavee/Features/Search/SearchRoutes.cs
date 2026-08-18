using System;
using Wavee.Features.Browse;

namespace Wavee;

/// <summary>Search-entity routes that are not a first-class page of their own. Genre IDs share the browse
/// <c>spotify:page:{id}</c> namespace; a URI we cannot parse falls back to committing the genre name as a search.</summary>
static class SearchRoutes
{
    const string GenrePrefix = "spotify:genre:";

    public static void OpenGenre(string uri, string name, Action<string, string?> go)
    {
        if (uri.StartsWith(GenrePrefix, StringComparison.Ordinal) && uri.Length > GenrePrefix.Length)
            go(BrowseRoutes.Page("spotify:page:" + uri[GenrePrefix.Length..]), name);
        else
            go("search", name.Length == 0 ? null : name);
    }
}
