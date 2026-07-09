using FluentGpu.Foundation;
using FluentGpu.Signals;

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
    internal static readonly Prop<ColorF> PrimaryTextBrush = Prop.Of(static () => Tok.TextPrimary);
    internal static readonly Prop<ColorF> SecondaryTextBrush = Prop.Of(static () => Tok.TextSecondary);
    internal static readonly Prop<ColorF> TertiaryTextBrush = Prop.Of(static () => Tok.TextTertiary);
    internal static readonly Prop<ColorF> DisabledTextBrush = Prop.Of(static () => Tok.TextDisabled);
    internal static readonly Prop<ColorF> AccentTextBrush = Prop.Of(static () => Tok.AccentDefault);
    internal static readonly Prop<ColorF> OnAccentTextBrush = Prop.Of(static () => Tok.TextOnAccentPrimary);

    /// <summary>True IFF <paramref name="p"/> is one of the shared theme-text brushes above — a bound Prop whose payload
    /// is the singleton thunk instance (each <c>static</c> lambda is compiler-cached, so identity is stable). The
    /// reconciler's recycle path treats these as recyclable-safe: the persisted mount-time color binding re-fires on
    /// <c>RethemeAll</c> for the SAME thunk, so a recycled node keeps live-theme correctness with NO column rewrite. A
    /// bound text color that is NOT one of these is caller-owned identity and stays non-recyclable (mounts fresh).</summary>
    internal static bool IsThemeTextBrush(in Prop<ColorF> p)
    {
        var th = p.Thunk;
        return th is not null && (
            ReferenceEquals(th, PrimaryTextBrush.Thunk) || ReferenceEquals(th, SecondaryTextBrush.Thunk)
            || ReferenceEquals(th, TertiaryTextBrush.Thunk) || ReferenceEquals(th, DisabledTextBrush.Thunk)
            || ReferenceEquals(th, AccentTextBrush.Thunk) || ReferenceEquals(th, OnAccentTextBrush.Thunk));
    }

    public static TextEl Caption(string t) => new(t) { Size = 12f, LineHeight = 16f, Color = SecondaryTextBrush };
    public static TextEl Body(string t) => new(t) { Size = 14f, LineHeight = 20f, Color = PrimaryTextBrush, Wrap = TextWrap.Wrap };
    public static TextEl BodyStrong(string t) => new(t) { Size = 14f, LineHeight = 20f, Weight = 600, Color = PrimaryTextBrush };
    public static TextEl BodyLarge(string t) => new(t) { Size = 18f, LineHeight = 24f, Color = PrimaryTextBrush, Wrap = TextWrap.Wrap };
    public static TextEl Subtitle(string t) => new(t) { Size = 20f, LineHeight = 28f, Weight = 600, Color = PrimaryTextBrush };
    public static TextEl Title(string t) => new(t) { Size = 28f, LineHeight = 36f, Weight = 600, Color = PrimaryTextBrush };
    public static TextEl TitleLarge(string t) => new(t) { Size = 40f, LineHeight = 52f, Weight = 600, Color = PrimaryTextBrush };
    public static TextEl Display(string t) => new(t) { Size = 68f, LineHeight = 92f, Weight = 600, Color = PrimaryTextBrush };
}

/// <summary>Color-tier modifiers for any TextEl: <c>Ui.Body("x").Secondary()</c>.</summary>
public static class TextTiers
{
    public static TextEl Primary(this TextEl t) => t with { Color = Ui.PrimaryTextBrush };
    public static TextEl Secondary(this TextEl t) => t with { Color = Ui.SecondaryTextBrush };
    public static TextEl Tertiary(this TextEl t) => t with { Color = Ui.TertiaryTextBrush };
    public static TextEl Disabled(this TextEl t) => t with { Color = Ui.DisabledTextBrush };
    public static TextEl Accent(this TextEl t) => t with { Color = Ui.AccentTextBrush };
    public static TextEl OnAccent(this TextEl t) => t with { Color = Ui.OnAccentTextBrush };
}
