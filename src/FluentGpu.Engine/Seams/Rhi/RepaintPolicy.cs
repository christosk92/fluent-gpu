using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Render;

namespace FluentGpu.Rhi;

/// <summary>How a submit reached the swapchain (reported on <see cref="GpuFrameCounters.Route"/>). The primary window
/// always composites its retained tiles (gpu-renderer.md §13); a secondary swapchain (a windowed popup, a detached
/// pop-out) replays its stream straight into its back buffer.</summary>
public enum RepaintRoute : byte
{
    /// <summary>Clear + replay the WHOLE stream straight into the back buffer inside one render pass — the secondary
    /// swapchains' route (popups keep direct raster).</summary>
    FullDirect = 0,
    /// <summary>The retained-tile composite (gpu-renderer.md §13): the frame's invalid tiles rastered into their surfaces,
    /// then every composite item drawn into the back buffer in one render pass. The primary window's only route.</summary>
    Composite = 1,
}

/// <summary>An integer DEVICE-PIXEL rect, HALF-OPEN on both axes (<c>[Left,Right) × [Top,Bottom)</c>) — the exact shape
/// D3D12's <c>RSSetScissorRects</c> consumes and the shape a Present1 dirty rect takes (<see cref="PresentParams"/>).</summary>
public struct PixelRect : IEquatable<PixelRect>
{
    public int Left, Top, Right, Bottom;

    public PixelRect(int left, int top, int right, int bottom)
    { Left = left; Top = top; Right = right; Bottom = bottom; }

    /// <summary>No pixels at all (half-open ⇒ an empty span on either axis).</summary>
    public readonly bool IsEmpty => Right <= Left || Bottom <= Top;

    public readonly bool Equals(PixelRect o) => Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom;
    public readonly override bool Equals(object? obj) => obj is PixelRect p && Equals(p);
    public readonly override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
    public readonly override string ToString() => $"[{Left},{Top} → {Right},{Bottom})";
}

/// <summary>The ONE DIP → device-pixel conversion for repaint geometry (gpu-renderer.md §13): the backend's scissor
/// helper, a tile's decode-time cull box and the Present dirty-rect census all go through <see cref="ToPixel"/>, so
/// "scissored box", "culled box" and "dirty box" describe the same pixels by construction.</summary>
public static class RepaintPolicy
{
    /// <summary>
    /// DIP → DEVICE PIXELS, rounding OUT (floor/ceil) and clamping to the target.
    /// </summary>
    public static PixelRect ToPixel(in RectF r, float scale, int targetW, int targetH)
    {
        float s = scale <= 0f ? 1f : scale;
        int left = (int)MathF.Floor(r.X * s);
        int top = (int)MathF.Floor(r.Y * s);
        int right = (int)MathF.Ceiling((r.X + r.W) * s);
        int bottom = (int)MathF.Ceiling((r.Y + r.H) * s);
        if (targetW < 0) targetW = 0;
        if (targetH < 0) targetH = 0;
        left = Math.Clamp(left, 0, targetW);
        top = Math.Clamp(top, 0, targetH);
        right = Math.Clamp(right, left, targetW);
        bottom = Math.Clamp(bottom, top, targetH);
        return new PixelRect(left, top, right, bottom);
    }
}

