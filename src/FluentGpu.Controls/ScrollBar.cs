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
/// VerticalPanningThumb (ScrollBar_themeresources.xaml:713-715 — the touch indicator).</item>
/// <item><see cref="Anatomy"/> — the full WinUI mouse scrollbar: 12px rail (ScrollBarSize :180), acrylic track
/// (ScrollBarTrackFill = AcrylicInAppFillColorDefaultBrush, :31/:143 both themes), two arrow RepeatButtons
/// (glyphs EDDB up / EDDC down at FontSize 8 — :186/:344/:387; pressed arrow scale 0.875 :187), track-click PAGE
/// zones (LargeDecrease/LargeIncrease RepeatButtons :704/:710), and the conscious expand/collapse: collapsed 2px
/// visible thumb ↔ expanded 6px (the 8px/12px thumb minus the 6px transparent stroke trick —
/// ScrollBarVerticalThumbMinWidth 8 :182, ScrollBarThumbStrokeThickness 6 :185, ScrollBarSize 12 :180; both states
/// leave the FILL's right edge inset 3px from the lane edge: collapsed rect [4,12] / expanded [0,12], fill inset 3
/// per side → [7,9] / [3,9]). Expand begins 400ms after lane hover (ScrollBarExpandBeginTime :188) / collapse
/// begins 500ms after leave (ScrollBarContractBeginTime :189), both 167ms with KeySpline 0,0,0,1 (:587/:543 →
/// <see cref="Easing.FluentPopOpen"/>); track + arrows fade 83ms LINEAR (ScrollBarOpacityChangeDuration :174,
/// plain DoubleAnimation :575-584) after the same begin times. The arrow CELLS are ALWAYS in the grid (rows 0/4,
/// fixed Height = ScrollBarSize :703/:711) — only their OPACITY animates, so the track length and thumb geometry
/// never jump on hover. Thumb fill = ControlStrongFillColorDefault with NO hover/press recolor (:26-28); arrow
/// button background stays SubtleFillColorTransparent in EVERY state (:14 — the :226-248 storyboards recolor only
/// the Arrow foreground); disabled = ControlStrongFillColorDisabled (:29) + root opacity 0.5 (:436).
/// IsTabStop = false (:206).</item>
/// </list>
/// Thumb POSITION is compositor-bound (a <c>TransformBind</c> on the position signal): scrolling moves the thumb the
/// same frame with no re-render/relayout and never enters the FLIP pipeline — WinUI's instant Thumb layout. ONLY the
/// conscious cross-axis 2px↔6px expand (and the 83ms chrome fades) animate, after the debounced begin times; that
/// choreography is stepped by a mounted FrameClock ticker at the engine's 16ms-per-frame convention (the
/// <c>UseAnimatedValue</c> precedent), so it is deterministic on the headless FixedFrameTimeSource host.
/// The auto-hiding OVERLAY scrollbar on scroll viewports is engine-drawn (SceneRecorder.EmitScrollbar) with its
/// timing in <c>Animation.ScrollAnimator</c> — this control is the standalone (always-visible) ScrollBar element.
/// </summary>
public static partial class ScrollBar
{
    // WinUI ScrollBar_themeresources.xaml metrics (line cites in the class doc).
    public const float RailSize = 12f;            // ScrollBarSize (:180)
    public const float CollapsedThumb = 2f;       // visible: ThumbMinWidth 8 − stroke 6 (:182/:185)
    public const float ExpandedThumb = 6f;        // visible: ScrollBarSize 12 − stroke 6 (:180/:185)
    public const float ThumbInset = 3f;           // fill inset from the lane edge — stroke 6 / 2, BOTH states (:185)
    public const float MinThumbLength = 30f;      // ScrollBarVerticalThumbMinHeight (:181)
    public const float ExpandBeginMs = 400f;      // ScrollBarExpandBeginTime (:188)
    public const float ContractBeginMs = 500f;    // ScrollBarContractBeginTime (:189)
    public const float ExpandMs = 167f;           // ScrollBarExpandDuration / ContractDuration (:173/:176)
    public const float FadeMs = 83f;              // ScrollBarOpacityChangeDuration (:174)
    public const float ArrowFontSize = 8f;        // ScrollBarButtonArrowIconFontSize (:186)
    public const float ArrowScalePressed = 0.875f;// ScrollBarButtonArrowScalePressed (:187)

