using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// An engine-drawn window caption button (minimize / maximize-restore / close) for the custom-frame
/// <see cref="TitleBar"/> — the Win11 shell look: 46×bar-height, square-edged (caption buttons abut the window edge,
/// no corner radius), a 10px Segoe Fluent chrome glyph, subtle hover/press fills (close = the shell red with a white
/// glyph). The OS owns those pixels' hit-testing (WM_NCHITTEST returns HTMINBUTTON/HTMAXBUTTON/HTCLOSE so the Win11
/// snap-layouts flyout works); the Win32 PAL synthesizes pointer input back into the engine from the WM_NC* mouse
/// messages, so these ramps run through the normal InteractionAnimator path even though the pixels are non-client.
/// Visual states = BoxEl fill ramp + TextEl foreground ramp at 83ms (WinUI ControlFasterAnimationDuration) with the
/// BoxEl default (0,0,0,1) spline — never a per-control state machine.
/// </summary>
public static class CaptionButton
{
    /// <summary>Win11 caption button width @100% (FluentAvalonia WindowDecorations.axaml:9; ×scale at hit-test).</summary>
    public const float Width = 46f;
    /// <summary>Chrome glyph point size (FluentAvalonia WindowDecorations.axaml:117).</summary>
    public const float GlyphSize = 10f;

    public sealed record Style
    {
        public ColorF Fill { get; init; }
        public ColorF HoverFill { get; init; }
        public ColorF PressedFill { get; init; }
        public ColorF Foreground { get; init; }
        public ColorF HoverForeground { get; init; }
        public ColorF PressedForeground { get; init; }
        /// <summary>Glyph color while the window is deactivated (the shell dims caption GLYPHS, never fills).</summary>
        public ColorF InactiveForeground { get; init; }
    }

    /// <summary>Minimize/maximize ramp: transparent → SubtleSecondary → SubtleTertiary fills; glyph primary → secondary.</summary>
    public static Style MinMax => new()
    {
        Fill = Tok.FillSubtleTransparent,
        HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary,
        Foreground = Tok.TextPrimary,
        HoverForeground = Tok.TextPrimary,
        PressedForeground = Tok.TextSecondary,
        InactiveForeground = Tok.TextDisabled,
    };

    /// <summary>Close ramp: the Win11 shell red on hover/press with a white glyph (pressed glyph at ~70%).</summary>
    public static Style Close => MinMax with
    {
        HoverFill = Tok.CaptionCloseHover,
        PressedFill = Tok.CaptionClosePressed,
        HoverForeground = ColorF.FromRgba(0xFF, 0xFF, 0xFF),                 // white on red in both themes
        PressedForeground = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3),
    };

    /// <summary><paramref name="active"/> = window activation (false dims the glyph to
    /// <see cref="Style.InactiveForeground"/> — fills stay wired; Win11 shows hover states on inactive captions too).
    /// <paramref name="height"/> = the bar height (caption buttons stretch the full bar, flush to the top edge).
    /// <paramref name="onRealized"/> captures the node handle for the TitleBar's WM_NCHITTEST region report.</summary>
    public static BoxEl Create(string glyph, Action onClick, Style style, bool active = true,
                               float height = TitleBar.ExpandedHeight, Action<NodeHandle>? onRealized = null)
        => new BoxEl
        {
            Width = Width, Height = height, Direction = 0, Role = AutomationRole.Button,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Fill = style.Fill, HoverFill = style.HoverFill, PressedFill = style.PressedFill,
            HoverDurationMs = 83f, PressDurationMs = 83f,   // ControlFaster; BoxEl default easing = spline (0,0,0,1)
            // Window chrome: never a Tab stop, never steals focus (the OS caption buttons aren't/don't).
            AllowFocusOnInteraction = false,
            OnClick = onClick,
            OnRealized = onRealized,
            Children =
            [
                new TextEl(glyph)
                {
                    Size = GlyphSize, FontFamily = Theme.IconFont,
                    Color = active ? style.Foreground : style.InactiveForeground,
                    HoverColor = style.HoverForeground,
                    PressedColor = style.PressedForeground,
                },
            ],
        };
}
