using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Scene;

public enum VisualKind : byte { None = 0, Box = 1, Text = 2, Image = 3, PolylineStroke = 4, TabShape = 5, IconLayer = 6 }

/// <summary>Sparse image-only payload kept out of the dense paint column. The source id stays in
/// <see cref="NodePaint.ImageId"/>; <see cref="DerivedImageId"/> is selected only after its bake reaches Ready.</summary>
public readonly record struct ImageVisualEffects(int DerivedImageId, ColorF Overlay, ImageMaskSpec Mask);

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

    public FlexJustify Justify;   // container: main-axis distribution
    public FlexAlign AlignItems;  // container: default child cross alignment
    public bool Wrap;             // container: wrap children to multiple lines when the main axis is constrained

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
/// column. Ownership (layout.md §6 / architecture-spec §5.5): <b>Input</b> owns <c>Offset*</c> (clamped to the
/// published content); <b>Layout</b> publishes <c>Content*</c>/<c>Viewport*</c>; the <b>virtualizer</b> owns the
/// <c>Item*</c> / realized-range / anchor fields. Scroll is layout-free: the <c>-ScrollOffset</c> translation is the
/// <see cref="ContentNode"/>'s <c>LocalTransform</c>, never a relayout.
/// </summary>
public struct ScrollState
{
    public float OffsetX, OffsetY;        // Input-owned scroll position (DIP) — the live (eased) offset
    public float TargetX, TargetY;        // smooth-scroll destination (the offset eases toward it; == Offset when idle)
    public float ContentW, ContentH;      // Layout-published full content extent (DIP)
    public float ViewportW, ViewportH;    // Layout-published viewport inner size (for clamp + window math)
    public byte  Orientation;             // 0 = vertical scroll (Y), 1 = horizontal scroll (X)
    public float FlingVelocity;           // seed/live coast velocity along Orientation (px/s, signed in offset space); REUSED by
                                          // Fling (friction coast) AND WheelAnimating (the velocity-preserving chase carries it).
    // ── §2.4 intent columns (scroll-feel-rework-v2). The dispatcher/scrollbar/programmatic callers RECORD these + arm the
    // node; the ScrollIntegrator (phase 7) is the sole consumer + the sole Offset/OverscrollPx writer (§2.1).
    public byte  Phase;                   // the §2.2 state enum (ScrollIntegrator.Idle/TouchpadTracking/WheelAnimating/Fling/
                                          // Overscroll/SnapBack). Replaces the untyped ScrollMode 0/1/2/3.
    public byte  PhaseFlags;              // OsOwned | Programmatic | Wheel bitfield (auxiliary sub-modes; see the const bits)
    public float PendingTargetX;          // WheelAnimating/Programmatic/scrollbar accumulated, hard-clamped chase target on X
                                          // (absolute offset-space; NaN = none). §4.2: PendingTarget = clamp(PendingTarget + distance, 0, max).
    public float PendingTargetY;          // …and on Y. Per axis (a viewport scrolls one axis per Orientation). NaN = none.
    public float PendingRawOffset;        // §2.1 direct-touch (WM_POINTER) pan intent: the UNCLAMPED desired offset (anchor −
                                          // panDelta) on the scroll axis. Per-NODE (not the singleton resampler) so CONCURRENT
                                          // multi-touch pans on sibling scrollers are independent; the phase-7 integrator splits
                                          // it into a clamped offset + rubber-band once (TouchpadTracking, PhaseTouchPan). NaN = none.
    public float PendingAnchorShift;      // accumulated virtualization anchor re-pin delta (DIP) since the last integrator tick.
                                          // The layout re-pin (ArrangeVirtualVariable/Measured) shifts the offset to keep the
                                          // topmost item fixed across extent corrections; it records the shift HERE so the phase-7
                                          // ScrollIntegrator can move its own live intents (resampler anchor / chase targets) by the
                                          // same amount instead of fighting the re-pin. Consumed + zeroed every Tick (default 0).
    public int PrevArrangedFirst;         // the realized row window [first..last] the PREVIOUS virtual arrange saw. A row outside
    public int PrevArrangedLast;          // it is FRESH this arrange: its first measure can be transiently short (deferred inner
                                          // content lands a frame later), so a fresh row ABOVE the anchor must not push that
                                          // transient into the extent table — the dip+restore re-pin pair was the felt scroll
                                          // jitter. Default 0/-1 = empty window (a mount treats every row as fresh).
    public bool  FlingRetargeted;         // a snap-configured fling has had its velocity re-solved to land on the snap value
                                          // (the ScrollIntegrator does this ONCE on fling entry; reset when a fresh fling is seeded).
    public float FlingFromOffset;         // the offset captured when the fling was seeded (the impulse "ignored value" anchor)
    public float FlingSnapTarget;         // the exact snap value a retargeted fling lands on (the ScrollIntegrator writes THIS
                                          // on settle, so discrete-integration drift never leaves it a fraction off the snap). NaN = no snap target.
    public bool  ContentSized;            // auto-size to content then clamp (popup lists); false = hard viewport
    // Pinch-zoom (WinUI ScrollPresenter ZoomFactor; opt-in like ScrollingZoomMode — default Disabled). When Zoomable, a
    // SECOND touch contact over this viewport scales the content about the gesture midpoint (Input owns ZoomFactor; it is
    // applied as a TRANSFORM-only term composed with the -offset translation on the ContentNode, never a relayout). The
    // committed factor scales the content extent the offset clamps against (Content*Zoom − Viewport), so a zoomed-in pan
    // reaches the full magnified content. Defaults: factor 1, Min 0.1 / Max 10.0 (ScrollPresenter.h:63-64).
    public bool  Zoomable;                // the viewport opts into pinch-zoom (WinUI ScrollingZoomMode.Enabled)
    public float ZoomFactor;              // committed content scale (1 = unzoomed); Input clamps to [MinZoom, MaxZoom]
    public float MinZoom, MaxZoom;        // zoom clamp bounds (ScrollPresenter s_defaultMin/MaxZoomFactor = 0.1 / 10.0)
    // ── Snap points (WinUI ScrollPresenter Mandatory snap-point model — controls\dev\ScrollPresenter\SnapPoint.cpp).
    // A touch fling retargets its friction decay to land EXACTLY on the nearest applicable snap value (the ScrollIntegrator
    // computes the natural rest from v0 over the decay integral, picks the snap per the zone rules, then re-solves the
    // velocity so the SAME decay curve lands there — see ScrollSnap + ScrollIntegrator). POD, per-viewport: a uniform
    // interval (the WinUI RepeatedScrollSnapPoint, e.g. a LoopingSelector item row) and/or an explicit sorted list (the
    // WinUI ScrollSnapPoint irregular case). Both empty (SnapInterval ≤ 0 ∧ SnapPoints null) ⇒ no snapping, the plain
    // fling. The snap math is "Mandatory" (no applicable-range gaps): every value falls in some snap point's zone, the
    // zone boundary between two adjacent points being their midpoint (SnapPoint.cpp Influence(), :453/:474). Snapping
    // applies to flings only (a wheel/keyboard/programmatic offset is hard-clamped, never snapped — matching the clamp
    // contract); the offset axis is Orientation's.
    public float SnapInterval;            // uniform snap spacing (DIP) on the scroll axis; ≤ 0 = no interval snapping
    public float SnapStart;               // first snap value / lower bound of the repeated zone (DIP; default 0)
    public float SnapEnd;                 // upper bound of the repeated zone (DIP); ≤ SnapStart = open (clamp-max bound)
    public float[]? SnapPoints;           // optional explicit sorted snap values (the irregular case); null = none. The
                                          // managed ref is fine in the dict-backed side-table (like Layout / GridSpec.Columns).
    // Rubber-band overscroll (WinUI manipulation overpan; TOUCH-PAN ONLY). A claimed touch pan dragging PAST the [0,max]
    // clamp produces a transient visual DISPLACEMENT band — a SEPARATE transform term composed with the -offset
    // translation on the ContentNode — so the content visibly gives with resistance while OffsetX/Y NEVER leaves [0,max]
    // (the SetScrollOffset clamp contract is untouched: wheel/keyboard/programmatic stay hard-clamped, no band). On
    // release the band springs back to 0 with the critically-damped StepSpring in phase 7 (TransformDirty only). Signed
    // in offset space (negative = pulled past the top/left, positive = past the bottom/right). 0 at rest.
    public float OverscrollPx;            // current visual displacement past the clamp (Animation springs it back to 0)
    public float OverscrollVel;           // spring velocity for the release spring-back (px/s)
    public bool  Overscrolling;           // TOUCH only: finger drives the band 1:1 (no spring-back yet); cleared on pointer-up
    public float OverscrollReleaseOmega;  // optional per-release spring frequency; 0 = the active ScrollTuning default.
                                          // Touchpad uses a faster recoil than direct touch without splitting the band model.

