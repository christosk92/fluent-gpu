using FluentGpu.Scene;
using FluentGpu.Scroll.Extent;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A measured <see cref="GridVirtualLayout"/> (RepeatLayout.GridAuto/GridFit) keeps the row heights it already measured
/// when the width or the item count changes. It used to reset every row to the estimate, outside SetMeasured, so no
/// anchor delta shifted the plan: a scrolled grid jumped to a different row on each resize-drag frame and each append.
/// </summary>
public sealed class MeasuredGridRowsTests
{
    const int Count = 400;          // 5 columns at 500 DIP: 80 rows
    const float Row = 250f;         // a real card row (cover + two text lines)
    const float Estimate = 120f;    // the GridFit default

    static VirtualLayoutExtent MeasuredGrid(out GridVirtualLayout layout)
    {
        layout = new GridVirtualLayout(0, 0f, gap: 0f, minCellWidth: 100f, estimate: Estimate);
        var ext = new VirtualLayoutExtent(layout, Count, 500f, horizontal: false);
        _ = ext.Total;   // the table exists before the first SetMeasured, as FlexLayout.ArrangeVirtual does
        layout.ResetMeasurePass(Count, 500f);
        for (int i = 0; i < Count; i++) ext.SetMeasured(i, Row, 0);
        Assert.Equal(40 * Row, ext.OffsetOf(200), 2);   // item 200 starts row 40
        return ext;
    }

    [Fact]
    public void AResizeDragFrameKeepsTheMeasuredRows()
    {
        var ext = MeasuredGrid(out var layout);
        ext.Cross = 500.7f;   // still 5 columns
        Assert.Equal(5, layout.EffectiveColumns(500.7f));
        Assert.Equal(40 * Row, ext.OffsetOf(200), 2);
        Assert.Equal(80 * Row, ext.Total, 2);
    }

    [Fact]
    public void AnAppendKeepsTheMeasuredRowsAndSeedsOnlyTheNewOnes()
    {
        var ext = MeasuredGrid(out _);
        ext.Resize(Count + 10);   // a page of items arrives: 2 new rows
        Assert.Equal(80 * Row + 2 * Estimate, ext.Total, 2);
        Assert.Equal(40 * Row, ext.OffsetOf(200), 2);
    }

    [Fact]
    public void AMeasurePassAfterAResizeStillShrinksARealizedRow()
    {
        var ext = MeasuredGrid(out var layout);
        ext.Cross = 500.7f;
        layout.ResetMeasurePass(Count, 500.7f);
        for (int i = 200; i < 205; i++) ext.SetMeasured(i, 200f, 0);   // row 40 re-measures shorter
        Assert.Equal(200f, ext.ExtentOf(200), 2);
        Assert.Equal(40 * Row, ext.OffsetOf(200), 2);   // the rows above keep their heights
        Assert.Equal(79 * Row + 200f, ext.Total, 2);
    }
}
