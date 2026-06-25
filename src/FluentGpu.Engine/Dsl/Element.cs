using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Dsl;

/// <summary>Immutable description of a UI node (the "virtual DOM"). Cheap to build, never touches the scene directly.</summary>
public abstract record Element
{
    public string? Key { get; init; }

    /// <summary>Optional shared-element (connected-animation / matched-geometry "Hero") key: a node tagged with a
    /// MorphId is a transition participant — when a like-tagged node mounts on the next route, this node's art flies
    /// from the rect it occupied to the new node's rect (backdrop-effects-animation.md §5.4/§5.6). Drives the engine's
    /// <c>ConnectedAnimation</c> registry. Null = not a participant (the default).</summary>
    public string? MorphId { get; init; }

    /// <summary>Generic scroll-driven bindings (design/plans/generic-hookable-scroll-engine-design.md): each entry slaves
    /// one of this node's compositor properties (transform / opacity / clip / presented-size) to a normalized scroll
    /// progress of its enclosing <see cref="ScrollEl"/> — or pins it (sticky, <see cref="ScrollBindDsl.PinTop"/>) /
    /// stretches it (overscroll hero, <see cref="ScrollBindDsl.StretchFromTop"/>). Evaluated allocation-free in the frame
    /// loop. Empty = none. This is the one generic surface that subsumes the old StickyTop/OnPinned/ScrollStretchHeader.</summary>
    public ScrollBindDsl[] ScrollBinds { get; init; } = [];

    /// <summary>Stable per-record-type id for integer type-dispatch in the reconciler (the source-gen'd ElementTypeId).</summary>
    public abstract ushort ElementTypeId { get; }

    /// <summary>Skeleton-derivation opt-out (the native skeleton-loading kit): <see cref="SkeletonMode.Off"/> ⇒ the
    /// deriver emits a same-sized empty spacer (keeps the slot, no shimmer bar) for this node; <see cref="SkeletonMode.Auto"/>
    /// (default) ⇒ the deriver maps it to a shimmer. Construction-time metadata read ONLY by <c>SkeletonDeriver</c> —
    /// never written to a scene column, so it costs nothing at runtime. Set with <c>el.Skeletonized(false)</c>.</summary>
    public SkeletonMode SkeletonMode { get; init; }
    /// <summary>A bespoke shimmer subtree the deriver substitutes for this node (overrides the auto-map). Set with
    /// <c>el.Skel(customShimmer)</c>.</summary>
    public Element? SkeletonOverride { get; init; }

    // ── Declarative motion (the rework's authoring surface; on the BASE element so EVERY element shares ONE motion
    //    vocabulary — fixing the per-record HoverScale/BrushTransitionMs duplication). ADDITIVE + inert until the
    //    AnimScheduler switch-over wires the reconciler bake (Reconciler/AnimBake) + the InteractionState resolver.
    /// <summary>Implicit on-change transition (the CSS/SwiftUI primitive): when a bound channel's realized value
    /// changes, interpolate FROM the current value over this motion token instead of snapping — for ANY channel,
    /// incl. Fill/Color (the engine-owned generalization of the per-element <c>BrushTransitionMs</c>). Null = snap.</summary>
    public FluentGpu.Animation.MotionTokenDef? Transition { get; init; }
    /// <summary>Gesture-state targets (Framer <c>whileHover</c>/<c>whileTap</c>): the node springs to these while
    /// hovered/pressed/focused and back to rest on release, via the InteractionState priority resolver (generalizing
    /// the discrete <c>HoverScale</c>/<c>HoverOpacity</c> spellings).</summary>
    public FluentGpu.Animation.MotionTarget? WhileHover { get; init; }
    public FluentGpu.Animation.MotionTarget? WhilePressed { get; init; }
    public FluentGpu.Animation.MotionTarget? WhileFocus { get; init; }
    /// <summary>Declarative enter terminal (Framer <c>initial</c>; CSS <c>@starting-style</c>): the node animates FROM
    /// this to identity on mount, seeded under a Presence boundary.</summary>
    public EnterExit? Enter { get; init; }
    /// <summary>Declarative exit terminal (Framer <c>exit</c>; CSS <c>allow-discrete</c>): a removed node animates TO
    /// this before its structural removal (deferred via the DetachedAnimSlab / Presence completion gate).</summary>
    public EnterExit? Exit { get; init; }
    /// <summary>Per-child entrance stagger (seconds): under a Presence/list boundary, child <c>i</c>'s Enter delay is
    /// <c>index * Stagger</c>, BAKED at reconcile (no runtime closure, no O(n²) sort).</summary>
    public float Stagger { get; init; }
    /// <summary>Declarative auto-FLIP on layout change (the first-class spelling of the per-box <c>Animate</c> opt-in).
    /// Any layout move/resize of this node animates via transform only.</summary>
    public LayoutTransition? Layout { get; init; }

    /// <summary>FLIP coherence (Framer <c>layout</c> relativeTarget): compute this node's FLIP relative to the frame of
    /// the node carrying this <see cref="MorphId"/> (a shared-layout GROUP anchor) instead of its layout parent — so a
    /// reordered/moved item animates coherently WITH the anchor rather than double-counting the anchor's own motion.
    /// Null = the default parent-relative coherence. Resolved to the target node at FLIP-capture time.</summary>
    public string? RelativeTo { get; init; }
}

/// <summary>Per-node skeleton-derivation policy (see <see cref="Element.SkeletonMode"/>).</summary>
public enum SkeletonMode : byte { Auto, Off }

/// <summary>The form-validation visual state of a control surface (form-validation.md). <see cref="Warning"/> is
/// reserved for non-blocking advisory styling; v1 styles only <see cref="Error"/> (red border + message).</summary>
public enum ValidationState : byte { None, Warning, Error }

/// <summary>A box: container/layout node and/or a filled, optionally-clickable surface (VStack/HStack/Button/Box).</summary>
public sealed record BoxEl : Element
{
    public override ushort ElementTypeId => 1;

