using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Scene;

public enum VisualKind : byte { None = 0, Box = 1, Text = 2, Image = 3, PolylineStroke = 4, TabShape = 5, IconLayer = 6, Video = 7, Path = 8 }

/// <summary>Sparse image-only payload kept out of the dense paint column. The source id stays in
/// <see cref="NodePaint.ImageId"/>; <see cref="DerivedImageId"/> is selected only after its bake reaches Ready.</summary>
public readonly record struct ImageVisualEffects(int DerivedImageId, ColorF Overlay, ImageMaskSpec Mask, float Saturation = 1f);

/// <summary>Per-text-node measure cache (layout.md §2.3): a pure-function cache of (text, style, availWidth) → size, so a
/// scoped relayout skips re-shaping a text leaf whose inputs are unchanged. Self-invalidating — any input change makes
/// the stored key not match. Helps the real DirectWrite shaping path; neutral for the headless fake font.
/// Besides the size, the cache retains the face's DECORATION metrics from the same <c>TextMetrics</c> (top-down DIP,
/// the line frame of <c>Baseline</c> — see FluentGpu.Text.TextMetrics): the recorder reads them at record time to
/// place underline/strikethrough bars (NodePaint.TextDecorations) without re-touching the font seam. Filled by the
/// layout engine's measure-miss path; 0 ⇒ the backend reported no face metrics (the recorder falls back to a
/// size-derived approximation).</summary>
public struct TextMeasureCache
{
    public bool Valid;
    public StringId Text;
    public TextStyle Style;
    public float MaxW;
    public Size2 Size;
    /// <summary>Auto-fit resolved font size (TextEl.MinSize / TextStyle.MinSizeDip): the size the measure pass shrank
    /// to so the run fits MaxLines at MaxW. 0 ⇒ no auto-fit (the recorder shapes at the authored SizeDip).</summary>
    public float FitSize;
    /// <summary>Underline bar top, measured DOWN from the line top (DWrite underlinePosition flipped over the baseline
    /// — TextLayoutEngine.cs:141; headless model: baseline + 1).</summary>
    public float UnderlineY;
    /// <summary>Underline bar thickness (DWrite underlineThickness; also reused for the strikethrough bar, the
    /// DWrite/WinUI convention).</summary>
    public float UnderlineThickness;
    /// <summary>Strikethrough bar top, measured DOWN from the line top (DWrite strikethroughPosition flipped;
    /// headless model: SizeDip × 0.8).</summary>
    public float StrikeY;
}

/// <summary>Layout-input column (flexbox: direction + gap + padding + margin + flex grow/shrink/basis + justify/align + min/max + explicit size + text style).</summary>
public struct LayoutInput
{
    public byte Direction;        // 0 = row (main = X), 1 = column (main = Y)
    public float Gap;             // between-children spacing on the main axis
    public Edges4 Padding;
    public Edges4 Margin;
    public float Width;           // NaN = auto (content)
    public float Height;          // NaN = auto (content)
    public float AspectRatio;     // width÷height; NaN = off. Derives the missing extent for a fluid leaf (CSS aspect-ratio)
    public float MinW, MinH, MaxW, MaxH;   // NaN = unconstrained

    public float FlexGrow;        // share of positive free space (default 0)
    public float FlexShrink;      // share of negative free space (default 0, Yoga-style)
    public float FlexBasis;       // NaN = auto (content / explicit main size)
    public FlexAlign AlignSelf;   // Auto = inherit container AlignItems
    public FlexAlign JustifySelf; // ZStack overlay child: horizontal placement. Auto = inherit the stack's Justify

    public FlexJustify Justify;   // container: main-axis distribution
    public FlexAlign AlignItems;  // container: default child cross alignment
    public bool Wrap;             // container: wrap children to multiple lines when the main axis is constrained
    // ZStack overlay child only (A3/layout.md MeasureZStack): ignore the stack's own constrained childAvail and
    // measure at PositiveInfinity instead, reporting the child's natural content width — e.g. a rail tooltip that
    // must report its real text width even though the rail itself stays pinned to a fixed, narrower ZStack. Packed
    // alongside Wrap in the existing LayoutInput column (no new SoA column).
    public bool MeasureUnboundedWidth;

    public TextStyle TextStyle;   // for VisualKind.Text leaves

    public static LayoutInput Default => new()
    {
        Direction = 1,            // default container stacks vertically
        Gap = 0,
        Padding = default,
        Margin = default,
        Width = float.NaN,
        Height = float.NaN,
        AspectRatio = float.NaN,
        MinW = float.NaN, MinH = float.NaN, MaxW = float.NaN, MaxH = float.NaN,
        FlexGrow = 0f,
        FlexShrink = 0f,
        FlexBasis = float.NaN,
        AlignSelf = FlexAlign.Auto,
        JustifySelf = FlexAlign.Auto,
        Justify = FlexJustify.Start,
        AlignItems = FlexAlign.Stretch,
    };
}

/// <summary>Paint column — one cache line of per-node visual state read by the record phase.</summary>
public struct NodePaint
{
    public Affine2D LocalTransform;
    public float Opacity;
    public float HoverOpacity, PressedOpacity;
    // Per-node self-blur sigma (px), animated by AnimChannel.BlurSigma (the Expressive Motion Kit's perceptual softener).
    // When > ε the recorder wraps this node's subtree in a PushLayer{Blur}…PopLayer (subtree → pooled offscreen RT →
    // separable Gaussian → composite) — the same offscreen-layer machinery as OpacityGroup, with the AcrylicCompositor
    // Gaussian. 0 = no blur layer (the default); a change sets PaintDirty (never LayoutDirty).
    public float BlurSigma;
    public BlurCachePolicy BlurCachePolicy;
    // Engine-owned transient intent: 1 only while a LIVE, non-parked AnimChannel.BlurSigma row drives this node.
    // Kept beside BlurCachePolicy so it consumes that byte field's existing alignment padding (no NodePaint growth).
    // This is deliberately not authored by BoxEl: the animation slab is the single source of truth and clears it on
    // settle/cancel/park, allowing the compositor to choose an animated-blur strategy without guessing from sigma.
    internal byte BlurAnimationActive;
    // Composited transform origin (normalized 0..1 of the node box; default centre 0.5,0.5). The recorder scales/transforms
    // the node about (OriginX·W, OriginY·H) — so e.g. a menu can scale/unfold from its TOP edge (OriginY=0).
    public float OriginX, OriginY;
    // Presented extent (a layout-transition "Reveal"): when not NaN, the recorder draws this node's fill + clips its
    // children to PresentedW/PresentedH instead of the laid-out Bounds — so a size change animates without relayout,
    // and the presented size may exceed the model bounds (shrink reveals). Written by AnimEngine (AnimChannel.SizeW/H).
    public float PresentedW, PresentedH;
    // Authored clip-rect (node-local space): when not Infinite, the recorder intersects the child clip with it (composes
    // with ClipsToBounds). Animated by AnimEngine ClipL/T/R/B (e.g. an Expander/CommandBarFlyout reveal). Default Infinite.
    public RectF ClipRect;

