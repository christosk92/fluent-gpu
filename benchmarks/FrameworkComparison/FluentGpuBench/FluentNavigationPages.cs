using Bench.Contracts;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpuBench;

/// <summary>
/// The two <see cref="BenchScenarios.PageNavigation"/> destination pages, each built from scratch on every navigation.
///
/// Nothing here is cached: unlike <c>tree-churn</c> (which alternates two element trees built once at startup and so
/// measures the reconciler's diff of a known-shaped swap), a measured navigation constructs the whole destination tree
/// - every <see cref="BoxEl"/>, every <see cref="TextEl"/>, every stamped string - inside the measured section, exactly
/// as a real application's navigation builds the page it is navigating to. Every geometry number and every string comes
/// from <see cref="BenchWorkload"/>, which the WinUI host consumes too, so both frameworks build the same page.
/// </summary>
internal static class FluentNavigationPages
{
    /// <summary>Page A: hero header, a 6 x 4 grid of 24 cards, and a 40-row three-column list.</summary>
    internal static Element BuildDetail(int iteration)
    {
        var cardRows = new Element[BenchWorkload.NavCardRows];
        int card = 0;
        for (int r = 0; r < cardRows.Length; r++)
        {
            var cards = new Element[BenchWorkload.NavCardColumns];
            for (int c = 0; c < cards.Length; c++, card++)
            {
                cards[c] = new BoxEl
                {
                    Direction = 1,
                    Width = BenchWorkload.NavCardWidth,
                    Height = BenchWorkload.NavCardHeight,
                    Padding = Edges4.All(BenchWorkload.NavCardPadding),
                    Gap = BenchWorkload.NavCardInnerGap,
                    Corners = CornerRadius4.All(BenchWorkload.NavCardCorner),
                    Fill = Tok.FillCardDefault,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = BenchWorkload.NavCardThumb,
                            Height = BenchWorkload.NavCardThumb,
                            Fill = Tok.FillControlSecondary,
                            Corners = CornerRadius4.All(BenchWorkload.NavTileCorner),
                        },
                        new TextEl(BenchWorkload.NavCardTitle(card, iteration))
                        {
                            Size = BenchWorkload.NavCardTitleSize,
                            Height = BenchWorkload.NavCardTitleHeight,
                        },
                        new TextEl(BenchWorkload.NavCardSubtitle(card, iteration))
                        {
                            Size = BenchWorkload.NavCardSubtitleSize,
                            Height = BenchWorkload.NavCardSubtitleHeight,
                            Color = Tok.TextSecondary,
                        },
                    ],
                };
            }
            cardRows[r] = new BoxEl
            {
                Direction = 0,
                Gap = BenchWorkload.NavCardGap,
                Height = BenchWorkload.NavCardHeight,
                Children = cards,
            };
        }

        var listRows = new Element[BenchWorkload.NavDetailListRows];
        for (int i = 0; i < listRows.Length; i++)
        {
            listRows[i] = new BoxEl
            {
                Direction = 0,
                Height = BenchWorkload.NavDetailRowHeight,
                Gap = BenchWorkload.NavDetailRowGap,
                Padding = new Edges4(BenchWorkload.NavDetailRowPadding, 0f, BenchWorkload.NavDetailRowPadding, 0f),
                AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl(BenchWorkload.NavDetailCell(i, 0, iteration))
                    {
                        Size = BenchWorkload.NavDetailTextSize,
                        Width = BenchWorkload.NavDetailCol0Width,
                        Height = BenchWorkload.NavDetailTextHeight,
                    },
                    new TextEl(BenchWorkload.NavDetailCell(i, 1, iteration))
                    {
                        Size = BenchWorkload.NavDetailTextSize,
                        Width = BenchWorkload.NavDetailCol1Width,
                        Height = BenchWorkload.NavDetailTextHeight,
                        Color = Tok.TextSecondary,
                    },
                    new TextEl(BenchWorkload.NavDetailCell(i, 2, iteration))
                    {
                        Size = BenchWorkload.NavDetailTextSize,
                        Width = BenchWorkload.NavDetailCol2Width,
                        Height = BenchWorkload.NavDetailTextHeight,
                        Color = Tok.TextTertiary,
                    },
                ],
            };
        }

        var hero = new BoxEl
        {
            Direction = 0,
            Height = BenchWorkload.NavHeroHeight,
            Gap = BenchWorkload.NavHeroGap,
            AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Width = BenchWorkload.NavHeroBoxWidth,
                    Height = BenchWorkload.NavHeroBoxHeight,
                    Fill = Tok.AccentDefault,
                    Corners = CornerRadius4.All(BenchWorkload.NavCardCorner),
                },
                new BoxEl
                {
                    Direction = 1,
                    Gap = BenchWorkload.NavCardGap,
                    Children =
                    [
                        new TextEl(BenchWorkload.NavHeroTitle(iteration))
                        {
                            Size = BenchWorkload.NavHeroTitleSize,
                            Height = BenchWorkload.NavHeroTitleHeight,
                        },
                        new TextEl(BenchWorkload.NavHeroSubtitle(iteration))
                        {
                            Size = BenchWorkload.NavHeroSubtitleSize,
                            Height = BenchWorkload.NavHeroSubtitleHeight,
                            Color = Tok.TextSecondary,
                        },
                    ],
                },
            ],
        };

        return Page("detail",
            new BoxEl
            {
                Direction = 1,
                Width = BenchWorkload.NavMainColumnWidth,
                Gap = BenchWorkload.NavHeroSectionGap,
                Children =
                [
                    hero,
                    new BoxEl { Direction = 1, Gap = BenchWorkload.NavCardGap, Children = cardRows },
                ],
            },
            new BoxEl { Direction = 1, Width = BenchWorkload.NavSideListWidth, Children = listRows });
    }

    /// <summary>Page B: two-line header, a 5 x 8 grid of 40 tiles, and a 20-row two-column list.</summary>
    internal static Element BuildLibrary(int iteration)
    {
        var tileRows = new Element[BenchWorkload.NavTileRows];
        int tile = 0;
        for (int r = 0; r < tileRows.Length; r++)
        {
            var tiles = new Element[BenchWorkload.NavTileColumns];
            for (int c = 0; c < tiles.Length; c++, tile++)
            {
                tiles[c] = new BoxEl
                {
                    Direction = 1,
                    Width = BenchWorkload.NavTileWidth,
                    Height = BenchWorkload.NavTileHeight,
                    Padding = Edges4.All(BenchWorkload.NavTilePadding),
                    Corners = CornerRadius4.All(BenchWorkload.NavTileCorner),
                    Fill = Tok.FillControlSecondary,
                    Justify = FlexJustify.End,
                    Children =
                    [
                        new TextEl(BenchWorkload.NavTileLabel(tile, iteration))
                        {
                            Size = BenchWorkload.NavTileTextSize,
                            Height = BenchWorkload.NavTileTextHeight,
                        },
                    ],
                };
            }
            tileRows[r] = new BoxEl
            {
                Direction = 0,
                Gap = BenchWorkload.NavTileGap,
                Height = BenchWorkload.NavTileHeight,
                Children = tiles,
            };
        }

        var listRows = new Element[BenchWorkload.NavLibraryListRows];
        for (int i = 0; i < listRows.Length; i++)
        {
            listRows[i] = new BoxEl
            {
                Direction = 0,
                Height = BenchWorkload.NavLibraryRowHeight,
                Gap = BenchWorkload.NavLibraryRowGap,
                Padding = new Edges4(BenchWorkload.NavLibraryRowPadding, 0f, BenchWorkload.NavLibraryRowPadding, 0f),
                AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl(BenchWorkload.NavLibraryCell(i, 0, iteration))
                    {
                        Size = BenchWorkload.NavLibraryTextSize,
                        Width = BenchWorkload.NavLibraryCol0Width,
                        Height = BenchWorkload.NavLibraryTextHeight,
                    },
                    new TextEl(BenchWorkload.NavLibraryCell(i, 1, iteration))
                    {
                        Size = BenchWorkload.NavLibraryTextSize,
                        Width = BenchWorkload.NavLibraryCol1Width,
                        Height = BenchWorkload.NavLibraryTextHeight,
                        Color = Tok.TextSecondary,
                    },
                ],
            };
        }

        var header = new BoxEl
        {
            Direction = 1,
            Height = BenchWorkload.NavLibraryHeaderHeight,
            Children =
            [
                new TextEl(BenchWorkload.NavLibraryHeading(iteration))
                {
                    Size = BenchWorkload.NavLibraryTitleSize,
                    Height = BenchWorkload.NavLibraryTitleHeight,
                },
                new TextEl(BenchWorkload.NavLibrarySubheading(iteration))
                {
                    Size = BenchWorkload.NavLibrarySubtitleSize,
                    Height = BenchWorkload.NavLibrarySubtitleHeight,
                    Color = Tok.TextSecondary,
                },
            ],
        };

        // The library page stacks header over body, where the detail page is two columns top to bottom: the two
        // destinations are deliberately different shapes, so navigating between them is a structural replacement and
        // never a same-shape property diff.
        return new BoxEl
        {
            Key = "library",
            Direction = 1,
            Width = BenchWorkload.WindowWidth,
            Height = BenchWorkload.WindowHeight,
            Padding = Edges4.All(BenchWorkload.NavPagePadding),
            Gap = BenchWorkload.NavLibraryHeaderGap,
            ClipToBounds = true,
            Children =
            [
                header,
                new BoxEl
                {
                    Direction = 0,
                    Gap = BenchWorkload.NavColumnGap,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1,
                            Width = BenchWorkload.NavMainColumnWidth,
                            Gap = BenchWorkload.NavTileGap,
                            Children = tileRows,
                        },
                        new BoxEl { Direction = 1, Width = BenchWorkload.NavSideListWidth, Children = listRows },
                    ],
                },
            ],
        };
    }

    private static Element Page(string key, Element main, Element side) => new BoxEl
    {
        Key = key,
        Direction = 0,
        Width = BenchWorkload.WindowWidth,
        Height = BenchWorkload.WindowHeight,
        Padding = Edges4.All(BenchWorkload.NavPagePadding),
        Gap = BenchWorkload.NavColumnGap,
        ClipToBounds = true,
        Children = [main, side],
    };
}
