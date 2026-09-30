using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// E7 — <see cref="FillRowVirtualLayout"/>'s lead-item span (PagedShelf's wide "hero" first card: CoverShelf/
/// MixedCovers's lead when it carries a header image). Covers the pure arithmetic: <c>CellsOf</c>, <c>FirstItemOfPage</c>,
/// span-aware <c>ItemRect</c>, the <c>Rows &gt; 1</c> fallback (a multi-row grid never spans its lead item), and page
/// count. <c>leadSpan == 1</c> is asserted byte-identical to the pre-E7 shape throughout (the whole feature's fallback
/// contract).
/// <para>E11 — the HALF-ROW rule: <see cref="FillRowVirtualLayout.EffectiveLeadSpan"/> now honours the raw
/// <see cref="FillRowVirtualLayout.LeadSpan"/> only while <c>PerPage &gt; 2*LeadSpan</c> (at least <c>LeadSpan + 1</c>
/// ordinary cells remain beside it) and COLLAPSES to 1 otherwise — replacing the earlier
/// <c>Math.Clamp(LeadSpan, 1, PerPage)</c> rule, which let a lead claim half a row or more (down to the whole,
/// single-column row). Every test below that exercises a NON-collapsing span (PerPage &gt; 2*LeadSpan) is unchanged
/// from the pre-E11 shape; the ones that exercised the old clamp specifically are replaced.</para>
/// </summary>
public class VirtualLayoutLeadSpanTests
{
    // A uniform 6-up shelf: 100 DIP cards, 10 DIP gaps, fixed width so PerPage is exactly 6 with no fit ambiguity.
    // main = 6*100 + 5*10 = 650; Fit's fixedCardW path floors (main+gap)/(cardW+gap) = 660/110 = 6.
    const float CardW = 100f, Gap = 10f, Main = 650f, Cross = 200f;

    static FillRowVirtualLayout SixUp(int rows = 1)
    {
        var layout = new FillRowVirtualLayout(minCardW: CardW, maxCardW: CardW, gap: Gap, rows: rows, fixedCardW: CardW);
        layout.SetViewport(Main, Cross);
        return layout;
    }

    // A shelf fitted to EXACTLY `perPage` uniform 100-DIP/10-DIP-gap columns (E11's half-row matrix needs several
    // column counts; SixUp above is the perPage==6 special case of this).
    static FillRowVirtualLayout ShelfOf(int perPage, int rows = 1)
    {
        float main = perPage * CardW + (perPage - 1) * Gap;
        var layout = new FillRowVirtualLayout(minCardW: CardW, maxCardW: CardW, gap: Gap, rows: rows, fixedCardW: CardW);
        layout.SetViewport(main, Cross);
        return layout;
    }

    [Fact]
    public void PerPage_is_six_before_any_span_is_set()
    {
        var layout = SixUp();
        Assert.Equal(6, layout.PerPage);
        Assert.Equal(1, layout.LeadSpan);
        Assert.Equal(1, layout.EffectiveLeadSpan);   // default — byte-identical to a plain fill-row layout
    }

    // ── CellsOf ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CellsOf_is_the_identity_when_leadSpan_is_one()
    {
        var layout = SixUp();
        Assert.Equal(0, layout.CellsOf(0));
        Assert.Equal(1, layout.CellsOf(1));
        Assert.Equal(13, layout.CellsOf(13));
    }

    [Fact]
    public void CellsOf_adds_span_minus_one_cells_once_a_lead_span_is_set()
    {
        var layout = SixUp();
        layout.SetLeadSpan(2);
        Assert.Equal(0, layout.CellsOf(0));     // no items ⇒ no cells, even with a span
        Assert.Equal(2, layout.CellsOf(1));     // item 0 alone already occupies its whole span
        Assert.Equal(3, layout.CellsOf(2));
        Assert.Equal(14, layout.CellsOf(13));   // 13 + 2 - 1
    }

    // ── FirstItemOfPage ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstItemOfPage_is_page_times_perPage_when_leadSpan_is_one()
    {
        var layout = SixUp();
        Assert.Equal(0, layout.FirstItemOfPage(0));
        Assert.Equal(6, layout.FirstItemOfPage(1));
        Assert.Equal(12, layout.FirstItemOfPage(2));
    }