    public float FadeT;                   // scrollbar indicator opacity 0..1 (eased in on scroll/hover, auto-hides after idle)
    public float ExpandT;                 // WinUI conscious scrollbar expansion 0=thin indicator, 1=full gutter + buttons

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
    // Per-viewport PROGRAMMATIC bring-into-view spring override (both 0 = the default critically-damped 95 ms-halflife
    // chase). Zeta in (0,1) + Omega (rad/s) select the UNDERDAMPED closed form in the ScrollIntegrator's Programmatic
    // WheelAnimating branch — the Apple-Music lyric follow (soft ~0.65 s settle, a whisper of overshoot) needs ζ<1,
    // which the fixed ζ=1 exponential cannot express. Velocity-continuous retargets work identically in both forms.
    public float ProgrammaticZeta;        // damping ratio; 0 (default) ⇒ legacy critically-damped chase
    public float ProgrammaticOmega;       // natural frequency ω0 rad/s; settle ≈ 4/(ζ·ω0)
    // Persistent scrollbar: keep the bar visible (thin rail) whenever content overflows, bypassing the auto-hide FadeT
    // gate at record time (hover still expands it). Set by the reconciler from ScrollEl.AlwaysShowScrollbar.
    public bool  AlwaysShowBar;
    public bool  SuppressBar;             // never draw the conscious scrollbar (paged shelves nav by pager, not the bar)
    public int   LoadingBarSuppressors;   // number of live descendant skeleton regions currently loading. This is
                                          // ownership-counted: a region may unmount while pending, and sibling regions
                                          // may resolve independently. A plain bool can therefore latch forever or clear
                                          // too early. Recorder suppression is LoadingBarSuppressors > 0.
    public float IdleMs;                  // time since the last scroll movement / hover (drives the auto-hide)
    public bool PointerOver;              // pointer is inside this scroll viewport
    public bool PointerOverScrollbar;     // pointer is inside this viewport's scrollbar gutter
    public bool ScrollMoved;              // a SYNCHRONOUS offset write (touch content-pan / thumb-drag / edge-auto-scroll)
                                          // moved the offset this frame — a one-frame reveal pulse the ScrollIntegrator reads
                                          // (and clears) so the thin indicator shows during a touch pan even though Offset==Target.
                                          // Set by Input.SetScrollOffset on a real move; consumed every Tick. Reveals FadeT only —
                                          // never PointerOver/ExpandT (a content pan is not a lane hover), so the bar idle-hides
                                          // naturally on the move stopping (the WinUI TouchIndicator shows through a manipulation).
    public bool UserScrollActive;         // set by ScrollIntegrator.Tick each frame = the viewport is in USER-scroll motion
                                          // (TouchpadTracking / Fling incl. OsOwned / WheelAnimating / SnapBack) this frame — i.e.
                                          // movingNow && (PhaseFlags & PhaseProgrammatic) == 0. FALSE for a programmatic bring-into-view
                                          // ease, its SETTLE frame (off==tgt), a stationary relayout that re-asserts the content
                                          // transform, and at rest. SceneRecorder's self-blur (DoF) defer keys off THIS so the
                                          // auto-scroll settle + relayout no longer drop the whole panel's blur for one frame
                                          // on a lyric line-advance (the DoF-dropout bug). Only a real user scroll defers.

