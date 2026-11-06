using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Layout;

// BoxEl.StackFallback: a row that stacks when its children do not fit (the shape of WinUI's InfoBarPanel, InfoBarPanel.cpp).
// Each child is measured at the box's content width; a child that would overflow comes back as wide as the box, so the sum
// of the widths is over the box whenever the row would have to wrap anything. The form is a pure function of the box's width
// and its children, worked out afresh by measure and by arrange: nothing is remembered between them, and a width that keeps
// the form moves nothing. One child stacks too (the stacked spacings are the ones that suit a lone child). A child that
// measures empty takes no slot and no margin; the first child's leading margin and the last child's trailing margin count for
// nothing. Children sit at the top and the start of their slot, and their Grow, Shrink and AlignSelf do not apply: the last
// child of a row takes what is left of the box. Like a grid or a z-stack this path keeps no memo of its own.
public sealed partial class FlexLayout
{
    private static float InnerWidth(float width, float padding)
        => float.IsInfinity(width) ? width : MathF.Max(0f, width - padding);

    /// <summary>Whether the box stacks at border-box <paramref name="width"/>; else the size of its row, without padding, and
    /// its last non-empty child.</summary>
    private bool StacksAt(NodeHandle node, in LayoutInput li, StackFallback fallback, float width, out float rowW, out float rowH, out NodeHandle last)
    {
        float avail = InnerWidth(width, li.Padding.Horizontal);
        float total = 0f, tallest = 0f, trailing = 0f;
        int items = 0;
        last = NodeHandle.Null;
        for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
        {
            var cs = Measure(c, avail);
            if (cs.Width <= 0f || cs.Height <= 0f) continue;
            ref LayoutInput cli = ref _scene.Layout(c);
            total += cs.Width + (items > 0 ? trailing + cli.Margin.Left : 0f);
            trailing = cli.Margin.Right;
            tallest = MathF.Max(tallest, cs.Height + cli.Margin.Vertical);
            last = c;
            items++;
        }
        rowW = total;
        rowH = tallest;
        return items == 1 || total > avail + 0.01f || (!float.IsNaN(fallback.MaxRowHeight) && tallest > fallback.MaxRowHeight + 0.01f);
    }

    /// <summary>The width a stacked child is measured and arranged at: the stack's content width less the child's own margins
    /// (a negative margin, a hyperlink's optical pull, gives no extra room).</summary>
    private static float StackedChildAvail(StackFallback fallback, in Edges4 margin, float width)
        => InnerWidth(width, fallback.Padding.Horizontal + MathF.Max(0f, margin.Horizontal));

    private Size2 MeasureStackFallback(NodeHandle node, in LayoutInput li, StackFallback fallback, float availW)
    {
        float width = DefiniteWidth(in li, availW);
        float w, h;
        if (StacksAt(node, in li, fallback, width, out float rowW, out float rowH, out _))
        {
            float widest = 0f, stacked = 0f, bottom = 0f;
            int items = 0, i = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), i++)
            {
                if (Collapsed(c)) continue;
                var m = fallback.MarginAt(i);
                var cs = Measure(c, StackedChildAvail(fallback, in m, width));
                if (cs.Width <= 0f || cs.Height <= 0f) continue;
                widest = MathF.Max(widest, cs.Width + m.Horizontal);
                stacked += cs.Height + (items > 0 ? bottom + m.Top : 0f);
                bottom = m.Bottom;
                items++;
            }
            w = widest + fallback.Padding.Horizontal;
            h = stacked + fallback.Padding.Vertical;
        }
        else
        {
            w = rowW + li.Padding.Horizontal;
            h = rowH + li.Padding.Vertical;
        }
        if (!float.IsNaN(li.Width)) w = li.Width;
        if (!float.IsNaN(li.Height)) h = li.Height;
        w = Clamp(w, li.MinW, li.MaxW); h = Clamp(h, li.MinH, li.MaxH);
        WriteMeasuredBounds(node, w, h);
        return new Size2(w, h);
    }

    private void ArrangeStackFallback(NodeHandle node, float finalW, in LayoutInput li, StackFallback fallback)
    {
        if (StacksAt(node, in li, fallback, finalW, out _, out _, out var last))
        {
            float y = fallback.Padding.Top;
            bool first = true;
            int i = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), i++)
            {
                if (Collapsed(c)) continue;
                var m = fallback.MarginAt(i);
                var cs = Measure(c, StackedChildAvail(fallback, in m, finalW));
                if (cs.Width <= 0f || cs.Height <= 0f) { Arrange(c, fallback.Padding.Left, y, cs.Width, cs.Height); continue; }
                if (!first) y += m.Top;
                Arrange(c, fallback.Padding.Left + m.Left, y, cs.Width, cs.Height);
                y += cs.Height + m.Bottom;
                first = false;
            }
        }
        else
        {
            float avail = InnerWidth(finalW, li.Padding.Horizontal);
            float x = li.Padding.Left, trailing = 0f;
            bool first = true;
            for (var c = FirstVisibleChild(node); !c.IsNull; c = NextVisibleSibling(c))
            {
                var cs = Measure(c, avail);
                if (cs.Width <= 0f || cs.Height <= 0f) { Arrange(c, x, li.Padding.Top, cs.Width, cs.Height); continue; }
                float mL, mT, mR;
                { ref LayoutInput cli = ref _scene.Layout(c); mL = cli.Margin.Left; mT = cli.Margin.Top; mR = cli.Margin.Right; }
                if (!first) x += trailing + mL;
                float cw = c == last ? MathF.Max(cs.Width, finalW - li.Padding.Right - x) : cs.Width;
                Arrange(c, x, li.Padding.Top + mT, cw, cs.Height);
                x += cw;
                trailing = mR;
                first = false;
            }
        }
    }
}
