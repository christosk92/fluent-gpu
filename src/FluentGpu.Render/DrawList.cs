using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;

namespace FluentGpu.Render;

public enum DrawOp : int
{
    FillRoundRect = 1, DrawGlyphRun = 2, PushClip = 3, PopClip = 4, DrawImage = 5,
    DrawRoundRectStroke = 6,   // SDF outline (focus ring) — same pipeline as FillRoundRect, with a stroke width
    DrawShadow = 7,            // soft drop shadow (analytic rounded-box gaussian)
    DrawGradientRect = 8,      // linear/radial gradient fill (≤4 stops)
    PushLayer = 9,             // begin a layer: Acrylic (blur the canvas behind DeviceRect) or Opacity (flat subtree alpha)
    PopLayer = 10,             // end the layer: Acrylic composites tint+noise; Opacity composites the subtree RT at GroupAlpha
    DrawGradientStroke = 11,   // gradient-tinted SDF outline (WinUI elevation border) — gradient PS + a stroke band
    DrawArc = 12,              // SDF circular-arc stroke with round caps (ProgressRing: a trimmed ring, like WinUI's Lottie)
    DrawPolylineStroke = 13,   // SDF stroked polyline with trim start/end (AnimatedIcon path-trim)
    DrawTabShape = 14,         // WinUI selected-tab shape: rounded-TOP rect + inverted (concave) bottom corner flares
                               // (TabViewItem::UpdateTabGeometry — microsoft-ui-xaml controls\dev\TabView\TabViewItem.cpp:98-123)
}

/// <summary>How a <see cref="FillRoundRectCmd"/> fills its interior.</summary>
public enum FillKind : int
{
    /// <summary>Flat <c>Fill</c> color (the default; ColorB/CellPx ignored).</summary>
    Solid = 0,
    /// <summary>Alpha-transparency checkerboard (the ColorPicker alpha lane / preview swatch): square cells of
    /// <c>CellPx</c> alternating <c>Fill</c> (cells where ⌊x/c⌋+⌊y/c⌋ is even) and <c>ColorB</c> (odd) — exactly WinUI's
    /// CreateCheckeredBackgroundAsync pattern ((x/CheckerSize + y/CheckerSize) % 2 == 0 ⇒ blank/transparent, else the
    /// checker color; CheckerSize = 4 px — microsoft-ui-xaml controls\dev\ColorPicker\ColorHelpers.cpp:9,384-404; the
    /// checker color is the SystemListLowColor resource — ColorPicker.xaml:12). Map Fill=transparent, ColorB=checker
    /// for WinUI parity.</summary>
    Checker = 1,
}

/// <summary>What a <see cref="PushLayerCmd"/>…PopLayer pair composites.</summary>
public enum LayerKind : int
{
    /// <summary>In-app acrylic: snapshot+blur the canvas under DeviceRect, composite the WinUI AcrylicBrush recipe
    /// (tint/luminosity/noise — AcrylicBrush.cpp:500-548), then the subtree draws on top.</summary>
    Acrylic = 0,
    /// <summary>Flat opacity group (WinUI Composition LayerVisual semantics): the subtree between Push/Pop renders at
    /// FULL alpha into a pooled offscreen RT, then composites ONCE over the canvas at <see cref="PushLayerCmd.GroupAlpha"/>
    /// — overlapping children do not double-blend (unlike the default per-node multiplied opacity, which matches
    /// WinUI's plain Visual.Opacity). Tint/blur fields are unused for this kind.</summary>
    Opacity = 1,
}

// POD payloads (unmanaged). Encoded as [int op][payload] in the byte stream.
// Transform is the composited world transform (local→device); Opacity is the cumulative subtree opacity.
//
// A rounded-rect fill. FillKind selects Solid (default) or Checker (see FillKind.Checker for the cell rule);
// ColorB/CellPx are the checker's second color + square cell size in local units (ignored for Solid).
public readonly record struct FillRoundRectCmd(RectF Rect, CornerRadius4 Radii, ColorF Fill, Affine2D Transform, float Opacity,
    int FillKind = 0, ColorF ColorB = default, float CellPx = 0f);
