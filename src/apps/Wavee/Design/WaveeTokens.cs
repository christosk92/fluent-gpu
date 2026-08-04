using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

// Wavee's geometry token layer. COLOR comes entirely from the engine's WinUI-faithful `Tok.*` (Dsl/Tokens.cs) and the
// spacing / rounding scales come from the engine's `Spacing.*` / `Radii.*` supersets — we do NOT duplicate either. This
// keeps only the fixed sizing scale Tok doesn't carry. The 4px grid is the native tell; every value here is a multiple of 4.

/// <summary>Fixed control / surface dimensions.</summary>
public static class WaveeSize
{
    public const float ControlH = 32, NavItemH = 44, TrackRowH = 56, PlayerBarH = 72;   // taller dock: room for the seek row
    public const float RailCard = 180, NavPaneW = 240, NavCompactW = 56;   // NavPaneW 240 = WinUI OpenPaneLength (flush, no inset gap)
    public const float ArtThumb = 40, ArtNowPlaying = 64, ArtPlayerBar = 48;
    // Detail-page left-rail widths (the shared playlist/album/single detail surface; liked is single-column → no rail).
    public const float RailAlbum = 280, RailPlaylist = 240;
}

/// <summary>The bottom player-bar dock geometry. Pages reserve this height so their last row clears the transport.</summary>
public static class PlayerDock
{
    public const float BarH = 72;
    public const float Margin = 0;
    public const float Reserve = BarH;
}

/// <summary>Wavee app shell colors. Derived from the active <see cref="Tok.Palette"/> shell ramp. The window itself is
/// transparent to DWM Mica and the MUX tabbed-window ladder is built on top of it: the TAB RAIL row stays unpainted so
/// bare Mica Alt is the frame; the app body sits on ONE translucent LayerOnMicaBaseAlt plate (toolbar, nav pane, player
/// dock, content-pane backing); the content pane is a LayerFillColorDefault step on that plate; and (light) the opaque
/// card is the step on the pane.</summary>
public static class WaveeColors
{
    /// <summary>One theme's shell surfaces (the values that aren't simply a plain engine token).</summary>
    public sealed record Palette(
        ColorF Toolbar, ColorF Sidebar, ColorF PlayerBar, ColorF FileArea, ColorF Content, ColorF ContentAlt,
        ColorF PremiumText,
        ColorF RowZebra, ColorF RowHover, ColorF RowHoverZebra, ColorF RowPressed, ColorF RowPressedZebra);

    static ShellPalette ActiveShell => Tok.Theme == ThemeKind.Light ? Tok.Palette.LightShell : Tok.Palette.DarkShell;

    static Palette Active => new(
        ActiveShell.Toolbar, ActiveShell.Sidebar, ActiveShell.PlayerBar,
        ActiveShell.FileArea, ActiveShell.Content, ActiveShell.ContentAlt,
        PremiumText: Tok.Theme == ThemeKind.Light ? Tok.SystemFillSuccess : ColorF.FromRgba(0x1D, 0xB9, 0x54),
        ActiveShell.RowZebra, ActiveShell.RowHover, ActiveShell.RowHoverZebra,
        ActiveShell.RowPressed, ActiveShell.RowPressedZebra);

    // Both themes are published AS-IS: the shell ramp carries the stock WinUI alphas directly (an unpainted tab rail
    // over real Mica + one LayerOnMicaBaseAlt body plate + one LayerFillColorDefault content pane on it), so the old
    // light-only alpha re-multiply would re-thin values that are already the stock ones.
    public static ColorF Toolbar => Active.Toolbar;
    public static ColorF Sidebar => Active.Sidebar;

    static ColorF MicaBase => Tok.Theme == ThemeKind.Light ? MicaRef.LightDefault : MicaRef.DarkDefault;

    /// <summary>Opaque equivalent of the app-body PLATE (rung 1) — for a floating pane that replaces a docked CHROME
    /// band (the narrow nav drawer). It must not be translucent: the page it covers would read through it.</summary>
    public static ColorF FloatingChrome => ColorContrast.Flatten(Active.Toolbar, MicaBase);

