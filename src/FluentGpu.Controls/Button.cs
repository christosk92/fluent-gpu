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
/// The button's <b>appearance</b> axis — WHICH token ramp fills/strokes the chrome. ORTHOGONAL to
/// <see cref="ControlSize"/> (Radix Themes/CVA precedent: appearance selects colors, size selects geometry; they
/// compose freely, they are not a flattened product). Standard/Accent are the two WinUI 3 button styles; Subtle/Outline
/// are documented extensions built from existing WinUI tokens (see <see cref="ButtonPalette.For"/>).
/// </summary>
public enum ButtonAppearance : byte
{
    /// <summary>WinUI DefaultButtonStyle — neutral control fill.</summary>
    Standard,
    /// <summary>WinUI AccentButtonStyle — the accent (primary) fill.</summary>
    Accent,
    /// <summary>Transparent chrome that reveals a subtle fill on interaction (the WinUI SubtleFillColor* ramp).</summary>
    Subtle,
    /// <summary>A hollow button: a solid stroke in all states + transparent interior (a documented Fluent-2 extension).</summary>
    Outline,
}

/// <summary>The Button control: barebone behavior (clickable, hover/pressed, focusable) + a default Fluent style on the
/// two ORTHOGONAL axes <see cref="ButtonAppearance"/> × <see cref="ControlSize"/>. Override the look per-instance (pass a
/// <see cref="Style"/> — the full-override escape hatch), globally (<see cref="StyleHook"/>), or ad-hoc (chain
/// modifiers). Defaults are sourced from WinUI 3 DefaultButtonStyle / AccentButtonStyle (microsoft-ui-xaml
/// controls\dev\CommonStyles\Button_themeresources.xaml): ButtonPadding 11,5,11,6 (line 152); ControlCornerRadius 4
/// (line 168); ControlContentThemeFontSize 14; ButtonBorderThemeThickness 1 (lines 29/90/127); FocusVisualMargin −3
/// (line 167); BackgroundTransition 83ms (lines 173-175).
/// WinUI Button has NO scale animation — its state storyboards swap Background/BorderBrush/Foreground only
/// (Button_themeresources.xaml:176-229), so no Hover/PressScale here (removed at the Wave-1 parity pass).
/// Keyboard: the engine arms a pressed visual on Space/Enter key-DOWN and clicks on key-UP — held keys never repeat,
/// any other key cancels without firing (WinUI ButtonBaseKeyProcess.h; E2) — and draws the keyboard focus
/// ring itself (E1) — nothing control-side. The pointer cursor stays the WinUI arrow (only HyperlinkButton shows a
/// hand — dxaml\xcp\dxaml\lib\HyperLinkButton_Partial.cpp:32). <c>partial</c> so apps/framework can add variants.
/// </summary>
public static partial class Button
{
    // Template parts (see TemplateParts; docs/guide/control-fidelity.md §6). Each part's doc lists the props the
    // control OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The button chrome (the WinUI ContentPresenter root). Owned: OnClick, Role, Children (the icon+label slots).</summary>
    public const string PartRoot = "Root";
    /// <summary>The optional leading-icon run — a <see cref="TextEl"/> in the icon font, present only when a
    /// <c>glyph</c> was passed. Customize via <c>parts.Set&lt;TextEl&gt;(Button.PartGlyph, …)</c>. Owned: none.</summary>
    public const string PartGlyph = "Glyph";
    /// <summary>The label run — a <see cref="TextEl"/>, so customize via <c>parts.Set&lt;TextEl&gt;(Button.PartLabel, …)</c>.
    /// Owned: none (the P2 foreground ramp is style-driven and a modifier may override it).</summary>
    public const string PartLabel = "Label";

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