    [Fact]
    public void FirstItemOfPage_shifts_back_by_span_minus_one_for_every_page_after_the_first()
    {
        var layout = SixUp();
        layout.SetLeadSpan(2);
        // Page 0 always starts at item 0 (the lead item occupies cells [0,2)); every later page starts (s-1) = 1
        // item earlier than a plain page*PerPage would, because the lead ate one extra cell out of page 0's budget.
        Assert.Equal(0, layout.FirstItemOfPage(0));
        Assert.Equal(5, layout.FirstItemOfPage(1));    // 1*6 - 1
        Assert.Equal(11, layout.FirstItemOfPage(2));   // 2*6 - 1
    }

    [Fact]
    public void FirstItemOfPage_never_goes_negative()
    {
        var layout = SixUp();
        layout.SetLeadSpan(6);   // == PerPage — E11's half-row rule (6 > 2*6 is false) collapses this to 1 anyway,
        Assert.Equal(1, layout.EffectiveLeadSpan);   // so a would-be whole-row lead never reaches ItemRect at all.
        Assert.Equal(0, layout.FirstItemOfPage(0));
        Assert.True(layout.FirstItemOfPage(1) >= 0);
    }

    // ── ItemRect ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ItemRect_is_unchanged_when_leadSpan_is_one()
    {
        var layout = SixUp();
        for (int i = 0; i < 8; i++)
        {
            var r = layout.ItemRect(i, Cross);
            Assert.Equal(i * (CardW + Gap), r.X);
            Assert.Equal(CardW, r.W);
        }
    }

    [Fact]
    public void ItemRect_widens_the_lead_item_and_shifts_later_items_by_span_minus_one_cells()
    {
        var layout = SixUp();
        layout.SetLeadSpan(2);

        var lead = layout.ItemRect(0, Cross);
        Assert.Equal(0f, lead.X);
        Assert.Equal(2 * CardW + Gap, lead.W);   // spans 2 cells: 2*100 + 1*10 = 210

        // Item 1 sits at cell (1 + 2 - 1) = 2, not cell 1 — it starts where the lead's span ends.
        var item1 = layout.ItemRect(1, Cross);
        Assert.Equal(2 * (CardW + Gap), item1.X);
        Assert.Equal(CardW, item1.W);

        var item5 = layout.ItemRect(5, Cross);
        Assert.Equal(6 * (CardW + Gap), item5.X);
        Assert.Equal(CardW, item5.W);

        // Every non-lead item still has the plain card width — only item 0 widens.
        for (int i = 1; i < 8; i++) Assert.Equal(CardW, layout.ItemRect(i, Cross).W);
    }

    [Fact]
    public void ItemRect_respects_a_lead_inset_with_a_span()
    {
        var layout = new FillRowVirtualLayout(minCardW: CardW, maxCardW: CardW, gap: Gap, rows: 1,
            fixedCardW: CardW, leadInset: 12f, trailInset: 12f);
        layout.SetViewport(Main + 24f, Cross);   // engine feeds the widened viewport (content + 2*inset)
        layout.SetLeadSpan(2);

        var lead = layout.ItemRect(0, Cross);
        Assert.Equal(12f, lead.X);                        // starts at the inset, like a plain layout's item 0
        Assert.Equal(2 * CardW + Gap, lead.W);

        var item1 = layout.ItemRect(1, Cross);
        Assert.Equal(12f + 2 * (CardW + Gap), item1.X);
    }

    // ── Rows > 1 fallback — a multi-row grid never spans its lead item ─────────────────────────────────────

    [Fact]
    public void EffectiveLeadSpan_is_forced_to_one_on_a_multi_row_grid()
    {
        var layout = SixUp(rows: 3);
        layout.SetLeadSpan(3);
        Assert.Equal(3, layout.LeadSpan);          // the RAW request is still recorded…
        Assert.Equal(1, layout.EffectiveLeadSpan); // …but geometry never sees it: Rows > 1 forces 1
    }

    [Fact]
    public void ItemRect_on_a_multi_row_grid_is_untouched_by_a_lead_span()
    {
        var plain = SixUp(rows: 3);
        var spanned = SixUp(rows: 3);
        spanned.SetLeadSpan(4);

        for (int i = 0; i < 12; i++)
        {
            var a = plain.ItemRect(i, Cross);
            var b = spanned.ItemRect(i, Cross);
            Assert.Equal(a.X, b.X);
            Assert.Equal(a.Y, b.Y);
            Assert.Equal(a.W, b.W);
        }
    }

    [Fact]
    public void CellsOf_and_ContentExtent_are_unaffected_by_a_lead_span_on_a_multi_row_grid()
    {
        var plain = SixUp(rows: 3);
        var spanned = SixUp(rows: 3);
        spanned.SetLeadSpan(5);
        Assert.Equal(plain.CellsOf(11), spanned.CellsOf(11));
        Assert.Equal(plain.ContentExtent(11, Cross), spanned.ContentExtent(11, Cross));
    }