    /// <summary>The half-extent the STICKY viewport cut (<c>ScrollBindDsl.ClipTopAtViewport</c>, written by
    /// <c>ScrollBindEval.ApplyStickyClip</c>) puts on the three edges it does not own, and therefore the SIGNATURE that
    /// tells a sticky cut apart from every other <see cref="ClipRect"/> writer.
    ///
    /// <para>The distinction is load-bearing beyond paint: <c>InputDispatcher</c> gates INPUT on a sticky cut (content
    /// guillotined at an unpainted pinned band's edge must not keep taking that band's clicks — it is not merely
    /// invisible there, it is not there), and deliberately does NOT gate input on a finite reveal/flight box (a
    /// ComboBox dropdown splitting open, a connected-animation flight), where the clip is a transient presentation
    /// over a surface that is already logically live. Big enough to be unreachable as a real coordinate, small enough
    /// that it can never be mistaken for <see cref="RectF.Infinite"/> (whose sentinel is 1e9).</para></summary>
    public const float StickyClipSpan = 1e8f;

    // Child-group offset (a SizeMode.Reflow Trailing anchor): when non-zero, the recorder shifts every CHILD's origin
    // by this amount while the node's own fill/border/clip stay put — so the content's end edge rides the animated
    // layout edge (the Expander slide-from-under-the-header). Written by the reflow re-solve each tick; 0 at rest.
    public float ChildShiftX, ChildShiftY;
    public float StrokeTrimStart, StrokeTrimEnd;
    public ColorF Fill;
    public ColorF HoverFill;      // A==0 ⇒ recorder auto-lightens Fill on hover
    public ColorF PressedFill;    // A==0 ⇒ recorder auto-darkens Fill on press
    public ColorF BorderColor;
    public ColorF HoverBorderColor;    // A==0 ⇒ recorder auto-lightens BorderColor on hover (else eases to this exact token)
    public ColorF PressedBorderColor;  // A==0 ⇒ recorder auto-darkens BorderColor on press (else eases to this exact token)
    // Validation error border (form-validation.md): the theme-resolved invalid color, written by the reconciler from the
    // bound BoxEl.Validation channel. A==0 ⇒ valid/none; A>0 ⇒ the recorder overrides the resolved border with it.
    public ColorF ValidationBorder;
    public float BorderWidth;
    public float BorderDashOn, BorderDashOff;   // 0/0 = solid stroke; >0 = dashed (DropZone look). Solid-border path only.
    public float TabFlareRadius;
    public CornerRadius4 Corners;
    public ColorF TextColor;
    // Stateful foreground ramps (text/glyph). A==0 ⇒ no state color for that axis; the recorder leaves TextColor as-is.
    // Hover/Pressed ease with the nearest interactive ancestor's progress; Disabled/Focused are steps (see ResolveTextColor).
    public ColorF TextHoverColor;
    public ColorF TextPressedColor;
    public ColorF TextDisabledColor;
    public ColorF TextFocusedColor;
    public StringId Text;
    /// <summary>Text decoration flags for a <see cref="VisualKind.Text"/> leaf (<see cref="UnderlineBit"/> |
    /// <see cref="StrikethroughBit"/>; 0 = none). The recorder emits the bars itself — FillRoundRect quads placed by
    /// the face metrics cached on <see cref="TextMeasureCache"/> (no new opcode), colored with the SAME resolved
    /// foreground (hover/press ramps + BrushTransition) as the glyph run — matching DWrite, which draws decorations
    /// from the face's underline position/thickness rather than glyph geometry. Written by the reconciler from
    /// <c>TextEl.Underline</c>/<c>Strikethrough</c> (WinUI <c>TextDecorations</c>; HyperlinkButton underlines only when
    /// the HyperlinkUnderlineVisible directive is set or under HighContrast — HyperLinkButton_Partial.cpp:207-212).</summary>
    public byte TextDecorations;
    /// <summary>Flat opacity group opt-in (WinUI Composition LayerVisual semantics): when set and the node's resolved
    /// opacity &lt; 1, the recorder wraps the subtree in PushLayer{Opacity}…PopLayer — children render at FULL alpha
    /// offscreen and composite ONCE at the group alpha, so overlapping children don't double-blend. Default false =
    /// plain multiplied opacity (WinUI Visual.Opacity's per-visual behavior, the engine default).</summary>
    public bool OpacityGroup;
    public int ImageId;           // VisualKind.Image: handle into the ImageCache (Fill doubles as the placeholder tint).
                                  // VisualKind.IconLayer: DOUBLES as the IconGeometryTable.Shared PathId (Fill doubles as
                                  // the resolved, theme-live layer tint) — no new NodePaint field, so the 64B cache line holds.
                                  // VisualKind.Video: DOUBLES as the video registry SurfaceId (the same pun as IconLayer's
                                  // PathId — the hole punch carries no color, so nothing else on the node is displaced).
    public byte ImageFit;         // VisualKind.Image: (ImageFit) content-fit mode; 0 = Cover (default). Read by the recorder
    public float ImageFocusX, ImageFocusY;
    public VisualKind VisualKind;

    /// <summary><see cref="TextDecorations"/>: draw the face-metric underline bar.</summary>
    public const byte UnderlineBit = 1;
    /// <summary><see cref="TextDecorations"/>: draw the face-metric strikethrough bar.</summary>
    public const byte StrikethroughBit = 2;

