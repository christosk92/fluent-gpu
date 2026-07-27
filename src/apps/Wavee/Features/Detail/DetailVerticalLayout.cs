using System;

namespace Wavee.Features.Detail;

/// <summary>Which way the track-detail Hero composes: desktop side-by-side, immersive full-width, or compact thumbnail.</summary>
public enum DetailHeroOrientation { SideBySide, Immersive, Compact }

/// <summary>Pure width→layout rules for the Apple-Music-inspired vertical track-detail hero. BCL-only (no FluentGpu
/// types) so it is source-included by Wavee.Tests. The persisted page-layout preference is an int (<see cref="PageAuto"/> ·
/// <see cref="PageHero"/>) that selects the page SYSTEM (rail-when-wide vs always-hero); the hero's own side-by-side ↔
/// immersive composition is always width-driven.</summary>
public static class DetailVerticalLayout
{
    // WaveeSettings.DetailPageLayout values: Automatic = the responsive rail↔hero behavior; Hero = the vertical hero
    // system at EVERY width (the metadata rail is never composed for track pages).
    public const int PageAuto = 0;
    public const int PageHero = 1;

    public const float HeroPad = 24f;
    public const float HeroGap = 24f;
    public const float CompactIdentityHeight = 56f;
    public const float CompactArtworkSize = 36f;
    public const float CompactPillHeight = 44f;
    public const float CompactPlaySize = 40f;
    public const float CompactPillMaxWidth = 480f;
    public const float CompactPillViewportRatio = 0.46f;
    public const float ImmersiveIdentityTokenSize = 44f;
    public const float IdentityVisibleFraction = 0.68f;
    public const float MorphHysteresis = 24f;
    public const float ToolsEnterProgress = 0.62f;
    public const float ToolsExitProgress = 0.50f;
    public const float SideHeroBottomPad = 0f;
    public const float SideToolbarTopPad = 0f;
    public const float ExpandedToolbarTopPad = 8f;
    public const float ExpandedToolbarBottomPad = 4f;
    public const float ExpandedContentFadeDistance = 96f;
    public const float ExpandedToolbarFadeDistance = 72f;
    public const float ChromeHeaderHeight = 36f;
    public const float ChromeDividerHeight = 1f;
    public const float StickyClipInset = CompactIdentityHeight + ChromeHeaderHeight + ChromeDividerHeight; // 93 DIP
    public const float ImmersiveNominalW = 580f;
    public const float ImmersiveEnterW = 560f;
    public const float ImmersiveLeaveW = 600f;
    public const float CompactNominalW = 420f;
    public const float CompactEnterW = 400f;
    public const float CompactLeaveW = 440f;
    public const float CompactHeroPad = 16f;
    public const float CompactHeroGap = 14f;
    public const float CompactHeroArtworkSize = 96f;
    public const float MinimalHeroArtworkSize = 64f;
    public const float MinimalHeroEnterW = 340f;
    public const float SideArtworkSize = 200f;
    public const float FallbackW = ImmersiveNominalW;

    /// <summary>Nominal width-driven composition used for first layout and skeleton selection.</summary>
    public static DetailHeroOrientation OrientationFor(float availableW)
    {
        float w = availableW > 0f ? availableW : FallbackW;
        return w < CompactNominalW ? DetailHeroOrientation.Compact
            : w < ImmersiveNominalW ? DetailHeroOrientation.Immersive
            : DetailHeroOrientation.SideBySide;
    }

