using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Render;

namespace FluentGpu.Lottie;

/// <summary>One resolved paint (a <c>fl</c>/<c>gf</c> fill or an <c>st</c>/<c>gs</c> stroke) inside a Shape-layer's
/// shape tree — the geometry-side output <see cref="LottieGeometry"/> hands the compiler, which layers on the
/// LAYER-level time mapping (<c>U(t)</c>) to build the paint's actual <see cref="LottieNode"/> + <see cref="LottieTrack"/>s.
/// Everything already baked (<see cref="Geometry"/>/<see cref="SwitchGeometries"/>, <see cref="StaticOpacity"/>,
/// <see cref="StaticWidth"/>) is in FINAL node-local units — the running group affine is folded in at bake time, not
/// carried forward as a separate matrix.</summary>
public sealed class LottiePaintDescriptor
{
    public string Name = "";
    public bool IsStroke;

    /// <summary>The static case: one already-baked multi-contour geometry (every non-animated path/rect/ellipse
    /// shape this paint covers, unioned into one <see cref="PathData"/>).</summary>
    public PathData? Geometry;
    /// <summary>The animated-bezier "switch" case (§4): N discrete baked samples: exactly one is visible at a time,
    /// selected by a step (Hold) Opacity track the compiler builds from <see cref="SwitchFrames"/> — never a
    /// per-frame morph. Null when <see cref="Geometry"/> is set instead.</summary>
    public PathData[]? SwitchGeometries;
    /// <summary>Layer-local frame numbers parallel to <see cref="SwitchGeometries"/> (ascending) — the compiler maps
    /// each to normalized <c>U(t)</c> to build the step track.</summary>
    public float[]? SwitchFrames;

    public ColorF Color;
    /// <summary>Baked static opacity (paint <c>o</c> × every ancestor group's static <c>tr.o</c> down to this shape,
    /// as a fraction 0..1). When <see cref="OpacityTrack"/> is set this is its value AT t=0 (the rest-pose baseline
    /// — see <see cref="LottieNode"/> doc); the track always wins once wired.</summary>
    public float StaticOpacity = 1f;
    /// <summary>Set when the paint's OWN <c>o</c> is animated (ancestor group opacity animation is not modeled — none
    /// of the shipped assets animate a group's own <c>tr.o</c>).</summary>
    public AnimScalar? OpacityTrack;

    public FillRule Rule = FillRule.NonZero;
    public LineCap Cap = LineCap.Round;
    public LineJoin Join = LineJoin.Round;
    /// <summary>Baked static stroke width (rest-pose baseline — see <see cref="WidthTrack"/>).</summary>
    public float StaticWidth;
    /// <summary>Set when the stroke's <c>w</c> is animated — the compiler can't animate stroke width directly (no
    /// engine channel), so it builds an <c>Opacity = w(t)/w(1)</c> proxy track instead (§4) and counts an
    /// approximation.</summary>
    public AnimScalar? WidthTrack;

    public bool HasTrim;
    public float StaticTrimStart, StaticTrimEnd = 1f;
    public byte TrimMode;
    public AnimScalar? TrimStartTrack, TrimEndTrack;
}

/// <summary>
/// Walks a Shape-layer's <c>shapes[]</c> tree (§4 "Shapes") into a flat list of <see cref="LottiePaintDescriptor"/>s,
/// baking every STATIC group/shape transform into final geometry with a running <see cref="Affine2D"/> (no per-frame
/// matrix multiply — an animated group <c>tr</c> falls back to its value at t=0, an approximation; none of the
/// shipped assets need the general case). One instance is owned per <see cref="LottieCompiler.Compile"/> call (never
/// the shared static <c>PathDataParser</c> builder) so a compile can run entirely off the UI thread.
/// </summary>
public sealed class LottieGeometry
{
    private const float Kappa = 0.5523f;

    private readonly PathBuilder _builder = new();
    private readonly List<LottiePaintDescriptor> _paints = new(8);
    private readonly float _frameRate;

    public int Approximations { get; private set; }
    public int GeometryCount { get; private set; }

    public LottieGeometry(float frameRate) => _frameRate = frameRate <= 0f ? 60f : frameRate;

    /// <summary>Build every paint in <paramref name="shapes"/> (a layer's own top-level shape list). Paints come back
    /// in FIRST-LISTED-ON-TOP order (§4: "emit paints in reverse list order") — reverse the returned list to get
    /// bottom-to-top PAINTER order (index 0 painted first).</summary>
    public List<LottiePaintDescriptor> Build(LottieShape[] shapes)
    {
        _paints.Clear();
        var pendingStatic = new List<LottieBezier>(4);
        LottiePendingAnimated? pendingAnimated = null;
        WalkGroup(shapes, Affine2D.Identity, 1f, pendingStatic, ref pendingAnimated, trim: null);
        var result = new List<LottiePaintDescriptor>(_paints);
        result.Reverse();   // first-listed-on-top (array order) -> painter order (bottom to top)
        return result;
    }