    public byte Direction { get; init; }          // 0 = row, 1 = column
    public float Gap { get; init; }
    public Edges4 Padding { get; init; }
    public Edges4 Margin { get; init; }
    /// <summary>Unified channel (Prop&lt;T&gt;): a static color, a <c>Func&lt;ColorF&gt;</c> thunk, or a concrete signal.</summary>
    public Prop<ColorF> Fill { get; init; }
    public ColorF HoverFill { get; init; }
    public ColorF PressedFill { get; init; }
    public ColorF BorderColor { get; init; }
    public ColorF HoverBorderColor { get; init; }    // A==0 ⇒ recorder auto-lightens BorderColor on hover; else eases to this exact state token
    public ColorF PressedBorderColor { get; init; }  // A==0 ⇒ recorder auto-darkens BorderColor on press; else eases to this exact state token
    public float BorderWidth { get; init; }
    /// <summary>Dashed (solid) border: the dash ON/OFF run lengths in DIP along the perimeter (e.g. 6/4). Both 0 (the
    /// default) = a solid stroke. Applies to the plain <see cref="BorderColor"/> stroke only (not a gradient border).
    /// The "drop zone" look (and the <c>DropZone</c> control) uses this.</summary>
    public float BorderDashOn { get; init; }
    public float BorderDashOff { get; init; }
    public CornerRadius4 Corners { get; init; }
    /// <summary>Form-validation visual state (form-validation.md). Bind it to a field's error memo
    /// (<c>Validation = Prop.Of(() =&gt; field.Error.Value.IsValid ? ValidationState.None : ValidationState.Error)</c>):
    /// on <see cref="ValidationState.Error"/> the reconciler resolves the theme critical color and the recorder swaps
    /// this node's border to it. A bound channel — no re-render per keystroke; the write is equality-gated.</summary>
    public Prop<ValidationState> Validation { get; init; }

    // Optional rich paint (carried into sparse scene side-tables by the reconciler; default = none).
    public ShadowSpec? Shadow { get; init; }       // soft drop shadow / elevation, drawn beneath the fill
    public ArcSpec? Arc { get; init; }             // circular-arc stroke (ProgressRing) — SDF ring trimmed to a sweep
    public GradientSpec? Gradient { get; init; }   // gradient fill — supersedes Fill at record time when set
    public GradientSpec? BorderBrush { get; init; }// gradient border stroke (WinUI ControlElevationBorderBrush); needs BorderWidth > 0
    // Stateful gradient variants: the recorder per-frame interpolates the resting gradient's stops toward these by the
    // eased hover/press progress (same HoverT/PressT that cross-fades a solid Fill). Must share the resting stop count.
    public GradientSpec? HoverGradient { get; init; }
    public GradientSpec? PressedGradient { get; init; }
    public GradientSpec? HoverBorderBrush { get; init; }
    public GradientSpec? PressedBorderBrush { get; init; }
    public AcrylicSpec? Acrylic { get; init; }     // per-node frosted-glass backdrop (blur + tint + noise)
    public bool TabShape { get; init; }            // selected TabView header: rounded top + bottom flares
    public float TabFlareRadius { get; init; } = 4f;
    /// <summary>Per-element edge fade (gpu-renderer.md): feather this element's content alpha to transparent (+ optional
    /// blur) near the chosen edges, following its rounded <see cref="Corners"/> (the curve) — it dissolves into whatever
    /// is behind. One offscreen RT per faded element. Null = none.</summary>
    public EdgeFadeSpec? EdgeFade { get; init; }

