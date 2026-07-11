using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>A WinUI AppBarButton (AppBarButton_themeresources.xaml). Three layout families:
/// <list type="bullet">
/// <item><b>FullSize</b> (default style): a 68-wide (:133 Width=68), 64-min-high (AppBarThemeMinHeight,
/// CommandBar_themeresources.xaml:71) vertical stack — 16px icon (AppBarButtonContentHeight) over a 12px Caption
/// label.</item>
/// <item><b>Compact</b> (<c>isCompact: true</c> — the closed CommandBar): the label collapses; icon-only at the 48px
/// compact height (AppBarThemeCompactHeight :72), ContentViewboxCompactMargin 0,12,0,12 (:111).</item>
/// <item><b>Overflow</b> (<see cref="CreateOverflow"/> — the CommandBar overflow menu): a HORIZONTAL row — optional
/// toggle-check column, optional 16×16 icon at the left (OverflowWithMenuIcons :211-223), the label as a 14px Body run
/// (OverflowTextLabel, padding 0,5,0,8 = AppBarButtonOverflowTextLabelPadding :114; lead margin 12 / 38 with toggles /
/// 76 with toggles+icons :192-238), and right-aligned 12px accelerator text (KeyboardAcceleratorTextLabel Margin
/// 24,0,12,0 :371).</item>
/// </list>
/// State fills are the subtle ramp (AppBarButtonBackground/​PointerOver/​Pressed = SubtleFill Transparent/Secondary/
/// Tertiary); press dims the foreground to TextSecondary. FocusVisualMargin = −3 (:135). A
/// <see cref="KeyAccelerator"/> chord invokes the button from anywhere (WinUI KeyboardAccelerator).</summary>
public static class AppBarButton
{
    /// <summary>AppBarButton visual style — dimensions and color tokens from WinUI 3 (AppBarButton_themeresources.xaml:
    /// Width 68, MinHeight 64, icon 16px, label 12px, ControlCornerRadius 4, inner-border margin 2,6,2,6).</summary>
    public sealed record Style
    {
        public float Width { get; init; } = 68f;                     // default style Width (:133)
        public float MinHeight { get; init; } = 64f;                 // AppBarThemeMinHeight (CommandBar_themeresources.xaml:71)
        public float CompactSize { get; init; } = 48f;               // AppBarThemeCompactHeight (:72)
        public float IconHeight { get; init; } = 16f;                // AppBarButtonContentHeight
        public float LabelFontSize { get; init; } = 12f;
        public float Gap { get; init; } = 4f;
        public float CornerRadius { get; init; } = Radii.Control;    // ControlCornerRadius = 4
        public Edges4 Padding { get; init; } = new(2, 6, 2, 6);      // AppBarButtonInnerBorderMargin (:117)

        public ColorF RestFill { get; init; }
        public ColorF RestForeground { get; init; }
        public ColorF HoverFill { get; init; }
        public ColorF HoverForeground { get; init; }
        public ColorF PressedFill { get; init; }
        public ColorF PressedForeground { get; init; }
        public ColorF DisabledFill { get; init; }
        public ColorF DisabledForeground { get; init; }
    }