    /// <summary>The <b>appearance</b>-axis color bundle — the four interaction-state ramps (fill, foreground, border) +
    /// the WinUI BackgroundSizing, selected by ONE 4-arm switch (<see cref="For"/>) over the live <see cref="Tok"/>. This
    /// is the only place variant colors live; there are no per-variant <see cref="Style"/> copies.</summary>
    internal readonly record struct ButtonPalette(StateBrush Background, StateBrush Foreground, BorderRamp Border, BackgroundSizing Sizing)
    {
        /// <summary>The ONE 4-arm appearance switch. Reads Tok fresh on every call, so it is theme-live like the old
        /// computed styles were.</summary>
        public static ButtonPalette For(ButtonAppearance appearance) => appearance switch
        {
            // Accent — WinUI AccentButtonStyle. AccentButton* resources: Button_themeresources.xaml:5-16 (Default=dark)/
            // :103-114 (Light). AccentButtonBackground = AccentFillColorDefault/Secondary/Tertiary/Disabled (5-8);
            // Foreground = TextOnAccentFillColorPrimary/Primary/Secondary/Disabled (9-12);
            // Border = AccentControlElevationBorder (13) → Pointer-over unchanged (14) → Pressed/Disabled = transparent
            // (ControlFillColorTransparent, 15-16). BackgroundSizing = OuterBorderEdge (setter, :238).
            ButtonAppearance.Accent => new ButtonPalette(
                Background: new StateBrush(Tok.AccentDefault, Tok.AccentSecondary, Tok.AccentTertiary, Tok.AccentDisabled),
                Foreground: new StateBrush(Tok.TextOnAccentPrimary, Tok.TextOnAccentPrimary, Tok.TextOnAccentSecondary, Tok.TextOnAccentDisabled),
                Border: new BorderRamp(Tok.AccentControlElevationBorder, Tok.AccentControlElevationBorder,
                                       GradientSpec.Solid(ColorF.Transparent), GradientSpec.Solid(ColorF.Transparent)),
                Sizing: BackgroundSizing.OuterBorderEdge),

            // Subtle — the WinUI SubtleFillColor* ramp (AppBarButton/command-bar chrome, AppBarButton_themeresources.xaml
            // :5-8: SubtleFillColorTransparent/Secondary/Tertiary; SubtleFillColorDisabled = #00FFFFFF → transparent in
            // both themes, Common_themeresources_any.xaml:28/232). Text = Standard's neutral ramp. Border transparent
            // in all states. BackgroundSizing = InnerBorderEdge (the neutral default).
            ButtonAppearance.Subtle => new ButtonPalette(
                Background: new StateBrush(Tok.FillSubtleTransparent, Tok.FillSubtleSecondary, Tok.FillSubtleTertiary, Tok.FillSubtleTransparent),
                Foreground: new StateBrush(Tok.TextPrimary, Tok.TextPrimary, Tok.TextSecondary, Tok.TextDisabled),
                Border: BorderRamp.Flat(GradientSpec.Solid(ColorF.Transparent)),
                Sizing: BackgroundSizing.InnerBorderEdge),

            // Outline — a DOCUMENTED Fluent-2 extension (no direct WinUI button style): a solid ControlStrokeColorDefault
            // border in ALL states (the "outline" identity) over a transparent interior that picks up the same subtle
            // fill ramp as Subtle on interaction (SubtleFillColorSecondary/Tertiary). Text = Standard's neutral ramp.
            // Uses only existing WinUI tokens + the existing 83ms brush timing — no invented colors or curves.
            ButtonAppearance.Outline => new ButtonPalette(
                Background: new StateBrush(Tok.FillSubtleTransparent, Tok.FillSubtleSecondary, Tok.FillSubtleTertiary, Tok.FillSubtleTransparent),
                Foreground: new StateBrush(Tok.TextPrimary, Tok.TextPrimary, Tok.TextSecondary, Tok.TextDisabled),
                Border: BorderRamp.Flat(GradientSpec.Solid(Tok.StrokeControlDefault)),
                Sizing: BackgroundSizing.InnerBorderEdge),

            // Standard — WinUI DefaultButtonStyle. Button* resources: Button_themeresources.xaml:30-41 (Default=dark)/
            // :128-139 (Light). Background = ControlFillColorDefault/Secondary/Tertiary/Disabled (30-33);
            // Foreground = TextFillColorPrimary/Primary/Secondary/Disabled (34-37);
            // Border = ControlElevationBorder (38) → Pointer-over unchanged (39) → Pressed/Disabled = solid
            // ControlStrokeColorDefault (40-41). BackgroundSizing = InnerBorderEdge (style default, :156).
            _ => new ButtonPalette(
                Background: new StateBrush(Tok.FillControlDefault, Tok.FillControlSecondary, Tok.FillControlTertiary, Tok.FillControlDisabled),
                Foreground: new StateBrush(Tok.TextPrimary, Tok.TextPrimary, Tok.TextSecondary, Tok.TextDisabled),
                Border: new BorderRamp(Tok.ControlElevationBorder, Tok.ControlElevationBorder,
                                       GradientSpec.Solid(Tok.StrokeControlDefault), GradientSpec.Solid(Tok.StrokeControlDefault)),
                Sizing: BackgroundSizing.InnerBorderEdge),
        };
    }

