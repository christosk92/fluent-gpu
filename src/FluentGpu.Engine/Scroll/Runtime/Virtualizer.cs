using System;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Extent;
using FluentGpu.Scroll.Motion;

namespace FluentGpu.Scroll.Runtime;

/// <summary>The realize window one frame of a viewport must materialize (design §6): rows
/// [<see cref="First"/>, <see cref="Last"/>] inclusive, covering content [<see cref="CoverStart"/>, <see cref="CoverEnd"/>).
/// <see cref="AnchorIndex"/> is the first FULLY visible row — the row a measured-extent correction anchors against;
/// [<see cref="VisibleFirst"/>, <see cref="VisibleLast"/>] is the strictly visible band (partially visible rows
/// included). An empty source yields <c>Last = First − 1</c>.</summary>
public readonly record struct RealizeWindow(int First, int Last, double CoverStart, double CoverEnd, int AnchorIndex, int VisibleFirst = 0, int VisibleLast = -1)
{
    public int Count => Last - First + 1;
    public bool IsEmpty => Last < First;
}

/// <summary>
/// Pure per-viewport virtualization decisions (design §6): the realize window from the plan's present-time position
/// and velocity (velocity-sized overscan on the leading side, a fixed floor behind), the row the realized window is
/// arranged relative to, and the one extent-write path that keeps the anchor row pinned on screen by shifting the
/// plan's frame in the same call.
/// </summary>
public static class Virtualizer
{
    /// <summary>The largest content distance (DIP) a realized row may sit from the arrange origin. Row positions are
    /// floats RELATIVE to the origin; within ±16384 DIP a float resolves 1/512 DIP, far below a device pixel at any
    /// scale — so a 100k-row list keeps exact geometry however deep it scrolls.</summary>
    public const double MaxLocalExtent = 16384.0;

    /// <summary>Decides the window to realize this frame. Overscan ahead of the motion is
    /// <c>clamp(|v|·LookaheadS, OverscanMinPx, OverscanMaxPx)</c>; behind it is <c>OverscanMinPx</c>. At rest both
    /// sides get the floor. <paramref name="anchorIndexPrev"/> is returned as the anchor when the source is empty
    /// (there is no visible row to anchor to).</summary>
    public static RealizeWindow Plan(IExtentSource ext, double p, double v, double viewport, in MotionFeel feel, int anchorIndexPrev)
    {
        int n = ext.Count;
        if (n <= 0) return new RealizeWindow(0, -1, 0.0, 0.0, anchorIndexPrev);

        double lead = Math.Abs(v) * feel.LookaheadS;
        if (lead < feel.OverscanMinPx) lead = feel.OverscanMinPx;
        if (lead > feel.OverscanMaxPx) lead = feel.OverscanMaxPx;
        double trail = feel.OverscanMinPx;
        double behind = v < 0.0 ? lead : trail;
        double ahead = v < 0.0 ? trail : lead;

        int first = ext.IndexAt(p - behind);
        int last = ext.IndexAt(p + viewport + ahead);
        if (last < first) last = first;

        int visibleFirst = ext.IndexAt(p);   // the row containing the shown offset (possibly partially scrolled off)
        int visibleLast = ext.IndexAt(p + viewport);
        if (visibleLast < visibleFirst) visibleLast = visibleFirst;
        // The anchor is the first FULLY visible row: a correction to the partially hidden top row is ABOVE it, so the
        // frame shifts and the rows the user is reading stay put (only the hidden part of that row moves).
        int anchor = visibleFirst;
        if (ext.OffsetOf(anchor) < p && anchor + 1 < n && ext.OffsetOf(anchor + 1) < p + viewport) anchor++;

        return new RealizeWindow(first, last, ext.OffsetOf(first), ext.OffsetOf(last + 1), anchor, visibleFirst, visibleLast);
    }

    /// <summary>The row the realized window [<paramref name="first"/>, <paramref name="lastExclusive"/>) is arranged
    /// relative to. The previous origin row is KEPT while every realized row still sits within
    /// <see cref="MaxLocalExtent"/> of it — so a window shift arranges only the entering rows and every retained row
    /// keeps its box (its recorded span stays reusable); otherwise the origin re-centres on the window's middle row, which
    /// leaves the most room before the next re-centre in either scroll direction.</summary>
    public static int ArrangeOriginIndex(IExtentSource ext, int prevOriginIndex, int first, int lastExclusive)
    {
        int n = ext.Count;
        if (n <= 0) return 0;
        double start = ext.OffsetOf(first), end = ext.OffsetOf(lastExclusive);
        if ((uint)prevOriginIndex < (uint)n)
        {
            double o = ext.OffsetOf(prevOriginIndex);
            if (start >= o - MaxLocalExtent && end <= o + MaxLocalExtent) return prevOriginIndex;
        }
        return Math.Clamp(ext.IndexAt(0.5 * (start + end)), 0, n - 1);
    }

    /// <summary>The ONE extent write (design §1 #5): records row <paramref name="index"/>'s measured extent; when the
    /// correction lands above <paramref name="anchorIndex"/> (so the anchor's own offset moved by <c>delta</c>), shifts
    /// <paramref name="vp"/>'s plan frame by that same delta in this call — the anchor row's screen position
    /// (<c>OffsetOf(anchor) − p</c>) is unchanged before layout ever runs. Returns the delta the caller must also add
    /// to its <c>WindowOrigin</c> and already-arranged rows this frame.</summary>
    public static double ApplyMeasured(IExtentSource ext, int index, double extent, int anchorIndex, PlanSlots slots, ScrollViewportId vp)
    {
        double delta = ext.SetMeasured(index, extent, anchorIndex);
        if (delta != 0.0)
        {
            bool anchored = slots.Shift(vp, delta);
            ScrollProbe.Extent(vp.Node, 0, index, delta, anchored);
        }
        return delta;
    }
}
