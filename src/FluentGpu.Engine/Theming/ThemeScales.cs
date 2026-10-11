namespace FluentGpu.Theming;

/// <summary>
/// The spacing rhythm of a <see cref="ThemeDefinition"/>: the raw scale (XXS..XXXL, Gutter) plus the semantic names (page
/// gutters, card spacing, internal padding, stack gaps). Read through <see cref="FluentGpu.Dsl.Spacing"/>
/// (<c>Spacing.M</c>), which resolves against <see cref="FluentGpu.Dsl.Tok.Definition"/> on every read, so a definition swap
/// re-lays-out in the same publication. Every default below is the Fluent 4px-grid value the engine shipped as a
/// <c>const</c> before E3; the semantic names are independent members (a theme sets them explicitly rather than
/// inheriting a change of the raw rung they used to alias).
/// </summary>
public sealed record SpacingScale
{
    public float XXS { get; init; } = 2f;
    public float XS { get; init; } = 4f;
    public float S { get; init; } = 8f;
    public float M { get; init; } = 12f;
    public float L { get; init; } = 16f;
    public float XL { get; init; } = 20f;
    public float XXL { get; init; } = 24f;
    public float XXXL { get; init; } = 32f;
    /// <summary>Section / content gutter.</summary>
    public float Gutter { get; init; } = 24f;
    /// <summary>Desktop page gutter (NavigationView content margin) - off-grid by WinUI design.</summary>
    public float PageWide { get; init; } = 36f;
    /// <summary>Narrow-layout page gutter.</summary>
    public float PageNarrow { get; init; } = 16f;
    /// <summary>Between cards in a grid.</summary>
    public float Card { get; init; } = 12f;
    /// <summary>Card / panel internal padding.</summary>
    public float Inner { get; init; } = 16f;
    public float StackS { get; init; } = 4f;
    public float StackM { get; init; } = 8f;
    public float StackL { get; init; } = 12f;

    /// <summary>The Fluent default scale (today's engine values).</summary>
    public static SpacingScale FluentDefault { get; } = new();
}

/// <summary>
/// The corner-radius ramp of a <see cref="ThemeDefinition"/> (ControlCornerRadius / OverlayCornerRadius + the pill, plus
/// the none / card / full rungs). Read through <see cref="FluentGpu.Dsl.Radii"/> (<c>Radii.Control</c>,
/// <c>Radii.ControlAll</c>), resolved against the active definition on every read. Defaults are the Fluent values.
/// </summary>
public sealed record RadiiScale
{
    /// <summary>Square corners.</summary>
    public float None { get; init; } = 0f;
    /// <summary>ControlCornerRadius: buttons, inputs, small controls.</summary>
    public float Control { get; init; } = 4f;
    /// <summary>Surface / media card.</summary>
    public float Card { get; init; } = 8f;
    /// <summary>OverlayCornerRadius: cards, flyouts, dialogs, expanders.</summary>
    public float Overlay { get; init; } = 8f;
    /// <summary>SelectorBar / pill / badge.</summary>
    public float Pill { get; init; } = 16f;
    /// <summary>Fully rounded (circle / capsule); clamped to half the box at record time.</summary>
    public float Full { get; init; } = 999f;

    /// <summary>The Fluent default ramp (today's engine values).</summary>
    public static RadiiScale FluentDefault { get; } = new();
}

/// <summary>One rung of the type ramp: font size and line height in DIPs, and the numeric font weight (0 = unset, which
/// the text pipeline resolves to Normal 400).</summary>
public readonly record struct TypeStyle(float Size, float LineHeight, ushort Weight = 0);

/// <summary>
/// The type ramp of a <see cref="ThemeDefinition"/>: the eight styled-text rungs behind <c>Ui.Caption</c> ..
/// <c>Ui.Display</c>. Defaults are the WinUI TextBlock styles the engine shipped (sizes 12/14/18/20/28/40/68, line
/// heights 16/20/24/28/36/52/92; Caption, Body and BodyLarge are Normal, BodyStrong and everything from Subtitle up is
/// SemiBold 600).
/// </summary>
public sealed record TypeRamp
{
    public TypeStyle Caption { get; init; } = new(12f, 16f);
    public TypeStyle Body { get; init; } = new(14f, 20f);
    public TypeStyle BodyStrong { get; init; } = new(14f, 20f, 600);
    public TypeStyle BodyLarge { get; init; } = new(18f, 24f);
    public TypeStyle Subtitle { get; init; } = new(20f, 28f, 600);
    public TypeStyle Title { get; init; } = new(28f, 36f, 600);
    public TypeStyle TitleLarge { get; init; } = new(40f, 52f, 600);
    public TypeStyle Display { get; init; } = new(68f, 92f, 600);

    /// <summary>The Fluent default ramp (today's engine values).</summary>
    public static TypeRamp FluentDefault { get; } = new();
}