    public static NodePaint Default => new()
    {
        LocalTransform = Affine2D.Identity,
        Opacity = 1f,
        HoverOpacity = float.NaN,
        PressedOpacity = float.NaN,
        OriginX = 0.5f,
        OriginY = 0.5f,
        PresentedW = float.NaN,
        PresentedH = float.NaN,
        ClipRect = RectF.Infinite,
        StrokeTrimStart = float.NaN,
        StrokeTrimEnd = float.NaN,
        Fill = ColorF.Transparent,
        ImageFocusX = 0.5f,
        ImageFocusY = 0.5f,
        VisualKind = VisualKind.None,
    };
}

/// <summary>
/// Scroll + virtualization state for a viewport node (marked <c>NodeFlags.Scrollable</c>). There are O(viewports)
/// of these — not one per node — so the store keeps them in a sparse side-table keyed by node index, not a parallel
/// column. Ownership (scroll-v3-plan §3.1 — supersedes the scroll-feel-rework-v2 contract this doc used to state):
/// <b>Layout</b> publishes <c>Content*</c>/<c>Viewport*</c>; the <b>virtualizer</b> owns the <c>Item*</c> /
/// realized-range / anchor fields; the reconciler/controls own the config fields (Snap*, EdgeCue*, zoom bounds, …).
/// The RESULT columns (offset, band, zoom, velocity, activity — see the group below) are owned by the
/// <c>FluentGpu.Scroll.ScrollKernel</c> and are settable ONLY through <see cref="ApplyMotion"/>, called exactly once
/// per moved body per kernel tick/reclamp from <c>FluentGpu.Scroll.SceneScrollSink.Apply</c> — the kernel is the sole
/// writer, the sink is the sole call site. Scroll is layout-free: the <c>-ScrollOffset</c> translation is the
/// <see cref="ContentNode"/>'s <c>LocalTransform</c>, never a relayout. Scrollbar chrome (fade/expand/hover/idle) is
/// NOT here — it lives in <c>FluentGpu.Scroll.ScrollBarChromeTable</c>, a separate side-table chrome never mixes
/// with motion (scroll-v3-plan §4).
/// </summary>
public struct ScrollState
{
    // ── RESULT columns (scroll-v3-plan §3.1): the kernel's per-tick physics output. Backing storage is private —
    // the public members are get-only ({ get; private set; }-equivalent); the ONLY method that assigns them is
    // ApplyMotion below, which itself only accepts a FluentGpu.Scroll.SceneScrollSink.ScrollWriteToken minted for the
    // duration of SceneScrollSink.Apply (DEBUG/FLUENTGPU_DIAG: a ThreadStatic nonce check; Release: erased to a plain
    // call). No other file can write these — every former direct writer (dispatcher/layout/reconciler/controls) now
    // posts a FluentGpu.Scroll.ScrollInput command instead (scroll-v3-plan §3.2).
    public float OffsetX { get; private set; }   // live scroll position (DIP) on X
    public float OffsetY { get; private set; }   // live scroll position (DIP) on Y
    public float BandX { get; private set; }     // rubber-band visual displacement past the X clamp (0 = at rest)
    public float BandY { get; private set; }     // …and Y (rename of the old single-axis OverscrollPx — a viewport only
                                                  // ever bands on its own Orientation axis, but the kernel's ScrollWrite
                                                  // is a fixed POD carrying both, so both live here too)
    /// <summary>The band on this viewport's own scroll axis (<see cref="Orientation"/> picks X or Y) — the field the
    /// rest of the engine actually wants; <see cref="BandX"/>/<see cref="BandY"/> exist because the kernel writes a
    /// fixed-shape POD, not because a viewport bands on both axes at once.</summary>
    public readonly float BandMain => Orientation == 1 ? BandX : BandY;
    public float ZoomFactor { get; private set; }        // committed content scale (1 = unzoomed)
    /// <summary>Signed live coast/chase velocity along <see cref="Orientation"/> (DIP/s, offset space) — the kernel's
    /// current physics velocity for this body (drag/ballistic/driven all funnel through here). Replaces the old
    /// dual-purpose <c>FlingVelocity</c> intent column; this is a RESULT, not something a caller seeds.</summary>
    public float Velocity { get; private set; }
    // Live CONTENT speed (DIP/s, unsigned) of the composed -(offset + band) transform, written every kernel tick from
    // the advance that tick actually committed. Read by SceneRecorder to soften text in proportion to how fast the
    // list is really moving (see TextMotionSoftness).
    public float LiveSpeedDip { get; private set; }
    /// <summary>What kind of motion is moving this body right now (scroll-v3-plan §2.1 <c>FluentGpu.Scroll.ScrollActivity</c>:
    /// Idle/Drag/Ballistic/Driven). Overscroll is NOT a state here — it's <see cref="BandMain"/> ≠ 0 under any activity.</summary>
    public FluentGpu.Scroll.ScrollActivity Activity { get; private set; }
    /// <summary>Auxiliary sub-mode bits on <see cref="Activity"/> (scroll-v3-plan §2.1 <c>ScrollActivityFlags</c>:
    /// Programmatic/Wheel/Chained/Banding/Bouncing/Autoscroll).</summary>
    public FluentGpu.Scroll.ScrollActivityFlags ActivityFlags { get; private set; }
    /// <summary>Derived each <see cref="ApplyMotion"/> call: true while this viewport is in USER-driven motion this
    /// write (<see cref="Activity"/> != Idle, not Programmatic, and the write actually moved something). Replaces the
    /// old FSM-computed field of the same name — the derivation lives at the one chokepoint instead of the deleted
    /// ScrollIntegrator. SceneRecorder's self-blur (DoF) defer keys off this.</summary>
    public bool UserScrollActive { get; private set; }
    public float LastReleaseVelocity { get; private set; }   // the LIFT velocity of the most recent contact gesture on
                                                              // this viewport (px/s, signed, offset space) — a release
                                                              // RECORD, not physics (PagedShelf's directional commit).
    /// <summary>The frame index (SceneScrollSink.FrameIndex, stamped into the write token) of the most recent
    /// ApplyMotion call that actually moved this body. <see cref="ScrollBarChromeTable"/>'s "moved this frame" test is
    /// <c>LastMovedFrame == currentFrame</c> — chrome never reads Offset/Band itself (scroll-v3-plan §4).</summary>
    public uint LastMovedFrame { get; private set; }

