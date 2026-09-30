using FluentGpu.Foundation;

namespace FluentGpu.Render.Tiles;

/// <summary>
/// A composite-time analytic edge feather (docs/plans/scroll-gpu-retained-tiles-implementation.md §A.5): the composite
/// pixel shader multiplies a slice's premultiplied sample by <see cref="EdgeFeatherMask.Evaluate(in EdgeFeather, float, float)"/>
/// — no offscreen layer, no back-buffer strip copies. <see cref="Rect"/> and the bands are DEVICE px in the composite's
/// target space; a band ≤ 0 disables that edge. Where a rounded corner's two adjacent edges both fade, the feather follows
/// the corner ARC (<see cref="Radii"/>, device px). The default value is "no feather" (evaluates to exactly 1).
/// </summary>
public readonly record struct EdgeFeather(
    RectF Rect, float BandLeft, float BandTop, float BandRight, float BandBottom, CornerRadius4 Radii,
    FadeFalloff Falloff = FadeFalloff.Smoothstep, float Intensity = 1f)
{
    public bool IsNone => Intensity <= 0f || (BandLeft <= 0f && BandTop <= 0f && BandRight <= 0f && BandBottom <= 0f);

    /// <summary>The device-px feather an authored <see cref="EdgeFadeSpec"/> (DIP bands, only its enabled edges) produces
    /// over a node box <paramref name="dipRect"/> with corner radii <paramref name="dipRadii"/> at raster
    /// <paramref name="scale"/>. <see cref="EdgeFadeMode.Blur"/>-only specs carry no alpha feather.</summary>
    public static EdgeFeather FromSpec(in EdgeFadeSpec spec, in RectF dipRect, in CornerRadius4 dipRadii, float scale)
    {
        if (spec.IsNone || spec.Mode == EdgeFadeMode.Blur) return default;
        return new EdgeFeather(
            new RectF(dipRect.X * scale, dipRect.Y * scale, dipRect.W * scale, dipRect.H * scale),
            spec.Band(EdgeMask.Left) * scale, spec.Band(EdgeMask.Top) * scale,
            spec.Band(EdgeMask.Right) * scale, spec.Band(EdgeMask.Bottom) * scale,
            new CornerRadius4(dipRadii.TopLeft * scale, dipRadii.TopRight * scale, dipRadii.BottomRight * scale, dipRadii.BottomLeft * scale),
            spec.Falloff, spec.Intensity);
    }
}

/// <summary>
/// The ONE source of truth for the composite feather: <see cref="Hlsl"/> is the shader function the composite PS includes
/// verbatim, <see cref="Pack"/> writes exactly the constants it reads, and <see cref="Evaluate(ReadOnlySpan{float}, float, float)"/>
/// is its line-for-line C# port over those same packed constants (so the headless reference and the GPU can only
/// disagree by float rounding — the tile-feather-identity pixel gate holds them to ≤ 1/255). Portable HLSL
/// (saturate/lerp/length/min only) so a Metal port re-implements it verbatim.
/// <para>Packed layout (<see cref="PackedFloats"/> = 16 floats = 4 float4 registers):
/// <c>rect</c> = (minX, minY, maxX, maxY); <c>band</c> = (L, T, R, B) px, 0 = disabled; <c>corner</c> = (TL, TR, BR, BL)
/// px; <c>misc</c> = (falloff 0 linear / 1 smoothstep / 2 cubic, intensity ∈ [0,1], 0, 0).</para>
/// </summary>
public static class EdgeFeatherMask
{
    public const int PackedFloats = 16;

    /// <summary>The shader function: <c>float edgeFeather(float2 p, float4 rect, float4 band, float4 corner, float4 misc)</c>,
    /// <c>p</c> = the device pixel centre (SV_Position.xy). Returns the alpha multiplier in [0, 1].</summary>
    public const string Hlsl = """
float efCurve(float t, float mode) { t = saturate(t); if (mode < 0.5) return t; if (mode < 1.5) return t * t * (3.0 - 2.0 * t); return t * t * t; }
float efArc(float2 p, float2 c, float r, float cb, bool act) { return (act && r > 0.0 && cb > 0.0) ? (r - length(p - c)) / cb : 1e9; }
float edgeFeather(float2 p, float4 rect, float4 band, float4 corner, float4 misc)
{
    float bl = band.x, bt = band.y, br = band.z, bb = band.w;
    float n = 1e9;
    if (bl > 0.0) n = min(n, (p.x - rect.x) / bl);
    if (bt > 0.0) n = min(n, (p.y - rect.y) / bt);
    if (br > 0.0) n = min(n, (rect.z - p.x) / br);
    if (bb > 0.0) n = min(n, (rect.w - p.y) / bb);
    float2 tl = float2(rect.x + corner.x, rect.y + corner.x);
    n = min(n, efArc(p, tl, corner.x, min(bl, bt), bl > 0.0 && bt > 0.0 && p.x < tl.x && p.y < tl.y));
    float2 tr = float2(rect.z - corner.y, rect.y + corner.y);
    n = min(n, efArc(p, tr, corner.y, min(br, bt), br > 0.0 && bt > 0.0 && p.x > tr.x && p.y < tr.y));
    float2 bR = float2(rect.z - corner.z, rect.w - corner.z);
    n = min(n, efArc(p, bR, corner.z, min(br, bb), br > 0.0 && bb > 0.0 && p.x > bR.x && p.y > bR.y));
    float2 bL = float2(rect.x + corner.w, rect.w - corner.w);
    n = min(n, efArc(p, bL, corner.w, min(bl, bb), bl > 0.0 && bb > 0.0 && p.x < bL.x && p.y > bL.y));
    return lerp(1.0, efCurve(n, misc.x), misc.y);
}
""";

