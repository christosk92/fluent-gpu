namespace FluentGpu.Foundation;

/// <summary>How a text run breaks across lines (mirrors WinUI TextWrapping).</summary>
public enum TextWrap : byte
{
    NoWrap = 0,        // single line; overflows (or trims) at the box width
    Wrap = 1,          // break at word boundaries; break inside a word only if a single word exceeds the width
    WrapWholeWords = 2 // break only at word boundaries; never split a word (it may overflow)
}

/// <summary>How overflowing text is truncated (mirrors WinUI TextTrimming). Applies to the last visible line.</summary>
public enum TextTrim : byte
{
    None = 0,              // no trimming (overflows)
    Clip = 1,              // hard-clip at the box edge
    CharacterEllipsis = 2, // drop characters and append "…"
    WordEllipsis = 3       // drop whole words and append "…"
}

/// <summary>How an explicit LineHeight interacts with the font-natural line box (mirrors WinUI LineStackingStrategy;
/// BaselineToBaseline is not modeled). MaxHeight — the WinUI BaseTextBlockStyle default
/// (TextBlock_themeresources.xaml:16) — makes the line advance max(font-natural, LineHeight); BlockLineHeight makes
/// the line advance exactly LineHeight (lines may visually overlap when smaller than natural).</summary>
public enum LineStacking : byte
{
    MaxHeight = 0,
    BlockLineHeight = 1
}

/// <summary>Which vertical band a text line reports as its box (mirrors WinUI TextLineBounds, minimum set —
/// TrimToCapHeight/TrimToBaseline individually are not modeled). Full — the WinUI BaseTextBlockStyle default
/// (TextBlock_themeresources.xaml:17) — is the font-natural ascent..descent box; Tight trims the box to
/// cap-height..baseline so vertical centering is optical (e.g. PersonPicture initials, PersonPicture.xaml:66).</summary>
public enum TextLineBounds : byte
{
    Full = 0,
    Tight = 1
}

/// <summary>Case transform applied to a text run AT SHAPING (E19), culture-invariantly: the stored string is never rewritten
/// and nothing is allocated per render. <see cref="Lower"/> / <see cref="Upper"/> are the simple Unicode case mappings of
/// the invariant culture (a Turkish current culture never turns <c>i</c> into <c>İ</c>), UTF-16 length preserving, so every
/// index (caret, selection, span range) is the same in the written and the shaped text. Wavee feeds it from theme-written
/// labels only; content strings keep <see cref="AsWritten"/>.</summary>
public enum TextCase : byte
{
    AsWritten = 0,
    Lower = 1,
    Upper = 2
}

/// <summary>Horizontal alignment of each laid line inside the text box (WinUI <c>TextBlock.TextAlignment</c>, CSS
/// <c>text-align</c>). Applied per line once widths are known: the line's width EXCLUDES trailing hanging whitespace,
/// and a line wider than its box (no-wrap overflow) is start-aligned — it never shifts left past the box start.
/// No paragraph direction is modeled yet, so Start = left and End = right even for RTL text. Justify widens the
/// inter-word whitespace of every line except the last and hard-broken ones (which stay Start).</summary>
public enum TextAlign : byte
{
    Start = 0,
    Center = 1,
    End = 2,
    Justify = 3
}

/// <summary>OpenType features applied when shaping a run (CSS <c>font-feature-settings</c> subset). Each flag turns one
/// feature ON for the whole run; the shaper passes the same feature list to both glyph mapping and placement. A font
/// without the feature ignores it (OpenType semantics — no fallback, no error). <see cref="TabularFigures"/> is the
/// usual fix for numbers that jitter as they tick (every digit gets the same advance).</summary>
[Flags]
public enum FontFeatures : uint
{
    None = 0,
    /// <summary><c>tnum</c> — equal-width digits.</summary>
    TabularFigures = 1u << 0,
    /// <summary><c>lnum</c> — cap-height digits.</summary>
    LiningFigures = 1u << 1,
    /// <summary><c>onum</c> — x-height digits with ascenders/descenders.</summary>
    OldStyleFigures = 1u << 2,
    /// <summary><c>zero</c> — a zero with a slash (tells 0 from O).</summary>
    SlashedZero = 1u << 3,
    /// <summary><c>ss01</c>..<c>ss20</c> — the font's stylistic sets.</summary>
    StylisticSet01 = 1u << 8, StylisticSet02 = 1u << 9, StylisticSet03 = 1u << 10, StylisticSet04 = 1u << 11,
    StylisticSet05 = 1u << 12, StylisticSet06 = 1u << 13, StylisticSet07 = 1u << 14, StylisticSet08 = 1u << 15,
    StylisticSet09 = 1u << 16, StylisticSet10 = 1u << 17, StylisticSet11 = 1u << 18, StylisticSet12 = 1u << 19,
    StylisticSet13 = 1u << 20, StylisticSet14 = 1u << 21, StylisticSet15 = 1u << 22, StylisticSet16 = 1u << 23,
    StylisticSet17 = 1u << 24, StylisticSet18 = 1u << 25, StylisticSet19 = 1u << 26, StylisticSet20 = 1u << 27,
}