    private readonly struct LottiePendingAnimated
    {
        public readonly AnimPath Anim;
        public readonly Affine2D M;
        public LottiePendingAnimated(AnimPath anim, Affine2D m) { Anim = anim; M = m; }
    }

    private void WalkGroup(LottieShape[] items, Affine2D m, float groupAlpha,
        List<LottieBezier> pendingStatic, ref LottiePendingAnimated? pendingAnimated, LottieShape? trim)
    {
        // A group's own `tm` (trim) applies to every paint this group produces, regardless of its list position
        // (the shipped assets always place it after the stroke it trims) — scan for one up front.
        foreach (LottieShape item in items)
            if (item.Type == LottieShapeType.Trim) { trim = item; break; }

        foreach (LottieShape item in items)
        {
            if (item.Hidden) continue;
            switch (item.Type)
            {
                case LottieShapeType.Group:
                {
                    Affine2D childM = m;
                    float childAlpha = groupAlpha;
                    if (item.GroupTransform is { } gt)
                    {
                        if (gt.IsAnyAnimated || gt.HasUnsupportedSkewOrAxis) Approximations++;   // static-bake fallback (§ file doc)
                        childM = m.Multiply(GroupAffine(gt));
                        childAlpha *= StaticOf(gt.Opacity) / 100f;
                    }
                    var subPending = new List<LottieBezier>(4);
                    LottiePendingAnimated? subAnimated = null;
                    WalkGroup(item.Items, childM, childAlpha, subPending, ref subAnimated, trim: null);
                    break;
                }
                case LottieShapeType.Path:
                    if (item.Path is { } ap)
                    {
                        if (ap.IsAnimated) pendingAnimated = new LottiePendingAnimated(ap, m);
                        else pendingStatic.Add(TransformBezier(ap.Static, m));
                    }
                    break;
                case LottieShapeType.Rect:
                    pendingStatic.Add(TransformBezier(BuildRect(item), m));
                    break;
                case LottieShapeType.Ellipse:
                    pendingStatic.Add(TransformBezier(BuildEllipse(item), m));
                    break;
                case LottieShapeType.Fill:
                case LottieShapeType.GradientFill:
                    EmitPaint(item, isStroke: false, m, groupAlpha, pendingStatic, pendingAnimated, trim);
                    break;
                case LottieShapeType.Stroke:
                case LottieShapeType.GradientStroke:
                    EmitPaint(item, isStroke: true, m, groupAlpha, pendingStatic, pendingAnimated, trim);
                    break;
                case LottieShapeType.Merge:
                case LottieShapeType.Repeater:
                    Approximations++;   // unmerged / unrepeated — its child shapes still render individually
                    break;
                case LottieShapeType.Trim:
                case LottieShapeType.Transform:
                case LottieShapeType.Unknown:
                default:
                    break;
            }
        }
    }

    private void EmitPaint(LottieShape paint, bool isStroke, Affine2D m, float groupAlpha,
        List<LottieBezier> pendingStatic, LottiePendingAnimated? pendingAnimated, LottieShape? trim)
    {
        var desc = new LottiePaintDescriptor
        {
            Name = paint.Name ?? (isStroke ? "Stroke" : "Fill"),
            IsStroke = isStroke,
            Rule = paint.Rule,
            Cap = paint.Cap,
            Join = paint.Join,
        };

        if (paint.Gradient is { } grad)
        {
            desc.Color = grad.MidStop;   // v1 approximation: solid mid-stop instead of a real gradient fill (§4)
            Approximations++;
        }
        else
        {
            desc.Color = paint.Color;
        }

        float paintOpacityStatic = StaticOf(paint.Opacity) / 100f;
        desc.StaticOpacity = Math.Clamp(groupAlpha * paintOpacityStatic, 0f, 1f);
        if (paint.Opacity.IsAnimated) desc.OpacityTrack = paint.Opacity;

        if (isStroke)
        {
            float widthStatic = paint.Width is { } w ? StaticOf(w) : 0f;
            desc.StaticWidth = widthStatic;
            if (paint.Width is { IsAnimated: true } wa) { desc.WidthTrack = wa; Approximations++; }   // stroke-width proxy (§4)

            if (trim is not null)
            {
                desc.HasTrim = true;
                desc.TrimMode = trim.TrimMode;
                float s = trim.TrimStart is { } ts ? StaticOf(ts) / 100f : 0f;
                float e = trim.TrimEnd is { } te ? StaticOf(te) / 100f : 1f;
                if (trim.TrimStart is { IsAnimated: true }) desc.TrimStartTrack = trim.TrimStart;
                if (trim.TrimEnd is { IsAnimated: true }) desc.TrimEndTrack = trim.TrimEnd;
                if (trim.TrimOffset is { } off)
                {
                    if (off.IsAnimated) Approximations++;   // animated offset ignored entirely (§4)
                    else if (MathF.Abs(off.Static) > 1e-4f)
                    {
                        float shift = off.Static / 100f;
                        s = Math.Clamp(s + shift, 0f, 1f);
                        e = Math.Clamp(e + shift, 0f, 1f);
                        Approximations++;   // static offset folded by a plain clamped add (§4)
                    }
                }
                desc.StaticTrimStart = s;
                desc.StaticTrimEnd = e;
            }
        }

        if (pendingAnimated is { } pa)
        {
            BuildSwitchGeometry(pa, desc);
        }
        else if (pendingStatic.Count > 0)
        {
            desc.Geometry = BakeMultiContour(pendingStatic, paint.Rule);
            GeometryCount++;
        }

        _paints.Add(desc);
    }

