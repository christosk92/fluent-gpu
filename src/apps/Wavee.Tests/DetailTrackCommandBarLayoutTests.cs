using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public class DetailTrackCommandBarLayoutTests
{
    static readonly DetailTrackCommandWidths Widths = new(120, 92, 96, 156, 144, 82);

    [Fact]
    public void WidePlaylist_LeavesEveryOptionalCommandInline_WithoutAutoExpandingSearch()
    {
        var fit = DetailTrackCommandBarLayout.Resolve(
            1000, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);

        Assert.False(fit.SearchExpanded);
        Assert.Equal(DetailTrackCommandBarLayout.SearchIconWidth, fit.SearchWidth);
        Assert.True(fit.Has(DetailTrackInlineCommand.Shuffle));
        Assert.True(fit.Has(DetailTrackInlineCommand.Sort));
        Assert.True(fit.Has(DetailTrackInlineCommand.Density));
        Assert.True(fit.Has(DetailTrackInlineCommand.Select));
    }

    [Fact]
    public void NarrowAlbum_PreservesRequiredTargetsAndOverflowsOptionalCommands()
    {
        var fit = DetailTrackCommandBarLayout.Resolve(
            150, Widths, vertical: false, hasTune: false, hasSelect: true, explicitSearch: false);

        Assert.False(fit.SearchExpanded);
        Assert.Equal(DetailTrackInlineCommand.None, fit.Inline);
        Assert.Equal(DetailTrackCommandBarLayout.SearchIconWidth, fit.SearchWidth);
    }

    [Fact]
    public void ExplicitSearch_StaysExpandedAtCompactWidth()
    {
        var fit = DetailTrackCommandBarLayout.Resolve(
            240, Widths, vertical: true, hasTune: false, hasSelect: false, explicitSearch: true);

        Assert.True(fit.SearchExpanded);
        Assert.True(fit.SearchWidth >= DetailTrackCommandBarLayout.SearchMinExplicit);
    }

    [Fact]
    public void PromotionUsesHysteresis()
    {
        var rich = DetailTrackCommandBarLayout.Resolve(
            800, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);
        var near = DetailTrackCommandBarLayout.Resolve(
            620, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false, rich);

        var fresh = DetailTrackCommandBarLayout.Resolve(
            620, Widths, vertical: false, hasTune: true, hasSelect: true, explicitSearch: false);
        Assert.True(near.Richness <= fresh.Richness);
    }
}