    public sealed record Style
    {
        public float ThumbWidth { get; init; } = 8f;                 // ScrollBarVerticalThumbMinWidth (collapsed)
        public float MinThumb { get; init; } = 30f;                  // ScrollBarVerticalThumbMinHeight
        public float CornerRadius { get; init; } = 3f;               // ScrollBarCornerRadius
        public ColorF Thumb { get; init; }                          // ControlStrongFillColorDefault (rest)
        public ColorF ThumbHover { get; init; }                     // ScrollBarThumbFillPointerOver (== rest, no recolour)
        public ColorF ThumbPressed { get; init; }                   // ScrollBarThumbFillPressed (== rest, no recolour)
        public ColorF ThumbDisabled { get; init; }                  // ControlStrongFillColorDisabled
        public float ThumbHoverScale { get; init; } = 1.15f;
        public float ThumbPressScale { get; init; } = 1.15f;
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Thumb = Tok.FillControlStrong,
        ThumbHover = Tok.FillControlStrong,           // WinUI: hover/press do NOT recolour the thumb (:27-28)
        ThumbPressed = Tok.FillControlStrong,
        ThumbDisabled = Tok.FillControlStrongDisabled,
    };

    /// <summary>The legacy thin panning-indicator scrollbar (see the class doc): a draggable thumb on an invisible
    /// rail; press/drag maps the pointer to an absolute 0..1 position. Kept source-compatible.</summary>
    public static BoxEl Create(float fraction, float position, Action<float> onScroll, float height = 200f, Style? style = null, bool disabled = false)
    {
        var s = style ?? DefaultStyle;
        fraction = Math.Clamp(fraction, 0.05f, 1f);
        position = Math.Clamp(position, 0f, 1f);
        float thumbH = MathF.Max(s.MinThumb, fraction * height);
        float travel = MathF.Max(1f, height - thumbH);
        void Set(Point2 p) => onScroll(Math.Clamp((p.Y - thumbH * 0.5f) / travel, 0f, 1f));
        var thumb = new BoxEl
        {
            Width = s.ThumbWidth, Height = thumbH, Corners = CornerRadius4.All(s.CornerRadius),
            Fill = disabled ? s.ThumbDisabled : s.Thumb,
            HoverScale = disabled ? 1f : s.ThumbHoverScale,
            PressScale = disabled ? 1f : s.ThumbPressScale,
        };
        return new BoxEl
        {
            Width = s.ThumbWidth, Height = height, Direction = 1, Role = AutomationRole.ScrollBar,
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
    /// <see cref="Anatomy(float, Signal{float}, Action{float}, float, bool)"/> signal overload for a live,
    /// app-controlled position).
    /// </summary>
    public static Element Anatomy(float fraction, float position, Action<float> onScroll,
                                  float length = 200f, bool disabled = false)
        => Anatomy(fraction, new Signal<float>(Math.Clamp(position, 0f, 1f)), onScroll, length, disabled);

    /// <summary>
    /// The full WinUI mouse-scrollbar anatomy (see the class doc for cites). <paramref name="fraction"/> =
    /// viewport/content (thumb proportion); <paramref name="position"/> = offset/(content−viewport) in 0..1, read
    /// through a signal so writes move the thumb compositor-instantly (no re-render); <paramref name="onScroll"/>
    /// receives the new position for every interaction: thumb drag (absolute), track-click paging (±viewport·0.875
    /// per repeat), and arrow small-change (page/8 per repeat — the engine RepeatTicker cadence stands in for the
    /// template's Interval=50 RepeatButtons, :681-711).
    /// </summary>
    public static Element Anatomy(float fraction, Signal<float> position, Action<float> onScroll,
                                  float length = 200f, bool disabled = false)
        => Embed.Comp(() => new ScrollBarAnatomy
        {
            Fraction = fraction,
            Position = position,
            OnScroll = onScroll,
            Length = length,
            Disabled = disabled,
        });
}

/// <summary>Component behind <see cref="ScrollBar.Anatomy"/> — owns the WinUI "conscious" state machine:
/// lane-hover dwell 400ms → expand / lane-leave dwell 500ms → contract (the template storyboards' BeginTimes,
/// modeled as a debounce so geometry never rides a delayed FLIP), then the 167ms KeySpline(0,0,0,1) cross-axis
/// width 2↔6 + the 83ms linear chrome fades, stepped per frame by a mounted <see cref="FrameClock"/> ticker
/// (16ms-per-frame engine convention — the <c>UseAnimatedValue</c> precedent; deterministic headlessly). The eased
/// values flow through SIGNALS into a <c>WidthBind</c> (thumb cross-axis, scoped relayout) and <c>OpacityBind</c>s
/// (track/arrow fades, compositor-only) — the component itself never re-renders per frame. Thumb position is a
/// <c>TransformBind</c> on the position signal: instant, compositor-only, never animated. The arrow cells are
/// ALWAYS reserved, so track length and thumb geometry are hover-invariant (WinUI rows 0/4 with fixed Height,
/// :703/:711).</summary>
internal sealed class ScrollBarAnatomy : Component
{
    public float Fraction;
    public required Signal<float> Position;
    public Action<float>? OnScroll;
    public float Length = 200f;
    public bool Disabled;

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
    private Signal<float>? _widthSig;        // eased thumb cross-axis width (2 ↔ 6) → thumb WidthBind
    private Signal<float>? _chromeSig;       // eased track/arrow opacity (0 ↔ 1) → OpacityBinds

    public override Element Render()
    {
        var (ticking, setTicking) = UseState(false);
        var widthSig = UseSignal(ScrollBar.CollapsedThumb);
        var chromeSig = UseSignal(0f);
        var grab = UseRef(0f);
        var dragging = UseRef(false);
        _ticking = ticking;
        _setTicking = setTicking;
        _widthSig = widthSig;
        _chromeSig = chromeSig;

        float fraction = Math.Clamp(Fraction, 0.05f, 1f);
        float rail = ScrollBar.RailSize;
        float trackLen = MathF.Max(1f, Length - 2f * rail);   // arrow cells ALWAYS reserved (:703/:711)
        float thumbLen = MathF.Min(trackLen, MathF.Max(ScrollBar.MinThumbLength, fraction * trackLen));
        float travel = MathF.Max(1f, trackLen - thumbLen);
        float page = MathF.Max(0.01f, fraction * 0.875f / MathF.Max(0.05f, 1f - fraction));   // viewport·0.875 in 0..1
        float small = page / 8f;

        void Move(float to) => OnScroll?.Invoke(Math.Clamp(to, 0f, 1f));

        // The track strip owns thumb-drag + page-click (LargeDecrease/LargeIncrease, :704/:710). Strip-local
        // coordinates run over the whole track band, so drags compute absolute positions (the dispatcher clamps
        // pointer locals to the handler node's own box — a thumb-mounted handler could not see past itself).
        void StripDown(Point2 p)
        {
            float thumbTop = Math.Clamp(Position.Peek(), 0f, 1f) * travel;
            if (p.Y >= thumbTop && p.Y <= thumbTop + thumbLen)
            {
                dragging.Value = true;
                grab.Value = p.Y - thumbTop;
            }
            else
            {
                dragging.Value = false;
                Move(Position.Peek() + (p.Y < thumbTop ? -page : page));   // track-click page jump
            }
        }
        void StripDrag(Point2 p)
        {
            if (dragging.Value) Move((p.Y - grab.Value) / travel);
        }

        // Position → pixels through the compositor only (rule: prefer a transform bind for hot values): a signal
        // write translates the thumb the SAME frame, with no re-render/reconcile/relayout and no FLIP capture —
        // WinUI's instant Thumb position. The cross-axis WIDTH is a WidthBind on the conscious-ticker signal
        // (scoped relayout per eased step — WinUI's EnableDependentAnimation width keyframes, :585-588); the static
        // Width carries the CURRENT eased value so a re-render's column write never clobbers the bound width.
        var thumb = new BoxEl
        {
            Key = "sb-thumb",
            Width = widthSig.Peek(),                            // eased by the conscious ticker (167ms, :173/:587)
            WidthBind = () => widthSig.Value,
            Height = thumbLen,
            Corners = CornerRadius4.All(3f),                    // ScrollBarCornerRadius (:190)
            Fill = Disabled ? Tok.FillControlStrongDisabled : Tok.FillControlStrong,   // (:26/:29) — no hover recolor
            Margin = new Edges4(0f, 0f, ScrollBar.ThumbInset, 0f),   // fill right edge inset 3 in BOTH states (:185)
            AlignSelf = FlexAlign.End,                          // cross-axis END in a flex COLUMN (right edge anchored)
            HitTestVisible = false,                             // the strip owns the pointer
            TransformBind = () => Affine2D.Translation(0f, Math.Clamp(Position.Value, 0f, 1f) * travel),
        };

        var column = new BoxEl
        {
            Direction = 1,
            Width = rail,
            Height = Length,
            Children =
            [
                ArrowButton(up: true, chromeSig, () => Move(Position.Peek() - small)),
                thumb,                                          // laid out at the track top; the bind translates it
                new BoxEl { Grow = 1f },
                ArrowButton(up: false, chromeSig, () => Move(Position.Peek() + small)),
            ],
        };

        // The interaction strip spans the track band only (arrows stay clickable above it in the z-order walk —
        // hit-testing picks the deepest interactive node, and the strip excludes the arrow cells via its margins).
        // It also carries the LANE hover (hover routes to the deepest interactive node, never to this root).
        var strip = new BoxEl
        {
            Margin = new Edges4(0f, rail, 0f, rail),
            OnPointerDown = Disabled ? null : StripDown,
            OnDrag = Disabled ? null : StripDrag,
            OnHoverMove = Disabled ? null : _ => LaneHover(),
            OnPointerExit = Disabled ? null : () => LaneLeave(),
        };

        // Acrylic track, full rail (VerticalTrackRect :702): AcrylicInAppFillColorDefaultBrush (:31/:143), Opacity 0
        // resting (:702) — ALWAYS mounted; only its opacity fades (83ms after the debounced flip, :575-584).
        var track = new BoxEl
        {
            Key = "sb-track",
            Corners = CornerRadius4.All(6f),                    // CornerRadius 3 × the Scale=2 converter (:193-194/:702)
            Acrylic = Tok.AcrylicFlyout,
            Fill = Tok.AcrylicFlyout.Fallback,
            OpacityBind = () => chromeSig.Value,
            HitTestVisible = false,
        };

        return new BoxEl
        {
            Width = rail,
            Height = Length,
            ZStack = true,
            Role = AutomationRole.ScrollBar,
            Opacity = Disabled ? 0.5f : 1f,                     // Disabled Root.Opacity 0.5 (:436)
            OnHoverMove = Disabled ? null : _ => LaneHover(),
            OnPointerExit = Disabled ? null : () => LaneLeave(),
            Children = ticking
                ? [track, strip, column, Embed.Comp(() => new ScrollBarConsciousTicker { Owner = this })]
                : [track, strip, column],
        };
    }

    /// <summary>Step the conscious machine one frame (called by the mounted ticker): keep any in-flight 167ms/83ms
    /// tracks playing, advance the 400/500ms dwell toward the debounced flip, and write the eased values into the
    /// width/chrome signals (binds — no component re-render); unmount the ticker once everything settles.</summary>
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
        if (!_laneHovered) return;
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

    /// <summary>A 12×12 arrow RepeatButton cell (:703/:711 — Height = ScrollBarSize, ALWAYS in the grid): glyph EDDB
    /// up / EDDC down at FontSize 8 (:387/:344/:186), arrow padding 4 toward the rail end (ScrollBarVertical
    /// Decrease/Increase margins :197-198), foreground ControlStrongFill → TextSecondary hover/press (:22-24),
    /// background SubtleFillColorTransparent in EVERY state (:14 — the storyboards recolor only the foreground),
    /// pressed arrow scale 0.875 (:187). Auto-repeats while held (engine RepeatTicker); only its OPACITY fades.</summary>
    private Element ArrowButton(bool up, Signal<float> chrome, Action onClick)
        => new BoxEl
        {
            Key = up ? "sb-up" : "sb-down",
            Width = ScrollBar.RailSize,
            Height = ScrollBar.RailSize,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Padding = up ? new Edges4(0f, 4f, 0f, 0f) : new Edges4(0f, 0f, 0f, 4f),   // (:197-198)
            Fill = ColorF.Transparent,                          // ScrollBarButtonBackground, ALL states (:14)
            OpacityBind = () => chrome.Value,                   // 83ms fade (the conscious ticker eases the signal)
            Repeats = true,
            TabStop = false,                                    // IsTabStop=False on every part (:681-711)
            OnClick = Disabled ? null : onClick,
            IsEnabled = !Disabled,
            PressScale = ScrollBar.ArrowScalePressed,           // (:187)
            OnHoverMove = Disabled ? null : _ => LaneHover(),   // the arrow cell is part of the hover lane
            OnPointerExit = Disabled ? null : () => LaneLeave(),
            Children =
            [
                new TextEl(up ? "" : "")            // VerticalDecrement EDDB / Increment EDDC (:387/:344)
                {
                    Size = ScrollBar.ArrowFontSize,             // (:186)
                    FontFamily = Theme.IconFont,
                    Color = Disabled ? Tok.FillControlStrongDisabled : Tok.FillControlStrong,   // (:22/:25)
                    HoverColor = Tok.TextSecondary,             // (:23)
                    PressedColor = Tok.TextSecondary,           // (:24)
                },
            ],
        };
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
