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
                if (p.BorderWidth > 0f && p.BorderColor.A > 0f)
                {
                    dl.FillRoundRect(rect, p.Corners, p.BorderColor, key);        // border ring (drawn first)
                    float bw = p.BorderWidth;
                    var inner = new RectF(rect.X + bw, rect.Y + bw, MathF.Max(0f, rect.W - 2 * bw), MathF.Max(0f, rect.H - 2 * bw));
                    var ic = new CornerRadius4(
                        MathF.Max(0f, p.Corners.TopLeft - bw), MathF.Max(0f, p.Corners.TopRight - bw),
                        MathF.Max(0f, p.Corners.BottomRight - bw), MathF.Max(0f, p.Corners.BottomLeft - bw));
                    if (p.Fill.A > 0f) dl.FillRoundRect(inner, ic, p.Fill, key);   // inset fill on top
                }
                else
                {
                    dl.FillRoundRect(rect, p.Corners, p.Fill, key);
                }
                break;
            case VisualKind.Text when !p.Text.IsEmpty:
                ref var li = ref scene.Layout(node);
                dl.DrawGlyphRun(rect, p.TextColor, p.Text, li.TextStyle.SizeDip, li.TextStyle.Bold ? 1 : 0, key);
                break;
        }

        for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c))
            Walk(scene, dl, c, ax, ay, depth + 1);
    }
}
