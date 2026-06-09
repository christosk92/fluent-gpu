using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// A button that raises its click repeatedly while held (WinUI RepeatButton): the host's RepeatTicker fires the click
/// once on press, then after an initial delay, then at a steady interval, until release (or until the pointer drags
/// off — WinUI stops the timer when IsPointerOver drops, RepeatButton_Partial.cpp:530-548). Opting in is just
/// <see cref="BoxEl.Repeats"/> = true on a clickable node; the scheduling lives in the host (see RepeatTicker).
/// WinUI defaults: Delay = 500ms, Interval = 33ms — the DP metadata defaults
/// (dxaml\xcp\components\DependencyObject\DependencyProperty.cpp:714-720) — mirrored by RepeatTicker's constants.
/// Space-held keyboard repeat is engine-routed: the dispatcher re-fires the click on every Space key-down including OS
/// auto-repeat (WinUI instead arms the same 500/33 timer from OnKeyDown — RepeatButton_Partial.cpp:201-219; identical
/// press-and-hold semantics, cadence = the OS key-repeat rate rather than Interval).
/// Style source: controls\dev\CommonStyles\RepeatButton_themeresources.xaml (Default = dark :4-27, Light :52-75) —
/// state storyboards are color-only (lines 100-143), so no press scale (Wave-1 parity pass removed it).
/// </summary>
public static partial class RepeatButton
{
    public sealed record Style
    {
        public ColorF Background { get; init; }
        public ColorF Foreground { get; init; }
        public ColorF HoverBackground { get; init; }
        public ColorF HoverForeground { get; init; }      // RepeatButtonForegroundPointerOver = TextFillColorPrimary (line 11/59)
        public ColorF PressedBackground { get; init; }
        public ColorF PressedForeground { get; init; }    // RepeatButtonForegroundPressed = TextFillColorSecondary (line 12/60)
        public ColorF DisabledBackground { get; init; }   // RepeatButtonBackgroundDisabled = ControlFillColorDisabled (line 9/57)
        public ColorF DisabledForeground { get; init; }   // RepeatButtonForegroundDisabled = TextFillColorDisabled (line 13/61)
        public GradientSpec? BorderBrush { get; init; }
        public GradientSpec? HoverBorderBrush { get; init; }     // RepeatButtonBorderBrushPointerOver = ControlElevationBorder (line 15/63)
        public GradientSpec? PressedBorderBrush { get; init; }   // RepeatButtonBorderBrushPressed = ControlStrokeColorDefault (line 16/64)
        public GradientSpec? DisabledBorderBrush { get; init; }  // RepeatButtonBorderBrushDisabled = ControlStrokeColorDefault (line 17/65)
        public float BorderWidth { get; init; } = 1f;            // RepeatButtonBorderThemeThickness = 1 (line 5/41/53)
        public float CornerRadius { get; init; } = Radii.Control;          // ControlCornerRadius (RepeatButton_themeresources.xaml:92)
        public Edges4 Padding { get; init; } = new(11, 5, 11, 6);          // ButtonPadding (line 84 → Button_themeresources.xaml:152)
        public float FontSize { get; init; } = 14f;                        // ControlContentThemeFontSize (line 89)
        public float MinHeight { get; init; } = 32f;
        /// <summary>WinUI HorizontalContentAlignment equivalent; Control default Center (DependencyProperty.cpp:646-648).</summary>
        public FlexJustify HorizontalContentAlignment { get; init; } = FlexJustify.Center;
        /// <summary>WinUI VerticalContentAlignment equivalent; Control default Center (DependencyProperty.cpp:650-652).</summary>
        public FlexAlign VerticalContentAlignment { get; init; } = FlexAlign.Center;
        /// <summary>WinUI FocusVisualMargin = −3 (RepeatButton_themeresources.xaml:91); the engine draws the ring (E1).</summary>
        public Edges4 FocusVisualMargin { get; init; } = Edges4.All(-3f);
        /// <summary>WinUI BackgroundSizing = InnerBorderEdge (RepeatButton_themeresources.xaml:80). See
        /// <see cref="Controls.BackgroundSizing"/> for the renderer-mapping note.</summary>
        public BackgroundSizing BackgroundSizing { get; init; } = BackgroundSizing.InnerBorderEdge;
        /// <summary>WinUI ContentPresenter.BackgroundTransition = BrushTransition 83ms (RepeatButton_themeresources.xaml:97-99). NaN = snap.</summary>
        public float BrushTransitionMs { get; init; } = 83f;
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Background = Tok.FillControlDefault,            // RepeatButtonBackground = ControlFillColorDefault (line 6/54)
        Foreground = Tok.TextPrimary,                   // RepeatButtonForeground = TextFillColorPrimary (line 10/58)
        HoverBackground = Tok.FillControlSecondary,     // RepeatButtonBackgroundPointerOver = ControlFillColorSecondary (line 7/55)
        HoverForeground = Tok.TextPrimary,              // RepeatButtonForegroundPointerOver = TextFillColorPrimary (line 11/59)
        PressedBackground = Tok.FillControlTertiary,    // RepeatButtonBackgroundPressed = ControlFillColorTertiary (line 8/56)
        PressedForeground = Tok.TextSecondary,          // RepeatButtonForegroundPressed = TextFillColorSecondary (line 12/60)
        DisabledBackground = Tok.FillControlDisabled,   // RepeatButtonBackgroundDisabled = ControlFillColorDisabled (line 9/57)
        DisabledForeground = Tok.TextDisabled,          // RepeatButtonForegroundDisabled = TextFillColorDisabled (line 13/61)
        BorderBrush = Tok.ControlElevationBorder,       // RepeatButtonBorderBrush = ControlElevationBorder (line 14/62)
        HoverBorderBrush = Tok.ControlElevationBorder,  // RepeatButtonBorderBrushPointerOver (line 15/63) — unchanged from rest
        PressedBorderBrush = GradientSpec.Solid(Tok.StrokeControlDefault),  // line 16/64
        DisabledBorderBrush = GradientSpec.Solid(Tok.StrokeControlDefault), // line 17/65
    };