// Wrap/Trim are TextWrap/TextTrim enum values; MaxLines caps the line count (0 = unlimited). Bounds.W is the wrap width.
// Weight is the NUMERIC font weight (the int IS the DWRITE_FONT_WEIGHT; sugar like Bold resolves to 700/400 upstream).
// CharSpacing is WinUI CharacterSpacing (1/1000 em, per-glyph trailing advance); LineHeight (DIP; NaN/<=0 = natural)
// resolves per LineStacking (enum int) and TextLineBounds (enum int) — see FluentGpu.Text.TextStyle, the style source.
// SpanRunId (0 = plain) keys the SpanRunTable.Shared inline-run overlay (rtb-01): the renderer shapes the SAME single
// flow with per-range weight/size/family and tints per-span colors; ForceColor != 0 makes Color override the span
// colors too (the selected-text recolor re-emit — WinUI repaints selected glyphs uniformly).
public readonly record struct DrawGlyphRunCmd(RectF Bounds, ColorF Color, StringId Text, StringId Family, float FontSize, int Weight, int Wrap, int Trim, int MaxLines,
    float CharSpacing, float LineHeight, int LineStacking, int LineBounds, Affine2D Transform, float Opacity,
    int SpanRunId = 0, int ForceColor = 0);
// Tier-1 (scissor) clip: an axis-aligned DEVICE-space rect already intersected with the enclosing clip by the recorder.
// The RHI sets the scissor to <see cref="DeviceRect"/> on PushClip and restores the previous on PopClip.
// Tier-2 (rounded) clip: when <see cref="CornerRadius"/> > 0, <see cref="RoundedRect"/> is the clipping node's own
// device-space box and the RHI ADDITIONALLY clamps RoundRect-pipeline primitives (fills/strokes/checker/tab) to the
// rounded-box SDF of (RoundedRect, CornerRadius) — the animated-clip-with-Corners path (AnimChannel.ClipL/T/R/B on a
// rounded surface). Scope (documented honestly): the rounded clamp covers the RoundRect pipeline only; glyph runs,
// images, gradients, arcs and polylines still clip rectangularly via the scissor; axis-aligned transforms only (the
// same caveat the tier-1 scissor already has).
public readonly record struct ClipCmd(RectF DeviceRect, RectF RoundedRect = default, float CornerRadius = 0f);
// An image quad. <see cref="ImageId"/> is the ImageCache handle; <see cref="Ready"/>==0 ⇒ draw <see cref="Placeholder"/>
// (decode in flight). The GPU leaf samples the uploaded texture for the handle when ready (needs-pixels).
public readonly record struct DrawImageCmd(RectF Rect, CornerRadius4 Radii, int ImageId, int Ready, ColorF Placeholder, Affine2D Transform, float Opacity, float CrossFade = 1f);
// An SDF outline (focus visual / border ring). Same SDF rounded-box as FillRoundRect, but the PS draws a
// <see cref="StrokeWidth"/>-wide band centered on the edge instead of filling. Drawn over the control; works on any
// fill/background. DashOn/DashOff (device-independent px along the perimeter) modulate the band into dashes:
// 0 = solid (the default). The dash phase starts at the top edge's left end and runs clockwise.
public readonly record struct DrawRoundRectStrokeCmd(RectF Rect, CornerRadius4 Radii, ColorF Color, float StrokeWidth, Affine2D Transform, float Opacity,
    float DashOn = 0f, float DashOff = 0f);
// A soft drop shadow: a rounded-box SDF with a gaussian falloff (offset + blur + spread), drawn beneath the fill.
public readonly record struct DrawShadowCmd(RectF Rect, CornerRadius4 Radii, ColorF Color, float OffsetX, float OffsetY, float Blur, float Spread, Affine2D Transform, float Opacity);
// A gradient-filled rounded rect. Start/End are in local 0..1 axis coords; up to 4 stops (C0..C3 / O0..O3, StopCount used).
public readonly record struct DrawGradientRectCmd(RectF Rect, CornerRadius4 Radii, Point2 Start, Point2 End, int Shape, int StopCount,
    ColorF C0, ColorF C1, ColorF C2, ColorF C3, float O0, float O1, float O2, float O3, Affine2D Transform, float Opacity);
// A gradient-tinted SDF outline (elevation border): the gradient PS sampled along the local axis, drawn as a band of
// <see cref="StrokeWidth"/> centered on the rounded-box edge (instead of a fill). Same payload as the gradient fill + a width.
public readonly record struct DrawGradientStrokeCmd(RectF Rect, CornerRadius4 Radii, Point2 Start, Point2 End, int Shape, int StopCount,
    ColorF C0, ColorF C1, ColorF C2, ColorF C3, float O0, float O1, float O2, float O3, float StrokeWidth, Affine2D Transform, float Opacity);
