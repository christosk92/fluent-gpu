using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

// E2 (docs/plans/wavee/home-redesign-implementation.md Workstream E, C:\wavee\waveemusic): the glyph + controlled +
// checked-label + pop additions below (PartIcon/PartGlyph, Style.{OffGlyphForeground,OnGlyphForeground,GlyphSize,
// GlyphGap,ContentReflow,LabelSwap,CheckedPopScale,CheckedPopMs}, Create(glyph:/checkedGlyph:/checkedLabel:),
// Controlled) are the general FollowToggle/filter-chip primitive; FollowToggle itself is app-side
// (Wavee `Platform/Controls.cs`).

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
    /// Children (the icon + label slots).</summary>
    public const string PartRoot = "Root";
    /// <summary>The label run — a <see cref="TextEl"/>, so customize via <c>parts.Set&lt;TextEl&gt;(ToggleButton.PartLabel, …)</c>.
    /// Owned: none (the per-state foreground ramp is recomputed each render before the modifier, which may override it).</summary>
    public const string PartLabel = "Label";
    /// <summary>The icon wrapper (a <see cref="BoxEl"/>, <see cref="Style.GlyphSize"/> square) — present only when a
    /// glyph was passed. Owned: Children (the glyph mount).</summary>
    public const string PartIcon = "Icon";
    /// <summary>The glyph run — a <see cref="TextEl"/> in the icon font, so customize via
    /// <c>parts.Set&lt;TextEl&gt;(ToggleButton.PartGlyph, …)</c>. Node identity is stable across a checked flip (only
    /// its codepoint swaps between <c>glyph</c>/<c>checkedGlyph</c>) — the pop (<see cref="Style.CheckedPopScale"/>)
    /// and <see cref="Style.OffGlyphForeground"/>/<see cref="Style.OnGlyphForeground"/> both rely on that stability.
    /// Owned: none.</summary>
    public const string PartGlyph = "Glyph";

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

        // ── E2 glyph + checked-label + pop additions (docs/plans/wavee/home-redesign-implementation.md Workstream E).
        // All default to "off" (null / 0f), so a caller who never touches them gets the pre-E2 tree/behavior exactly.
        /// <summary>Glyph foreground override for the OFF (and indeterminate) state. Null ⇒ ride the label's own
        /// ramp (<see cref="OffForeground"/>/<see cref="OffPressedForeground"/>/<see cref="OffDisabledForeground"/>) —
        /// the pre-E2 default when no glyph tint is authored.</summary>
        public ColorF? OffGlyphForeground { get; init; }
        /// <summary>Glyph foreground override for the ON (checked) state. Null ⇒ ride the label's own ramp
        /// (<see cref="OnForeground"/>/<see cref="OnPressedForeground"/>/<see cref="OnDisabledForeground"/>) — e.g.
        /// FollowToggle's checked heart tints to the page accent while the label stays <see cref="OnForeground"/>.</summary>
        public ColorF? OnGlyphForeground { get; init; }
        /// <summary>The icon wrapper's square size (WinUI icon-font glyph box).</summary>
        public float GlyphSize { get; init; } = 16f;
        /// <summary>Gap between the icon and the label — only applied when a glyph is present (no glyph ⇒ 0, same as
        /// today).</summary>
        public float GlyphGap { get; init; } = 8f;
        /// <summary>Root <see cref="BoxEl.Animate"/> for a content-driven size change (e.g. <c>checkedLabel</c>
        /// swapping to a longer string and the root reflowing wider). Null (the default) ⇒ the root snaps, exactly
        /// like every other Style field here that was zero/null pre-E2.</summary>
        public LayoutTransition? ContentReflow { get; init; }
        /// <summary>Cross-fade transition for the label when it is KEYED per logical state (only wired when
        /// <c>checkedLabel</c> is passed to <see cref="ToggleButton.Build"/> — otherwise the label is the same node
        /// across a flip and this is never consulted). Null ⇒ the label snaps to its new text.</summary>
        public LayoutTransition? LabelSwap { get; init; }
        /// <summary>Peak scale for the checked-glyph pop (<c>MotionRecipes.Pulse</c>) on a USER-initiated false→true
        /// flip. 0 (the default) = pop off — no glyph node scale track is ever seeded.</summary>
        public float CheckedPopScale { get; init; } = 0f;
        /// <summary>Duration of the checked-glyph pop.</summary>
        public float CheckedPopMs { get; init; } = 250f;
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

    /// <summary>The per-control clamp seam (adjustment #6). ToggleButton honors every size — identity.</summary>
    internal static ControlSize ClampSize(ControlSize size) => size;

    /// <summary>Resolve the effective style: an explicit style is the full-override escape hatch; otherwise the size
    /// axis composes over the default's geometry (Medium byte-identical to the pre-axis default).</summary>
    static Style Sized(Style? style, ControlSize size)
    {
        if (style is not null) return style;
        var cs = ClampSize(size);
        if (cs == ControlSize.Medium) return DefaultStyle;
        var m = ControlMetrics.For(cs);
        return DefaultStyle with { Padding = m.Padding, MinHeight = m.MinHeight, FontSize = m.FontSize, CornerRadius = m.CornerRadius };
    }

    /// <summary>Two-state toggle. The on/off state is a caller <see cref="Signal{T}"/> (read directly in the core —
    /// live); a click WRITES it then fires <paramref name="onChange"/> once; a programmatic write re-skins with no
    /// onChange echo. Pass no signal (<paramref name="on"/> = null) to auto-materialize an internal one (one code path).
    /// <paramref name="glyph"/>/<paramref name="checkedGlyph"/>/<paramref name="checkedLabel"/> (E2) add the optional
    /// icon slot / checked-glyph swap / checked-label swap — appended named so every existing call site is unchanged.</summary>
    public static Element Create(string label, Signal<bool>? on = null, Action<bool>? onChange = null, Style? style = null, bool isEnabled = true, TemplateParts? parts = null, ControlSize size = ControlSize.Medium,
        string? glyph = null, string? checkedGlyph = null, string? checkedLabel = null)
        => Embed.Comp(new Props(label, on, null, onChange, null, Sized(style, size), isEnabled, parts, glyph, checkedGlyph, checkedLabel),
                      () => new ToggleButtonCore());

    /// <summary>Three-state toggle (adds the mixed "indeterminate" look). The <see cref="CheckState"/> is a caller
    /// <see cref="Signal{T}"/>; a click cycles Unchecked → Checked → Indeterminate, WRITING it then firing onChange.
    /// <paramref name="glyph"/>/<paramref name="checkedGlyph"/>/<paramref name="checkedLabel"/> (E2) — see the 2-state
    /// overload; the Checked state is "on", Indeterminate reads as "off" (the existing neutral-fill convention).</summary>
    public static Element Create(string label, Signal<CheckState> state, Action<CheckState>? onChange = null, Style? style = null, bool isEnabled = true, TemplateParts? parts = null, ControlSize size = ControlSize.Medium,
        string? glyph = null, string? checkedGlyph = null, string? checkedLabel = null)
        => Embed.Comp(new Props(label, null, state, null, onChange, Sized(style, size), isEnabled, parts, glyph, checkedGlyph, checkedLabel),
                      () => new ToggleButtonCore());

    /// <summary>E2: a VALUE-CONTROLLED toggle — the checked state lives entirely in the CALLER (a signal, a library
    /// row's saved-set membership, …); this control writes NO internal state at all. A click calls
    /// <paramref name="onToggle"/>(!<paramref name="isChecked"/>) and nothing else — <paramref name="isChecked"/> only
    /// changes when the caller re-renders with a new value (the props-re-push contract: <c>Embed.Comp(props, factory)</c>,
    /// live on every parent render). Needed for the checked-glyph pop (<see cref="Style.CheckedPopScale"/>), which must
    /// tell a USER click apart from a data-driven flip — that bit of state (<c>UseRef&lt;bool&gt; userFlip</c>) lives in
    /// the mounted <see cref="ToggleButtonControlledCore"/> component, consumed by a <c>UseLayoutEffect</c> keyed on the
    /// logical checked state; it is not exposed to the caller and never persists beyond the next render.</summary>
    public static Element Controlled(string label, bool isChecked, Action<bool> onToggle, string? glyph = null, string? checkedGlyph = null,
        string? checkedLabel = null, Style? style = null, bool isEnabled = true, TemplateParts? parts = null, ControlSize size = ControlSize.Medium)
        => Embed.Comp(new ControlledProps(label, isChecked, onToggle, glyph, checkedGlyph, checkedLabel, Sized(style, size), isEnabled, parts),
                      () => new ToggleButtonControlledCore());

    /// <summary>Controlled props RE-PUSHED to <see cref="ToggleButtonCore"/>. Exactly one of <see cref="Bool"/> /
    /// <see cref="Tri"/> is set by the matching overload (both null ⇒ 2-state auto-materialize). <see cref="Glyph"/>/
    /// <see cref="CheckedGlyph"/>/<see cref="CheckedLabel"/> are E2's optional content additions.</summary>
    internal sealed record Props(string Label, Signal<bool>? Bool, Signal<CheckState>? Tri, Action<bool>? OnBool,
                                 Action<CheckState>? OnTri, Style Style, bool IsEnabled, TemplateParts? Parts,
                                 string? Glyph = null, string? CheckedGlyph = null, string? CheckedLabel = null);

    /// <summary>Re-pushed props for <see cref="Controlled"/>/<see cref="ToggleButtonControlledCore"/> — see
    /// <see cref="Controlled"/> for the value-controlled contract.</summary>
    internal sealed record ControlledProps(string Label, bool IsChecked, Action<bool> OnToggle, string? Glyph,
                                           string? CheckedGlyph, string? CheckedLabel, Style Style, bool IsEnabled, TemplateParts? Parts);

    internal static BoxEl Build(string label, CheckState state, Action<CheckState> onClick, Style? style, bool isEnabled, TemplateParts? parts,
        string? glyph = null, string? checkedGlyph = null, string? checkedLabel = null, Action<NodeHandle>? onGlyphRealized = null)
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

        // The label text: checkedLabel swaps in only while ON (E2) — off/indeterminate always show `label`.
        string activeLabel = on && checkedLabel is not null ? checkedLabel : label;
        Element labelEl;
        if (checkedLabel is not null)
        {
            // Keyed per logical state so the reconciler treats the flip as a structural replace (old label exits,
            // new label enters) — the mechanism LabelSwap's Enter/Exit terminals cross-fade over.
            var innerLabel = parts.Apply(PartLabel, new TextEl(activeLabel)
            {
                Size = s.FontSize, Color = restFg, HoverColor = restFg, PressedColor = pressFg, DisabledColor = disFg,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
            labelEl = new BoxEl
            {
                Key = on ? "toggle-label-on" : "toggle-label-off",
                Direction = 0, Shrink = 1f, MinWidth = 0f,
                Animate = s.LabelSwap,
                Children = [innerLabel],
            };
        }
        else
        {
            labelEl = parts.Apply(PartLabel, new TextEl(activeLabel)
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
                // Button parity (E2): trim instead of overrunning a narrower arranged width.
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        }

        // Optional leading icon slot (E2) — present only when a glyph was passed; the codepoint swaps between
        // glyph/checkedGlyph but the GLYPH NODE ITSELF never remounts (no Key on it), so a live pop animation and a
        // hover/press-eased foreground both keep riding the SAME node across a checked flip.
        Element[] children;
        if (glyph is not null)
        {
            string activeGlyph = on && checkedGlyph is not null ? checkedGlyph : glyph;
            ColorF glyphRest = on ? (s.OnGlyphForeground ?? restFg) : (s.OffGlyphForeground ?? restFg);
            ColorF glyphPressed = on ? (s.OnGlyphForeground ?? pressFg) : (s.OffGlyphForeground ?? pressFg);
            ColorF glyphDisabled = on ? (s.OnGlyphForeground ?? disFg) : (s.OffGlyphForeground ?? disFg);
            var glyphEl = parts.Apply(PartGlyph, new TextEl(activeGlyph)
            {
                Size = s.GlyphSize, FontFamily = Theme.IconFont,
                Color = glyphRest,
                HoverColor = glyphRest,   // pinned unchanged on hover, same convention as the label above
                PressedColor = glyphPressed,
                DisabledColor = glyphDisabled,
                OnRealized = onGlyphRealized,
            });
            var iconEl = new BoxEl
            {
                Width = s.GlyphSize, Height = s.GlyphSize, Direction = 0,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [glyphEl],
            };
            if (parts is not null) iconEl = parts.Apply(PartIcon, iconEl) with { Children = iconEl.Children };
            children = [iconEl, labelEl];
        }
        else
        {
            children = [labelEl];
        }

        var root = new BoxEl
        {
            Direction = 0, Role = AutomationRole.ToggleButton, Padding = s.Padding, MinHeight = s.MinHeight,
            // Icon↔label gap only when the icon slot is present (Button's own convention) — no glyph ⇒ 0 ⇒ layout
            // byte-identical to the pre-E2 ToggleButton.
            Gap = glyph is not null ? s.GlyphGap : 0f,
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
            // E2: an optional content-driven reflow (the root's own bounds change, e.g. a checked-label swap widening
            // it) — null (pre-E2 default) means the root snaps exactly like before.
            Animate = s.ContentReflow,
            // WinUI UseSystemFocusVisuals + FocusVisualMargin −3 (ToggleButton_themeresources.xaml:192-193); engine-drawn (E1).
            Focusable = true,
            FocusVisualMargin = s.FocusVisualMargin,
            // No Cursor: WinUI ToggleButton never calls SetCursor (arrow by inheritance, ToggleButton_Partial.cpp;
            // only HyperlinkButton sets the hand — HyperLinkButton_Partial.cpp:32).
            IsEnabled = isEnabled,
            OnClick = click,
            Children = children,
        };
        // Parts: restyle anything (fills, corners, padding…); the cycle mechanics and the label/icon slots always win.
        return parts is null ? root : parts.Apply(PartRoot, root) with { OnClick = click, Role = AutomationRole.ToggleButton, Children = root.Children };
    }
}

/// <summary>The stateful core: reads the caller's value signal DIRECTLY (2-state <c>bool</c> or 3-state
/// <see cref="CheckState"/>) so a flip/cycle re-skins granularly, reusing <see cref="ToggleButton.Build"/> for the
/// exact WinUI visuals. A click writes the signal first, then fires the matching onChange.
/// E2 checked-glyph pop: only the 2-state <c>bool</c> path can ever pop — a click there flags
/// <c>userFlip</c> BEFORE writing the signal, and the deps-gated <c>UseLayoutEffect</c> (keyed on the logical
/// checked bool) consumes that flag exactly once per false→true transition, so a data-driven external write (no
/// click, <c>userFlip</c> never set) never pops. The 3-state path never sets <c>userFlip</c>, so Indeterminate↔Checked
/// cycling never pops either — pop is a 2-state ("Follow"-shaped) concept, not a 3-state one.</summary>
internal sealed class ToggleButtonCore : Component
{
    public override Element Render()
    {
        var p = UseProps<ToggleButton.Props>();
        var own = UseSignal(false);   // internal 2-state signal (auto-materialize; unconditional hook)
        var userFlip = UseRef(false);           // E2: true only across a click-initiated false→true transition
        var glyphNode = UseRef<NodeHandle>(default);   // E2: the stable glyph node the pop scales (OnRealized-captured)

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
            onClick = _ =>
            {
                bool next = !on;
                if (next) userFlip.Value = true;   // flag BEFORE the write so the effect below sees it this render
                b.Value = next;
                p.OnBool?.Invoke(next);
            };
        }
        bool checkedNow = state == CheckState.Checked;
        UseLayoutEffect(() =>
        {
            if (checkedNow && userFlip.Value)
            {
                userFlip.Value = false;
                if (p.Style.CheckedPopScale != 0f && !glyphNode.Value.IsNull && Context.Anim is { } anim)
                    anim.Pulse(glyphNode.Value, p.Style.CheckedPopScale, p.Style.CheckedPopMs);
            }
        }, DepKey.From(checkedNow));
        return ToggleButton.Build(p.Label, state, onClick, p.Style, p.IsEnabled, p.Parts,
            p.Glyph, p.CheckedGlyph, p.CheckedLabel, onGlyphRealized: h => glyphNode.Value = h);
    }
}

