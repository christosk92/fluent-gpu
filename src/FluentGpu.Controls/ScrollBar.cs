using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>ScrollBar</c>. Two surfaces:
/// <list type="bullet">
/// <item><see cref="Create"/> — the legacy thin PANNING-indicator variant (thumb-only, absolute drag), kept
/// source/behavior-compatible (VerticalSlice check 48 and the gallery drive it); it mirrors the template's
/// VerticalPanningThumb (ScrollBar_themeresources.xaml:714 — Width 2, MinHeight 32, Margin 2,0,2,0, NO
/// hover/press states — the touch indicator).</item>
/// <item><see cref="Anatomy"/> — the full WinUI mouse scrollbar: 12px rail (ScrollBarSize :180), acrylic track
/// (ScrollBarTrackFill = AcrylicInAppFillColorDefaultBrush, :31/:143 both themes), two arrow RepeatButtons
/// (vertical glyphs EDDB up / EDDC down, horizontal EDD9 left / EDDA right, FontSize 8 — :387/:344/:301/:258/:186;
/// pressed arrow scale 0.875 :187; Interval=50 like every template RepeatButton :681-711), track-click PAGE
/// zones (LargeDecrease/LargeIncrease RepeatButtons :704/:710 — first page on press, then 500ms delay + 50ms
/// repeat re-evaluated against the live thumb), and the conscious expand/collapse: collapsed 2px
/// visible thumb ↔ expanded 6px (the 8px/12px thumb minus the 6px transparent stroke trick —
/// ScrollBarVerticalThumbMinWidth 8 :182, ScrollBarThumbStrokeThickness 6 :185, ScrollBarSize 12 :180; both states
/// leave the FILL's far edge inset 3px from the lane edge: collapsed rect [4,12] / expanded [0,12], fill inset 3
/// per side → [7,9] / [3,9]). Expand begins 400ms after lane hover (ScrollBarExpandBeginTime :188) / collapse
/// begins 500ms after leave (ScrollBarContractBeginTime :189), both 167ms with KeySpline 0,0,0,1 (:587/:543 →
/// <see cref="Easing.FluentPopOpen"/>); track + arrows fade 83ms LINEAR (ScrollBarOpacityChangeDuration :174,
/// plain DoubleAnimation :575-584) after the same begin times. While the thumb is DRAGGED a lane exit does NOT
/// start the contract dwell (WinUI OnPointerExited skips UpdateVisualState while IsDragging,
/// ScrollBar_Partial.cpp:548-561) — the leave replays at the drag's release edge. The arrow CELLS are ALWAYS in
/// the grid (rows 0/4, fixed extent = ScrollBarSize :703/:711) — only their OPACITY animates, so the track length
/// and thumb geometry never jump on hover. Thumb fill = ControlStrongFillColorDefault with NO hover/press recolor
/// (:26-28); arrow button background stays SubtleFillColorTransparent in EVERY state (:14 — the :226-248
/// storyboards recolor only the Arrow foreground); disabled = ControlStrongFillColorDisabled (:29) + root opacity
/// 0.5 (:436) + the mouse thumb itself hidden (ThumbVisual Opacity→0, the VerticalThumbTemplate Disabled
/// storyboard :399-406). IsTabStop = false (:206). Vertical by default; <c>horizontal: true</c> mirrors the
/// HorizontalRoot anatomy on the X axis (:672-693).</item>
/// </list>
/// Thumb POSITION is compositor-bound (a <c>TransformBind</c> on the position signal): scrolling moves the thumb the
/// same frame with no re-render/relayout and never enters the FLIP pipeline — WinUI's instant Thumb layout. ONLY the
/// conscious cross-axis 2px↔6px expand (and the 83ms chrome fades) animate, after the debounced begin times; that
/// choreography is stepped by a mounted FrameClock ticker at the engine's 16ms-per-frame convention (the
/// <c>UseAnimatedValue</c> precedent), so it is deterministic on the headless FixedFrameTimeSource host.
/// The auto-hiding OVERLAY scrollbar on scroll viewports is engine-drawn (SceneRecorder.EmitScrollbar) with its
/// timing in <c>Animation.ScrollIntegrator</c> — this control is the standalone (always-visible) ScrollBar element.
/// </summary>
public static partial class ScrollBar
{
    // Template parts (see TemplateParts). Each part's doc lists the props the control OWNS (re-asserted after any
    // modifier — a Parts customization cannot win those). Both surfaces take a trailing `TemplateParts? parts` and
    // route the same vocabulary (the legacy panning variant has only the thumb). Const VALUES happen to match the
    // internal reconcile Keys; the Keys themselves stay literal on the elements (never derived from these consts).
    /// <summary>The draggable thumb (WinUI VerticalThumb/HorizontalThumb). Owned (Anatomy): Key, the main-axis
    /// length + the cross-axis Width/HeightBind (the conscious-expand eased cross-axis size — bind-driven geometry,
    /// not style), TransformBind (the compositor-bound scroll position). On the legacy panning variant everything
    /// is style (drag lives on the rail, no binds).</summary>
    public const string PartThumb = "sb-thumb";
    /// <summary>The always-mounted acrylic track (WinUI Vertical/HorizontalTrackRect). Owned: Key, the bound Opacity (the
    /// 83ms chrome fade).</summary>
    public const string PartTrack = "sb-track";
    /// <summary>The TRAILING arrow cell (WinUI VerticalSmallIncrease — glyph EDDC, scrolls down; horizontal
    /// HorizontalSmallIncrease — glyph EDDA, scrolls right). Owned: Key, the bound Opacity (the chrome fade),
    /// Repeats + RepeatIntervalMs + OnClick (the small-change repeat step), OnHoverMove/OnPointerExit (the
    /// conscious hover lane).</summary>
    public const string PartIncreaseButton = "sb-down";
    /// <summary>The LEADING arrow cell (WinUI VerticalSmallDecrease — glyph EDDB, scrolls up; horizontal
    /// HorizontalSmallDecrease — glyph EDD9, scrolls left). Owned: same set as
    /// <see cref="PartIncreaseButton"/>.</summary>
    public const string PartDecreaseButton = "sb-up";