    // ── Predicate channel (generic scroll-binding model — design/plans/generic-hookable-scroll-engine-design.md §3.5/§7).
    // A fixed bitfield recomputed AFTER the integrator settles, struct-compared to ScrollFlagsPrev so a managed OnFlag
    // callback / flag-triggered time-animation fires only on an edge flip (CSS scroll-state container queries). Different
    // update cadence from the continuous progress channel (every frame) is what keeps both paths zero-alloc.
    public byte ScrollFlags;              // current frame's scroll-state vector
    public byte ScrollFlagsPrev;          // last frame's vector — struct-compare gate
    public const byte StuckTopBit = 1, StuckBottomBit = 2, SnappedBit = 4, ScrollableUpBit = 8,
                      ScrollableDownBit = 16, ScrolledFwdBit = 32, MovingNowBit = 64, IdleExpiredBit = 128;
    // Distance-latched scroll direction: OffsetPrev advances to Offset only when |Offset − OffsetPrev| crosses a px
    // hysteresis, so ScrolledFwd is geometry-derived and dt-invariant (no raw per-frame delta that scales with dt).
    public float OffsetPrev;              // last latched offset (direction reference)
    public bool  DirLatched;              // OffsetPrev has been seeded (the first sample never spuriously flips the dir bit)

    // Nested-scroll chaining (the overscroll-behavior analog; design §10). 0 = Auto (an inner pan at its edge hands the
    // residual to the nearest same-axis ancestor scroller, Compose-style), 1 = Contain (the inner rubber-bands; no
    // hand-off), 2 = None (no band, no hand-off). Wheel already bubbles via ScrollAxis; this governs the TOUCH pan path.
    public byte  Chaining;                // 0 = Auto, 1 = Contain, 2 = None

