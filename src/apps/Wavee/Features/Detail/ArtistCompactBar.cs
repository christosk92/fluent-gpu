using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;

namespace Wavee;

/// <summary>
/// The artist page's arm of the shared text-chrome <see cref="ContextBand"/>: <b>title · pivot · actions</b>.
///
/// <para><b>What this replaced.</b> The previous bar was an art-tinted 56-DIP band carrying a 36-DIP circular avatar,
/// the artist's name and the Play / Following capsules — i.e. a smaller copy of the hero that had just scrolled away,
/// which told the visitor nothing the hero had not already told them and cost the page's whole sticky budget to say
/// it. The avatar, the tint and the capsules are deleted.</para>
///
/// <para><b>The pivot is the point.</b> An artist page is a magazine — Popular, Singles &amp; EPs, Albums, Appears on,
/// About, Similar artists — and the one thing a visitor genuinely cannot do once the hero is gone is see where they
/// are in it or jump. So the band's middle is the page's OWN sections as text links, with a 2-DIP accent underline on
/// the one currently under the band. That underline is AccentSelection doing its actual job ("you are here"), the
/// same family as the tab strip's, not decoration — and it is the only accent in the band besides the primary Play.
/// The section titles are the page's existing localized strings; the pivot invents no vocabulary of its own.</para>
///
/// <para>The right cluster invokes the SAME handlers the old capsules did (the page's Play, and the shared library
/// follow toggle), now as text actions — see <see cref="WaveeCta.TextAction"/> and its fence.</para>
/// </summary>
static class ArtistCompactBar
{
    /// <summary>Build the band. <paramref name="pivot"/> is the page's live section list (it grows as extras land, so
    /// it is re-pushed to the pivot component as props, never frozen); <paramref name="anchors"/> is the registry the
    /// spy reads; <paramref name="scroll"/> is the page's already-published offset signal — the band adds no second
    /// scroll observer.</summary>
    public static Element Build(Artist artist, string uri, float width, float collapseDistance, ColorF accent,
                                Action play, bool canHit, ContextPivotItem[] pivot, SectionAnchors anchors,
                                IReadSignal<float> scroll)
    {
        float gutter = ArtistHeroLayout.PageGutterFor(width);
        float rowWidth = MathF.Min(MathF.Max(1f, width), WaveeSize.PageMaxW);
        float inner = MathF.Max(0f, rowWidth - 2f * gutter);

        string playLabel = Loc.Get(Strings.Artist.Play);
        // The follow leg is budgeted at its LONGER wording ("Following"), so the row does not re-fit when the toggle
        // flips — a pivot item appearing because the user unfollowed would be nonsense.
        string followLabel = Loc.Get(Strings.Artist.Following);
        Span<float> actionWidths =
        [
            ContextBandLayout.EstimateLabelWidth(playLabel, ContextBandLayout.ActionPadX),
            ContextBandLayout.EstimateLabelWidth(followLabel, ContextBandLayout.ActionPadX),
        ];
        float actionsW = ContextBandLayout.ActionsWidth(actionWidths);

        int items = Math.Min(pivot.Length, ContextPivot.MaxItems);
        Span<float> pivotWidths = stackalloc float[ContextPivot.MaxItems];
        for (int i = 0; i < items; i++)
            pivotWidths[i] = ContextBandLayout.EstimateLabelWidth(pivot[i].Label, ContextBandLayout.PivotPadX);

        var fit = ContextBandLayout.Resolve(inner,
            ContextBandLayout.EstimateLabelWidth(artist.Name, 0f), actionsW, pivotWidths[..items]);

        Element title = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Shrink = 1f, MaxWidth = fit.TitleWidth,
            Children = [ContextBand.Title(artist.Name)],
        };

        Element pivotCluster = Embed.Comp(
            new ContextPivot.Props(pivot, fit.PivotCount, ContextBandLayout.Height, accent),
            () => new ContextPivot(anchors, scroll))
            with { Key = "artist-pivot:" + uri, SkeletonProxy = EmptyShape };

        Element actions = new BoxEl
        {
            Direction = 0, Gap = ContextBandLayout.ActionGap, Shrink = 0f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                WaveeCta.TextAction(playLabel, play, primary: true),
                Embed.Comp(() => new FollowTextAction(uri, artist.Name)) with
                {
                    Key = "artist-band-follow:" + uri, SkeletonProxy = EmptyShape,
                },
            ],
        }.Skeletonized(false);

        Element row = ContextBand.Row(rowWidth, gutter,
            [
                title,
                pivotCluster,
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Height = 1f, HitTestVisible = false },
                actions,
            ]);

        // The band's CONTENT is centred at the page's 1600 measure like the magazine body under it, while the band's
        // extent stays full-bleed. It used to need that distinction because it was a painted surface and an opaque
        // plate stopping at the content measure would have left two transparent shoulders with live rows sliding
        // through them. It paints nothing now (the OFFSET model — see ContextBand), and the shoulders are covered by
        // the same thing the rest of the band is: the clip in ArtistPage.Body, which is full-bleed by construction.
        Element surface = new BoxEl
        {
            Direction = 0, Width = width, Height = ContextBandLayout.Height, Justify = FlexJustify.Center,
            Children = [row],
        };

        return new BoxEl
        {
            Width = width, Height = ContextBandLayout.Height,
            ZStack = true, HitTestVisible = canHit, HitTestPassThrough = true,
            Children = [surface, ContextBand.HairlineOverlay(width)],
        }.Reveal(ArtistHeroLayout.CompactRevealStart(collapseDistance), collapseDistance, Spacing.XS);
    }

    // The band paints nothing while the page is shimmering (it is invisible at offset 0 anyway); an empty proxy stops
    // the skeleton deriver inventing a phantom link row above the hero — the same reason the old bar was
    // Skeletonized(false).
    static Element EmptyShape() => new BoxEl();
}