    /// <summary>The ONE chokepoint that writes the result columns above (scroll-v3-plan §3.1 "Token"). Only
    /// <c>FluentGpu.Scroll.SceneScrollSink.Apply</c> can construct a valid token, and only for the duration of one
    /// <c>IScrollSink.Apply</c> call — see <c>SceneScrollSink.ScrollWriteToken</c>. Zero-alloc, AggressiveInlining in
    /// Release (the token collapses to an empty ref struct there).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void ApplyMotion(in FluentGpu.Scroll.SceneScrollSink.ScrollWriteToken token, in FluentGpu.Scroll.ScrollWrite w)
    {
#if DEBUG || FLUENTGPU_DIAG
        if (!token.IsValid)
            throw new System.InvalidOperationException("ScrollState.ApplyMotion: token is only valid inside SceneScrollSink.Apply — result columns are the kernel's alone to write (scroll-v3-plan §3.1).");
#endif
        OffsetX = w.OffsetX; OffsetY = w.OffsetY;
        BandX = w.BandX; BandY = w.BandY;
        ZoomFactor = w.Zoom;
        Velocity = w.VelocityMain;
        LiveSpeedDip = w.VisualSpeedMain;
        Activity = w.Activity;
        ActivityFlags = w.Flags;
        bool moved = w.Moved != default;
        UserScrollActive = w.Activity != FluentGpu.Scroll.ScrollActivity.Idle
            && (w.Flags & FluentGpu.Scroll.ScrollActivityFlags.Programmatic) == 0
            && moved;
        LastReleaseVelocity = w.LastReleaseVelocity;
        if (moved) LastMovedFrame = token.FrameIndex;
    }

    // ── config/geometry (unchanged writers — layout/reconciler/virtualizer, per scroll-v3-plan §3.1) ──
    public float ContentW, ContentH;      // Layout-published full content extent (DIP)
    public float ViewportW, ViewportH;    // Layout-published viewport inner size (for clamp + window math)
    public byte  Orientation;             // 0 = vertical scroll (Y), 1 = horizontal scroll (X)
    public int PrevArrangedFirst;         // the realized row window [first..last] the PREVIOUS virtual arrange saw. A row outside
    public int PrevArrangedLast;          // it is FRESH this arrange: its first measure can be transiently short (deferred inner
                                          // content lands a frame later), so a fresh row ABOVE the anchor must not push that
                                          // transient into the extent table — the dip+restore re-pin pair was the felt scroll
                                          // jitter. Default 0/-1 = empty window (a mount treats every row as fresh).
    public bool  ContentSized;            // auto-size to content then clamp (popup lists); false = hard viewport
    // Pinch-zoom (WinUI ScrollPresenter ZoomFactor; opt-in like ScrollingZoomMode — default Disabled). When Zoomable, a
    // SECOND touch contact over this viewport scales the content about the gesture midpoint (Input owns ZoomFactor; it is
    // applied as a TRANSFORM-only term composed with the -offset translation on the ContentNode, never a relayout). The
    // committed factor scales the content extent the offset clamps against (Content*Zoom − Viewport), so a zoomed-in pan
    // reaches the full magnified content. Defaults: factor 1, Min 0.1 / Max 10.0 (ScrollPresenter.h:63-64).
    public bool  Zoomable;                // the viewport opts into pinch-zoom (WinUI ScrollingZoomMode.Enabled)
    public float MinZoom, MaxZoom;        // zoom clamp bounds (ScrollPresenter s_defaultMin/MaxZoomFactor = 0.1 / 10.0)
    // ── Snap points (WinUI ScrollPresenter Mandatory snap-point model — controls\dev\ScrollPresenter\SnapPoint.cpp).
    // A touch fling retargets its friction decay to land EXACTLY on the nearest applicable snap value (the kernel's
    // ScrollPhysics.SnapTarget — ported from this file's old ScrollSnap — computes the natural rest from v0 over the
    // decay integral, picks the snap per the zone rules, then re-solves the velocity so the SAME decay curve lands
    // there). POD, per-viewport: a uniform
    // interval (the WinUI RepeatedScrollSnapPoint, e.g. a LoopingSelector item row) and/or an explicit sorted list (the
    // WinUI ScrollSnapPoint irregular case). Both empty (SnapInterval ≤ 0 ∧ SnapPoints null) ⇒ no snapping, the plain
    // fling. The snap math is "Mandatory" (no applicable-range gaps): every value falls in some snap point's zone, the
    // zone boundary between two adjacent points being their midpoint (SnapPoint.cpp Influence(), :453/:474). Snapping
    // applies to flings only (a wheel/keyboard/programmatic offset is hard-clamped, never snapped — matching the clamp
    // contract); the offset axis is Orientation's.
    // WRITER CONTRACT: these four fields are DECLARATION-GATED. The reconciler writes them ONLY when the element carries a
    // non-null Snap (ScrollEl.Snap / VirtualListEl.Snap ⇒ SnapSpec.ApplyTo) — for every other viewport the patch never
    // touches them, so a control/probe that writes SnapInterval onto the scene after mount keeps it across every
    // reconcile. A declaring element OWNS them and re-asserts on each patch; a control whose interval is a live layout
    // measure (a page stride that re-fits on resize) therefore writes the scene directly instead of declaring, because a
    // frozen-at-mount options record cannot re-declare a width-reactive value.
    public float SnapInterval;            // uniform snap spacing (DIP) on the scroll axis; ≤ 0 = no interval snapping
    public float SnapStart;               // first snap value / lower bound of the repeated zone (DIP; default 0)
    public float SnapEnd;                 // upper bound of the repeated zone (DIP); ≤ SnapStart = open (clamp-max bound)
    public float[]? SnapPoints;           // optional explicit sorted snap values (the irregular case); null = none. The
                                          // managed ref is fine in the dict-backed side-table (like Layout / GridSpec.Columns).
    // Rubber-band overscroll (WinUI manipulation overpan) is the RESULT-column pair BandX/BandY declared above — the
    // kernel is the sole writer via ApplyMotion, same chokepoint as Offset. Nothing config-level lives here anymore.

