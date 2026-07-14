using FluentGpu.Foundation;
using FluentGpu.Scene;

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

public readonly record struct SceneRecordStats(int NodesVisited, int DrawnNodeCount, int CulledNodeCount, RectF Damage = default)
{
    public int NodesCulled { get; init; }
    public int BlurCandidateCount { get; init; }
    public int BlurGroupCount { get; init; }
    public int BlurSuppressedByScrollCount { get; init; }
    public int BlurHoldCandidateCount { get; init; }
    public int EdgeFadeGroupCount { get; init; }
    public int SpansReused { get; init; }
    public int SpansRebased { get; init; }
    public int SpansReRecorded { get; init; }
    public int SpanBytesCopied { get; init; }
    public SpanReuseDisabledReason SpanReuseDisabledReasons { get; init; }
    /// <summary>Spatial reuse-scoping (scene-memory.md): how many ancestor-chain nodes were span-reuse-BLOCKED this
    /// frame (popup/overlay/orphan/fly chains). &gt; 0 means a scoped block was active WITHOUT the old whole-tree kill.</summary>
    public int ScopedBlocks { get; init; }
}

/// <summary>
/// Phase 8 (record): walks the retained SceneStore and emits the DrawList. Composites like a browser — each node's
/// geometry is emitted in LOCAL space with a world transform (parent ∘ translate ∘ LocalTransform, scale/rotate about
/// the node's center) and a cumulative opacity, so transform/opacity animate without relayout or re-record of content.
/// Hover/press cross-fade via the eased <see cref="InteractionAnim"/> row; a focused node gets a dual-stroke focus ring.
/// </summary>
public static class SceneRecorder
{
    private const bool EnableSubtreeCull = true;

    // Opt-in scroll diagnostics: set FG_SCROLLLOG=1, run, scroll, copy the [scroll] lines.
    private static readonly bool ScrollLog = Environment.GetEnvironmentVariable("FG_SCROLLLOG") == "1";
    private static int _scrollLogFrame;
    private static bool ScrollLogNow => ScrollLog && (_scrollLogFrame % 45) == 0;


    // Overlay-scrollbar arrow glyphs: the host pre-interns the four Segoe Fluent arrow chars + the icon family once
    // at startup (AppHost ctor) so EmitScrollbar draws the SAME solid-triangle glyphs as the standalone ScrollBar
    // control (ScrollBar_themeresources.xaml :387/:344/:301/:258) — one scrollbar visual language, not two. A host
    // that never configures them (bare recorder tests) falls back to the stroked-chevron primitive.
    private static StringId _sbUpGlyph, _sbDownGlyph, _sbLeftGlyph, _sbRightGlyph, _sbIconFamily;
    private static bool _sbArrowGlyphsSet;

    public static void ConfigureScrollbarArrowGlyphs(StringId up, StringId down, StringId left, StringId right, StringId iconFamily)
    {
        _sbUpGlyph = up; _sbDownGlyph = down; _sbLeftGlyph = left; _sbRightGlyph = right; _sbIconFamily = iconFamily;
        _sbArrowGlyphsSet = true;
    }

    // ── Per-frame damage ENTRY pool (design §2.3 / E9 own-subtree carve-out) ────────────────────────────────────────
    // The single frame-damage UNION (RecordAccumulator.Damage) is AUGMENTED with an ordered list of individual damage
    // entries so a cached acrylic layer can exclude the damage its OWN subtree emitted (drawn ON TOP of its snapshot, so
    // it can never invalidate it). Entries append in Walk emission order; a layer's own-subtree entries are the
    // contiguous range [entryCountAtPush, entryCountAtPop). Fixed capacity (no growth in the record hot phase); overflow
    // ⇒ the compositor falls back to the whole-frame union (no carve-out, the prior safe behavior). UI-thread-only static
    // scratch (record is single-threaded); reset at the top of Record, consumed by PatchDamageRanges before Record returns.
    private const int DamageEntryCap = 32;
    private static readonly RectF[] _dmgEntries = new RectF[DamageEntryCap];
    private static int _dmgEntryCount;
    private static bool _dmgOverflow;

    private struct AcrylicDamageRange { public int PushByteOffset; public int Start; public int End; }
    private const int AcrylicRangeCap = 32;
    private static readonly AcrylicDamageRange[] _acrylicRanges = new AcrylicDamageRange[AcrylicRangeCap];
    private static int _acrylicRangeCount;

    private static void ResetDamageEntries()
    {
        _dmgEntryCount = 0; _dmgOverflow = false; _acrylicRangeCount = 0;
    }

    // Register a freshly-walked cached-acrylic layer's PushLayer byte offset + its own-subtree entry-range start.
    // Returns the range slot index (or -1 on range-pool overflow ⇒ this layer is left unpatched ⇒ union fallback).
    private static int OpenAcrylicRange(int pushByteOffset, int start)
    {
        if (_acrylicRangeCount >= AcrylicRangeCap) return -1;
        int idx = _acrylicRangeCount++;
        _acrylicRanges[idx] = new AcrylicDamageRange { PushByteOffset = pushByteOffset, Start = start, End = start };
        return idx;
    }

    // Post-walk: bake each tracked cached-acrylic layer's EXTERNAL damage rect (union of entries OUTSIDE its own subtree)
    // + the frame epoch into its PushLayerCmd, so the compositor tests only genuine behind-the-layer damage (§2.3/E9).
    private static void PatchDamageRanges(DrawList dl, in RectF fullUnion, ulong damageEpoch)
    {
        for (int i = 0; i < _acrylicRangeCount; i++)
        {
            ref var r = ref _acrylicRanges[i];
            RectF external = AcrylicBackdropMath.ExternalDamageUnion(_dmgEntries, _dmgEntryCount, r.Start, r.End, _dmgOverflow, in fullUnion);
            dl.PatchLayerExternalDamage(r.PushByteOffset, in external, damageEpoch);
        }
    }

    private struct RecordAccumulator
    {
        public int NodesVisited;
        public int DrawnNodeCount;
        public int CulledNodeCount;
        public int NodesCulled;
        public int BlurCandidateCount;
        public int BlurGroupCount;
        public int BlurHoldCandidateCount;
        public int EdgeFadeGroupCount;
        public int SpansReused;
        public int SpansRebased;
        public int SpansReRecorded;
        public int SpanBytesCopied;
        public int ScopedBlocks;
        public SpanReuseDisabledReason SpanReuseDisabledReasons;
        public RectF Damage;       // union of this frame's changed-node device bounds → the acrylic backdrop-cache damage region
        public bool HasDamage;

        // Union a changed node's device bounds into the frame damage region (region-aware acrylic invalidation).
        public void AddDamage(in RectF r)
        {
            if (r.W <= 0f || r.H <= 0f) return;
            // Record the individual entry (own-subtree carve-out, §2.3/E9) as well as unioning it into the frame damage.
            if (_dmgEntryCount < DamageEntryCap) _dmgEntries[_dmgEntryCount++] = r;
            else _dmgOverflow = true;
            if (!HasDamage) { Damage = r; HasDamage = true; return; }
            float x0 = MathF.Min(Damage.X, r.X), y0 = MathF.Min(Damage.Y, r.Y);
            float x1 = MathF.Max(Damage.X + Damage.W, r.X + r.W), y1 = MathF.Max(Damage.Y + Damage.H, r.Y + r.H);
            Damage = new RectF(x0, y0, x1 - x0, y1 - y0);
        }

        public readonly SceneRecordStats ToStats() => new(NodesVisited, DrawnNodeCount, CulledNodeCount, HasDamage ? Damage : default)
        {
            NodesCulled = this.NodesCulled,
            BlurCandidateCount = this.BlurCandidateCount,
            BlurGroupCount = this.BlurGroupCount,
            BlurSuppressedByScrollCount = 0,
            BlurHoldCandidateCount = this.BlurHoldCandidateCount,
            EdgeFadeGroupCount = this.EdgeFadeGroupCount,
            SpansReused = this.SpansReused,
            SpansRebased = this.SpansRebased,
            SpansReRecorded = this.SpansReRecorded,
            SpanBytesCopied = this.SpanBytesCopied,
            ScopedBlocks = this.ScopedBlocks,
            SpanReuseDisabledReasons = this.SpanReuseDisabledReasons,
        };
    }

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

    private readonly struct InheritedState
    {
        public readonly float HoverT;
        public readonly float PressT;
        public readonly NodeFlags InteractiveFlags;
        public readonly byte HasProgress;
        public readonly byte Disabled;

        public InheritedState(float hoverT, float pressT, NodeFlags interactiveFlags, byte hasProgress, byte disabled)
        {
            HoverT = hoverT;
            PressT = pressT;
            InteractiveFlags = interactiveFlags;
            HasProgress = hasProgress;
            Disabled = disabled;
        }

        public readonly InheritedState ForChild(NodeFlags flags, bool nodeInteractive, bool hasLocalProgress, float localHoverT, float localPressT)
        {
            NodeFlags interactiveFlags = nodeInteractive ? flags : InteractiveFlags;
            byte disabled = Disabled != 0 || (flags & NodeFlags.Disabled) != 0 ? (byte)1 : (byte)0;
            if (nodeInteractive && hasLocalProgress)
                return new InheritedState(localHoverT, localPressT, interactiveFlags, 1, disabled);
            return new InheritedState(HoverT, PressT, interactiveFlags, HasProgress, disabled);
        }
    }

