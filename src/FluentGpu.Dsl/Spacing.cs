using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>The WinUI Gallery spacing rhythm (page gutters, card spacing, internal padding, stack gaps).</summary>
public static class Spacing
{
    public const float PageWide = 36f;   // desktop page gutter (NavigationView content margin)
    public const float PageNarrow = 16f; // narrow layout
    public const float Card = 12f;       // between cards in a grid
    public const float Inner = 16f;      // card / panel internal padding
    public const float StackS = 4f;
    public const float StackM = 8f;
    public const float StackL = 12f;

    public static readonly Edges4 PagePadWide = new(PageWide, 24f, PageWide, PageWide);
    public static readonly Edges4 PagePadNarrow = Edges4.All(PageNarrow);
    public static readonly Edges4 CardPad = Edges4.All(Inner);
}