    // Scroll-edge cues (controls.md §8.3): a surface-colour gradient fade (+ optional chevron) at any edge with more
    // content past it, so a clipped list signals there is more below the fold. Reconciler-resolved from the
    // ScrollEdgeCues prop (Auto already resolved to ScrollEdgeCuesDefaults.Default), read at record time by
    // SceneRecorder.EmitScrollEdgeCues. 0 = no cue (None / a synthetic scroller the reconciler never touched).
    public byte EdgeCueConfig;            // bit0 = fade, bit1 = chevron
    public const byte EdgeCueFadeBit = 1, EdgeCueChevronBit = 2;
    public readonly bool EdgeCueFade => (EdgeCueConfig & EdgeCueFadeBit) != 0;
    public readonly bool EdgeCueChevron => (EdgeCueConfig & EdgeCueChevronBit) != 0;
    // Auto edge fade (premium alpha-mask cue): the recorder feathers only the edges that currently overflow, ramped by
    // the scroll offset. Set by the reconciler from ScrollEl/VirtualListEl.AutoEdgeFade. Band 0 = off.
    public bool  AutoEdgeFade;
    public float AutoEdgeFadeBand;        // DIP
    // Programmatic bring-into-view spring shape (zeta/omega/settle velocity/halflife) is now a FluentGpu.Scroll.ScrollInput
    // ScrollTo/ScrollBy argument (C/D/E, halflife via B) posted per-call, not a per-viewport latch here — the kernel
    // body carries the live chase state (scroll-v3-plan §2.1/§3.2).
    // Persistent scrollbar: keep the bar visible (thin rail) whenever content overflows, bypassing the auto-hide FadeT
    // gate at record time (hover still expands it). Set by the reconciler from ScrollEl.AlwaysShowScrollbar.
    public bool  AlwaysShowBar;
    public bool  SuppressBar;             // never draw the conscious scrollbar (paged shelves nav by pager, not the bar)
    public int   LoadingBarSuppressors;   // number of live descendant skeleton regions currently loading. This is
                                          // ownership-counted: a region may unmount while pending, and sibling regions
                                          // may resolve independently. A plain bool can therefore latch forever or clear
                                          // too early. Recorder suppression is LoadingBarSuppressors > 0.
    // FadeT/ExpandT/PointerOver/PointerOverScrollbar/IdleMs moved to FluentGpu.Scroll.ScrollBarChromeTable
    // (scroll-v3-plan §3.1/§4) — chrome is a UI-side ticker, never a motion writer. ScrollMoved (the old
    // synchronous-write reveal pulse) is subsumed by LastMovedFrame above; UserScrollActive is now a RESULT column
    // (see the group at the top of this struct) derived once inside ApplyMotion instead of by a separate FSM pass.

    // ── Predicate channel (generic scroll-binding model — design/plans/generic-hookable-scroll-engine-design.md §3.5/§7).
    // A fixed bitfield recomputed AFTER the integrator settles, struct-compared to ScrollFlagsPrev so a managed OnFlag
    // callback / flag-triggered time-animation fires only on an edge flip (CSS scroll-state container queries). Different
    // update cadence from the continuous progress channel (every frame) is what keeps both paths zero-alloc.
    public byte ScrollFlags;              // current frame's scroll-state vector
    public byte ScrollFlagsPrev;          // last frame's vector — struct-compare gate
    public const byte StuckTopBit = 1, SnappedBit = 4, ScrollableUpBit = 8,
                      ScrollableDownBit = 16, ScrolledFwdBit = 32, MovingNowBit = 64, IdleExpiredBit = 128;
    // StuckBottomBit (was 2) deleted (scroll-v3-plan §3.1) — dead bind channel per the plan §1 deletion list.
    // Distance-latched scroll direction: OffsetPrev advances to Offset only when |Offset − OffsetPrev| crosses a px
    // hysteresis, so ScrolledFwd is geometry-derived and dt-invariant (no raw per-frame delta that scales with dt).
    public float OffsetPrev;              // last latched offset (direction reference)
    public bool  DirLatched;              // OffsetPrev has been seeded (the first sample never spuriously flips the dir bit)

    // Nested-scroll chaining (the overscroll-behavior analog) is now Auto-only, always (scroll-v3-plan §2.2 "Policy is
    // always Auto — ScrollChainingMode deleted"): the kernel's drag-time chain routing needs no per-viewport mode.

    // Virtualization (ItemCount == 0 ⇒ a plain ScrollView, non-virtual).
    public int   ItemCount;
    public IVirtualLayout? Layout;        // pluggable layout (stack/grid/custom; IMeasuredVirtualLayout ⇒ variable-extent
                                          // estimate-then-correct + anchoring); null ⇒ the legacy Fenwick extent-table path
    public int   Overscan;                // rows realized beyond the viewport on each side
    public int   PersistentPrefixCount;   // leading logical items retained before the recyclable [First,Last) window
    public float ItemClipTopInset;         // viewport-space top clip for recyclable items; NaN = disabled
    public float ItemClipTopFadeBand;      // top alpha feather for recyclable items; 0 = disabled
    // One contiguous expand/collapse band over the flat virtual child ladder. Progress 0 = collapsed, 1 = expanded;
    // NaN disables the presentation. The recorder and input dispatcher consume the same row-range geometry.
    public int   DisclosureFirst;
    public int   DisclosureCount;
    public float DisclosureTop;
    public float DisclosureExtent;
    public float DisclosureT;
    public int   FirstRealized, LastRealized;
    public int   ExtentTableRef;          // -1 = uniform / non-virtual; else index into the ExtentTable slab
    public NodeHandle ContentNode;        // the single content child carrying the -ScrollOffset LocalTransform

    // Scroll anchoring (variable path): keep the topmost-visible item visually fixed across extent corrections.
    public int   AnchorIndex;
    public StringId AnchorKey;
    public float AnchorViewportDelta;

    // ── Scroll-position restoration (per content-identity, survives KeepAlive eviction). The reconciler keys a global
    // ScrollMemory cache by (ScrollScope, ScrollKey): ScrollKey is the app-supplied content identity (a route key), and
    // ScrollScope is the engine-computed enclosing KeepAlive-slot key (so the SAME content open in two tabs never shares a
    // saved position). On mount / content-identity change the reconciler posts a FluentGpu.Scroll.ScrollInput.Restore
    // command instead of latching Restore* fields here (scroll-v3-plan §3.2 Reconciler.cs:1921) — the kernel body keeps
    // retrying it each Reclamp() until the real, taller content extent can hold it. Managed refs are fine here
    // (dict-backed, like SnapPoints/Layout). The whole point of the cache living off-node is to outlive the freed
    // subtree on eviction.
    public string? ScrollKey;             // content identity (app-supplied); null ⇒ no restoration for this viewport
    public string? ScrollScope;           // enclosing KeepAlive-slot key (engine-computed at mount); composes the cache key

