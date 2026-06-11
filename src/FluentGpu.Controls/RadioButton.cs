using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI RadioButton — style values from microsoft-ui-xaml controls\dev\CommonStyles\RadioButton_themeresources.xaml
/// ("the template"; ThemeDictionaries Default=dark :4-61 / Light :120-177): a 20px ring (OuterEllipse/CheckOuterEllipse,
/// :371/373) + (when selected) a TextOnAccent dot (CheckGlyph, :374) that grows 12→14 on hover / shrinks →10 on press
/// over ControlNormalAnimationDuration (250ms) with the ControlFastOutSlowInKeySpline (:255-260/:292-297), plus the
/// unchecked-pressed 4px proto-dot (PressedCheckGlyph, :376) revealing toward 10 over ControlFastAnimationDuration
/// (167ms, :298-306). Mutual exclusion comes from a shared selected index — use <see cref="Group"/> (an ad-hoc set) or
/// the full <see cref="RadioButtons"/> container (header, columns, roving keyboard). Controlled.
/// </summary>
public static partial class RadioButton
{
    // Template parts (the TemplateParts door; see Expander for the reference adoption). Each part's doc lists the
    // props the control OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The returned clickable row (WinUI RootGrid). Owned: OnClick (the select), OnKeyDown (the container's
    /// arrow roving), OnRealized (handle capture, chained), Role, TabStop (the roving single-tab-stop contract),
    /// Children.</summary>
    public const string PartRoot = "Root";
    /// <summary>The 20px outer ellipse (WinUI OuterEllipse/CheckOuterEllipse). Owned: Children (the conditional dot
    /// mount — CheckGlyph when selected, PressedCheckGlyph when not).</summary>
    public const string PartRing = "Ring";
    /// <summary>The selected accent dot (WinUI CheckGlyph). Owned: Key (reconcile identity), Animate (the
    /// disabled-entry 12→14 resize spec).</summary>
    public const string PartDot = "Dot";
    /// <summary>The unchecked-pressed 4px proto-dot (WinUI PressedCheckGlyph). Owned: Key (reconcile identity).</summary>
    public const string PartPressedDot = "PressedDot";
    /// <summary>The default text label (a <see cref="TextEl"/> — use <c>Parts.Set&lt;TextEl&gt;(RadioButton.PartLabel, …)</c>);
    /// not applied when a content element fills the slot (slots restructure, parts style). Owned: none.</summary>
    public const string PartLabel = "Label";

    internal static class RadioButtonMotion
    {
        public const float ControlFastMs = 167f;     // Common_themeresources_any.xaml:604 ControlFastAnimationDuration
        public const float ControlNormalMs = 250f;   // Common_themeresources_any.xaml:603 ControlNormalAnimationDuration
        public static readonly EasingSpec FastOutSlowIn = Easing.FluentPopOpen; // ControlFastOutSlowInKeySpline = 0,0,0,1 (:602)
    }

    public sealed record Style
    {
        public float RingSize { get; init; } = 20f;        // OuterEllipse Width/Height (template:371/373)
        public float DotSize { get; init; } = 12f;         // RadioButtonCheckGlyphSize (template:179)
        public float DotHoverSize { get; init; } = 14f;    // RadioButtonCheckGlyphPointerOverSize (template:180)
        public float DotPressedSize { get; init; } = 10f;  // RadioButtonCheckGlyphPressedOverSize (template:181)
        public float DotDisabledSize { get; init; } = 14f; // Disabled storyboard animates CheckGlyph W/H → 14 (template:338-343)
        public float PressedDotSize { get; init; } = 4f;   // PressedCheckGlyph Width/Height (template:376)
        public float FontSize { get; init; } = 14f;        // ControlContentThemeFontSize (template:193)
        public float MinHeight { get; init; } = 32f;       // glyph grid Height=32 (template:370)
        public float MinWidth { get; init; } = 120f;       // DefaultRadioButtonStyle MinWidth (template:194)
        public float ContentGap { get; init; } = 8f;       // Padding 8,6,0,0 → 8px between the 20px glyph column and the label (template:187/366-367)
        /// <summary>WinUI FocusVisualMargin −7,−3,−7,−3 (template:196); the engine draws the ring (E1).</summary>
        public Edges4 FocusVisualMargin { get; init; } = new(-7f, -3f, -7f, -3f);

