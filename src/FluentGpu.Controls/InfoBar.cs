using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>The status level of an <see cref="InfoBar"/>, which selects its icon glyph and severity color.</summary>
public enum InfoBarSeverity : byte { Informational = 0, Success = 1, Warning = 2, Error = 3 }

/// <summary>
/// A WinUI InfoBar: an inline, dismissable status surface. A filled, colored ~16px severity disc with an inverse-colored
/// glyph, a growing Title (above) + Message (below) content column, and an optional close button. The background is a
/// subtle tint of the severity color over the card, with a card stroke. Controlled: <paramref name="isOpen"/> false
/// collapses it.
/// </summary>
public static class InfoBar
{
    // Severity glyphs from Segoe Fluent Icons (the InfoBar StandardIcon foreground glyphs).
    private const string GlyphInfo = "\uF13F";    // InfoBarInformationalIconGlyph
    private const string GlyphSuccess = "\uF13E";    // InfoBarSuccessIconGlyph
    private const string GlyphWarning = "\uF13C";    // InfoBarWarningIconGlyph
    private const string GlyphError = "\uF13D";    // InfoBarErrorIconGlyph

    /// <param name="isClosable">When true (and <paramref name="onClose"/> is supplied) the trailing close button is shown.</param>
    /// <param name="isOpen">Whether the bar is shown. WinUI's IsOpen property defaults to <c>false</c>; this stateless
    /// factory defaults to <c>true</c> so a constructed bar renders (callers gate visibility with their own state).</param>
    public static Element Create(InfoBarSeverity severity, string title, string message, Action? onClose = null, bool isOpen = true, bool isClosable = true)
    {
        if (!isOpen)
            return new BoxEl { };

        // Severity -> StandardIcon glyph + severity disc color (SystemFillColor*) + tinted background (SystemFillColor*Background).
        // Informational follows the OS accent (WinUI SystemFillColorAttention == SystemAccentColor).
        string glyph;
        ColorF disc;
        ColorF tint;
        switch (severity)
        {
            case InfoBarSeverity.Success:
                glyph = GlyphSuccess; disc = Tok.SystemFillSuccess; tint = Tok.SystemFillSuccessBackground; break;
            case InfoBarSeverity.Warning:
                glyph = GlyphWarning; disc = Tok.SystemFillCaution; tint = Tok.SystemFillCautionBackground; break;
            case InfoBarSeverity.Error:
                glyph = GlyphError; disc = Tok.SystemFillCritical; tint = Tok.SystemFillCriticalBackground; break;
            case InfoBarSeverity.Informational:
            default:
                glyph = GlyphInfo; disc = Tok.SystemFillAttention; tint = Tok.SystemFillAttentionBackground; break;
        }

        // WinUI uses TextFillColorInverse for the foreground glyph on every severity disc.
        var icon = new BoxEl
        {
            Width = 16f,
            Height = 16f,
            Corners = Radii.Circle(16f),
            Fill = disc,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children = [new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = Tok.TextInverse }],
        };

        // WinUI: title is SemiBold (InfoBarTitleFontWeight), message is regular. TextEl only exposes Bold, the
        // closest available weight to SemiBold. Panel right margin (0,0,16,0) holds the message off the close button.
        var content = new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Gap = 2f,
            Margin = new Edges4(0f, 0f, 16f, 0f),
            Children =
            [
                new TextEl(title) { Size = 14f, Bold = true, Color = Tok.TextPrimary },
                new TextEl(message) { Size = 14f, Color = Tok.TextPrimary },
            ],
        };

        var children = new List<Element> { icon, content };

        if (onClose is not null && isClosable)
        {
            children.Add(new BoxEl
            {
                Width = 38f,
                Height = 38f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                HoverFill = Tok.FillSubtleSecondary,
                OnClick = onClose,
                Children = [new TextEl(Icons.Cancel) { Size = 16f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary }],
            });
        }

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 14f,   // InfoBarIconMargin right (icon→content spacing)
            MinHeight = 48f,
            Padding = new Edges4(16f, 0f, 0f, 0f),   // InfoBarContentRootPadding 16,0,0,0
            Corners = Radii.ControlAll,
            Fill = tint,
            BorderWidth = 1f,
            BorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.InfoBar,
            Children = children.ToArray(),
        };
    }
}