    // Virtualization (ItemCount == 0 ⇒ a plain ScrollView, non-virtual).
    public int   ItemCount;
    public IVirtualLayout? Layout;        // pluggable layout (stack/grid/custom; IMeasuredVirtualLayout ⇒ variable-extent
                                          // estimate-then-correct + anchoring); null ⇒ the legacy Fenwick extent-table path
    public int   Overscan;                // rows realized beyond the viewport on each side
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
    // saved position). On mount / content-identity change the reconciler seeds Offset + arms RestorePending; on
    // unmount/content-swap it saves the live offset. RestorePending re-asserts the restored offset on EACH layout (the
    // loading skeleton is short → the offset can't be applied until the real, taller content lands) until the extent can
    // satisfy it — then it releases. Cleared early by a user scroll (Input). Managed refs are fine here (dict-backed,
    // like SnapPoints/Layout). The whole point of the cache living off-node is to outlive the freed subtree on eviction.
    public string? ScrollKey;             // content identity (app-supplied); null ⇒ no restoration for this viewport
    public string? ScrollScope;           // enclosing KeepAlive-slot key (engine-computed at mount); composes the cache key
    public bool  RestorePending;          // a seeded offset is waiting for the real content extent to be able to hold it
    public float RestoreX, RestoreY;      // the target offset to restore (offset space)

    // ── §2.4 PhaseFlags bits (auxiliary sub-modes on Phase; not separate states).
    /// <summary>Fling consuming OS <c>INERTIA</c> deltas verbatim (DM path) — no decay math, no estimator.</summary>
    public const byte PhaseOsOwned = 1;
    /// <summary>WheelAnimating seeded by a bring-into-view (halflife 95ms) — EXCLUDED from <see cref="UserScrollActive"/>.</summary>
    public const byte PhaseProgrammatic = 2;
    /// <summary>WheelAnimating driven by a mouse notch — hard-stops at extents, never bands (§2.2 extent asymmetry).</summary>
    public const byte PhaseWheel = 4;
    /// <summary>WheelAnimating recorded as an EXACT absolute-offset intent (scrollbar thumb-drag, pinch focal offset, edge
    /// auto-scroll): the phase-7 integrator jumps to <see cref="PendingTargetX"/>/Y verbatim (no spring, no lag) and ends,
    /// so a synchronous manipulation still lands the same frame it was recorded while the offset stays single-writer
    /// (scroll-feel-rework-v2 §2.1 — these callers RECORD PendingTarget, they never write the offset themselves).</summary>
    public const byte PhaseImmediate = 8;
    /// <summary>TouchpadTracking driven by a per-node direct-touch (WM_POINTER) pan: the integrator reads
    /// <see cref="PendingRawOffset"/> (this node's own unclamped desired offset) instead of the singleton PTP resampler, so
    /// two fingers panning two sibling scrollers stay independent (the resampler is single-gesture). Splits into a clamped
    /// offset + rubber-band exactly like the resampler path (scroll-feel-rework-v2 §4.1/§4.4).</summary>
    public const byte PhaseTouchPan = 16;

