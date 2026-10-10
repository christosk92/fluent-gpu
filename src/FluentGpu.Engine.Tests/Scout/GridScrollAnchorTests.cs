using FluentGpu.Scene;
using FluentGpu.Scroll.Extent;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A measured grid (RepeatLayout.GridAuto/GridFit) scrolled so its top row is partly hidden anchors on the NEXT row, not
/// on the next cell of the hidden row: a height correction to the hidden row then shifts the frame, so the rows the user
/// is reading stay put. The anchor used to land on row r col 1 (same offset as the hidden row), every correction to that
/// row reported a zero delta, and all visible rows below moved by it (a resize drag drifted them down).
/// </summary>
public sealed class GridScrollAnchorTests
{
    const int Count = 30;           // 3 columns at 300 DIP: 10 rows
    const float Estimate = 100f;

    static VirtualLayoutExtent MeasuredGrid(out GridVirtualLayout layout)
    {
        layout = new GridVirtualLayout(3, 0f, gap: 0f, estimate: Estimate);
        var ext = new VirtualLayoutExtent(layout, Count, 300f, horizontal: false);
        _ = ext.Total;   // the table exists before the first SetMeasured, as FlexLayout.ArrangeVirtual does
        layout.ResetMeasurePass(Count, 300f);
        return ext;
    }

    [Fact]
    public void APartlyHiddenTopRowAnchorsOnTheFirstCellOfTheNextRow()
    {
        var ext = MeasuredGrid(out _);
        var feel = FeelProfiles.Standard;
        RealizeWindow w = Virtualizer.Plan(ext, 150.0, 0.0, 400.0, in feel, 0);   // row 1 (items 3..5) is half hidden
        Assert.Equal(3, w.VisibleFirst);
        Assert.Equal(6, w.AnchorIndex);   // row 2, not row 1 col 1
        RealizeWindow aligned = Virtualizer.Plan(ext, 100.0, 0.0, 400.0, in feel, 0);   // row 1 exactly at the top
        Assert.Equal(3, aligned.AnchorIndex);
        RealizeWindow sliver = Virtualizer.Plan(ext, 150.0, 0.0, 40.0, in feel, 0);   // row 2 starts below the viewport
        Assert.Equal(3, sliver.AnchorIndex);
    }

    [Fact]
    public void GrowingTheHiddenTopRowKeepsTheVisibleRowsOnScreen()
    {
        var ext = MeasuredGrid(out _);
        var feel = FeelProfiles.Standard;
        double p = 150.0;
        RealizeWindow w = Virtualizer.Plan(ext, p, 0.0, 400.0, in feel, 0);
        double screenBefore = ext.OffsetOf(6) - p;   // row 2's top on screen
        double delta = 0.0;
        for (int i = 3; i < 6; i++) delta += ext.SetMeasured(i, 160f, w.AnchorIndex);   // row 1 grows by 60
        Assert.Equal(60.0, delta, 2);   // the frame shifts by the hidden row's growth
        Assert.Equal(screenBefore, ext.OffsetOf(6) - (p + delta), 2);   // row 2 has not moved on screen
    }
}
