using FluentGpu.Dsl;

namespace FluentGpu.Theming;

/// <summary>
/// A complete theme: every table the engine reads at paint or reconcile time, published as ONE immutable value.
/// <c>Tok.Use(definition, kind)</c> swaps the whole thing with a single pointer write plus one epoch bump, so colours,
/// spacing, radii, type ramp, elevation, motion and fonts change in the same publication (docs/design/subsystems/theming.md).
/// <para>Colours are the per-kind <see cref="TokenSet"/>s and <see cref="ShellPalette"/>s plus the <see cref="Accent"/>
/// ramp and the <see cref="Ext"/> table; everything else is a non-colour table. <see cref="DiffersOnlyInColours"/> tells
/// the two kinds of change apart, which is what lets a colour-only change keep the live cross-fade path while any other
/// change is applied as a unit.</para>
/// <para>Build one with <c>ThemeDefinition.FluentDefault with { ... }</c> (the engine's default, bit-for-bit what
/// the engine shipped before definitions existed) or from scratch with every <c>required</c> member.</para>
/// </summary>
public sealed record ThemeDefinition
{
    /// <summary>Stable identifier (a preset id such as <c>"warm"</c>, or the application's own theme id).</summary>
    public required string Id { get; init; }

    public required TokenSet Light { get; init; }
    public required TokenSet Dark { get; init; }
    /// <summary>High-contrast token set. Reserved: <see cref="ThemeKind"/> has no high-contrast kind yet, so nothing
    /// reads this member today; it is carried (and compared) so a definition can already declare it.</summary>
    public TokenSet? HighContrast { get; init; }

    public required ShellPalette LightShell { get; init; }
    public required ShellPalette DarkShell { get; init; }

    /// <summary>The accent ramp. When it equals <see cref="AccentRamp.FluentDefault"/> the accent tokens are the baked
    /// per-kind values of <see cref="Light"/>/<see cref="Dark"/> (the engine default); any other ramp drives every accent
    /// token theme-aware, exactly like <c>Tok.SetAccent(in AccentRamp)</c>. A <c>Tok.SetAccent</c> override still wins
    /// over this member.</summary>
    public required AccentRamp Accent { get; init; }

    public required TypeRamp Type { get; init; }
    public required SpacingScale Spacing { get; init; }
    public required RadiiScale Radii { get; init; }
    public required ElevationSet Elevation { get; init; }
    public required MotionTable Motion { get; init; }
    public required FontSet Fonts { get; init; }
    public required MaterialPolicy Materials { get; init; }
    public required IconSet Icons { get; init; }

    /// <summary>Theme-defined extra colour tokens.</summary>
    public ExtTokenTable Ext { get; init; } = ExtTokenTable.Empty;

    /// <summary>Per-control style table (<see cref="ControlStyleTable"/>): control kind to the provider of its default style. Read by
    /// each control at render (<c>Tok.Definition.Controls</c>); an absent entry is the control's built-in Fluent look, so
    /// <see cref="ControlStyleTable.FluentDefault"/> (the empty table) reproduces the shipped looks. Replaces the old
    /// global <c>Button.StyleHook</c>.</summary>
    public required ControlStyleTable Controls { get; init; }

    /// <summary>Application-owned role table: colours AND non-colour roles (row heights, corner choices, weights) the engine
    /// knows nothing about. It is the source of <c>ViewContext.App&lt;T&gt;()</c> (cached per <c>Tok.Epoch</c>). It must implement
    /// <see cref="IAppRoles"/>, whose <see cref="IAppRoles.DiffersOnlyInColours"/> is what <see cref="DiffersOnlyInColours"/> asks
    /// (reference equality is the shortcut, never the verdict for two different tables).</summary>
    public IAppRoles? AppRoles { get; init; }

    /// <summary>
    /// The engine default: the Warm preset's colours with every non-colour table at the value the engine shipped before
    /// theme definitions existed (Fluent 4px spacing grid, 4/8 radii, the WinUI type ramp, ThemeShadow-class elevation,
    /// the MotionTok recipes, Segoe UI / Segoe Fluent Icons). It is the same instance as <c>Tok.WarmPalette</c>.
    /// </summary>
    public static ThemeDefinition FluentDefault { get; } = PaletteBuilder.Build(PaletteBuilder.Warm);

    /// <summary>
    /// True when <paramref name="other"/> differs from this definition only in colours: the <see cref="Light"/>,
    /// <see cref="Dark"/>, <see cref="HighContrast"/>, <see cref="LightShell"/>, <see cref="DarkShell"/>, <see cref="Accent"/>
    /// and <see cref="Ext"/> members and the <see cref="Id"/>. Every other member (type ramp, spacing, radii, elevation,
    /// motion, fonts, materials, icons, control styles) must be equal, and the app roles must differ only in colours as their own
    /// <see cref="IAppRoles.DiffersOnlyInColours"/> reports (a missing table against a present one is a difference). Two equal definitions return true. Used
    /// to choose the live cross-fade path (colour-only) over an atomic transaction (anything else).
    /// </summary>
    public bool DiffersOnlyInColours(ThemeDefinition other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(this, other) || HasSameStyleAs(other);
    }

    /// <summary>True when every non-colour member equals <paramref name="other"/>'s. Allocation-free.</summary>
    internal bool HasSameStyleAs(ThemeDefinition other)
        => Type.Equals(other.Type)
        && Spacing.Equals(other.Spacing)
        && Radii.Equals(other.Radii)
        && Elevation.Equals(other.Elevation)
        && Motion.Equals(other.Motion)
        && Fonts.Equals(other.Fonts)
        && Materials.Equals(other.Materials)
        && Icons.Equals(other.Icons)
        && Controls.Equals(other.Controls)
        && AppRolesSameStyle(AppRoles, other.AppRoles);

    // AppRoles holds colours and non-colour roles, so only the table itself can say whether a difference is colour-only.
    private static bool AppRolesSameStyle(IAppRoles? a, IAppRoles? b)
        => ReferenceEquals(a, b) || (a is not null && b is not null && a.DiffersOnlyInColours(b));
}
