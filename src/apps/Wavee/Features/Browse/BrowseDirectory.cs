using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Features.Browse;

/// <summary>The Browse landing surface: an eyebrow, a title, and every category as a plain text link grouped into
/// bands (Top / For you / Genres / Mood &amp; activity / Charts / More).
///
/// Deliberately typographic rather than a wall of coloured tiles. This page is a table of contents — its job is to let
/// someone find one of ~70 categories fast, and text in alphabetised columns is scannable in a way that 70 colour
/// blocks are not. The category's own colour is kept for the page it opens, where it means something.
///
/// Rendered as Search's empty state: type to search, don't type and you're browsing.</summary>
sealed class BrowseDirectory : Component
{
    internal sealed record Model(Action<string, string> OnOpenCategory, Action<string> OnOpenFeature);
    internal static readonly Context<Model?> Props = new(null);

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var model = UseContext(Props);

        // One fetch per mount, on the engine's own loader. The service caches on a 6h TTL, so re-entering Browse inside
        // a session is instant and this never becomes a per-keystroke cost when the user clears the search box.
        //
        // The hand-rolled `_loading` signal this replaces could strand the page on its skeleton: it was flipped only
        // from inside an async continuation, so any path that did not reach that continuation left the shimmer up with
        // nothing to explain it. Skel.Region owns Pending / Ready / Empty / Failed, so "stuck loading" stops being a
        // state this component can express.
        var cats = UseResource(
            async ct => svc is null
                ? Array.Empty<BrowseCategory>()
                : await LoadAsync(svc, ct).ConfigureAwait(false),
            seed: Array.Empty<BrowseCategory>(),
            deps: svc is null ? 0 : 1).Loadable;

