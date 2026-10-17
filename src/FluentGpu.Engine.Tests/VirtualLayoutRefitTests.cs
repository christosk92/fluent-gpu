using FluentGpu.Foundation;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// A mounted virtual layout re-fits in place when its host's band changes (a window-tier crossing): <see cref="FillRowVirtualLayout.SetFit"/>
/// (PagedShelf's live card band), <see cref="FillRowVirtualLayout.SetRows"/> (its live rows) and <see cref="GridVirtualLayout.SetMinCellWidth"/> (a bound ItemsView's live cell floor). The same instance
/// answers for the new band, so the viewport's extent source, its offset and its realized cells survive; the grid's re-fit lands through the
/// anchored-reseed seam, so the layout pass keeps the anchor row where the user sees it.
/// </summary>
public class VirtualLayoutRefitTests
{
    const float Main = 1000f, Cross = 300f;

    [Fact]
    public void A_fill_row_re_fits_to_a_new_band_at_the_viewport_it_was_fed()
    {
        var layout = new FillRowVirtualLayout(minCardW: 150f, maxCardW: 200f, gap: 12f);
        layout.SetViewport(Main, Cross);
        var wide = FillRowVirtualLayout.Fit(Main, 150f, 200f, 12f);
        Assert.Equal(wide.PerPage, layout.PerPage);

        Assert.True(layout.SetFit(164f, 164f, 0f, 164f, 0, 0));          // a Compact strip: fixed 164 cards, no gap
        var strip = FillRowVirtualLayout.Fit(Main, 164f, 164f, 0f, fixedCardW: 164f);
        Assert.Equal(strip.PerPage, layout.PerPage);
        Assert.Equal(strip.CardW, layout.CardW);
        Assert.Equal(164f, layout.FixedCardW);

        Assert.False(layout.SetFit(164f, 164f, 0f, 164f, 0, 0));         // unchanged: nothing written
        Assert.True(layout.SetFit(150f, 9999f, 12f, 0f, 10, 4));         // a Huge band: ten columns at most
        Assert.Equal(FillRowVirtualLayout.Fit(Main, 150f, 9999f, 12f, maxColumns: 10).PerPage, layout.PerPage);
        Assert.Equal(10, layout.MaxColumns);
        Assert.Equal(4, layout.LeadMinColumns);
    }

    [Fact]
    public void A_multi_row_fill_row_re_fits_to_a_new_row_count_in_place()
    {
        // A chart of three columns of five (Huge) takes four columns of four past a rung (Huge 4K): the same instance re-fits.
        var layout = new FillRowVirtualLayout(minCardW: 264f, maxCardW: 9999f, gap: 8f, rows: 5, maxColumns: 3);
        layout.SetViewport(Main, Cross);
        Assert.Equal(new RectF(0f, 0f, layout.CardW, (Cross - 4 * 8f) / 5f), layout.ItemRect(0, Cross));
        Assert.Equal(layout.CardW + 8f, layout.ItemRect(5, Cross).X, 3);   // item 5 tops column 1 at five rows
        Assert.Equal(0f, layout.ItemRect(5, Cross).Y, 3);

        Assert.True(layout.SetRows(4));
        Assert.Equal(4, layout.Rows);
        Assert.False(layout.SetRows(4));                                  // unchanged: nothing written
        float rh = (Cross - 3 * 8f) / 4f;
        var r5 = layout.ItemRect(5, Cross);                               // item 5: column 1, row 1 at four rows
        Assert.Equal(layout.CardW + 8f, r5.X, 3);
        Assert.Equal(rh + 8f, r5.Y, 3);
        Assert.Equal(rh, r5.H, 3);
        Assert.Equal(2 * layout.CardW + 8f, layout.ContentExtent(8, Cross), 3);   // eight items: two columns of four

        Assert.False(layout.SetRows(1));                                  // one row is another shape: never a live re-fit
        Assert.Equal(4, layout.Rows);
        var single = new FillRowVirtualLayout(rows: 1);
        Assert.False(single.SetRows(5));
        Assert.Equal(1, single.Rows);
    }

    [Theory]
    [InlineData(0, 15, 16, 0)]
    [InlineData(1, 15, 16, 0)]    // page 1 starts at item 15, which a page of sixteen holds on page 0
    [InlineData(2, 15, 16, 1)]    // item 30 -> page 1 (16..31)
    [InlineData(3, 15, 16, 2)]    // item 45 -> page 2 (32..47)
    [InlineData(1, 16, 15, 1)]    // back across the rung: item 16 -> page 1 (15..29)
    [InlineData(2, 16, 15, 2)]    // item 32 -> page 2 (30..44)
    [InlineData(2, 15, 15, 2)]    // the same page size keeps the page
    public void A_multi_row_page_keeps_its_first_item_across_a_page_size_change(int page, int oldPerPage, int newPerPage, int expected)
        => Assert.Equal(expected, FillRowVirtualLayout.PageKeepingFirstItem(page, oldPerPage, newPerPage));

    [Fact]
    public void A_responsive_grid_takes_a_new_cell_floor_on_the_layout_pass_and_keeps_its_anchor_row()
    {
        var grid = new GridVirtualLayout(columns: 0, itemHeight: 100f, gap: 0f, minCellWidth: 200f);
        Assert.Equal(1000f, grid.ContentExtent(100, Main) / 2f);          // 5 columns: 20 rows of 100
        Assert.Equal(5, grid.EffectiveColumns(Main));

        Assert.True(grid.SetMinCellWidth(100f));
        Assert.Equal(5, grid.EffectiveColumns(Main));                     // pending until the layout pass takes it
        Assert.False(grid.SetMinCellWidth(100f));                         // the same floor again: nothing new

        Assert.True(grid.TakeReseedShift(50, out double delta));          // item 50: row 10 (offset 1000) -> row 5 (offset 500)
        Assert.Equal(-500.0, delta, 3);
        Assert.Equal(10, grid.EffectiveColumns(Main));
        Assert.Equal(100f, grid.MinCellWidth);
        Assert.False(grid.TakeReseedShift(50, out _));                    // consumed
    }

    [Fact]
    public void A_fixed_column_grid_is_not_re_fitted()
    {
        var grid = new GridVirtualLayout(columns: 4, itemHeight: 100f);
        Assert.False(grid.SetMinCellWidth(120f));
        Assert.False(grid.TakeReseedShift(10, out _));
        Assert.Equal(4, grid.EffectiveColumns(Main));
    }
}