    // ── switch-group (animated bezier) baking ────────────────────────────────────────────────────────────────

    private void BuildSwitchGeometry(LottiePendingAnimated pending, LottiePaintDescriptor desc)
    {
        AnimPath anim = pending.Anim;
        var keys = anim.Keys!;   // IsAnimated already verified by the caller
        var frames = new List<float>(12);
        var shapes = new List<LottieBezier>(12);

        frames.Add(keys[0].T);
        shapes.Add(keys[0].Shape);

        for (int k = 0; k < keys.Length - 1 && frames.Count < 12; k++)
        {
            PathKey a = keys[k], b = keys[k + 1];
            float segMs = (b.T - a.T) / _frameRate * 1000f;
            int subSamples = Math.Clamp((int)MathF.Round(segMs / 33f), 1, 6);
            bool vertexMismatch = a.Shape.V.Length != b.Shape.V.Length;
            if (vertexMismatch) Approximations++;

            for (int s = 1; s <= subSamples && frames.Count < 12; s++)
            {
                float t = a.T + (b.T - a.T) * s / subSamples;
                LottieBezier sample = vertexMismatch ? a.Shape : LerpBezier(a.Shape, b.Shape, (float)s / subSamples);
                frames.Add(t);
                shapes.Add(sample);
            }
        }

        var geoms = new PathData[shapes.Count];
        for (int i = 0; i < shapes.Count; i++)
        {
            geoms[i] = BakeMultiContour([TransformBezier(shapes[i], pending.M)], desc.Rule);
            GeometryCount++;
        }
        desc.SwitchGeometries = geoms;
        desc.SwitchFrames = frames.ToArray();
    }

    private static LottieBezier LerpBezier(LottieBezier a, LottieBezier b, float t)
    {
        int n = a.V.Length;
        var r = new LottieBezier { Closed = a.Closed, V = new Point2[n], I = new Point2[n], O = new Point2[n] };
        for (int i = 0; i < n; i++)
        {
            r.V[i] = Lerp(a.V[i], b.V[i], t);
            r.I[i] = Lerp(a.I[i], b.I[i], t);
            r.O[i] = Lerp(a.O[i], b.O[i], t);
        }
        return r;
    }