    /// <summary>Opaque equivalent of the CONTENT PANE (rung 2 = pane on plate) — for a floating pane that replaces a
    /// docked CONTENT surface (the non-docked right rail). It must be flattened through BOTH rungs, or the floating rail
    /// desyncs from the docked rail it stands in for.</summary>
    public static ColorF FloatingPane => ColorContrast.Flatten(Active.Content,
        ColorContrast.Flatten(Active.Toolbar, MicaBase));
    public static ColorF PlayerBar => Active.PlayerBar;
    public static ColorF FileArea => Active.FileArea;

    /// <summary>Rungs 1+2 as ONE translucent surface — the content pane (<see cref="FileArea"/>) pre-composited onto the
    /// app-body plate (<see cref="Toolbar"/>), still translucent so live Mica reads through it exactly as before. Source-over
    /// is associative, so painting this one rect is pixel-identical to painting the plate and then the pane on top of it
    /// (<c>Flatten(Over(pane, plate), mica) == Flatten(pane, Flatten(plate, mica))</c>, for ANY backdrop — including the
    /// deactivate swing, where DWM drops the backdrop to the opaque window fallback). The shell's content region uses it so
    /// that region pays ONE blended full-region SDF pass instead of two (neither rung can ever take the opaque no-blend PSO
    /// — α &lt; 1 is the ladder contract). Computed live like its siblings, so a theme/preset switch re-fires it via
    /// <c>Tok.Epoch</c>; the raw rungs stay published unchanged for the plate remainders and the gates.</summary>
    public static ColorF ContentPaneMerged => ColorContrast.Over(Active.FileArea, Active.Toolbar);
    public static ColorF Content => Active.Content;
    public static ColorF ContentAlt => Active.ContentAlt;
    public static ColorF PremiumText => Active.PremiumText;

    // White-alpha stripes disappear over the near-white light Mica/page composite. Use a restrained neutral-ink ramp
    // in light mode: visible enough to scan long lists, still quieter than selection and hover states. Dark keeps the
    // palette-provided white overlays.
    public static ColorF RowZebra => Tok.Theme == ThemeKind.Light ? ColorF.FromRgba(0, 0, 0, 0x08) : Active.RowZebra;
    public static ColorF RowHover => Active.RowHover;
    public static ColorF RowHoverZebra => Tok.Theme == ThemeKind.Light ? ColorF.FromRgba(0, 0, 0, 0x0F) : Active.RowHoverZebra;
    public static ColorF RowPressed => Active.RowPressed;
    public static ColorF RowPressedZebra => Tok.Theme == ThemeKind.Light ? ColorF.FromRgba(0, 0, 0, 0x14) : Active.RowPressedZebra;

    public static ColorF ChromeHover => Tok.FillSubtleSecondary;
    public static ColorF ChromePressed => Tok.FillSubtleTertiary;
    public static ColorF Badge => Tok.AccentDefault;

    /// <summary>Swatch preview for the palette picker: the preset's CONTENT PANE flattened through the PLATE and over
    /// the reference Mica for the CURRENT theme — i.e. what that preset's largest surface actually reads as, so the
    /// swatch matches what clicking it produces. Keyed on rung 2 because it is both the largest surface AND, riding a
    /// tinted plate, the most tinted point of the whole ladder (the preset hue is applied twice there; pairwise dark
    /// max-channel deltas measure 16/8/16/9/6/8 at the pane vs single digits at rung 1). Reads the ARGUMENT's shells,
    /// not <c>Active</c> — the swatch previews a palette that is not the active one.</summary>
    public static ColorF PresetSwatch(ThemePalette palette) => Tok.Theme == ThemeKind.Light
        ? ColorContrast.Flatten(palette.LightShell.Content,
            ColorContrast.Flatten(palette.LightShell.Toolbar, MicaRef.LightDefault))
        : ColorContrast.Flatten(palette.DarkShell.Content,
            ColorContrast.Flatten(palette.DarkShell.Toolbar, MicaRef.DarkDefault));
}
