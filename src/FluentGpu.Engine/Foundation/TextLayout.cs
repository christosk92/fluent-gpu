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
