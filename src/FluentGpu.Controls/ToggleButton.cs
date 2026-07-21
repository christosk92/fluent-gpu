using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A two/three-state toggle (shuffle/repeat/follow). Accent-filled when on — WinUI ToggleButton's checked state
/// (AccentFillColorDefault bg + TextOnAccent fg + AccentControlElevationBorder); the unchecked AND indeterminate
/// states are a standard control fill. Controlled: pass the state and a toggle/cycle callback.
/// Style source: controls\dev\CommonStyles\ToggleButton_themeresources.xaml (Default = dark :4-61, Light :120-177;
/// DefaultToggleButtonStyle :180-363). The full WinUI matrix is wired: {Unchecked, Checked, Indeterminate} ×
/// {Normal, PointerOver, Pressed, Disabled} for fill, foreground and border, verified in both themes.
/// State flips (checked↔unchecked) cross-fade over the WinUI BrushTransition 83ms
/// (ContentPresenter.BackgroundTransition, ToggleButton_themeresources.xaml:199-201) — the E3 primitive.
/// No scale animation: WinUI state storyboards are color-only (lines 202-357).
/// </summary>
public static partial class ToggleButton
{
    // Template parts (see TemplateParts; docs/guide/control-fidelity.md §6). Each part's doc lists the props the
    // control OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The toggle chrome (the WinUI ContentPresenter root). Owned: OnClick (the state flip/cycle), Role,
    /// Children (the label slot).</summary>
    public const string PartRoot = "Root";
    /// <summary>The label run — a <see cref="TextEl"/>, so customize via <c>parts.Set&lt;TextEl&gt;(ToggleButton.PartLabel, …)</c>.
    /// Owned: none (the per-state foreground ramp is recomputed each render before the modifier, which may override it).</summary>
    public const string PartLabel = "Label";

