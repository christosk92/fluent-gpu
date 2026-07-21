using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>The per-severity computed visuals shared by <see cref="InfoBar"/> and <see cref="Toast"/> so their severity
/// mapping CANNOT drift: the standard status glyph, the icon-background fill (SystemFillColor*Brush), the inverse glyph
/// foreground, and the tinted content-root background (SystemFillColor*BackgroundBrush). Extracted from InfoBar's
/// TemplateSettings (WinUI InfoBar_themeresources.xaml:70-74 glyphs + the SystemFill* ramps). A pure per-severity
/// factory — no per-frame allocation.</summary>
internal readonly record struct SeverityVisual(string Glyph, ColorF IconBackground, ColorF IconForeground, ColorF Background);

internal static class SeverityVisuals
{
    // WinUI InfoBar_themeresources.xaml:70-74 standard icon glyphs (Segoe Fluent Icons), single-sourced from Icons.cs.
    internal const string BackgroundGlyph = Icons.InfoBarBackgroundCircle;   // F136 (filled circle)

    /// <summary>The severity → visuals mapping. Informational follows the OS accent (SystemFillColorAttention);
    /// IconForeground is always TextFillColorInverse; the background is the SystemFillColor*Background tint.</summary>
    internal static SeverityVisual For(InfoBarSeverity severity) => severity switch
    {
        InfoBarSeverity.Success => new(Icons.StatusSuccess, Tok.SystemFillSuccess,  Tok.TextInverse, Tok.SystemFillSuccessBackground),
        InfoBarSeverity.Warning => new(Icons.StatusWarning, Tok.SystemFillCaution,  Tok.TextInverse, Tok.SystemFillCautionBackground),
        InfoBarSeverity.Error   => new(Icons.StatusError,   Tok.SystemFillCritical, Tok.TextInverse, Tok.SystemFillCriticalBackground),
        _                       => new(Icons.StatusInfo,    Tok.SystemFillAttention,Tok.TextInverse, Tok.SystemFillAttentionBackground),
    };
}
