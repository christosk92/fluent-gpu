using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>The spacing rhythm of the ACTIVE theme definition (<c>Tok.Definition.Spacing</c>): the raw scale (XXS…XXXL +
/// Gutter) plus the semantic names (page gutters, card spacing, internal padding, stack gaps). Every member is a static
/// PROPERTY that resolves against <see cref="Tok.Definition"/> at the moment it is read, so a definition swap
/// (<c>Tok.Use(definition, kind)</c>) re-lays-out everything built from it. They are therefore not constants: do not
/// read one into a <c>const</c>, a <c>static readonly</c> field, an attribute argument, a <c>case</c> label or an
/// optional-parameter default (the FGTH004 analyzer fences the field forms) — read it where the element is built.
/// The Fluent default values live in <see cref="FluentGpu.Theming.SpacingScale"/>.</summary>
public static class Spacing
{
    // ── The 4px-grid scale (DIPs). Use for gaps, padding, gutters.
    public static float XXS => Tok.Definition.Spacing.XXS;
    public static float XS => Tok.Definition.Spacing.XS;
    public static float S => Tok.Definition.Spacing.S;
    public static float M => Tok.Definition.Spacing.M;
    public static float L => Tok.Definition.Spacing.L;
    public static float XL => Tok.Definition.Spacing.XL;
    public static float XXL => Tok.Definition.Spacing.XXL;
    public static float XXXL => Tok.Definition.Spacing.XXXL;
    public static float Gutter => Tok.Definition.Spacing.Gutter;     // section / content gutter

    // ── Semantic names.
    public static float PageWide => Tok.Definition.Spacing.PageWide;       // desktop page gutter (NavigationView content margin)
    public static float PageNarrow => Tok.Definition.Spacing.PageNarrow;   // narrow layout
    public static float Card => Tok.Definition.Spacing.Card;               // between cards in a grid
    public static float Inner => Tok.Definition.Spacing.Inner;             // card / panel internal padding
    public static float StackS => Tok.Definition.Spacing.StackS;
    public static float StackM => Tok.Definition.Spacing.StackM;
    public static float StackL => Tok.Definition.Spacing.StackL;

    public static Edges4 PagePadWide
    {
        get { var s = Tok.Definition.Spacing; return new Edges4(s.PageWide, s.XXL, s.PageWide, s.PageWide); }
    }
    public static Edges4 PagePadNarrow => Edges4.All(Tok.Definition.Spacing.PageNarrow);
    public static Edges4 CardPad => Edges4.All(Tok.Definition.Spacing.Inner);
}
