using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Rhi;

namespace FluentGpu.Render.Tiles;

/// <summary>
/// The conservative painted footprint of ONE recorded primitive (docs/plans/scroll-gpu-retained-tiles-implementation.md
/// §A.4): its declared rect through its world transform (all four corners), inflated by the per-kind halo the backend's
/// decode-time cull uses (<see cref="RepaintCull"/>). The slice recorder unions these into each segment's bounds — the
/// tiles a segment requests — and the per-tile replay culls against the same numbers, so a primitive is never kept by
/// one and dropped by the other. Window DIP (the slice's pose-free space). Pure; zero allocation.
/// </summary>
public static class SliceOpBounds
{
    /// <summary>Footprint of <paramref name="op"/> (<paramref name="payload"/> = its body). False for scope ops, markers
    /// and anything that paints nothing.</summary>
    public static bool TryGet(DrawOp op, ReadOnlySpan<byte> payload, out RectF bounds)
    {
        switch (op)
        {
            case DrawOp.FillRoundRect: { var c = Read<FillRoundRectCmd>(payload); return Box(c.Rect, c.Transform, RepaintCull.AaHaloDip, out bounds); }
            case DrawOp.DrawGlyphRun: { var c = Read<DrawGlyphRunCmd>(payload); return Box(c.Bounds, c.Transform, RepaintCull.GlyphHalo(c.FontSize), out bounds); }
            case DrawOp.DrawGlyphRunGradient: { var c = Read<DrawGlyphRunGradientCmd>(payload); return Box(c.Bounds, c.Transform, RepaintCull.GlyphHalo(c.FontSize, c.Lift), out bounds); }
            case DrawOp.DrawImage: { var c = Read<DrawImageCmd>(payload); return Box(c.Rect, c.Transform, RepaintCull.AaHaloDip, out bounds); }
            case DrawOp.DrawRoundRectStroke: { var c = Read<DrawRoundRectStrokeCmd>(payload); return Box(c.Rect, c.Transform, RepaintCull.StrokeHalo(c.StrokeWidth), out bounds); }
            case DrawOp.DrawShadow:
            {
                var c = Read<DrawShadowCmd>(payload);
                var r = new RectF(c.Rect.X + c.OffsetX, c.Rect.Y + c.OffsetY, c.Rect.W, c.Rect.H);
                return Box(r, c.Transform, RepaintCull.ShadowHalo(c.Spread, c.Blur), out bounds);
            }
            case DrawOp.DrawArc: { var c = Read<DrawArcCmd>(payload); return Box(c.Rect, c.Transform, RepaintCull.StrokeHalo(c.Thickness), out bounds); }
            case DrawOp.DrawPolylineStroke: { var c = Read<DrawPolylineStrokeCmd>(payload); return Box(c.Rect, c.Transform, RepaintCull.StrokeHalo(c.Thickness), out bounds); }
            case DrawOp.DrawGradientRect: { var c = Read<DrawGradientRectCmd>(payload); return Box(c.Rect, c.Transform, RepaintCull.AaHaloDip, out bounds); }
            case DrawOp.DrawGradientStroke: { var c = Read<DrawGradientStrokeCmd>(payload); return Box(c.Rect, c.Transform, RepaintCull.StrokeHalo(c.StrokeWidth), out bounds); }
            case DrawOp.DrawTabShape: { var c = Read<DrawTabShapeCmd>(payload); return Box(c.Rect, c.Transform, RepaintCull.AaHaloDip, out bounds); }
            case DrawOp.DrawIconMask: { var c = Read<DrawIconMaskCmd>(payload); return Box(c.Rect, c.Transform, RepaintCull.AaHaloDip, out bounds); }
            case DrawOp.DrawVideo: { var c = Read<DrawVideoCmd>(payload); return Box(c.Dst, c.Transform, RepaintCull.AaHaloDip, out bounds); }
            case DrawOp.EraseRoundRect: { var c = Read<EraseRoundRectCmd>(payload); return Box(c.Rect, c.Transform, RepaintCull.AaHaloDip, out bounds); }
            case DrawOp.FillPath: { var c = Read<FillPathCmd>(payload); return Box(c.Rect, c.Transform, RepaintCull.AaHaloDip, out bounds); }
            case DrawOp.StrokePath: { var c = Read<StrokePathCmd>(payload); return Box(c.Rect, c.Transform, RepaintCull.AaHaloDip, out bounds); }
            case DrawOp.DrawSeries: { var c = Read<DrawSeriesCmd>(payload); return Box(c.Rect, c.Transform, c.Shape >= 2 ? RepaintCull.StrokeHalo(c.Thickness + 2f) : RepaintCull.AaHaloDip, out bounds); }
            case DrawOp.DrawSprites: { var c = Read<DrawSpritesCmd>(payload); return Box(c.Rect, c.Transform, RepaintCull.AaHaloDip + 1f, out bounds); }
            case DrawOp.PushLayer:
            {
                // An inline (folded) layer: an acrylic paints its frosted rect; a blur's Gaussian reaches past its content
                // by the tap radius (device px — the same number is a superset in DIP at any scale ≥ 1).
                var l = Read<PushLayerCmd>(payload);
                float halo = l.Kind == (int)LayerKind.Blur && l.BlurSigma > 0f ? SelfBlurRegion.TapRadius(l.BlurSigma) + RepaintCull.AaHaloDip
                    : RepaintCull.AaHaloDip;
                return Box(l.DeviceRect, Affine2D.Identity, halo, out bounds);
            }
            default:
                bounds = default;
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T Read<T>(ReadOnlySpan<byte> p) where T : struct => MemoryMarshal.Read<T>(p);

    private static bool Box(in RectF r, in Affine2D t, float halo, out RectF bounds)
    {
        if (!(r.W > 0f) || !(r.H > 0f) || float.IsNaN(r.X) || float.IsNaN(r.Y)) { bounds = default; return false; }
        RepaintCull.Aabb(r.X, r.Y, r.W, r.H, t.M11, t.M12, t.M21, t.M22, t.Dx, t.Dy, out float l, out float tp, out float rr, out float b);
        bounds = new RectF(l - halo, tp - halo, rr - l + 2f * halo, b - tp + 2f * halo);
        return true;
    }
}
