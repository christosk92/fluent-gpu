using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>The Fluent spacing rhythm on the native 4px grid — the raw scale (XXS…XXXL + Gutter) plus the semantic
/// names (page gutters, card spacing, internal padding, stack gaps) re-pointed onto it. A superset of any app's
/// spacing layer (e.g. Wavee's WaveeSpace) so an app can drop its duplicate and read these const-for-const.</summary>
public static class Spacing
{
    // ── The 4px-grid scale (DIPs). Use for gaps, padding, gutters. Every value is a multiple of 4 (XXS the sole 2px step).
    public const float XXS = 2f;
    public const float XS = 4f;
    public const float S = 8f;
    public const float M = 12f;
    public const float L = 16f;
    public const float XL = 20f;
    public const float XXL = 24f;
    public const float XXXL = 32f;
    public const float Gutter = 24f;     // section / content gutter (== XXL)

    // ── Semantic names (retained), re-pointed onto the scale above.
    public const float PageWide = 36f;   // desktop page gutter (NavigationView content margin) — off-grid by WinUI design
    public const float PageNarrow = L;   // narrow layout (16)
    public const float Card = M;         // between cards in a grid (12)
    public const float Inner = L;        // card / panel internal padding (16)
    public const float StackS = XS;      // 4
    public const float StackM = S;       // 8
    public const float StackL = M;       // 12

    public static readonly Edges4 PagePadWide = new(PageWide, XXL, PageWide, PageWide);
    public static readonly Edges4 PagePadNarrow = Edges4.All(PageNarrow);
    public static readonly Edges4 CardPad = Edges4.All(Inner);
}