        // Unchecked ellipse — ControlAltFillColor* fill + ControlStrongStrokeColor* ring
        public ColorF OffFill { get; init; }               // RadioButtonOuterEllipseFill → ControlAltFillColorSecondary (template:22/138)
        public ColorF OffHover { get; init; }              // ...FillPointerOver → ControlAltFillColorTertiary (template:23/139)
        public ColorF OffPressed { get; init; }            // ...FillPressed → ControlAltFillColorQuarternary (template:24/140)
        public ColorF OffBorder { get; init; }             // RadioButtonOuterEllipseStroke → ControlStrongStrokeColorDefault; PointerOver SAME (template:18-19/134-135)
        public ColorF OffBorderPressed { get; init; }      // ...StrokePressed → ControlStrongStrokeColorDisabled (template:20/136)

        // Checked ellipse — accent fill + accent ring (stroke == fill at rest; hover/press recolor BOTH)
        public ColorF OnRing { get; init; }                // RadioButtonOuterEllipseCheckedFill/Stroke → AccentFillColorDefault (template:26,30/142,146)
        public ColorF OnHover { get; init; }               // ...CheckedFill/StrokePointerOver → AccentFillColorSecondary (template:27,31/143,147)
        public ColorF OnPressed { get; init; }             // ...CheckedFill/StrokePressed → AccentFillColorTertiary (template:28,32/144,148)
        public ColorF OnBorder { get; init; }              // RadioButtonOuterEllipseCheckedStroke → AccentFillColorDefault (template:26/142)

        public ColorF Dot { get; init; }                   // RadioButtonCheckGlyphFill → TextOnAccentFillColorPrimary (template:34-36/150-152)
        public GradientSpec? DotBorder { get; init; }      // RadioButtonCheckGlyphStrokeChecked → AccentControlElevationBorder (template:42-44/158-160)
        public GradientSpec? DotBorderDisabled { get; init; } // ...StrokeCheckedDisabled → ControlElevationBorder (template:45/161)
        public GradientSpec? PressedDotBorder { get; init; }  // PressedCheckGlyph BorderBrush = RadioButtonCheckGlyphStroke → CircleElevationBorder (template:38/154 + :376)
        public ColorF Foreground { get; init; }            // RadioButtonForeground → TextFillColorPrimary (== PointerOver/Pressed, template:6-8/122-124)
        public ColorF DisabledForeground { get; init; }    // RadioButtonForegroundDisabled → TextFillColorDisabled (template:9/125)

        // Disabled resting swaps — WinUI recolors ring + glyph in the Disabled visual state (template:309-345).
        public ColorF OffFillDisabled { get; init; }       // ...FillDisabled → ControlAltFillColorDisabled (template:25/141)
        public ColorF OffBorderDisabled { get; init; }     // ...StrokeDisabled → ControlStrongStrokeColorDisabled (template:21/137)
        public ColorF OnRingDisabled { get; init; }        // ...CheckedFill/StrokeDisabled → AccentFillColorDisabled (template:29,33/145,149)
        public ColorF DotDisabled { get; init; }           // RadioButtonCheckGlyphFillDisabled → TextOnAccentFillColorPrimary — NOT the Disabled token (template:37/153)
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        OffFill = Tok.FillControlAltSecondary, OffHover = Tok.FillControlAltTertiary, OffPressed = Tok.FillControlAltQuaternary,
        OffBorder = Tok.StrokeControlStrongDefault, OffBorderPressed = Tok.StrokeControlStrongDisabled,
        OnRing = Tok.AccentDefault, OnHover = Tok.AccentSecondary, OnPressed = Tok.AccentTertiary, OnBorder = Tok.AccentDefault,
        Dot = Tok.TextOnAccentPrimary, DotBorder = Tok.AccentControlElevationBorder,
        DotBorderDisabled = Tok.ControlElevationBorder, PressedDotBorder = Tok.CircleElevationBorder,
        Foreground = Tok.TextPrimary, DisabledForeground = Tok.TextDisabled,
        OffFillDisabled = Tok.FillControlAltDisabled, OffBorderDisabled = Tok.StrokeControlStrongDisabled,
        OnRingDisabled = Tok.AccentDisabled, DotDisabled = Tok.TextOnAccentPrimary,
    };

    public static BoxEl Create(string label, bool selected, Action onSelect, Style? style = null, bool isEnabled = true,
                               TemplateParts? parts = null)
        => Build(label, null, selected, onSelect, style ?? DefaultStyle, isEnabled,
                 focusable: true, onKeyDown: null, onRealized: null, parts: parts);

