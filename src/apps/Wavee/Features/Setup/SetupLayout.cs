using System;

namespace Wavee;

/// <summary>The setup plate's width-pressure ladder. Wide is the approved 896×576 composition with the 192-DIP
/// hero rail; Compact drops that tertiary rail; Narrow stacks dense page-specific rows; UltraNarrow also stacks
/// the footer commands. Pure by construction so resize hysteresis is pinned in Wavee.Tests.</summary>
enum SetupLayoutTier { Wide, Compact, Narrow, UltraNarrow }

/// <summary>All setup sizing and tier decisions in one place. Narrowing happens immediately; widening needs a
/// 24-DIP recovery band so a live resize cannot chatter across a boundary.</summary>
static class SetupLayout
{
    public const float TargetWidth = 896f;
    public const float TargetHeight = 576f;
    public const float MinWidth = 300f;
    public const float MinHeight = 200f;
    public const float ViewportMargin = 32f;

    public const float HeroWidth = 192f;
    public const float HeroEnterWidth = 700f;
    public const float NarrowEnterWidth = 520f;
    public const float UltraNarrowEnterWidth = 360f;
    public const float TierHysteresisDip = 24f;

    public const float FooterHeight = 80f;
    public const float NarrowFooterHeight = 108f;
    public const float UltraNarrowFooterHeight = 144f;
    public const float ProgressLaneWidth = 210f;
    public const float ProgressWidth = 162f;
    public const float CompactPairingWidth = 196f;
    public const float CompactQrSize = 138f;
    public const float CompactDividerWidth = 40f;
    public const float SignInBodyMinHeight = 336f;
    public const float AgreementHeaderHeight = 44f;

    public static float PlateWidth(float viewportWidth) => viewportWidth > 0f
        ? Math.Clamp(TargetWidth, MinWidth, Math.Max(MinWidth, viewportWidth - ViewportMargin))
        : TargetWidth;

    public static float PlateHeight(float viewportHeight) => viewportHeight > 0f
        ? Math.Clamp(TargetHeight, MinHeight, Math.Max(MinHeight, viewportHeight - ViewportMargin))
        : TargetHeight;

    public static SetupLayoutTier NominalTierFor(float plateWidth) => plateWidth switch
    {
        >= HeroEnterWidth => SetupLayoutTier.Wide,
        >= NarrowEnterWidth => SetupLayoutTier.Compact,
        >= UltraNarrowEnterWidth => SetupLayoutTier.Narrow,
        _ => SetupLayoutTier.UltraNarrow,
    };

    public static SetupLayoutTier TierFor(float plateWidth, SetupLayoutTier current, bool initialized = true)
    {
        if (plateWidth <= 0f) return current;
        if (!initialized) return NominalTierFor(plateWidth);

        SetupLayoutTier nominal = NominalTierFor(plateWidth);
        if (nominal > current) return nominal; // pressure increased: drop structure immediately
        if (nominal == current) return current;

        // Pressure decreased: re-admit each rung only after its own 24-DIP recovery band.
        return current switch
        {
            SetupLayoutTier.Compact when plateWidth >= HeroEnterWidth + TierHysteresisDip
                => SetupLayoutTier.Wide,
            SetupLayoutTier.Narrow when plateWidth >= NarrowEnterWidth + TierHysteresisDip
                => NominalTierFor(plateWidth),
            SetupLayoutTier.UltraNarrow when plateWidth >= UltraNarrowEnterWidth + TierHysteresisDip
                => NominalTierFor(plateWidth),
            _ => current,
        };
    }

    public static bool ShowsHero(SetupLayoutTier tier) => tier == SetupLayoutTier.Wide;
    public static bool StacksSignIn(SetupLayoutTier tier) => tier >= SetupLayoutTier.Narrow;
    public static bool StacksFooter(SetupLayoutTier tier) => tier >= SetupLayoutTier.Narrow;
    public static bool StacksFooterActions(SetupLayoutTier tier) => tier == SetupLayoutTier.UltraNarrow;
    public static float FooterHeightFor(SetupLayoutTier tier) => tier switch
    {
        SetupLayoutTier.UltraNarrow => UltraNarrowFooterHeight,
        SetupLayoutTier.Narrow => NarrowFooterHeight,
        _ => FooterHeight,
    };
}
