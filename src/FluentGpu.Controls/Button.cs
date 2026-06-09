using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>BackgroundSizing</c>: whether the control fill stops at the inner border edge or extends under the border
/// to its outer edge. WinUI DefaultButtonStyle = InnerBorderEdge, AccentButtonStyle = OuterBorderEdge
/// (microsoft-ui-xaml controls\dev\CommonStyles\Button_themeresources.xaml:156,238); ToggleButton flips
/// InnerBorderEdge → OuterBorderEdge while checked (ToggleButton_themeresources.xaml:182,6).
/// Carried on the button-family styles as the API/parity contract. HONEST NOTE: the renderer today draws every fill to
/// the OUTER rounded-rect edge with the border stroked on top — i.e. OuterBorderEdge-equivalent for all values; the
/// 1px fill inset of InnerBorderEdge is a named E9 render-primitive gap, not silently dropped. With the WinUI hairline
/// (1px) translucent elevation borders the visual delta is sub-pixel-class.
/// </summary>
public enum BackgroundSizing : byte
{
    InnerBorderEdge = 0,
    OuterBorderEdge = 1,
}

/// <summary>
/// The Button control: barebone behavior (clickable, hover/pressed, focusable) + a default Fluent style. Overrideable
/// globally (<see cref="AccentStyleOverride"/>/<see cref="StandardStyleOverride"/>), per-instance (pass a
/// <see cref="Style"/>), or ad-hoc (chain modifiers). Defaults are sourced from WinUI 3 DefaultButtonStyle /
/// AccentButtonStyle (microsoft-ui-xaml controls\dev\CommonStyles\Button_themeresources.xaml): ButtonPadding 11,5,11,6
/// (line 152); ControlCornerRadius 4 (line 168); ControlContentThemeFontSize 14; ButtonBorderThemeThickness 1
/// (lines 29/90/127); FocusVisualMargin −3 (line 167); BackgroundTransition 83ms (lines 173-175).
/// WinUI Button has NO scale animation — its state storyboards swap Background/BorderBrush/Foreground only
/// (Button_themeresources.xaml:176-229), so no Hover/PressScale here (removed at the Wave-1 parity pass).
/// Keyboard: the engine activates a focused clickable on Enter-down / Space-up (E2), and draws the keyboard focus
/// ring itself (E1) — nothing control-side. The pointer cursor stays the WinUI arrow (only HyperlinkButton shows a
/// hand — dxaml\xcp\dxaml\lib\HyperLinkButton_Partial.cpp:32). <c>partial</c> so apps/framework can add variants.
/// </summary>
public static partial class Button
{
    /// <summary>A button's visual style. Colours resolve from <see cref="Tok"/> in the computed default styles below.
    /// State colours follow WinUI's 4-state matrix (normal/hover/pressed/disabled) for fill, foreground and border.</summary>
    public sealed record Style
    {
        public ColorF Background { get; init; }
        public ColorF Foreground { get; init; }
        /// <summary>The one border knob: a WinUI elevation gradient (the default) or a solid via <c>GradientSpec.Solid(c)</c>.</summary>
        public GradientSpec? BorderBrush { get; init; }
        public ColorF HoverBackground { get; init; }
        public ColorF PressedBackground { get; init; }
        // WinUI ButtonBackgroundDisabled / ButtonForeground{PointerOver,Pressed,Disabled} / ButtonBorderBrush{Pressed,Disabled}.
        // Fully wired: Hover/Pressed/Disabled foreground → the TextEl interaction ramps (P2), per-state border →
        // Hover/PressedBorderBrush (P4b), Disabled fill/border + IsEnabled gate (P1) drive the disabled logical state.
        public ColorF DisabledBackground { get; init; }
        public ColorF HoverForeground { get; init; }
        public ColorF PressedForeground { get; init; }
        public ColorF DisabledForeground { get; init; }
        public GradientSpec? HoverBorderBrush { get; init; }
        public GradientSpec? PressedBorderBrush { get; init; }
        public GradientSpec? DisabledBorderBrush { get; init; }
        public float BorderWidth { get; init; } = 1f;                // ButtonBorderThemeThickness = 1 (Button_themeresources.xaml:29/90/127)
        public float CornerRadius { get; init; } = Radii.Control;    // ControlCornerRadius = 4 (Button_themeresources.xaml:168)
        public Edges4 Padding { get; init; } = new(11, 5, 11, 6);    // ButtonPadding (Button_themeresources.xaml:152)
        public float FontSize { get; init; } = 14f;                  // ControlContentThemeFontSize (Button_themeresources.xaml:165)
        public float MinHeight { get; init; } = 32f;                 // effective WinUI button height (padding + 14px line)
        public bool Bold { get; init; }
        /// <summary>WinUI HorizontalContentAlignment equivalent (main-axis Justify of the row box). WinUI Control
        /// default = Center (dxaml\xcp\components\DependencyObject\DependencyProperty.cpp:646-648) — content centres
        /// when the button is wider than its label.</summary>
        public FlexJustify HorizontalContentAlignment { get; init; } = FlexJustify.Center;
        /// <summary>WinUI VerticalContentAlignment equivalent (cross-axis AlignItems). WinUI Control default = Center
        /// (DependencyProperty.cpp:650-652).</summary>
        public FlexAlign VerticalContentAlignment { get; init; } = FlexAlign.Center;
        /// <summary>WinUI FocusVisualMargin = −3: the keyboard focus ring sits 3px OUTSIDE the bounds
        /// (Button_themeresources.xaml:167). The engine draws the ring (E1).</summary>
        public Edges4 FocusVisualMargin { get; init; } = Edges4.All(-3f);
        /// <summary>WinUI BackgroundSizing: DefaultButtonStyle = InnerBorderEdge (Button_themeresources.xaml:156);
        /// the accent style overrides to OuterBorderEdge. See <see cref="Controls.BackgroundSizing"/> for the
        /// renderer-mapping note.</summary>
        public BackgroundSizing BackgroundSizing { get; init; } = BackgroundSizing.InnerBorderEdge;
        /// <summary>WinUI ContentPresenter.BackgroundTransition = BrushTransition 83ms (Button_themeresources.xaml:173-175):
        /// a re-render that flips the resting fill on a live node cross-fades instead of snapping (the E3 primitive).
        /// WinUI scopes the transition to Background only; the engine routes border diffs through the same ramp —
        /// a documented sub-100ms over-coverage. NaN = snap.</summary>
        public float BrushTransitionMs { get; init; } = 83f;
    }

