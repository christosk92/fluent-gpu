using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Committed-query genre results. Fetches <see cref="SearchFacet.Genres"/> (a separate Pathfinder op) so the
/// All-tab list is not capped by the unified top-results page.
///
/// Rendered as plain text links in <see cref="LinkColumns"/> — the same recipe Browse's directory uses, and
/// deliberately indistinguishable from a browse-category link. The genre's accent still means something on the page it
/// opens, which is where it stays; a wall of 30 coloured plates here was decoration, not navigation.</summary>
sealed class SearchGenreTiles : Component
{
    readonly string _query;
    readonly Action<string, string?> _go;

    public SearchGenreTiles(string query, Action<string, string?> go)
    {
        _query = query;
        _go = go;
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        if (svc is null || _query.Length == 0) return new BoxEl();
        var results = UseResource(
            ct => svc.Library.SearchAsync(_query, SearchFacet.Genres, 0, 30, ct),
            SearchResults.Empty, _query).Loadable;
        // The shimmer must stay NON-zero-height and smoothResize must stay false — both are load-bearing for a
        // separate engine issue. 8 rows reads as a plausible genre list at every column count.
        return SearchSectionRoot.Stretch(Skel.Region(results, () => LinkColumns.Skeleton(8),
            r => Grid(r.Genres, _go),
            isEmpty: r => r.Genres is not { Count: > 0 },
            onEmpty: () => new BoxEl(),
            onFailed: () => new BoxEl(),
            smoothResize: false));
    }

    internal static Element Grid(IReadOnlyList<SearchGenre>? genres, Action<string, string?> go, bool header = true)
    {
        if (genres is not { Count: > 0 }) return new BoxEl();
        var items = new LinkColumns.Item[genres.Count];
        for (int i = 0; i < genres.Count; i++)
        {
            var g = genres[i];
            items[i] = new LinkColumns.Item(g.Name, g.Uri, () => SearchRoutes.OpenGenre(g.Uri, g.Name, go));
        }
        Element body = LinkColumns.Create(items);
        if (!header) return body;
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children = [SearchChrome.TickHeader(Loc.Get(Strings.Search.Genres)), body],
        };
    }
}

/// <summary>Related autocomplete queries from the same typeahead op, issued once on commit.</summary>
sealed class SearchRelatedQueries : Component
{
    readonly string _query;
    readonly Action<string, string?> _go;

    public SearchRelatedQueries(string query, Action<string, string?> go)
    {
        _query = query;
        _go = go;
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        if (svc is null || _query.Length == 0) return new BoxEl();
        var suggestions = UseResource(
            ct => svc.Library.SuggestRichAsync(_query, ct),
            SearchSuggestions.Empty, _query).Loadable;
        return SearchSectionRoot.Stretch(Skel.Region(suggestions, () => new BoxEl(),
            s => Body(s.Queries, _query, _go),
            isEmpty: s => s.Queries.Count == 0,
            onEmpty: () => new BoxEl(),
            onFailed: () => new BoxEl()));
    }

    static Element Body(IReadOnlyList<string> queries, string typed, Action<string, string?> go)
    {
        var links = new List<Element>(queries.Count);
        for (int i = 0; i < queries.Count; i++)
        {
            string q = queries[i];
            if (q.Equals(typed, StringComparison.OrdinalIgnoreCase)) continue;
            string committed = q;
            links.Add(RelatedLink(committed, go) with { Key = "rel:" + committed });
        }
        if (links.Count == 0) return new BoxEl();
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                SearchChrome.TickHeader(Loc.Get(Strings.Search.RelatedSearches)),
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.M, Wrap = true, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                    Children = links.ToArray(),
                },
            ],
        };
    }

    static BoxEl RelatedLink(string q, Action<string, string?> go) => new()
    {
        Shrink = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Button,
        OnClick = () => go("search", q),
        Children =
        [
            WaveeType.ModuleHeader(q.ToLowerInvariant()) with
            {
                Color = Tok.TextSecondary,
                HoverColor = Tok.AccentTextPrimary,
                Weight = 400,
                MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f,
            },
        ],
    };
}

/// <summary><see cref="ComponentEl"/> has no layout props — stretch has to live on the rendered root so a TickHeader
/// actually receives pane width instead of measuring as one glyph.</summary>
static class SearchSectionRoot
{
    internal static BoxEl Stretch(Element child) => new()
    {
        Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
        Children = [child],
    };
}