/// <summary>The DrawList's opcode framing: the ONE opcode→payload-size table every stream walker (the D3D12 decoder, the
/// tile rasterizer's span/segment walks, the slice recorder, the headless reference replay) sizes ops through.</summary>
public static class RepaintStreamSafety
{
    /// <summary>
    /// The payload size of <paramref name="op"/>. Every stream walk that must FRAME the byte stream goes through this,
    /// so a new opcode can never be sized two ways in two walkers (the drift class the ownership map exists to prevent).
    /// Returns false for an opcode this table does not know, which every caller must treat as "stop walking".
    /// </summary>
    public static bool TryBodySize(DrawOp op, out int body)
    {
        switch (op)
        {
            case DrawOp.FillRoundRect: body = Unsafe.SizeOf<FillRoundRectCmd>(); break;
            case DrawOp.DrawGlyphRun: body = Unsafe.SizeOf<DrawGlyphRunCmd>(); break;
            case DrawOp.DrawGlyphRunGradient: body = Unsafe.SizeOf<DrawGlyphRunGradientCmd>(); break;
            case DrawOp.PushClip: body = Unsafe.SizeOf<ClipCmd>(); break;
            case DrawOp.PopClip: body = 0; break;
            case DrawOp.DrawImage: body = Unsafe.SizeOf<DrawImageCmd>(); break;
            case DrawOp.DrawRoundRectStroke: body = Unsafe.SizeOf<DrawRoundRectStrokeCmd>(); break;
            case DrawOp.DrawShadow: body = Unsafe.SizeOf<DrawShadowCmd>(); break;
            case DrawOp.DrawArc: body = Unsafe.SizeOf<DrawArcCmd>(); break;
            case DrawOp.DrawPolylineStroke: body = Unsafe.SizeOf<DrawPolylineStrokeCmd>(); break;
            case DrawOp.DrawGradientRect: body = Unsafe.SizeOf<DrawGradientRectCmd>(); break;
            case DrawOp.DrawGradientStroke: body = Unsafe.SizeOf<DrawGradientStrokeCmd>(); break;
            case DrawOp.DrawTabShape: body = Unsafe.SizeOf<DrawTabShapeCmd>(); break;
            case DrawOp.DrawIconMask: body = Unsafe.SizeOf<DrawIconMaskCmd>(); break;
            case DrawOp.DrawVideo: body = Unsafe.SizeOf<DrawVideoCmd>(); break;
            case DrawOp.EraseRoundRect: body = Unsafe.SizeOf<EraseRoundRectCmd>(); break;
            case DrawOp.FillPath: body = Unsafe.SizeOf<FillPathCmd>(); break;
            case DrawOp.StrokePath: body = Unsafe.SizeOf<StrokePathCmd>(); break;
            case DrawOp.DrawSeries: body = Unsafe.SizeOf<DrawSeriesCmd>(); break;
            case DrawOp.PushStencilClip: body = Unsafe.SizeOf<PushStencilClipCmd>(); break;
            case DrawOp.PopStencilClip: body = Unsafe.SizeOf<PopStencilClipCmd>(); break;
            case DrawOp.PopLayer: body = Unsafe.SizeOf<PopLayerCmd>(); break;
            case DrawOp.PushLayer: body = Unsafe.SizeOf<PushLayerCmd>(); break;
            // The retained-tile slice marker: recorder-internal (a submitted stream never carries one), framed here so
            // every slice-stream walker sizes it from the ONE table.
            case DrawOp.CompositeSlice: body = Unsafe.SizeOf<CompositeSliceCmd>(); break;
            default: body = 0; return false;
        }
        return true;
    }
}

/// <summary>
/// Decode-time primitive culling for a region-bounded replay — a retained tile's raster, or a degraded segment's chunk
/// (gpu-renderer.md §13; mandatory, not an optimization). The pipes' instance banks are PER FRAME, not per replay: N
/// naive full-stream replays multiply consumption ×N and silently DROP primitives when a bank overflows (Gradient's is
/// only 512). Skipping a primitive whose conservative device AABB misses the replay's target keeps consumption at ≈ one
/// frame's worth plus boundary straddlers.
/// <para>
/// The halos below are the per-kind slack between an op's declared rect and the pixels its VERTEX SHADER actually
/// rasterizes, so a primitive whose geometry lies outside the rect but whose FOOTPRINT reaches into it is kept. They are
/// deliberately ≥ the shader's own quad inflation. A primitive exactly ON the rect edge is KEPT (the tests are inclusive).
/// </para>
/// </summary>
public static class RepaintCull
{
    /// <summary>The SDF pipelines inflate their quad by 2 local units for the AA feather (RoundRectPipeline /
    /// GradientPipeline VS: <c>margin = stroke/2 + 2</c>). Also the floor for every other kind.</summary>
    public const float AaHaloDip = 2f;

    /// <summary>Glyph halo floor. A run's declared <c>Bounds</c> is the NODE BOX, not an ink box, and the rasterized
    /// quads can reach outside it.</summary>
    public const float GlyphHaloMinDip = 8f;

