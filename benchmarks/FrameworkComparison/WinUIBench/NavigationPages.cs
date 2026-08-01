using Bench.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinUIBench;

/// <summary>
/// The WinUI half of <see cref="BenchScenarios.PageNavigation"/>: the same two destination pages the FluentGPU host
/// builds, from the same <see cref="BenchWorkload"/> counts, sizes and string formats.
///
/// <para><b>Navigation is deliberately un-cached.</b> The host does not use a <c>Frame</c>: a <c>Frame</c> would bring
/// its page cache (<c>NavigationCacheMode</c>) and its own <c>NavigationTransitionInfo</c> animation, and the point of
/// the scenario is the cost of <em>constructing and rendering</em> a destination page. Building the page as a
/// <see cref="UIElement"/> tree in code and swapping it into a <see cref="ContentControl"/> is the same technique every
/// other scenario in this host uses, and it is strictly stronger than <c>NavigationCacheMode.Disabled</c>: nothing is
/// retained between navigations on either side. <see cref="ContentControl.ContentTransitions"/> is set to null so no
/// implicit content transition runs, matching the FluentGPU host, which has no transition either.</para>
///
/// <para>Brushes are created once and reused. The FluentGPU side paints from theme tokens, which allocate nothing per
/// navigation; charging WinUI for a fresh <see cref="SolidColorBrush"/> per element would be an artefact of this
/// harness rather than a property of the framework.</para>
/// </summary>
internal sealed partial class BenchmarkWindow
{
    private ContentControl? _navHost;
    private readonly SolidColorBrush _navCardBrush = new(Windows.UI.Color.FromArgb(255, 0x2E, 0x2E, 0x2E));
    private readonly SolidColorBrush _navThumbBrush = new(Windows.UI.Color.FromArgb(255, 0x46, 0x46, 0x46));
    private readonly SolidColorBrush _navAccentBrush = new(Windows.UI.Color.FromArgb(255, 0x00, 0x78, 0xD4));
    private readonly SolidColorBrush _navTextPrimary = new(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _navTextSecondary = new(Windows.UI.Color.FromArgb(255, 0xC8, 0xC8, 0xC8));
    private readonly SolidColorBrush _navTextTertiary = new(Windows.UI.Color.FromArgb(255, 0x96, 0x96, 0x96));

    private FrameworkElement BuildNavigationHost()
    {
        _navHost = new ContentControl
        {
            Width = BenchWorkload.WindowWidth,
            Height = BenchWorkload.WindowHeight,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            ContentTransitions = null,
            Content = BuildNavigationPage(0),
        };
        return _navHost;
    }

    private UIElement BuildNavigationPage(int iteration)
        => BenchWorkload.NavIsDetailPage(iteration) ? BuildDetailPage(iteration) : BuildLibraryPage(iteration);

    /// <summary>Page A: hero header, a 6 x 4 grid of 24 cards, and a 40-row three-column list.</summary>
    private UIElement BuildDetailPage(int iteration)
    {
        var hero = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = BenchWorkload.NavHeroHeight,
            Spacing = BenchWorkload.NavHeroGap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        hero.Children.Add(new Border
        {
            Width = BenchWorkload.NavHeroBoxWidth,
            Height = BenchWorkload.NavHeroBoxHeight,
            CornerRadius = new CornerRadius(BenchWorkload.NavCardCorner),
            Background = _navAccentBrush,
        });
        // Vertically centred against the 160-DIP hero box, matching the FluentGPU hero row's AlignItems=Center.
        var heroText = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = BenchWorkload.NavCardGap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        heroText.Children.Add(Label(BenchWorkload.NavHeroTitle(iteration), BenchWorkload.NavHeroTitleSize,
            BenchWorkload.NavHeroTitleHeight, _navTextPrimary));
        heroText.Children.Add(Label(BenchWorkload.NavHeroSubtitle(iteration), BenchWorkload.NavHeroSubtitleSize,
            BenchWorkload.NavHeroSubtitleHeight, _navTextSecondary));
        hero.Children.Add(heroText);

        var grid = new StackPanel { Orientation = Orientation.Vertical, Spacing = BenchWorkload.NavCardGap };
        int card = 0;
        for (int r = 0; r < BenchWorkload.NavCardRows; r++)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Height = BenchWorkload.NavCardHeight,
                Spacing = BenchWorkload.NavCardGap,
            };
            for (int c = 0; c < BenchWorkload.NavCardColumns; c++, card++)
            {
                // Two nodes where FluentGPU uses one: a WinUI Border cannot lay out a stack of children, so the
                // rounded card surface and the column that fills it are separate elements. That is a framework
                // property, not a harness choice - both hosts still produce one card with one thumbnail and two
                // text runs.
                var body = new StackPanel { Orientation = Orientation.Vertical, Spacing = BenchWorkload.NavCardInnerGap };
                body.Children.Add(new Border
                {
                    Width = BenchWorkload.NavCardThumb,
                    Height = BenchWorkload.NavCardThumb,
                    CornerRadius = new CornerRadius(BenchWorkload.NavTileCorner),
                    Background = _navThumbBrush,
                    // A fixed-width child of a stretch StackPanel centres by default; flex cross-axis stretch on the
                    // FluentGPU side leaves it at the start. Pin it so both cards look the same.
                    HorizontalAlignment = HorizontalAlignment.Left,
                });
                body.Children.Add(Label(BenchWorkload.NavCardTitle(card, iteration), BenchWorkload.NavCardTitleSize,
                    BenchWorkload.NavCardTitleHeight, _navTextPrimary));
                body.Children.Add(Label(BenchWorkload.NavCardSubtitle(card, iteration),
                    BenchWorkload.NavCardSubtitleSize, BenchWorkload.NavCardSubtitleHeight, _navTextSecondary));
                row.Children.Add(new Border
                {
                    Width = BenchWorkload.NavCardWidth,
                    Height = BenchWorkload.NavCardHeight,
                    Padding = new Thickness(BenchWorkload.NavCardPadding),
                    CornerRadius = new CornerRadius(BenchWorkload.NavCardCorner),
                    Background = _navCardBrush,
                    Child = body,
                });
            }
            grid.Children.Add(row);
        }

