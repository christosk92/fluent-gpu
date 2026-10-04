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
    DrawIconMask = 16,         // a ThemedIcon vector-layer mask: a CPU-rasterized colorless R8 coverage mask (interned
                               // path geometry in IconGeometryTable.Shared, keyed by PathId) tinted per-instance and drawn
                               // through the EXISTING glyph atlas/PSO — no new shader/PSO/texture/RHI method. Deliberately
                               // NOT the gpu-renderer.md §5 tessellation lane (same non-tessellation-sibling posture as
                               // DrawTabShape) — icons are tiny glyph-shaped workloads that ride the R8 atlas like text.
    DrawVideo = 17,            // the VIDEO HOLE PUNCH (gpu-renderer.md §7.3): ERASES the already-painted UI pixels under
                               // Dst toward premultiplied zero (dst' = dst×(1−VideoReady×cov)) so the DComp video visual
                               // z-BELOW the premultiplied UI swapchain shows through. Rides the RoundRect SDF shader on a
                               // third DestOut PSO (SrcBlend=ZERO, DestBlend=INV_SRC_ALPHA) — no new shader, and coverage
                               // AA, per-corner radii, and both clip tiers come free. Painter order only: later siblings
                               // (letterbox bars, transport chrome) simply repaint over the hole.
    EraseRoundRect = 18,       // the GENERAL rounded-rect ERASE (gpu-renderer.md §7.4): dst' = dst×(1−Strength×cov×Opacity).
                               // The SAME SDF shader on the SAME DestOut PSO as DrawVideo — no new shader/PSO/RHI method —
                               // but with no surface identity: it is pure geometry, so it is not a video anything. Its one
                               // use is CUTOUTS inside an opacity group (the drop-spotlight scrim band): fill the group
                               // opaque, erase the holes, composite once at the group alpha ⇒ a scrim with rounded,
                               // anti-aliased windows over the compatible drop targets. Emitted OUTSIDE a group it erases
                               // the canvas itself — that is DrawVideo's contract, use that opcode instead.
    FillPath = 19,             // tessellated path fill (gpu-renderer.md §5): a triangle-soup fill realized once by
                               // PathRealizationCache.Shared and referenced here by (VtxStart,VtxCount,IdxStart,IdxCount)
                               // into its retained slab. Pure geometry — exact under a span translation (patch Transform
                               // only), safe under a damage-clamped scissor replay.
    StrokePath = 20,           // tessellated path stroke (gpu-renderer.md §5): same realization-cache contract as
                               // FillPath, plus a per-frame TrimStart/TrimEnd/dash uniform that never touches the
                               // realization key — so a 60 Hz stroke-trim draw-on still hits the SAME cached tessellation.
    PushStencilClip = 21,      // tier-3 STENCIL PATH CLIP (gpu-renderer.md §6): a mask pre-pass from a
                               // PathRealizationCache fill realization; every draw until the matching pop is
                               // stencil-ref tested against it. DeviceRect is ALSO the tier-1 scissor for the whole
                               // scope, so damage/headless-balance/scissor machinery keeps working unchanged.
    PopStencilClip = 22,       // closes the scope; RE-CARRIES the same realization so the backend can DECR_SAT-erase
                               // an inner nesting level without keeping a geometry stack of its own.
    CompositeSlice = 23,       // the retained-tile SLICE MARKER (docs/plans/scroll-gpu-retained-tiles-implementation.md
                               // §A.2): a recorder-internal op that stands, inside a slice's own stream, in the paint
                               // position of a CHILD slice (a scroll content root, an effect root, a thumb, an item band)
                               // whose commands live in that child's own arena. Never reaches a backend decoder: the
                               // composite seam (IGpuDevice.SubmitComposite) consumes slices by descriptor — the
                               // marker splits a slice's stream into segments. See CompositeSliceCmd.
    DrawSeries = 24,           // one CHUNK (≤ 32 inline samples) of a SeriesEl sample series (gpu-renderer.md §3.1): a baseline
                               // / mirrored area or a stroke ribbon through BOUND samples with a ≤ 4-stop gradient by
                               // amplitude. Fixed-size POD (nothing variable-length rides the stream, so clean-span reuse
                               // and every TryBodySize walker stay valid) — a series of N samples is ⌈(N−1)/31⌉ chunks.
                               // See DrawSeriesCmd.
    DrawSprites = 26,          // one CHUNK (≤ 16 inline sprites) of a SpriteFieldEl (visualizer F5): discs / capsules / streaks /
                               // segments from a bound instance buffer, expanded by the GPU into SDF quads. See DrawSpritesCmd.
    SetBlend = 25,             // the PAINT BLEND for the primitives that follow in this stream (visualizer F4): SetBlendCmd.Mode
                               // 0 = SrcOver, 1 = Additive (colour ONE/ONE, alpha ZERO/ONE — inside a transparent-cleared tile
                               // the tile then composites Over the page as page + glow). The recorder emits it in balanced
                               // pairs around an additive subtree (BoxEl.Blend) or leaf (SeriesEl/SpriteFieldEl.Blend), so a
                               // stream always ends SrcOver. Rects, gradients, series and sprites honour it; glyphs, images
                               // and paths stay SrcOver. No bounds of its own: every tile replay keeps it.
}

/// <summary>The payload of <see cref="DrawOp.SetBlend"/>: the <see cref="PaintBlend"/> as an int.</summary>
public readonly record struct SetBlendCmd(int Mode);

/// <summary>16 inline sprites (C# inline array) — the chunk payload of <see cref="DrawSpritesCmd"/>.</summary>
[InlineArray(16)]
public struct Sprites16 { private Sprite _e0; }

/// <summary>One CHUNK of a <c>SpriteFieldEl</c> (DrawOp.DrawSprites, visualizer F5): ≤ 16 sprites inline, so nothing
/// variable-length rides the stream. <see cref="Rect"/> is the union of THIS chunk's sprite bounds (kernel-exact,
/// rotation-safe; node-local) — what cull, slice bounds and damage use. A plain struct (the inline array must not enter a
/// generated Equals); a slice translation patches <see cref="Transform"/> only.</summary>
public struct DrawSpritesCmd
{
    public RectF Rect;
    public Affine2D Transform;
    public float Opacity;
    public int Kernel, Count, Total, Index;
    public Sprites16 S;
}