    public Action? OnClick { get; init; }
    public Action<KeyEventArgs>? OnKeyDown { get; init; }
    /// <summary>Text (character) input — the IME/layout-resolved codepoint, routed to the focused node and bubbled
    /// (distinct from <see cref="OnKeyDown"/>'s raw virtual-key). Set by editable controls (EditableText/ComboBox).</summary>
    public Action<CharEventArgs>? OnCharInput { get; init; }
    // Position-aware pointer (local coords) — for sliders/scrollbars: OnPointerDown fires on press, OnDrag while held.
    public Action<Point2>? OnPointerDown { get; init; }
    public Action<Point2>? OnDrag { get; init; }
    /// <summary>Marks an <see cref="OnDrag"/> node a CROSS-AXIS content pan (SwipeControl row swipe, FlipView page drag):
    /// instead of eagerly capturing the contact on touch-down — the Slider/EditableText scrub default — this drag enrolls
    /// an AXIS-LOCKED gesture-arena member that competes with an enclosing scroller's Pan (input-a11y.md §7A). It wins the
    /// contact only when the gesture runs ALONG its own axis and yields (the list scrolls) on a cross-axis drag — the
    /// declarative form of <c>DragController.YieldsToPan</c>. Its axis is inferred from this node's main axis
    /// (<see cref="Direction"/>): a row box (Direction=0) is a horizontal swipe, a column box a vertical one. No effect
    /// without <see cref="OnDrag"/>, and no effect on the mouse path (mouse drag still captures immediately).</summary>
    public bool DragYieldsToPan { get; init; }
    /// <summary>Position-aware press carrying click count (double/triple-click), modifier chord, button and device kind —
    /// the text-selection / list-interaction press handler. Fires alongside <see cref="OnPointerDown"/> on left press.</summary>
    public Action<PointerEventArgs>? OnPointerPressed { get; init; }
    /// <summary>Context-menu request (WinUI ContextRequested): right-click release over this node, or the Menu key /
    /// Shift+F10 while it has focus. Local coords (keyboard invocations pass the node's centre).</summary>
    public Action<Point2>? OnContextRequested { get; init; }
    /// <summary>Keyboard-accelerator chord (WinUI KeyboardAccelerator): invokes <see cref="OnClick"/> from anywhere once
    /// focused routing leaves the chord unhandled (e.g. Ctrl+W close-tab).</summary>
    public KeyAccelerator? Accelerator { get; init; }
    /// <summary>Access-key mnemonic (WinUI AccessKey): Alt+letter invokes <see cref="OnClick"/>. Uppercase 'A'..'Z'/'0'..'9'.</summary>
    public char AccessKey { get; init; }
    /// <summary>Pointer cursor shown while hovering this node or any cursor-less descendant (WinUI SetCursor). Null =
    /// inherit from the nearest declaring ancestor, else the system arrow — clickability does NOT imply the hand. An
    /// explicit value (Arrow included) terminates the lookup, masking an ancestor's I-beam/hand (the TextBox delete
    /// button / PasswordBox reveal force Arrow over the field's I-beam — TextBox_Partial.cpp:884).</summary>
    public CursorId? Cursor { get; init; }
    /// <summary>Element-level wheel hook (WinUI PointerWheelChanged), consulted BEFORE the enclosing viewport scrolls;
    /// set <c>Handled</c> to consume (NumberBox value stepping). Unhandled keeps walking up (routed-event semantics).</summary>
    public Action<WheelEventArgs>? OnPointerWheel { get; init; }
    /// <summary>Position-aware BARE hover (local coords), fired on pointer move while hovering with no button down —
    /// e.g. RatingControl filling stars to the cursor on hover. Makes the node hit-testable so it receives hover.</summary>
    public Action<Point2>? OnHoverMove { get; init; }
    /// <summary>Fired when the pointer LEAVES this node (loses hover) — to reset a hover preview to its resting state
    /// (RatingControl reverting to the committed rating, a ToolTip dismissing). Makes the node hit-testable.</summary>
    public Action? OnPointerExit { get; init; }
    /// <summary>Fired when the dispatcher moves keyboard/pointer focus ONTO (true) or OFF (false) this node — the WinUI
    /// GotFocus/LostFocus pair. Delivered by <c>InputDispatcher.SetFocus</c> directly (never via hit-testing); editable
    /// controls use it to arm the caret blinker / IME and capture the Escape-revert snapshot.</summary>
    public Action<bool>? OnFocusChanged { get; init; }
    /// <summary>Marks this box a drag-reorder source (WinUI CanDragItems/CanReorderItems item container): a left press
    /// on it (or any non-draggable descendant) arms the engine's DragController; pointer travel past the 4px drag box
    /// (per-axis, ListViewBaseItem_Partial.cpp:1864-1878) promotes the press to a drag — the node follows the pointer
    /// at WinUI ListViewItemDragThemeOpacity 0.80 (ListViewItem_themeresources.xaml:7) with a lifted shadow, stops
    /// hit-testing, and the eventual release SUPPRESSES the click.</summary>
    public bool CanDrag { get; init; }
    /// <summary>Drag lifecycle (needs <see cref="CanDrag"/>): fired once when the press crosses the drag box (WinUI
    /// DragItemsStarting). The args instance is reused for the whole gesture — copy what you keep.</summary>
    public Action<DragEventArgs>? OnDragStarted { get; init; }
    /// <summary>Every pointer move while the drag is active: accumulated gesture deltas + smoothed velocity — feed
    /// <c>ReorderList.Update(e.TotalDy)</c> and re-render with its projected order / offset hints.</summary>
    public Action<DragEventArgs>? OnDragDelta { get; init; }
    /// <summary>Release after an active drag (WinUI DragItemsCompleted): commit the reorder here
    /// (<c>ReorderList.Complete()</c>); the drop-glide and the displaced-sibling FLIP retarget off this commit.</summary>
    public Action<DragEventArgs>? OnDragCompleted { get; init; }
    /// <summary>The drag aborted (Escape / pointer-capture loss / window blur): drop hints without committing.</summary>
    public Action? OnDragCanceled { get; init; }
    /// <summary>E5-L2 typed drag SOURCE (the Flutter Draggable / react-beautiful-dnd model — deliberately NOT WinUI
    /// OLE, per the 2026-06-10 user ruling): marks this box draggable (implies <see cref="CanDrag"/> — the L1 gesture
    /// armer) with a string Kind discriminator + a payload factory the engine resolves ONCE when the press promotes
    /// past the drag box. The live <see cref="DragSession"/> then routes to the nearest accepting
    /// <see cref="DropTarget"/> under the pointer on every move.</summary>
    public DragSource? Draggable { get; init; }
    /// <summary>E5-L2 drop TARGET (Flutter DragTarget / SwiftUI dropDestination): accepts sessions whose Kind is in
    /// <c>AcceptKinds</c> — OnEnter/OnOver/OnLeave fire on hover transitions, OnDrop on release over it (before the
    /// L1 completion; <c>SettleOnDrop</c> keeps the drop-glide for reorder targets). Discovery is hit-test-CHAIN
    /// based (nearest accepting ancestor of the node under the pointer) — the spec alone does NOT make this box
    /// click/pointer hit-testable.</summary>
    public DropTargetSpec? DropTarget { get; init; }
    /// <summary>Opt this clickable node into auto-repeat: while held, the host's RepeatTicker re-invokes <see cref="OnClick"/>
    /// after an initial delay, then at a fixed interval (WinUI RepeatButton). Pauses while the held pointer leaves the
    /// node (fresh delay on re-entry — RepeatButton_Partial.cpp:530-574); a held Space arms the same engine timer.</summary>
    public bool Repeats { get; init; }
    /// <summary>WinUI RepeatButton <c>Delay</c>/<c>Interval</c> (ms) for <see cref="Repeats"/> nodes. NaN = the WinUI
    /// DP defaults (500/33); the ScrollBar template arrows use Interval=50 (ScrollBar_themeresources.xaml).</summary>
    public float RepeatDelayMs { get; init; } = float.NaN;
    public float RepeatIntervalMs { get; init; } = float.NaN;
    /// <summary>WinUI <c>KeyPress::Button bAcceptsReturn</c>: false = Enter does NOT activate this clickable (it falls
    /// through to normal key routing) — CheckBox (CheckBox_Partial.cpp:27), RadioButton (RadioButton_Partial.cpp:30)
    /// and ToggleSwitch (ToggleSwitch_Partial.cpp:1002-1007) toggle on Space only. Space activation is unaffected.</summary>
    public bool ActivateOnEnter { get; init; } = true;
    /// <summary>WinUI <c>AllowFocusOnInteraction</c>: false = a pointer press never moves focus to this focusable (and
    /// never falls past it to an ancestor — focus stays where it was, AppBarButton_themeresources.xaml:136); keyboard
    /// Tab still reaches it. True (default) = press focuses the nearest focusable self-or-ancestor.</summary>
    public bool AllowFocusOnInteraction { get; init; } = true;
    public bool HitTestVisible { get; init; } = true;
    /// <summary>Input pass-through (WinUI <c>OverlayInputPassThroughElement</c>): when true, this node yields the hit to
    /// whatever is BEHIND it wherever none of its OWN children are hit — so a full-bleed floating overlay can host an
    /// interactive child (a command bar) while clicks in its empty area fall through to the page beneath. (Unlike
    /// <see cref="HitTestVisible"/>=false, which excludes the whole subtree and would make the child unclickable.)</summary>
    public bool HitTestPassThrough { get; init; }
    /// <summary>Input-enabled (the default). When false the engine gates this node's interaction: it does not hit-test,
    /// focus, take keyboard activation, repeat, drag, or click — so control factories no longer null their handlers by
    /// hand. Disabled <em>visuals</em> stay control-chosen (pick the disabled token via <c>StateBrush.Resting(enabled)</c>).</summary>
    public bool IsEnabled { get; init; } = true;
    public bool Focusable { get; init; }
    /// <summary>WinUI <c>Control.IsTabStop</c>: null = auto (clickable nodes are focusable), false = NEVER keyboard
    /// focusable even when clickable (the light-dismiss catcher layer — WinUI's dismiss layer is not a tab stop),
    /// true = force focusable.</summary>
    public bool? TabStop { get; init; }
    public int TabIndex { get; init; }
    /// <summary>WinUI FocusVisualMargin: negative values push the keyboard-focus ring OUTSIDE the bounds. Null = the
    /// WinUI template default (−3 all around); Slider uses −7,0,−7,0.</summary>
    public Edges4? FocusVisualMargin { get; init; }
    /// <summary>Semantic control role (set by the control factories; a button IS a BoxEl). Surfaced to a11y/devtools/tests.</summary>
    public AutomationRole Role { get; init; }

