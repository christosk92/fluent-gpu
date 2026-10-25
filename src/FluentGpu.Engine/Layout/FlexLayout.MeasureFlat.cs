using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Layout;

// ── Measure-validity intervals (PERF r3 UI, C1) ───────────────────────────────────────────────────────────────────────────
//
// A row measures each content-sized child at the row's whole content width, then its Arrange re-measures every child at its
// FINAL main size; a column's Arrange re-measures every child at its final cross width. A content-sized child re-measured at
// its own natural width answers the same, and so does its whole subtree, level by level (--fg measure-census, nav-burst: 90 %
// of real measures run inside Arrange; 97 % of the re-measures at a new width return the same size). And a CLEAN subtree is
// re-measured at another width every time a layout-driving animation or a relayout moves its offer (a third of all real
// measures in nav-burst), which the cross-pass ring, keyed on the exact offer, cannot answer.
//
// A real measure at offer `a` that yields width `w` records the interval [w, a] - or every offer, when its result does not
// depend on the offer - over which a re-measure gives the same size for the node AND the same measured size for every
// descendant. An offer inside the interval returns the recorded size without descending: within the same pass from the
// node's record (MeasureCore), and across passes from the cross-pass ring slot that holds the measure, under the ring's own
// guards (the subtree clean, its version and layout signature unchanged: the subtree the interval was derived for). The
// interval is derived, never assumed:
// - a text run: the wrap is greedy against maxW (+ the 1-DIP slack), so at any maxW in [the widest line, the maxW it was laid
//   at] every line keeps the words it had - each fits, and the word that broke it did not fit at the larger maxW - and the trim
//   and auto-fit choose the same cut. An unbounded maxW (no wrap, no trim, no auto-fit) or an explicit width does not move with
//   the offer at all. Its interval is [w, a] when maxW at w still covers the widest line, every offer when the run is free.
// - a flex box laid out by the general path (no wrap, no shrink re-measure, no reflow orphan, no offer-driven aspect ratio): its
//   size is a function of its children's sizes, so it is flat on [w, a] when EACH child is flat at the offer it got (the high
//   end, checked as the child is measured) and at the offer it would get at the low end. Offers are monotone in the box's offer,
//   so the two ends cover the range, and the low end reduces to one number, the child-available width it needs (FlatTrack): a
//   column child its FlatLo plus its margin, a fixed row child its FlatLo, a grow row child (FlatLo - basis)·G/g plus the fixed
//   children and the gaps. An explicit width hands every child the same offer whatever the box is offered: every offer.
// - everything else (viewports, grids, z-stacks, wrap boxes, a probe): no interval, today's behaviour.
// A within-pass interval holds only inside its pass (_measureGen), is replaced by the node's next real measure, and is cleared
// when the node is arranged (its arrange re-measures its children at final widths). A ring slot's interval is dropped whenever
// the slot is rewritten. The layout-verify oracle re-solves with neither (Verifying) and compares every node's Bounds.
public sealed partial class FlexLayout
{
    /// <summary>Measure-validity intervals on (default). <c>--fg no-measure-flat</c> turns them off (the A/B arm).</summary>
    public static bool MeasureFlatEnabled = true;

    /// <summary>Intervals in the cross-pass ring: <c>--fg measure-flat-ring</c>, OFF by default. A ring answer is only as sound as the
    /// subtree's dirty marks, and today some writers change layout inputs without <c>Mark(LayoutDirty)</c> (the layout-verify oracle's
    /// unmarked-write tripwire fires on the base build too). An exact-offer ring answer rarely meets such a subtree (its offer usually
    /// moves with its content); an interval answer does: with it on, nav-burst showed bounds divergences (a run measured while empty,
    /// answered again after its text arrived). Within-pass intervals do not depend on dirty marks (everything they answer was measured
    /// from the current state in the same pass) and stay on.</summary>
    public static bool MeasureFlatRingEnabled;

