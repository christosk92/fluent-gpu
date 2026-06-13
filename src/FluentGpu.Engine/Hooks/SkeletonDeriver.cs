using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Hooks;

/// <summary>
/// Derives a SHIMMER element tree from the author's ONE real element subtree (so skeleton-loading needs no second
/// hand-authored tree). A pure recursive switch over the closed Element hierarchy — runs ONCE per pending→loaded edge
/// (a reconcile-edge event, never a paint phase) and never reads a live/measured node or a bound Prop:
/// <list type="bullet">
/// <item><b>Container</b> (BoxEl/GridEl with children): COPY layout + chrome (fill/border/corners) verbatim, gate input
/// (<c>IsEnabled=false</c>), drop effects/state/transform, and recurse — so the skeleton sits in the IDENTICAL layout
/// slot (no content-jump on swap).</item>
/// <item><b>Leaf</b> (TextEl/ImageEl/childless box/polyline): replace with a shimmer bar sized from the leaf's DECLARED
/// statics — a bound dimension reads via <c>Prop.ValueOr(NaN)</c> (NOT the inert 0), Grow is preserved (so a Grow title
/// bar fills like the title), TextEl height ≈ Size·ratio with pill corners.</item>
/// </list>
/// Honours per-node <c>SkeletonOverride</c> / <c>SkeletonMode.Off</c>. The pulse + the swap reveal are wired by the
/// reconciler (the existing recipes); this only builds Elements.
/// </summary>
internal static class SkeletonDeriver
{
    public static Element Derive(Element real, in SkeletonStyle s)
    {
        if (real.SkeletonOverride is { } custom) return custom;            // bespoke shimmer for this subtree
        if (real.SkeletonMode == SkeletonMode.Off) return Spacer(real, s); // keep the slot, no shimmer

        switch (real)
        {
            case BoxEl b when b.Children is { Length: > 0 }:
            {
                // Container: keep layout + chrome, gate interactivity, neutralize effects/state/transform, recurse.
                var kids = new Element[b.Children.Length];
                for (int i = 0; i < kids.Length; i++) kids[i] = Derive(b.Children[i], s);
                return b with
                {
                    Children = kids,
                    IsEnabled = false, HitTestVisible = false, Focusable = false, TabStop = false,
                    Animate = default, Blur = 0f, Arc = null, Acrylic = null, Shadow = null,
                    OnClick = null, OnPointerWheel = null, OnHoverMove = null, OnPointerExit = null, Cursor = null, CanDrag = false,
                    HoverFill = default, PressedFill = default, HoverOpacity = float.NaN, PressedOpacity = float.NaN,
                    OffsetX = 0f, OffsetY = 0f, ScaleX = 1f, ScaleY = 1f, Rotation = 0f,
                };
            }
            case BoxEl b:
                // Childless box (avatar/chip/cover/swatch): a shimmer bar of its declared size + corners + grow.
                return Bar(s, b.Width.ValueOr(float.NaN), b.Height.ValueOr(float.NaN), b.Grow, b.Corners, b.Margin, b.AlignSelf);

            case TextEl t:
            {
                float h = BarHeight(t.Size, s);
                return Bar(s, t.Width, h, t.Grow, Radii.Circle(h), t.Margin, t.AlignSelf);
            }

            case ImageEl img:
                return Bar(s, img.Width, img.Height, 0f, img.Corners, img.Margin, img.AlignSelf);

            case GridEl g when g.Children is { Length: > 0 }:
            {
                var gk = new Element[g.Children.Length];
                for (int i = 0; i < gk.Length; i++) gk[i] = Derive(g.Children[i], s);
                return g with { Children = gk };
            }

            case PolylineStrokeEl p:
                return Bar(s, p.Width, p.Height, p.Grow, default, default, FlexAlign.Auto);

            case ScrollEl sc:
                return sc with { Content = Derive(sc.Content, s) };

            case SpanTextEl span:
                return Bar(s, span.Width, BarHeight(span.Size <= 0f ? 14f : span.Size, s), span.Grow, default, span.Margin, span.AlignSelf);

            default:
                // Unknown / dynamic boundary (ComponentEl/Show/For/nested region): a single default bar placeholder.
                return Bar(s, float.NaN, BarHeight(14f, s), 1f, default, default, FlexAlign.Auto);
        }
    }

    private static float BarHeight(float textSize, in SkeletonStyle s) => MathF.Round((textSize <= 0f ? 14f : textSize) * s.TextRatio);

    private static Element Bar(in SkeletonStyle s, float w, float h, float grow, CornerRadius4 corners, Edges4 margin, FlexAlign alignSelf)
    {
        float bh = float.IsNaN(h) || h <= 0f ? 14f : h;
        bool noCorners = corners.Equals(default(CornerRadius4));
        return new BoxEl
        {
            Width = w,                                  // float → Prop<float>; NaN (auto) when the leaf had no declared width
            Height = bh,
            Grow = grow,                                // preserve the leaf's grow so a Grow title bar fills like the title
            Margin = margin,
            AlignSelf = alignSelf,
            Fill = s.BarColor,
            Corners = noCorners ? Radii.Circle(MathF.Min(bh, s.BarRadius * 2f)) : corners,
            IsEnabled = false, HitTestVisible = false,
        };
    }

    // SkeletonMode.Off: an empty box of the same declared size — keeps the layout slot without a shimmer bar.
    private static Element Spacer(Element real, in SkeletonStyle s)
    {
        (float w, float h, float grow, Edges4 m, FlexAlign a) = real switch
        {
            BoxEl b => (b.Width.ValueOr(float.NaN), b.Height.ValueOr(float.NaN), b.Grow, b.Margin, b.AlignSelf),
            TextEl t => (t.Width, BarHeight(t.Size, s), t.Grow, t.Margin, t.AlignSelf),
            ImageEl img => (img.Width, img.Height, 0f, img.Margin, img.AlignSelf),
            _ => (float.NaN, float.NaN, 0f, default, FlexAlign.Auto),
        };
        return new BoxEl { Width = w, Height = h, Grow = grow, Margin = m, AlignSelf = a, IsEnabled = false, HitTestVisible = false };
    }
}