    // WinUI ScrollBar_themeresources.xaml metrics (line cites in the class doc).
    public const float RailSize = 12f;            // ScrollBarSize (:180)
    public const float CollapsedThumb = 2f;       // visible: ThumbMinWidth 8 − stroke 6 (:182/:185)
    public const float ExpandedThumb = 6f;        // visible: ScrollBarSize 12 − stroke 6 (:180/:185)
    public const float ThumbInset = 3f;           // fill inset from the lane edge — stroke 6 / 2, BOTH states (:185)
    public const float MinThumbLength = 30f;      // ScrollBarVerticalThumbMinHeight / HorizontalThumbMinWidth (:181/:183)
    public const float ExpandBeginMs = 400f;      // ScrollBarExpandBeginTime (:188)
    public const float ContractBeginMs = 500f;    // ScrollBarContractBeginTime (:189)
    public const float ExpandMs = 167f;           // ScrollBarExpandDuration / ContractDuration (:173/:176)
    public const float FadeMs = 83f;              // ScrollBarOpacityChangeDuration (:174)
    public const float ArrowFontSize = 8f;        // ScrollBarButtonArrowIconFontSize (:186)
    public const float ArrowScalePressed = 0.875f;// ScrollBarButtonArrowScalePressed (:187)
    public const float LineDeltaPx = 16f;         // ScrollViewerLineDelta — one arrow line step (dxaml/xcp/dxaml/lib/ScrollViewer_Partial.h:27)
    public const float RepeatDelayMs = 500f;      // RepeatButton Delay DP default (dxaml/xcp/components/dependencyObject/DependencyProperty.cpp:714-720)
    public const float RepeatIntervalMs = 50f;    // every scrollbar RepeatButton sets Interval=50 (:681-711)
    public const float PanningThumbMargin = 2f;   // VerticalPanningThumb Margin 2,0,2,0 (:714)

    public sealed record Style
    {
        public float ThumbWidth { get; init; } = 2f;                 // VerticalPanningThumb Width=2 (:714)
        public float MinThumb { get; init; } = 32f;                  // VerticalPanningThumb MinHeight=32 (:714)
        public float CornerRadius { get; init; } = 3f;               // ScrollBarCornerRadius (:190)
        public ColorF Thumb { get; init; }                          // ScrollBarPanningThumbBackground = ControlStrongFillColorDefault (:170)
        public ColorF ThumbHover { get; init; }                     // == rest: the panning thumb has NO visual states (:713-715)
        public ColorF ThumbPressed { get; init; }                   // == rest (:713-715)
        public ColorF ThumbDisabled { get; init; }                  // ScrollBarPanningThumbBackgroundDisabled = ControlStrongFillColorDisabled (:39/:442)
        public float ThumbHoverScale { get; init; } = 1f;            // no hover/press states on the panning thumb (:713-715)
        public float ThumbPressScale { get; init; } = 1f;
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Thumb = Tok.FillControlStrong,
        ThumbHover = Tok.FillControlStrong,           // WinUI: the panning thumb never recolours on hover/press (:713-715)
        ThumbPressed = Tok.FillControlStrong,
        ThumbDisabled = Tok.FillControlStrongDisabled,
    };