    /// <summary>
    /// The shared item factory — also the <see cref="RadioButtons"/> container seam: <paramref name="content"/>
    /// replaces the plain text label (WinUI RadioButtons item content), <paramref name="focusable"/> implements the
    /// container's roving single tab stop (RadioButtons.xaml:5-6 IsTabStop=False + TabNavigation=Once), and
    /// <paramref name="onKeyDown"/>/<paramref name="onRealized"/> wire the arrow-key roving focus.
    /// </summary>
    internal static BoxEl Build(string? label, Element? content, bool selected, Action onSelect, Style s, bool isEnabled,
                                bool focusable, Action<KeyEventArgs>? onKeyDown, Action<NodeHandle>? onRealized,
                                TemplateParts? parts = null)
    {
        float hoverScale = s.DotSize > 0f ? s.DotHoverSize / s.DotSize : 1f;
        float pressScale = s.DotSize > 0f ? s.DotPressedSize / s.DotSize : 1f;
        float pressedDotScale = s.PressedDotSize > 0f ? s.DotPressedSize / s.PressedDotSize : 1f;
        // Disabled is a resting swap for the RING (no hover/press progress reaches a disabled node), but the GLYPH gets
        // an animated resize 12→14 over ControlFastAnimationDuration on Disabled entry (template:338-343).
        ColorF ringFill = !isEnabled ? (selected ? s.OnRingDisabled : s.OffFillDisabled) : (selected ? s.OnRing : s.OffFill);
        ColorF ringBorder = !isEnabled ? (selected ? s.OnRingDisabled : s.OffBorderDisabled) : (selected ? s.OnBorder : s.OffBorder);
        float dotSize = isEnabled ? s.DotSize : s.DotDisabledSize;
        // Disabled entry animates the resize 12→14 over 167ms FastOutSlowIn (template:338-343); the spec is attached
        // only on the disabled render, so re-enabling reverts instantly (no Normal-state keyframes).
        LayoutTransition? dotResize = isEnabled
            ? null
            : new LayoutTransition(TransitionChannels.Bounds,
                                   TransitionDynamics.Tween(RadioButtonMotion.ControlFastMs, Easing.FluentPopOpen),
                                   SizeMode.ScaleCorrect);
        Element[] ringKids;
        if (selected)
        {
            var dot = new BoxEl
            {
                Key = "CheckGlyph",
                Width = dotSize, Height = dotSize,
                Corners = Radii.Circle(dotSize),
                Fill = !isEnabled ? s.DotDisabled : s.Dot,
                BorderBrush = !isEnabled ? s.DotBorderDisabled : s.DotBorder,   // StrokeChecked / StrokeCheckedDisabled (template:42/45)
                BorderWidth = (isEnabled ? s.DotBorder : s.DotBorderDisabled) is null ? 0f : 1f,   // Ellipse default StrokeThickness = 1
                HoverScale = isEnabled ? hoverScale : 1f,
                PressScale = isEnabled ? pressScale : 1f,
                HoverDurationMs = RadioButtonMotion.ControlNormalMs,   // 250ms (template:255-260)
                PressDurationMs = RadioButtonMotion.ControlNormalMs,   // 250ms (template:292-297)
                HoverEasing = RadioButtonMotion.FastOutSlowIn,
                PressEasing = RadioButtonMotion.FastOutSlowIn,
                Animate = dotResize,
            };
            // Parts: restyle the dot (fill, size, the grow/shrink ramp); identity + the disabled resize spec win.
            ringKids = [parts.Apply(PartDot, dot) with { Key = "CheckGlyph", Animate = dotResize }];
        }
        else
        {
            var pressedDot = new BoxEl
            {
                Key = "PressedCheckGlyph",
                Width = s.PressedDotSize, Height = s.PressedDotSize,
                Corners = Radii.Circle(s.PressedDotSize),
                Fill = !isEnabled ? s.DotDisabled : s.Dot,             // PressedCheckGlyph Background = RadioButtonCheckGlyphFill (template:376)
                BorderBrush = s.PressedDotBorder,                      // BorderBrush = RadioButtonCheckGlyphStroke → CircleElevation (template:38/376)
                BorderWidth = s.PressedDotBorder is null ? 0f : 1f,
                Opacity = 0f,
                PressedOpacity = 1f,                                   // Opacity → 1 at KeyTime 0 (template:298-300)
                PressScale = pressedDotScale,                          // 4 → 10 (template:301-306)
                PressDurationMs = RadioButtonMotion.ControlFastMs,     // 167ms
                PressEasing = RadioButtonMotion.FastOutSlowIn,
            };
            ringKids = [parts.Apply(PartPressedDot, pressedDot) with { Key = "PressedCheckGlyph" }];
        }

        var ring = new BoxEl
        {
            Width = s.RingSize, Height = s.RingSize,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.Circle(s.RingSize),
            BorderWidth = 1f,                              // RadioButtonBorderThemeThickness (template:5/121)
            BorderColor = ringBorder,
            // The stroke is stateful too: unchecked hover KEEPS ControlStrongDefault / pressed dims to StrongDisabled
            // (template:18-20/134-136); checked hover/pressed recolor to AccentSecondary/Tertiary (template:26-28/142-144).
            // Explicit values defeat the recorder's A==0 auto-lighten fallback (Element.cs:27-28).
            HoverBorderColor = selected ? s.OnHover : s.OffBorder,
            PressedBorderColor = selected ? s.OnPressed : s.OffBorderPressed,
            Fill = ringFill,
            HoverFill = selected ? s.OnHover : s.OffHover,
            PressedFill = selected ? s.OnPressed : s.OffPressed,
            Children = ringKids,
        };
        // Parts: restyle the ellipse (the per-state ramps above sit BEFORE the modifier); the dot mount always wins.
        ring = parts.Apply(PartRing, ring) with { Children = ringKids };

        Element[] children =
        [
            ring,
            // WinUI keeps RadioButtonForeground == PointerOver == Pressed (TextPrimary), so only the disabled ramp differs.
            content ?? parts.Apply(PartLabel,
                new TextEl(label ?? "") { Size = s.FontSize, Color = s.Foreground, DisabledColor = s.DisabledForeground }),
        ];

        var root = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = s.ContentGap,                                            // Padding 8,6,0,0 (template:187)
            MinHeight = s.MinHeight,
            MinWidth = s.MinWidth,                                         // 120 (template:194)
            Role = AutomationRole.RadioButton,
            IsEnabled = isEnabled,
            // The roving SINGLE tab stop (RadioButtons.xaml:5-6 IsTabStop=False + TabNavigation=Once): an explicit
            // TabStop (the WinUI Control.IsTabStop equivalent) — NOT Focusable — because a clickable node is otherwise
            // auto-derived focusable (Reconciler: TabStop ?? (Focusable || OnClick != null)), which would put every
            // radio in the tab order instead of only the selected one.
            TabStop = focusable && isEnabled,
            FocusVisualMargin = s.FocusVisualMargin,                       // −7,−3,−7,−3 (template:196)
            // Space ONLY (bAcceptsReturn=false, RadioButton_Partial.cpp:30) — Enter routes on (dialog default button).
            ActivateOnEnter = false,
            OnClick = onSelect,
            OnKeyDown = onKeyDown,
            OnRealized = onRealized,
            Children = children,
        };
        if (parts is { } pp)
        {
            // Parts: restyle anything on the row; the select/roving mechanics and structure always win.
            var m = pp.Apply(PartRoot, root);
            root = m with
            {
                OnClick = onSelect,
                OnKeyDown = onKeyDown,
                OnRealized = TemplateParts.Chain(onRealized, m.OnRealized),
                Role = AutomationRole.RadioButton,
                TabStop = focusable && isEnabled,
                Children = children,
            };
        }
        return root;
    }

    /// <summary>A mutually-exclusive group: renders one radio per option; clicking option i invokes <paramref name="onSelect"/>(i).
    /// Ad-hoc (every item is a tab stop, no arrow roving) — prefer <see cref="RadioButtons"/> for the WinUI container semantics.</summary>
    public static BoxEl Group(IReadOnlyList<string> options, int selected, Action<int> onSelect, bool horizontal = false, Style? style = null, bool isEnabled = true,
                              TemplateParts? parts = null)
    {
        var children = new Element[options.Count];
        for (int i = 0; i < options.Count; i++)
        {
            int idx = i;
            children[i] = Create(options[i], i == selected, () => onSelect(idx), style, isEnabled, parts);
        }
        return new BoxEl { Direction = horizontal ? (byte)0 : (byte)1, Gap = horizontal ? 16f : 4f, Children = children };
    }
}