    public sealed record Style
    {
        public float CornerRadius { get; init; } = Radii.Control;    // ControlCornerRadius = 4 (ToggleButton_themeresources.xaml:194)
        public Edges4 Padding { get; init; } = new(11, 5, 11, 6);    // ButtonPadding (line 186 → Button_themeresources.xaml:152) — NOT the legacy generic.xaml 8,4,8,5
        public float MinHeight { get; init; } = 32f;
        public float FontSize { get; init; } = 14f;                  // ControlContentThemeFontSize (line 191)
        public float BorderWidth { get; init; } = 1f;                // ToggleButtonBorderThemeThickness = 1 (line 5/63/121)
        /// <summary>WinUI FocusVisualMargin = −3 (ToggleButton_themeresources.xaml:193); the engine draws the ring (E1).</summary>
        public Edges4 FocusVisualMargin { get; init; } = Edges4.All(-3f);
        /// <summary>WinUI ContentPresenter.BackgroundTransition = BrushTransition 83ms (lines 199-201): a logical state
        /// flip (checked↔unchecked↔indeterminate) cross-fades the resting fill via the E3 sparse BrushAnim row instead
        /// of snapping. WinUI scopes the transition to Background only; the engine also ramps the border diff —
        /// a documented sub-100ms over-coverage. NaN = snap.</summary>
        public float BrushTransitionMs { get; init; } = 83f;
        /// <summary>WinUI BackgroundSizing: style default InnerBorderEdge (line 182). See
        /// <see cref="Controls.BackgroundSizing"/> for the renderer-mapping note.</summary>
        public BackgroundSizing BackgroundSizing { get; init; } = BackgroundSizing.InnerBorderEdge;
        /// <summary>WinUI flips to OuterBorderEdge while Checked/CheckedPointerOver/CheckedPressed
        /// (ToggleButtonCheckedStateBackgroundSizing, lines 6 and 255-257/271-273/287-289).</summary>
        public BackgroundSizing CheckedBackgroundSizing { get; init; } = BackgroundSizing.OuterBorderEdge;
        // On (Checked) state — ToggleButton*Checked* (Default :11-14,23-26,35-38 / Light :127-130,139-142,151-154)
        public ColorF OnBackground { get; init; }
        public ColorF OnHover { get; init; }
        public ColorF OnPressed { get; init; }
        public ColorF OnDisabledBackground { get; init; }  // ToggleButtonBackgroundCheckedDisabled = AccentFillColorDisabled (line 14/130)
        public ColorF OnForeground { get; init; }
        public ColorF OnPressedForeground { get; init; }   // ToggleButtonForegroundCheckedPressed = TextOnAccentFillColorSecondary (line 25/141)
        public ColorF OnDisabledForeground { get; init; }  // ToggleButtonForegroundCheckedDisabled = TextOnAccentFillColorDisabled (line 26/142)
        public GradientSpec? OnBorder { get; init; }
        public GradientSpec? OnHoverBorder { get; init; }    // ToggleButtonBorderBrushCheckedPointerOver = AccentControlElevationBorder (line 36/152) — unchanged from rest
        public GradientSpec? OnPressedBorder { get; init; }  // ToggleButtonBorderBrushCheckedPressed = ControlFillColorTransparent (line 37/153)
        public GradientSpec? OnDisabledBorder { get; init; } // ToggleButtonBorderBrushCheckedDisabled = ControlFillColorTransparent (line 38/154)
        // Off (Unchecked) state — and the Indeterminate state, which uses the SAME neutral tokens
        // (ToggleButton*Indeterminate* :15-18,27-30,39-42 == ToggleButton* :7-10,19-22,31-34).
        public ColorF OffBackground { get; init; }
        public ColorF OffHover { get; init; }
        public ColorF OffPressed { get; init; }
        public ColorF OffDisabledBackground { get; init; }  // ToggleButtonBackgroundDisabled = ControlFillColorDisabled (line 10/126)
        public ColorF OffForeground { get; init; }
        public ColorF OffPressedForeground { get; init; }   // ToggleButtonForegroundPressed = TextFillColorSecondary (line 21/137)
        public ColorF OffDisabledForeground { get; init; }  // ToggleButtonForegroundDisabled = TextFillColorDisabled (line 22/138)
        public GradientSpec? OffBorder { get; init; }
        public GradientSpec? OffHoverBorder { get; init; }    // ToggleButtonBorderBrushPointerOver = ControlElevationBorder (line 32/148) — unchanged from rest
        public GradientSpec? OffPressedBorder { get; init; }  // ToggleButtonBorderBrushPressed = ControlStrokeColorDefault (line 33/149)
        public GradientSpec? OffDisabledBorder { get; init; } // ToggleButtonBorderBrushDisabled = ControlStrokeColorDefault (line 34/150)
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        OnBackground = Tok.AccentDefault,                  // ToggleButtonBackgroundChecked = AccentFillColorDefault (line 11/127)
        OnHover = Tok.AccentSecondary,                     // ToggleButtonBackgroundCheckedPointerOver = AccentFillColorSecondary (line 12/128)
        OnPressed = Tok.AccentTertiary,                    // ToggleButtonBackgroundCheckedPressed = AccentFillColorTertiary (line 13/129)
        OnDisabledBackground = Tok.AccentDisabled,
        OnForeground = Tok.TextOnAccentPrimary,            // ToggleButtonForegroundChecked = TextOnAccentFillColorPrimary (line 23/139)
        OnBorder = Tok.AccentControlElevationBorder,       // ToggleButtonBorderBrushChecked = AccentControlElevationBorder (line 35/151)
        OnHoverBorder = Tok.AccentControlElevationBorder,
        OnPressedForeground = Tok.TextOnAccentSecondary, OnDisabledForeground = Tok.TextOnAccentDisabled,
        // ControlFillColorTransparent — an EXPLICIT transparent border (the stroke disappears on checked-pressed),
        // not "keep the resting gradient": pass Solid(Transparent) like the accent Button does.
        OnPressedBorder = GradientSpec.Solid(ColorF.Transparent),
        OnDisabledBorder = GradientSpec.Solid(ColorF.Transparent),
        OffBackground = Tok.FillControlDefault,            // ToggleButtonBackground = ControlFillColorDefault (line 7/123)
        OffHover = Tok.FillControlSecondary,               // ToggleButtonBackgroundPointerOver = ControlFillColorSecondary (line 8/124)
        OffPressed = Tok.FillControlTertiary,              // ToggleButtonBackgroundPressed = ControlFillColorTertiary (line 9/125)
        OffDisabledBackground = Tok.FillControlDisabled,
        OffForeground = Tok.TextPrimary,                   // ToggleButtonForeground = TextFillColorPrimary (line 19/135)
        OffBorder = Tok.ControlElevationBorder,            // ToggleButtonBorderBrush = ControlElevationBorder (line 31/147)
        OffHoverBorder = Tok.ControlElevationBorder,
        OffPressedForeground = Tok.TextSecondary, OffDisabledForeground = Tok.TextDisabled,
        OffPressedBorder = GradientSpec.Solid(Tok.StrokeControlDefault),
        OffDisabledBorder = GradientSpec.Solid(Tok.StrokeControlDefault),
    };

