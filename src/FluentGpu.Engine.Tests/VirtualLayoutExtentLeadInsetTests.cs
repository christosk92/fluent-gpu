using FluentGpu.Scene;
using FluentGpu.Scroll.Extent;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Item H of the 2026-09-25 artist-page RCA: the Top tracks chart's FIRST row sat 12 DIP (18 px at 150 %) left of the
/// rows below it. Evidence (verify bundle 20260925-030316-H-chart, keyed.tsv): row 1's LAID-OUT box at layoutX=258.917,
/// rows 2–5 at 270.917, own translation 0 on all — a layout fact, not a pose; the pixel queries name the same strip tiles.
/// The chart is a <see cref="FillRowVirtualLayout"/> with a 12-DIP lead inset (the halo-bleed gutter): every item's
/// <c>ItemRect</c> starts at the inset, but <see cref="VirtualLayoutExtent.OffsetOf"/> answered 0 for item 0 without
/// asking the layout, so the arrange (<c>OffsetOf(index) − origin</c>) put item 0 at the viewport's edge and the rest of
/// its column one gutter in.
/// </summary>
public class VirtualLayoutExtentLeadInsetTests
{
    const float Cross = 372f;

    static (FillRowVirtualLayout Layout, VirtualLayoutExtent Extent) Chart(int items = 10)
    {
        // The artist chart's shape (Artist.UI.Chart.cs → PagedShelf): 5 rows, 2 columns max, 12 gaps, 12-DIP gutters.
        var layout = new FillRowVirtualLayout(minCardW: 264f, maxCardW: 9999f, gap: 12f, rows: 5, maxColumns: 2,
            leadInset: 12f, trailInset: 12f);
        layout.SetViewport(773.083f, Cross);   // the widened viewport the engine feeds (shelf width + 2 gutters)
        return (layout, new VirtualLayoutExtent(layout, items, Cross, horizontal: true));
    }

    [Fact]
    public void The_first_item_starts_at_the_lead_inset_like_the_rest_of_its_column()
    {
        var (layout, ext) = Chart();
        Assert.Equal(layout.ItemRect(0, Cross).X, (float)ext.OffsetOf(0));
        Assert.Equal(12.0, ext.OffsetOf(0));
        Assert.Equal(ext.OffsetOf(1), ext.OffsetOf(0));     // one column ⇒ one main offset for all five rows
        Assert.Equal(ext.OffsetOf(4), ext.OffsetOf(0));
    }

    [Fact]
    public void Every_item_offset_is_its_layout_rect()
    {
        var (layout, ext) = Chart();
        for (int i = 0; i < 10; i++) Assert.Equal(layout.ItemRect(i, Cross).X, (float)ext.OffsetOf(i));
        Assert.Equal(ext.Total, ext.OffsetOf(10));            // one past the end is the whole extent
    }

    [Fact]
    public void A_layout_without_an_inset_still_starts_at_zero()
    {
        var layout = new FillRowVirtualLayout(minCardW: 150f, maxCardW: 200f, gap: 8f, rows: 1);
        layout.SetViewport(640f, 200f);
        var ext = new VirtualLayoutExtent(layout, 6, 200f, horizontal: true);
        Assert.Equal(0.0, ext.OffsetOf(0));
        Assert.Equal(0.0, new VirtualLayoutExtent(layout, 0, 200f, horizontal: true).OffsetOf(0));
    }
}
