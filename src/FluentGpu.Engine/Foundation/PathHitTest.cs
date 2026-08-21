using System.Runtime.InteropServices;

namespace FluentGpu.Foundation;

/// <summary>
/// Fill-rule-aware point-in-path containment over ALREADY-FLATTENED polygon contours (gpu-renderer.md §5.1 — "the
/// <see cref="Rule"/> the tessellator and hit-test both fill"). This is the ONE containment routine in this repo:
/// <c>PathSuite</c>'s own fill-rule differential gate (<c>gate.path.fill.winding</c>) and
/// <see cref="Input.InputDispatcher"/>'s opt-in geometry hit-test (<c>PathSpec.HitTestGeometry</c>) both call THIS
/// implementation — there is deliberately no second point-in-polygon routine anywhere that could silently disagree
/// with it.
///
/// <para><b>Algorithm:</b> a standard horizontal-ray signed-crossing accumulation. For each contour edge (a, b) whose
/// Y span straddles the test Y under the half-open rule <c>(a.Y &lt;= y) != (b.Y &lt;= y)</c> — the standard tie-break
/// that makes a ray passing exactly through a shared vertex count on exactly ONE of its two adjoining edges, never
/// zero or two — compute the edge's X at that Y and, if it lies to the right of the test point, accumulate a signed
/// crossing (+1 for an upward edge, -1 for a downward one). <see cref="FillRule.NonZero"/> asks whether the
/// accumulated winding is non-zero; <see cref="FillRule.EvenOdd"/> asks whether it is odd.</para>
///
/// <para><b>Degenerate input is clamped, never thrown</b> (this repo's documented posture for malformed geometry,
/// validation.md): a contour with fewer than 3 points is skipped outright (it encloses no area under any winding,
/// so it cannot change the answer) and a zero-length edge (two consecutive duplicate points) is skipped by the same
/// "no Y span" test that already skips a purely horizontal edge — neither case needs its own branch.</para>
///
/// <para><b>Cost:</b> O(total edges), zero heap allocation, no LINQ, no closures — this runs on the input-dispatch
/// path (<c>gate.path.hit.alloc-zero</c> proves the allocation half), not merely inside a frame hot phase.</para>
/// </summary>
public static class PathHitTest
{
    /// <summary>
    /// True iff (<paramref name="x"/>, <paramref name="y"/>) is inside the polygon described by
    /// <paramref name="starts"/>/<paramref name="counts"/> contour runs over interleaved-XY <paramref name="coords"/>
    /// (<c>coords[2*i]</c> = point i's X, <c>coords[2*i+1]</c> = its Y — the same layout <c>IconRaster</c> and
    /// <c>PathSuite</c>'s differential-raster gate already use), under <paramref name="rule"/>.
    /// </summary>
    public static bool Contains(ReadOnlySpan<float> coords, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts,
        FillRule rule, float x, float y)
    {
        int winding = 0;
        for (int c = 0; c < starts.Length; c++)
        {
            int off = starts[c], n = counts[c];
            if (n < 3) continue;   // no area under any winding — a degenerate contour contributes nothing

            for (int i = 0; i < n; i++)
            {
                int ia = off + i;
                int ib = off + (i + 1 == n ? 0 : i + 1);   // implicit close: last point wraps to the contour's first
                float ax = coords[ia * 2], ay = coords[ia * 2 + 1];
                float bx = coords[ib * 2], by = coords[ib * 2 + 1];
                if (ay == by) continue;   // horizontal edge (a zero-length duplicate-point edge is one of these too): no Y crossing

                bool up = ay < by;
                float lo = up ? ay : by, hi = up ? by : ay;
                if (y < lo || y >= hi) continue;   // half-open [lo, hi): the shared-vertex tie-break (see type doc)

                float t = (y - ay) / (by - ay);
                float xAt = ax + (bx - ax) * t;
                if (xAt > x) winding += up ? 1 : -1;
            }
        }
        return rule == FillRule.EvenOdd ? (winding & 1) != 0 : winding != 0;
    }

    /// <summary>Convenience overload over <see cref="Point2"/> contours — the exact shape
    /// <see cref="Render.PathFlatten.Flatten"/> emits. <see cref="Point2"/> is two blittable floats (X then Y), so
    /// this is a zero-cost reinterpret cast into the primary overload above, NOT a second implementation.</summary>
    public static bool Contains(ReadOnlySpan<Point2> points, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts,
        FillRule rule, float x, float y)
        => Contains(MemoryMarshal.Cast<Point2, float>(points), starts, counts, rule, x, y);
}
