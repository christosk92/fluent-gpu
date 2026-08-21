using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// Curve flattening for <see cref="PathTessellator"/> (gpu-renderer.md §5 step 1): Wang's-formula segment counts
/// computed UP FRONT (no recursion, no growing call stack — <see cref="IconPathParser"/>'s recursive flattener is the
/// deliberate contrast; that one is fine for tiny static icon masks, this one must bound its own depth for arbitrary
/// author-supplied vector art) and a device-pixel grid snap applied to every emitted point.
///
/// <para><b>Why snap at all:</b> integerizing the flattened input BEFORE <see cref="PathSweep"/>'s trapezoidal sweep
/// runs is the single highest-leverage robustness decision in this file — it is what makes near-exact orientation/
/// crossing predicates possible downstream (two edges that were meant to share an endpoint now do, bit-for-bit,
/// instead of differing in the last mantissa bit after independent curve evaluation). The grid is expressed in DEVICE
/// pixels (1/256th of one) but applied in the path's own LOCAL units — <c>256 * deviceScale</c> subdivisions per path
/// unit — so the result stays translation-invariant (the realization cache keys on content + scale, never on screen
/// position, so a cache entry must not depend on where the node ultimately lands) while still snapping to a grid fine
/// enough that no visible artifact survives at any real device scale. One multiply + one round + one divide per point.</para>
/// </summary>
public static class PathFlatten
{
    /// <summary>Hard floor/ceiling on a single curve's subdivision count (canon: clamp to [1, 512]).</summary>
    public const int MinSegs = 1, MaxSegs = 512;

    /// <summary>Device-pixel grid fineness for <see cref="SnapToDevicePixelGrid"/> (1/256th of one device pixel).</summary>
    public const float GridSubdivisions = 256f;

    /// <summary>
    /// Wang's-formula segment count for a quadratic Bézier flattened to per-segment chord deviation ≤ <paramref name="tol"/>
    /// (path units). EXACT closed form (not an approximation): the quadratic's deviation from its chord at parameter t is
    /// <c>t(1-t)·(P0 - 2·P1 + P2)</c>, maximized at t=0.5 to <c>|P0-2P1+P2|/4</c>; subdividing into n equal-parameter
    /// spans scales that per-span deviation by 1/n² (the standard de Casteljau subdivision scaling), so solving
    /// <c>|P0-2P1+P2| / (4n²) ≤ tol</c> for the smallest integer n gives this formula exactly (no empirical fudge factor).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WangSegmentsQuad(Point2 p0, Point2 p1, Point2 p2, float tol)
    {
        float dx = p0.X - 2f * p1.X + p2.X, dy = p0.Y - 2f * p1.Y + p2.Y;
        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (d <= 1e-9f) return MinSegs;
        float t = MathF.Max(tol, 1e-6f);
        int n = (int)MathF.Ceiling(MathF.Sqrt(d / (4f * t)));
        return Math.Clamp(n, MinSegs, MaxSegs);
    }

    /// <summary>
    /// Wang's-formula segment count for a cubic Bézier. This is the classic APPROXIMATE cubic form (Wang 1984; the same
    /// one used by most production flatteners) — unlike the quadratic case a cubic has no single exact closed-form
    /// deviation-vs-t expression, so this bounds it via the two second-difference vectors
    /// <c>a = P0-2P1+P2</c>, <c>b = P1-2P2+P3</c>: <c>n = ceil(sqrt(3·max(|a|,|b|) / (4·tol)))</c>. Deliberately erring
    /// high rather than low keeps <c>gate.path.flatten.tolerance</c> honest — see that gate for the empirical proof this
    /// bound actually holds, not just that the formula is self-consistent.
    /// </summary>
    public static int WangSegmentsCubic(Point2 p0, Point2 p1, Point2 p2, Point2 p3, float tol)
    {
        float ax = p0.X - 2f * p1.X + p2.X, ay = p0.Y - 2f * p1.Y + p2.Y;
        float bx = p1.X - 2f * p2.X + p3.X, by = p1.Y - 2f * p2.Y + p3.Y;
        float da = MathF.Sqrt(ax * ax + ay * ay), db = MathF.Sqrt(bx * bx + by * by);
        float d = MathF.Max(da, db);
        if (d <= 1e-9f) return MinSegs;
        float t = MathF.Max(tol, 1e-6f);
        int n = (int)MathF.Ceiling(MathF.Sqrt(3f * d / (4f * t)));
        return Math.Clamp(n, MinSegs, MaxSegs);
    }

    /// <summary>Snap a point to the 1/256-device-pixel grid, expressed in the path's own LOCAL units (see type doc for
    /// why this must be translation-invariant rather than an absolute screen-space pixel snap).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Point2 SnapToDevicePixelGrid(Point2 p, float deviceScale)
    {
        float g = GridSubdivisions * MathF.Max(deviceScale, 1e-6f);
        return new Point2(MathF.Round(p.X * g) / g, MathF.Round(p.Y * g) / g);
    }

    // Reusable UI-thread-only scratch (the same discipline as PathDataParser.s_builder / IconPathParser's
    // ContourBuilder — tessellation runs at mount time or on a cache miss, never inside a frame hot phase, so a plain
    // managed List is the accepted cost, same precedent as IconRaster's per-call crossing list). NOT thread-safe.
    private sealed class Scratch
    {
        public readonly List<Point2> Points = new(256);
        public readonly List<int> Starts = new(16);
        public readonly List<int> Counts = new(16);
        public readonly List<bool> Closed = new(16);
        public void Clear() { Points.Clear(); Starts.Clear(); Counts.Clear(); Closed.Clear(); }
    }
    private static readonly Scratch s_scratch = new();

