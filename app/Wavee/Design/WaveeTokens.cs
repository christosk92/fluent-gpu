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

/// <summary>Wavee app shell colors. Kept app-local so the engine theme tokens stay WinUI-faithful. Two baked palettes
/// (light / dark) selected by the active theme — mirrors the engine's TokenSet pointer-swap, so the theme branch exists
/// exactly ONCE (no per-property ternary, and nothing "defaults" to one theme). The standard WinUI layer-over-Mica
/// material now lives in the engine as <see cref="Tok.LayerOnMicaBaseAlt"/>; the values here are Wavee's own design.</summary>
public static class WaveeColors
{
    /// <summary>One theme's shell surfaces (the values that aren't simply a plain engine token).</summary>
    public sealed record Palette(
        ColorF Toolbar, ColorF Sidebar, ColorF PlayerBar, ColorF FileArea, ColorF Content, ColorF ContentAlt,
        // Track-list row states. Zebra = odd-row rest; hover/press carry an even + a (deeper) zebra variant. The odd/even
        // pick stays at the call site (that's row STATE, not theme); here we only hold the two themes' values.
        ColorF RowZebra, ColorF RowHover, ColorF RowHoverZebra, ColorF RowPressed, ColorF RowPressedZebra);

    // LIGHT — a TRANSLUCENT warm layer over Mica (so the desktop backdrop reads through), warm-tinted so it doesn't wash
    // out into the near-white light Mica the way WinUI's cold #B3FFFFFF did. Zones differ by tint + opacity: toolbar
    // shelf (lightest / most bleed) > sidebar rail > player dock (deepest / most opaque). The content "page" floats on
    // top — translucent enough that Mica textures it (kills the flat sheet), warm + high-alpha so the track list is crisp.
    static readonly Palette LightPalette = new(
        Toolbar:    ColorF.FromRgba(0xF6, 0xF4, 0xF0, 0xB3),   // warm shelf @ ~70%
        Sidebar:    ColorF.FromRgba(0xEC, 0xEA, 0xE5, 0xC4),   // recessed warm rail @ ~77%
        PlayerBar:  ColorF.FromRgba(0xE7, 0xE5, 0xE0, 0xD1),   // deepest dock @ ~82% — least bleed
        FileArea:   ColorF.FromRgba(0xFB, 0xFA, 0xF8, 0xD9),   // warm page @ ~85%
        Content:    ColorF.FromRgba(0xFB, 0xFA, 0xF8, 0xD9),
        ContentAlt: ColorF.FromRgba(0xF4, 0xF3, 0xF0),         // recessed inset within content (opaque canvas tone)
        RowZebra:        ColorF.FromRgba(0xF7, 0xF6, 0xF3),    // subtle warm zebra band (odd rows)
        RowHover:        ColorF.FromRgba(0xEC, 0xE9, 0xE2),    // even-row hover
        RowHoverZebra:   ColorF.FromRgba(0xE6, 0xE3, 0xDB),    // zebra-row hover (deeper — the card starts lighter)
        RowPressed:      ColorF.FromRgba(0xE5, 0xE2, 0xDA),
        RowPressedZebra: ColorF.FromRgba(0xDF, 0xDC, 0xD3));

    // DARK — WinUI-faithful: the chrome is the one translucent dark layer over Mica (Tok.LayerOnMicaBaseAlt = #733A3A3A);
    // the content card is the translucent FillCardDefault (Mica bleeds through nicely). Read off the DARK TokenSet
    // directly so it bakes once — these tokens don't depend on the live accent override.
    static readonly Palette DarkPalette = new(
        Toolbar:    Tok.Dark.LayerOnMicaBaseAlt,
        Sidebar:    Tok.Dark.LayerOnMicaBaseAlt,
        PlayerBar:  Tok.Dark.LayerOnMicaBaseAlt,
        FileArea:   Tok.Dark.FillCardDefault,
        Content:    Tok.Dark.FillCardDefault,
        ContentAlt: Tok.Dark.FillCardSecondary,
        RowZebra:        Tok.Dark.FillSubtleTertiary,     // dark rows: WinUI subtle translucent fills (Mica bleeds through)
        RowHover:        Tok.Dark.FillSubtleSecondary,
        RowHoverZebra:   Tok.Dark.FillSubtleSecondary,    // dark hover isn't zebra-split — same subtle fill either way
        RowPressed:      Tok.Dark.FillSubtleTertiary,
        RowPressedZebra: Tok.Dark.FillSubtleTertiary);

    /// <summary>The ONE theme switch (mirrors <c>Tok.T</c>); every surface below resolves through it.</summary>
    static Palette Active => Tok.Theme == ThemeKind.Light ? LightPalette : DarkPalette;

    // The shell root is Mica passthrough: FluentApp sets Theme.WindowBackground = Transparent when mica:true, so DWM
    // composites Mica BaseAlt behind the client area; the (translucent) chrome above tints it. The root must NOT paint an
    // opaque slab or it covers the backdrop entirely (inactive-window determinism is handled by AppHost).
    public static ColorF Window => ColorF.Transparent;
    public static ColorF TitleBar => ColorF.Transparent;

    public static ColorF Toolbar => Active.Toolbar;
    public static ColorF Sidebar => Active.Sidebar;
    public static ColorF PlayerBar => Active.PlayerBar;
    public static ColorF FileArea => Active.FileArea;
    public static ColorF Content => Active.Content;
    public static ColorF ContentAlt => Active.ContentAlt;

    public static ColorF RowZebra => Active.RowZebra;
    public static ColorF RowHover => Active.RowHover;
    public static ColorF RowHoverZebra => Active.RowHoverZebra;
    public static ColorF RowPressed => Active.RowPressed;
    public static ColorF RowPressedZebra => Active.RowPressedZebra;

    public static ColorF ChromeHover => Tok.FillSubtleSecondary;
    public static ColorF ChromePressed => Tok.FillSubtleTertiary;
    public static ColorF Badge => Tok.AccentDefault;
}
