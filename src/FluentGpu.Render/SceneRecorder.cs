using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Render;

/// <summary>
/// Phase 8 (record): walks the retained SceneStore and emits the DrawList. The slice composes absolute positions
/// by accumulating parent origin (transforms are translations here); the full engine composes WorldTransform[].
/// </summary>
public static class SceneRecorder
{
    public static void Record(SceneStore scene, DrawList dl)
    {
        dl.Reset();
        if (scene.Root.IsNull) return;
        Walk(scene, dl, scene.Root, 0f, 0f, 0);
    }

    private static void Walk(SceneStore scene, DrawList dl, NodeHandle node, float ox, float oy, int depth)
    {
        ref RectF b = ref scene.Bounds(node);
        float ax = ox + b.X, ay = oy + b.Y;
        var rect = new RectF(ax, ay, b.W, b.H);
        ref NodePaint p = ref scene.Paint(node);
        ulong key = (ulong)depth << 32;   // painter order ~ depth for the slice

        switch (p.VisualKind)
        {
            case VisualKind.Box when p.Fill.A > 0f || (p.BorderWidth > 0f && p.BorderColor.A > 0f):
            {
                // Interaction visual states (composition-style — no re-render): pressed darkens, hover lightens.
                NodeFlags f = scene.Flags(node);
                ColorF fill = p.Fill, border = p.BorderColor;
                if ((f & NodeFlags.Pressed) != 0) { fill = Darken(fill, 0.12f); border = Darken(border, 0.12f); }
                else if ((f & NodeFlags.Hovered) != 0) { fill = Lighten(fill, 0.08f); border = Lighten(border, 0.08f); }

                if (p.BorderWidth > 0f && border.A > 0f)
                {
                    dl.FillRoundRect(rect, p.Corners, border, key);              // border ring (drawn first)
                    float bw = p.BorderWidth;
                    var inner = new RectF(rect.X + bw, rect.Y + bw, MathF.Max(0f, rect.W - 2 * bw), MathF.Max(0f, rect.H - 2 * bw));
                    var ic = new CornerRadius4(
                        MathF.Max(0f, p.Corners.TopLeft - bw), MathF.Max(0f, p.Corners.TopRight - bw),
                        MathF.Max(0f, p.Corners.BottomRight - bw), MathF.Max(0f, p.Corners.BottomLeft - bw));
                    if (fill.A > 0f) dl.FillRoundRect(inner, ic, fill, key);     // inset fill on top
                }
                else
                {
                    dl.FillRoundRect(rect, p.Corners, fill, key);
                }
                break;
            }
            case VisualKind.Text when !p.Text.IsEmpty:
                ref var li = ref scene.Layout(node);
                dl.DrawGlyphRun(rect, p.TextColor, p.Text, li.TextStyle.SizeDip, li.TextStyle.Bold ? 1 : 0, key);
                break;
        }

        for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c))
            Walk(scene, dl, c, ax, ay, depth + 1);
    }

    private static ColorF Lighten(ColorF c, float t) => new(c.R + (1f - c.R) * t, c.G + (1f - c.G) * t, c.B + (1f - c.B) * t, c.A);
    private static ColorF Darken(ColorF c, float t) => new(c.R * (1f - t), c.G * (1f - t), c.B * (1f - t), c.A);
}
