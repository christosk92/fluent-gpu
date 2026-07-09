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
    DrawGlyphRunGradient = 15, // a glyph run filled per-glyph by a karaoke WIPE (played/unplayed split along the run-local
                               // x-axis). Reuses the glyph PSO (per-instance color); the per-glyph colors are computed at
                               // REPLAY from the split, so the cache key (shaping) is unchanged. Decoded only when emitted.
}

/// <summary>Per-opcode command counts for the current <see cref="DrawList"/>. Stored as scalar fields so the host can
/// log the failed frame's shape after device loss without reparsing the command stream or retaining payload bytes.</summary>
public struct DrawListOpcodeStats
{
    public int FillRoundRect, DrawGlyphRun, PushClip, PopClip, DrawImage, DrawRoundRectStroke, DrawShadow;
    public int DrawGradientRect, PushLayer, PopLayer, DrawGradientStroke, DrawArc, DrawPolylineStroke, DrawTabShape, DrawGlyphRunGradient;

    public void Add(DrawOp op)
    {
        switch (op)
        {
            case DrawOp.FillRoundRect: FillRoundRect++; break;
            case DrawOp.DrawGlyphRun: DrawGlyphRun++; break;
            case DrawOp.PushClip: PushClip++; break;
            case DrawOp.PopClip: PopClip++; break;
            case DrawOp.DrawImage: DrawImage++; break;
            case DrawOp.DrawRoundRectStroke: DrawRoundRectStroke++; break;
            case DrawOp.DrawShadow: DrawShadow++; break;
            case DrawOp.DrawGradientRect: DrawGradientRect++; break;
            case DrawOp.PushLayer: PushLayer++; break;
            case DrawOp.PopLayer: PopLayer++; break;
            case DrawOp.DrawGradientStroke: DrawGradientStroke++; break;
            case DrawOp.DrawArc: DrawArc++; break;
            case DrawOp.DrawPolylineStroke: DrawPolylineStroke++; break;
            case DrawOp.DrawTabShape: DrawTabShape++; break;
            case DrawOp.DrawGlyphRunGradient: DrawGlyphRunGradient++; break;
        }
    }

    public void Add(in DrawListOpcodeStats other)
    {
        FillRoundRect += other.FillRoundRect;
        DrawGlyphRun += other.DrawGlyphRun;
        PushClip += other.PushClip;
        PopClip += other.PopClip;
        DrawImage += other.DrawImage;
        DrawRoundRectStroke += other.DrawRoundRectStroke;
        DrawShadow += other.DrawShadow;
        DrawGradientRect += other.DrawGradientRect;
        PushLayer += other.PushLayer;
        PopLayer += other.PopLayer;
        DrawGradientStroke += other.DrawGradientStroke;
        DrawArc += other.DrawArc;
        DrawPolylineStroke += other.DrawPolylineStroke;
        DrawTabShape += other.DrawTabShape;
        DrawGlyphRunGradient += other.DrawGlyphRunGradient;
    }

    public readonly bool CanTranslateCopiedSpan
        => DrawGlyphRun == 0 && DrawGlyphRunGradient == 0
           && PushClip == 0 && PopClip == 0
           && PushLayer == 0 && PopLayer == 0;

    public readonly DrawListOpcodeStats Minus(in DrawListOpcodeStats other) => new()
    {
        FillRoundRect = FillRoundRect - other.FillRoundRect,
        DrawGlyphRun = DrawGlyphRun - other.DrawGlyphRun,
        PushClip = PushClip - other.PushClip,
        PopClip = PopClip - other.PopClip,
        DrawImage = DrawImage - other.DrawImage,
        DrawRoundRectStroke = DrawRoundRectStroke - other.DrawRoundRectStroke,
        DrawShadow = DrawShadow - other.DrawShadow,
        DrawGradientRect = DrawGradientRect - other.DrawGradientRect,
        PushLayer = PushLayer - other.PushLayer,
        PopLayer = PopLayer - other.PopLayer,
        DrawGradientStroke = DrawGradientStroke - other.DrawGradientStroke,
        DrawArc = DrawArc - other.DrawArc,
        DrawPolylineStroke = DrawPolylineStroke - other.DrawPolylineStroke,
        DrawTabShape = DrawTabShape - other.DrawTabShape,
        DrawGlyphRunGradient = DrawGlyphRunGradient - other.DrawGlyphRunGradient,
    };