    // Composited (animate without relayout): transform (offset/scale/rotate about the transform origin) + opacity, applied to this node + subtree.
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
    public float ScaleX { get; init; } = 1f;
    public float ScaleY { get; init; } = 1f;
    public float Rotation { get; init; }   // degrees
    /// <summary>Unified channel (Prop&lt;T&gt;): a static opacity, a thunk, or a concrete signal.</summary>
    public Prop<float> Opacity { get; init; } = 1f;
    public float HoverOpacity { get; init; } = float.NaN;
    public float PressedOpacity { get; init; } = float.NaN;
    /// <summary>Flat opacity group (WinUI Composition LayerVisual semantics): when set and the resolved opacity is
    /// &lt; 1, the subtree renders at FULL alpha into a pooled offscreen RT and composites ONCE at the group alpha —
    /// overlapping children don't double-blend (a fading dialog plate + its buttons). Default false = per-node
    /// multiplied opacity (WinUI's plain Visual.Opacity behavior). Engine primitive: PushLayer{Opacity} (E9).</summary>
    public bool OpacityGroup { get; init; }
    /// <summary>Per-node self-blur radius σ (px) — the Expressive Motion Kit's perceptual softener. &gt; 0 wraps the
    /// node's subtree in a PushLayer{Blur} (subtree → pooled offscreen RT → separable Gaussian → composite at the group
    /// alpha), so the node's OWN pixels blur (CSS <c>filter: blur()</c>, not the backdrop). Animate it via
    /// <c>AnimChannel.BlurSigma</c> (UseTransition/UseKeyframes) for the transitions.dev recipes (number pop-in, skeleton
    /// reveal, icon swap, page slide, …). 0 = no blur (the default). Composited only — never relayout.</summary>
    public float Blur { get; init; }
    /// <summary>Transform origin (normalized 0..1 of the box). Composited scale/rotate (and animated ScaleX/Y) pivot here;
    /// default centre (0.5,0.5). Set OriginY=0 to scale/unfold from the TOP edge (a flyout/menu), 1 for the bottom.</summary>
    public float TransformOriginX { get; init; } = 0.5f;
    public float TransformOriginY { get; init; } = 0.5f;

    // Interaction-driven composited scale (1 = none): grows/shrinks this node about its centre by the eased hover/press
    // progress at record time (a WinUI slider/scrollbar thumb that pops on hover). Needs a pointer handler to receive the
    // hover/press flags. Composited only — never changes layout or hit-testing.
    public float HoverScale { get; init; } = 1f;
    public float PressScale { get; init; } = 1f;
    public float HoverDurationMs { get; init; } = float.NaN;
    public float PressDurationMs { get; init; } = float.NaN;
    public EasingSpec HoverEasing { get; init; } = Easing.FluentPopOpen;
    public EasingSpec PressEasing { get; init; } = Easing.FluentPopOpen;

    /// <summary>Implicit brush transition (WinUI <c>BrushTransition</c>): when a re-render changes Fill/BorderColor on
    /// this LIVE node (a logical state flip — checked, selected…), the displayed color cross-fades over this duration
    /// instead of snapping. NaN = snap (the default); WinUI control templates use 83ms.</summary>
    public float BrushTransitionMs { get; init; } = float.NaN;

    // ── Fine-grained reactive bindings (signals-first). Every bindable channel is ONE Prop<T> property: a static
    // value (re-asserted each reconcile iff not bound), a Func<T> thunk reading signals, or a concrete signal — the
    // reconciler wires a bound channel into a mount-time effect that writes only this node's column + marks the
    // matching dirty axis (no component re-render, no reconcile). Transform/Opacity/Fill are compositor-only;
    // Width/Height bind layout and trigger a scoped relayout.
    /// <summary>The whole-matrix transform channel — bind-only in v1 (a thunk/signal producing the full Affine2D;
    /// the STATIC path composes from the decomposed OffsetX/Y/ScaleX/Y/Rotation floats above). When bound, the
    /// decomposed statics are ignored: one transform owner per node — never combine with a sticky/stretch ScrollBind or
    /// transform-channel animations. An unbound Transform's Value is never read.</summary>
    public Prop<Affine2D> Transform { get; init; }

