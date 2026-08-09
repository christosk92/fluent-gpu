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

    /// <summary>The in-page thumbnail ladder — the ONLY sizes an in-content cover/avatar may take. An 8px ladder off the
    /// 4-grid: anything between two rungs (36, 44, 52, 57 …) snapped to its nearest rung, which is what stopped a page
    /// from carrying five almost-identical art sizes that read as sloppiness rather than as hierarchy.</summary>
    public const float Thumb32 = 32, Thumb40 = 40, Thumb48 = 48, Thumb56 = 56, Thumb64 = 64;

    /// <summary>The vertical rhythm BETWEEN page sections / feed modules — the wide rung at desktop widths and the
    /// narrow rung below the layout's own breakpoint. Home's module gap and the artist page's section stack read the
    /// SAME two constants, so a section boundary is the same distance in both places.</summary>
    public const float SectionGap = 32, SectionGapWide = 40;

    /// <summary>The widest a page's content column grows before it stops tracking the window. DetailShell and
    /// ArtistPage cap their two-column row here; Home caps each feed row at it, so the three pages line up at the
    /// same measure on an ultra-wide display instead of one of them running edge to edge.</summary>
    public const float PageMaxW = 1600;
}

/// <summary>The bottom player-bar dock geometry. Pages reserve this height so their last row clears the transport.</summary>
public static class PlayerDock
{
    public const float BarH = 72;
    public const float Margin = 0;
    public const float Reserve = BarH;
}