/// <summary>The stateful core behind <see cref="ToggleButton.Controlled"/> (E2) — see that factory for the
/// value-controlled contract. The ONLY component state this owns is the pop bookkeeping (<c>userFlip</c>, the
/// glyph <see cref="NodeHandle"/>); the checked VALUE itself is never written here — a click only calls
/// <see cref="ToggleButton.ControlledProps.OnToggle"/>(!<see cref="ToggleButton.ControlledProps.IsChecked"/>).</summary>
internal sealed class ToggleButtonControlledCore : Component
{
    public override Element Render()
    {
        var p = UseProps<ToggleButton.ControlledProps>();
        var userFlip = UseRef(false);
        var glyphNode = UseRef<NodeHandle>(default);

        CheckState state = p.IsChecked ? CheckState.Checked : CheckState.Unchecked;
        Action<CheckState> onClick = _ =>
        {
            bool next = !p.IsChecked;
            if (next) userFlip.Value = true;
            p.OnToggle(next);   // writes NOTHING here — the caller owns the checked value
        };
        bool checkedNow = state == CheckState.Checked;
        UseLayoutEffect(() =>
        {
            if (checkedNow && userFlip.Value)
            {
                userFlip.Value = false;
                if (p.Style.CheckedPopScale != 0f && !glyphNode.Value.IsNull && Context.Anim is { } anim)
                    anim.Pulse(glyphNode.Value, p.Style.CheckedPopScale, p.Style.CheckedPopMs);
            }
        }, DepKey.From(checkedNow));
        return ToggleButton.Build(p.Label, state, onClick, p.Style, p.IsEnabled, p.Parts,
            p.Glyph, p.CheckedGlyph, p.CheckedLabel, onGlyphRealized: h => glyphNode.Value = h);
    }
}