    /// <summary>The legacy thin panning-indicator scrollbar (see the class doc): a draggable thumb on an invisible
    /// rail; press/drag maps the pointer to an absolute 0..1 position. Kept source-compatible. Geometry mirrors the
    /// template's VerticalPanningThumb: 2px thumb, 32px min length, 2px side margins (ScrollBar_themeresources
    /// .xaml:714). <paramref name="parts"/> = per-part styling (only <see cref="PartThumb"/> exists on this
    /// variant).</summary>
    public static BoxEl Create(float fraction, float position, Action<float> onChange, float height = 200f, Style? style = null, bool disabled = false, TemplateParts? parts = null)
    {
        var s = style ?? DefaultStyle;
        fraction = Math.Clamp(fraction, 0.05f, 1f);
        position = Math.Clamp(position, 0f, 1f);
        float thumbH = MathF.Max(s.MinThumb, fraction * height);
        float travel = MathF.Max(1f, height - thumbH);
        void Set(Point2 p) => onChange(Math.Clamp((p.Y - thumbH * 0.5f) / travel, 0f, 1f));
        var thumb = new BoxEl
        {
            Width = s.ThumbWidth, Height = thumbH, Corners = CornerRadius4.All(s.CornerRadius),
            Fill = disabled ? s.ThumbDisabled : s.Thumb,
            Margin = new Edges4(PanningThumbMargin, 0f, PanningThumbMargin, 0f),   // Margin 2,0,2,0 (:714)
            HoverScale = disabled ? 1f : s.ThumbHoverScale,
            PressScale = disabled ? 1f : s.ThumbPressScale,
        };
        // Parts: the legacy panning thumb is pure style (no Key/binds; drag lives on the rail) — nothing to re-assert.
        thumb = parts.Apply(PartThumb, thumb);
        return new BoxEl
        {
            // The rail spans the thumb + its 2px side margins (:714) so the hit lane stays comfortably wide.
            Width = s.ThumbWidth + 2f * PanningThumbMargin, Height = height, Direction = 1, Role = AutomationRole.ScrollBar,
            // Disabled: drop the drag/press handlers so the thumb is inert (WinUI disabled scrollbar).
            OnPointerDown = disabled ? null : Set, OnDrag = disabled ? null : Set,
            Children =
            [
                new BoxEl { Height = position * travel },   // spacer above the thumb
                thumb,
            ],
        };
    }

    /// <summary>
    /// The full WinUI mouse-scrollbar anatomy with a FROZEN position (component props freeze at mount — use the
    /// <see cref="Anatomy(float, Signal{float}, Action{float}, float, bool, TemplateParts, bool, float, float)"/>
    /// signal overload for a live, app-controlled position).
    /// </summary>
    public static Element Anatomy(float fraction, float position, Action<float> onChange,
                                  float length = 200f, bool disabled = false, TemplateParts? parts = null,
                                  bool horizontal = false, float largeChange01 = float.NaN, float smallChange01 = float.NaN)
        => Anatomy(fraction, new Signal<float>(Math.Clamp(position, 0f, 1f)), onChange, length, disabled, parts,
                   horizontal, largeChange01, smallChange01);

    /// <summary>
    /// The full WinUI mouse-scrollbar anatomy (see the class doc for cites). <paramref name="fraction"/> =
    /// viewport/content (thumb proportion); <paramref name="position"/> = offset/(content−viewport) in 0..1, read
    /// through a signal so writes move the thumb compositor-instantly (no re-render); <paramref name="onChange"/>
    /// receives the new position for every interaction: thumb drag (absolute), track-click paging (one VIEWPORT
    /// per page — LargeDecrement→IScrollInfo::PageUp = SetVerticalOffset(offset − viewport),
    /// ScrollViewer_Partial.cpp:3146-3150 / ScrollContentPresenter_Partial.cpp:543-547 — auto-repeating at the
    /// template's Interval=50 after the 500ms RepeatButton delay, stopping when the thumb reaches the pointer),
    /// and arrow small-change (one 16px LINE per step — ScrollViewerLineDelta, ScrollViewer_Partial.h:27).
    /// <paramref name="horizontal"/> mirrors the anatomy on the X axis (HorizontalRoot, :672-693).
    /// <paramref name="largeChange01"/>/<paramref name="smallChange01"/> override the page/line amounts in 0..1
    /// position units (NaN = the WinUI-derived defaults above; pass 16/scrollableExtentPx for the exact line when
    /// the real content extent is known). <paramref name="parts"/> = per-part styling keyed by the <c>PartXxx</c>
    /// consts (see <see cref="TemplateParts"/> for the contract).
    /// </summary>
    public static Element Anatomy(float fraction, Signal<float> position, Action<float> onChange,
                                  float length = 200f, bool disabled = false, TemplateParts? parts = null,
                                  bool horizontal = false, float largeChange01 = float.NaN, float smallChange01 = float.NaN)
        => Embed.Comp(() => new ScrollBarAnatomy
        {
            Fraction = fraction,
            Position = position,
            OnChange = onChange,
            Length = length,
            Disabled = disabled,
            Parts = parts,
            Horizontal = horizontal,
            LargeChange01 = largeChange01,
            SmallChange01 = smallChange01,
        });
}