/// <summary>What the composite applies around a <see cref="CompositeSliceCmd"/>'s child slice, and in which space its
/// parameters live. <see cref="OuterClip"/> = push <see cref="CompositeSliceCmd.OuterClip"/> first (an item
/// band's viewport-fixed clip); <see cref="Layer"/> = push <see cref="CompositeSliceCmd.Layer"/> (an effect slice's
/// opacity / self-blur / edge-fade group, or a band's top feather); <see cref="InnerClip"/> = push
/// <see cref="CompositeSliceCmd.InnerClip"/> right after the layer (the self-blur SOURCE clip); <see cref="ParamsUp"/> =
/// the clip/layer parameters are expressed in the space ONE slice level above the containing stream (an item band's
/// clip is fixed to the VIEWPORT while its rows ride the content's translation); <see cref="DistributeFade"/> = the
/// layer is an edge fade that passed the RECORD-time half of the distribution test (fade mode, alpha 1, no own paint): it
/// is cut without spending the effect budget, and the composite plan distributes it as an analytic per-item feather
/// whenever the placement-time half holds (gpu-renderer.md §13.1e); <see cref="StickyClip"/> = the slice root's
/// <c>.StickyClip</c> (its <c>NodePaint.ClipRect</c>, a ClipTop scroll pose) is NOT in the recorded bytes: the composite
/// plan applies it as a clip on the slice's item(s) at placement, read from the node's current pose, so a page scroll that
/// walks the band line through the slice records nothing (gpu-renderer.md §13.1e); <see cref="FadeWhileStuck"/> = the
/// layer is an <c>EdgeFadeSpec.WhileStuck</c> edge fade on a <see cref="StickyClip"/> slice: the composite plan applies it
/// only on a turn whose sticky clip is ENGAGED (posed at the band line) and places the slice with no layer otherwise — the
/// feather switches on the same render turn as the cut, never a re-render later.</summary>
[Flags]
public enum CompositeSliceFlags : int { None = 0, OuterClip = 1, Layer = 2, InnerClip = 4, ParamsUp = 8, DistributeFade = 16, StickyClip = 32, FadeWhileStuck = 64 }

/// <summary>The payload of <see cref="DrawOp.CompositeSlice"/>. (<see cref="NodeIndex"/>, <see cref="Gen"/>,
/// <see cref="Sub"/>) is the child slice's stable key (the scene node it was cut at + its role: 0 main, 1 thumb, 2 item
/// band, 3 pinned band). <see cref="Kind"/> is its <c>SliceKind</c>. <see cref="Clip"/> is the composite clip in effect at
/// the marker — the analytic clip its composite item draws under (the containing stream's space, or one level up with
/// <see cref="CompositeSliceFlags.ParamsUp"/>). <see cref="OuterClip"/>/<see cref="Layer"/>/<see cref="InnerClip"/>/
/// <see cref="PopRect"/> are the group parameters that USED to be inline ops (see <see cref="CompositeSliceFlags"/>);
/// <see cref="SortKey"/>/<see cref="LayerSortKey"/> the sort keys those ops carried.</summary>
public readonly record struct CompositeSliceCmd(int NodeIndex, uint Gen, int Sub, int Kind, int Flags, RectF Clip,
    ClipCmd OuterClip, PushLayerCmd Layer, ClipCmd InnerClip, RectF PopRect, ulong SortKey, ulong LayerSortKey)
{
    public bool Has(CompositeSliceFlags f) => (Flags & (int)f) != 0;
}

/// <summary>Which CLIP TIER a scope in the command stream establishes (gpu-renderer.md §6's three-tier table). The
/// as-built stand-in for the spec's <c>ClipEntry.Kind</c> field: there is no ClipTable slab as-built — clips are
/// stream-scoped push/pop ops — so this exists for DIAGNOSTICS and headless modelling, naming the tier a given scope
/// belongs to. <see cref="ScissorRect"/> = a plain <see cref="DrawOp.PushClip"/>; <see cref="SdfRoundRect"/> = the
/// same op carrying a non-zero <see cref="ClipCmd.CornerRadius"/>; <see cref="StencilPath"/> = a
/// <see cref="DrawOp.PushStencilClip"/> scope.</summary>
public enum ClipKind : byte { ScissorRect = 0, SdfRoundRect = 1, StencilPath = 2 }