    public static ScrollState Default => new() { ExtentTableRef = -1, ZoomFactor = 1f, MinZoom = 0.1f, MaxZoom = 10f, ItemClipTopInset = float.NaN, DisclosureFirst = -1, DisclosureT = float.NaN, PrevArrangedFirst = 0, PrevArrangedLast = -1 };

    /// <summary>True when this viewport has any snap points configured (a fling lands on one).</summary>
    public readonly bool HasSnap => SnapInterval > 0f || (SnapPoints is { Length: > 0 });
}

/// <summary>
/// The DECLARATIVE snap-point spec: one POD an element (<c>ScrollEl.Snap</c> / <c>VirtualListEl.Snap</c>) or a control
/// (<c>ScrollOptions.Snap</c>) hands the reconciler to configure a viewport's <see cref="ScrollState"/> snap fields.
/// Mirrors the two WinUI kinds 1:1 (the math is <c>FluentGpu.Scroll.ScrollPhysics.SnapTarget</c>'s): a uniform <see cref="Interval"/> (the
/// <c>RepeatedScrollSnapPoint</c>) and/or an explicit ascending <see cref="Points"/> list (the irregular
/// <c>ScrollSnapPoint</c>). Both empty ⇒ no snapping.
/// <para>Snapping applies to FLINGS only — a wheel/keyboard/programmatic offset stays hard-clamped (the clamp contract),
/// so a control that wants a wheel/settle to REST on a boundary re-snaps itself through the programmatic path.</para>
/// <para>Declaring is OPT-IN and gated: null ⇒ the reconciler never touches the snap fields (a post-mount scene write
/// survives every reconcile); non-null ⇒ this element owns them and <see cref="ApplyTo"/> re-asserts on every patch. The
/// only allocation is the optional <see cref="Points"/> array the author supplies (the patch itself is alloc-free).</para>
/// </summary>
public readonly record struct SnapSpec(float Interval, float Start = 0f, float End = 0f, float[]? Points = null)
{
    /// <summary>A uniform grid every <paramref name="interval"/> DIP anchored at <paramref name="start"/> (a row height, a
    /// page stride). <paramref name="end"/> ≤ <paramref name="start"/> (the default) leaves the upper bound open, so the
    /// content clamp is the only bound.</summary>
    public static SnapSpec Every(float interval, float start = 0f, float end = 0f) => new(interval, start, end);

    /// <summary>An explicit ASCENDING-sorted set of snap offsets (the irregular case — variable-extent sections).</summary>
    public static SnapSpec At(params float[] points) => new(0f, 0f, 0f, points);

    /// <summary>True when this spec configures nothing — applying it CLEARS the viewport's snapping.</summary>
    public readonly bool IsEmpty => Interval <= 0f && (Points is null || Points.Length == 0);

    /// <summary>Write this declaration onto a viewport's snap fields. The single translation from spec → columns: the
    /// reconciler patch sites call it, and so does a control writing the scene directly, so the two paths can never
    /// disagree about field semantics.
    /// <para>Scroll-v3-plan §3.1: these four fields are config, not kernel state — <see cref="ApplyTo"/> only writes
    /// <see cref="ScrollState"/>'s own columns here. Getting them INTO the kernel body (so a fling can actually snap
    /// to them) is the layout's job: the reconciler/layout patch site that calls this also posts a
    /// <c>FluentGpu.Scroll.ScrollInput.SetFrame</c> carrying the same interval/start/end/points on the
    /// <c>ScrollFrameSpec</c> (§3.2) — <see cref="ApplyTo"/> has no scene/node handle to post that command itself, so
    /// it stays a pure column writer and the SetFrame call site is the one that actually arms snapping.</para></summary>
    public readonly void ApplyTo(ref ScrollState sc)
    {
        sc.SnapInterval = Interval > 0f ? Interval : 0f;
        sc.SnapStart = Start;
        sc.SnapEnd = End;
        sc.SnapPoints = Points is { Length: > 0 } p ? p : null;
    }
}

// Snap-point evaluation (WinUI ScrollPresenter "Mandatory" snap points, SnapPoint.cpp) — the old static ScrollSnap
// class that lived here is DELETED (scroll-v3-plan §3.1): its only caller was Animation/ScrollIntegrator.cs, which
// this phase deletes outright, and nothing else in Scene/Layout referenced it (verified). The math is ported
// verbatim into FluentGpu.Scroll.ScrollPhysics.SnapTarget (WP-A, §2 kernel shape) — SnapSpec above is unchanged; only
// the evaluator moved out of the Scene assembly boundary into the portable kernel.

/// <summary>
/// Grid layout spec for a grid container node (sparse side-table, O(grids)). The reconciler writes it from a
/// <c>GridEl</c>; the layout engine resolves column tracks at the final width and auto-flows the cells row-major.
/// </summary>
public struct GridSpec
{
    public TrackSize[] Columns;   // managed ref is fine in the dict-backed side-table
    public float ColGap, RowGap;
    public float RowHeight;       // NaN ⇒ auto (max child height per row)
    public float MinColWidth;     // > 0 ⇒ auto-fill: ignore Columns; pack as many equal 1fr tracks as fit at this min width
}

/// <summary>
/// Eased interaction progress for a node (sparse side-table, O(interacted nodes)). The InteractionAnimator eases
/// <c>HoverT</c>/<c>PressT</c> toward their targets on pointer enter/leave/press; the recorder lerps Fill/Border with them
/// for the WinUI ~83ms brush transition (instead of the instant flag switch).
/// </summary>
public struct InteractionAnim
{
    public float HoverT, HoverTarget, PressT, PressTarget;
    public float HoverStart, PressStart, HoverElapsedMs, PressElapsedMs;
    public float HoverDurationMs, PressDurationMs;
    public EasingSpec HoverEasing, PressEasing;
    // Record-time composited scale targets (1 = none). The recorder scales the node about its centre by
    // lerp(lerp(1,HoverScale,HoverT),PressScale,PressT) — e.g. a slider/scrollbar thumb that grows on hover, shrinks on
    // press. Composited only: it never changes layout or hit-testing (HitTest reads Bounds, not the world transform).
    public float HoverScale, PressScale;
    public const float ControlFasterMs = 83f;
    public const float ControlFastMs = 167f;
    public const float ControlNormalMs = 250f;
    public static InteractionAnim Default => new()
    {
        HoverScale = 1f,
        PressScale = 1f,
        HoverDurationMs = ControlFasterMs,
        PressDurationMs = ControlFasterMs,
        HoverEasing = Easing.FluentPopOpen,
        PressEasing = Easing.FluentPopOpen,
    };
}

