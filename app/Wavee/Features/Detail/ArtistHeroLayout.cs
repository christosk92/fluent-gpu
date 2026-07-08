namespace Wavee.Features.Detail;

/// <summary>Pure width→height rule for the artist hero so narrow pages can rebalance into a taller banner.</summary>
public static class ArtistHeroLayout
{
    public const float WideHeight = 420f;
    public const float NarrowHeight = 640f;
    public const float WideWidth = 900f;
    public const float NarrowWidth = 420f;

    public static float HeroHeightFor(float width)
    {
        if (width <= 0f) return WideHeight;
        if (width <= NarrowWidth) return NarrowHeight;
        if (width >= WideWidth) return WideHeight;

        float t = (width - NarrowWidth) / (WideWidth - NarrowWidth);
        return NarrowHeight + (WideHeight - NarrowHeight) * t;
    }
}