/// <summary>Per-opcode command counts for the current <see cref="DrawList"/>. Stored as scalar fields so the host can
/// log the failed frame's shape after device loss without reparsing the command stream or retaining payload bytes.</summary>
public struct DrawListOpcodeStats
{
    public int FillRoundRect, DrawGlyphRun, PushClip, PopClip, DrawImage, DrawRoundRectStroke, DrawShadow;
    public int DrawGradientRect, PushLayer, PopLayer, DrawGradientStroke, DrawArc, DrawPolylineStroke, DrawTabShape, DrawGlyphRunGradient;
    public int DrawIconMask, DrawVideo, EraseRoundRect;
    public int FillPath, StrokePath;
    public int PushStencilClip, PopStencilClip;
    /// <summary>Series chunks (<see cref="DrawOp.DrawSeries"/>) — a SeriesEl of N samples records ⌈(N−1)/31⌉ of these.</summary>
    public int DrawSeries;
    /// <summary>Paint-blend switches (<see cref="DrawOp.SetBlend"/>).</summary>
    public int SetBlend;
    /// <summary>Sprite chunks (<see cref="DrawOp.DrawSprites"/>) — a SpriteFieldEl of N sprites records ⌈N/16⌉ of these.</summary>
    public int DrawSprites;
    /// <summary>Retained-tile slice markers (<see cref="DrawOp.CompositeSlice"/>) — a span whose stats carry any holds a
    /// child slice's paint position.</summary>
    public int CompositeSlice;
    /// <summary>PushLayer commands specifically of <see cref="LayerKind.Acrylic"/> (a subset of <see cref="PushLayer"/>,
    /// which does not discriminate kind). Incremented only by the raw <see cref="DrawList.PushLayer"/> emitter — the
    /// Opacity/Blur/EdgeFade convenience methods pass a different <c>Kind</c> and do not touch this field. Lets a caller
    /// that only has a subtree's aggregate <see cref="DrawSpan.OpcodeStats"/> answer "does this span contain an Acrylic
    /// layer" without re-walking it.</summary>
    public int Acrylic;

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
            case DrawOp.DrawIconMask: DrawIconMask++; break;
            case DrawOp.DrawVideo: DrawVideo++; break;
            case DrawOp.EraseRoundRect: EraseRoundRect++; break;
            case DrawOp.FillPath: FillPath++; break;
            case DrawOp.StrokePath: StrokePath++; break;
            case DrawOp.PushStencilClip: PushStencilClip++; break;
            case DrawOp.PopStencilClip: PopStencilClip++; break;
            case DrawOp.CompositeSlice: CompositeSlice++; break;
            case DrawOp.DrawSeries: DrawSeries++; break;
            case DrawOp.SetBlend: SetBlend++; break;
            case DrawOp.DrawSprites: DrawSprites++; break;
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
        DrawIconMask += other.DrawIconMask;
        DrawVideo += other.DrawVideo;
        EraseRoundRect += other.EraseRoundRect;
        FillPath += other.FillPath;
        StrokePath += other.StrokePath;
        PushStencilClip += other.PushStencilClip;
        PopStencilClip += other.PopStencilClip;
        CompositeSlice += other.CompositeSlice;
        DrawSeries += other.DrawSeries;
        Acrylic += other.Acrylic;
    }

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
        DrawIconMask = DrawIconMask - other.DrawIconMask,
        DrawVideo = DrawVideo - other.DrawVideo,
        EraseRoundRect = EraseRoundRect - other.EraseRoundRect,
        FillPath = FillPath - other.FillPath,
        StrokePath = StrokePath - other.StrokePath,
        PushStencilClip = PushStencilClip - other.PushStencilClip,
        PopStencilClip = PopStencilClip - other.PopStencilClip,
        CompositeSlice = CompositeSlice - other.CompositeSlice,
        DrawSeries = DrawSeries - other.DrawSeries,
        Acrylic = Acrylic - other.Acrylic,
    };

    public override readonly string ToString()
        => $"fill={FillRoundRect} glyph={DrawGlyphRun} glyphGrad={DrawGlyphRunGradient} clip={PushClip}/{PopClip} img={DrawImage} stroke={DrawRoundRectStroke} shadow={DrawShadow} grad={DrawGradientRect}/{DrawGradientStroke} layer={PushLayer}/{PopLayer} arc={DrawArc} poly={DrawPolylineStroke} tab={DrawTabShape} icon={DrawIconMask} video={DrawVideo} erase={EraseRoundRect} fillPath={FillPath} strokePath={StrokePath} stencil={PushStencilClip}/{PopStencilClip} slice={CompositeSlice} series={DrawSeries} acrylic={Acrylic}";
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
// InMotion is a QUANTIZED 0..255 text-motion SOFTNESS (see DrawList.QuantizeMotionSoft), not the boolean the name
// implies — kept as the same field/slot so the payload size and every rebase path stay unchanged. 0 = snap the run
// onto the glyph atlas's sub-pixel phase grid (crisp); 255 = apply no correction, letting the run sit at its natural
// fractional device Y so the LINEAR/CLAMP atlas sampler softens it. DELETED at the source (audit 2026-09-22, cause
// #2 — SceneRecorder.TextMotionSoftness, which used to derive this from the enclosing viewport's live scroll speed,
// no longer exists): every call site now leaves this at its default, 0, so every run records crisp regardless of
// scroll speed. The field/slot stays (renderer + GPU upload still read it) so the payload layout is unchanged.
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
// device-space box and the RHI ADDITIONALLY clamps RoundRect, Image, and Gradient pipeline primitives to the
// rounded-box SDF of (RoundedRect, CornerRadius) — the animated-clip-with-Corners path (AnimChannel.ClipL/T/R/B on a
// rounded surface). Scope (documented honestly): glyph runs, arcs and polylines still clip rectangularly via the
// scissor; axis-aligned transforms only (the
// same caveat the tier-1 scissor already has).
public readonly record struct ClipCmd(RectF DeviceRect, RectF RoundedRect = default, float CornerRadius = 0f);
// Tier-3 (stencil) path clip — the PUSH (gpu-renderer.md §6). A mask pre-pass draws the realization referenced by
// VtxStart/VtxCount/IdxStart/IdxCount (PathRealizationCache.Shared's retained slab, the FillPathCmd contract exactly,
// with the same slab-eviction posture: quarantine window + BeginFrame-only compaction) into the stencil buffer through
// <see cref="Transform"/>; every draw until the matching PopStencilClip is stencil-ref tested against it.
// <see cref="DeviceRect"/> is the clip's device-space AABB ALREADY intersected with the enclosing clip by the recorder,
// so it doubles as the tier-1 SCISSOR for the whole scope — a stencil clip IS also a scissor push, which is why every
// existing scissor/damage/headless-balance mechanism keeps working with no special case.
// HARD EDGE BY DESIGN: the mask pass discards fringe pixels below coverage 0.5 (the tessellator's AA fringe must not
// smear the mask), so the silhouette lands on the half-coverage line at device-pixel resolution. Anti-aliased path
// clipping is §7.1's PushLayer route, deliberately not this tier.
// <see cref="RealizationId"/> mirrors <see cref="FillPathCmd.RealizationId"/> (reserved, 0). <see cref="Rule"/> is
// metadata — the winding is already baked into the triangle soup by tessellation — kept for parity with FillPathCmd
// and for headless/hit-test validation.
public readonly record struct PushStencilClipCmd(RectF DeviceRect, int RealizationId,
    int VtxStart, int VtxCount, int IdxStart, int IdxCount, byte Rule, Affine2D Transform);
// Tier-3 (stencil) path clip — the POP. RE-CARRIES the push's geometry so the backend can DECR_SAT-erase an INNER
// nesting level without keeping a geometry stack of its own (a self-describing stream, this file's idiom).
// <see cref="DeviceRect"/> is the same scope rect the push carried, so the scissor pop is symmetric.
public readonly record struct PopStencilClipCmd(RectF DeviceRect, int RealizationId,
    int VtxStart, int VtxCount, int IdxStart, int IdxCount, Affine2D Transform);
