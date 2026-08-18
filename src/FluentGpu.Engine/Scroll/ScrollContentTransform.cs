using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Scroll;

/// <summary>
/// The content-child transform composer + edge-band-sign guard — ported VERBATIM (scroll-v3-plan §10 WP-B item 3)
/// from <c>FluentGpu.Animation.OverscrollPhysics.WriteContentTransform</c> / <c>GuardBandSign</c>
/// (<c>Animation/OverscrollPhysics.cs:188-225</c>, pre-scroll-v3). The physics that PRODUCES <c>offset</c>/<c>band</c>
/// moved into the portable kernel (<c>ScrollPhysics</c>, WP-A); this half — turning a committed offset+band into the
/// content node's <c>LocalTransform</c> — stays UI-side because it writes a <see cref="NodePaint"/> row, which the
/// kernel (Scene-agnostic by design, §2) cannot touch. The sole caller is <see cref="SceneScrollSink"/>.
/// </summary>
public static class ScrollContentTransform
{
    /// <summary>Compose the content child's <c>LocalTransform</c> from the committed scroll offset + rubber-band
    /// displacement + zoom factor (verbatim port — see remarks). Sub-pixel: no device-grid snap (crisp text under
    /// sub-pixel translation is the glyph renderer's job, the sub-pixel phase atlas, not this transform).</summary>
    public static void WriteContentTransform(
        ref NodePaint cp, in RectF contentBounds,
        bool horizontal, float offset, float band,
        float zoomFactor, float scale)
    {
        float z = (!float.IsFinite(zoomFactor) || zoomFactor <= 0f) ? 1f : zoomFactor;
        _ = scale;   // kept in the signature (every caller passes the live DeviceScale); no longer a snap denominator
        float t = offset + band;   // sub-pixel: no device-grid snap (see the remarks above)
        float offX = horizontal ? t : 0f;
        float offY = horizontal ? 0f : t;

        z = System.Math.Clamp(z, 1e-3f, 64f); // pick max to match product needs

        const float epsilon = 1e-4f;
        if (MathF.Abs(z - 1f) <= epsilon)
        {
            cp.LocalTransform = Affine2D.Translation(-offX, -offY);
            return;
        }

        float w = contentBounds.W, h = contentBounds.H;
        float ox = w * cp.OriginX, oy = h * cp.OriginY;
        var map = new Affine2D(z, 0f, 0f, z, -offX, -offY);
        cp.LocalTransform = Affine2D.Translation(-ox, -oy).Multiply(map).Multiply(Affine2D.Translation(ox, oy));
    }

    /// <summary>Clamp band sign at clamped edges (prevents a 1-frame wrong-way flash during spring / relayout).</summary>
    public static float GuardBandSign(float band, float offset, float maxOffset)
    {
        if (offset <= 0.5f && band > 0f) return 0f;
        if (offset >= maxOffset - 0.5f && band < 0f) return 0f;
        return band;
    }
}
