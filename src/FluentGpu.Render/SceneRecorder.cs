using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Render;

/// <summary>The focus-visual brushes (WinUI FocusStrokeColorOuter/Inner + thickness). Passed into the recorder by the
/// host (which reads the theme); default = disabled, so the headless/test paths can opt in.</summary>
public readonly record struct FocusVisualStyle(ColorF Outer, ColorF Inner, float Thickness)
{
    public bool Enabled => Outer.A > 0f || Inner.A > 0f;
}

/// <summary>
/// Phase 8 (record): walks the retained SceneStore and emits the DrawList. Composites like a browser — each node's
/// geometry is emitted in LOCAL space with a world transform (parent ∘ translate ∘ LocalTransform, scale/rotate about
/// the node's center) and a cumulative opacity, so transform/opacity animate without relayout or re-record of content.
/// Hover/press cross-fade via the eased <see cref="InteractionAnim"/> row; a focused node gets a dual-stroke focus ring.
/// </summary>
public static class SceneRecorder
{
    public static void Record(SceneStore scene, DrawList dl, ImageCache? images = null, in FocusVisualStyle focus = default,
                              ColorF scrollThumb = default, ColorF scrollTrack = default)
    {
        dl.Reset();
        if (scene.Root.IsNull) return;
        Walk(scene, dl, images, scene.Root, Affine2D.Identity, 1f, 0, RectF.Infinite, in focus, scrollThumb, scrollTrack);
    }