        var main = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = BenchWorkload.NavMainColumnWidth,
            Spacing = BenchWorkload.NavHeroSectionGap,
        };
        main.Children.Add(hero);
        main.Children.Add(grid);

        var side = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = BenchWorkload.NavSideListWidth,
        };
        for (int i = 0; i < BenchWorkload.NavDetailListRows; i++)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Height = BenchWorkload.NavDetailRowHeight,
                Spacing = BenchWorkload.NavDetailRowGap,
                Padding = new Thickness(BenchWorkload.NavDetailRowPadding, 0, BenchWorkload.NavDetailRowPadding, 0),
            };
            row.Children.Add(Cell(BenchWorkload.NavDetailCell(i, 0, iteration), BenchWorkload.NavDetailTextSize,
                BenchWorkload.NavDetailTextHeight, BenchWorkload.NavDetailCol0Width, _navTextPrimary));
            row.Children.Add(Cell(BenchWorkload.NavDetailCell(i, 1, iteration), BenchWorkload.NavDetailTextSize,
                BenchWorkload.NavDetailTextHeight, BenchWorkload.NavDetailCol1Width, _navTextSecondary));
            row.Children.Add(Cell(BenchWorkload.NavDetailCell(i, 2, iteration), BenchWorkload.NavDetailTextSize,
                BenchWorkload.NavDetailTextHeight, BenchWorkload.NavDetailCol2Width, _navTextTertiary));
            side.Children.Add(row);
        }

        var page = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Width = BenchWorkload.WindowWidth,
            Height = BenchWorkload.WindowHeight,
            Padding = new Thickness(BenchWorkload.NavPagePadding),
            Spacing = BenchWorkload.NavColumnGap,
        };
        page.Children.Add(main);
        page.Children.Add(side);
        return page;
    }

    /// <summary>Page B: two-line header, a 5 x 8 grid of 40 tiles, and a 20-row two-column list.</summary>
    private UIElement BuildLibraryPage(int iteration)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Height = BenchWorkload.NavLibraryHeaderHeight,
        };
        header.Children.Add(Label(BenchWorkload.NavLibraryHeading(iteration), BenchWorkload.NavLibraryTitleSize,
            BenchWorkload.NavLibraryTitleHeight, _navTextPrimary));
        header.Children.Add(Label(BenchWorkload.NavLibrarySubheading(iteration), BenchWorkload.NavLibrarySubtitleSize,
            BenchWorkload.NavLibrarySubtitleHeight, _navTextSecondary));

        var tiles = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = BenchWorkload.NavMainColumnWidth,
            Spacing = BenchWorkload.NavTileGap,
        };
        int tile = 0;
        for (int r = 0; r < BenchWorkload.NavTileRows; r++)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Height = BenchWorkload.NavTileHeight,
                Spacing = BenchWorkload.NavTileGap,
            };
            for (int c = 0; c < BenchWorkload.NavTileColumns; c++, tile++)
            {
                TextBlock label = Label(BenchWorkload.NavTileLabel(tile, iteration), BenchWorkload.NavTileTextSize,
                    BenchWorkload.NavTileTextHeight, _navTextPrimary);
                label.VerticalAlignment = VerticalAlignment.Bottom;
                row.Children.Add(new Border
                {
                    Width = BenchWorkload.NavTileWidth,
                    Height = BenchWorkload.NavTileHeight,
                    Padding = new Thickness(BenchWorkload.NavTilePadding),
                    CornerRadius = new CornerRadius(BenchWorkload.NavTileCorner),
                    Background = _navThumbBrush,
                    Child = label,
                });
            }
            tiles.Children.Add(row);
        }

        var side = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = BenchWorkload.NavSideListWidth,
        };
        for (int i = 0; i < BenchWorkload.NavLibraryListRows; i++)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Height = BenchWorkload.NavLibraryRowHeight,
                Spacing = BenchWorkload.NavLibraryRowGap,
                Padding = new Thickness(BenchWorkload.NavLibraryRowPadding, 0, BenchWorkload.NavLibraryRowPadding, 0),
            };
            row.Children.Add(Cell(BenchWorkload.NavLibraryCell(i, 0, iteration), BenchWorkload.NavLibraryTextSize,
                BenchWorkload.NavLibraryTextHeight, BenchWorkload.NavLibraryCol0Width, _navTextPrimary));
            row.Children.Add(Cell(BenchWorkload.NavLibraryCell(i, 1, iteration), BenchWorkload.NavLibraryTextSize,
                BenchWorkload.NavLibraryTextHeight, BenchWorkload.NavLibraryCol1Width, _navTextSecondary));
            side.Children.Add(row);
        }

        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = BenchWorkload.NavColumnGap };
        body.Children.Add(tiles);
        body.Children.Add(side);

        var page = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = BenchWorkload.WindowWidth,
            Height = BenchWorkload.WindowHeight,
            Padding = new Thickness(BenchWorkload.NavPagePadding),
            Spacing = BenchWorkload.NavLibraryHeaderGap,
        };
        page.Children.Add(header);
        page.Children.Add(body);
        return page;
    }

    private static TextBlock Label(string text, double size, double height, Brush foreground) => new()
    {
        Text = text,
        FontSize = size,
        Height = height,
        Foreground = foreground,
    };

    private static TextBlock Cell(string text, double size, double height, double width, Brush foreground)
    {
        TextBlock block = Label(text, size, height, foreground);
        block.Width = width;
        block.VerticalAlignment = VerticalAlignment.Center;
        return block;
    }
}
