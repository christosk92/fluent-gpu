using System;

namespace Wavee.Features.Detail;

/// <summary>Which way the vertical (narrow) track-detail hero composes: artwork beside the info column, or stacked
/// above centered text.</summary>
public enum DetailHeroOrientation { SideBySide, Stacked }

/// <summary>Pure width→layout rules for the Apple-Music-inspired vertical track-detail hero. BCL-only (no FluentGpu
/// types) so it is source-included by Wavee.Tests. The persisted page-layout preference is an int (<see cref="PageAuto"/> ·
/// <see cref="PageHero"/>) that selects the page SYSTEM (rail-when-wide vs always-hero); the hero's own side-by-side ↔
/// stacked composition is always width-driven.</summary>
public static class DetailVerticalLayout
{
    // WaveeSettings.DetailPageLayout values: Automatic = the responsive rail↔hero behavior; Hero = the vertical hero
    // system at EVERY width (the metadata rail is never composed for track pages).
    public const int PageAuto = 0;
    public const int PageHero = 1;

    public const float HeroPad = 24f;
    public const float HeroGap = 24f;
    public const float StackBelowW = 440f;   // the hero stacks below this measured width
    public const float FallbackW = 540f;     // assumed width before the first bounds pass (unmeasured)

    /// <summary>Width-driven hero composition: stacked below the 440 threshold, side-by-side otherwise. An unmeasured
    /// width (≤ 0) uses the 540 fallback so the first frame composes side-by-side, then corrects.</summary>
    public static DetailHeroOrientation OrientationFor(float availableW)
    {
        float w = availableW > 0f ? availableW : FallbackW;
        return w < StackBelowW ? DetailHeroOrientation.Stacked : DetailHeroOrientation.SideBySide;
    }

    /// <summary>The hero artwork edge: a big centered cover when stacked, or a leading square ~36% of the width beside
    /// the info column. The wide side-by-side form gets a 256-DIP ceiling so the cover carries the complete metadata,
    /// actions, and description block; the stacked form stays at 240 so it does not dominate a narrow viewport.</summary>
    public static float ArtworkFor(float availableW, DetailHeroOrientation o)
    {
        float w = availableW > 0f ? availableW : FallbackW;
        return o == DetailHeroOrientation.Stacked
            ? Math.Clamp(w - 2f * HeroPad, 180f, 240f)
            : Math.Clamp(w * 0.36f, 160f, 256f);
    }

    /// <summary>Description line cap: a touch shorter beside the artwork, a touch taller when stacked.</summary>
    public static int DescriptionMaxLines(DetailHeroOrientation o) => o == DetailHeroOrientation.Stacked ? 4 : 3;
}