    public static ScrollState Default => new() { ExtentTableRef = -1, ZoomFactor = 1f, MinZoom = 0.1f, MaxZoom = 10f, FlingSnapTarget = float.NaN, PendingTargetX = float.NaN, PendingTargetY = float.NaN, PendingRawOffset = float.NaN, PrevArrangedFirst = 0, PrevArrangedLast = -1 };

    /// <summary>True when this viewport has any snap points configured (a fling lands on one).</summary>
    public readonly bool HasSnap => SnapInterval > 0f || (SnapPoints is { Length: > 0 });
}

/// <summary>
/// Snap-point evaluation (WinUI ScrollPresenter "Mandatory" snap points), ported as portable plain math from
/// <c>microsoft-ui-xaml controls\dev\ScrollPresenter\SnapPoint.cpp</c> — the applicable-zone / midpoint model, with no
/// Composition <c>ExpressionAnimation</c> (the engine's fling integrator is downstream of the arbitration clock, not an
/// OS InteractionTracker). Two snap kinds, exactly as WinUI:
/// <list type="bullet">
/// <item><b>Repeated</b> (uniform interval — <c>RepeatedScrollSnapPoint</c>): the resting value is the nearer of the two
/// interval multiples bracketing the natural value. <c>Evaluate</c> (SnapPoint.cpp:1032-1059): <c>first = offset −
/// floor((offset − start)/interval)·interval</c> (:851), <c>prev = floor((v−first)/interval)·interval + first</c> (:1040),
/// <c>next = prev + interval</c> (:1041), pick <c>prev</c> iff <c>(v−prev) ≤ (next−v)</c> (:1043).</item>
/// <item><b>Irregular</b> (explicit sorted list — <c>ScrollSnapPoint</c>): each value owns the applicable zone
/// <c>[midpoint-to-prev, midpoint-to-next]</c>, the boundary between two adjacent points being their midpoint
/// <c>(a+b)/2</c> (<c>Influence</c>, SnapPoint.cpp:453/:474, "Mandatory" branch :467 returns the bare midpoint). A value
/// is snapped to whichever point's zone contains it — i.e. the nearest point (<c>Evaluate</c>, :527-536).</item>
/// </list>
/// <b>Impulse</b> (a fling, vs a within-content drag): WinUI marks the snap point AT the gesture-start position as the
/// "ignored value" so the inertia must advance at least to the NEXT snap point — a flick never settles back where it
/// started (<c>UpdateSnapPointsIgnoredValue</c> / <c>ImpulseInfluence</c>, ScrollPresenter.cpp:2243, SnapPoint.cpp:478).
/// We realize that as: in impulse mode, if the natural value snaps back to the start point, step one snap in the fling
/// direction instead. All math is <c>double</c> (the WinUI type) collapsed to the value the offset clamps to.
/// </summary>
public static class ScrollSnap
{
    /// <summary>Snap <paramref name="natural"/> (the unsnapped natural resting value) to this viewport's nearest
    /// applicable snap value. <paramref name="impulse"/> = the move is a fling (apply the ignored-start rule);
    /// <paramref name="fromOffset"/> is the gesture-start offset (the ignored value lives there). Returns the snapped
    /// value; identity when the viewport has no snap points. Pure; 0-alloc.</summary>
    public static float Snap(in ScrollState sc, float natural, bool impulse, float fromOffset)
    {
        if (!sc.HasSnap) return natural;

        // Both kinds present ⇒ evaluate each and take the candidate NEARER the natural value (the WinUI sorted-set scan
        // resolves to the closest applicable zone; with two independent sources the nearest snapped candidate wins).
        float best = natural;
        float bestDist = float.PositiveInfinity;

        if (sc.SnapInterval > 0f)
        {
            float cand = SnapRepeated(natural, sc.SnapInterval, sc.SnapStart, sc.SnapEnd);
            float d = MathF.Abs(cand - natural);
            if (d < bestDist) { best = cand; bestDist = d; }
        }
        if (sc.SnapPoints is { Length: > 0 } pts)
        {
            float cand = SnapIrregular(natural, pts);
            float d = MathF.Abs(cand - natural);
            if (d < bestDist) { best = cand; bestDist = d; }
        }

        if (!impulse) return best;

        // Impulse: the snap point at the gesture-start position is "ignored" (SnapPoint.cpp:478 ImpulseInfluence) — a
        // flick must travel at least one snap. If the natural value snapped right back to the start point, advance one
        // snap step in the fling's travel direction (natural − fromOffset gives the sign).
        float startSnap = Snap(in sc, fromOffset, impulse: false, fromOffset);
        if (MathF.Abs(best - startSnap) < 0.5f)
        {
            float dir = natural - fromOffset;
            if (MathF.Abs(dir) > 0.0001f)
                best = NextSnap(in sc, startSnap, dir > 0f);
        }
        return best;
    }

