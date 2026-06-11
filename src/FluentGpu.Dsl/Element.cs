using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Dsl;

/// <summary>Immutable description of a UI node (the "virtual DOM"). Cheap to build, never touches the scene directly.</summary>
public abstract record Element
{
    public string? Key { get; init; }

    /// <summary>Stable per-record-type id for integer type-dispatch in the reconciler (the source-gen'd ElementTypeId).</summary>
    public abstract ushort ElementTypeId { get; }
}

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
    public CornerRadius4 Corners { get; init; }

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

    public Action? OnClick { get; init; }
    public Action<KeyEventArgs>? OnKeyDown { get; init; }
    /// <summary>Text (character) input — the IME/layout-resolved codepoint, routed to the focused node and bubbled
    /// (distinct from <see cref="OnKeyDown"/>'s raw virtual-key). Set by editable controls (EditableText/ComboBox).</summary>
    public Action<CharEventArgs>? OnCharInput { get; init; }
    // Position-aware pointer (local coords) — for sliders/scrollbars: OnPointerDown fires on press, OnDrag while held.
    public Action<Point2>? OnPointerDown { get; init; }
    public Action<Point2>? OnDrag { get; init; }
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
    /// decomposed statics are ignored: one transform owner per node — never combine with StickyTop or
    /// transform-channel animations. An unbound Transform's Value is never read.</summary>
    public Prop<Affine2D> Transform { get; init; }

    // Legacy *Bind spellings — write-only init-aliases into the unified channel props, deleted per-channel by the
    // migration waves. A null assignment leaves the channel static (preserves the `cond ? null : bind` idiom).
    public Func<Affine2D>? TransformBind { init { if (value is not null) Transform = value; } }   // → LocalTransform (TransformDirty | PaintDirty)
    public Func<ColorF>? FillBind { init { if (value is not null) Fill = value; } }               // → Fill (PaintDirty)

    /// <summary>Called once when this box is realized into the scene, with its node handle — for a control factory to
    /// capture the handle (e.g. to wire a signal binding that needs the live node). Fires at mount only.</summary>
    public Action<NodeHandle>? OnRealized { get; init; }

    public Element[] Children { get; init; } = [];

    /// <summary>Z-stack: children overlay at this box's origin (each filling it unless sized), painted in order
    /// (last on top) — for overlays, scrims, flyouts, the NavigationView Minimal pane. (A flexbox container otherwise.)</summary>
    public bool ZStack { get; init; }
    public bool ClipToBounds { get; init; }

    /// <summary>Opt this box into general layout-change animation: the host diffs its presented rect vs its new
    /// laid-out rect each commit and drives the residual through the spec's channels/dynamics (no relayout, no
    /// per-frame re-render). Null ⇒ snap (the default). See <see cref="FluentGpu.Foundation.LayoutTransition"/>.</summary>
    public LayoutTransition? Animate { get; init; }

    /// <summary>CSS <c>position: sticky; top: N</c> — the box scrolls normally until its top edge reaches N device-
    /// independent pixels below its nearest scroll viewport's top, then PINS there while its PARENT (the containing
    /// block) is in view; it never escapes the parent's bounds. The pin is a per-frame LocalTransform written by the
    /// host (compositor-only, no relayout) and hit-testing follows the pinned position; while pinned the node paints
    /// ABOVE its siblings so content scrolls underneath. One transform owner per node: do not combine with
    /// <see cref="TransformBind"/> or transform-channel animations. Null = not sticky (the default).</summary>
    public float? StickyTop { get; init; }

    /// <summary>Observable pin state for a <see cref="StickyTop"/> box — the signals-native CSS <c>:stuck</c>: the
    /// host invokes this whenever the box transitions pinned ↔ unpinned. Wire it to a signal and restyle ANYTHING off
    /// it (an opaque fill so content can't show through, a shadow, a compact variant, different children) — strictly
    /// more flexible than a fixed pinned style. Fired at most once per transition, never per frame.</summary>
    public Action<bool>? OnPinned { get; init; }

    /// <summary>Optional shared-element key: a node with the same MorphId across reconciles morphs from the old node's
    /// rect to the new one (matched-geometry / Hero). Reserved for v1.1.</summary>
    public string? MorphId { get; init; }

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
    public Func<string>? TextBind { init { if (value is not null) Text = value; } }   // → text content (LayoutDirty → scoped relayout if metrics change)
    public Func<ColorF>? ColorBind { init { if (value is not null) Color = value; } } // → text color  (PaintDirty)

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
    public CornerRadius4 Corners { get; init; }
    /// <summary>Unified channel: static tint, thunk, or signal (pairs with a bound <see cref="Source"/>).</summary>
    public Prop<ColorF> Placeholder { get; init; } = ColorF.FromRgba(0x33, 0x33, 0x33);
    // Legacy *Bind spellings — write-only init-aliases, deleted by the migration waves.
    public Func<string>? SourceBind { init { if (value is not null) Source = value; } }
    public Func<ColorF>? PlaceholderBind { init { if (value is not null) Placeholder = value; } }
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
}
