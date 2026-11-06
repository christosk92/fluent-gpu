namespace FluentGpu.Foundation;

/// <summary>
/// The font WEIGHT WORD the text pipeline carries everywhere a "numeric weight" travels (<c>TextStyle.Weight</c>,
/// <c>SpanStyle.Weight</c>, the registry's face key, the glyph-atlas key, the headless metrics): the DWrite numeric
/// weight (1–999) in the low ten bits plus the STYLE AXIS in bit 10 (<see cref="ItalicBit"/>). Carrying the axis in
/// the same word keeps every POD seam, cache key and column unchanged — a face keyed by (family, word) is a distinct
/// face per style, which is exactly the invalidation an italic needs — and the two leaves that talk to the OS
/// (<c>DWriteFontFaceSource.CreateSystemFace</c>, the fallback <c>MapCharacters</c> call) decode it into
/// <c>DWRITE_FONT_STYLE</c>. text.md §4 ("face resolution") owns the rule.
/// </summary>
public static class FontWeightWord
{
    /// <summary>Bit 10: the face is the family's italic (DWRITE_FONT_STYLE_ITALIC; nearest match, so a family without
    /// an italic face renders upright — no synthetic slant for system faces).</summary>
    public const int ItalicBit = 1 << 10;
    /// <summary>The low ten bits: the numeric weight.</summary>
    public const int WeightMask = 0x3FF;

    public static int Weight(int word) => word & WeightMask;
    public static bool IsItalic(int word) => (word & ItalicBit) != 0;
    /// <summary>Compose a word from a numeric weight (0 = unset stays 0 unless italic) and the style axis.</summary>
    public static ushort Make(int weight, bool italic) => (ushort)((weight & WeightMask) | (italic ? ItalicBit : 0));
}