// Begin/end a layer. Kind = Acrylic (0, the default): the engine snapshots the canvas under DeviceRect, gaussian-blurs
// it, then on PopLayer tints + adds noise + a luminosity wash; the subtree between the two draws on top.
// Kind = Opacity (1): the subtree renders at FULL alpha into a pooled offscreen RT, then PopLayer composites it ONCE
// over the canvas at <see cref="GroupAlpha"/> (flat group opacity — no double-blend of overlapping children); the
// acrylic fields (Tint/Fallback/TintOpacity/BlurSigma/NoiseOpacity/LuminosityOpacity) are unused for this kind.
public readonly record struct PushLayerCmd(RectF DeviceRect, CornerRadius4 Radii, ColorF Tint, ColorF Fallback, float TintOpacity, float BlurSigma, float NoiseOpacity, float LuminosityOpacity,
    int Kind = 0, float GroupAlpha = 1f);
public readonly record struct PopLayerCmd(RectF DeviceRect);
// A circular-arc stroke (ProgressRing). The arc is centred in <see cref="Rect"/> with radius (min(W,H)-Thickness)/2, a
// <see cref="Thickness"/>-wide stroke, swept from <see cref="StartDeg"/> for <see cref="SweepDeg"/> degrees (0° = 12 o'clock,
// clockwise), with round caps when <see cref="RoundCaps"/> != 0. Matches WinUI's trimmed round-cap ring stroke.
public readonly record struct DrawArcCmd(RectF Rect, ColorF Color, float Thickness, float StartDeg, float SweepDeg, int RoundCaps, Affine2D Transform, float Opacity);
public readonly record struct DrawPolylineStrokeCmd(RectF Rect, ColorF Color, float Thickness,
    Point2 P0, Point2 P1, Point2 P2, Point2 P3, int PointCount, float TrimStart, float TrimEnd, int RoundCaps,
    Affine2D Transform, float Opacity);
// The WinUI selected-tab shape (TabViewItem::UpdateTabGeometry — TabViewItem.cpp:98-123): a tab body whose TOP corners
// round at <see cref="TopRadius"/> (WinUI: OverlayCornerRadius, 8) and whose bottom OUTER corners flare OUT through
// concave quarter-arcs of <see cref="FlareRadius"/> (WinUI hardcodes 4 — TabViewItem.cpp:106 "Assumes 4px curving-out
// corners" + the SelectedBackgroundPath's Margin="-4,0", TabView.xaml:551). <see cref="Rect"/> is the FULL shape box
// including both flares (WinUI's path spans tabWidth + 2·flare): the body occupies Rect inset by FlareRadius on each
// side; the flares fill the bottom-corner squares outside the body, minus a FlareRadius disc centred at the outer edge
// FlareRadius above the bottom — the inverted-fillet base the tab "grows" out of. Drawn as a RoundRect-pipeline SDF
// variant — deliberately NOT a general path tessellator (BUILD-ROADMAP row-27 scope).
public readonly record struct DrawTabShapeCmd(RectF Rect, float TopRadius, float FlareRadius, ColorF Fill, Affine2D Transform, float Opacity);

/// <summary>
/// Flat POD command stream consumed by the RHI (<c>SubmitDrawList</c>). The slice grows a single contiguous buffer;
/// the full engine uses double-buffered arenas + clean-span memcpy. SortKeys are recorded in parallel.
/// </summary>
public sealed class DrawList
{
    private byte[] _buf;
    private int _len;
    private ulong[] _sort;
    private int _sortLen;

    public DrawList(int capacity = 4096)
    {
        _buf = GC.AllocateUninitializedArray<byte>(capacity, pinned: false);
        _sort = new ulong[256];
    }

    public ReadOnlySpan<byte> Bytes => _buf.AsSpan(0, _len);
    public ReadOnlySpan<ulong> SortKeys => _sort.AsSpan(0, _sortLen);
    public int CommandCount { get; private set; }

    public void Reset() { _len = 0; _sortLen = 0; CommandCount = 0; }