    /// <summary>The border sibling of <see cref="StateBrush"/>: the four interaction-state border gradients (a
    /// WinUI elevation gradient or a solid via <c>GradientSpec.Solid</c>). Null legs = no stroke.</summary>
    internal readonly record struct BorderRamp(GradientSpec? Rest, GradientSpec? Hover, GradientSpec? Pressed, GradientSpec? Disabled)
    {
        /// <summary>The same gradient in all four states (a border that doesn't react to interaction).</summary>
        public static BorderRamp Flat(GradientSpec g) => new(g, g, g, g);
    }

    /// <summary>Global style hook consulted BEFORE <see cref="DefaultStyle(ButtonAppearance, ControlSize)"/> composes:
    /// return a <see cref="Style"/> to override that (appearance, size) pair, or <c>null</c> to fall through to the
    /// composed default. The single, axis-aware replacement for the old per-appearance style-override statics
    /// (the accent/standard override fields deleted in G5d).</summary>
    public static Func<ButtonAppearance, ControlSize, Style?>? StyleHook;

    /// <summary>Composes the full 24-member <see cref="Style"/> for a point on the (<paramref name="appearance"/>,
    /// <paramref name="size"/>) axes: the <see cref="StyleHook"/> wins if it returns non-null, else the
    /// <see cref="ButtonPalette"/> (colors) + <see cref="ControlMetrics"/> (geometry) fold into the record. Everything
    /// else (BorderWidth 1, Center content alignment, FocusVisualMargin −3, 83ms brush) keeps the record defaults.</summary>
    public static Style DefaultStyle(ButtonAppearance appearance, ControlSize size = ControlSize.Medium)
    {
        if (StyleHook is { } hook && hook(appearance, size) is { } custom) return custom;
        var p = ButtonPalette.For(appearance);
        var m = ControlMetrics.For(size);
        return new Style
        {
            Background = p.Background.Rest,
            HoverBackground = p.Background.Hover,
            PressedBackground = p.Background.Pressed,
            DisabledBackground = p.Background.Disabled,
            Foreground = p.Foreground.Rest,
            HoverForeground = p.Foreground.Hover,
            PressedForeground = p.Foreground.Pressed,
            DisabledForeground = p.Foreground.Disabled,
            BorderBrush = p.Border.Rest,
            HoverBorderBrush = p.Border.Hover,
            PressedBorderBrush = p.Border.Pressed,
            DisabledBorderBrush = p.Border.Disabled,
            BackgroundSizing = p.Sizing,
            Padding = m.Padding,
            MinHeight = m.MinHeight,
            FontSize = m.FontSize,
            CornerRadius = m.CornerRadius,
        };
    }

    /// <summary>The accent (primary) button's default Medium style — kept as a property so <c>with</c>-tweak call sites
    /// (<c>Button.AccentStyle with { … }</c>) stay compiling. Equivalent to <c>DefaultStyle(ButtonAppearance.Accent)</c>.</summary>
    public static Style AccentStyle => DefaultStyle(ButtonAppearance.Accent);
    /// <summary>The neutral (standard) button's default Medium style. Equivalent to <c>DefaultStyle(ButtonAppearance.Standard)</c>.</summary>
    public static Style StandardStyle => DefaultStyle(ButtonAppearance.Standard);

    /// <summary>The per-control clamp seam (adjustment #6 — a shared axis value a control can contextually refuse,
    /// Radix precedent). Button honors every size, so this is the identity; siblings like <see cref="IconButton"/>
    /// override it (IconButton clamps Large to keep its square glyph box sane).</summary>
    internal static ControlSize ClampSize(ControlSize size) => size;