/// <summary>Component behind <see cref="ScrollBar.Anatomy"/> — owns the WinUI "conscious" state machine:
/// lane-hover dwell 400ms → expand / lane-leave dwell 500ms → contract (the template storyboards' BeginTimes,
/// modeled as a debounce so geometry never rides a delayed FLIP), then the 167ms KeySpline(0,0,0,1) cross-axis
/// width 2↔6 + the 83ms linear chrome fades, stepped per frame by a mounted <see cref="FrameClock"/> ticker
/// (16ms-per-frame engine convention — the <c>UseAnimatedValue</c> precedent; deterministic headlessly). The eased
/// values flow through SIGNALS into a cross-axis <c>Width/HeightBind</c> (scoped relayout) and bound <c>Opacity</c>s
/// (track/arrow fades, compositor-only) — the component itself never re-renders per frame. Thumb position is a
/// <c>TransformBind</c> on the position signal: instant, compositor-only, never animated. The arrow cells are
/// ALWAYS reserved, so track length and thumb geometry are hover-invariant (WinUI rows 0/4 with fixed extent,
/// :703/:711). The ticker also drives the held track-press page repeat (Interval=50 RepeatButtons :704/:710).</summary>
internal sealed class ScrollBarAnatomy : Component
{
    public float Fraction;
    public required Signal<float> Position;
    public Action<float>? OnChange;
    public float Length = 200f;
    public bool Disabled;
    /// <summary>Mirror the anatomy on the X axis (WinUI HorizontalRoot, ScrollBar_themeresources.xaml:672-693).</summary>
    public bool Horizontal;
    /// <summary>Page amount in 0..1 position units; NaN = one viewport (fraction/(1−fraction)).</summary>
    public float LargeChange01 = float.NaN;
    /// <summary>Arrow step in 0..1 position units; NaN = one 16px line under viewport ≈ Length.</summary>
    public float SmallChange01 = float.NaN;
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>ScrollBar.PartXxx</c> consts;
    /// see <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    // Engine-time step per ticker frame — the UseAnimatedValue convention (RenderContext.cs: Elapsed += 16f per
    // render) and the headless FixedFrameTimeSource step. Deterministic in the VerticalSlice harness.
    private const float TickStepMs = 16f;

    // Conscious-state machine (instance fields persist for the component's lifetime; the ticker steps them).
    private bool _laneHovered;
    private bool _expanded;
    private float _dwellMs;                  // continuous time in the hover≠expanded mismatch (toward 400/500ms)
    private float _animMs = 10_000f;         // time since the last expanded flip (drives the 167ms width + 83ms fade)
    private float _flipFromW = ScrollBar.CollapsedThumb;   // eased width at the flip instant (interruption continuity)
    private float _flipFromFade;             // eased chrome opacity at the flip instant
    private bool _exitedSinceTick;           // lane exit seen since the last ticker step (sub-part crossing coalescing)
    private float _dwellAtExit;              // dwell snapshot for a same-frame strip↔arrow crossing
    private bool _ticking;
    private Action<bool>? _setTicking;
    private Signal<float>? _widthSig;        // eased thumb cross-axis size (2 ↔ 6) → thumb Width/HeightBind
    private Signal<float>? _chromeSig;       // eased track/arrow opacity (0 ↔ 1) → the bound Opacity channels

    // Gesture state (instance fields, the conscious-machine idiom — none of these drive a re-render).
    private bool _dragging;                  // live thumb drag (WinUI Thumb.IsDragging)
    private float _grab;                     // main-axis pointer offset within the thumb at drag start
    private bool _exitWhileDragging;         // a lane exit swallowed by the IsDragging gate — replayed at the release edge
    private bool _pageHeld;                  // a track press is held in a page zone (LargeDecrease/LargeIncrease)
    private bool _pageDown;                  // page direction, fixed at press (WinUI: only the PRESSED zone repeats)
    private float _pagePointer;              // latest strip-local main-axis pointer position while the press is held
    private float _pageElapsedMs;            // accumulator toward the next page-repeat fire
    private bool _pageRepeating;             // false = still inside the 500ms initial-delay window
    private float _travel;                   // track travel px (geometry snapshot for OnTick)
    private float _thumbLen;                 // thumb main-axis length px
    private float _page;                     // resolved page amount in 0..1 position units

