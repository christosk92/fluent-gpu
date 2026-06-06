using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Text;

namespace FluentGpu.Layout;

/// <summary>
/// Slice-subset flex: main-axis stacking with gap + padding, cross-axis = max child, leaves sized by content
/// (text via the IFontSystem measure seam). Two passes: measure (bottom-up, writes W/H) then arrange (top-down,
/// writes X/Y). The full engine is the ported Yoga (grow/shrink/justify/align/wrap/abs-pos) over the same columns.
/// </summary>
public sealed class FlexLayout
{
    private readonly SceneStore _scene;
    private readonly IFontSystem _fonts;

    public FlexLayout(SceneStore scene, IFontSystem fonts)
    {
        _scene = scene;
        _fonts = fonts;
    }

    public void Run(NodeHandle root)
    {
        if (root.IsNull) return;
        Measure(root);
        Arrange(root, 0f, 0f);
    }

    private Size2 Measure(NodeHandle node)
    {
        ref LayoutInput li = ref _scene.Layout(node);
        ref NodePaint paint = ref _scene.Paint(node);

        Size2 size;
        if (paint.VisualKind == VisualKind.Text)
        {
            var m = _fonts.Measure(paint.Text, li.TextStyle);
            size = m.Size;
        }
        else
        {
            float main = 0f, cross = 0f;
            int n = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
            {
                var cs = Measure(c);
                if (li.Direction == 0) { main += cs.Width; cross = MathF.Max(cross, cs.Height); }
                else { main += cs.Height; cross = MathF.Max(cross, cs.Width); }
                n++;
            }
            if (n > 1) main += li.Gap * (n - 1);

            float w = li.Direction == 0 ? main : cross;
            float h = li.Direction == 0 ? cross : main;
            w += li.Padding.Horizontal;
            h += li.Padding.Vertical;
            size = new Size2(w, h);
        }

        if (!float.IsNaN(li.Width)) size = size with { Width = li.Width };
        if (!float.IsNaN(li.Height)) size = size with { Height = li.Height };

        ref RectF b = ref _scene.Bounds(node);
        b = b with { W = size.Width, H = size.Height };
        return size;
    }

    private void Arrange(NodeHandle node, float x, float y)
    {
        ref RectF b = ref _scene.Bounds(node);
        b = b with { X = x, Y = y };

        ref LayoutInput li = ref _scene.Layout(node);
        float cursorMain = li.Direction == 0 ? li.Padding.Left : li.Padding.Top;
        float crossStart = li.Direction == 0 ? li.Padding.Top : li.Padding.Left;

        bool first = true;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            if (!first) cursorMain += li.Gap;
            first = false;

            ref RectF cb = ref _scene.Bounds(c);
            float cx, cy;
            if (li.Direction == 0) { cx = cursorMain; cy = crossStart; cursorMain += cb.W; }
            else { cx = crossStart; cy = cursorMain; cursorMain += cb.H; }

            Arrange(c, cx, cy);   // child X/Y are LOCAL (relative to this node's top-left)
        }
    }
}