    /// <summary>Set to globally replace the accent-button default style.</summary>
    public static Style? AccentStyleOverride;
    /// <summary>Set to globally replace the standard-button default style.</summary>
    public static Style? StandardStyleOverride;

    // AccentButton* resources: Button_themeresources.xaml:5-16 (Default = dark) / :103-114 (Light) — same semantic
    // tokens in both themes; the theme split happens inside Tok (Common_themeresources_any.xaml values).
    public static Style AccentStyle => AccentStyleOverride ?? new Style
    {
        Background = Tok.AccentDefault,                     // AccentButtonBackground = AccentFillColorDefault (line 5)
        Foreground = Tok.TextOnAccentPrimary,               // AccentButtonForeground = TextOnAccentFillColorPrimary (line 9)
        BorderBrush = Tok.AccentControlElevationBorder,     // AccentButtonBorderBrush = AccentControlElevationBorder (line 13)
        HoverBackground = Tok.AccentSecondary,              // AccentButtonBackgroundPointerOver = AccentFillColorSecondary (line 6)
        PressedBackground = Tok.AccentTertiary,             // AccentButtonBackgroundPressed = AccentFillColorTertiary (line 7)
        DisabledBackground = Tok.AccentDisabled,            // AccentButtonBackgroundDisabled = AccentFillColorDisabled (line 8)
        HoverForeground = Tok.TextOnAccentPrimary,          // AccentButtonForegroundPointerOver (line 10)
        PressedForeground = Tok.TextOnAccentSecondary,      // AccentButtonForegroundPressed = TextOnAccentFillColorSecondary (line 11)
        DisabledForeground = Tok.TextOnAccentDisabled,      // AccentButtonForegroundDisabled = TextOnAccentFillColorDisabled (line 12)
        HoverBorderBrush = Tok.AccentControlElevationBorder,          // AccentButtonBorderBrushPointerOver (line 14)
        PressedBorderBrush = GradientSpec.Solid(ColorF.Transparent),  // AccentButtonBorderBrushPressed = ControlFillColorTransparent (line 15)
        DisabledBorderBrush = GradientSpec.Solid(ColorF.Transparent), // AccentButtonBorderBrushDisabled = ControlFillColorTransparent (line 16)
        BackgroundSizing = BackgroundSizing.OuterBorderEdge,          // AccentButtonStyle setter (Button_themeresources.xaml:238)
    };