    // ── E11: the half-row rule ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EffectiveLeadSpan_collapses_to_one_once_the_span_would_take_half_the_row_or_more()
    {
        // A span that used to CLAMP down to whatever the row could hold — all the way down to a lead that WAS the
        // entire (single-column) row — now COLLAPSES to an ordinary, unspanned lead instead (E11).
        var layout = SixUp();
        layout.SetLeadSpan(10);   // more columns than one page can ever hold
        Assert.Equal(10, layout.LeadSpan);          // raw value preserved verbatim
        Assert.Equal(1, layout.EffectiveLeadSpan);  // 6 > 2*10 is false ⇒ collapsed, not clamped to 6
        Assert.Equal(1, layout.CellsOf(1));         // the (unspanned) lead occupies exactly one cell
    }

    [Theory]
    // perPage, expected EffectiveLeadSpan for a 2-span lead (PerPage > 2*LeadSpan == PerPage > 4 honours it).
    [InlineData(6, 2)]   // 6 > 4 ⇒ honoured — the app's own shelves pass leadSpan: 2 unconditionally at 6-up
    [InlineData(5, 2)]   // 5 > 4 ⇒ still honoured — one ordinary cell is enough headroom beside the span
    [InlineData(4, 1)]   // 4 > 4 is false ⇒ collapsed (a 2-span lead in a 4-up row IS half the row)
    [InlineData(3, 1)]   // 3 > 4 is false ⇒ collapsed
    public void EffectiveLeadSpan_honours_a_two_span_lead_only_while_the_half_row_rule_holds(int perPage, int expected)
    {
        var layout = ShelfOf(perPage);
        layout.SetLeadSpan(2);
        Assert.Equal(perPage, layout.PerPage);        // the fit landed at the column count this case assumes
        Assert.Equal(2, layout.LeadSpan);              // raw value always preserved verbatim
        Assert.Equal(expected, layout.EffectiveLeadSpan);
    }

    // ── E22: the per-shelf lead minimum (MinColsFor — the ONE place the threshold lives) ───────────────────

    [Theory]
    // leadSpan, leadMinColumns, expected minimum columns.
    [InlineData(2, 0, 5)]   // 0 ⇒ E11's half-row rule verbatim: 2*2+1 (PerPage > 4 ⇔ PerPage >= 5)
    [InlineData(3, 0, 7)]   // 2*3+1
    [InlineData(2, 4, 4)]   // Home's cover shelf: a 4-up page keeps its 2-span lead beside two ordinary cells
    [InlineData(2, 3, 3)]   // floor is span+1 (one ordinary cell beside the lead) — 3 is exactly that
    [InlineData(2, 2, 3)]   // a minimum BELOW span+1 is floored: a lead that is the whole row is never a lead
    [InlineData(3, 2, 4)]   // same floor for a wider span
    [InlineData(2, 9, 9)]   // a minimum above the half-row rule is honoured as-is (stricter than E11)
    public void MinColsFor_is_the_half_row_rule_at_zero_and_a_floored_caller_minimum_otherwise(int span, int min, int expected)
        => Assert.Equal(expected, FillRowVirtualLayout.MinColsFor(span, min));

    [Theory]
    // perPage, leadMinColumns, expected EffectiveLeadSpan for a 2-span lead.
    [InlineData(4, 0, 1)]   // default — E11 collapses a 2-span lead on a 4-up row (byte-identical to before E22)
    [InlineData(4, 4, 2)]   // leadMinColumns 4 ⇒ 4 >= max(3, 4) ⇒ honoured
    [InlineData(3, 4, 1)]   // …but not at 3 columns
    [InlineData(3, 3, 2)]   // leadMinColumns 3 ⇒ lead + ONE ordinary cell, the floor
    [InlineData(6, 4, 2)]   // wide rows are unaffected by a lower minimum
    [InlineData(6, 8, 1)]   // a minimum ABOVE the row's column count collapses even a 6-up lead
    public void EffectiveLeadSpan_honours_the_per_shelf_lead_minimum(int perPage, int leadMinColumns, int expected)
    {
        float main = perPage * CardW + (perPage - 1) * Gap;
        var layout = new FillRowVirtualLayout(minCardW: CardW, maxCardW: CardW, gap: Gap, rows: 1, fixedCardW: CardW,
                                              leadMinColumns: leadMinColumns);
        layout.SetViewport(main, Cross);
        layout.SetLeadSpan(2);
        Assert.Equal(perPage, layout.PerPage);
        Assert.Equal(Math.Max(0, leadMinColumns), layout.LeadMinColumns);
        Assert.Equal(expected, layout.EffectiveLeadSpan);
        // The geometry follows the SAME answer: a honoured lead is 2 cells + gap wide, a collapsed one a plain card.
        Assert.Equal(expected == 2 ? 2 * CardW + Gap : CardW, layout.ItemRect(0, Cross).W);
    }

