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
    /// <summary>Live window resize: the window is resized every composed frame along a fixed trajectory while the
    /// desktop is sampled; measures whether the presented content matches the window it sits in.</summary>
    public const string WindowResize = "window-resize";
    /// <summary>Light/Dark theme switch on the realistic detail page; measures switch to first fully re-themed frame.</summary>
    public const string ThemeSwitch = "theme-switch";

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
        WindowResize,
        ThemeSwitch,
    ];

    public static bool IsKnown(string value) => Array.IndexOf(All, value) >= 0;

    /// <summary>
    /// Scenarios driven and observed from the desktop by <see cref="LiveWindowDriver"/> rather than by a per-frame
    /// mutation loop. They only exist display-paced (the observation IS the composed desktop), so the runner runs them
    /// on the cadence pass only, and the hosts run them through their ordinary interactive loop.
    /// </summary>
    public static bool IsDesktopDriven(string value)
        => value is WindowResize or ThemeSwitch;

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
    // ---------------------------------------------------------------------------------------------------------------
    // window-resize: the detail page laid out responsively (main column grows, the card grid re-wraps, the side list
    // stays docked right), framed by two edge strips that are part of the layout and two size probes that paint the size
    // the content was LAID OUT at. The trajectory is a corner drag: width and height move together every composed frame.
    // ---------------------------------------------------------------------------------------------------------------

    public const int ResizeMinWidth = 960;
    public const int ResizeMaxWidth = 1440;
    public const int ResizeMinHeight = 600;
    public const int ResizeMaxHeight = 840;
    /// <summary>Steps from the smallest to the largest size (and back): one full cycle is twice this.</summary>
    public const int ResizeHalfCycleSteps = 80;
    /// <summary>6 DIP per composed frame (9 px at 150 %): ~720 DIP/s at 120 Hz, a brisk but ordinary edge drag.</summary>
    public const int ResizeStepWidth = (ResizeMaxWidth - ResizeMinWidth) / ResizeHalfCycleSteps;
    public const int ResizeStepHeight = (ResizeMaxHeight - ResizeMinHeight) / ResizeHalfCycleSteps;
    /// <summary>Phase offset so step 0 is the shared 1200 x 720 start size.</summary>
    public const int ResizeStartPhase = (WindowWidth - ResizeMinWidth) / ResizeStepWidth;
    /// <summary>Top row holding the two size probes: margin + probe + margin.</summary>
    public const int ResizeProbeRowHeight = 64;
    public const int ResizeProbeSize = 48;
    public const int ResizeProbeMargin = 8;
    /// <summary>Client-space X of the height probe (the width probe sits at <see cref="ResizeProbeMargin"/>).</summary>
    public const int ResizeProbe2X = ResizeProbeMargin * 2 + ResizeProbeSize;
    /// <summary>Width of the right and bottom edge strips; they are layout children, so they sit exactly on the edge
    /// of whatever size the content was laid out at.</summary>
    public const int ResizeEdgeStrip = 6;
    public const byte ResizeEdgeR = 0xE0, ResizeEdgeG = 0x20, ResizeEdgeB = 0xB0;
    /// <summary>Card grid cell pitch: the wrap grid places a 128 x 130 card every 132 x 134 DIP.</summary>
    public const int ResizeCardCellWidth = NavCardWidth + NavCardGap;
    public const int ResizeCardCellHeight = NavCardHeight + NavCardGap;
    /// <summary>Window minimum the resize scenario allows (below the trajectory's minimum).</summary>
    public const int ResizeWindowMinWidth = 640;
    public const int ResizeWindowMinHeight = 400;

    /// <summary>Client size (DIP) of trajectory step <paramref name="step"/>; step 0 is 1200 x 720.</summary>
    public static (int Width, int Height) ResizeSizeDip(int step)
    {
        int period = 2 * ResizeHalfCycleSteps;
        int p = ((step + ResizeStartPhase) % period + period) % period;
        int tri = p <= ResizeHalfCycleSteps ? p : period - p;
        return (ResizeMinWidth + ResizeStepWidth * tri, ResizeMinHeight + ResizeStepHeight * tri);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // theme-switch: the detail page (page A, built once, theme-aware colours on both hosts) flips Light <-> Dark.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Measured switches per process (alternating direction); each waits for its fully re-themed frame plus a
    /// hold, so a switch costs ~10 composed frames and 1,000 of them would only repeat the same distribution.</summary>
    public const int ThemeMaxSwitches = 200;
    public const int ThemeWarmupSwitches = 10;
    /// <summary>Composed frames a switch must stay fully re-themed after it first is (catches flicker-back).</summary>
    public const int ThemeHoldFrames = 6;
    public const int ThemeTimeoutMs = 2000;

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