    /// <summary>Multiple of the em the glyph halo allows past the run's node box, on every side.
    /// <para>Unlike the five geometric halos, this is NOT derived from a vertex shader — <c>GlyphRenderer</c> places
    /// quads from shaped advances + atlas bearings, so the true bound is data, not a formula. The old <c>em/2</c>
    /// survives ordinary Latin text (descender ≈ 0.2 em, italic overhang ≈ 0.2 em) but three real classes exceed it: an
    /// oversized COLR/CBDT colour-emoji fallback, a tight <c>LineStacking</c>/<c>LineBounds</c> line box narrower than
    /// the font's ascent+descent, and a run measured with trimming whose shaped form overflows. Each under-cover chops
    /// a glyph at a tile edge and freezes the half-letter into the retained tile. Culling is a one-sided bet — keeping
    /// a straddler costs one scissored draw, dropping one is a visible defect — so the floor is set well past any
    /// plausible overhang and the actual quads are measured against it at decode time (the device's
    /// <c>glyphHaloBreach</c> counter, DEBUG/FLUENTGPU_DIAG only), which turns the heuristic into a monitored
    /// bound.</para></summary>
    public const float GlyphHaloEmScale = 1.5f;

    /// <summary>An outline band straddles the edge by <c>width/2</c>, plus the AA feather.</summary>
    public static float StrokeHalo(float strokeWidth)
        => (strokeWidth > 0f ? strokeWidth * 0.5f : 0f) + AaHaloDip;

    /// <summary>A drop shadow's quad half-extent past its (already offset) box: ShadowPipeline's VS uses
    /// <c>spread + 3·max(blur/2, 0.5)</c>; this returns <c>spread + 3·max(blur, 0.5) + AA</c>, a deliberate superset so a
    /// future shader tweak cannot silently under-cull. The OFFSET is applied by the caller (it shifts the box, it does
    /// not grow it symmetrically).</summary>
    public static float ShadowHalo(float spread, float blur)
        => MathF.Max(spread, 0f) + 3f * MathF.Max(blur, 0.5f) + AaHaloDip;

    /// <summary>Glyph-run halo: <c>max(<see cref="GlyphHaloMinDip"/>, |fontSize| × <see cref="GlyphHaloEmScale"/>)</c>,
    /// plus any per-glyph vertical wipe lift. See <see cref="GlyphHaloEmScale"/> for why this one is a monitored bound
    /// rather than a shader derivation.</summary>
    public static float GlyphHalo(float fontSize, float lift = 0f)
        => MathF.Max(GlyphHaloMinDip, MathF.Abs(fontSize) * GlyphHaloEmScale) + MathF.Abs(lift);

    /// <summary>Device-space AABB of a local rect under a 2×3 affine — all four corners, so rotation/skew are handled
    /// (canon §13.1 says the damage AABBs come from all four transformed corners; the cull test must agree).</summary>
    public static void Aabb(float x, float y, float w, float h,
        float m11, float m12, float m21, float m22, float dx, float dy,
        out float left, out float top, out float right, out float bottom)
    {
        float x0 = x, y0 = y, x1 = x + w, y1 = y + h;
        float ax = x0 * m11 + y0 * m21 + dx, ay = x0 * m12 + y0 * m22 + dy;
        float bx = x1 * m11 + y0 * m21 + dx, by = x1 * m12 + y0 * m22 + dy;
        float cx = x0 * m11 + y1 * m21 + dx, cy = x0 * m12 + y1 * m22 + dy;
        float ex = x1 * m11 + y1 * m21 + dx, ey = x1 * m12 + y1 * m22 + dy;
        left = MathF.Min(MathF.Min(ax, bx), MathF.Min(cx, ex));
        right = MathF.Max(MathF.Max(ax, bx), MathF.Max(cx, ex));
        top = MathF.Min(MathF.Min(ay, by), MathF.Min(cy, ey));
        bottom = MathF.Max(MathF.Max(ay, by), MathF.Max(cy, ey));
    }

    /// <summary>Keep the primitive whose device AABB (inflated by <paramref name="halo"/>) touches
    /// <paramref name="rect"/>. INCLUSIVE on every side: a primitive exactly on the rect edge is KEPT.</summary>
    public static bool Keep(float left, float top, float right, float bottom, float halo, in RectF rect)
        => left - halo <= rect.Right && right + halo >= rect.X
        && top - halo <= rect.Bottom && bottom + halo >= rect.Y;
}