    /// <param name="skipRoots">Subtree roots EXCLUDED from this record pass — out-of-bounds popup wrappers that render
    /// into their own popup window instead (E4 windowed popups; see <see cref="RecordSubtree"/>). The subtrees stay in
    /// the one SceneStore (layout/hit-test unchanged) — only their pixels move to the popup window's DrawList.</param>
    public static SceneRecordStats Record(SceneStore scene, DrawList dl, ImageCache? images = null, in FocusVisualStyle focus = default,
                                          ColorF scrollThumb = default, ColorF scrollTrack = default, in TextEditStyle textEdit = default,
                                          ReadOnlySpan<NodeHandle> skipRoots = default, bool holdSelfBlurForAnyUserScroll = false,
                                          SpanTable? spans = null,
                                          SpanReuseDisabledReason spanReuseDisabled = SpanReuseDisabledReason.None,
                                          ReadOnlySpan<RectF> pendingStructuralDamage = default,
                                          ulong damageEpoch = 0,
                                          ReadOnlySpan<NodeHandle> reuseBlockRoots = default)
    {
        if (spans is null) dl.Reset();
        else dl.SwapAndReset();
        if (scene.Root.IsNull) return default;

        _scrollLogFrame++;
        ResetDamageEntries();
        var stats = new RecordAccumulator();
        // Seed the frame damage with any band a structural-track CANCEL vacated this frame (AnimEngine.PendingStructuralDamage):
        // when a suppressed/resized FLIP or Reveal is snapped to its final bounds, the node stops covering the extent it drew
        // at last frame, but no node re-touches that band — so the region-aware acrylic/backdrop cache would freeze last
        // frame's pixels there (the "ghost rail"). Unioning the last-presented rects into the damage forces a repaint of the
        // vacated region. AddDamage handles the first-rect/empty case; the host drains the list after this record.
        for (int i = 0; i < pendingStructuralDamage.Length; i++)
            stats.AddDamage(pendingStructuralDamage[i]);
        uint spanFrame = spans?.BeginFrame(scene.Capacity) ?? 0;
        SpanReuseDisabledReason disabledReasons = spanReuseDisabled;
        if (spans is not null && !spans.HasPrior) disabledReasons |= SpanReuseDisabledReason.FirstRecord;
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
        int overlayCount = scene.OverlayCount;
        if (hasGhost) disabledReasons |= SpanReuseDisabledReason.DragGhost;   // DragGhost stays GLOBAL this wave (drags are rare; scope later)
        if (overlayCount != 0) disabledReasons |= SpanReuseDisabledReason.Overlays;
        if (scene.OrphanCount != 0) disabledReasons |= SpanReuseDisabledReason.Orphans;
        if (!reuseBlockRoots.IsEmpty) disabledReasons |= SpanReuseDisabledReason.Detached;
        // Only these force the WHOLE-canvas reuse off; the scoped reasons above are handled per-chain by BlockSpecials.
        const SpanReuseDisabledReason GlobalReuseKill =
            SpanReuseDisabledReason.FirstRecord | SpanReuseDisabledReason.SceneChanged | SpanReuseDisabledReason.Layout |
            SpanReuseDisabledReason.Resize | SpanReuseDisabledReason.ModalPaint | SpanReuseDisabledReason.DragGhost |
            SpanReuseDisabledReason.ImageContent;
        bool spanReuseOff = spans is null || (disabledReasons & GlobalReuseKill) != 0;
        // The span STORE stays alive under every scoped reason (blocked nodes self-gate via IsBlocked, so the off-screen
        // cull survives for the unblocked rest of the tree); only DragGhost (still global) suppresses the store wholesale.
        bool spanStoreOn = spans is not null && (disabledReasons & SpanReuseDisabledReason.DragGhost) == 0;
        stats.SpanReuseDisabledReasons = disabledReasons;
        Span<NodeHandle> skips = stackalloc NodeHandle[skipRoots.Length + 1 + overlayCount];
        skipRoots.CopyTo(skips);
        int skipCount = skipRoots.Length;
        if (hasGhost) skips[skipCount++] = ghost;
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
        // orphan's visual parent, and each connected-anim fly anchor (reuseBlockRoots). Only meaningful while span reuse is
        // globally alive (a global kill already re-records everything). O(specials × depth), alloc-free.
        if (spans is not null && !spanReuseOff && spanStoreOn)
            BlockSpecials(scene, spans, spanFrame, skipRoots, overlayCount, reuseBlockRoots, ref stats);

        Walk(scene, dl, images, scene.Root, Affine2D.Identity, 1f, 0, RectF.Infinite, in focus, in textEdit, scrollThumb, scrollTrack,
            1f, 1f, false, holdSelfBlurForAnyUserScroll, default, skips[..skipCount], spans, spanFrame, spanReuseOff, spanStoreOn, ref stats);

        // Defensive rootless-orphan fallback. Normal exits replay inside their former parent's Walk (preserving ancestor
        // clips/layers, painter order, and popup-window routing); only nodes that genuinely had no visual parent land here.
        for (int i = 0; i < scene.OrphanCount; i++)
        {
            if (!scene.OrphanVisualParentAt(i).IsNull) continue;   // replayed inside the former parent's Walk
            var o = scene.OrphanAt(i, out float px, out float py);
            if ((scene.Flags(o) & NodeFlags.ConnectedOverlay) != 0) continue;   // an overlay-flagged orphan draws in the top band, not here
            Walk(scene, dl, images, o, Affine2D.Translation(px, py), 1f, 0, RectF.Infinite, in focus, in textEdit, scrollThumb, scrollTrack,
                1f, 1f, false, holdSelfBlurForAnyUserScroll, default, skipRoots, null, 0, true, false, ref stats);
        }

        // E5 drag-ghost top band: walk the ghost subtree at its LIVE parent-world origin (scroll / animated ancestor
        // translations included — AbsoluteRect minus the node's own bounds offset and translate; Walk re-applies
        // both) with an INFINITE clip. Emitted last ⇒ the painter draws it over the whole frame.
        if (hasGhost)
        {
            var abs = scene.AbsoluteRect(ghost);
            ref RectF gb = ref scene.Bounds(ghost);
            ref NodePaint gp = ref scene.Paint(ghost);
            Walk(scene, dl, images, ghost,
                 Affine2D.Translation(abs.X - gb.X - gp.LocalTransform.Dx, abs.Y - gb.Y - gp.LocalTransform.Dy),
                 1f, 1 << 16, RectF.Infinite, in focus, in textEdit, scrollThumb, scrollTrack,
                 1f, 1f, false, holdSelfBlurForAnyUserScroll, default, default, null, 0, true, false, ref stats);
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
            ref RectF ob = ref scene.Bounds(ov);
            ref NodePaint op = ref scene.Paint(ov);
            Walk(scene, dl, images, ov,
                 Affine2D.Translation(abs.X - ob.X - op.LocalTransform.Dx, abs.Y - ob.Y - op.LocalTransform.Dy),
                 1f, (1 << 16) | 1, overlayClip, in focus, in textEdit, scrollThumb, scrollTrack,
                 1f, 1f, false, holdSelfBlurForAnyUserScroll, default, default, null, 0, true, false, ref stats);
        }
        // E9 own-subtree carve-out: now that every entry + every cached-acrylic own-subtree range is known, bake each
        // layer's EXTERNAL damage rect + this frame's epoch into its PushLayerCmd (before the DrawList is published).
        PatchDamageRanges(dl, stats.HasDamage ? stats.Damage : default, damageEpoch);
        return stats.ToStats();
    }