    // Legacy *Bind spellings — write-only init-aliases into the unified channel props, deleted per-channel by the
    // migration waves. A null assignment leaves the channel static (preserves the `cond ? null : bind` idiom).

    /// <summary>Called once when this box is realized into the scene, with its node handle — for a control factory to
    /// capture the handle (e.g. to wire a signal binding that needs the live node). Fires at mount only.</summary>
    public Action<NodeHandle>? OnRealized { get; init; }
    /// <summary>Called after layout when this node's arranged local bounds change. Intended for retained leaf controls
    /// that need their own laid-out width/height without subscribing to raw viewport changes.</summary>
    public Action<RectF>? OnBoundsChanged { get; init; }

    public Element[] Children { get; init; } = [];

    /// <summary>Z-stack: children overlay at this box's origin (each filling it unless sized), painted in order
    /// (last on top) — for overlays, scrims, flyouts, the NavigationView Minimal pane. (A flexbox container otherwise.)</summary>
    public bool ZStack { get; init; }
    public bool ClipToBounds { get; init; }

    /// <summary>Layout firewall (opt-in): declare that this box's size is PARENT-determined (it fills/clips and is never
    /// content-sized), so a re-render or state change deep inside its subtree re-solves ONLY this subtree (scoped layout)
    /// instead of falling back to a full-tree layout from the root. Use on a page/content host that fills the shell content
    /// region. Contract: only set this where the box truly cannot need to change its own outer size from a descendant — the
    /// scoped relayout reuses its current bounds. A window resize still triggers a full layout, so resize stays correct.</summary>
    public bool IsolateLayout { get; init; }

    /// <summary>Opt this box into general layout-change animation: the host diffs its presented rect vs its new
    /// laid-out rect each commit and drives the residual through the spec's channels/dynamics (no relayout, no
    /// per-frame re-render). Null ⇒ snap (the default). See <see cref="FluentGpu.Foundation.LayoutTransition"/>.</summary>
    public LayoutTransition? Animate { get; init; }

    /// <summary>Opt this child OUT of a <see cref="FluentGpu.Foundation.SizeMode.ScaleCorrect"/> ancestor's scale: the
    /// recorder applies the inverse scale so the child stays undistorted (Framer-Motion projection correction).</summary>
    public bool CounterScale { get; init; }

    // Flexbox
    /// <summary>Unified channels (Prop&lt;T&gt;): static size, thunk, or concrete signal (bound ⇒ scoped relayout).</summary>
    public Prop<float> Width { get; init; } = float.NaN;
    public Prop<float> Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    public FlexJustify Justify { get; init; } = FlexJustify.Start;
    public FlexAlign AlignItems { get; init; } = FlexAlign.Stretch;
    public bool Wrap { get; init; }
}

/// <summary>
/// A CSS-Grid container: column tracks (Pixel/Star/Auto) + gaps; children auto-flow row-major into cells. Rows take
/// <see cref="RowHeight"/> (or the tallest cell when NaN). True tracks — every cell in a column shares its width
/// (unlike nested-flex faking). Width-aware: resolves inside a ScrollView. (layout.md §7.)
/// </summary>
public sealed record GridEl : Element
{
    public override ushort ElementTypeId => 9;

    public TrackSize[] Columns { get; init; } = [];
    public float ColGap { get; init; }
    public float RowGap { get; init; }
    public float RowHeight { get; init; } = float.NaN;   // NaN = auto (tallest cell in the row)
    public float MinColWidth { get; init; }              // > 0 = auto-fill: as many 1fr columns as fit at this min width (Columns ignored)
    public Element[] Children { get; init; } = [];

    // sizing/participation
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
    public Edges4 Padding { get; init; }
}

/// <summary>A text run. <see cref="FontFamily"/> selects the face — a system family name ("Segoe UI",
/// "Segoe Fluent Icons") or a custom file as <c>"path/to.ttf#Family Name"</c> (the WinUI syntax). Null = the theme
/// body font. Combine with an icon-font family + a private-use glyph to render icons (see <c>Ui.Icon</c>).</summary>
/// <summary>A leaf stroked polyline, drawn analytically by the renderer and revealable through stroke trim.</summary>
public sealed record PolylineStrokeEl : Element
{
    public override ushort ElementTypeId => 11;

    public Point2 P0 { get; init; }
    public Point2 P1 { get; init; }
    public Point2 P2 { get; init; }
    public Point2 P3 { get; init; }
    public int PointCount { get; init; } = 2;
    public ColorF Color { get; init; }
    public float Thickness { get; init; } = 1f;
    public float TrimStart { get; init; }
    public float TrimEnd { get; init; } = 1f;
    public bool RoundCaps { get; init; } = true;

    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
    public float ScaleX { get; init; } = 1f;
    public float ScaleY { get; init; } = 1f;
    public float Rotation { get; init; }
    public float Opacity { get; init; } = 1f;
    public float TransformOriginX { get; init; } = 0.5f;
    public float TransformOriginY { get; init; } = 0.5f;
    public float HoverScale { get; init; } = 1f;
    public float PressScale { get; init; } = 1f;

    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
}

public sealed record TextEl(Prop<string> Text) : Element
{
    public override ushort ElementTypeId => 2;

    // Unified channels: Text/Color each take a static value, a Func<T> thunk, or a concrete signal (the positional
    // ctor keeps `new TextEl("hi")` compiling via the string → Prop<string> conversion). Legacy *Bind init-aliases
    // below are deleted by the migration waves.

