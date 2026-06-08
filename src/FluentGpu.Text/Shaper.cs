namespace FluentGpu.Text;

/// <summary>
/// One shaped glyph in logical→visual order: the POST-shaping glyph id (NOT a codepoint), its DIP advance, the
/// pen offset (DIP), and the first source text index it maps to (its cluster start, for wrap/hit-test/selection).
/// Produced by the shaper (text.md §4.4); consumed by the layout engine (wrap/measure) and the glyph rasterizer.
/// </summary>
public readonly record struct GlyphPlacement(ushort GlyphId, float Advance, float OffsetX, float OffsetY, int Cluster);