    /// <summary>Draw the <see cref="FluentGpu.Animation.DetachedAnimSlab"/>'s live snapshots (the connected-animation
    /// Hero fly; presence exits) in the top band — the rework's replacement for the live-overlay Walk. Each row draws
    /// from its VALUE snapshot (no live node): the world/opacity/corners/image are baked by
    /// <c>ConnectedAnimation.SyncDetached</c>; fit + image readiness resolve here exactly like the main-pass image case.
    /// Emitted LAST (after <see cref="Record"/>) so it paints above all content; clipped to <paramref name="clip"/>
    /// (a content-region rect; <c>RectF.Infinite</c> ⇒ unbounded).</summary>
    public static void RecordDetached(SceneStore scene, DrawList dl, ImageCache? images, FluentGpu.Animation.DetachedAnimSlab detached, RectF clip)
    {
        if (detached.Count == 0) return;
        bool clipped = clip.W < 1e7f && clip.H < 1e7f;   // a finite content-region clip (RectF.Infinite ⇒ unbounded fly)
        if (clipped) dl.PushClip(clip);
        int n = detached.NodeCount;
        for (int s = 0; s < n; s++)
        {
            ref FluentGpu.Animation.DetachedNode d = ref detached.At(s);
            if (!d.InUse || (VisualKind)d.Kind != VisualKind.Image || d.ImageId == 0) continue;
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
            dl.DrawImage(drawRect, d.Corners, d.ImageId, ready, d.Fill, d.WorldTransform, d.Opacity, uv, fadeStart, fadeDur, fadeEase, key);
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
    public static SceneRecordStats RecordSubtree(SceneStore scene, DrawList dl, ImageCache? images, in FocusVisualStyle focus,
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
        var stats = new RecordAccumulator();
        Walk(scene, dl, images, root, Affine2D.Translation(pax - originDip.X, pay - originDip.Y), 1f, 0, RectF.Infinite,
             in focus, in textEdit, scrollThumb, scrollTrack, 1f, 1f, false, false, default, default, null, 0, true, false, ref stats);
        return stats.ToStats();
    }

    private static bool ContainsNode(ReadOnlySpan<NodeHandle> roots, NodeHandle node)
    {
        for (int i = 0; i < roots.Length; i++)
            if (roots[i] == node) return true;
        return false;
    }

    /// <summary>True when <paramref name="node"/> is inside any skip-root subtree (a windowed-popup wrapper) — its
    /// pixels belong to that popup's own pass, never the main window.</summary>
    private static bool UnderAnySkipRoot(SceneStore scene, ReadOnlySpan<NodeHandle> roots, NodeHandle node)
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
    private static void BlockSpecials(SceneStore scene, SpanTable spans, uint frame,
        ReadOnlySpan<NodeHandle> skipRoots, int overlayCount, ReadOnlySpan<NodeHandle> reuseBlockRoots, ref RecordAccumulator stats)
    {
        for (int i = 0; i < skipRoots.Length; i++) BlockChain(scene, spans, frame, skipRoots[i], ref stats);
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
    private static void BlockChain(SceneStore scene, SpanTable spans, uint frame, NodeHandle start, ref RecordAccumulator stats)
    {
        for (var n = start; !n.IsNull; n = scene.Parent(n))
        {
            int idx = (int)n.Raw.Index;
            if (spans.IsBlocked(idx, frame)) break;
            spans.MarkBlocked(idx, frame);
            stats.ScopedBlocks++;
        }
    }

    private static SpanRecordResult Walk(SceneStore scene, DrawList dl, ImageCache? images, NodeHandle node, Affine2D parentWorld, float parentOpacity,
                                         int depth, RectF clip, in FocusVisualStyle focus, in TextEditStyle textEdit, ColorF scrollThumb, ColorF scrollTrack,
                                         float parentScaleX, float parentScaleY, bool parentInMotion, bool parentScrollInMotion, InheritedState inherited,
                                         ReadOnlySpan<NodeHandle> skipRoots, SpanTable? spans, uint spanFrame, bool spanReuseDisabled, bool spanStoreEnabled,
                                         ref RecordAccumulator stats)
    {
        if (!skipRoots.IsEmpty && ContainsNode(skipRoots, node)) return default;   // subtree renders in its own popup window
        NodeFlags flags = scene.Flags(node);
        if ((flags & NodeFlags.Visible) == 0) return default;   // invisible subtree contributes nothing
        stats.NodesVisited++;
        bool maybeSparsePaint = (flags & NodeFlags.SparsePaint) != 0;
        bool hasInteractionAnim = (flags & NodeFlags.InteractionAnim) != 0;
        ref InteractionInfo interaction = ref scene.Interaction(node);
        const int interactiveMask = InteractionInfo.ClickBit | InteractionInfo.PointerBit;
        bool nodeInteractive = (interaction.HandlerMask & interactiveMask) != 0;
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

        // Motion gate for glyph snapping: a transform write this frame (scroll/fling/drag/FLIP/sticky — every motion
        // writer marks TransformDirty; the host clears the bits right after record) means this subtree is mid-motion.
        // Its glyph runs skip the device-grid baseline snap and ride sub-pixel WITH their plates (no 1px shear against
        // the smoothly-translating fill), then re-snap crisp on the settle frame the host queues after the last write.
        bool inMotion = parentInMotion || (flags & NodeFlags.TransformDirty) != 0;
        // parentScrollInMotion may be seeded globally by the host when ANY viewport is in real user scroll motion. This
        // makes stationary sibling self-blurs (lyrics DoF) HOLD under their cache policy during user scrolling (their pin
        // serves the translated strip) rather than re-Gaussian every forced submit, so scroll input gets the GPU.
        // Scroll-defer: a self-blur inside a viewport being actively USER-scrolled (wheel/fling/drag) this frame is HELD
        // (its BlurCachePolicy governs a miss: crisp-on-miss or skip-on-miss), NOT re-blurred — the content is translating,
        // so re-running both Gaussian passes on every forced submit (a scroll defeats skip-submit) is wasted; the
        // position-INDEPENDENT pin key HITS the moving (translated) rect, so the DoF stays visible without a re-blur. The
        // host can seed this globally so sibling effects are deferred during the same user scroll.
        // Keys off ScrollState.UserScrollActive (ScrollIntegrator.Tick sets it = movingNow && !PhaseProgrammatic), NOT
        // the raw Phase. The auto-scroll bring-into-view ease KEEPS the DoF (PhaseProgrammatic ⇒ not user); crucially so
        // do its SETTLE frame (settled ⇒ movingNow false, though the Phase has already flipped to Idle while still
        // emitting that tick's final content-transform write) and a stationary relayout that re-asserts the content
        // transform — both left UserScrollActive false. Gating on the raw Phase dropped the blur on those frames, so the
        // whole panel's DoF visibly vanished for one frame on every line change. The content-node TransformDirty guard
        // confirms the content actually translated this frame (only ScrollIntegrator/Input write that), not scale/blur.
        bool scrollInMotion = parentScrollInMotion;
        if (!scrollInMotion && (flags & NodeFlags.Scrollable) != 0 && scene.HasScroll(node))
        {
            ref var scrollState = ref scene.ScrollRef(node);
            var scrollContent = scrollState.ContentNode;
            if (!scrollContent.IsNull && scene.IsLive(scrollContent)
                && (scene.Flags(scrollContent) & NodeFlags.TransformDirty) != 0)
            {
                // Content translated this frame — descendants must re-record. UserScrollActive still gates blur-hold
                // (holdBlur below); span reuse is blocked for ANY offset write (wheel, fling, programmatic bring-into-view).
                scrollInMotion = true;
            }
            else if (scrollState.UserScrollActive)
            {
                // Offset unchanged but still coasting — keep blur-hold without forcing a full re-record.
                scrollInMotion = true;
            }
        }

        ref RectF b = ref scene.Bounds(node);
        ref NodePaint p = ref scene.Paint(node);

        // node-local → device: parent ∘ translate(node pos) ∘ (local transform about the node's transform-origin)
        Affine2D world = parentWorld.Translate(b.X, b.Y);
        float ox = b.W * p.OriginX, oy = b.H * p.OriginY;   // transform origin (default centre; e.g. top edge for a menu unfold)
        if (!p.LocalTransform.IsIdentity)
            world = world.Translate(ox, oy).Multiply(p.LocalTransform).Translate(-ox, -oy);
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
        float childScaleX = netScaleX * p.LocalTransform.M11;   // the scale this node imposes on its children
        float childScaleY = netScaleY * p.LocalTransform.M22;

        float opacity = parentOpacity * ResolveOpacity(flags, in p, in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT);

        // Presented extent (layout-transition "Reveal"): the node's own fill + its child clip are drawn at PresentedW/H
        // when set (which may exceed the model bounds during a shrink), while layout/hit-test keep the model Bounds.
        float pw = float.IsNaN(p.PresentedW) ? b.W : p.PresentedW;
        float ph = float.IsNaN(p.PresentedH) ? b.H : p.PresentedH;
        var pb = new RectF(b.X, b.Y, pw, ph);   // presented rect (== b when no reveal in flight)

        ulong key = (ulong)depth << 32;   // painter order ~ depth for the slice
        ulong layerId = ((ulong)node.Raw.Index << 32) | node.Raw.Gen;
        var local = new RectF(0f, 0f, pw, ph);
        var deviceBounds = world.TransformBounds(local);
        bool overlapsClip = deviceBounds.Overlaps(clip);
        var result = new SpanRecordResult();
        result.Include(deviceBounds);

        ulong spanInputSig = 0;
        ulong spanMoveSig = 0;
        int spanByteStart = 0, spanSortStart = 0, spanCommandStart = 0;
        DrawListOpcodeStats spanOpcodeStart = default;
        // Spatial reuse-scoping: this node is on the ancestor chain of a special-cased visual (popup/overlay/orphan/fly),
        // so its stored bytes could be stale. Deny reuse AND skip the store (the not-store-while-blocked safety property) —
        // its children that are NOT on a blocked chain still reuse/store normally, so only the chain re-records.
        bool blocked = spans is not null && spans.IsBlocked((int)node.Raw.Index, spanFrame);
        bool spanTracking = spans is not null && spanStoreEnabled && !blocked;
        if (spans is not null)
        {
            byte recordDirtyBits = scene.RecordDirtyBits(node);
            byte descendantDirtyBits = scene.RecordDirtyDescendantBits(node);
            bool directMovingScrollContent = IsDirectMovingScrollContent(scene, node, flags);

            // Off-screen clean-subtree cull — valid under any spanReuseDisabled reason, but NOT while blocked (StoreCulled
            // would poison a chain node's future frame with a span that omits the special).
            // GUARD: the prior subtree bounds are trusted only when the node's CURRENT device box is ALSO out of view —
            // a reveal/reflow animation (the right rail opening) re-lays a subtree without record-dirty bits, so the
            // stored bounds go stale; culling on them alone blanks the freshly-revealed content for several frames
            // (the "rail background only shows a couple frames later" report).
            if (EnableSubtreeCull && spanStoreEnabled && !blocked
                && recordDirtyBits == 0 && descendantDirtyBits == 0
                && !deviceBounds.Overlaps(clip)
                && spans.TryGetSubtree((int)node.Raw.Index, node.Raw.Gen, spanFrame, out var priorWorldC, out var priorSubtreeC)
                && TryTranslationDelta(in priorWorldC, in world, out float cdx, out float cdy))
            {
                RectF curSubtree = TranslateBounds(priorSubtreeC, cdx, cdy);
                if (!curSubtree.Overlaps(clip))
                {
                    spans.StoreCulled((int)node.Raw.Index, node.Raw.Gen, spanFrame, world, curSubtree);
                    stats.NodesCulled++;
                    var culled = new SpanRecordResult();
                    culled.Include(curSubtree);
                    return culled;
                }
            }

            spanInputSig = ComputeSpanInputSig(scene, node, flags, depth, in clip, in world, opacity,
                parentScaleX, parentScaleY, childScaleX, childScaleY, pw, ph,
                inMotion, scrollInMotion,
                inherited, in focus, in textEdit, scrollThumb, scrollTrack);
            spanMoveSig = ComputeSpanMoveSig(scene, node, flags, depth, in clip, in world, opacity,
                parentScaleX, parentScaleY, childScaleX, childScaleY, pw, ph,
                scrollInMotion, inherited, in focus, in textEdit, scrollThumb, scrollTrack);
            // Span reuse copies a prior frame's recorded subtree byte-for-byte. Exact copy needs a fully clean subtree.
            // Translated copy is allowed only when no descendant is dirty and the old/current clip both contain the
            // whole span, so a moving scroll-content boundary re-walks edge/entering rows instead of freezing them.
            if (!spanReuseDisabled && !blocked && !scrollInMotion && recordDirtyBits == 0
                && spans.TryGet((int)node.Raw.Index, node.Raw.Gen, spanFrame, spanInputSig, out var span)
                && span.ClipComplete
                && IsClipComplete(span.SubtreeBounds, in clip)
                && dl.CanCopyPriorSpan(span.ByteStart, span.ByteLength, span.SortStart, span.SortCount))
            {
                int copiedByteStart = dl.BytePosition;
                int copiedSortStart = dl.SortPosition;
                var copiedStats = span.OpcodeStats;
                dl.CopySpanFromPrior(span.ByteStart, span.ByteLength, span.SortStart, span.SortCount,
                    span.CommandCount, in copiedStats);
                var currentSpan = span with { ByteStart = copiedByteStart, SortStart = copiedSortStart, World = world };
                spans.Store((int)node.Raw.Index, node.Raw.Gen, spanFrame, spanInputSig, spanMoveSig, in currentSpan);
                stats.SpansReused++;
                stats.SpanBytesCopied += span.ByteLength;
                var copiedResult = new SpanRecordResult();
                copiedResult.Include(span.SubtreeBounds);
                return copiedResult;
            }
            if (!spanReuseDisabled && !blocked
                && descendantDirtyBits == 0
                && !directMovingScrollContent
                && (recordDirtyBits & SceneStore.RecordDirtyContent) == 0
                && spans.TryGetTranslated((int)node.Raw.Index, node.Raw.Gen, spanFrame, spanMoveSig, out span))
            {
                var priorWorld = span.World;
                var copiedStats = span.OpcodeStats;
                int copiedByteStart = dl.BytePosition;
                int copiedSortStart = dl.SortPosition;
                if (TryTranslationDelta(in priorWorld, in world, out float dx, out float dy)
                    && span.ClipComplete
                    && IsClipComplete(TranslateBounds(span.SubtreeBounds, dx, dy), in clip)
                    && dl.CopySpanFromPriorTranslated(span.ByteStart, span.ByteLength, span.SortStart, span.SortCount,
                        span.CommandCount, in copiedStats, dx, dy))
                {
                    RectF translatedBounds = TranslateBounds(span.SubtreeBounds, dx, dy);
                    var currentSpan = span with { ByteStart = copiedByteStart, SortStart = copiedSortStart, World = world, SubtreeBounds = translatedBounds, ClipComplete = true };
                    spans.Store((int)node.Raw.Index, node.Raw.Gen, spanFrame, spanInputSig, spanMoveSig, in currentSpan);
                    if ((flags & NodeFlags.TransformDirty) != 0)
                    {
                        var dmgParent = scene.Parent(node);
                        if (dmgParent.IsNull || (scene.Flags(dmgParent) & NodeFlags.Scrollable) == 0)
                            stats.AddDamage(deviceBounds);
                    }
                    stats.SpansReused++;
                    stats.SpansRebased++;
                    stats.SpanBytesCopied += span.ByteLength;
                    var translatedResult = new SpanRecordResult();
                    translatedResult.Include(translatedBounds);
                    return translatedResult;
                }
            }

            if (spanTracking)
            {
                spanByteStart = dl.BytePosition;
                spanSortStart = dl.SortPosition;
                spanCommandStart = dl.CommandCount;
                spanOpcodeStart = dl.OpcodeStats;
            }
        }

        // Backdrop damage (region-aware acrylic cache): a node whose TRANSFORM moved this frame changes what an acrylic
        // layer would blur, so union its device bounds into the frame damage — EXCEPT a scroll viewport's own content
        // (it draws OVER the backdrop; including it would re-blur the popup's own backdrop on every in-popup scroll
        // frame). Paint-/layout-only changes behind a STATIONARY overlay aren't tracked (PaintDirty is sticky,
        // LayoutDirty clears pre-record) — they refresh on the next motion/re-open (design §2.3 "v1 at rest"). The player
        // bar's bottom-anchored ambient motion is correctly ignored for a top popup by the region intersection.
        if ((flags & NodeFlags.TransformDirty) != 0)
        {
            var dmgParent = scene.Parent(node);
            if (dmgParent.IsNull || (scene.Flags(dmgParent) & NodeFlags.Scrollable) == 0)
                stats.AddDamage(deviceBounds);
        }

        // ── flat opacity group (NodePaint.OpacityGroup, WinUI Composition LayerVisual semantics): the subtree renders
        // at FULL alpha into a pooled offscreen RT and composites ONCE at the group alpha — overlapping children
        // (a fading dialog's plate + buttons, a stacked badge) don't double-blend. The cumulative opacity resets to 1
        // for everything this node emits (self, children, border, focus ring, scrollbar — all inside the group); the
        // PushLayer carries the would-be cumulative alpha as GroupAlpha. Skipped at alpha ≈ 1 (composite would be a
        // no-op) and ≈ 0 (invisible — but still walked, matching the non-group path's behavior for hit/anim state). ──
        // A self-blur group (NodePaint.BlurSigma, the Expressive Motion Kit) takes precedence: the subtree renders at
        // FULL alpha into a pooled offscreen RT, gets a separable Gaussian, and composites ONCE at the cumulative alpha
        // — so blur + fade (the transitions.dev recipes) read as one motion. It SUBSUMES the opacity group (already a
        // composite-once-at-group-alpha), so the opacity group is skipped while blurring. The compositor scissors the blur
        // to the layer rect + ±3σ halo (OpacityLayerCompositor.BlurInPlace). Skipped at sigma ≈ 0 (no blur) and when fully clipped out.
        // Edge fade (gpu-renderer.md): feather the subtree's alpha (+ optional blur) near chosen edges, following the
        // rounded corners (the curve). Takes precedence over the opacity/self-blur groups — it composites once at the
        // group alpha and blurs via its own sigma. Explicit (BoxEl/ScrollEl.EdgeFade) or a scroller's AutoEdgeFade.
        EdgeFadeSpec edgeFade = default;
        bool isEdgeFade = overlapsClip && TryResolveEdgeFade(scene, node, flags, maybeSparsePaint, out edgeFade);
        if (isEdgeFade) stats.EdgeFadeGroupCount++;
        bool isBlurCandidate = !isEdgeFade && p.BlurSigma > 0.01f && overlapsClip;
        if (isBlurCandidate)
        {
            stats.BlurCandidateCount++;
        }
        // During a user scroll, HONOR the node's hold policy (HoldIfCached ⇒ crisp-on-miss; HoldOrSkipOnMiss ⇒
        // skip-on-miss) so a moving self-blur isn't re-Gaussian'd on every forced-submit scroll frame; at rest every
        // policy blurs Normally (builds the pin cache, no glow dropout). NB: this must pass the ACTUAL policy through —
        // collapsing HoldOrSkipOnMiss to Normal here silently downgraded the lyrics glow to a full per-frame Gaussian
        // during scroll (the skip-on-miss path went dead), reintroducing the UMA-bound cost this whole change removes.
        bool holdBlur = isBlurCandidate && scrollInMotion && p.BlurCachePolicy != BlurCachePolicy.Normal;
        if (holdBlur) stats.BlurHoldCandidateCount++;
        bool isBlurGroup = isBlurCandidate;
        if (isBlurGroup) stats.BlurGroupCount++;
        bool isOpacityGroup = !isEdgeFade && !isBlurGroup && p.OpacityGroup && opacity < 0.999f && overlapsClip;
        if (isEdgeFade)
        {
            // DIP→device px scale from this node's own box (uniform DPI ⇒ sx≈sy); corners + bands scale with it.
            float efsx = b.W > 0.01f ? deviceBounds.W / b.W : 1f;
            float efsy = b.H > 0.01f ? deviceBounds.H / b.H : 1f;
            var efc = new CornerRadius4(p.Corners.TopLeft * efsx, p.Corners.TopRight * efsx, p.Corners.BottomRight * efsx, p.Corners.BottomLeft * efsx);
            RectF edgeCompositeClip = deviceBounds.Intersect(clip);
            if (!p.ClipRect.IsInfinite)
                edgeCompositeClip = edgeCompositeClip.Intersect(world.TransformBounds(p.ClipRect));
            dl.PushEdgeFadeLayer(deviceBounds, edgeCompositeClip, efc, opacity, (int)edgeFade.Edges,
                (edgeFade.Edges & EdgeMask.Left) != 0 ? edgeFade.BandLeft * efsx : 0f,
                (edgeFade.Edges & EdgeMask.Top) != 0 ? edgeFade.BandTop * efsy : 0f,
                (edgeFade.Edges & EdgeMask.Right) != 0 ? edgeFade.BandRight * efsx : 0f,
                (edgeFade.Edges & EdgeMask.Bottom) != 0 ? edgeFade.BandBottom * efsy : 0f,
                (int)edgeFade.Falloff, edgeFade.Intensity,
                edgeFade.Mode == EdgeFadeMode.Fade ? 0f : edgeFade.BlurSigma * efsx, key, layerId);
            opacity = 1f;
        }
        else if (isBlurGroup)
        {
            dl.PushBlurLayer(deviceBounds, p.Corners, p.BlurSigma, opacity, key,
                holdBlur ? p.BlurCachePolicy : BlurCachePolicy.Normal, inMotion, layerId);
            opacity = 1f;
        }
        else if (isOpacityGroup)
        {
            dl.PushOpacityLayer(deviceBounds, p.Corners, opacity, key, layerId);
            opacity = 1f;
        }

        // ── shadow: drawn beneath the fill, BEFORE this node pushes its OWN clip — otherwise a ClipToBounds node (a flyout
        //    surface, a dialog) would clip its own soft-shadow halo away (the halo extends outside the node bounds). It is
        //    still bounded by the PARENT clip via the deviceBounds.Overlaps(clip) gate. ──
        if (maybeSparsePaint && overlapsClip && scene.TryGetShadow(node, out var sh) && !sh.IsNone)
            dl.Shadow(local, p.Corners, sh.Color, sh.OffsetX, sh.OffsetY, sh.Blur, sh.Spread, world, opacity, key);

        // Circular-arc stroke (ProgressRing): a trimmed, round-capped ring drawn as its own SDF primitive. The ring node
        // carries no fill (the arc IS the visual), so its order vs the fill block below doesn't matter for its own node.
        // The arc honors the StrokeTrim paint channels (AnimChannel.StrokeTrimStart/End) as a fraction of its sweep, so the
        // indeterminate ring can "breathe" (animate its arc length) — not just rotate.
        if (maybeSparsePaint && overlapsClip && scene.TryGetArc(node, out var arcS) && !arcS.IsNone)
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
        bool pushedClip = false;
        RectF childClip = clip;
        bool wantClip = (flags & NodeFlags.ClipsToBounds) != 0 || !p.ClipRect.IsInfinite;
        if ((flags & NodeFlags.ClipsToBounds) != 0)
            childClip = childClip.Intersect(deviceBounds);
        if (!p.ClipRect.IsInfinite)
            childClip = childClip.Intersect(world.TransformBounds(p.ClipRect));
        bool childClipEmpty = wantClip && childClip.IsEmpty;
        if (wantClip && !childClipEmpty)
        {
            // Tier-2 rounded clip (E9): a clipping node WITH rounded corners (an Expander/CommandBarFlyout surface
            // running an AnimChannel.ClipL/T/R/B reveal, or a plain rounded ClipsToBounds) clips RoundRect-pipeline
            // primitives to its rounded-box SDF as well as the scissor. The rounded box is the node's own device box;
            // the (possibly animated) clip-rect keeps intersecting RECTANGULARLY into the scissor — so a reveal sweeps
            // a straight edge while the surface's own corners stay round, exactly the WinUI composition-clip look.
            // Uniform radius (TopLeft, the pipeline-wide convention — the RoundRect/acrylic shaders read one radius);
            // scaled into device units by the axis-aligned world scale. Honest scope is documented on ClipCmd:
            // glyphs/images/gradients/arcs/polylines still clip by scissor only.
            float clipRadius = p.Corners.TopLeft;
            if (clipRadius > 0f)
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
            }
            else
                dl.PushClip(childClip, key);
            pushedClip = true;
        }

        // ── acrylic: snapshot + blur the backdrop drawn so far, composite the frosted surface, then content draws on top ──
        AcrylicSpec ac = default;
        bool isAcrylic = maybeSparsePaint && overlapsClip && scene.TryGetAcrylic(node, out ac);
        int acrylicRangeIdx = -1;   // E9: own-subtree damage-entry range slot for this layer (−1 = not a cached acrylic)
        if (isAcrylic)
        {
            // layerId = the stable node handle (index|gen) → keys the compositor's retained blurred-backdrop cache, so a
            // stationary acrylic surface reuses its blur across frames (scrolling inside it no longer re-blurs).
            // Capture the PushLayer byte offset + open the own-subtree entry range BEFORE emitting, so PatchDamageRanges
            // can bake this layer's external damage (union of entries NOT in [start,end)) + the frame epoch post-walk.
            int pushByteOffset = dl.BytePosition;
            acrylicRangeIdx = OpenAcrylicRange(pushByteOffset, _dmgEntryCount);
            dl.PushLayer(deviceBounds, p.Corners, ac.Tint, ac.Fallback, ac.TintOpacity, ac.BlurSigma, ac.NoiseOpacity, ac.LuminosityOpacity, key,
                ((ulong)node.Raw.Index << 32) | node.Raw.Gen, Math.Clamp(ac.FeatherTop, 0f, 1f));
        }

        // Cull this node's OWN draw if it falls entirely outside the active clip (offscreen virtualized/overscan rows).
        bool hasOwnVisual = p.VisualKind != VisualKind.None;
        bool ownVisible = overlapsClip;
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
        GradientSpec nodeGradient = default;
        bool hasNodeGradient = maybeSparsePaint && scene.TryGetGradient(node, out nodeGradient) && nodeGradient.Stops is { Length: > 0 };
        if (drawSelf)
        switch (p.VisualKind)
        {
            case VisualKind.Box when p.Fill.A > 0f || p.HoverFill.A > 0f || p.PressedFill.A > 0f || p.BorderWidth > 0f
                                     || p.ValidationBorder.A > 0f
                                     || hasNodeGradient:
            {
                ResolveSurface(scene, node, flags, in p, in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT, out ColorF fill, out ColorF border);
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
                    EmitGradient(dl, local, p.Corners, in g, in hg, hasHG, in pg, hasPG, gHoverT, gPressT,
                        hasRadialCenter, radialCenter, world, opacity, key);
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
            case VisualKind.Text:
            {
                ref var li = ref scene.Layout(node);
                ColorF textColor = ResolveTextColor(scene, node, flags, in p, in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT);
                // Auto-fit: the measure pass may have shrunk the font (TextEl.MinSize) and recorded the chosen size on
                // the cache. Shape at it so the glyphs match the box the layout sized. 0 ⇒ no fit (authored size).
                ref TextMeasureCache mc = ref scene.MeasureCacheRef(node);
                float effSize = mc.Valid && mc.FitSize > 0f ? mc.FitSize : li.TextStyle.SizeDip;

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
                int spanRunId = li.TextStyle.SpanRunId;
                if (!p.Text.IsEmpty)
                {
                    if (scene.TryGetGlyphWipe(node, out var wipe))   // glyph wipe (lyrics karaoke): per-glyph color + lift from the split
                        dl.DrawGlyphRunGradient(local, p.Text, li.TextStyle.FontFamily, effSize, li.TextStyle.Weight,
                            (int)li.TextStyle.Wrap, (int)li.TextStyle.Trim, li.TextStyle.MaxLines,
                            li.TextStyle.CharSpacing, li.TextStyle.LineHeight, (int)li.TextStyle.Stacking, (int)li.TextStyle.LineBounds,
                            world, opacity, wipe.Before, wipe.After, wipe.Split, wipe.Softness, wipe.Lift, key, spanRunId, inMotion);
                    else
                        dl.DrawGlyphRun(local, textColor, p.Text, li.TextStyle.FontFamily, effSize, li.TextStyle.Weight,
                            (int)li.TextStyle.Wrap, (int)li.TextStyle.Trim, li.TextStyle.MaxLines,
                            li.TextStyle.CharSpacing, li.TextStyle.LineHeight, (int)li.TextStyle.Stacking, (int)li.TextStyle.LineBounds,
                            world, opacity, key, spanRunId, inMotion: inMotion);
                }

                // (b1) span-run decoration bars (per-LINE, per span — the rich-text refinement of (b2) below): the
                // text seam published the laid bar rects on the run at measure (SpanRunRects — link bands are input's;
                // Underline/Strikethrough entries are ready-positioned bars), so record stays 0-touch on the font
                // seam. Bar color = the span's color when set, else the node's resolved foreground — the same
                // same-brush-as-glyphs rule WinUI's TextDecorations follow.
                if (spanRunId != 0 && SpanRunTable.Shared.Resolve(spanRunId) is { } spanRun && spanRun.Rects is { } spanRects)
                {
                    var arts = spanRects.Rects;
                    for (int i = 0; i < arts.Length; i++)
                    {
                        if (arts[i].Kind == SpanStyle.LinkBit) continue;   // hit-test bands, not painted
                        ColorF spanColor = spanRun.Spans[arts[i].Span].Color;
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
                if (p.TextDecorations != 0 && !p.Text.IsEmpty)
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
                        dl.DrawGlyphRun(local, textEdit.SelectedText, p.Text, li.TextStyle.FontFamily, effSize, li.TextStyle.Weight,
                            (int)li.TextStyle.Wrap, (int)li.TextStyle.Trim, li.TextStyle.MaxLines,
                            li.TextStyle.CharSpacing, li.TextStyle.LineHeight, (int)li.TextStyle.Stacking, (int)li.TextStyle.LineBounds,
                            world, opacity, key | 0x1, spanRunId, forceColor: true, inMotion: inMotion);
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
                var ih = new ImageHandle(p.ImageId);
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

                dl.DrawImage(drawRect, p.Corners, p.ImageId, ready, p.Fill, world, opacity, uv, fadeStart, fadeDur, fadeEase, key);
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
        }

        // Child-group shift (SizeMode.Reflow Trailing anchor): every child rides this offset while the node's own
        // fill/border/clip stay put — the content's end edge tracks the animated layout edge under the already-pushed
        // clip (the Expander slide-from-under-the-header). Zero at rest; compositor-composed, no per-child knowledge.
        Affine2D childWorld = p.ChildShiftX != 0f || p.ChildShiftY != 0f
            ? world.Translate(p.ChildShiftX, p.ChildShiftY)
            : world;
        InheritedState childState = inherited.ForChild(flags, nodeInteractive, hasLocalProgress, localHoverT, localPressT);
        // Logically-detached exits remain visually owned by this node. Emit them before live children so incoming
        // content paints over them while this parent's transform, opacity/layers, clip, and popup target stay active.
        var exitingChildren = scene.OrphanChildrenOf(node);
        if (exitingChildren is not null)
            for (int i = 0; i < exitingChildren.Count; i++)
            {
                var exiting = exitingChildren[i];
                if (!scene.IsLive(exiting)) continue;
                var exitResult = Walk(scene, dl, images, exiting, childWorld, opacity, depth + 1, childClip, in focus, in textEdit, scrollThumb, scrollTrack,
                    childScaleX, childScaleY, inMotion, scrollInMotion, childState, skipRoots, spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
                result.Include(exitResult);
            }
        // Sticky pin paint order: a PINNED child (position:sticky engaged) is emitted AFTER its siblings so the
        // content scrolling beneath it paints underneath — CSS sticky's implicit stacking. Unpinned = normal order.
        {
            bool anyPinned = false;
            for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c))
            {
                if ((scene.Flags(c) & NodeFlags.StickyPinned) != 0) { anyPinned = true; continue; }
                var childResult = Walk(scene, dl, images, c, childWorld, opacity, depth + 1, childClip, in focus, in textEdit, scrollThumb, scrollTrack,
                    childScaleX, childScaleY, inMotion, scrollInMotion, childState, skipRoots, spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
                result.Include(childResult);
            }
            if (anyPinned)
                for (var c = scene.FirstChild(node); !c.IsNull; c = scene.NextSibling(c))
                    if ((scene.Flags(c) & NodeFlags.StickyPinned) != 0)
                    {
                        var childResult = Walk(scene, dl, images, c, childWorld, opacity, depth + 1, childClip, in focus, in textEdit, scrollThumb, scrollTrack,
                            childScaleX, childScaleY, inMotion, scrollInMotion, childState, skipRoots, spans, spanFrame, spanReuseDisabled, spanStoreEnabled, ref stats);
                        result.Include(childResult);
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

        if (isAcrylic)
        {
            // Close this layer's own-subtree entry range: [start, entryCountNow) is exactly what its subtree emitted.
            if (acrylicRangeIdx >= 0) _acrylicRanges[acrylicRangeIdx].End = _dmgEntryCount;
            dl.PopLayer(deviceBounds, key);
        }

        // ── auto-hiding scrollbar thumb (overlay; over content, within the viewport bounds) ──
        if (pushedClip) dl.PopClip(key);

        // ── focus ring: keyboard focus only (FocusVisual), drawn last so it overlays children. Emitted AFTER the
        // node's own clip pops — the WinUI ring lives OUTSIDE the bounds (FocusVisualMargin −3), so a ClipsToBounds
        // control (a TextBox field) must not scissor its own ring away. Ancestor clips still apply (correct).
        if (focus.Enabled && (flags & NodeFlags.FocusVisual) != 0 && overlapsClip)
            EmitFocusRing(dl, b, p.Corners, interaction.FocusVisualMargin, world, opacity, in focus, key | 0x10);

        // Auto-hiding scrollbar overlay: draw after popping the viewport's content clip so the expanded gutter/thumb
        // are not chopped at the viewport edge, while still positioning them inside the viewport bounds. EmitScrollbar
        // self-gates on overflow and FadeT.
        if (ScrollLogNow && (flags & NodeFlags.Scrollable) != 0)
        {
            bool hs = scene.TryGetScroll(node, out var d);
            Console.Error.WriteLine(
                $"[scroll] gate n#{node.Raw.Index} bounds={b.W:0}x{b.H:0} dev=({deviceBounds.X:0},{deviceBounds.Y:0} {deviceBounds.W:0}x{deviceBounds.H:0}) " +
                $"clip=({clip.X:0},{clip.Y:0} {clip.W:0}x{clip.H:0}) overlapClip={overlapsClip} thumbA={scrollThumb.A:0.00} " +
                $"hasState={hs} content={(hs ? d.ContentW : 0):0}x{(hs ? d.ContentH : 0):0} viewport={(hs ? d.ViewportW : 0):0}x{(hs ? d.ViewportH : 0):0} " +
                $"offset={(hs ? d.OffsetX : 0):0},{(hs ? d.OffsetY : 0):0} fadeT={(hs ? d.FadeT : 0):0.00} orient={(hs ? d.Orientation : 0)} " +
                $"autoEdge={(hs && d.AutoEdgeFade)} band={(hs ? d.AutoEdgeFadeBand : 0):0} " +
                $"edgeResolved={isEdgeFade} edges={(isEdgeFade ? edgeFade.Edges : EdgeMask.None)} " +
                $"edgeBands={(isEdgeFade ? edgeFade.BandLeft : 0):0.0},{(isEdgeFade ? edgeFade.BandTop : 0):0.0}," +
                $"{(isEdgeFade ? edgeFade.BandRight : 0):0.0},{(isEdgeFade ? edgeFade.BandBottom : 0):0.0} " +
                $"loadingSuppressors={(hs ? d.LoadingBarSuppressors : 0)}");
        }
        // Scroll-edge cues (controls.md §8.3): a surface-colour fade at any overflowing edge so a clipped list reads as
        // scrollable. Drawn BEFORE the scrollbar (under the thumb) and NOT gated on FadeT — the fade is always-on while
        // there is more content (unlike the auto-hiding bar). Self-gates on overflow + per-edge offset + the resolved
        // opaque surface to fade toward (no opaque plate ⇒ skip rather than a wrong-colour fade).
        if (overlapsClip && !isEdgeFade && (flags & NodeFlags.Scrollable) != 0 &&
            scene.TryGetScroll(node, out var sec) && sec.EdgeCueConfig != 0 &&
            TryResolveCueSurface(scene, node, flags, in p, out var cueSurface))
            EmitScrollEdgeCues(dl, b, in sec, p.Corners, world, opacity, key | 0x18, cueSurface, scrollThumb);

        if (scrollThumb.A > 0f && overlapsClip && (flags & NodeFlags.Scrollable) != 0 &&
            scene.TryGetScroll(node, out var scb))
            EmitScrollbar(dl, b, in scb, world, opacity, key | 0x20, scrollThumb, scrollTrack);

        // Close the flat opacity / self-blur group LAST: everything this node emitted (shadow, fill, children, border,
        // focus ring, scrollbar) flattens into the offscreen RT and composites once (blurred, for the blur group) at the
        // group alpha. Exactly one of these was pushed (blur subsumes the opacity group).
        if (isEdgeFade) dl.PopLayer(deviceBounds, key);
        if (isOpacityGroup) dl.PopLayer(deviceBounds, key);
        if (isBlurGroup) dl.PopLayer(deviceBounds, key);

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
            spans!.Store((int)node.Raw.Index, node.Raw.Gen, spanFrame, spanInputSig, spanMoveSig, in span);
            stats.SpansReRecorded++;
        }

        return result;
    }

    private static ulong ComputeSpanInputSig(SceneStore scene, NodeHandle node, NodeFlags flags, int depth, in RectF clip, in Affine2D world,
                                             float opacity, float parentScaleX, float parentScaleY, float childScaleX, float childScaleY,
                                             float pw, float ph, bool inMotion, bool scrollInMotion, in InheritedState inherited,
                                             in FocusVisualStyle focus, in TextEditStyle textEdit, ColorF scrollThumb, ColorF scrollTrack)
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
        Mix(ref h, scrollInMotion ? 1u : 0u);
        MixScrollViewport(scene, node, flags, ref h);
        MixPaintReveal(scene, node, ref h);
        MixFloat(ref h, inherited.HoverT);
        MixFloat(ref h, inherited.PressT);
        Mix(ref h, (uint)inherited.InteractiveFlags);
        Mix(ref h, inherited.HasProgress);
        Mix(ref h, inherited.Disabled);
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

    private static ulong ComputeSpanMoveSig(SceneStore scene, NodeHandle node, NodeFlags flags, int depth, in RectF clip, in Affine2D world,
                                            float opacity, float parentScaleX, float parentScaleY, float childScaleX, float childScaleY,
                                            float pw, float ph, bool scrollInMotion, in InheritedState inherited,
                                            in FocusVisualStyle focus, in TextEditStyle textEdit, ColorF scrollThumb, ColorF scrollTrack)
    {
        ulong h = 14695981039346656037UL;
        Mix(ref h, node.Raw.Gen);
        Mix(ref h, (uint)(flags & ~NodeFlags.TransformDirty));
        Mix(ref h, (uint)depth);
        MixRect(ref h, in clip);
        MixAffineLinear(ref h, in world);
        MixFloat(ref h, opacity);
        MixFloat(ref h, parentScaleX);
        MixFloat(ref h, parentScaleY);
        MixFloat(ref h, childScaleX);
        MixFloat(ref h, childScaleY);
        MixFloat(ref h, pw);
        MixFloat(ref h, ph);
        Mix(ref h, scrollInMotion ? 1u : 0u);
        MixScrollViewport(scene, node, flags, ref h);
        MixPaintReveal(scene, node, ref h);
        MixFloat(ref h, inherited.HoverT);
        MixFloat(ref h, inherited.PressT);
        Mix(ref h, (uint)inherited.InteractiveFlags);
        Mix(ref h, inherited.HasProgress);
        Mix(ref h, inherited.Disabled);
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
    /// cannot byte-copy a subtree recorded under a different reveal clip — the focus-regain / re-theme steady frame after
    /// <see cref="FluentGpu.Animation.ScrollBindEval.ApplyContinuousPass"/> was the regression path.</summary>
    private static void MixPaintReveal(SceneStore scene, NodeHandle node, ref ulong h)
    {
        ref NodePaint p = ref scene.Paint(node);
        MixFloat(ref h, p.ChildShiftX);
        MixFloat(ref h, p.ChildShiftY);
        if (!p.ClipRect.IsInfinite) MixRect(ref h, in p.ClipRect);
    }

    private static void MixScrollViewport(SceneStore scene, NodeHandle node, NodeFlags flags, ref ulong h)
    {
        if ((flags & NodeFlags.Scrollable) == 0 || !scene.HasScroll(node)) return;
        ref var sc = ref scene.ScrollRef(node);
        // ScrollState is partly layout-/animation-owned rather than reconciler-owned. These values can therefore change
        // without changing the element or node bounds. They all affect commands emitted by this viewport (edge mask,
        // edge cues, thumb geometry/alpha, or whether the thumb exists), so they must participate in both exact-copy and
        // translated-copy keys. Omitting them let a clean retained span resurrect a no-fade/no-scrollbar frame forever.
        MixFloat(ref h, sc.ContentW);
        MixFloat(ref h, sc.ContentH);
        MixFloat(ref h, sc.ViewportW);
        MixFloat(ref h, sc.ViewportH);
        MixFloat(ref h, sc.OffsetX);
        MixFloat(ref h, sc.OffsetY);
        MixFloat(ref h, sc.OverscrollPx);
        MixFloat(ref h, sc.FadeT);
        MixFloat(ref h, sc.ExpandT);
        MixFloat(ref h, sc.AutoEdgeFadeBand);
        Mix(ref h, sc.Orientation);
        Mix(ref h, sc.EdgeCueConfig);
        Mix(ref h, sc.AutoEdgeFade ? 1u : 0u);
        Mix(ref h, sc.AlwaysShowBar ? 1u : 0u);
        Mix(ref h, sc.SuppressBar ? 1u : 0u);
        Mix(ref h, (uint)sc.LoadingBarSuppressors);
    }

    private static bool IsDirectMovingScrollContent(SceneStore scene, NodeHandle node, NodeFlags flags)
    {
        if ((flags & NodeFlags.TransformDirty) == 0) return false;
        var parent = scene.Parent(node);
        return !parent.IsNull && scene.HasScroll(parent) && scene.ScrollRef(parent).ContentNode == node;
    }

    private static RectF TranslateBounds(in RectF bounds, float dx, float dy)
        => bounds.IsEmpty ? bounds : new RectF(bounds.X + dx, bounds.Y + dy, bounds.W, bounds.H);

    private static bool IsClipComplete(in RectF bounds, in RectF clip)
    {
        if (bounds.IsEmpty || clip.IsInfinite) return true;
        const float epsilon = 0.01f;
        return bounds.X >= clip.X - epsilon
            && bounds.Y >= clip.Y - epsilon
            && bounds.Right <= clip.Right + epsilon
            && bounds.Bottom <= clip.Bottom + epsilon;
    }

    private static bool TryTranslationDelta(in Affine2D from, in Affine2D to, out float dx, out float dy)
    {
        dx = dy = 0f;
        if (!Nearly(from.M11, to.M11) || !Nearly(from.M12, to.M12)
            || !Nearly(from.M21, to.M21) || !Nearly(from.M22, to.M22))
            return false;
        dx = to.Dx - from.Dx;
        dy = to.Dy - from.Dy;
        return true;
    }

    private static bool Nearly(float a, float b) => MathF.Abs(a - b) <= 0.0001f;

    private static void MixRect(ref ulong h, in RectF r)
    {
        MixFloat(ref h, r.X);
        MixFloat(ref h, r.Y);
        MixFloat(ref h, r.W);
        MixFloat(ref h, r.H);
    }

    private static void MixAffine(ref ulong h, in Affine2D a)
    {
        MixFloat(ref h, a.M11);
        MixFloat(ref h, a.M12);
        MixFloat(ref h, a.M21);
        MixFloat(ref h, a.M22);
        MixFloat(ref h, a.Dx);
        MixFloat(ref h, a.Dy);
    }

    private static void MixAffineLinear(ref ulong h, in Affine2D a)
    {
        MixFloat(ref h, a.M11);
        MixFloat(ref h, a.M12);
        MixFloat(ref h, a.M21);
        MixFloat(ref h, a.M22);
    }

    private static void MixColor(ref ulong h, ColorF c)
    {
        MixFloat(ref h, c.R);
        MixFloat(ref h, c.G);
        MixFloat(ref h, c.B);
        MixFloat(ref h, c.A);
    }

    private static void MixFloat(ref ulong h, float v) => Mix(ref h, BitConverter.SingleToUInt32Bits(v));

    private static void Mix(ref ulong h, uint v)
    {
        h ^= v;
        h *= 1099511628211UL;
    }

    /// <summary>Resolve the surface fill/border for this frame: eased hover/press if an interaction row exists,
    /// else the instantaneous flag behaviour (first frame / no animator).</summary>
    private static bool TryResolveInteractionProgress(in InheritedState inherited, bool nodeInteractive, bool hasLocalProgress,
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

    private static float ResolveOpacity(NodeFlags flags, in NodePaint p, in InheritedState inherited, bool nodeInteractive,
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

    private static void ResolveSurface(SceneStore scene, NodeHandle node, NodeFlags flags, in NodePaint p, in InheritedState inherited,
                                       bool nodeInteractive, bool hasLocalProgress, float localHoverT, float localPressT,
                                       out ColorF fill, out ColorF border)
    {
        fill = p.Fill; border = p.BorderColor;
        if (TryResolveInteractionProgress(in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT, out float hoverT, out float pressT)
            && (hoverT > 0.001f || pressT > 0.001f))
        {
            ColorF hov = p.HoverFill.A > 0f ? p.HoverFill : Lighten(p.Fill, 0.08f);
            ColorF prs = p.PressedFill.A > 0f ? p.PressedFill : Darken(p.Fill, 0.12f);
            // Cross-fade in LINEAR light (color canon: linear-blend / premultiplied) — not straight sRGB.
            fill = ColorF.LerpLinear(p.Fill, hov, hoverT);
            fill = ColorF.LerpLinear(fill, prs, pressT);
            // Border eases to its explicit per-state token when set (e.g. CheckBox unchecked-pressed stroke →
            // ControlStrongStrokeColorDisabled), else falls back to a lighten/darken of the resting border.
            ColorF hb = p.HoverBorderColor.A > 0f ? p.HoverBorderColor : Lighten(p.BorderColor, 0.08f);
            ColorF pb = p.PressedBorderColor.A > 0f ? p.PressedBorderColor : Darken(p.BorderColor, 0.12f);
            border = ColorF.LerpLinear(p.BorderColor, hb, hoverT);
            border = ColorF.LerpLinear(border, pb, pressT);
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
    private static ColorF ResolveTextColor(SceneStore scene, NodeHandle node, NodeFlags flags, in NodePaint p, in InheritedState inherited,
                                           bool nodeInteractive, bool hasLocalProgress, float localHoverT, float localPressT)
    {
        ColorF resolved = ResolveTextColorCore(flags, in p, in inherited, nodeInteractive, hasLocalProgress, localHoverT, localPressT);
        // Implicit BrushTransition on the foreground (logical state flip): cross-fade from the previously-displayed color.
        if (scene.TryGetBrushAnim(node, out var ba) && (ba.Channels & BrushAnim.TextBit) != 0)
            resolved = ColorF.LerpLinear(ba.TextFrom, resolved, ba.T);
        return resolved;
    }

    private static ColorF ResolveTextColorCore(NodeFlags flags, in NodePaint p, in InheritedState inherited, bool nodeInteractive,
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
    private static CornerRadius4 InsetCorners(in CornerRadius4 c, float d)
        => new(MathF.Max(0f, c.TopLeft - d), MathF.Max(0f, c.TopRight - d), MathF.Max(0f, c.BottomRight - d), MathF.Max(0f, c.BottomLeft - d));

    private static void EmitBorderRing(DrawList dl, in RectF local, in RectF b, in CornerRadius4 corners, float bw, in ColorF border, float dashOn, float dashOff, in Affine2D world, float opacity, ulong key)
    {
        var rect = new RectF(bw * 0.5f, bw * 0.5f, MathF.Max(0f, b.W - bw), MathF.Max(0f, b.H - bw));
        var ins = InsetCorners(corners, bw * 0.5f);
        if (dashOn > 0f)
            dl.StrokeRoundRectDashed(rect, ins, border, bw, dashOn, dashOff, world, opacity, key);   // DropZone "drop here" look
        else
            dl.StrokeRoundRect(rect, ins, border, bw, world, opacity, key);
    }

    private static void EmitGradient(DrawList dl, in RectF local, in CornerRadius4 corners, in GradientSpec g,
        in GradientSpec hover, bool hasHover, in GradientSpec pressed, bool hasPressed, float hoverT, float pressT,
        bool hasRadialCenter, Point2 radialCenter, in Affine2D world, float opacity, ulong key)
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
    private static void RemapAbsoluteAxis(in GradientSpec g, float extent, int n,
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
    private static void LerpStops(ref ColorF c0, ref ColorF c1, ref ColorF c2, ref ColorF c3,
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
    private static void EmitGradientBorderRing(DrawList dl, in RectF b, in CornerRadius4 corners, float bw, in GradientSpec g,
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
    private static void EmitFocusRing(DrawList dl, in RectF b, in CornerRadius4 corners, in Edges4 margin, in Affine2D world, float opacity, in FocusVisualStyle f, ulong key)
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
    private static void EmitScrollbar(DrawList dl, in RectF b, in ScrollState sc, in Affine2D world, float opacity, ulong key, ColorF thumb, ColorF track)
    {
        if (sc.SuppressBar || sc.LoadingBarSuppressors > 0) return;   // pager-driven shelf, or a descendant skeleton is loading — no rail
        bool horizontal = sc.Orientation == 1;
        float content = horizontal ? sc.ContentW : sc.ContentH;
        float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        {
            if (content <= viewport + 0.5f)
            {
                if (ScrollLogNow)
                    Console.Error.WriteLine($"[scroll]   emit SKIPPED: no overflow (content={content:0} <= viewport={viewport:0})");
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
            float fade = sc.AlwaysShowBar ? 1f : Math.Clamp(sc.FadeT, 0f, 1f);
            float expand = Math.Clamp(sc.ExpandT, 0f, 1f);
            if (fade <= 0.01f && expand <= 0.01f)
            {
                if (ScrollLogNow) Console.Error.WriteLine("[scroll]   emit SKIPPED: faded out");
                return;
            }

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

            const float expandedVisible = 6f;                       // ScrollBarSize 12 − stroke 6, centred (3px insets)
            float thick = collapsed + (expandedVisible - collapsed) * expand;
            float collapsedCrossPos = cross - collapsed - thumbOffset;   // [cross−3, cross−1]
            float expandedCrossPos = cross - bar + 3f;                   // [cross−9, cross−3]
            float crossPos = collapsedCrossPos + (expandedCrossPos - collapsedCrossPos) * expand;
            RectF thumbRect = horizontal
                ? new RectF(pos, crossPos, thumbLen, thick)
                : new RectF(crossPos, pos, thick, thumbLen);
            dl.FillRoundRect(thumbRect, CornerRadius4.All(radius), thumbCol, world, opacity, key | 0x1);

            if (ScrollLogNow)
            {
                RectF devThumb = world.TransformBounds(thumbRect);
                Console.Error.WriteLine(
                    $"[scroll]   emit THUMB local=({thumbRect.X:0},{thumbRect.Y:0} {thumbRect.W:0}x{thumbRect.H:0}) " +
                    $"device=({devThumb.X:0},{devThumb.Y:0} {devThumb.W:0}x{devThumb.H:0}) thick={thick:0.0} fade={fade:0.00} " +
                    $"thumbAlpha={thumbCol.A:0.000} expand={expand:0.00}  (cross={cross:0} crossPos={crossPos:0})");
            }

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
                    dl.DrawGlyphRun(decRect, arrow, dec, _sbIconFamily, glyphSize, 400, 0, 0, 1, 0f, float.NaN, 0, 0, world, opacity, key | 0x2);
                    dl.DrawGlyphRun(incRect, arrow, inc, _sbIconFamily, glyphSize, 400, 0, 0, 1, 0f, float.NaN, 0, 0, world, opacity, key | 0x3);
                }
                else if (horizontal)
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

    private const float EdgeCueBandPx = 28f;    // fade-band depth along the scroll axis
    private const float EdgeCueRunwayPx = 24f;  // alpha ramps 0→1 over the last Runway px of overflow (smooth in/out, no pop)

    /// <summary>Resolve a node's edge fade: an explicit <c>BoxEl/ScrollEl.EdgeFade</c> spec, or a scroller's
    /// <c>AutoEdgeFade</c> synthesized from its live overflow (feather only the edges with more content past them, the
    /// per-edge band ramped to 0 over the last <c>runway</c> px so it appears/disappears smoothly with the offset).</summary>
    private static bool TryResolveEdgeFade(SceneStore scene, NodeHandle node, NodeFlags flags, bool maybeSparsePaint, out EdgeFadeSpec ef)
    {
        if (maybeSparsePaint && scene.TryGetEdgeFade(node, out ef) && !ef.IsNone) return true;          // explicit, any element
        if ((flags & NodeFlags.Scrollable) != 0 && scene.TryGetScroll(node, out var sc)
            && sc.AutoEdgeFade && sc.AutoEdgeFadeBand > 0.5f)
        {
            float band = sc.AutoEdgeFadeBand;
            const float runway = 24f;
            EdgeMask edges = EdgeMask.None;
            float bl = 0f, bt = 0f, br = 0f, bb = 0f;
            if (sc.Orientation == 1)   // horizontal
            {
                if (sc.OffsetX > 0.5f) { edges |= EdgeMask.Left; bl = band * Math.Clamp(sc.OffsetX / runway, 0f, 1f); }
                float pastR = sc.ContentW - (sc.OffsetX + sc.ViewportW);
                if (pastR > 0.5f) { edges |= EdgeMask.Right; br = band * Math.Clamp(pastR / runway, 0f, 1f); }
            }
            else                       // vertical
            {
                if (sc.OffsetY > 0.5f) { edges |= EdgeMask.Top; bt = band * Math.Clamp(sc.OffsetY / runway, 0f, 1f); }
                float pastB = sc.ContentH - (sc.OffsetY + sc.ViewportH);
                if (pastB > 0.5f) { edges |= EdgeMask.Bottom; bb = band * Math.Clamp(pastB / runway, 0f, 1f); }
            }
            if (edges != EdgeMask.None) { ef = new EdgeFadeSpec(edges, bl, bt, br, bb); return true; }
        }
        ef = default;
        return false;
    }

    /// <summary>The opaque surface colour the edge fade dissolves into: the viewport's own fill if opaque, else the
    /// nearest opaque self-or-ancestor fill, OR an elevated acrylic/flyout plate's solid <c>Fallback</c> (so a ComboBox /
    /// MenuFlyout / AutoSuggest dropdown fades into the MENU colour, not the page — the popup acrylic is translucent so
    /// its Fill is not opaque). A translucent card composites ≈ the page base, so the first opaque plate is a good
    /// approximation. No opaque plate found ⇒ skip the cue rather than draw a wrong-colour fade.</summary>
    private static bool TryResolveCueSurface(SceneStore scene, NodeHandle node, NodeFlags flags, in NodePaint p, out ColorF surface)
    {
        const float opaqueA = 0.985f;
        if (p.Fill.A >= opaqueA) { surface = p.Fill; return true; }
        if ((flags & NodeFlags.SparsePaint) != 0 && scene.TryGetAcrylic(node, out var selfAc) && selfAc.Fallback.A >= opaqueA) { surface = selfAc.Fallback; return true; }
        for (var n = scene.Parent(node); !n.IsNull; n = scene.Parent(n))
        {
            ColorF f = scene.Paint(n).Fill;
            if (f.A >= opaqueA) { surface = f; return true; }
            if ((scene.Flags(n) & NodeFlags.SparsePaint) != 0 && scene.TryGetAcrylic(n, out var ac) && ac.Fallback.A >= opaqueA) { surface = ac.Fallback; return true; }
        }
        surface = default;
        return false;
    }

    /// <summary>A surface-colour gradient fade (opaque at the edge → transparent over a band) at each scroll edge with
    /// more content past it, optionally with a small chevron. Drawn beside the overlay scrollbar (after the viewport
    /// clip pops), read straight off <see cref="ScrollState"/> — zero new scene nodes, zero managed allocation. The band
    /// alpha ramps with how far past the edge the content runs (<see cref="EdgeCueRunwayPx"/>) so it fades in/out
    /// smoothly with the already-eased offset. controls.md §8.3.</summary>
    private static void EmitScrollEdgeCues(DrawList dl, in RectF b, in ScrollState sc, CornerRadius4 corners,
        in Affine2D world, float opacity, ulong key, ColorF surface, ColorF chevron)
    {
        bool horizontal = sc.Orientation == 1;
        float content = horizontal ? sc.ContentW : sc.ContentH;
        float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        if (content <= viewport + 0.5f) return;                       // fits — no overflow at either edge

        float offset = horizontal ? sc.OffsetX : sc.OffsetY;
        float axis = horizontal ? b.W : b.H;
        float band = MathF.Min(EdgeCueBandPx, axis * 0.5f);           // never let the two bands meet on a tiny viewport
        if (band <= 0.5f) return;

        ColorF edge = surface;                                        // opaque at the edge
        ColorF clear = surface with { A = 0f };                       // transparent into the content
        bool chev = sc.EdgeCueChevron;
        const float chevHalf = 4.0f;                                  // ~8px chevron

        // before edge (top / left): fades in as content is scrolled past the start.
        float aBefore = Math.Clamp(offset / EdgeCueRunwayPx, 0f, 1f);
        if (aBefore > 0.01f)
        {
            RectF rect = horizontal ? new RectF(0f, 0f, band, b.H) : new RectF(0f, 0f, b.W, band);
            CornerRadius4 r = horizontal
                ? new CornerRadius4(MathF.Min(corners.TopLeft, band), 0f, 0f, MathF.Min(corners.BottomLeft, band))
                : new CornerRadius4(MathF.Min(corners.TopLeft, band), MathF.Min(corners.TopRight, band), 0f, 0f);
            Point2 start = new(0f, 0f);
            Point2 end = horizontal ? new Point2(1f, 0f) : new Point2(0f, 1f);
            dl.GradientRect(new DrawGradientRectCmd(rect, r, start, end, 0, 2,
                edge, clear, default, default, 0f, 1f, 1f, 1f, world, opacity * aBefore), key | 0x0);
            if (chev)
            {
                Point2 cc = horizontal ? new Point2(band * 0.5f, b.H * 0.5f) : new Point2(b.W * 0.5f, band * 0.5f);
                EmitChevron(dl, cc, horizontal, positive: false, chevron with { A = chevron.A * aBefore }, world, opacity, key | 0x1, chevHalf);
            }
        }

        // after edge (bottom / right): fades in as content is scrolled past the end.
        float aAfter = Math.Clamp((content - (offset + viewport)) / EdgeCueRunwayPx, 0f, 1f);
        if (aAfter > 0.01f)
        {
            RectF rect = horizontal ? new RectF(b.W - band, 0f, band, b.H) : new RectF(0f, b.H - band, b.W, band);
            CornerRadius4 r = horizontal
                ? new CornerRadius4(0f, MathF.Min(corners.TopRight, band), MathF.Min(corners.BottomRight, band), 0f)
                : new CornerRadius4(0f, 0f, MathF.Min(corners.BottomRight, band), MathF.Min(corners.BottomLeft, band));
            Point2 start = horizontal ? new Point2(1f, 0f) : new Point2(0f, 1f);
            Point2 end = new(0f, 0f);
            dl.GradientRect(new DrawGradientRectCmd(rect, r, start, end, 0, 2,
                edge, clear, default, default, 0f, 1f, 1f, 1f, world, opacity * aAfter), key | 0x4);
            if (chev)
            {
                Point2 cc = horizontal ? new Point2(b.W - band * 0.5f, b.H * 0.5f) : new Point2(b.W * 0.5f, b.H - band * 0.5f);
                EmitChevron(dl, cc, horizontal, positive: true, chevron with { A = chevron.A * aAfter }, world, opacity, key | 0x5, chevHalf);
            }
        }
    }

    private static void EmitChevron(DrawList dl, Point2 c, bool horizontal, bool positive, ColorF color, in Affine2D world, float opacity, ulong key, float size = 3.0f)
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

    private static void EmitSegment(DrawList dl, Point2 a, Point2 b, ColorF color, in Affine2D world, float opacity, ulong key)
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

    private static ColorF Lighten(ColorF c, float t) => new(c.R + (1f - c.R) * t, c.G + (1f - c.G) * t, c.B + (1f - c.B) * t, c.A);
    private static ColorF Darken(ColorF c, float t) => new(c.R * (1f - t), c.G * (1f - t), c.B * (1f - t), c.A);
}