    public void FillRoundRect(in RectF rect, in CornerRadius4 radii, in ColorF fill, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.FillRoundRect);
        WritePayload(new FillRoundRectCmd(rect, radii, fill, transform, opacity));
        PushSort(sortKey);
    }

    /// <summary>A checkerboard-filled rounded rect (<see cref="FillKind.Checker"/>): square <paramref name="cellPx"/>
    /// cells alternating <paramref name="fillA"/> (even cells) and <paramref name="fillB"/> (odd) — the ColorPicker
    /// alpha-lane / preview-swatch transparency pattern (WinUI CheckerSize = 4, ColorHelpers.cpp:9).</summary>
    public void FillRoundRectChecker(in RectF rect, in CornerRadius4 radii, in ColorF fillA, in ColorF fillB, float cellPx, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.FillRoundRect);
        WritePayload(new FillRoundRectCmd(rect, radii, fillA, transform, opacity, (int)FillKind.Checker, fillB, MathF.Max(1f, cellPx)));
        PushSort(sortKey);
    }

    public void DrawGlyphRun(in RectF bounds, in ColorF color, StringId text, StringId family, float fontSize, int weight, int wrap, int trim, int maxLines,
        float charSpacing, float lineHeight, int lineStacking, int lineBounds, in Affine2D transform, float opacity, ulong sortKey = 0,
        int spanRunId = 0, bool forceColor = false)
    {
        WriteOp(DrawOp.DrawGlyphRun);
        WritePayload(new DrawGlyphRunCmd(bounds, color, text, family, fontSize, weight, wrap, trim, maxLines,
            charSpacing, lineHeight, lineStacking, lineBounds, transform, opacity, spanRunId, forceColor ? 1 : 0));
        PushSort(sortKey);
    }

    /// <summary>Push a tier-1 scissor clip (device-space, pre-intersected). Pair with <see cref="PopClip"/>.</summary>
    public void PushClip(in RectF deviceRect, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PushClip);
        WritePayload(new ClipCmd(deviceRect));
        PushSort(sortKey);
    }

    /// <summary>Push a tier-2 ROUNDED clip: the scissor still clamps to <paramref name="deviceRect"/>, and RoundRect-
    /// pipeline primitives additionally clamp to the rounded-box SDF of (<paramref name="roundedRect"/>,
    /// <paramref name="cornerRadius"/>) — both device-space. Pair with <see cref="PopClip"/>. See <see cref="ClipCmd"/>
    /// for the honest coverage scope (RoundRect-pipeline primitives only).</summary>
    public void PushClipRounded(in RectF deviceRect, in RectF roundedRect, float cornerRadius, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PushClip);
        WritePayload(new ClipCmd(deviceRect, roundedRect, MathF.Max(0f, cornerRadius)));
        PushSort(sortKey);
    }

    public void PopClip(ulong sortKey = 0)
    {
        WriteOp(DrawOp.PopClip);
        PushSort(sortKey);
    }

    public void DrawImage(in RectF rect, in CornerRadius4 radii, int imageId, bool ready, in ColorF placeholder, in Affine2D transform, float opacity, float crossFade = 1f, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawImage);
        WritePayload(new DrawImageCmd(rect, radii, imageId, ready ? 1 : 0, placeholder, transform, opacity, crossFade));
        PushSort(sortKey);
    }

    public void StrokeRoundRect(in RectF rect, in CornerRadius4 radii, in ColorF color, float strokeWidth, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawRoundRectStroke);
        WritePayload(new DrawRoundRectStrokeCmd(rect, radii, color, strokeWidth, transform, opacity));
        PushSort(sortKey);
    }

    /// <summary>A DASHED SDF outline: the stroke band is modulated along the perimeter into <paramref name="dashOn"/>-px
    /// dashes separated by <paramref name="dashOff"/>-px gaps (clockwise from the top edge's left end). dashOn ≤ 0 falls
    /// back to a solid stroke.</summary>
    public void StrokeRoundRectDashed(in RectF rect, in CornerRadius4 radii, in ColorF color, float strokeWidth, float dashOn, float dashOff, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawRoundRectStroke);
        WritePayload(new DrawRoundRectStrokeCmd(rect, radii, color, strokeWidth, transform, opacity, MathF.Max(0f, dashOn), MathF.Max(0f, dashOff)));
        PushSort(sortKey);
    }

    public void Shadow(in RectF rect, in CornerRadius4 radii, in ColorF color, float offsetX, float offsetY, float blur, float spread, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawShadow);
        WritePayload(new DrawShadowCmd(rect, radii, color, offsetX, offsetY, blur, spread, transform, opacity));
        PushSort(sortKey);
    }

    public void GradientRect(in DrawGradientRectCmd cmd, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawGradientRect);
        WritePayload(cmd);
        PushSort(sortKey);
    }

    public void GradientStroke(in DrawGradientStrokeCmd cmd, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawGradientStroke);
        WritePayload(cmd);
        PushSort(sortKey);
    }

    public void PushLayer(in RectF deviceRect, in CornerRadius4 radii, in ColorF tint, in ColorF fallback, float tintOpacity, float blurSigma, float noiseOpacity, float luminosityOpacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PushLayer);
        WritePayload(new PushLayerCmd(deviceRect, radii, tint, fallback, tintOpacity, blurSigma, noiseOpacity, luminosityOpacity));
        PushSort(sortKey);
    }

    /// <summary>Begin a FLAT opacity group (<see cref="LayerKind.Opacity"/>): everything until the matching
    /// <see cref="PopLayer"/> renders at full alpha offscreen and composites once at <paramref name="groupAlpha"/> —
    /// WinUI Composition LayerVisual semantics (no double-blend of overlapping children). Subtree commands should be
    /// recorded with opacity relative to 1, NOT pre-multiplied by the group alpha.</summary>
    public void PushOpacityLayer(in RectF deviceRect, in CornerRadius4 radii, float groupAlpha, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PushLayer);
        WritePayload(new PushLayerCmd(deviceRect, radii, default, default, 0f, 0f, 0f, 0f,
            (int)LayerKind.Opacity, Math.Clamp(groupAlpha, 0f, 1f)));
        PushSort(sortKey);
    }

    public void PopLayer(in RectF deviceRect, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PopLayer);
        WritePayload(new PopLayerCmd(deviceRect));
        PushSort(sortKey);
    }

    public void Arc(in RectF rect, in ColorF color, float thickness, float startDeg, float sweepDeg, bool roundCaps, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawArc);
        WritePayload(new DrawArcCmd(rect, color, thickness, startDeg, sweepDeg, roundCaps ? 1 : 0, transform, opacity));
        PushSort(sortKey);
    }

    public void PolylineStroke(in RectF rect, in ColorF color, float thickness,
                               in Point2 p0, in Point2 p1, in Point2 p2, in Point2 p3, int pointCount,
                               float trimStart, float trimEnd, bool roundCaps, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawPolylineStroke);
        WritePayload(new DrawPolylineStrokeCmd(rect, color, thickness, p0, p1, p2, p3, pointCount,
            trimStart, trimEnd, roundCaps ? 1 : 0, transform, opacity));
        PushSort(sortKey);
    }

    /// <summary>The WinUI selected-tab shape (see <see cref="DrawTabShapeCmd"/>): <paramref name="rect"/> is the FULL
    /// shape box including both bottom flares; the tab body is inset by <paramref name="flareRadius"/> per side with
    /// <paramref name="topRadius"/> top corners. WinUI values: topRadius = OverlayCornerRadius (8), flareRadius = 4
    /// (TabViewItem.cpp:106).</summary>
    public void TabShape(in RectF rect, float topRadius, float flareRadius, in ColorF fill, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawTabShape);
        WritePayload(new DrawTabShapeCmd(rect, MathF.Max(0f, topRadius), MathF.Max(0f, flareRadius), fill, transform, opacity));
        PushSort(sortKey);
    }

    private void WriteOp(DrawOp op)
    {
        Ensure(sizeof(int));
        int v = (int)op;
        MemoryMarshal.Write(_buf.AsSpan(_len), in v);
        _len += sizeof(int);
        CommandCount++;
    }

    private void WritePayload<T>(in T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        Ensure(size);
        MemoryMarshal.Write(_buf.AsSpan(_len), in value);
        _len += size;
    }

    private void PushSort(ulong key)
    {
        if (_sortLen == _sort.Length) Array.Resize(ref _sort, _sort.Length * 2);
        _sort[_sortLen++] = key;
    }

    private void Ensure(int extra)
    {
        if (_len + extra <= _buf.Length) return;
        int n = _buf.Length * 2;
        while (n < _len + extra) n *= 2;
        var nb = GC.AllocateUninitializedArray<byte>(n, pinned: false);
        Array.Copy(_buf, nb, _len);
        _buf = nb;
    }
}
