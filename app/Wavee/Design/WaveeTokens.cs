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

/// <summary>Wavee app shell colors. Kept app-local so engine theme tokens remain WinUI-faithful.</summary>
public static class WaveeColors
{
    // WaveeMusic App.xaml maps sidebar/address-bar surfaces to LayerOnMicaBaseAltFillColorDefault. THEME-AWARE (WinUI):
    // dark = #733A3A3A (a translucent dark layer over Mica), light = #B3FFFFFF (a translucent WHITE layer). Hardcoding the
    // dark value made the chrome read near-black in light theme over a dark-OS Mica backdrop — the 70%-white light layer
    // masks the dark Mica instead. Theme-dependent, so the surfaces that use it must be BOUND to follow a live switch.
    //
    // Light override: WinUI's #B3FFFFFF (70% white) blends invisibly into the near-white Mica light backdrop — no depth.
    // We use the opaque WinUI SolidBase grey (#F3F3F3) so the chrome is a DEFINITE surface; the white content card then
    // floats visibly above it. Dark keeps the original translucent dark layer (Mica tints through nicely there).
    public static ColorF LayerOnMicaBaseAlt => Tok.Theme == ThemeKind.Light
        ? Tok.FillSolidBase                          // #F3F3F3 opaque — visible grey chrome
        : ColorF.FromRgba(0x3A, 0x3A, 0x3A, 0x73);
    // The shell root is Mica passthrough: FluentApp sets Theme.WindowBackground = Transparent when mica:true, so DWM
    // composites Mica BaseAlt behind the client area. The chrome (Toolbar/Sidebar = LayerOnMicaBaseAlt) tints it; the
    // content card stays opaque (FileArea). Inactive-window determinism is handled by AppHost (clears #202020 when
    // unfocused) — so the root must NOT paint an opaque slab, or it covers the backdrop entirely.
    public static ColorF Window => ColorF.Transparent;
    public static ColorF TitleBar => ColorF.Transparent;
    public static ColorF Toolbar => LayerOnMicaBaseAlt;
    public static ColorF Sidebar => LayerOnMicaBaseAlt;
    // Light: pure white card floats over the #F3F3F3 chrome — the depth contrast that makes it read as "floating".
    // Dark: keep the WinUI-faithful translucent card (Mica bleeds through nicely).
    public static ColorF FileArea => Tok.Theme == ThemeKind.Light
        ? ColorF.FromRgba(0xFF, 0xFF, 0xFF)
        : Tok.FillCardDefault;
    public static ColorF Content => Tok.Theme == ThemeKind.Light
        ? ColorF.FromRgba(0xFF, 0xFF, 0xFF)
        : Tok.FillCardDefault;
    public static ColorF ContentAlt => Tok.Theme == ThemeKind.Light
        ? ColorF.FromRgba(0xF9, 0xF9, 0xF9)         // very subtle tint for alternate/secondary surfaces
        : Tok.FillCardSecondary;
    public static ColorF ChromeHover => Tok.FillSubtleSecondary;
    public static ColorF ChromePressed => Tok.FillSubtleTertiary;
    public static ColorF Badge => Tok.AccentDefault;
}
