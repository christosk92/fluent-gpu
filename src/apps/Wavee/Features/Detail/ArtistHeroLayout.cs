using System;
using FluentGpu.Dsl;

namespace Wavee.Features.Detail;

public enum ArtistHeroTier : byte { Narrow, Compact, Medium, Wide }
public enum ArtistHeroVeilAxis : byte { Horizontal, Vertical }

public readonly record struct ArtistCompactBarPolicy(bool ShowFollow);

public readonly record struct ArtistHeroMetrics(
    ArtistHeroTier Tier,
    float MinHeight,
    float Gutter,
    ArtistHeroVeilAxis VeilAxis,
    float CopyMaxWidth)
{
    public bool Stacked => VeilAxis == ArtistHeroVeilAxis.Vertical;
}

/// <summary>Responsive geometry for the full-bleed artist hero. The photo always fills the hero; pressure changes only
/// the copy placement and veil direction. Tier selection retains the detail layout's 24-DIP recovery band.</summary>
public static class ArtistHeroLayout
{
    public const float CompactWidth = 480f;
    public const float MediumWidth = 760f;
    public const float WideWidth = 1040f;
    public const float TierHysteresis = 24f;

    public const float WideHeight = 440f;
    public const float MediumHeight = 384f;
    public const float CompactHeight = 540f;
    public const float NarrowHeight = 516f;

    public const float WideCopyMaxWidth = 1120f;
    public const float MediumCopyMaxWidth = 760f;
    public const float CompactCopyMaxWidth = 640f;
    public const float NarrowCopyMaxWidth = 520f;
    public const float PhotoParallaxFraction = 0.15f;
    public const float ContentBlendTail = Spacing.XXXL * 3f;
    public const float CompactIdentityHeight = DetailVerticalLayout.CompactIdentityHeight;

    public static ArtistHeroTier TierFor(float width, ArtistHeroTier previous)
    {
        if (previous == ArtistHeroTier.Wide && width >= WideWidth - TierHysteresis) return previous;
        if (previous == ArtistHeroTier.Medium && width >= MediumWidth - TierHysteresis && width < WideWidth + TierHysteresis) return previous;
        if (previous == ArtistHeroTier.Compact && width >= CompactWidth - TierHysteresis && width < MediumWidth + TierHysteresis) return previous;

        if (width >= WideWidth + ((byte)previous < (byte)ArtistHeroTier.Wide ? TierHysteresis : 0f)) return ArtistHeroTier.Wide;
        if (width >= MediumWidth + ((byte)previous < (byte)ArtistHeroTier.Medium ? TierHysteresis : 0f)) return ArtistHeroTier.Medium;
        if (width >= CompactWidth + ((byte)previous < (byte)ArtistHeroTier.Compact ? TierHysteresis : 0f)) return ArtistHeroTier.Compact;
        return ArtistHeroTier.Narrow;
    }

    public static ArtistHeroMetrics For(float width, ArtistHeroTier previous)
    {
        var tier = TierFor(width, previous);
        return tier switch
        {
            ArtistHeroTier.Wide => new(tier, WideHeight, Spacing.PageWide, ArtistHeroVeilAxis.Horizontal, WideCopyMaxWidth),
            ArtistHeroTier.Medium => new(tier, MediumHeight, Spacing.XXXL, ArtistHeroVeilAxis.Horizontal, MediumCopyMaxWidth),
            ArtistHeroTier.Compact => new(tier, CompactHeight, Spacing.L, ArtistHeroVeilAxis.Vertical, CompactCopyMaxWidth),
            _ => new(tier, NarrowHeight, Spacing.PageNarrow, ArtistHeroVeilAxis.Vertical, NarrowCopyMaxWidth),
        };
    }

    public static float PageGutterFor(float width) => width >= WideWidth ? Spacing.PageWide
        : width >= MediumWidth ? Spacing.XXXL
        : width >= CompactWidth ? Spacing.L
        : Spacing.PageNarrow;

    public static float PhotoFadeBandFor(float height) => Math.Clamp(height * 0.28f, 120f, 180f);
    public static float CollapseDistance(float height) => MathF.Max(1f, height - CompactIdentityHeight);
    public static float ExpandedFadeStart(float collapseDistance) => DetailVerticalLayout.ExpandedFadeStart(collapseDistance);
    public static float CompactRevealStart(float collapseDistance) => DetailVerticalLayout.CompactRevealStart(collapseDistance);

    public static ArtistCompactBarPolicy CompactBarPolicyFor(ArtistHeroTier tier) => tier switch
    {
        ArtistHeroTier.Wide or ArtistHeroTier.Medium => new(true),
        _ => new(false),
    };

    public static float HeroHeightFor(float width) => width >= WideWidth ? WideHeight
        : width >= MediumWidth ? MediumHeight
        : width >= CompactWidth ? CompactHeight
        : NarrowHeight;
    public static float BlendBackdropHeightFor(float width) => HeroHeightFor(width) + ContentBlendTail;
    public static float BlendBoundaryFor(float width)
    {
        float height = HeroHeightFor(width);
        return height / (height + ContentBlendTail);
    }
}
