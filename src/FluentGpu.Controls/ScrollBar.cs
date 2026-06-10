using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>ScrollBar</c>. Two surfaces:
/// <list type="bullet">
/// <item><see cref="Create"/> — the legacy thin PANNING-indicator variant (thumb-only, absolute drag), kept
/// source/behavior-compatible (VerticalSlice check 48 and the gallery drive it); it mirrors the template's
/// VerticalPanningThumb (ScrollBar_themeresources.xaml:713-715 — the touch indicator).</item>
/// <item><see cref="Anatomy"/> — the full WinUI mouse scrollbar: 12px rail (ScrollBarSize :180), acrylic track
/// (ScrollBarTrackFill = AcrylicInAppFillColorDefaultBrush, :31/:143 both themes), two arrow RepeatButtons
/// (glyphs EDDB up / EDDC down / EDD9 left / EDDA right at FontSize 8 — :186/:258/:301/:344/:387; pressed arrow
/// scale 0.875 :187), track-click PAGE zones (LargeDecrease/LargeIncrease RepeatButtons :704/:710), and the
/// conscious expand/collapse: collapsed 2px visible thumb ↔ expanded 6px (the 8px/12px thumb minus the 6px
/// transparent stroke trick — ScrollBarVerticalThumbMinWidth 8 :182, ScrollBarThumbStrokeThickness 6 :185,
/// ScrollBarSize 12), expand begins 400ms after lane hover (ScrollBarExpandBeginTime :188) / collapse begins
/// 500ms after leave (ScrollBarContractBeginTime :189), both 167ms with KeySpline 0,0,0,1 (:587 →
/// <see cref="Easing.FluentPopOpen"/>); fades 83ms (ScrollBarOpacityChangeDuration :174). Thumb fill =
/// ControlStrongFillColorDefault with NO hover/press recolor (:26-28), disabled = ControlStrongFillColorDisabled
/// (:29) + root opacity 0.5 (Disabled state :436). IsTabStop = false (:206).</item>
/// </list>
/// The auto-hiding OVERLAY scrollbar on scroll viewports is engine-drawn (SceneRecorder.EmitScrollbar) with its
/// timing in <c>Animation.ScrollAnimator</c> — this control is the standalone (always-visible) ScrollBar element.
/// </summary>
public static partial class ScrollBar
{
    // WinUI ScrollBar_themeresources.xaml metrics (line cites in the class doc).
    public const float RailSize = 12f;            // ScrollBarSize (:180)
    public const float CollapsedThumb = 2f;       // visible: ThumbMinWidth 8 − stroke 6 (:182/:185)
    public const float ExpandedThumb = 6f;        // visible: ScrollBarSize 12 − stroke 6 (:180/:185)
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
    /// The full WinUI mouse-scrollbar anatomy (see the class doc for cites). <paramref name="fraction"/> =
    /// viewport/content (thumb proportion), <paramref name="position"/> = offset/(content−viewport) in 0..1;
    /// <paramref name="onScroll"/> receives the new position for every interaction: thumb drag (absolute),
    /// track-click paging (±viewport·0.875 per repeat), and arrow small-change (page/8 per repeat — the engine
    /// RepeatTicker cadence stands in for the template's Interval=50 RepeatButtons, :681-711).
    /// </summary>
    public static Element Anatomy(float fraction, float position, Action<float> onScroll,
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

/// <summary>Component behind <see cref="ScrollBar.Anatomy"/> — owns the conscious expand/collapse hover state.
/// Geometry changes ride the FLIP pipeline with the WinUI begin-times (DelayMs 400 expand / 500 collapse) and the
/// 167ms KeySpline(0,0,0,1) tween; track/buttons mount with the 83ms fade after the same expand delay (their exit
/// fade re-uses the spec captured at mount — a begin-time-only divergence on the collapse fade, documented).</summary>
internal sealed class ScrollBarAnatomy : Component
{
    public float Fraction;
    public float Position;
    public Action<float>? OnScroll;
    public float Length = 200f;
    public bool Disabled;

    public override Element Render()
    {
        var (expanded, setExpanded) = UseState(false);
        var grab = UseRef(0f);
        var dragging = UseRef(false);

        float fraction = Math.Clamp(Fraction, 0.05f, 1f);
        float position = Math.Clamp(Position, 0f, 1f);
        float rail = ScrollBar.RailSize;
        float buttons = expanded ? rail : 0f;                       // arrow cells only exist expanded (:575-584 fades)
        float trackLen = MathF.Max(1f, Length - 2f * buttons);
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
            float thumbTop = position * travel;
            if (p.Y >= thumbTop && p.Y <= thumbTop + thumbLen)
            {
                dragging.Value = true;
                grab.Value = p.Y - thumbTop;
            }
            else
            {
                dragging.Value = false;
                Move(Position + (p.Y < thumbTop ? -page : page));   // track-click page jump
            }
        }
        void StripDrag(Point2 p)
        {
            if (dragging.Value) Move((p.Y - grab.Value) / travel);
        }

        float delay = expanded ? ScrollBar.ExpandBeginMs : ScrollBar.ContractBeginMs;   // 400 expand / 500 collapse
        var geometry = new LayoutTransition(
            TransitionChannels.Bounds,
            TransitionDynamics.Tween(ScrollBar.ExpandMs, Easing.FluentPopOpen),         // 167ms KeySpline 0,0,0,1
            SizeMode.Reveal,
            DelayMs: delay);

        float thumbW = expanded ? ScrollBar.ExpandedThumb : ScrollBar.CollapsedThumb;
        // Cross-axis inset: collapsed thumb rides 1px from the edge (8px rect, +2 translate, 6px transparent stroke
        // → 2px fill at [cross−3, cross−1]); expanded centred 3px insets (12 − 6).
        float thumbInset = expanded ? 3f : 1f;

        var column = new BoxEl
        {
            Direction = 1,
            Width = rail,
            Height = Length,
            Children =
            [
                expanded ? ArrowButton(up: true, () => Move(Position - small)) : new BoxEl(),
                new BoxEl { Height = position * travel },           // resting offset above the thumb
                new BoxEl
                {
                    Key = "sb-thumb",
                    Width = thumbW,
                    Height = thumbLen,
                    Corners = CornerRadius4.All(3f),                // ScrollBarCornerRadius (:190)
                    Fill = Disabled ? Tok.FillControlStrongDisabled : Tok.FillControlStrong,   // (:26/:29) — no hover recolor
                    Margin = new Edges4(0f, 0f, thumbInset, 0f),
                    AlignSelf = FlexAlign.End,
                    HitTestVisible = false,                         // the strip owns the pointer
                    Animate = geometry,
                },
                new BoxEl { Grow = 1f },
                expanded ? ArrowButton(up: false, () => Move(Position + small)) : new BoxEl(),
            ],
        };

        // The interaction strip spans the track band only (arrows stay clickable above it in the z-order walk —
        // hit-testing picks the deepest interactive node, and the strip excludes the arrow cells via its margins).
        var strip = new BoxEl
        {
            Margin = new Edges4(0f, buttons, 0f, buttons),
            OnPointerDown = Disabled ? null : StripDown,
            OnDrag = Disabled ? null : StripDrag,
        };

        var layers = new List<Element>(3);
        if (expanded)
        {
            // Acrylic track, full rail (VerticalTrackRect :702): AcrylicInAppFillColorDefaultBrush (:31/:143),
            // fading in 83ms after the 400ms expand begin (:575-584).
            layers.Add(new BoxEl
            {
                Key = "sb-track",
                Corners = CornerRadius4.All(6f),                    // CornerRadius × 2 converter (:680 Scale=2)
                Acrylic = Tok.AcrylicFlyout,
                Fill = Tok.AcrylicFlyout.Fallback,
                HitTestVisible = false,
                Animate = new LayoutTransition(
                    TransitionChannels.Opacity,
                    TransitionDynamics.Tween(ScrollBar.FadeMs, Easing.Linear),
                    Enter: new EnterExit(Opacity: 0f, Active: true),
                    Exit: new EnterExit(Opacity: 0f, Active: true),
                    DelayMs: ScrollBar.ExpandBeginMs),
            });
        }
        layers.Add(strip);
        layers.Add(column);

        return new BoxEl
        {
            Width = rail,
            Height = Length,
            ZStack = true,
            Role = AutomationRole.ScrollBar,
            Opacity = Disabled ? 0.5f : 1f,                         // Disabled Root.Opacity 0.5 (:436)
            // Lane hover drives the conscious expand; the engine eases the visual change after the WinUI delays.
            OnHoverMove = Disabled ? null : _ => { if (!expanded) setExpanded(true); },
            OnPointerExit = Disabled ? null : () => setExpanded(false),
            Children = layers.ToArray(),
        };
    }

    /// <summary>A 12×12 arrow RepeatButton (:703/:711): glyph EDDB up / EDDC down at FontSize 8, foreground
    /// ControlStrongFill → TextSecondary hover/press (:22-24), background SubtleSecondary hover / ControlStrongFill
    /// press (:15-16), pressed arrow scale 0.875 (:187). Auto-repeats while held (engine RepeatTicker).</summary>
    private Element ArrowButton(bool up, Action onClick)
        => new BoxEl
        {
            Key = up ? "sb-up" : "sb-down",
            Width = ScrollBar.RailSize,
            Height = ScrollBar.RailSize,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(2f),
            Fill = ColorF.Transparent,                              // ScrollBarButtonBackground (:14)
            HoverFill = Tok.FillSubtleSecondary,                    // PointerOver (:15)
            PressedFill = Tok.FillControlStrong,                    // Pressed (:16)
            Repeats = true,
            TabStop = false,                                        // IsTabStop=False on every part (:681-711)
            OnClick = Disabled ? null : onClick,
            IsEnabled = !Disabled,
            PressScale = ScrollBar.ArrowScalePressed,               // (:187)
            Animate = new LayoutTransition(
                TransitionChannels.Opacity,
                TransitionDynamics.Tween(ScrollBar.FadeMs, Easing.Linear),
                Enter: new EnterExit(Opacity: 0f, Active: true),
                Exit: new EnterExit(Opacity: 0f, Active: true),
                DelayMs: ScrollBar.ExpandBeginMs),
            Children =
            [
                new TextEl(up ? "" : "")                // VerticalDecrement EDDB / Increment EDDC (:387/:344)
                {
                    Size = ScrollBar.ArrowFontSize,                 // (:186)
                    FontFamily = Theme.IconFont,
                    Color = Disabled ? Tok.FillControlStrongDisabled : Tok.FillControlStrong,   // (:22/:25)
                    HoverColor = Tok.TextSecondary,                 // (:23)
                    PressedColor = Tok.TextSecondary,               // (:24)
                },
            ],
        };
}
