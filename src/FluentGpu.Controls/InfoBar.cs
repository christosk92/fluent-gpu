using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>The status level of an <see cref="InfoBar"/>, which selects its icon glyph and accent tint.</summary>
public enum InfoBarSeverity : byte { Informational = 0, Success = 1, Warning = 2, Error = 3 }

/// <summary>
/// A WinUI InfoBar: an inline, dismissable status surface. A 20x20 severity icon (accent-colored glyph), a growing
/// title + message content column, and an optional close button. The background is a subtle tint of the severity
/// accent (alpha 0x14) over the card, with a card stroke. Controlled: <paramref name="isOpen"/> false collapses it.
/// </summary>
public static class InfoBar
{
    // Severity glyphs from Segoe Fluent Icons (the Info/Warning/Error symbols are not in the shared Icons table).
    private const string GlyphInfo = "";    // Info
    private const string GlyphWarning = ""; // Warning
    private const string GlyphError = "";   // ErrorBadge

    public static Element Create(InfoBarSeverity severity, string title, string message, Action? onClose = null, bool isOpen = true)
    {
        if (!isOpen)
            return new BoxEl { };

        // Severity -> glyph + accent color.
        string glyph;
        ColorF accent;
        byte r, g, b;
        switch (severity)
        {
            case InfoBarSeverity.Success:
                glyph = Icons.Accept; r = 0x6C; g = 0xCB; b = 0x5F; accent = ColorF.FromRgba(r, g, b); break;
            case InfoBarSeverity.Warning:
                glyph = GlyphWarning; r = 0xFC; g = 0xE1; b = 0x00; accent = ColorF.FromRgba(r, g, b); break;
            case InfoBarSeverity.Error:
                glyph = GlyphError; r = 0xFF; g = 0x99; b = 0xA4; accent = ColorF.FromRgba(r, g, b); break;
            case InfoBarSeverity.Informational:
            default:
                glyph = GlyphInfo; accent = Tok.AccentDefault; r = 0x60; g = 0xCD; b = 0xFF; break;
        }

        // Subtle tint of the severity rgb over the card (alpha 0x14).
        ColorF tint = severity == InfoBarSeverity.Informational
            ? ColorF.FromRgba(0x60, 0xCD, 0xFF, 0x14)
            : ColorF.FromRgba(r, g, b, 0x14);

        var icon = new BoxEl
        {
            Width = 20f,
            Height = 20f,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children = [new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = accent }],
        };

        var content = new BoxEl
        {
            Direction = 0,
            Grow = 1f,
            Gap = 8f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                new TextEl(title) { Size = 14f, Bold = true, Color = Tok.TextPrimary },
                new TextEl(message) { Size = 14f, Color = Tok.TextPrimary },
            ],
        };

        var children = new List<Element> { icon, content };

        if (onClose is not null)
        {
            children.Add(new BoxEl
            {
                Width = 30f,
                Height = 30f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                HoverFill = Tok.FillSubtleSecondary,
                OnClick = onClose,
                Children = [new TextEl(Icons.Cancel) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary }],
            });
        }

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 12f,
            MinHeight = 48f,
            Padding = new Edges4(16f, 8f, 12f, 8f),
            Corners = Radii.OverlayAll,
            Fill = tint,
            BorderWidth = 1f,
            BorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.InfoBar,
            Children = children.ToArray(),
        };
    }
}
