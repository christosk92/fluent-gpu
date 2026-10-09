using System.Diagnostics;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using FluentGpu.Text;

namespace FluentGpu.Render;

/// <summary>The focus-visual brushes (WinUI FocusStrokeColorOuter/Inner + thickness). Passed into the recorder by the
/// host (which reads the theme); default = disabled, so the headless/test paths can opt in.</summary>
public readonly record struct FocusVisualStyle(ColorF Outer, ColorF Inner, float Thickness)
{
    public bool Enabled => Outer.A > 0f || Inner.A > 0f;
}

/// <summary>The text-edit decoration brushes (WinUI TextControlSelectionHighlightColor = AccentFillColorSelectedTextBackgroundBrush,
/// TextOnAccentFillColorSelectedTextBrush, and the caret = the text foreground). Passed into the recorder by the host
/// (which reads the theme), like <see cref="FocusVisualStyle"/>; default = disabled, so existing paths are untouched.</summary>
public readonly record struct TextEditStyle(ColorF SelectionFill, ColorF SelectedText, ColorF CaretColor)
{
    public bool Enabled => SelectionFill.A > 0f || CaretColor.A > 0f;
}

/// <summary>Diagnostic counts for the exact-copy gate conditions (why a clean-span copy was declined). ALWAYS ON —
/// cheap int increments against state the recorder already computed, in every build configuration. (The
/// translated-copy counters left with the translated-copy branch: a scroll is a composite parameter since the
/// retained-tile recorder partition, so nothing is ever rebased.)</summary>
public readonly record struct SpanReuseMissStats(
    int GlobalDisabled,
    int ScopedBlocked,
    int ExactDirty,
    int ExactKey,
    int ExactClip,
    int ExactCapacity);

public readonly record struct SceneRecordStats(int NodesVisited, int DrawnNodeCount, int CulledNodeCount)
{
    public int NodesCulled { get; init; }
    public int BlurCandidateCount { get; init; }
    public int BlurGroupCount { get; init; }
    public int EdgeFadeGroupCount { get; init; }
    public int SpansReused { get; init; }
    public int SpansReRecorded { get; init; }
    /// <summary>The retained-tile slice partition's census for this pass (slices walked / kept whole / effect slices /
    /// folded effect candidates / bytes recorded; <see cref="SliceRecordStats.KeptAll"/> = nothing recorded at all).</summary>
    public SliceRecordStats Slices { get; init; }
    public int SpanBytesCopied { get; init; }
    public SpanReuseDisabledReason SpanReuseDisabledReasons { get; init; }
    public SpanReuseMissStats SpanReuseMisses { get; init; }
    /// <summary>Spatial reuse-scoping (scene-memory.md): how many ancestor-chain nodes were span-reuse-BLOCKED this
    /// frame (popup/overlay/orphan/fly chains). &gt; 0 means a scoped block was active WITHOUT the old whole-tree kill.</summary>
    public int ScopedBlocks { get; init; }
    /// <summary>Retained for the host's settle latch pairing. Always 0: glyph runs carry a 0..255 softness scalar
    /// (not a boolean unsnap), so there is nothing for a follow-up frame to re-snap. Rect-only and glyph transform
    /// ticks must both skip that latch.</summary>
    public int UnsnappedGlyphSpans { get; init; }
    /// <summary>Subtrees the record walk REFUSED to descend into because the recording thread's stack was about to run
    /// out (<c>SceneRecorder.HasWalkStackHeadroom</c>): each one is a node whose own visual was emitted but whose whole
    /// subtree was NOT painted. Nonzero means the frame is visibly incomplete — a page rendering "empty" content. The
    /// host publishes it in FrameStats/wakediag so this can never again be a silent blank.</summary>
    public int DepthAborts { get; init; }
    /// <summary>The frame's REPAINT set (gpu-renderer.md §13.1) — every region whose pixels may differ from the last
    /// presented frame. A SECOND accumulator, deliberately independent of <see cref="Damage"/>: that one is the acrylic
    /// blur-cache union (transform-moved nodes only, scroll content and paint-only writes excluded by design). Never
    /// substitute one for the other.</summary>
    public RepaintDamageRegion RepaintDamage { get; init; }
}

/// <summary>
/// Phase 8 (record): walks the retained SceneStore and emits the DrawList. Composites like a browser — each node's
/// geometry is emitted in LOCAL space with a world transform (parent ∘ translate ∘ LocalTransform, scale/rotate about
/// the node's center) and a cumulative opacity, so transform/opacity animate without relayout or re-record of content.
/// Hover/press cross-fade via the eased <see cref="InteractionAnim"/> row; a focused node gets a dual-stroke focus ring.
/// </summary>
public static class SceneRecorder
{
    public static void ConfigureScrollbarArrowGlyphs(SceneStore scene, StringId up, StringId down, StringId left, StringId right, StringId iconFamily)
        => scene.Recording.ConfigureScrollbarArrowGlyphs(up, down, left, right, iconFamily);

    public static bool RectOverVideoHole(SceneStore scene, in RectF worldRect, float minCoveredFraction = 0.5f)
        => scene.Recording.RectOverVideoHole(in worldRect, minCoveredFraction);


    public static (RectF DrawRect, RectF Uv) ImageContentFit(ImageFit fit, in RectF box, int srcW, int srcH, float focusX = 0.5f, float focusY = 0.5f)
        => SceneRecordingContext.ImageContentFit(fit, in box, srcW, srcH, focusX, focusY);

    /// <summary>Record <paramref name="scene"/> into its slice arenas (<paramref name="slices"/>, or the scene's own
    /// recorder) and FLATTEN the result into <paramref name="dl"/> at the scene's current poses — the one painter-ordered
    /// stream every backend replays until P2 (see <see cref="SliceRecorder"/>).</summary>
    public static SceneRecordStats Record(SceneStore scene, DrawList dl, ImageCache? images = null, in FocusVisualStyle focus = default,
                                          ColorF scrollThumb = default, ColorF scrollTrack = default, in TextEditStyle textEdit = default,
                                          ReadOnlySpan<NodeHandle> skipRoots = default,
                                          SpanTable? spans = null,
                                          SpanReuseDisabledReason spanReuseDisabled = SpanReuseDisabledReason.None,
                                          ReadOnlySpan<RectF> pendingStructuralDamage = default,
                                          ReadOnlySpan<NodeHandle> reuseBlockRoots = default, bool collectSpanReuseMisses = false,
                                          SliceRecorder? slices = null, FluentGpu.Animation.DetachedAnimSlab? detached = null)
    {
        var context = scene.Recording;
        var snapshot = context.CaptureInline(scene, images);
        ReadOnlySpan<FluentGpu.Animation.DetachedNode> detachedNodes = default;
        if (detached is not null && detached.Count > 0)
        {
            for (int s = 0; s < detached.NodeCount; s++)
            {
                ref readonly var node = ref detached.At(s);
                if (node.InUse && (VisualKind)node.Kind == VisualKind.Image && node.ImageId != 0)
                    context.InlineImages.AddReferenced(images, [node.ImageId]);
            }
            detachedNodes = detached.Nodes;
        }
        var result = context.Record(snapshot, dl, context.InlineImages, in focus, scrollThumb, scrollTrack, in textEdit, skipRoots,
            spans, spanReuseDisabled, pendingStructuralDamage, reuseBlockRoots, collectSpanReuseMisses,
            slices, detachedNodes);
        scene.ClearPendingRemovals();
        return result;
    }

    public static void RecordDetached(SceneStore scene, DrawList dl, ImageCache? images, FluentGpu.Animation.DetachedAnimSlab detached, RectF clip)
    {
        var context = scene.Recording;
        // perf plan item 5/item 1: reuse THIS frame's Record() capture instead of re-walking the whole scene + image
        // cache again (Record always runs first, synchronously, with no scene mutation in between — see
        // CaptureInlineForFrame). A detached fly's ImageId lives in the DetachedAnimSlab, not on any scene node, so it
        // was never in the scene walk's referenced-image set (item 1); fold those few (typically 0-3) ids in too.
        int detachedCount = detached.NodeCount;
        Span<int> detachedImageIds = stackalloc int[detachedCount];
        int detachedImageIdCount = 0;
        for (int s = 0; s < detachedCount; s++)
        {
            ref readonly var node = ref detached.At(s);
            if (node.InUse && (VisualKind)node.Kind == VisualKind.Image && node.ImageId != 0)
                detachedImageIds[detachedImageIdCount++] = node.ImageId;
        }
        var snapshot = context.CaptureInlineForFrame(scene, images, detachedImageIds[..detachedImageIdCount]);
        context.RecordDetached(snapshot, dl, context.InlineImages, detached, clip);
    }

    public static SceneRecordStats RecordSubtree(SceneStore scene, DrawList dl, ImageCache? images, in FocusVisualStyle focus,
                                                 ColorF scrollThumb, ColorF scrollTrack, in TextEditStyle textEdit,
                                                 NodeHandle root, Point2 originDip)
    {
        var context = scene.Recording;
        // perf plan item 5: reuse THIS frame's Record() capture (see CaptureInlineForFrame) — a popup subtree's nodes
        // are already part of the main scene walk (popups stay children of Root; only Walk's emission, not Capture,
        // respects skipRoots), so no extra referenced-image ids are needed here.
        var snapshot = context.CaptureInlineForFrame(scene, images);
        return context.RecordSubtree(snapshot, dl, context.InlineImages, in focus, scrollThumb, scrollTrack, in textEdit, root, originDip);
    }
}

/// <summary>One recording target's mutable scratch. Never shared with another target or recording thread.</summary>
internal sealed class SceneRecordingContext
{
    internal bool PopupPresented;
    internal void CopyConfigurationFrom(SceneRecordingContext source)
    {
        ConfigureScrollbarArrowGlyphs(source._sbUpGlyph, source._sbDownGlyph, source._sbLeftGlyph, source._sbRightGlyph, source._sbIconFamily);
        _sbArrowGlyphsSet = source._sbArrowGlyphsSet;
    }

    internal void RetainConfigurationStrings(SceneRecordingSnapshot scene, StringTable strings)
        => scene.RetainStrings(strings, [_sbUpGlyph, _sbDownGlyph, _sbLeftGlyph, _sbRightGlyph, _sbIconFamily]);

    internal int CopyVideoRects(Span<RectF> destination)
    {
        int count = Math.Min(destination.Length, _publishedVideoRectCount);
        _publishedVideoRects.AsSpan(0, count).CopyTo(destination);
        return count;
    }

    internal void ImportVideoRects(ReadOnlySpan<RectF> source)
    {
        _publishedVideoRectCount = Math.Min(source.Length, _publishedVideoRects.Length);
        source[.._publishedVideoRectCount].CopyTo(_publishedVideoRects);
    }
    private SceneRecordingSnapshot? _inlineScene;
    private SceneStore? _inlineCapturedScene;
    private ImageCache? _inlineCapturedImages;
    internal ImageRecordingSnapshot InlineImages { get; } = new();
    /// <summary>The last inline (UI-thread) capture — the snapshot the inline composite turn reads poses off.</summary>
    internal SceneRecordingSnapshot? InlineSnapshot => _inlineScene;
    internal void ReleaseInlineResources() => _inlineScene?.ReleaseResources();

    /// <summary>The frame's authoritative capture — always re-walks the scene (perf plan item 1: image inputs are
    /// narrowed to <see cref="SceneRecordingSnapshot.ReferencedImageIds"/>, the set the recorder can actually draw).
    /// Called by <see cref="Record"/>, which is always the first of the (Record, RecordDetached, N×RecordSubtree)
    /// group in a real inline/headless frame (Hosting/AppHost.cs's `!recordOnRender` branch) — see
    /// <see cref="CaptureInlineForFrame"/> for the sibling calls that reuse this result instead of re-capturing.</summary>
    internal SceneRecordingSnapshot CaptureInline(SceneStore scene, ImageCache? images)
    {
        var snapshot = _inlineScene ??= new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        // The slice recorder reads the scroll-effect rows (which nodes are translation-effect roots) off the capture,
        // exactly as the render thread's publication carries them.
        snapshot.ScrollCoverage.Clear();
        scene.CaptureScrollCoverage?.Invoke(snapshot.ScrollCoverage);
        InlineImages.Capture(images, snapshot.ReferencedImageIds);
        _inlineCapturedScene = scene;
        _inlineCapturedImages = images;
        return snapshot;
    }

    /// <summary>perf plan item 5: <see cref="RecordDetached"/>/<see cref="RecordSubtree"/> call this instead of
    /// <see cref="CaptureInline"/> — a headless/inline frame captures (scene, images) with
    /// <see cref="CaptureInline"/> exactly once (from <see cref="Record"/>) and this reuses that same snapshot for the
    /// rest of the frame's passes (RecordDetached, each popup's RecordSubtree) when nothing has mutated the (scene,
    /// images) pair since — a plain reference-equality check against the pair last captured, so a fresh/different
    /// scene (a test calling RecordDetached on its own SceneStore with no preceding Record) always falls back to a
    /// real capture. <paramref name="extraImageIds"/> (small — the detached-fly slab's own image ids) are merged into
    /// the already-captured/reused <see cref="InlineImages"/> without re-capturing the scene.</summary>
    internal SceneRecordingSnapshot CaptureInlineForFrame(SceneStore scene, ImageCache? images, ReadOnlySpan<int> extraImageIds = default)
    {
        bool reuse = _inlineScene is not null && ReferenceEquals(_inlineCapturedScene, scene) && ReferenceEquals(_inlineCapturedImages, images);
        var snapshot = reuse ? _inlineScene! : CaptureInline(scene, images);
        if (!extraImageIds.IsEmpty) InlineImages.AddReferenced(images, extraImageIds);
        return snapshot;
    }
    private const bool EnableSubtreeCull = true;

    // Opaque occlusion cull (ALWAYS ON): skip a node's own visual when a later-drawn direct child provably, fully,
    // opaquely covers it — those fill/border pixels are overwritten regardless, so the emit is dead work (finding: the
    // nested opaque background stack overdraws 4-8× with no z-reject). INVARIANT: the predicate is strict and
    // space-consistent — the child's covered rect is transformed through the SAME parent `world` as the node's own
    // device rect (no AbsoluteRect coordinate-space mismatch), so containment is sound under any DPI / ancestor scale;
    // any doubt returns false. An earlier default-on attempt was reverted 2026-07-23 because it compared the
    // translation-only AbsoluteRect against a full-world device rect (two spaces), wrongly dropping the Wavee seek-bar
    // rail under its Scale(0.3,1) value-fill; the rewrite computes the child rect in device space and rejects any
    // non-identity linear transform, so that case is handled correctly. Regression-gated by gate.record.occlusion-*.



    // Overlay-scrollbar arrow glyphs: the host pre-interns the four Segoe Fluent arrow chars + the icon family once
    // at startup (AppHost ctor) so EmitScrollbar draws the SAME solid-triangle glyphs as the standalone ScrollBar
    // control (ScrollBar_themeresources.xaml :387/:344/:301/:258) — one scrollbar visual language, not two. A host
    // that never configures them (bare recorder tests) falls back to the stroked-chevron primitive.
    private StringId _sbUpGlyph, _sbDownGlyph, _sbLeftGlyph, _sbRightGlyph, _sbIconFamily;
    private bool _sbArrowGlyphsSet;

    public void ConfigureScrollbarArrowGlyphs(StringId up, StringId down, StringId left, StringId right, StringId iconFamily)
    {
        _sbUpGlyph = up; _sbDownGlyph = down; _sbLeftGlyph = left; _sbRightGlyph = right; _sbIconFamily = iconFamily;
        _sbArrowGlyphsSet = true;
        ConfigurationVersion++;
    }

    /// <summary>Moves on every change to the recording configuration <see cref="CopyConfigurationFrom"/> copies into a
    /// publication — the host's no-op publication skip compares it.</summary>
    internal int ConfigurationVersion { get; private set; }

    // ── Repaint-damage scratch (gpu-renderer.md §13.1) ──────────────────────────────────────────────────────────────
    // The AA floor every emitted repaint rect is padded by. Per-kind effect extent (shadow offset+spread+3σ, self-blur
    // 3σ) rides on TOP of it via DamageExtent below — this constant is only the anti-aliasing/ink slop, same class as
    // OpacityGroupExtentPadDip above. internal (not private): Reconciler.AddImageNodeRepaint (image-landing/crossfade
    // node rects, damage-scoped-repaint-design.md "Step 3") pads by the SAME floor so an image band and an ordinary
    // record-side band agree on how far AA slop reaches.
    internal const float RepaintAaPadDip = 8f;

    // A DrawVideo punches a hole the compositor fills from a DComp visual. The hole set is read off the placed composite
    // plan (window space, every slice at its current pose, kept slices included — SliceRecorder.VideoRects); capacity is
    // tiny because a frame has at most a handful.
    private const int VideoRectCap = 8;

    // The last COMPLETED main-window flatten's video Dst set, published so UI-thread code that runs BETWEEN records
    // (host phase 7.1 - the overlay lifecycle/placement pass) can ask "is this rect sitting on top of a video hole?".
    // Fixed capacity, no allocation — identical discipline to the scratch it copies.
    private readonly RectF[] _publishedVideoRects = new RectF[VideoRectCap];
    private int _publishedVideoRectCount;

    // F070: the same turn's holes as the video placement must follow them (unclipped posed rect + the clip that cut it, per registry
    // token). Written by Record / Compose on the thread that records, read by that thread's present turn right after - the render
    // thread's VideoPlacementApplier moves the video by each hole's travel since the UI published, so it lands under the hole of
    // the frame it is presented with.
    private readonly FluentGpu.Media.VideoPosedHole[] _publishedPosedHoles = new FluentGpu.Media.VideoPosedHole[FluentGpu.Media.VideoSurfaceRegistry.MaxSurfaces];
    private int _publishedPosedHoleCount;

    /// <summary>The holes of the last completed record or composite, with the pose the composite applied (F070).</summary>
    internal ReadOnlySpan<FluentGpu.Media.VideoPosedHole> PosedVideoHoles => _publishedPosedHoles.AsSpan(0, _publishedPosedHoleCount);

    /// <summary>
    /// Does <paramref name="worldRect"/> sit on top of a video hole punch? True when a single <c>DrawVideo</c> Dst from
    /// the last completed record covers at least <paramref name="minCoveredFraction"/> of its area.
    /// <para>
    /// The point of the question is that a hole punch is a <b>DestOut erase</b> whose pixels are premultiplied ZERO, and
    /// the video itself is a sibling DComp visual z-BELOW the UI swapchain - so an ACRYLIC plate placed over a hole
    /// blurs nothing at all (it samples transparent black) while still costing a backdrop pass (a mini-composite of
    /// everything beneath it, blurred, every frame the video under it changes). Callers use this to drop the acrylic for
    /// a flat fallback fill - a visual no-op there.
    /// </para>
    /// <para>Answers from the PREVIOUS frame's geometry by construction (it is asked before this frame is recorded).
    /// That is the correct latency: a hole that just appeared or moved costs one frame of the old answer, and the answer
    /// only ever selects between two visually-equivalent paints, never between correct and incorrect pixels.</para>
    /// <para><b>Limitation 1 — the published rect is not the same rect every frame, so the answer can FLIP with no
    /// scene change.</b> (Historical: the hole set was fed by three recorder sites that did not agree on granularity; it is now read off the flattened stream's exact Dst rects.) A
    /// fresh walk publishes the exact <c>DrawVideo</c> Dst (the <c>VisualKind.Video</c> case below), while a clean-span
    /// reuse publishes the whole <c>span.SubtreeBounds</c> of the span that CONTAINED the DrawVideo — letterbox floor,
    /// transport chrome and shadow halos folded in — and a rebased reuse publishes that same coarse box translated.
    /// For a query rect well inside the Dst the two agree; for one straddling the Dst's edge they do not, and which
    /// path serves the video's subtree changes frame to frame (a paint-dirty write anywhere in it disables reuse).
    /// A caller that cannot tolerate an answer flipping under it must LATCH the first affirmative rather than re-ask
    /// — which is what <c>OverlayHost.SyncWindowedMenuBackdrop</c> does, because each flip there costs a full-window
    /// repaint. Not fixed by aligning the sites: the coarse box is the correct repaint-damage extent for a reused span,
    /// which is what the hole set primarily exists to feed.</para>
    /// <para><b>Limitation 2 — coverage is tested PER RECT and never unioned.</b> The loop returns true only when a
    /// SINGLE hole covers <paramref name="minCoveredFraction"/> of the query rect, so two adjacent holes each covering
    /// 40% of it answer <c>false</c> even though 80% of the rect is over video. Deliberate for the sizes involved (a
    /// frame has at most a handful of holes and a popup plate normally straddles one), but a caller placing a plate
    /// across a multi-video wall must not read a <c>false</c> as "no video underneath".</para>
    /// </summary>
    public bool RectOverVideoHole(in RectF worldRect, float minCoveredFraction = 0.5f)
    {
        if (_publishedVideoRectCount == 0 || worldRect.W <= 0f || worldRect.H <= 0f) return false;
        float area = worldRect.W * worldRect.H;
        for (int i = 0; i < _publishedVideoRectCount; i++)
        {
            RectF hole = _publishedVideoRects[i];
            float x0 = MathF.Max(worldRect.X, hole.X), y0 = MathF.Max(worldRect.Y, hole.Y);
            float x1 = MathF.Min(worldRect.Right, hole.Right), y1 = MathF.Min(worldRect.Bottom, hole.Bottom);
            if (x1 <= x0 || y1 <= y0) continue;
            if ((x1 - x0) * (y1 - y0) >= area * minCoveredFraction) return true;
        }
        return false;
    }


    // Effect-halo allowance for an extent captured OUTSIDE the recorder — a freed node's model rect, a structural-cancel
    // seed. Those carry no shadow/blur information, whereas every extent the recorder itself produces already does:
    // SpanRecordResult.SubtreeBounds unions the shadow quad's `offset + spread + 3σ` box (see the shadow emit) and the
    // self-blur's ±3σ OutputBounds (see `visualBounds`) as they are recorded. Sized to the engine's largest stock
    // elevation (Elevation.CardHover — blur 16, offset 8 ⇒ 8 + 3·8 = 32), same conservative-bound reasoning as
    // HoverElevateHoistSlackDip.
    // It is an EFFECT-HALO bound and nothing else. (An ancestor's TRANSLATION no longer moves recorded pixels: the
    // translated-copy branch is gone and a slice's motion is a composite parameter mapped separately.)
    private const float RepaintUnknownHaloDip = 32f;

    /// <summary>The repaint band for an extent: the AA/ink floor, plus <paramref name="extraHalo"/> for extents whose
    /// per-kind effect halo is NOT already folded in. The ONE place repaint rects are padded.</summary>
    private RectF RepaintBand(in RectF extent, float extraHalo = 0f)
    {
        if (extent.W <= 0f || extent.H <= 0f) return default;
        float pad = RepaintAaPadDip + extraHalo;
        return new RectF(extent.X - pad, extent.Y - pad, extent.W + 2f * pad, extent.H + 2f * pad);
    }

    /// <summary>Sum, in DIP, of the blur reach of every ANCESTOR of <paramref name="node"/> that carries a self-blur
    /// (§2b). A dirty node's band covers its own pixels — and its OWN self-blur halo, which is already folded into its
    /// span extent as <c>visualBounds</c> — but NOT what those pixels become once an ENCLOSING blur spreads them:
    /// changing one glyph inside a blurred surface changes the blurred output over that glyph ⊕ ~3σ, so repainting only
    /// the glyph's band leaves a stale ring around it. Harmless while a blurred frame was vetoed to a full redraw;
    /// load-bearing now that such a frame may be replayed under a clamp.
    /// <para>The same rule WebRender applies by mapping a dirty primitive's rect through every blur surface between it
    /// and the tile cache. Additive over nesting; σ = 0 contributes nothing — which is every node in a UI that is not
    /// inside a blurred surface — so this is O(depth) and returns 0 outright for almost every node. TapRadius is
    /// PHYSICAL px and a repaint band is DIP, so the sum converts on the way out.</para></summary>
    private static float EnclosingBlurHaloDip(SceneRecordingSnapshot scene, NodeHandle node)
    {
        int haloPx = 0;
        for (NodeHandle a = scene.Parent(node); !a.IsNull; a = scene.Parent(a))
        {
            float sigma = scene.Paint(a).BlurSigma;
            if (sigma > 0.01f) haloPx += SelfBlurRegion.TapRadius(sigma);
        }
        if (haloPx == 0) return 0f;
        float s = scene.DeviceScale > 0f ? scene.DeviceScale : 1f;
        return haloPx / s;
    }

    private struct RecordAccumulator
    {
        public SceneRecordingContext Owner;
        public int NodesVisited;
        public int DrawnNodeCount;
        public int CulledNodeCount;
        public int NodesCulled;
        public int BlurCandidateCount;
        public int BlurGroupCount;
        public int EdgeFadeGroupCount;
        public int SpansReused;
        public int SpansReRecorded;
        public int SpanBytesCopied;
        public int SpanMissGlobalDisabled;
        public int SpanMissScopedBlocked;
        public int SpanMissExactDirty;
        public int SpanMissExactKey;
        public int SpanMissExactClip;
        public int SpanMissExactCapacity;
        public int ScopedBlocks;

        // ── the retained-tile slice partition (SliceRecorder) ─────────────────────────────────────────────────────
        // Slicing = this walk records into slice arenas (the main pass only; popups/top bands record inline).
        // CurSlot/CurGen/PriorGen = the arena being written (spans store with CurGen and copy out of PriorGen);
        // CurDx/CurDy = that slice's accumulated RECORD-time posed offset (window DIP) — every repaint/backdrop rect the
        // walk produces in slice space is mapped into the window by it. SliceDepth = depth below the slice root (span
        // index). Self* = the pending slice-root walk the containing cut handed over (consumed by WalkCore).
        public SliceRecorder? Slices;
        /// <summary>How many enclosing <see cref="Walk"/> frames of the CURRENT arena are inside an additive subtree
        /// (BoxEl.Blend). Saved at every slice cut and restored after it; zeroed for the cut's arena unless the cut carries
        /// the bracket (<see cref="SelfAdditive"/>), which then counts as one.</summary>
        public int AdditiveDepth;
        public bool Slicing;
        public int CurSlot;
        public ulong CurGen, PriorGen;
        /// <summary>The lineage of the walking node's prior bytes (<see cref="SpanTable.Relocation"/>): lets a clean
        /// descendant copy out of the prior buffer although an earlier exact copy of an ancestor left its row on the retired
        /// buffer it was recorded into. Set per walked node, restored when its walk returns.</summary>
        public SpanReloc Reloc;
        public float CurDx, CurDy;
        public float SliceOwnDx, SliceOwnDy;
        // The composite clip (WINDOW space) of the current slice chain: a translation slice records under an unbounded
        // clip, so its repaint bands are bounded here instead (nothing it paints outside its viewport is visible).
        public RectF SliceClip;
        public int SliceDepth;
        public NodeHandle SelfNode;
        public int SelfSlot;
        public bool SelfHasLocal;
        public Affine2D SelfLocal;
        public bool SelfOmitLayer;
        public SliceRecorder.PoseKind SelfPose;
        // The pending slice-root walk is an ACRYLIC slice cut at that node (consumed with SelfNode): only such a walk may
        // record the Acrylic PushLayer, because only its slice gets the composite Backdrop item the layer's tile replay
        // expects beneath it (an inline acrylic layer ERASES its plate rect — without a backdrop it is a transparent hole).
        public bool SelfAcrylic;
        // The pending slice-root walk carries its node's .StickyClip as a COMPOSITE-TIME clip on its marker
        // (CompositeSliceFlags.StickyClip): that walk leaves NodePaint.ClipRect out of every byte it records.
        public bool SelfStickyClip;
        // The pending slice-root walk re-opens the additive bracket its cut fell inside (BoxEl.Blend): EnterSlice sets it
        // for a cut that would record inside that bracket when folded inline, WalkCore emits the pair inside the root's span.
        public bool SelfAdditive;
        // The walk records into slice arenas that composite through retained tiles (the host's pass — every band of it,
        // top bands included), as opposed to a standalone stream a backend replays whole (where an inline acrylic layer
        // composites at push time).
        public bool CompositeArenas;
        // Open INLINE group layers (opacity / self-blur / edge fade, including a folded one) and stencil (path) clips in the
        // arena being written: a tile replay skips a child slice's marker, so an inline group wrapped around one would wrap
        // NOTHING (the child composites unfaded / unblurred / unfeathered) and a stencil clip would clip nothing (the child
        // composites as its marker's rectangle). While > 0 no slice is cut: everything records inline, poses baked.
        public int InlineLayerDepth;
        public int KeepDenySlot;
#pragma warning disable CS0649 // Retained AppHost settle-latch pair; softness made the increment a lie (always 0).
        public int UnsnappedGlyphSpans;
#pragma warning restore CS0649
        public int DepthAborts;    // subtrees dropped by the stack-headroom guard (see HasWalkStackHeadroom) — never silent
        public SpanReuseDisabledReason SpanReuseDisabledReasons;
        // The frame's repaint set: "what pixels must be redrawn?" (moves ∪ paint/layout re-records ∪ removals ∪ scrolled
        // viewports) — a forced-full region invalidates every tile; the rects are the Present dirty-rect census.
        public RepaintDamageRegion Repaint;
        // Geometry damage (retained tiles): set while walking below a node that already damages its whole subtree
        // this frame (content-dirty, transform-moved, or moved/resized by layout) - its descendants' own geometry
        // changes are inside that band. Reset at every slice entry (the band lives in the enclosing slice's space).
        public bool GeomCovered;
        public bool HasActiveVirtualDisclosures;
        // Vestigial (scroll-root-cause-2026-09-23 §5.2 Part B): the SpanMiss* counters below used to be gated on
        // this AND a Release-only compiled-out const; both gates are gone, so this field is written from the
        // `collectSpanReuseMisses` parameter but no longer read by anything. Kept only so existing callers of
        // Record(...) that still pass that parameter keep compiling; remove both once they're updated to drop it.
        public bool CollectSpanMisses;

        // Hover-elevate clip-ESCAPE (HoverElevateClipRootBit): under a flagged clip root, the sibling deferral PARKS
        // the elevated child here (with everything its re-walk needs) instead of emitting; the flagged ancestor hoists
        // it after its own clip + edge-fade scope closes, recording against the clip OUTSIDE the strip. One slot: walks
        // are sequential, each root consumes its own subtree's park before the next shelf starts (innermost root wins).
        public NodeHandle PendingElevate;
        public Affine2D PendingElevateWorld;
        public float PendingElevateOpacity;
        public int PendingElevateDepth;
        public float PendingElevateScaleX, PendingElevateScaleY;
        public bool PendingElevateInMotion, PendingElevateScrollInMotion;
        public InheritedState PendingElevateState;
        public int PendingElevateSlot;
        public float PendingElevateDx, PendingElevateDy;

        /// <summary>Union a CURRENT-SLICE-space rect into the REPAINT set (already padded at the call site), mapped into
        /// the window by the slice's record-time offset.</summary>
        public void AddRepaint(in RectF sliceRect)
        {
            RectF w = SliceRecorder.Offset(in sliceRect, CurDx, CurDy);
            if (!SliceClip.IsInfinite && !SliceClip.IsEmpty) w = w.Intersect(SliceClip);
            Repaint.Add(in w);
        }

        /// <summary>Union a WINDOW-space rect (an AbsoluteRect, a structural-cancel seed, a mapped prior extent).</summary>
        public void AddRepaintWindow(in RectF r) => Repaint.Add(in r);

        public readonly SceneRecordStats ToStats() => new(NodesVisited, DrawnNodeCount, CulledNodeCount)
        {
            RepaintDamage = this.Repaint,
            NodesCulled = this.NodesCulled,
            BlurCandidateCount = this.BlurCandidateCount,
            BlurGroupCount = this.BlurGroupCount,
            EdgeFadeGroupCount = this.EdgeFadeGroupCount,
            SpansReused = this.SpansReused,
            SpansReRecorded = this.SpansReRecorded,
            SpanBytesCopied = this.SpanBytesCopied,
            SpanReuseMisses = new SpanReuseMissStats(
                SpanMissGlobalDisabled,
                SpanMissScopedBlocked,
                SpanMissExactDirty,
                SpanMissExactKey,
                SpanMissExactClip,
                SpanMissExactCapacity),
            ScopedBlocks = this.ScopedBlocks,
            UnsnappedGlyphSpans = this.UnsnappedGlyphSpans,
            DepthAborts = this.DepthAborts,
            SpanReuseDisabledReasons = this.SpanReuseDisabledReasons,
        };
    }

    // Slop (DIP/device space) added to a plain opacity group's patched draw extent (see the PopLayer patch in Walk).
    // SubtreeBounds below covers every node's device box + its shadow/self-blur halo + its focus ring exactly, but three
    // things ride just outside a node's box: an SDF border ring straddles the edge by stroke/2, AA fringes by ~1px, and
    // a glyph's INK can overshoot its layout box (accents/descenders/italic overhang — the only size-dependent one, a
    // fraction of the em). The extent MUST be a superset of the drawn pixels (too small = the composite drops them; too
    // large only costs fill), so pad generously — the win is full-canvas → group box; a handful of px back is free.
    private const float OpacityGroupExtentPadDip = 8f;

    // Halo/shadow slack (device space) a hover-elevate HOIST may bleed past its clip root's own box. A hoisted subtree
    // escapes the root's clip by design — that IS the mechanism — but escape is not licence: the root's INCOMING clip is
    // routinely the whole canvas, so an unbounded re-walk lets a hovered descendant paint arbitrarily far outside the
    // surface that owns it (a chart row over a neighbouring column). CONSTRAINT: a hoisted subtree may not bleed further
    // than the lift + halo class the mechanism exists for — PagedShelf's HaloBleed 12 + ShadowClearance 12, plus AA/ink
    // slop — so this bounds every clip root to its own box inflated by that. Deliberately conservative rather than
    // exact: too small shaves a real halo, too large only costs fill, and either way the escape stays BOUNDED.
    private const float HoverElevateHoistSlackDip = 32f;

    private struct SpanRecordResult
    {
        public bool HasBounds;
        public RectF SubtreeBounds;

        public void Include(in RectF bounds)
        {
            if (bounds.W <= 0f || bounds.H <= 0f) return;
            if (!HasBounds) { SubtreeBounds = bounds; HasBounds = true; return; }
            float x0 = MathF.Min(SubtreeBounds.X, bounds.X), y0 = MathF.Min(SubtreeBounds.Y, bounds.Y);
            float x1 = MathF.Max(SubtreeBounds.Right, bounds.Right), y1 = MathF.Max(SubtreeBounds.Bottom, bounds.Bottom);
            SubtreeBounds = new RectF(x0, y0, x1 - x0, y1 - y0);
        }

        public void Include(in SpanRecordResult other)
        {
            if (other.HasBounds) Include(other.SubtreeBounds);
        }
    }

