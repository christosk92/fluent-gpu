using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

// Wavee's geometry token layer. COLOR comes entirely from the engine's WinUI-faithful `Tok.*` (Dsl/Tokens.cs) — we do
// NOT duplicate it. This adds the spacing / rounding / sizing scale Tok doesn't carry. The 4px grid is the native tell;
// every value here is a multiple of 4.

/// <summary>Spacing scale (DIPs) — the 4px grid. Use for gaps, padding, gutters.</summary>
public static class WaveeSpace
{
    public const float XS = 4, S = 8, M = 12, L = 16, XL = 20, XXL = 24, Gutter = 24;
}

/// <summary>Corner radii. Native Fluent tell: 8 for surfaces/cards, 4 for nested controls (outer-8 → inner-4).</summary>
public static class WaveeRadius
{
    public const float Control = 4, Card = 8, Overlay = 8, Pill = 999, Straight = 0;
}

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
/// transparent to DWM Mica; chrome and content are translucent semantic layers composited over it. This is the WinUI
/// two-layer model: Mica remains the foundation while low-alpha fills establish navigation and content hierarchy.</summary>
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

    // The shell root is Mica passthrough: FluentApp sets Theme.WindowBackground = Transparent when mica:true, so DWM
    // composites Mica BaseAlt behind the client area; the translucent chrome above tints it (both themes).
    public static ColorF Window => ColorF.Transparent;
    public static ColorF TitleBar => ColorF.Transparent;

    // WinUI's stock LayerOnMica brush is intentionally conservative. Wavee uses thinner light composites so the Mica
    // color remains perceptible across its large, persistent shell regions; RGB still comes from the selected palette.
    static ColorF LightComposite(ColorF color, float alpha) => Tok.Theme == ThemeKind.Light ? color with { A = alpha } : color;

    public static ColorF Toolbar => LightComposite(Active.Toolbar, 0.46f);
    public static ColorF Sidebar => LightComposite(Active.Sidebar, 0.38f);
    public static ColorF SelectedTab => LightComposite(Tok.LayerOnMicaBaseAlt, 0.58f);
    public static ColorF RailOverlay => Tok.Theme == ThemeKind.Light
        ? Active.ContentAlt with { A = 0.58f }
        : ColorF.FromRgba(0x1C, 0x1D, 0x20);
    public static ColorF PlayerBar => LightComposite(Active.PlayerBar, 0.42f);
    public static ColorF FileArea => LightComposite(Active.FileArea, 0.62f);
    public static ColorF Content => LightComposite(Active.Content, 0.62f);
    public static ColorF ContentAlt => LightComposite(Active.ContentAlt, 0.40f);
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

    /// <summary>Swatch preview for the palette picker: the preset's chrome bar flattened over the reference Mica for
    /// the CURRENT theme — i.e. what that preset's toolbar actually reads as, so the swatch matches what clicking it
    /// produces (the old hardcoded swatch hexes drifted from the real preset output and ignored dark entirely).</summary>
    public static ColorF PresetSwatch(ThemePalette palette) => Tok.Theme == ThemeKind.Light
        ? ColorContrast.Flatten(palette.LightShell.Toolbar, MicaRef.LightDefault)
        : ColorContrast.Flatten(palette.DarkShell.Toolbar, MicaRef.DarkDefault);
}
