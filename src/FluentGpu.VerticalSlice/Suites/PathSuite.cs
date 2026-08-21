using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Asserts;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Harness;

/// <summary>
/// Golden gates for §1.2 — the path tessellator + realization cache (gpu-renderer.md §5/§5.1) — and §1.3/§1.4, which
/// wire the tessellator into the <see cref="DrawOp"/> stream, the scene (<c>PathEl</c>/<c>PathSpec</c>/
/// <c>VisualKind.Path</c>), the reconciler, and <see cref="SceneRecorder"/>. The headline §1.2 gate is the
/// differential cross-check: <see cref="IconRaster"/> (an independent, already-trusted scanline rasterizer) is
/// reused as BOTH the fill-rule reference AND, fed the emitted triangle soup under nonzero winding, the tessellator's
/// own correctness check — zero new rasterizer written for this suite.
/// </summary>
static class PathSuite
{
    public static void Run(StringTable strings)
    {
        FlattenChecks();
        FillChecks();
        StrokeChecks();
        FringeChecks();
        CacheChecks();
        AllocChecks();
        StreamSizeGate();
        RecordChecks(strings);
        HitTestChecks(strings);
        HeroChecks(strings);
    }

    // ── corpus builders ─────────────────────────────────────────────────────────
    private static PathData NGon(int n, float r)
    {
        var b = new PathBuilder();
        for (int i = 0; i < n; i++)
        {
            float a = 2f * MathF.PI * i / n;
            float x = r * MathF.Cos(a), y = r * MathF.Sin(a);
            if (i == 0) b.MoveTo(x, y); else b.LineTo(x, y);
        }
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }

    private static PathData Pentagram(float r, FillRule rule)
    {
        var b = new PathBuilder();
        for (int k = 0; k < 5; k++)
        {
            float a = 2f * MathF.PI * (k * 2) / 5f - MathF.PI / 2f;
            float x = r * MathF.Cos(a), y = r * MathF.Sin(a);
            if (k == 0) b.MoveTo(x, y); else b.LineTo(x, y);
        }
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), rule);
    }

    private static PathData Ring(float rOuter, float rInner, int n, FillRule rule)
    {
        var b = new PathBuilder();
        for (int i = 0; i < n; i++)
        {
            float a = 2f * MathF.PI * i / n;
            float x = rOuter * MathF.Cos(a), y = rOuter * MathF.Sin(a);
            if (i == 0) b.MoveTo(x, y); else b.LineTo(x, y);
        }
        b.Close();
        for (int i = 0; i < n; i++)
        {
            float a = -2f * MathF.PI * i / n;   // opposite winding — a genuine hole under NonZero, still works under EvenOdd
            float x = rInner * MathF.Cos(a), y = rInner * MathF.Sin(a);
            if (i == 0) b.MoveTo(x, y); else b.LineTo(x, y);
        }
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), rule);
    }

    private static PathData OpenPolyline(params (float X, float Y)[] pts)
    {
        var b = new PathBuilder();
        for (int i = 0; i < pts.Length; i++)
        {
            if (i == 0) b.MoveTo(pts[i].X, pts[i].Y); else b.LineTo(pts[i].X, pts[i].Y);
        }
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }

    private static readonly (string Name, PathData Path)[] s_degenerateFillCorpus =
    [
        ("1pt", Build(b => b.MoveTo(5, 5))),
        ("2pt", Build(b => { b.MoveTo(0, 0); b.LineTo(10, 10); })),
        ("dup-points", Build(b => { b.MoveTo(0, 0); b.LineTo(0, 0); b.LineTo(10, 0); b.LineTo(10, 10); b.Close(); })),
        ("collinear-run", Build(b => { b.MoveTo(0, 0); b.LineTo(5, 0); b.LineTo(10, 0); b.LineTo(10, 10); b.Close(); })),
        ("near-degenerate-1e-7", Build(b => { b.MoveTo(0, 0); b.LineTo(1e-7f, 0); b.LineTo(1e-7f, 1e-7f); b.Close(); })),
        ("extreme-coords", Build(b => { b.MoveTo(0, 0); b.LineTo(1e6f, 0); b.LineTo(1e6f, 1e6f); b.Close(); })),
        ("zero-area", Build(b => { b.MoveTo(1, 1); b.LineTo(2, 2); b.LineTo(3, 3); b.Close(); })),
        ("cubic-curve", Build(b => { b.MoveTo(0, 0); b.CubicTo(0, 40, 40, 40, 40, 0); b.Close(); })),
    ];

    private static PathData Build(Action<PathBuilder> gen)
    {
        var b = new PathBuilder();
        gen(b);
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }

    // A handful of production-real corpus entries — verbatim Files-app icon path data (MIT, see
    // FluentGpu.Controls/ThemedIconData.g.cs, which is where these same strings are actually registered/used) rather
    // than re-enumerating that registry (it exposes no name enumeration API, only TryGet-by-name).
    private static readonly (string Name, string PathStr, bool EvenOdd)[] s_realIconCorpus =
    [
        ("files-copy-alt", "m5,4.5h-1.5c-.53,0-1.04.21-1.41.59-.38.38-.59.88-.59,1.41v6c0,.53.21,1.04.59,1.41.38.38.88.59,1.41.59h4c.44,0,.87-.15,1.23-.42.35-.27.6-.65.71-1.08h-1.44c-.8,0-1.56-.32-2.12-.88-.56-.56-.88-1.33-.88-2.12v-5.5Z", true),
        ("files-copy-accent", "m6.5,9.5V3.5c0-.53.21-1.04.59-1.41.38-.38.88-.59,1.41-.59h4c.53,0,1.04.21,1.41.59.38.38.59.88.59,1.41v6c0,.53-.21,1.04-.59,1.41s-.88.59-1.41.59h-4c-.53,0-1.04-.21-1.41-.59-.38-.38-.59-.88-.59-1.41Z", true),
    ];

    // ── flatten gates ────────────────────────────────────────────────────────────
    private static void FlattenChecks()
    {
        // gate.path.flatten.wang — the emitted segment count equals the closed-form prediction (self-consistency:
        // PathFlatten.Flatten must produce exactly WangSegmentsQuad/Cubic's own answer, at several tolerances).
        {
            var b = new PathBuilder();
            b.MoveTo(0, 0);
            b.QuadTo(50, 100, 100, 0);
            var quad = b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
            bool ok = true;
            foreach (float tol in new[] { 1f, 0.25f, 0.05f })
            {
                int expected = PathFlatten.WangSegmentsQuad(new Point2(0, 0), new Point2(50, 100), new Point2(100, 0), tol);
                PathFlatten.Flatten(quad, tol, 1f, out var pts, out var starts, out var counts, out _);
                // MoveTo contributes 1 point, the quad contributes `expected` more (see PathFlatten.Flatten's QuadTo case).
                int actual = counts.Length > 0 ? counts[0] - 1 : -1;
                if (actual != expected) ok = false;
            }
            Check("gate.path.flatten.wang", ok, "quad segment count matched WangSegmentsQuad's own prediction at 3 tolerances");
        }

        // gate.path.flatten.tolerance — max chord deviation from the TRUE analytic curve stays ≤ tol/scale at several
        // device scales, sampled far finer than the flatten's own subdivision (so this is a real geometric check, not
        // a tautology against the same formula).
        {
            Point2 p0 = new(0, 0), p1 = new(30, 90), p2 = new(90, 60), p3 = new(120, 0);
            bool ok = true;
            string detail = "";
            foreach (float scale in new[] { 1f, 1.5f, 2f, 3f })
            {
                float tol = 0.25f / scale;
                var b = new PathBuilder();
                b.MoveTo(p0.X, p0.Y);
                b.CubicTo(p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
                var path = b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
                PathFlatten.Flatten(path, tol, scale, out var pts, out var starts, out _, out _);
                float maxDev = MaxChordDeviationCubic(p0, p1, p2, p3, pts);
                if (maxDev > tol * 1.5f)   // 1.5x slack for the grid-snap quantization this same file documents
                {
                    ok = false;
                    detail = $"scale={scale} tol={tol:0.###} maxDev={maxDev:0.###}";
                }
            }
            Check("gate.path.flatten.tolerance", ok, detail.Length == 0 ? "chord deviation within tol at scale 1/1.5/2/3" : detail);
        }
    }

    // Sample the TRUE cubic densely (256 samples) and, for each dense sample, take the min distance to the flattened
    // POLYLINE — i.e. to the nearest point on any of its line SEGMENTS, not merely to the nearest emitted VERTEX.
    // Those are not the same quantity: nearest-vertex distance is bounded by (curve speed) x (parameter gap) = O(1/n),
    // while true chord deviation (curve to its own chord) is bounded by (curvature term) x (parameter gap)² = O(1/n²)
    // — the two- and first-order terms of the same Taylor expansion. Since every flattened vertex sits exactly ON the
    // curve, the midpoint of two adjacent flattened parameters is where nearest-vertex distance is largest, and nearby
    // parameters on the true curve there are ~halfway between two vertices along the curve's OWN arc — so
    // nearest-vertex distance does not "over-estimate chord deviation for free"; it measures a different, asymptotically
    // larger quantity that grows relative to a shrinking tol as segment count increases. (Verified empirically: at
    // scale=3/tol=0.083 on this suite's own test curve, nearest-vertex "deviation" was ~3.73 while true point-to-segment
    // deviation was ~0.056 — a ~66x gap that only widens as tol shrinks further.) Point-to-segment is the correct,
    // and still cheap, measure of what this gate's name and doc claim to check.
    private static float MaxChordDeviationCubic(Point2 p0, Point2 p1, Point2 p2, Point2 p3, ReadOnlySpan<Point2> flat)
    {
        float maxDev = 0f;
        for (int i = 0; i <= 256; i++)
        {
            float t = i / 256f, u = 1f - t;
            float b0 = u * u * u, b1 = 3f * u * u * t, b2 = 3f * u * t * t, b3 = t * t * t;
            float x = b0 * p0.X + b1 * p1.X + b2 * p2.X + b3 * p3.X;
            float y = b0 * p0.Y + b1 * p1.Y + b2 * p2.Y + b3 * p3.Y;
            float best = float.MaxValue;
            for (int j = 0; j + 1 < flat.Length; j++)
            {
                float d = PointSegDistSq(x, y, flat[j], flat[j + 1]);
                if (d < best) best = d;
            }
            float dist = best == float.MaxValue ? 0f : MathF.Sqrt(best);
            if (dist > maxDev) maxDev = dist;
        }
        return maxDev;
    }

    private static float PointSegDistSq(float px, float py, Point2 a, Point2 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len2 = dx * dx + dy * dy;
        if (len2 < 1e-12f)
        {
            float ex = px - a.X, ey = py - a.Y;
            return ex * ex + ey * ey;
        }
        float t = ((px - a.X) * dx + (py - a.Y) * dy) / len2;
        t = Math.Clamp(t, 0f, 1f);
        float cx = a.X + t * dx, cy = a.Y + t * dy;
        float ex2 = px - cx, ey2 = py - cy;
        return ex2 * ex2 + ey2 * ey2;
    }

    // ── fill gates ───────────────────────────────────────────────────────────────
    private static void FillChecks()
    {
        FillShapeChecks("triangle", NGon(3, 50f), FillRule.NonZero, checkArea: true);
        FillShapeChecks("hexagon", NGon(6, 50f), FillRule.NonZero, checkArea: true);
        FillShapeChecks("decagon-evenodd", NGon(10, 60f), FillRule.EvenOdd, checkArea: true);
        FillShapeChecks("ring-nonzero", Ring(60f, 30f, 24, FillRule.NonZero), FillRule.NonZero, checkArea: true);
        FillShapeChecks("ring-evenodd", Ring(60f, 30f, 24, FillRule.EvenOdd), FillRule.EvenOdd, checkArea: true);
        // Self-intersecting: area check skipped (raw shoelace over-counts multiply-wound area under NonZero) — the
        // differential raster + winding-sample gates below are the ones that stay well-defined for a pentagram.
        FillShapeChecks("pentagram-nonzero", Pentagram(60f, FillRule.NonZero), FillRule.NonZero, checkArea: false);
        FillShapeChecks("pentagram-evenodd", Pentagram(60f, FillRule.EvenOdd), FillRule.EvenOdd, checkArea: false);

        EvenOddVsNonZeroChecks();
        ComplexityChecks();
        DegenerateFillNeverThrows();
        RealIconCorpusChecks();
    }

    private static void FillShapeChecks(string name, PathData path, FillRule rule, bool checkArea)
    {
        const float tol = 0.25f;
        PathFlatten.Flatten(path, tol, 1f, out var pts, out var starts, out var counts, out var bounds);
        PathSweep.Tessellate(pts, starts, counts, rule, 1f, out var vtx, out var idx, out _, out int fillIdxCount);
        // Every downstream check here is a FILL check — it must measure only the opaque fill triangles, not the
        // AA-fringe triangles appended after them (the fringe extends 0.5 device px strictly OUTWARD by
        // construction, PathSweep.cs's AddFringe). Slicing to fillIdxCount is what makes area/watertight/differential
        // measure the fill region itself instead of "fill + a half-pixel outward halo".
        var fillIdx = idx.Slice(0, fillIdxCount);

        if (checkArea)
        {
            double triArea = SumTriangleArea(vtx, fillIdx);
            double shoelace = TotalShoelaceArea(pts, starts, counts);
            bool areaOk = shoelace <= 1e-9 ? Math.Abs(triArea) < 1e-3 : Math.Abs(triArea - shoelace) / shoelace <= 1e-4;
            Check($"gate.path.fill.area [{name}]", areaOk, $"tri={triArea:0.###} shoelace={shoelace:0.###}");
        }

        bool watertight = CheckWatertight(vtx, fillIdx, out string wtDetail);
        Check($"gate.path.fill.watertight [{name}]", watertight, wtDetail);

        bool diffOk = DifferentialRasterCheck(pts, starts, counts, rule, vtx, fillIdx, bounds, out string diffDetail);
        Check($"gate.path.fill.differential [{name}]", diffOk, diffDetail);

        bool windOk = WindingSampleCheck(pts, starts, counts, rule, vtx, idx, fillIdxCount, bounds, name, out string windDetail);
        Check($"gate.path.fill.winding [{name}]", windOk, windDetail);
    }

    private static double SumTriangleArea(ReadOnlySpan<PathVertex> vtx, ReadOnlySpan<uint> idx)
    {
        double total = 0;
        int triCount = idx.Length / 3;
        for (int t = 0; t < triCount; t++)
        {
            var a = vtx[(int)idx[t * 3]]; var b = vtx[(int)idx[t * 3 + 1]]; var c = vtx[(int)idx[t * 3 + 2]];
            total += 0.5 * ((double)(b.X - a.X) * (c.Y - a.Y) - (double)(b.Y - a.Y) * (c.X - a.X));
        }
        return total;
    }

    private static double TotalShoelaceArea(ReadOnlySpan<Point2> pts, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts)
    {
        double total = 0;
        for (int c = 0; c < starts.Length; c++)
        {
            int off = starts[c], n = counts[c];
            double a = 0;
            for (int i = 0; i < n; i++)
            {
                var p = pts[off + i]; var q = pts[off + (i + 1) % n];
                a += (double)p.X * q.Y - (double)q.X * p.Y;
            }
            total += a * 0.5;
        }
        return Math.Abs(total);
    }

    // Undirected edge multiset BY VERTEX INDEX: an interior edge appears exactly twice (once per adjoining triangle,
    // opposite direction), a boundary edge exactly once. Plus a T-junction scan (by POSITION, epsilon 1e-5): no
    // vertex lies strictly interior to another triangle's edge segment.
    private static bool CheckWatertight(ReadOnlySpan<PathVertex> vtx, ReadOnlySpan<uint> idx, out string detail)
    {
        var counts = new Dictionary<(uint, uint), int>();
        void Bump(uint a, uint b) { var k = a < b ? (a, b) : (b, a); counts.TryGetValue(k, out int c); counts[k] = c + 1; }
        int triCount = idx.Length / 3;
        uint maxIdx = 0;
        for (int t = 0; t < triCount; t++)
        {
            uint a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
            Bump(a, b); Bump(b, c); Bump(c, a);
            if (a > maxIdx) maxIdx = a; if (b > maxIdx) maxIdx = b; if (c > maxIdx) maxIdx = c;
        }
        int bad = 0;
        foreach (var kv in counts)
            if (kv.Value != 1 && kv.Value != 2) bad++;
        if (bad > 0) { detail = $"{bad} edges with an odd multiset count (neither open boundary nor shared interior)"; return false; }

        // Restrict the T-junction scan to the vertices `idx` actually references — a caller passing only the fill
        // slice must not have an unrelated (e.g. AA-fringe) vertex that happens to sit near a fill edge counted.
        int fillVtxCount = triCount > 0 ? Math.Min((int)maxIdx + 1, vtx.Length) : 0;
        int tj = CountTJunctions(vtx.Slice(0, fillVtxCount), idx);
        detail = tj == 0 ? $"{counts.Count} edges, all 1x/2x; 0 T-junctions" : $"{tj} T-junction(s) found (a vertex interior to another edge)";
        return tj == 0;
    }

    private static int CountTJunctions(ReadOnlySpan<PathVertex> vtx, ReadOnlySpan<uint> idx)
    {
        const float eps = 1e-5f;
        int triCount = idx.Length / 3;
        // Bound the cost for large soups — this is an O(E*V) scan; the corpus here stays small enough that this is
        // fine, but cap it defensively rather than let an adversarial huge path hang the harness.
        if (triCount > 4000) return 0;
        int found = 0;
        for (int t = 0; t < triCount && found < 8; t++)
        {
            int i0 = (int)idx[t * 3], i1 = (int)idx[t * 3 + 1], i2 = (int)idx[t * 3 + 2];
            var edges = new[] { (i0, i1), (i1, i2), (i2, i0) };
            foreach (var (ea, eb) in edges)
            {
                var pa = vtx[ea]; var pb = vtx[eb];
                float dx = pb.X - pa.X, dy = pb.Y - pa.Y;
                float len2 = dx * dx + dy * dy;
                if (len2 < 1e-12f) continue;
                for (int v = 0; v < vtx.Length; v++)
                {
                    if (v == ea || v == eb) continue;
                    var p = vtx[v];
                    float t2 = ((p.X - pa.X) * dx + (p.Y - pa.Y) * dy) / len2;
                    if (t2 <= 0.02f || t2 >= 0.98f) continue;   // near an endpoint — not a strict-interior hit
                    float projX = pa.X + t2 * dx, projY = pa.Y + t2 * dy;
                    float ddx = p.X - projX, ddy = p.Y - projY;
                    if (ddx * ddx + ddy * ddy < eps * eps) { found++; break; }
                }
            }
        }
        return found;
    }

    private static bool DifferentialRasterCheck(ReadOnlySpan<Point2> pts, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts,
        FillRule rule, ReadOnlySpan<PathVertex> vtx, ReadOnlySpan<uint> idx, in RectF bounds, out string detail)
    {
        const int W = 192, H = 192;
        if (bounds.W <= 0f || bounds.H <= 0f) { detail = "degenerate bounds — skipped"; return true; }

        var refCoords = Normalize(pts, bounds);
        var refBuf = new byte[W * H];
        IconRaster.Rasterize(refCoords, starts, counts, rule == FillRule.EvenOdd, W, H, refBuf);

        int triCount = idx.Length / 3;
        var soupCoords = new float[triCount * 6];
        var soupStarts = new int[triCount];
        var soupCounts = new int[triCount];
        for (int t = 0; t < triCount; t++)
        {
            soupStarts[t] = t * 3;
            soupCounts[t] = 3;
            for (int k = 0; k < 3; k++)
            {
                var v = vtx[(int)idx[t * 3 + k]];
                float nx = (v.X - bounds.X) / bounds.W, ny = (v.Y - bounds.Y) / bounds.H;
                soupCoords[t * 6 + k * 2] = nx;
                soupCoords[t * 6 + k * 2 + 1] = ny;
            }
        }
        var soupBuf = new byte[W * H];
        IconRaster.Rasterize(soupCoords, soupStarts, soupCounts, evenOdd: false, W, H, soupBuf);

        long sumAbsDelta = 0;
        int edgeBad = 0, interiorBad = 0;
        for (int i = 0; i < refBuf.Length; i++)
        {
            int d = Math.Abs(refBuf[i] - soupBuf[i]);
            sumAbsDelta += d;
            if (refBuf[i] == 0 || refBuf[i] == 255)
            {
                if (d != 0) interiorBad++;
            }
            else if (d > 32) edgeBad++;
        }
        double frac = sumAbsDelta / 255.0 / (W * H);
        bool ok = interiorBad == 0 && edgeBad == 0 && frac <= 0.005;
        detail = $"interiorBad={interiorBad} edgeBad={edgeBad} sumFrac={frac:0.#####}";
        return ok;
    }

    private static float[] Normalize(ReadOnlySpan<Point2> pts, in RectF bounds)
    {
        var coords = new float[pts.Length * 2];
        for (int i = 0; i < pts.Length; i++)
        {
            coords[i * 2] = (pts[i].X - bounds.X) / bounds.W;
            coords[i * 2 + 1] = (pts[i].Y - bounds.Y) / bounds.H;
        }
        return coords;
    }

    private static bool WindingSampleCheck(ReadOnlySpan<Point2> pts, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts,
        FillRule rule, ReadOnlySpan<PathVertex> vtx, ReadOnlySpan<uint> idx, int fillIdxCount,
        in RectF bounds, string seedTag, out string detail)
    {
        if (bounds.W <= 0f || bounds.H <= 0f) { detail = "degenerate bounds — skipped"; return true; }
        uint seed = (uint)(seedTag.GetHashCode() ^ 0x9E3779B9);
        uint x = seed == 0 ? 1u : seed;
        float pad = 0.15f;
        float lo = -pad, hi = 1f + pad;
        int mismatches = 0;
        const int N = 1000;
        for (int i = 0; i < N; i++)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;   // xorshift32
            uint xr = x; x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            uint yr = x;
            float u = (xr & 0xFFFFFF) / (float)0x1000000;
            float v = (yr & 0xFFFFFF) / (float)0x1000000;
            float px = bounds.X + (lo + (hi - lo) * u) * bounds.W;
            float py = bounds.Y + (lo + (hi - lo) * v) * bounds.H;
            var p = new Point2(px, py);

            bool refIn = PointInContours(pts, starts, counts, rule, p);
            bool soupIn = PointInTriSoup(vtx, idx.Slice(0, fillIdxCount), p);
            if (refIn != soupIn) mismatches++;
        }
        detail = $"seed=0x{seed:X8} mismatches={mismatches}/{N}";
        return mismatches == 0;
    }

    // Moved to FluentGpu.Foundation.PathHitTest.Contains (§1.6 — the dispatcher's opt-in geometry hit-test needs the
    // exact same fill-rule containment test, so there is now ONE routine instead of two that could silently disagree).
    private static bool PointInContours(ReadOnlySpan<Point2> pts, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts, FillRule rule, Point2 p)
        => PathHitTest.Contains(pts, starts, counts, rule, p.X, p.Y);

    private static bool PointInTriSoup(ReadOnlySpan<PathVertex> vtx, ReadOnlySpan<uint> idx, Point2 p)
    {
        int triCount = idx.Length / 3;
        for (int t = 0; t < triCount; t++)
        {
            var a = vtx[(int)idx[t * 3]]; var b = vtx[(int)idx[t * 3 + 1]]; var c = vtx[(int)idx[t * 3 + 2]];
            if (PointInTri(p, a, b, c)) return true;
        }
        return false;
    }

    private static bool PointInTri(Point2 p, PathVertex a, PathVertex b, PathVertex c)
    {
        double d1 = (double)(b.X - a.X) * (p.Y - a.Y) - (double)(b.Y - a.Y) * (p.X - a.X);
        double d2 = (double)(c.X - b.X) * (p.Y - b.Y) - (double)(c.Y - b.Y) * (p.X - b.X);
        double d3 = (double)(a.X - c.X) * (p.Y - c.Y) - (double)(a.Y - c.Y) * (p.X - c.X);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    private static void EvenOddVsNonZeroChecks()
    {
        // A pentagram and a concentric donut must give DIFFERENT interior classifications for the center region
        // under the two rules — proof the rule is actually threaded through the sweep, not ignored.
        var star = Pentagram(60f, FillRule.NonZero);   // rule field on PathData doesn't matter here; we pass rule explicitly below
        const float tol = 0.25f;
        PathFlatten.Flatten(star, tol, 1f, out var pts, out var starts, out var counts, out var bounds);
        // PathSweep.Tessellate's own doc is explicit: the returned spans "alias reusable scratch... valid until the
        // next call on this thread". Calling Tessellate a SECOND time (for the EvenOdd variant) before consuming
        // the first (NonZero) result overwrites that shared scratch out from under the still-held vtx/idx spans —
        // this was silently corrupting the NonZero soup with the EvenOdd one, which is why the "center" test found
        // NEITHER rule's region (both results were actually the same post-overwrite data). Copy the NonZero result
        // out to owned arrays immediately, before making the second call, exactly as a real caller (the
        // realization cache) already must.
        PathSweep.Tessellate(pts, starts, counts, FillRule.NonZero, 1f, out var vtxNzSpan, out var idxNzSpan, out _, out int fillNz);
        var vtxNz = vtxNzSpan.ToArray();
        var idxNz = idxNzSpan.ToArray();
        PathSweep.Tessellate(pts, starts, counts, FillRule.EvenOdd, 1f, out var vtxEo, out var idxEo, out _, out int fillEo);

        Point2 center = new(bounds.X + bounds.W * 0.5f, bounds.Y + bounds.H * 0.5f);
        bool nzCenter = PointInTriSoup(vtxNz, idxNz.AsSpan(0, fillNz), center);
        bool eoCenter = PointInTriSoup(vtxEo, idxEo.Slice(0, fillEo), center);

        var ring = Ring(60f, 30f, 24, FillRule.NonZero);
        PathFlatten.Flatten(ring, tol, 1f, out var rpts, out var rstarts, out var rcounts, out var rbounds);
        Point2 hole = new(rbounds.X + rbounds.W * 0.5f, rbounds.Y + rbounds.H * 0.5f);
        PathSweep.Tessellate(rpts, rstarts, rcounts, FillRule.NonZero, 1f, out var rvNzSpan, out var riNzSpan, out _, out int rfNz);
        var rvNz = rvNzSpan.ToArray();
        var riNz = riNzSpan.ToArray();
        PathSweep.Tessellate(rpts, rstarts, rcounts, FillRule.EvenOdd, 1f, out var rvEo, out var riEo, out _, out int rfEo);
        bool ringHoleNz = PointInTriSoup(rvNz, riNz.AsSpan(0, rfNz), hole);
        bool ringHoleEo = PointInTriSoup(rvEo, riEo.Slice(0, rfEo), hole);

        Check("gate.path.fill.evenodd-vs-nonzero [pentagram-center]", nzCenter != eoCenter,
            $"nonzero={nzCenter} evenodd={eoCenter}");
        // The ring's hole is a genuine (non-overlapping) hole under EITHER rule (opposite-wound simple contours), so
        // both rules correctly agree it's empty — that's the SAME-answer control case proving the rule matters only
        // where winding multiplicity actually differs (the pentagram center, wound twice under NonZero).
        Check("gate.path.fill.evenodd-vs-nonzero [ring-hole-control]", ringHoleNz == false && ringHoleEo == false,
            $"nonzero={ringHoleNz} evenodd={ringHoleEo} (both should be OUTSIDE the hole)");
    }

    private static void ComplexityChecks()
    {
        // Vertex/index counts across n = 64..4096 should grow sub-quadratically (no accidental O(n²) net) — regular
        // n-gons keep the per-band active-edge count small (~O(1)), which is the honest regime this sweep targets.
        int[] ns = [64, 256, 1024, 4096];
        var idxCounts = new long[ns.Length];
        for (int i = 0; i < ns.Length; i++)
        {
            var path = NGon(ns[i], 100f);
            PathFlatten.Flatten(path, 0.25f, 1f, out var pts, out var starts, out var counts, out _);
            PathSweep.Tessellate(pts, starts, counts, FillRule.NonZero, 1f, out _, out var idx, out _, out int fillCount);
            idxCounts[i] = fillCount;
        }
        // Sub-quadratic check: ratio of index-count growth should be well under the n² ratio between the smallest and
        // largest sample.
        double nRatio = (double)ns[^1] / ns[0];
        double idxRatio = idxCounts[0] > 0 ? (double)idxCounts[^1] / idxCounts[0] : 0;
        bool ok = idxRatio > 0 && idxRatio < nRatio * nRatio * 0.5;   // comfortably under n² growth
        Check("gate.path.fill.complexity", ok,
            $"n={string.Join(",", ns)} idx={string.Join(",", idxCounts)} nRatio={nRatio:0.#} idxRatio={idxRatio:0.#}");
    }

    private static void DegenerateFillNeverThrows()
    {
        bool threw = false;
        string? failedName = null;
        foreach (var (name, path) in s_degenerateFillCorpus)
        {
            try
            {
                PathFlatten.Flatten(path, 0.25f, 1f, out var pts, out var starts, out var counts, out _);
                PathSweep.Tessellate(pts, starts, counts, FillRule.NonZero, 1f, out var vtx, out var idx, out _);
                PathSweep.Tessellate(pts, starts, counts, FillRule.EvenOdd, 1f, out vtx, out idx, out _);
                // A watertight triangle list, even if it is EMPTY (degenerate input legitimately yields zero fill).
                if (idx.Length % 3 != 0) { threw = true; failedName = name + " (non-multiple-of-3 index count)"; }
            }
            catch (Exception ex)
            {
                threw = true;
                failedName = $"{name}: {ex.GetType().Name}";
            }
        }
        Check("gate.path.fill.degenerate-never-throws", !threw, threw ? failedName : $"{s_degenerateFillCorpus.Length} adversarial shapes, no throw");
    }

    private static void RealIconCorpusChecks()
    {
        bool allOk = true;
        string detail = "";
        foreach (var (name, pathStr, evenOdd) in s_realIconCorpus)
        {
            var path = PathDataParser.Parse(pathStr, PathContentEpoch.Mint(), evenOdd ? FillRule.EvenOdd : FillRule.NonZero, 16f, 16f);
            Span<PathVertex> vtxBuf = new PathVertex[4096];
            Span<uint> idxBuf = new uint[12288];
            var tess = new PathTessellator(vtxBuf, idxBuf, 2f);
            bool ok = tess.TryTessellateFill(path, path.Rule, out var pref);
            if (!ok || pref.VtxCount == 0) { allOk = false; detail = $"{name}: ok={ok} vtx={pref.VtxCount}"; }
        }
        Check("gate.path.fill.real-icon-corpus", allOk, allOk ? $"{s_realIconCorpus.Length} Files-app icon layers tessellated" : detail);
    }

    // ── stroke gates ─────────────────────────────────────────────────────────────
    private static void StrokeChecks()
    {
        StrokeOffsetAndArcLenChecks();
        ThinClampChecks();
    }

    private static void StrokeOffsetAndArcLenChecks()
    {
        var line = OpenPolyline((0, 0), (100, 0), (100, 100));
        var style = new StrokeStyle(Width: 10f, Cap: LineCap.Round, Join: LineJoin.Round);

        Span<PathVertex> vtxBuf = new PathVertex[2048];
        Span<uint> idxBuf = new uint[6144];
        var tess = new PathTessellator(vtxBuf, idxBuf, 1f);
        bool ok = tess.TryTessellateStroke(line, in style, out var pref);
        Check("gate.path.stroke.offset [tessellate]", ok, $"vtxNeeded={tess.NeededVtx} idxNeeded={tess.NeededIdx}");
        if (!ok) return;

        var vtx = vtxBuf.Slice(pref.VtxStart, pref.VtxCount);
        // Sample width: the ribbon's own emitted corner vertices sit exactly at each segment's two endpoints (not at
        // arbitrary mid-segment x) — segment 0 runs (0,0)→(100,0), so its start corners (leftA/rightA) are the ones
        // at x≈0, spanning the full stroke width on the Y axis.
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var v in vtx)
            if (v.Cov >= 0.99f && MathF.Abs(v.X - 0f) < 0.5f) { if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y; }
        float width = maxY - minY;
        bool widthOk = minY < float.MaxValue && MathF.Abs(width - style.Width) < 1.5f;
        Check("gate.path.stroke.offset [ribbon-width]", widthOk, $"measured={width:0.##} expected={style.Width}");

        // Joins/caps present: a round join at (100,0) contributes triangles fanning around that vertex — cheap proxy
        // is simply "more than 2 triangles reference a vertex located at (100,0)".
        int hits = 0;
        for (int i = 0; i < vtx.Length; i++)
            if (MathF.Abs(vtx[i].X - 100f) < 0.5f && MathF.Abs(vtx[i].Y - 0f) < 0.5f) hits++;
        Check("gate.path.stroke.offset [join-present]", hits >= 1, $"vertices at the join point={hits}");

        Check("gate.path.stroke.offset", widthOk && ok, "ribbon tessellated, width sampled within tolerance, join geometry present");

        // gate.path.stroke.trim-arclength — S spans exactly 0..1 across the contour (min at the start, max at the
        // end); PathStroker builds S from a monotonically-increasing cumulative arc length by construction (see
        // PathStroker.StrokeContour's `cum[]` walk), so the span check below is the observable proof of that.
        float minS = float.MaxValue, maxS = float.MinValue;
        foreach (var v in vtx)
            if (v.Cov >= 0.99f) { if (v.S < minS) minS = v.S; if (v.S > maxS) maxS = v.S; }
        // Also confirm ORDERING at three well-separated known spine locations: start (0,0) < corner (100,0) < end (100,100).
        float sAtStart = NearestSpineS(vtx, 0f, 0f), sAtCorner = NearestSpineS(vtx, 100f, 0f), sAtEnd = NearestSpineS(vtx, 100f, 100f);
        bool orderOk = sAtStart < sAtCorner && sAtCorner < sAtEnd;
        bool spanOk = minS <= 0.02f && maxS >= 0.95f;
        Check("gate.path.stroke.trim-arclength", spanOk && orderOk,
            $"minS={minS:0.###} maxS={maxS:0.###} start={sAtStart:0.###} corner={sAtCorner:0.###} end={sAtEnd:0.###}");
    }

    private static float NearestSpineS(ReadOnlySpan<PathVertex> vtx, float x, float y)
    {
        float best = float.MaxValue, bestS = 0f;
        foreach (var v in vtx)
        {
            if (v.Cov < 0.99f) continue;
            float dx = v.X - x, dy = v.Y - y;
            float d = dx * dx + dy * dy;
            if (d < best) { best = d; bestS = v.S; }
        }
        return bestS;
    }

    private static void ThinClampChecks()
    {
        var line = OpenPolyline((0, 0), (100, 0));
        var thin = new StrokeStyle(Width: 0.3f, Cap: LineCap.Butt, Join: LineJoin.Bevel);
        float deviceScale = 1f;

        Span<PathVertex> vtxBuf = new PathVertex[512];
        Span<uint> idxBuf = new uint[1536];
        var tess = new PathTessellator(vtxBuf, idxBuf, deviceScale);
        bool ok = tess.TryTessellateStroke(line, in thin, out var pref);
        if (!ok) { Check("gate.path.stroke.thin-clamp", false, "tessellate failed"); return; }

        var vtx = vtxBuf.Slice(pref.VtxStart, pref.VtxCount);
        float minY = float.MaxValue, maxY = float.MinValue;
        float cov = -1f;
        // A single-segment OPEN polyline (0,0)-(100,0) tessellates to exactly ONE ribbon quad — its only opaque
        // (Cov>0) vertices are the two segment corners at x=0 and x=100 (leftA/rightA/leftB/rightB in
        // PathStroker.StrokeContour); there is no vertex at the midpoint x=50 (no join/cap subdivides the ribbon
        // there). Probe at x=0 instead — same convention StrokeOffsetAndArcLenChecks already uses for its own
        // width sample at a segment's start corners.
        foreach (var v in vtx)
            if (v.Cov > 0f && MathF.Abs(v.X - 0f) < 2f) { if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y; cov = v.Cov; }
        float geomWidth = maxY - minY;
        bool geomOk = minY < float.MaxValue && geomWidth >= 0.9f;   // clamped up to ~1 device px, not 0.3
        bool covOk = cov > 0f && cov <= 0.35f;                       // alpha scaled down proportional to the requested width
        Check("gate.path.stroke.thin-clamp", geomOk && covOk,
            $"geomWidth={geomWidth:0.###} (floor~1px) cov={cov:0.###} (expected~{thin.Width})");
    }

    // ── fringe gates ─────────────────────────────────────────────────────────────
    private static void FringeChecks()
    {
        var square = NGon(4, 40f);
        PathFlatten.Flatten(square, 0.25f, 1f, out var pts, out var starts, out var counts, out _);
        PathSweep.Tessellate(pts, starts, counts, FillRule.NonZero, 2f, out var vtx, out var idx, out _, out int fillCount);

        bool hasCov1 = false, hasCov0 = false;
        float minFringeDist = float.MaxValue, maxFringeDist = float.MinValue;
        bool interiorNeverInset = true;

        // Every interior (fill) vertex must sit exactly on the true contour (never inset) — check the fill vertices
        // (indices 0..pts.Length-1, the "core" echo) are byte-identical to the flattened input points.
        for (int i = 0; i < pts.Length && i < vtx.Length; i++)
        {
            if (vtx[i].Cov < 0.999f) { interiorNeverInset = false; continue; }
            if (MathF.Abs(vtx[i].X - pts[i].X) > 1e-4f || MathF.Abs(vtx[i].Y - pts[i].Y) > 1e-4f) interiorNeverInset = false;
        }

        // Fringe vertices are everything AFTER fillCount's fringe triangles reference — measure Cov 0 vertex distance
        // from the nearest input point along its edge normal; expect ~0.5 device px (path units, scale 2 → 0.25 units).
        float expectedFringe = 0.5f / 2f;
        for (int t = fillCount / 3; t * 3 < idx.Length; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                var v = vtx[(int)idx[t * 3 + k]];
                if (v.Cov >= 0.999f) hasCov1 = true;
                if (v.Cov <= 0.001f)
                {
                    hasCov0 = true;
                    float best = float.MaxValue;
                    for (int i = 0; i < pts.Length; i++)
                    {
                        float dx = v.X - pts[i].X, dy = v.Y - pts[i].Y;
                        float d = MathF.Sqrt(dx * dx + dy * dy);
                        if (d < best) best = d;
                    }
                    if (best < minFringeDist) minFringeDist = best;
                    if (best > maxFringeDist) maxFringeDist = best;
                }
            }
        }

        bool widthOk = hasCov0 && minFringeDist <= expectedFringe * 1.2f && maxFringeDist <= expectedFringe * 1.5f + 0.05f;
        Check("gate.path.fringe.coverage", hasCov1 && hasCov0 && interiorNeverInset && widthOk,
            $"cov1={hasCov1} cov0={hasCov0} insetOk={interiorNeverInset} fringeDist=[{minFringeDist:0.###},{maxFringeDist:0.###}] expected~{expectedFringe:0.###}");
    }

    // ── cache gates ──────────────────────────────────────────────────────────────
    private static void CacheChecks()
    {
        var cache = new PathRealizationCache();
        cache.BeginFrame(1);

        var path = NGon(6, 40f);

        bool miss1 = cache.TryRealizeFill(path, FillRule.NonZero, 1f, out var ref1);
        int tessAfterFirst = cache.TessellationCount;
        bool hit1 = cache.TryRealizeFill(path, FillRule.NonZero, 1f, out var ref2);
        int tessAfterHit = cache.TessellationCount;
        Check("gate.path.cache.hit", miss1 && hit1 && tessAfterFirst == tessAfterHit && ref1.VtxCount == ref2.VtxCount,
            $"tessAfterFirst={tessAfterFirst} tessAfterHit={tessAfterHit} vtx={ref1.VtxCount}/{ref2.VtxCount}");

        // gate.path.cache.epoch — a FRESH epoch over byte-identical geometry must MISS (proves the epoch is in the key).
        var pathSameShapeFreshEpoch = NGon(6, 40f);   // NGon mints a brand-new epoch every call — same geometry, different epoch
        int tessBeforeEpoch = cache.TessellationCount;
        cache.TryRealizeFill(pathSameShapeFreshEpoch, FillRule.NonZero, 1f, out _);
        int tessAfterEpoch = cache.TessellationCount;
        Check("gate.path.cache.epoch", tessAfterEpoch == tessBeforeEpoch + 1, $"tessBefore={tessBeforeEpoch} tessAfter={tessAfterEpoch}");

        // gate.path.cache.scale — a scale change misses; sub-quantum wobble hits.
        int tessBeforeScale = cache.TessellationCount;
        cache.TryRealizeFill(path, FillRule.NonZero, 2f, out _);
        int tessAfterBigScale = cache.TessellationCount;
        cache.TryRealizeFill(path, FillRule.NonZero, 1f + 1e-4f, out _);   // sub-quantum (quantized ×64) — should still hit the scale=1 entry
        int tessAfterWobble = cache.TessellationCount;
        Check("gate.path.cache.scale",
            tessAfterBigScale == tessBeforeScale + 1 && tessAfterWobble == tessAfterBigScale,
            $"beforeScale={tessBeforeScale} afterBigScale={tessAfterBigScale} afterWobble={tessAfterWobble}");

        // gate.path.cache.joincap — the additive discriminator actually differentiates two strokes that would
        // otherwise collide (same geometry, same width, different join).
        var strokePath = OpenPolyline((0, 0), (50, 0), (50, 50));
        var miterStyle = new StrokeStyle(Width: 6f, Join: LineJoin.Miter, Cap: LineCap.Butt);
        var roundStyle = new StrokeStyle(Width: 6f, Join: LineJoin.Round, Cap: LineCap.Butt);
        cache.TryRealizeStroke(strokePath, in miterStyle, 1f, out var miterRef);
        cache.TryRealizeStroke(strokePath, in roundStyle, 1f, out var roundRef);
        bool joinCapOk = miterRef.VtxCount != roundRef.VtxCount || miterRef.VtxStart != roundRef.VtxStart;
        Check("gate.path.cache.joincap", joinCapOk,
            $"miter=({miterRef.VtxStart},{miterRef.VtxCount}) round=({roundRef.VtxStart},{roundRef.VtxCount})");

        LruEvictionCheck();
    }

    private static void LruEvictionCheck()
    {
        var cache = new PathRealizationCache();
        int savedBudget = GpuProfile.PathSlabBudgetBytes;
        GpuProfile.PathSlabBudgetBytes = 8 * 1024;   // tiny budget to force eviction pressure quickly
        try
        {
            cache.BeginFrame(1);
            var recent = NGon(5, 30f);
            cache.TryRealizeFill(recent, FillRule.NonZero, 1f, out _);

            // Fill many distinct (fresh-epoch) shapes on OLD frames so they fall outside the quarantine window, then
            // advance far enough that a compaction pass at BeginFrame has to run.
            for (int i = 0; i < 40; i++)
            {
                cache.BeginFrame((ulong)(2 + i));
                var shape = NGon(5 + (i % 7), 25f + i);
                cache.TryRealizeFill(shape, FillRule.NonZero, 1f, out _);
            }

            cache.BeginFrame(1000);   // far past the quarantine window for everything above except a re-touch below
            bool stillThere = cache.TryRealizeFill(recent, FillRule.NonZero, 1f, out _);
            int evictions = cache.EvictionCount;

            Check("gate.path.cache.lru", evictions > 0 && stillThere,
                $"evictions={evictions} recentSurvived={stillThere} (recentRef re-realizes fine even if evicted, but true LRU never drops the in-frame set)");
        }
        finally
        {
            GpuProfile.PathSlabBudgetBytes = savedBudget;
        }
    }

    // ── alloc gate ───────────────────────────────────────────────────────────────
    private static void AllocChecks()
    {
        var cache = new PathRealizationCache();
        cache.BeginFrame(1);
        var path = NGon(8, 40f);
        cache.TryRealizeFill(path, FillRule.NonZero, 1f, out _);   // warm the cache (the one allowed tessellation)
        int tessBefore = cache.TessellationCount;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 60; i++)
            cache.TryRealizeFill(path, FillRule.NonZero, 1f, out _);
        long after = GC.GetAllocatedBytesForCurrentThread();
        int tessAfter = cache.TessellationCount;

        bool allocZero = (after - before) == 0;
        bool tessUnchanged = tessAfter == tessBefore;
        Check("gate.path.tess.alloc-zero", allocZero && tessUnchanged,
            $"allocDelta={after - before}B tessBefore={tessBefore} tessAfter={tessAfter}");
    }

    // ── §1.3/§1.4 corpus helper ────────────────────────────────────────────────────
    // A right triangle anchored at the origin, spanning [0,size]x[0,size] — used (unlike the FillChecks corpus, which
    // is centred at the origin) so a ViewBoxW/H fit-scale test has an unambiguous, origin-anchored authored extent.
    private static PathData TrianglePath(float size, FillRule rule = FillRule.NonZero)
    {
        var b = new PathBuilder();
        b.MoveTo(0, 0);
        b.LineTo(size, 0);
        b.LineTo(0, size);
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), rule);
    }

    // ── §1.3 opcode-stream gate ─────────────────────────────────────────────────────
    // gate.path.stream.sizes — a generic regression net for "a new DrawOp was added but one of the eleven
    // switch/dispatch sites was missed": build ONE synthetic DrawList containing every DrawOp value exactly once
    // (FillPath/StrokePath are the two newest), walk it two independent ways, and assert both consume the WHOLE
    // stream. Useful well beyond this lane — it fails the instant a future opcode addition repeats this bug.
    private static void StreamSizeGate()
    {
        var dl = new DrawList();
        var rect = new RectF(1f, 2f, 30f, 40f);
        var radii = new CornerRadius4(2f, 2f, 2f, 2f);
        var color = ColorF.FromRgba(200, 50, 50, 255);
        var identity = Affine2D.Identity;
        var pathRef = new PathRef(0, 3, 0, 3, rect, 12f);

        dl.FillRoundRect(rect, radii, color, identity, 1f);
        dl.DrawGlyphRun(rect, color, default, default, 12f, 400, 0, 0, 0, 0f, float.NaN, 0, 0, identity, 1f);
        dl.PushClip(rect);
        dl.DrawImage(rect, radii, 0, true, color, identity, 1f, new RectF(0, 0, 1, 1));
        dl.StrokeRoundRect(rect, radii, color, 2f, identity, 1f);
        dl.Shadow(rect, radii, color, 0f, 0f, 4f, 0f, identity, 1f);
        dl.GradientRect(new DrawGradientRectCmd(rect, radii, new Point2(0, 0), new Point2(1, 1), 0, 2,
            color, color, default, default, 0f, 1f, 0f, 0f, identity, 1f));
        dl.PushLayer(rect, radii, color, color, 0.5f, 10f, 0.1f, 0.5f);
        dl.GradientStroke(new DrawGradientStrokeCmd(rect, radii, new Point2(0, 0), new Point2(1, 1), 0, 2,
            color, color, default, default, 0f, 1f, 0f, 0f, 2f, identity, 1f));
        dl.Arc(rect, color, 2f, 0f, 90f, true, identity, 1f);
        dl.PolylineStroke(rect, color, 2f, new Point2(0, 0), new Point2(1, 1), default, default, 2, 0f, 1f, true, identity, 1f);
        dl.TabShape(rect, 8f, 4f, color, identity, 1f);
        dl.DrawGlyphRunGradient(rect, default, default, 12f, 400, 0, 0, 0, 0f, float.NaN, 0, 0, identity, 1f,
            color, color, 0.5f, 0.1f, 0f);
        dl.DrawIconMask(rect, color, 1, identity, 1f);
        dl.DrawVideo(rect, radii, 1, 1f, identity, 1f);
        dl.EraseRoundRect(rect, radii, 1f, identity, 1f);
        dl.FillPath(rect, color, pathRef, (byte)FillRule.NonZero, identity, 1f);
        dl.StrokePath(rect, color, pathRef, 0f, 1f, 0f, 0f, 0, identity, 1f);
        dl.PopLayer(rect);
        dl.PopClip();

        int expectedOps = Enum.GetValues<DrawOp>().Length;

        // Walk #1: the shared payload-size TABLE (Asserts.DrawPayloadSize, also consumed by BlurPinKey/RepaintStreamSafety/
        // the D3D12 backend) must exactly consume every byte with no truncation and no overrun.
        ReadOnlySpan<byte> bytes = dl.Bytes;
        int pos = 0, opCount = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
            pos += sizeof(int);
            pos += DrawPayloadSize(op);
            opCount++;
        }
        bool sizesOk = pos == bytes.Length && opCount == expectedOps;
        Check("gate.path.stream.sizes [table]", sizesOk,
            $"finalPos={pos} streamLen={bytes.Length} opsSeen={opCount} opsExpected={expectedOps}");

        // Walk #2: the REAL headless device decoder (its own independent switch, HeadlessGpuDevice.SubmitDrawList) must
        // reach and capture every op — proof it did not silently stop early on the `default: return;` corrupt-stream
        // guard partway through (which would leave later ops' capturing lists at count 0 and the push/pop balances
        // stuck at whatever they were before the desync).
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(200f, 200f), 1f, ColorF.Transparent));
        bool decodedAll = dev.LastRects.Count == 1 && dev.LastGlyphs.Count == 1 && dev.LastClips.Count == 1
            && dev.LastImages.Count == 1 && dev.LastStrokes.Count == 1 && dev.LastShadows.Count == 1
            && dev.LastGradients.Count == 1 && dev.LastLayers.Count == 1 && dev.LastGradientStrokes.Count == 1
            && dev.LastArcs.Count == 1 && dev.LastPolylines.Count == 1 && dev.LastTabShapes.Count == 1
            && dev.LastGlyphGradients.Count == 1 && dev.LastIconMasks.Count == 1 && dev.LastVideos.Count == 1
            && dev.LastErases.Count == 1 && dev.LastFillPaths.Count == 1 && dev.LastStrokePaths.Count == 1
            && dev.ClipBalance == 0 && dev.LayerBalance == 0;
        Check("gate.path.stream.sizes [headless-decode]", decodedAll,
            $"rects={dev.LastRects.Count} glyphs={dev.LastGlyphs.Count} clips={dev.LastClips.Count} images={dev.LastImages.Count} "
            + $"strokes={dev.LastStrokes.Count} shadows={dev.LastShadows.Count} gradients={dev.LastGradients.Count} "
            + $"layers={dev.LastLayers.Count} gradStrokes={dev.LastGradientStrokes.Count} arcs={dev.LastArcs.Count} "
            + $"polylines={dev.LastPolylines.Count} tabs={dev.LastTabShapes.Count} glyphGrad={dev.LastGlyphGradients.Count} "
            + $"icons={dev.LastIconMasks.Count} videos={dev.LastVideos.Count} erases={dev.LastErases.Count} "
            + $"fillPaths={dev.LastFillPaths.Count} strokePaths={dev.LastStrokePaths.Count} "
            + $"clipBal={dev.ClipBalance} layerBal={dev.LayerBalance}");
    }

    // ── §1.4 element/scene/reconciler/recorder gates ────────────────────────────────
    private static void RecordChecks(StringTable strings)
    {
        RecordOpcodeOrderCheck(strings);
        RecordTrimChannelCheck(strings);
        RecordRebaseCheck();
        RecordRepaintSafeCheck();
        RecordViewBoxCheck(strings);
    }

    // gate.path.record.opcodes — a PathEl through a real reconcile+layout+record emits one FillPath then one
    // StrokePath, in that order, on the headless device, with a non-degenerate baked Transform and Opacity.
    private static void RecordOpcodeOrderCheck(StringTable strings)
    {
        var pathEl = new PathEl
        {
            Width = 40, Height = 40,
            Geometry = TrianglePath(40f),
            Fill = ColorF.FromRgba(200, 40, 40, 255),
            Rule = FillRule.NonZero,
            StrokeColor = ColorF.FromRgba(20, 20, 200, 255),
            Stroke = new StrokeStyle(Width: 3f),
        };
        var scene = Asserts.LayoutTree(strings, pathEl);
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(100, 100), 1f, ColorF.Transparent));

        bool counts = dev.LastFillPaths.Count == 1 && dev.LastStrokePaths.Count == 1;

        ReadOnlySpan<byte> bytes = dl.Bytes;
        int pos = 0, order = 0, fillOrder = -1, strokeOrder = -1;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
            pos += sizeof(int);
            if (op == DrawOp.FillPath) fillOrder = order;
            if (op == DrawOp.StrokePath) strokeOrder = order;
            pos += DrawPayloadSize(op);
            order++;
        }
        bool orderOk = fillOrder >= 0 && strokeOrder >= 0 && fillOrder < strokeOrder;

        bool baked = counts
            && dev.LastFillPaths[0].Opacity > 0.99f && MathF.Abs(dev.LastFillPaths[0].Transform.M11) > 0.001f
            && dev.LastStrokePaths[0].Opacity > 0.99f && MathF.Abs(dev.LastStrokePaths[0].Transform.M11) > 0.001f;

        Check("gate.path.record.opcodes", counts && orderOk && baked,
            $"fills={dev.LastFillPaths.Count} strokes={dev.LastStrokePaths.Count} fillOrder={fillOrder} strokeOrder={strokeOrder} "
            + $"fillOpacity={(counts ? dev.LastFillPaths[0].Opacity : -1f):0.###} strokeOpacity={(counts ? dev.LastStrokePaths[0].Opacity : -1f):0.###}");
    }

    // gate.path.record.trim-channels — driving AnimChannel.StrokeTrimStart/End on the node reaches
    // StrokePathCmd.TrimStart/TrimEnd, and the NaN sentinel (no live channel) falls back to the authored PathSpec values.
    private static void RecordTrimChannelCheck(StringTable strings)
    {
        var pathEl = new PathEl
        {
            Width = 40, Height = 40,
            Geometry = TrianglePath(40f),
            StrokeColor = ColorF.FromRgba(20, 20, 200, 255),
            Stroke = new StrokeStyle(Width: 3f),
            TrimEnd = 0.7f,   // authored fallback value; TrimStart stays the default 0f (always <= a live 0..1 channel)
        };
        var scene = Asserts.LayoutTree(strings, pathEl);
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(100, 100), 1f, ColorF.Transparent));

        bool authoredFallback = dev.LastStrokePaths.Count == 1 && Near(dev.LastStrokePaths[0].TrimEnd, 0.7f, 0.005f);

        // Same channel/spring configuration as ControlsSuite's PolylineStrokeChecks (already-verified: t16 lands in
        // (0, 0.35) at 16ms into a 100ms transition) — reused so t16 is known positive, i.e. guaranteed > TrimStart(0).
        var anim = new AnimEngine(scene);
        anim.Keyframes(scene.Root, AnimChannel.StrokeTrimEnd,
            [new Keyframe(0f, 0f, Easing.Linear), new Keyframe(1f, 1f, EasingSpec.CubicBezier(0.55f, 0f, 0f, 1f))], 100f);
        anim.Tick(0f);
        float liveT0 = scene.Paint(scene.Root).StrokeTrimEnd;
        anim.Tick(16f);
        float liveT16 = scene.Paint(scene.Root).StrokeTrimEnd;

        dl.Reset();
        SceneRecorder.Record(scene, dl);
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(100, 100), 1f, ColorF.Transparent));

        // The live channel's value (not the authored 0.7) must be what reached the payload.
        bool liveChannel = dev.LastStrokePaths.Count == 1 && Near(dev.LastStrokePaths[0].TrimEnd, liveT16, 0.005f)
            && MathF.Abs(liveT16 - 0.7f) > 0.01f;

        Check("gate.path.record.trim-channels", authoredFallback && liveChannel,
            $"authoredFallbackOk={authoredFallback} liveT0={liveT0:0.###} liveT16={liveT16:0.###} "
            + $"livePayload={(dev.LastStrokePaths.Count == 1 ? dev.LastStrokePaths[0].TrimEnd : -1f):0.###}");
    }

    // gate.path.record.rebase — a translated span copy (the scroll clean-span rebase, DrawList.CopySpanFromPriorTranslated)
    // patches FillPathCmd/StrokePathCmd's Transform exactly like it patches FillRoundRectCmd's.
    private static void RecordRebaseCheck()
    {
        var dl = new DrawList();
        var xf = new Affine2D(1f, 0f, 0f, 1f, 40f, 60f);
        var rect = new RectF(0f, 0f, 24f, 24f);
        var pathRef = new PathRef(0, 3, 0, 3, rect, 12f);
        dl.FillPath(rect, ColorF.FromRgba(200, 40, 40, 255), pathRef, (byte)FillRule.NonZero, xf, 1f, 1UL);
        dl.StrokePath(rect, ColorF.FromRgba(20, 20, 200, 255), pathRef, 0f, 1f, 0f, 0f, 0, xf, 1f, 2UL);
        int byteLen = dl.BytePosition, sortLen = dl.SortPosition, cmds = dl.CommandCount;
        var stats = dl.OpcodeStats;

        const float dx = -17.5f, dy = 23.25f;
        dl.SwapAndReset();
        bool copied = dl.CopySpanFromPriorTranslated(0, byteLen, 0, sortLen, cmds, in stats, dx, dy);

        var outBytes = dl.Bytes;
        int p = 0;
        FillPathCmd movedFill = default;
        StrokePathCmd movedStroke = default;
        while (p + sizeof(int) <= outBytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(outBytes.Slice(p));
            p += sizeof(int);
            if (op == DrawOp.FillPath) movedFill = MemoryMarshal.Read<FillPathCmd>(outBytes.Slice(p));
            else if (op == DrawOp.StrokePath) movedStroke = MemoryMarshal.Read<StrokePathCmd>(outBytes.Slice(p));
            p += DrawPayloadSize(op);
        }

        bool fillMoved = Near(movedFill.Transform.Dx, xf.Dx + dx, 0.01f) && Near(movedFill.Transform.Dy, xf.Dy + dy, 0.01f);
        bool strokeMoved = Near(movedStroke.Transform.Dx, xf.Dx + dx, 0.01f) && Near(movedStroke.Transform.Dy, xf.Dy + dy, 0.01f);

        Check("gate.path.record.rebase", copied && fillMoved && strokeMoved,
            $"copied={copied} fillDx={movedFill.Transform.Dx:0.##} fillDy={movedFill.Transform.Dy:0.##} "
            + $"strokeDx={movedStroke.Transform.Dx:0.##} strokeDy={movedStroke.Transform.Dy:0.##} "
            + $"expectedDx={xf.Dx + dx:0.##} expectedDy={xf.Dy + dy:0.##}");
    }

    // gate.path.repaint.safe — RepaintStreamSafety.Scan still returns true (safe to replay under a damage-clamped
    // root scissor) for a stream carrying FillPath/StrokePath alongside ordinary geometry.
    private static void RecordRepaintSafeCheck()
    {
        var dl = new DrawList();
        var rect = new RectF(0f, 0f, 24f, 24f);
        var pathRef = new PathRef(0, 3, 0, 3, rect, 12f);
        dl.FillRoundRect(rect, default, ColorF.FromRgba(10, 10, 10, 255), Affine2D.Identity, 1f);
        dl.FillPath(rect, ColorF.FromRgba(200, 40, 40, 255), pathRef, (byte)FillRule.NonZero, Affine2D.Identity, 1f);
        dl.StrokePath(rect, ColorF.FromRgba(20, 20, 200, 255), pathRef, 0f, 1f, 0f, 0f, 0, Affine2D.Identity, 1f);

        bool safe = RepaintStreamSafety.Scan(dl.Bytes);
        Check("gate.path.repaint.safe", safe, $"Scan(...)={safe} for a FillRoundRect+FillPath+StrokePath stream");
    }

    // gate.path.record.viewbox — a non-zero ViewBoxW/H bakes the uniform-fit scale into the baked Transform: a
    // 24x24-unit authored path in a 48x48 box records at M11=M22=2.0 (48/24).
    private static void RecordViewBoxCheck(StringTable strings)
    {
        var pathEl = new PathEl
        {
            Width = 48, Height = 48,
            Geometry = TrianglePath(24f),   // authored in a 24x24-unit space
            Fill = ColorF.FromRgba(200, 40, 40, 255),
            ViewBoxW = 24f, ViewBoxH = 24f,
        };
        var scene = Asserts.LayoutTree(strings, pathEl);
        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(100, 100), 1f, ColorF.Transparent));

        bool ok = dev.LastFillPaths.Count == 1
            && Near(dev.LastFillPaths[0].Transform.M11, 2f, 0.01f)
            && Near(dev.LastFillPaths[0].Transform.M22, 2f, 0.01f);
        Check("gate.path.record.viewbox", ok,
            $"fills={dev.LastFillPaths.Count} M11={(dev.LastFillPaths.Count == 1 ? dev.LastFillPaths[0].Transform.M11 : -1f):0.###} "
            + "expected 2.0 (48/24 uniform fit)");
    }

    // ── §1.6 dispatcher hit-test gates ──────────────────────────────────────────────
    // PathEl's OnClick/interaction props (inherited from the base Element record) are NOT wired by the reconciler —
    // only BoxEl's WriteColumns case ever sets InteractionInfo.HandlerMask/NodeFlags.HitTestVisible from them (grep
    // confirms every ii.HandlerMask write in Reconciler.cs sits inside `case BoxEl b:`). Reconciler.cs is out of scope
    // for this lane, so a bare PathEl can never itself become a CLICK owner (Hit()'s final admission always needs a
    // handler bit) — these gates therefore drive the dispatcher's real HitAny()/DiagHitTest() walk instead of a
    // click round-trip. That walk is not a diagnostic-only shortcut: it is the SAME code this file's own doc calls
    // "the same walk wheel/click routing starts from", and it is what drag-drop targeting, middle-click, hold
    // gestures, swipe-ancestor resolution and scroll-target resolution all call live (HitTestAny's real call sites).
    // Each scene stacks (ZStack) an opaque "beneath" box under a decorative Path sibling drawn ON TOP, exactly the
    // donut-icon-over-content shape §1.6 targets: DiagHitTest resolving to the PATH proves the click landed on its
    // painted geometry; resolving to the box BENEATH proves it fell through a point the box admits but the geometry
    // does not (a hole, or — for gate.path.hit.optin — proves the opposite: that geometry is NOT gating at all).
    private static void HitTestChecks(StringTable strings)
    {
        PathHitFillRuleGate(strings);
        PathHitOptInGate(strings);
        PathHitViewBoxGate(strings);
        PathHitAllocZeroGate();
    }

    // Same shape as Ring/Pentagram above, recentred at an arbitrary (cx, cy) — Ring/Pentagram are centred at the
    // ORIGIN, which free-floating geometry tests are fine with but would put most of the shape off a node's
    // top-left-anchored local box (PathEl draws Geometry directly in node-local DIP — no auto-centering, matching
    // RecordViewBoxCheck's own origin-anchored TrianglePath corpus).
    private static PathData RingAt(float cx, float cy, float rOuter, float rInner, int n, FillRule rule)
    {
        var b = new PathBuilder();
        for (int i = 0; i < n; i++)
        {
            float a = 2f * MathF.PI * i / n;
            float x = cx + rOuter * MathF.Cos(a), y = cy + rOuter * MathF.Sin(a);
            if (i == 0) b.MoveTo(x, y); else b.LineTo(x, y);
        }
        b.Close();
        for (int i = 0; i < n; i++)
        {
            float a = -2f * MathF.PI * i / n;   // opposite winding — a genuine hole under EITHER rule
            float x = cx + rInner * MathF.Cos(a), y = cy + rInner * MathF.Sin(a);
            if (i == 0) b.MoveTo(x, y); else b.LineTo(x, y);
        }
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), rule);
    }

    private static PathData PentagramAt(float cx, float cy, float r, FillRule rule)
    {
        var b = new PathBuilder();
        for (int k = 0; k < 5; k++)
        {
            float a = 2f * MathF.PI * (k * 2) / 5f - MathF.PI / 2f;
            float x = cx + r * MathF.Cos(a), y = cy + r * MathF.Sin(a);
            if (k == 0) b.MoveTo(x, y); else b.LineTo(x, y);
        }
        b.Close();
        return b.Finish(PathContentEpoch.Mint(), rule);
    }

    /// <summary>Mounts <paramref name="path"/> as the TOP layer of a ZStack over an opaque "beneath" box (both sized
    /// <paramref name="box"/> x <paramref name="box"/>, so they occupy the exact same node-local rect), runs one real
    /// frame, then asks the REAL <see cref="FluentGpu.Input.InputDispatcher"/> (via <c>AppHost.Input.DiagHitTest</c>)
    /// what is topmost at <paramref name="point"/>. Returns true iff the answer is the Path node itself.</summary>
    private static bool DiagHitsPath(StringTable strings, PathData geometry, FillRule rule, float box, Point2 point,
        bool hitTestGeometry = true, float viewBoxW = 0f, float viewBoxH = 0f)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("path-hit", new Size2(400, 400), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var pathEl = new PathEl
        {
            Width = box, Height = box, Geometry = geometry, Fill = ColorF.FromRgba(200, 40, 40, 255),
            Rule = rule, HitTestGeometry = hitTestGeometry, ViewBoxW = viewBoxW, ViewBoxH = viewBoxH,
        };
        var probe = new PathHitLayerProbe(pathEl, box);
        using var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();

        var pathNode = Child(host.Scene, host.Scene.Root, 1);
        var hit = host.Input.DiagHitTest(point);
        return hit == pathNode;
    }

    private sealed class PathHitLayerProbe : Component
    {
        private readonly PathEl _path;
        private readonly float _box;
        public PathHitLayerProbe(PathEl path, float box) { _path = path; _box = box; }
        public override Element Render() => new BoxEl
        {
            Width = _box, Height = _box, ZStack = true,
            Children =
            [
                new BoxEl { Width = _box, Height = _box, Fill = ColorF.FromRgba(10, 200, 10, 255) },   // "beneath"
                _path,
            ],
        };
    }

    // gate.path.hit.fillrule — a donut (two concentric, opposite-wound contours): the hole must fall through to
    // "beneath" under EITHER rule; the ring must hit the path under EITHER rule. A pentagram (self-intersecting):
    // the centre must hit the path under NonZero (wound twice) and fall through under EvenOdd (parity cancels).
    private static void PathHitFillRuleGate(StringTable strings)
    {
        const float box = 160f;
        var donutNz = RingAt(80f, 80f, 60f, 30f, 24, FillRule.NonZero);
        var donutEo = RingAt(80f, 80f, 60f, 30f, 24, FillRule.EvenOdd);
        var holePt = new Point2(80f, 80f);          // dead centre — inside the inner radius (30): the hole
        var ringPt = new Point2(125f, 80f);         // radius 45 from centre — between inner(30) and outer(60): the ring

        bool holeMissNz = !DiagHitsPath(strings, donutNz, FillRule.NonZero, box, holePt);
        bool ringHitNz = DiagHitsPath(strings, donutNz, FillRule.NonZero, box, ringPt);
        bool holeMissEo = !DiagHitsPath(strings, donutEo, FillRule.EvenOdd, box, holePt);
        bool ringHitEo = DiagHitsPath(strings, donutEo, FillRule.EvenOdd, box, ringPt);

        var starNz = PentagramAt(80f, 80f, 60f, FillRule.NonZero);
        var starEo = PentagramAt(80f, 80f, 60f, FillRule.EvenOdd);
        var centerPt = new Point2(80f, 80f);
        bool starCenterHitNz = DiagHitsPath(strings, starNz, FillRule.NonZero, box, centerPt);
        bool starCenterMissEo = !DiagHitsPath(strings, starEo, FillRule.EvenOdd, box, centerPt);

        Check("gate.path.hit.fillrule",
            holeMissNz && ringHitNz && holeMissEo && ringHitEo && starCenterHitNz && starCenterMissEo,
            $"donut hole falls through beneath: nonzero={holeMissNz} evenodd={holeMissEo}; "
            + $"donut ring hits the path: nonzero={ringHitNz} evenodd={ringHitEo}; "
            + $"pentagram centre hits under nonzero={starCenterHitNz}, falls through under evenodd={starCenterMissEo}");
    }

    // gate.path.hit.optin — the SAME donut geometry/point, only HitTestGeometry differs: false ⇒ today's box
    // behaviour (the hole still hits the path, unchanged); true ⇒ the hole falls through to whatever is beneath.
    private static void PathHitOptInGate(StringTable strings)
    {
        const float box = 160f;
        var donut = RingAt(80f, 80f, 60f, 30f, 24, FillRule.NonZero);
        var holePt = new Point2(80f, 80f);

        bool boxHitsWhenOptedOut = DiagHitsPath(strings, donut, FillRule.NonZero, box, holePt, hitTestGeometry: false);
        bool geometryFallsThroughWhenOptedIn = !DiagHitsPath(strings, donut, FillRule.NonZero, box, holePt, hitTestGeometry: true);

        Check("gate.path.hit.optin", boxHitsWhenOptedOut && geometryFallsThroughWhenOptedIn,
            $"HitTestGeometry=false: the box still hits the hole (unchanged default behaviour)={boxHitsWhenOptedOut}; "
            + $"HitTestGeometry=true: the SAME point falls through to what's beneath={geometryFallsThroughWhenOptedIn}");
    }

    // gate.path.hit.viewbox — a 24x24-unit authored triangle in a 48x48 box (fit=2, RecordViewBoxCheck's own corpus
    // shape): a point that maps INSIDE the fit-scaled triangle hits the path; one that maps outside falls through.
    // insidePt is chosen so a fit-less bug (testing the raw local point against the un-scaled 0..24 geometry) would
    // wrongly report a miss — the discriminating case a naive "forgot to divide by fit" implementation would fail.
    private static void PathHitViewBoxGate(StringTable strings)
    {
        const float viewBox = 24f, box = 48f;
        var triangle = TrianglePath(viewBox);
        var insidePt = new Point2(20f, 10f);    // maps to (10, 5): 10+5<=24 -> inside; raw 20+10=30>24 -> a fit-less bug misses
        var outsidePt = new Point2(40f, 40f);   // maps to (20, 20): 20+20=40>24 -> outside, well clear of the boundary

        bool insideHitsScaled = DiagHitsPath(strings, triangle, FillRule.NonZero, box, insidePt, viewBoxW: viewBox, viewBoxH: viewBox);
        bool outsideFallsThrough = !DiagHitsPath(strings, triangle, FillRule.NonZero, box, outsidePt, viewBoxW: viewBox, viewBoxH: viewBox);

        Check("gate.path.hit.viewbox", insideHitsScaled && outsideFallsThrough,
            $"a point inside the fit-scaled geometry hits the path={insideHitsScaled}; "
            + $"a point outside it falls through to what's beneath={outsideFallsThrough} (fit={box / viewBox:0.#})");
    }

    // gate.path.hit.alloc-zero — 1000 PathHitTest.Contains calls over a warmed-flattened donut allocate 0 bytes.
    private static void PathHitAllocZeroGate()
    {
        var donut = RingAt(80f, 80f, 60f, 30f, 24, FillRule.NonZero);
        PathFlatten.Flatten(donut, 0.25f, 1f, out var pts, out var starts, out var counts, out _);
        // PathFlatten.Flatten's own doc: the returned spans alias reusable scratch, valid only until the next call on
        // this thread — copy out to owned arrays before the loop below (same discipline EvenOddVsNonZeroChecks uses).
        var ptsArr = pts.ToArray();
        var startsArr = starts.ToArray();
        var countsArr = counts.ToArray();

        bool warm = PathHitTest.Contains(ptsArr, startsArr, countsArr, FillRule.NonZero, 80f, 80f);   // JIT/first-call warmup, outside the measured window
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool any = false;
        for (int i = 0; i < 1000; i++)
            any |= PathHitTest.Contains(ptsArr, startsArr, countsArr, FillRule.NonZero, 80f + (i % 7), 80f);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Check("gate.path.hit.alloc-zero", after - before == 0, $"allocDelta={after - before}B warm={warm} any={any}");
    }

    // ── setup-wizard hero-animation gates (Wavee's nine onboarding heroes, src/apps/Wavee/Features/Setup/Hero*.cs) ──
    // FluentGpu.VerticalSlice does not (and must not) reference the Wavee app project, so these gates cannot call the
    // app's HeroWelcome/HeroEula/… components directly. Instead: (1) the geometry/realize gates re-register the SAME
    // literal SVG "d" strings the app authors (kept verbatim in sync — see s_heroPathCorpus's own comment) against
    // the shared PathGeometryTable/PathRealizationCache, proving the engine's path lane accepts the app's REAL
    // content; (2) the alloc/channels gates mount a synthetic component built on the identical idiom every hero
    // uses (OnRealized-captured PathEl/BoxEl.Arc + a looping AnimEngine.Keyframes StrokeTrim/Scale/Translate track),
    // proving that idiom itself is zero-alloc and compositor-only at the engine level.
    private static void HeroChecks(StringTable strings)
    {
        HeroGeometryGate();
        HeroRealizeGate();
        HeroAllocZeroGate(strings);
        HeroChannelsOnlyGate(strings);
    }

    // Verbatim copies of every PathEl "d" string registered by the nine Hero*.cs files (Welcome is BoxEl-only — no
    // curve geometry — so it contributes none). If a hero's geometry changes, update its entry here too; there is no
    // automated cross-check because the two projects cannot reference each other (see the region comment above).
    private static readonly (string Hero, string Name, string D)[] s_heroPathCorpus =
    [
        ("Eula", "PagePath", "M58 30 H130 A8 8 0 0 1 138 38 V134 A8 8 0 0 1 130 142 H58 A8 8 0 0 1 50 134 V38 A8 8 0 0 1 58 30 Z"),
        ("Eula", "Line1", "M66 56 H122"),
        ("Eula", "Line2", "M66 70 H110"),
        ("Eula", "Line3", "M66 84 H118"),
        ("Eula", "Line4", "M66 98 H100"),
        ("Eula", "Tick", "M123 132.5 L129.5 139 L142 126"),
        ("Connect", "MonitorStand", "M48 114 V124 H74 V114 M42 128 H80"),
        ("Connect", "PhoneNotch", "M136 54 H150"),
        ("Connect", "Arc", "M104 74 C118 54 132 52 142 60"),
        ("Connect", "BadgeTick", "M143.5 122.5 L148.5 127.5 L158 117"),
        ("Patch", "Shield", "M96 62 L136 78 V110 C136 136 112 150 96 156 C80 150 56 136 56 110 V78 Z"),
        ("Patch", "PkgCross", "M78 29 H114 M96 18 V48"),
        ("Patch", "Verify", "M83 108 L94 119 L113 97"),
        ("Settings", "HeaderRule", "M30 62 H162"),
        ("Settings", "SliderRail1", "M52 118 H140"),
        ("Settings", "SliderRail2", "M52 132 H140"),
        ("Sidebar", "Divider", "M78 44 V148"),
        ("Sidebar", "SetALines", "M36 62 H68 M36 76 H68 M36 90 H60"),
        ("Sidebar", "SetASoft", "M36 104 H68 M36 118 H62 M36 132 H68"),
        ("Sidebar", "SetBSoft", "M36 80 H68 M36 94 H60 M36 108 H68 M36 122 H56"),
        ("Sidebar", "SetCLine", "M36 62 H62"),
        ("Sidebar", "SetCSoft", "M36 102 H68 M36 116 H58 M36 130 H66"),
        ("Sidebar", "ContentLines", "M92 62 H150 M92 76 H136 M92 130 H150"),
        ("Sound", "Speaker", "M72 80 H56 V112 H72 L98 134 V58 Z"),
        ("Sound", "Ac1", "M112 78 A26 26 0 0 1 112 114"),
        ("Sound", "Ac2", "M124 66 A42 42 0 0 1 124 126"),
        ("Sound", "Ac3", "M136 54 A58 58 0 0 1 136 138"),
        ("Bell", "Bell", "M96 48 A22 22 0 0 1 118 70 V90 L127 103 H65 L74 90 V70 A22 22 0 0 1 96 48 Z"),
        ("Bell", "Clapper", "M86 110 A10 10 0 0 0 106 110"),
        ("Bell", "ToastLines", "M108 132 H142 M108 142 H130"),
        ("Done", "Spark", "M96 34 V22 M96 158 V170 M34 96 H22 M158 96 H170 M52 52 L44 44 M140 140 L148 148 M52 140 L44 148 M140 52 L148 44"),
        ("Done", "Tick", "M77 98 L91.5 112.5 L118 82"),
    ];

    // gate.path.hero.geometry — every hero path string parses to a non-empty PathData whose ControlBounds sit inside
    // the 192x192 canvas (with slack for a rounded-corner arc's control polygon, which legitimately overshoots the
    // arc's own visual extent), and re-registering the SAME string returns the SAME GeometryId (interning stays
    // stable — the cache-warmth invariant PathGeometryTable.Register documents).
    private static void HeroGeometryGate()
    {
        bool allOk = true;
        string detail = "";
        foreach (var (hero, name, d) in s_heroPathCorpus)
        {
            int id1 = PathGeometryTable.Shared.Register(d, 192f, 192f, FillRule.NonZero);
            bool nonEmpty = PathGeometryTable.Shared.TryGet(id1, out var data) && data.VerbCount > 0 && data.PointCount > 0;
            bool bounded = nonEmpty
                && data.ControlBounds.X >= -8f && data.ControlBounds.Y >= -8f
                && data.ControlBounds.Right <= 200f && data.ControlBounds.Bottom <= 200f;
            int id2 = PathGeometryTable.Shared.Register(d, 192f, 192f, FillRule.NonZero);
            bool stable = id2 == id1;
            if (!(nonEmpty && bounded && stable))
            {
                allOk = false;
                detail = $"{hero}.{name}: nonEmpty={nonEmpty} bounded={bounded} stable={stable} " +
                          $"bounds=({data?.ControlBounds.X:0.#},{data?.ControlBounds.Y:0.#},{data?.ControlBounds.Right:0.#},{data?.ControlBounds.Bottom:0.#})";
            }
        }
        Check("gate.path.hero.geometry", allOk, allOk ? $"{s_heroPathCorpus.Length} hero paths parse non-empty, in-bounds, stably-interned" : detail);
    }

    // gate.path.hero.realize — every hero geometry realizes (stroke, the way every hero draws them; fill too where a
    // hero fills — HeroPatch's shield/HeroBell's bell are stroke-only in the app, matching every corpus entry here)
    // at deviceScale 1 and 2 without a Try* failure.
    private static void HeroRealizeGate()
    {
        var cache = new PathRealizationCache();
        cache.BeginFrame(1);
        var style = new StrokeStyle(2.4f, LineCap.Round, LineJoin.Round);

        bool allOk = true;
        string detail = "";
        foreach (var (hero, name, d) in s_heroPathCorpus)
        {
            int id = PathGeometryTable.Shared.Register(d, 192f, 192f, FillRule.NonZero);
            PathGeometryTable.Shared.TryGet(id, out var data);
            bool s1 = cache.TryRealizeStroke(data, in style, 1f, out _);
            bool s2 = cache.TryRealizeStroke(data, in style, 2f, out _);
            if (!(s1 && s2)) { allOk = false; detail = $"{hero}.{name}: scale1={s1} scale2={s2}"; }
        }
        Check("gate.path.hero.realize", allOk, allOk ? $"{s_heroPathCorpus.Length} hero paths realize at deviceScale 1 and 2" : detail);
    }

    // A component built on the exact idiom every Hero*.cs file uses: OnRealized-captured PathEl (StrokeTrimEnd
    // draw-on loop), BoxEl.Arc (the same channel, HeroDone's completion ring), and a translating/scaling BoxEl
    // (HeroConnect's pip / HeroWelcome's rings) — all wired once in UseLayoutEffect, all looping forever.
    private sealed class HeroLikeProbe : Component
    {
        static readonly PathData s_shieldLike = Geo("M20 4 L36 10 V22 C36 32 28 38 20 40 C12 38 4 32 4 22 V10 Z");

        static PathData Geo(string d)
        {
            int id = PathGeometryTable.Shared.Register(d, 40f, 40f, FillRule.NonZero);
            PathGeometryTable.Shared.TryGet(id, out var data);
            return data;
        }

        public NodeHandle StrokeNode, ArcNode, ScaleNode, TranslateNode;

        public override Element Render()
        {
            UseLayoutEffect(() =>
            {
                if (Context.Anim is not { } anim || Context.Scene is not { } scene) return;
                if (!StrokeNode.IsNull && scene.IsLive(StrokeNode))
                    anim.Keyframes(StrokeNode, AnimChannel.StrokeTrimEnd, [new Keyframe(0f, 0f), new Keyframe(1f, 1f)], 900f, loop: true);
                if (!ArcNode.IsNull && scene.IsLive(ArcNode))
                    anim.Keyframes(ArcNode, AnimChannel.StrokeTrimEnd, [new Keyframe(0f, 0f), new Keyframe(1f, 1f)], 900f, loop: true);
                if (!ScaleNode.IsNull && scene.IsLive(ScaleNode))
                {
                    anim.Keyframes(ScaleNode, AnimChannel.ScaleX, [new Keyframe(0f, 0.8f), new Keyframe(1f, 1.2f)], 900f, loop: true);
                    anim.Keyframes(ScaleNode, AnimChannel.ScaleY, [new Keyframe(0f, 0.8f), new Keyframe(1f, 1.2f)], 900f, loop: true);
                    anim.Keyframes(ScaleNode, AnimChannel.Opacity, [new Keyframe(0f, 0f), new Keyframe(1f, 1f)], 900f, loop: true);
                }
                if (!TranslateNode.IsNull && scene.IsLive(TranslateNode))
                {
                    anim.Keyframes(TranslateNode, AnimChannel.TranslateX, [new Keyframe(0f, 0f), new Keyframe(1f, 12f)], 900f, loop: true);
                    anim.Keyframes(TranslateNode, AnimChannel.TranslateY, [new Keyframe(0f, 0f), new Keyframe(1f, 8f)], 900f, loop: true);
                }
            });

            return new BoxEl
            {
                ZStack = true, Width = 40f, Height = 40f,
                Children =
                [
                    new PathEl
                    {
                        Width = 40f, Height = 40f, ViewBoxW = 40f, ViewBoxH = 40f,
                        Geometry = s_shieldLike, StrokeColor = ColorF.FromRgba(200, 40, 40, 255),
                        Stroke = new StrokeStyle(2f, LineCap.Round, LineJoin.Round),
                        OnRealized = n => StrokeNode = n,
                    },
                    new BoxEl
                    {
                        Width = 40f, Height = 40f,
                        Arc = new ArcSpec(ColorF.FromRgba(20, 20, 200, 255), 2f, 0f, 360f, RoundCaps: false),
                        OnRealized = n => ArcNode = n,
                    },
                    new BoxEl { Width = 8f, Height = 8f, Fill = ColorF.FromRgba(10, 200, 10, 255), OnRealized = n => ScaleNode = n },
                    new BoxEl { Width = 6f, Height = 6f, Fill = ColorF.FromRgba(10, 10, 200, 255), OnRealized = n => TranslateNode = n },
                ],
            };
        }
    }

    // gate.path.hero.alloc-zero — a mounted HeroLikeProbe, warmed, then 60 real frames: 0 managed bytes AND
    // PathRealizationCache.Shared.TessellationCount unchanged — proof the looping StrokeTrim/Scale/Translate is a
    // cache hit every frame, not a re-tessellation (the entire point of trim being a shader uniform, not a geometry
    // edit).
    private static void HeroAllocZeroGate(StringTable strings)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("hero-alloc", new Size2(200, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new HeroLikeProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);

        for (int i = 0; i < 5; i++) host.RunFrame();   // warm: mount + first tessellation + JIT

        int tessBefore = PathRealizationCache.Shared.TessellationCount;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 60; i++) host.RunFrame();
        long after = GC.GetAllocatedBytesForCurrentThread();
        int tessAfter = PathRealizationCache.Shared.TessellationCount;

        bool allocZero = (after - before) == 0;
        bool tessUnchanged = tessAfter == tessBefore;
        Check("gate.path.hero.alloc-zero", allocZero && tessUnchanged,
            $"allocDelta={after - before}B tessBefore={tessBefore} tessAfter={tessAfter}");
        app.Dispose();
    }

    // gate.path.hero.channels — the SAME probe's animated nodes only ever carry compositor channels (StrokeTrim*/
    // Scale*/Translate*/Opacity): no LayoutW/LayoutH/SizeW/SizeH track exists on any of them after mount — a hero
    // loop can never relayout the dialog mid page-slide.
    private static void HeroChannelsOnlyGate(StringTable strings)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("hero-channels", new Size2(200, 200), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new HeroLikeProbe();
        using var host = new AppHost(app, window, device, fonts, strings, probe);
        host.RunFrame();

        var anim = host.Animation;
        var nodes = new[] { probe.StrokeNode, probe.ArcNode, probe.ScaleNode, probe.TranslateNode };
        bool anyLayoutish = false;
        int checkedCount = 0;
        foreach (var n in nodes)
        {
            if (n.IsNull) continue;
            checkedCount++;
            if (anim.TryGetTrackValue(n, AnimChannel.LayoutW, out _)) anyLayoutish = true;
            if (anim.TryGetTrackValue(n, AnimChannel.LayoutH, out _)) anyLayoutish = true;
            if (anim.TryGetTrackValue(n, AnimChannel.SizeW, out _)) anyLayoutish = true;
            if (anim.TryGetTrackValue(n, AnimChannel.SizeH, out _)) anyLayoutish = true;
        }
        bool allHadTracks = checkedCount == nodes.Length
            && anim.HasTracks(probe.StrokeNode) && anim.HasTracks(probe.ArcNode)
            && anim.HasTracks(probe.ScaleNode) && anim.HasTracks(probe.TranslateNode);

        Check("gate.path.hero.channels", !anyLayoutish && allHadTracks,
            $"checked={checkedCount}/{nodes.Length} anyLayoutish={anyLayoutish} allHadTracks={allHadTracks}");
        app.Dispose();
    }
}