    public override Element Render()
    {
        var (ticking, setTicking) = UseState(false);
        var widthSig = UseSignal(ScrollBar.CollapsedThumb);
        var chromeSig = UseSignal(0f);
        _ticking = ticking;
        _setTicking = setTicking;
        _widthSig = widthSig;
        _chromeSig = chromeSig;

        bool horiz = Horizontal;
        float fraction = Math.Clamp(Fraction, 0.05f, 1f);
        float rail = ScrollBar.RailSize;
        float trackLen = MathF.Max(1f, Length - 2f * rail);   // arrow cells ALWAYS reserved (:703/:711)
        float thumbLen = MathF.Min(trackLen, MathF.Max(ScrollBar.MinThumbLength, fraction * trackLen));
        float travel = MathF.Max(1f, trackLen - thumbLen);
        // Page (LargeChange) = ONE viewport: LargeDecrement/Increment route to IScrollInfo::PageUp/PageDown =
        // SetVerticalOffset(offset ∓ viewport) (ScrollViewer_Partial.cpp:3146-3150; ScrollContentPresenter_Partial
        // .cpp:543-547) — in 0..1 position units (offset/(content−viewport)) that is fraction/(1−fraction).
        float page = !float.IsNaN(LargeChange01)
            ? MathF.Max(0.001f, LargeChange01)
            : MathF.Max(0.01f, fraction / MathF.Max(0.05f, 1f - fraction));
        // Small change = ONE 16px line per arrow step (ScrollViewerLineDelta 16.0f, ScrollViewer_Partial.h:27;
        // LineUp/LineDownImpl SetVerticalOffset(offset ∓ 16), ScrollContentPresenter_Partial.cpp:442/:467),
        // normalized assuming viewport ≈ Length (the standalone bar spans its viewport) — callers with the real
        // content extent pass smallChange01 = 16/scrollableExtentPx for the exact line.
        float small = !float.IsNaN(SmallChange01)
            ? MathF.Max(0.0001f, SmallChange01)
            : ScrollBar.LineDeltaPx * fraction / (MathF.Max(1f, Length) * MathF.Max(0.05f, 1f - fraction));
        _travel = travel;
        _thumbLen = thumbLen;
        _page = page;

        // The track strip owns thumb-drag + page-click (LargeDecrease/LargeIncrease, :704/:710). Strip-local
        // coordinates run over the whole track band, so drags compute absolute positions (the dispatcher clamps
        // pointer locals to the handler node's own box — a thumb-mounted handler could not see past itself).
        void StripDown(Point2 p)
        {
            float m = MainOf(p);
            float thumbTop = Math.Clamp(Position.Peek(), 0f, 1f) * travel;
            _exitWhileDragging = false;   // fresh gesture — clear a stale deferred leave (e.g. after a cancel)
            _pageHeld = false;
            if (m >= thumbTop && m <= thumbTop + thumbLen)
            {
                _dragging = true;
                _grab = m - thumbTop;
            }
            else
            {
                // Track-press page jump: a RepeatButton clicks on the PRESS edge, then auto-repeats while held —
                // 500ms Delay (the DP default; the template sets only Interval) then Interval=50 (:704/:710),
                // stepped by the conscious ticker against the LIVE thumb (see OnTick).
                _dragging = false;
                _pageDown = m > thumbTop + thumbLen;
                _pagePointer = m;
                _pageElapsedMs = 0f;
                _pageRepeating = false;
                _pageHeld = true;
                Move(Position.Peek() + (_pageDown ? page : -page));
                if (!_ticking) _setTicking?.Invoke(true);
            }
        }
        void StripDrag(Point2 p)
        {
            if (_dragging) Move((MainOf(p) - _grab) / travel);
            else if (_pageHeld) _pagePointer = MainOf(p);   // held page press: stop/resume re-evaluates the live pointer
        }

        // Position → pixels through the compositor only (rule: prefer a transform bind for hot values): a signal
        // write translates the thumb the SAME frame, with no re-render/reconcile/relayout and no FLIP capture —
        // WinUI's instant Thumb position. The cross-axis SIZE is the bound Width/Height on the conscious-ticker
        // signal (scoped relayout per eased step — WinUI's EnableDependentAnimation width keyframes, :585-588); the
        // bound channel ignores any static, and the mount runNow fire seeds the size before the first layout.
        // Ternary rule: the cast goes on the VALUE arm (Prop<float> ∪ Func<float> unify via the Func conversion).
        Func<float> thumbCrossBind = () => widthSig.Value;
        Func<Affine2D> thumbPositionBind = horiz
            ? () => Affine2D.Translation(Math.Clamp(Position.Value, 0f, 1f) * travel, 0f)
            : () => Affine2D.Translation(0f, Math.Clamp(Position.Value, 0f, 1f) * travel);
        var thumb = new BoxEl
        {
            Key = "sb-thumb",
            Width = horiz ? (Prop<float>)thumbLen : thumbCrossBind,   // eased by the conscious ticker (167ms, :173/:587)
            Height = horiz ? thumbCrossBind : (Prop<float>)thumbLen,
            Corners = CornerRadius4.All(3f),                    // ScrollBarCornerRadius (:190)
            Fill = Disabled ? Tok.FillControlStrongDisabled : Tok.FillControlStrong,   // (:26/:29) — no hover recolor
            // Disabled hides the mouse thumb: ThumbVisual Opacity → 0 over ScrollBarOpacityChangeDuration 83ms
            // (VerticalThumbTemplate Disabled storyboard, :399-406) — held at 0 (props freeze at mount, so there
            // is no live enabled→disabled transition to animate).
            Opacity = Disabled ? 0f : 1f,
            // Fill far-edge inset 3 in BOTH states (:185): right edge (vertical) / bottom edge (horizontal).
            Margin = horiz ? new Edges4(0f, 0f, 0f, ScrollBar.ThumbInset) : new Edges4(0f, 0f, ScrollBar.ThumbInset, 0f),
            AlignSelf = FlexAlign.End,                          // cross-axis END (far edge anchored)
            HitTestVisible = false,                             // the strip owns the pointer
            Transform = thumbPositionBind,
        };
        // Parts: restyle anything (fill, corners, the 3px inset…); the Key and the bind-driven geometry always win —
        // the eased cross-axis size (+ its static carrier) and the compositor position bind ARE the scrollbar, not style.
        if (Parts is { } tp)
        {
            var m = tp.Apply(ScrollBar.PartThumb, thumb);
            thumb = m with
            {
                Key = "sb-thumb",
                Width = horiz ? (Prop<float>)thumbLen : thumbCrossBind,
                Height = horiz ? thumbCrossBind : (Prop<float>)thumbLen,
                Transform = thumbPositionBind,
            };
        }

        var stack = new BoxEl   // the lane: column (vertical) / row (horizontal)
        {
            Direction = (byte)(horiz ? 0 : 1),
            Width = horiz ? Length : rail,
            Height = horiz ? rail : Length,
            Children =
            [
                ArrowButton(dec: true, chromeSig, () => Move(Position.Peek() - small)),
                thumb,                                          // laid out at the track start; the bind translates it
                new BoxEl { Grow = 1f },
                ArrowButton(dec: false, chromeSig, () => Move(Position.Peek() + small)),
            ],
        };

        // The interaction strip spans the track band only (arrows stay clickable above it in the z-order walk —
        // hit-testing picks the deepest interactive node, and the strip excludes the arrow cells via its margins).
        // It also carries the LANE hover (hover routes to the deepest interactive node, never to this root).
        var strip = new BoxEl
        {
            Margin = horiz ? new Edges4(rail, 0f, rail, 0f) : new Edges4(0f, rail, 0f, rail),
            OnPointerDown = Disabled ? null : StripDown,
            OnDrag = Disabled ? null : StripDrag,
            // OnClick on an OnDrag-gesture node is its RELEASE edge (implicit pointer capture delivers the release
            // to the gesture owner even off-node — the InputDispatcher release path): ends a held page-repeat and
            // replays the lane exit a live thumb drag deferred (WinUI re-evaluates visual state at drag end).
            OnClick = Disabled ? null : ReleaseStrip,
            TabStop = false,                                    // IsTabStop=False on every template part (:681-711)
            AllowFocusOnInteraction = false,                    // the page zones never take focus on press (:704/:710)
            OnHoverMove = Disabled ? null : _ => LaneHover(),
            OnPointerExit = Disabled ? null : () => LaneLeave(),
        };

        // Acrylic track, full rail (Vertical/HorizontalTrackRect :702/:680): AcrylicInAppFillColorDefaultBrush
        // (:31/:143), Opacity 0 resting — ALWAYS mounted; only its opacity fades (83ms after the debounced flip,
        // :575-584).
        Func<float> trackFadeBind = () => chromeSig.Value;
        var track = new BoxEl
        {
            Key = "sb-track",
            Corners = CornerRadius4.All(6f),                    // CornerRadius 3 × the Scale=2 converter (:193-194/:702)
            Acrylic = Tok.AcrylicFlyout,
            Fill = Tok.AcrylicFlyout.Fallback,
            Opacity = trackFadeBind,
            HitTestVisible = false,
        };
        // Parts: restyle anything (acrylic, fill, corners…); the Key and the 83ms chrome-fade bind always win.
        if (Parts is not null)
            track = Parts.Apply(ScrollBar.PartTrack, track) with { Key = "sb-track", Opacity = trackFadeBind };

        return new BoxEl
        {
            Width = horiz ? Length : rail,
            Height = horiz ? rail : Length,
            ZStack = true,
            Role = AutomationRole.ScrollBar,
            Opacity = Disabled ? 0.5f : 1f,                     // Disabled Root.Opacity 0.5 (:436)
            OnHoverMove = Disabled ? null : _ => LaneHover(),
            OnPointerExit = Disabled ? null : () => LaneLeave(),
            Children = ticking
                ? [track, strip, stack, Embed.Comp(() => new ScrollBarConsciousTicker { Owner = this })]
                : [track, strip, stack],
        };
    }