    /// <summary>Per-frame: re-measures answered by a within-pass interval (no descent). Reset with the other frame counters.</summary>
    public int DiagMeasureFlatHits => _dMeasureFlatHit;
    private int _dMeasureFlatHit;

    private void ClearFlat(NodeHandle node)
    {
        uint i = node.Raw.Index;
        if (i < (uint)_memo.Length) _memo[i].FlatGen = 0;
    }

    /// <summary>One child of a general-path flex box, right after its measure at <paramref name="cAvail"/>: it must be flat there
    /// (the box's high end); its demand on the box's low end is folded into <paramref name="need"/> (or, for a grow child of a row,
    /// into <paramref name="growScaled"/>, resolved once the fixed children and the gaps are known).</summary>
    private void FlatTrack(NodeHandle c, in LayoutInput cli, bool row, float cAvail, Size2 cs, ref float need, ref float growScaled,
                           ref float fixedSum, ref float growSum, ref int kind)
    {
        uint ci = c.Raw.Index;
        if (ci >= (uint)_memo.Length) { kind = 0; return; }
        ref MeasureMemo cm = ref _memo[ci];
        if (cm.FlatGen != _measureGen || !(cAvail >= cm.FlatLo && cAvail <= cm.FlatHi)) { kind = 0; return; }
        if (row)
        {
            if (cli.FlexGrow > 0f)
            {
                growSum += cli.FlexGrow;
                float basis = float.IsNaN(cli.FlexBasis) ? 0f : cli.FlexBasis;
                if (cm.FlatLo - basis > 0f) growScaled = MathF.Max(growScaled, (cm.FlatLo - basis) / cli.FlexGrow);
            }
            else
            {
                fixedSum += (float.IsNaN(cli.FlexBasis) ? cs.Width : cli.FlexBasis) + MarginMain(cli, row);
                need = MathF.Max(need, cm.FlatLo);
            }
        }
        else if (cm.FlatLo > 0f) need = MathF.Max(need, cm.FlatLo + cli.Margin.Horizontal);
    }

    private void RecordFlat(NodeHandle node, in LayoutInput li, float availW, float w, float h, int kind, bool textFree, float textW, float need)
    {
        uint i = node.Raw.Index;
        if (i >= (uint)_memo.Length) return;
        ref MeasureMemo m = ref _memo[i];
        m.FlatGen = 0;
        if (kind == 0) return;
        // An aspect ratio with neither extent set sizes from the offer itself.
        if (!float.IsNaN(li.AspectRatio) && li.AspectRatio > 0f && float.IsNaN(li.Width) && float.IsNaN(li.Height)) return;
        float lo, hi;
        bool free = kind == 1 ? textFree : !float.IsNaN(li.Width);   // offers that do not move with this node's offer
        if (free) { lo = float.NegativeInfinity; hi = float.PositiveInfinity; }
        else
        {
            lo = w; hi = availW;
            if (!(lo <= hi)) return;   // overflowed its offer (or NaN): no interval
            if (kind == 1)
            {
                // Text: maxW at the low end must still cover the widest laid line (the clamp could pull it under).
                if (MathF.Max(0f, DefiniteWidth(in li, lo)) < textW) return;
            }
            else
            {
                float mw = DefiniteWidth(in li, lo);
                float ca = float.IsInfinity(mw) ? mw : MathF.Max(0f, mw - li.Padding.Horizontal);
                if (ca < need) return;   // some child would be offered less than its interval at this box's low end
            }
        }
        m.FlatGen = _measureGen; m.FlatLo = lo; m.FlatHi = hi; m.FlatW = w; m.FlatH = h;
        // The ring slot StoreRing just wrote for this offer carries the interval across passes.
        uint gen = node.Raw.Gen;
        if (m.Ring0NodeGen == gen && m.Ring0AvailW == availW) { m.Ring0Lo = lo; m.Ring0Hi = hi; }
        else if (m.Ring1NodeGen == gen && m.Ring1AvailW == availW) { m.Ring1Lo = lo; m.Ring1Hi = hi; }
    }
}