    /// <summary>The repeated-interval resting value (SnapPoint.cpp Evaluate :1032-1059; First :851).</summary>
    private static float SnapRepeated(float value, float interval, float start, float end)
    {
        // first = start (offset == start here: a uniform grid anchored at SnapStart). prev/next bracket the value.
        float first = start;
        float prev = MathF.Floor((value - first) / interval) * interval + first;
        float next = prev + interval;
        float snapped = (value - prev) <= (next - value) ? prev : next;
        // Clamp to the configured [start, end] band when end is a real upper bound (else open).
        if (end > start) snapped = Math.Clamp(snapped, start, end);
        else if (snapped < start) snapped = start;
        return snapped;
    }

    /// <summary>The irregular nearest-point resting value via the midpoint zones (SnapPoint.cpp Influence :453/:474 +
    /// Evaluate :527-536). The list is assumed sorted ascending; nearest point == zone-containing point.</summary>
    private static float SnapIrregular(float value, float[] pts)
    {
        float best = pts[0];
        float bestDist = MathF.Abs(pts[0] - value);
        for (int i = 1; i < pts.Length; i++)
        {
            float d = MathF.Abs(pts[i] - value);
            if (d < bestDist) { best = pts[i]; bestDist = d; }
        }
        return best;
    }

    /// <summary>The snap value immediately after (<paramref name="forward"/>) or before <paramref name="from"/> — used by
    /// the impulse ignored-start rule. Combines both kinds: the nearest snap strictly on the requested side.</summary>
    private static float NextSnap(in ScrollState sc, float from, bool forward)
    {
        float best = from;
        float bestGap = float.PositiveInfinity;
        if (sc.SnapInterval > 0f)
        {
            float cand = forward ? from + sc.SnapInterval : from - sc.SnapInterval;
            if (sc.SnapEnd > sc.SnapStart) cand = Math.Clamp(cand, sc.SnapStart, sc.SnapEnd);
            float gap = MathF.Abs(cand - from);
            if (gap > 0.5f && gap < bestGap) { best = cand; bestGap = gap; }
        }
        if (sc.SnapPoints is { Length: > 0 } pts)
        {
            for (int i = 0; i < pts.Length; i++)
            {
                float p = pts[i];
                bool side = forward ? p > from + 0.5f : p < from - 0.5f;
                if (!side) continue;
                float gap = MathF.Abs(p - from);
                if (gap < bestGap) { best = p; bestGap = gap; }
            }
        }
        return best;
    }
}

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