    public override readonly string ToString()
        => $"fill={FillRoundRect} glyph={DrawGlyphRun} glyphGrad={DrawGlyphRunGradient} clip={PushClip}/{PopClip} img={DrawImage} stroke={DrawRoundRectStroke} shadow={DrawShadow} grad={DrawGradientRect}/{DrawGradientStroke} layer={PushLayer}/{PopLayer} arc={DrawArc} poly={DrawPolylineStroke} tab={DrawTabShape}";
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
    /// <summary>Per-node SELF-blur (the Expressive Motion Kit, <see cref="PushLayerCmd.BlurSigma"/>): the subtree renders
    /// at FULL alpha into a pooled offscreen RT (like <see cref="Opacity"/>), then a separable Gaussian of radius
    /// <see cref="PushLayerCmd.BlurSigma"/> is run over it and the result composites ONCE at <see cref="PushLayerCmd.GroupAlpha"/>
    /// — so a node's own pixels (and its subtree) blur + fade together (CSS <c>filter: blur()</c> on the element). The
    /// tint/noise/luminosity acrylic fields are unused for this kind (this blurs the element, NOT the backdrop behind it).</summary>
    Blur = 2,

    /// <summary>EDGE FADE: like <see cref="Opacity"/>/<see cref="Blur"/> the subtree renders at full alpha into a pooled
    /// offscreen RT, then composites once while a per-edge feather (which follows the rounded corners — the curve)
    /// attenuates the premultiplied alpha to 0 over a band near each enabled edge, so the content dissolves into whatever
    /// is behind. The feather fields (<see cref="PushLayerCmd.FadeBandL"/>… / FadeFalloff / FadeIntensity / FadeEdges)
    /// carry the per-edge bands; <see cref="PushLayerCmd.BlurSigma"/> &gt; 0 Gaussian-blurs the RT first. The acrylic
    /// tint/noise/luminosity fields are unused for this kind.</summary>
    EdgeFade = 3,
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
// InMotion (1 = the run's world transform was written THIS frame — scroll/fling/drag/FLIP): the renderer skips the
// device-grid baseline snap so moving text rides sub-pixel WITH its plate (no 1px shear at half-pixel crossings),
// then re-snaps crisp on the settle frame the host queues after the last transform write.
public readonly record struct DrawGlyphRunCmd(RectF Bounds, ColorF Color, StringId Text, StringId Family, float FontSize, int Weight, int Wrap, int Trim, int MaxLines,
    float CharSpacing, float LineHeight, int LineStacking, int LineBounds, Affine2D Transform, float Opacity,
    int SpanRunId = 0, int ForceColor = 0, int InMotion = 0);
// A glyph run filled by a left->right WIPE (the GlyphWipe primitive): Split (0..1) is a fraction of the run's content in
// READING ORDER — the replay lays a wrapped run's visual lines END-TO-END over glyph EDGES, so a glyph before Split in
// reading order is painted Before and one after it After, with a Softness-wide soft blend the replay remaps so Split==1
// fully clears the run's trailing edge; Lift floats a just-passed glyph up by Lift DIP (settling). Reuses the glyph PSO +
// per-instance color/offset (NO new shader/PSO). The per-glyph values are computed at replay from Split, so the shaping
// cache key is identical to the plain run (no reshape as the split advances). General text-reveal; the lyrics karaoke uses it.
public readonly record struct DrawGlyphRunGradientCmd(RectF Bounds, StringId Text, StringId Family, float FontSize, int Weight, int Wrap, int Trim, int MaxLines,
    float CharSpacing, float LineHeight, int LineStacking, int LineBounds, Affine2D Transform, float Opacity,
    ColorF Before, ColorF After, float Split, float Softness, float Lift, int SpanRunId = 0, int InMotion = 0);
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
// <see cref="UvRect"/> is the content-fit sub-rect in 0..1 source space ((0,0,1,1) = whole texture): the recorder
// bakes ImageFit.Cover crops here; the device composes it with the texture's atlas cell before sampling.
public readonly record struct DrawImageCmd(RectF Rect, CornerRadius4 Radii, int ImageId, int Ready, ColorF Placeholder, Affine2D Transform, float Opacity, RectF UvRect, float FadeStartMs = float.NaN, float FadeDurationMs = 0f, int FadeEasing = 0);
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
    int Kind = 0, float GroupAlpha = 1f,
    // EdgeFade-only (Kind == 3): per-edge feather band depth in DEVICE px (0 = edge disabled), falloff curve, fade
    // intensity, enabled-edge bit mask, and the exact device-space clip used when compositing the offscreen layer.
    // The rounded-corner radii come from Radii (the feather follows them).
    float FadeBandL = 0f, float FadeBandT = 0f, float FadeBandR = 0f, float FadeBandB = 0f, int FadeFalloff = 0, float FadeIntensity = 1f, int FadeEdges = 0,
    RectF CompositeClip = default,
    // Acrylic-only: a STABLE per-overlay id (the scene node handle, packed index|gen) keying the compositor's retained
    // blurred-backdrop cache across frames, so a stationary acrylic surface REUSES its blur instead of re-blurring every
    // frame (design/subsystems/backdrop-effects-animation.md §2.3). 0 ⇒ no caching (re-blur every frame — prior behavior).
    ulong LayerId = 0,
    int BlurCachePolicy = 0,
    // Self-blur only: 1 = the node's world transform moved THIS frame (recorder's inMotion — scroll/fling/FLIP). NOT part
    // of the cross-frame pin key (backdrop-effects-animation.md §FA-2a); at rest (0) the compositor does one exact re-mint
    // when a HIT would otherwise composite a pin captured at a different position (settle exactness for non-glyph subtrees).
    int InMotion = 0);
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
    private byte[] _priorBuf;
    private int _priorLen;
    private ulong[] _priorSort;
    private int _priorSortLen;
    private DrawListOpcodeStats _opcodeStats;

    public DrawList(int capacity = 4096)
    {
        _buf = GC.AllocateUninitializedArray<byte>(capacity, pinned: false);
        _sort = new ulong[256];
        _priorBuf = GC.AllocateUninitializedArray<byte>(capacity, pinned: false);
        _priorSort = new ulong[256];
    }

    public ReadOnlySpan<byte> Bytes => _buf.AsSpan(0, _len);
    public ReadOnlySpan<ulong> SortKeys => _sort.AsSpan(0, _sortLen);
    public int CommandCount { get; private set; }
    public DrawListOpcodeStats OpcodeStats => _opcodeStats;
    public int BytePosition => _len;
    public int SortPosition => _sortLen;
    public int PriorByteLength => _priorLen;
    public int PriorSortLength => _priorSortLen;

    public void Reset() { _len = 0; _sortLen = 0; CommandCount = 0; _opcodeStats = default; }

    /// <summary>Start a record pass while preserving the previous command/sort arenas for clean-span copies.</summary>
    public void SwapAndReset()
    {
        (_buf, _priorBuf) = (_priorBuf, _buf);
        (_sort, _priorSort) = (_priorSort, _sort);
        _priorLen = _len;
        _priorSortLen = _sortLen;
        Reset();
    }

    public bool CanCopyPriorSpan(int byteStart, int byteLength, int sortStart, int sortCount)
        => byteStart >= 0 && byteLength >= 0 && byteStart + byteLength <= _priorLen
           && sortStart >= 0 && sortCount >= 0 && sortStart + sortCount <= _priorSortLen;

    public void CopySpanFromPrior(int byteStart, int byteLength, int sortStart, int sortCount,
                                  int commandCount, in DrawListOpcodeStats opcodeStats)
    {
        if (byteLength > 0)
        {
            Ensure(byteLength);
            Array.Copy(_priorBuf, byteStart, _buf, _len, byteLength);
            _len += byteLength;
        }
        if (sortCount > 0)
        {
            EnsureSort(sortCount);
            Array.Copy(_priorSort, sortStart, _sort, _sortLen, sortCount);
            _sortLen += sortCount;
        }
        CommandCount += commandCount;
        _opcodeStats.Add(in opcodeStats);
    }

    public bool CopySpanFromPriorTranslated(int byteStart, int byteLength, int sortStart, int sortCount,
                                            int commandCount, in DrawListOpcodeStats opcodeStats,
                                            float dx, float dy)
    {
        if (!opcodeStats.CanTranslateCopiedSpan || !CanCopyPriorSpan(byteStart, byteLength, sortStart, sortCount))
            return false;

        int byteDst = _len;
        int sortDst = _sortLen;
        int cmdBefore = CommandCount;
        var statsBefore = _opcodeStats;

        if (byteLength > 0)
        {
            Ensure(byteLength);
            Array.Copy(_priorBuf, byteStart, _buf, _len, byteLength);
            _len += byteLength;
        }
        if (sortCount > 0)
        {
            EnsureSort(sortCount);
            Array.Copy(_priorSort, sortStart, _sort, _sortLen, sortCount);
            _sortLen += sortCount;
        }

        if (!TranslateCopiedSpan(byteDst, byteLength, dx, dy))
        {
            _len = byteDst;
            _sortLen = sortDst;
            CommandCount = cmdBefore;
            _opcodeStats = statsBefore;
            return false;
        }

        CommandCount += commandCount;
        _opcodeStats.Add(in opcodeStats);
        return true;
    }

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
        int spanRunId = 0, bool forceColor = false, bool inMotion = false)
    {
        WriteOp(DrawOp.DrawGlyphRun);
        WritePayload(new DrawGlyphRunCmd(bounds, color, text, family, fontSize, weight, wrap, trim, maxLines,
            charSpacing, lineHeight, lineStacking, lineBounds, transform, opacity, spanRunId, forceColor ? 1 : 0, inMotion ? 1 : 0));
        PushSort(sortKey);
    }

    /// <summary>A glyph run filled by a left→right wipe (the <c>GlyphWipe</c> primitive): <paramref name="split"/>
    /// (0..1 along the run's content in READING ORDER — visual lines laid end-to-end over glyph edges) divides
    /// <paramref name="before"/> (sung) from <paramref name="after"/> (unsung), with a <paramref name="softness"/>-wide
    /// soft boundary the replay remaps so split==1 fully clears the trailing edge; <paramref name="lift"/> floats a
    /// just-passed glyph up. Reuses the glyph pipeline (per-instance color/offset computed at replay) — no new shader.</summary>
    public void DrawGlyphRunGradient(in RectF bounds, StringId text, StringId family, float fontSize, int weight, int wrap, int trim, int maxLines,
        float charSpacing, float lineHeight, int lineStacking, int lineBounds, in Affine2D transform, float opacity,
        in ColorF before, in ColorF after, float split, float softness, float lift, ulong sortKey = 0, int spanRunId = 0, bool inMotion = false)
    {
        WriteOp(DrawOp.DrawGlyphRunGradient);
        WritePayload(new DrawGlyphRunGradientCmd(bounds, text, family, fontSize, weight, wrap, trim, maxLines,
            charSpacing, lineHeight, lineStacking, lineBounds, transform, opacity, before, after, split, softness, lift, spanRunId, inMotion ? 1 : 0));
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

    public void DrawImage(in RectF rect, in CornerRadius4 radii, int imageId, bool ready, in ColorF placeholder, in Affine2D transform, float opacity, in RectF uvRect, float fadeStartMs = float.NaN, float fadeDurationMs = 0f, int fadeEasing = 0, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawImage);
        WritePayload(new DrawImageCmd(rect, radii, imageId, ready ? 1 : 0, placeholder, transform, opacity, uvRect, fadeStartMs, fadeDurationMs, fadeEasing));
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

    public void PushLayer(in RectF deviceRect, in CornerRadius4 radii, in ColorF tint, in ColorF fallback, float tintOpacity, float blurSigma, float noiseOpacity, float luminosityOpacity, ulong sortKey = 0, ulong layerId = 0)
    {
        WriteOp(DrawOp.PushLayer);
        WritePayload(new PushLayerCmd(deviceRect, radii, tint, fallback, tintOpacity, blurSigma, noiseOpacity, luminosityOpacity, LayerId: layerId));
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

    /// <summary>Begin a per-node SELF-blur group (<see cref="LayerKind.Blur"/>): the subtree until the matching
    /// <see cref="PopLayer"/> renders at full alpha into a pooled offscreen RT, is separable-Gaussian-blurred by
    /// <paramref name="blurSigma"/> px, and composites once at <paramref name="groupAlpha"/> (so blur + fade read as one
    /// motion). The element's OWN pixels blur — not the backdrop behind it. Subtree commands record at opacity relative
    /// to 1, NOT pre-multiplied by the group alpha.</summary>
    public void PushBlurLayer(in RectF deviceRect, in CornerRadius4 radii, float blurSigma, float groupAlpha, ulong sortKey = 0, BlurCachePolicy cachePolicy = BlurCachePolicy.Normal, bool inMotion = false)
    {
        WriteOp(DrawOp.PushLayer);
        WritePayload(new PushLayerCmd(deviceRect, radii, default, default, 0f, MathF.Max(0f, blurSigma), 0f, 0f,
            (int)LayerKind.Blur, Math.Clamp(groupAlpha, 0f, 1f), BlurCachePolicy: (int)cachePolicy, InMotion: inMotion ? 1 : 0));
        PushSort(sortKey);
    }

    /// <summary>Begin an EDGE-FADE group (<see cref="LayerKind.EdgeFade"/>): the subtree until the matching
    /// <see cref="PopLayer"/> renders at full alpha into a pooled offscreen RT, then composites once while feathering the
    /// premultiplied alpha to 0 over a per-edge band near each enabled edge — so the content dissolves into whatever is
    /// behind. The feather follows the rounded corners in <paramref name="radii"/> (the curve). Bands are DEVICE px (the
    /// recorder scales the DIP spec by the world scale); <paramref name="blurSigma"/> &gt; 0 Gaussian-blurs the RT before
    /// the feather. Subtree commands record at opacity relative to 1.</summary>
    public void PushEdgeFadeLayer(in RectF deviceRect, in RectF compositeClip, in CornerRadius4 radii, float groupAlpha,
        int edges, float bandL, float bandT, float bandR, float bandB, int falloff, float intensity, float blurSigma = 0f, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PushLayer);
        WritePayload(new PushLayerCmd(deviceRect, radii, default, default, 0f, MathF.Max(0f, blurSigma), 0f, 0f,
            (int)LayerKind.EdgeFade, Math.Clamp(groupAlpha, 0f, 1f),
            MathF.Max(0f, bandL), MathF.Max(0f, bandT), MathF.Max(0f, bandR), MathF.Max(0f, bandB),
            falloff, Math.Clamp(intensity, 0f, 1f), edges, compositeClip));
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
        _opcodeStats.Add(op);
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
        EnsureSort(1);
        _sort[_sortLen++] = key;
    }

    private void EnsureSort(int extra)
    {
        if (_sortLen + extra <= _sort.Length) return;
        int n = _sort.Length * 2;
        while (n < _sortLen + extra) n *= 2;
        Array.Resize(ref _sort, n);
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

    private bool TranslateCopiedSpan(int start, int length, float dx, float dy)
    {
        if (dx == 0f && dy == 0f) return true;
        int p = start;
        int end = start + length;
        while (p < end)
        {
            if (end - p < sizeof(int)) return false;
            var op = (DrawOp)MemoryMarshal.Read<int>(_buf.AsSpan(p, sizeof(int)));
            p += sizeof(int);
            switch (op)
            {
                case DrawOp.FillRoundRect:
                    if (!TranslatePayload<FillRoundRectCmd>(ref p, end, dx, dy, static (c, x, y) => c with { Transform = Translate(c.Transform, x, y) })) return false;
                    break;
                case DrawOp.DrawImage:
                    if (!TranslatePayload<DrawImageCmd>(ref p, end, dx, dy, static (c, x, y) => c with { Transform = Translate(c.Transform, x, y) })) return false;
                    break;
                case DrawOp.DrawRoundRectStroke:
                    if (!TranslatePayload<DrawRoundRectStrokeCmd>(ref p, end, dx, dy, static (c, x, y) => c with { Transform = Translate(c.Transform, x, y) })) return false;
                    break;
                case DrawOp.DrawShadow:
                    if (!TranslatePayload<DrawShadowCmd>(ref p, end, dx, dy, static (c, x, y) => c with { Transform = Translate(c.Transform, x, y) })) return false;
                    break;
                case DrawOp.DrawGradientRect:
                    if (!TranslatePayload<DrawGradientRectCmd>(ref p, end, dx, dy, static (c, x, y) => c with { Transform = Translate(c.Transform, x, y) })) return false;
                    break;
                case DrawOp.DrawGradientStroke:
                    if (!TranslatePayload<DrawGradientStrokeCmd>(ref p, end, dx, dy, static (c, x, y) => c with { Transform = Translate(c.Transform, x, y) })) return false;
                    break;
                case DrawOp.DrawArc:
                    if (!TranslatePayload<DrawArcCmd>(ref p, end, dx, dy, static (c, x, y) => c with { Transform = Translate(c.Transform, x, y) })) return false;
                    break;
                case DrawOp.DrawPolylineStroke:
                    if (!TranslatePayload<DrawPolylineStrokeCmd>(ref p, end, dx, dy, static (c, x, y) => c with { Transform = Translate(c.Transform, x, y) })) return false;
                    break;
                case DrawOp.DrawTabShape:
                    if (!TranslatePayload<DrawTabShapeCmd>(ref p, end, dx, dy, static (c, x, y) => c with { Transform = Translate(c.Transform, x, y) })) return false;
                    break;
                default:
                    return false;
            }
        }
        return p == end;
    }

    private delegate T TranslatePayloadFunc<T>(T value, float dx, float dy) where T : unmanaged;

    private bool TranslatePayload<T>(ref int p, int end, float dx, float dy, TranslatePayloadFunc<T> translate) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (p + size > end) return false;
        var span = _buf.AsSpan(p, size);
        T cmd = MemoryMarshal.Read<T>(span);
        cmd = translate(cmd, dx, dy);
        MemoryMarshal.Write(span, in cmd);
        p += size;
        return true;
    }

    private static Affine2D Translate(in Affine2D t, float dx, float dy)
        => new(t.M11, t.M12, t.M21, t.M22, t.Dx + dx, t.Dy + dy);
}