    // TextMotionSoftness / MotionSoftStartDip / MotionSoftFullDip DELETED (audit 2026-09-22, cause #2): this used to
    // ramp a scrolled run's snap-to-atlas-phase correction off in proportion to live scroll speed (0 at 220 DIP/s,
    // fully soft at 1400 DIP/s) — a deliberate reproduction of WinUI's bilinear-resample-during-motion look. A wheel
    // stream swings through that whole range every notch, so text visibly flickered soft/crisp at the notch rate, then
    // popped sharp on settle — no reference engine (Chromium, Flutter, Slint, egui…) blurs text during scroll. Removed
    // outright: every glyph run now records crisp (DrawGlyphRunCmd.InMotion at its default, 0) regardless of speed —
    // see gate.record.textCrispDuringFastScroll (AnimSuite.cs) for the bit-identical-at-any-speed proof.

    private readonly struct InheritedState
    {
        public readonly float HoverT;
        public readonly float PressT;
        public readonly NodeFlags InteractiveFlags;
        public readonly byte HasProgress;
        public readonly byte Disabled;
        // A HoverElevateClipRoot ancestor is above this subtree: the sibling deferral PARKS the hover-elevated child
        // in the accumulator for that root to hoist after its clip/fade scope closes, instead of emitting in place.
        public readonly byte UnderElevateRoot;
        // Text-motion softness (0..1, ramped from the nearest scrolling ancestor's live speed) used to ride here —
        // DELETED (audit 2026-09-22, cause #2): see the removed TextMotionSoftness/MotionSoftStartDip/MotionSoftFullDip
        // for why (every reference engine draws scroll-time text as crisp as at-rest text; this was WinUI's bilinear-
        // resample look reproduced on purpose, and it flickered soft↔crisp at wheel-notch rate). Every glyph run now
        // records with DrawGlyphRunCmd.InMotion at its default (0, crisp) — see DrawList.DrawGlyphRun.

        public InheritedState(float hoverT, float pressT, NodeFlags interactiveFlags, byte hasProgress, byte disabled, byte underElevateRoot = 0)
        {
            HoverT = hoverT;
            PressT = pressT;
            InteractiveFlags = interactiveFlags;
            HasProgress = hasProgress;
            Disabled = disabled;
            UnderElevateRoot = underElevateRoot;
        }

        /// <summary>The state this node hands its children. An interaction SCOPE (<paramref name="scopeBoundary"/>:
        /// interactive and not <c>HoverScopeTransparent</c> — the cascade's <c>IsNestedHoverBoundary</c> rule) always
        /// starts its subtree from ITS OWN hover/press: the eased local progress when it has one, else its own instant
        /// flag state (Hovered/HoverWithin ⇒ 1, Pressed ⇒ 1). Never its ancestor's — a page ItemsView's interactive
        /// row container gets HoverWithin for any pointer in the row, and letting that HoverT=1 fall through an
        /// un-hovered card slot root (PressedBit/OnPointerReleased only, no InteractionAnim row) painted every card's
        /// HoverFill at once. A transparent listener (the ToolTip wrapper) and a non-interactive wrapper pass the
        /// enclosing scope's state through unchanged (a listener that is itself HoverWithin reports 1 either way).</summary>
        public readonly InheritedState ForChild(NodeFlags flags, bool nodeInteractive, bool scopeBoundary, bool hasLocalProgress, float localHoverT, float localPressT)
        {
            NodeFlags interactiveFlags = nodeInteractive ? flags : InteractiveFlags;
            byte disabled = Disabled != 0 || (flags & NodeFlags.Disabled) != 0 ? (byte)1 : (byte)0;
            if (nodeInteractive && hasLocalProgress)
                return new InheritedState(localHoverT, localPressT, interactiveFlags, 1, disabled, UnderElevateRoot);
            if (scopeBoundary)
            {
                float ownHoverT = (flags & (NodeFlags.Hovered | NodeFlags.HoverWithin)) != 0 ? 1f : 0f;
                float ownPressT = (flags & NodeFlags.Pressed) != 0 ? 1f : 0f;
                return new InheritedState(ownHoverT, ownPressT, interactiveFlags, 1, disabled, UnderElevateRoot);
            }
            return new InheritedState(HoverT, PressT, interactiveFlags, HasProgress, disabled, UnderElevateRoot);
        }

        /// <summary>The state a HoverElevateClipRoot hands its children — descendant deferrals park, not emit.</summary>
        public readonly InheritedState WithUnderElevateRoot()
            => new(HoverT, PressT, InteractiveFlags, HasProgress, Disabled, 1);

        /// <summary>The state the HOISTED subtree records under: it is already outside the root, so any flagged child
        /// INSIDE it (the card wrapper within the hoisted cell) defers in place — a re-park would have no consumer
        /// left (the root's consume already ran) and the subtree would silently vanish.</summary>
        public readonly InheritedState WithoutUnderElevateRoot()
            => new(HoverT, PressT, InteractiveFlags, HasProgress, Disabled, 0);
    }

    /// <summary>The recording target's own slice recorder — used when the caller hands none (a bare
    /// <c>SceneRecorder.Record(scene, dl)</c>): the arenas persist with the scene, like its recording scratch.</summary>
    private SliceRecorder? _ownSlices;


    /// <param name="skipRoots">Subtree roots EXCLUDED from this record pass — out-of-bounds popup wrappers that render
    /// into their own popup window instead (E4 windowed popups; see <see cref="RecordSubtree"/>). The subtrees stay in
    /// the one SceneStore (layout/hit-test unchanged) — only their pixels move to the popup window's DrawList.</param>
    /// <param name="slices">The slice arenas to record into (paired with <paramref name="spans"/>); null = this target's own.</param>
    /// <param name="detached">Detached fly snapshots drawn in the top band (the root slice's static-over tail).</param>
    public SceneRecordStats Record(SceneRecordingSnapshot scene, DrawList dl, ImageRecordingSnapshot? images = null, in FocusVisualStyle focus = default,
                                          ColorF scrollThumb = default, ColorF scrollTrack = default, in TextEditStyle textEdit = default,
                                          ReadOnlySpan<NodeHandle> skipRoots = default,
                                          SpanTable? spans = null,
                                          SpanReuseDisabledReason spanReuseDisabled = SpanReuseDisabledReason.None,
                                          ReadOnlySpan<RectF> pendingStructuralDamage = default,
                                          ReadOnlySpan<NodeHandle> reuseBlockRoots = default,
                                          bool collectSpanReuseMisses = false,
                                          SliceRecorder? slices = null,
                                          ReadOnlySpan<FluentGpu.Animation.DetachedNode> detached = default)
    {
        // No slice recorder handed in = a STANDALONE record (gates, tools): the whole scene records INLINE — no slice is
        // cut, every pose is baked — into this target's own recorder's root arena, and lands in `dl` as ONE stream. With a
        // slice recorder (the host's), the scene records into the slice arenas and `dl` stays empty: the composite plan
        // (SliceRecorder.Place → BuildComposite) is what every backend consumes.
        bool standalone = slices is null;
        slices ??= _ownSlices ??= new SliceRecorder();
        dl.Reset();
        if (scene.Root.IsNull) return default;

        var stats = new RecordAccumulator
        {
            Owner = this,
            HasActiveVirtualDisclosures = scene.HasActiveVirtualDisclosures,
            CollectSpanMisses = collectSpanReuseMisses,
            Slices = slices,
            Slicing = !standalone,
            CompositeArenas = !standalone,
            CurSlot = SliceRecorder.RootSlot,
            KeepDenySlot = -1,
            SliceClip = RectF.Infinite,
        };
        // Seed the frame's REPAINT set with any band a structural-track CANCEL vacated this frame
        // (AnimEngine.PendingStructuralDamage): when a suppressed/resized FLIP or Reveal is snapped to its final bounds, the
        // node stops covering the extent it drew at last frame and no node re-touches that band. They arrive from outside
        // the recorder in WINDOW space, so they get the unknown-halo allowance (§13.1 "structural-track cancellation damages
        // the last-presented extent"). The retained tiles under that band need nothing from here: the snap re-records the
        // node at its final transform, so every tile it left changes its content want (gpu-renderer.md §13.1c).
        for (int i = 0; i < pendingStructuralDamage.Length; i++)
            stats.AddRepaintWindow(RepaintBand(pendingStructuralDamage[i], RepaintUnknownHaloDip));
        uint spanFrame = spans?.BeginFrame(scene.Capacity) ?? 0;
        slices.BeginPass(scene);
        // Unmount/removal damage: a freed subtree stops covering the band it presented at, and — unlike a move — nothing
        // re-touches that band, so a region-aware repaint would freeze last frame's pixels there. The span table holds the
        // EXACT extent it presented at (halos folded in) under its pre-free (index, gen), in the space of the slice it was
        // recorded into — mapped into the window by that slice's offset; the scene's ledger holds the model rect as the
        // fallback. Neither ⇒ the vacated band is unknown ⇒ full. Record is the ledger's only consumer, so it drains it here.
        var removals = scene.PendingRemovalExtents;
        if (scene.PendingRemovalOverflow) stats.Repaint.ForceFull(RepaintFullReason.StructuralInvalidation);
        for (int i = 0; i < removals.Length; i++)
        {
            RectF presented = default;
            bool got = spans is not null
                && spans.TryGetPriorExtent(removals[i].NodeIndex, removals[i].Gen, spanFrame, out presented, out _);
            if (got)
            {
                int slot = spans!.SliceSlotOf(removals[i].NodeIndex, removals[i].Gen);
                if (slices.TryPresentedDelta(slot, out float rdx, out float rdy))
                    stats.AddRepaintWindow(RepaintBand(SliceRecorder.Offset(in presented, rdx, rdy)));
                else stats.Repaint.ForceFull(RepaintFullReason.MissingRemovalExtent);
            }
            else if (!removals[i].ModelRect.IsEmpty)
                stats.AddRepaintWindow(RepaintBand(removals[i].ModelRect, RepaintUnknownHaloDip));
            else stats.Repaint.ForceFull(RepaintFullReason.MissingRemovalExtent);   // never presented AND no model rect
        }
        SpanReuseDisabledReason disabledReasons = spanReuseDisabled;
        if (spans is not null && !spans.HasPrior) disabledReasons |= SpanReuseDisabledReason.FirstRecord;
        // A path-slab compaction moved every realization that the prior bytes' FillPath/StrokePath/PushStencilClip index.
        if (spans is not null && spans.SyncPathSlab(PathRealizationCache.Shared.Generation)) disabledReasons |= SpanReuseDisabledReason.PathSlab;
        // PopupWindows/Overlays/Orphans/Detached are now SPATIALLY SCOPED (scene-memory.md): rather than killing span reuse
        // + the off-screen cull for the WHOLE tree while a flyout/fly/exit is in flight, we block ONLY the ancestor chains
        // of each special-cased visual (BlockSpecials below). The bits are still recorded for diagnostics; they no longer
        // force the global off (see GlobalReuseKill). Containment argument: a stored span's bytes are wrong only if the
        // special-cased visual lives INSIDE that subtree, so exactly its ancestor chains can hold a stale span.
        if (!skipRoots.IsEmpty) disabledReasons |= SpanReuseDisabledReason.PopupWindows;

        // E5 drag ghost: the lifted drag visual (SceneStore.DragGhost, set by Input.DragController at promotion; the
        // node also carries NodeFlags.DragGhost) is EXCLUDED from the clipped main pass and re-walked below in an
        // UNCLIPPED top band — it escapes every ancestor scissor (a row dragged out of a clipped list keeps drawing)
        // and, emitted LAST, paints above everything including overlays (the Flutter/rbd ghost layer). A ghost inside
        // a skipped popup subtree stays with its popup window (no main-window hoist).
        var ghost = scene.DragGhost;
        bool hasGhost = !ghost.IsNull && scene.IsLive(ghost) && !UnderAnySkipRoot(scene, skipRoots, ghost);
        // E5 drag OVERLAY (SceneStore.DragOverlay — a mounted DragPreviewLayer's container): the same hoist as the
        // ghost, one band HIGHER, so a drag chip paints above the main pass, the ghost band AND the connected-animation
        // overlays and can never be clipped by an ancestor scissor. Registered for the layer's whole lifetime (idle it
        // is an empty box), so the exclusion is stable frame-to-frame and no ancestor span can go stale from it.
        var dragOverlay = scene.DragOverlay;
        bool hasDragOverlay = !dragOverlay.IsNull && scene.IsLive(dragOverlay)
                              && !UnderAnySkipRoot(scene, skipRoots, dragOverlay);
        int overlayCount = scene.OverlayCount;
        if (hasGhost) disabledReasons |= SpanReuseDisabledReason.DragGhost;   // DragGhost stays GLOBAL this wave (drags are rare; scope later)
        if (scene.DropSpotlightActive) disabledReasons |= SpanReuseDisabledReason.DragSpotlight;
        if (overlayCount != 0) disabledReasons |= SpanReuseDisabledReason.Overlays;
        if (scene.OrphanCount != 0) disabledReasons |= SpanReuseDisabledReason.Orphans;
        if (!reuseBlockRoots.IsEmpty) disabledReasons |= SpanReuseDisabledReason.Detached;
        // Only these force the WHOLE-canvas reuse off; the scoped reasons above are handled per-chain by BlockSpecials.
        const SpanReuseDisabledReason GlobalReuseKill =
            SpanReuseDisabledReason.FirstRecord | SpanReuseDisabledReason.SceneChanged | SpanReuseDisabledReason.Layout |
            SpanReuseDisabledReason.Resize | SpanReuseDisabledReason.ModalPaint | SpanReuseDisabledReason.DragGhost |
            SpanReuseDisabledReason.ImageContent | SpanReuseDisabledReason.DragSpotlight | SpanReuseDisabledReason.PathSlab;
        bool spanReuseOff = spans is null || (disabledReasons & GlobalReuseKill) != 0;
        // The span STORE stays alive under EVERY reason, global ones included (blocked nodes self-gate via IsBlocked, so
        // the off-screen cull survives for the unblocked rest of the tree). A pass that stored nothing would leave every
        // span pointing into a buffer the next swap retires — the pass after would re-record the whole table. Reuse is
        // still killed globally on those frames via GlobalReuseKill; only the store survives, and the chains that could
        // store a span missing a hoisted visual are stamped by BlockSpecials below.
        bool spanStoreOn = spans is not null;
        stats.SpanReuseDisabledReasons = disabledReasons;
        Span<NodeHandle> skips = stackalloc NodeHandle[skipRoots.Length + 2 + overlayCount];
        skipRoots.CopyTo(skips);
        int skipCount = skipRoots.Length;
        if (hasGhost) skips[skipCount++] = ghost;
        if (hasDragOverlay) skips[skipCount++] = dragOverlay;   // drawn in its own top band below, never in the main pass
        // Connected-animation overlays are excluded from the clipped main pass and re-walked LAST in their own
        // top band (below). Add them to the skip set so a tree-resident overlay (the DragGhost case) is not also
        // drawn clipped in the main pass; standalone overlays are not tree descendants so this is belt-and-suspenders.
        for (int i = 0; i < overlayCount; i++)
        {
            var ov = scene.OverlayAt(i);
            if (scene.IsLive(ov)) skips[skipCount++] = ov;
        }

        // Spatial reuse-blocking (scene-memory.md): stamp the ancestor chains whose stored spans could go stale because a
        // special-cased visual lives (or lived last frame) inside them — each popup skipRoot, each live overlay, each exit
        // orphan's visual parent, each connected-anim fly anchor (reuseBlockRoots), and the hoisted drag visuals. This runs
        // whenever the store runs, NOT only while reuse is globally alive: a global kill re-records everything, but those
        // re-recorded chains omit the hoisted visual, so storing them unblocked is exactly how a stale span is minted.
        // O(specials × depth), alloc-free.
        //
        // The drag chip's chain is stamped only while a drag visual is actually in flight. The overlay registers once for
        // the preview layer's whole lifetime (DragPreviewLayer's mount effect) and is in `skips` on every one of those
        // frames, so no ancestor span can go stale from it at rest — stamping it unconditionally would instead block the
        // app root's chain from storing forever.
        if (spanStoreOn)
        {
            BlockSpecials(scene, spans!, spanFrame, skipRoots, overlayCount, reuseBlockRoots,
                hasGhost ? ghost : NodeHandle.Null,
                hasDragOverlay && (hasGhost || scene.DropSpotlightActive) ? dragOverlay : NodeHandle.Null,
                ref stats);
            // A pose the retained slices cannot honour (a baked transform moved, a viewport's offset-dependent chrome
            // changed, a pose-locked slice moved, a slice root's posed linear part changed): re-record exactly that chain.
            foreach (NodeHandle n in slices.MustRewalk) BlockChain(scene, spans!, spanFrame, n, ref stats);
        }

        // The root slice is kept whole only when nothing is appended behind the root's own span (the top bands and the
        // detached flies write into the root arena after it) on this pass or the last.
        bool hasRootlessOrphan = false;
        for (int i = 0; i < scene.OrphanCount && !hasRootlessOrphan; i++)
            hasRootlessOrphan = scene.OrphanVisualParentAt(i).IsNull;
        bool rootTail = hasRootlessOrphan || scene.DropSpotlightActive || hasGhost || overlayCount != 0 || hasDragOverlay || !detached.IsEmpty;
        if (rootTail || slices.RootHadTail) stats.KeepDenySlot = SliceRecorder.RootSlot;
        slices.RootHadTail = rootTail;

        DrawList rootDl = slices.Arena(SliceRecorder.RootSlot);
        stats.SelfNode = scene.Root;
        stats.SelfSlot = SliceRecorder.RootSlot;
        stats.SelfHasLocal = false;
        stats.SelfOmitLayer = false;
        stats.SelfPose = SliceRecorder.PoseKind.None;
        stats.SelfStickyClip = false;
        stats.CurGen = slices.CurGen(SliceRecorder.RootSlot);
        stats.PriorGen = slices.PriorGen(SliceRecorder.RootSlot);
        stats.SliceDepth = -1;
        Walk(scene, rootDl, images, scene.Root, Affine2D.Identity, 1f, 0, RectF.Infinite, in focus, in textEdit, scrollThumb, scrollTrack,
            1f, 1f, false, false, default, skips[..skipCount], spans, spanFrame, spanReuseOff, spanStoreOn, ref stats);
        stats.SelfNode = NodeHandle.Null;
        bool keptAll = slices.IsRegisteredThisPass(SliceRecorder.RootSlot) && !slices.WalkedThisPass(SliceRecorder.RootSlot);
        if (!slices.IsRegisteredThisPass(SliceRecorder.RootSlot))
        {
            slices.BeginWalk(SliceRecorder.RootSlot, WalkWhy.Invisible);   // an invisible root: an EMPTY static slice, never a stale one
            keptAll = false;
        }
        stats.CurSlot = SliceRecorder.RootSlot;
        stats.CurGen = slices.CurGen(SliceRecorder.RootSlot);
        stats.PriorGen = slices.PriorGen(SliceRecorder.RootSlot);
        stats.CurDx = stats.CurDy = 0f;
        stats.Slicing = false;   // the top bands below record inline into the root slice's static-over tail

        // Defensive rootless-orphan fallback. Normal exits replay inside their former parent's Walk (preserving ancestor
        // clips/layers, painter order, and popup-window routing); only nodes that genuinely had no visual parent land here.
        for (int i = 0; i < scene.OrphanCount; i++)
        {
            if (!scene.OrphanVisualParentAt(i).IsNull) continue;   // replayed inside the former parent's Walk
            var o = scene.OrphanAt(i, out float px, out float py);
            if ((scene.Flags(o) & NodeFlags.ConnectedOverlay) != 0) continue;   // an overlay-flagged orphan draws in the top band, not here
            Walk(scene, rootDl, images, o, Affine2D.Translation(px, py), 1f, 0, RectF.Infinite, in focus, in textEdit, scrollThumb, scrollTrack,
                1f, 1f, false, false, default, skipRoots, null, 0, true, false, ref stats);
        }

        // E5 drop-spotlight SCRIM band. One explicit band — a flat DragVisualTok.ScrimColor rect with a rounded CUTOUT
        // per compatible destination — emitted AFTER the main pass + the orphan fallback (so it covers all ordinary
        // content) and BEFORE the ghost / connected-overlay / chip bands (so the lifted visual and the drag chip paint
        // ABOVE it). It replaces the old multiply/divide hack (root opacity ×0.28, then ÷0.28 back on every spotlight
        // subtree), which mutated a channel the nodes themselves own.
        //
        // Compositing: the band is a flat OPACITY GROUP at ScrimOpacity. Inside it the scrim fills at alpha 1 and each
        // cutout ERASES (DestOut) to premultiplied zero, so the composite lays exactly one uniform veil with clean,
        // anti-aliased, per-corner-rounded windows — the erase can only reach the group's own RT, never the canvas
        // under it. Costs one offscreen composite for the scrim's rect, and only while a drag actually has spotlight
        // destinations. Alloc-free: pure rect math over the scene's own root list, no scratch buffers.
        if (scene.DropSpotlightActive)
        {
            RectF scrim = scene.SpotlightScrimClip ?? scene.AbsoluteRect(scene.Root);
            if (!scrim.IsEmpty)
            {
                ulong scrimKey = (ulong)ScrimBandDepth << 32;
                int scrimPushOffset = rootDl.BytePosition;
                rootDl.PushOpacityLayer(scrim, default, DragVisualTok.ScrimOpacity, scrimKey);
                rootDl.PatchOpacityLayerExtent(scrimPushOffset, in scrim);
                rootDl.FillRoundRect(new RectF(0f, 0f, scrim.W, scrim.H), default, DragVisualTok.ScrimColor,
                                 Affine2D.Translation(scrim.X, scrim.Y), 1f, scrimKey);
                int rootCount = scene.DropSpotlightRootCount;
                for (int i = 0; i < rootCount; i++)
                {
                    var root = scene.DropSpotlightRootAt(i);
                    if (root.IsNull || !scene.IsLive(root)) continue;   // freed since the last cold refresh
                    if ((scene.Flags(root) & NodeFlags.Visible) == 0) continue;
                    RectF hole = scene.AbsoluteRect(root);
                    // The destination's own ancestor scissors bound what of it is actually on screen — a row scrolled
                    // half out of its list must not punch a window through the list's edge. Walk to the root and fold
                    // every ClipsToBounds ancestor in, then scope to the scrim itself.
                    for (var a = scene.Parent(root); !a.IsNull; a = scene.Parent(a))
                        if ((scene.Flags(a) & NodeFlags.ClipsToBounds) != 0)
                            hole = hole.Intersect(scene.AbsoluteRect(a));
                    hole = hole.Intersect(scrim);
                    if (hole.IsEmpty) continue;
                    // The destination's OWN corner radii, so the window reads as that card/row and not as a rectangle
                    // cut around it. Clamped by the DrawList's shape contract when the clip shaved the box.
                    rootDl.EraseRoundRect(new RectF(0f, 0f, hole.W, hole.H), scene.Paint(root).Corners, 1f,
                                      Affine2D.Translation(hole.X, hole.Y), 1f, scrimKey);
                }
                rootDl.PopLayer(scrim, scrimKey);
            }
        }

        // E5 drag-ghost top band: walk the ghost subtree at its LIVE parent-world origin (scroll / animated ancestor
        // translations included — AbsoluteRect minus the node's own bounds offset and translate; Walk re-applies
        // both) with an INFINITE clip. Emitted last ⇒ the painter draws it over the whole frame.
        if (hasGhost)
        {
            var abs = scene.AbsoluteRect(ghost);
            ref readonly RectF gb = ref scene.Bounds(ghost);
            ref readonly NodePaint gp = ref scene.Paint(ghost);
            // E11: the popup skipRoots must be threaded through — a windowed popup that happens to live INSIDE the
            // dragged subtree renders in its own popup window, and passing `default` here drew it a second time,
            // unclipped, in the main window's ghost band.
            Walk(scene, rootDl, images, ghost,
                 Affine2D.Translation(abs.X - gb.X - gp.LocalTransform.Dx, abs.Y - gb.Y - gp.LocalTransform.Dy),
                 1f, 1 << 16, RectF.Infinite, in focus, in textEdit, scrollThumb, scrollTrack,
                 1f, 1f, false, false, default, skipRoots, null, 0, true, false, ref stats);
        }

        // Connected-animation overlay band: flying shared-element (Hero) visuals. Each overlay draws in a top band ABOVE
        // the drag ghost (depth (1<<16)|1) at its own world origin, clipped to scene.OverlayClip (RectF.Infinite ⇒
        // unbounded; a content-region rect ⇒ the fly stays on the page and never sails over the sidebar/chrome, while
        // still clearing the inner rail scissor so the cover isn't cut off). Its LocalTransform carries the animated fly
        // translate+scale (set by ConnectedAnimation); AbsoluteRect strips only the node's own bounds offset + translate.
        RectF overlayClip = scene.OverlayClip;
        for (int i = 0; i < overlayCount; i++)
        {
            var ov = scene.OverlayAt(i);
            if (!scene.IsLive(ov)) continue;
            var abs = scene.AbsoluteRect(ov);
            ref readonly RectF ob = ref scene.Bounds(ov);
            ref readonly NodePaint op = ref scene.Paint(ov);
            Walk(scene, rootDl, images, ov,
                 Affine2D.Translation(abs.X - ob.X - op.LocalTransform.Dx, abs.Y - ob.Y - op.LocalTransform.Dy),
                 1f, (1 << 16) | 1, overlayClip, in focus, in textEdit, scrollThumb, scrollTrack,
                 1f, 1f, false, false, default, default, null, 0, true, false, ref stats);
        }
        // E5 drag-OVERLAY top band (the chip): same hoist as the ghost band above, at band depth (1<<16)|2 and emitted
        // AFTER the connected-animation overlays, so the drag chip is the topmost thing in the frame. Unclipped, at
        // parent opacity 1 (the chip owns its own alpha), with the popup skipRoots threaded like every other band.
        if (hasDragOverlay)
        {
            var abs = scene.AbsoluteRect(dragOverlay);
            ref readonly RectF ob = ref scene.Bounds(dragOverlay);
            ref readonly NodePaint op = ref scene.Paint(dragOverlay);
            Walk(scene, rootDl, images, dragOverlay,
                 Affine2D.Translation(abs.X - ob.X - op.LocalTransform.Dx, abs.Y - ob.Y - op.LocalTransform.Dy),
                 1f, (1 << 16) | 2, RectF.Infinite, in focus, in textEdit, scrollThumb, scrollTrack,
                 1f, 1f, false, false, default, skipRoots, null, 0, true, false, ref stats);
        }
        // The detached fly snapshots: the topmost band (after every scene band), in the root slice's tail.
        if (!detached.IsEmpty) RecordDetachedNodes(rootDl, images, detached, scene.OverlayClip);

        slices.EndPass(keptAll);

        // Lay out the composite plan at the scene's current poses (no stream is written). A slice whose placement moved
        // since the last turn adds its old ∪ new footprint to the repaint set.
        slices.Place(scene, ref stats.Repaint);

        // Publish this record's hole set for the between-frames RectOverVideoHole query (see that method).
        PublishVideoRects(slices.VideoRects);
        PublishPosedHoles(slices.PosedHoles);
        if (standalone)
        {
            DrawList root = slices.Arena(SliceRecorder.RootSlot);
            dl.AppendRaw(root.Bytes, root.SortKeys, root.CommandCount, root.OpcodeStats);
        }
        return stats.ToStats() with { Slices = slices.LastStats };
    }

    /// <summary>A composite-only turn (render thread; only slice poses moved, nothing recorded): re-place the retained
    /// slices at the snapshot's current poses. Returns the turn's repaint region (moved slices' old ∪ new footprints).</summary>
    internal RepaintDamageRegion Compose(SceneRecordingSnapshot scene, SliceRecorder slices)
    {
        RepaintDamageRegion repaint = default;
        slices.Place(scene, ref repaint);
        slices.NoteCompositeOnly();
        PublishVideoRects(slices.VideoRects);
        PublishPosedHoles(slices.PosedHoles);
        return repaint;
    }

    private void PublishVideoRects(ReadOnlySpan<RectF> holes)
    {
        int n = Math.Min(holes.Length, _publishedVideoRects.Length);
        for (int v = 0; v < n; v++) _publishedVideoRects[v] = holes[v];
        _publishedVideoRectCount = n;
    }

    private void PublishPosedHoles(ReadOnlySpan<FluentGpu.Media.VideoPosedHole> holes)
    {
        int n = Math.Min(holes.Length, _publishedPosedHoles.Length);
        holes[..n].CopyTo(_publishedPosedHoles);
        _publishedPosedHoleCount = n;
    }

    /// <summary>Draw the <see cref="FluentGpu.Animation.DetachedAnimSlab"/>'s live snapshots (the connected-animation
    /// Hero fly; presence exits) in the top band — the rework's replacement for the live-overlay Walk. Each row draws
    /// from its VALUE snapshot (no live node): the world/opacity/corners/image are baked by
    /// <c>ConnectedAnimation.SyncDetached</c>; fit + image readiness resolve here exactly like the main-pass image case.
    /// Emitted LAST (after <see cref="Record"/>) so it paints above all content; clipped to <paramref name="clip"/>
    /// (a content-region rect; <c>RectF.Infinite</c> ⇒ unbounded).</summary>
    public void RecordDetached(SceneRecordingSnapshot scene, DrawList dl, ImageRecordingSnapshot? images, FluentGpu.Animation.DetachedAnimSlab detached, RectF clip)
    {
        if (detached.Count == 0) return;
        for (int s = 0; s < detached.NodeCount; s++)
            RecordDetachedNode(dl, images, in detached.At(s), clip);
    }

    internal void RecordDetachedNodes(DrawList dl, ImageRecordingSnapshot? images, ReadOnlySpan<FluentGpu.Animation.DetachedNode> detached, RectF clip)
    {
        foreach (ref readonly var node in detached) RecordDetachedNode(dl, images, in node, clip);
    }

    private void RecordDetachedNode(DrawList dl, ImageRecordingSnapshot? images, in FluentGpu.Animation.DetachedNode d, RectF clip)
    {
        if (!d.InUse || (VisualKind)d.Kind != VisualKind.Image || d.ImageId == 0) return;
        bool clipped = clip.W < 1e7f && clip.H < 1e7f;   // a finite content-region clip (RectF.Infinite ⇒ unbounded fly)
        if (clipped) dl.PushClip(clip);
        {
            var ih = new ImageHandle(d.ImageId);
            bool ready = images is not null && images.StateOf(ih) == ImageState.Ready;
            float fadeStart = float.NaN, fadeDur = 0f;
            int fadeEase = 0;
            if (images is not null && images.FadeParamsOf(ih, out fadeStart, out fadeDur, out fadeEase)) { }
            RectF local = new RectF(0f, 0f, d.Bounds.W, d.Bounds.H);
            RectF drawRect = local;
            RectF uv = new RectF(0f, 0f, 1f, 1f);
            if (ready && images is not null)
            {
                var (srcW, srcH) = images.SizeOf(ih);
                (drawRect, uv) = ImageContentFit((ImageFit)d.Fit, in local, srcW, srcH);
            }
            ulong key = (ulong)((1 << 16) | 1) << 32;
            bool localClip = !d.ClipRect.IsInfinite;
            if (localClip) dl.PushClip(d.WorldTransform.TransformBounds(d.ClipRect).Intersect(clip), key);
            dl.DrawImage(drawRect, d.Corners, d.ImageId, ready, d.Fill, d.WorldTransform, d.Opacity, uv, fadeStart, fadeDur, fadeEase, key);
            if (localClip) dl.PopClip(key);
        }
        if (clipped) dl.PopClip();
    }

    /// <summary>
    /// Record ONE subtree into its own DrawList, re-origined so the subtree's on-screen top-left lands at the
    /// DrawList's (0,0) — the root-override path for E4 out-of-bounds popup windows (the popup subtree stays in the
    /// single SceneStore; the host presents this list on the popup window's own swapchain). <paramref name="originDip"/>
    /// is the subtree's window-DIP top-left (= the popup's placed position): the walk starts at the subtree root with
    /// a parent-world translation of (parentAbs − origin), so every node's own bounds/transform chain composes exactly
    /// as in the main pass, shifted into popup-window space. Parent-scoped exit orphans are replayed by this same Walk,
    /// so an exiting popup row stays in the popup swapchain rather than leaking into the main window.
    /// </summary>
    public SceneRecordStats RecordSubtree(SceneRecordingSnapshot scene, DrawList dl, ImageRecordingSnapshot? images, in FocusVisualStyle focus,
                                                 ColorF scrollThumb, ColorF scrollTrack, in TextEditStyle textEdit,
                                                 NodeHandle root, Point2 originDip)
    {
        dl.Reset();
        if (root.IsNull || !scene.IsLive(root)) return default;

        float pax = 0f, pay = 0f;
        var parent = scene.Parent(root);
        if (!parent.IsNull)
        {
            var pr = scene.AbsoluteRect(parent);   // translation-only ancestor chain (the overlay positioning hosts)
            pax = pr.X;
            pay = pr.Y;
        }
        var stats = new RecordAccumulator { Owner = this, HasActiveVirtualDisclosures = scene.HasActiveVirtualDisclosures };
        Walk(scene, dl, images, root, Affine2D.Translation(pax - originDip.X, pay - originDip.Y), 1f, 0, RectF.Infinite,
             in focus, in textEdit, scrollThumb, scrollTrack, 1f, 1f, false, false, default, default, null, 0, true, false, ref stats);
        return stats.ToStats();
    }

    private bool ContainsNode(ReadOnlySpan<NodeHandle> roots, NodeHandle node)
    {
        for (int i = 0; i < roots.Length; i++)
            if (roots[i] == node) return true;
        return false;
    }

    /// <summary>True when <paramref name="node"/> is inside any skip-root subtree (a windowed-popup wrapper) — its
    /// pixels belong to that popup's own pass, never the main window.</summary>
    private bool UnderAnySkipRoot(SceneRecordingSnapshot scene, ReadOnlySpan<NodeHandle> roots, NodeHandle node)
    {
        if (roots.IsEmpty) return false;
        for (var n = node; !n.IsNull; n = scene.Parent(n))
            if (ContainsNode(roots, n)) return true;
        return false;
    }

    /// <summary>Spatial reuse-scoping (scene-memory.md): stamp the ancestor chains of every special-cased visual so their
    /// possibly-stale spans are neither reused nor stored this frame, while the rest of the tree keeps reusing/culling.
    /// The set: popup skipRoots + live overlays (both excluded from the main pass via <c>skips</c>, and the skip set can
    /// change without a record-dirty mark) + each exit orphan's visual parent (orphans replay INSIDE that parent's Walk)
    /// + each connected-anim fly anchor (source/dest nodes pinned/hidden by <see cref="FluentGpu.Animation.ConnectedAnimation"/>).</summary>
    private void BlockSpecials(SceneRecordingSnapshot scene, SpanTable spans, uint frame,
        ReadOnlySpan<NodeHandle> skipRoots, int overlayCount, ReadOnlySpan<NodeHandle> reuseBlockRoots,
        NodeHandle ghost, NodeHandle dragOverlay, ref RecordAccumulator stats)
    {
        for (int i = 0; i < skipRoots.Length; i++) BlockChain(scene, spans, frame, skipRoots[i], ref stats);
        // The hoisted drag visuals are excluded from the main pass and re-walked in their own top bands, so an ancestor
        // that stores while one of them is lifted stores a subtree with the dragged row missing.
        BlockChain(scene, spans, frame, ghost, ref stats);
        BlockChain(scene, spans, frame, dragOverlay, ref stats);
        for (int i = 0; i < overlayCount; i++)
        {
            var ov = scene.OverlayAt(i);
            if (scene.IsLive(ov)) BlockChain(scene, spans, frame, ov, ref stats);
        }
        for (int i = 0; i < scene.OrphanCount; i++) BlockChain(scene, spans, frame, scene.OrphanVisualParentAt(i), ref stats);
        for (int i = 0; i < reuseBlockRoots.Length; i++) BlockChain(scene, spans, frame, reuseBlockRoots[i], ref stats);
    }

