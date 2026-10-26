using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Reconciler;
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
        // P1 presence (layout.md §4.7): a STATICALLY collapsed real node (Visible unbound and false) has nothing to
        // shimmer — derive it to nothing (out of flow, like the real node), not a shimmer bar or even a same-size
        // spacer. A BOUND Visible is left alone here: its resolved value isn't known at derive time (a construction-
        // time, reconcile-edge pass — see the class doc), and the mounted node's own BindPresence effect governs
        // collapse for the shimmer the same as it would for the real subtree.
        if (!real.Visible.IsBound && !real.Visible.Value)
            return new BoxEl { Visible = false, IsEnabled = false, HitTestVisible = false };

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
                    ScrollEffects = [],
                };
            }
            case BoxEl b:
            {
                // A BOUND extent is declared too (a gutter that eases, a width a signal sizes): the derived box carries the same binding, never
                // reads it, so a bound-width spacer keeps its width instead of deriving 0 wide.
                bool wBound = b.Width.IsBound, hBound = b.Height.IsBound;
                float w = b.Width.ValueOr(float.NaN);
                float h = b.Height.ValueOr(float.NaN);
                if (!wBound && float.IsNaN(w) && !float.IsNaN(b.MinWidth)) w = b.MinWidth;
                if (!hBound && float.IsNaN(h) && !float.IsNaN(b.MinHeight)) h = b.MinHeight;

                // A statically invisible leaf is chrome reserved for another interaction state (the canonical case is
                // MediaCard's Grow=1 hover plate: Opacity=0, HoverOpacity=1). It participates in the real ZStack but is
                // not loading content. Turning it into a default-height bar leaves a floating stripe over every card.
                // Preserve its slot exactly like SkeletonMode.Off, without reading a bound opacity thunk.
                if (b.Opacity.ValueOr(1f) <= 0.001f)
                    return Spacer(b, s);

                // A box DECLARED zero on an axis (`Height = collapsed ? 0f : h`, a 0-wide strut) has no extent to stand in for: as a bar
                // its 0 read as "unset" and took the default 14, so a blank row the real page lays out at 0 grew a page-wide stripe and
                // the page settled up by 14 when the data landed. Keep its slot as declared, as SkeletonMode.Off does.
                if ((!wBound && w == 0f) || (!hBound && h == 0f))
                    return Spacer(b, s);

                // Empty conditional branches (`condition ? real : new BoxEl()`) and paint-only layers such as a hero
                // gradient are not content. They must remain zero/transparent; mapping them to the default 14px bar
                // creates phantom full-width rows, while a full-size gradient becomes a second image-sized slab.
                bool noDeclaredExtent = !wBound && !hBound && float.IsNaN(w) && float.IsNaN(h) && float.IsNaN(b.Basis) && b.Grow <= 0f;
                if (noDeclaredExtent || b.Gradient is not null || b.Acrylic is not null)
                    return EmptySpacer(b.Margin, b.AlignSelf);

                // AIR: an unpainted box that is not sized on BOTH axes by plain numbers (a strut with only a height, a flexible spacer, a bound-width
                // gutter) is space, not a placeholder: as a bar its open axis stretched across the column (a page-wide stripe the real page never
                // draws) and a bound axis read as unset. It keeps its slot as declared. A placeholder is a box that paints (a cover plate, a swatch)
                // or a slot sized on both axes (a title slot, a verb's circle), which stays a bar of its size.
                bool paints = b.Fill.IsBound || b.Fill.ValueOr(ColorF.Transparent).A > 0f || b.BorderWidth > 0f || b.BorderBrush is not null;
                if (!paints && (wBound || hBound || float.IsNaN(w) || float.IsNaN(h)))
                    return Spacer(b, s);

                // Childless box (avatar/chip/cover/swatch): a shimmer bar of its declared size + corners + grow.
                // Preserve MorphId so a connected-animation cover slot stays a Hero participant THROUGH the skeleton
                // (the art flies into the reserved slot immediately, without waiting for the async content to land).
                return Bar(s, w, h, b.Grow, b.Corners.ValueOr(default), b.Margin, b.AlignSelf, b.MorphId) with
                {
                    Width = wBound ? b.Width : w,
                    Height = hBound ? b.Height : (float.IsNaN(h) || h <= 0f ? 14f : h),
                };
            }

            case TextEl t:
            {
                // THE LINE BOX, NOT THE BAR. The bar is TextRatio of the em, but the loaded run takes its line box (the font's natural line,
                // or LineHeight per the run's stacking): a 14 run is a 10 bar in a 19 line, a 64 title a 46 bar in an 85 line. As a bare
                // bar every derived line came up short and the page grew on load by the difference, line by line. So the slot is the
                // run's own line box: ONE line of the same type, unseen (Opacity 0, never hit), with the bar centred over it.
                // The slot shrinks as its text would (the text's own Shrink and MinWidth): an estimated width wider than the line's room
                // gives way like the run it stands for, instead of running past its column and the window's edge.
                float h = BarHeight(t.Size, s);
                BoxEl bar = Bar(s, float.NaN, h, 0f, Radii.Circle(h), default, FlexAlign.Center) with { JustifySelf = FlexAlign.Stretch };
                return new BoxEl
                {
                    ZStack = true,
                    Width = TextBarWidth(t), Grow = t.Grow, Shrink = t.Shrink, MinWidth = t.MinWidth, MaxWidth = t.MaxWidth,
                    Margin = t.Margin, AlignSelf = t.AlignSelf,
                    IsEnabled = false, HitTestVisible = false,
                    Children =
                    [
                        new BoxEl
                        {
                            Opacity = 0f, HitTestVisible = false, IsEnabled = false, JustifySelf = FlexAlign.Start, MinWidth = 0f,
                            Children = [LineProbe(t)],
                        },
                        bar,
                    ],
                };
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

            case PathEl pe:
                return Bar(s, pe.Width, pe.Height, pe.Grow, default, default, FlexAlign.Auto);

            case SeriesEl se:
                return Bar(s, se.Width, se.Height, se.Grow, default, default, FlexAlign.Auto);

            case SpriteFieldEl sf:
                return Bar(s, sf.Width, sf.Height, sf.Grow, default, default, FlexAlign.Auto);

            case ScrollEl sc:
                // The shimmer viewport is a stand-in, not the content's identity. A copied ScrollKey would restore the
                // remembered offset into the short shimmer (clamped to ITS extent), and the shimmer's unmount on the Ready
                // swap would save that clamped offset back over the remembered one before the real viewport restores it.
                // A copied Handle would be authored on two live viewports (the exiting shimmer and the real one) that the
                // host rebinds back and forth, and OnRealized would hand a composing control the shimmer node.
                return sc with { Content = Derive(sc.Content, s), ScrollKey = null, Handle = null, OnRealized = null };

            case SkelRegionEl region:
                // A content(seed) subtree may itself contain an async region (Wavee's detail track list is the
                // canonical case). Derive through that boundary instead of collapsing the nested region to one bar.
                // An explicit source is still the nested region's real row/template source; otherwise Content() reads
                // that region's pending seed, exactly as its own reconciler edge would.
                return Derive(region.ShimmerSource?.Invoke() ?? region.Content(), s);

            case VirtualListEl v when v.RowBind is null && v.ItemCount > 0:
            {
                // A virtual viewport has no authored Children for the generic recursion above, but its RenderItem is
                // still the ONE real row source. Materialize only a small pending window and derive those real rows;
                // never expand the whole collection. This keeps content(seed) representative for Home-style
                // heterogeneous measured lists without authoring a second skeleton tree or defeating virtualization.
                int count = Math.Min(v.ItemCount, 8);
                var rows = new Element[count];
                for (int i = 0; i < count; i++) rows[i] = Derive(v.RenderItem(i), s);
                return new BoxEl
                {
                    Direction = v.Horizontal ? (byte)0 : (byte)1,
                    Children = rows,
                    Width = v.Width, Height = v.Height,
                    MinWidth = v.MinWidth, MinHeight = v.MinHeight,
                    MaxWidth = v.MaxWidth, MaxHeight = v.MaxHeight,
                    Grow = v.Grow, Shrink = v.Shrink, Basis = v.Basis,
                    AlignSelf = v.AlignSelf, Margin = v.Margin,
                    ClipToBounds = true,
                    IsEnabled = false, HitTestVisible = false,
                };
            }

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

    /// <summary>One line of <paramref name="t"/>'s type (its font, size, weight, line height and stacking, line bounds), unseen: it measures
    /// the line box the loaded run's first line takes. One glyph, never the run's own words (a seed string could wrap or be empty).</summary>
    private static TextEl LineProbe(TextEl t) => t with
    {
        Text = "M", MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.None, MinSize = 0f,
        Width = float.NaN, MinWidth = 0f, MaxWidth = float.NaN, Grow = 0f, Shrink = 0f, Margin = default, AlignSelf = FlexAlign.Auto,
        Key = null,
    };

    private static float BarHeight(float textSize, in SkeletonStyle s) => MathF.Round((textSize <= 0f ? 14f : textSize) * s.TextRatio);

    private static float TextBarWidth(TextEl text)
    {
        if (!float.IsNaN(text.Width)) return text.Width;
        if (text.Grow > 0f) return float.NaN;

        float size = text.Size <= 0f ? 14f : text.Size;
        if (string.Equals(text.FontFamily, Tok.Definition.Fonts.Icon, StringComparison.OrdinalIgnoreCase))
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

    private static BoxEl Bar(in SkeletonStyle s, float w, float h, float grow, CornerRadius4 corners, Edges4 margin,
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

    // SkeletonMode.Off: an empty box of the same declared size — keeps the layout slot without a shimmer bar. A box's extents are copied as they
    // are declared, a binding included (the spacer follows the same signal), with its flex sizing (grow, shrink, basis, min / max).
    private static Element Spacer(Element real, in SkeletonStyle s)
    {
        if (real is BoxEl box)
            return new BoxEl
            {
                Width = box.Width, Height = box.Height, Grow = box.Grow, Shrink = box.Shrink, Basis = box.Basis,
                MinWidth = box.MinWidth, MinHeight = box.MinHeight, MaxWidth = box.MaxWidth, MaxHeight = box.MaxHeight,
                Margin = box.Margin, AlignSelf = box.AlignSelf, IsEnabled = false, HitTestVisible = false,
            };
        (float w, float h, float grow, Edges4 m, FlexAlign a) = real switch
        {
            TextEl t => (t.Width, BarHeight(t.Size, s), t.Grow, t.Margin, t.AlignSelf),
            ImageEl img => (img.Width, img.Height, 0f, img.Margin, img.AlignSelf),
            _ => (float.NaN, float.NaN, 0f, default, FlexAlign.Auto),
        };
        return new BoxEl { Width = w, Height = h, Grow = grow, Margin = m, AlignSelf = a, IsEnabled = false, HitTestVisible = false };
    }
}
