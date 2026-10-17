using FluentGpu.Controls;
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

    // ── separate row and column gaps (a grid that draws 12 between rows and 16 between columns) ───────────────────────────────────────

    [Fact]
    public void A_single_gap_grid_keeps_the_row_gap_equal_to_the_column_gap()
    {
        var grid = new GridVirtualLayout(columns: 4, itemHeight: 100f, gap: 8f);
        Assert.Equal(8f, grid.Gap);
        Assert.Equal(8f, grid.RowGap);
        Assert.Equal(2 * 100f + 8f, grid.ContentExtent(8, Main), 3);           // two rows of 100 and ONE 8 between them
        var below = grid.ItemRect(4, Main);                                      // row 1, column 0
        Assert.Equal(108f, below.Y, 3);
        Assert.Equal((Main - 3 * 8f) / 4f + 8f, grid.ItemRect(1, Main).X, 3);    // columns step by cell + the same 8

        var spec = RepeatLayout.GridFit(100f, 8f);
        Assert.Equal(8f, spec.EffectiveRowGap);
        Assert.True(float.IsNaN(spec.RowGap));
    }

    [Fact]
    public void A_grid_with_its_own_row_gap_steps_rows_by_it_and_columns_by_the_column_gap()
    {
        var grid = new GridVirtualLayout(columns: 0, itemHeight: 100f, gap: 16f, minCellWidth: 90f, rowGap: 12f);
        Assert.Equal(16f, grid.Gap);
        Assert.Equal(12f, grid.RowGap);
        Assert.Equal(2, grid.EffectiveColumns(216f));                           // 2 x 90 + 16 fits 216 (columns follow the COLUMN gap only)
        float cw = (216f - 16f) / 2f;
        Assert.Equal(cw + 16f, grid.ItemRect(1, 216f).X, 3);                    // a column steps by cell + 16
        Assert.Equal(112f, grid.ItemRect(2, 216f).Y, 3);                        // a row steps by 100 + 12
        Assert.Equal(3 * 100f + 2 * 12f, grid.ContentExtent(6, 216f), 3);       // three rows, two 12 gaps
        Assert.Equal(112f, grid.OffsetOf(2, 216f), 3);
    }

    [Fact]
    public void A_measured_grid_adds_the_row_gap_between_its_measured_rows()
    {
        var grid = new GridVirtualLayout(columns: 2, itemHeight: 0f, gap: 16f, estimate: 100f, rowGap: 12f);
        grid.ContentExtent(4, 216f);
        grid.SetMeasured(0, 80f, 216f);
        grid.SetMeasured(2, 60f, 216f);
        Assert.Equal(0f, grid.ItemRect(0, 216f).Y, 3);
        Assert.Equal(80f + 12f, grid.ItemRect(2, 216f).Y, 3);
        Assert.Equal(80f + 12f + 60f, grid.ContentExtent(4, 216f), 3);
    }

    [Fact]
    public void A_responsive_grid_takes_new_gaps_on_the_layout_pass_and_keeps_its_anchor_row()
    {
        var grid = new GridVirtualLayout(columns: 0, itemHeight: 100f, gap: 8f, minCellWidth: 100f);
        Assert.Equal(8f, grid.RowGap);
        grid.ContentExtent(100, 216f);                                            // the cross the pass last laid out
        Assert.True(grid.SetFit(100f, 16f, 12f));
        Assert.Equal(8f, grid.Gap);                                               // pending until the layout pass takes it
        Assert.Equal(8f, grid.RowGap);
        Assert.False(grid.SetFit(100f, 16f, 12f));                                // the same fit again: nothing new
        Assert.False(grid.SetMinCellWidth(100f));                                 // the floor alone: the pending gaps are kept, nothing new

        // Item 10: at 216 wide, gap 8: columns = (216 + 8) / 108 = 2, row 5, offset 5 * 108 = 540. Gap 16 / 12: columns = 232 / 116 = 2, row 5, offset 5 * 112 = 560.
        Assert.True(grid.TakeReseedShift(10, out double delta));
        Assert.Equal(20.0, delta, 3);
        Assert.Equal(16f, grid.Gap);
        Assert.Equal(12f, grid.RowGap);
        Assert.False(grid.TakeReseedShift(10, out _));                            // consumed
        Assert.False(grid.SetFit(100f, 16f, 12f));                                // the applied fit is the current one
    }

    [Fact]
    public void Setting_the_floor_alone_keeps_the_gaps_and_a_fixed_column_grid_is_not_re_fitted_by_gaps()
    {
        var grid = new GridVirtualLayout(columns: 0, itemHeight: 100f, gap: 16f, minCellWidth: 100f, rowGap: 12f);
        Assert.True(grid.SetMinCellWidth(80f));
        Assert.True(grid.TakeReseedShift(0, out _));
        Assert.Equal(80f, grid.MinCellWidth);
        Assert.Equal(16f, grid.Gap);
        Assert.Equal(12f, grid.RowGap);

        var fixedCols = new GridVirtualLayout(columns: 4, itemHeight: 100f, gap: 8f);
        Assert.False(fixedCols.SetFit(100f, 16f, 12f));
        Assert.Equal(8f, fixedCols.Gap);
    }

    [Fact]
    public void The_repeat_layout_spec_carries_a_row_gap_and_re_fits_across_a_gap_change_only_when_responsive()
    {
        var single = RepeatLayout.GridFit(96f, 8f, 120f);
        var split = RepeatLayout.GridFit(96f, 16f, 120f).WithRowGap(12f);
        Assert.Equal(16f, split.Gap);
        Assert.Equal(12f, split.RowGap);
        Assert.Equal(12f, split.EffectiveRowGap);
        Assert.False(RepeatLayout.SameGeometry(in single, in split));
        Assert.True(RepeatLayout.SameGeometry(in split, RepeatLayout.GridFit(96f, 16f, 120f).WithRowGap(12f)));
        // A different row gap alone is a different geometry, and a gap change on a responsive grid is a LIVE re-fit (the same mounted layout).
        Assert.False(RepeatLayout.SameGeometry(RepeatLayout.GridFit(96f, 16f), RepeatLayout.GridFit(96f, 16f).WithRowGap(12f)));
        Assert.True(RepeatLayout.IsCellFloorRefit(in single, in split));
        Assert.False(RepeatLayout.IsCellFloorRefit(RepeatLayout.Grid(4, 100f, 8f), RepeatLayout.Grid(4, 100f, 8f).WithRowGap(12f)));   // a fixed-column grid is not
        // WithRowGap on a non-grid layout is a no-op.
        var stack = RepeatLayout.Stack(40f);
        Assert.True(RepeatLayout.SameGeometry(in stack, stack.WithRowGap(12f)));
    }
}