    private static Point2 Lerp(Point2 a, Point2 b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    // ── geometry baking ───────────────────────────────────────────────────────────────────────────────────────

    private PathData BakeMultiContour(List<LottieBezier> shapes, FillRule rule)
    {
        _builder.Clear();
        foreach (LottieBezier b in shapes) EmitContour(b);
        return _builder.Finish(PathContentEpoch.Mint(), rule);
    }

    private void EmitContour(LottieBezier b)
    {
        int n = b.V.Length;
        if (n == 0) return;
        _builder.MoveTo(b.V[0].X, b.V[0].Y);
        int segments = b.Closed ? n : n - 1;
        for (int i = 0; i < segments; i++)
        {
            int j = (i + 1) % n;
            Point2 c1 = b.V[i] + b.O[i];
            Point2 c2 = b.V[j] + b.I[j];
            _builder.CubicTo(c1.X, c1.Y, c2.X, c2.Y, b.V[j].X, b.V[j].Y);
        }
        if (b.Closed) _builder.Close();
    }

    private static LottieBezier TransformBezier(LottieBezier b, Affine2D m)
    {
        if (m.IsIdentity) return b;
        int n = b.V.Length;
        var r = new LottieBezier { Closed = b.Closed, V = new Point2[n], I = new Point2[n], O = new Point2[n] };
        for (int i = 0; i < n; i++)
        {
            // I/O are OFFSETS relative to V (Lottie's own convention) — transform the absolute control point, then
            // re-derive the offset, so a rotation/scale in M rotates/scales the handle too (not just its origin).
            Point2 v = m.Transform(b.V[i]);
            Point2 ci = m.Transform(b.V[i] + b.I[i]);
            Point2 co = m.Transform(b.V[i] + b.O[i]);
            r.V[i] = v;
            r.I[i] = ci - v;
            r.O[i] = co - v;
        }
        return r;
    }

    private static Affine2D GroupAffine(LottieTransform t)
    {
        Point2 anchor = StaticOf(t.Anchor);
        Point2 position = StaticOf(t.Position);
        Point2 scale = StaticOf(t.Scale);
        float rotation = StaticOf(t.Rotation);
        // translate(position) * about(anchor){ rotate * scale } — matches the engine's own transform-order note (§4).
        return Affine2D.Translation(position.X, position.Y)
            .Multiply(Affine2D.Rotation(rotation * MathF.PI / 180f))
            .Multiply(Affine2D.Scale(scale.X / 100f, scale.Y / 100f))
            .Multiply(Affine2D.Translation(-anchor.X, -anchor.Y));
    }

    private static LottieBezier BuildRect(LottieShape rc)
    {
        Point2 c = rc.Center is { } ce ? StaticOf(ce) : default;
        Point2 sz = rc.Size is { } s ? StaticOf(s) : default;
        float hw = MathF.Abs(sz.X) / 2f, hh = MathF.Abs(sz.Y) / 2f;
        float r = rc.Radius is { } rad ? Math.Clamp(StaticOf(rad), 0f, MathF.Min(hw, hh)) : 0f;
        float k = r * Kappa;

        // Clockwise from top-right straight-edge start (AE convention close enough for fill/stroke parity here —
        // exact seam placement is not gate-observable).
        Point2[] v = [
            new(c.X + hw - r, c.Y - hh), new(c.X + hw, c.Y - hh + r),
            new(c.X + hw, c.Y + hh - r), new(c.X + hw - r, c.Y + hh),
            new(c.X - hw + r, c.Y + hh), new(c.X - hw, c.Y + hh - r),
            new(c.X - hw, c.Y - hh + r), new(c.X - hw + r, c.Y - hh),
        ];
        Point2[] o = [
            new(k, 0), new(0, 0), new(0, k), new(0, 0), new(-k, 0), new(0, 0), new(0, -k), new(0, 0),
        ];
        Point2[] i = [
            new(0, 0), new(0, -k), new(0, 0), new(k, 0), new(0, 0), new(0, k), new(0, 0), new(-k, 0),
        ];
        var bez = new LottieBezier { V = v, O = o, I = i, Closed = true };
        return rc.Direction == 3 ? Reversed(bez) : bez;
    }

    private static LottieBezier BuildEllipse(LottieShape el)
    {
        Point2 c = el.Center is { } ce ? StaticOf(ce) : default;
        Point2 sz = el.Size is { } s ? StaticOf(s) : default;
        float rx = MathF.Abs(sz.X) / 2f, ry = MathF.Abs(sz.Y) / 2f;
        float kx = rx * Kappa, ky = ry * Kappa;

        Point2[] v = [new(c.X, c.Y - ry), new(c.X + rx, c.Y), new(c.X, c.Y + ry), new(c.X - rx, c.Y)];
        Point2[] o = [new(kx, 0), new(0, ky), new(-kx, 0), new(0, -ky)];
        Point2[] i = [new(-kx, 0), new(0, -ky), new(kx, 0), new(0, ky)];
        var bez = new LottieBezier { V = v, O = o, I = i, Closed = true };
        return el.Direction == 3 ? Reversed(bez) : bez;
    }

    private static LottieBezier Reversed(LottieBezier b)
    {
        int n = b.V.Length;
        var v = new Point2[n]; var o = new Point2[n]; var i = new Point2[n];
        for (int k = 0; k < n; k++)
        {
            int src = n - 1 - k;
            v[k] = b.V[src];
            o[k] = b.I[src];   // swap in/out when reversing direction
            i[k] = b.O[src];
        }
        return new LottieBezier { V = v, O = o, I = i, Closed = b.Closed };
    }

    // ── small AnimX -> baseline-value helpers (rest-pose / static-bake reads) ───────────────────────────────────

    internal static float StaticOf(AnimScalar s) => s.IsAnimated ? s.Keys![0].Value : s.Static;
    internal static Point2 StaticOf(AnimVec2 v) => v.IsAnimated ? v.Keys![0].Value : v.Static;
}