    private float MainOf(Point2 p) => Horizontal ? p.X : p.Y;

    private void Move(float to) => OnChange?.Invoke(Math.Clamp(to, 0f, 1f));

    /// <summary>The strip gesture's release edge (wired as the strip's OnClick — an OnDrag gesture delivers its
    /// release to the gesture owner via implicit capture): ends a held page-repeat and replays a lane exit that
    /// the WinUI IsDragging gate deferred (ScrollBar_Partial.cpp:548-561 — UpdateVisualState re-runs at drag end).</summary>
    private void ReleaseStrip()
    {
        _pageHeld = false;
        _dragging = false;
        if (_exitWhileDragging)
        {
            _exitWhileDragging = false;
            LaneLeave();
        }
    }

    /// <summary>Step the conscious machine one frame (called by the mounted ticker): keep any in-flight 167ms/83ms
    /// tracks playing, advance the 400/500ms dwell toward the debounced flip, write the eased values into the
    /// width/chrome signals (binds — no component re-render), and run the held track-press page repeat; unmount the
    /// ticker once everything settles.</summary>
    internal void OnTick()
    {
        _exitedSinceTick = false;
        bool busy = false;

        if (_animMs < ScrollBar.ExpandMs)           // 167ms width (and the shorter 83ms fade) still in flight
        {
            _animMs += TickStepMs;
            WriteEased();
            busy = true;
        }

        bool desired = _laneHovered;
        if (desired != _expanded)
        {
            _dwellMs += TickStepMs;
            float begin = desired ? ScrollBar.ExpandBeginMs : ScrollBar.ContractBeginMs;   // 400 / 500 (:188/:189)
            if (_dwellMs >= begin)
            {
                _flipFromW = _widthSig?.Peek() ?? ScrollBar.CollapsedThumb;   // retarget from the LIVE eased values
                _flipFromFade = _chromeSig?.Peek() ?? 0f;                     // (mid-flight continuity)
                _expanded = desired;
                _animMs = 0f;
                WriteEased();                       // t = 0 holds the from values; the next ticks ease toward target
            }
            busy = true;                            // dwell pending (or just flipped) — keep ticking
        }

        if (_pageHeld)
        {
            // Track-press auto-repeat (LargeDecrease/LargeIncrease RepeatButtons Interval=50, :704/:710; Delay =
            // the 500ms RepeatButton DP default — the template sets only Interval). Every fire re-evaluates the
            // LIVE thumb: paging stops once the thumb reaches the held pointer (the WinUI zone shrinks away
            // beneath it and the RepeatButton pauses) and re-arms with a FRESH delay when the pointer is past the
            // thumb again (pause/resume semantics, RepeatButton_Partial.cpp:530-574). Direction is fixed at press
            // — only the PRESSED zone repeats; the release edge (ReleaseStrip) or a lane exit ends the hold.
            _pageElapsedMs += TickStepMs;
            float wait = _pageRepeating ? ScrollBar.RepeatIntervalMs : ScrollBar.RepeatDelayMs;
            while (_pageElapsedMs >= wait)
            {
                _pageElapsedMs -= wait;
                float thumbTop = Math.Clamp(Position.Peek(), 0f, 1f) * _travel;
                bool pastThumb = _pageDown ? _pagePointer > thumbTop + _thumbLen : _pagePointer < thumbTop;
                if (!pastThumb)
                {
                    _pageElapsedMs = 0f;
                    _pageRepeating = false;         // paused — fresh delay once the pointer is past the thumb again
                    break;
                }
                _pageRepeating = true;
                Move(Position.Peek() + (_pageDown ? _page : -_page));
                wait = ScrollBar.RepeatIntervalMs;
            }
            busy = true;                            // keep ticking while held (the release edge clears _pageHeld)
        }

        if (!busy && _ticking) _setTicking?.Invoke(false);   // settled — unmount the ticker, the frame loop idles
    }