/// <summary>
/// An implicit brush transition (WinUI <c>BrushTransition</c>, 83ms): when a LOGICAL state flip re-renders a node with a
/// different Fill/BorderColor/TextColor and the element opted in (<c>BrushTransitionMs</c>), the reconciler captures the
/// previously-DISPLAYED color here and the recorder cross-fades from it to the new resolved color as <c>T</c> advances
/// (linear-light, like the hover/press cross-fade). Sparse side-table — O(transitioning nodes), 0-alloc steady frames.
/// </summary>
public struct BrushAnim
{
    public ColorF FillFrom, BorderFrom, TextFrom;
    public float T;            // 0 → 1 progress (advanced by SceneStore.AdvanceBrushAnims at phase 7)
    public float DurationMs;
    public byte Channels;
    public const byte FillBit = 1, BorderBit = 2, TextBit = 4;
}

/// <summary>
/// Sparse text-edit state for an editor's TEXT node (side-table, O(editors)): caret geometry + caret-follow scroll +
/// in-flight IME composition span + focus/blink flags. Written by the editing control (UI thread, edit/drag time) and
/// the <c>CaretBlinker</c> phase-7 ticker; the recorder only READS it (plus the pooled decoration rects on
/// <see cref="SceneStore.SetTextEditRects"/>) to emit selection highlight / selected-text recolor / IME underlines /
/// the caret bar — retained scene state, never composed elements (0 alloc in phases 6–13).
/// </summary>
public struct TextEditState
{
    public int CompStart, CompLen;          // in-flight IME composition span (document indices); CompLen 0 = none
    public float ScrollX;                   // horizontal caret-follow offset (applied by the control as a transform)
    public float CaretX, CaretTop, CaretH;  // caret bar geometry in TEXT-NODE-LOCAL coords (already scrolled)
    public byte Flags;
    public const byte CaretVisible = 1, Focused = 2, SelectionActive = 4;
}

/// <summary>Hit-test / input column.</summary>
public struct InteractionInfo
{
    public uint HandlerMask;      // bit0 click, bit1 key, bit2 pointer, bit3 char, bit4 repeat, bit5 pressed, bit6 context,
                                  // bit7 focus, bit8 drag, bit9 explicit cursor, bit10 no-Enter-activate,
                                  // bit11 no-pointer-focus, bit12 wheel, bit13 selectable-text, bit14 span-links,
                                  // bit15 gesture, bit16 click-requests-context (widened ushort→uint for bit 16 —
                                  // input-a11y §6.5.1; every clear-site masks with the uint complement ~(uint)Bit —
                                  // a ushort-truncated complement would stomp bit 16)
    /// <summary>Meaningful only while <see cref="CursorBit"/> is set (an element-declared cursor); without the bit the
    /// dispatcher's hover walk skips this node and falls through to the system arrow — there is no clickable⇒hand default.</summary>
    public CursorId Cursor;
    public AutomationRole Role;   // semantic control role (set by control factories) → UIA ControlType / devtools / tests
    public bool Focusable;
    public int TabIndex;
    /// <summary>Access-key mnemonic (Alt+letter; uppercase VK 'A'..'Z' / '0'..'9'). 0 = none.</summary>
    public char AccessKey;
    /// <summary>WinUI FocusVisualMargin (negative = the focus ring expands OUTSIDE the bounds; WinUI templates use −3,
    /// Slider −7,0,−7,0). Written resolved by the reconciler (default −3 all around).</summary>
    public Edges4 FocusVisualMargin;
    /// <summary>Keyboard-accelerator chord: invoked from anywhere once focused routing leaves the key unhandled. 0 = none.</summary>
    public int AccelKey;
    public KeyModifiers AccelMods;
    public const ushort ClickBit = 1;
    public const ushort KeyBit = 2;
    public const ushort PointerBit = 4;   // position-aware press/drag (slider/scrollbar)
    public const ushort CharBit = 8;      // text (character) input handler present
    public const ushort RepeatBit = 16;   // clickable opts into press-and-hold auto-repeat (RepeatButton)
    public const ushort PressedBit = 32;  // position-aware press carrying click-count/modifiers (OnPointerPressed)
    public const ushort ContextBit = 64;  // right-click / Menu-key context request (OnContextRequested)
    public const ushort FocusBit = 128;   // focus-change handler present (OnFocusChanged) — reached via the dispatcher's
                                          // SetFocus (WinUI GotFocus/LostFocus), never via hit-testing; the bit lets the
                                          // dispatcher skip the handler-column lookup on every focus move
    public const ushort DragBit = 256;    // drag-reorder source (BoxEl.CanDrag): hit-testable; a press arms
                                          // Input.DragController and the drag lifecycle columns fire past the 4px box
    public const ushort CursorBit = 512;  // element declared an explicit Cursor (WinUI SetCursor): the hover walk
                                          // resolves it and STOPS here — an explicit Arrow masks an ancestor I-beam/hand
                                          // (TextBox delete button / PasswordBox reveal over the field's I-beam)
    public const ushort NoEnterActivateBit = 1024;  // clickable opts OUT of Enter activation (WinUI KeyPress::Button
                                                    // bAcceptsReturn=false — CheckBox/RadioButton/ToggleSwitch are
                                                    // Space-only; Enter falls through to normal key routing)
    public const ushort NoPointerFocusBit = 2048;   // WinUI AllowFocusOnInteraction=False: a press never moves focus
                                                    // to (or past) this focusable — Tab still reaches it
    public const ushort WheelBit = 4096;            // element-level OnPointerWheel handler (NumberBox value stepping):
                                                    // consulted before the viewport scroll; Handled stops the scroll
    public const ushort SelectableTextBit = 8192;   // read-only text selection (rtb-02, TextEl/SpanTextEl
                                                    // IsTextSelectionEnabled): hit-testable; the dispatcher runs the
                                                    // drag-select/word-select/Ctrl+C gestures against the text seam
                                                    // (WinUI TextSelectionManager — RichTextBlock.cpp:1730 default-on)
    public const ushort SpanLinksBit = 16384;       // the node's span run carries hyperlink spans (TextSpan.OnClick):
                                                    // hit-testable; the dispatcher resolves Hand over the span's laid
                                                    // rects and fires the span action on click (RichTextBlock.cpp:2995)
    public const ushort GestureBit = 32768;         // the node declared a UseGesture handler (§13): hit-testable so a
                                                    // tap/hold/pan over it opens a gesture arena even when the node is
                                                    // not otherwise clickable; set/cleared by SceneStore.SetGestureHandler
    public const uint HoverElevatePaintBit = 1u << 17;     // Element.HoverElevatePaint (scene-memory.md): a PAINT-ORDER
                                                    // discriminator ONLY — while this node is on the hover path the
                                                    // recorder defers it to paint above its non-elevated siblings
                                                    // (the declarative z-index of a hovered card). Like
                                                    // ClickRequestsContextBit it is deliberately NOT in
                                                    // AnyInteractiveMask or the hit-test self-hit mask: it never makes
                                                    // the node a hit/press/focus target. Clear as `~HoverElevatePaintBit`.
    public const uint HoverElevateClipRootBit = 1u << 18;  // Element.HoverElevateClipRoot (scene-memory.md): marks a
                                                    // clipping ancestor (a shelf viewport) as the ESCAPE level for its
                                                    // hover-elevated descendant — the recorder HOISTS the deferred
                                                    // HoverElevatePaint child out of this node's clip + edge-fade
                                                    // scope and records it after the scope closes, against the clip
                                                    // in effect OUTSIDE this node. Paint-order only, like the bit
                                                    // above: never a hit/press/focus target.
    public const uint BlocksDragArmBit = 1u << 19;         // Element.BlocksDragArm: a drag-ARM BARRIER. DragController's
                                                    // TryArm walks UP from the press target for the nearest DragBit
                                                    // node (a press on a row's child drags the row — the WinUI item-
                                                    // container rule); a child that is its own affordance — a card's
                                                    // play FAB, its "…" corner — must not be a handle for the card's
                                                    // drag, so this bit STOPS that walk at itself. Discriminator only:
                                                    // never a hit/press/focus target, deliberately outside
                                                    // AnyInteractiveMask. Clear as `~BlocksDragArmBit`.
    public const uint ClickRequestsContextBit = 1u << 16;  // BoxEl.ClickRequestsContext (input-a11y §6.5.1): a
                                                    // commit-time DISCRIMINATOR only — a left-click / touch-tap /
                                                    // Space-Enter activation of this node re-enters the context-request
                                                    // funnel at it (RequestContextFrom) instead of firing a click
                                                    // handler. Deliberately NOT in AnyInteractiveMask or the hit-test
                                                    // self-hit mask: the implied ClickBit already covers hit-test /
                                                    // press / hover / press-target. This is the ONE bit above 15 —
                                                    // hence HandlerMask is uint; clear it as `~(uint)ClickRequestsContextBit`.

