using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>
/// The WinUI type ramp as styled-<see cref="TextEl"/> factories on <see cref="Ui"/> — sizes/weights match the Fluent
/// TextBlock styles (CaptionTextBlockStyle … DisplayTextBlockStyle, TextBlock_themeresources.xaml:3-51): font sizes
/// 12/14/18/20/28/40/68 (:3-9); BaseTextBlockStyle FontWeight=SemiBold 600 inherited by BodyStrong and everything
/// Subtitle-up (:13); Caption/Body/BodyLarge override back to Normal 400 (:21, :24, :28). LineHeight carries the
/// Fluent type-ramp spec figures 16/20/24/28/36/52/92 (WinUI gets them from the Segoe UI Variable font metrics — its
/// XAML sets no LineHeight; our engine's natural ascent+descent box is tighter, so the ramp pins them explicitly,
/// resolved via the default LineStackingStrategy=MaxHeight, :16). Default color is <see cref="Tok.TextPrimary"/>;
/// chain the tier helpers in <see cref="TextTiers"/> for secondary/tertiary/accent.
/// </summary>
public static partial class Ui
{
    public static TextEl Caption(string t) => new(t) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary };
    public static TextEl Body(string t) => new(t) { Size = 14f, LineHeight = 20f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap };
    public static TextEl BodyStrong(string t) => new(t) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary };
    public static TextEl BodyLarge(string t) => new(t) { Size = 18f, LineHeight = 24f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap };
    public static TextEl Subtitle(string t) => new(t) { Size = 20f, LineHeight = 28f, Weight = 600, Color = Tok.TextPrimary };
    public static TextEl Title(string t) => new(t) { Size = 28f, LineHeight = 36f, Weight = 600, Color = Tok.TextPrimary };
    public static TextEl TitleLarge(string t) => new(t) { Size = 40f, LineHeight = 52f, Weight = 600, Color = Tok.TextPrimary };
    public static TextEl Display(string t) => new(t) { Size = 68f, LineHeight = 92f, Weight = 600, Color = Tok.TextPrimary };
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
