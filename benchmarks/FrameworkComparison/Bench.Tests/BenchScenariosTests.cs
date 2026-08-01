using Bench.Contracts;
using Xunit;

namespace Bench.Tests;

public sealed class BenchScenariosTests
{
    [Fact]
    public void BothVirtualScrollScenariosAreEnumeratedAndAreNotColdLoad()
    {
        Assert.Contains(BenchScenarios.VirtualScroll1K, BenchScenarios.All);
        Assert.Contains(BenchScenarios.VirtualScroll10K, BenchScenarios.All);
        Assert.True(BenchScenarios.IsKnown(BenchScenarios.VirtualScroll1K));
        Assert.False(BenchScenarios.IsColdLoad(BenchScenarios.VirtualScroll1K));
        Assert.False(BenchScenarios.IsColdLoad(BenchScenarios.VirtualScroll10K));
        Assert.All(BenchScenarios.All, s => Assert.True(BenchScenarios.IsKnown(s)));
    }

    [Fact]
    public void RowCountIsTheOnlyDifferenceBetweenTheTwoScrollScenarios()
    {
        Assert.Equal(BenchWorkload.VirtualItemCountSmall, BenchWorkload.RowsFor(BenchScenarios.VirtualScroll1K));
        Assert.Equal(BenchWorkload.VirtualItemCount, BenchWorkload.RowsFor(BenchScenarios.VirtualScroll10K));
        Assert.Equal(1_000, BenchWorkload.VirtualItemCountSmall);
        Assert.Equal(10_000, BenchWorkload.VirtualItemCount);
        Assert.Equal(
            BenchWorkload.ScrollResetPeriodFor(BenchScenarios.VirtualScroll10K),
            BenchWorkload.ScrollResetPeriodFor(BenchScenarios.VirtualScroll1K));
    }

    [Theory]
    [InlineData(BenchScenarios.VirtualScroll1K)]
    [InlineData(BenchScenarios.VirtualScroll10K)]
    public void ScrollCursorWrapsBeforeItCanPinAtTheEndOfTheList(string scenario)
    {
        int rows = BenchWorkload.RowsFor(scenario);
        int period = BenchWorkload.ScrollResetPeriodFor(scenario);
        int index = 0;
        int highWater = 0;
        for (int iteration = 0; iteration < BenchWorkload.DefaultIterations; iteration++)
        {
            index = iteration > 0 && iteration % period == 0
                ? 0
                : index + BenchWorkload.VirtualScrollRowsPerOperation;
            highWater = Math.Max(highWater, index);
        }

        // Never clamps at the last row: the scenario keeps scrolling instead of degenerating into a no-op.
        Assert.True(highWater < rows, $"{scenario} reached row {highWater} of {rows}.");
    }

    [Fact]
    public void PageNavigationIsEnumeratedAndIsNotColdLoad()
    {
        Assert.Contains(BenchScenarios.PageNavigation, BenchScenarios.All);
        Assert.True(BenchScenarios.IsKnown(BenchScenarios.PageNavigation));
        Assert.False(BenchScenarios.IsColdLoad(BenchScenarios.PageNavigation));
        Assert.False(BenchScenarios.IsVirtualScroll(BenchScenarios.PageNavigation));
        Assert.Equal(24, BenchWorkload.NavCardCount);
        Assert.Equal(40, BenchWorkload.NavTileCount);
    }

    /// <summary>
    /// Every measured iteration navigates to the other destination, and no two consecutive navigations show the same
    /// strings - so a framework cannot serve a measured navigation out of a text cache the previous one populated.
    /// </summary>
    [Fact]
    public void NavigationAlternatesDestinationsAndStampsEveryIterationDistinctly()
    {
        for (int i = 0; i < BenchWorkload.DefaultIterations; i++)
        {
            Assert.NotEqual(BenchWorkload.NavIsDetailPage(i), BenchWorkload.NavIsDetailPage(i + 1));
            // Same page, two navigations apart: the structure repeats, the strings must not.
            Assert.NotEqual(BenchWorkload.NavCardTitle(0, i), BenchWorkload.NavCardTitle(0, i + 2));
            Assert.NotEqual(BenchWorkload.NavTileLabel(0, i), BenchWorkload.NavTileLabel(0, i + 2));
            Assert.NotEqual(BenchWorkload.NavDetailCell(0, 0, i), BenchWorkload.NavDetailCell(0, 0, i + 2));
            Assert.NotEqual(BenchWorkload.NavLibraryCell(0, 0, i), BenchWorkload.NavLibraryCell(0, 0, i + 2));
        }
        // Distinct cards/rows/tiles within one navigation, so the page is not one string repeated N times.
        Assert.NotEqual(BenchWorkload.NavCardTitle(0, 7), BenchWorkload.NavCardTitle(1, 7));
        Assert.NotEqual(BenchWorkload.NavTileLabel(0, 7), BenchWorkload.NavTileLabel(1, 7));
    }

