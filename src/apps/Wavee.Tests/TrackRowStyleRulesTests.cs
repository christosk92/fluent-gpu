using Xunit;

namespace Wavee.Tests;

public sealed class TrackRowStyleRulesTests
{
    [Fact]
    public void Modern_UsesArtworkPreferenceAndKeepsArtistInTitle()
    {
        var shown = DetailTrackTableRules.IdentityColumns(
            classic: false, showArtThumb: true, artworkHidden: false, showTrackArtist: true, tier: 0);
        var hidden = DetailTrackTableRules.IdentityColumns(
            classic: false, showArtThumb: true, artworkHidden: true, showTrackArtist: true, tier: 0);

        Assert.True(shown.Thumb);
        Assert.False(shown.Artist);
        Assert.True(shown.ArtistInTitle);
        Assert.False(hidden.Thumb);
    }

    [Fact]
    public void Classic_ProjectsPlaylistAlbumAndCompilationIdentityColumns()
    {
        var playlist = DetailTrackTableRules.IdentityColumns(
            classic: true, showArtThumb: true, artworkHidden: false, showTrackArtist: true, tier: 0);
        var album = DetailTrackTableRules.IdentityColumns(
            classic: true, showArtThumb: false, artworkHidden: false, showTrackArtist: false, tier: 0);
        var compilation = DetailTrackTableRules.IdentityColumns(
            classic: true, showArtThumb: false, artworkHidden: false, showTrackArtist: true, tier: 0);

        Assert.Equal(new TrackIdentityColumns(Thumb: false, Artist: true, ArtistInTitle: false), playlist);
        Assert.Equal(new TrackIdentityColumns(Thumb: false, Artist: false, ArtistInTitle: false), album);
        Assert.Equal(new TrackIdentityColumns(Thumb: false, Artist: true, ArtistInTitle: false), compilation);
    }

    [Fact]
    public void Classic_ArtistSurvivesTierThreeAndFoldsAtTierFour()
    {
        var medium = DetailTrackTableRules.IdentityColumns(
            classic: true, showArtThumb: true, artworkHidden: false, showTrackArtist: true, tier: 3);
        var narrow = DetailTrackTableRules.IdentityColumns(
            classic: true, showArtThumb: true, artworkHidden: false, showTrackArtist: true, tier: 4);

        Assert.True(medium.Artist);
        Assert.False(medium.ArtistInTitle);
        Assert.False(narrow.Artist);
        Assert.True(narrow.ArtistInTitle);
    }

    [Theory]
    [InlineData(0, 36f)]
    [InlineData(1, 40f)]
    [InlineData(2, 44f)]
    [InlineData(3, 48f)]
    public void Classic_UsesTightIndependentDensityLadder(int density, float expected)
    {
        Assert.Equal(expected, DetailTrackTableRules.RowHeightFor(density, classic: true));
        Assert.Equal(32f, DetailTrackTableRules.HeaderHeightFor(classic: true));
    }

    [Fact]
    public void Classic_FoldsMediaChromeIntoTitleAndOverflow()
    {
        var classic = DetailTrackTableRules.TrailingColumns(
            classic: true, hasVideo: true, showVersions: true, tier: 0);
        var modern = DetailTrackTableRules.TrailingColumns(
            classic: false, hasVideo: true, showVersions: true, tier: 0);

        Assert.Equal(new TrackTrailingColumns(Video: false, Actions: true, Expand: false), classic);
        Assert.Equal(new TrackTrailingColumns(Video: true, Actions: false, Expand: true), modern);
        Assert.True(DetailTrackTableRules.ShowClassicInlineVideo(true, hasVideo: true, tier: 3));
        Assert.False(DetailTrackTableRules.ShowClassicInlineVideo(true, hasVideo: true, tier: 4));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void VersionsMenu_IsClassicSingleTrackOnly(bool classic, bool versions, bool single, bool expected)
    {
        Assert.Equal(expected, DetailTrackTableRules.ShowClassicVersionsMenu(classic, versions, single));
    }

    [Fact]
    public void DedicatedArtistColumn_SplitsTitleAndArtistSortCycles()
    {
        var titleAsc = DetailTrackTableRules.NextSort(DetailTrackSort.Default, SortColumn.Title, artistColumn: true);
        var titleDesc = DetailTrackTableRules.NextSort(titleAsc, SortColumn.Title, artistColumn: true);
        var titleDefault = DetailTrackTableRules.NextSort(titleDesc, SortColumn.Title, artistColumn: true);
        var artistAsc = DetailTrackTableRules.NextSort(DetailTrackSort.Default, SortColumn.Artist, artistColumn: true);
        var artistDesc = DetailTrackTableRules.NextSort(artistAsc, SortColumn.Artist, artistColumn: true);
        var artistDefault = DetailTrackTableRules.NextSort(artistDesc, SortColumn.Artist, artistColumn: true);

        Assert.Equal(new DetailTrackSort(SortColumn.Title, false), titleAsc);
        Assert.Equal(new DetailTrackSort(SortColumn.Title, true), titleDesc);
        Assert.Equal(DetailTrackSort.Default, titleDefault);
        Assert.Equal(new DetailTrackSort(SortColumn.Artist, false), artistAsc);
        Assert.Equal(new DetailTrackSort(SortColumn.Artist, true), artistDesc);
        Assert.Equal(DetailTrackSort.Default, artistDefault);
        Assert.False(DetailTrackTableRules.HeaderActive(SortColumn.Title, SortColumn.Artist, artistColumn: true));
        Assert.True(DetailTrackTableRules.HeaderActive(SortColumn.Artist, SortColumn.Artist, artistColumn: true));
    }

    [Fact]
    public void FoldedArtist_RestoresLegacyTitleArtistCycleAndOwnership()
    {
        var titleAsc = DetailTrackTableRules.NextSort(DetailTrackSort.Default, SortColumn.Title, artistColumn: false);
        var titleDesc = DetailTrackTableRules.NextSort(titleAsc, SortColumn.Title, artistColumn: false);
        var artistAsc = DetailTrackTableRules.NextSort(titleDesc, SortColumn.Title, artistColumn: false);
        var artistDesc = DetailTrackTableRules.NextSort(artistAsc, SortColumn.Title, artistColumn: false);
        var reset = DetailTrackTableRules.NextSort(artistDesc, SortColumn.Title, artistColumn: false);

        Assert.Equal(new DetailTrackSort(SortColumn.Title, false), titleAsc);
        Assert.Equal(new DetailTrackSort(SortColumn.Title, true), titleDesc);
        Assert.Equal(new DetailTrackSort(SortColumn.Artist, false), artistAsc);
        Assert.Equal(new DetailTrackSort(SortColumn.Artist, true), artistDesc);
        Assert.Equal(DetailTrackSort.Default, reset);
        Assert.True(DetailTrackTableRules.HeaderActive(SortColumn.Title, SortColumn.Artist, artistColumn: false));
    }
}