/// <summary>Wavee app shell colors. The authenticated shell is the STOCK Windows 11 Mica stack (learn.microsoft.com
/// system-backdrops; the WinUI-Gallery shell is the reference build):
/// <list type="number">
/// <item>BASE LAYER — Mica-passthrough. The shell root paints NOTHING; every chrome band (merged title row, sidebar,
/// player dock) is a paint-site OMISSION over the live window material. The merged single-row chrome is what makes this
/// safe: there are no plates left, so the two-material title-bar-vs-toolbar seam the old deterministic ground was
/// invented for cannot recur. <see cref="ShellGround"/> (light #EDEDED = <see cref="MicaRef.LightDefault"/>, dark
/// #202020) survives as the no-Mica FALLBACK and as the flatten base for opaque floating surfaces.</item>
/// <item>MATERIAL — the page-published layer above the backdrop (<see cref="ShellMaterialState"/>): a low-alpha flat
/// tint, or Home's three clipped radial washes — a scrim over Mica, so the material carries the page's hue.</item>
/// <item>CONTENT LAYER — <see cref="FileArea"/> (stock <c>LayerFillColorDefault</c>), TRANSLUCENT: the content region
/// and the right rail band paint this smoke over the base, PAIRED with a 1px <c>Tok.StrokeCardDefault</c> stroke on
/// their LEFT+TOP edges only, one rounded corner, and no shadow. That pairing is the whole separation model — a
/// translucent fill alone is too small a step to read as an edge, and stock never adds elevation to this layer.
/// <see cref="ContentSurface"/> is the OPAQUE equivalent of this rung; the shell no longer paints it, but every
/// FLOATING stand-in for a content band does (<see cref="FloatingPane"/>), and it is the flatten target the palette
/// gates measure against.</item>
/// </list>
/// The login screen still shows bare DWM Mica (no shell is mounted there); the translucent <see cref="Toolbar"/> /
/// <see cref="FileArea"/> rungs stay published because the palette contrast gates and the palette recipes are still
/// written in terms of them.</summary>
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

    // The translucent MUX rungs. The authenticated shell no longer PAINTS them — every chrome band is now a paint-site
    // omission over the ground — but they stay published verbatim: the palette contrast gates measure the preset's tint
    // through them, and the palette recipes are defined in terms of them.
    public static ColorF Toolbar => Active.Toolbar;
    public static ColorF Sidebar => Active.Sidebar;

    /// <summary>The chrome GROUND, as a value — for a floating pane that stands in for a docked CHROME band (the narrow
    /// nav drawer, the sidebar preview well, the floating right-rail backing). Identical to what the shell paints under
    /// the docked band, and opaque by construction: the page it covers must not read through it.</summary>
    public static ColorF FloatingChrome => ShellGround;

    /// <summary>The CONTENT surface, as a value — for a floating surface that stands in for a docked content band, and
    /// for the login view, which has no shell under it at all. Same rung as <see cref="ContentSurface"/> by definition;
    /// the two names mark intent (docked surface vs. floating stand-in), not different colours.</summary>
    public static ColorF FloatingPane => ContentSurface;
    public static ColorF PlayerBar => Active.PlayerBar;
    public static ColorF FileArea => Active.FileArea;

    // The dark ground's lightness (PaletteBuilder's `ColorRamp.Tinted(0.125f, …)` canvas) and the lift that takes it to
    // #282828. Expressed as a mix-toward-white FRACTION rather than a literal grey so a tinted preset canvas gets the
    // same step instead of being flattened back to neutral; at chroma 0 it lands on exactly 40/255 per channel.
    const float DarkGroundL = 0.125f;
    const float DarkContentLift = (40f / 255f - DarkGroundL) / (1f - DarkGroundL);

    // The LIGHT ground's DROP off the stock canvas, in the same idiom mirrored to the other mix endpoint: the lift above
    // is (target − L) / (1 − L) because `ColorRamp.Lighten` mixes toward WHITE (1); `ColorRamp.Darken` mixes toward
    // BLACK (0), so the matching fraction is (L − target) / (L − 0). The neutral light canvas is #F3F3F3 (243/255) and
    // the target is MicaRef.LightDefault #EDEDED (237/255) — the bare Mica Alt tone the Files tab rail uses as its
    // DARKEST chrome band, which is the reference this ladder was modelled on. So the drop is (243 − 237) / 243 = 6/243
    // ≈ 0.02469. A FRACTION, not a literal grey, so a tinted preset's canvas takes an equivalent perceptual step down
    // instead of being flattened back to neutral; at chroma 0 it lands on exactly 237/255 per channel.
    const float LightGroundL = 243f / 255f;
    const float LightGroundDrop = (LightGroundL - 237f / 255f) / LightGroundL;

    /// <summary>The chrome GROUND: the full-bleed opaque rect the whole authenticated shell sits on. Light is the stock
    /// <c>FillSolidBase</c> #F3F3F3 dropped to #EDEDED — <see cref="MicaRef.LightDefault"/>, the bare Mica Alt tone the
    /// Files tab rail (the reference this ladder copies) carries as its darkest chrome band; #F3F3F3 read as page, not
    /// chrome. Dark keeps <c>FillSolidBase</c> #202020 verbatim — it is already that band. Computed live like its
    /// siblings, so a theme/preset switch re-fires it via <c>Tok.Epoch</c>.</summary>
    public static ColorF ShellGround => ShellGroundFor(
        Tok.Theme == ThemeKind.Light ? Tok.Palette.Light : Tok.Palette.Dark, Tok.Theme);

    /// <summary>Pure overload of <see cref="ShellGround"/> for an arbitrary token set — used by the gates and by
    /// <see cref="PresetSwatch"/> to resolve a palette that is not the active one, without mutating global theme
    /// state.</summary>
    public static ColorF ShellGroundFor(TokenSet set, ThemeKind theme) => theme == ThemeKind.Light
        ? ColorRamp.Darken(set.FillSolidBase, LightGroundDrop)
        : set.FillSolidBase;

    /// <summary>The OPAQUE content rung — ONE step above the ground. NOT what the docked shell paints any more (the
    /// content region and the rail band are <see cref="FileArea"/> + a stock stroke over live Mica): this is the
    /// wallpaper-independent stand-in every FLOATING content surface takes (<see cref="FloatingPane"/>, the login view,
    /// which has no shell under it at all), and the value the palette gates flatten the translucent ladder against.
    /// Light resolves to the stock
    /// <c>FillSolidTertiary</c> #F9F9F9 over the #EDEDED ground; dark is a deliberate #282828 over the #202020 ground —
    /// <c>FillSolidTertiary</c>'s dark arm lifts 6% (#2D2D2D), which reads as a second card rather than the page. Opaque
    /// on purpose: nothing behind the authenticated shell is meant to show through, so this rung takes the no-blend PSO.
    /// Computed live like its siblings, so a theme/preset switch re-fires it via <c>Tok.Epoch</c>.</summary>
    public static ColorF ContentSurface => ContentSurfaceFor(
        Tok.Theme == ThemeKind.Light ? Tok.Palette.Light : Tok.Palette.Dark, Tok.Theme);

    /// <summary>Pure overload of <see cref="ContentSurface"/> for an arbitrary token set — used by the gates to pin both
    /// themes without mutating the global active theme.</summary>
    public static ColorF ContentSurfaceFor(TokenSet set, ThemeKind theme) => theme == ThemeKind.Light
        ? set.FillSolidTertiary
        : ColorRamp.Lighten(set.FillSolidBase, DarkContentLift);

    // The content step as a TRANSLUCENT white layer: source-over of white@α onto a ground G lands on G + α(1−G), i.e.
    // the same mix-toward-white the opaque rung uses — so α IS the lift fraction. Dark reuses DarkContentLift verbatim;
    // light solves (249−237)/(255−237) = 2/3 against the #EDEDED ground. Neutral therefore composites to exactly
    // ContentSurface over the bare ground — and over a page-published tint it lands on the TINTED equivalent, which an
    // opaque fill cannot (it would punch a neutral hole through the wash).
    const float LightContentLayerA = (249f / 255f - 237f / 255f) / (1f - 237f / 255f);

    /// <summary>The content step expressed as a translucent white LAYER: it composites to <see cref="ContentSurface"/>
    /// over the bare ground AND lands on the TINTED equivalent over a page's published shell tint, which an opaque fill
    /// cannot (it would punch a neutral hole through the wash).
    /// <para>NOTHING IN THE SHELL PAINTS THIS TODAY — the content region and the rail band take the stock
    /// <see cref="FileArea"/> rung, and the text-first tab strip carries no plate at all (weight + opacity + one accent
    /// underline). It stays published as the LAYER-equivalent of <see cref="ContentSurface"/>: the identity
    /// <c>Over(ContentLayer, ShellGround) == ContentSurface</c> is what a fallback or a gate uses to reason about the
    /// ladder in either space, and <c>ShellMergedRungTests</c> pins it. If a chrome-band-scale plate is ever wanted
    /// again, this is the value it takes — not a hand-mixed grey.</para></summary>
    public static ColorF ContentLayer => ContentLayerFor(Tok.Theme);

    /// <summary>Pure overload of <see cref="ContentLayer"/> — theme in, no global reads, for the gates.</summary>
    public static ColorF ContentLayerFor(ThemeKind theme) => ColorF.FromRgba(0xFF, 0xFF, 0xFF,
        (byte)((theme == ThemeKind.Light ? LightContentLayerA : DarkContentLift) * 255f + 0.5f));

    public static ColorF Content => Active.Content;
    public static ColorF ContentAlt => Active.ContentAlt;
    public static ColorF PremiumText => Active.PremiumText;

    // White-alpha stripes disappear over the near-white light content surface. Use a restrained neutral-ink ramp
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

    /// <summary>Swatch preview for the palette picker: the preset's CONTENT LAYER over the bare Mica base, for the
    /// CURRENT theme — i.e. exactly the composite the shell's content region produces, so the swatch matches what
    /// clicking it does to the largest surface in the window.
    /// <para>It flattens <see cref="FileArea"/> (the translucent <c>LayerFillColorDefault</c> rung the region really
    /// paints) rather than the opaque <see cref="ContentSurface"/> the swatch used to preview — that showed a rung the
    /// authenticated shell stopped painting, so a preset whose translucent recipe differed from its opaque one
    /// advertised the wrong colour.</para>
    /// <para>THE APPROXIMATION, stated: live Mica has no colour until there is a desktop behind it, so the base here is
    /// <see cref="MicaRef"/>'s neutral no-wallpaper reference tone (LightDefault / DarkDefault) — the same stand-in the
    /// palette contrast gates measure against. On a strongly tinted wallpaper the real region drifts toward it; the
    /// swatch cannot and should not chase that, because it is comparing PRESETS, not desktops.</para>
    /// Reads the ARGUMENT's token sets, not the active ones — the swatch previews a palette that is not the active
    /// one.</summary>
    public static ColorF PresetSwatch(ThemePalette palette) => Tok.Theme == ThemeKind.Light
        ? ColorContrast.Flatten(palette.LightShell.FileArea, MicaRef.LightDefault)
        : ColorContrast.Flatten(palette.DarkShell.FileArea, MicaRef.DarkDefault);
}