    /// <summary>
    /// Both destination pages are laid out from explicit sizes that must add up to the shared 1200 x 720 DIP client
    /// area. If they did not, one framework's text metrics could push content out of the viewport and the two hosts
    /// would silently stop rendering the same page.
    /// </summary>
    [Fact]
    public void BothDestinationPagesFitTheSharedClientArea()
    {
        Assert.Equal(BenchWorkload.WindowWidth,
            2 * BenchWorkload.NavPagePadding + BenchWorkload.NavMainColumnWidth + BenchWorkload.NavColumnGap +
            BenchWorkload.NavSideListWidth);

        // Page A: hero + card grid down the main column; 40 rows down the side column.
        int cardGrid = BenchWorkload.NavCardRows * BenchWorkload.NavCardHeight +
                       (BenchWorkload.NavCardRows - 1) * BenchWorkload.NavCardGap;
        Assert.True(BenchWorkload.NavHeroHeight + BenchWorkload.NavHeroSectionGap + cardGrid <=
                    BenchWorkload.NavContentHeight, "detail page main column overflows the client area");
        Assert.True(BenchWorkload.NavCardColumns * BenchWorkload.NavCardWidth +
                    (BenchWorkload.NavCardColumns - 1) * BenchWorkload.NavCardGap <=
                    BenchWorkload.NavMainColumnWidth, "card row overflows the main column");
        Assert.True(2 * BenchWorkload.NavCardPadding + BenchWorkload.NavCardThumb +
                    2 * BenchWorkload.NavCardInnerGap + BenchWorkload.NavCardTitleHeight +
                    BenchWorkload.NavCardSubtitleHeight <= BenchWorkload.NavCardHeight, "card content overflows a card");
        Assert.True(2 * BenchWorkload.NavCardPadding + BenchWorkload.NavCardThumb <= BenchWorkload.NavCardWidth,
            "card thumbnail overflows a card");
        Assert.True(BenchWorkload.NavDetailListRows * BenchWorkload.NavDetailRowHeight <=
                    BenchWorkload.NavContentHeight, "detail list overflows the client area");
        Assert.True(2 * BenchWorkload.NavDetailRowPadding + 2 * BenchWorkload.NavDetailRowGap +
                    BenchWorkload.NavDetailCol0Width + BenchWorkload.NavDetailCol1Width +
                    BenchWorkload.NavDetailCol2Width <= BenchWorkload.NavSideListWidth, "detail row overflows its column");

        // Page B: header over a tile grid and a 20-row list.
        Assert.Equal(BenchWorkload.NavLibraryHeaderHeight,
            BenchWorkload.NavLibraryTitleHeight + BenchWorkload.NavLibrarySubtitleHeight);
        int tileGrid = BenchWorkload.NavTileRows * BenchWorkload.NavTileHeight +
                       (BenchWorkload.NavTileRows - 1) * BenchWorkload.NavTileGap;
        Assert.True(BenchWorkload.NavLibraryHeaderHeight + BenchWorkload.NavLibraryHeaderGap + tileGrid <=
                    BenchWorkload.NavContentHeight, "library tile grid overflows the client area");
        Assert.True(BenchWorkload.NavTileColumns * BenchWorkload.NavTileWidth +
                    (BenchWorkload.NavTileColumns - 1) * BenchWorkload.NavTileGap <=
                    BenchWorkload.NavMainColumnWidth, "tile row overflows the main column");
        Assert.True(2 * BenchWorkload.NavTilePadding + BenchWorkload.NavTileTextHeight <= BenchWorkload.NavTileHeight,
            "tile label overflows a tile");
        Assert.True(BenchWorkload.NavLibraryHeaderHeight + BenchWorkload.NavLibraryHeaderGap +
                    BenchWorkload.NavLibraryListRows * BenchWorkload.NavLibraryRowHeight <=
                    BenchWorkload.NavContentHeight, "library list overflows the client area");
        Assert.True(2 * BenchWorkload.NavLibraryRowPadding + BenchWorkload.NavLibraryRowGap +
                    BenchWorkload.NavLibraryCol0Width + BenchWorkload.NavLibraryCol1Width <=
                    BenchWorkload.NavSideListWidth, "library row overflows its column");
    }
}