// An image quad. <see cref="ImageId"/> is the ImageCache handle; <see cref="Ready"/>==0 ⇒ draw <see cref="Placeholder"/>
// (decode in flight). The GPU leaf samples the uploaded texture for the handle when ready (needs-pixels).
// <see cref="UvRect"/> is the content-fit sub-rect in 0..1 source space ((0,0,1,1) = whole texture): the recorder
// bakes ImageFit.Cover crops here; the device composes it with the texture's atlas cell before sampling.
public readonly record struct DrawImageCmd(RectF Rect, CornerRadius4 Radii, int ImageId, int Ready, ColorF Placeholder,
    Affine2D Transform, float Opacity, RectF UvRect, float FadeStartMs = float.NaN, float FadeDurationMs = 0f,
    int FadeEasing = 0, ColorF Overlay = default, int MaskEdges = 0, float MaskLeft = 0f, float MaskTop = 0f,
    float MaskRight = 0f, float MaskBottom = 0f, int MaskFalloff = 0, float MaskIntensity = 0f, float Saturation = 1f);
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
// Begin/end a layer (gpu-renderer.md §13 — on the primary target every layer is an effect slice, composited by the
// retained-tile composite). Kind = Acrylic (0, the default): the backdrop under DeviceRect is blurred, tinted, noised
// and luminosity-washed; the subtree between the two draws on top.
// Kind = Opacity (1): the subtree renders at FULL alpha offscreen, then composites ONCE at <see cref="GroupAlpha"/>
// (flat group opacity — no double-blend of overlapping children); the
// acrylic fields (Tint/Fallback/TintOpacity/BlurSigma/NoiseOpacity/LuminosityOpacity) are unused for this kind.
public readonly record struct PushLayerCmd(RectF DeviceRect, CornerRadius4 Radii, ColorF Tint, ColorF Fallback, float TintOpacity, float BlurSigma, float NoiseOpacity, float LuminosityOpacity,
    int Kind = 0, float GroupAlpha = 1f,
    // EdgeFade (Kind == 3): per-edge feather band depth in DEVICE px (0 = edge disabled), falloff curve, fade intensity,
    // and enabled-edge bit mask. CompositeClip is the inherited active device-space clip for EdgeFade AND self-blur;
    // it bounds the offscreen result that can reach the target. The rounded-corner radii come from Radii.
    // For Kind == Opacity the same field carries the group's DRAWN EXTENT (the recorder's accumulated subtree draw
    // bounds ∩ the enclosing clip), back-patched at PopLayer — see PatchOpacityLayerExtent. An EMPTY rect there (the
    // default) means "extent unknown" and the backend composites over the whole target.
    float FadeBandL = 0f, float FadeBandT = 0f, float FadeBandR = 0f, float FadeBandB = 0f, int FadeFalloff = 0, float FadeIntensity = 1f, int FadeEdges = 0,
    RectF CompositeClip = default,
    // Acrylic-only: feather the frost in from the TOP over this FRACTION of the layer height (0 = hard edge). See AcrylicSpec.FeatherTop.
    float FeatherFrac = 0f);
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
// A ThemedIcon vector-layer mask (see DrawOp.DrawIconMask). <see cref="Rect"/> is the node-local (DIP) icon box;
// <see cref="PathId"/> is the interned IconGeometryTable.Shared geometry; <see cref="Tint"/> is the per-instance,
// theme-resolved layer color (bound → re-fires on RethemeAll with NO re-raster — the mask is colorless). The backend
// rasterizes the mask lazily on a (PathId, device-px) atlas miss and appends ONE glyph instance tinted by Tint, so it
// batches in the glyph pass (text-like z within a layer scope). NOT the §5 tessellation lane (see the opcode note).
public readonly record struct DrawIconMaskCmd(RectF Rect, ColorF Tint, int PathId, Affine2D Transform, float Opacity);
// The video hole punch (see DrawOp.DrawVideo). <see cref="Dst"/> is the node-local (DIP) video box with <see cref="Radii"/>
// per-corner rounding, placed by the baked world <see cref="Transform"/> — the FillRoundRect shape contract exactly.
// <see cref="SurfaceId"/> is the video registry's slot token: diagnostic at replay only, since the presenter positions the
// DComp visual independently. <see cref="VideoReady"/> (0..1) is the ERASE STRENGTH — the backend writes premultiplied
// (0,0,0,VideoReady·cov·Opacity) through a DestOut blend, so 1 fully clears the hole and 0 records nothing.
public readonly record struct DrawVideoCmd(RectF Dst, CornerRadius4 Radii, int SurfaceId, float VideoReady, Affine2D Transform, float Opacity);
// A general rounded-rect ERASE (DrawOp.EraseRoundRect): dst' = dst x (1 - Strength*coverage*Opacity), premultiplied.
// Same SDF geometry contract as FillRoundRectCmd, drawn through the DestOut PSO DrawVideo already builds. Pure geometry:
// no surface identity, no registry. The scrim band uses it INSIDE an opacity group to cut spotlight windows.
public readonly record struct EraseRoundRectCmd(RectF Rect, CornerRadius4 Radii, float Strength, Affine2D Transform, float Opacity);

// A tessellated path FILL (see DrawOp.FillPath, gpu-renderer.md §5). Rect is the node-local box (metadata, like every
// other cmd's Rect — the actual triangle-soup positions are PathRealizationCache.Shared's PathVertex.X/Y, in the
// path's own authored coordinate space; Transform maps that space to device). VtxStart/VtxCount/IdxStart/IdxCount
// index PathRealizationCache.Shared's retained slab (a cache HIT reuses them across every frame the geometry/style/
// scale key doesn't change — zero re-tessellation). RealizationId is reserved for a future GPU-resident realization
// handle (§1.5's PathPipeline residency); unpopulated (0) until that lands — the CPU-side offsets already fully
// identify the realization for the headless/decode paths that exist today. Self-describing POD: a clean-span memcpy
// and a slice translation both work with only a Transform patch (see Render/SliceRecorder).
public readonly record struct FillPathCmd(RectF Rect, ColorF Fill, int RealizationId,
    int VtxStart, int VtxCount, int IdxStart, int IdxCount, byte Rule,
    Affine2D Transform, float Opacity);
// A tessellated path STROKE (see DrawOp.StrokePath). Same realization-slab contract as FillPathCmd (RealizationId
// reserved, see its doc), plus the trim/dash/arc-length uniforms a draw-on or dashed stroke needs PER FRAME.
// TrimStart/TrimEnd/DashOn/DashOff/TrimMode are DELIBERATELY payload-only, never folded into the realization
// key (PathRealizationCache.TryRealizeStroke's key is geometry+style+scale) — so a 60 Hz stroke-trim animation
// (AnimChannel.StrokeTrimStart/End) hits the SAME cached tessellation every frame; only this uniform changes.
// ArcLenPx (device px, from PathStroker via PathRef.ArcLenPx) is the total contour length, for a dash-phase/draw-on
// shader that needs it without re-walking the geometry. TrimMode mirrors PathTrimSpace (0 = PerContour, 1 = WholePath).
public readonly record struct StrokePathCmd(RectF Rect, ColorF Color, int RealizationId,
    int VtxStart, int VtxCount, int IdxStart, int IdxCount,
    float TrimStart, float TrimEnd, float DashOn, float DashOff, float ArcLenPx, byte TrimMode,
    Affine2D Transform, float Opacity);

/// <summary>32 inline samples (C# inline array) — the chunk payload of <see cref="DrawSeriesCmd"/>.</summary>
[InlineArray(32)]
public struct Samples32 { private float _e0; }

