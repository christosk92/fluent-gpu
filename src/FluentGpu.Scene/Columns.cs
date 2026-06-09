using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Scene;

public enum VisualKind : byte { None = 0, Box = 1, Text = 2, Image = 3, PolylineStroke = 4 }

/// <summary>Per-text-node measure cache (layout.md §2.3): a pure-function cache of (text, style, availWidth) → size, so a
/// scoped relayout skips re-shaping a text leaf whose inputs are unchanged. Self-invalidating — any input change makes
/// the stored key not match. Helps the real DirectWrite shaping path; neutral for the headless fake font.</summary>
public struct TextMeasureCache
{
    public bool Valid;
    public StringId Text;
    public TextStyle Style;
    public float MaxW;
    public Size2 Size;
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
    public float StrokeTrimStart, StrokeTrimEnd;
    public ColorF Fill;
    public ColorF HoverFill;      // A==0 ⇒ recorder auto-lightens Fill on hover
    public ColorF PressedFill;    // A==0 ⇒ recorder auto-darkens Fill on press
    public ColorF BorderColor;
    public ColorF HoverBorderColor;    // A==0 ⇒ recorder auto-lightens BorderColor on hover (else eases to this exact token)
    public ColorF PressedBorderColor;  // A==0 ⇒ recorder auto-darkens BorderColor on press (else eases to this exact token)
    public float BorderWidth;
    public CornerRadius4 Corners;
    public ColorF TextColor;
    // Stateful foreground ramps (text/glyph). A==0 ⇒ no state color for that axis; the recorder leaves TextColor as-is.
    // Hover/Pressed ease with the nearest interactive ancestor's progress; Disabled/Focused are steps (see ResolveTextColor).
    public ColorF TextHoverColor;
    public ColorF TextPressedColor;
    public ColorF TextDisabledColor;
    public ColorF TextFocusedColor;
    public StringId Text;
    public int ImageId;           // VisualKind.Image: handle into the ImageCache (Fill doubles as the placeholder tint)
    public VisualKind VisualKind;

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
    public bool  ContentSized;            // auto-size to content then clamp (popup lists); false = hard viewport
    public float FadeT;                   // scrollbar indicator opacity 0..1 (eased in on scroll/hover, auto-hides after idle)
    public float ExpandT;                 // WinUI conscious scrollbar expansion 0=thin indicator, 1=full gutter + buttons
    public float IdleMs;                  // time since the last scroll movement / hover (drives the auto-hide)
    public bool PointerOver;              // pointer is inside this scroll viewport
    public bool PointerOverScrollbar;     // pointer is inside this viewport's scrollbar gutter

    // Virtualization (ItemCount == 0 ⇒ a plain ScrollView, non-virtual).
    public int   ItemCount;
    public IVirtualLayout? Layout;        // pluggable fixed-geometry layout (stack/grid/custom); null ⇒ variable (extent table)
    public int   Overscan;                // rows realized beyond the viewport on each side
    public int   FirstRealized, LastRealized;
    public int   ExtentTableRef;          // -1 = uniform / non-virtual; else index into the ExtentTable slab
    public NodeHandle ContentNode;        // the single content child carrying the -ScrollOffset LocalTransform

    // Scroll anchoring (variable path): keep the topmost-visible item visually fixed across extent corrections.
    public int   AnchorIndex;
    public StringId AnchorKey;
    public float AnchorViewportDelta;

    public static ScrollState Default => new() { ExtentTableRef = -1 };
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

/// <summary>Hit-test / input column.</summary>
public struct InteractionInfo
{
    public ushort HandlerMask;    // bit0 = click/pointer, bit1 = key, bit2 = pointer, bit3 = char, bit4 = repeat, bit5 = pressed, bit6 = context
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
}
