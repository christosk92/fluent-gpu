using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// A compact icon/glyph button (transport play/next/prev, command-bar actions). WinUI shape:
/// AppBarButton/MediaTransportControls — a rounded-square (ControlCornerRadius 4), subtle fills, a 16px glyph.
/// State colors verified against controls\dev\CommonStyles\AppBarButton_themeresources.xaml (Default = dark :5-12,
/// Light :75-82). The BOX never scales (WinUI AppBarButton states swap colors only — lines 244-259); the inner glyph
/// wrapper carries the AnimatedIcon-analogue hover/press scale (WinUI sets <c>AnimatedIcon.State</c> on the
/// presenter — Button_themeresources.xaml:192/208 — and the animated glyph responds; see AnimatedIcon.cs).
/// Stateless/controlled: pass a click handler. Size/glyph/colours live on <see cref="Style"/>; tweak via <c>with</c>.
/// </summary>
public static partial class IconButton
{
    public sealed record Style
    {
        public float Size { get; init; } = 36f;
        public float GlyphSize { get; init; } = 16f;                 // WinUI NavigationViewItem / AppBar icon box height
        public float CornerRadius { get; init; } = Radii.Control;    // ControlCornerRadius (AppBarButton_themeresources.xaml:137) — rounded-square, NOT a circle
        public string IconFont { get; init; } = Theme.IconFont;
        public ColorF Foreground { get; init; }
        public ColorF Fill { get; init; }                           // rest (AppBarButtonBackground = SubtleFillColorTransparent, line 5/75)
        public ColorF HoverFill { get; init; }
        public ColorF PressedFill { get; init; }
        public ColorF HoverForeground { get; init; }                // AppBarButtonForegroundPointerOver = TextFillColorPrimary (line 10/80)
        public ColorF PressedForeground { get; init; }              // AppBarButtonForegroundPressed = TextFillColorSecondary (line 11/81)
        public ColorF DisabledForeground { get; init; }             // AppBarButtonForegroundDisabled = TextFillColorDisabled (line 12/82)
        public ColorF DisabledFill { get; init; }                   // AppBarButtonBackgroundDisabled = SubtleFillColorDisabled (line 8/78)
        /// <summary>WinUI FocusVisualMargin = −3 (AppBarButton_themeresources.xaml:135); the engine draws the ring (E1).</summary>
        public Edges4 FocusVisualMargin { get; init; } = Edges4.All(-3f);
        // AnimatedIcon-analogue glyph motion (the WRAPPER scales, never the box — mirrors AnimatedIcon.Glyph).
        public float IconHoverScale { get; init; } = 1.08f;
        public float IconPressScale { get; init; } = 0.88f;
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Foreground = Tok.TextPrimary,            // AppBarButtonForeground = TextFillColorPrimary (line 9/79)
        Fill = Tok.FillSubtleTransparent,        // EXPLICIT rest (AppBarButtonBackground = SubtleFillColorTransparent, line 5/75)
        HoverFill = Tok.FillSubtleSecondary,     // AppBarButtonBackgroundPointerOver = SubtleFillColorSecondary (line 6/76)
        PressedFill = Tok.FillSubtleTertiary,    // AppBarButtonBackgroundPressed = SubtleFillColorTertiary (line 7/77)
        HoverForeground = Tok.TextPrimary,       // AppBarButtonForegroundPointerOver (line 10/80)
        PressedForeground = Tok.TextSecondary,   // AppBarButtonForegroundPressed (line 11/81)
        DisabledForeground = Tok.TextDisabled,   // AppBarButtonForegroundDisabled (line 12/82)
        // SubtleFillColorDisabled = #00FFFFFF in BOTH themes (Common_themeresources_any.xaml:28/232) → transparent.
        DisabledFill = Tok.FillSubtleTransparent,
    };

    public static BoxEl Create(string glyph, Action onClick, Style? style = null, bool isEnabled = true)
    {
        var s = style ?? DefaultStyle;
        return new BoxEl
        {
            Width = s.Size, Height = s.Size, Direction = 0, Role = AutomationRole.Button,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(s.CornerRadius),
            // Disabled is a logical state: resting fill swaps to the WinUI disabled token. Hover/Pressed never fire while
            // disabled (the engine gate stops hit-test), so HoverFill/PressedFill stay wired but inert.
            Fill = isEnabled ? s.Fill : s.DisabledFill,
            HoverFill = s.HoverFill, PressedFill = s.PressedFill,
            // WinUI UseSystemFocusVisuals + FocusVisualMargin −3 (AppBarButton_themeresources.xaml:134-135); engine-drawn (E1).
            Focusable = true,
            FocusVisualMargin = s.FocusVisualMargin,
            // WinUI buttons keep the ARROW cursor (only HyperlinkButton shows the hand — HyperLinkButton_Partial.cpp:32).
            Cursor = CursorId.Arrow,
            IsEnabled = isEnabled,                            // P1 engine gate (no manual handler-nulling)
            OnClick = onClick,
            // Inline glyph wrapper (mirrors AnimatedIcon.Glyph) so the TextEl carries the foreground interaction ramps
            // and the wrapper carries the AnimatedIcon-analogue scale (rides the button's eased hover/press progress).
            Children =
            [
                new BoxEl
                {
                    Width = s.GlyphSize, Height = s.GlyphSize, Direction = 0,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    HoverScale = s.IconHoverScale, PressScale = s.IconPressScale,
                    Children =
                    [
                        new TextEl(glyph)
                        {
                            Size = s.GlyphSize, FontFamily = s.IconFont,
                            Color = s.Foreground,                // P2 foreground ramp: rest → hover → pressed; disabled via the gate
                            HoverColor = s.HoverForeground,
                            PressedColor = s.PressedForeground,
                            DisabledColor = s.DisabledForeground,
                        },
                    ],
                },
            ],
        };
    }
}
