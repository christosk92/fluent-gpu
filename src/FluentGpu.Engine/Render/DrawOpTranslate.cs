using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Render;

/// <summary>
/// Translate ONE recorded op payload by a window-space offset: every primitive carries its absolute position only in its
/// world <c>Transform</c> (path triangle soups, glyph runs and video holes are authored in local space), and every scope
/// op (clip, stencil clip, layer) in its device rects — so patching those is exact. Used by the headless composite model
/// to place a slice segment at its posed offset. Pure; zero allocation.
/// </summary>
public static class DrawOpTranslate
{
    /// <summary>Patch <paramref name="payload"/> (the body of <paramref name="op"/>) in place by (<paramref name="dx"/>,
    /// <paramref name="dy"/>).</summary>
    public static void Apply(DrawOp op, Span<byte> payload, float dx, float dy)
    {
        if (dx == 0f && dy == 0f) return;
        switch (op)
        {
            case DrawOp.FillRoundRect: { var c = Read<FillRoundRectCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.DrawImage: { var c = Read<DrawImageCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.DrawRoundRectStroke: { var c = Read<DrawRoundRectStrokeCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.DrawShadow: { var c = Read<DrawShadowCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.DrawGradientRect: { var c = Read<DrawGradientRectCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.DrawGradientStroke: { var c = Read<DrawGradientStrokeCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.DrawArc: { var c = Read<DrawArcCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.DrawPolylineStroke: { var c = Read<DrawPolylineStrokeCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.DrawTabShape: { var c = Read<DrawTabShapeCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.DrawIconMask: { var c = Read<DrawIconMaskCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.DrawVideo: { var c = Read<DrawVideoCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.EraseRoundRect: { var c = Read<EraseRoundRectCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.FillPath: { var c = Read<FillPathCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.StrokePath: { var c = Read<StrokePathCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.DrawSeries: { var c = Read<DrawSeriesCmd>(payload); c.Transform = Move(c.Transform, dx, dy); Write(payload, c); break; }
            case DrawOp.DrawGlyphRun: { var c = Read<DrawGlyphRunCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.DrawGlyphRunGradient: { var c = Read<DrawGlyphRunGradientCmd>(payload); Write(payload, c with { Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.PushClip:
            {
                var c = Read<ClipCmd>(payload);
                Write(payload, new ClipCmd(Offset(c.DeviceRect, dx, dy), c.CornerRadius > 0f ? Offset(c.RoundedRect, dx, dy) : c.RoundedRect, c.CornerRadius));
                break;
            }
            case DrawOp.PushStencilClip: { var c = Read<PushStencilClipCmd>(payload); Write(payload, c with { DeviceRect = Offset(c.DeviceRect, dx, dy), Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.PopStencilClip: { var c = Read<PopStencilClipCmd>(payload); Write(payload, c with { DeviceRect = Offset(c.DeviceRect, dx, dy), Transform = Move(c.Transform, dx, dy) }); break; }
            case DrawOp.PushLayer: { var c = Read<PushLayerCmd>(payload); Write(payload, Translate(in c, dx, dy)); break; }
            case DrawOp.PopLayer: { var c = Read<PopLayerCmd>(payload); Write(payload, new PopLayerCmd(Offset(c.DeviceRect, dx, dy))); break; }
        }
    }

    /// <summary>A layer command moved by (<paramref name="dx"/>, <paramref name="dy"/>) (its device rect and composite clip).</summary>
    public static PushLayerCmd Translate(in PushLayerCmd l, float dx, float dy)
        => dx == 0f && dy == 0f ? l : l with
        {
            DeviceRect = Offset(l.DeviceRect, dx, dy),
            CompositeClip = l.CompositeClip.IsEmpty ? l.CompositeClip : Offset(l.CompositeClip, dx, dy),
        };

    /// <summary>Offset a device-space rect; an empty rect stays empty and an UNBOUNDED axis stays unbounded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RectF Offset(in RectF r, float dx, float dy)
    {
        if ((dx == 0f && dy == 0f) || r.IsEmpty) return r;
        const float unbounded = -5e8f;
        return new RectF(r.X <= unbounded ? r.X : r.X + dx, r.Y <= unbounded ? r.Y : r.Y + dy, r.W, r.H);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Affine2D Move(in Affine2D t, float dx, float dy)
        => new(t.M11, t.M12, t.M21, t.M22, t.Dx + dx, t.Dy + dy);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T Read<T>(Span<byte> p) where T : struct => MemoryMarshal.Read<T>(p);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write<T>(Span<byte> p, in T v) where T : struct => MemoryMarshal.Write(p, in v);
}
