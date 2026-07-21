using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// A control's density on the ORTHOGONAL size axis — independent of appearance/variant (Radix Themes/CVA precedent:
/// axes compose, they are not a flattened product). Shared kit-wide: the button family adopts it here, and the rest of
/// the control kit adopts it over time. A control MAY refuse or remap a size value it can't honor sensibly (its
/// <c>ClampSize</c> seam — e.g. IconButton clamps Large so its square glyph box stays sane, mirroring Radix's
/// radius=full-refused-on-Checkbox).
/// </summary>
public enum ControlSize : byte
{
    /// <summary>WinUI "Compact" density.</summary>
    Small,
    /// <summary>The default — pixel-identical to the pre-axis control metrics.</summary>
    Medium,
    /// <summary>A documented Fluent-2 extension (WinUI has no built-in Large button).</summary>
    Large,
}

/// <summary>
/// The size-axis metric bundle — the geometry a control derives from <see cref="ControlSize"/>, selected by ONE 3-arm
/// switch (<see cref="For"/>). Orthogonal to the appearance/variant axis (which selects colors). Composed into a
/// control's full style record; the record survives as the full-override escape hatch.
/// Provenance:
///  • Medium = today's exact button metrics — WinUI DefaultButtonStyle (ButtonPadding 11,5,11,6 / ControlCornerRadius 4
///    / ControlContentThemeFontSize 14, Button_themeresources.xaml:152/168/165); MinHeight 32 = effective WinUI height
///    (padding + 14px line). Icon 16 = WinUI AppBar/NavigationViewItem glyph box (AppBarButton_themeresources.xaml:34).
///  • Small = WinUI Compact deltas (ButtonPadding 7,2,7,3; ControlContentThemeFontSize 12); MinHeight 24. Icon 14.
///  • Large = a DOCUMENTED Fluent-2 extension (no WinUI source): MinHeight 40, Padding 15,9,15,10, Font 14, Icon 20 —
///    the padding scaled off the Medium ratio, font held at 14 (WinUI never bumps ControlContentThemeFontSize past 14).
/// </summary>
public readonly record struct ControlMetrics(Edges4 Padding, float MinHeight, float FontSize, float CornerRadius, float IconSize)
{
    /// <summary>The ONE 3-arm size switch. Every adopting control resolves its geometry through this — no per-size copies.</summary>
    public static ControlMetrics For(ControlSize size) => size switch
    {
        // Small — WinUI Compact deltas (ButtonPadding 7,2,7,3; Font 12).
        ControlSize.Small => new ControlMetrics(new Edges4(7, 2, 7, 3), MinHeight: 24f, FontSize: 12f, CornerRadius: Radii.Control, IconSize: 14f),
        // Large — documented Fluent-2 extension.
        ControlSize.Large => new ControlMetrics(new Edges4(15, 9, 15, 10), MinHeight: 40f, FontSize: 14f, CornerRadius: Radii.Control, IconSize: 20f),
        // Medium — WinUI DefaultButtonStyle (the pre-axis defaults; pixel-identical).
        _ => new ControlMetrics(new Edges4(11, 5, 11, 6), MinHeight: 32f, FontSize: 14f, CornerRadius: Radii.Control, IconSize: 16f),
    };
}