    /// <summary>Write the 16 constants the <see cref="Hlsl"/> function reads (rect, band, corner, misc).</summary>
    public static void Pack(in EdgeFeather f, Span<float> dst)
    {
        if (dst.Length < PackedFloats) throw new ArgumentException("needs 16 floats", nameof(dst));
        dst[0] = f.Rect.X; dst[1] = f.Rect.Y; dst[2] = f.Rect.Right; dst[3] = f.Rect.Bottom;
        dst[4] = MathF.Max(0f, f.BandLeft); dst[5] = MathF.Max(0f, f.BandTop);
        dst[6] = MathF.Max(0f, f.BandRight); dst[7] = MathF.Max(0f, f.BandBottom);
        dst[8] = MathF.Max(0f, f.Radii.TopLeft); dst[9] = MathF.Max(0f, f.Radii.TopRight);
        dst[10] = MathF.Max(0f, f.Radii.BottomRight); dst[11] = MathF.Max(0f, f.Radii.BottomLeft);
        dst[12] = (float)f.Falloff;
        dst[13] = Math.Clamp(f.Intensity, 0f, 1f);
        dst[14] = 0f; dst[15] = 0f;
    }

    /// <summary>The feather at device pixel centre (<paramref name="px"/>, <paramref name="py"/>). Zero allocation.</summary>
    public static float Evaluate(in EdgeFeather f, float px, float py)
    {
        Span<float> c = stackalloc float[PackedFloats];
        Pack(f, c);
        return Evaluate(c, px, py);
    }

    /// <summary>The C# port of <see cref="Hlsl"/>'s <c>edgeFeather</c> over the packed constants — same operations, same
    /// order, float precision.</summary>
    public static float Evaluate(ReadOnlySpan<float> k, float px, float py)
    {
        float rx = k[0], ry = k[1], rz = k[2], rw = k[3];
        float bl = k[4], bt = k[5], br = k[6], bb = k[7];
        float cx = k[8], cy = k[9], cz = k[10], cw = k[11];

        float n = 1e9f;
        if (bl > 0f) n = MathF.Min(n, (px - rx) / bl);
        if (bt > 0f) n = MathF.Min(n, (py - ry) / bt);
        if (br > 0f) n = MathF.Min(n, (rz - px) / br);
        if (bb > 0f) n = MathF.Min(n, (rw - py) / bb);

        float tlx = rx + cx, tly = ry + cx;
        n = MathF.Min(n, Arc(px, py, tlx, tly, cx, MathF.Min(bl, bt), bl > 0f && bt > 0f && px < tlx && py < tly));
        float trx = rz - cy, tr_y = ry + cy;
        n = MathF.Min(n, Arc(px, py, trx, tr_y, cy, MathF.Min(br, bt), br > 0f && bt > 0f && px > trx && py < tr_y));
        float brx = rz - cz, bry = rw - cz;
        n = MathF.Min(n, Arc(px, py, brx, bry, cz, MathF.Min(br, bb), br > 0f && bb > 0f && px > brx && py > bry));
        float blx = rx + cw, bly = rw - cw;
        n = MathF.Min(n, Arc(px, py, blx, bly, cw, MathF.Min(bl, bb), bl > 0f && bb > 0f && px < blx && py > bly));

        float curve = Curve(n, k[12]);
        return 1f + (curve - 1f) * k[13];   // lerp(1, curve, intensity)
    }

