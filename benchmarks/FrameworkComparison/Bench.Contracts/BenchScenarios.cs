namespace Bench.Contracts;

public static class BenchScenarios
{
    public const string Startup = "startup";
    public const string Buttons225 = "buttons-225";
    public const string Text1125 = "text-1125";
    public const string VirtualScroll1K = "virtual-scroll-1k";
    public const string VirtualScroll10K = "virtual-scroll-10k";
    public const string LocalizedTransform = "localized-transform";
    public const string LocalizedText = "localized-text";
    public const string TreeChurn = "tree-churn";
    public const string PageNavigation = "page-navigation";

    /// <summary>Every scenario a pass enumerates by default, in run order.</summary>
    public static readonly string[] All =
    [
        Startup,
        Buttons225,
        Text1125,
        VirtualScroll1K,
        VirtualScroll10K,
        LocalizedTransform,
        LocalizedText,
        TreeChurn,
        PageNavigation,
    ];

    public static bool IsKnown(string value) => Array.IndexOf(All, value) >= 0;

    public static bool IsColdLoad(string value)
        => value is Startup or Buttons225 or Text1125;

    /// <summary>
    /// The two virtualized-scroll scenarios. They differ only in row count: <see cref="VirtualScroll10K"/> is the
    /// robustness gauge, <see cref="VirtualScroll1K"/> the like-for-like comparison point. Everything else - row height,
    /// row content, rows moved per operation, and the reset period - is shared.
    /// </summary>
    public static bool IsVirtualScroll(string value)
        => value is VirtualScroll1K or VirtualScroll10K;
}

public static class BenchWorkload
{
    public const int WindowWidth = 1200;
    public const int WindowHeight = 720;
    public const int ButtonColumns = 15;
    public const int ButtonRows = 15;
    public const int TextColumns = 25;
    public const int TextRows = 45;
    public const int VirtualItemCount = 10_000;
    public const int VirtualItemCountSmall = 1_000;
    public const int VirtualRowHeight = 44;
    public const int VirtualScrollRowsPerOperation = 5;
    public const int VirtualScrollResetPeriod = 100;
    public const int LocalizedNodeCount = 1_000;
    public const int ChurnSubtreeNodes = 500;

    // ---------------------------------------------------------------------------------------------------------------
    // page-navigation: two structurally different destination pages, each CONSTRUCTED FRESH on every navigation.
    // Every count, size and string format below is shared by both hosts so the two page trees are the same workload;
    // the geometry is fully explicit (no text-metric-dependent sizing) so the two frameworks lay out the same boxes.
    // The numbers add up to exactly the 1200 x 720 DIP client area on both pages - see the assertions in
    // Bench.Tests/BenchScenariosTests.cs, which fail the build rather than let a page silently overflow the viewport.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Outer padding of both destination pages.</summary>
    public const int NavPagePadding = 10;
    /// <summary>Gap between the main column and the side list on both pages.</summary>
    public const int NavColumnGap = 12;
    /// <summary>Width of the side list column on both pages.</summary>
    public const int NavSideListWidth = 380;
    /// <summary>Width of the main (hero/grid) column: the client width minus padding, the gap and the side list.</summary>
    public const int NavMainColumnWidth = WindowWidth - 2 * NavPagePadding - NavColumnGap - NavSideListWidth;

    // Page A ("detail"): hero + 6x4 card grid + 40-row list.
    public const int NavHeroBoxWidth = 300;
    public const int NavHeroBoxHeight = 160;
    public const int NavHeroHeight = NavHeroBoxHeight;
    public const int NavHeroGap = 16;
    public const int NavHeroSectionGap = 8;
    public const float NavHeroTitleSize = 34f;
    public const int NavHeroTitleHeight = 42;
    public const float NavHeroSubtitleSize = 14f;
    public const int NavHeroSubtitleHeight = 18;
    public const int NavCardColumns = 6;
    public const int NavCardRows = 4;
    public const int NavCardCount = NavCardColumns * NavCardRows;
    public const int NavCardGap = 4;
    public const int NavCardWidth = 128;
    public const int NavCardHeight = 130;
    public const int NavCardCorner = 8;
    public const int NavCardPadding = 4;
    public const int NavCardInnerGap = 1;
    public const int NavCardThumb = 96;
    public const float NavCardTitleSize = 10f;
    public const int NavCardTitleHeight = 13;
    public const float NavCardSubtitleSize = 9f;
    public const int NavCardSubtitleHeight = 11;
    public const int NavDetailListRows = 40;
    public const int NavDetailRowHeight = 17;
    public const int NavDetailRowPadding = 2;
    public const int NavDetailRowGap = 4;
    public const int NavDetailCol0Width = 156;
    public const int NavDetailCol1Width = 120;
    public const int NavDetailCol2Width = 90;
    public const float NavDetailTextSize = 10f;
    public const int NavDetailTextHeight = 13;

