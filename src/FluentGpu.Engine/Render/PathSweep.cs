using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// Fill triangulation for <see cref="PathTessellator"/> (gpu-renderer.md §5 step 2) — the genuinely new algorithm in
/// this batch. <b>Ear-clipping is explicitly deleted by canon; this is the one vetted O(n log n) monotone/trapezoidal
/// sweep instead.</b>
///
/// <para><b>Why flattening first makes this far simpler than a curve-aware sweep:</b> every edge handed to this class
/// is already a straight line segment (<see cref="PathFlatten"/> ran first), and a straight segment is trivially
/// y-monotone — there is no need to split edges at local curvature extrema the way a curve-aware sweep would. The
/// entire "find y-monotone pieces" phase that makes classical polygon triangulation intricate simply does not exist
/// here.</para>
///
/// <para><b>Self-intersecting fills (a pentagram, a self-crossing ribbon) come out correct BY CONSTRUCTION</b> — the
/// winding-accumulation sweep does not care whether the polygon is simple; it only cares about crossing edges and
/// their direction. That is worth calling out because it is a reason to prefer this algorithm even beyond its
/// complexity bound: a triangulator built around "the input is a simple polygon" (ear-clipping, monotone-piece
/// fan-out) gets a self-intersecting input wrong; this one doesn't.</para>
/// </summary>
public static class PathSweep
{
    /// <summary>Depth cap on the local crossing-refinement band split (gpu-renderer.md §5 step 2 / the design brief's
    /// "crossing refinement instead of Bentley–Ottmann"). Hitting the cap emits the band anyway — a sub-pixel sliver,
    /// never a crash (this repo's clamp-not-crash posture, validation.md).</summary>
    private const int MaxBandSplitDepth = 6;

    private struct Edge
    {
        public double X0, Y0, X1, Y1;   // Y0 < Y1 always (normalized on insert)
        public int Dir;                  // +1 if the ORIGINAL segment went upward in Y (Y0 was the start), else -1

        public double XAt(double y)
        {
            double t = (y - Y0) / (Y1 - Y0);
            return X0 + (X1 - X0) * t;
        }
    }

    // Reusable UI-thread-only scratch (cold path — a tessellation cache MISS, never a frame hot phase; the exact
    // precedent this repo already accepts for cold-path allocation is IconRaster's per-call crossing list).
    private sealed class Scratch
    {
        public readonly List<PathVertex> Vtx = new(256);
        public readonly List<uint> Idx = new(384);
        public readonly List<Edge> Edges = new(128);
        public readonly List<double> Ys = new(64);
        public readonly List<int> Active = new(64);
        // Trapezoid-corner dedup, keyed by EXACT (x,y) — see EmitTrapezoid: two adjacent bands sharing a boundary
        // recompute the SAME edge's x at the SAME boundary y via the identical `double` formula, so IEEE-754
        // determinism guarantees a bit-identical result both times. Deduping on that exact match is what makes
        // gate.path.fill.watertight's BY-VERTEX-INDEX check (not just by position) hold for the general sweep path —
        // without it, every interior band-to-band seam would look like a boundary edge (index-distinct on both sides)
        // even though it is geometrically closed.
        public readonly Dictionary<(float X, float Y), uint> TrapDedup = new(256);
        // Per-trapezoid rail scratch (EmitTrapezoid's top/bottom X lists + the fan polygon) — reused across every
        // trapezoid emitted in one Tessellate call rather than allocated per call, same discipline as the rest of
        // this Scratch.
        public readonly List<float> RailScratch1 = new(4), RailScratch2 = new(4);
        public readonly List<uint> RailPoly = new(8);
        public void Clear() { Vtx.Clear(); Idx.Clear(); Edges.Clear(); Ys.Clear(); Active.Clear(); TrapDedup.Clear(); }
    }
    private static readonly Scratch s_scratch = new();