    public float Size { get; init; } = 14f;
    /// <summary>Bold sugar — kept for the many existing call sites; equivalent to <see cref="Weight"/> = 700.
    /// Ignored when <see cref="Weight"/> is set explicitly.</summary>
    public bool Bold { get; init; }
    /// <summary>Numeric font weight (WinUI FontWeight, 1–999 — the value IS the DWrite weight; the WinUI type ramp is
    /// SemiBold 600, BaseTextBlockStyle FontWeight, TextBlock_themeresources.xaml:13). 0 = unset → resolves from
    /// <see cref="Bold"/> (700) or Normal (400); see <see cref="ResolvedWeight"/>.</summary>
    public ushort Weight { get; init; }
    /// <summary>The weight the text pipeline shapes with: <see cref="Weight"/> when set, else Bold→700 / 400.</summary>
    public ushort ResolvedWeight => Weight != 0 ? Weight : Bold ? (ushort)700 : (ushort)400;
    /// <summary>WinUI <c>TextElement.CharacterSpacing</c>: tracking in 1/1000 em (negative = tighter), applied as a
    /// per-glyph trailing advance adjustment after shaping (e.g. Pivot headers use −25).</summary>
    public float CharSpacing { get; init; }
    /// <summary>WinUI <c>TextBlock.LineHeight</c> in DIP (NaN = font-natural); interpreted per <see cref="LineStacking"/>.</summary>
    public float LineHeight { get; init; } = float.NaN;
    /// <summary>WinUI <c>TextBlock.LineStackingStrategy</c> (default MaxHeight — TextBlock_themeresources.xaml:16):
    /// how an explicit <see cref="LineHeight"/> combines with the font-natural line box.</summary>
    public LineStacking LineStacking { get; init; } = LineStacking.MaxHeight;
    /// <summary>WinUI <c>TextBlock.TextLineBounds</c> (default Full — TextBlock_themeresources.xaml:17): Tight trims
    /// the measured line box to cap-height..baseline so vertical centering is optical (PersonPicture initials).</summary>
    public TextLineBounds LineBounds { get; init; } = TextLineBounds.Full;
    /// <summary>Defaults to the live theme's <c>TextFillColorPrimary</c> (WinUI TextBlock default foreground —
    /// dark #FFFFFF / light #E4000000), resolved at element CONSTRUCTION. NOTE: a runtime <c>Tok.Use</c> switch does
    /// not itself re-render — the theme is set at startup today; a live theme switcher must force a full re-render
    /// or every construction-resolved color (this default, control Style records) goes stale.</summary>
    public Prop<ColorF> Color { get; init; } = Tok.TextPrimary;
    // Stateful foreground ramps (WinUI dims/recolors label & glyph foreground on hover/press/disabled/focus). A==0 ⇒
    // "no state color" → the recorder leaves Color/ColorBind untouched. Hover/Pressed ease with the nearest interactive
    // ancestor's progress (the same eased HoverT/PressT that cross-fades the box fill — no per-control animator).
    // Disabled/Focused are steps gated by the ancestor's NodeFlags.Disabled / this node's NodeFlags.Focused.
    public ColorF HoverColor { get; init; }
    public ColorF PressedColor { get; init; }
    public ColorF DisabledColor { get; init; }
    public ColorF FocusedColor { get; init; }
    /// <summary>WinUI <c>TextDecorations.Underline</c>: the recorder draws the face-metric underline bar (DWrite
    /// underlinePosition/underlineThickness, cached at measure on the scene's TextMeasureCache) under the run, in the
    /// SAME resolved foreground as the glyphs (hover/press ramps + BrushTransition). Single-line frame — per-line
    /// decoration of wrapped runs is the rich-text (SpanTextEl/RichTextBlock) pass. HyperlinkButton drives this from
    /// its HyperlinkUnderlineVisible/HighContrast gate (HyperLinkButton_Partial.cpp:207-212).</summary>
    public bool Underline { get; init; }
    /// <summary>WinUI <c>TextDecorations.Strikethrough</c>: the face-metric strikethrough bar (reuses the underline
    /// thickness, the DWrite convention) — e.g. CalendarView blackout dates.</summary>
    public bool Strikethrough { get; init; }
    /// <summary>Implicit brush transition for the resting <see cref="Color"/>: a re-render that changes it on this LIVE
    /// node cross-fades over this duration (WinUI BrushTransition, 83ms in templates). NaN = snap.</summary>
    public float BrushTransitionMs { get; init; } = float.NaN;
    /// <summary>Read-only text selection (rtb-02): mouse drag selects, double-click selects the word, triple-click all,
    /// Ctrl+C copies via the clipboard seam; the highlight reuses the editor's selection-rect path. Default FALSE —
    /// WinUI TextBlock selection is opt-in (TextBlock.cpp:583 IsTextSelectionEnabled property change creates the
    /// selection manager on demand); RichTextBlock turns it on by default (RichTextBlock.cpp:1730). A selectable run
    /// is focusable (Ctrl+C routes to it) and shows the I-beam cursor.</summary>
    public bool IsTextSelectionEnabled { get; init; }
    /// <summary>Per-control selection highlight (api-04, WinUI <c>TextBlock.SelectionHighlightColor</c> —
    /// TextBlock.cpp:266/330). A==0 (default) = the engine/theme brush (the system accent,
    /// TextSelectionManager.cpp:52-56 GetDefaultSelectionHighlightColor → GetSystemAccentColor ≡ the host's
    /// TextEditStyle.SelectionFill).</summary>
    public ColorF SelectionHighlightColor { get; init; }
    public string? FontFamily { get; init; }
    public DynamicTextKind DynamicText { get; init; }
    /// <summary>Line-break behavior (WinUI TextWrapping): NoWrap / Wrap / WrapWholeWords.</summary>
    public TextWrap Wrap { get; init; } = TextWrap.NoWrap;
    /// <summary>Overflow trimming (WinUI TextTrimming): None / Clip / CharacterEllipsis / WordEllipsis.</summary>
    public TextTrim Trim { get; init; } = TextTrim.None;
    /// <summary>Cap the visible line count (0 = unlimited); the last line trims per <see cref="Trim"/>.</summary>
    public int MaxLines { get; init; }
    /// <summary>Auto-fit (WinUI has no analogue; a Viewbox-for-text): when set (and &lt; <see cref="Size"/>, with
    /// <see cref="MaxLines"/> &gt; 0, a wrapping style, and a definite width), the layout SHRINKS the font from
    /// <see cref="Size"/> down to this floor to make the run fit in <see cref="MaxLines"/> at the available width —
    /// minimizing wraps and avoiding trimming. The largest size that fits wins; if even this floor doesn't fit,
    /// <see cref="Trim"/> ellipsis applies at the floor. Use a font-natural line height (leave <see cref="LineHeight"/>
    /// unset) so the chosen size's spacing scales with it. NaN = off (no auto-fit; the default).</summary>
    public float MinSize { get; init; } = float.NaN;

