using FluentGpu.Foundation;

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
    public ColorF Fill { get; init; }
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
    /// <summary>Pointer cursor shown while hovering this node (overrides the OnClick hand default). Null = inherit.</summary>
    public CursorId? Cursor { get; init; }
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
    /// after an initial delay, then at a fixed interval (WinUI RepeatButton). Cancels on release / drag-off.</summary>
    public bool Repeats { get; init; }
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
    public float Opacity { get; init; } = 1f;
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

    // ── Fine-grained reactive bindings (signals-first). A bind is a thunk that READS signals; the reconciler turns it
    // into an effect at mount that writes only this node's channel + marks the matching dirty axis, so a signal change
    // updates exactly this node with no component re-render / no reconcile. Transform/Opacity/Fill are compositor-only
    // (no relayout); Width/Height bind layout and trigger a scoped relayout. Null = static (the default).
    public Func<Affine2D>? TransformBind { get; init; }   // → LocalTransform (TransformDirty | PaintDirty)
    public Func<float>? OpacityBind { get; init; }        // → Opacity      (PaintDirty)
    public Func<ColorF>? FillBind { get; init; }          // → Fill         (PaintDirty)
    public Func<float>? WidthBind { get; init; }          // → LayoutInput.Width  (LayoutDirty → scoped relayout)
    public Func<float>? HeightBind { get; init; }         // → LayoutInput.Height (LayoutDirty → scoped relayout)

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

    /// <summary>Optional shared-element key: a node with the same MorphId across reconciles morphs from the old node's
    /// rect to the new one (matched-geometry / Hero). Reserved for v1.1.</summary>
    public string? MorphId { get; init; }

    /// <summary>Opt this child OUT of a <see cref="FluentGpu.Foundation.SizeMode.ScaleCorrect"/> ancestor's scale: the
    /// recorder applies the inverse scale so the child stays undistorted (Framer-Motion projection correction).</summary>
    public bool CounterScale { get; init; }

    // Flexbox
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

public sealed record TextEl(string Text) : Element
{
    public override ushort ElementTypeId => 2;

    // Fine-grained reactive bindings: a thunk reading signals → updates just this text node when the signal changes.
    public Func<string>? TextBind { get; init; }   // → text content (LayoutDirty → scoped relayout if metrics change)
    public Func<ColorF>? ColorBind { get; init; }  // → text color  (PaintDirty)

    public float Size { get; init; } = 14f;
    public bool Bold { get; init; }
    public ColorF Color { get; init; } = ColorF.FromRgba(0xE6, 0xE6, 0xE6);
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
/// An async image (album art): shows <see cref="Placeholder"/> until the decode lands, then the bitmap. Decode is
/// off-thread + cached + residency-pinned while on screen (see <c>ImageCache</c>).
/// </summary>
public sealed record ImageEl : Element
{
    public override ushort ElementTypeId => 8;

    public string Source { get; init; } = "";
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    public CornerRadius4 Corners { get; init; }
    public ColorF Placeholder { get; init; } = ColorF.FromRgba(0x33, 0x33, 0x33);
    /// <summary>Reactive source binding (bound virtual rows): re-requests the image when the thunk's signals change —
    /// the recycled slot swaps art without an element rebuild. Overrides <see cref="Source"/> when set.</summary>
    public Func<string>? SourceBind { get; init; }
    /// <summary>Reactive placeholder-tint binding (pairs with <see cref="SourceBind"/>). Overrides <see cref="Placeholder"/>.</summary>
    public Func<ColorF>? PlaceholderBind { get; init; }
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
