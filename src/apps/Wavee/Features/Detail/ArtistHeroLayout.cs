namespace Wavee.Features.Detail;

/// <summary>Pure width→height rule for the artist hero so narrow pages can rebalance into a taller banner.</summary>
public static class ArtistHeroLayout
{
    public const float WideHeight = 420f;
    public const float NarrowHeight = 640f;
    public const float WideWidth = 900f;
    public const float NarrowWidth = 420f;
    public const float PhotoFadeBand = 260f;
    /// <summary>How far past the hero the translucent accent wash keeps painting before releasing to alpha 0 —
    /// the wash dissolves THROUGH the first content band rather than cutting off at the hero's bottom edge.</summary>
    public const float ContentBlendTail = 320f;

    public static float HeroHeightFor(float width)
    {
        if (width <= 0f) return WideHeight;
        if (width <= NarrowWidth) return NarrowHeight;
        if (width >= WideWidth) return WideHeight;

        float t = (width - NarrowWidth) / (WideWidth - NarrowWidth);
        return NarrowHeight + (WideHeight - NarrowHeight) * t;
    }

    public static float BlendBackdropHeightFor(float width) => HeroHeightFor(width) + ContentBlendTail;

    public static float BlendBoundaryFor(float width)
    {
        float h = HeroHeightFor(width);
        return h / (h + ContentBlendTail);
    }

}