    /// <summary>
    /// Flatten <paramref name="path"/>'s verb/point streams into contours shaped exactly like <see cref="IconRaster"/>
    /// wants (point list + per-contour start/count) — deliberately the same shape as <see cref="ContourBuilder"/>'s
    /// output, so <see cref="PathSweep"/>'s differential gate can feed the identical flatten into both the reference
    /// rasterizer and the real sweep. Every emitted point is grid-snapped (<see cref="SnapToDevicePixelGrid"/>).
    /// Contours are NOT closed by an explicit duplicate final point (implicit wrap, same convention as
    /// <see cref="IconRaster"/>/<see cref="ContourBuilder"/>) and are NOT dropped for being degenerate (&lt;3 points) —
    /// that decision belongs to the consumer (<see cref="PathSweep"/> skips degenerate contours for triangulation;
    /// <see cref="PathStroker"/> strokes even a 1- or 2-point contour, per the adversarial corpus).
    /// <para>Returned spans alias reusable scratch — valid until the next call on this thread. UI-thread-only
    /// (mount-time / cache-miss cold path, never a frame hot phase).</para>
    /// </summary>
    internal static void Flatten(PathData path, float tol, float deviceScale,
        out ReadOnlySpan<Point2> points, out ReadOnlySpan<int> starts, out ReadOnlySpan<int> counts, out RectF bounds)
        => Flatten(path, tol, deviceScale, out points, out starts, out counts, out _, out bounds);

    /// <summary>Overload that also reports, per contour, whether an explicit <see cref="PathVerb.Close"/> was present
    /// (join back to the contour's start, no caps) versus an open subpath (needs <see cref="PathStroker"/> end caps).
    /// Fill triangulation (<see cref="PathSweep"/>) does not need this — SVG/Direct2D fill semantics treat every
    /// subpath as implicitly closed regardless of an explicit Z, so the plain overload omits it.</summary>
    internal static void Flatten(PathData path, float tol, float deviceScale,
        out ReadOnlySpan<Point2> points, out ReadOnlySpan<int> starts, out ReadOnlySpan<int> counts,
        out ReadOnlySpan<bool> closed, out RectF bounds)
    {
        var s = s_scratch;
        s.Clear();
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        bool any = false;

        void Track(Point2 p)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            any = true;
        }

        void Emit(Point2 p)
        {
            var snapped = SnapToDevicePixelGrid(p, deviceScale);
            s.Points.Add(snapped);
            Track(snapped);
        }

        void EndContour(int contourStart, bool wasClosed)
        {
            int count = s.Points.Count - contourStart;
            if (count > 0) { s.Starts.Add(contourStart); s.Counts.Add(count); s.Closed.Add(wasClosed); }
        }

        ReadOnlySpan<PathVerb> verbs = path.Verbs;
        ReadOnlySpan<Point2> pts = path.Points;
        int pi = 0;
        int contourStart = -1;
        bool contourClosed = false;
        Point2 cur = default;

        for (int vi = 0; vi < verbs.Length; vi++)
        {
            switch (verbs[vi])
            {
                case PathVerb.MoveTo:
                {
                    if (contourStart >= 0) EndContour(contourStart, contourClosed);
                    contourStart = s.Points.Count;
                    contourClosed = false;
                    cur = pts[pi++];
                    Emit(cur);
                    break;
                }
                case PathVerb.LineTo:
                {
                    cur = pts[pi++];
                    Emit(cur);
                    break;
                }
                case PathVerb.QuadTo:
                {
                    Point2 p0 = cur, p1 = pts[pi++], p2 = pts[pi++];
                    int n = WangSegmentsQuad(p0, p1, p2, tol);
                    for (int i = 1; i <= n; i++)
                    {
                        float t = (float)i / n;
                        float u = 1f - t;
                        float x = u * u * p0.X + 2f * u * t * p1.X + t * t * p2.X;
                        float y = u * u * p0.Y + 2f * u * t * p1.Y + t * t * p2.Y;
                        Emit(new Point2(x, y));
                    }
                    cur = p2;
                    break;
                }
                case PathVerb.CubicTo:
                {
                    Point2 p0 = cur, p1 = pts[pi++], p2 = pts[pi++], p3 = pts[pi++];
                    int n = WangSegmentsCubic(p0, p1, p2, p3, tol);
                    for (int i = 1; i <= n; i++)
                    {
                        float t = (float)i / n;
                        float u = 1f - t;
                        float uu = u * u, tt = t * t;
                        float b0 = uu * u, b1 = 3f * uu * t, b2 = 3f * u * tt, b3 = tt * t;
                        float x = b0 * p0.X + b1 * p1.X + b2 * p2.X + b3 * p3.X;
                        float y = b0 * p0.Y + b1 * p1.Y + b2 * p2.Y + b3 * p3.Y;
                        Emit(new Point2(x, y));
                    }
                    cur = p3;
                    break;
                }
                case PathVerb.Close:
                    // Implicit-close contour convention (matches IconRaster/ContourBuilder): no point emitted. A
                    // Close is typically followed by a MoveTo (which ends this contour) or is the last verb (ended below).
                    contourClosed = true;
                    break;
            }
        }
        if (contourStart >= 0) EndContour(contourStart, contourClosed);

        points = CollectionsMarshal.AsSpan(s.Points);
        starts = CollectionsMarshal.AsSpan(s.Starts);
        counts = CollectionsMarshal.AsSpan(s.Counts);
        closed = CollectionsMarshal.AsSpan(s.Closed);
        bounds = any ? RectF.FromLTRB(minX, minY, maxX, maxY) : default;
    }
}