    // Leaf layout participation. Text needs the same sizing/flex knobs as other leaves so wrapped runs can be
    // constrained by their container instead of contributing their full single-line width to parent measure.
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
}

/// <summary>
/// A rich-text PARAGRAPH of typed inline runs (rtb-01 — the WinUI <c>TextBlock.Inlines</c>/RichTextBlock paragraph
/// model: Run/Bold/Hyperlink): the spans concatenate and shape as ONE flow (one wrap pass — a styled word never
/// re-flows independently of its sentence). Per-span weight/size/family/color/underline/strikethrough overlay the
/// base style here; a span with <c>OnClick</c> is a HYPERLINK — the engine resolves the Hand cursor over its laid
/// rects and fires the action on click (RichTextBlock.cpp:2995 / TextBlock.cpp:3488 SetCursor(MouseCursorHand));
/// accent foreground + underline are the CALLER's styling (HyperlinkForeground, generic.xaml:1120-1122).
/// No per-span italic: the shaper has no style axis yet (faces resolve by family+weight only).
/// </summary>
public sealed record SpanTextEl(TextSpan[] Spans) : Element
{
    public override ushort ElementTypeId => 12;

    // ── base (paragraph) style — every TextSpan field that is unset inherits these ──
    public float Size { get; init; } = 14f;
    /// <summary>Base numeric font weight (0 = Normal 400); spans override per range.</summary>
    public ushort Weight { get; init; }
    /// <summary>Defaults to the live theme's <c>TextFillColorPrimary</c> (the TextEl default), resolved at construction.</summary>
    public ColorF Color { get; init; } = Tok.TextPrimary;
    public string? FontFamily { get; init; }
    public float CharSpacing { get; init; }
    public float LineHeight { get; init; } = float.NaN;
    public LineStacking LineStacking { get; init; } = LineStacking.MaxHeight;
    public TextLineBounds LineBounds { get; init; } = TextLineBounds.Full;
    /// <summary>Paragraphs WRAP by default — the WinUI BaseRichTextBlockStyle sets TextWrapping=Wrap
    /// (generic.xaml:13286-13295); pass NoWrap explicitly for a single-line run of spans.</summary>
    public TextWrap Wrap { get; init; } = TextWrap.Wrap;
    public TextTrim Trim { get; init; } = TextTrim.None;
    public int MaxLines { get; init; }

    /// <summary>Read-only selection (rtb-02): drag selects across the whole paragraph flow, Ctrl+C copies. OPT-IN here
    /// (like WinUI TextBlock, TextBlock.cpp:583); the RichTextBlock control turns it on by default
    /// (IsTextSelectionEnabled=TRUE by default on CRichTextBlock, RichTextBlock.cpp:1730).</summary>
    public bool IsTextSelectionEnabled { get; init; }
    /// <summary>Per-control selection highlight (api-04). A==0 = the engine/theme accent brush
    /// (TextSelectionManager.cpp:52-56).</summary>
    public ColorF SelectionHighlightColor { get; init; }

    // Leaf layout participation (same knobs as TextEl so paragraphs are container-constrained).
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
}

/// <summary>
/// An async image (album art): shows <see cref="Placeholder"/> until the decode lands, then the bitmap. Decode is
/// off-thread + cached + residency-pinned while on screen (see <c>ImageCache</c>).
/// </summary>
public sealed record ImageEl : Element
{
    public override ushort ElementTypeId => 8;

    /// <summary>Unified channel: a static path/URL, a thunk (bound virtual rows re-request art when the thunk's
    /// signals change — the recycled slot swaps without an element rebuild), or a concrete signal.</summary>
    public Prop<string> Source { get; init; } = "";
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    /// <summary>Width÷height ratio (CSS <c>aspect-ratio</c>). When set, a fluid image (one or both of
    /// <see cref="Width"/>/<see cref="Height"/> left <c>NaN</c>) <i>derives</i> the missing dimension — e.g. a tile that
    /// fills its column width and stays square (<c>1f</c>). The thing WinUI lacks (no SizeChanged/Viewbox hacks).
    /// <c>NaN</c> = off (use explicit <see cref="Width"/>/<see cref="Height"/>).</summary>
    public float AspectRatio { get; init; } = float.NaN;
    /// <summary>How the decoded pixels map into the layout box — see <see cref="ImageFit"/>. Default
    /// <see cref="ImageFit.Cover"/> (aspect-preserving crop), what most grids want; harmless for square-source/square-box.</summary>
    public ImageFit Fit { get; init; } = ImageFit.Cover;
    /// <summary>Decode-size hint (target px) used when the layout extent is fluid (<see cref="Width"/> is <c>NaN</c>, so the
    /// real box size isn't known at request time). Ignored when <see cref="Width"/> is explicit (that drives the decode).
    /// <c>NaN</c> ⇒ decode at source resolution.</summary>
    public float DecodePx { get; init; } = float.NaN;
    public CornerRadius4 Corners { get; init; }
    /// <summary>Unified channel: static tint, thunk, or signal (pairs with a bound <see cref="Source"/>).</summary>
    public Prop<ColorF> Placeholder { get; init; } = ColorF.FromRgba(0x33, 0x33, 0x33);
    /// <summary>Optional BlurHash string — a tiny blurred LQIP preview shown instantly (decoded to a small texture)
    /// until the full-res art lands. Falls back to the flat <see cref="Placeholder"/> tint when null.</summary>
    public string? BlurHash { get; init; }
    /// <summary>Override the placeholder→image reveal transition (duration + easing). Null ⇒ <see cref="ImageTransition.Default"/>;
    /// pass <see cref="ImageTransition.None"/> to disable the fade (instant).</summary>
    public ImageTransition? Transition { get; init; }
    public Edges4 Margin { get; init; }
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
}

/// <summary>
/// A clipping, scrolling viewport over a single (possibly oversized) content child. Scroll is layout-free
/// (layout.md §6): layout arranges the content at the content-box origin and publishes <c>ContentSize</c>; the
/// <c>-ScrollOffset</c> translation is the content's <c>LocalTransform</c>, written by Input on wheel/drag.
/// </summary>
public sealed record ScrollEl : Element
{
    public override ushort ElementTypeId => 5;

