using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Render;

/// <summary>
/// Phase 8 (record): walks the retained SceneStore and emits the DrawList. Composites like a browser — each node's
/// geometry is emitted in LOCAL space with a world transform (parent ∘ translate ∘ LocalTransform, scale/rotate about
/// the node's center) and a cumulative opacity, so transform/opacity animate without relayout or re-record of content.
/// </summary>
public static class SceneRecorder
{
    public static void Record(SceneStore scene, DrawList dl)
    {
        dl.Reset();
        if (scene.Root.IsNull) return;
        Walk(scene, dl, scene.Root, Affine2D.Identity, 1f, 0);
    }

    private static void Walk(SceneStore scene, DrawList dl, NodeHandle node, Affine2D parentWorld, float parentOpacity, int depth)
    {
        ref RectF b = ref scene.Bounds(node);
        ref NodePaint p = ref scene.Paint(node);

        // node-local → device: parent ∘ translate(node pos) ∘ (local transform about the node centre)
        Affine2D world = parentWorld.Multiply(Affine2D.Translation(b.X, b.Y));
        if (!p.LocalTransform.IsIdentity)
        {
            float cx = b.W * 0.5f, cy = b.H * 0.5f;
            world = world.Multiply(Affine2D.Translation(cx, cy)).Multiply(p.LocalTransform).Multiply(Affine2D.Translation(-cx, -cy));
        }
        float opacity = parentOpacity * p.Opacity;

        ulong key = (ulong)depth << 32;   // painter order ~ depth for the slice
        var local = new RectF(0f, 0f, b.W, b.H);

        switch (p.VisualKind)
        {
            case VisualKind.Box when p.Fill.A > 0f || (p.BorderWidth > 0f && p.BorderColor.A > 0f):
            {
                // Interaction visual states (composition-style): pressed darkens, hover lightens.
                NodeFlags f = scene.Flags(node);
                ColorF fill = p.Fill, border = p.BorderColor;
                if ((f & NodeFlags.Pressed) != 0) { fill = p.PressedFill.A > 0f ? p.PressedFill : Darken(fill, 0.12f); border = Darken(border, 0.12f); }
                else if ((f & NodeFlags.Hovered) != 0) { fill = p.HoverFill.A > 0f ? p.HoverFill : Lighten(fill, 0.08f); border = Lighten(border, 0.08f); }

                if (p.BorderWidth > 0f && border.A > 0f)
                {
                    dl.FillRoundRect(local, p.Corners, border, world, opacity, key);     // border ring (local space)
                    float bw = p.BorderWidth;
                    var inner = new RectF(bw, bw, MathF.Max(0f, b.W - 2 * bw), MathF.Max(0f, b.H - 2 * bw));
                    var ic = new CornerRadius4(
                        MathF.Max(0f, p.Corners.TopLeft - bw), MathF.Max(0f, p.Corners.TopRight - bw),
                        MathF.Max(0f, p.Corners.BottomRight - bw), MathF.Max(0f, p.Corners.BottomLeft - bw));
                    if (fill.A > 0f) dl.FillRoundRect(inner, ic, fill, world, opacity, key);
                }
                else
                {
                    dl.FillRoundRect(local, p.Corners, fill, world, opacity, key);
                }
                break;
            }
            case VisualKind.Text when !p.Text.IsEmpty:
            {
                ref var li = ref scene.Layout(node);
                dl.DrawGlyphRun(local, p.TextColor, p.Text, li.TextStyle.SizeDip, li.TextStyle.Bold ? 1 : 0, world, opacity, key);
                break;
            }
        }

        for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c))
            Walk(scene, dl, c, world, opacity, depth + 1);
    }

    private static ColorF Lighten(ColorF c, float t) => new(c.R + (1f - c.R) * t, c.G + (1f - c.G) * t, c.B + (1f - c.B) * t, c.A);
    private static ColorF Darken(ColorF c, float t) => new(c.R * (1f - t), c.G * (1f - t), c.B * (1f - t), c.A);
}
