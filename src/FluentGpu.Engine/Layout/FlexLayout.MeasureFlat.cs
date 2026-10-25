using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Layout;

// ── Measure-validity intervals (PERF r3 UI, C1) ───────────────────────────────────────────────────────────────────────────
//
// A row measures each content-sized child at the row's whole content width, then its Arrange re-measures every child at its
// FINAL main size; a column's Arrange re-measures every child at its final cross width. A content-sized child re-measured at
// its own natural width answers the same, and so does its whole subtree, level by level (--fg measure-census, nav-burst: 90 %
// of real measures run inside Arrange; 97 % of the re-measures at a new width return the same size).
//
// A real measure at offer `a` that yields width `w` records the interval [w, a] - or every offer, when its result does not
// depend on the offer - over which a re-measure gives the same size for the node AND the same measured size for every
// descendant. Within the same measure pass, an offer inside the interval returns the recorded size without descending
// (MeasureCore). The interval is derived, never assumed:
// - a text run: the wrap is greedy against maxW (+ the 1-DIP slack), so at any maxW in [the widest line, the maxW it was laid
//   at] every line keeps the words it had - each fits, and the word that broke it did not fit at the larger maxW - and the trim
//   and auto-fit choose the same cut. An unbounded maxW (no wrap, no trim, no auto-fit) or an explicit width does not move with
//   the offer at all. Its interval is [w, a] when maxW at w still covers the widest line, every offer when the run is free.
// - a flex box laid out by the general path (no wrap, no shrink re-measure, no reflow orphan, no offer-driven aspect ratio): its
//   size is a function of its children's sizes, so it is flat on [w, a] when EACH child is flat at the offers this box would hand
//   it at BOTH ends - recomputed with the same formulas MeasureCore uses (OffersInside) and checked against the child's own
//   interval; offers are monotone in the box's offer, so the two ends cover the whole range. An explicit width hands the same
//   offers whatever the box is offered: every offer, when the children are flat at theirs.
// - everything else (viewports, grids, z-stacks, wrap boxes, a probe): no interval, today's behaviour.
// An interval holds only inside its pass (_measureGen), is replaced by the node's next real measure, and is cleared when the
// node is arranged (its arrange re-measures its children at final widths, after which it no longer describes them). The
// layout-verify oracle re-solves with intervals off (Verifying) and compares every node's Bounds.
public sealed partial class FlexLayout
{
    /// <summary>Measure-validity intervals on (default). <c>--fg no-measure-flat</c> turns them off (the A/B arm).</summary>
    public static bool MeasureFlatEnabled = true;

    /// <summary>Per-frame: re-measures answered by an interval (no descent). Reset with the other frame counters.</summary>
    public int DiagMeasureFlatHits => _dMeasureFlatHit;
    private int _dMeasureFlatHit;

    private void ClearFlat(NodeHandle node)
    {
        uint i = node.Raw.Index;
        if (i < (uint)_memo.Length) _memo[i].FlatGen = 0;
    }

    private void RecordFlat(NodeHandle node, in LayoutInput li, float availW, float w, float h, int kind, bool textFree, float textW)
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
            // Text: maxW at the low end must still cover the widest laid line (the clamp could pull it under).
            if (kind == 1 && MathF.Max(0f, DefiniteWidth(in li, lo)) < textW) return;
        }
        if (kind == 2 && !FlexChildrenFlat(node, in li, Row(li), lo, hi, free)) return;
        m.FlatGen = _measureGen; m.FlatLo = lo; m.FlatHi = hi; m.FlatW = w; m.FlatH = h;
    }

    /// <summary>Every visible child is flat at the offers this box hands it at both ends of [lo, hi] (for a free box: at the one
    /// offer it hands whatever it is offered).</summary>
    private bool FlexChildrenFlat(NodeHandle node, in LayoutInput li, bool row, float lo, float hi, bool free)
    {
        if (free) return OffersInside(node, in li, row, 0f);   // DefiniteWidth ignores the offer when Width is set
        return OffersInside(node, in li, row, lo) && OffersInside(node, in li, row, hi);
    }

    // The offers MeasureCore's general path hands each visible child when the box is offered `x` (kept line for line with it).
    private bool OffersInside(NodeHandle node, in LayoutInput li, bool row, float x)
    {
        float measureW = DefiniteWidth(in li, x);
        float childAvail = float.IsInfinity(measureW) ? measureW : MathF.Max(0f, measureW - li.Padding.Horizontal);
        float growAvail = childAvail;
        float totalGrow = 0f;
        if (row && !float.IsInfinity(childAvail))
        {
            float fixedMain = 0f; int cc = 0;
            for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
            {
                ref LayoutInput cli2 = ref _scene.Layout(c);
                cc++;
                if (cli2.FlexGrow > 0f) { totalGrow += cli2.FlexGrow; continue; }
                float cm;
                if (!float.IsNaN(cli2.FlexBasis)) cm = cli2.FlexBasis;
                else
                {
                    uint ci = c.Raw.Index;
                    if (ci >= (uint)_memo.Length || _memo[ci].FlatGen != _measureGen) return false;
                    cm = _memo[ci].FlatW;   // a flat child's width is the same at every offer in its interval
                }
                fixedMain += cm + MarginMain(cli2, row);
            }
            if (totalGrow > 0f) growAvail = MathF.Max(0f, childAvail - fixedMain - (cc > 1 ? li.Gap * (cc - 1) : 0f));
        }
        for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
        {
            ref LayoutInput cli = ref _scene.Layout(c);
            float cAvail = row && cli.FlexGrow > 0f && totalGrow > 0f && !float.IsInfinity(growAvail)
                ? growAvail * (cli.FlexGrow / totalGrow) + (float.IsNaN(cli.FlexBasis) ? 0f : cli.FlexBasis)
                : row ? (cli.FlexGrow > 0f ? growAvail : childAvail)
                : float.IsInfinity(childAvail) ? childAvail : MathF.Max(0f, childAvail - cli.Margin.Horizontal);
            uint ci = c.Raw.Index;
            if (ci >= (uint)_memo.Length) return false;
            ref MeasureMemo cm2 = ref _memo[ci];
            if (cm2.FlatGen != _measureGen || !(cAvail >= cm2.FlatLo && cAvail <= cm2.FlatHi)) return false;
        }
        return true;
    }
}