    public static BoxEl Create(string label, Action onClick, Style? style = null, bool isEnabled = true)
    {
        var s = style ?? DefaultStyle;
        return new BoxEl
        {
            Direction = 0,
            Role = AutomationRole.Button,
            Padding = s.Padding,
            MinHeight = s.MinHeight,
            Justify = s.HorizontalContentAlignment,
            AlignItems = s.VerticalContentAlignment,
            Corners = CornerRadius4.All(s.CornerRadius),
            BorderWidth = s.BorderWidth,
            BorderBrush = isEnabled ? s.BorderBrush : (s.DisabledBorderBrush ?? s.BorderBrush),
            HoverBorderBrush = s.HoverBorderBrush,
            PressedBorderBrush = s.PressedBorderBrush,
            Fill = isEnabled ? s.Background : s.DisabledBackground,
            HoverFill = s.HoverBackground,
            PressedFill = s.PressedBackground,
            // WinUI ContentPresenter.BackgroundTransition 83ms (RepeatButton_themeresources.xaml:97-99) — E3 primitive.
            BrushTransitionMs = s.BrushTransitionMs,
            // WinUI UseSystemFocusVisuals + FocusVisualMargin −3 (RepeatButton_themeresources.xaml:90-91); engine-drawn (E1).
            Focusable = true,
            FocusVisualMargin = s.FocusVisualMargin,
            // WinUI RepeatButton keeps the arrow cursor (no SetCursor call in RepeatButton_Partial.cpp).
            Cursor = CursorId.Arrow,
            // Auto-repeat: once now, again after 500ms, then every 33ms (RepeatTicker mirrors the WinUI DP defaults,
            // DependencyProperty.cpp:714-720). Space-held repeat rides the same engine path.
            Repeats = true,
            IsEnabled = isEnabled,   // engine gate also halts the RepeatTicker when disabled
            OnClick = onClick,
            Children = [new TextEl(label)
            {
                Size = s.FontSize,
                Color = s.Foreground,
                HoverColor = s.HoverForeground,
                PressedColor = s.PressedForeground,
                DisabledColor = s.DisabledForeground,
            }],
        };
    }
}