    /// <summary>Write the eased width/chrome values for the current <c>_animMs</c> into the bind signals
    /// (equality-gated: settled values are no-op writes).</summary>
    private void WriteEased()
    {
        if (_widthSig is null || _chromeSig is null) return;
        _widthSig.Value = EasedThumbWidth();
        _chromeSig.Value = EasedChromeFade();
    }

    private void LaneHover()
    {
        // A BARE hover move while a gesture latch is still set means the gesture was CANCELLED (capture loss /
        // window blur deliver no release click, and the dispatcher suppresses bare hover during a live gesture) —
        // heal the latches so the IsDragging gate can't stick the bar expanded.
        if (_dragging || _pageHeld)
        {
            _dragging = false;
            _pageHeld = false;
            _exitWhileDragging = false;   // the pointer is back over the lane — nothing to replay
        }
        if (!_laneHovered)
        {
            _laneHovered = true;
            // A strip↔arrow crossing fires exit+enter inside ONE dispatch (before any ticker step): restore the
            // dwell so moving along the lane never resets the 400ms expand begin (WinUI treats the bar as one lane).
            _dwellMs = _exitedSinceTick ? _dwellAtExit : 0f;
        }
        if (_laneHovered != _expanded && !_ticking) _setTicking?.Invoke(true);   // a dwell is pending → start stepping
    }

    private void LaneLeave()
    {
        // The held page press pauses when the pointer leaves the lane (the WinUI RepeatButton pauses off-node);
        // this also ends the hold on a pointer cancel (the dispatcher fires the gesture owner's OnPointerExit).
        _pageHeld = false;
        if (!_laneHovered) return;
        if (_dragging)
        {
            // WinUI: OnPointerExited sets m_isPointerOver=FALSE but SKIPS UpdateVisualState while the thumb
            // IsDragging (ScrollBar_Partial.cpp:548-561) — the bar stays Expanded until the drag ends; the
            // release edge (ReleaseStrip) replays this leave (the drag-end re-evaluation).
            _exitWhileDragging = true;
            return;
        }
        _laneHovered = false;
        _exitedSinceTick = true;
        _dwellAtExit = _dwellMs;
        _dwellMs = 0f;
        if (_laneHovered != _expanded && !_ticking) _setTicking?.Invoke(true);   // a dwell is pending → start stepping
    }

    // Width 2 ↔ 6 over ScrollBarExpandDuration/ContractDuration 167ms, KeySpline 0,0,0,1 (:173/:176/:587/:543).
    private float EasedThumbWidth()
    {
        float target = _expanded ? ScrollBar.ExpandedThumb : ScrollBar.CollapsedThumb;
        float t = Math.Clamp(_animMs / ScrollBar.ExpandMs, 0f, 1f);
        return _flipFromW + (target - _flipFromW) * Easings.Ease(Easing.FluentPopOpen, t);
    }

