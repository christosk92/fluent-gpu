using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Text;

namespace FluentGpu.Layout;

/// <summary>
/// Flexbox layout over the SoA scene columns. Two descents: Measure (bottom-up — content/basis base sizes, honoring
/// explicit size + min/max + text content) then Arrange (top-down — distribute free space by flex-grow/shrink,
/// position by justify-content, align the cross axis by align-items/align-self incl. stretch, applying margins).
/// Direction 0 = row (main = X), 1 = column (main = Y). Wrap / grid / absolute positioning are the remaining layout work.
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
        var size = Measure(root);
        Arrange(root, 0f, 0f, size.Width, size.Height);
    }

    private static bool Row(in LayoutInput li) => li.Direction == 0;
    private static float Clamp(float v, float min, float max)
    {
        if (!float.IsNaN(min) && v < min) v = min;
        if (!float.IsNaN(max) && v > max) v = max;
        return v;
    }

    // ── Measure: fill Bounds.W/H with each node's base (hypothetical) border-box size ──
    private Size2 Measure(NodeHandle node)
    {
        ref LayoutInput li = ref _scene.Layout(node);
        ref NodePaint paint = ref _scene.Paint(node);

        float w, h;
        if (paint.VisualKind == VisualKind.Text)
        {
            var m = _fonts.Measure(paint.Text, li.TextStyle);
            w = m.Size.Width; h = m.Size.Height;
        }
        else
        {
            bool row = Row(li);
            float main = 0f, cross = 0f;
            int n = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
            {
                var cs = Measure(c);
                ref LayoutInput cli = ref _scene.Layout(c);
                float cMain = row ? cs.Width : cs.Height;
                float cCross = row ? cs.Height : cs.Width;
                if (!float.IsNaN(cli.FlexBasis)) cMain = cli.FlexBasis;
                cMain += MarginMain(cli, row);
                cCross += MarginCross(cli, row);
                main += cMain;
                cross = MathF.Max(cross, cCross);
                n++;
            }
            if (n > 1) main += li.Gap * (n - 1);
            main += row ? li.Padding.Horizontal : li.Padding.Vertical;
            cross += row ? li.Padding.Vertical : li.Padding.Horizontal;
            w = row ? main : cross;
            h = row ? cross : main;
        }

        if (!float.IsNaN(li.Width)) w = li.Width;
        if (!float.IsNaN(li.Height)) h = li.Height;
        w = Clamp(w, li.MinW, li.MaxW);
        h = Clamp(h, li.MinH, li.MaxH);

        ref RectF b = ref _scene.Bounds(node);
        b = new RectF(b.X, b.Y, w, h);
        return new Size2(w, h);
    }

    // ── Arrange: position + size children within the node's final box ──
    private void Arrange(NodeHandle node, float x, float y, float finalW, float finalH)
    {
        ref RectF b = ref _scene.Bounds(node);
        b = new RectF(x, y, finalW, finalH);

        ref LayoutInput li = ref _scene.Layout(node);
        if (_scene.FirstChild(node).IsNull) return;
        bool row = Row(li);

        float availMain = (row ? finalW : finalH) - (row ? li.Padding.Horizontal : li.Padding.Vertical);
        float availCross = (row ? finalH : finalW) - (row ? li.Padding.Vertical : li.Padding.Horizontal);
        float padMainStart = row ? li.Padding.Left : li.Padding.Top;
        float padCrossStart = row ? li.Padding.Top : li.Padding.Left;

        // First pass: base main sizes + counts.
        int n = 0; float usedMain = 0f, totalGrow = 0f, totalShrinkScaled = 0f;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            ref LayoutInput cli = ref _scene.Layout(c);
            ref RectF cb = ref _scene.Bounds(c);
            float baseMain = !float.IsNaN(cli.FlexBasis) ? cli.FlexBasis : (row ? cb.W : cb.H);
            baseMain = ClampMain(cli, row, baseMain);
            usedMain += baseMain + MarginMain(cli, row);
            totalGrow += cli.FlexGrow;
            totalShrinkScaled += cli.FlexShrink * baseMain;
            n++;
        }
        if (n > 1) usedMain += li.Gap * (n - 1);
        float free = availMain - usedMain;

        // Distribute free space → each child's final main size.
        Span<float> finalMain = n <= 64 ? stackalloc float[n] : new float[n];
        {
            int i = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), i++)
            {
                ref LayoutInput cli = ref _scene.Layout(c);
                ref RectF cb = ref _scene.Bounds(c);
                float baseMain = !float.IsNaN(cli.FlexBasis) ? cli.FlexBasis : (row ? cb.W : cb.H);
                baseMain = ClampMain(cli, row, baseMain);
                float fm = baseMain;
                if (free > 0f && totalGrow > 0f) fm = baseMain + free * (cli.FlexGrow / totalGrow);
                else if (free < 0f && totalShrinkScaled > 0f) fm = baseMain + free * (cli.FlexShrink * baseMain / totalShrinkScaled);
                finalMain[i] = MathF.Max(0f, ClampMain(cli, row, fm));
            }
        }

        // Leftover after sizing → justify-content spacing.
        float consumed = 0f; for (int i = 0; i < n; i++) consumed += finalMain[i];
        { int i = 0; for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), i++) consumed += MarginMain(_scene.Layout(c), row); }
        if (n > 1) consumed += li.Gap * (n - 1);
        float leftover = MathF.Max(0f, availMain - consumed);
        (float lead, float between) = Distribute(li.Justify, leftover, n);

        // Place children.
        float cursor = padMainStart + lead;
        int idx = 0;
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), idx++)
        {
            ref LayoutInput cli = ref _scene.Layout(c);
            ref RectF cb = ref _scene.Bounds(c);

            float fMain = finalMain[idx];
            FlexAlign align = cli.AlignSelf == FlexAlign.Auto ? li.AlignItems : cli.AlignSelf;
            float crossMargin = MarginCross(cli, row);
            float baseCross = row ? cb.H : cb.W;
            bool hasExplicitCross = !float.IsNaN(row ? cli.Height : cli.Width);
            float fCross = (align == FlexAlign.Stretch && !hasExplicitCross)
                ? ClampCross(cli, row, availCross - crossMargin)
                : baseCross;

            float crossFree = availCross - (fCross + crossMargin);
            float crossOff = align switch
            {
                FlexAlign.Center => crossFree / 2f,
                FlexAlign.End => crossFree,
                _ => 0f,   // Start / Stretch
            };

            float mainStart = cursor + MarginMainStart(cli, row);
            float crossStart = padCrossStart + crossOff + MarginCrossStart(cli, row);

            float cx = row ? mainStart : crossStart;
            float cy = row ? crossStart : mainStart;
            float cw = row ? fMain : fCross;
            float ch = row ? fCross : fMain;

            Arrange(c, cx, cy, cw, ch);
            cursor += MarginMain(cli, row) + fMain + li.Gap + between;
        }
    }

    private static (float lead, float between) Distribute(FlexJustify j, float leftover, int n) => j switch
    {
        FlexJustify.Center => (leftover / 2f, 0f),
        FlexJustify.End => (leftover, 0f),
        FlexJustify.SpaceBetween => (0f, n > 1 ? leftover / (n - 1) : 0f),
        FlexJustify.SpaceAround => (n > 0 ? leftover / n / 2f : 0f, n > 0 ? leftover / n : 0f),
        FlexJustify.SpaceEvenly => (leftover / (n + 1), leftover / (n + 1)),
        _ => (0f, 0f),   // Start
    };

    private static float MarginMain(in LayoutInput li, bool row) => row ? li.Margin.Horizontal : li.Margin.Vertical;
    private static float MarginCross(in LayoutInput li, bool row) => row ? li.Margin.Vertical : li.Margin.Horizontal;
    private static float MarginMainStart(in LayoutInput li, bool row) => row ? li.Margin.Left : li.Margin.Top;
    private static float MarginCrossStart(in LayoutInput li, bool row) => row ? li.Margin.Top : li.Margin.Left;
    private static float ClampMain(in LayoutInput li, bool row, float v) => row ? Clamp(v, li.MinW, li.MaxW) : Clamp(v, li.MinH, li.MaxH);
    private static float ClampCross(in LayoutInput li, bool row, float v) => row ? Clamp(v, li.MinH, li.MaxH) : Clamp(v, li.MinW, li.MaxW);
}