    // Button* resources: Button_themeresources.xaml:30-41 (Default = dark) / :128-139 (Light).
    public static Style StandardStyle => StandardStyleOverride ?? new Style
    {
        Background = Tok.FillControlDefault,                // ButtonBackground = ControlFillColorDefault (line 30)
        Foreground = Tok.TextPrimary,                       // ButtonForeground = TextFillColorPrimary (line 34)
        BorderBrush = Tok.ControlElevationBorder,           // ButtonBorderBrush = ControlElevationBorder (line 38)
        HoverBackground = Tok.FillControlSecondary,         // ButtonBackgroundPointerOver = ControlFillColorSecondary (line 31)
        PressedBackground = Tok.FillControlTertiary,        // ButtonBackgroundPressed = ControlFillColorTertiary (line 32)
        DisabledBackground = Tok.FillControlDisabled,       // ButtonBackgroundDisabled = ControlFillColorDisabled (line 33)
        HoverForeground = Tok.TextPrimary,                  // ButtonForegroundPointerOver = TextFillColorPrimary (line 35)
        PressedForeground = Tok.TextSecondary,              // ButtonForegroundPressed = TextFillColorSecondary (line 36)
        DisabledForeground = Tok.TextDisabled,              // ButtonForegroundDisabled = TextFillColorDisabled (line 37)
        HoverBorderBrush = Tok.ControlElevationBorder,      // ButtonBorderBrushPointerOver = ControlElevationBorder (line 39)
        PressedBorderBrush = GradientSpec.Solid(Tok.StrokeControlDefault),  // ButtonBorderBrushPressed = ControlStrokeColorDefault (line 40)
        DisabledBorderBrush = GradientSpec.Solid(Tok.StrokeControlDefault), // ButtonBorderBrushDisabled = ControlStrokeColorDefault (line 41)
        // BackgroundSizing stays the InnerBorderEdge style default (Button_themeresources.xaml:156).
    };

    /// <summary>An accent (primary) button. Override the look by passing a <see cref="Style"/>.</summary>
    public static BoxEl Accent(string label, Action onClick, Style? style = null, bool isEnabled = true) => Build(label, onClick, style ?? AccentStyle, isEnabled);

    /// <summary>A neutral (standard) button.</summary>
    public static BoxEl Standard(string label, Action onClick, Style? style = null, bool isEnabled = true) => Build(label, onClick, style ?? StandardStyle, isEnabled);

    private static BoxEl Build(string label, Action onClick, Style s, bool enabled) => new()
    {
        Direction = 0,
        Role = AutomationRole.Button,
        Padding = s.Padding,
        MinHeight = s.MinHeight,
        Justify = s.HorizontalContentAlignment,   // WinUI HorizontalContentAlignment default Center (DependencyProperty.cpp:646-648)
        AlignItems = s.VerticalContentAlignment,  // WinUI VerticalContentAlignment default Center (DependencyProperty.cpp:650-652)
        // Disabled is a logical state (no engine ramp): resting fill/border swap to the WinUI disabled tokens. Hover/Pressed
        // never fire while disabled (the engine gate stops hit-test), so HoverFill/PressedFill stay wired but inert.
        Fill = enabled ? s.Background : s.DisabledBackground,
        HoverFill = s.HoverBackground,
        PressedFill = s.PressedBackground,
        BorderBrush = enabled ? s.BorderBrush : (s.DisabledBorderBrush ?? s.BorderBrush),
        HoverBorderBrush = s.HoverBorderBrush,           // P4b state-gradient border
        PressedBorderBrush = s.PressedBorderBrush,
        BorderWidth = s.BorderWidth,
        Corners = CornerRadius4.All(s.CornerRadius),
        // WinUI ContentPresenter.BackgroundTransition (83ms BrushTransition, Button_themeresources.xaml:173-175):
        // logical fill flips (enabled↔disabled, restyle) cross-fade via the E3 sparse BrushAnim row.
        BrushTransitionMs = s.BrushTransitionMs,
        // Keyboard focus: WinUI UseSystemFocusVisuals + FocusVisualMargin −3 (Button_themeresources.xaml:166-167).
        // The engine draws the 2px-outer/1px-inner ring outside the bounds on keyboard focus only (E1).
        Focusable = true,
        FocusVisualMargin = s.FocusVisualMargin,
        // WinUI buttons keep the ARROW cursor — only HyperlinkButton calls SetCursor(MouseCursorHand)
        // (HyperLinkButton_Partial.cpp:28-34). Explicit Arrow overrides the engine's OnClick hand default.
        Cursor = CursorId.Arrow,
        IsEnabled = enabled,                              // P1 engine gate (no manual handler-nulling)
        OnClick = onClick,
        Children =
        [
            new TextEl(label)
            {
                Size = s.FontSize, Bold = s.Bold,
                Color = s.Foreground,                    // P2 foreground ramp: rest → hover → pressed; disabled via the gate
                HoverColor = s.HoverForeground,
                PressedColor = s.PressedForeground,
                DisabledColor = s.DisabledForeground,
            },
        ],
    };
}