    // Page B ("library"): two-line header + 5x8 tile grid + 20-row two-column list.
    public const float NavLibraryTitleSize = 20f;
    public const int NavLibraryTitleHeight = 26;
    public const float NavLibrarySubtitleSize = 12f;
    public const int NavLibrarySubtitleHeight = 16;
    public const int NavLibraryHeaderHeight = NavLibraryTitleHeight + NavLibrarySubtitleHeight;
    public const int NavLibraryHeaderGap = 8;
    public const int NavTileColumns = 5;
    public const int NavTileRows = 8;
    public const int NavTileCount = NavTileColumns * NavTileRows;
    public const int NavTileGap = 6;
    public const int NavTileWidth = 152;
    public const int NavTileHeight = 76;
    public const int NavTileCorner = 6;
    public const int NavTilePadding = 6;
    public const float NavTileTextSize = 11f;
    public const int NavTileTextHeight = 14;
    public const int NavLibraryListRows = 20;
    public const int NavLibraryRowHeight = 32;
    public const int NavLibraryRowPadding = 2;
    public const int NavLibraryRowGap = 4;
    public const int NavLibraryCol0Width = 230;
    public const int NavLibraryCol1Width = 130;
    public const float NavLibraryTextSize = 12f;
    public const int NavLibraryTextHeight = 15;

    /// <summary>Height available to page content inside the outer padding.</summary>
    public const int NavContentHeight = WindowHeight - 2 * NavPagePadding;

    /// <summary>
    /// Iteration <c>i</c> navigates to the detail page when <c>(i &amp; 1) == 0</c> and to the library page otherwise, so
    /// a measured iteration is always a navigation between two structurally different destinations - never a re-render
    /// of the page that is already up.
    /// </summary>
    public static bool NavIsDetailPage(int iteration) => (iteration & 1) == 0;

    // Every string a destination page shows is stamped with the navigation's iteration number, so no framework can
    // serve a measured navigation out of a text/shaping cache populated by an earlier one.
    public static string NavHeroTitle(int iteration) => $"Detail Page i{iteration:0000}";
    public static string NavHeroSubtitle(int iteration) => $"{NavCardCount} releases - updated i{iteration:0000}";
    public static string NavCardTitle(int card, int iteration) => $"Card {card:00} - i{iteration:0000}";
    public static string NavCardSubtitle(int card, int iteration) => $"Album {card:00} i{iteration:0000}";

    public static string NavDetailCell(int row, int column, int iteration) => column switch
    {
        0 => $"Track {row:00} i{iteration:0000}",
        1 => $"Artist {row:00}",
        _ => $"{iteration:0000}:{row:00}",
    };

    public static string NavLibraryHeading(int iteration) => $"Library i{iteration:0000}";
    public static string NavLibrarySubheading(int iteration) => $"{NavTileCount} items - updated i{iteration:0000}";
    public static string NavTileLabel(int tile, int iteration) => $"Tile {tile:00} i{iteration:0000}";

    public static string NavLibraryCell(int row, int column, int iteration) => column == 0
        ? $"Playlist {row:00} i{iteration:0000}"
        : $"{iteration:0000}/{row:00}";
    public const int DefaultWarmupFrames = 60;
    public const int DefaultIterations = 1_000;
    public const int FrameIdProbeSize = FrameIdProbe.SizePx;
    public const int FrameIdProbeMargin = FrameIdProbe.MarginPx;

    /// <summary>Row count of a virtualized-scroll scenario; the only thing that differs between the two.</summary>
    public static int RowsFor(string scenario)
        => scenario == BenchScenarios.VirtualScroll1K ? VirtualItemCountSmall : VirtualItemCount;

    /// <summary>
    /// Iterations between the two scroll resets back to row 0. A cycle advances
    /// <see cref="VirtualScrollRowsPerOperation"/> rows on every iteration except the reset, so it reaches row
    /// <c>(period - 1) * rowsPerOperation</c>; the clamp keeps that strictly inside the list for any row count, so the
    /// scenario scrolls instead of pinning at the end. Both shipped row counts resolve to
    /// <see cref="VirtualScrollResetPeriod"/>, which keeps the two scenarios identical in reset cadence as well.
    /// </summary>
    public static int ScrollResetPeriodFor(string scenario)
        => Math.Min(VirtualScrollResetPeriod, (RowsFor(scenario) - 1) / VirtualScrollRowsPerOperation + 1);
}
