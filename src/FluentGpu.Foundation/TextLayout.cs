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
