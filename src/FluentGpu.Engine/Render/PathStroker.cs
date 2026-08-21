using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// Stroke tessellation for <see cref="PathTessellator"/> (gpu-renderer.md §5 step 3): offset the flattened polyline
/// by ±<c>Width/2</c>, with all three <see cref="LineJoin"/>s and all three <see cref="LineCap"/>s, plus its own
/// outward AA fringe and a baked normalized arc-length attribute (<see cref="PathVertex.S"/>).
///
/// <para><b>Trim/dash are deliberately NOT tessellation inputs.</b> <see cref="StrokeStyle.DashOn"/>/
/// <see cref="StrokeStyle.DashOff"/> exist on the style record but this tessellator ignores them on purpose: they —
/// and any animated trim (draw-on reveal) — are meant to be per-frame PIXEL-SHADER uniforms that read the baked
/// <see cref="PathVertex.S"/> attribute, not a re-tessellation input. A 60 Hz draw-on animation that re-tessellated
/// per frame would blow the "static geometry costs nothing per frame" guarantee the realization cache exists to
/// provide — the whole point of baking <c>S</c> once is that the SAME cached <see cref="PathRef"/> serves every
/// frame of that animation as a cache HIT. Do not "helpfully" wire dash into this file — that is exactly the kind of
/// change a later reader is tempted to make, and exactly the one this comment exists to head off.</para>
/// </summary>
public static class PathStroker
{
    private const float TwoPi = MathF.PI * 2f;
    private const float RoundStepRadians = 0.35f;   // ~20° per round join/cap segment — bounded, tessellated to visual tol

    private sealed class Scratch
    {
        public readonly List<PathVertex> Vtx = new(256);
        public readonly List<uint> Idx = new(384);
        public void Clear() { Vtx.Clear(); Idx.Clear(); }
    }
    private static readonly Scratch s_scratch = new();

    /// <summary>
    /// Stroke flattened <paramref name="points"/>/<paramref name="starts"/>/<paramref name="counts"/>/
    /// <paramref name="closedFlags"/> (the <see cref="PathFlatten.Flatten"/> shape, closed-overload) per
    /// <paramref name="style"/>. <paramref name="trimSpace"/> selects where <see cref="PathVertex.S"/> resets
    /// (<see cref="PathTrimSpace"/>). <paramref name="arcLenPx"/> is the TOTAL contour length in device px across
    /// every subpath (report-only — see <see cref="PathRef.ArcLenPx"/>). Returned spans alias reusable scratch
    /// (UI-thread-only cold path, valid until the next call).
    ///
    /// <para><b>Also implemented here (a real quality cliff, not a nicety):</b> the geometric stroke width is
    /// clamped to a 1-device-pixel floor, and <see cref="PathVertex.Cov"/> on every OPAQUE (non-fringe) vertex is
    /// scaled by <c>actualWidthDevicePx / 1px</c> below that floor. Without this, a stroke thinner than one device
    /// pixel has its two AA fringes overlap, coverage saturates back toward 1, and the line reads too dark and
    /// wobbles frame-to-frame under animation/scale changes — reusing the SAME 0..1 coverage channel the fringe
    /// already carries is what makes this a one-line fix instead of a second shader path.</para>
    /// <para><b>Known v1 gap</b> (documented, not fixed): an opaque stroke that overlaps ITSELF (a join whose miter/
    /// round radius exceeds the local curvature radius) double-blends under premultiplied SrcOver at the overlap.
    /// Acceptable for opaque line art (the overlap is invisible); a translucent stroke shows a visible seam. NOT
    /// counted by a <see cref="Diag"/> counter here — this tessellator only sees geometry, never the brush alpha that
    /// would make "translucent" decidable, so that counter (if added) belongs at the DrawList-record layer where
    /// color is known, not in this file. Fixing the overlap itself would need a stencil/coverage-max pass this
    /// tessellator doesn't have.</para>
    /// </summary>
    internal static void Tessellate(ReadOnlySpan<Point2> points, ReadOnlySpan<int> starts, ReadOnlySpan<int> counts,
        ReadOnlySpan<bool> closedFlags, in StrokeStyle style, float deviceScale, PathTrimSpace trimSpace,
        out ReadOnlySpan<PathVertex> vtx, out ReadOnlySpan<uint> idx, out RectF bounds, out float arcLenPx)
    {
        var s = s_scratch;
        s.Clear();

        int nc = starts.Length;
        var contourLenPx = nc > 0 ? new float[nc] : Array.Empty<float>();
        float wholeTotalPx = 0f;
        for (int c = 0; c < nc; c++)
        {
            contourLenPx[c] = ContourLengthPx(points, starts[c], counts[c], closedFlags[c], deviceScale);
            wholeTotalPx += contourLenPx[c];
        }

        float prefix = 0f;
        for (int c = 0; c < nc; c++)
        {
            StrokeContour(s, points, starts[c], counts[c], closedFlags[c], style, deviceScale, trimSpace,
                contourLenPx[c], prefix, wholeTotalPx);
            prefix += contourLenPx[c];
        }

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < s.Vtx.Count; i++)
        {
            var v = s.Vtx[i];
            if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
        }
        bounds = s.Vtx.Count > 0 ? RectF.FromLTRB(minX, minY, maxX, maxY) : default;
        arcLenPx = wholeTotalPx;