    /// <summary>Two-state toggle. The on/off state is a caller <see cref="Signal{T}"/> (read directly in the core —
    /// live); a click WRITES it then fires <paramref name="onChange"/> once; a programmatic write re-skins with no
    /// onChange echo. Pass no signal (<paramref name="on"/> = null) to auto-materialize an internal one (one code path).</summary>
    public static Element Create(string label, Signal<bool>? on = null, Action<bool>? onChange = null, Style? style = null, bool isEnabled = true, TemplateParts? parts = null)
        => Embed.Comp(new Props(label, on, null, onChange, null, style ?? DefaultStyle, isEnabled, parts),
                      () => new ToggleButtonCore());

    /// <summary>Three-state toggle (adds the mixed "indeterminate" look). The <see cref="CheckState"/> is a caller
    /// <see cref="Signal{T}"/>; a click cycles Unchecked → Checked → Indeterminate, WRITING it then firing onChange.</summary>
    public static Element Create(string label, Signal<CheckState> state, Action<CheckState>? onChange = null, Style? style = null, bool isEnabled = true, TemplateParts? parts = null)
        => Embed.Comp(new Props(label, null, state, null, onChange, style ?? DefaultStyle, isEnabled, parts),
                      () => new ToggleButtonCore());

    /// <summary>Controlled props RE-PUSHED to <see cref="ToggleButtonCore"/>. Exactly one of <see cref="Bool"/> /
    /// <see cref="Tri"/> is set by the matching overload (both null ⇒ 2-state auto-materialize).</summary>
    internal sealed record Props(string Label, Signal<bool>? Bool, Signal<CheckState>? Tri, Action<bool>? OnBool,
                                 Action<CheckState>? OnTri, Style Style, bool IsEnabled, TemplateParts? Parts);