    /// <summary>Any handler bit that makes a node a PRESS TARGET (interactive, though not necessarily focusable). A press
    /// on such a node is NOT an inert "background" press — the light-dismiss/modal scrim (Click/Pressed), an OnDrag/OnPointer
    /// node (Pointer), a CanDrag handle (Drag), a selectable label (SelectableText), a hyperlink span (SpanLinks), a
    /// NumberBox wheel-stepper (Wheel), a UseGesture node (Gesture). Excludes the pure MARKER bits (Key/Char/Focus/Repeat/
    /// Cursor/NoEnterActivate/NoPointerFocus) that don't make a node a press target. Consumed by InputDispatcher's
    /// clear-focus-on-inert-background-press rule (input-a11y §8). Note: scroll-viewport-ness is NOT a HandlerMask bit
    /// (it lives in ScrollState/NodeFlags.Scrollable) — the touch press site additionally excludes a pan candidate.</summary>
    public const ushort AnyInteractiveMask =
        ClickBit | PointerBit | PressedBit | ContextBit | DragBit | SelectableTextBit | SpanLinksBit | WheelBit | GestureBit;

    /// <summary>WinUI RepeatButton Delay/Interval (ms) for <see cref="RepeatBit"/> nodes. NaN (or non-positive) = the
    /// WinUI DP defaults (500/33, DependencyProperty.cpp:714-720); ScrollBar template arrows use Interval=50.</summary>
    public float RepeatDelayMs, RepeatIntervalMs;
}

/// <summary>A node's <c>UseGesture</c> declaration (input-a11y.md §13) — stored sparsely in <see cref="SceneStore"/>
/// (only subscribing nodes have a row). One handler slot per Phase-3 usable kind (<see cref="GestureType.Tap"/> /
/// <see cref="GestureType.Hold"/> / <see cref="GestureType.Pan"/>); a component declaring several gestures fills several
/// slots. The handler is the only GC edge (a mount-time user closure, like every <c>HandlerTable</c> column — foundations
/// §1: GC at the edge is allowed). Reserved kinds (DoubleTap/RightTap/Drag/Pinch) are accepted by the hook but not yet
/// routed (Phase-4), so they need no slot here today.</summary>
public struct GestureSubscription
{
    private Action<GestureEventArgs>? _tap, _hold, _pan;

    /// <summary>True while any usable-kind handler is installed (the row is dropped when the last one clears).</summary>
    public readonly bool HasAny => _tap is not null || _hold is not null || _pan is not null;

    /// <summary>The installed handler for <paramref name="kind"/>, or null (reserved kinds always null this Phase).</summary>
    public readonly Action<GestureEventArgs>? Handler(GestureType kind) => kind switch
    {
        GestureType.Tap => _tap,
        GestureType.Hold => _hold,
        GestureType.Pan => _pan,
        _ => null,   // DoubleTap/RightTap/Drag/Pinch: Phase-4 routing — declared but not wired
    };

    /// <summary>Set (or clear, null) the handler for a usable kind. Reserved kinds are ignored (no slot).</summary>
    public void Set(GestureType kind, Action<GestureEventArgs>? handler)
    {
        switch (kind)
        {
            case GestureType.Tap: _tap = handler; break;
            case GestureType.Hold: _hold = handler; break;
            case GestureType.Pan: _pan = handler; break;
            // DoubleTap/RightTap/Drag/Pinch: accepted by UseGesture for forward-compat, not stored/routed yet.
        }
    }
}