    // Track/arrow opacity 0 ↔ 1 over ScrollBarOpacityChangeDuration 83ms, LINEAR (plain DoubleAnimation, :575-584).
    private float EasedChromeFade()
    {
        float target = _expanded ? 1f : 0f;
        float t = Math.Clamp(_animMs / ScrollBar.FadeMs, 0f, 1f);
        return _flipFromFade + (target - _flipFromFade) * t;
    }

    /// <summary>A 12×12 arrow RepeatButton cell (:703/:711 — main-axis extent = ScrollBarSize, ALWAYS in the grid):
    /// vertical glyphs EDDB up / EDDC down (:387/:344), horizontal EDD9 left / EDDA right (:301/:258), FontSize 8
    /// (:186), arrow padding 4 toward the rail end (ScrollBarVertical/Horizontal Decrease/Increase margins
    /// :195-198), foreground ControlStrongFill → TextSecondary hover/press (:22-24), background
    /// SubtleFillColorTransparent in EVERY state (:14 — the storyboards recolor only the foreground), pressed arrow
    /// scale 0.875 (:187). Auto-repeats while held at the template's Interval=50 (every scrollbar RepeatButton
    /// sets it, :681-711) via the engine RepeatTicker; only its OPACITY fades.</summary>
    private Element ArrowButton(bool dec, Signal<float> chrome, Action onClick)
    {
        Func<float> fadeBind = () => chrome.Value;              // 83ms fade (the conscious ticker eases the signal)
        Action? click = Disabled ? null : onClick;
        Action<Point2>? laneHover = Disabled ? null : _ => LaneHover();   // the arrow cell is part of the hover lane
        Action? laneLeave = Disabled ? null : () => LaneLeave();
        bool horiz = Horizontal;
        var cell = new BoxEl
        {
            Key = dec ? "sb-up" : "sb-down",
            Width = ScrollBar.RailSize,
            Height = ScrollBar.RailSize,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Padding = horiz
                ? (dec ? new Edges4(4f, 0f, 0f, 0f) : new Edges4(0f, 0f, 4f, 0f))    // (:195-196)
                : (dec ? new Edges4(0f, 4f, 0f, 0f) : new Edges4(0f, 0f, 0f, 4f)),   // (:197-198)
            Fill = ColorF.Transparent,                          // ScrollBarButtonBackground, ALL states (:14)
            Opacity = fadeBind,
            Repeats = true,
            RepeatIntervalMs = ScrollBar.RepeatIntervalMs,      // Interval=50 (:703/:711); Delay stays the 500ms default
            TabStop = false,                                    // IsTabStop=False on every part (:681-711)
            OnClick = click,
            IsEnabled = !Disabled,
            PressScale = ScrollBar.ArrowScalePressed,           // (:187)
            OnHoverMove = laneHover,
            OnPointerExit = laneLeave,
            Children =
            [
                // VerticalDecrement EDDB / Increment EDDC (:387/:344); HorizontalDecrement EDD9 / Increment EDDA (:301/:258)
                new TextEl(horiz ? (dec ? IconGlyphs.CaretLeftSolid8 : IconGlyphs.CaretRightSolid8) : (dec ? IconGlyphs.CaretUpSolid8 : IconGlyphs.CaretDownSolid8))
                {
                    Size = ScrollBar.ArrowFontSize,             // (:186)
                    FontFamily = Theme.IconFont,
                    Color = Disabled ? Tok.FillControlStrongDisabled : Tok.FillControlStrong,   // (:22/:25)
                    HoverColor = Tok.TextSecondary,             // (:23)
                    PressedColor = Tok.TextSecondary,           // (:24)
                },
            ],
        };
        // Parts: restyle anything (fill, padding, the glyph ramp via the TextEl child…); the Key, the chrome-fade
        // bind, the repeat-step click (with the template's Interval=50) and the hover-lane handlers always win —
        // clobbering them would break the conscious expand/contract on arrow hover.
        if (Parts is { } ap)
        {
            var m = ap.Apply(dec ? ScrollBar.PartDecreaseButton : ScrollBar.PartIncreaseButton, cell);
            cell = m with
            {
                Key = dec ? "sb-up" : "sb-down",
                Opacity = fadeBind,
                Repeats = true,
                RepeatIntervalMs = ScrollBar.RepeatIntervalMs,
                OnClick = click,
                OnHoverMove = laneHover,
                OnPointerExit = laneLeave,
            };
        }
        return cell;
    }
}

/// <summary>Per-frame stepper for the conscious scrollbar (the DebounceTicker idiom): mounted only while a dwell or
/// the 167ms/83ms tracks are live; subscribes to the host frame clock so <see cref="ScrollBarAnatomy.OnTick"/> runs
/// every frame, and is unmounted by the owner when everything settles (the frame loop idles again).</summary>
internal sealed class ScrollBarConsciousTicker : Component
{
    public required ScrollBarAnatomy Owner;

    public override Element Render()
    {
        var tick = UseContext(FrameClock.Tick);   // re-render every frame while mounted
        UseEffect(() => Owner.OnTick(), tick);
        return new BoxEl { HitTestVisible = false };
    }
}