    /// <summary>
    /// Triangulate flattened <paramref name="points"/>/<paramref name="starts"/>/<paramref name="counts"/> (the
    /// <see cref="PathFlatten.Flatten"/> shape) under <paramref name="rule"/>, plus the AA fringe (gpu-renderer.md §5
    /// step 4 — "MADE: AA-fringe, MSAA off"). Every input point is also emitted as an interior vertex verbatim (index
    /// == its position in <paramref name="points"/>) so the fast path can fan-triangulate it directly and the fringe
    /// can share it as the "inner" (Cov=1) rail of each extruded edge quad — interior coverage is NEVER inset.
    /// Returned spans alias reusable scratch (UI-thread-only, valid until the next call).
    /// </summary>
    internal static void Tessellate(ReadOnlySpan<Point2> points, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts,
        FillRule rule, float deviceScale, out ReadOnlySpan<PathVertex> vtx, out ReadOnlySpan<uint> idx, out RectF bounds)
        => Tessellate(points, starts, counts, rule, deviceScale, out vtx, out idx, out bounds, out _);

    /// <summary>Overload that also reports <paramref name="fillIdxCount"/> — the index-count boundary between the
    /// opaque fill triangles and the AA-fringe triangles appended after them (gates only: lets
    /// <c>gate.path.fill.winding</c> compare against the crisp fill geometry without the fringe's half-pixel halo
    /// muddying near-boundary samples). The plain overload omits it — production callers never need the split.</summary>
    internal static void Tessellate(ReadOnlySpan<Point2> points, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts,
        FillRule rule, float deviceScale, out ReadOnlySpan<PathVertex> vtx, out ReadOnlySpan<uint> idx, out RectF bounds,
        out int fillIdxCount)
    {
        var s = s_scratch;
        s.Clear();

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i];
            s.Vtx.Add(new PathVertex { X = p.X, Y = p.Y, Cov = 1f, S = 0f });
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }
        bounds = points.Length > 0 ? RectF.FromLTRB(minX, minY, maxX, maxY) : default;

        // The fast path only applies to a LONE convex contour: multi-contour winding (a donut, a letter with a hole)
        // requires every contour's edges in ONE joint sweep, or an independent per-contour triangulation would fill
        // a hole solid instead of punching it. So a single contour gets fan-triangulated when convex, swept when not;
        // two or more contours always go through the joint general sweep (correctness-first — still O(n log n)).
        if (starts.Length == 1)
        {
            int off = starts[0], n = counts[0];
            if (n >= 3)
            {
                if (IsConvex(points, off, n)) FanTriangulate(s, off, n);
                else SweepTriangulate(s, points, off, n, rule, deviceScale);
            }
        }
        else if (starts.Length > 1)
        {
            SweepTriangulateAll(s, points, starts, counts, rule, deviceScale);
        }

        fillIdxCount = s.Idx.Count;
        AddFringe(s, points, starts, counts, deviceScale);

        vtx = CollectionsMarshal.AsSpan(s.Vtx);
        idx = CollectionsMarshal.AsSpan(s.Idx);
    }

    // ── fast path ─────────────────────────────────────────────────────────────
    // A convex SIMPLE polygon can never self-intersect, so checking "every consecutive edge-pair turns the same way"
    // (an O(n) scan) is sufficient to license the O(n) fan — no O(n²) simplicity check is needed to reach this
    // branch. BUT same-sign consecutive turns alone is not sufficient: a self-intersecting star polygon (e.g. a
    // {5/2} pentagram, drawn as ONE closed contour that connects every 2nd vertex of a regular pentagon) also turns
    // the SAME way at every vertex — it just turns around the center TWICE (winding/density 2) instead of once, so
    // its total turning is 4π, not 2π. Accumulate the actual signed turning angle (via atan2, not just its sign) and
    // require the total to be exactly one full turn (±2π) — that is what actually distinguishes "convex" (winding
    // number 1) from "same-handedness but multiply-wound" (any {n/k} star with k>1). A fan over the latter would
    // fill the multiply-wound center under BOTH fill rules, which is exactly the bug this check exists to prevent.
    private static bool IsConvex(ReadOnlySpan<Point2> pts, int off, int n)
    {
        int sign = 0;
        double turnSum = 0;
        for (int i = 0; i < n; i++)
        {
            Point2 a = pts[off + i], b = pts[off + (i + 1) % n], c = pts[off + (i + 2) % n];
            double ux = b.X - a.X, uy = b.Y - a.Y;
            double vx = c.X - b.X, vy = c.Y - b.Y;
            double cross = ux * vy - uy * vx;
            if (Math.Abs(cross) < 1e-12) continue;   // collinear run — not decisive either way
            double dot = ux * vx + uy * vy;
            int s2 = cross > 0 ? 1 : -1;
            if (sign == 0) sign = s2;
            else if (s2 != sign) return false;
            turnSum += Math.Atan2(cross, dot);   // signed turn angle at vertex b, in (-π, π]
        }
        if (sign == 0) return false;
        return Math.Abs(Math.Abs(turnSum) - 2.0 * Math.PI) < 1e-3;
    }

    private static void FanTriangulate(Scratch s, int off, int n)
    {
        for (int i = 1; i < n - 1; i++)
            EmitTriCcw(s, (uint)off, (uint)(off + i), (uint)(off + i + 1));
    }

    // ── general sweep ────────────────────────────────────────────────────────
    private static void SweepTriangulate(Scratch s, ReadOnlySpan<Point2> pts, int off, int n, FillRule rule, float deviceScale)
    {
        Span<int> singleStart = stackalloc int[1] { off };
        Span<int> singleCount = stackalloc int[1] { n };
        SweepTriangulateAll(s, pts, singleStart, singleCount, rule, deviceScale);
    }

    private static void SweepTriangulateAll(Scratch s, ReadOnlySpan<Point2> pts, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts, FillRule rule, float deviceScale)
    {
        s.Edges.Clear();
        s.Ys.Clear();
        // An edge whose two endpoints were MEANT to share a Y (e.g. two vertices placed by independent trig
        // evaluations at symmetric angles) is not guaranteed to land bit-identical even after PathFlatten's grid
        // snap: a raw value sitting almost exactly on a grid-cell boundary can round to either neighboring cell
        // depending on which side of the boundary a sub-ULP difference happens to fall — so two "same-Y" points can
        // snap to grid cells ONE QUANTUM apart instead of colliding onto the same one (observed: a pentagram's
        // {5/2} contour, whose two bottom vertices are symmetric across the Y axis, produced Y's exactly 1 grid
        // step apart). An edge that thin is NOT a real 1-quantum-tall feature — it is grid-snap noise around a
        // TRUE horizontal edge — but its line equation (Edge.XAt, which divides by Y1-Y0) is numerically unstable
        // at that height, and the exact `a.Y == b.Y` check let it through as a "real" edge, corrupting the sweep
        // for the entire (correctly-large) band it got activated across. Treat anything at or under ~1 grid
        // quantum as horizontal (skip it) — genuine sub-pixel-tall edges have no visible vertical extent to sweep
        // anyway.
        float horizEps = 1.5f / (PathFlatten.GridSubdivisions * MathF.Max(deviceScale, 1e-6f));
        for (int c = 0; c < starts.Length; c++)
        {
            int off = starts[c], n = counts[c];
            if (n < 3) continue;
            for (int i = 0; i < n; i++)
            {
                Point2 a = pts[off + i], b = pts[off + (i + 1) % n];
                if (MathF.Abs(a.Y - b.Y) <= horizEps) continue;   // horizontal (or grid-snap-noise-thin) — no vertical crossings
                bool up = a.Y < b.Y;
                var e = new Edge
                {
                    X0 = up ? a.X : b.X, Y0 = up ? a.Y : b.Y,
                    X1 = up ? b.X : a.X, Y1 = up ? b.Y : a.Y,
                    Dir = up ? 1 : -1,
                };
                s.Edges.Add(e);
                s.Ys.Add(e.Y0); s.Ys.Add(e.Y1);
            }
        }
        if (s.Edges.Count == 0) return;

        // Extremum registry: a vertex where a contour's own two neighbors are BOTH on the same Y side of it (a
        // local Y-min or Y-max — the same vertices EmitTrapezoid's own top/bottom-is-a-point collapse targets) can
        // sit exactly on ANOTHER, unrelated contour's trapezoid edge at that same Y — e.g. two concentric regular
        // n-gons rotated so the inner one's bottom tip lands exactly on the still-hole-free outer span below it.
        // Without this, that outer trapezoid's edge is emitted unsplit, while the region above (where the inner
        // contour IS active) legitimately splits at that same point — a geometrically-correct-but-topologically-
        // cracked mesh (a T-junction: a vertex sitting in the strict interior of an unrelated triangle's edge).
        // Registering these up front lets EmitTrapezoid split ANY edge that would otherwise pass through one.
        Dictionary<float, List<float>>? extrema = null;
        for (int c = 0; c < starts.Length; c++)
        {
            int off = starts[c], n = counts[c];
            if (n < 3) continue;
            for (int i = 0; i < n; i++)
            {
                Point2 prev = pts[off + (i - 1 + n) % n], cur = pts[off + i], next = pts[off + (i + 1) % n];
                bool isMin = cur.Y <= prev.Y && cur.Y <= next.Y && (cur.Y < prev.Y || cur.Y < next.Y);
                bool isMax = cur.Y >= prev.Y && cur.Y >= next.Y && (cur.Y > prev.Y || cur.Y > next.Y);
                if (!isMin && !isMax) continue;
                extrema ??= new Dictionary<float, List<float>>();
                if (!extrema.TryGetValue(cur.Y, out var xs)) extrema[cur.Y] = xs = new List<float>();
                if (!xs.Contains(cur.X)) xs.Add(cur.X);
            }
        }

        s.Ys.Sort();
        // Dedup in place.
        int m = 0;
        for (int i = 0; i < s.Ys.Count; i++)
        {
            if (i == 0 || s.Ys[i] - s.Ys[m - 1] > 1e-9) { s.Ys[m] = s.Ys[i]; m++; }
        }
        var boundaries = new double[m];
        for (int i = 0; i < m; i++) boundaries[i] = s.Ys[i];
        if (m < 2) return;

        int edgeCount = s.Edges.Count;
        var lo = new int[edgeCount];
        var hi = new int[edgeCount];
        for (int e = 0; e < edgeCount; e++)
        {
            lo[e] = LowerBound(boundaries, s.Edges[e].Y0);
            hi[e] = LowerBound(boundaries, s.Edges[e].Y1);
        }

        int bandCount = m - 1;
        var addsAt = new List<int>[bandCount];
        var removesAt = new List<int>[bandCount];
        for (int e = 0; e < edgeCount; e++)
        {
            int a = lo[e], h = hi[e];
            if (a < bandCount) (addsAt[a] ??= new List<int>()).Add(e);
            if (h - 1 >= 0 && h - 1 < bandCount) (removesAt[h - 1] ??= new List<int>()).Add(e);
        }

        s.Active.Clear();
        for (int band = 0; band < bandCount; band++)
        {
            var adds = addsAt[band];
            if (adds != null) foreach (int e in adds) s.Active.Add(e);

            double y0 = boundaries[band], y1 = boundaries[band + 1];
            if (y1 - y0 > 1e-12 && s.Active.Count > 0)
                EmitBand(s, y0, y1, s.Active, rule, 0, deviceScale, extrema);

            var rem = removesAt[band];
            if (rem != null) foreach (int e in rem) s.Active.Remove(e);
        }
    }

    private static int LowerBound(double[] sorted, double v)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (sorted[mid] < v - 1e-9) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // Local crossing refinement: sort active edges by x at the band midpoint; if an adjacent pair's x-order flips
    // between y0 and y1, they cross somewhere inside — split the band at (an estimate of) that crossing y and recurse.
    // Depth-capped and always converges to SOME emission (never a crash) — the design brief's alternative to a global
    // Bentley–Ottmann event queue.
    private static void EmitBand(Scratch s, double y0, double y1, List<int> active, FillRule rule, int depth, float deviceScale, Dictionary<float, List<float>>? extrema)
    {
        int k = active.Count;
        if (k == 0) return;
        double ymid = (y0 + y1) * 0.5;

        var order = new int[k];
        for (int i = 0; i < k; i++) order[i] = i;
        var xmid = new double[k];
        for (int i = 0; i < k; i++) xmid[i] = s.Edges[active[i]].XAt(ymid);
        Array.Sort(order, (a, b) => xmid[a].CompareTo(xmid[b]));

        if (depth < MaxBandSplitDepth)
        {
            double bestCross = double.NaN;
            for (int i = 0; i + 1 < k; i++)
            {
                var ea = s.Edges[active[order[i]]];
                var eb = s.Edges[active[order[i + 1]]];
                double xa0 = ea.XAt(y0), xb0 = eb.XAt(y0);
                double xa1 = ea.XAt(y1), xb1 = eb.XAt(y1);
                double s0 = xa0 - xb0, s1 = xa1 - xb1;
                if (s0 == 0 || s1 == 0 || (s0 < 0) == (s1 < 0)) continue;   // no flip between top and bottom

                // Both edges are linear in y: solve xa(y) - xb(y) = 0 for y. This is EXACT (one division of two
                // values that are themselves exact at y0/y1) — not an estimate needing many refinement passes to
                // converge. The clamp below exists ONLY to keep yc strictly inside the OPEN interval (y0,y1) so the
                // two recursive sub-bands are never zero-height; it must stay a tiny absolute margin, not a coarse
                // fraction. A previous 0.02..0.98 margin discarded up to 2% of the (possibly already-small) remaining
                // sub-band on EVERY level, so a crossing near a sub-band's edge converged only linearly (not
                // exponentially) toward the true intersection — after MaxBandSplitDepth splits it could still be
                // measurably off, producing several near-but-not-quite-coincident interned vertices for what should
                // be ONE shared self-intersection point (a self-intersecting star's inner points, observed as e.g.
                // -13.47 / -13.46 / -13.471 instead of one dedup'd vertex) — which broke the by-index watertight
                // check. A near-zero margin lands on (or immeasurably close to) the true crossing in one split.
                double denom = (s1 - s0);
                if (Math.Abs(denom) < 1e-15) continue;
                double t = -s0 / denom;   // EXACT fraction of (y0,y1) where they cross
                double yc = y0 + (y1 - y0) * Math.Clamp(t, 1e-9, 1.0 - 1e-9);
                // Prefer the device-pixel-grid-ALIGNED y nearest this crossing, when it's still strictly inside the
                // band. Two edge pairs that are meant to cross at the SAME y (e.g. a mirror-symmetric self-
                // intersecting star's left and right inner points) generally do NOT compute to bit-identical y —
                // each comes from a different pair of edges, independently evaluated — so without this, one pair's
                // crossing gets resolved by this split while the OTHER pair's (nearly-but-not-exactly-the-same-y)
                // crossing needs a SEPARATE, much-thinner recursive split immediately adjacent to it, producing two
                // near-duplicate boundary vertices instead of one shared point. Snapping the split itself onto the
                // same grid PathFlatten already snapped the input to makes both pairs land on the SAME boundary
                // when they are meant to, resolving them together in one split.
                float snappedYc = PathFlatten.SnapToDevicePixelGrid(new Point2(0f, (float)yc), deviceScale).Y;
                if (snappedYc > y0 && snappedYc < y1) yc = snappedYc;
                if (double.IsNaN(bestCross) || yc < bestCross) bestCross = yc;
            }
            if (!double.IsNaN(bestCross) && bestCross > y0 + 1e-12 && bestCross < y1 - 1e-12)
            {
                EmitBand(s, y0, bestCross, active, rule, depth + 1, deviceScale, extrema);
                EmitBand(s, bestCross, y1, active, rule, depth + 1, deviceScale, extrema);
                return;
            }
        }

        // Safe (or depth-capped) — emit trapezoids directly using the midpoint order.
        int winding = 0;
        for (int i = 0; i + 1 < k; i++)
        {
            var e = s.Edges[active[order[i]]];
            winding += e.Dir;
            bool inside = rule == FillRule.EvenOdd ? (winding & 1) != 0 : winding != 0;
            if (inside)
            {
                var eL = s.Edges[active[order[i]]];
                var eR = s.Edges[active[order[i + 1]]];
                EmitTrapezoid(s, eL, eR, y0, y1, deviceScale, extrema);
            }
        }
    }

    // Any registered extremum X strictly between lo and hi (exclusive, with a small margin) gets appended to `into`
    // — a rail that would otherwise pass straight through a point another contour's degenerate apex ALSO needs at
    // this exact Y must be split there instead, or the two meshes disagree at that shared boundary (a T-junction).
    private static void AddInteriorSplits(Dictionary<float, List<float>>? extrema, float y, float lo, float hi, List<float> into)
    {
        if (extrema == null || !extrema.TryGetValue(y, out var xs)) return;
        const float eps = 1e-4f;
        foreach (float x in xs)
        {
            if (x > lo + eps && x < hi - eps) into.Add(x);
        }
        if (into.Count > 2) into.Sort();   // keep left-to-right order for the fan below (cheap insertion order is fine for 1-2 splits)
    }

    private static void EmitTrapezoid(Scratch s, in Edge left, in Edge right, double y0, double y1, float deviceScale, Dictionary<float, List<float>>? extrema)
    {
        double xl0 = left.XAt(y0), xl1 = left.XAt(y1);
        double xr0 = right.XAt(y0), xr1 = right.XAt(y1);
        if (xr0 < xl0) (xl0, xr0) = (xr0, xl0);
        if (xr1 < xl1) (xl1, xr1) = (xr1, xl1);

        // Grid-snap every INTERNED (computed) vertex to the SAME device-pixel grid PathFlatten already snapped the
        // ORIGINAL input points to (see PathFlatten's type doc: "the single highest-leverage robustness decision in
        // this file" — two points meant to coincide now do, bit-for-bit). That invariant only holds for points
        // PathFlatten touched; a crossing/trapezoid corner computed HERE (by the sweep, from two edges' line
        // equations) never went through that snap. Two crossings that are mathematically meant to be identical
        // (e.g. a mirror-symmetric self-intersecting star's left/right inner points, each computed from a
        // DIFFERENT pair of edges) can differ by a fraction of a device px from independent floating-point
        // evaluation — enough to defeat exact-key interning (gate.path.fill.watertight sees them as distinct
        // vertices with a coincidentally-overlapping edge). Snapping here restores the same "meant to be equal now
        // IS equal" guarantee for derived points, not just input ones.
        float fxl0 = PathFlatten.SnapToDevicePixelGrid(new Point2((float)xl0, (float)y0), deviceScale).X;
        float fxr0 = PathFlatten.SnapToDevicePixelGrid(new Point2((float)xr0, (float)y0), deviceScale).X;
        float fxl1 = PathFlatten.SnapToDevicePixelGrid(new Point2((float)xl1, (float)y1), deviceScale).X;
        float fxr1 = PathFlatten.SnapToDevicePixelGrid(new Point2((float)xr1, (float)y1), deviceScale).X;
        float fy0 = PathFlatten.SnapToDevicePixelGrid(new Point2(0f, (float)y0), deviceScale).Y;
        float fy1 = PathFlatten.SnapToDevicePixelGrid(new Point2(0f, (float)y1), deviceScale).Y;
        // A band whose (double) height is smaller than the grid quantum snaps BOTH rails onto the same grid line
        // (fy0 == fy1) — the band is genuinely there (it can legitimately arise adjacent to an extremum's own
        // degenerate band, or from the crossing-refinement's recursive bisection landing a sub-band this thin) but
        // has no visible extent left after snapping. Emitting it anyway would produce a flat, zero-area triangle
        // with 3 DISTINCT vertex indices (not caught by EmitTriCcw's same-index guard) that duplicates whatever
        // real edge already exists at that Y, corrupting the watertight check exactly like an unsnapped one would.
        if (fy0 == fy1) return;

        // A band boundary that lands exactly on a contour EXTREMUM (the tip of a circle/star point — the min-Y or
        // max-Y vertex where two edges meet) makes the left and right edge evaluate to the SAME x at that boundary:
        // the "trapezoid" is really a TRIANGLE there, not a quad split into two triangles. Emitting both a-b-c and
        // a-c-d unconditionally in that case creates a zero-area triangle with vertex a duplicated as b (or c as d),
        // which corrupts the by-index edge multiset (gate.path.fill.watertight): the degenerate triangle's two
        // non-self-loop edges are BOTH the same undirected pair as one edge of the real triangle, so that edge's
        // count becomes 3 instead of 1 or 2. Collapse to a single triangle instead of splitting when either rail is
        // a point — decided on the SNAPPED values InternTrapVertex will actually key on, not the raw doubles: a
        // double gap too small to matter in double precision can still be larger than the eventual snap quantum is
        // small, i.e. two doubles a fraction of a unit apart can snap to the identical grid point — so comparing
        // raw doubles risks calling a pair "distinct" when InternTrapVertex is about to collide them into the SAME
        // vertex anyway, which reproduces the exact self-loop bug this guards against.
        bool topIsPoint = fxl0 == fxr0;
        bool botIsPoint = fxl1 == fxr1;
        if (topIsPoint && botIsPoint) return;   // the whole band has zero width along this edge pair — nothing to fill

        // Build the top rail (left→right) and bottom rail (left→right) as ordered X lists, inserting any
        // registered extremum point that falls strictly inside a non-degenerate rail (see AddInteriorSplits) — a
        // rail that is already a single point (topIsPoint/botIsPoint) needs no split, it IS the shared point.
        var topXs = s.RailScratch1; topXs.Clear();
        topXs.Add(fxl0);
        if (!topIsPoint) { AddInteriorSplits(extrema, fy0, fxl0, fxr0, topXs); topXs.Add(fxr0); }
        var botXs = s.RailScratch2; botXs.Clear();
        botXs.Add(fxl1);
        if (!botIsPoint) { AddInteriorSplits(extrema, fy1, fxl1, fxr1, botXs); botXs.Add(fxr1); }

        // Fan-triangulate the polygon [top rail, left→right] + [bottom rail, right→left]. This is valid for a
        // convex (or edge-collinear, i.e. a straight rail with one or more extra mid-points) polygon, which every
        // trapezoid — degenerate-collapsed or extremum-split — here is by construction. This single routine
        // subsumes the plain quad (2 top + 2 bottom), both single-apex triangle collapses (1 top or 1 bottom), and
        // any of those WITH one or more extremum splits on either rail.
        //
        // The fan ORIGIN must be a vertex on the rail with the FEWER points (ties → top), never one on a rail that
        // has an extremum split. Fanning from a vertex whose OWN rail has 3+ collinear points makes the very first
        // triangle in the fan connect that vertex to its two immediate neighbors — which are BOTH still on that
        // same flat rail, i.e. a zero-area triangle — and every triangle after it still bridges the rail's two
        // OUTER points directly (skipping the interior split entirely), which is precisely the split this whole
        // routine exists to avoid: it leaves the split point sitting in the strict interior of that direct edge, a
        // T-junction against whatever OTHER trapezoid (in an adjacent band) legitimately uses the split. Originating
        // the fan from the OTHER rail instead walks every polygon edge — including both sides of the split — in
        // sequence, so no edge ever "jumps over" an interior point.
        var poly = s.RailPoly; poly.Clear();
        for (int i = 0; i < topXs.Count; i++) poly.Add(InternTrapVertex(s, topXs[i], fy0));
        for (int i = botXs.Count - 1; i >= 0; i--) poly.Add(InternTrapVertex(s, botXs[i], fy1));
        int n = poly.Count;
        int origin = topXs.Count <= botXs.Count ? 0 : topXs.Count;
        for (int k = 1; k + 1 < n; k++)
        {
            int i1 = (origin + k) % n, i2 = (origin + k + 1) % n;
            EmitTriCcw(s, poly[origin], poly[i1], poly[i2]);
        }
    }

    private static uint InternTrapVertex(Scratch s, float x, float y)
    {
        var key = (x, y);
        if (s.TrapDedup.TryGetValue(key, out uint idx)) return idx;
        idx = (uint)s.Vtx.Count;
        s.Vtx.Add(new PathVertex { X = x, Y = y, Cov = 1f, S = 0f });
        s.TrapDedup[key] = idx;
        return idx;
    }

    /// <summary>Emit one triangle, self-correcting winding to CCW (checked in Debug — the differential gate depends on
    /// it, so this is enforced by construction rather than trusted from callers).</summary>
    private static void EmitTriCcw(Scratch s, uint a, uint b, uint c)
    {
        // A repeated vertex INDEX (a==b, b==c, or a==c) is a zero-area degenerate — never a legitimate triangle.
        // The two callers that can produce one (EmitTrapezoid's top/bottom-is-a-point collapse and any two-band
        // trapezoid corners that snap onto the SAME device-pixel grid cell from two different, very-close-together
        // computations — e.g. a symmetric self-intersecting star's crossing points, or a trapezoid apex landing
        // exactly on a coordinate an adjacent one also reduced to) already collapse the RAILS that are meant to
        // coincide; this is the universal backstop for any OTHER combination that still slips through (a top rail
        // landing on the same snapped point as a bottom one). Skipping it costs nothing — it contributes zero fill
        // area either way — while emitting it would corrupt the by-index watertight check with a self-loop edge.
        if (a == b || b == c || a == c) return;
        var pa = s.Vtx[(int)a]; var pb = s.Vtx[(int)b]; var pc = s.Vtx[(int)c];
        double cross = (double)(pb.X - pa.X) * (pc.Y - pa.Y) - (double)(pb.Y - pa.Y) * (pc.X - pa.X);
        if (cross < 0) (b, c) = (c, b);
        s.Idx.Add(a); s.Idx.Add(b); s.Idx.Add(c);
        Debug.Assert(IsCcw(s, a, b, c) || cross == 0, "PathSweep emitted a non-CCW triangle");
    }

    private static bool IsCcw(Scratch s, uint a, uint b, uint c)
    {
        var pa = s.Vtx[(int)a]; var pb = s.Vtx[(int)b]; var pc = s.Vtx[(int)c];
        double cross = (double)(pb.X - pa.X) * (pc.Y - pa.Y) - (double)(pb.Y - pa.Y) * (pc.X - pa.X);
        return cross >= 0;
    }

    // ── AA fringe (gpu-renderer.md §5 step 4) ───────────────────────────────
    // Generated from the INPUT contours (not the trapezoid soup): one outward-extruded quad per contour edge, Cov=1 on
    // the inner rail (shared with the interior vertices — never inset) and Cov=0 on the outer rail, plus a wedge
    // triangle at each vertex so adjacent edge fringes abut. Known residual (canon's open OQ-1): at a sharp CONCAVE
    // vertex the two adjacent fringe quads can overlap slightly — accepted for v1, not fixed here.
    private static void AddFringe(Scratch s, ReadOnlySpan<Point2> pts, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts, float deviceScale)
    {
        float width = 0.5f / MathF.Max(deviceScale, 1e-6f);   // 0.5 device px, in path-local units

        for (int c = 0; c < starts.Length; c++)
        {
            int off = starts[c], n = counts[c];
            if (n < 3) continue;   // no fill area to feather (zero-area / degenerate contour)

            double area2 = 0;
            for (int i = 0; i < n; i++)
            {
                Point2 a = pts[off + i], b = pts[off + (i + 1) % n];
                area2 += (double)a.X * b.Y - (double)b.X * a.Y;
            }
            bool ccw = area2 > 0;   // orientation of THIS contour — used only to pick the outward rotation sign

            uint? prevOuter = null;
            uint firstOuter0 = 0;
            for (int i = 0; i < n; i++)
            {
                int i1 = (i + 1) % n;
                Point2 a = pts[off + i], b = pts[off + i1];
                float dx = b.X - a.X, dy = b.Y - a.Y;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9f) continue;
                float nx = ccw ? dy / len : -dy / len;
                float ny = ccw ? -dx / len : dx / len;

                uint innerA = (uint)(off + i), innerB = (uint)(off + i1);
                uint outerA = (uint)s.Vtx.Count;
                s.Vtx.Add(new PathVertex { X = a.X + nx * width, Y = a.Y + ny * width, Cov = 0f, S = 0f });
                uint outerB = (uint)s.Vtx.Count;
                s.Vtx.Add(new PathVertex { X = b.X + nx * width, Y = b.Y + ny * width, Cov = 0f, S = 0f });

                EmitTriCcw(s, innerA, outerA, outerB);
                EmitTriCcw(s, innerA, outerB, innerB);

                if (i == 0) firstOuter0 = outerA;
                if (prevOuter.HasValue)
                    EmitTriCcw(s, innerA, prevOuter.Value, outerA);
                prevOuter = outerB;
            }
            if (n >= 3 && prevOuter.HasValue)
                EmitTriCcw(s, (uint)off, prevOuter.Value, firstOuter0);   // close the wedge ring
        }
    }
}