    public static Style? StyleOverride;

    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        RestFill = Tok.FillSubtleTransparent,                        // AppBarButtonBackground = SubtleFillColorTransparent
        RestForeground = Tok.TextPrimary,                           // AppBarButtonForeground = TextFillColorPrimary
        HoverFill = Tok.FillSubtleSecondary,                        // AppBarButtonBackgroundPointerOver
        HoverForeground = Tok.TextPrimary,                          // AppBarButtonForegroundPointerOver = TextFillColorPrimary
        PressedFill = Tok.FillSubtleTertiary,                       // AppBarButtonBackgroundPressed
        PressedForeground = Tok.TextSecondary,                      // AppBarButtonForegroundPressed = TextFillColorSecondary (WinUI dims on press)
        DisabledFill = Tok.FillSubtleTransparent,                   // AppBarButtonBackgroundDisabled = SubtleFillColorDisabled = #00FFFFFF (transparent)
        DisabledForeground = Tok.TextDisabled,                      // AppBarButtonForegroundDisabled = TextFillColorDisabled
    };

    /// <summary>FullSize (labeled, 68×64) or Compact (icon-only, 48×48) AppBarButton.
    /// <paramref name="accelerator"/> wires a real engine keyboard-accelerator chord onto the click.</summary>
    public static BoxEl Create(string glyph, string label, Action onClick, bool enabled = true, Style? style = null,
                               bool isCompact = false, KeyAccelerator? accelerator = null)
    {
        var s = style ?? DefaultStyle;
        bool labeled = !isCompact && label.Length > 0;
        // The engine IsEnabled gate stops hit-test/focus/keyboard when disabled; the child TextEls inherit the box's
        // eased hover/press progress (WinUI dims the foreground to TextSecondary on press) and fall back to
        // DisabledColor under a disabled ancestor. Resting fill stays the control-chosen disabled token.
        var children = new List<Element>(2)
        {
            new TextEl(glyph)
            {
                Size = s.IconHeight,
                Color = s.RestForeground,
                HoverColor = s.HoverForeground,
                PressedColor = s.PressedForeground,
                DisabledColor = s.DisabledForeground,
                FontFamily = Theme.IconFont,
            },
        };
        if (labeled)
            children.Add(new TextEl(label)
            {
                Size = s.LabelFontSize,
                Color = s.RestForeground,
                HoverColor = s.HoverForeground,
                PressedColor = s.PressedForeground,
                DisabledColor = s.DisabledForeground,
            });

        return new BoxEl
        {
            Direction = 1,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Gap = s.Gap,
            Width = isCompact ? s.CompactSize : s.Width,
            MinHeight = isCompact ? s.CompactSize : s.MinHeight,
            // Compact: ContentViewboxCompactMargin 0,12,0,12 centers the bare icon in the 48 box (:111);
            // FullSize keeps AppBarButtonInnerBorderMargin 2,6,2,6 (:117).
            Padding = isCompact ? new Edges4(0, 12, 0, 12) : s.Padding,
            Corners = CornerRadius4.All(s.CornerRadius),
            Fill = enabled ? s.RestFill : s.DisabledFill,
            HoverFill = s.HoverFill,
            PressedFill = s.PressedFill,
            // AppBarButtonInnerBorder BackgroundTransition = 83ms BrushTransition (perf2026 template) — also covers
            // the logical compact↔labeled fill swap on a live node.
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            HoverEasing = Easing.FluentPopOpen, PressEasing = Easing.FluentPopOpen,
            IsEnabled = enabled,
            Focusable = enabled,
            FocusVisualMargin = Edges4.All(-3f),                    // FocusVisualMargin = -3 (:135)
            Accelerator = accelerator,                              // WinUI KeyboardAccelerator → invokes OnClick
            OnClick = onClick,
            Role = AutomationRole.Button,
            Children = children.ToArray(),
        };
    }

    /// <summary>An Overflow-state AppBarButton row for the CommandBar overflow menu (AppBarButton_themeresources.xaml
    /// :192-238 Overflow/OverflowWithToggleButtons/OverflowWithMenuIcons/OverflowWithToggleButtonsAndMenuIcons):
    /// [check column when <paramref name="hasToggles"/>] [16×16 icon at left when <paramref name="hasIcons"/>]
    /// label (Body 14, padding 0,5,0,8) … right-aligned accelerator text (Caption 12, margin 24,0,12,0).</summary>
    public static BoxEl CreateOverflow(
        AppBarCommand cmd, bool hasToggles, bool hasIcons, Action onInvoke, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        bool enabled = cmd.Enabled;
        bool isToggle = cmd.Kind == AppBarCommandKind.ToggleButton;
        bool isChecked = isToggle && cmd.IsChecked;
        ColorF fg = enabled ? s.RestForeground : s.DisabledForeground;

        var children = new List<Element>(4);

        // Toggle-check column (the 38px lead): E73E @12, visible when checked — AppBarToggleButton overflow
        // OverflowCheckGlyph (the CommandBarFlyout twin sits at Margin 15,4,14,4; the 38px label lead is the contract).
        if (hasToggles)
        {
            Element check = isChecked
                ? new TextEl(Icons.Accept) { Size = 12f, Color = fg, PressedColor = enabled ? s.PressedForeground : fg, FontFamily = Theme.IconFont }
                : new BoxEl();
            children.Add(new BoxEl
            {
                Width = 38f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [check],
            });
        }

        // Menu-icon column: 16×16 at the left, Margin 12,0,12,0 (38,0,12,0 with toggles — the toggle column above
        // already supplies the 38 lead, so the icon cell keeps its own 12 inset relative to it).
        if (hasIcons)
        {
            Element icon = IconView.Render(cmd.Icon, 16f, glyphColor: fg,
                pressedColor: enabled ? s.PressedForeground : fg, disabledColor: s.DisabledForeground,
                enabled: () => enabled);
            children.Add(new BoxEl
            {
                Width = 16f, Height = 16f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Margin = hasToggles ? new Edges4(0, 0, 12, 0) : new Edges4(12, 0, 12, 0),
                Children = [icon],
            });
        }

        // OverflowTextLabel: BodyTextBlockStyle (14px), TextTrimming=Clip, padding 0,5,0,8
        // (AppBarButtonOverflowTextLabelPadding :114); lead margin 12 when no leading columns.
        children.Add(new BoxEl
        {
            Grow = 1f,
            Padding = new Edges4(0, 5, 0, 8),
            Margin = (hasToggles || hasIcons) ? default : new Edges4(12, 0, 12, 0),
            AlignItems = FlexAlign.Center,
            Direction = 0,
            Children = [new TextEl(cmd.Label) { Size = 14f, Color = fg, PressedColor = enabled ? s.PressedForeground : fg, DisabledColor = s.DisabledForeground, Trim = TextTrim.Clip }],
        });

        // KeyboardAcceleratorTextLabel: Caption 12, right-aligned, Margin 24,0,12,0,
        // AppBarButtonKeyboardAcceleratorTextForeground = TextFillColorSecondary ramp (:371 + the per-state resources).
        if (cmd.AcceleratorText is { Length: > 0 } acc)
            children.Add(new TextEl(acc)
            {
                Size = 12f, Margin = new Edges4(24, 0, 12, 0), AlignSelf = FlexAlign.Center,
                Color = enabled ? Tok.TextSecondary : Tok.TextDisabled,
                PressedColor = enabled ? Tok.TextTertiary : Tok.TextDisabled,
                DisabledColor = Tok.TextDisabled,
            });

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            MinHeight = 0f,                                          // Overflow sets ContentRoot.MinHeight = 0 (:193)
            Corners = CornerRadius4.All(s.CornerRadius),
            Fill = enabled ? s.RestFill : s.DisabledFill,
            HoverFill = enabled ? s.HoverFill : s.DisabledFill,
            PressedFill = enabled ? s.PressedFill : s.DisabledFill,
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            HoverEasing = Easing.FluentPopOpen, PressEasing = Easing.FluentPopOpen,
            IsEnabled = enabled,
            Focusable = enabled,
            OnClick = onInvoke,
            Role = isToggle ? AutomationRole.ToggleButton : AutomationRole.MenuItem,
            Children = children.ToArray(),
        };
    }
}