    // Walk start→root stamping each node blocked; early-out at the first already-stamped node (chains share prefixes, so
    // if a node is stamped every ancestor above it already is). A null start (a rootless orphan's visual parent) is a no-op.
    private void BlockChain(SceneRecordingSnapshot scene, SpanTable spans, uint frame, NodeHandle start, ref RecordAccumulator stats)
    {
        for (var n = start; !n.IsNull; n = scene.Parent(n))
        {
            int idx = (int)n.Raw.Index;
            if (spans.IsBlocked(idx, frame)) break;
            spans.MarkBlocked(idx, frame);
            stats.ScopedBlocks++;
        }
    }

    // Walk is recursive with a ~21 KB frame (measured again in Wavee.exe.74360.dmp: SP delta 0x5620 = 21.5 KB). Two
    // distinct failures are guarded here, and they need different guards:
    //
    //  * A CYCLE — the same node on the current path — is a reconciler bug. PushWalkPath aborts that subtree.
    //  * DEPTH. The previous note claimed "no real Wavee page reaches ~70" and so left depth unguarded. The dumps
    //    disprove it: Wavee.exe.74360.dmp overflowed the stack at exactly 68 levels, from Record → Walk with no cycle.
    //    At 21.5 KB a frame, a 1.5 MB stack holds ~68 levels, and an ordinary page (shell → content → scroll → column →
    //    section → card → grid → row → text, plus any expander/virtualizer) genuinely gets there.
    //
    // A depth NUMBER cap is still the wrong instrument — it blanks real pages, which is why it was removed. Probe the
    // ACTUAL remaining stack instead: TryEnsureSufficientExecutionStack is depth-independent, so a page only degrades
    // when it is truly about to run out, and it degrades by clipping the deepest subtree rather than killing the
    // process. A StackOverflowException cannot be caught, so there is no recovering after the fact — the check has to
    // happen before the call.
    //
    // The 21.5 KB frame is itself the underlying inefficiency (WalkCore is one ~1000-line method, so the JIT unions the
    // locals of every branch and inlined callee into a single frame). Shrinking it multiplies the depth budget and is
    // worth doing, but it is a measured optimization, not this guard.
    //
    // NOTE: `depth` still carries a z-band in the high bits for painter order; that is unrelated to these guards.
    private uint[]? t_walkPath;
    private int t_walkPathLen;
    private int s_cycleAbortLogged;
    private int s_depthAbortLogged;

    /// <summary>True while there is stack headroom for another <see cref="WalkCore"/> frame. On the false edge the
    /// caller must stop descending: the deepest subtree goes unpainted, which is visible but survivable — a stack
    /// overflow is neither catchable nor survivable.
    /// <para>NEVER SILENT. The first trip used to go only to <c>Trace</c> (OutputDebugString — invisible without a
    /// debugger) and nothing counted it, which is how a Debug build painted every detail page's row skins but no row
    /// content for a whole session with no clue: the Debug JIT frame of <see cref="WalkCore"/> is far larger than the
    /// measured 21.5 KB Release frame, so the very same 1.5 MB stack tripped this at the depth of a track row's content.
    /// Now every refused subtree is counted into <see cref="SceneRecordStats.DepthAborts"/> (surfaced by the host in
    /// FrameStats / wakediag), the first one is written to <c>Console.Error</c> as well, and Diag counts each one. The
    /// depth budget itself is owned by the host: <c>FluentApp.RunCore</c> runs the UI/frame loop on a dedicated 32 MB
    /// thread precisely so this guard is a last-resort net, not a page-blanking cliff.</para></summary>
    private bool HasWalkStackHeadroom(ref RecordAccumulator stats)
    {
        if (System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack()) return true;
        stats.DepthAborts++;
        if (Diag.CompiledIn && Diag.Enabled)
        {
            Diag.Count("record", "depth-abort");
            Diag.Event("record", $"depth-abort path-depth={t_walkPathLen} — subtree not painted (recording thread out of stack)");
        }
        if (Interlocked.Exchange(ref s_depthAbortLogged, 1) == 0)
        {
            string msg = $"SceneRecorder.Walk out of stack at path depth {t_walkPathLen} — deepest subtree not painted. "
                       + "The recording thread's stack is too small for this scene (see SceneRecordStats.DepthAborts / FluentApp.RunCore).";
            Trace.WriteLine(msg);
            Console.Error.WriteLine("[record] " + msg);
        }
        return false;
    }

    /// <summary>Sort depth for the drop-spotlight scrim band. The high 16 bits of a record depth are the Z-BAND (0 = the
    /// main pass + orphan fallback, 1 = the drag ghost, (1&lt;&lt;16)|1 = connected-animation overlays, (1&lt;&lt;16)|2 =
    /// the drag chip). The scrim is not a band of its own: it is the LAST thing in band 0 — above every ordinary node,
    /// below every hoisted drag/overlay visual — so it takes the top of band 0's depth range.</summary>
    private const int ScrimBandDepth = (1 << 16) - 1;

    /// <summary>Push <paramref name="node"/> onto the current Walk path. False if it is already on the path (a
    /// reconciler cycle). O(depth) int compares — noise next to Walk's per-node work. Thread-static: Record is
    /// single-threaded per scene, and a second thread gets its own path.</summary>
    private bool PushWalkPath(NodeHandle node)
    {
        uint idx = node.Raw.Index;
        uint[] path = t_walkPath ??= new uint[64];
        int n = t_walkPathLen;
        for (int i = 0; i < n; i++)
        {
            if (path[i] != idx) continue;
            if (Interlocked.Exchange(ref s_cycleAbortLogged, 1) == 0)
                Trace.WriteLine($"SceneRecorder.Walk cycle at node {idx} (path depth {n}).");
            Debug.Fail($"SceneRecorder.Walk cycle at node {idx}.");
            return false;
        }
        if (n == path.Length)
        {
            var grown = new uint[path.Length * 2];
            Array.Copy(path, grown, n);
            t_walkPath = path = grown;
        }
        path[n] = idx;
        t_walkPathLen = n + 1;
        return true;
    }

    private void PopWalkPath() => t_walkPathLen--;