    public Element Content { get; init; } = new BoxEl();
    public bool Horizontal { get; init; }     // false = vertical scroll (the common case)
    /// <summary>
    /// Measure to content when auto-sized, then clamp by Min/Max. Default false keeps ScrollView a hard viewport
    /// boundary for app/page/navigation scrolling; popup lists (ComboBox/MenuFlyout/AutoSuggest) opt in so short lists
    /// size to their rows and tall lists scroll after MaxHeight.
    /// </summary>
    public bool ContentSized { get; init; }

    /// <summary>Opt into pinch-zoom (WinUI <c>ScrollingZoomMode.Enabled</c> — default off, mirroring
    /// ScrollPresenter's <c>s_defaultZoomMode = Disabled</c>). When set, a SECOND touch contact over the viewport scales
    /// the content about the gesture midpoint (transform-only; never a relayout) and a single-finger remainder continues
    /// as a pan. <see cref="MinZoom"/>/<see cref="MaxZoom"/> bound the factor.</summary>
    public bool Zoomable { get; init; }
    /// <summary>Minimum pinch-zoom factor (WinUI <c>s_defaultMinZoomFactor = 0.1</c>, ScrollPresenter.h:63). Ignored unless
    /// <see cref="Zoomable"/>.</summary>
    public float MinZoom { get; init; } = 0.1f;
    /// <summary>Maximum pinch-zoom factor (WinUI <c>s_defaultMaxZoomFactor = 10.0</c>, ScrollPresenter.h:64). Ignored unless
    /// <see cref="Zoomable"/>.</summary>
    public float MaxZoom { get; init; } = 10f;

    // The viewport participates in its parent's layout like a box (size + flex + margin + a backing fill).
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public float MinWidth { get; init; } = float.NaN;
    public float MinHeight { get; init; } = float.NaN;
    public float MaxWidth { get; init; } = float.NaN;
    public float MaxHeight { get; init; } = float.NaN;
    public float Grow { get; init; }
    public float Shrink { get; init; }
    public float Basis { get; init; } = float.NaN;
    public FlexAlign AlignSelf { get; init; } = FlexAlign.Auto;
    public Edges4 Margin { get; init; }
    public Edges4 Padding { get; init; }
    public ColorF Fill { get; init; }
    public CornerRadius4 Corners { get; init; }

    /// <summary>Scroll-edge cues (controls.md §8.3): a surface-colour gradient fade at any edge with more content past it,
    /// so a clipped list signals there is more below the fold. <see cref="ScrollEdgeCues.Auto"/> (default) resolves to
    /// <see cref="ScrollEdgeCuesDefaults.Default"/> (ON, fade-only); set <see cref="ScrollEdgeCues.None"/> to opt out.</summary>
    public ScrollEdgeCues EdgeCues { get; init; } = ScrollEdgeCues.Auto;
    /// <summary>Explicit edge fade on the viewport (e.g. <c>EdgeFadeSpec.Horizontal()</c>) — the premium alpha-mask cue:
    /// content dissolves into anything behind, following the corners. One offscreen RT. Null = none.</summary>
    public EdgeFadeSpec? EdgeFade { get; init; }
    /// <summary>Auto edge fade: feather only the edges that currently OVERFLOW (more content past them), ramped with the
    /// scroll offset — the discoverable-overflow affordance as a true alpha fade. Ignored when <see cref="EdgeFade"/> is set.</summary>
    public bool AutoEdgeFade { get; init; }
    /// <summary>Keep the scrollbar VISIBLE (a persistent thin rail) whenever the content overflows, instead of the default
    /// auto-hide that only reveals on hover/scroll. Hover still expands the rail to the full draggable bar. For navigation
    /// surfaces (a sidebar) where a discoverable, always-present scroll affordance is wanted (WinUI 11 nav behavior).</summary>
    public bool AlwaysShowScrollbar { get; init; }

    /// <summary>Nested-scroll chaining policy (the CSS <c>overscroll-behavior</c> analog). Default <see cref="ScrollChainingMode.Auto"/>:
    /// a touch pan that reaches this scroller's edge hands the residual to the nearest same-axis ancestor scroller
    /// (Compose nested-scroll), and a flick at the edge throws into it. <see cref="ScrollChainingMode.Contain"/> rubber-bands
    /// instead. (Wheel scrolling already bubbles to an outer scroller regardless.)</summary>
    public ScrollChainingMode Chaining { get; init; } = ScrollChainingMode.Auto;

    /// <summary>Change-only scroll-geometry observer (the escape hatch; SwiftUI <c>onScrollGeometryChange</c>). The host
    /// projects the live geometry to a coarse <c>long</c> key after the integrator settles and fires the action only when
    /// that key changes (never per-px, never per-frame) — for pull-to-refresh triggers, analytics, bespoke app logic.
    /// UI-thread, pre-publish. Project a COARSE key (e.g. <c>g => g.Band &lt; -80 ? 1 : 0</c>), not raw px.</summary>
    public (Func<FluentGpu.Animation.ScrollGeometry, long> Project, Action<FluentGpu.Animation.ScrollGeometry> Action)? OnScrollGeometryChanged { get; init; }

    /// <summary>Scroll-position restoration key: a STABLE per-content identity (e.g. a route key like <c>"artist:&lt;uri&gt;"</c>).
    /// When set, the engine saves this viewport's offset under it and restores it when the same content is shown again —
    /// even after the page was evicted from KeepAlive (cold remount), seeded BEFORE the first layout so there is no
    /// scroll-to-top flash. Distinct content (a different key) starts at the top; the same content open in two tabs is
    /// kept separate automatically (the engine namespaces by the enclosing KeepAlive slot). Null ⇒ no restoration.</summary>
    public string? ScrollKey { get; init; }
    /// <summary>Never draw the conscious scrollbar for this viewport (parity with <see cref="VirtualListEl"/>); the offset
    /// is still programmatically scrollable. Used to hide the rail while a region is loading its skeleton.</summary>
    public bool SuppressScrollBar { get; init; }
}
