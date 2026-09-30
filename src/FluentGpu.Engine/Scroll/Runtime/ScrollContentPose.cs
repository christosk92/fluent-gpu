using System;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Extent;
using FluentGpu.Scroll.Motion;

namespace FluentGpu.Scroll.Runtime;

/// <summary>
/// The content-child transform composer shared by the UI thread (layout / the host's frame step — hit-testing and the
/// published frame) and the render-side pose sink: the content node's translate is <c>WindowOrigin − shown</c> along
/// the scroll axis (a small float relative to the realized window's origin, never the raw offset — design §6), snapped
/// to the device-pixel grid through the ONE snap function, composed with the committed pinch-zoom about the node's
/// authored origin.
/// </summary>
public static class ScrollContentPose
{
    /// <summary>The device-snapped content translate for a viewport whose realized rows sit at <paramref name="windowOrigin"/>
    /// and whose shown offset is <paramref name="shown"/>.</summary>
    public static float Translate(double windowOrigin, double shown, float dpiScale)
        => ScrollEffectEval.SnapToDevicePixel((float)(windowOrigin - shown), dpiScale);

    /// <summary>Compose the content child's <c>LocalTransform</c> from a translate along the scroll axis + the zoom
    /// factor (1 = none) about the node's authored origin.</summary>
    public static void WriteContentTransform(ref NodePaint cp, in RectF contentBounds, bool horizontal, float trans, float zoomFactor)
    {
        float z = (!float.IsFinite(zoomFactor) || zoomFactor <= 0f) ? 1f : zoomFactor;
        float offX = horizontal ? trans : 0f;
        float offY = horizontal ? 0f : trans;

        z = Math.Clamp(z, 1e-3f, 64f);
        const float epsilon = 1e-4f;
        if (MathF.Abs(z - 1f) <= epsilon)
        {
            cp.LocalTransform = Affine2D.Translation(offX, offY);
            return;
        }

        float w = contentBounds.W, h = contentBounds.H;
        float ox = w * cp.OriginX, oy = h * cp.OriginY;
        var map = new Affine2D(z, 0f, 0f, z, offX, offY);
        cp.LocalTransform = Affine2D.Translation(-ox, -oy).Multiply(map).Multiply(Affine2D.Translation(ox, oy));
    }

    /// <summary>The content range a virtual viewport's realized children actually cover (design §6) — THE coverage the
    /// posers clamp against (<see cref="ScrollCoverageRow.Start"/>/<see cref="ScrollCoverageRow.End"/>) and the tile
    /// scheduler requests rows from. The children are a persistent prefix <c>[0, prefix)</c> (retained, arranged at their
    /// real slots at every depth) plus the recyclable window <c>[firstRealized, lastRealized)</c>:
    /// <list type="bullet">
    /// <item>when the window starts at or right after the prefix, the two bands are ONE contiguous band from the content
    /// start, so <paramref name="start"/> = 0 — a prefixed list at offset 0 shows offset 0 (the hero it retains IS
    /// realized; publishing <c>OffsetOf(firstRealized)</c> instead clamped every such list to hero + chrome at rest);</item>
    /// <item>otherwise the band the viewport can sit in is the window itself, <c>OffsetOf(firstRealized)</c>;</item>
    /// <item>a window reaching the last item covers through the content end (<see cref="IExtentSource.Total"/>).</item>
    /// </list>
    /// The content start and end are the extent's own edges (0 and <c>Total</c>), not the first/last item's slot, so a
    /// leading/trailing pad around the items is covered content, never a clamp.</summary>
    public static void CoverageOf(IExtentSource ext, int prefix, int firstRealized, int lastRealized, out double start, out double end)
    {
        int n = ext.Count;
        prefix = Math.Clamp(prefix, 0, n);
        firstRealized = Math.Clamp(firstRealized, 0, n);
        lastRealized = Math.Clamp(lastRealized, firstRealized, n);
        start = firstRealized <= prefix ? 0.0 : ext.OffsetOf(firstRealized);
        end = lastRealized >= n ? ext.Total : ext.OffsetOf(lastRealized);
        if (end < start) end = start;
    }

    /// <summary>Does the realized window <c>[FirstRealized, LastRealized)</c> fail to cover the present-time window
    /// <paramref name="rw"/> (design §6)? A persistent prefix is covered by retained children.</summary>
    public static bool NeedsRealize(in ScrollState sc, in RealizeWindow rw)
    {
        if (sc.ItemCount <= 0) return false;
        if (rw.IsEmpty) return false;
        int prefix = Math.Clamp(sc.PersistentPrefixCount, 0, sc.ItemCount);
        int wantFirst = Math.Max(rw.First, prefix);
        int wantLast = Math.Max(rw.Last + 1, prefix);
        if (wantLast <= prefix) return false;
        // MeasureAll: the realized window is the whole item range — re-realize only when it no longer is (a count change).
        if (sc.MeasureAll) return sc.FirstRealized > prefix || sc.LastRealized < sc.ItemCount;
        if (sc.LastRealized <= sc.FirstRealized) return true;
        // The strictly visible band must ALWAYS be realized (no blank row, ever).
        int visFirst = Math.Max(rw.VisibleFirst, prefix);
        int visLast = Math.Max(rw.VisibleLast + 1, prefix);
        if (visFirst < sc.FirstRealized || visLast > sc.LastRealized) return true;
        // The lead band (velocity-sized overscan) is realized the moment it outruns the realized window.
        if (wantFirst < sc.FirstRealized || wantLast > sc.LastRealized) return true;
        // Trim: the realized window is wider than the present-time one by more than a slack band (a settled fling left
        // its velocity-sized overscan realized) — re-realize so the surplus parks. The slack keeps a slowing fling from
        // re-windowing every frame.
        int realizedCount = sc.LastRealized - sc.FirstRealized;
        int wantCount = wantLast - wantFirst;
        return realizedCount > wantCount + Math.Max(TrimSlackRows, wantCount / 2);
    }

    /// <summary>Rows of surplus a realized window may keep over the present-time window before it trims.</summary>
    public const int TrimSlackRows = 4;


    /// <summary>The viewport's declared snap grid in the plan's double domain.</summary>
    public static SnapGrid SnapGridOf(in ScrollState sc)
        => sc.HasSnap ? new SnapGrid(sc.SnapInterval, sc.SnapStart, sc.SnapEnd, sc.SnapPoints) : SnapGrid.None;
}