    private static float Curve(float t, float mode)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (mode < 0.5f) return t;
        if (mode < 1.5f) return t * t * (3f - 2f * t);
        return t * t * t;
    }

    private static float Arc(float px, float py, float cx, float cy, float r, float cb, bool active)
    {
        if (!active || r <= 0f || cb <= 0f) return 1e9f;
        float dx = px - cx, dy = py - cy;
        return (r - MathF.Sqrt(dx * dx + dy * dy)) / cb;
    }

    /// <summary>The whole-pixel rect (device px, LTRB) at every pixel centre of which <paramref name="f"/> is EXACTLY 1 —
    /// its unit interior: each enabled edge pulled in by its band (and by the radius of any corner arc that edge takes part
    /// in), rounded inward to whole pixels, so every centre inside clears the ramp by ≥ ½ px (n &gt; 1, and the curve and
    /// the intensity lerp return 1.0 exactly). A disabled edge does not bound it (±<see cref="Unbounded"/>); a
    /// <see cref="EdgeFeather.IsNone"/> feather is unbounded everywhere. The composite spends the feather's per-pixel
    /// evaluation only OUTSIDE this rect (<see cref="FeatherQuadSplit"/>).</summary>
    public static void UnitInterior(in EdgeFeather f, out float left, out float top, out float right, out float bottom)
    {
        left = -Unbounded; top = -Unbounded; right = Unbounded; bottom = Unbounded;
        if (f.IsNone) return;
        float bl = MathF.Max(0f, f.BandLeft), bt = MathF.Max(0f, f.BandTop), br = MathF.Max(0f, f.BandRight), bb = MathF.Max(0f, f.BandBottom);
        float tl = MathF.Max(0f, f.Radii.TopLeft), tr = MathF.Max(0f, f.Radii.TopRight);
        float brr = MathF.Max(0f, f.Radii.BottomRight), blr = MathF.Max(0f, f.Radii.BottomLeft);
        if (bl > 0f) left = MathF.Ceiling(f.Rect.X + MathF.Max(bl, MathF.Max(tl, blr)));
        if (bt > 0f) top = MathF.Ceiling(f.Rect.Y + MathF.Max(bt, MathF.Max(tl, tr)));
        if (br > 0f) right = MathF.Floor(f.Rect.Right - MathF.Max(br, MathF.Max(tr, brr)));
        if (bb > 0f) bottom = MathF.Floor(f.Rect.Bottom - MathF.Max(bb, MathF.Max(brr, blr)));
    }

    /// <summary>"No bound" for <see cref="UnitInterior"/>'s disabled edges.</summary>
    public const float Unbounded = 1e9f;
}

/// <summary>One piece of a feathered composite quad (device px, LTRB): drawn with the feather evaluated
/// (<see cref="Feathered"/>) or without it (inside the feather's unit interior, where it is exactly 1).</summary>
public readonly record struct FeatherPiece(float X0, float Y0, float X1, float Y1, bool Feathered)
{
    public float Area => MathF.Max(0f, X1 - X0) * MathF.Max(0f, Y1 - Y0);
}

/// <summary>
/// Splits a composite quad by a feather's unit interior (<see cref="EdgeFeatherMask.UnitInterior"/>): the part inside is
/// drawn WITHOUT the feather (it is exactly 1 there, so the pixels are bit-identical), and at most four strips around it
/// with the feather. A feathered viewport then pays the per-pixel feather only in its bands, not over its whole area.
/// Pieces are pixel-aligned where they meet and partition the quad exactly (the rasterizer's top-left rule covers each
/// pixel once).
/// </summary>
public static class FeatherQuadSplit
{
    public const int MaxPieces = 5;

    /// <summary>Split (<paramref name="x0"/>, <paramref name="y0"/>)–(<paramref name="x1"/>, <paramref name="y1"/>) by the
    /// unit interior LTRB; returns the piece count written to <paramref name="dst"/> (≥ <see cref="MaxPieces"/>).</summary>
    public static int Split(float x0, float y0, float x1, float y1, float il, float it, float ir, float ib, Span<FeatherPiece> dst)
    {
        float ix0 = MathF.Max(x0, il), iy0 = MathF.Max(y0, it), ix1 = MathF.Min(x1, ir), iy1 = MathF.Min(y1, ib);
        if (ix0 >= ix1 || iy0 >= iy1) { dst[0] = new FeatherPiece(x0, y0, x1, y1, true); return 1; }
        int n = 0;
        dst[n++] = new FeatherPiece(ix0, iy0, ix1, iy1, false);
        if (iy0 > y0) dst[n++] = new FeatherPiece(x0, y0, x1, iy0, true);
        if (y1 > iy1) dst[n++] = new FeatherPiece(x0, iy1, x1, y1, true);
        if (ix0 > x0) dst[n++] = new FeatherPiece(x0, iy0, ix0, iy1, true);
        if (x1 > ix1) dst[n++] = new FeatherPiece(ix1, iy0, x1, iy1, true);
        return n;
    }
}
