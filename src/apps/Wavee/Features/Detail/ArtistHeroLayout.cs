using System;
using FluentGpu.Dsl;

namespace Wavee.Features.Detail;

/// <summary>Pure width→geometry rules for the artist hero — the height ladder, the page gutter the hero copy shares with
/// the content column, and the hero↔content blend depths.
/// <para>HEIGHT: at or below <see cref="NarrowWidth"/> the page rebalances into a tall banner
/// (<see cref="NarrowHeight"/>); between <see cref="NarrowWidth"/> and <see cref="WideWidth"/> it interpolates down to
/// <see cref="WideHeight"/>; at or above <see cref="WideWidth"/> it GROWS with the window at 0.32·w, clamped
/// [<see cref="WideHeight"/>, <see cref="MaxHeight"/>]. That clamp is what keeps the ladder continuous at 900
/// (0.32·900 = 288, so the floor holds); the banner first exceeds 420 at ~1312px and reaches the 560 cap at ~1750px, so
/// an ultra-wide window gets a banner in proportion instead of a 420 strip.</para>
/// <para>The photo feather and the wash tail are HEIGHT-relative / short, not flat and deep — see
/// <see cref="PhotoFadeBandFor"/> and <see cref="ContentBlendTail"/>.</para></summary>
public static class ArtistHeroLayout
{
    public const float WideHeight = 420f;
    public const float NarrowHeight = 640f;
    public const float WideWidth = 900f;
    public const float NarrowWidth = 420f;
    /// <summary>The ultra-wide growth cap. Past ~1750px the banner stops tracking the window: beyond this a hero costs
    /// more of the first screen than the content it introduces.</summary>
    public const float MaxHeight = 560f;
    /// <summary>The page gutter. The hero copy and the content column BOTH inset by this, so the artist name, the meta
    /// line and the first section band all start on one vertical. One step wider than the stock content gutter preserves
    /// the magazine rhythm without the old 48-DIP moat between the navigation rail and every artist-page heading.</summary>
    public const float PageGutter = Spacing.XXXL;
    /// <summary>How far past the hero the translucent accent wash keeps painting before releasing to alpha 0 — the wash
    /// dissolves THROUGH the top of the first content band rather than cutting off at the hero's bottom edge. SHORT on
    /// purpose: a tail long enough to span a whole band reads as a plate laid over that band, not as a release.</summary>
    public const float ContentBlendTail = 96f;

    public static float HeroHeightFor(float width)
    {
        if (width <= 0f) return WideHeight;
        if (width <= NarrowWidth) return NarrowHeight;
        // The floor makes this continuous with the branch below at exactly WideWidth (0.32·900 = 288 < 420).
        if (width >= WideWidth) return Math.Clamp(width * 0.32f, WideHeight, MaxHeight);

        float t = (width - NarrowWidth) / (WideWidth - NarrowWidth);
        return NarrowHeight + (WideHeight - NarrowHeight) * t;
    }

    /// <summary>The photo's bottom edge-fade depth for a hero of height <paramref name="h"/>. It MUST exceed the
    /// collapse parallax's counter-translate (+0.18·h) with margin, or the presented-height clip line reappears as a
    /// hard cut once the photo has drifted. Bounded [120, 180] because the band is dead weight at rest: the flat 260 it
    /// replaces was 62% of a 420 hero, so the photo's bottom third was permanently half-erased.</summary>
    public static float PhotoFadeBandFor(float h) => Math.Clamp(h * 0.28f, 120f, 180f);

    public static float BlendBackdropHeightFor(float width) => HeroHeightFor(width) + ContentBlendTail;

    public static float BlendBoundaryFor(float width)
    {
        float h = HeroHeightFor(width);
        return h / (h + ContentBlendTail);
    }

}
