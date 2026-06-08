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
        DisabledForeground = Tok.TextDisabled,
        DisabledFill = Tok.FillControlDisabled,  // no SubtleFillColorDisabled token → closest existing
    };

    public static BoxEl Create(string glyph, Action onClick, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        return new BoxEl
        {
            Width = s.Size, Height = s.Size, Direction = 0, Role = AutomationRole.Button,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(s.CornerRadius),
            Fill = s.Fill, HoverFill = s.HoverFill, PressedFill = s.PressedFill,
            HoverScale = s.HoverScale, PressScale = s.PressScale,
            OnClick = onClick,
            Children = [AnimatedIcon.Glyph(glyph, s.GlyphSize, s.Foreground, s.IconFont, s.IconHoverScale, s.IconPressScale)],
        };
    }
}
