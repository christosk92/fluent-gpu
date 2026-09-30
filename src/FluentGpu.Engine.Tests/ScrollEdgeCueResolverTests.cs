using FluentGpu.Foundation;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>An edge cue's fade IS the viewport's analytic edge feather (<c>AutoEdgeFade</c>, gpu-renderer.md §13.1e) — no
/// painted band over the content, no guessed surface colour. <see cref="ScrollEdgeCueResolver"/> is the one place a
/// scroller's EdgeCues / AutoEdgeFade / AutoEdgeFadeBand / EdgeFade props resolve.</summary>
public sealed class ScrollEdgeCueResolverTests
{
    [Fact]
    public void AFadeCueIsTheAnalyticFeather_WithTheStandardBand_AndNoChevron()
    {
        var r = ScrollEdgeCueResolver.Resolve(ScrollEdgeCues.Fade, authoredEdgeFade: false, autoEdgeFade: false, autoEdgeFadeBand: 0f);
        Assert.True(r.AutoEdgeFade);
        Assert.Equal(ScrollEdgeCueResolver.DefaultBandDip, r.AutoEdgeFadeBand);
        Assert.False(r.Chevron);
    }

    [Fact]
    public void AutoResolvesExactlyAsTheAppDefault()
        => Assert.Equal(ScrollEdgeCueResolver.Resolve(ScrollEdgeCuesDefaults.Default, false, false, 0f),
                        ScrollEdgeCueResolver.Resolve(ScrollEdgeCues.Auto, false, false, 0f));

    [Fact]
    public void AnAuthoredEdgeFadeWins_ButTheChevronStays()
    {
        var fade = ScrollEdgeCueResolver.Resolve(ScrollEdgeCues.Fade, authoredEdgeFade: true, autoEdgeFade: false, autoEdgeFadeBand: 0f);
        Assert.False(fade.AutoEdgeFade);
        Assert.Equal(0f, fade.AutoEdgeFadeBand);
        var chevron = ScrollEdgeCueResolver.Resolve(ScrollEdgeCues.FadeAndChevron, authoredEdgeFade: true, autoEdgeFade: false, autoEdgeFadeBand: 0f);
        Assert.False(chevron.AutoEdgeFade);
        Assert.True(chevron.Chevron);
    }

    [Fact]
    public void NoneOptsOutOfTheCue_ButAnExplicitAutoEdgeFadeKeepsItsOwnBand()
    {
        var none = ScrollEdgeCueResolver.Resolve(ScrollEdgeCues.None, false, autoEdgeFade: false, autoEdgeFadeBand: 0f);
        Assert.False(none.AutoEdgeFade);
        Assert.Equal(0f, none.AutoEdgeFadeBand);
        var own = ScrollEdgeCueResolver.Resolve(ScrollEdgeCues.None, false, autoEdgeFade: true, autoEdgeFadeBand: 24f);
        Assert.True(own.AutoEdgeFade);
        Assert.Equal(24f, own.AutoEdgeFadeBand);
    }

    [Fact]
    public void ADeclaredZeroOrNegativeBandResolvesToTheStandardOne()
    {
        Assert.Equal(ScrollEdgeCueResolver.DefaultBandDip, ScrollEdgeCueResolver.Resolve(ScrollEdgeCues.Fade, false, true, 0f).AutoEdgeFadeBand);
        Assert.Equal(ScrollEdgeCueResolver.DefaultBandDip, ScrollEdgeCueResolver.Resolve(ScrollEdgeCues.Fade, false, true, -3f).AutoEdgeFadeBand);
    }
}
