using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// <c>FlexLayout</c>'s grid ignored its cells' Margin: an Auto track sized to the bare cell width, an auto row to the bare
/// cell height, and <c>ArrangeGrid</c> placed every cell at its full slot. SettingsCard's action chevron (Margin.Left 14
/// in an Auto track) therefore sat flush against the card's content, a margined cell filled its whole track, and a
/// cell with a vertical margin painted over the next row. The fix counts the margin in Auto tracks and auto rows and
/// insets each cell by it inside its slot, the rule ArrangeVirtual already applied.
/// </summary>
public sealed class GridCellMarginTests
{
    private sealed class Harness
    {
        public readonly SceneStore Scene = new();
        public readonly FlexLayout Layout;
        public readonly NodeHandle Root, Grid;

        public Harness(GridSpec spec)
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Layout = new FlexLayout(Scene, new HeadlessFontSystem(new StringTable()));
            Root = Scene.CreateNode(1);
            Scene.Layout(Root).Direction = 1;   // column; AlignItems defaults to Stretch
            Scene.Layout(Root).Width = 400f;
            Grid = Scene.CreateNode(2);
            Scene.AppendChild(Root, Grid);
            Scene.SetGrid(Grid, spec);
        }

        public NodeHandle Cell(float w, float h, Edges4 margin = default)
        {
            var c = Scene.CreateNode(3);
            Scene.AppendChild(Grid, c);
            Scene.Layout(c).Width = w;
            Scene.Layout(c).Height = h;
            Scene.Layout(c).Margin = margin;
            return c;
        }

        public NodeHandle Next()
        {
            var n = Scene.CreateNode(4);
            Scene.AppendChild(Root, n);
            Scene.Layout(n).Height = 10f;
            return n;
        }

        public void Run() => Layout.Run(Root, new Size2(400f, 400f));
    }

    private static GridSpec Tracks(float rowHeight, params TrackSize[] cols) => new() { Columns = cols, RowHeight = rowHeight };

    // SettingsCard.BuildRightRow's shape: [Star header | Auto content | Auto action(Margin.Left 14)].
    [Fact]
    public void AnAutoTrackHoldsItsCellsMarginAndPlacesTheCellInsetByIt()
    {
        var h = new Harness(Tracks(float.NaN, TrackSize.Star(), TrackSize.Auto, TrackSize.Auto));
        var header = h.Cell(float.NaN, 20f);
        var content = h.Cell(100f, 32f);
        var action = h.Cell(12f, 12f, new Edges4(14f, 0f, 0f, 0f));
        h.Run();

        var a = h.Scene.AbsoluteRect(action);
        var c = h.Scene.AbsoluteRect(content);
        Assert.Equal(388f, a.X, 2);
        Assert.Equal(12f, a.W, 2);
        Assert.Equal(274f, c.X, 2);   // the action track is 12 + 14 wide, not 12
        Assert.Equal(14f, a.X - (c.X + c.W), 2);   // was 0: the chevron sat flush against the content
        Assert.Equal(274f, h.Scene.AbsoluteRect(header).W, 2);
    }

    [Fact]
    public void AMarginedCellIsInsetInsideAFixedSlot()
    {
        var h = new Harness(Tracks(40f, TrackSize.Px(100f), TrackSize.Px(100f)));
        var first = h.Cell(float.NaN, float.NaN, new Edges4(10f, 5f, 10f, 5f));
        var second = h.Cell(float.NaN, float.NaN);
        h.Run();

        var r = h.Scene.AbsoluteRect(first);
        Assert.Equal(10f, r.X, 2);
        Assert.Equal(5f, r.Y, 2);
        Assert.Equal(80f, r.W, 2);   // was 100: the cell filled its whole track and touched its neighbour
        Assert.Equal(30f, r.H, 2);
        var s = h.Scene.AbsoluteRect(second);
        Assert.Equal(100f, s.X, 2);
        Assert.Equal(100f, s.W, 2);
    }

    [Fact]
    public void AnAutoRowCountsItsCellsVerticalMargin()
    {
        var h = new Harness(Tracks(float.NaN, TrackSize.Star()));
        var first = h.Cell(float.NaN, 20f, new Edges4(0f, 6f, 0f, 6f));
        var second = h.Cell(float.NaN, 20f);
        var next = h.Next();
        h.Run();

        Assert.Equal(6f, h.Scene.AbsoluteRect(first).Y, 2);
        Assert.Equal(32f, h.Scene.AbsoluteRect(second).Y, 2);   // was 20: the first cell's bottom margin overlapped it
        Assert.Equal(52f, h.Scene.AbsoluteRect(h.Grid).H, 2);
        Assert.Equal(52f, h.Scene.AbsoluteRect(next).Y, 2);
    }

    // A collapsed cell keeps its track at 0×0 (gate.presence.grid-cell-keeps-track): its margin must not hold the track open.
    [Fact]
    public void ACollapsedMarginedCellLeavesItsAutoTrackEmpty()
    {
        var h = new Harness(Tracks(float.NaN, TrackSize.Star(), TrackSize.Auto, TrackSize.Auto));
        h.Cell(float.NaN, 20f);
        var content = h.Cell(100f, 32f);
        var action = h.Cell(12f, 12f, new Edges4(14f, 0f, 0f, 0f));
        h.Scene.SetCollapsed(action, true);
        h.Run();

        Assert.Equal(300f, h.Scene.AbsoluteRect(content).X, 2);
    }
}
