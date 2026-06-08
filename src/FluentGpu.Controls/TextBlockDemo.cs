using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>
/// WinUI <c>TextBlock</c> equivalents: read-only styled text helpers mirroring the
/// WinUI type ramp (Title / Subtitle / Body / BodyStrong / Caption). Display only — no input.
/// </summary>
public static class TextBlocks
{
    /// <summary>TitleTextBlockStyle — 28px, bold, primary text.</summary>
    public static TextEl Title(string s) => new(s) { Size = 28f, Bold = true, Color = Tok.TextPrimary };

    /// <summary>SubtitleTextBlockStyle — 20px, regular, primary text.</summary>
    public static TextEl Subtitle(string s) => new(s) { Size = 20f, Bold = false, Color = Tok.TextPrimary };

    /// <summary>BodyTextBlockStyle — 14px, regular, primary text.</summary>
    public static TextEl Body(string s) => new(s) { Size = 14f, Color = Tok.TextPrimary };

    /// <summary>BodyStrongTextBlockStyle — 14px, bold, primary text.</summary>
    public static TextEl BodyStrong(string s) => new(s) { Size = 14f, Bold = true, Color = Tok.TextPrimary };

    /// <summary>CaptionTextBlockStyle — 12px, regular, secondary text.</summary>
    public static TextEl Caption(string s) => new(s) { Size = 12f, Color = Tok.TextSecondary };
}