    [Fact]
    public void LeadMinColumns_is_clamped_to_zero_and_never_spans_a_multi_row_grid()
    {
        var negative = new FillRowVirtualLayout(minCardW: CardW, maxCardW: CardW, gap: Gap, rows: 1, fixedCardW: CardW, leadMinColumns: -3);
        Assert.Equal(0, negative.LeadMinColumns);   // ⇒ the default half-row rule

        var grid = new FillRowVirtualLayout(minCardW: CardW, maxCardW: CardW, gap: Gap, rows: 2, fixedCardW: CardW, leadMinColumns: 2);
        grid.SetViewport(Main, Cross);
        grid.SetLeadSpan(2);
        Assert.Equal(1, grid.EffectiveLeadSpan);   // Rows > 1 still forces 1 — the minimum never overrides that
    }

    [Fact]
    public void SetLeadSpan_clamps_a_non_positive_request_to_one()
    {
        var layout = SixUp();
        layout.SetLeadSpan(0);
        Assert.Equal(1, layout.LeadSpan);
        layout.SetLeadSpan(-5);
        Assert.Equal(1, layout.LeadSpan);
    }

    // ── Page count (via CellsOf, the same math PagedShelfCore.PageCountFor uses) ───────────────────────────

    [Fact]
    public void Page_count_from_cells_matches_plain_paging_when_leadSpan_is_one()
    {
        var layout = SixUp();
        int perPage = layout.PerPage;
        int n = 13;
        int pages = (layout.CellsOf(n) + perPage - 1) / perPage;
        Assert.Equal(3, pages);   // 13 items / 6 per page ⇒ 3 pages (6, 6, 1), unaffected by leadSpan==1
    }

    [Fact]
    public void Page_count_grows_by_the_cells_the_lead_span_consumes()
    {
        var layout = SixUp();
        layout.SetLeadSpan(2);
        int perPage = layout.PerPage;
        int n = 13;
        // CellsOf(13) = 13 + 2 - 1 = 14 cells over 6/page ⇒ ceil(14/6) = 3 pages (6, 6, 2) — same 3 pages as the
        // unspanned case here (14 still fits in 3 sixes), but FirstItemOfPage proves the boundary itself moved.
        int pages = (layout.CellsOf(n) + perPage - 1) / perPage;
        Assert.Equal(3, pages);
        Assert.Equal(11, layout.FirstItemOfPage(2));
        Assert.Equal(13 - 11, /* items on the last page */ n - layout.FirstItemOfPage(2));
    }

    [Fact]
    public void Page_count_can_grow_by_a_whole_page_once_a_honoured_span_pushes_cells_past_a_boundary()
    {
        // An 8-up shelf: a 3-span lead clears E11's half-row rule (8 > 2*3 = 6), so it stays honoured — the
        // pre-E11 version of this test used a 6-up/3-span shelf, which E11 now collapses to an unspanned lead
        // (6 > 6 is false); this is the same boundary-jump scenario ported to a column count that still honours it.
        var layout = ShelfOf(8);
        layout.SetLeadSpan(3);
        Assert.Equal(3, layout.EffectiveLeadSpan);   // 8 > 2*3 ⇒ honoured
        int perPage = layout.PerPage;

        int n = 22;   // CellsOf(22) = 22 + 3 - 1 = 24 ⇒ exactly 3 pages of 8
        int spannedPages = (layout.CellsOf(n) + perPage - 1) / perPage;
        int plainPages = (n + perPage - 1) / perPage;   // 22 items / 8 per page ⇒ 3 pages unspanned too (22→24 stays in 3)
        Assert.Equal(3, spannedPages);
        Assert.Equal(3, plainPages);

        // One more item tips the spanned layout into a 4th page while the plain layout is still on its 3rd.
        int n2 = 23;
        int spannedPages2 = (layout.CellsOf(n2) + perPage - 1) / perPage;   // CellsOf = 25 ⇒ ceil(25/8) = 4
        int plainPages2 = (n2 + perPage - 1) / perPage;                    // 23 ⇒ ceil(23/8) = 3
        Assert.Equal(4, spannedPages2);
        Assert.Equal(3, plainPages2);
    }
}