    /// <summary>The canonical factory: a button on the orthogonal <paramref name="appearance"/> × <paramref name="size"/>
    /// axes, with an optional leading <paramref name="glyph"/> (icon-font codepoint). Pass a <see cref="Style"/> to
    /// fully override both axes (the escape hatch); restyle internals via <paramref name="parts"/>.</summary>
    public static BoxEl Create(string label, Action onClick, ButtonAppearance appearance = ButtonAppearance.Standard,
        ControlSize size = ControlSize.Medium, string? glyph = null, Style? style = null, bool isEnabled = true, TemplateParts? parts = null)
    {
        var cs = ClampSize(size);
        return Build(label, onClick, glyph, ControlMetrics.For(cs).IconSize, style ?? DefaultStyle(appearance, cs), isEnabled, parts);
    }

    /// <summary>Sugar: an accent (primary) button. One-line forwarder to <see cref="Create"/> — signature preserved so
    /// existing call sites (and <c>Button.Accent(label, onClick, style)</c>) keep compiling.</summary>
    public static BoxEl Accent(string label, Action onClick, Style? style = null, bool isEnabled = true, TemplateParts? parts = null)
        => Create(label, onClick, ButtonAppearance.Accent, style: style, isEnabled: isEnabled, parts: parts);

    /// <summary>Sugar: a neutral (standard) button.</summary>
    public static BoxEl Standard(string label, Action onClick, Style? style = null, bool isEnabled = true, TemplateParts? parts = null)
        => Create(label, onClick, ButtonAppearance.Standard, style: style, isEnabled: isEnabled, parts: parts);

    /// <summary>Sugar: a subtle button (transparent chrome, subtle fill on interaction).</summary>
    public static BoxEl Subtle(string label, Action onClick, Style? style = null, bool isEnabled = true, TemplateParts? parts = null)
        => Create(label, onClick, ButtonAppearance.Subtle, style: style, isEnabled: isEnabled, parts: parts);

    /// <summary>Sugar: an outline button (solid stroke, transparent interior).</summary>
    public static BoxEl Outline(string label, Action onClick, Style? style = null, bool isEnabled = true, TemplateParts? parts = null)
        => Create(label, onClick, ButtonAppearance.Outline, style: style, isEnabled: isEnabled, parts: parts);

    private static BoxEl Build(string label, Action onClick, string? glyph, float iconSize, Style s, bool enabled, TemplateParts? parts)
    {
        var labelEl = parts.Apply(PartLabel, new TextEl(label)
        {
            Size = s.FontSize, Bold = s.Bold,
            Color = s.Foreground,                    // P2 foreground ramp: rest → hover → pressed; disabled via the gate
            HoverColor = s.HoverForeground,
            PressedColor = s.PressedForeground,
            DisabledColor = s.DisabledForeground,
        });
        // Optional leading icon: an icon-font glyph riding the SAME foreground ramp as the label (the most-requested
        // composition). Present only when a glyph was passed — without it the child list is label-only (structure
        // identical to the pre-axis button, so Standard/Accent stay pixel-identical).
        Element[] children;
        if (glyph is not null)
        {
            var glyphEl = parts.Apply(PartGlyph, new TextEl(glyph)
            {
                Size = iconSize, FontFamily = Theme.IconFont,
                Color = s.Foreground,
                HoverColor = s.HoverForeground,
                PressedColor = s.PressedForeground,
                DisabledColor = s.DisabledForeground,
            });
            children = [glyphEl, labelEl];
        }
        else
        {
            children = [labelEl];
        }
        var root = new BoxEl
        {
            Direction = 0,
            Role = AutomationRole.Button,
            Padding = s.Padding,
            MinHeight = s.MinHeight,
            // Icon↔label gap only when the icon slot is present (8px = the standard content gutter); no glyph ⇒ 0 ⇒
            // layout byte-identical to the pre-axis button.
            Gap = glyph is not null ? 8f : 0f,
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
            // No Cursor: WinUI buttons never call SetCursor (arrow by inheritance — only HyperlinkButton sets the
            // hand, HyperLinkButton_Partial.cpp:28-34); unset also lets an ancestor's explicit cursor show through.
            IsEnabled = enabled,                              // P1 engine gate (no manual handler-nulling)
            OnClick = onClick,
            Children = children,
        };
        // Parts: restyle anything (fills, corners, padding…); the click mechanics and the icon+label slots always win.
        return parts.Apply(PartRoot, root) with { OnClick = onClick, Role = AutomationRole.Button, Children = root.Children };
    }
}
