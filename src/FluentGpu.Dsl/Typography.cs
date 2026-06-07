using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>
/// The WinUI type ramp as styled-<see cref="TextEl"/> factories on <see cref="Ui"/> — sizes/weights match the Fluent
/// TextBlock styles (CaptionTextBlockStyle … DisplayTextBlockStyle). Default color is <see cref="Tok.TextPrimary"/>;
/// chain the tier helpers in <see cref="TextTiers"/> for secondary/tertiary/accent. (TextEl.Bold is one bit — WinUI
/// "SemiBold" maps to Bold until the Text subsystem exposes a numeric weight.)
/// </summary>
public static partial class Ui
{
    public static TextEl Caption(string t) => new(t) { Size = 12f, Color = Tok.TextSecondary };
    public static TextEl Body(string t) => new(t) { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap };
    public static TextEl BodyStrong(string t) => new(t) { Size = 14f, Bold = true, Color = Tok.TextPrimary };
    public static TextEl BodyLarge(string t) => new(t) { Size = 18f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap };
    public static TextEl Subtitle(string t) => new(t) { Size = 20f, Bold = true, Color = Tok.TextPrimary };
    public static TextEl Title(string t) => new(t) { Size = 28f, Bold = true, Color = Tok.TextPrimary };
    public static TextEl TitleLarge(string t) => new(t) { Size = 40f, Bold = true, Color = Tok.TextPrimary };
    public static TextEl Display(string t) => new(t) { Size = 68f, Bold = true, Color = Tok.TextPrimary };
}

/// <summary>Color-tier modifiers for any TextEl: <c>Ui.Body("x").Secondary()</c>.</summary>
public static class TextTiers
{
    public static TextEl Primary(this TextEl t) => t with { Color = Tok.TextPrimary };
    public static TextEl Secondary(this TextEl t) => t with { Color = Tok.TextSecondary };
    public static TextEl Tertiary(this TextEl t) => t with { Color = Tok.TextTertiary };
    public static TextEl Disabled(this TextEl t) => t with { Color = Tok.TextDisabled };
    public static TextEl Accent(this TextEl t) => t with { Color = Tok.AccentDefault };
    public static TextEl OnAccent(this TextEl t) => t with { Color = Tok.TextOnAccentPrimary };
}