    // True when a direct child provably, fully, opaquely covers this node's VISIBLE rect — so the node's own fill/border
    // are dead pixels the child overwrites (all children paint AFTER the node's own visual, in painter order). ALWAYS ON;
    // strict — any doubt returns false. The child's covered rect is computed in the SAME device space as the node's own
    // rect: its LAYOUT bounds (+ its own translation + the parent's ChildShift) transformed through the PARENT's `world`,
    // exactly as the child's own Walk will place it. No AbsoluteRect (that was translation-only, a different space than
    // the full-world nodeDevice — the 2026-07-23 seek-bar regression). Regression-gated by gate.record.occlusion-*.
    private bool IsOccludedByOpaqueChild(SceneRecordingSnapshot scene, NodeHandle node, in Affine2D world,
        float childShiftX, float childShiftY, in RectF nodeDevice, in RectF clip, bool inMotion, SliceRecorder? slices)
    {
        if (inMotion) return false;   // a transform in flight ⇒ the child's persisted LocalTransform may not equal its drawn rect — don't risk it
        RectF visible = nodeDevice.Intersect(clip);
        if (visible.W <= 0f || visible.H <= 0f) return false;
        for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c))
        {
            if (!scene.IsLive(c)) continue;
            var cf = scene.Flags(c);
            if ((cf & NodeFlags.Visible) == 0) continue;
            if ((cf & NodeFlags.ClipsToBounds) != 0) continue;   // a self-clipping child may cover less than its bounds
            // Retained tiles: a translation slice root (scroll content, a sticky/parallax effect) is drawn at a COMPOSITE
            // pose this record never sees — where it sits next turn is not where its layout says, so it covers nothing.
            if (slices is not null && (IsScrollContent(scene, c, out _) || slices.TranslationEffectClass((int)c.Raw.Index) != 0)) continue;
            // Drawn extents diverge from layout bounds under an interaction scale (hover/press grow) or a counter-scale —
            // neither is modeled by cb-through-world, so be conservative and keep the parent's fill.
            if ((cf & (NodeFlags.InteractionAnim | NodeFlags.CounterScaled)) != 0) continue;
            ref readonly NodePaint cp = ref scene.Paint(c);
            if (cp.VisualKind != VisualKind.Box) continue;
            if (cp.Fill.A < 1f || cp.Opacity < 1f) continue;     // must paint FULLY opaque to overwrite
            // ...and stay opaque under the pointer: a hover/press opacity (eased by its own or an ancestor's progress, or
            // stepped by the bare Hovered/Pressed flags) or a translucent hover/press fill lets the parent show through.
            // Static props, so the cull never depends on a hover flip that does not re-record this parent.
            if (cp.HoverOpacity < 1f || cp.PressedOpacity < 1f) continue;   // NaN (unset) compares false
            if ((cp.HoverFill.A is > 0f and < 1f) || (cp.PressedFill.A is > 0f and < 1f)) continue;
            // Fill is the BrushTransition TARGET: mid-fade the child draws LerpLinear(FillFrom, Fill, T), which is not the
            // opaque cover its target claims — culling the parent then would show a hole for the whole fade.
            if ((cf & NodeFlags.SparsePaint) != 0 && scene.TryGetBrushAnim(c, out var cba)
                && (cba.Channels & BrushAnim.FillBit) != 0 && cba.T < 1f) continue;
            if (cp.BlurSigma > 0.01f || cp.OpacityGroup) continue;
            // A clip-rect (an AnimChannel.ClipL/T/R/B reveal, a .StickyClip pose, a collapse cut) is pushed BEFORE the
            // child's own fill, so it scissors that fill too: the child covers only the clipped part of its bounds. A
            // sticky clip is a pose written without a re-record, so it is rejected even while it reads released.
            if (!cp.ClipRect.IsInfinite) continue;
            if (slices is not null && slices.IsStickyClipNode((int)c.Raw.Index)) continue;
            // A fill that does not REPLACE the pixels under it needs the parent's fill beneath it: additive paint adds onto
            // it, a Screen boundary screens onto it, an acrylic surface frosts it (and drops its Fallback fill where the
            // layer runs), an edge fade feathers it to transparent along its edges.
            if ((cf & NodeFlags.SparsePaint) != 0
                && (scene.PaintBlendOf(c) != PaintBlend.SrcOver || scene.LayerBlendOf(c) != LayerBlend.SrcOver
                    || scene.TryGetAcrylic(c, out _) || (scene.TryGetEdgeFade(c, out EdgeFadeSpec cef) && !cef.IsNone)))
                continue;
            if (!float.IsNaN(cp.PresentedW) || !float.IsNaN(cp.PresentedH)) continue;   // a reveal draws non-layout extents
            var cn = cp.Corners;
            // Rounded opaque children may occlude if the VISIBLE rect lies inside the child's bounds deflated by each
            // corner radius (conservative — the rounded nibs never claim coverage). Square children keep the old path
            // (inset 0). Uniform max-radius inset is enough for the content-pane TL radius case (B4 → scroll cull).
            float inset = MathF.Max(MathF.Max(cn.TopLeft, cn.TopRight), MathF.Max(cn.BottomRight, cn.BottomLeft));
            // Only a translated (linear-identity) child is modeled: its drawn rect is its layout bounds shifted by its
            // own Dx/Dy (translation composes cleanly), transformed through the parent `world`. A non-identity linear
            // part (the seek-bar value-fill's Scale(0.3,1)) draws a rect we can't derive from cb through `world` alone —
            // its scale pivots about a transform origin we don't reconstruct here — so reject it.
            var clt = cp.LocalTransform;
            if (clt.M11 != 1f || clt.M22 != 1f || clt.M12 != 0f || clt.M21 != 0f) continue;
            RectF cb = scene.Bounds(c);   // LAYOUT bounds (parent-content space), NOT AbsoluteRect
            RectF childDevice = world.TransformBounds(new RectF(
                childShiftX + cb.X + clt.Dx, childShiftY + cb.Y + clt.Dy, cb.W, cb.H));
            // Containment with a tiny slack: EXPAND the child rect by ε (never shrink `visible`) so AA-irrelevant subpixel
            // rounding can't defeat an otherwise-full opaque cover. For rounded children, also DEFATE by `inset` so the
            // quarter-disc nibs are never treated as covering the parent.
            const float eps = 0.01f;
            float covL = childDevice.X + inset - eps;
            float covT = childDevice.Y + inset - eps;
            float covR = childDevice.X + childDevice.W - inset + eps;
            float covB = childDevice.Y + childDevice.H - inset + eps;
            if (covL <= visible.X && covT <= visible.Y
                && covR >= visible.X + visible.W
                && covB >= visible.Y + visible.H)
                return true;   // this child's opaque fill (square or conservatively-inset rounded) covers the visible rect
        }
        return false;
    }

    private SpanRecordResult Walk(SceneRecordingSnapshot scene, DrawList dl, ImageRecordingSnapshot? images, NodeHandle node, Affine2D parentWorld, float parentOpacity,
                                         int depth, RectF clip, in FocusVisualStyle focus, in TextEditStyle textEdit, ColorF scrollThumb, ColorF scrollTrack,
                                         float parentScaleX, float parentScaleY, bool parentInMotion,
                                         bool parentScrollInMotion, InheritedState inherited,
                                         ReadOnlySpan<NodeHandle> skipRoots, SpanTable? spans, uint spanFrame, bool spanReuseDisabled, bool spanStoreEnabled,
                                         ref RecordAccumulator stats)
    {
        if (!skipRoots.IsEmpty && ContainsNode(skipRoots, node)) return default;   // subtree renders in its own popup window
        if ((scene.Flags(node) & NodeFlags.Visible) == 0) return default;
        if (!PushWalkPath(node)) return default;
        if (!HasWalkStackHeadroom(ref stats)) { PopWalkPath(); return default; }
        // Retained tiles: a PRE-ORDER span-index entry for each of the first SpanIndexDepth levels below a slice root —
        // (slice-space bounds, byte/command range, holds-a-child-marker) — what a per-tile replay (and the flattening
        // seam) culls against. The slice root's own walk is never an entry of its own slice.
        int below = ++stats.SliceDepth;
        int entry = -1, entrySlot = stats.CurSlot, markersBefore = 0;
        if (stats.Slicing && below >= 1 && below <= SliceRecorder.SpanIndexDepth && stats.SelfNode != node)
        {
            entry = stats.Slices!.BeginIndexEntry(entrySlot, dl.BytePosition, dl.SortPosition, below);
            markersBefore = dl.OpcodeStats.CompositeSlice;
        }
        // BoxEl.Blend: an additive subtree is bracketed by a balanced SetBlend pair INSIDE this node's span-index entry, so a
        // clean-span copy carries both ends. Nested additive subtrees emit nothing (depth-counted, per arena: a repaint
        // boundary starts its own arena at depth 0, so the bracket never reaches across it; a cut that can fold back
        // inline re-opens the bracket in its own arena, see EnterSlice).
        bool additive = scene.PaintBlendOf(node) == PaintBlend.Additive;
        bool opened = additive && stats.AdditiveDepth++ == 0;
        if (opened) dl.SetBlend(PaintBlend.Additive);
        bool closed = !additive;
        try
        {
            var r = WalkCore(scene, dl, images, node, parentWorld, parentOpacity, depth, clip, in focus, in textEdit,
                scrollThumb, scrollTrack, parentScaleX, parentScaleY, parentInMotion,
                parentScrollInMotion, inherited, skipRoots, spans, spanFrame,
                spanReuseDisabled, spanStoreEnabled, ref stats);
            if (!closed) { closed = true; stats.AdditiveDepth--; if (opened) dl.SetBlend(PaintBlend.SrcOver); }
            if (entry >= 0)
                stats.Slices!.EndIndexEntry(entrySlot, entry, r.HasBounds ? r.SubtreeBounds : default, dl.BytePosition,
                    dl.SortPosition, dl.OpcodeStats.CompositeSlice != markersBefore);
            return r;
        }
        finally
        {
            if (!closed) { stats.AdditiveDepth--; if (opened) dl.SetBlend(PaintBlend.SrcOver); }   // an exceptional exit still balances
            stats.SliceDepth--;
            PopWalkPath();
        }
    }

    /// <summary>The slice context a cut saves around the child slice's walk.</summary>
    private struct SliceCtx
    {
        public int CurSlot, SliceDepth;
        public ulong CurGen, PriorGen;
        public float CurDx, CurDy, OwnDx, OwnDy;
        public RectF SliceClip;
        public bool GeomCovered;
        public int AdditiveDepth;
    }

    private static SliceCtx SaveSlice(ref RecordAccumulator stats) => new()
    {
        CurSlot = stats.CurSlot, SliceDepth = stats.SliceDepth, CurGen = stats.CurGen, PriorGen = stats.PriorGen,
        CurDx = stats.CurDx, CurDy = stats.CurDy, OwnDx = stats.SliceOwnDx, OwnDy = stats.SliceOwnDy,
        SliceClip = stats.SliceClip, GeomCovered = stats.GeomCovered, AdditiveDepth = stats.AdditiveDepth,
    };

    private static void RestoreSlice(ref RecordAccumulator stats, in SliceCtx c)
    {
        stats.CurSlot = c.CurSlot; stats.SliceDepth = c.SliceDepth; stats.CurGen = c.CurGen; stats.PriorGen = c.PriorGen;
        stats.CurDx = c.CurDx; stats.CurDy = c.CurDy; stats.SliceOwnDx = c.OwnDx; stats.SliceOwnDy = c.OwnDy;
        stats.SliceClip = c.SliceClip;
        stats.GeomCovered = c.GeomCovered;
        stats.AdditiveDepth = c.AdditiveDepth;
    }

    private static void EnterSlice(ref RecordAccumulator stats, SliceRecorder sl, int slot, float ownDx, float ownDy,
        bool carryAdditive = false)
    {
        stats.CurSlot = slot;
        stats.CurDx += ownDx; stats.CurDy += ownDy;
        stats.SliceOwnDx = ownDx; stats.SliceOwnDy = ownDy;
        stats.SliceDepth = 0;
        stats.GeomCovered = false;
        // A child arena opens its own SetBlend brackets. A cut that records inline once the effect budget is spent (or
        // inside an inline group layer) would paint inside an open additive bracket there, so it re-opens that bracket
        // in its own walk: cut or folded, the subtree adds light alike. A repaint boundary starts source-over.
        stats.SelfAdditive = carryAdditive && stats.AdditiveDepth > 0;
        stats.AdditiveDepth = stats.SelfAdditive ? 1 : 0;
        stats.CurGen = sl.CurGen(slot);
        stats.PriorGen = sl.PriorGen(slot);
    }

    /// <summary>Is <paramref name="node"/> the content child of a scroll viewport (the slice cut point of a scroll root)?</summary>
    private static bool IsScrollContent(SceneRecordingSnapshot scene, NodeHandle node, out NodeHandle viewport)
    {
        viewport = scene.Parent(node);
        return !viewport.IsNull && (scene.Flags(viewport) & NodeFlags.Scrollable) != 0 && scene.HasScroll(viewport)
            && scene.ScrollRef(viewport).ContentNode == node;
    }

    /// <summary>
    /// Retained tiles (§A.2): cut a TRANSLATION slice — a scroll viewport's content root, or a translation-only scroll
    /// effect root (sticky / parallax) — at <paramref name="node"/>. The containing stream gets a
    /// <see cref="DrawOp.CompositeSlice"/> marker; the node's subtree records into its own arena in its POSE-FREE space
    /// (the content's scroll translate — or the effect's translation — is identity inside the walk, under an unbounded
    /// clip, so the viewport can move over it without a byte changing) and the pose becomes the slice's composite
    /// offset. A scaled effect, a pose that is not a pure translation of the free one, a spent effect budget or a node
    /// reached twice records INLINE with its pose baked (returns false; the recorder re-records it when that pose moves).
    /// </summary>
    private bool TryCutTranslation(SceneRecordingSnapshot scene, DrawList dl, ImageRecordingSnapshot? images, NodeHandle node,
        in RectF b, in NodePaint p, Affine2D parentWorld, float parentOpacity, int depth, RectF clip,
        in FocusVisualStyle focus, in TextEditStyle textEdit, ColorF scrollThumb, ColorF scrollTrack,
        float parentScaleX, float parentScaleY, bool parentInMotion, bool parentScrollInMotion, InheritedState inherited,
        ReadOnlySpan<NodeHandle> skipRoots, SpanTable? spans, uint spanFrame, bool spanReuseDisabled, bool spanStoreEnabled,
        ref RecordAccumulator stats, out SpanRecordResult result)
    {
        result = default;
        var sl = stats.Slices!;
        int idx = (int)node.Raw.Index;
        bool isContent = IsScrollContent(scene, node, out NodeHandle vp);
        byte effectClass = sl.TranslationEffectClass(idx);
        SliceRecorder.PoseKind pose;
        if (isContent) pose = SliceRecorder.PoseKind.Content;
        else if (effectClass == 1) pose = SliceRecorder.PoseKind.Effect;
        else
        {
            if (effectClass == 2) sl.AddBaked(stats.CurSlot, node, p.LocalTransform);   // a scaled effect: inline, pose baked
            return false;
        }
        // Inside an inline group layer no slice may be cut (its marker would sit inside a layer the tile replay applies to
        // nothing): the subtree records inline with its pose baked, and re-records when that pose moves.
        if (stats.InlineLayerDepth > 0 || (pose == SliceRecorder.PoseKind.Effect && !sl.EffectBudgetLeft))
        {
            if (pose == SliceRecorder.PoseKind.Effect) sl.NoteFolded();
            sl.AddBaked(stats.CurSlot, node, p.LocalTransform);
            return false;
        }
        SliceKind kind = pose == SliceRecorder.PoseKind.Content ? SliceKind.Scroll : SliceKind.Effect;
        int slot = sl.FindOrCreate(idx, node.Raw.Gen, SliceRole.Main, kind);
        if (slot < 0) { sl.AddBaked(stats.CurSlot, node, p.LocalTransform); return false; }
        sl.SetBudget(slot, SliceRecorder.BudgetClass.Effect);

        float ox = b.W * p.OriginX, oy = b.H * p.OriginY;
        Affine2D baseWorld = parentWorld.Translate(b.X, b.Y);
        Affine2D posedLocal = p.LocalTransform;
        Affine2D freeLocal = Affine2D.Identity;
        double baseOffset = double.NaN;
        bool horizontal = false;
        if (pose == SliceRecorder.PoseKind.Content)
        {
            ref readonly ScrollState vsc = ref scene.ScrollRef(vp);
            horizontal = vsc.Orientation == 1;
            baseOffset = sl.ScrollBase(slot, vsc.WindowOrigin);
            NodePaint free = p;
            FluentGpu.Scroll.Runtime.ScrollContentPose.WriteContentTransform(ref free, in b, horizontal,
                (float)(vsc.WindowOrigin - baseOffset), vsc.ZoomFactor);
            freeLocal = free.LocalTransform;
        }
        if (!SliceRecorder.SameLinear(in posedLocal, in freeLocal))
        {
            // a pose that is not a pure translation of the free one (a zoom mid-gesture, an animated scale on the content
            // node): record inline with it baked; the fresh slot, never registered, retires at the end of the pass.
            sl.AddBaked(stats.CurSlot, node, posedLocal);
            return false;
        }
        if (!HasWalkStackHeadroom(ref stats)) return true;   // counted + reported; the subtree goes unpainted, never a stack overflow
        Affine2D posedW = SliceRecorder.Conjugate(in baseWorld, in posedLocal, ox, oy);
        Affine2D freeW = SliceRecorder.Conjugate(in baseWorld, in freeLocal, ox, oy);
        float ownDx = posedW.Dx - freeW.Dx, ownDy = posedW.Dy - freeW.Dy;

        ulong key = (ulong)depth << 32;
        var cmd = new CompositeSliceCmd(idx, node.Raw.Gen, (int)SliceRole.Main, (int)kind, 0, clip, default, default, default, default, key, key);
        dl.CompositeSlice(in cmd, key);
        sl.AddChild(stats.CurSlot, slot);
        sl.SetPose(slot, pose, in baseWorld, ox, oy, in freeLocal, ownDx, ownDy);
        sl.SetMarker(slot, in clip, 0, default, default);
        if (pose == SliceRecorder.PoseKind.Content)
        {
            ref readonly ScrollState vsc = ref scene.ScrollRef(vp);
            bool hasChrome = vsc.AutoEdgeFade || vsc.EdgeCueConfig != 0;
            sl.SetScroll(slot, (int)vp.Raw.Index, vp.Raw.Gen, horizontal, baseOffset, hasChrome,
                hasChrome ? SliceRecorder.ChromeSig(in vsc, SliceRecorder.ShownOffset(scene, vp, in vsc)) : 0UL);
        }

        var saved = SaveSlice(ref stats);
        if (!clip.IsInfinite)
        {
            RectF wclip = SliceRecorder.Offset(in clip, stats.CurDx, stats.CurDy);
            stats.SliceClip = stats.SliceClip.IsInfinite ? wclip : stats.SliceClip.Intersect(wclip);
        }
        stats.SelfNode = node;
        stats.SelfSlot = slot;
        stats.SelfHasLocal = true;
        stats.SelfLocal = freeLocal;
        stats.SelfOmitLayer = false;
        stats.SelfPose = pose;
        stats.SelfAcrylic = false;
        stats.SelfStickyClip = false;   // a translation root's sticky clip (a nonsensical .Sticky().StickyClip()) records baked
        EnterSlice(ref stats, sl, slot, ownDx, ownDy, carryAdditive: true);
        SpanRecordResult res;
        try
        {
            res = WalkCore(scene, sl.Arena(slot), images, node, parentWorld, parentOpacity, depth, RectF.Infinite, in focus, in textEdit,
                scrollThumb, scrollTrack, parentScaleX, parentScaleY, parentInMotion, parentScrollInMotion, inherited, skipRoots,
                spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
        }
        finally
        {
            stats.SelfNode = NodeHandle.Null;
            RestoreSlice(ref stats, in saved);
        }
        // What the slice can paint at its record-time pose, bounded by its composite clip — the containing node's extent
        // (span bounds, damage, cull) covers the viewport it owns, never the whole (far taller) content.
        if (res.HasBounds)
        {
            RectF painted = SliceRecorder.Offset(res.SubtreeBounds, ownDx, ownDy);
            result.Include(clip.IsInfinite ? painted : painted.Intersect(clip));
        }
        return true;
    }

    private SpanRecordResult WalkCore(SceneRecordingSnapshot scene, DrawList dl, ImageRecordingSnapshot? images, NodeHandle node, Affine2D parentWorld, float parentOpacity,
                                         int depth, RectF clip, in FocusVisualStyle focus, in TextEditStyle textEdit, ColorF scrollThumb, ColorF scrollTrack,
                                         float parentScaleX, float parentScaleY, bool parentInMotion,
                                         bool parentScrollInMotion, InheritedState inherited,
                                         ReadOnlySpan<NodeHandle> skipRoots, SpanTable? spans, uint spanFrame, bool spanReuseDisabled, bool spanStoreEnabled,
                                         ref RecordAccumulator stats)
    {
        stats.NodesVisited++;
        NodeFlags flags = scene.Flags(node);
        // Retained tiles: the slice-root walk a containing cut handed over (consumed here, exactly once). Such a walk
        // records into the slice's own arena — KEPT whole when its root is clean (zero bytes), else swapped + walked.
        bool isSliceSelf = !stats.SelfNode.IsNull && stats.SelfNode == node;
        int selfSlot = -1;
        bool selfHasLocal = false, omitLayer = false, acrylicCut = false, selfSticky = false, selfAdditive = false;
        Affine2D selfLocal = default;
        SliceRecorder.PoseKind selfPose = SliceRecorder.PoseKind.None;
        if (isSliceSelf)
        {
            selfSlot = stats.SelfSlot;
            selfHasLocal = stats.SelfHasLocal;
            selfLocal = stats.SelfLocal;
            omitLayer = stats.SelfOmitLayer;
            selfPose = stats.SelfPose;
            acrylicCut = stats.SelfAcrylic;
            selfSticky = stats.SelfStickyClip;
            selfAdditive = stats.SelfAdditive;
            stats.SelfNode = NodeHandle.Null;
            stats.SelfAcrylic = false;
            stats.SelfStickyClip = false;
            stats.SelfAdditive = false;
        }
        bool maybeSparsePaint = (flags & NodeFlags.SparsePaint) != 0;
        bool hasInteractionAnim = (flags & NodeFlags.InteractionAnim) != 0;
        ref readonly InteractionInfo interaction = ref scene.Interaction(node);
        // Same mask as the cascade's IsNestedHoverBoundary (AnimScheduler.Hover.cs) — "does this node own its own
        // interaction scope". PressedBit belongs in it: a selection row that handles only OnPointerPressed/Released is a
        // control in its own right, and leaving the bit out made it a cascade boundary that nonetheless INHERITED its
        // ancestor's progress here at record time. The dispatcher publishes HoverWithin for PressedBit nodes, so such a
        // node still lights from its own hover (:838 below).
        const int interactiveMask = InteractionInfo.ClickBit | InteractionInfo.PointerBit | InteractionInfo.PressedBit;
        bool nodeInteractive = (interaction.HandlerMask & interactiveMask) != 0;
        // The full boundary rule (IsNestedHoverBoundary): a transparent listener is interactive for its OWN paint but
        // is not a scope — its subtree keeps reading the enclosing scope's state (InheritedState.ForChild).
        bool scopeBoundary = nodeInteractive && (interaction.HandlerMask & InteractionInfo.HoverScopeTransparentBit) == 0;
        InteractionAnim localInteraction = default;
        bool hasOwnInteraction = hasInteractionAnim && scene.TryGetInteract(node, out localInteraction);
        float localHoverT = 0f, localPressT = 0f;
        bool hasLocalProgress = false;
        if (hasOwnInteraction)
        {
            localHoverT = (flags & NodeFlags.HoverWithin) != 0 ? 1f : localInteraction.HoverT;
            localPressT = localInteraction.PressT;
            hasLocalProgress = true;
        }
        else if ((flags & NodeFlags.HoverWithin) != 0)
        {
            localHoverT = 1f;
            hasLocalProgress = true;
        }

        // Motion gate: a transform write this frame (fling/drag/FLIP — every motion writer marks TransformDirty; the host
        // clears the bits right after record) means this subtree is mid-motion (occlusion cull off, self-blur InMotion).
        // A translation slice root's own transform is its composite pose, stripped from its walk — it is never "motion"
        // for what it records.
        bool inMotion = parentInMotion || (!selfHasLocal && (flags & NodeFlags.TransformDirty) != 0);
        // Scroll motion is deliberately LOCAL to this viewport chain (a descendant hint only; nothing recorded keys on it).
        bool scrollInMotion = parentScrollInMotion;
        if ((flags & NodeFlags.Scrollable) != 0 && scene.HasScroll(node) && !scrollInMotion)
        {
            ref readonly var scrollState = ref scene.ScrollRef(node);
            var scrollContent = scrollState.ContentNode;
            if (!scrollContent.IsNull && scene.IsLive(scrollContent)
                && (scene.Flags(scrollContent) & NodeFlags.TransformDirty) != 0)
                scrollInMotion = true;
        }

        ref readonly RectF b = ref scene.Bounds(node);
        ref readonly NodePaint p = ref scene.Paint(node);

        // ── retained tiles: a TRANSLATION slice root (scroll content / sticky-parallax effect) is cut here ──
        if (!isSliceSelf && stats.Slicing
            && TryCutTranslation(scene, dl, images, node, in b, in p, parentWorld, parentOpacity, depth, clip, in focus, in textEdit,
                scrollThumb, scrollTrack, parentScaleX, parentScaleY, parentInMotion, parentScrollInMotion, inherited, skipRoots,
                spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats, out var cutResult))
        {
            stats.NodesVisited--;   // the slice-root walk counted it
            return cutResult;
        }

        // node-local → device: parent ∘ translate(node pos) ∘ (local transform about the node's transform-origin). A
        // translation slice root walks under its POSE-FREE local (the cut computed it).
        Affine2D localT = selfHasLocal ? selfLocal : p.LocalTransform;
        Affine2D world = parentWorld.Translate(b.X, b.Y);
        float ox = b.W * p.OriginX, oy = b.H * p.OriginY;   // transform origin (default centre; e.g. top edge for a menu unfold)
        if (!localT.IsIdentity)
            world = world.Translate(ox, oy).Multiply(localT).Translate(-ox, -oy);
        // Interaction-driven composited scale (thumb hover-grow): scale about the node centre by the eased hover/press.
        // The progress comes from the node's own row if it is interactive, else from the nearest interactive ancestor
        // (a slider/scrollbar thumb is non-interactive — drag stays on the track — but grows when the control is used).
        if (hasOwnInteraction && (localInteraction.HoverScale != 1f || localInteraction.PressScale != 1f))
        {
            TryResolveInteractionProgress(in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT, out float useH, out float useP);
            float hs = 1f + (localInteraction.HoverScale - 1f) * useH;
            float isc = hs + (localInteraction.PressScale - hs) * useP;
            if (MathF.Abs(isc - 1f) > 0.0008f)
                world = world.Translate(ox, oy).Multiply(Affine2D.Scale(isc, isc)).Translate(-ox, -oy);
        }
        // ScaleCorrect counter-scale: a child that opted out of an ancestor's animated scale applies the inverse (about
        // its centre) so it stays undistorted, and passes an un-scaled factor to ITS children (Framer-Motion projection).
        float netScaleX = parentScaleX, netScaleY = parentScaleY;
        if ((flags & NodeFlags.CounterScaled) != 0 && (MathF.Abs(parentScaleX - 1f) > 1e-4f || MathF.Abs(parentScaleY - 1f) > 1e-4f))
        {
            float cx = b.W * 0.5f, cy = b.H * 0.5f;
            world = world.Translate(cx, cy).Multiply(Affine2D.Scale(1f / parentScaleX, 1f / parentScaleY)).Translate(-cx, -cy);
            netScaleX = 1f; netScaleY = 1f;
        }
        float childScaleX = netScaleX * localT.M11;   // the scale this node imposes on its children
        float childScaleY = netScaleY * localT.M22;

        float opacity = parentOpacity * ResolveOpacity(flags, in p, in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT);

        // Presented extent (layout-transition "Reveal"): the node's own fill + its child clip are drawn at PresentedW/H
        // when set (which may exceed the model bounds during a shrink), while layout/hit-test keep the model Bounds.
        float pw = float.IsNaN(p.PresentedW) ? b.W : p.PresentedW;
        float ph = float.IsNaN(p.PresentedH) ? b.H : p.PresentedH;
        var pb = new RectF(b.X, b.Y, pw, ph);   // presented rect (== b when no reveal in flight)

        ulong key = (ulong)depth << 32;   // painter order ~ depth for the slice
        var local = new RectF(0f, 0f, pw, ph);
        var deviceBounds = world.TransformBounds(local);
        bool overlapsClip = deviceBounds.Overlaps(clip);
        bool hasSelfBlur = p.BlurSigma > 0.01f;
        SelfBlurRecordGeometry blurGeometry = hasSelfBlur
            ? SelfBlurRegion.ComputeRecordGeometry(deviceBounds, clip, p.BlurSigma,
                scene.DeviceScale > 0f ? scene.DeviceScale : 1f)
            : default;
        // A self-blur paints beyond its sharp layout/device rect. Store the UNCLIPPED halo in every span so an
        // off-screen clean ancestor cannot discard a row whose Gaussian support has already entered the viewport.
        RectF visualBounds = hasSelfBlur && !blurGeometry.OutputBounds.IsEmpty
            ? blurGeometry.OutputBounds
            : deviceBounds;
        var result = new SpanRecordResult();
        result.Include(visualBounds);

        // ── .StickyClip (gpu-renderer.md §13.1e): the node's NodePaint.ClipRect is a ClipTop scroll POSE — a half-plane
        // that stays viewport-fixed while the node rides the page. Like the item band's viewport-fixed clip, it becomes a
        // COMPOSITE-TIME clip on the node's slice marker (CompositeSliceFlags.StickyClip): the slice records pose-free
        // bytes and the composite plan cuts its item(s) at the band line read from the node's current pose, so a page
        // scroll records nothing. Exact wherever the clip is a plain scissor over everything the node emits: not when the
        // node's own paint escapes its clip in the paint route (a shadow / arc drawn before the clip push), nor for a blur
        // (a blur of clipped content is not a clipped blur) or an acrylic frost. Those — and a sticky node inside an
        // inline group layer, past the effect budget, or under the StickyClipInPaint knockout — record the clip INLINE
        // and re-record it when the pose moves (SliceRecorder.BakeClip).
        int nodeIdx = (int)node.Raw.Index;
        bool stickyNode = stats.Slicing && stats.Slices!.IsStickyClipNode(nodeIdx);
        bool stickyCut = stickyNode && !isSliceSelf && stats.InlineLayerDepth == 0 && !stats.Slices!.InlineStickyThisPass
            && StickyClipIsPlainScissor(scene, node, maybeSparsePaint);

        // ── the node's GROUP decisions, hoisted above the span block (a layer group is a slice cut) ────────────────
        // Edge fade (gpu-renderer.md): feather the subtree's alpha (+ optional blur) near chosen edges, following the
        // rounded corners. Takes precedence over the opacity/self-blur groups. Explicit (BoxEl/ScrollEl.EdgeFade) or a
        // scroller's AutoEdgeFade. Resolve PRESENCE independently of sharp visibility: a node carrying both effects remains
        // an edge-fade node even while only a hypothetical self-blur halo would overlap. Skipped at alpha ≈ 0: the
        // composite multiplies by GroupAlpha, so at alpha 0 it writes the destination back unchanged — exact dead work;
        // skipping also skips the `opacity = 1f` group reset, so the subtree keeps drawing at its true ≈0 alpha.
        EdgeFadeSpec edgeFade = default;
        bool hasEdgeFade = TryResolveEdgeFade(scene, node, flags, maybeSparsePaint, out edgeFade);
        bool isEdgeFade = overlapsClip && hasEdgeFade && opacity > 0.001f;
        // An opacity-zero stagger row still has a non-zero authored blur during its delay. It contributes no pixels, so
        // allocating/clearing an offscreen RT and running two Gaussian passes is exact dead work. Keep walking for
        // animation/hit state, but do not emit a blur group.
        bool isBlurCandidate = !hasEdgeFade && opacity > 0.001f && hasSelfBlur
            && !blurGeometry.VisibleOutput.IsEmpty && !blurGeometry.RequiredSource.IsEmpty;
        bool isBlurGroup = isBlurCandidate;
        // ── flat opacity group (NodePaint.OpacityGroup, WinUI Composition LayerVisual semantics): the subtree renders at
        // FULL alpha into a pooled offscreen RT and composites ONCE at the group alpha. Skipped at alpha ≈ 1 (a no-op) and
        // ≈ 0 (invisible — still walked for hit/anim state). A self-blur group SUBSUMES it.
        bool isOpacityGroup = !isEdgeFade && !isBlurGroup && p.OpacityGroup && opacity < 0.999f && opacity > 0.001f && overlapsClip;
        if (isBlurGroup || (isEdgeFade && edgeFade.Mode != EdgeFadeMode.Fade)) stickyCut = false;
        // EdgeFadeSpec.WhileStuck: the fade exists only while the node's own sticky clip is engaged. On a composite sticky
        // cut the MARKER carries it (CompositeSliceFlags.FadeWhileStuck) and the composite plan decides per turn from the
        // clip's pose; recorded inline (a baked clip) the pose is known here — a released clip (Infinite) records no fade.
        // A collapse cut (NodePaint.CollapseCut) lies on the presented edge the fade rect already ends at: to the fade it
        // is no clip at all (and never an engaged sticky clip).
        bool fadeClipped = !p.ClipRect.IsInfinite && !NodePaint.IsCollapseCut(in p.ClipRect);
        if (isEdgeFade && edgeFade.WhileStuck && !stickyCut && !fadeClipped) isEdgeFade = false;
        PushLayerCmd groupLayer = default;
        RectF groupSourceClip = default;
        if (isEdgeFade)
        {
            // DIP→device px scale from this node's own box (uniform DPI ⇒ sx≈sy); corners + bands scale with it.
            float efsx = b.W > 0.01f ? deviceBounds.W / b.W : 1f;
            float efsy = b.H > 0.01f ? deviceBounds.H / b.H : 1f;
            var efc = new CornerRadius4(p.Corners.TopLeft * efsx, p.Corners.TopRight * efsx, p.Corners.BottomRight * efsx, p.Corners.BottomLeft * efsx);
            RectF edgeCompositeClip = deviceBounds.Intersect(clip);
            // Feather from the VISIBLE boundary, not the element box. The bands are measured off the FADE RECT, so a
            // viewport-anchored ClipRect (a StickyClip scroll effect — a sticky cut that walks up the node while the page
            // scrolls) IS the edge the content dissolves at. Un-clipped nodes keep deviceBounds.
            RectF fadeRect = deviceBounds;
            if (!stickyCut && fadeClipped)   // a composite sticky clip cuts the fade rect at placement instead
            {
                RectF wc = world.TransformBounds(p.ClipRect);
                edgeCompositeClip = edgeCompositeClip.Intersect(wc);
                fadeRect = deviceBounds.Intersect(wc);
            }
            groupLayer = DrawList.EdgeFadeLayerCmd(fadeRect, edgeCompositeClip, efc, opacity, (int)edgeFade.Edges,
                (edgeFade.Edges & EdgeMask.Left) != 0 ? edgeFade.BandLeft * efsx : 0f,
                (edgeFade.Edges & EdgeMask.Top) != 0 ? edgeFade.BandTop * efsy : 0f,
                (edgeFade.Edges & EdgeMask.Right) != 0 ? edgeFade.BandRight * efsx : 0f,
                (edgeFade.Edges & EdgeMask.Bottom) != 0 ? edgeFade.BandBottom * efsy : 0f,
                (int)edgeFade.Falloff, edgeFade.Intensity,
                edgeFade.Mode == EdgeFadeMode.Fade ? 0f : edgeFade.BlurSigma * efsx);
        }
        else if (isBlurGroup)
        {
            groupLayer = DrawList.BlurLayerCmd(deviceBounds, p.Corners, p.BlurSigma, opacity, clip);
            groupSourceClip = blurGeometry.RequiredSource;
        }
        else if (isOpacityGroup)
            groupLayer = DrawList.OpacityLayerCmd(deviceBounds, p.Corners, opacity);
        // In-app acrylic (resolved here too: an acrylic node is an effect slice root). Materials.AcrylicEnabled is WinUI's
        // own fallback policy read at the ONE emission point: with it false no PushLayer is written and every frosted
        // surface resolves to the FallbackColor fill it already paints underneath.
        AcrylicSpec ac = default;
        bool isAcrylic = maybeSparsePaint && deviceBounds.Overlaps(isBlurGroup ? groupSourceClip : clip)
            && FluentGpu.Dsl.Materials.AcrylicEnabled && scene.TryGetAcrylic(node, out ac);

        // ── retained tiles: a LAYER effect slice root (group opacity / self-blur / edge fade / acrylic) is cut here ──
        // Budgets (gpu-renderer.md §13.1e): an ACRYLIC surface always cuts its own slice — a frost exists only as a
        // composite Backdrop item, so it is never foldable — bounded by its own AcrylicSliceCap; a DISTRIBUTABLE edge fade
        // (fade mode, alpha 1, no paint of its own: the record-time half of the distribution test) spends no budget — the
        // composite plan feathers its items analytically whenever placement allows; everything else (opacity group,
        // self-blur, an edge fade over its own paint) spends EffectSliceCap and past it records inline. Inside an inline
        // group layer nothing is cut (see InlineLayerDepth).
        bool distributeFade = isEdgeFade && edgeFade.Mode == EdgeFadeMode.Fade && opacity >= 0.999f
            && !HasOwnPaint(scene, node, maybeSparsePaint, in p);
        // A slice ROOT that itself needs a group layer — the scene root, a scroll content root, a sticky/parallax root that
        // is (say) a fading scroller — cannot put that layer on its own marker (it IS the slice), and recorded inline it
        // would fold everything below it (nothing is cut inside an inline layer: every scroll of a nested scroller would
        // re-record). Its layer is cut as a nested Layer-role slice of the same node instead; this slice's own stream is
        // then that one marker. (An effect slice's own walk already carries its layer on its marker — omitLayer — and an
        // acrylic slice's own walk records its frost layer itself.)
        bool selfLayerCut = isSliceSelf && !omitLayer && !acrylicCut;
        if ((!isSliceSelf || selfLayerCut) && stats.Slicing && (isEdgeFade || isBlurGroup || isOpacityGroup || isAcrylic))
        {
            var sl = stats.Slices!;
            var budget = isAcrylic ? SliceRecorder.BudgetClass.Acrylic
                : distributeFade ? SliceRecorder.BudgetClass.FreeFade : SliceRecorder.BudgetClass.Effect;
            bool budgetLeft = budget switch
            {
                SliceRecorder.BudgetClass.Acrylic => sl.AcrylicBudgetLeft,
                SliceRecorder.BudgetClass.FreeFade => true,
                _ => sl.EffectBudgetLeft,
            };
            if (stats.InlineLayerDepth > 0 || !budgetLeft) { if (!isAcrylic) sl.NoteFolded(); }
            else
            {
                SliceRole role = selfLayerCut ? SliceRole.Layer : SliceRole.Main;
                int slot = sl.FindOrCreate((int)node.Raw.Index, node.Raw.Gen, role, SliceKind.Effect);
                if (slot >= 0 && HasWalkStackHeadroom(ref stats))
                {
                    sl.SetBudget(slot, budget);
                    if (!isAcrylic && !selfLayerCut) sl.SetLowRes(slot, scene.RepaintBoundaryDown(node));
                    bool group = isEdgeFade || isBlurGroup || isOpacityGroup;
                    int mflags = (group ? (int)CompositeSliceFlags.Layer | (isBlurGroup ? (int)CompositeSliceFlags.InnerClip : 0)
                        | (isEdgeFade && distributeFade ? (int)CompositeSliceFlags.DistributeFade : 0) : 0)
                        | (stickyCut ? (int)CompositeSliceFlags.StickyClip : 0)
                        | (stickyCut && isEdgeFade && edgeFade.WhileStuck ? (int)CompositeSliceFlags.FadeWhileStuck : 0);
                    var acrylic = isAcrylic
                        ? new AcrylicRecipe(ac.Tint, ac.Fallback, ac.TintOpacity, ac.BlurSigma, ac.NoiseOpacity, ac.LuminosityOpacity, ac.FeatherTop)
                        : default;
                    var cmd = new CompositeSliceCmd((int)node.Raw.Index, node.Raw.Gen, (int)role, (int)SliceKind.Effect, mflags, clip,
                        default, group ? groupLayer : default, new ClipCmd(groupSourceClip), deviceBounds, key, key);
                    if (selfLayerCut)
                    {
                        // This slice's stream is the Layer marker alone: KEEP it whole when nothing below changed, the
                        // marker is the one it already holds AND the Layer's content was recorded under the same inputs (else
                        // swap its arena and write the marker afresh). The marker carries no opacity for a bare acrylic and
                        // no inherited state / focus / text-edit / scroll colours for any layer, and record-dirty only rises:
                        // a parent's fade, hover or theme reaches this clean subtree only through the span input signature.
                        ulong layerSig = ComputeSpanInputSig(scene, node, flags, depth, in clip, in world, opacity,
                            parentScaleX, parentScaleY, childScaleX, childScaleY, pw, ph, inMotion, inherited, in focus, in textEdit,
                            scrollThumb, scrollTrack, clipComposite: stickyCut);
                        bool clean = spans is not null && !spanReuseDisabled && scene.RecordDirtyBits(node) == 0
                            && !spans.IsBlocked((int)node.Raw.Index, spanFrame) && stats.KeepDenySlot != selfSlot;
                        if (clean && sl.HoldsOnlyMarker(selfSlot, in cmd, layerSig))
                        {
                            sl.Keep(selfSlot);
                            stats.NodesVisited--;
                            var kept = new SpanRecordResult();
                            kept.Include(sl.BoundsOf(selfSlot));
                            return kept;
                        }
                        // Why (the walk ledger — read-only, never a decision): clean + a different marker = its LAYER inputs
                        // changed (SigMiss); else the keep test's own first failing clause.
                        WalkWhy lwhy = WalkClassifier.Classify(spans is not null, spanReuseDisabled, (uint)stats.SpanReuseDisabledReasons,
                            spans is not null && spans.IsBlocked((int)node.Raw.Index, spanFrame), scene.RecordDirtyBits(node),
                            stats.KeepDenySlot == selfSlot, selfSlot == SliceRecorder.RootSlot, missClass: 2, wholeArena: true, out uint lwhyDetail);
                        sl.BeginWalk(selfSlot, lwhy, lwhyDetail);
                        sl.SetLayerInputSig(selfSlot, layerSig);
                        stats.CurGen = sl.CurGen(selfSlot);
                        stats.PriorGen = sl.PriorGen(selfSlot);
                    }
                    int markerAt = dl.CompositeSlice(in cmd, key);
                    CountGroups(ref stats, isEdgeFade, isBlurCandidate, isBlurGroup);
                    sl.AddChild(stats.CurSlot, slot);
                    sl.SetPose(slot, SliceRecorder.PoseKind.None, Affine2D.Identity, 0f, 0f, Affine2D.Identity, 0f, 0f);
                    sl.SetMarker(slot, in clip, mflags, group ? groupLayer : default, in acrylic, in deviceBounds, p.Corners, opacity);
                    sl.SetSticky(slot, stickyCut, in world);
                    if (stickyCut) sl.UnbakeClip(nodeIdx);
                    var saved = SaveSlice(ref stats);
                    stats.SelfNode = node;
                    stats.SelfSlot = slot;
                    // a Layer slice walks its node under the same (pose-free) local the node's own slice walks it under
                    stats.SelfHasLocal = selfLayerCut && selfHasLocal;
                    stats.SelfLocal = selfLayerCut ? selfLocal : default;
                    stats.SelfOmitLayer = group;   // the group ops ride the marker; the slice records the group's CONTENT
                    stats.SelfPose = SliceRecorder.PoseKind.None;
                    stats.SelfAcrylic = isAcrylic;
                    stats.SelfStickyClip = stickyCut;
                    EnterSlice(ref stats, sl, slot, 0f, 0f, carryAdditive: true);
                    SpanRecordResult res;
                    try
                    {
                        res = WalkCore(scene, sl.Arena(slot), images, node, parentWorld, parentOpacity, depth, clip, in focus, in textEdit,
                            scrollThumb, scrollTrack, parentScaleX, parentScaleY, parentInMotion, parentScrollInMotion, inherited, skipRoots,
                            spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
                    }
                    finally
                    {
                        stats.SelfNode = NodeHandle.Null;
                        RestoreSlice(ref stats, in saved);
                    }
                    // The opacity group's drawn extent (see the PopLayer patch below) rides the marker's layer; a nested
                    // layer or slice inside leaves it unpatched (full-canvas clear + composite) exactly as inline.
                    var arenaStats = sl.Arena(slot).OpcodeStats;
                    if (isOpacityGroup && res.HasBounds && arenaStats.PushLayer == 0 && arenaStats.CompositeSlice == 0)
                    {
                        RectF ext = new RectF(res.SubtreeBounds.X - OpacityGroupExtentPadDip, res.SubtreeBounds.Y - OpacityGroupExtentPadDip,
                                              res.SubtreeBounds.W + 2f * OpacityGroupExtentPadDip, res.SubtreeBounds.H + 2f * OpacityGroupExtentPadDip)
                            .Intersect(clip);
                        if (!ext.IsEmpty)
                        {
                            var patched = cmd with { Layer = groupLayer with { CompositeClip = ext } };
                            dl.PatchCompositeSlice(markerAt, in patched);
                            sl.SetMarker(slot, in clip, mflags, patched.Layer, in acrylic, in deviceBounds, p.Corners, opacity);
                        }
                    }
                    if (selfLayerCut) sl.EndWalk(selfSlot, res.HasBounds ? res.SubtreeBounds : default);
                    stats.NodesVisited--;   // the slice-root walk counted it
                    return res;
                }
            }
        }

        if (stickyCut && (isEdgeFade || isBlurGroup || isOpacityGroup || isAcrylic))
        {
            // The layer could not be cut (budget / slot / headroom): its group records inline and the sticky clip with it —
            // the fade rect takes the clip exactly as the paint route builds it (a WhileStuck fade on a released clip: none).
            stickyCut = false;
            if (isEdgeFade && edgeFade.WhileStuck && p.ClipRect.IsInfinite) isEdgeFade = false;
            if (isEdgeFade && !p.ClipRect.IsInfinite)
            {
                RectF wc = world.TransformBounds(p.ClipRect);
                groupLayer = groupLayer with { DeviceRect = deviceBounds.Intersect(wc), CompositeClip = groupLayer.CompositeClip.Intersect(wc) };
            }
        }
        else if (stickyCut || (!isSliceSelf && stats.Slicing && stats.InlineLayerDepth == 0 && maybeSparsePaint
                 && !(isEdgeFade || isBlurGroup || isOpacityGroup || isAcrylic) && scene.IsRepaintBoundary(node)))
        {
            // A plain sticky node (no layer of its own): cut an Effect slice whose marker carries only the sticky clip. It
            // spends the effect budget; past it the clip records inline (baked).
            // A REPAINT BOUNDARY (BoxEl.RepaintBoundary) takes the same cut without the sticky clip: an isolation slice with
            // no layer and no pose, so its continuously-changing subtree re-rasters only its own tiles and never the tiles
            // of what is painted under / over it (the parent's segments are content-validated per tile; the marker's
            // identity is its node, not its bounds). Past the budget it records inline — identical pixels.
            var sl = stats.Slices!;
            int slot = sl.EffectBudgetLeft ? sl.FindOrCreate(nodeIdx, node.Raw.Gen, SliceRole.Main, SliceKind.Effect) : -1;
            if (slot >= 0 && HasWalkStackHeadroom(ref stats))
            {
                sl.SetBudget(slot, SliceRecorder.BudgetClass.Effect);
                // BoxEl.CompositePose: a posed image layer records POSE-FREE (its own scale / translate becomes a composite
                // parameter, not bytes) when its pose and base world are axis-aligned and its stream has not proved
                // ineligible; otherwise it is the ordinary boundary (pose baked into its tiles).
                bool posed = !stickyCut && scene.IsCompositePose(node) && !sl.PoseFallback(nodeIdx, node.Raw.Gen)
                    && p.LocalTransform.M12 == 0f && p.LocalTransform.M21 == 0f
                    && parentWorld.M12 == 0f && parentWorld.M21 == 0f && parentWorld.M11 != 0f && parentWorld.M22 != 0f;
                if (!stickyCut) sl.SetLowRes(slot, scene.RepaintBoundaryDown(node));
                sl.SetScreen(slot, !stickyCut && scene.LayerBlendOf(node) == LayerBlend.Screen);
                if (!stickyCut && scene.TryGetFeedback(node, out var feedbackState)) sl.SetFeedback(slot, in feedbackState);
                int sflags = stickyCut ? (int)CompositeSliceFlags.StickyClip : 0;
                var cmd = new CompositeSliceCmd(nodeIdx, node.Raw.Gen, (int)SliceRole.Main, (int)SliceKind.Effect, sflags, clip,
                    default, default, default, deviceBounds, key, key);
                dl.CompositeSlice(in cmd, key);
                sl.AddChild(stats.CurSlot, slot);
                if (posed) sl.SetPose(slot, SliceRecorder.PoseKind.Posed, parentWorld.Translate(b.X, b.Y), ox, oy, Affine2D.Identity, 0f, 0f);
                else sl.SetPose(slot, SliceRecorder.PoseKind.None, Affine2D.Identity, 0f, 0f, Affine2D.Identity, 0f, 0f);
                sl.SetMarker(slot, in clip, sflags, default, default);
                sl.SetSticky(slot, stickyCut, in world);
                if (stickyCut) sl.UnbakeClip(nodeIdx);
                var saved = SaveSlice(ref stats);
                stats.SelfNode = node;
                stats.SelfSlot = slot;
                stats.SelfHasLocal = posed;
                stats.SelfLocal = Affine2D.Identity;
                stats.SelfOmitLayer = false;
                stats.SelfPose = posed ? SliceRecorder.PoseKind.Posed : SliceRecorder.PoseKind.None;
                stats.SelfAcrylic = false;
                stats.SelfStickyClip = stickyCut;
                EnterSlice(ref stats, sl, slot, 0f, 0f, carryAdditive: stickyCut);
                SpanRecordResult res;
                try
                {
                    res = WalkCore(scene, sl.Arena(slot), images, node, parentWorld, parentOpacity, depth, posed ? RectF.Infinite : clip, in focus, in textEdit,
                        scrollThumb, scrollTrack, parentScaleX, parentScaleY, parentInMotion, parentScrollInMotion, inherited, skipRoots,
                        spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
                }
                finally
                {
                    stats.SelfNode = NodeHandle.Null;
                    RestoreSlice(ref stats, in saved);
                }
                stats.NodesVisited--;   // the slice-root walk counted it
                return res;
            }
            stickyCut = false;
        }
        // The node's clip as its own bytes carry it: none when its slice composites it (the pose never reaches a byte),
        // else BAKED — the recorder watches the posed value and re-records this chain when it moves.
        RectF nodeClipRect = selfSticky ? RectF.Infinite : p.ClipRect;
        bool clipBakedChanged = false;
        if (stickyNode && !selfSticky)
        {
            stats.Slices!.BakeClip(node, in p.ClipRect);
            clipBakedChanged = stats.Slices.ClipChangedThisPass(nodeIdx);
        }

        // The acrylic LAYER is recorded only where its frost exists: in an acrylic slice's own walk (its Backdrop item
        // composites beneath the slice), or in a standalone stream a backend composites at push time. An acrylic that
        // could not be cut in a composite arena (acrylic cap, a node reached twice, walk headroom, inside an inline group
        // layer, a top band) records NO layer and keeps its opaque FallbackColor plate — WinUI's no-backdrop answer —
        // instead of an inline layer whose tile replay would erase the plate rect over nothing (a transparent hole).
        bool acrylicLayer = isAcrylic && (acrylicCut || !stats.CompositeArenas);
        if (isAcrylic && !acrylicLayer) stats.Slices?.NoteAcrylicFallback(stats.CurSlot);

        ulong spanInputSig = 0;
        int spanByteStart = 0, spanSortStart = 0, spanCommandStart = 0;
        DrawListOpcodeStats spanOpcodeStart = default;
        // Spatial reuse-scoping: this node is on the ancestor chain of a special-cased visual (popup/overlay/orphan/fly),
        // so its stored bytes could be stale. Deny reuse AND skip the store (the not-store-while-blocked safety property) —
        // its children that are NOT on a blocked chain still reuse/store normally, so only the chain re-records.
        bool blocked = spans is not null && spans.IsBlocked((int)node.Raw.Index, spanFrame);
        SpanReloc parentReloc = stats.Reloc;
        bool spanTracking = spans is not null && spanStoreEnabled && !blocked;
        if (spans is not null)
        {
            byte recordDirtyBits = scene.RecordDirtyBits(node);
            byte descendantDirtyBits = scene.RecordDirtyDescendantBits(node);
            // ALWAYS ON, in every configuration — cheap int increments against locals already computed above.
            if (blocked) stats.SpanMissScopedBlocked++;
            else
            {
                if (spanReuseDisabled) stats.SpanMissGlobalDisabled++;
                if (recordDirtyBits != 0) stats.SpanMissExactDirty++;
            }

            // Off-screen clean-subtree cull — valid under any spanReuseDisabled reason, but NOT while blocked (StoreCulled
            // would poison a chain node's future frame with a span that omits the special), and never for a slice root's
            // own walk (the slice is registered — kept or walked — whole).
            // GUARD: the prior subtree bounds are trusted only when the node's CURRENT device box is ALSO out of view —
            // a reveal/reflow animation re-lays a subtree without record-dirty bits, so the stored bounds go stale.
            if (EnableSubtreeCull && spanStoreEnabled && !blocked && !isSliceSelf
                && recordDirtyBits == 0 && descendantDirtyBits == 0
                && !visualBounds.Overlaps(clip)
                && spans.TryGetSubtree((int)node.Raw.Index, node.Raw.Gen, spanFrame, out var priorWorldC, out var priorSubtreeC)
                && TryTranslationDelta(in priorWorldC, in world, out float cdx, out float cdy))
            {
                RectF curSubtree = TranslateBounds(priorSubtreeC, cdx, cdy);
                if (!curSubtree.Overlaps(clip))
                {
                    // Moved (by layout) out of view: the band it presented at is vacated.
                    if ((cdx != 0f || cdy != 0f) && !stats.GeomCovered)
                        EmitGeometryDamage(scene, node, spans, in curSubtree, ref stats, withCurrent: false);
                    spans.StoreCulled((int)node.Raw.Index, node.Raw.Gen, spanFrame, world, curSubtree, stats.CurSlot);
                    stats.NodesCulled++;
                    var culled = new SpanRecordResult();
                    culled.Include(curSubtree);
                    return culled;
                }
            }

            spanInputSig = ComputeSpanInputSig(scene, node, flags, depth, in clip, in world, opacity,
                parentScaleX, parentScaleY, childScaleX, childScaleY, pw, ph,
                inMotion,
                inherited, in focus, in textEdit, scrollThumb, scrollTrack, clipComposite: selfSticky);
            // Inside an inline group layer the subtree records with no slice cut (its markers would be skipped): a span
            // recorded outside one (which may carry markers) is not the same bytes.
            if (stats.InlineLayerDepth > 0) spanInputSig = (spanInputSig ^ 0x1A7E_1A7EUL) * 1099511628211UL;
            // Inside an additive bracket a nested additive node or sprite field emits no SetBlend of its own: a span recorded
            // outside one carries its own pair, so it is not the same bytes either.
            if (stats.AdditiveDepth > 0) spanInputSig = (spanInputSig ^ 0xADD0_ADD0UL) * 1099511628211UL;
            if (isSliceSelf)
            {
                // KEEP the slice whole: its root is clean and its span is the whole CURRENT arena ⇒ not one byte of it can
                // differ (every descendant is clean too — record-dirty is an aggregate), so the arena stays untouched and
                // its child slices are re-registered as they are. Zero bytes. Else swap the arena and walk.
                var sl = stats.Slices!;
                if (!spanReuseDisabled && !blocked && recordDirtyBits == 0 && stats.KeepDenySlot != selfSlot
                    && spans.TryGet((int)node.Raw.Index, node.Raw.Gen, sl.CurGen(selfSlot), spanInputSig, out var kept)
                    && kept.ByteStart == 0 && kept.ByteLength == dl.BytePosition)
                {
                    sl.Keep(selfSlot);
                    spans.Store((int)node.Raw.Index, node.Raw.Gen, spanFrame, spanInputSig, in kept, sl.CurGen(selfSlot), selfSlot);
                    stats.SpansReused++;
                    var keptResult = new SpanRecordResult();
                    keptResult.Include(kept.SubtreeBounds);
                    return keptResult;
                }
                // Why the keep failed — read-only probes replaying the test above, for the walk ledger (never a decision).
                byte missClass = spans.ClassifyMiss((int)node.Raw.Index, node.Raw.Gen, sl.CurGen(selfSlot), spanInputSig);
                WalkWhy why = WalkClassifier.Classify(true, spanReuseDisabled, (uint)stats.SpanReuseDisabledReasons, blocked,
                    recordDirtyBits, stats.KeepDenySlot == selfSlot, selfSlot == SliceRecorder.RootSlot, missClass,
                    wholeArena: missClass != 0, out uint whyDetail);
                sl.BeginWalk(selfSlot, why, whyDetail);
                stats.CurGen = sl.CurGen(selfSlot);
                stats.PriorGen = sl.PriorGen(selfSlot);
            }
            // Span reuse copies a prior pass's recorded subtree byte-for-byte out of THIS arena's prior buffer. Exact copy
            // needs a fully clean subtree (`recordDirtyBits` is the AGGREGATE self | descendants) and a matching input
            // signature (world, clip, opacity, inherited state, viewport chrome…). A span carrying child slice markers is
            // copied only when every one of those slices can be kept (they are re-registered in stream order).
            else if (!spanReuseDisabled && !blocked && recordDirtyBits == 0
                && spans.TryGet((int)node.Raw.Index, node.Raw.Gen, stats.PriorGen, stats.CurSlot, in parentReloc, spanInputSig, out var span)
                && span.ClipComplete
                && IsClipComplete(span.SubtreeBounds, in clip)
                && dl.CanCopyPriorSpan(span.ByteStart, span.ByteLength, span.SortStart, span.SortCount)
                && (span.OpcodeStats.CompositeSlice == 0 || stats.Slices is null
                    || stats.Slices.CanKeepMarkersInPrior(dl.PriorBytes(span.ByteStart, span.ByteLength))))
            {
                int copiedByteStart = dl.BytePosition;
                int copiedSortStart = dl.SortPosition;
                var copiedStats = span.OpcodeStats;
                dl.CopySpanFromPrior(span.ByteStart, span.ByteLength, span.SortStart, span.SortCount,
                    span.CommandCount, in copiedStats);
                if (copiedStats.CompositeSlice > 0 && stats.Slices is not null)
                    stats.Slices.KeepMarkersIn(dl, copiedByteStart, span.ByteLength, stats.CurSlot);
                if (stats.Slicing && stats.SliceDepth >= 0 && stats.SliceDepth < SliceRecorder.SpanIndexDepth)
                    stats.Slices!.CopyIndexFromPrior(stats.CurSlot, span.ByteStart, span.ByteLength, span.SortStart,
                        copiedByteStart, copiedSortStart, stats.SliceDepth);
                // …and the baked poses inside it: their nodes are not walked, but their bytes still need watching
                if (stats.Slicing) stats.Slices!.CopyBakedFromPrior(stats.CurSlot, span.ByteStart, span.ByteLength, copiedByteStart);
                var currentSpan = span with { ByteStart = copiedByteStart, SortStart = copiedSortStart, World = world };
                spans.Store((int)node.Raw.Index, node.Raw.Gen, spanFrame, spanInputSig, in currentSpan, stats.CurGen, stats.CurSlot);
                stats.SpansReused++;
                stats.SpanBytesCopied += span.ByteLength;
                var copiedResult = new SpanRecordResult();
                copiedResult.Include(span.SubtreeBounds);
                return copiedResult;
            }

            // Successful reuse has already returned. This classifies the fallthrough with read-only table probes — always
            // on: the decision flow above remains the sole behavior authority, this only re-asks the SAME lookup to
            // attribute WHY, and only for a span that already missed reuse.
            if (!spanReuseDisabled && !blocked && recordDirtyBits == 0 && !isSliceSelf)
            {
                if (!spans.TryGet((int)node.Raw.Index, node.Raw.Gen, stats.PriorGen, stats.CurSlot, in parentReloc, spanInputSig, out var exactMiss))
                    stats.SpanMissExactKey++;
                else if (!exactMiss.ClipComplete || !IsClipComplete(exactMiss.SubtreeBounds, in clip))
                    stats.SpanMissExactClip++;
                else if (!dl.CanCopyPriorSpan(exactMiss.ByteStart, exactMiss.ByteLength, exactMiss.SortStart, exactMiss.SortCount))
                    stats.SpanMissExactCapacity++;
            }

            // This node re-records: its children copy out of the bytes it held, wherever an earlier exact copy put them.
            stats.Reloc = spans.Relocation((int)node.Raw.Index, node.Raw.Gen, stats.PriorGen, stats.CurSlot, in parentReloc);

            if (spanTracking)
            {
                spanByteStart = dl.BytePosition;
                spanSortStart = dl.SortPosition;
                spanCommandStart = dl.CommandCount;
                spanOpcodeStart = dl.OpcodeStats;
            }
        }
        else if (isSliceSelf)
        {
            var sl = stats.Slices!;
            sl.BeginWalk(selfSlot, WalkWhy.SpansOff);
            stats.CurGen = sl.CurGen(selfSlot);
            stats.PriorGen = sl.PriorGen(selfSlot);
        }
        // The additive bracket a carrying cut re-opens (EnterSlice): inside the root's span, so a kept arena holds both ends
        // (the carried depth is already in the span signature).
        if (selfAdditive) dl.SetBlend(PaintBlend.Additive);

        // -- Geometry damage decision (retained tiles) --
        // A node that re-records WITHOUT a paint change of its own (clean itself, or only layout / structure dirty) damages
        // exactly what moved: when its placement (world) or its own visual box differs from where it last presented, its
        // subtree's old + new; else nothing - its children decide for themselves. A re-window that re-appends rows at the
        // positions they held (and grows the content's realized range) therefore damages only the rows that entered.
        // Content-dirty / transform-moved nodes keep damaging their whole subtree below; either way the descendants'
        // own geometry changes are covered.
        bool prevGeomCovered = stats.GeomCovered;
        bool geomDamage = false;
        {
            byte selfBits = scene.RecordDirtySelfBits(node);
            bool subtreeDamaged = (selfBits & SceneStore.RecordDirtyContent) != 0
                                  || ((flags & NodeFlags.TransformDirty) != 0 && !selfHasLocal);
            if (subtreeDamaged) stats.GeomCovered = true;
            else if (!stats.GeomCovered && spans is not null && !stats.Repaint.IsFull
                     && spans.TryGetPriorGeometry((int)node.Raw.Index, node.Raw.Gen, out Affine2D gw, out RectF gself, out _, out int gslot)
                     && (gslot != stats.CurSlot || !SameAffine(in gw, in world) || !SameRect(in gself, in visualBounds)))
            {
                geomDamage = true;
                stats.GeomCovered = true;
            }
        }

        // ── the group pushes (decided above). A slice-root walk whose group rides its marker (omitLayer) records only
        // the group's CONTENT: at full alpha, and a self-blur's content under its source clip — exactly what the group
        // op would have wrapped. The edge fade / self-blur / opacity group reset the cumulative opacity to 1 for
        // everything the node emits (self, children, border, focus ring, scrollbar — all inside the group). ──
        int opacityPushByteOffset = -1;   // set at the opacity group's push; consumed by the extent back-patch at PopLayer
        int opacityPushLayerCount = 0;    // PushLayer + slice-marker count at that emit — a nonzero delta at Pop = nested layers
        if (!omitLayer) CountGroups(ref stats, isEdgeFade, isBlurCandidate, isBlurGroup);   // (a cut counted them at its marker)
        RectF recordClip = clip;
        bool pushedBlurSourceClip = false;
        // A scroller's CHROME (edge-cue chevrons, the overlay scrollbar's rail, arrows and thumb) sits OVER its edge feather,
        // never under it: the feather dissolves the content, and a rail whose ends (and arrows) faded to nothing at exactly
        // the edges it serves would be wrong. (Under a partial opacity the chrome stays inside the group, as a whole-node
        // opacity demands.) SliceRecorder mirrors this for a sliced fade: its Chrome/Thumb slices trail its content and are
        // placed outside its feather.
        bool chromeOverFade = isEdgeFade && opacity >= 0.999f;
        if (isEdgeFade)
        {
            if (!omitLayer) { dl.PushLayerCmdRaw(in groupLayer, key); stats.InlineLayerDepth++; }
            opacity = 1f;
        }
        else if (isBlurGroup)
        {
            // The viewport clip applies to the FINAL composite, not to the crisp pixels feeding the Gaussian. Record and
            // rasterize the exact contributing strip inside the layer; this explicit nested clip also makes the D3D target
            // scissor widen before the first subtree draw, then restores the composite clip before PopLayer.
            recordClip = groupSourceClip;
            if (!omitLayer)
            {
                dl.PushLayerCmdRaw(in groupLayer, key);
                dl.PushClip(recordClip, key);
                pushedBlurSourceClip = true;
                stats.InlineLayerDepth++;
            }
            opacity = 1f;
        }
        else if (isOpacityGroup)
        {
            if (!omitLayer)
            {
                // Remember the PushLayerCmd byte offset: at PopLayer we back-patch this group's ACCUMULATED subtree draw
                // extent into it, so the backend leases + clears + composites only those pixels instead of the whole
                // canvas. A nested layer (or a child slice, whose group rides its marker) inside the group is the one
                // thing that READS the group RT outside its own drawn box, so any leaves the extent unpatched.
                opacityPushByteOffset = dl.BytePosition;
                dl.PushLayerCmdRaw(in groupLayer, key);
                opacityPushLayerCount = dl.OpcodeStats.PushLayer + dl.OpcodeStats.CompositeSlice;
                stats.InlineLayerDepth++;
            }
            opacity = 1f;
        }

        bool overlapsRecordClip = deviceBounds.Overlaps(recordClip);

        // ── shadow: drawn beneath the fill, BEFORE this node pushes its OWN clip — otherwise a ClipToBounds node (a flyout
        //    surface, a dialog) would clip its own soft-shadow halo away (the halo extends outside the node bounds). It is
        //    still bounded by the PARENT clip via the deviceBounds.Overlaps(clip) gate. It is ALSO gated on the cumulative
        //    opacity (see the rationale above the edge-fade/blur/opacity-group gates): a shadow at alpha ≈ 0 is a
        //    zero-output quad. Span-reuse-safe — `opacity` is already an input to ComputeSpanInputSig, so crossing the
        //    threshold invalidates the span and re-records. ──
        if (maybeSparsePaint && overlapsRecordClip && opacity > 0.001f && scene.TryGetShadow(node, out var sh) && !sh.IsNone)
        {
            dl.Shadow(local, p.Corners, sh.Color, sh.OffsetX, sh.OffsetY, sh.Blur, sh.Spread, world, opacity, key);
            // The halo paints OUTSIDE the node box — union its extent into the subtree bounds so anything that reads
            // those bounds as "every pixel this subtree drew" (the off-screen span cull, clip-completeness, the opacity
            // group's extent patch below) stays a SUPERSET. Same box the shadow quad spans: the node rect displaced by
            // the offset and grown by spread + the 3σ gaussian tail (ShadowPipeline's VSMain, sigma = max(blur/2, ½)).
            float shHalo = sh.Spread + 3f * MathF.Max(sh.Blur * 0.5f, 0.5f);
            result.Include(world.TransformBounds(new RectF(
                local.X + sh.OffsetX - shHalo, local.Y + sh.OffsetY - shHalo,
                local.W + 2f * shHalo, local.H + 2f * shHalo)));
        }

        // Circular-arc stroke (ProgressRing): a trimmed, round-capped ring drawn as its own SDF primitive. The ring node
        // carries no fill (the arc IS the visual), so its order vs the fill block below doesn't matter for its own node.
        // The arc honors the StrokeTrim paint channels (AnimChannel.StrokeTrimStart/End) as a fraction of its sweep, so the
        // indeterminate ring can "breathe" (animate its arc length) — not just rotate.
        if (maybeSparsePaint && overlapsRecordClip && scene.TryGetArc(node, out var arcS) && !arcS.IsNone)
        {
            float trimS = float.IsNaN(p.StrokeTrimStart) ? 0f : Math.Clamp(p.StrokeTrimStart, 0f, 1f);
            float trimE = float.IsNaN(p.StrokeTrimEnd) ? 1f : Math.Clamp(p.StrokeTrimEnd, 0f, 1f);
            float aStart = arcS.StartDeg + trimS * arcS.SweepDeg;
            float aSweep = (trimE - trimS) * arcS.SweepDeg;
            if (aSweep > 0.01f)
                dl.Arc(local, arcS.Color, arcS.Thickness, aStart, aSweep, arcS.RoundCaps, world, opacity, key);
        }

        // A clipping node (scroll viewport / virtual list) intersects the active clip and pushes the scissor. An authored
        // clip-rect (AnimChannel.ClipL/T/R/B, node-local) composes with ClipsToBounds into a single combined scissor.
        // A leading .Collapse's cut (NodePaint.CollapseCut) is a LINE at the presented bottom edge, open above, left and
        // right: the children are cut below it and nowhere else, so a child drawing above the node's top (a
        // .StretchFromTop() photo filling a top overscroll) keeps those pixels. It cuts at the EXACT presented edge
        // (deviceBounds IS the presented rect), never at the rect's float-rounded sentinel bottom; a ClipsToBounds node
        // already clips there through its box, so it keeps exactly its box clip (cutOnly = false).
        bool pushedClip = false;
        RectF childClip = recordClip;
        bool wantClip = (flags & NodeFlags.ClipsToBounds) != 0 || !nodeClipRect.IsInfinite;
        bool collapseCut = NodePaint.IsCollapseCut(in nodeClipRect);
        bool cutOnly = collapseCut && (flags & NodeFlags.ClipsToBounds) == 0;
        if ((flags & NodeFlags.ClipsToBounds) != 0)
            childClip = childClip.Intersect(deviceBounds);
        if (cutOnly)
        {
            float cutBottom = deviceBounds.Bottom;
            if (cutBottom < childClip.Bottom)
                childClip = cutBottom > childClip.Y ? new RectF(childClip.X, childClip.Y, childClip.W, cutBottom - childClip.Y) : default;
        }
        else if (!collapseCut && !nodeClipRect.IsInfinite)
            childClip = childClip.Intersect(world.TransformBounds(nodeClipRect));
        bool childClipEmpty = wantClip && childClip.IsEmpty;

        // Tier-3 (stencil) path clip (gpu-renderer.md §6): a ClipPath node clips its whole subtree to an arbitrary
        // silhouette instead of the rectangle. Emitted BEFORE the tier-2/tier-1 emission below and INSTEAD of it — the
        // push's DeviceRect IS the scope's scissor, so the two never both fire for one node. A realize FAILURE falls
        // through to the plain scissor push (a conservative rectangle): a clip is never silently dropped.
        // Steady state is zero-alloc: TryGetClipPath is a slab read and TryRealizeFill is a cache hit (the FillPath
        // lane's contract, same key). SetClipPath -> MarkRecordDirty invalidates spans exactly like SetPath does, so
        // ComputeSpanInputSig needs no new input.
        bool pushedStencil = false;
        PathRef stencilClipRef = default;      // valid iff pushedStencil
        Affine2D stencilClipWorld = default;
        if (!childClipEmpty && (flags & (NodeFlags.ClipsToBounds | NodeFlags.SparsePaint))
                               == (NodeFlags.ClipsToBounds | NodeFlags.SparsePaint)
            && scene.TryGetClipPath(node, out var cps) && !cps.IsNone)
        {
            var cg = cps.Geometry!;
            Affine2D clipWorld = world;
            if (cps.ViewBoxW > 0f && cps.ViewBoxH > 0f && pw > 0f && ph > 0f)
            {
                float cfit = MathF.Min(pw / cps.ViewBoxW, ph / cps.ViewBoxH);
                clipWorld = world.Multiply(Affine2D.Scale(cfit, cfit));
            }
            float cScaleQ = MathF.Abs(clipWorld.M11 != 0f ? clipWorld.M11 : 1f)
                            * (scene.DeviceScale > 0f ? scene.DeviceScale : 1f);
            if (PathRealizationCache.Shared.TryRealizeFill(cg, cps.Rule, cScaleQ, out var cfr) && cfr.VtxCount > 0)
            {
                childClip = childClip.Intersect(clipWorld.TransformBounds(cfr.Bounds));
                childClipEmpty = childClip.IsEmpty;
                if (!childClipEmpty)
                {
                    dl.PushStencilClip(childClip, cfr, (byte)cps.Rule, clipWorld, key);
                    pushedClip = true; pushedStencil = true;
                    stencilClipRef = cfr; stencilClipWorld = clipWorld;
                    // A child slice's marker carries only rectangular / rounded clips to the composite: cut inside the
                    // scope it would composite as the silhouette's AABB. Nothing below is cut (see InlineLayerDepth).
                    stats.InlineLayerDepth++;
                }
            }
        }
        if (wantClip && !childClipEmpty && !pushedStencil)
        {
            // Tier-2 rounded clip (E9): a clipping node WITH rounded corners (an Expander/CommandBarFlyout surface
            // running an AnimChannel.ClipL/T/R/B reveal, or a plain rounded ClipsToBounds) clips RoundRect-pipeline
            // primitives to its rounded-box SDF as well as the scissor. The rounded box is the node's own device box;
            // the (possibly animated) clip-rect keeps intersecting RECTANGULARLY into the scissor — so a reveal sweeps
            // a straight edge while the surface's own corners stay round, exactly the WinUI composition-clip look.
            // Uniform radius (TopLeft, the pipeline-wide convention — the RoundRect/acrylic shaders read one radius);
            // scaled into device units by the axis-aligned world scale. Honest scope is documented on ClipCmd:
            // glyphs/images/gradients/arcs/polylines still clip by scissor only. A bare collapse cut (cutOnly) is a
            // straight line, not a box: it never rounds (the node-box SDF would cut a stretch above the top again).
            float clipRadius = p.Corners.TopLeft;
            if (clipRadius > 0f && !cutOnly)
            {
                float rDev = clipRadius * MathF.Abs(world.M11 != 0f ? world.M11 : 1f);
                // The rounded-clip SDF carries ONE radius, but a side whose corners are BOTH square must clip STRAIGHT —
                // the shell content card (rounded top, square bottom) was getting its CONTENT rounded away at the
                // bottom too. Extend the SDF box past the scissor on fully-square sides so that side's rounding falls
                // outside the visible clip; the rectangular scissor still bounds the true edge.
                RectF sdfBox = deviceBounds;
                var c4 = p.Corners;
                if (c4.BottomLeft <= 0f && c4.BottomRight <= 0f) sdfBox = new RectF(sdfBox.X, sdfBox.Y, sdfBox.W, sdfBox.H + rDev);
                if (c4.TopLeft <= 0f && c4.TopRight <= 0f) sdfBox = new RectF(sdfBox.X, sdfBox.Y - rDev, sdfBox.W, sdfBox.H + rDev);
                if (c4.TopRight <= 0f && c4.BottomRight <= 0f) sdfBox = new RectF(sdfBox.X, sdfBox.Y, sdfBox.W + rDev, sdfBox.H);
                if (c4.TopLeft <= 0f && c4.BottomLeft <= 0f) sdfBox = new RectF(sdfBox.X - rDev, sdfBox.Y, sdfBox.W + rDev, sdfBox.H);
                dl.PushClipRounded(childClip, sdfBox, rDev, key);
                pushedClip = true;
            }
            // A plain scissor equal to the clip ALREADY in effect is a no-op push — very common: a ClipsToBounds container
            // whose device bounds already contain the incoming clip (the shell's nested page/content-host panels). Skip the
            // PushClip+PopClip pair + its two sort entries: the active scissor is unchanged, so children clip identically.
            // Cuts command-stream bloat and scissor churn on every such container, every frame, incl. every reused span.
            else if (childClip != recordClip)
            {
                dl.PushClip(childClip, key);
                pushedClip = true;
            }
        }

        // ── acrylic: snapshot + blur the backdrop drawn so far, composite the frosted surface, then content draws on top ──
        // (isAcrylic / ac were resolved with the group decisions above.)
        if (acrylicLayer)
        {
            dl.PushLayer(deviceBounds, p.Corners, ac.Tint, ac.Fallback, ac.TintOpacity, ac.BlurSigma, ac.NoiseOpacity, ac.LuminosityOpacity, key,
                Math.Clamp(ac.FeatherTop, 0f, 1f), opacity);
        }

        // Cull this node's OWN draw if it falls entirely outside the active clip (offscreen virtualized/overscan rows).
        bool hasOwnVisual = p.VisualKind != VisualKind.None;
        bool ownVisible = overlapsRecordClip;
        if (hasOwnVisual)
        {
            if (ownVisible) stats.DrawnNodeCount++;
            else stats.CulledNodeCount++;
        }

        bool pendingSolidBorder = false;
        bool pendingGradientBorder = false;
        ColorF pendingBorder = default;
        GradientSpec pendingBorderBrush = default;
        GradientSpec pendingHoverBorderBrush = default;
        GradientSpec pendingPressedBorderBrush = default;
        bool pendingHasHoverBorderBrush = false;
        bool pendingHasPressedBorderBrush = false;
        float pendingBorderHoverT = 0f, pendingBorderPressT = 0f;

        bool drawSelf = hasOwnVisual && ownVisible;
        // Occlusion cull (always on): drop this node's own fill when a later opaque square child fully covers it. Only when
        // the node has NO border — the SDF border ring straddles the edge (extends ~stroke/2 OUTSIDE deviceBounds), which a
        // child that merely contains deviceBounds wouldn't cover, so a bordered node keeps drawing to be safe. Never inside
        // an additive bracket (this node's own Blend or an enclosing one in this arena): its children ADD onto its fill.
        // Never under a partial NON-group opacity (its own, e.g. an Enter fade, or inherited): the child then draws at that
        // same alpha, so it no longer overwrites the fill beneath it (a group has already reset `opacity` to 1 here).
        if (drawSelf && opacity >= 0.999f && p.BorderWidth <= 0f && p.ValidationBorder.A <= 0f && stats.AdditiveDepth == 0
            && IsOccludedByOpaqueChild(scene, node, in world, p.ChildShiftX, p.ChildShiftY, in deviceBounds, in recordClip, inMotion,
                stats.Slicing ? stats.Slices : null))
            drawSelf = false;
        // E3 drag-ghost BACKPLATE (DragVisualStyle.Backplate, published at promotion as SceneStore.DragGhostBackplate):
        // an opaque plate under the WHOLE lifted subtree, drawn INSIDE the ghost's opacity group (DragController sets
        // NodePaint.OpacityGroup on the lift) and before the node's own fill — so a transparent list row stops letting
        // the content beneath the ghost read through its text. Corner radii come from the ghost node itself (square when
        // it has none). One handle compare per node per frame, and DragGhost is Null except during a lifted drag.
        if (node == scene.DragGhost && scene.DragGhostBackplate is { } dragBackplate && overlapsRecordClip)
            dl.FillRoundRect(local, p.Corners, dragBackplate, world, opacity, key);

        GradientSpec nodeGradient = default;
        bool hasNodeGradient = maybeSparsePaint && scene.TryGetGradient(node, out nodeGradient) && nodeGradient.Stops is { Length: > 0 };
        if (drawSelf)
        switch (p.VisualKind)
        {
            case VisualKind.Box when p.Fill.A > 0f || p.HoverFill.A > 0f || p.PressedFill.A > 0f || p.BorderWidth > 0f
                                     || p.ValidationBorder.A > 0f
                                     || hasNodeGradient
                                     // A fill fading TO transparent still shows LerpLinear(FillFrom, Fill, T): draw it
                                     // until the BrushTransition lands (the reconciler holds VisualKind.Box for it).
                                     || (maybeSparsePaint && FillFadeVisible(scene, node)):
            {
                ResolveSurface(scene, node, flags, in p, in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT, out ColorF fill, out ColorF border);
                // A frosted surface's authored fill IS its acrylic FallbackColor: the kit paints it (FlyoutSurface on
                // both shapes it builds, ScrollBar's track, every Acrylic+Fallback pairing) so the surface stays solid
                // wherever the acrylic LAYER cannot run. Where the layer DID run, that fill is not redundant, it is
                // DESTRUCTIVE. An Acrylic PushLayer composites at PUSH time — blurred backdrop, SourceOver the same
                // opaque Fallback, luminosity blend, tint, noise — and the node's own draw + its subtree land ON TOP of
                // the result. Re-filling the node with that opaque Fallback therefore paints a flat slab straight over
                // the frost that was just produced. Every popup presenter did exactly this (menus, ComboBox dropdowns,
                // AutoSuggestBox suggestions, FlyoutPresenter all route through FlyoutSurface), which is why they read
                // as a flat #2C2C2C panel over any page, however saturated — the acrylic ran, and was then covered up.
                // Only an EXACT match is dropped: a hover wash, a tinted card, any fill that is not literally the
                // layer's own base still paints. Where the layer does NOT run the spec is absent (Materials policy off,
                // a video hole, an OS-backed popup — all clear it) or the backend supplies the Fallback itself
                // (SubmitStreaming's PushLayer, the non-primary-swapchain route), so the solid plate survives.
                // FeatherTop is the one exception: a feathered layer deliberately fades the frost in from the top, so
                // the band above it is MEANT to show what is behind the surface — the plate fill there is content, not
                // an occluder, and it stays.
                // Only where the layer was actually RECORDED (acrylicLayer: an acrylic slice's own walk, whose Backdrop
                // item frosts the plate, or a standalone stream): an acrylic that could not be cut keeps this fill — it
                // is the whole surface there.
                if (acrylicLayer && ac.FeatherTop <= 0f && fill.Equals(ac.Fallback)) fill = ColorF.Transparent;
                bool hasGradFill = hasNodeGradient;
                GradientSpec g = nodeGradient;
                GradientSpec bb = default;
                bool hasGradBorder = p.BorderWidth > 0f && maybeSparsePaint && scene.TryGetBorderBrush(node, out bb) && bb.Stops is { Length: > 0 };

                // Stateful gradient variants (P4b): resolve once; the eased progress feeds the per-stop blend in Emit*.
                GradientSpec hg = default, pg = default, hbb = default, pbb = default;
                bool hasHG = hasGradFill && scene.TryGetHoverGradient(node, out hg) && hg.Stops is { Length: > 0 };
                bool hasPG = hasGradFill && scene.TryGetPressedGradient(node, out pg) && pg.Stops is { Length: > 0 };
                bool hasHBB = hasGradBorder && scene.TryGetHoverBorderBrush(node, out hbb) && hbb.Stops is { Length: > 0 };
                bool hasPBB = hasGradBorder && scene.TryGetPressedBorderBrush(node, out pbb) && pbb.Stops is { Length: > 0 };
                float gHoverT = 0f, gPressT = 0f;
                if (hasHG || hasPG || hasHBB || hasPBB)
                    TryResolveInteractionProgress(in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT, out gHoverT, out gPressT);

                // ── fill ── the interior is always filled at its FULL geometry; the border is a hollow SDF ring drawn
                // ON TOP of the fill edge (below). We must NOT fill the whole box with the border colour and overlay an
                // inset interior (the old "donut"): with a translucent interior (e.g. the unchecked CheckBox/RadioButton
                // fill ≈ black@10%) the opaque ring shows straight through → a solid grey chip. A hollow ring composites
                // correctly over ANY fill opacity, exactly like the gradient-border path always has.
                if (hasGradFill)
                {
                    bool hasRadialCenter = scene.TryGetRadialGradientCenter(node, out Point2 radialCenter);
                    GradientSpec gto = default;
                    float gMix = scene.TryGetGradientMix(node, out float mixValue) ? mixValue : 0f;
                    bool hasTo = gMix > 0f && scene.TryGetGradientTo(node, out gto) && gto.Stops is { Length: > 0 };
                    EmitGradient(dl, local, p.Corners, in g, in hg, hasHG, in pg, hasPG, gHoverT, gPressT,
                        hasRadialCenter, radialCenter, world, opacity, key, in gto, hasTo ? gMix : 0f);
                }
                else if (fill.A > 0f)
                    dl.FillRoundRect(local, p.Corners, fill, world, opacity, key);

                // ── border ring (SDF band, drawn over the fill edge — inside the bounds, WinUI-style) ── ONE hollow ring
                // for every border, solid or gradient; the SDF stroke never paints the interior.
                if (p.ValidationBorder.A > 0f && p.BorderWidth > 0f)
                {
                    // form-validation.md: a validation error forces a SOLID error-colored ring, overriding any resting
                    // gradient (TextBox/ComboBox use the gradient elevation border) or solid border. `border` already
                    // holds the resolved error color (ResolveSurface).
                    pendingSolidBorder = true;
                    pendingBorder = border;
                }
                else if (hasGradBorder)
                {
                    pendingGradientBorder = true;
                    pendingBorderBrush = bb;
                    pendingHoverBorderBrush = hbb;
                    pendingPressedBorderBrush = pbb;
                    pendingHasHoverBorderBrush = hasHBB;
                    pendingHasPressedBorderBrush = hasPBB;
                    pendingBorderHoverT = gHoverT;
                    pendingBorderPressT = gPressT;
                }
                else if (p.BorderWidth > 0f && border.A > 0f)
                {
                    pendingSolidBorder = true;
                    pendingBorder = border;
                }
                break;
            }
            case VisualKind.TabShape:
            {
                ResolveSurface(scene, node, flags, in p, in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT, out ColorF fill, out _);
                if (fill.A > 0f)
                    dl.TabShape(local, p.Corners.TopLeft, p.TabFlareRadius, fill, world, opacity, key);
                break;
            }
            case VisualKind.Video:
            {
                // The video hole punch — this node ERASES instead of painting, so there is no fill to resolve. The
                // PAINTER-ORDER contract does the compositing work: everything recorded BEFORE the hole (the page,
                // the shell, the stage's letterbox-colored root) is what gets erased, and everything recorded AFTER
                // it (letterbox bars, transport chrome, overlays) paints back over the video — so the emitter must
                // place the hole as the FIRST child of the video stage, never last. Erase strength is a constant 1
                // (the poster↔hole swap is discrete; a graded crossfade would grade the POSTER, not this value).
                // Limitation (gpu-renderer.md §7.3): inside a PushLayer the erase hits the offscreen layer RT rather
                // than the back buffer, so the hole vanishes under acrylic and during enter/exit fades.
                // ImageId carries the video registry SurfaceId (the IconLayer PathId pun — Columns.cs).
                dl.DrawVideo(local, p.Corners, p.ImageId, 1f, world, opacity, key);
                break;
            }
            case VisualKind.Text:
            {
                TextStyle style = scene.RecordingTextStyle(node);
                ColorF textColor = ResolveTextColor(scene, node, flags, in p, in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT);
                // Auto-fit: the measure pass may have shrunk the font (TextEl.MinSize) and recorded the chosen size on
                // the cache. Shape at it so the glyphs match the box the layout sized. 0 ⇒ no fit (authored size).
                // P4: the measure cache is a 2-ENTRY RING (Scene/Columns.cs) — resolve the entry whose measure width
                // matches the box this run is being drawn in, falling back to the most recently used entry (which is
                // what the pre-ring single slot's "whichever was written last" read always resolved to).
                TextMeasureEntry mc = scene.ResolveMeasureForWidth(node, b.W);
                float effSize = mc.Valid && mc.FitSize > 0f ? mc.FitSize : style.SizeDip;

                // Text-edit decorations (editor TEXT nodes only — sparse side-table, recorder READS only).
                // WinUI-exact emit order: selection highlight UNDER the glyphs → base glyph run → per-rect clipped
                // glyph re-emit in the on-accent selected-text color → IME clause underline bars → the caret bar.
                TextEditState tes = default;
                bool hasEdit = textEdit.Enabled && scene.TryGetTextEdit(node, out tes);
                ReadOnlySpan<RectF> selRects = hasEdit ? scene.GetTextEditSelectionRects(node) : default;

                // (a) selection highlight: the per-node SelectionHighlightColor override (api-04, WinUI
                // TextBlock.SelectionHighlightColor — TextBlock.cpp:266/330) wins over the host theme brush
                // (TextControlSelectionHighlightColor ≡ the system accent, TextSelectionManager.cpp:52-56).
                ColorF selFill = scene.TryGetSelectionHighlight(node, out var selOverride) ? selOverride : textEdit.SelectionFill;
                if (selFill.A > 0f)
                    for (int i = 0; i < selRects.Length; i++)
                        dl.FillRoundRect(selRects[i], default, selFill, world, opacity, key);

                // (b) the base glyph run. A span run (TextStyle.SpanRunId, rtb-01) rides the SAME op — the renderer
                // overlays the per-range styles from SpanRunTable.Shared and tints per-span colors over textColor.
                int spanRunId = style.SpanRunId;
                // A run is only painted into a box that HAS area. A glyph run does not clip to its own bounds, so a
                // text node layout has sized to 0×0 (a collapsed slot, a zero-width column) would otherwise still draw
                // its glyphs past the empty box. Text has no visible extent without a box: nothing to paint.
                bool paintsText = !p.Text.IsEmpty && b.W > 0f && b.H > 0f;
                // The run's INK: the box grown by what the measured text overflows it (a no-wrap / no-trim run wider than
                // its box, more lines than its height). The overflow side depends on alignment, so it is taken on BOTH
                // sides. Every cull, slice bound, content hash and sub-tile damage reads it; the replay still shapes into
                // the box. Empty when the text fits (the box is the footprint).
                RectF ink = default;
                if (paintsText && mc.Valid)
                {
                    float ow = MathF.Max(0f, mc.Size.Width - local.W), oh = MathF.Max(0f, mc.Size.Height - local.H);
                    if (ow > 0f || oh > 0f)
                    {
                        ink = new RectF(local.X - ow, local.Y - oh, local.W + 2f * ow, local.H + 2f * oh);
                        result.Include(world.TransformBounds(ink));   // the span bounds a clean-subtree cull reads
                    }
                }
                if (paintsText)
                {
                    // No longer counted on motion: with the glyph renderer's sub-pixel phase atlas a moving run is drawn
                    // CRISP at its 1/N device row rather than unsnapped, so there is nothing for the host's settle frame
                    // to re-snap. The counter stays (it is a public record stat) and now reports the truth: zero.
                    if (scene.TryGetGlyphWipe(node, out var wipe))   // glyph wipe (lyrics karaoke): per-glyph color + lift from the split
                        dl.DrawGlyphRunGradient(local, p.Text, style.FontFamily, effSize, style.Weight,
                            (int)style.Wrap, (int)style.Trim, style.MaxLines,
                            style.CharSpacing, style.LineHeight, (int)style.Stacking, (int)style.LineBounds,
                            world, opacity, wipe.Before, wipe.After, wipe.Split, wipe.Softness, wipe.Lift, key, spanRunId, ink: in ink);
                    else
                        dl.DrawGlyphRun(local, textColor, p.Text, style.FontFamily, effSize, style.Weight,
                            (int)style.Wrap, (int)style.Trim, style.MaxLines,
                            style.CharSpacing, style.LineHeight, (int)style.Stacking, (int)style.LineBounds,
                            world, opacity, key, spanRunId, ink: in ink);
                }

                // (b1) span-run decoration bars (per-LINE, per span — the rich-text refinement of (b2) below): the
                // text seam published the laid bar rects on the run at measure (SpanRunRects — link bands are input's;
                // Underline/Strikethrough entries are ready-positioned bars), so record stays 0-touch on the font
                // seam. Bar color = the span's color when set, else the node's resolved foreground — the same
                // same-brush-as-glyphs rule WinUI's TextDecorations follow.
                if (spanRunId != 0 && scene.TryGetSpanDecorations(node, out var spanStyles, out var arts))
                {
                    for (int i = 0; i < arts.Length; i++)
                    {
                        if (arts[i].Kind == SpanStyle.LinkBit) continue;   // hit-test bands, not painted
                        ColorF spanColor = spanStyles[arts[i].Span].Color;
                        dl.FillRoundRect(arts[i].Rect, default, spanColor.A > 0f ? spanColor : textColor, world, opacity, key | 0x8);
                    }
                }

                // (b2) text decorations (TextEl.Underline/Strikethrough → NodePaint.TextDecorations, E9): bars placed
                // by the FACE metrics the measure pass cached on TextMeasureCache (UnderlineY/UnderlineThickness/StrikeY
                // — the DWrite underlinePosition/underlineThickness flipped top-down, TextLayoutEngine.cs:141-143;
                // headless model documented at HeadlessFontSystem.cs:13). The bars span the measured run advance (not
                // the stretched box) and ride the SAME resolved foreground as the glyphs (hover/press ramps +
                // BrushTransition), like WinUI's TextDecorations underline. No new opcode — plain radius-0 fills.
                // Scope (honest): single-line frame — a wrapped multi-line run gets the first line's bar only; per-line
                // decoration belongs to the SpanTextEl/RichTextBlock rich-text pass (Wave 5).
                if (p.TextDecorations != 0 && paintsText)
                {
                    // The measure pass get-or-created this row for every text leaf (mc captured above), so 0-alloc here.
                    float barW = mc.Valid ? MathF.Min(mc.Size.Width, pw) : pw;
                    // Fallbacks mirror the engine's font-metric conventions for backends that report none (GDI):
                    // thickness max(1, size/14) = TextLayoutEngine.cs:142's own fallback; positions = the headless model.
                    float thick = mc.Valid && mc.UnderlineThickness > 0f ? mc.UnderlineThickness : MathF.Max(1f, effSize / 14f);
                    if ((p.TextDecorations & NodePaint.UnderlineBit) != 0 && barW > 0f)
                    {
                        float y = mc.Valid && mc.UnderlineY > 0f ? mc.UnderlineY : effSize * 1.1f + 1f;
                        dl.FillRoundRect(new RectF(0f, y, barW, thick), default, textColor, world, opacity, key | 0x8);
                    }
                    if ((p.TextDecorations & NodePaint.StrikethroughBit) != 0 && barW > 0f)
                    {
                        float y = mc.Valid && mc.StrikeY > 0f ? mc.StrikeY : effSize * 0.8f;
                        dl.FillRoundRect(new RectF(0f, y, barW, thick), default, textColor, world, opacity, key | 0x8);
                    }
                }

                // (c) selected-text recolor: re-emit the SAME run scissored to each selection rect (device-space,
                // intersected with the active clip like every PushClip the recorder emits). Glyph cost ×2 only while
                // a selection exists — exactly WinUI's selected-text recolor, no per-glyph splitting.
                if (!p.Text.IsEmpty && textEdit.SelectedText.A > 0f)
                    for (int i = 0; i < selRects.Length; i++)
                    {
                        RectF selDevice = world.TransformBounds(selRects[i]).Intersect(childClip);
                        if (selDevice.IsEmpty) continue;
                        dl.PushClip(selDevice, key | 0x1);
                        // forceColor: selected glyphs repaint UNIFORMLY in the on-accent color — span colors must not
                        // bleed through the selection (WinUI's selected-text recolor).
                        dl.DrawGlyphRun(local, textEdit.SelectedText, p.Text, style.FontFamily, effSize, style.Weight,
                            (int)style.Wrap, (int)style.Trim, style.MaxLines,
                            style.CharSpacing, style.LineHeight, (int)style.Stacking, (int)style.LineBounds,
                            world, opacity, key | 0x1, spanRunId, forceColor: true);
                        dl.PopClip(key | 0x1);
                    }

                // (d) IME composition clause underlines: thin bars in the text foreground (the control computes
                // thickness/position from the face metrics — the rects are used as given).
                if (hasEdit)
                {
                    ReadOnlySpan<RectF> ulRects = scene.GetTextEditUnderlineRects(node);
                    for (int i = 0; i < ulRects.Length; i++)
                        dl.FillRoundRect(ulRects[i], default, textColor, world, opacity, key | 0x2);
                }

                // (e) the caret: a 1px bar, drawn only while focused AND blink-visible, pixel-snapped in device space
                // (round the device X, push the delta back through the world scale).
                if (hasEdit && textEdit.CaretColor.A > 0f && tes.CaretH > 0f
                    && (tes.Flags & (TextEditState.CaretVisible | TextEditState.Focused))
                       == (TextEditState.CaretVisible | TextEditState.Focused))
                {
                    float sx = world.M11 != 0f ? world.M11 : 1f;
                    float devX = world.Transform(new Point2(tes.CaretX, tes.CaretTop)).X;
                    float caretX = tes.CaretX + (MathF.Round(devX) - devX) / sx;
                    dl.FillRoundRect(new RectF(caretX, tes.CaretTop, 1f, tes.CaretH), default, textEdit.CaretColor, world, opacity, key | 0x4);
                }
                break;
            }
            case VisualKind.Image:
            {
                bool hasEffects = scene.TryGetImageEffects(node, out ImageVisualEffects effects);
                float saturation = hasEffects ? effects.Saturation : 1f;
                int imageId = p.ImageId;
                if (effects.DerivedImageId != 0 && images is not null
                    && images.StateOf(new ImageHandle(effects.DerivedImageId)) == ImageState.Ready)
                    imageId = effects.DerivedImageId;
                var ih = new ImageHandle(imageId);
                bool ready = images is not null && images.StateOf(ih) == ImageState.Ready;
                float fadeStart = float.NaN, fadeDur = 0f;
                int fadeEase = 0;
                if (images is not null && images.FadeParamsOf(ih, out fadeStart, out fadeDur, out fadeEase)) { }

                RectF drawRect = local;
                RectF uv = new RectF(0f, 0f, 1f, 1f);
                if (ready && images is not null)
                {
                    var (srcW, srcH) = images.SizeOf(ih);
                    (drawRect, uv) = ImageContentFit((ImageFit)p.ImageFit, in local, srcW, srcH, p.ImageFocusX, p.ImageFocusY);
                }

                ImageMaskSpec mask = effects.Mask;
                ColorF placeholder = p.Fill;
                // Image swap crossfade (Reconciler hold-last-good commit onto a DIFFERENT picture): the texture that was
                // on screen is drawn first, OPAQUE for the whole window and gone after it (SwapOutgoingEasing), and this
                // node's image fades in over it on the SAME window with a transparent placeholder — a dissolve between
                // two real pictures, never a placeholder frame. Both draws bake the window, so a reused span keeps
                // animating against the replay clock and the outgoing resolves to nothing once the window has passed.
                // A cut (the SAME picture at another decode size) keeps the outgoing under it the same way, with no fade-in:
                // the incoming draws at once wherever its pixels are drawable, and lets the held picture show where not yet.
                if (ready && images is not null && effects.SwapOutgoingId != 0 && effects.SwapMs > 0f
                    && images.StateOf(new ImageHandle(effects.SwapOutgoingId)) == ImageState.Ready)
                {
                    var oh = new ImageHandle(effects.SwapOutgoingId);
                    var (outW, outH) = images.SizeOf(oh);
                    var (outRect, outUv) = ImageContentFit((ImageFit)p.ImageFit, in local, outW, outH, p.ImageFocusX, p.ImageFocusY);
                    dl.DrawImage(outRect, p.Corners, oh.Id, true, default, world, opacity, outUv, effects.SwapStartMs,
                        effects.SwapMs, ImageCache.SwapOutgoingEasing, key | 0x1, effects.Overlay, (int)mask.Edges,
                        mask.BandLeft, mask.BandTop, mask.BandRight, mask.BandBottom, (int)mask.Falloff, mask.Intensity,
                        saturation);
                    if (!effects.SwapCut)
                    {
                        fadeStart = effects.SwapStartMs;
                        fadeDur = effects.SwapMs;
                        fadeEase = (int)ImageCache.SwapCrossfadeEasing;
                    }
                    placeholder = default;
                }
                dl.DrawImage(drawRect, p.Corners, imageId, ready, placeholder, world, opacity, uv, fadeStart, fadeDur,
                    fadeEase, key, effects.Overlay, (int)mask.Edges, mask.BandLeft, mask.BandTop, mask.BandRight,
                    mask.BandBottom, (int)mask.Falloff, mask.Intensity, saturation);
                break;
            }
            case VisualKind.ListRow:
            {
                // Scroll-rework Wave 0.E (scroll-rework-design.md §B.4): ONE node, ONE recorder case, ≤8 cells.
                // Background priority Selected(=PressedFill reuse) > Hover > resting Fill — a STATIC per-frame
                // resolve, not the Box case's ResolveSurface cross-fade (that helper blends on the POINTER-DOWN
                // press progress, which is the wrong signal for an app-level "selected" boolean; ListRowEl's hover
                // wash therefore snaps instead of cross-fading — a documented scope cut for this wave).
                ColorF rowFill = p.PressedFill.A > 0f ? p.PressedFill
                    : (p.HoverFill.A > 0f && (flags & (NodeFlags.Hovered | NodeFlags.HoverWithin)) != 0) ? p.HoverFill
                    : p.Fill;
                if (rowFill.A > 0f) dl.FillRoundRect(local, p.Corners, rowFill, world, opacity, key);

                if (scene.TryGetRowCells(node, out var rowCells, out bool placeholder, out ColorF placeholderColor))
                {
                    for (int ci = 0; ci < rowCells.Length; ci++)
                    {
                        ref readonly var cell = ref rowCells[ci];
                        RectF cellRect = cell.Rect;   // row-local — the SAME frame as `local` (this node's own box)
                        if (cellRect.W <= 0f || cellRect.H <= 0f) continue;

                        if (placeholder && cell.Kind != RowCellKind.Rect)
                        {
                            // Same geometry, no type swap (layout.md's Placeholder contract): a rounded grey bar at
                            // the cell's OWN rect — a pill for Text/Glyph, the cell's authored rounding for Image.
                            CornerRadius4 phCorners = cell.Kind == RowCellKind.Image
                                ? cell.Corners : CornerRadius4.All(cellRect.H * 0.5f);
                            dl.FillRoundRect(cellRect, phCorners, placeholderColor, world, opacity, key);
                            continue;
                        }

                        switch (cell.Kind)
                        {
                            case RowCellKind.Rect:
                                if (cell.Color.A > 0f) dl.FillRoundRect(cellRect, cell.Corners, cell.Color, world, opacity, key);
                                break;
                            case RowCellKind.Text:
                            case RowCellKind.Glyph:
                                // The SAME DrawGlyphRun opcode/backend glyph-run cache TextEl uses (2063 above) — a
                                // cell reshapes only when its own (text, style) changes, never once per frame, with
                                // no new shaping-cache plumbing (RowCell's doc comment).
                                if (!cell.Text.IsEmpty && cell.Color.A > 0f)
                                    dl.DrawGlyphRun(cellRect, cell.Color, cell.Text, cell.FontFamily, cell.FontSize, cell.FontWeight,
                                        (int)TextWrap.NoWrap, (int)cell.Trim, 1, 0f, float.NaN,
                                        (int)LineStacking.MaxHeight, (int)TextLineBounds.Full, world, opacity, key, spanRunId: 0);
                                break;
                            case RowCellKind.Image:
                                if (cell.ImageId != 0 && images is not null)
                                {
                                    bool cellReady = images.StateOf(new ImageHandle(cell.ImageId)) == ImageState.Ready;
                                    RectF cellDraw = cellRect;
                                    RectF cellUv = new RectF(0f, 0f, 1f, 1f);
                                    if (cellReady)
                                    {
                                        var (cw, ch) = images.SizeOf(new ImageHandle(cell.ImageId));
                                        (cellDraw, cellUv) = ImageContentFit(ImageFit.Cover, in cellRect, cw, ch, 0.5f, 0.5f);
                                    }
                                    dl.DrawImage(cellDraw, cell.Corners, cell.ImageId, cellReady, cell.Color, world, opacity,
                                        cellUv, float.NaN, 0f, 0, key, default, 0, 0f, 0f, 0f, 0f, 0, 0f, 1f);
                                }
                                break;
                        }
                    }
                }
                break;
            }
            case VisualKind.IconLayer:
            {
                // ThemedIcon layer: ImageId doubles as the IconGeometryTable PathId; Fill carries the resolved,
                // theme-live layer tint (bound thunk). The tint rides the command (colorless mask), so a retheme
                // recolors with no re-raster. Cross-fade rides the SAME BrushAnim(Fill) path as every surface tint.
                ColorF tint = p.Fill;
                if (maybeSparsePaint && scene.TryGetBrushAnim(node, out var iba) && (iba.Channels & BrushAnim.FillBit) != 0)
                    tint = ColorF.LerpLinear(iba.FillFrom, tint, iba.T);
                if (p.ImageId != 0 && tint.A > 0f)
                    dl.DrawIconMask(local, tint, p.ImageId, world, opacity, key);
                break;
            }
            case VisualKind.PolylineStroke:
            {
                if (maybeSparsePaint && scene.TryGetPolylineStroke(node, out var pl))
                {
                    float trimStart = float.IsNaN(p.StrokeTrimStart) ? pl.TrimStart : p.StrokeTrimStart;
                    float trimEnd = float.IsNaN(p.StrokeTrimEnd) ? pl.TrimEnd : p.StrokeTrimEnd;
                    trimStart = Math.Clamp(trimStart, 0f, 1f);
                    trimEnd = Math.Clamp(trimEnd, 0f, 1f);
                    if (pl.Color.A > 0f && pl.Thickness > 0f && pl.PointCount >= 2 && trimEnd > trimStart)
                        dl.PolylineStroke(local, pl.Color, pl.Thickness, pl.P0, pl.P1, pl.P2, pl.P3,
                            pl.PointCount, trimStart, trimEnd, pl.RoundCaps, world, opacity, key);
                }
                break;
            }
            case VisualKind.Sprites:
            {
                if (!maybeSparsePaint || !overlapsRecordClip) break;
                if (!scene.TryGetSpriteSpec(node, out var sps) || !scene.TryGetSprites(node, out var sprites) || sprites.Length == 0) break;
                bool spriteAdd = sps.Blend == PaintBlend.Additive && stats.AdditiveDepth == 0;
                if (spriteAdd) dl.SetBlend(PaintBlend.Additive);
                dl.Sprites(sps.Kernel, sprites, world, opacity, key);
                if (spriteAdd) dl.SetBlend(PaintBlend.SrcOver);
                // the subtree bounds: the union of every sprite (kernel-exact) + the AA pad, in world space
                float sl = float.PositiveInfinity, st = float.PositiveInfinity, sr = float.NegativeInfinity, sbm = float.NegativeInfinity;
                for (int si = 0; si < sprites.Length; si++)
                {
                    RectF sb = sprites[si].Bounds(sps.Kernel);
                    sl = MathF.Min(sl, sb.X); st = MathF.Min(st, sb.Y); sr = MathF.Max(sr, sb.X + sb.W); sbm = MathF.Max(sbm, sb.Y + sb.H);
                }
                if (sr >= sl && sbm >= st) result.Include(world.TransformBounds(new RectF(sl - 1f, st - 1f, sr - sl + 2f, sbm - st + 2f)));
                break;
            }
            case VisualKind.Series:
            {
                if (!maybeSparsePaint || !overlapsRecordClip) break;
                if (!scene.TryGetSeries(node, out var ss) || !scene.TryGetSeriesSamples(node, out var seriesSamples) || seriesSamples.Length < 2) break;
                float seriesMix = scene.TryGetGradientMix(node, out float sm) ? sm : 0f;
                bool seriesAdd = ss.Blend == PaintBlend.Additive && stats.AdditiveDepth == 0;
                if (seriesAdd) dl.SetBlend(PaintBlend.Additive);
                dl.Series(local, in ss, seriesSamples, world, opacity, key, seriesMix);
                if (seriesAdd) dl.SetBlend(PaintBlend.SrcOver);
                float seriesHalo = ss.Shape is SeriesShape.Stroke or SeriesShape.Polar ? ss.Thickness + 1f : 0f;
                result.Include(world.TransformBounds(new RectF(local.X - seriesHalo, local.Y - seriesHalo, local.W + 2f * seriesHalo, local.H + 2f * seriesHalo)));
                break;
            }
            case VisualKind.Path:
            {
                if (!maybeSparsePaint || !overlapsRecordClip) break;
                if (!scene.TryGetPath(node, out var ps) || ps.IsNone) break;
                var geometry = ps.Geometry;
                if (geometry is null) break;   // ps.IsNone already implies this, but narrow explicitly for the nullable analyzer

                // ViewBoxW/H > 0: bake a uniform-fit (min-axis) scale into THIS draw's world transform, so one authored
                // path (e.g. a 24x24-unit icon) renders correctly at any box size (PathSpec's documented contract).
                // ViewBoxW/H == 0 (the common case): the geometry is already node-local DIP — no rebase needed.
                Affine2D pathWorld = world;
                if (ps.ViewBoxW > 0f && ps.ViewBoxH > 0f && pw > 0f && ph > 0f)
                {
                    float fit = MathF.Min(pw / ps.ViewBoxW, ph / ps.ViewBoxH);
                    pathWorld = world.Multiply(Affine2D.Scale(fit, fit));
                }
                // Realization-cache quantization scale: the axis-aligned |M11| scale THIS draw applies to the path's
                // own coordinate space, times the frame's device-pixel scale — the same world.M11 convention the
                // tier-2 rounded-clip radius above uses, so a sub-quantum DPI/zoom wobble still hits the cache.
                float scaleQ = MathF.Abs(pathWorld.M11 != 0f ? pathWorld.M11 : 1f) * (scene.DeviceScale > 0f ? scene.DeviceScale : 1f);

                if (ps.Fill.A > 0f && PathRealizationCache.Shared.TryRealizeFill(geometry, ps.Rule, scaleQ, out var fr))
                {
                    // The command's Rect is the realization's own bounds (path space — the space pathWorld maps, a viewbox
                    // fit included) inflated by the ½-device-px AA fringe: the painted extent every cull, slice bound and
                    // sub-tile damage reads (SliceOpBounds). The node box was neither (a viewbox-scaled icon, a stroke
                    // straddling the box edge).
                    float fillFringe = 0.5f / scaleQ;
                    var fillRect = new RectF(fr.Bounds.X - fillFringe, fr.Bounds.Y - fillFringe, fr.Bounds.W + 2f * fillFringe, fr.Bounds.H + 2f * fillFringe);
                    dl.FillPath(fr.Bounds.W > 0f && fr.Bounds.H > 0f ? fillRect : local, ps.Fill, fr, (byte)ps.Rule, pathWorld, opacity, key);
                    // Union the fill's device bounds — the same shape as the shadow-halo union above, so
                    // damage/off-screen-cull/opacity-extent see the true painted extent.
                    result.Include(pathWorld.TransformBounds(fillRect));
                }
                // Trim values reach the PAYLOAD, never the realization key (TryRealizeStroke's key folds geometry +
                // style + scale only) — so a 60 Hz stroke-trim draw-on (the same StrokeTrim channels arc/polyline
                // already consume) hits the SAME cached tessellation every frame; only the shader's per-frame trim
                // uniform changes.
                if (ps.StrokeColor.A > 0f && !ps.Stroke.IsNone)
                {
                    float t0 = float.IsNaN(p.StrokeTrimStart) ? ps.TrimStart : p.StrokeTrimStart;
                    float t1 = float.IsNaN(p.StrokeTrimEnd) ? ps.TrimEnd : p.StrokeTrimEnd;
                    t0 = Math.Clamp(t0, 0f, 1f);
                    t1 = Math.Clamp(t1, 0f, 1f);
                    if (t1 > t0 && PathRealizationCache.Shared.TryRealizeStroke(geometry, ps.Stroke, scaleQ, out var sr))
                    {
                        // Rect = the stroke realization's bounds (its width included) + the AA fringe, as for the fill.
                        float strokeFringe = 0.5f / scaleQ;
                        var strokeRect = new RectF(sr.Bounds.X - strokeFringe, sr.Bounds.Y - strokeFringe,
                            sr.Bounds.W + 2f * strokeFringe, sr.Bounds.H + 2f * strokeFringe);
                        dl.StrokePath(sr.Bounds.W > 0f && sr.Bounds.H > 0f ? strokeRect : local, ps.StrokeColor, sr, t0, t1,
                            ps.Stroke.DashOn, ps.Stroke.DashOff, ps.TrimMode, pathWorld, opacity, key | 0x1);
                        result.Include(pathWorld.TransformBounds(strokeRect));
                    }
                }
                break;
            }
        }

        // Child-group shift (SizeMode.Reflow Trailing anchor): every child rides this offset while the node's own
        // fill/border/clip stay put — the content's end edge tracks the animated layout edge under the already-pushed
        // clip (the Expander slide-from-under-the-header). Zero at rest; compositor-composed, no per-child knowledge.
        Affine2D childWorld = p.ChildShiftX != 0f || p.ChildShiftY != 0f
            ? world.Translate(p.ChildShiftX, p.ChildShiftY)
            : world;
        InheritedState childState = inherited.ForChild(flags, nodeInteractive, scopeBoundary, hasLocalProgress, localHoverT, localPressT);
        // A flagged clip root re-tags its subtree: descendant hover-elevate deferrals PARK for this node's hoist
        // (see the deferral below and the consume after this node's scope closes) instead of emitting in place.
        if ((interaction.HandlerMask & InteractionInfo.HoverElevateClipRootBit) != 0)
            childState = childState.WithUnderElevateRoot();
        bool hasItemBand = scene.TryGetVirtualItemBand(
            node, out int itemBandPrefix, out float itemBandTopInset, out float itemBandTopFade);
        int disclosureFirst = 0, disclosureCount = 0, disclosurePrefix = 0, disclosureFirstRealized = 0;
        float disclosureTop = 0f, disclosureExtent = 0f, disclosureT = 0f;
        bool hasDisclosure = stats.HasActiveVirtualDisclosures
            && scene.TryGetVirtualDisclosure(node, out disclosureFirst, out disclosureCount,
                out disclosureTop, out disclosureExtent, out disclosureT,
                out disclosurePrefix, out disclosureFirstRealized);
        int disclosureLast = 0;
        float disclosureShift = 0f;
        RectF disclosureClip = childClip;
        if (hasDisclosure)
        {
            disclosureLast = disclosureFirst + disclosureCount;
            disclosureShift = -disclosureExtent * (1f - disclosureT);
            float contentW = MathF.Max(1f, scene.Bounds(node).W);
            disclosureClip = childClip.Intersect(childWorld.TransformBounds(
                new RectF(0f, disclosureTop, contentW, disclosureExtent * disclosureT)));
        }
        RectF itemBandClip = childClip;
        bool itemBandClipChanged = false;
        if (hasItemBand)
        {
            // parentWorld is the viewport's child frame BEFORE this content node's scrolling LocalTransform. Transforming
            // the authored inset through it therefore keeps the clip line fixed in viewport space while item roots move.
            float bandTop = parentWorld.Transform(new Point2(0f, itemBandTopInset)).Y;
            float clippedTop = MathF.Max(childClip.Y, bandTop);
            itemBandClip = new RectF(childClip.X, clippedTop, childClip.W,
                MathF.Max(0f, childClip.Bottom - clippedTop));
            itemBandClipChanged = itemBandClip != childClip;
        }
        // Retained tiles: inside a scroll CONTENT slice the item band's clip + feather are VIEWPORT-fixed while the rows
        // ride the content's translation — so the recyclable band records as its own slice (SliceRole.Band) whose marker
        // carries that clip/feather one slice level up (ParamsUp). Anything else viewport-fixed that must record INLINE
        // in the content stream (an exiting row's band clip) is expressed against the content's RECORD-time offset and
        // pins the slice to it (pose-locked: a moved content re-records instead of compositing).
        bool contentSlice = isSliceSelf && selfPose == SliceRecorder.PoseKind.Content && stats.Slicing;
        RectF itemBandClipInline = contentSlice
            ? SliceRecorder.Offset(in itemBandClip, -stats.SliceOwnDx, -stats.SliceOwnDy)
            : itemBandClip;
        DrawList childDl = dl;
        // Logically-detached exits remain visually owned by this node. Emit them before live children so incoming
        // content paints over them while this parent's transform, opacity/layers, clip, and popup target stay active.
        // Perf: OrphanCount is a scene-wide field (0 whenever no presence-exit animation is in flight = the steady case),
        // so gate the per-node dictionary probe on it — skips thousands of always-null hash lookups per maximized frame.
        var exitingChildren = scene.OrphanCount > 0 ? scene.OrphanChildrenOf(node) : null;
        if (exitingChildren is not null)
        {
            if (itemBandClipChanged && !itemBandClip.IsEmpty)
            {
                dl.PushClip(itemBandClipInline, key);
                if (contentSlice) stats.Slices!.MarkPoseLocked(selfSlot);
            }
            // The exiting rows record into THIS stream, so they cull and build their nested clips against the band line in
            // the stream's own (pose-free) space: a clipping row's push replaces the band scissor, and the viewport-space
            // line sits SliceOwnDy away from it (above it on a list scrolled down: the row painted over the sticky header).
            for (int i = 0; i < exitingChildren.Count; i++)
            {
                var exiting = exitingChildren[i];
                if (!scene.IsLive(exiting)) continue;
                var exitResult = Walk(scene, dl, images, exiting, childWorld, opacity, depth + 1,
                    hasItemBand ? itemBandClipInline : childClip, in focus, in textEdit, scrollThumb, scrollTrack,
                    childScaleX, childScaleY, inMotion, scrollInMotion, childState, skipRoots, spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
                result.Include(exitResult);
            }
            if (itemBandClipChanged && !itemBandClip.IsEmpty) dl.PopClip(key);
        }
        // Sticky pin paint order: a PINNED child (position:sticky engaged) is emitted AFTER its siblings so the
        // content scrolling beneath it paints underneath — CSS sticky's implicit stacking. Unpinned = normal order.
        {
            bool anyPinned = false;
            // Hover-elevate paint order (Element.HoverElevatePaint): a flagged child on the hover path is deferred to
            // paint ABOVE its non-elevated siblings (the declarative z-index of a hovered card). One deferred slot:
            // at most one sibling is hovered, and a two-card cross-fade is resolved by keeping the HIGHER-progress
            // card deferred (the lower one records in place) — O(1) space, no sort, no allocation.
            NodeHandle deferElevate = NodeHandle.Null;
            float deferElevateT = 0f;
            Affine2D deferElevateWorld = childWorld;
            RectF deferElevateClip = childClip;
            int childOrdinal = 0;
            bool itemBandPushed = false;
            bool itemBandFadePushed = false;
            int bandSlot = -1;
            bool bandStarted = false;
            SliceCtx bandSaved = default;
            var bandResult = new SpanRecordResult();
            for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c))
            {
                if (hasItemBand && !bandStarted && childOrdinal >= itemBandPrefix)
                {
                    bandStarted = true;
                    // A prefix child deferred for hover elevation must paint before the recyclable-band scissor starts;
                    // the prefix is deliberately exempt from the sticky item clip.
                    if (!deferElevate.IsNull)
                    {
                        var prefixElevated = Walk(scene, childDl, images, deferElevate, deferElevateWorld, opacity, depth + 1,
                            deferElevateClip, in focus, in textEdit, scrollThumb, scrollTrack,
                            childScaleX, childScaleY, inMotion, scrollInMotion, childState, skipRoots,
                            spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
                        result.Include(prefixElevated);
                        deferElevate = NodeHandle.Null;
                        deferElevateT = 0f;
                    }
                    bool bandClip = itemBandClipChanged && !itemBandClip.IsEmpty;
                    bool bandFade = !itemBandClip.IsEmpty && itemBandTopFade > 0.5f;
                    // One offscreen group feathers the contiguous recyclable suffix. The prefix is emitted outside this
                    // scope (and a pinned prefix is replayed after it), so sticky chrome stays crisp while rows dissolve
                    // at the exact hard paint/input boundary. Group alpha remains 1: child opacity is already carried by
                    // each Walk, and multiplying it again here would double-dim a translucent list.
                    float bandPx = itemBandTopFade * MathF.Abs(parentScaleY);
                    if (contentSlice && (bandClip || bandFade) && stats.InlineLayerDepth == 0
                        && (bandSlot = stats.Slices!.FindOrCreate((int)node.Raw.Index, node.Raw.Gen, SliceRole.Band, SliceKind.Scroll)) >= 0)
                    {
                        var sl = stats.Slices!;
                        int bflags = (int)CompositeSliceFlags.ParamsUp
                            | (bandClip ? (int)CompositeSliceFlags.OuterClip : 0)
                            | (bandFade ? (int)CompositeSliceFlags.Layer : 0);
                        PushLayerCmd bandLayer = bandFade
                            ? DrawList.EdgeFadeLayerCmd(itemBandClip, itemBandClip, default, 1f, (int)EdgeMask.Top,
                                0f, bandPx, 0f, 0f, (int)FadeFalloff.Smoothstep, 1f, 0f)
                            : default;
                        var bcmd = new CompositeSliceCmd((int)node.Raw.Index, node.Raw.Gen, (int)SliceRole.Band, (int)SliceKind.Scroll,
                            bflags, itemBandClip, new ClipCmd(itemBandClip), bandLayer, default, itemBandClip, key, key | 0x0D);
                        dl.CompositeSlice(in bcmd, key);
                        sl.AddChild(selfSlot, bandSlot);
                        sl.SetPose(bandSlot, SliceRecorder.PoseKind.None, Affine2D.Identity, 0f, 0f, Affine2D.Identity, 0f, 0f);
                        sl.SetMarker(bandSlot, in itemBandClip, bflags, in bandLayer, default);
                        if (bandFade) stats.EdgeFadeGroupCount++;
                        bandSaved = SaveSlice(ref stats);
                        childDl = sl.BeginWalk(bandSlot);
                        EnterSlice(ref stats, sl, bandSlot, 0f, 0f);
                    }
                    else
                    {
                        bandSlot = -1;
                        if (bandClip)
                        {
                            childDl.PushClip(itemBandClipInline, key);
                            if (contentSlice) stats.Slices!.MarkPoseLocked(selfSlot);
                        }
                        itemBandPushed = bandClip;
                        if (bandFade)
                        {
                            childDl.PushEdgeFadeLayer(itemBandClipInline, itemBandClipInline, default, 1f, (int)EdgeMask.Top,
                                0f, bandPx, 0f, 0f, (int)FadeFalloff.Smoothstep, 1f,
                                sortKey: key | 0x0D);
                            if (contentSlice) stats.Slices!.MarkPoseLocked(selfSlot);
                            stats.EdgeFadeGroupCount++;
                            itemBandFadePushed = true;
                            stats.InlineLayerDepth++;
                        }
                    }
                }
                int ordinal = childOrdinal;
                // The band's viewport-fixed clip culls a suffix row only where it is RECORDED with the rows: inline (the
                // slice is then pose-locked, so the record-time offset is the present one), as the line in the stream's
                // own space (itemBandClipInline: pose-locking freezes SliceOwnDy, it does not make it 0). A band SLICE
                // carries that clip on its marker and the composite applies it at the live pose, while the rows' recording
                // is reused at every pose inside coverage — culling them against the record-time line lost every row whose
                // content-local top sat above it (the rows above a re-centred arrange origin: RCA 2026-09-25 G, the blank
                // playlist).
                RectF activeChildClip = hasItemBand && ordinal >= itemBandPrefix && bandSlot < 0 ? itemBandClipInline : childClip;
                Affine2D activeChildWorld = childWorld;
                // A disclosed row is revealed through the growing band (hit testing clips it there too). The clip a Walk
                // receives only CULLS, so a row the band cuts records under that band as its own scissor: a half-revealed
                // row otherwise paints at full height under the sliding suffix. The band rides the rows (content-local), so
                // a content slice needs no pose-lock; the push replaces the scissor, so it carries the full intersection.
                bool disclosed = false;
                if (hasDisclosure)
                {
                    int logicalIndex = ordinal < disclosurePrefix
                        ? ordinal
                        : disclosureFirstRealized + (ordinal - disclosurePrefix);
                    if (logicalIndex >= disclosureFirst && logicalIndex < disclosureLast)
                    {
                        RectF revealed = activeChildClip.Intersect(disclosureClip);
                        disclosed = revealed != activeChildClip;
                        activeChildClip = revealed;
                    }
                    else if (logicalIndex >= disclosureLast)
                        activeChildWorld = childWorld.Translate(0f, disclosureShift);
                }
                NodeFlags cf = scene.Flags(c);
                childOrdinal++;
                if ((cf & NodeFlags.StickyPinned) != 0) { anyPinned = true; continue; }
                if (activeChildClip.IsEmpty) continue;
                // A disclosed row records in place under its band scissor: elevation would replay (or hoist) it outside it.
                if (!disclosed && (scene.Interaction(c).HandlerMask & InteractionInfo.HoverElevatePaintBit) != 0)
                {
                    // Hover source published at record time: NodeFlags.Hovered/HoverWithin (instantaneous), else the
                    // node's own eased InteractionAnim.HoverT so the EXIT fade keeps it elevated until it decays < 0.01.
                    float ht = (cf & (NodeFlags.Hovered | NodeFlags.HoverWithin)) != 0 ? 1f
                             : (scene.TryGetInteract(c, out var cia) ? cia.HoverT : 0f);
                    if (ht > 0.01f)
                    {
                        if (deferElevate.IsNull)
                        {
                            deferElevate = c; deferElevateT = ht;
                            deferElevateWorld = activeChildWorld; deferElevateClip = activeChildClip;
                            continue;
                        }
                        if (ht > deferElevateT)
                        {
                            // A higher-progress card appeared — flush the previously-deferred (lower) one in place now,
                            // then defer this one so the most-hovered card ends up on top.
                            var flushed = Walk(scene, childDl, images, deferElevate, deferElevateWorld, opacity, depth + 1,
                                deferElevateClip, in focus, in textEdit, scrollThumb, scrollTrack,
                                childScaleX, childScaleY, inMotion, scrollInMotion, childState, skipRoots, spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
                            result.Include(flushed);
                            if (bandSlot >= 0) bandResult.Include(flushed);
                            deferElevate = c; deferElevateT = ht;
                            deferElevateWorld = activeChildWorld; deferElevateClip = activeChildClip;
                            continue;
                        }
                        // Lower progress than the deferred card → record it now in normal order (falls through).
                    }
                }
                if (disclosed) childDl.PushClip(activeChildClip, key);
                var childResult = Walk(scene, childDl, images, c, activeChildWorld, opacity, depth + 1,
                    activeChildClip, in focus, in textEdit, scrollThumb, scrollTrack,
                    childScaleX, childScaleY, inMotion, scrollInMotion, childState, skipRoots, spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
                if (disclosed) childDl.PopClip(key);
                result.Include(childResult);
                if (bandSlot >= 0) bandResult.Include(childResult);
            }
            // The elevated (hovered) card paints after its non-elevated siblings, but before any sticky-pinned chrome.
            // Under a HoverElevateClipRoot ancestor it doesn't emit HERE at all: it PARKS in the accumulator (one slot)
            // and the flagged ancestor hoists it after its clip + edge-fade scope closes — the card's lift/halo then paint
            // OUTSIDE the strip's clip against the root's incoming clip (the true z-index escape), while everything
            // resting stays exactly clipped. No root above (or the slot already taken) → emit in place as before.
            if (!deferElevate.IsNull)
            {
                if (childState.UnderElevateRoot != 0 && stats.PendingElevate.IsNull)
                {
                    stats.PendingElevate = deferElevate;
                    stats.PendingElevateWorld = deferElevateWorld;
                    stats.PendingElevateOpacity = opacity;
                    stats.PendingElevateDepth = depth + 1;
                    stats.PendingElevateScaleX = childScaleX;
                    stats.PendingElevateScaleY = childScaleY;
                    stats.PendingElevateInMotion = inMotion;
                    stats.PendingElevateScrollInMotion = scrollInMotion;
                    // Strip the under-root tag: the hoisted subtree records OUTSIDE the root, and a flagged child
                    // inside it (the card wrapper in the cell) must defer in place, not re-park into a dead slot.
                    stats.PendingElevateState = childState.WithoutUnderElevateRoot();
                    // The parked world is in THIS slice's space; the hoist re-bases it into its own (see the consume).
                    stats.PendingElevateSlot = stats.CurSlot;
                    stats.PendingElevateDx = stats.CurDx;
                    stats.PendingElevateDy = stats.CurDy;
                }
                else
                {
                    var elevResult = Walk(scene, childDl, images, deferElevate, deferElevateWorld, opacity, depth + 1,
                        deferElevateClip, in focus, in textEdit, scrollThumb, scrollTrack,
                        childScaleX, childScaleY, inMotion, scrollInMotion, childState, skipRoots, spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
                    result.Include(elevResult);
                    if (bandSlot >= 0) bandResult.Include(elevResult);
                }
            }
            if (bandSlot >= 0)
            {
                stats.Slices!.EndWalk(bandSlot, bandResult.HasBounds ? bandResult.SubtreeBounds : default);
                RestoreSlice(ref stats, in bandSaved);
                childDl = dl;
            }
            if (itemBandFadePushed) { childDl.PopLayer(itemBandClipInline, key | 0x0D); stats.InlineLayerDepth--; }
            if (itemBandPushed) childDl.PopClip(key);
            if (anyPinned)
            {
                int pinnedOrdinal = 0;
                bool pinnedBandPushed = false;
                int pinnedSlot = -1;
                SliceCtx pinnedSaved = default;
                var pinnedResult = new SpanRecordResult();
                for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c), pinnedOrdinal++)
                    if ((scene.Flags(c) & NodeFlags.StickyPinned) != 0)
                    {
                        if (hasItemBand && pinnedOrdinal >= itemBandPrefix && !pinnedBandPushed && pinnedSlot < 0
                            && itemBandClipChanged && !itemBandClip.IsEmpty)
                        {
                            if (contentSlice && stats.InlineLayerDepth == 0
                                && (pinnedSlot = stats.Slices!.FindOrCreate((int)node.Raw.Index, node.Raw.Gen, SliceRole.PinnedBand, SliceKind.Scroll)) >= 0)
                            {
                                var sl = stats.Slices!;
                                int pflags = (int)CompositeSliceFlags.ParamsUp | (int)CompositeSliceFlags.OuterClip;
                                var pcmd = new CompositeSliceCmd((int)node.Raw.Index, node.Raw.Gen, (int)SliceRole.PinnedBand, (int)SliceKind.Scroll,
                                    pflags, itemBandClip, new ClipCmd(itemBandClip), default, default, default, key, key);
                                dl.CompositeSlice(in pcmd, key);
                                sl.AddChild(selfSlot, pinnedSlot);
                                sl.SetPose(pinnedSlot, SliceRecorder.PoseKind.None, Affine2D.Identity, 0f, 0f, Affine2D.Identity, 0f, 0f);
                                sl.SetMarker(pinnedSlot, in itemBandClip, pflags, default, default);
                                pinnedSaved = SaveSlice(ref stats);
                                childDl = sl.BeginWalk(pinnedSlot);
                                EnterSlice(ref stats, sl, pinnedSlot, 0f, 0f);
                            }
                            else
                            {
                                pinnedSlot = -1;
                                childDl.PushClip(itemBandClipInline, key);
                                if (contentSlice) stats.Slices!.MarkPoseLocked(selfSlot);
                                pinnedBandPushed = true;
                            }
                        }
                        // Like the band rows: a PinnedBand slice carries the band line on its marker at the live pose, so its rows
                        // walk under the incoming clip; recorded inline (pose-locked) they cull and nest their clips against the
                        // line in the stream's own space (the viewport-space line sits SliceOwnDy away from it).
                        RectF pinnedClip = hasItemBand && pinnedOrdinal >= itemBandPrefix && pinnedSlot < 0 ? itemBandClipInline : childClip;
                        Affine2D pinnedWorld = childWorld;
                        bool pinnedDisclosed = false;   // scissored to the disclosure band like the in-order rows above
                        if (hasDisclosure)
                        {
                            int logicalIndex = pinnedOrdinal < disclosurePrefix
                                ? pinnedOrdinal
                                : disclosureFirstRealized + (pinnedOrdinal - disclosurePrefix);
                            if (logicalIndex >= disclosureFirst && logicalIndex < disclosureLast)
                            {
                                RectF revealed = pinnedClip.Intersect(disclosureClip);
                                pinnedDisclosed = revealed != pinnedClip;
                                pinnedClip = revealed;
                            }
                            else if (logicalIndex >= disclosureLast)
                                pinnedWorld = childWorld.Translate(0f, disclosureShift);
                        }
                        if (pinnedClip.IsEmpty) continue;
                        if (pinnedDisclosed) childDl.PushClip(pinnedClip, key);
                        var childResult = Walk(scene, childDl, images, c, pinnedWorld, opacity, depth + 1,
                            pinnedClip, in focus, in textEdit, scrollThumb, scrollTrack,
                            childScaleX, childScaleY, inMotion, scrollInMotion, childState, skipRoots, spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
                        if (pinnedDisclosed) childDl.PopClip(key);
                        result.Include(childResult);
                        if (pinnedSlot >= 0) pinnedResult.Include(childResult);
                    }
                if (pinnedSlot >= 0)
                {
                    stats.Slices!.EndWalk(pinnedSlot, pinnedResult.HasBounds ? pinnedResult.SubtreeBounds : default);
                    RestoreSlice(ref stats, in pinnedSaved);
                    childDl = dl;
                }
                if (pinnedBandPushed) childDl.PopClip(key);
            }
        }

        // Box border chrome paints after descendants. A control border must remain visible over filled child regions
        // (dialog command rows, split-button halves, presenter bodies) instead of forcing every control to fake a
        // border with nested fill plates.
        if (pendingGradientBorder)
            EmitGradientBorderRing(dl, pb, p.Corners, p.BorderWidth, in pendingBorderBrush, in pendingHoverBorderBrush,
                pendingHasHoverBorderBrush, in pendingPressedBorderBrush, pendingHasPressedBorderBrush,
                pendingBorderHoverT, pendingBorderPressT, world, opacity, key);
        else if (pendingSolidBorder)
            EmitBorderRing(dl, local, pb, p.Corners, p.BorderWidth, pendingBorder, p.BorderDashOn, p.BorderDashOff, world, opacity, key);

        if (acrylicLayer)
        {
            dl.PopLayer(deviceBounds, key);
        }

        // ── auto-hiding scrollbar thumb (overlay; over content, within the viewport bounds) ──
        if (pushedClip)
        {
            if (pushedStencil) { dl.PopStencilClip(childClip, stencilClipRef, stencilClipWorld, key); stats.InlineLayerDepth--; }
            else dl.PopClip(key);
        }

        // ── focus ring: keyboard focus only (FocusVisual), drawn last so it overlays children. Emitted AFTER the
        // node's own clip pops — the WinUI ring lives OUTSIDE the bounds (FocusVisualMargin −3), so a ClipsToBounds
        // control (a TextBox field) must not scissor its own ring away. Ancestor clips still apply (correct).
        if (focus.Enabled && (flags & NodeFlags.FocusVisual) != 0 && overlapsRecordClip)
        {
            EmitFocusRing(dl, b, p.Corners, interaction.FocusVisualMargin, world, opacity, in focus, key | 0x10);
            // The ring sits OUTSIDE the bounds (negative FocusVisualMargin) — union EmitFocusRing's own focus rect into
            // the subtree bounds, same superset rationale as the shadow halo above.
            var fvm = interaction.FocusVisualMargin;
            float feL = MathF.Max(0f, -fvm.Left), feT = MathF.Max(0f, -fvm.Top);
            float feR = MathF.Max(0f, -fvm.Right), feB = MathF.Max(0f, -fvm.Bottom);
            result.Include(world.TransformBounds(new RectF(-feL, -feT, b.W + feL + feR, b.H + feT + feB)));
        }

        // Auto-hiding scrollbar overlay: draw after popping the viewport's content clip so the expanded gutter/thumb
        // are not chopped at the viewport edge, while still positioning them inside the viewport bounds. EmitScrollbar
        // self-gates on overflow and FadeT.
        // Scroll-edge cues (controls.md §8.3): the FADE of an edge cue is this viewport's analytic edge feather (the
        // reconciler resolved it onto AutoEdgeFade — TryResolveEdgeFade synthesises the runway-ramped spec), so the
        // content dissolves into whatever lies behind it. Only a FadeAndChevron surface paints anything here: its
        // chevrons, drawn BEFORE the scrollbar (under the thumb), not gated on FadeT, self-gated on overflow per edge.
        // The edge feather closes BEFORE the chrome (see chromeOverFade).
        if (chromeOverFade && !omitLayer) { dl.PopLayer(deviceBounds, key); stats.InlineLayerDepth--; }
        if (overlapsRecordClip && (flags & NodeFlags.Scrollable) != 0 && scene.TryGetScroll(node, out var sec))
        {
            var scbChrome = scene.ScrollChrome.Get((int)node.Raw.Index);
            float shownOff = ShownOffset(scene, node, in sec);
            bool bar = scrollThumb.A > 0f;
            // A SLICED fade (its layer rides its marker, so everything this walk records lands inside it): the chevrons, the
            // rail and the arrows go to this viewport's Chrome slice, the thumb to its Thumb slice — both markers trail the
            // fade's content, so placement puts them outside its feather (SliceRecorder.IsScrollChrome). The Chrome slice
            // lives as long as the bar shows (born with the thumb's, not when the rail first expands — so a hover or a drag
            // that expands it mid-gesture allocates nothing); it has bounds only while something is drawn in it.
            bool chevrons = sec.EdgeCueChevron && ScrollOverflows(in sec);
            DrawList railDl = dl;
            int chromeSlot = -1;
            if (chromeOverFade && omitLayer && stats.Slicing && stats.InlineLayerDepth == 0 && stats.Slices is { } csl
                && (chevrons || (bar && ScrollBarShows(in sec, in scbChrome)))
                && (chromeSlot = csl.FindOrCreate((int)node.Raw.Index, node.Raw.Gen, SliceRole.Chrome, SliceKind.Effect)) >= 0)
            {
                var ccmd = new CompositeSliceCmd((int)node.Raw.Index, node.Raw.Gen, (int)SliceRole.Chrome, (int)SliceKind.Effect,
                    0, clip, default, default, default, default, key | 0x28, key | 0x28);
                dl.CompositeSlice(in ccmd, key | 0x28);
                csl.AddChild(stats.CurSlot, chromeSlot);
                csl.SetPose(chromeSlot, SliceRecorder.PoseKind.None, Affine2D.Identity, 0f, 0f, Affine2D.Identity, 0f, 0f);
                csl.SetMarker(chromeSlot, in clip, 0, default, default);
                railDl = csl.BeginWalk(chromeSlot);
            }
            if (sec.EdgeCueChevron) EmitScrollEdgeChevrons(railDl, b, in sec, shownOff, world, opacity, key | 0x18, scrollThumb);
            if (bar)
                EmitScrollbar(railDl, dl, node, b, in sec, shownOff, in scbChrome, world, opacity, key | 0x20, scrollThumb, scrollTrack,
                    clip, ref stats);
            if (chromeSlot >= 0)
                stats.Slices!.EndWalk(chromeSlot, chevrons || (bar && ScrollRailPaints(in sec, in scbChrome, scrollThumb, scrollTrack))
                    ? world.TransformBounds(b) : default);
        }

        // Close the flat opacity / self-blur group LAST: everything this node emitted (shadow, fill, children, border,
        // focus ring, scrollbar) flattens into the offscreen RT and composites once (blurred, for the blur group) at the
        // group alpha. Exactly one of these was pushed (blur subsumes the opacity group).
        if (isEdgeFade && !omitLayer && !chromeOverFade) { dl.PopLayer(deviceBounds, key); stats.InlineLayerDepth--; }
        if (isOpacityGroup && !omitLayer)
        {
            // Bound the group (see the push site): `result` now holds this subtree's accumulated draw bounds — every
            // walked node's device box, plus each self-blur halo / shadow halo / focus ring — which is a SUPERSET of the
            // pixels the group emitted, NOT merely the node's own bounds (descendants legitimately paint outside those).
            // Clamp to the enclosing clip (nothing past it can reach the canvas) and pad for sub-pixel ink overshoot.
            // Degenerate/absent/nested-layer ⇒ leave the cmd unpatched, i.e. today's full-canvas clear + composite.
            if (result.HasBounds && dl.OpcodeStats.PushLayer + dl.OpcodeStats.CompositeSlice == opacityPushLayerCount)
            {
                RectF ext = new RectF(result.SubtreeBounds.X - OpacityGroupExtentPadDip, result.SubtreeBounds.Y - OpacityGroupExtentPadDip,
                                      result.SubtreeBounds.W + 2f * OpacityGroupExtentPadDip, result.SubtreeBounds.H + 2f * OpacityGroupExtentPadDip)
                    .Intersect(clip);
                if (opacityPushByteOffset >= 0 && !ext.IsEmpty) dl.PatchOpacityLayerExtent(opacityPushByteOffset, in ext);
            }
            dl.PopLayer(deviceBounds, key);
            stats.InlineLayerDepth--;
        }
        if (pushedBlurSourceClip) dl.PopClip(key);
        if (isBlurGroup && !omitLayer) { dl.PopLayer(deviceBounds, key); stats.InlineLayerDepth--; }

        // Hover-elevate clip-ESCAPE consume: this node is the flagged clip root and a descendant deferral parked the
        // hovered card — re-walk it NOW, with this node's whole scope (clip, edge fade, groups) already closed, against
        // OUR incoming clip. The card's lift + halo paint outside the strip; resting content stayed exactly clipped.
        // Emitted BEFORE the span store so the hoisted commands live inside this node's stored span (a reused span
        // replays them at the same position). Span reuse/store are disabled for the hoisted subtree itself — its record
        // position is hover-dependent, never a stable reuse anchor.
        if ((interaction.HandlerMask & InteractionInfo.HoverElevateClipRootBit) != 0 && !stats.PendingElevate.IsNull)
        {
            var hoist = stats.PendingElevate;
            stats.PendingElevate = NodeHandle.Null;
            // BOUNDED escape: OUR incoming clip INTERSECTED with our own box inflated by the halo slack (see
            // HoverElevateHoistSlackDip). Passing the bare incoming clip made the escape unbounded — the hoisted card
            // could paint anywhere the ancestors allow, which for a multi-row strip is the whole page.
            RectF hoistClip = clip.Intersect(new RectF(
                deviceBounds.X - HoverElevateHoistSlackDip, deviceBounds.Y - HoverElevateHoistSlackDip,
                deviceBounds.W + 2f * HoverElevateHoistSlackDip, deviceBounds.H + 2f * HoverElevateHoistSlackDip));
            // Zero-area intersection ⇒ nothing the hoist could contribute; the pending slot is already released above.
            if (!hoistClip.IsEmpty)
            {
                // The card was parked in its own slice's space (a scrolled shelf's content). Re-base it into this one at
                // the RECORD-time offset between the two, and pin the slices in between to it: a later move of that
                // content re-records this hoist instead of compositing it (retained tiles: pose-locked). The pin holds
                // at a ZERO offset too (a shelf resting at page 0): the lock compares the slice's live offset with the
                // recorded one, so the first pan still re-records rather than sliding the row out from under the card.
                Affine2D hw = stats.PendingElevateWorld;
                float rdx = stats.PendingElevateDx - stats.CurDx, rdy = stats.PendingElevateDy - stats.CurDy;
                if (rdx != 0f || rdy != 0f)
                    hw = new Affine2D(hw.M11, hw.M12, hw.M21, hw.M22, hw.Dx + rdx, hw.Dy + rdy);
                stats.Slices?.MarkPoseLockedChain(stats.PendingElevateSlot, stats.CurSlot);
                var hoistResult = Walk(scene, dl, images, hoist, hw, stats.PendingElevateOpacity,
                    stats.PendingElevateDepth, hoistClip, in focus, in textEdit, scrollThumb, scrollTrack,
                    stats.PendingElevateScaleX, stats.PendingElevateScaleY, stats.PendingElevateInMotion,
                    stats.PendingElevateScrollInMotion, stats.PendingElevateState, skipRoots, spans, spanFrame,
                    spanReuseDisabled: true, spanStoreEnabled: false, ref stats);
                result.Include(hoistResult);
            }
        }

        // ── Repaint damage (§13.1), re-record path ──────────────────────────────────────────────────────────────────
        // Emitted HERE, at the tail, because `result.SubtreeBounds` is only complete now: it is the superset of every
        // pixel this node AND its descendants drew (device boxes + shadow halos + self-blur halos + focus rings), which
        // is what a repaint must cover — the node's own box is not (a moved card carries its children with it).
        // Read BEFORE the Store below, which overwrites the slot this frame's prior extent lives in.
        // Only SELF-dirty nodes emit: a node re-recorded merely because a descendant is dirty contributes nothing new,
        // and the descendant emits its own band. Rects here are in the CURRENT SLICE's space (AddRepaint maps them into
        // the window); a prior extent is in the space of the slice it was last recorded into, mapped by where that
        // slice was last presented. They are the Present census and the forced-full detector only: a retained tile is
        // invalidated by its content want, never by these rects (gpu-renderer.md §13.1c).
        stats.GeomCovered = prevGeomCovered;
        if (geomDamage && !stats.Repaint.IsFull && result.HasBounds)
            EmitGeometryDamage(scene, node, spans!, in result.SubtreeBounds, ref stats, withCurrent: true);
        if (!stats.Repaint.IsFull && result.HasBounds)
        {
            // A translation slice root's own transform is its composite pose (the flatten damages its motion): only a
            // content change of its own counts here.
            bool movedNode = (flags & NodeFlags.TransformDirty) != 0 && !selfHasLocal;
            bool contentDirtyNode = (scene.RecordDirtySelfBits(node) & SceneStore.RecordDirtyContent) != 0
                || clipBakedChanged;   // a baked sticky clip moved: a paint change no dirty bit carries
            if (movedNode || contentDirtyNode)
            {
                var repaintParent = scene.Parent(node);
                // §2b: every band this node emits grows by the reach of any BLURRED ancestor, because that is how far
                // this node's changed pixels travel once the enclosing blur spreads them. 0 for nearly every node.
                float blurHalo = EnclosingBlurHaloDip(scene, node);
                if (movedNode && !selfHasLocal && !repaintParent.IsNull && (scene.Flags(repaintParent) & NodeFlags.Scrollable) != 0)
                {
                    // A scrolled viewport's content node recorded INLINE (its pose baked): the changed pixels are the
                    // VIEWPORT (window space), not the content's (far taller) box.
                    stats.AddRepaintWindow(RepaintBand(scene.AbsoluteRect(repaintParent), blurHalo));
                }
                else
                {
                    stats.AddRepaint(RepaintBand(result.SubtreeBounds, blurHalo));
                    // old ∪ new: the band the node VACATED repaints too. A brand-new node never presented, so its current
                    // extent is the whole truth. With no span table at all there is no prior to recover and a move leaves
                    // an unknown vacated band ⇒ full.
                    if (spans is null)
                    {
                        if (movedNode) stats.Repaint.ForceFull(RepaintFullReason.MissingPriorExtent);
                    }
                    else if (spans.TryGetPriorExtent((int)node.Raw.Index, node.Raw.Gen, spanFrame, out RectF priorExtent, out _))
                    {
                        int priorSlot = spans.SliceSlotOf((int)node.Raw.Index, node.Raw.Gen);
                        if (stats.Slices is { } psl && psl.TryPresentedDelta(priorSlot, out float pdx, out float pdy))
                            stats.AddRepaintWindow(RepaintBand(SliceRecorder.Offset(in priorExtent, pdx, pdy), blurHalo));
                        else stats.AddRepaintWindow(RepaintBand(in priorExtent, blurHalo));
                    }
                }
            }
        }

        if (selfAdditive) dl.SetBlend(PaintBlend.SrcOver);

        if (spanTracking)
        {
            var span = new DrawSpan(
                spanByteStart,
                dl.BytePosition - spanByteStart,
                spanSortStart,
                dl.SortPosition - spanSortStart,
                dl.CommandCount - spanCommandStart,
                dl.OpcodeStats.Minus(in spanOpcodeStart),
                world,
                result.SubtreeBounds,
                IsClipComplete(result.SubtreeBounds, in clip));
            spans!.Store((int)node.Raw.Index, node.Raw.Gen, spanFrame, spanInputSig, in span, stats.CurGen, stats.CurSlot);
            spans.StoreSelf((int)node.Raw.Index, in visualBounds);
            stats.SpansReRecorded++;
        }
        if (isSliceSelf) stats.Slices!.EndWalk(selfSlot, result.HasBounds ? result.SubtreeBounds : default);
        stats.Reloc = parentReloc;

        return result;
    }

    private static void CountGroups(ref RecordAccumulator stats, bool edgeFade, bool blurCandidate, bool blurGroup)
    {
        if (edgeFade) stats.EdgeFadeGroupCount++;
        if (blurCandidate) stats.BlurCandidateCount++;
        if (blurGroup) stats.BlurGroupCount++;
    }

    /// <summary>A node moved or resized by layout (or carried out of view): the band it last presented at and - unless it
    /// was culled - the one it presents at now join the repaint set (the retained tiles under them re-raster by their
    /// content wants). Read BEFORE the span store overwrites the prior geometry.</summary>
    private void EmitGeometryDamage(SceneRecordingSnapshot scene, NodeHandle node, SpanTable spans, in RectF current,
        ref RecordAccumulator stats, bool withCurrent)
    {
        float blurHalo = EnclosingBlurHaloDip(scene, node);
        if (withCurrent) stats.AddRepaint(RepaintBand(current, blurHalo));
        if (spans.TryGetPriorGeometry((int)node.Raw.Index, node.Raw.Gen, out _, out _, out RectF prior, out int priorSlot))
        {
            if (stats.Slices is { } psl && psl.TryPresentedDelta(priorSlot, out float pdx, out float pdy))
                stats.AddRepaintWindow(RepaintBand(SliceRecorder.Offset(in prior, pdx, pdy), blurHalo));
            else stats.AddRepaintWindow(RepaintBand(in prior, blurHalo));
        }
    }

    private static bool SameAffine(in Affine2D a, in Affine2D b)
        => MathF.Abs(a.M11 - b.M11) <= 1e-4f && MathF.Abs(a.M12 - b.M12) <= 1e-4f && MathF.Abs(a.M21 - b.M21) <= 1e-4f
           && MathF.Abs(a.M22 - b.M22) <= 1e-4f && MathF.Abs(a.Dx - b.Dx) <= 0.01f && MathF.Abs(a.Dy - b.Dy) <= 0.01f;

    private static bool SameRect(in RectF a, in RectF b)
        => MathF.Abs(a.X - b.X) <= 0.01f && MathF.Abs(a.Y - b.Y) <= 0.01f
           && MathF.Abs(a.W - b.W) <= 0.01f && MathF.Abs(a.H - b.H) <= 0.01f;

    private ulong ComputeSpanInputSig(SceneRecordingSnapshot scene, NodeHandle node, NodeFlags flags, int depth, in RectF clip, in Affine2D world,
                                             float opacity, float parentScaleX, float parentScaleY, float childScaleX, float childScaleY,
                                             float pw, float ph, bool inMotion, in InheritedState inherited,
                                             in FocusVisualStyle focus, in TextEditStyle textEdit, ColorF scrollThumb, ColorF scrollTrack,
                                             bool clipComposite = false)
    {
        ulong h = 14695981039346656037UL;
        Mix(ref h, node.Raw.Gen);
        Mix(ref h, (uint)flags);
        Mix(ref h, (uint)depth);
        MixRect(ref h, in clip);
        MixAffine(ref h, in world);
        MixFloat(ref h, opacity);
        MixFloat(ref h, parentScaleX);
        MixFloat(ref h, parentScaleY);
        MixFloat(ref h, childScaleX);
        MixFloat(ref h, childScaleY);
        MixFloat(ref h, pw);
        MixFloat(ref h, ph);
        Mix(ref h, inMotion ? 1u : 0u);
        // userScrollActive, NOT scrollInMotion: only the former reaches emitted bytes (it defers a DoF blur to
        // HoldIfCached at the blur-hold decision), and scrollInMotion already kills exact reuse outright at its
        // own gate. Keying on scrollInMotion instead both missed the blur flip and needlessly re-keyed the whole
        // scroller subtree on programmatic offset writes (lyric follow, bring-into-view).
        MixScrollViewport(scene, node, flags, ref h);
        MixVirtualItemBand(scene, node, ref h);
        MixPaintReveal(scene, node, clipComposite, ref h);
        MixFloat(ref h, inherited.HoverT);
        MixFloat(ref h, inherited.PressT);
        Mix(ref h, (uint)inherited.InteractiveFlags);
        Mix(ref h, inherited.HasProgress);
        Mix(ref h, inherited.Disabled);
        // Text-motion softness (audit 2026-09-22, cause #2) used to reach emitted bytes here (DrawGlyphRunCmd.InMotion
        // fed from the enclosing viewport's live scroll speed) and had to key exact reuse or a decelerating fling
        // would replay glyph runs stamped with a faster frame's softness. TextMotionSoftness/InheritedState.MotionSoft
        // are deleted (SceneRecorder.WalkCore no longer ramps softness with speed — every run records crisp,
        // DrawGlyphRunCmd.InMotion defaults to 0 at every call site), so there is nothing left to key here.
        MixColor(ref h, focus.Outer);
        MixColor(ref h, focus.Inner);
        MixFloat(ref h, focus.Thickness);
        MixColor(ref h, textEdit.SelectionFill);
        MixColor(ref h, textEdit.SelectedText);
        MixColor(ref h, textEdit.CaretColor);
        MixColor(ref h, scrollThumb);
        MixColor(ref h, scrollTrack);
        return h;
    }

    /// <summary>Fold presented-size / child-shift / authored clip into the span key so a collapsing hero (PresentedHTrailing)
    /// cannot byte-copy a subtree recorded under a different reveal clip — the focus-regain / re-theme steady frame after a
    /// scroll-effect pose (a ClipTop / reveal channel rewritten by the pose sink) was the regression path.</summary>
    private void MixPaintReveal(SceneRecordingSnapshot scene, NodeHandle node, bool clipComposite, ref ulong h)
    {
        // clipComposite: the node's slice composites its sticky clip (it never reaches a byte), so the posed clip is not
        // an input of the bytes — a page scroll keeps the slice whole.
        ref readonly NodePaint p = ref scene.Paint(node);
        MixFloat(ref h, p.ChildShiftX);
        MixFloat(ref h, p.ChildShiftY);
        if (!clipComposite && !p.ClipRect.IsInfinite) MixRect(ref h, in p.ClipRect);
    }

    /// <summary>Is a .StickyClip on <paramref name="node"/> a plain scissor over everything the node emits — so it can be
    /// applied at composite time, exactly? Not when the paint route draws part of the node's own paint BEFORE its clip
    /// push (a drop shadow, a progress arc) or the clip feeds a frost (acrylic). A blur group is excluded by the caller.</summary>
    private static bool StickyClipIsPlainScissor(SceneRecordingSnapshot scene, NodeHandle node, bool maybeSparsePaint)
    {
        if (!maybeSparsePaint) return true;
        if (scene.TryGetShadow(node, out var sh) && !sh.IsNone) return false;
        if (scene.TryGetArc(node, out var arc) && !arc.IsNone) return false;
        if (scene.TryGetAcrylic(node, out _)) return false;
        return true;
    }

    /// <summary>The offset a viewport's chrome (thumb, edge fades, edge cues) draws against: the render poser's shown
    /// position when this tick posed the viewport (so the thumb never lags the content), else the UI frame's offset.</summary>
    private static float ShownOffset(SceneRecordingSnapshot scene, NodeHandle node, in ScrollState sc)
        => SliceRecorder.ShownOffset(scene, node, in sc);

    private void MixScrollViewport(SceneRecordingSnapshot scene, NodeHandle node, NodeFlags flags, ref ulong h)
    {
        if ((flags & NodeFlags.Scrollable) == 0 || !scene.HasScroll(node)) return;
        ref readonly var sc = ref scene.ScrollRef(node);
        // ScrollState is partly layout-/animation-owned rather than reconciler-owned. These values can therefore change
        // without changing the element or node bounds. They all affect commands emitted by this viewport (edge mask,
        // edge cues, thumb geometry/alpha, or whether the thumb exists), so they must participate in both exact-copy and
        // translated-copy keys. Omitting them let a clean retained span resurrect a no-fade/no-scrollbar frame forever.
        MixFloat(ref h, sc.ContentW);
        MixFloat(ref h, sc.ContentH);
        MixFloat(ref h, sc.ViewportW);
        MixFloat(ref h, sc.ViewportH);
        // NOT the shown offset: the thumb is a composite-posed slice (SliceRole.Thumb) and every other offset-dependent
        // value this viewport records — the auto edge fade's bands and the edge cues' alphas, which ramp only over the last
        // 24 DIP at either end — is folded into ChromeSig. Mid-list, a scroll never changes this key.
        ulong chromeSig = SliceRecorder.ChromeSig(in sc, ShownOffset(scene, node, in sc));
        Mix(ref h, (uint)chromeSig);
        Mix(ref h, (uint)(chromeSig >> 32));
        var chromeRow = scene.ScrollChrome.Get((int)node.Raw.Index);
        MixFloat(ref h, chromeRow.FadeT);
        MixFloat(ref h, chromeRow.ExpandT);
        MixFloat(ref h, sc.AutoEdgeFadeBand);
        Mix(ref h, sc.Orientation);
        Mix(ref h, sc.EdgeCueConfig);
        Mix(ref h, sc.AutoEdgeFade ? 1u : 0u);
        Mix(ref h, sc.AlwaysShowBar ? 1u : 0u);
        Mix(ref h, sc.SuppressBar ? 1u : 0u);
        Mix(ref h, (uint)sc.LoadingBarSuppressors);
    }

    private void MixVirtualItemBand(SceneRecordingSnapshot scene, NodeHandle node, ref ulong h)
    {
        if (!scene.TryGetVirtualItemBand(node, out int prefix, out float inset, out float fadeBand)) return;
        Mix(ref h, (uint)prefix);
        MixFloat(ref h, inset);
        MixFloat(ref h, fadeBand);
    }


    private RectF TranslateBounds(in RectF bounds, float dx, float dy)
        => bounds.IsEmpty ? bounds : new RectF(bounds.X + dx, bounds.Y + dy, bounds.W, bounds.H);

    private bool IsClipComplete(in RectF bounds, in RectF clip)
    {
        if (bounds.IsEmpty || clip.IsInfinite) return true;
        const float epsilon = 0.01f;
        return bounds.X >= clip.X - epsilon
            && bounds.Y >= clip.Y - epsilon
            && bounds.Right <= clip.Right + epsilon
            && bounds.Bottom <= clip.Bottom + epsilon;
    }

    private bool TryTranslationDelta(in Affine2D from, in Affine2D to, out float dx, out float dy)
    {
        dx = dy = 0f;
        if (!Nearly(from.M11, to.M11) || !Nearly(from.M12, to.M12)
            || !Nearly(from.M21, to.M21) || !Nearly(from.M22, to.M22))
            return false;
        dx = to.Dx - from.Dx;
        dy = to.Dy - from.Dy;
        return true;
    }

    private bool Nearly(float a, float b) => MathF.Abs(a - b) <= 0.0001f;

    private void MixRect(ref ulong h, in RectF r)
    {
        MixFloat(ref h, r.X);
        MixFloat(ref h, r.Y);
        MixFloat(ref h, r.W);
        MixFloat(ref h, r.H);
    }

    private void MixAffine(ref ulong h, in Affine2D a)
    {
        MixFloat(ref h, a.M11);
        MixFloat(ref h, a.M12);
        MixFloat(ref h, a.M21);
        MixFloat(ref h, a.M22);
        MixFloat(ref h, a.Dx);
        MixFloat(ref h, a.Dy);
    }

    private void MixColor(ref ulong h, ColorF c)
    {
        MixFloat(ref h, c.R);
        MixFloat(ref h, c.G);
        MixFloat(ref h, c.B);
        MixFloat(ref h, c.A);
    }

    private void MixFloat(ref ulong h, float v) => Mix(ref h, BitConverter.SingleToUInt32Bits(v));

    private void Mix(ref ulong h, uint v)
    {
        h ^= v;
        h *= 1099511628211UL;
    }

    /// <summary>Resolve the surface fill/border for this frame: eased hover/press if an interaction row exists,
    /// else the instantaneous flag behaviour (first frame / no animator).</summary>
    private bool TryResolveInteractionProgress(in InheritedState inherited, bool nodeInteractive, bool hasLocalProgress,
                                                      float localHoverT, float localPressT, out float hoverT, out float pressT)
    {
        if (hasLocalProgress)
        {
            hoverT = localHoverT;
            pressT = localPressT;
            return true;
        }
        // Non-interactive visuals inherit progress from the nearest interactive ancestor carried by the walk.
        if (!nodeInteractive && inherited.HasProgress != 0)
        {
            hoverT = inherited.HoverT;
            pressT = inherited.PressT;
            return true;
        }

        hoverT = 0f;
        pressT = 0f;
        return false;
    }

    private float ResolveOpacity(NodeFlags flags, in NodePaint p, in InheritedState inherited, bool nodeInteractive,
                                        bool hasLocalProgress, float localHoverT, float localPressT)
    {
        bool hasHover = !float.IsNaN(p.HoverOpacity);
        bool hasPress = !float.IsNaN(p.PressedOpacity);
        if (!hasHover && !hasPress) return p.Opacity;

        float opacity = p.Opacity;
        if (TryResolveInteractionProgress(in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT, out float hoverT, out float pressT))
        {
            if (hasHover)
                opacity += (p.HoverOpacity - opacity) * hoverT;
            if (hasPress)
                opacity += (p.PressedOpacity - opacity) * pressT;
            return opacity;
        }

        if (hasPress && (flags & NodeFlags.Pressed) != 0) return p.PressedOpacity;
        if (hasHover && (flags & NodeFlags.Hovered) != 0) return p.HoverOpacity;
        return opacity;
    }

    /// <summary>A live BrushTransition is fading this node's FILL away from a visible colour: the displayed
    /// <c>LerpLinear(FillFrom, Fill, T)</c> is still on screen even when the target Fill is transparent (the "no tint"
    /// fade-out). Only reached when the box has no other surface; callers gate it on SparsePaint.</summary>
    private static bool FillFadeVisible(SceneRecordingSnapshot scene, NodeHandle node)
        => scene.TryGetBrushAnim(node, out var ba)
           && (ba.Channels & BrushAnim.FillBit) != 0 && ba.FillFrom.A > 0f && ba.T < 1f;

    private void ResolveSurface(SceneRecordingSnapshot scene, NodeHandle node, NodeFlags flags, in NodePaint p, in InheritedState inherited,
                                       bool nodeInteractive, bool hasLocalProgress, float localHoverT, float localPressT,
                                       out ColorF fill, out ColorF border)
    {
        fill = p.Fill; border = p.BorderColor;
        if (TryResolveInteractionProgress(in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT, out float hoverT, out float pressT)
            && (hoverT > 0.001f || pressT > 0.001f))
        {
            // Inherited progress from a hovered container must NOT auto-lighten descendant fills — only an explicit
            // HoverFill/PressedFill opts in (matches FollowsContainerHover: reveal/scale only; pure-fill tracks the
            // actual pointer). Local interactive nodes keep the auto-lighten default when HoverFill is unset.
            // Cross-fade in LINEAR light (color canon: linear-blend / premultiplied) — not straight sRGB.
            bool local = hasLocalProgress;
            if (hoverT > 0.001f && (p.HoverFill.A > 0f || local))
            {
                ColorF hov = p.HoverFill.A > 0f ? p.HoverFill : Lighten(p.Fill, 0.08f);
                fill = ColorF.LerpLinear(p.Fill, hov, hoverT);
            }
            if (pressT > 0.001f && (p.PressedFill.A > 0f || local))
            {
                ColorF prs = p.PressedFill.A > 0f ? p.PressedFill : Darken(p.Fill, 0.12f);
                fill = ColorF.LerpLinear(fill, prs, pressT);
            }
            // Border eases to its explicit per-state token when set (e.g. CheckBox unchecked-pressed stroke →
            // ControlStrongStrokeColorDisabled), else falls back to a lighten/darken of the resting border —
            // same local-vs-inherited rule as fill (a toast strip's HoverWithin must not wash every card stroke).
            if (hoverT > 0.001f && (p.HoverBorderColor.A > 0f || local))
            {
                ColorF hb = p.HoverBorderColor.A > 0f ? p.HoverBorderColor : Lighten(p.BorderColor, 0.08f);
                border = ColorF.LerpLinear(p.BorderColor, hb, hoverT);
            }
            if (pressT > 0.001f && (p.PressedBorderColor.A > 0f || local))
            {
                ColorF pb = p.PressedBorderColor.A > 0f ? p.PressedBorderColor : Darken(p.BorderColor, 0.12f);
                border = ColorF.LerpLinear(border, pb, pressT);
            }
        }
        else if ((flags & NodeFlags.Pressed) != 0)
        {
            fill = p.PressedFill.A > 0f ? p.PressedFill : Darken(fill, 0.12f);
            border = p.PressedBorderColor.A > 0f ? p.PressedBorderColor : Darken(border, 0.12f);
        }
        else if ((flags & NodeFlags.Hovered) != 0)
        {
            fill = p.HoverFill.A > 0f ? p.HoverFill : Lighten(fill, 0.08f);
            border = p.HoverBorderColor.A > 0f ? p.HoverBorderColor : Lighten(border, 0.08f);
        }

        // Implicit BrushTransition (logical state flip): cross-fade from the previously-displayed color toward the
        // state-resolved color above. Linear-light, like every other brush cross-fade (color canon).
        if (scene.TryGetBrushAnim(node, out var ba))
        {
            if ((ba.Channels & BrushAnim.FillBit) != 0) fill = ColorF.LerpLinear(ba.FillFrom, fill, ba.T);
            if ((ba.Channels & BrushAnim.BorderBit) != 0) border = ColorF.LerpLinear(ba.BorderFrom, border, ba.T);
        }

        // Validation (form-validation.md): an invalid field's resolved error color (already theme-resolved on the UI
        // thread by the reconciler — the recorder stays theme-agnostic) overrides the resting/state border. A==0 ⇒ none.
        if (p.ValidationBorder.A > 0f) border = p.ValidationBorder;
    }

    /// <summary>Resolve a text/glyph node's foreground for this frame. Plain text (no state colors) returns instantly.
    /// Otherwise: Disabled wins as a step (self-or-ancestor input-disabled), then Hover/Pressed ease with the nearest
    /// interactive ancestor's progress (falling back to an instant flag-step when that ancestor has no anim row, exactly
    /// like <see cref="ResolveSurface"/> does for the box fill), then Focused as a step, else the resting color.</summary>
    private ColorF ResolveTextColor(SceneRecordingSnapshot scene, NodeHandle node, NodeFlags flags, in NodePaint p, in InheritedState inherited,
                                           bool nodeInteractive, bool hasLocalProgress, float localHoverT, float localPressT)
    {
        ColorF resolved = ResolveTextColorCore(flags, in p, in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT);
        // Implicit BrushTransition on the foreground (logical state flip): cross-fade from the previously-displayed color.
        if (scene.TryGetBrushAnim(node, out var ba) && (ba.Channels & BrushAnim.TextBit) != 0)
            resolved = ColorF.LerpLinear(ba.TextFrom, resolved, ba.T);
        return resolved;
    }

    private ColorF ResolveTextColorCore(NodeFlags flags, in NodePaint p, in InheritedState inherited, bool nodeInteractive,
                                               bool hasLocalProgress, float localHoverT, float localPressT)
    {
        // Fast path: the overwhelming majority of text has no state ramps (A==0 on every axis).
        if (p.TextHoverColor.A == 0f && p.TextPressedColor.A == 0f && p.TextDisabledColor.A == 0f && p.TextFocusedColor.A == 0f)
            return p.TextColor;

        if (p.TextDisabledColor.A > 0f && (inherited.Disabled != 0 || (flags & NodeFlags.Disabled) != 0))
            return p.TextDisabledColor;

        bool hasHover = p.TextHoverColor.A > 0f;
        bool hasPress = p.TextPressedColor.A > 0f;
        if (hasHover || hasPress)
        {
            if (TryResolveInteractionProgress(in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT, out float hoverT, out float pressT)
                && (hoverT > 0.001f || pressT > 0.001f))
            {
                ColorF c = p.TextColor;
                if (hasHover) c = ColorF.LerpLinear(c, p.TextHoverColor, hoverT);   // linear-light cross-fade (color canon)
                if (hasPress) c = ColorF.LerpLinear(c, p.TextPressedColor, pressT);
                return c;
            }
            // No progress row on the interactive ancestor → instant step from its hover/press flags.
            NodeFlags istate = nodeInteractive ? flags : inherited.InteractiveFlags;
            if (hasPress && (istate & NodeFlags.Pressed) != 0) return p.TextPressedColor;
            if (hasHover && (istate & NodeFlags.Hovered) != 0) return p.TextHoverColor;
        }

        if (p.TextFocusedColor.A > 0f && (flags & NodeFlags.Focused) != 0)
            return p.TextFocusedColor;

        return p.TextColor;
    }

    /// <summary>Map a decoded source (<paramref name="srcW"/>×<paramref name="srcH"/> px) into <paramref name="box"/>
    /// per <paramref name="fit"/> — returns the draw quad (node-local) and the 0..1 source UV sub-rect. <c>Cover</c>
    /// crops via a centered UV inset (quad = box); <c>Contain</c>/<c>None</c> shrink the quad and center it; <c>Fill</c>
    /// (and unknown / zero source) keeps the whole texture stretched to the box. Pure — also the golden-test entry point.</summary>
    public static (RectF DrawRect, RectF Uv) ImageContentFit(ImageFit fit, in RectF box, int srcW, int srcH, float focusX = 0.5f, float focusY = 0.5f)
    {
        RectF drawRect = box;
        RectF uv = new RectF(0f, 0f, 1f, 1f);
        if (fit == ImageFit.Fill || srcW <= 0 || srcH <= 0 || box.W <= 0f || box.H <= 0f) return (drawRect, uv);

        float srcAR = (float)srcW / srcH, boxAR = box.W / box.H;
        switch (fit)
        {
            case ImageFit.Cover:
                focusX = Math.Clamp(focusX, 0f, 1f);
                focusY = Math.Clamp(focusY, 0f, 1f);
                if (boxAR > srcAR)
                {
                    float uh = srcAR / boxAR;
                    float uy = Math.Clamp(focusY - uh * 0.5f, 0f, 1f - uh);
                    uv = new RectF(0f, uy, 1f, uh);
                }
                else if (boxAR < srcAR)
                {
                    float uw = boxAR / srcAR;
                    float ux = Math.Clamp(focusX - uw * 0.5f, 0f, 1f - uw);
                    uv = new RectF(ux, 0f, uw, 1f);
                }
                break;
            case ImageFit.Contain:
                if (boxAR > srcAR) { float w2 = box.H * srcAR; drawRect = new RectF(box.X + (box.W - w2) * 0.5f, box.Y, w2, box.H); }
                else if (boxAR < srcAR) { float h2 = box.W / srcAR; drawRect = new RectF(box.X, box.Y + (box.H - h2) * 0.5f, box.W, h2); }
                break;
            case ImageFit.None:
                float dw = MathF.Min(srcW, box.W), dh = MathF.Min(srcH, box.H);
                drawRect = new RectF(box.X + (box.W - dw) * 0.5f, box.Y + (box.H - dh) * 0.5f, dw, dh);
                uv = new RectF((1f - dw / srcW) * 0.5f, (1f - dh / srcH) * 0.5f, dw / srcW, dh / srcH);
                break;
        }
        return (drawRect, uv);
    }

    // A centerline-based SDF stroke insets the rect by bw/2; to keep the band CONCENTRIC with the box's rounded corner
    // (so the stroke's outer edge lands exactly on the bounds outline) the corner radius must shrink by the SAME bw/2 —
    // else the corner arc re-centres and the 1px ring reads as a rough/uneven corner instead of a smooth WinUI one.
    private CornerRadius4 InsetCorners(in CornerRadius4 c, float d)
        => new(MathF.Max(0f, c.TopLeft - d), MathF.Max(0f, c.TopRight - d), MathF.Max(0f, c.BottomRight - d), MathF.Max(0f, c.BottomLeft - d));

    private void EmitBorderRing(DrawList dl, in RectF local, in RectF b, in CornerRadius4 corners, float bw, in ColorF border, float dashOn, float dashOff, in Affine2D world, float opacity, ulong key)
    {
        var rect = new RectF(bw * 0.5f, bw * 0.5f, MathF.Max(0f, b.W - bw), MathF.Max(0f, b.H - bw));
        var ins = InsetCorners(corners, bw * 0.5f);
        if (dashOn > 0f)
            dl.StrokeRoundRectDashed(rect, ins, border, bw, dashOn, dashOff, world, opacity, key);   // DropZone "drop here" look
        else
            dl.StrokeRoundRect(rect, ins, border, bw, world, opacity, key);
    }

    private void EmitGradient(DrawList dl, in RectF local, in CornerRadius4 corners, in GradientSpec g,
        in GradientSpec hover, bool hasHover, in GradientSpec pressed, bool hasPressed, float hoverT, float pressT,
        bool hasRadialCenter, Point2 radialCenter, in Affine2D world, float opacity, ulong key,
        in GradientSpec mixTo = default, float mix = 0f)
    {
        // axis endpoints in local 0..1: linear from the angle (0 = →, 90 = ↓); radial carries its origin in `start`
        // and origin+radius in `end` (the shader reconstructs centre/radius from them).
        float rad = g.AngleDeg * (MathF.PI / 180f);
        float dx = MathF.Cos(rad), dy = MathF.Sin(rad);
        Point2 start, end;
        if (g.Shape == GradientShape.Radial)
        {
            Point2 center = hasRadialCenter ? radialCenter : g.RadialCenter;
            start = center;
            end = new Point2(center.X + g.RadialRadius.X, center.Y + g.RadialRadius.Y);
        }
        else
        {
            start = new Point2(0.5f - dx * 0.5f, 0.5f - dy * 0.5f);
            end = new Point2(0.5f + dx * 0.5f, 0.5f + dy * 0.5f);
        }
        var s = g.Stops;
        int n = Math.Min(s.Length, GradientSpec.MaxStops);
        ColorF c0 = s[0].Color, c1 = n > 1 ? s[1].Color : c0, c2 = n > 2 ? s[2].Color : c1, c3 = n > 3 ? s[3].Color : c2;
        float o0 = s[0].Offset, o1 = n > 1 ? s[1].Offset : 1f, o2 = n > 2 ? s[2].Offset : 1f, o3 = n > 3 ? s[3].Offset : 1f;
        // GradientMix (a palette cross-fade): the resting stops blend toward GradientTo FIRST, so hover/press still read on top.
        if (mix > 0.001f) LerpStops(ref c0, ref c1, ref c2, ref c3, ref o0, ref o1, ref o2, ref o3, n, in mixTo, mix);
        // P4b: per-frame interpolate the resting stops toward the hover/pressed gradient by the eased progress (stack locals,
        // never a new GradientSpec). Differing stop counts blend only the shared prefix (rest of resting stops hold).
        if (hasHover && hoverT > 0.001f) LerpStops(ref c0, ref c1, ref c2, ref c3, ref o0, ref o1, ref o2, ref o3, n, in hover, hoverT);
        if (hasPressed && pressT > 0.001f) LerpStops(ref c0, ref c1, ref c2, ref c3, ref o0, ref o1, ref o2, ref o3, n, in pressed, pressT);
        RemapAbsoluteAxis(in g, MathF.Abs(dx) * local.W + MathF.Abs(dy) * local.H, n,
            ref c0, ref c1, ref c2, ref c3, ref o0, ref o1, ref o2, ref o3);
        dl.GradientRect(new DrawGradientRectCmd(local, corners, start, end, (int)g.Shape, n, c0, c1, c2, c3, o0, o1, o2, o3, world, opacity), key);
    }

    /// <summary>WinUI <c>MappingMode="Absolute"</c> (record-time emulation): squeeze the stop ramp into
    /// <see cref="GradientSpec.AxisLengthPx"/> physical px of the node's axis extent — the shader's edge-clamp holds
    /// the boundary stop across the rest (the ControlElevationBorder 3px band, Common_themeresources_any.xaml:186).
    /// <see cref="GradientSpec.AnchorEnd"/> measures the band from the END of the axis (the ScaleY=-1 elevation
    /// mirror), which reverses the stop order so offsets stay ascending. Stack-only, zero alloc.</summary>
    private void RemapAbsoluteAxis(in GradientSpec g, float extent, int n,
        ref ColorF c0, ref ColorF c1, ref ColorF c2, ref ColorF c3,
        ref float o0, ref float o1, ref float o2, ref float o3)
    {
        if (g.AxisLengthPx <= 0f || extent <= 0.01f) return;
        float k = MathF.Min(1f, g.AxisLengthPx / extent);
        if (!g.AnchorEnd)
        {
            o0 *= k;
            if (n > 1) o1 *= k; else o1 = 1f;   // unused trailing slots stay at 1 (ascending, shader clamp intact)
            if (n > 2) o2 *= k; else o2 = 1f;
            if (n > 3) o3 *= k; else o3 = 1f;
            return;
        }
        Span<float> os = stackalloc float[4];
        Span<ColorF> cs = stackalloc ColorF[4];
        os[0] = o0; os[1] = o1; os[2] = o2; os[3] = o3;
        cs[0] = c0; cs[1] = c1; cs[2] = c2; cs[3] = c3;
        for (int i = 0; i < n; i++) os[i] = 1f - os[i] * k;
        for (int i = 0; i < n / 2; i++)
        {
            (os[i], os[n - 1 - i]) = (os[n - 1 - i], os[i]);
            (cs[i], cs[n - 1 - i]) = (cs[n - 1 - i], cs[i]);
        }
        c0 = cs[0]; c1 = n > 1 ? cs[1] : c0; c2 = n > 2 ? cs[2] : c1; c3 = n > 3 ? cs[3] : c2;
        o0 = os[0]; o1 = n > 1 ? os[1] : 1f; o2 = n > 2 ? os[2] : 1f; o3 = n > 3 ? os[3] : 1f;
    }

    // Blend the four stack-local gradient stops toward another spec's stops by t (linear-light color, linear offset).
    // Zero-alloc: reads the (stable, mount-allocated) stop array; blends only the prefix shared with the resting count.
    private void LerpStops(ref ColorF c0, ref ColorF c1, ref ColorF c2, ref ColorF c3,
        ref float o0, ref float o1, ref float o2, ref float o3, int n, in GradientSpec to, float t)
    {
        var s = to.Stops;
        int m = Math.Min(n, Math.Min(s.Length, GradientSpec.MaxStops));
        if (m > 0) { c0 = ColorF.LerpLinear(c0, s[0].Color, t); o0 += (s[0].Offset - o0) * t; }
        if (m > 1) { c1 = ColorF.LerpLinear(c1, s[1].Color, t); o1 += (s[1].Offset - o1) * t; }
        if (m > 2) { c2 = ColorF.LerpLinear(c2, s[2].Color, t); o2 += (s[2].Offset - o2) * t; }
        if (m > 3) { c3 = ColorF.LerpLinear(c3, s[3].Color, t); o3 += (s[3].Offset - o3) * t; }
    }

    /// <summary>A gradient-tinted border ring: the gradient PS sampled along the local axis, drawn as an SDF band of
    /// width <paramref name="bw"/> centered on a rect inset by bw/2 (so the stroke sits inside the bounds, WinUI-style).
    /// Relative specs span the whole control; <see cref="GradientSpec.AxisLengthPx"/> specs confine the blend to the
    /// WinUI absolute band (ControlElevationBorderBrush's 3px edge) via the record-time stop remap.</summary>
    private void EmitGradientBorderRing(DrawList dl, in RectF b, in CornerRadius4 corners, float bw, in GradientSpec g,
        in GradientSpec hover, bool hasHover, in GradientSpec pressed, bool hasPressed, float hoverT, float pressT,
        in Affine2D world, float opacity, ulong key)
    {
        float rad = g.AngleDeg * (MathF.PI / 180f);
        float dx = MathF.Cos(rad), dy = MathF.Sin(rad);
        var start = new Point2(0.5f - dx * 0.5f, 0.5f - dy * 0.5f);
        var end = new Point2(0.5f + dx * 0.5f, 0.5f + dy * 0.5f);
        var s = g.Stops;
        int n = Math.Min(s.Length, GradientSpec.MaxStops);
        ColorF c0 = s[0].Color, c1 = n > 1 ? s[1].Color : c0, c2 = n > 2 ? s[2].Color : c1, c3 = n > 3 ? s[3].Color : c2;
        float o0 = s[0].Offset, o1 = n > 1 ? s[1].Offset : 1f, o2 = n > 2 ? s[2].Offset : 1f, o3 = n > 3 ? s[3].Offset : 1f;
        if (hasHover && hoverT > 0.001f) LerpStops(ref c0, ref c1, ref c2, ref c3, ref o0, ref o1, ref o2, ref o3, n, in hover, hoverT);
        if (hasPressed && pressT > 0.001f) LerpStops(ref c0, ref c1, ref c2, ref c3, ref o0, ref o1, ref o2, ref o3, n, in pressed, pressT);
        RemapAbsoluteAxis(in g, MathF.Abs(dx) * b.W + MathF.Abs(dy) * b.H, n,
            ref c0, ref c1, ref c2, ref c3, ref o0, ref o1, ref o2, ref o3);
        var ring = new RectF(bw * 0.5f, bw * 0.5f, MathF.Max(0f, b.W - bw), MathF.Max(0f, b.H - bw));
        dl.GradientStroke(new DrawGradientStrokeCmd(ring, InsetCorners(corners, bw * 0.5f), start, end, (int)g.Shape, n, c0, c1, c2, c3, o0, o1, o2, o3, bw, world, opacity), key);
    }

    /// <summary>WinUI dual focus visual, margin-aware: the focus rect is the bounds expanded by −FocusVisualMargin
    /// (templates use −3 ⇒ 3px out; Slider −7,0,−7,0). The 2px PRIMARY (outer) stroke hugs the inside of the focus
    /// rect's edge; the 1px SECONDARY (inner) stroke sits immediately inside it — with the default margin the pair
    /// lands exactly on the control edge (edge → 1px inner → 2px outer). Centerline SDF strokes, so each rect insets
    /// by half its thickness; corner radii grow with the expansion to stay concentric.</summary>
    private void EmitFocusRing(DrawList dl, in RectF b, in CornerRadius4 corners, in Edges4 margin, in Affine2D world, float opacity, in FocusVisualStyle f, ulong key)
    {
        // Per-side expansion (negative WinUI margin = grow outward). Clamp ≥ 0 — a positive margin never shrinks inside.
        float eL = MathF.Max(0f, -margin.Left), eT = MathF.Max(0f, -margin.Top);
        float eR = MathF.Max(0f, -margin.Right), eB = MathF.Max(0f, -margin.Bottom);
        float tP = MathF.Max(1f, f.Thickness);   // primary (outer) thickness — WinUI FocusVisualPrimaryThickness = 2

        // The focus rect in node-local space.
        var fr = new RectF(-eL, -eT, b.W + eL + eR, b.H + eT + eB);
        // Corner radius grows by the smaller adjacent expansion so the arc stays concentric with the control corner.
        var fc = new CornerRadius4(
            corners.TopLeft + MathF.Min(eL, eT), corners.TopRight + MathF.Min(eR, eT),
            corners.BottomRight + MathF.Min(eR, eB), corners.BottomLeft + MathF.Min(eL, eB));

        if (f.Outer.A > 0f)   // primary band [edge-tP .. edge] inside the focus rect → centerline inset tP/2
            dl.StrokeRoundRect(
                new RectF(fr.X + tP * 0.5f, fr.Y + tP * 0.5f, MathF.Max(0f, fr.W - tP), MathF.Max(0f, fr.H - tP)),
                InsetCorners(fc, tP * 0.5f), f.Outer, tP, world, opacity, key);
        if (f.Inner.A > 0f)   // secondary 1px band immediately inside the primary → centerline inset tP + 0.5
        {
            float i = tP + 0.5f;
            dl.StrokeRoundRect(
                new RectF(fr.X + i, fr.Y + i, MathF.Max(0f, fr.W - 2f * i), MathF.Max(0f, fr.H - 2f * i)),
                InsetCorners(fc, i), f.Inner, 1f, world, opacity, key);
        }
    }

    /// <summary>An auto-hiding scrollbar thumb sized from the viewport's content/offset, faded by <c>FadeT</c>, expanded on lane hover.</summary>
    /// <summary>Does <paramref name="sc"/> overflow its viewport along its axis (anything to scroll)?</summary>
    private static bool ScrollOverflows(in ScrollState sc)
        => sc.Orientation == 1 ? sc.ContentW > sc.ViewportW + 0.5f : sc.ContentH > sc.ViewportH + 0.5f;

    /// <summary>Does <see cref="EmitScrollbar"/> draw anything at all (its early-outs, read up front)?</summary>
    private static bool ScrollBarShows(in ScrollState sc, in FluentGpu.Scroll.Runtime.ScrollBarChromeRow chrome)
    {
        if (sc.SuppressBar || sc.LoadingBarSuppressors > 0 || !ScrollOverflows(in sc)) return false;
        float fade = sc.AlwaysShowBar ? 1f : Math.Clamp(chrome.FadeT, 0f, 1f);
        float expand = Math.Clamp(chrome.ExpandT, 0f, 1f);
        return fade > 0.01f || expand > 0.01f;
    }

    /// <summary>Does <see cref="EmitScrollbar"/> paint any RAIL (the expanded gutter or the arrows) — the same gates it
    /// applies, read up front so a sliced fade cuts a Chrome slice only when it has something to hold.</summary>
    private static bool ScrollRailPaints(in ScrollState sc, in FluentGpu.Scroll.Runtime.ScrollBarChromeRow chrome, ColorF thumb, ColorF track)
    {
        if (!ScrollBarShows(in sc, in chrome)) return false;
        float fade = sc.AlwaysShowBar ? 1f : Math.Clamp(chrome.FadeT, 0f, 1f);
        float expand = Math.Clamp(chrome.ExpandT, 0f, 1f);
        var trackBase = track.A > 0f ? track : thumb with { A = 0.16f };
        return trackBase.A * fade * expand > 0.01f || fade * expand > 0.04f;
    }

    /// <summary>The overlay scrollbar: the rail (gutter, arrows) into <paramref name="railDl"/>, then the THUMB — its own
    /// posed slice, whose marker goes to <paramref name="dl"/>, or drawn into <paramref name="railDl"/> when it cannot be
    /// cut. The thumb is emitted last (over the arrows it can touch only mid-expand), so a sliced viewport's chrome markers
    /// trail its content.</summary>
    private void EmitScrollbar(DrawList railDl, DrawList dl, NodeHandle vpNode, in RectF b, in ScrollState sc, float shown, in FluentGpu.Scroll.Runtime.ScrollBarChromeRow chrome,
        in Affine2D world, float opacity, ulong key, ColorF thumb, ColorF track, in RectF clip, ref RecordAccumulator stats)
    {
        if (sc.SuppressBar || sc.LoadingBarSuppressors > 0) return;   // pager-driven shelf, or a descendant skeleton is loading — no rail
        bool horizontal = sc.Orientation == 1;
        float content = horizontal ? sc.ContentW : sc.ContentH;
        float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        {
            if (content <= viewport + 0.5f)
            {
                return;
            }

            const float bar = 12f;          // ScrollBarSize (ScrollBar_themeresources.xaml:180)
            const float collapsed = 2f;     // VISIBLE collapsed thumb: ThumbMinWidth 8 (:182) − transparent stroke 6 (:185)
            const float thumbOffset = 1f;   // collapsed fill rides 1px off the edge (8px rect, +2 translate, 6px stroke → [cross−3, cross−1])
            const float minExpanded = 30f;  // ScrollBarVerticalThumbMinHeight
            const float minCollapsed = 32f; // VerticalPanningThumb.MinHeight
            const float radius = 3f;        // ScrollBarCornerRadius

            // Persistent bar (ScrollEl.AlwaysShowScrollbar): pin the rail visible (fade=1) whenever content overflows,
            // bypassing the auto-hide FadeT. Hover still drives ExpandT (thin rail → full bar) through the normal arm path.
            float fade = sc.AlwaysShowBar ? 1f : Math.Clamp(chrome.FadeT, 0f, 1f);
            float expand = Math.Clamp(chrome.ExpandT, 0f, 1f);
            if (fade <= 0.01f && expand <= 0.01f) return;

            float axis = horizontal ? b.W : b.H;
            float cross = horizontal ? b.H : b.W;
            float button = bar * expand;
            float trackStart = button;
            float trackLen = MathF.Max(1f, axis - 2f * button);
            float frac = Math.Clamp(viewport / content, 0.08f, 1f);
            float minThumb = minCollapsed + (minExpanded - minCollapsed) * expand;
            float thumbLen = MathF.Min(trackLen, MathF.Max(minThumb, frac * trackLen));
            float travel = MathF.Max(1f, trackLen - thumbLen);
            float off = shown;
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
                railDl.FillRoundRect(gutter, CornerRadius4.All(radius), trackCol, world, opacity, key);
            }

            const float expandedVisible = 6f;                       // ScrollBarSize 12 − stroke 6, centred (3px insets)
            float thick = collapsed + (expandedVisible - collapsed) * expand;
            float collapsedCrossPos = cross - collapsed - thumbOffset;   // [cross−3, cross−1]
            float expandedCrossPos = cross - bar + 3f;                   // [cross−9, cross−3]
            float crossPos = collapsedCrossPos + (expandedCrossPos - collapsedCrossPos) * expand;
            RectF thumbRect = horizontal
                ? new RectF(pos, crossPos, thumbLen, thick)
                : new RectF(crossPos, pos, thick, thumbLen);

            float arrowOpacity = fade * expand;
            if (arrowOpacity > 0.04f)
            {
                var arrow = thumb with { A = thumb.A * arrowOpacity };
                if (_sbArrowGlyphsSet)
                {
                    // The standalone control's exact arrow anatomy (ScrollBar.ArrowButton): a 12px cell at each rail
                    // end, FontSize 8 (:186), the glyph nudged 4px toward the track (margins :195-198) and centred on
                    // the cross axis — so the overlay scrollbar and the ScrollBar element read as ONE control.
                    const float glyphSize = 8f;
                    float crossCentered = cross - bar + (bar - glyphSize) * 0.5f;
                    RectF decRect = horizontal
                        ? new RectF(4f, crossCentered, glyphSize, glyphSize)
                        : new RectF(crossCentered, 4f, glyphSize, glyphSize);
                    RectF incRect = horizontal
                        ? new RectF(axis - bar, crossCentered, glyphSize, glyphSize)
                        : new RectF(crossCentered, axis - bar, glyphSize, glyphSize);
                    StringId dec = horizontal ? _sbLeftGlyph : _sbUpGlyph;
                    StringId inc = horizontal ? _sbRightGlyph : _sbDownGlyph;
                    railDl.DrawGlyphRun(decRect, arrow, dec, _sbIconFamily, glyphSize, 400, 0, 0, 1, 0f, float.NaN, 0, 0, world, opacity, key | 0x2);
                    railDl.DrawGlyphRun(incRect, arrow, inc, _sbIconFamily, glyphSize, 400, 0, 0, 1, 0f, float.NaN, 0, 0, world, opacity, key | 0x3);
                }
                else if (horizontal)
                {
                    EmitChevron(railDl, new Point2(bar * 0.5f, cross - bar * 0.5f), horizontal: true, positive: false, arrow, world, opacity, key | 0x2);
                    EmitChevron(railDl, new Point2(axis - bar * 0.5f, cross - bar * 0.5f), horizontal: true, positive: true, arrow, world, opacity, key | 0x3);
                }
                else
                {
                    EmitChevron(railDl, new Point2(cross - bar * 0.5f, bar * 0.5f), horizontal: false, positive: false, arrow, world, opacity, key | 0x2);
                    EmitChevron(railDl, new Point2(cross - bar * 0.5f, axis - bar * 0.5f), horizontal: false, positive: true, arrow, world, opacity, key | 0x3);
                }
            }

            // Retained tiles: the THUMB is its own slice, recorded at offset 0 and posed along the track by the
            // composite (SliceRole.Thumb) — a scroll never re-records the viewport for it.
            int thumbSlot = -1;
            if (stats.Slicing && stats.InlineLayerDepth == 0 && stats.Slices is { } sl
                && (thumbSlot = sl.FindOrCreate((int)vpNode.Raw.Index, vpNode.Raw.Gen, SliceRole.Thumb, SliceKind.Effect)) >= 0)
            {
                RectF thumbAt0 = horizontal
                    ? new RectF(trackStart, crossPos, thumbLen, thick)
                    : new RectF(crossPos, trackStart, thick, thumbLen);
                float d = pos - trackStart;
                float lx = horizontal ? d : 0f, ly = horizontal ? 0f : d;
                var tcmd = new CompositeSliceCmd((int)vpNode.Raw.Index, vpNode.Raw.Gen, (int)SliceRole.Thumb,
                    (int)SliceKind.Effect, 0, clip, default, default, default, default, key | 0x1, key | 0x1);
                dl.CompositeSlice(in tcmd, key | 0x1);
                sl.AddChild(stats.CurSlot, thumbSlot);
                sl.SetPose(thumbSlot, SliceRecorder.PoseKind.Thumb, in world, 0f, 0f, Affine2D.Identity,
                    world.M11 * lx + world.M21 * ly, world.M12 * lx + world.M22 * ly);
                sl.SetThumb(thumbSlot, (int)vpNode.Raw.Index, vpNode.Raw.Gen, horizontal, travel, content, viewport);
                sl.SetMarker(thumbSlot, in clip, 0, default, default);
                DrawList tdl = sl.BeginWalk(thumbSlot);
                tdl.FillRoundRect(thumbAt0, CornerRadius4.All(radius), thumbCol, world, opacity, key | 0x1);
                sl.EndWalk(thumbSlot, world.TransformBounds(thumbAt0));
            }
            else railDl.FillRoundRect(thumbRect, CornerRadius4.All(radius), thumbCol, world, opacity, key | 0x1);
            return;
        }
    }

    private const float EdgeCueBandPx = 28f;    // the chevron band's depth along the scroll axis (the chevron sits at its centre)
    private const float EdgeCueRunwayPx = 24f;  // chevron alpha ramps 0→1 over the last Runway px of overflow (the fade's own runway)

    /// <summary>Resolve a node's edge fade: an explicit <c>BoxEl/ScrollEl.EdgeFade</c> spec, or a scroller's
    /// <c>AutoEdgeFade</c> synthesized from its live overflow (feather only the edges with more content past them, the
    /// per-edge band ramped to 0 over the last <c>runway</c> px so it appears/disappears smoothly with the offset).</summary>
    /// <summary>Does <paramref name="node"/> paint anything of its OWN (a fill, a border, a gradient, a shadow) — the
    /// record-time half of an edge fade's distribution test: a fade over a node that paints only its children can be
    /// distributed as an analytic per-item feather (<see cref="CompositeSliceFlags.DistributeFade"/>); one over its own
    /// paint cannot (that paint overlaps its content inside the band). Conservative: placement re-checks the painted
    /// segments of the slice every turn.</summary>
    private static bool HasOwnPaint(SceneRecordingSnapshot scene, NodeHandle node, bool maybeSparsePaint, in NodePaint p)
    {
        switch (p.VisualKind)
        {
            case VisualKind.None: break;
            case VisualKind.Box:
                if (p.Fill.A > 0f || p.HoverFill.A > 0f || p.PressedFill.A > 0f || p.BorderWidth > 0f || p.ValidationBorder.A > 0f) return true;
                if (maybeSparsePaint && scene.TryGetGradient(node, out var g) && g.Stops is { Length: > 0 }) return true;
                break;
            default: return true;
        }
        return maybeSparsePaint && scene.TryGetShadow(node, out var sh) && !sh.IsNone;
    }

    /// <summary>The translate an <see cref="EdgeFadeSpec.OverflowTail"/> cue reads (see its remarks): that of the FIRST
    /// translated node down <paramref name="node"/>'s first-child chain, at most <see cref="EdgeFadeSpec.OverflowChainDepth"/>
    /// deep; the walk stops at a node whose transform is more than a translation. 0 = nothing translated.</summary>
    internal static float OverflowContentDx(SceneRecordingSnapshot scene, NodeHandle node)
    {
        int depth = 0;
        for (var c = scene.FirstChild(node); !c.IsNull && scene.IsLive(c) && depth < EdgeFadeSpec.OverflowChainDepth;
             c = scene.FirstChild(c), depth++)
        {
            Affine2D t = scene.Paint(c).LocalTransform;
            if (t.M11 != 1f || t.M12 != 0f || t.M21 != 0f || t.M22 != 1f) return 0f;
            if (t.Dx != 0f) return t.Dx;
        }
        return 0f;
    }

    private bool TryResolveEdgeFade(SceneRecordingSnapshot scene, NodeHandle node, NodeFlags flags, bool maybeSparsePaint, out EdgeFadeSpec ef)
    {
        if (maybeSparsePaint && scene.TryGetEdgeFade(node, out ef) && !ef.IsNone)                      // explicit, any element
        {
            // An overflow cue reads its content's POSED translate (OverflowContentDx): on the render thread that is this
            // tick's compositor pose, so the fade moves on the same frame as the content it cues (the content's pose change
            // re-walks this node through the ancestor trail).
            if (ef.OverflowTail > 0f)
            {
                ef = ef.ResolveOverflow(OverflowContentDx(scene, node));
                return !ef.IsNone;
            }
            return true;
        }
        if ((flags & NodeFlags.Scrollable) != 0 && scene.TryGetScroll(node, out var sc)
            && sc.AutoEdgeFade && sc.AutoEdgeFadeBand > 0.5f)
        {
            float band = sc.AutoEdgeFadeBand;
            const float runway = 24f;
            EdgeMask edges = EdgeMask.None;
            float bl = 0f, bt = 0f, br = 0f, bb = 0f;
            float shown = ShownOffset(scene, node, in sc);
            if (sc.Orientation == 1)   // horizontal
            {
                if (shown > 0.5f) { edges |= EdgeMask.Left; bl = band * Math.Clamp(shown / runway, 0f, 1f); }
                float pastR = sc.ContentW - (shown + sc.ViewportW);
                if (pastR > 0.5f) { edges |= EdgeMask.Right; br = band * Math.Clamp(pastR / runway, 0f, 1f); }
            }
            else                       // vertical
            {
                if (shown > 0.5f) { edges |= EdgeMask.Top; bt = band * Math.Clamp(shown / runway, 0f, 1f); }
                float pastB = sc.ContentH - (shown + sc.ViewportH);
                if (pastB > 0.5f) { edges |= EdgeMask.Bottom; bb = band * Math.Clamp(pastB / runway, 0f, 1f); }
            }
            if (edges != EdgeMask.None) { ef = new EdgeFadeSpec(edges, bl, bt, br, bb); return true; }
        }
        ef = default;
        return false;
    }

    /// <summary>The edge-cue CHEVRONS (<see cref="ScrollEdgeCues.FadeAndChevron"/>): a small directional glyph centred in
    /// the band at each scroll edge with more content past it, its alpha ramped with how far past the edge the content runs
    /// (<see cref="EdgeCueRunwayPx"/>), read straight off <see cref="ScrollState"/> — zero new scene nodes, zero managed
    /// allocation. The cue's fade is the viewport's analytic feather, not paint. controls.md §8.3.</summary>
    private void EmitScrollEdgeChevrons(DrawList dl, in RectF b, in ScrollState sc, float offset, in Affine2D world, float opacity,
        ulong key, ColorF chevron)
    {
        bool horizontal = sc.Orientation == 1;
        float content = horizontal ? sc.ContentW : sc.ContentH;
        float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        if (content <= viewport + 0.5f) return;                       // fits — no overflow at either edge

        float axis = horizontal ? b.W : b.H;
        float band = MathF.Min(EdgeCueBandPx, axis * 0.5f);           // never let the two bands meet on a tiny viewport
        if (band <= 0.5f) return;
        const float chevHalf = 4.0f;                                  // ~8px chevron

        float aBefore = Math.Clamp(offset / EdgeCueRunwayPx, 0f, 1f);
        if (aBefore > 0.01f)
        {
            Point2 cc = horizontal ? new Point2(band * 0.5f, b.H * 0.5f) : new Point2(b.W * 0.5f, band * 0.5f);
            EmitChevron(dl, cc, horizontal, positive: false, chevron with { A = chevron.A * aBefore }, world, opacity, key | 0x1, chevHalf);
        }
        float aAfter = Math.Clamp((content - (offset + viewport)) / EdgeCueRunwayPx, 0f, 1f);
        if (aAfter > 0.01f)
        {
            Point2 cc = horizontal ? new Point2(b.W - band * 0.5f, b.H * 0.5f) : new Point2(b.W * 0.5f, b.H - band * 0.5f);
            EmitChevron(dl, cc, horizontal, positive: true, chevron with { A = chevron.A * aAfter }, world, opacity, key | 0x5, chevHalf);
        }
    }

    private void EmitChevron(DrawList dl, Point2 c, bool horizontal, bool positive, ColorF color, in Affine2D world, float opacity, ulong key, float size = 3.0f)
    {
        float s = size;
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

    private void EmitSegment(DrawList dl, Point2 a, Point2 b, ColorF color, in Affine2D world, float opacity, ulong key)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= 0.1f) return;

        const float thickness = 1.15f;
        var center = new Point2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
        var line = new RectF(-len * 0.5f, -thickness * 0.5f, len, thickness);
        var transform = world.Translate(center.X, center.Y)
                             .Multiply(Affine2D.Rotation(MathF.Atan2(dy, dx)));
        dl.FillRoundRect(line, CornerRadius4.All(thickness * 0.5f), color, transform, opacity, key);
    }

    private ColorF Lighten(ColorF c, float t) => new(c.R + (1f - c.R) * t, c.G + (1f - c.G) * t, c.B + (1f - c.B) * t, c.A);
    private ColorF Darken(ColorF c, float t) => new(c.R * (1f - t), c.G * (1f - t), c.B * (1f - t), c.A);
}
