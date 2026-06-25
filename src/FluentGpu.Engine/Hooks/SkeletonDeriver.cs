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
/// <item><b>Container</b> (BoxEl/GridEl with children): COPY layout only, gate input, remove chrome/effects, and recurse
/// — so cards/buttons/hero scrims do not become giant tinted placeholder slabs.</item>
/// <item><b>Leaf</b> (TextEl/ImageEl/childless box/polyline): replace with a shimmer bar sized from the leaf's DECLARED
/// statics. Auto-width text gets a finite width estimated from its authored text/font instead of an auto-width empty box
/// (which flex would stretch across the parent). Empty/decorative boxes stay empty.</item>
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
                // A container is layout, not skeleton content. Preserve geometry and recurse into semantic leaves, but
                // remove authored chrome. Copying a card fill, hero scrim, gradient, or border turns the whole container
                // into a giant pulsing slab and obscures the image/text placeholders inside it.
                var kids = new Element[b.Children.Length];
                for (int i = 0; i < kids.Length; i++) kids[i] = Derive(b.Children[i], s);
                return b with
                {
                    Children = kids,
                    IsEnabled = false, HitTestVisible = false, Focusable = false, TabStop = false,
                    Animate = default, Blur = 0f, Arc = null, Acrylic = null, Shadow = null,
                    Fill = ColorF.Transparent,
                    Gradient = null, HoverGradient = null, PressedGradient = null,
                    BorderWidth = 0f, BorderColor = default, HoverBorderColor = default, PressedBorderColor = default,
                    BorderBrush = null, HoverBorderBrush = null, PressedBorderBrush = null,
                    EdgeFade = null, OpacityGroup = false,
                    OnClick = null, OnPointerWheel = null, OnHoverMove = null, OnPointerExit = null, Cursor = null, CanDrag = false,
                    HoverFill = default, PressedFill = default, HoverOpacity = float.NaN, PressedOpacity = float.NaN,
                    OffsetX = 0f, OffsetY = 0f, ScaleX = 1f, ScaleY = 1f, Rotation = 0f,
                    ScrollBinds = [],
                };
            }
            case BoxEl b:
            {
                float w = b.Width.ValueOr(float.NaN);
                float h = b.Height.ValueOr(float.NaN);
                if (float.IsNaN(w) && !float.IsNaN(b.MinWidth)) w = b.MinWidth;
                if (float.IsNaN(h) && !float.IsNaN(b.MinHeight)) h = b.MinHeight;

                // Empty conditional branches (`condition ? real : new BoxEl()`) and paint-only layers such as a hero
                // gradient are not content. They must remain zero/transparent; mapping them to the default 14px bar
                // creates phantom full-width rows, while a full-size gradient becomes a second image-sized slab.
                bool noDeclaredExtent = float.IsNaN(w) && float.IsNaN(h) && float.IsNaN(b.Basis) && b.Grow <= 0f;
                if (noDeclaredExtent || b.Gradient is not null || b.Acrylic is not null)
                    return EmptySpacer(b.Margin, b.AlignSelf);

                // Childless box (avatar/chip/cover/swatch): a shimmer bar of its declared size + corners + grow.
                // Preserve MorphId so a connected-animation cover slot stays a Hero participant THROUGH the skeleton
                // (the art flies into the reserved slot immediately, without waiting for the async content to land).
                return Bar(s, w, h, b.Grow, b.Corners, b.Margin, b.AlignSelf, b.MorphId);
            }

            case TextEl t:
            {
                float h = BarHeight(t.Size, s);
                return Bar(s, TextBarWidth(t), h, t.Grow, Radii.Circle(h), t.Margin, t.AlignSelf);
            }

            case ImageEl img:
                // Keep the media leaf itself with an empty source. This preserves fluid geometry (especially
                // AspectRatio=1 card covers) that a BoxEl bar cannot express, while painting only the neutral placeholder.
                return img with
                {
                    Source = "",
                    Placeholder = MediaColor(s),
                    BlurHash = null,
                };

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
            {
                float size = span.Size <= 0f ? 14f : span.Size;
                float width = float.IsNaN(span.Width) ? MathF.Min(size * 18f, MathF.Max(size * 8f, 160f)) : span.Width;
                return Bar(s, width, BarHeight(size, s), span.Grow, default, span.Margin, span.AlignSelf);
            }

            case ComponentEl { DeriveRenderedOutput: true } dynamicProxy:
                // Preserve this pending-only component so it can measure/re-render normally. The reconciler derives each
                // rendered output with the current region style, which is required for live-width Responsive content.
                return dynamicProxy with { DerivedSkeletonStyle = s };

            case ComponentEl { SkeletonProxy: { } proxy }:
                // A size-reactive boundary that exposed its real shape: derive THAT (recursing into any nested boundaries
                // too) instead of one default bar — so a hero / shelf / band shimmers as its real layout, not a thin line.
                return Derive(proxy(), s);

            default:
                // Unknown dynamic boundaries must not claim all available width/height. Known reusable controls expose a
                // SkeletonProxy; a genuinely opaque boundary gets one modest placeholder instead of a page-wide stripe.
                return Bar(s, 160f, BarHeight(14f, s), 0f, default, default, FlexAlign.Auto);
        }
    }

    private static float BarHeight(float textSize, in SkeletonStyle s) => MathF.Round((textSize <= 0f ? 14f : textSize) * s.TextRatio);

    private static float TextBarWidth(TextEl text)
    {
        if (!float.IsNaN(text.Width)) return text.Width;
        if (text.Grow > 0f) return float.NaN;

        float size = text.Size <= 0f ? 14f : text.Size;
        if (string.Equals(text.FontFamily, Theme.IconFont, StringComparison.OrdinalIgnoreCase))
            return size;

        string value = text.Text.ValueOr("");
        int glyphs = 0;
        for (int i = 0; i < value.Length && glyphs < 24; i++)
            if (!char.IsWhiteSpace(value[i]) || (i > 0 && !char.IsWhiteSpace(value[i - 1]))) glyphs++;

        // Empty pending fields still need a useful, finite shape. Six average glyphs gives a compact title placeholder;
        // authored strings scale naturally but are capped so metadata/paragraph text does not become a full-width pill.
        if (glyphs == 0) glyphs = 6;
        float estimated = size * (0.56f * glyphs + 1.25f);
        float min = MathF.Max(size * 2.5f, 32f);
        float max = MathF.Max(min, size * 14f);
        if (!float.IsNaN(text.MaxWidth)) max = MathF.Min(max, text.MaxWidth);
        return Math.Clamp(estimated, min, max);
    }

    private static ColorF MediaColor(in SkeletonStyle s)
        => s.BarColor with { A = MathF.Max(0.018f, s.BarColor.A * 0.38f) };

    private static Element Bar(in SkeletonStyle s, float w, float h, float grow, CornerRadius4 corners, Edges4 margin,
        FlexAlign alignSelf, string? morphId = null, ColorF? fill = null)
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
            Fill = fill ?? s.BarColor,
            Corners = noCorners ? Radii.Circle(MathF.Min(bh, s.BarRadius * 2f)) : corners,
            IsEnabled = false, HitTestVisible = false,
            MorphId = morphId,                          // keep a connected-animation (Hero) tag through the skeleton
        };
    }

    private static Element EmptySpacer(Edges4 margin, FlexAlign alignSelf) => new BoxEl
    {
        Width = 0f,
        Height = 0f,
        Margin = margin,
        AlignSelf = alignSelf,
        IsEnabled = false,
        HitTestVisible = false,
    };

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
