using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// A compact icon/glyph button (transport play/next/prev, command-bar actions). WinUI shape:
/// AppBarButton/MediaTransportControls — a rounded-square (ControlCornerRadius 4), subtle fills, a 16px glyph.
/// Stateless/controlled: pass a click handler. Size/glyph/colours live on <see cref="Style"/>; tweak via <c>with</c>.
/// </summary>
public static partial class IconButton
{
    public sealed record Style
    {
        public float Size { get; init; } = 36f;
        public float GlyphSize { get; init; } = 16f;                 // WinUI NavigationViewItem / AppBar icon box height
        public float CornerRadius { get; init; } = Radii.Control;    // 4 — WinUI rounded-square (transport buttons are NOT circles)
        public string IconFont { get; init; } = Theme.IconFont;
        public ColorF Foreground { get; init; }
        public ColorF Fill { get; init; }                           // rest (AppBarButtonBackground = SubtleFillColorTransparent)
        public ColorF HoverFill { get; init; }
        public ColorF PressedFill { get; init; }
        public ColorF HoverForeground { get; init; }                // AppBarButtonForegroundPointerOver = TextPrimary
        public ColorF PressedForeground { get; init; }              // AppBarButtonForegroundPressed = TextSecondary
        public ColorF DisabledForeground { get; init; }             // AppBarButtonForegroundDisabled = TextDisabled
        public ColorF DisabledFill { get; init; }                   // AppBarButtonBackgroundDisabled = SubtleFillColorDisabled
        public float HoverScale { get; init; } = 1f;                // icon buttons don't grow by default
        public float PressScale { get; init; } = 1f;
        public float IconHoverScale { get; init; } = 1.08f;
        public float IconPressScale { get; init; } = 0.88f;
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Foreground = Tok.TextPrimary,
        Fill = Tok.FillSubtleTransparent,        // EXPLICIT rest (AppBarButtonBackground)
        HoverFill = Tok.FillSubtleSecondary,     // SubtleFillColorSecondary
        PressedFill = Tok.FillSubtleTertiary,    // SubtleFillColorTertiary
        HoverForeground = Tok.TextPrimary,       // AppBarButtonForegroundPointerOver
        PressedForeground = Tok.TextSecondary,   // AppBarButtonForegroundPressed
        DisabledForeground = Tok.TextDisabled,
        DisabledFill = Tok.FillSubtleTransparent,  // SubtleFillColorDisabled = transparent
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
            HoverScale = isEnabled ? s.HoverScale : 1f, PressScale = isEnabled ? s.PressScale : 1f,
            IsEnabled = isEnabled,                            // P1 engine gate (no manual handler-nulling)
            OnClick = onClick,
            // Inline glyph wrapper (mirrors AnimatedIcon.Glyph) so the TextEl carries the foreground interaction ramps.
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
