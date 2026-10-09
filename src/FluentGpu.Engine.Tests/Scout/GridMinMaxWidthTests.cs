using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// <c>FlexLayout.MeasureGrid</c> resolved an auto-fill grid's column count and row heights at the raw offered width and
/// only clamped the WIDTH by MinWidth/MaxWidth afterwards, while the column parent arranged it at the clamped width and
/// <c>ArrangeGrid</c> counted columns there. A MaxWidth=800 grid in a 1200 column measured 5 columns (3 rows) and painted
/// 3 columns (4 rows), the last row over the next sibling; a MinWidth grid measured rows too many and left an empty band.
/// The fix clamps the width before the tracks resolve, so measure and arrange count the same rows.
/// </summary>
public sealed class GridMinMaxWidthTests
{
    private const float MinCol = 200f;
    private const float Gap = 8f;
    private const float TileH = 100f;
    private const int Tiles = 12;

    /// <summary>root(column, <paramref name="pageW"/>, Stretch) ⊃ [ grid(auto-fill MinColWidth 200, gaps 8, Min/Max)
    /// ⊃ 12 × tile(h 100), next(h 20) ].</summary>
    private static (SceneStore Scene, NodeHandle Grid, NodeHandle[] Tile, NodeHandle Next) Build(float pageW, float minW, float maxW)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var layout = new FlexLayout(scene, new HeadlessFontSystem(new StringTable()));

        var root = scene.CreateNode(1);
        scene.Layout(root).Direction = 1;   // column; AlignItems defaults to Stretch
        scene.Layout(root).Width = pageW;

        var grid = scene.CreateNode(2);
        scene.AppendChild(root, grid);
        scene.SetGrid(grid, new GridSpec { MinColWidth = MinCol, ColGap = Gap, RowGap = Gap, RowHeight = float.NaN });
        scene.Layout(grid).MinW = minW;
        scene.Layout(grid).MaxW = maxW;

        var tile = new NodeHandle[Tiles];
        for (int i = 0; i < Tiles; i++)
        {
            tile[i] = scene.CreateNode(3);
            scene.AppendChild(grid, tile[i]);
            scene.Layout(tile[i]).Height = TileH;
        }

        var next = scene.CreateNode(4);
        scene.AppendChild(root, next);
        scene.Layout(next).Height = 20f;

        layout.Run(root, new Size2(pageW, 2000f));
        return (scene, grid, tile, next);
    }

    // At the clamped 800 three 200-min columns fit ((800 + 8) / 208 = 3), so twelve tiles are four rows.
    private static void AssertFourRowsAt800(SceneStore scene, NodeHandle grid, NodeHandle[] tile, NodeHandle next)
    {
        const float FourRows = 4f * TileH + 3f * Gap;   // 424
        var g = scene.AbsoluteRect(grid);
        var n = scene.AbsoluteRect(next);
        Assert.Equal(800f, g.W, 2);
        Assert.Equal(FourRows, g.H, 2);
        Assert.Equal(FourRows, n.Y, 2);
        Assert.Equal(3f * (TileH + Gap), scene.AbsoluteRect(tile[9]).Y, 2);   // tile 9 opens row 4
        foreach (var t in tile)
        {
            var r = scene.AbsoluteRect(t);
            Assert.True(r.Y + r.H <= n.Y + 0.01f, $"a tile (y={r.Y}, h={r.H}) paints over the next sibling (y={n.Y})");
        }
    }

    // Offered 1200 (5 columns, 3 rows), arranged at MaxWidth 800 (3 columns, 4 rows): measure used to report 3 rows (316).
    [Fact]
    public void AMaxWidthGridMeasuresTheRowsItArrangesAtTheClampedWidth()
    {
        var (scene, grid, tile, next) = Build(pageW: 1200f, minW: float.NaN, maxW: 800f);
        AssertFourRowsAt800(scene, grid, tile, next);
    }

    // Offered 500 (2 columns, 6 rows), arranged at MinWidth 800 (3 columns, 4 rows): measure used to report 6 rows (640).
    [Fact]
    public void AMinWidthGridMeasuresTheRowsItArrangesAtTheClampedWidth()
    {
        var (scene, grid, tile, next) = Build(pageW: 500f, minW: 800f, maxW: float.NaN);
        AssertFourRowsAt800(scene, grid, tile, next);
    }
}