    internal static BoxEl Build(string label, CheckState state, Action<CheckState> onClick, Style? style, bool isEnabled, TemplateParts? parts)
    {
        var s = style ?? DefaultStyle;
        bool on = state == CheckState.Checked;
        // Resting per-state fill / foreground / border (the engine eases hover/press; disabled visuals stay control-chosen).
        // WinUI ToggleButton Indeterminate == Unchecked (neutral control fill, ToggleButton_themeresources.xaml:15-18/27-30/39-42)
        // — not accent.
        ColorF restFill = on ? s.OnBackground : s.OffBackground;
        ColorF disFill = on ? s.OnDisabledBackground : s.OffDisabledBackground;  // indet uses the standard control disabled fill (line 18/134)
        ColorF restFg = on ? s.OnForeground : s.OffForeground;
        ColorF pressFg = on ? s.OnPressedForeground : s.OffPressedForeground;
        ColorF disFg = on ? s.OnDisabledForeground : s.OffDisabledForeground;  // indet → TextFillColorDisabled (line 30/146)
        GradientSpec? restBorder = on ? s.OnBorder : s.OffBorder;
        GradientSpec? hoverBorder = on ? s.OnHoverBorder : s.OffHoverBorder;
        GradientSpec? disBorder = on ? s.OnDisabledBorder : s.OffDisabledBorder;
        GradientSpec? pressBorder = on ? s.OnPressedBorder : s.OffPressedBorder;
        Action click = () => onClick(state);
        var labelEl = parts.Apply(PartLabel, new TextEl(label)
        {
            Size = s.FontSize,
            Color = restFg,
            // WinUI keeps the foreground UNCHANGED on hover in every state (PointerOver foreground == rest:
            // lines 20/24/28) — pinned explicitly so the hover ramp can never drift the label.
            HoverColor = restFg,
            PressedColor = pressFg,
            DisabledColor = disFg,
            // Foreground state flips are discrete in WinUI (KeyTime=0 storyboards; the 83ms BrushTransition covers
            // Background only) — so no BrushTransitionMs on the label.
        });
        var root = new BoxEl
        {
            Direction = 0, Role = AutomationRole.ToggleButton, Padding = s.Padding, MinHeight = s.MinHeight,
            Justify = FlexJustify.Center,     // WinUI HorizontalContentAlignment default Center (DependencyProperty.cpp:646-648)
            AlignItems = FlexAlign.Center,    // WinUI VerticalContentAlignment default Center (DependencyProperty.cpp:650-652)
            Corners = CornerRadius4.All(s.CornerRadius), BorderWidth = s.BorderWidth,
            Fill = isEnabled ? restFill : disFill,
            HoverFill = on ? s.OnHover : s.OffHover,
            PressedFill = on ? s.OnPressed : s.OffPressed,
            BorderBrush = isEnabled ? restBorder : (disBorder ?? restBorder),
            HoverBorderBrush = hoverBorder,        // == rest in WinUI (lines 32/36) — pinned so hover never drifts
            PressedBorderBrush = pressBorder,
            // The checked↔unchecked flip is a LOGICAL state change on a live node: the E3 BrushTransition ramp
            // cross-fades the resting fill over WinUI's 83ms (ToggleButton_themeresources.xaml:199-201).
            BrushTransitionMs = s.BrushTransitionMs,
            // WinUI UseSystemFocusVisuals + FocusVisualMargin −3 (ToggleButton_themeresources.xaml:192-193); engine-drawn (E1).
            Focusable = true,
            FocusVisualMargin = s.FocusVisualMargin,
            // No Cursor: WinUI ToggleButton never calls SetCursor (arrow by inheritance, ToggleButton_Partial.cpp;
            // only HyperlinkButton sets the hand — HyperLinkButton_Partial.cpp:32).
            IsEnabled = isEnabled,
            OnClick = click,
            Children = [labelEl],
        };
        // Parts: restyle anything (fills, corners, padding…); the cycle mechanics and the label slot always win.
        return parts.Apply(PartRoot, root) with { OnClick = click, Role = AutomationRole.ToggleButton, Children = root.Children };
    }
}

/// <summary>The stateful core: reads the caller's value signal DIRECTLY (2-state <c>bool</c> or 3-state
/// <see cref="CheckState"/>) so a flip/cycle re-skins granularly, reusing <see cref="ToggleButton.Build"/> for the
/// exact WinUI visuals. A click writes the signal first, then fires the matching onChange.</summary>
internal sealed class ToggleButtonCore : Component
{
    public override Element Render()
    {
        var p = UseProps<ToggleButton.Props>();
        var own = UseSignal(false);   // internal 2-state signal (auto-materialize; unconditional hook)

        CheckState state;
        Action<CheckState> onClick;
        if (p.Tri is { } tri)
        {
            state = tri.Value;
            var next = state switch
            {
                CheckState.Unchecked => CheckState.Checked,
                CheckState.Checked => CheckState.Indeterminate,
                _ => CheckState.Unchecked,
            };
            onClick = _ => { tri.Value = next; p.OnTri?.Invoke(next); };
        }
        else
        {
            var b = p.Bool ?? own;
            bool on = b.Value;
            state = on ? CheckState.Checked : CheckState.Unchecked;
            onClick = _ => { bool next = !on; b.Value = next; p.OnBool?.Invoke(next); };
        }
        return ToggleButton.Build(p.Label, state, onClick, p.Style, p.IsEnabled, p.Parts);
    }
}
