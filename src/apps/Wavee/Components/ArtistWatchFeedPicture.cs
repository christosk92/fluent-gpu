using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The artist's circular profile picture, doubling as the entry point to their watch feed.
///
/// Spotify's <c>artistUnion.watchFeedEntrypoint</c> is populated on every artist we captured and Wavee dropped it
/// entirely; the artist page had a hero band but no profile picture at all. This is both: the watch feed's own still
/// when there is one, the avatar otherwise, in one control with one code path.
///
/// Clipping is belt-and-braces on purpose. <c>ClipToBounds</c> plus a full-radius <c>Corners</c> on the SAME node that
/// carries the media means the circular mask survives whichever path actually paints it — a rounded parent alone does
/// not reliably clip a child surface.</summary>
static class ArtistWatchFeedPicture
{
    /// <param name="watch">The artist's watch-feed entry point, or null → a plain avatar.</param>
    /// <param name="avatar">Fallback image when the watch feed has no still of its own.</param>
    /// <param name="onOpen">Invoked on click / Enter / Space. Null → a non-interactive portrait.</param>
    public static Element Create(ArtistWatchFeed? watch, Image? avatar, string displayName, float size,
                                 Action? onOpen = null)
    {
        Image? still = watch?.Thumbnail ?? avatar;
        bool interactive = onOpen is not null && watch is not null;
        float radius = size / 2f;

        // The portrait itself. PersonPicture gives the WinUI initials fallback for an artist with no image at all, so a
        // missing photo is still an intentional-looking portrait rather than a hole.
        Element portrait = still?.Url is { Length: > 0 } url
            ? new BoxEl
            {
                Width = size, Height = size, ClipToBounds = true, Corners = CornerRadius4.All(radius),
                Fill = Surfaces.PlaceholderFor(url),
                Children = [Image(url, ImageFit.Cover, 1f, size * 2f, radius,
                                  placeholder: Surfaces.PlaceholderFor(url), blurHash: still.BlurHash)],
            }
            : PersonPicture.Create("", size, displayName: displayName);

        if (!interactive) return portrait;

        var layers = new System.Collections.Generic.List<Element>(4) { portrait };

        // The watch-feed loop, composited OVER the still (which stays as the poster underneath, so the circle is never
        // empty while the clip opens and never black if it fails). Only a resolved canvas URL qualifies — the mapper
        // sets CanvasUrl solely for `videoType: "URL"`, so an opaque file id or an absent video node leaves the still
        // in place. Reduced motion keeps the still and never starts a decoder.
        if (!Motion.ReducedMotion && watch?.CanvasUrl is { Length: > 0 } clip)
            layers.Add(new BoxEl
            {
                Width = size, Height = size, Corners = CornerRadius4.All(radius), ClipToBounds = true,
                HitTestPassThrough = true,   // the circle itself owns the click; the video must not eat it
                Children = [Embed.Comp(() => new WatchFeedClip { Url = clip, Size = size })],
            });

        // Hover ring — an accent inset stroke that reads as "this opens something", drawn INSIDE the circle so it
        // never changes the control's footprint (a growing outer ring would nudge the header layout).
        layers.Add(new BoxEl
        {
            Width = size, Height = size, Corners = CornerRadius4.All(radius),
            HitTestPassThrough = true,
            BorderWidth = 2f, BorderColor = ColorF.Transparent,
            HoverBorderColor = Tok.AccentDefault,
        });

        // Play scrim — hidden at rest, so the artist's face is never obscured until the user reaches for it.
        layers.Add(new BoxEl
        {
            Width = size, Height = size, Corners = CornerRadius4.All(radius), ClipToBounds = true,
            HitTestPassThrough = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Opacity = 0f, HoverOpacity = 1f,
            HoverDurationMs = 160f, HoverEasing = Easing.FluentDecelerate,
            Fill = ColorF.FromRgba(0, 0, 0) with { A = 0.34f },
            Children =
            [
                new BoxEl
                {
                    Width = size * 0.3f, Height = size * 0.3f, Corners = CornerRadius4.All(size * 0.15f),
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Fill = ColorF.FromRgba(255, 255, 255) with { A = 0.94f },
                    Children = [Icon(Icons.Play, size * 0.15f, ColorF.FromRgba(17, 17, 17))],
                },
            ],
        });

        return new BoxEl
        {
            Width = size, Height = size, ZStack = true, Shrink = 0f,
            Corners = CornerRadius4.All(radius), ClipToBounds = true,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            FocusVisualMargin = new Edges4(3f, 3f, 3f, 3f),
            // Reduced motion keeps the affordances but drops the scale — the ring and scrim still say "interactive".
            HoverScale = Motion.ReducedMotion ? 1f : 1.045f,
            HoverDurationMs = 180f, HoverEasing = Easing.FluentDecelerate,
            PressScale = Motion.ReducedMotion ? 1f : 0.99f,
            OnClick = onOpen,
            Children = layers.ToArray(),
        };
    }
}
