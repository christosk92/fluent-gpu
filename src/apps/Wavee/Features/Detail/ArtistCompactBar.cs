using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Core;
using Wavee.Features.Detail;

namespace Wavee;

static class ArtistCompactBar
{
    const float AvatarSize = 36f;
    const float DarkFallbackPull = 0.18f;
    const float LightFallbackPull = 0.10f;

    public static Element Build(Artist artist, string uri, float width, ArtistHeroTier tier, float collapseDistance,
                                ColorF accent, Action play, bool canHit)
    {
        var policy = ArtistHeroLayout.CompactBarPolicyFor(tier);
        Element identity = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children =
            [
                Ui.BodyStrong(artist.Name) with
                {
                    MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };

        var children = new System.Collections.Generic.List<Element>(6)
        {
            new BoxEl
            {
                Width = AvatarSize, Height = AvatarSize, Shrink = 0f,
                Corners = Radii.Circle(AvatarSize), ClipToBounds = true,
                Children =
                [
                    Surfaces.Artwork(artist.Image, artist.Id.GetHashCode() & 0x7fffffff,
                        AvatarSize, AvatarSize, AvatarSize * 0.5f, decodePx: 256),
                ],
            },
            identity,
            WaveeCta.Play(accent, play),
        };
        if (policy.ShowFollow)
            children.Add(Embed.Comp(() => new FollowButton(uri, artist.Name)) with
            {
                Key = "artist-bar-follow:" + uri,
                SkeletonProxy = FollowButton.SkeletonShape,
            });

        float gutter = ArtistHeroLayout.PageGutterFor(width);
        Element row = new BoxEl
        {
            Key = $"artist-compact-row:{uri}:{(byte)tier}",
            Direction = 0, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = 1600f,
            Height = ArtistHeroLayout.CompactIdentityHeight,
            Padding = new Edges4(gutter, 0f, gutter, 0f), Gap = Spacing.M,
            AlignItems = FlexAlign.Center,
            Children = children.ToArray(),
        };
        Element content = new BoxEl
        {
            Direction = 0, Width = width, Height = ArtistHeroLayout.CompactIdentityHeight,
            Justify = FlexJustify.Center, Children = [row],
        };
        Element surface = new BoxEl
        {
            Width = width, Height = ArtistHeroLayout.CompactIdentityHeight, ZStack = true,
            // A SOLID accent-pulled surface, not acrylic: the acrylic composite clips by scissor only (the engine's
            // tier-2 rounded clip covers rects/images/gradients, never the frosted layer), so the frosted band painted
            // the full square at the content pane's rounded top-left — a lighter notch OUTSIDE the pane contour. A rect
            // fill takes the rounded clip for free, and over today's opaque non-Mica ladder the blur bought nothing.
            Fill = CompactSurface(accent),
            Children =
            [
                content,
                new BoxEl
                {
                    Width = width, Height = ArtistHeroLayout.CompactIdentityHeight,
                    Direction = 1, Justify = FlexJustify.End, HitTestVisible = false,
                    Children = [new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeSurfaceDefault }],
                },
            ],
        };
        return new BoxEl
        {
            Width = width, Height = ArtistHeroLayout.CompactIdentityHeight,
            ZStack = true, HitTestVisible = canHit, HitTestPassThrough = true,
            ScrollBinds =
            [
                new() { From = ScrollChannel.Offset, To = BindSink.Opacity,
                    Range = ScrollRange.Px(ArtistHeroLayout.CompactRevealStart(collapseDistance), collapseDistance),
                    OutStart = 0f, OutEnd = 1f, Ease = Easing.Linear },
                new() { From = ScrollChannel.Offset, To = BindSink.TransY,
                    Range = ScrollRange.Px(ArtistHeroLayout.CompactRevealStart(collapseDistance), collapseDistance),
                    OutStart = Spacing.XS, OutEnd = 0f, Ease = Easing.Linear },
            ],
            Children = [surface],
        };
    }

    /// <summary>The bar's opaque surface: the old acrylic recipe's FALLBACK arm — the colour the acrylic already
    /// resolved to wherever compositing was unavailable — so the look survives the acrylic's removal unchanged.</summary>
    static ColorF CompactSurface(ColorF accent)
    {
        var recipe = Tok.AcrylicFlyout;
        bool dark = Tok.Theme == ThemeKind.Dark;
        return ColorF.Lerp(recipe.Fallback, accent, dark ? DarkFallbackPull : LightFallbackPull);
    }

}