        // SkelReveal.None, not the default Soft: the CONTENT owns its entrance here (the title + each band cascade in
        // through WaveeEntrance below), and a block-level blur-reveal on top of that would fade the whole directory in
        // as one slab while its bands were still arriving — two entrances for one mount. Same entrance-vs-reveal split
        // SearchPage documents for its bound Songs list.
        // smoothResize:false for the same reason SearchRecents and the search facet body set it: this is a PAGE-level
        // region whose two branches differ by hundreds of DIP, not a section whose height nudges by a row. Easing that
        // makes the region clip its own directory into a strip that grows. WaveeEntrance below owns the entrance.
        return Skel.Region(cats, Skeleton, c => Body(c, model),
            reveal: SkelReveal.None, smoothResize: false,
            isEmpty: c => c.Count == 0,
            onEmpty: () => EmptyState.Build(Loc.Get(Strings.Browse.Unavailable)),
            onFailed: () => EmptyState.Build(Loc.Get(Strings.Browse.Unavailable)));
    }

    static Element Body(IReadOnlyList<BrowseCategory> categories, Model? model)
    {
        var groups = BrowseTaxonomy.Grouped(categories);
        // The directory is EAGER and mounts exactly once per Search-page mount (no virtualization anywhere in it), and
        // its band count is fixed by the taxonomy — the two conditions WaveeEntrance requires. So the title lands
        // first and the bands follow it 40ms apart, which is the whole Zune "the page assembles itself" moment on the
        // surface a user sees the instant they open Search.
        var children = new List<Element>(groups.Count * 2 + 2)
        {
            new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Margin = new Edges4(0f, 0f, 0f, Spacing.L),
                Animate = WaveeEntrance.Row(0),
                Children = [WaveeType.PageHero(Loc.Get(Strings.Browse.Title))],
            },
        };

        int band = 1;
        foreach (var (group, items) in groups)
            children.Add(Group(GroupLabel(group), items, model, band++));

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.L, MinWidth = 0f,
            // 16 (host) + 16 = 32 left, the same gutter as the category page, the artist page and the Concerts hub.
            // This used to be Spacing.XL (20), giving 36 — a width no other page in the app uses, so descending from
            // the directory into a category shifted the whole body sideways.
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.XL),
            Children = children.ToArray(),
        };
    }

    static async System.Threading.Tasks.Task<IReadOnlyList<BrowseCategory>> LoadAsync(Services svc, System.Threading.CancellationToken ct)
    {
        try
        {
            return await svc.Browse.GetCategoriesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }   // cancellation is the resource's own concern, not an empty result
        catch (Exception ex)
        {
            // The SERVICE logs transport failures; this also catches a mapper/grouping throw, which it does not see.
            // An unreachable browse is an empty directory, not a crash — but never an unexplained one.
            svc.Log?.Event(WaveeLogLevel.Warning, "browse", "browse.directory.fail",
                "browse directory load failed", ex: ex);
            return Array.Empty<BrowseCategory>();
        }
    }

    // One band: an eyebrow heading over a responsive column grid of text links. `index` is the band's position in the
    // entrance cascade (see Body) — the ONLY thing it is used for.
    static Element Group(string label, IReadOnlyList<BrowseCategory> items, Model? model, int index)
        => new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Animate = WaveeEntrance.Row(index),
            Children =
            [
                WaveeType.Eyebrow(label) with { Color = Tok.TextTertiary },
                LinkColumns.Create(ToItems(items, model)),
            ],
        };

    // Browse's categories as the shared grid's items. A null model means the directory is inert (no navigation host
    // yet) — the link still renders and still highlights, it just does nothing, exactly as before.
    static IReadOnlyList<LinkColumns.Item> ToItems(IReadOnlyList<BrowseCategory> items, Model? model)
    {
        var mapped = new LinkColumns.Item[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var c = items[i];
            mapped[i] = new LinkColumns.Item(c.Title, c.Uri, model is null ? Noop : () =>
            {
                // A client feature (Live Events) is NOT a browse page — it routes into the client's own surface.
                if (c.IsClientFeature) model.OnOpenFeature(c.Uri);
                else model.OnOpenCategory(c.Uri, c.Title);
            });
        }
        return mapped;
    }

    static readonly Action Noop = static () => { };

    /// <summary>The localised band heading. Membership is fixed in BrowseTaxonomy (uri-keyed, culture-independent);
    /// only the label translates, so the two concerns stay on opposite sides of the UI boundary.</summary>
    static string GroupLabel(BrowseGroup g) => g switch
    {
        BrowseGroup.Top => Loc.Get(Strings.Browse.Top),
        BrowseGroup.ForYou => Loc.Get(Strings.Browse.ForYou),
        BrowseGroup.Genres => Loc.Get(Strings.Browse.Genres),
        BrowseGroup.MoodActivity => Loc.Get(Strings.Browse.MoodActivity),
        BrowseGroup.Charts => Loc.Get(Strings.Browse.Charts),
        _ => Loc.Get(Strings.Browse.More),
    };

    // While the directory loads, mirror the REAL layout: eyebrow, big title, then grouped bands of column-major link
    // bars at the same column count and row rhythm the loaded page uses. The previous version drew three thin bands of
    // six bars, which read as an almost-blank page rather than "the directory is arriving" — the whole point of a
    // skeleton is that the content lands into its own shape without the page jumping.
    static Element Skeleton()
    {
        // Row counts per band, roughly matching the real taxonomy (a short Top band, then progressively longer ones).
        ReadOnlySpan<int> bandRows = [2, 4, 9, 6, 3];

        var children = new List<Element>(bandRows.Length + 1)
        {
            new BoxEl
            {
                Width = 320f, Height = 34f, Corners = CornerRadius4.All(6f), Fill = Tok.FillSubtleTertiary,
                Margin = new Edges4(0f, 0f, 0f, Spacing.L),
            },
        };

        foreach (int rows in bandRows)
            children.Add(new BoxEl
            {
                Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Width = 90f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleTertiary },
                    // Same responsive column count as the loaded directory, so the bars sit where the links will.
                    LinkColumns.Skeleton(rows),
                ],
            });

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.L, MinWidth = 0f,
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.XL),   // matches the loaded page exactly
            Children = children.ToArray(),
        }.Skeletonized(true);
    }
}