/// <summary>One CHUNK of a sample series (DrawOp.DrawSeries, gpu-renderer.md §3.1). A series of N samples is recorded as
/// ⌈(N−1)/31⌉ chunks of ≤ 32 samples sharing one edge sample, so nothing variable-length rides the stream (every
/// walker frames it through RepaintStreamSafety.TryBodySize) and clean-span reuse stays valid — the data IS the span.
/// Sample x = X0 + i·Dx (node-local); heights are fractions of Rect.H (Mirrored: of Rect.H/2) about Baseline (a fraction
/// of Rect.H) scaled by Amplitude, clamped to the Rect by the vertex shader; Shape 0 = baseline area, 1 = mirrored area,
/// 2 = a Thickness-wide stroke ribbon. ≤ 4 gradient stops BY
/// AMPLITUDE (C0..C3 at O0..O3; StopCount 1 = solid). <see cref="Rect"/> is THIS chunk's box (cull / slice bounds /
/// damage): the chunk's X span and the FULL node box's Y/H. A plain struct, not a record: the inline array must not
/// enter a generated Equals. Self-describing POD: a slice translation patches Transform only (Render/DrawOpTranslate).</summary>
public struct DrawSeriesCmd
{
    public RectF Rect;            // THIS chunk's box (cull / slice bounds), node-local — the full node box's Y/H, the chunk's X span
    public Affine2D Transform;
    public float Opacity;
    public int Shape, Count, Total, Index;
    public float X0, Dx, Baseline, Amplitude, Thickness;
    public ColorF C0, C1, C2, C3;
    public float O0, O1, O2, O3;
    public int StopCount;
    /// <summary>Bit 0: AA fringe on a ribbon (Stroke/Polar). Bit 1: the gradient runs ALONG the series (by sample index),
    /// not by amplitude.</summary>
    public int Flags;
    /// <summary>The samples just outside this chunk (the neighbour of S[0] and of S[Count−1]), so a ribbon's tangent and a
    /// Polar loop's closing seam are continuous across chunk edges.</summary>
    public float Prev, Next;
    public Samples32 S;
}

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

    /// <summary>Pack 0..1 text-motion softness into the payload's existing int slot as 0..255. The slot used to carry a
    /// bare in-motion BOOL; a scalar fits the same 4 bytes and is what lets the softness track scroll SPEED instead of
    /// being all-or-nothing. 0 = crisp (snap to the glyph atlas sub-pixel phase grid), 255 = full smear.</summary>
    internal static int QuantizeMotionSoft(float soft)
        => soft > 0f ? (soft >= 1f ? 255 : (int)(soft * 255f + 0.5f)) : 0;   // NaN ⇒ 0 (crisp)

    public void DrawGlyphRun(in RectF bounds, in ColorF color, StringId text, StringId family, float fontSize, int weight, int wrap, int trim, int maxLines,
        float charSpacing, float lineHeight, int lineStacking, int lineBounds, in Affine2D transform, float opacity, ulong sortKey = 0,
        int spanRunId = 0, bool forceColor = false, float motionSoft = 0f)
    {
        WriteOp(DrawOp.DrawGlyphRun);
        WritePayload(new DrawGlyphRunCmd(bounds, color, text, family, fontSize, weight, wrap, trim, maxLines,
            charSpacing, lineHeight, lineStacking, lineBounds, transform, opacity, spanRunId, forceColor ? 1 : 0, QuantizeMotionSoft(motionSoft)));
        PushSort(sortKey);
    }

    /// <summary>A glyph run filled by a left→right wipe (the <c>GlyphWipe</c> primitive): <paramref name="split"/>
    /// (0..1 along the run's content in READING ORDER — visual lines laid end-to-end over glyph edges) divides
    /// <paramref name="before"/> (sung) from <paramref name="after"/> (unsung), with a <paramref name="softness"/>-wide
    /// soft boundary the replay remaps so split==1 fully clears the trailing edge; <paramref name="lift"/> floats a
    /// just-passed glyph up. Reuses the glyph pipeline (per-instance color/offset computed at replay) — no new shader.</summary>
    public void DrawGlyphRunGradient(in RectF bounds, StringId text, StringId family, float fontSize, int weight, int wrap, int trim, int maxLines,
        float charSpacing, float lineHeight, int lineStacking, int lineBounds, in Affine2D transform, float opacity,
        in ColorF before, in ColorF after, float split, float softness, float lift, ulong sortKey = 0, int spanRunId = 0, float motionSoft = 0f)
    {
        WriteOp(DrawOp.DrawGlyphRunGradient);
        WritePayload(new DrawGlyphRunGradientCmd(bounds, text, family, fontSize, weight, wrap, trim, maxLines,
            charSpacing, lineHeight, lineStacking, lineBounds, transform, opacity, before, after, split, softness, lift, spanRunId, QuantizeMotionSoft(motionSoft)));
        PushSort(sortKey);
    }

    /// <summary>Push a tier-1 scissor clip (device-space, pre-intersected). Pair with <see cref="PopClip"/>.</summary>
    public void PushClip(in RectF deviceRect, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PushClip);
        WritePayload(new ClipCmd(deviceRect));
        PushSort(sortKey);
    }

    /// <summary>Push a tier-2 ROUNDED clip: the scissor still clamps to <paramref name="deviceRect"/>, and RoundRect,
    /// Image, and Gradient pipeline primitives additionally clamp to the rounded-box SDF of (<paramref name="roundedRect"/>,
    /// <paramref name="cornerRadius"/>) — both device-space. Pair with <see cref="PopClip"/>. See <see cref="ClipCmd"/>
    /// for the honest coverage scope.</summary>
    public void PushClipRounded(in RectF deviceRect, in RectF roundedRect, float cornerRadius, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PushClip);
        WritePayload(new ClipCmd(deviceRect, roundedRect, MathF.Max(0f, cornerRadius)));
        PushSort(sortKey);
    }

    /// <summary>Record a sprite field as chunked <see cref="DrawSpritesCmd"/>s of 16; each chunk's rect is the union of its
    /// sprites' bounds. Alloc-free.</summary>
    public void Sprites(SpriteKernel kernel, ReadOnlySpan<Sprite> sprites, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        int n = sprites.Length;
        for (int start = 0; start < n; start += 16)
        {
            int count = Math.Min(16, n - start);
            var cmd = new DrawSpritesCmd { Transform = transform, Opacity = opacity, Kernel = (int)kernel, Count = count, Total = n, Index = start };
            float l = float.PositiveInfinity, t = float.PositiveInfinity, r = float.NegativeInfinity, b = float.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                Sprite s = sprites[start + i];
                cmd.S[i] = s;
                RectF sb = s.Bounds(kernel);
                if (sb.X < l) l = sb.X;
                if (sb.Y < t) t = sb.Y;
                if (sb.X + sb.W > r) r = sb.X + sb.W;
                if (sb.Y + sb.H > b) b = sb.Y + sb.H;
            }
            cmd.Rect = new RectF(l, t, MathF.Max(0f, r - l), MathF.Max(0f, b - t));
            WriteOp(DrawOp.DrawSprites);
            WritePayload(in cmd);
            PushSort(sortKey);
        }
    }

    /// <summary>Switch the paint blend for the primitives that follow (<see cref="DrawOp.SetBlend"/>). Alloc-free.</summary>
    public void SetBlend(PaintBlend mode, ulong sortKey = 0)
    {
        WriteOp(DrawOp.SetBlend);
        WritePayload(new SetBlendCmd((int)mode));
        PushSort(sortKey);
    }

    public void PopClip(ulong sortKey = 0)
    {
        WriteOp(DrawOp.PopClip);
        PushSort(sortKey);
    }

    /// <summary>Push a tier-3 STENCIL PATH CLIP (see <see cref="PushStencilClipCmd"/>): <paramref name="deviceRect"/>
    /// is the clip's device-space AABB already intersected with the enclosing clip (it doubles as the scope's tier-1
    /// scissor), and <paramref name="clipPath"/> is the <c>PathRealizationCache.Shared</c> fill realization the mask
    /// pre-pass draws. <paramref name="rule"/> is the <c>FillRule</c> byte the realization was keyed with
    /// (diagnostic/replay parity — the triangle soup already bakes it). Pair with <see cref="PopStencilClip"/>.</summary>
    public void PushStencilClip(in RectF deviceRect, in PathRef clipPath, byte rule, in Affine2D transform, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PushStencilClip);
        WritePayload(new PushStencilClipCmd(deviceRect, 0, clipPath.VtxStart, clipPath.VtxCount,
            clipPath.IdxStart, clipPath.IdxCount, rule, transform));
        PushSort(sortKey);
    }

    /// <summary>Close a tier-3 stencil scope. Re-carries the push's <paramref name="clipPath"/>/
    /// <paramref name="transform"/> so the backend can DECR_SAT-erase an inner nesting level without a geometry stack
    /// (see <see cref="PopStencilClipCmd"/>).</summary>
    public void PopStencilClip(in RectF deviceRect, in PathRef clipPath, in Affine2D transform, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PopStencilClip);
        WritePayload(new PopStencilClipCmd(deviceRect, 0, clipPath.VtxStart, clipPath.VtxCount,
            clipPath.IdxStart, clipPath.IdxCount, transform));
        PushSort(sortKey);
    }

    public void DrawImage(in RectF rect, in CornerRadius4 radii, int imageId, bool ready, in ColorF placeholder, in Affine2D transform, float opacity, in RectF uvRect, float fadeStartMs = float.NaN, float fadeDurationMs = 0f, int fadeEasing = 0, ulong sortKey = 0,
                          ColorF overlay = default, int maskEdges = 0, float maskLeft = 0f, float maskTop = 0f,
                          float maskRight = 0f, float maskBottom = 0f, int maskFalloff = 0, float maskIntensity = 0f,
                          float saturation = 1f)
    {
        // Radii.Full is an authoring sentinel for layout-derived circles/capsules. Normalize it at the portable command
        // boundary so every backend sees valid rounded-box geometry (and cached/replayed image paths cannot bypass it).
        float maxRadius = MathF.Min(rect.W, rect.H) * 0.5f;
        var clampedRadii = new CornerRadius4(
            MathF.Min(radii.TopLeft, maxRadius), MathF.Min(radii.TopRight, maxRadius),
            MathF.Min(radii.BottomRight, maxRadius), MathF.Min(radii.BottomLeft, maxRadius));
        WriteOp(DrawOp.DrawImage);
        WritePayload(new DrawImageCmd(rect, clampedRadii, imageId, ready ? 1 : 0, placeholder, transform, opacity, uvRect,
            fadeStartMs, fadeDurationMs, fadeEasing, overlay, maskEdges, maskLeft, maskTop, maskRight, maskBottom,
            maskFalloff, maskIntensity, saturation));
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

    public void PushLayer(in RectF deviceRect, in CornerRadius4 radii, in ColorF tint, in ColorF fallback, float tintOpacity, float blurSigma, float noiseOpacity, float luminosityOpacity, ulong sortKey = 0, float featherFrac = 0f, float groupAlpha = 1f)
    {
        WriteOp(DrawOp.PushLayer);
        WritePayload(new PushLayerCmd(deviceRect, radii, tint, fallback, tintOpacity, blurSigma, noiseOpacity, luminosityOpacity,
            GroupAlpha: Math.Clamp(groupAlpha, 0f, 1f), FeatherFrac: featherFrac));
        PushSort(sortKey);
        _opcodeStats.Acrylic++;   // the raw PushLayer emitter's default Kind IS Acrylic (0) — see DrawListOpcodeStats.Acrylic
    }

    /// <summary>Begin a FLAT opacity group (<see cref="LayerKind.Opacity"/>): everything until the matching
    /// <see cref="PopLayer"/> renders at full alpha offscreen and composites once at <paramref name="groupAlpha"/> —
    /// WinUI Composition LayerVisual semantics (no double-blend of overlapping children). Subtree commands should be
    /// recorded with opacity relative to 1, NOT pre-multiplied by the group alpha.</summary>
    public void PushOpacityLayer(in RectF deviceRect, in CornerRadius4 radii, float groupAlpha, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PushLayer);
        WritePayload(OpacityLayerCmd(deviceRect, radii, groupAlpha));
        PushSort(sortKey);
    }

    /// <summary>The payload <see cref="PushOpacityLayer"/> writes (shared with the slice marker's carried layer).</summary>
    public static PushLayerCmd OpacityLayerCmd(in RectF deviceRect, in CornerRadius4 radii, float groupAlpha)
        => new(deviceRect, radii, default, default, 0f, 0f, 0f, 0f,
            (int)LayerKind.Opacity, Math.Clamp(groupAlpha, 0f, 1f));

    /// <summary>Begin a per-node SELF-blur group (<see cref="LayerKind.Blur"/>): the subtree until the matching
    /// <see cref="PopLayer"/> renders at full alpha into a pooled offscreen RT, is separable-Gaussian-blurred by
    /// <paramref name="blurSigma"/> px, and composites once at <paramref name="groupAlpha"/> (so blur + fade read as one
    /// motion). The element's OWN pixels blur — not the backdrop behind it. Subtree commands record at opacity relative
    /// to 1, NOT pre-multiplied by the group alpha.</summary>
    public void PushBlurLayer(in RectF deviceRect, in CornerRadius4 radii, float blurSigma, float groupAlpha, ulong sortKey = 0, RectF compositeClip = default)
    {
        WriteOp(DrawOp.PushLayer);
        WritePayload(BlurLayerCmd(deviceRect, radii, blurSigma, groupAlpha, compositeClip));
        PushSort(sortKey);
    }

    /// <summary>The payload <see cref="PushBlurLayer"/> writes (shared with the slice marker's carried layer).</summary>
    public static PushLayerCmd BlurLayerCmd(in RectF deviceRect, in CornerRadius4 radii, float blurSigma, float groupAlpha,
        in RectF compositeClip)
        => new(deviceRect, radii, default, default, 0f, MathF.Max(0f, blurSigma), 0f, 0f,
            (int)LayerKind.Blur, Math.Clamp(groupAlpha, 0f, 1f), CompositeClip: compositeClip);

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
        WritePayload(EdgeFadeLayerCmd(deviceRect, compositeClip, radii, groupAlpha, edges, bandL, bandT, bandR, bandB,
            falloff, intensity, blurSigma));
        PushSort(sortKey);
    }

    /// <summary>The payload <see cref="PushEdgeFadeLayer"/> writes (shared with the slice marker's carried layer).</summary>
    public static PushLayerCmd EdgeFadeLayerCmd(in RectF deviceRect, in RectF compositeClip, in CornerRadius4 radii, float groupAlpha,
        int edges, float bandL, float bandT, float bandR, float bandB, int falloff, float intensity, float blurSigma)
        => new(deviceRect, radii, default, default, 0f, MathF.Max(0f, blurSigma), 0f, 0f,
            (int)LayerKind.EdgeFade, Math.Clamp(groupAlpha, 0f, 1f),
            MathF.Max(0f, bandL), MathF.Max(0f, bandT), MathF.Max(0f, bandR), MathF.Max(0f, bandB),
            falloff, Math.Clamp(intensity, 0f, 1f), edges, compositeClip);

    public void PopLayer(in RectF deviceRect, ulong sortKey = 0)
    {
        WriteOp(DrawOp.PopLayer);
        WritePayload(new PopLayerCmd(deviceRect));
        PushSort(sortKey);
    }

    /// <summary>Patch an already-emitted PLAIN-OPACITY <see cref="PushLayerCmd"/> (at the byte offset captured before the
    /// <see cref="PushOpacityLayer"/> call — i.e. the offset of its op code) with the group's DRAWN EXTENT: the recorder's
    /// accumulated subtree draw bounds, in the same device space as <see cref="PushLayerCmd.DeviceRect"/>. The extent is
    /// known only once the whole subtree has been walked, so it is written back over the payload in place — alloc-free.
    /// It rides in <see cref="PushLayerCmd.CompositeClip"/>, unused for <see cref="LayerKind.Opacity"/> otherwise (only
    /// EdgeFade/self-blur read it), so the opcode shape is unchanged. Leaving a layer UNPATCHED keeps the empty default =
    /// "extent unknown" = the whole target.</summary>
    public void PatchOpacityLayerExtent(int pushLayerByteStart, in RectF drawnExtent)
    {
        int payloadOff = pushLayerByteStart + sizeof(int);   // skip the op code int
        var span = _buf.AsSpan(payloadOff, Unsafe.SizeOf<PushLayerCmd>());
        var cmd = MemoryMarshal.Read<PushLayerCmd>(span);
        cmd = cmd with { CompositeClip = drawnExtent };
        MemoryMarshal.Write(span, in cmd);
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

    /// <summary>A ThemedIcon vector-layer mask (see <see cref="DrawIconMaskCmd"/>): the colorless coverage mask interned
    /// as <paramref name="pathId"/> in <c>IconGeometryTable.Shared</c>, tinted by <paramref name="tint"/> per instance and
    /// drawn through the glyph atlas/PSO. No new shader/PSO/texture — icons ride the R8 glyph pipeline like text.</summary>
    public void DrawIconMask(in RectF rect, in ColorF tint, int pathId, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawIconMask);
        WritePayload(new DrawIconMaskCmd(rect, tint, pathId, transform, opacity));
        PushSort(sortKey);
    }

    /// <summary>The video hole punch (see <see cref="DrawVideoCmd"/>): ERASE the already-painted UI pixels under
    /// <paramref name="dst"/> toward premultiplied zero at strength <paramref name="videoReady"/>, so the DComp video
    /// visual composited BELOW the UI swapchain shows through. Emit it AFTER everything it must erase and BEFORE the
    /// chrome that must sit over the video — the hole is painter-ordered like any other primitive.</summary>
    public void DrawVideo(in RectF dst, in CornerRadius4 radii, int surfaceId, float videoReady, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.DrawVideo);
        WritePayload(new DrawVideoCmd(dst, radii, surfaceId, Math.Clamp(videoReady, 0f, 1f), transform, opacity));
        PushSort(sortKey);
    }

    /// <summary>The general rounded-rect ERASE (see <see cref="EraseRoundRectCmd"/>): scrub the destination toward
    /// premultiplied zero by <paramref name="strength"/> x coverage x <paramref name="opacity"/>. Meant for CUTOUTS inside
    /// an opacity group (<see cref="PushOpacityLayer"/>) — outside one it erases the canvas, which is
    /// <see cref="DrawVideo"/>'s contract.</summary>
    public void EraseRoundRect(in RectF rect, in CornerRadius4 radii, float strength, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.EraseRoundRect);
        WritePayload(new EraseRoundRectCmd(rect, radii, Math.Clamp(strength, 0f, 1f), transform, opacity));
        PushSort(sortKey);
    }

    /// <summary>A tessellated path FILL (see <see cref="FillPathCmd"/>): <paramref name="pathRef"/> is the
    /// <c>PathRealizationCache.Shared</c> slab reference (fill or miss-then-fill already resolved by the caller).
    /// <paramref name="rule"/> is the <c>FillRule</c> byte value used for the realization (diagnostic/replay parity —
    /// the triangle soup itself already bakes the rule).</summary>
    public void FillPath(in RectF rect, in ColorF fill, in PathRef pathRef, byte rule, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.FillPath);
        WritePayload(new FillPathCmd(rect, fill, 0, pathRef.VtxStart, pathRef.VtxCount, pathRef.IdxStart, pathRef.IdxCount, rule, transform, opacity));
        PushSort(sortKey);
    }

    /// <summary>A tessellated path STROKE (see <see cref="StrokePathCmd"/>): <paramref name="trimStart"/>/
    /// <paramref name="trimEnd"/> (0..1, clamped here) and <paramref name="dashOn"/>/<paramref name="dashOff"/> ride the
    /// PAYLOAD only — never the realization key — so a per-frame trim/dash animation replays the SAME cached
    /// tessellation (<paramref name="pathRef"/>) with zero re-tessellation. <paramref name="pathRef"/>.ArcLenPx carries
    /// through as the command's device-px contour length.</summary>
    public void StrokePath(in RectF rect, in ColorF color, in PathRef pathRef, float trimStart, float trimEnd,
        float dashOn, float dashOff, byte trimMode, in Affine2D transform, float opacity, ulong sortKey = 0)
    {
        WriteOp(DrawOp.StrokePath);
        WritePayload(new StrokePathCmd(rect, color, 0, pathRef.VtxStart, pathRef.VtxCount, pathRef.IdxStart, pathRef.IdxCount,
            Math.Clamp(trimStart, 0f, 1f), Math.Clamp(trimEnd, 0f, 1f), MathF.Max(0f, dashOn), MathF.Max(0f, dashOff),
            pathRef.ArcLenPx, trimMode, transform, opacity));
        PushSort(sortKey);
    }

    /// <summary>Record a sample series as chunked <see cref="DrawSeriesCmd"/>s. <paramref name="rect"/> is the node box;
    /// <paramref name="samples"/> beyond <see cref="SeriesSpec.MaxSamples"/> are dropped. Alloc-free.</summary>
    public void Series(in RectF rect, in SeriesSpec spec, ReadOnlySpan<float> samples, in Affine2D transform, float opacity, ulong sortKey = 0,
                       float gradientMix = 0f)
    {
        int n = Math.Min(samples.Length, SeriesSpec.MaxSamples);
        if (n < 2 || rect.W <= 0f || rect.H <= 0f) return;
        float dx = rect.W / (n - 1);
        int stops = 1;
        ColorF c0 = spec.Color, c1 = spec.Color, c2 = spec.Color, c3 = spec.Color;
        float o0 = 0f, o1 = 1f, o2 = 1f, o3 = 1f;
        if (spec.Gradient is { } g && g.Stops is { Length: > 0 } st)
        {
            stops = Math.Min(st.Length, GradientSpec.MaxStops);
            c0 = st[0].Color; o0 = st[0].Offset;
            if (stops > 1) { c1 = st[1].Color; o1 = st[1].Offset; }
            if (stops > 2) { c2 = st[2].Color; o2 = st[2].Offset; }
            if (stops > 3) { c3 = st[3].Color; o3 = st[3].Offset; }
            // GradientMix: blend the stops toward GradientTo on stack locals (the BoxEl LerpStops rule: shared prefix only).
            if (gradientMix > 0.001f && spec.GradientTo is { } gt && gt.Stops is { Length: > 0 } ts)
            {
                float t = MathF.Min(1f, gradientMix);
                int m = Math.Min(stops, Math.Min(ts.Length, GradientSpec.MaxStops));
                if (m > 0) { c0 = ColorF.LerpLinear(c0, ts[0].Color, t); o0 += (ts[0].Offset - o0) * t; }
                if (m > 1) { c1 = ColorF.LerpLinear(c1, ts[1].Color, t); o1 += (ts[1].Offset - o1) * t; }
                if (m > 2) { c2 = ColorF.LerpLinear(c2, ts[2].Color, t); o2 += (ts[2].Offset - o2) * t; }
                if (m > 3) { c3 = ColorF.LerpLinear(c3, ts[3].Color, t); o3 += (ts[3].Offset - o3) * t; }
            }
        }
        bool polar = spec.Shape == SeriesShape.Polar;
        float baseline = float.IsNaN(spec.Baseline) ? (spec.Shape == SeriesShape.Mirrored ? 0.5f : 1f) : spec.Baseline;
        int flags = (spec.AntiAlias ? 1 : 0) | (spec.GradientAxis == SeriesGradientAxis.Along ? 2 : 0);
        for (int start = 0; start < n - 1; start += SeriesSpec.ChunkSamples - 1)
        {
            int count = Math.Min(SeriesSpec.ChunkSamples, n - start);
            int before = start - 1, after = start + count;
            float prev = before >= 0 ? samples[before] : (polar ? samples[Math.Max(0, n - 2)] : samples[0]);
            float next = after < n ? samples[after] : (polar ? samples[Math.Min(1, n - 1)] : samples[n - 1]);
            var cmd = new DrawSeriesCmd
            {
                // A Polar chunk can reach anywhere in the node box, so its cull/slice rect is the whole box.
                Rect = polar ? rect : new RectF(rect.X + start * dx, rect.Y, (count - 1) * dx, rect.H),
                Transform = transform, Opacity = opacity,
                Shape = (int)spec.Shape, Count = count, Total = n, Index = start,
                X0 = rect.X + start * dx, Dx = dx, Baseline = baseline, Amplitude = spec.Amplitude, Thickness = spec.Thickness,
                C0 = c0, C1 = c1, C2 = c2, C3 = c3, O0 = o0, O1 = o1, O2 = o2, O3 = o3, StopCount = stops,
                Flags = flags, Prev = prev, Next = next,
            };
            for (int i = 0; i < count; i++) cmd.S[i] = samples[start + i];
            WriteOp(DrawOp.DrawSeries);
            WritePayload(in cmd);
            PushSort(sortKey);
        }
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

    // ── raw access for the slice recorder (Render/SliceRecorder) and the headless composite model ───────────────────
    // The slice recorder owns one DrawList per slice ARENA; a standalone record and the headless device's composed stream
    // append whole runs. These keep the byte/sort/command/stats invariants in one place (every op = one opcode int + its
    // payload + ONE sort key).

    /// <summary>The live command bytes (valid for <see cref="BytePosition"/> bytes).</summary>
    internal byte[] RawBytes => _buf;
    /// <summary>The live sort keys (valid for <see cref="SortPosition"/> entries; one per command).</summary>
    internal ulong[] RawSortKeys => _sort;

    /// <summary>Push a layer whose payload was built by <see cref="OpacityLayerCmd"/> / <see cref="BlurLayerCmd"/> /
    /// <see cref="EdgeFadeLayerCmd"/> (the recorder builds it once: inline, or carried by a slice marker).</summary>
    internal void PushLayerCmdRaw(in PushLayerCmd cmd, ulong sortKey)
    {
        WriteOp(DrawOp.PushLayer);
        WritePayload(cmd);
        PushSort(sortKey);
    }

    /// <summary>A byte range of the PRIOR buffer (the one <see cref="CopySpanFromPrior"/> reads) — the slice recorder
    /// inspects a clean span's child markers before copying it.</summary>
    internal ReadOnlySpan<byte> PriorBytes(int byteStart, int byteLength) => _priorBuf.AsSpan(byteStart, byteLength);

    /// <summary>Emit a <see cref="DrawOp.CompositeSlice"/> marker. Returns the op's byte offset (for
    /// <see cref="PatchCompositeSlice"/>).</summary>
    internal int CompositeSlice(in CompositeSliceCmd cmd, ulong sortKey)
    {
        int at = _len;
        WriteOp(DrawOp.CompositeSlice);
        WritePayload(cmd);
        PushSort(sortKey);
        return at;
    }

    /// <summary>Read the marker whose op starts at <paramref name="opByteStart"/>.</summary>
    internal CompositeSliceCmd ReadCompositeSlice(int opByteStart)
        => MemoryMarshal.Read<CompositeSliceCmd>(_buf.AsSpan(opByteStart + sizeof(int), Unsafe.SizeOf<CompositeSliceCmd>()));

    /// <summary>Rewrite the marker whose op starts at <paramref name="opByteStart"/> in place (the opacity-group extent is
    /// only known once the slice's subtree has been walked — the marker twin of <see cref="PatchOpacityLayerExtent"/>).</summary>
    internal void PatchCompositeSlice(int opByteStart, in CompositeSliceCmd cmd)
        => MemoryMarshal.Write(_buf.AsSpan(opByteStart + sizeof(int), Unsafe.SizeOf<CompositeSliceCmd>()), in cmd);

    /// <summary>Append one op with a zeroed payload of <paramref name="payloadSize"/> bytes and return the payload span for
    /// the caller to fill (the headless composite model's per-op translate path).</summary>
    internal Span<byte> AppendOp(DrawOp op, int payloadSize, ulong sortKey)
    {
        WriteOp(op);
        Ensure(payloadSize);
        Span<byte> payload = _buf.AsSpan(_len, payloadSize);
        _len += payloadSize;
        PushSort(sortKey);
        return payload;
    }

    /// <summary>Append a verbatim, already-framed run of commands (no translation) — a standalone record's bulk path.</summary>
    internal void AppendRaw(ReadOnlySpan<byte> bytes, ReadOnlySpan<ulong> sortKeys, int commandCount, in DrawListOpcodeStats stats)
    {
        if (!bytes.IsEmpty)
        {
            Ensure(bytes.Length);
            bytes.CopyTo(_buf.AsSpan(_len));
            _len += bytes.Length;
        }
        if (!sortKeys.IsEmpty)
        {
            EnsureSort(sortKeys.Length);
            sortKeys.CopyTo(_sort.AsSpan(_sortLen));
            _sortLen += sortKeys.Length;
        }
        CommandCount += commandCount;
        _opcodeStats.Add(in stats);
    }
}