    private static void Walk(SceneStore scene, DrawList dl, ImageCache? images, NodeHandle node, Affine2D parentWorld, float parentOpacity,
                             int depth, RectF clip, in FocusVisualStyle focus, ColorF scrollThumb, ColorF scrollTrack)
    {
        NodeFlags flags = scene.Flags(node);
        if ((flags & NodeFlags.Visible) == 0) return;   // invisible subtree contributes nothing

        ref RectF b = ref scene.Bounds(node);
        ref NodePaint p = ref scene.Paint(node);

        // node-local → device: parent ∘ translate(node pos) ∘ (local transform about the node centre)
        Affine2D world = parentWorld.Multiply(Affine2D.Translation(b.X, b.Y));
        if (!p.LocalTransform.IsIdentity)
        {
            float cx = b.W * 0.5f, cy = b.H * 0.5f;
            world = world.Multiply(Affine2D.Translation(cx, cy)).Multiply(p.LocalTransform).Multiply(Affine2D.Translation(-cx, -cy));
        }
        // Interaction-driven composited scale (thumb hover-grow): scale about the node centre by the eased hover/press.
        // The progress comes from the node's own row if it is interactive, else from the nearest interactive ancestor
        // (a slider/scrollbar thumb is non-interactive — drag stays on the track — but grows when the control is used).
        if (scene.TryGetInteract(node, out var iaScale) && (iaScale.HoverScale != 1f || iaScale.PressScale != 1f))
        {
            int interactive = InteractionInfo.ClickBit | InteractionInfo.PointerBit;
            float useH = iaScale.HoverT, useP = iaScale.PressT;
            if ((scene.Interaction(node).HandlerMask & interactive) == 0)
                for (var anc = scene.Parent(node); !anc.IsNull; anc = scene.Parent(anc))
                    if ((scene.Interaction(anc).HandlerMask & interactive) != 0 && scene.TryGetInteract(anc, out var pr)) { useH = pr.HoverT; useP = pr.PressT; break; }
            float hs = 1f + (iaScale.HoverScale - 1f) * useH;
            float isc = hs + (iaScale.PressScale - hs) * useP;
            if (MathF.Abs(isc - 1f) > 0.0008f)
            {
                float cx = b.W * 0.5f, cy = b.H * 0.5f;
                world = world.Multiply(Affine2D.Translation(cx, cy)).Multiply(Affine2D.Scale(isc, isc)).Multiply(Affine2D.Translation(-cx, -cy));
            }
        }
        float opacity = parentOpacity * p.Opacity;

        ulong key = (ulong)depth << 32;   // painter order ~ depth for the slice
        var local = new RectF(0f, 0f, b.W, b.H);
        var deviceBounds = world.TransformBounds(local);

        // A clipping node (scroll viewport / virtual list) intersects the active clip and pushes the scissor.
        bool pushedClip = false;
        RectF childClip = clip;
        if ((flags & NodeFlags.ClipsToBounds) != 0)
        {
            childClip = clip.Intersect(deviceBounds);
            dl.PushClip(childClip, key);
            pushedClip = true;
        }

        // ── shadow: drawn beneath the fill (even for a transparent container), if a shadow row exists ──
        if (scene.TryGetShadow(node, out var sh) && !sh.IsNone && deviceBounds.Overlaps(clip))
            dl.Shadow(local, p.Corners, sh.Color, sh.OffsetX, sh.OffsetY, sh.Blur, sh.Spread, world, opacity, key);

        // ── acrylic: snapshot + blur the backdrop drawn so far, composite the frosted surface, then content draws on top ──
        bool isAcrylic = scene.TryGetAcrylic(node, out var ac) && deviceBounds.Overlaps(clip);
        if (isAcrylic)
            dl.PushLayer(deviceBounds, p.Corners, ac.Tint, ac.TintOpacity, ac.BlurSigma, ac.NoiseOpacity, ac.LuminosityOpacity, key);

        // Cull this node's OWN draw if it falls entirely outside the active clip (offscreen virtualized/overscan rows).
        bool drawSelf = p.VisualKind != VisualKind.None && deviceBounds.Overlaps(clip);
        if (drawSelf)
        switch (p.VisualKind)
        {
            case VisualKind.Box when p.Fill.A > 0f || p.HoverFill.A > 0f || p.PressedFill.A > 0f || p.BorderWidth > 0f:
            {
                ResolveSurface(scene, node, flags, in p, out ColorF fill, out ColorF border);
                bool hasGradFill = scene.TryGetGradient(node, out var g) && g.Stops is { Length: > 0 };
                GradientSpec bb = default;
                bool hasGradBorder = p.BorderWidth > 0f && scene.TryGetBorderBrush(node, out bb) && bb.Stops is { Length: > 0 };

                // ── fill ──  gradient fill supersedes the solid fill; a flat solid border uses the inset "donut".
                if (hasGradFill)
                {
                    EmitGradient(dl, local, p.Corners, in g, world, opacity, key);
                }
                else if (p.BorderWidth > 0f && border.A > 0f && !hasGradBorder)
                {
                    dl.FillRoundRect(local, p.Corners, border, world, opacity, key);     // flat border ring (local space)
                    float bw = p.BorderWidth;
                    var inner = new RectF(bw, bw, MathF.Max(0f, b.W - 2 * bw), MathF.Max(0f, b.H - 2 * bw));
                    var ic = new CornerRadius4(
                        MathF.Max(0f, p.Corners.TopLeft - bw), MathF.Max(0f, p.Corners.TopRight - bw),
                        MathF.Max(0f, p.Corners.BottomRight - bw), MathF.Max(0f, p.Corners.BottomLeft - bw));
                    if (fill.A > 0f) dl.FillRoundRect(inner, ic, fill, world, opacity, key);
                }
                else if (fill.A > 0f)
                {
                    dl.FillRoundRect(local, p.Corners, fill, world, opacity, key);
                }

                // ── border ring (SDF band, drawn over the fill edge — inside the bounds, WinUI-style) ──
                if (hasGradBorder)
                    EmitGradientBorderRing(dl, b, p.Corners, p.BorderWidth, in bb, world, opacity, key);
                else if (hasGradFill && p.BorderWidth > 0f && border.A > 0f)
                    EmitBorderRing(dl, local, b, p.Corners, p.BorderWidth, border, world, opacity, key);
                break;
            }
            case VisualKind.Text when !p.Text.IsEmpty:
            {
                ref var li = ref scene.Layout(node);
                dl.DrawGlyphRun(local, p.TextColor, p.Text, li.TextStyle.FontFamily, li.TextStyle.SizeDip, li.TextStyle.Bold ? 1 : 0,
                    (int)li.TextStyle.Wrap, (int)li.TextStyle.Trim, li.TextStyle.MaxLines, world, opacity, key);
                break;
            }
            case VisualKind.Image:
            {
                bool ready = images is not null && images.StateOf(new ImageHandle(p.ImageId)) == ImageState.Ready;
                dl.DrawImage(local, p.Corners, p.ImageId, ready, p.Fill, world, opacity, key);
                break;
            }
        }

        for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c))
            Walk(scene, dl, images, c, world, opacity, depth + 1, childClip, in focus, scrollThumb, scrollTrack);

        if (isAcrylic) dl.PopLayer(deviceBounds, key);

        // ── focus ring: keyboard focus only (FocusVisual), drawn last so it overlays children ──
        if (focus.Enabled && (flags & NodeFlags.FocusVisual) != 0 && deviceBounds.Overlaps(clip))
            EmitFocusRing(dl, b, p.Corners, world, opacity, in focus, key | 0x10);

        // ── auto-hiding scrollbar thumb (overlay; over content, within the viewport bounds) ──
        if (pushedClip) dl.PopClip(key);

        // Auto-hiding scrollbar overlay: draw after popping the viewport's content clip so the expanded gutter/thumb
        // are not chopped at the viewport edge, while still positioning them inside the viewport bounds.
        if (scrollThumb.A > 0f && deviceBounds.Overlaps(clip) && (flags & NodeFlags.Scrollable) != 0 &&
            scene.TryGetScroll(node, out var scb) && scb.FadeT > 0.01f)
            EmitScrollbar(dl, b, in scb, world, opacity, key | 0x20, scrollThumb, scrollTrack);
    }

    /// <summary>Resolve the surface fill/border for this frame: eased hover/press if an interaction row exists,
    /// else the instantaneous flag behaviour (first frame / no animator).</summary>
    private static void ResolveSurface(SceneStore scene, NodeHandle node, NodeFlags flags, in NodePaint p, out ColorF fill, out ColorF border)
    {
        fill = p.Fill; border = p.BorderColor;
        if (scene.TryGetInteract(node, out var ia) && (ia.HoverT > 0.001f || ia.PressT > 0.001f))
        {
            ColorF hov = p.HoverFill.A > 0f ? p.HoverFill : Lighten(p.Fill, 0.08f);
            ColorF prs = p.PressedFill.A > 0f ? p.PressedFill : Darken(p.Fill, 0.12f);
            // Cross-fade in LINEAR light (color canon: linear-blend / premultiplied) — not straight sRGB.
            fill = ColorF.LerpLinear(p.Fill, hov, ia.HoverT);
            fill = ColorF.LerpLinear(fill, prs, ia.PressT);
            ColorF hb = Lighten(p.BorderColor, 0.08f), pb = Darken(p.BorderColor, 0.12f);
            border = ColorF.LerpLinear(p.BorderColor, hb, ia.HoverT);
            border = ColorF.LerpLinear(border, pb, ia.PressT);
        }
        else if ((flags & NodeFlags.Pressed) != 0)
        {
            fill = p.PressedFill.A > 0f ? p.PressedFill : Darken(fill, 0.12f); border = Darken(border, 0.12f);
        }
        else if ((flags & NodeFlags.Hovered) != 0)
        {
            fill = p.HoverFill.A > 0f ? p.HoverFill : Lighten(fill, 0.08f); border = Lighten(border, 0.08f);
        }
    }

    private static void EmitBorderRing(DrawList dl, in RectF local, in RectF b, in CornerRadius4 corners, float bw, in ColorF border, in Affine2D world, float opacity, ulong key)
        => dl.StrokeRoundRect(new RectF(bw * 0.5f, bw * 0.5f, MathF.Max(0f, b.W - bw), MathF.Max(0f, b.H - bw)), corners, border, bw, world, opacity, key);

    private static void EmitGradient(DrawList dl, in RectF local, in CornerRadius4 corners, in GradientSpec g, in Affine2D world, float opacity, ulong key)
    {
        // axis endpoints in local 0..1 from the angle (0 = →, 90 = ↓); radial ignores the axis.
        float rad = g.AngleDeg * (MathF.PI / 180f);
        float dx = MathF.Cos(rad), dy = MathF.Sin(rad);
        var start = new Point2(0.5f - dx * 0.5f, 0.5f - dy * 0.5f);
        var end = new Point2(0.5f + dx * 0.5f, 0.5f + dy * 0.5f);
        var s = g.Stops;
        int n = Math.Min(s.Length, GradientSpec.MaxStops);
        ColorF c0 = s[0].Color, c1 = n > 1 ? s[1].Color : c0, c2 = n > 2 ? s[2].Color : c1, c3 = n > 3 ? s[3].Color : c2;
        float o0 = s[0].Offset, o1 = n > 1 ? s[1].Offset : 1f, o2 = n > 2 ? s[2].Offset : 1f, o3 = n > 3 ? s[3].Offset : 1f;
        dl.GradientRect(new DrawGradientRectCmd(local, corners, start, end, (int)g.Shape, n, c0, c1, c2, c3, o0, o1, o2, o3, world, opacity), key);
    }

    /// <summary>A gradient-tinted border ring: the gradient PS sampled along the local axis, drawn as an SDF band of
    /// width <paramref name="bw"/> centered on a rect inset by bw/2 (so the stroke sits inside the bounds, WinUI-style).
    /// The vertical axis spans the whole control, matching WinUI's ControlElevationBorderBrush.</summary>
    private static void EmitGradientBorderRing(DrawList dl, in RectF b, in CornerRadius4 corners, float bw, in GradientSpec g, in Affine2D world, float opacity, ulong key)
    {
        float rad = g.AngleDeg * (MathF.PI / 180f);
        float dx = MathF.Cos(rad), dy = MathF.Sin(rad);
        var start = new Point2(0.5f - dx * 0.5f, 0.5f - dy * 0.5f);
        var end = new Point2(0.5f + dx * 0.5f, 0.5f + dy * 0.5f);
        var s = g.Stops;
        int n = Math.Min(s.Length, GradientSpec.MaxStops);
        ColorF c0 = s[0].Color, c1 = n > 1 ? s[1].Color : c0, c2 = n > 2 ? s[2].Color : c1, c3 = n > 3 ? s[3].Color : c2;
        float o0 = s[0].Offset, o1 = n > 1 ? s[1].Offset : 1f, o2 = n > 2 ? s[2].Offset : 1f, o3 = n > 3 ? s[3].Offset : 1f;
        var ring = new RectF(bw * 0.5f, bw * 0.5f, MathF.Max(0f, b.W - bw), MathF.Max(0f, b.H - bw));
        dl.GradientStroke(new DrawGradientStrokeCmd(ring, corners, start, end, (int)g.Shape, n, c0, c1, c2, c3, o0, o1, o2, o3, bw, world, opacity), key);
    }

    /// <summary>WinUI dual focus visual: a 1px inner secondary stroke at the control edge + a 2px outer primary stroke
    /// just outside it. Real SDF strokes, so they're correct over any fill/background.</summary>
    private static void EmitFocusRing(DrawList dl, in RectF b, in CornerRadius4 corners, in Affine2D world, float opacity, in FocusVisualStyle f, ulong key)
    {
        if (f.Inner.A > 0f)
            dl.StrokeRoundRect(new RectF(0f, 0f, b.W, b.H), corners, f.Inner, 1f, world, opacity, key);
        if (f.Outer.A > 0f)
        {
            float g = MathF.Max(1f, f.Thickness);
            var grown = new RectF(-g, -g, b.W + 2f * g, b.H + 2f * g);
            var gc = new CornerRadius4(corners.TopLeft + g, corners.TopRight + g, corners.BottomRight + g, corners.BottomLeft + g);
            dl.StrokeRoundRect(grown, gc, f.Outer, f.Thickness, world, opacity, key);
        }
    }

    /// <summary>An auto-hiding scrollbar thumb sized from the viewport's content/offset, faded by <c>FadeT</c>, expanded on lane hover.</summary>
    private static void EmitScrollbar(DrawList dl, in RectF b, in ScrollState sc, in Affine2D world, float opacity, ulong key, ColorF thumb, ColorF track)
    {
        bool horizontal = sc.Orientation == 1;
        float content = horizontal ? sc.ContentW : sc.ContentH;
        float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        {
            if (content <= viewport + 0.5f) return;

            const float bar = 12f;          // ScrollBarSize
            const float collapsed = 2f;     // panning indicator width/height
            const float thumbOffset = 2f;   // ScrollBarThumbOffset: cross-axis inset in the collapsed state
            const float minExpanded = 30f;  // ScrollBarVerticalThumbMinHeight
            const float minCollapsed = 32f; // VerticalPanningThumb.MinHeight
            const float radius = 3f;        // ScrollBarCornerRadius

            float fade = Math.Clamp(sc.FadeT, 0f, 1f);
            float expand = Math.Clamp(sc.ExpandT, 0f, 1f);
            if (fade <= 0.01f) return;

            float axis = horizontal ? b.W : b.H;
            float cross = horizontal ? b.H : b.W;
            float button = bar * expand;
            float trackStart = button;
            float trackLen = MathF.Max(1f, axis - 2f * button);
            float frac = Math.Clamp(viewport / content, 0.08f, 1f);
            float minThumb = minCollapsed + (minExpanded - minCollapsed) * expand;
            float thumbLen = MathF.Min(trackLen, MathF.Max(minThumb, frac * trackLen));
            float travel = MathF.Max(1f, trackLen - thumbLen);
            float off = horizontal ? sc.OffsetX : sc.OffsetY;
            float pos = trackStart + Math.Clamp(off / MathF.Max(content - viewport, 1f), 0f, 1f) * travel;

            var thumbCol = thumb with { A = thumb.A * fade };
            var fallbackTrack = thumb with { A = 0.16f };
            var trackBase = track.A > 0f ? track : fallbackTrack;
            var trackCol = trackBase with { A = trackBase.A * fade * expand };
            if (trackCol.A > 0.01f)
            {
                RectF gutter = horizontal
                    ? new RectF(0f, cross - bar, axis, bar)
                    : new RectF(cross - bar, 0f, bar, axis);
                dl.FillRoundRect(gutter, CornerRadius4.All(radius), trackCol, world, opacity, key);
            }

            float thick = collapsed + (bar - collapsed) * expand;
            float collapsedCrossPos = cross - collapsed - thumbOffset;
            float expandedCrossPos = cross - bar;
            float crossPos = collapsedCrossPos + (expandedCrossPos - collapsedCrossPos) * expand;
            RectF thumbRect = horizontal
                ? new RectF(pos, crossPos, thumbLen, thick)
                : new RectF(crossPos, pos, thick, thumbLen);
            dl.FillRoundRect(thumbRect, CornerRadius4.All(radius), thumbCol, world, opacity, key | 0x1);

            float arrowOpacity = fade * expand;
            if (arrowOpacity > 0.04f)
            {
                var arrow = thumb with { A = thumb.A * arrowOpacity };
                if (horizontal)
                {
                    EmitChevron(dl, new Point2(bar * 0.5f, cross - bar * 0.5f), horizontal: true, positive: false, arrow, world, opacity, key | 0x2);
                    EmitChevron(dl, new Point2(axis - bar * 0.5f, cross - bar * 0.5f), horizontal: true, positive: true, arrow, world, opacity, key | 0x3);
                }
                else
                {
                    EmitChevron(dl, new Point2(cross - bar * 0.5f, bar * 0.5f), horizontal: false, positive: false, arrow, world, opacity, key | 0x2);
                    EmitChevron(dl, new Point2(cross - bar * 0.5f, axis - bar * 0.5f), horizontal: false, positive: true, arrow, world, opacity, key | 0x3);
                }
            }

            return;
        }
    }

    private static void EmitChevron(DrawList dl, Point2 c, bool horizontal, bool positive, ColorF color, in Affine2D world, float opacity, ulong key)
    {
        const float s = 3.0f;
        Point2 tip, a, b;
        if (horizontal)
        {
            tip = new Point2(c.X + (positive ? s : -s) * 0.55f, c.Y);
            a = new Point2(c.X - (positive ? s : -s) * 0.45f, c.Y - s);
            b = new Point2(c.X - (positive ? s : -s) * 0.45f, c.Y + s);
        }
        else
        {
            tip = new Point2(c.X, c.Y + (positive ? s : -s) * 0.55f);
            a = new Point2(c.X - s, c.Y - (positive ? s : -s) * 0.45f);
            b = new Point2(c.X + s, c.Y - (positive ? s : -s) * 0.45f);
        }

        EmitSegment(dl, a, tip, color, world, opacity, key);
        EmitSegment(dl, tip, b, color, world, opacity, key);
    }

    private static void EmitSegment(DrawList dl, Point2 a, Point2 b, ColorF color, in Affine2D world, float opacity, ulong key)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= 0.1f) return;

        const float thickness = 1.15f;
        var center = new Point2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
        var line = new RectF(-len * 0.5f, -thickness * 0.5f, len, thickness);
        var transform = world.Multiply(Affine2D.Translation(center.X, center.Y))
                             .Multiply(Affine2D.Rotation(MathF.Atan2(dy, dx)));
        dl.FillRoundRect(line, CornerRadius4.All(thickness * 0.5f), color, transform, opacity, key);
    }

    private static ColorF Lighten(ColorF c, float t) => new(c.R + (1f - c.R) * t, c.G + (1f - c.G) * t, c.B + (1f - c.B) * t, c.A);
    private static ColorF Darken(ColorF c, float t) => new(c.R * (1f - t), c.G * (1f - t), c.B * (1f - t), c.A);
}
