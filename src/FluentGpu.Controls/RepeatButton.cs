using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// A button that raises its click repeatedly while held (WinUI RepeatButton): the host's RepeatTicker fires the click
/// once on press, then after an initial delay, then at a steady interval, until release (or until the pointer drags
/// off — WinUI stops the timer when IsPointerOver drops, RepeatButton_Partial.cpp:530-548). Opting in is just
/// <see cref="BoxEl.Repeats"/> = true on a clickable node; the scheduling lives in the host (see RepeatTicker).
/// WinUI defaults: Delay = 500ms, Interval = 33ms — the DP metadata defaults
/// (dxaml\xcp\components\DependencyObject\DependencyProperty.cpp:714-720) — overridable per instance via
/// <see cref="Style.DelayMs"/>/<see cref="Style.IntervalMs"/> (RepeatButton_Partial.cpp:149-182 validates positive).
/// Keyboard mirrors WinUI exactly: a held Space arms the SAME engine timer (RepeatButton_Partial.cpp:212-217 —
/// m_keyboardCausingRepeat; OS key auto-repeat is ignored), a held Enter yields exactly ONE click on its down edge
/// (the ClickMode.Press initialize, RepeatButton_Partial.cpp:29).
/// Style source: controls\dev\CommonStyles\RepeatButton_themeresources.xaml (Default = dark :4-27, Light :52-75) —
/// state storyboards are color-only (lines 100-143), so no press scale (Wave-1 parity pass removed it).
/// </summary>
public static partial class RepeatButton
{
    // Template parts (see TemplateParts; docs/guide/control-fidelity.md §6). Each part's doc lists the props the
    // control OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The button chrome. Owned: OnClick, Repeats (the auto-repeat opt-in IS this control), Role,
    /// Children (the label slot).</summary>
    public const string PartRoot = "Root";
    /// <summary>The label run — a <see cref="TextEl"/>, so customize via <c>parts.Set&lt;TextEl&gt;(RepeatButton.PartLabel, …)</c>.
    /// Owned: none.</summary>
    public const string PartLabel = "Label";

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
        /// <summary>WinUI RepeatButton <c>Delay</c> (ms before the repeat starts) — NaN = the DP default 500
        /// (DependencyProperty.cpp:714-720); must be positive (RepeatButton_Partial.cpp:149-165).</summary>
        public float DelayMs { get; init; } = float.NaN;
        /// <summary>WinUI RepeatButton <c>Interval</c> (ms between repeats) — NaN = the DP default 33; the ScrollBar
        /// template arrows use 50 (RepeatButton_Partial.cpp:167-182 validates positive).</summary>
        public float IntervalMs { get; init; } = float.NaN;
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

    /// <summary>The per-control clamp seam (adjustment #6). RepeatButton honors every size — identity.</summary>
    internal static ControlSize ClampSize(ControlSize size) => size;

    public static BoxEl Create(string label, Action onClick, Style? style = null, bool isEnabled = true, TemplateParts? parts = null, ControlSize size = ControlSize.Medium)
    {
        var cs = ClampSize(size);
        var m = ControlMetrics.For(cs);
        // Size axis composes over the default style's geometry; Medium is byte-identical to the pre-axis default.
        var s = style ?? (cs == ControlSize.Medium ? DefaultStyle
            : DefaultStyle with { Padding = m.Padding, MinHeight = m.MinHeight, FontSize = m.FontSize, CornerRadius = m.CornerRadius });
        var labelEl = parts.Apply(PartLabel, new TextEl(label)
        {
            Size = s.FontSize,
            Color = s.Foreground,
            HoverColor = s.HoverForeground,
            PressedColor = s.PressedForeground,
            DisabledColor = s.DisabledForeground,
        });
        var root = new BoxEl
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
            // No Cursor: WinUI RepeatButton never calls SetCursor (arrow by inheritance, RepeatButton_Partial.cpp) —
            // an unset cursor also lets an ancestor's explicit cursor show through (e.g. inside an editing surface).
            // Auto-repeat: once now, again after Delay, then every Interval (NaN = the WinUI DP defaults 500/33,
            // DependencyProperty.cpp:714-720). A held Space arms the same engine timer; the held pointer leaving the
            // node pauses it (fresh delay on re-entry, RepeatButton_Partial.cpp:530-574).
            Repeats = true,
            RepeatDelayMs = s.DelayMs,
            RepeatIntervalMs = s.IntervalMs,
            IsEnabled = isEnabled,   // engine gate also halts the RepeatTicker when disabled
            OnClick = onClick,
            Children = [labelEl],
        };
        // Parts: restyle anything; the click + auto-repeat mechanics and the label slot always win.
        return parts.Apply(PartRoot, root) with { OnClick = onClick, Repeats = true, Role = AutomationRole.Button, Children = root.Children };
    }
}