        vtx = CollectionsMarshal.AsSpan(s.Vtx);
        idx = CollectionsMarshal.AsSpan(s.Idx);
    }

    private static float ContourLengthPx(ReadOnlySpan<Point2> points, int off, int n, bool closed, float deviceScale)
    {
        if (n < 2) return 0f;
        float total = 0f;
        int edges = closed ? n : n - 1;
        for (int i = 0; i < edges; i++)
        {
            Point2 a = points[off + i], b = points[off + (i + 1) % n];
            total += Dist(a, b);
        }
        return total * deviceScale;
    }

    private static void StrokeContour(Scratch s, ReadOnlySpan<Point2> points, int off, int n, bool closed,
        in StrokeStyle style, float deviceScale, PathTrimSpace trimSpace,
        float contourLenPx, float prefixLenPx, float wholeTotalPx)
    {
        if (n < 2) return;   // a 1-point contour has no direction to stroke — nothing to draw (documented)

        float widthDevicePx = style.Width * deviceScale;
        float clampedDevicePx = MathF.Max(widthDevicePx, 1f);
        float covScale = Math.Clamp(widthDevicePx, 0f, 1f);
        float halfW = (clampedDevicePx / deviceScale) * 0.5f;
        float fringeW = 0.5f / MathF.Max(deviceScale, 1e-6f);

        float totalForNorm = trimSpace == PathTrimSpace.WholePath ? wholeTotalPx : contourLenPx;
        float baseOffsetPx = trimSpace == PathTrimSpace.WholePath ? prefixLenPx : 0f;
        float SAt(float arcPx) => totalForNorm > 1e-6f ? Math.Clamp((baseOffsetPx + arcPx) / totalForNorm, 0f, 1f) : 0f;

        int segCount = closed ? n : n - 1;
        var normals = new Point2[segCount];
        var cum = new float[n];
        cum[0] = 0f;
        for (int i = 0; i < segCount; i++)
        {
            Point2 a = points[off + i], b = points[off + (i + 1) % n];
            float dx = b.X - a.X, dy = b.Y - a.Y, len = MathF.Sqrt(dx * dx + dy * dy);
            normals[i] = len > 1e-9f ? new Point2(-dy / len, dx / len) : new Point2(0f, 0f);
            if (i + 1 < n) cum[i + 1] = cum[i] + len * deviceScale;
        }

        // ── ribbon quads, one per segment, plus the outward fringe on both rails ──
        for (int i = 0; i < segCount; i++)
        {
            Point2 a = points[off + i], b = points[off + (i + 1) % n];
            Point2 nrm = normals[i];
            Point2 offs = new(nrm.X * halfW, nrm.Y * halfW);
            Point2 leftA = new(a.X + offs.X, a.Y + offs.Y), rightA = new(a.X - offs.X, a.Y - offs.Y);
            Point2 leftB = new(b.X + offs.X, b.Y + offs.Y), rightB = new(b.X - offs.X, b.Y - offs.Y);
            float sa = SAt(cum[i]);
            // The wrap segment's end point is index 0 again (cum[0] == 0f) but its true arc position is the FULL
            // contour length, not the start — use contourLenPx explicitly rather than cum[(i+1)%n] there.
            float sb = i + 1 < n ? SAt(cum[i + 1]) : SAt(contourLenPx);

            uint vA = AddVtx(s, leftA, covScale, sa);
            uint vB = AddVtx(s, rightA, covScale, sa);
            uint vC = AddVtx(s, rightB, covScale, sb);
            uint vD = AddVtx(s, leftB, covScale, sb);
            EmitTriCcw(s, vA, vB, vC);
            EmitTriCcw(s, vA, vC, vD);

            Point2 fOffs = new(nrm.X * (halfW + fringeW), nrm.Y * (halfW + fringeW));
            Point2 leftAf = new(a.X + fOffs.X, a.Y + fOffs.Y), leftBf = new(b.X + fOffs.X, b.Y + fOffs.Y);
            Point2 rightAf = new(a.X - fOffs.X, a.Y - fOffs.Y), rightBf = new(b.X - fOffs.X, b.Y - fOffs.Y);
            uint lfA = AddVtx(s, leftAf, 0f, sa), lfB = AddVtx(s, leftBf, 0f, sb);
            EmitTriCcw(s, vA, lfA, lfB); EmitTriCcw(s, vA, lfB, vD);
            uint rfA = AddVtx(s, rightAf, 0f, sa), rfB = AddVtx(s, rightBf, 0f, sb);
            EmitTriCcw(s, vB, rfB, rfA); EmitTriCcw(s, vB, vC, rfB);
        }

        // ── joins at interior spine vertices (and the wrap vertex when closed) ──
        int firstJoin = closed ? 0 : 1;
        int lastJoin = closed ? n - 1 : n - 2;
        for (int v = firstJoin; v <= lastJoin; v++)
        {
            int prevSeg = ((v - 1) % segCount + segCount) % segCount;
            int nextSeg = v % segCount;
            EmitJoin(s, points[off + v], normals[prevSeg], normals[nextSeg], style, halfW, covScale, SAt(cum[v]));
        }

        // ── caps at the two open endpoints ──
        if (!closed)
        {
            EmitCap(s, points[off + 0], normals[0], style.Cap, halfW, covScale, SAt(0f), startCap: true);
            EmitCap(s, points[off + n - 1], normals[segCount - 1], style.Cap, halfW, covScale, SAt(contourLenPx), startCap: false);
        }
    }

    private static uint AddVtx(Scratch s, Point2 p, float cov, float sVal)
    {
        s.Vtx.Add(new PathVertex { X = p.X, Y = p.Y, Cov = cov, S = sVal });
        return (uint)(s.Vtx.Count - 1);
    }

    // outerNormal points from the SPINE vertex toward the outer/gap side of the turn for the given (prev,next)
    // segment pair; the opposite (inner) side always gets a plain bevel fill (accepted overlap, documented above).
    private static void EmitJoin(Scratch s, Point2 v, Point2 prevN, Point2 nextN, in StrokeStyle style,
        float halfW, float cov, float sVal)
    {
        // turn sign: cross(prevDir, nextDir); prevDir ⟂ prevN (rotate -90), so this is equivalent to cross(prevN,nextN).
        float cross = prevN.X * nextN.Y - prevN.Y * nextN.X;
        if (MathF.Abs(cross) < 1e-7f) return;   // straight-through (or a 180° reversal) — nothing to fill
        bool leftIsOuter = cross < 0f;

        Point2 outerPrevN = leftIsOuter ? prevN : Neg(prevN);
        Point2 outerNextN = leftIsOuter ? nextN : Neg(nextN);
        Point2 innerPrevN = Neg(outerPrevN), innerNextN = Neg(outerNextN);

        Point2 outerPrev = Off(v, outerPrevN, halfW), outerNext = Off(v, outerNextN, halfW);
        Point2 innerPrev = Off(v, innerPrevN, halfW), innerNext = Off(v, innerNextN, halfW);

        uint spine = AddVtx(s, v, cov, sVal);
        uint iPrev = AddVtx(s, innerPrev, cov, sVal), iNext = AddVtx(s, innerNext, cov, sVal);
        EmitTriCcw(s, spine, iPrev, iNext);   // inner side: plain bevel fill, overlap accepted

        switch (style.Join)
        {
            case LineJoin.Bevel:
            {
                uint oPrev = AddVtx(s, outerPrev, cov, sVal), oNext = AddVtx(s, outerNext, cov, sVal);
                EmitTriCcw(s, spine, oPrev, oNext);
                break;
            }
            case LineJoin.Round:
            {
                // The short way around from outerPrevN to outerNextN follows sign(cross(outerPrevN, outerNextN)) —
                // which equals sign(cross) regardless of leftIsOuter (negating both operands preserves a cross
                // product's sign), so the sweep direction is simply cross > 0, independent of which side is outer.
                EmitRoundFan(s, v, outerPrevN, outerNextN, halfW, cov, sVal, sweepPositive: cross > 0f);
                break;
            }
            default: // Miter, with bevel fallback past the miter limit
            {
                Point2 bis = new(outerPrevN.X + outerNextN.X, outerPrevN.Y + outerNextN.Y);
                float bisLen = MathF.Sqrt(bis.X * bis.X + bis.Y * bis.Y);
                float cosHalf = bisLen > 1e-6f ? (outerPrevN.X * (bis.X / bisLen) + outerPrevN.Y * (bis.Y / bisLen)) : 0f;
                if (bisLen > 1e-6f && cosHalf > 1e-3f && 1f / cosHalf <= MathF.Max(1f, style.MiterLimit))
                {
                    float miterLen = halfW / cosHalf;
                    Point2 m = new(v.X + bis.X / bisLen * miterLen, v.Y + bis.Y / bisLen * miterLen);
                    uint oPrev = AddVtx(s, outerPrev, cov, sVal), oNext = AddVtx(s, outerNext, cov, sVal), mv = AddVtx(s, m, cov, sVal);
                    EmitTriCcw(s, spine, oPrev, mv);
                    EmitTriCcw(s, spine, mv, oNext);
                }
                else
                {
                    uint oPrev = AddVtx(s, outerPrev, cov, sVal), oNext = AddVtx(s, outerNext, cov, sVal);
                    EmitTriCcw(s, spine, oPrev, oNext);
                }
                break;
            }
        }
    }

    private static void EmitCap(Scratch s, Point2 v, Point2 segN, LineCap cap, float halfW, float cov, float sVal, bool startCap)
    {
        // Tangent pointing AWAY from the stroke body: rotating the segment normal by +90° lands exactly on the
        // start-cap outward tangent (see PathStroker design notes) and by -90° on the end-cap outward tangent.
        Point2 tangent = startCap ? new Point2(-segN.Y, segN.X) : new Point2(segN.Y, -segN.X);
        Point2 left = Off(v, segN, halfW), right = Off(v, Neg(segN), halfW);

        switch (cap)
        {
            case LineCap.Butt:
                break;   // the ribbon's own flat segment end already IS the cap — nothing to add
            case LineCap.Square:
            {
                Point2 leftExt = new(left.X + tangent.X * halfW, left.Y + tangent.Y * halfW);
                Point2 rightExt = new(right.X + tangent.X * halfW, right.Y + tangent.Y * halfW);
                uint vl = AddVtx(s, left, cov, sVal), vr = AddVtx(s, right, cov, sVal);
                uint vle = AddVtx(s, leftExt, cov, sVal), vre = AddVtx(s, rightExt, cov, sVal);
                EmitTriCcw(s, vl, vle, vre);
                EmitTriCcw(s, vl, vre, vr);
                break;
            }
            case LineCap.Round:
            {
                // Sweep 180° from segN to -segN through `tangent` — a start cap sweeps the +90° way, an end cap the
                // -90° way (matching the tangent rotation direction chosen above).
                EmitRoundFan(s, v, segN, Neg(segN), halfW, cov, sVal, sweepPositive: startCap);
                break;
            }
        }
    }

    // Fan-triangulate a semicircular (or turn-angle) arc from normal fromN to normal toN, both scaled by halfW from
    // center v, sweeping through +90°/-90° increments of RoundStepRadians as selected by sweepPositive. Used by both
    // round joins (turn-angle sweep) and round caps (fixed 180° sweep).
    private static void EmitRoundFan(Scratch s, Point2 v, Point2 fromN, Point2 toN, float halfW, float cov, float sVal, bool sweepPositive)
    {
        float a0 = MathF.Atan2(fromN.Y, fromN.X);
        float a1 = MathF.Atan2(toN.Y, toN.X);
        float sweep = a1 - a0;
        // Normalize to the SHORTEST signed angle in (-π, π] — a join's outer-side wedge is never more than a half
        // turn, so the short way is always the geometrically correct one.
        while (sweep > MathF.PI) sweep -= TwoPi;
        while (sweep <= -MathF.PI) sweep += TwoPi;
        // The one genuinely ambiguous case is an EXACT 180° cap (fromN/toN antiparallel, EmitCap's fixed sweep) —
        // the "shortest way" is a tie there and floating point could resolve it to either sign, so sweepPositive
        // breaks the tie explicitly. A join's turn angle never reaches exactly 180° in practice (EmitJoin already
        // bails out near straight-through/reversal above), so this branch only ever fires for a round CAP.
        if (MathF.Abs(MathF.Abs(sweep) - MathF.PI) < 1e-4f)
            sweep = sweepPositive ? MathF.PI : -MathF.PI;

        int segs = Math.Clamp((int)MathF.Ceiling(MathF.Abs(sweep) / RoundStepRadians), 1, 32);
        uint center = AddVtx(s, v, cov, sVal);
        uint prev = AddVtx(s, Off(v, fromN, halfW), cov, sVal);
        for (int i = 1; i <= segs; i++)
        {
            float a = a0 + sweep * i / segs;
            Point2 p = new(v.X + MathF.Cos(a) * halfW, v.Y + MathF.Sin(a) * halfW);
            uint cur = AddVtx(s, p, cov, sVal);
            EmitTriCcw(s, center, prev, cur);
            prev = cur;
        }
    }

    private static Point2 Off(Point2 v, Point2 n, float r) => new(v.X + n.X * r, v.Y + n.Y * r);
    private static Point2 Neg(Point2 n) => new(-n.X, -n.Y);
    private static float Dist(Point2 a, Point2 b) { float dx = a.X - b.X, dy = a.Y - b.Y; return MathF.Sqrt(dx * dx + dy * dy); }

    /// <summary>Emit one triangle, self-correcting winding to CCW — the same by-construction guarantee as
    /// <see cref="PathSweep"/> (the differential gate assumes it repo-wide, not just for fills).</summary>
    private static void EmitTriCcw(Scratch s, uint a, uint b, uint c)
    {
        var pa = s.Vtx[(int)a]; var pb = s.Vtx[(int)b]; var pc = s.Vtx[(int)c];
        double cross = (double)(pb.X - pa.X) * (pc.Y - pa.Y) - (double)(pb.Y - pa.Y) * (pc.X - pa.X);
        if (cross < 0) (b, c) = (c, b);
        s.Idx.Add(a); s.Idx.Add(b); s.Idx.Add(c);
    }
}