    /// <summary>Resize-hysteretic composition: side/immersive uses 560–600 DIP; immersive/compact uses 400–440 DIP.</summary>
    public static DetailHeroOrientation OrientationFor(float availableW, DetailHeroOrientation current, bool initialized)
    {
        if (!initialized) return OrientationFor(availableW);
        float w = availableW > 0f ? availableW : FallbackW;
        return current switch
        {
            DetailHeroOrientation.Compact => w >= CompactLeaveW
                ? w >= ImmersiveLeaveW ? DetailHeroOrientation.SideBySide : DetailHeroOrientation.Immersive
                : DetailHeroOrientation.Compact,
            DetailHeroOrientation.Immersive => w <= CompactEnterW
                ? DetailHeroOrientation.Compact
                : w >= ImmersiveLeaveW ? DetailHeroOrientation.SideBySide : DetailHeroOrientation.Immersive,
            _ => w <= CompactEnterW
                ? DetailHeroOrientation.Compact
                : w <= ImmersiveEnterW ? DetailHeroOrientation.Immersive : DetailHeroOrientation.SideBySide,
        };
    }

    /// <summary>Artwork steps from 200 DIP, to a full-width immersive square, to a 96/64-DIP compact thumbnail.</summary>
    public static float ArtworkFor(float availableW, DetailHeroOrientation o)
    {
        float w = availableW > 0f ? availableW : FallbackW;
        return o switch
        {
            DetailHeroOrientation.Immersive => MathF.Max(1f, w),
            DetailHeroOrientation.Compact => w < MinimalHeroEnterW
                ? MinimalHeroArtworkSize : CompactHeroArtworkSize,
            _ => SideArtworkSize,
        };
    }

    /// <summary>Description line cap: a touch shorter beside the artwork, a touch taller when immersive.</summary>
    public static int DescriptionMaxLines(DetailHeroOrientation o) => o switch
    {
        DetailHeroOrientation.Immersive => 4,
        DetailHeroOrientation.Compact => 0,
        _ => 3,
    };

    /// <summary>Scroll distance over which the expanded hero becomes the 56-DIP compact identity.</summary>
    public static float CollapseDistance(float expandedHeight)
        => MathF.Max(1f, expandedHeight - CompactIdentityHeight);

    /// <summary>Late, geometry-derived shared-transition edge. The shared source keeps 68% of its useful identity before
    /// it yields to the shy pill; immersive uses its 44-DIP bottom token, never the full-bleed background.</summary>
    public static float IdentityMorphEnterOffset(DetailHeroOrientation orientation, float artworkSize, float collapseDistance)
    {
        float raw = orientation switch
        {
            DetailHeroOrientation.SideBySide => HeroPad + artworkSize * (1f - IdentityVisibleFraction),
            DetailHeroOrientation.Compact => CompactHeroPad + artworkSize * (1f - IdentityVisibleFraction),
            _ => artworkSize - HeroPad - ImmersiveIdentityTokenSize * IdentityVisibleFraction,
        };
        float hi = MathF.Max(1f, collapseDistance - 48f);
        float lo = MathF.Min(72f, hi);
        return Math.Clamp(raw, lo, hi);
    }

    public static float IdentityMorphExitOffset(float enterOffset)
        => MathF.Max(0f, enterOffset - MorphHysteresis);

    /// <summary>The search/play cluster follows the identity rather than arriving in the same flush. On short heroes the
    /// progress curve wins; on immersive heroes the lower token edge keeps tools after the shared identity.</summary>
    public static float ToolsEnterOffset(float collapseDistance, float identityEnter)
        => MathF.Min(MathF.Max(1f, collapseDistance - 8f),
            MathF.Max(identityEnter + 16f, collapseDistance * ToolsEnterProgress));

    public static float ToolsExitOffset(float collapseDistance, float identityExit)
        => MathF.Min(MathF.Max(0f, collapseDistance - 12f),
            MathF.Max(identityExit + 8f, collapseDistance * ToolsExitProgress));

    public static float CompactPillWidthCap(float viewportWidth)
        => MathF.Min(CompactPillMaxWidth,
            MathF.Max(160f, MathF.Max(1f, viewportWidth) * CompactPillViewportRatio));

    /// <summary>Decode bucket for a full-width immersive cover. The source mapper retains the largest CDN rendition;
    /// this controls the decoded texture size without churning a cache key on every resize pixel.</summary>
    public static int ImmersiveArtworkDecodePx(float artworkSize)
        => artworkSize <= 384f ? 512 : 1024;
}
