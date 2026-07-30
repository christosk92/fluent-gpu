using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public class ArtistHeroLayoutTests
{
    [Fact]
    public void HeroHeightFor_WideWidth_KeepsCurrentBannerHeight()
    {
        // 0.32·900 = 288 and 0.32·1200 = 384 — both under the 420 floor, so the wide branch is CONTINUOUS with the
        // interpolated branch at exactly WideWidth and the classic banner height still holds through ~1312px.
        Assert.Equal(420f, ArtistHeroLayout.HeroHeightFor(900f));
        Assert.Equal(420f, ArtistHeroLayout.HeroHeightFor(1200f));
    }

    [Fact]
    public void HeroHeightFor_NarrowWidths_GrowsProgressivelyTaller()
    {
        Assert.Equal(640f, ArtistHeroLayout.HeroHeightFor(420f));
        Assert.Equal(585f, ArtistHeroLayout.HeroHeightFor(540f));
        Assert.Equal(530f, ArtistHeroLayout.HeroHeightFor(660f));
    }

    [Fact]
    public void HeroHeightFor_MonotonicallyShrinksAsWidthWidens()
    {
        float narrow = ArtistHeroLayout.HeroHeightFor(420f);
        float medium = ArtistHeroLayout.HeroHeightFor(660f);
        float wide = ArtistHeroLayout.HeroHeightFor(900f);

        Assert.True(narrow > medium);
        Assert.True(medium > wide);
    }

    [Fact]
    public void HeroHeightFor_UltraWide_GrowsWithTheWindowUntilTheCap()
    {
        // 0.32·1312 = 419.84 — the last width the floor still wins, so the growth ramp starts here.
        Assert.Equal(420f, ArtistHeroLayout.HeroHeightFor(1312f), 3);
        Assert.Equal(480f, ArtistHeroLayout.HeroHeightFor(1500f), 3);
        Assert.Equal(544f, ArtistHeroLayout.HeroHeightFor(1700f), 3);
        // 0.32·1750 = 560 — the cap is reached exactly here and holds for every wider window.
        Assert.Equal(ArtistHeroLayout.MaxHeight, ArtistHeroLayout.HeroHeightFor(1750f), 3);
        Assert.Equal(ArtistHeroLayout.MaxHeight, ArtistHeroLayout.HeroHeightFor(2560f));
        Assert.Equal(ArtistHeroLayout.MaxHeight, ArtistHeroLayout.HeroHeightFor(3840f));
    }

    [Fact]
    public void HeroHeightFor_WideBranch_IsContinuousAndNonDecreasing()
    {
        float previous = ArtistHeroLayout.HeroHeightFor(ArtistHeroLayout.WideWidth);
        Assert.Equal(ArtistHeroLayout.WideHeight, previous);
        for (float w = 900f; w <= 4000f; w += 25f)
        {
            float h = ArtistHeroLayout.HeroHeightFor(w);
            Assert.True(h >= previous - 0.001f, $"hero height dipped at {w}: {h} < {previous}");
            Assert.InRange(h, ArtistHeroLayout.WideHeight, ArtistHeroLayout.MaxHeight);
            previous = h;
        }
    }

    [Fact]
    public void PhotoFadeBandFor_ExceedsTheParallaxDriftAndStaysBounded()
    {
        // The collapse parallax counter-translates the photo by +0.18·h, so the feather must be deeper than that at
        // EVERY hero height the ladder can produce, or the presented-height clip line reappears as a hard cut.
        foreach (float w in new[] { 320f, 420f, 660f, 900f, 1200f, 1500f, 1750f, 3840f })
        {
            float h = ArtistHeroLayout.HeroHeightFor(w);
            float band = ArtistHeroLayout.PhotoFadeBandFor(h);
            Assert.True(band > h * 0.18f, $"fade band {band} does not cover the {h * 0.18f} parallax drift at h={h}");
            Assert.InRange(band, 120f, 180f);
        }
    }

    [Fact]
    public void PhotoFadeBandFor_ClampsBothEndsAndTracksHeightBetween()
    {
        Assert.Equal(120f, ArtistHeroLayout.PhotoFadeBandFor(0f));            // floor
        Assert.Equal(120f, ArtistHeroLayout.PhotoFadeBandFor(420f), 3);       // 0.28·420 = 117.6 → floor
        Assert.Equal(134.4f, ArtistHeroLayout.PhotoFadeBandFor(480f), 3);
        Assert.Equal(156.8f, ArtistHeroLayout.PhotoFadeBandFor(560f), 3);
        Assert.Equal(179.2f, ArtistHeroLayout.PhotoFadeBandFor(640f), 3);
        Assert.Equal(180f, ArtistHeroLayout.PhotoFadeBandFor(1000f), 3);      // cap
    }

    [Theory]
    [InlineData(420f)]
    [InlineData(660f)]
    [InlineData(900f)]
    [InlineData(1200f)]
    [InlineData(1500f)]
    [InlineData(2560f)]
    public void BlendBackdrop_ExtendsPastHeroAndReleasesInContent(float width)
    {
        float hero = ArtistHeroLayout.HeroHeightFor(width);
        float backdrop = ArtistHeroLayout.BlendBackdropHeightFor(width);

        Assert.Equal(ArtistHeroLayout.ContentBlendTail, backdrop - hero);
        Assert.Equal(hero / backdrop, ArtistHeroLayout.BlendBoundaryFor(width), 5);
    }

    [Fact]
    public void BlendBoundary_SitsLateEnoughThatTheWashIsAlreadyThinAtTheSeam()
    {
        // The short tail (96) is the point: the boundary stop must land well past 3/4 of the backdrop, so the wash is
        // spent by the hero's edge instead of holding a tint across the whole first content band.
        foreach (float w in new[] { 420f, 660f, 900f, 1500f, 2560f })
            Assert.True(ArtistHeroLayout.BlendBoundaryFor(w) > 0.8f,
                $"blend boundary {ArtistHeroLayout.BlendBoundaryFor(w)} at width {w} is too early");
    }
}
