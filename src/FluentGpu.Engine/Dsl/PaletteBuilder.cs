using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>Seed for a shadcn-style base palette — hue/chroma of the neutral ramp, light-mode luminance anchors
/// (interpreted as FLATTENED-over-Mica targets for the translucent shell, see <see cref="PaletteBuilder"/>), and the
/// per-preset chrome saturations that make presets read distinct (fed to <see cref="ColorRamp.Tinted"/>, which skips
/// the high-lightness chroma softening that crushes <see cref="ColorRamp.Neutral"/> tints to sub-perceptual).</summary>
public sealed record PaletteSeed(
    string Id,
    float NeutralHueDeg,
    float NeutralChroma,
    float LightFrameL,
    float LightRailL,
    float LightPageL,
    float LightCardL,
    float LightChromeSat,
    float DarkChromeSat);

/// <summary>Wavee app-shell surfaces derived from a <see cref="PaletteSeed"/> (one light + one dark per preset).</summary>
public sealed record ShellPalette(
    ColorF Toolbar, ColorF Sidebar, ColorF PlayerBar, ColorF FileArea, ColorF Content, ColorF ContentAlt,
    ColorF RowZebra, ColorF RowHover, ColorF RowHoverZebra, ColorF RowPressed, ColorF RowPressedZebra);

/// <summary>A named preset: paired light/dark <see cref="TokenSet"/>s + shell surfaces. Switched via <see cref="Tok.Use(ThemePalette, ThemeKind)"/>.</summary>
public sealed record ThemePalette(string Id, TokenSet Light, TokenSet Dark, ShellPalette LightShell, ShellPalette DarkShell);

/// <summary>Build-time generator for seed-derived theme palettes (startup-only; no per-frame allocation).</summary>
public static class PaletteBuilder
{
    // Slate sits at 230° (not the accent default's 210°) so slate and accent-tinted stay tellable apart.
    public static readonly PaletteSeed Warm = new("warm", 40f, 0.03f, 0.862f, 0.898f, 0.938f, 0.984f, 0.32f, 0.22f);
    public static readonly PaletteSeed Slate = new("slate", 230f, 0.03f, 0.862f, 0.898f, 0.938f, 0.984f, 0.34f, 0.24f);
    public static readonly PaletteSeed Neutral = new("neutral", 0f, 0f, 0.862f, 0.898f, 0.938f, 0.984f, 0f, 0f);

    public static PaletteSeed AccentTinted(float accentHueDeg)
        => new("accent", accentHueDeg, 0.025f, 0.862f, 0.898f, 0.938f, 0.984f, 0.26f, 0.22f);

    public static ThemePalette Build(PaletteSeed seed) => new(seed.Id, BuildLight(seed), BuildDark(seed), BuildLightShell(seed), BuildDarkShell(seed));

    public static ThemePalette BuildAccentTinted(ColorF accent) => Build(AccentTinted(ColorRamp.HueDegrees(accent)));

    /// <summary>Files / WaveeMusic light file-area brush (<c>App.Theme.FileArea.BackgroundBrush</c> = <c>#C0FCFCFC</c>).</summary>
    public static readonly ColorF FilesLightFileArea = ColorF.FromRgba(0xFC, 0xFC, 0xFC, 0xC0);

    static ShellPalette BuildLightShell(PaletteSeed seed)
    {
        if (seed.Id == "neutral")
            return BuildFilesLightShell();
        // Mica-first light chrome: translucent seed-tinted layers over the DWM backdrop — the light analogue of the
        // dark #3A3A3A@0x73 stack. The seed's luminance anchors are FLATTENED targets: each tint's lightness is
        // solved so compositing at its alpha over the reference light Mica lands on the anchor
        //     tintL = (anchorL − micaL·(1−a)) / a
        // so the frame < rail < page ladder holds on the reference backdrop and, because every surface shares the
        // Mica term scaled by (1−a), it compresses proportionally under wallpaper swings but never inverts.
        const float barA = 0x73 / 255f;    // bars match dark's 45% — real Mica/wallpaper bleed
        const float pageA = 0x8C / 255f;   // frame + text-hosting page keep 55% — stable contrast floor
        float micaL = MicaRef.LightDefault.R;
        float Solve(float anchorL, float a) => Math.Clamp((anchorL - micaL * (1f - a)) / a, 0f, 1f);
        float h = seed.NeutralHueDeg, s = seed.LightChromeSat;
        var frame = ColorRamp.Tinted(Solve(seed.LightFrameL, pageA), h, s, 0x8C);
        var rail = ColorRamp.Tinted(Solve(seed.LightRailL, barA), h, s * 0.9f, 0x73);
        var dock = ColorRamp.Tinted(Solve(seed.LightRailL + 0.014f, barA), h, s * 0.9f, 0x73);
        var page = ColorRamp.Tinted(Solve(seed.LightPageL, pageA), h, s * 0.5f, 0x8C);
        var inset = ColorRamp.Tinted(Solve(seed.LightPageL - 0.036f, pageA), h, s * 0.6f, 0x8C);
        // Rows are neutral overlays (mirrors dark's white-alpha rows): the preset tint comes from the translucent
        // page beneath, so row states are preset-independent by construction.
        return new(frame, rail, dock, page, page, inset,
            RowZebra:        ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x32),
            RowHover:        ColorF.FromRgba(0x00, 0x00, 0x00, 0x09),
            RowHoverZebra:   ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x20),
            RowPressed:      ColorF.FromRgba(0x00, 0x00, 0x00, 0x0C),
            RowPressedZebra: ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x16));
    }

    /// <summary>Files-faithful light shell (see <c>C:\WAVEE\Files\src\Files.App\App.xaml</c> Light theme dict).</summary>
    static ShellPalette BuildFilesLightShell()
    {
        var onMica = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3);   // LayerOnMicaBaseAltFillColorDefault
        var cardSecondary = ColorF.FromRgba(0xF6, 0xF6, 0xF6, 0x80); // CardBackgroundFillColorSecondary
        var fileArea = FilesLightFileArea;
        return new(onMica, onMica, cardSecondary, fileArea, fileArea, cardSecondary,
            RowZebra:        ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x32),
            RowHover:        ColorF.FromRgba(0x00, 0x00, 0x00, 0x09),
            RowHoverZebra:   ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x20),
            RowPressed:      ColorF.FromRgba(0x00, 0x00, 0x00, 0x0C),
            RowPressedZebra: ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x16));
    }

    static ShellPalette BuildDarkShell(PaletteSeed seed)
    {
        // Seed-tinted translucent bars over dark Mica — the neutral seed (sat 0) reproduces WinUI's #3A3A3A@0x73
        // exactly, so today's proven dark stack is the sat-0 special case. Page card + rows stay the WinUI
        // white-alpha overlays: the bars and the solid canvas (BuildDark) carry the preset tint.
        var bar = ColorRamp.Tinted(0.227f, seed.NeutralHueDeg, seed.DarkChromeSat, 0x73);
        return new(
            Toolbar:    bar,
            Sidebar:    bar,
            PlayerBar:  bar,
            FileArea:   ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0D),
            Content:    ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0D),
            ContentAlt: ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x08),
            RowZebra:        ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0A),
            RowHover:        ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0F),
            RowHoverZebra:   ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0F),
            RowPressed:      ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0A),
            RowPressedZebra: ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0A));
    }

    static TokenSet BuildLight(PaletteSeed seed)
    {
        if (seed.Id == "neutral")
            return BuildWinUILight();
        if (seed.Id == "warm")
            return BuildWarmLight();

        ColorF N(float l) => ColorRamp.Neutral(l, seed.NeutralHueDeg, seed.NeutralChroma);
        var frame = N(seed.LightFrameL);
        var page = N(seed.LightPageL);
        var card = N(seed.LightCardL);
        var card2 = ColorRamp.Darken(card, 0.03f);
        var controlHover = ColorRamp.Darken(card, 0.06f);
        var controlPress = ColorRamp.Darken(card, 0.10f);
        var layer = ColorRamp.Darken(card, 0.015f);
        var well = ColorRamp.Darken(frame, 0.04f);
        var strokeCard = ColorRamp.Darken(page, 0.08f);
        var strokeCtrl = ColorRamp.Darken(page, 0.12f);
        var strokeCtrl2 = ColorRamp.Darken(page, 0.18f);
        var strokeDiv = ColorRamp.Darken(page, 0.10f);
        var textPrimary = TintInk(seed, 0.12f);
        var textSecondary = TintInk(seed, 0.36f);
        // Tertiary must clear AA on the LIGHTEST surface it can land on — the opaque card, or the zebra row
        // flattened over the translucent page on the brightest assumed Mica — not the mid-tone rail (the old,
        // inverted target: darker bg = easier ratio for dark-on-light text).
        var shell = BuildLightShell(seed);
        var zebraHost = ColorContrast.Flatten(shell.RowZebra, ColorContrast.Flatten(shell.FileArea, MicaRef.LightBright));
        var lightestHost = ColorContrast.RelativeLuminance(card) >= ColorContrast.RelativeLuminance(zebraHost) ? card : zebraHost;
        var textTertiary = SolveTertiaryText(lightestHost, TintInk(seed, 0.40f));

        return new TokenSet
        {
            FillControlDefault   = card,
            FillControlSecondary = controlHover,
            FillControlTertiary  = controlPress,
            FillControlDisabled  = ColorF.FromRgba(0xF9, 0xF9, 0xF9, 0x4D),
            FillControlStrong         = ColorF.FromRgba(0x00, 0x00, 0x00, 0x72),
            FillControlStrongDisabled = ColorF.FromRgba(0x00, 0x00, 0x00, 0x51),
            FillControlSolid          = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            FillControlOnImage   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xC9),
            FillSubtleTransparent= ColorF.Transparent,
            FillSubtleSecondary  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x09),
            FillSubtleTertiary   = ColorF.FromRgba(0x00, 0x00, 0x00, 0x06),
            FillCardDefault      = card,
            FillCardSecondary    = card2,
            FillLayerDefault     = layer,
            FillLayerAlt         = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            LayerOnMicaBaseAlt   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3),
            FillSolidBase        = ColorRamp.Darken(page, 0.05f),
            FillSolidBaseAlt     = well,
            FillSolidSecondary   = controlPress,
            FillSolidTertiary    = ColorRamp.Lighten(page, 0.01f),
            FillSmoke            = ColorF.FromRgba(0x00, 0x00, 0x00, 0x4D),
            StrokeControlDefault = strokeCtrl,
            StrokeControlSecondary = strokeCtrl2,
            StrokeCardDefault    = strokeCard,
            StrokeDividerDefault = strokeDiv,
            StrokeSurfaceDefault = ColorF.FromRgba(0x75, 0x75, 0x75, 0x66),
            StrokeFlyoutDefault = ColorF.FromRgba(0x00, 0x00, 0x00, 0x17),   // edge definition for the near-solid light flyout plate
            StrokeControlOnAccentDefault = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x14),
            StrokeControlOnAccentSecondary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x66),
            StrokeControlOnAccentTertiary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x37),
            TextPrimary   = textPrimary,
            TextSecondary = textSecondary,
            TextTertiary  = textTertiary,
            TextDisabled  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x5C),
            TextOnAccentPrimary   = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            TextOnAccentSecondary = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3),
            TextOnAccentDisabled  = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            TextOnAccentSelectedText = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            CaptionCloseHover   = ColorF.FromRgba(0xC4, 0x2B, 0x1C),
            CaptionClosePressed = ColorF.FromRgba(0xC4, 0x2B, 0x1C, 0xE6),
            AccentDefault = ColorF.FromRgba(0x00, 0x5F, 0xB8),
            AccentSecondary = ColorF.FromRgba(0x00, 0x5F, 0xB8, 0xE6),
            AccentTertiary  = ColorF.FromRgba(0x00, 0x5F, 0xB8, 0xCC),
            AccentTextPrimary   = ColorF.FromRgba(0x00, 0x42, 0x75),
            AccentTextSecondary = ColorF.FromRgba(0x00, 0x26, 0x42),
            AccentTextTertiary  = ColorF.FromRgba(0x00, 0x5F, 0xB8),
            AccentDisabled  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x37),
            AccentSubtle    = ColorF.FromRgba(0x00, 0x5F, 0xB8, 0x24),
            AccentSelectedTextBackground = ColorF.FromRgba(0x00, 0x78, 0xD4),
            FocusOuter = ColorF.FromRgba(0x00, 0x00, 0x00, 0xE4),
            FocusInner = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3),
            ScrollThumb = ColorF.FromRgba(0x00, 0x00, 0x00, 0x72),
            AcrylicTint = ColorF.FromRgba(0xFC, 0xFC, 0xFC, 0xD9),
            AcrylicBase = ColorF.FromRgba(0xF3, 0xF3, 0xF3, 0xE6),
            AcrylicFlyout = AcrylicSpec.FlyoutLight,
            HeroGradientTop = ColorF.FromRgba(0x7A, 0xB6, 0xE6, 0xB3),
            HeroGradientBottom = ColorF.FromRgba(0xF3, 0xF3, 0xF3, 0x00),
            FillControlAltSecondary  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x06),
            FillControlAltTertiary   = ColorF.FromRgba(0x00, 0x00, 0x00, 0x0F),
            FillControlAltQuaternary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x18),
            FillControlAltDisabled   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x00),
            StrokeControlStrongDefault  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x72),
            StrokeControlStrongDisabled = ColorF.FromRgba(0x00, 0x00, 0x00, 0x37),
            FillControlInputActive   = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            TextControlHeaderForeground         = ColorF.FromRgba(0x00, 0x00, 0x00),
            TextControlHeaderForegroundDisabled = ColorF.FromRgba(0x00, 0x00, 0x00, 0x66),
            TextControlDescriptionForeground    = ColorF.FromRgba(0x00, 0x00, 0x00, 0x99),
            TextControlForegroundDisabled       = ColorF.FromRgba(0x01, 0x01, 0x01, 0x5C),
            SystemFillCritical = ColorF.FromRgba(0xC4, 0x2B, 0x1C),
            SystemFillCaution  = ColorF.FromRgba(0x9D, 0x5D, 0x00),
            SystemFillSuccess  = ColorF.FromRgba(0x0C, 0x6B, 0x0C),
            SystemFillCriticalBackground  = ColorF.FromRgba(0xFD, 0xE7, 0xE9),
            SystemFillCautionBackground   = ColorF.FromRgba(0xFF, 0xF4, 0xCE),
            SystemFillSuccessBackground   = ColorF.FromRgba(0xDF, 0xF6, 0xDD),
            SystemFillAttentionBackground = ColorF.FromRgba(0xF6, 0xF6, 0xF6, 0x80),
            SystemFillSolidNeutral = ColorF.FromRgba(0x8A, 0x8A, 0x8A),
            TextInverse = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            WindowBackground = frame,
        };
    }

    static TokenSet BuildDark(PaletteSeed seed)
    {
        // WinUI-faithful dark — the seed tints the solid canvas (WindowBackground / FillSolid*) so the preset hue
        // reads through the translucent chrome; Tinted (not Neutral) so the tint survives at low lightness.
        ColorF canvas = ColorRamp.Tinted(0.125f, seed.NeutralHueDeg, seed.DarkChromeSat * 0.6f);
        return new TokenSet
        {
            FillControlDefault   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0F),
            FillControlSecondary = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x15),
            FillControlTertiary  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x08),
            FillControlDisabled  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0B),
            FillControlStrong         = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x8B),
            FillControlStrongDisabled = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x3F),
            FillControlSolid          = ColorF.FromRgba(0x45, 0x45, 0x45),
            FillControlOnImage   = ColorF.FromRgba(0x1C, 0x1C, 0x1C, 0xB3),
            FillSubtleTransparent= ColorF.Transparent,
            FillSubtleSecondary  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0F),
            FillSubtleTertiary   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0A),
            FillCardDefault      = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0D),
            FillCardSecondary    = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x08),
            FillLayerDefault     = ColorF.FromRgba(0x3A, 0x3A, 0x3A, 0x4C),
            FillLayerAlt         = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0D),
            LayerOnMicaBaseAlt   = ColorF.FromRgba(0x3A, 0x3A, 0x3A, 0x73),
            FillSolidBase        = canvas,
            FillSolidBaseAlt     = ColorRamp.Darken(canvas, 0.08f),
            FillSolidSecondary   = ColorRamp.Darken(canvas, 0.08f),
            FillSolidTertiary    = ColorRamp.Lighten(canvas, 0.06f),
            FillSmoke            = ColorF.FromRgba(0x00, 0x00, 0x00, 0x4D),
            StrokeControlDefault = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x12),
            StrokeControlSecondary = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x18),
            StrokeCardDefault    = ColorF.FromRgba(0x00, 0x00, 0x00, 0x19),
            StrokeDividerDefault = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x15),
            StrokeSurfaceDefault = ColorF.FromRgba(0x75, 0x75, 0x75, 0x66),
            StrokeFlyoutDefault = ColorF.FromRgba(0x00, 0x00, 0x00, 0x33),
            StrokeControlOnAccentDefault = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x14),
            StrokeControlOnAccentSecondary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x23),
            StrokeControlOnAccentTertiary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x37),
            TextPrimary   = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            TextSecondary = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xC5),
            TextTertiary  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x87),
            TextDisabled  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x5D),
            TextOnAccentPrimary   = ColorF.FromRgba(0x00, 0x00, 0x00),
            TextOnAccentSecondary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x80),
            TextOnAccentDisabled  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x87),
            TextOnAccentSelectedText = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            CaptionCloseHover   = ColorF.FromRgba(0xC4, 0x2B, 0x1C),
            CaptionClosePressed = ColorF.FromRgba(0xC4, 0x2B, 0x1C, 0xE6),
            AccentDefault = ColorF.FromRgba(0x60, 0xCD, 0xFF),
            AccentSecondary = ColorF.FromRgba(0x60, 0xCD, 0xFF, 0xE6),
            AccentTertiary  = ColorF.FromRgba(0x60, 0xCD, 0xFF, 0xCC),
            AccentTextPrimary   = ColorF.FromRgba(0xA6, 0xD8, 0xFF),
            AccentTextSecondary = ColorF.FromRgba(0xA6, 0xD8, 0xFF),
            AccentTextTertiary  = ColorF.FromRgba(0x76, 0xB9, 0xED),
            AccentDisabled  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x28),
            AccentSubtle    = ColorF.FromRgba(0x60, 0xCD, 0xFF, 0x29),
            AccentSelectedTextBackground = ColorF.FromRgba(0x00, 0x78, 0xD4),
            FocusOuter = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            FocusInner = ColorF.FromRgba(0x00, 0x00, 0x00, 0xB3),
            ScrollThumb = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x8B),
            AcrylicTint = ColorF.FromRgba(0x2C, 0x2C, 0x2C, 0xCC),
            AcrylicBase = ColorF.FromRgba(0x20, 0x20, 0x20, 0xD9),
            AcrylicFlyout = AcrylicSpec.InAppDefault,
            HeroGradientTop = ColorF.FromRgba(0x2A, 0x4A, 0x66, 0xCC),
            HeroGradientBottom = ColorF.FromRgba(0x20, 0x20, 0x20, 0x00),
            FillControlAltSecondary  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x19),
            FillControlAltTertiary   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0B),
            FillControlAltQuaternary = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x12),
            FillControlAltDisabled   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x00),
            StrokeControlStrongDefault  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x8B),
            StrokeControlStrongDisabled = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x28),
            FillControlInputActive   = ColorF.FromRgba(0x1E, 0x1E, 0x1E, 0xB3),
            TextControlHeaderForeground         = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            TextControlHeaderForegroundDisabled = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x66),
            TextControlDescriptionForeground    = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x99),
            TextControlForegroundDisabled       = ColorF.FromRgba(0xFE, 0xFE, 0xFE, 0x5D),
            SystemFillCritical = ColorF.FromRgba(0xFF, 0x99, 0xA4),
            SystemFillCaution  = ColorF.FromRgba(0xFC, 0xE1, 0x00),
            SystemFillSuccess  = ColorF.FromRgba(0x6C, 0xCB, 0x5F),
            SystemFillCriticalBackground  = ColorF.FromRgba(0x44, 0x27, 0x26),
            SystemFillCautionBackground   = ColorF.FromRgba(0x43, 0x35, 0x19),
            SystemFillSuccessBackground   = ColorF.FromRgba(0x39, 0x3D, 0x1B),
            SystemFillAttentionBackground = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x08),
            SystemFillSolidNeutral = ColorF.FromRgba(0x9D, 0x9D, 0x9D),
            TextInverse = ColorF.FromRgba(0x00, 0x00, 0x00, 0xE4),
            WindowBackground = canvas,
        };
    }

    static ColorF TintInk(PaletteSeed seed, float lightness)
        => ColorRamp.Neutral(lightness, seed.NeutralHueDeg, seed.NeutralChroma * 0.3f);

    /// <summary>Darken <paramref name="start"/> until it meets AA against the lightest surface it can host on.</summary>
    static ColorF SolveTertiaryText(ColorF lightestHostingBg, ColorF start)
    {
        var c = start;
        for (int i = 0; i < 12 && !ColorContrast.MeetsAaText(c, lightestHostingBg); i++)
            c = ColorRamp.Darken(c, 0.06f);
        return c;
    }

    /// <summary>WinUI-faithful neutral light tokens (Files / WaveeMusic default palette baseline).</summary>
    static TokenSet BuildWinUILight()
    {
        var shell = BuildFilesLightShell();
        var card = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3);       // CardBackgroundFillColorDefault
        var card2 = ColorF.FromRgba(0xF6, 0xF6, 0xF6, 0x80);      // CardBackgroundFillColorSecondary
        var controlHover = ColorF.FromRgba(0xF9, 0xF9, 0xF9, 0x80);
        var controlPress = ColorF.FromRgba(0xF9, 0xF9, 0xF9, 0x4D);
        var solidBase = ColorF.FromRgba(0xF3, 0xF3, 0xF3);
        var fileFlat = ColorContrast.Flatten(shell.FileArea, MicaRef.LightBright);
        var zebraHost = ColorContrast.Flatten(shell.RowZebra, fileFlat);
        var lightestHost = ColorContrast.RelativeLuminance(card) >= ColorContrast.RelativeLuminance(zebraHost) ? card : zebraHost;
        var textTertiary = SolveTertiaryText(lightestHost, ColorF.FromRgba(0x00, 0x00, 0x00, 0x72));

        return new TokenSet
        {
            FillControlDefault   = card,
            FillControlSecondary = controlHover,
            FillControlTertiary  = controlPress,
            FillControlDisabled  = ColorF.FromRgba(0xF9, 0xF9, 0xF9, 0x4D),
            FillControlStrong         = ColorF.FromRgba(0x00, 0x00, 0x00, 0x72),
            FillControlStrongDisabled = ColorF.FromRgba(0x00, 0x00, 0x00, 0x51),
            FillControlSolid          = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            FillControlOnImage   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xC9),
            FillSubtleTransparent= ColorF.Transparent,
            FillSubtleSecondary  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x09),
            FillSubtleTertiary   = ColorF.FromRgba(0x00, 0x00, 0x00, 0x06),
            FillCardDefault      = card,
            FillCardSecondary    = card2,
            FillLayerDefault     = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x80),
            FillLayerAlt         = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            LayerOnMicaBaseAlt   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3),
            FillSolidBase        = solidBase,
            FillSolidBaseAlt     = ColorF.FromRgba(0xDA, 0xDA, 0xDA),
            FillSolidSecondary   = ColorF.FromRgba(0xEE, 0xEE, 0xEE),
            FillSolidTertiary    = ColorF.FromRgba(0xF9, 0xF9, 0xF9),
            FillSmoke            = ColorF.FromRgba(0x00, 0x00, 0x00, 0x4D),
            StrokeControlDefault = ColorF.FromRgba(0x00, 0x00, 0x00, 0x17),
            StrokeControlSecondary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x29),
            StrokeCardDefault    = ColorF.FromRgba(0x00, 0x00, 0x00, 0x0F),
            StrokeDividerDefault = ColorF.FromRgba(0x00, 0x00, 0x00, 0x0F),
            StrokeSurfaceDefault = ColorF.FromRgba(0x75, 0x75, 0x75, 0x66),
            StrokeFlyoutDefault = ColorF.FromRgba(0x00, 0x00, 0x00, 0x17),
            StrokeControlOnAccentDefault = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x14),
            StrokeControlOnAccentSecondary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x66),
            StrokeControlOnAccentTertiary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x37),
            TextPrimary   = ColorF.FromRgba(0x00, 0x00, 0x00, 0xE4),
            TextSecondary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x9E),
            TextTertiary  = textTertiary,
            TextDisabled  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x5C),
            TextOnAccentPrimary   = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            TextOnAccentSecondary = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3),
            TextOnAccentDisabled  = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            TextOnAccentSelectedText = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            CaptionCloseHover   = ColorF.FromRgba(0xC4, 0x2B, 0x1C),
            CaptionClosePressed = ColorF.FromRgba(0xC4, 0x2B, 0x1C, 0xE6),
            AccentDefault = ColorF.FromRgba(0x00, 0x5F, 0xB8),
            AccentSecondary = ColorF.FromRgba(0x00, 0x5F, 0xB8, 0xE6),
            AccentTertiary  = ColorF.FromRgba(0x00, 0x5F, 0xB8, 0xCC),
            AccentTextPrimary   = ColorF.FromRgba(0x00, 0x42, 0x75),
            AccentTextSecondary = ColorF.FromRgba(0x00, 0x26, 0x42),
            AccentTextTertiary  = ColorF.FromRgba(0x00, 0x5F, 0xB8),
            AccentDisabled  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x37),
            AccentSubtle    = ColorF.FromRgba(0x00, 0x5F, 0xB8, 0x24),
            AccentSelectedTextBackground = ColorF.FromRgba(0x00, 0x78, 0xD4),
            FocusOuter = ColorF.FromRgba(0x00, 0x00, 0x00, 0xE4),
            FocusInner = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3),
            ScrollThumb = ColorF.FromRgba(0x00, 0x00, 0x00, 0x72),
            AcrylicTint = ColorF.FromRgba(0xFC, 0xFC, 0xFC, 0xD9),
            AcrylicBase = ColorF.FromRgba(0xF3, 0xF3, 0xF3, 0xE6),
            AcrylicFlyout = AcrylicSpec.FlyoutLight,
            HeroGradientTop = ColorF.FromRgba(0x7A, 0xB6, 0xE6, 0xB3),
            HeroGradientBottom = ColorF.FromRgba(0xF3, 0xF3, 0xF3, 0x00),
            FillControlAltSecondary  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x06),
            FillControlAltTertiary   = ColorF.FromRgba(0x00, 0x00, 0x00, 0x0F),
            FillControlAltQuaternary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x18),
            FillControlAltDisabled   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x00),
            StrokeControlStrongDefault  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x72),
            StrokeControlStrongDisabled = ColorF.FromRgba(0x00, 0x00, 0x00, 0x37),
            FillControlInputActive   = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            TextControlHeaderForeground         = ColorF.FromRgba(0x00, 0x00, 0x00),
            TextControlHeaderForegroundDisabled = ColorF.FromRgba(0x00, 0x00, 0x00, 0x66),
            TextControlDescriptionForeground    = ColorF.FromRgba(0x00, 0x00, 0x00, 0x99),
            TextControlForegroundDisabled       = ColorF.FromRgba(0x01, 0x01, 0x01, 0x5C),
            SystemFillCritical = ColorF.FromRgba(0xC4, 0x2B, 0x1C),
            SystemFillCaution  = ColorF.FromRgba(0x9D, 0x5D, 0x00),
            SystemFillSuccess  = ColorF.FromRgba(0x0C, 0x6B, 0x0C),
            SystemFillCriticalBackground  = ColorF.FromRgba(0xFD, 0xE7, 0xE9),
            SystemFillCautionBackground   = ColorF.FromRgba(0xFF, 0xF4, 0xCE),
            SystemFillSuccessBackground   = ColorF.FromRgba(0xDF, 0xF6, 0xDD),
            SystemFillAttentionBackground = ColorF.FromRgba(0xF6, 0xF6, 0xF6, 0x80),
            SystemFillSolidNeutral = ColorF.FromRgba(0x8A, 0x8A, 0x8A),
            TextInverse = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
            WindowBackground = solidBase,
        };
    }

    /// <summary>Phase A calibrated Warm light tokens (the regression anchor for seed derivation).</summary>
    static TokenSet BuildWarmLight() => new()
    {
        FillControlDefault   = ColorF.FromRgba(0xFC, 0xFB, 0xF9),
        FillControlSecondary = ColorF.FromRgba(0xF3, 0xF2, 0xEF),
        FillControlTertiary  = ColorF.FromRgba(0xEC, 0xEB, 0xE8),
        FillControlDisabled  = ColorF.FromRgba(0xF9, 0xF9, 0xF9, 0x4D),
        FillControlStrong         = ColorF.FromRgba(0x00, 0x00, 0x00, 0x72),
        FillControlStrongDisabled = ColorF.FromRgba(0x00, 0x00, 0x00, 0x51),
        FillControlSolid          = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        FillControlOnImage   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xC9),
        FillSubtleTransparent= ColorF.Transparent,
        FillSubtleSecondary  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x09),
        FillSubtleTertiary   = ColorF.FromRgba(0x00, 0x00, 0x00, 0x06),
        FillCardDefault      = ColorF.FromRgba(0xFC, 0xFB, 0xF9),
        FillCardSecondary    = ColorF.FromRgba(0xF7, 0xF6, 0xF3),
        FillLayerDefault     = ColorF.FromRgba(0xFA, 0xF9, 0xF6),
        FillLayerAlt         = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        LayerOnMicaBaseAlt   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3),
        FillSolidBase        = ColorF.FromRgba(0xEF, 0xEE, 0xEB),
        FillSolidBaseAlt     = ColorF.FromRgba(0xE8, 0xE7, 0xE3),
        FillSolidSecondary   = ColorF.FromRgba(0xEC, 0xEB, 0xE8),
        FillSolidTertiary    = ColorF.FromRgba(0xFB, 0xFA, 0xF8),
        FillSmoke            = ColorF.FromRgba(0x00, 0x00, 0x00, 0x4D),
        StrokeControlDefault = ColorF.FromRgba(0xDE, 0xDD, 0xD9),
        StrokeControlSecondary = ColorF.FromRgba(0xC9, 0xC8, 0xC3),
        StrokeCardDefault    = ColorF.FromRgba(0xDC, 0xDA, 0xD4),
        StrokeDividerDefault = ColorF.FromRgba(0xE3, 0xE2, 0xDF),
        StrokeSurfaceDefault = ColorF.FromRgba(0x75, 0x75, 0x75, 0x66),
        StrokeFlyoutDefault = ColorF.FromRgba(0x00, 0x00, 0x00, 0x17),   // edge definition for the near-solid light flyout plate
        StrokeControlOnAccentDefault = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x14),
        StrokeControlOnAccentSecondary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x66),
        StrokeControlOnAccentTertiary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x37),
        TextPrimary   = ColorF.FromRgba(0x1F, 0x1E, 0x1B),
        TextSecondary = ColorF.FromRgba(0x5C, 0x5B, 0x57),
        TextTertiary  = ColorF.FromRgba(0x65, 0x64, 0x60),
        TextDisabled  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x5C),
        TextOnAccentPrimary   = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        TextOnAccentSecondary = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3),
        TextOnAccentDisabled  = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        TextOnAccentSelectedText = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        CaptionCloseHover   = ColorF.FromRgba(0xC4, 0x2B, 0x1C),
        CaptionClosePressed = ColorF.FromRgba(0xC4, 0x2B, 0x1C, 0xE6),
        AccentDefault = ColorF.FromRgba(0x00, 0x5F, 0xB8),
        AccentSecondary = ColorF.FromRgba(0x00, 0x5F, 0xB8, 0xE6),
        AccentTertiary  = ColorF.FromRgba(0x00, 0x5F, 0xB8, 0xCC),
        AccentTextPrimary   = ColorF.FromRgba(0x00, 0x42, 0x75),
        AccentTextSecondary = ColorF.FromRgba(0x00, 0x26, 0x42),
        AccentTextTertiary  = ColorF.FromRgba(0x00, 0x5F, 0xB8),
        AccentDisabled  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x37),
        AccentSubtle    = ColorF.FromRgba(0x00, 0x5F, 0xB8, 0x24),
        AccentSelectedTextBackground = ColorF.FromRgba(0x00, 0x78, 0xD4),
        FocusOuter = ColorF.FromRgba(0x00, 0x00, 0x00, 0xE4),
        FocusInner = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3),
        ScrollThumb = ColorF.FromRgba(0x00, 0x00, 0x00, 0x72),
        AcrylicTint = ColorF.FromRgba(0xFC, 0xFC, 0xFC, 0xD9),
        AcrylicBase = ColorF.FromRgba(0xF3, 0xF3, 0xF3, 0xE6),
        AcrylicFlyout = AcrylicSpec.FlyoutLight,
        HeroGradientTop = ColorF.FromRgba(0x7A, 0xB6, 0xE6, 0xB3),
        HeroGradientBottom = ColorF.FromRgba(0xF3, 0xF3, 0xF3, 0x00),
        FillControlAltSecondary  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x06),
        FillControlAltTertiary   = ColorF.FromRgba(0x00, 0x00, 0x00, 0x0F),
        FillControlAltQuaternary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x18),
        FillControlAltDisabled   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x00),
        StrokeControlStrongDefault  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x72),
        StrokeControlStrongDisabled = ColorF.FromRgba(0x00, 0x00, 0x00, 0x37),
        FillControlInputActive   = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        TextControlHeaderForeground         = ColorF.FromRgba(0x00, 0x00, 0x00),
        TextControlHeaderForegroundDisabled = ColorF.FromRgba(0x00, 0x00, 0x00, 0x66),
        TextControlDescriptionForeground    = ColorF.FromRgba(0x00, 0x00, 0x00, 0x99),
        TextControlForegroundDisabled       = ColorF.FromRgba(0x01, 0x01, 0x01, 0x5C),
        SystemFillCritical = ColorF.FromRgba(0xC4, 0x2B, 0x1C),
        SystemFillCaution  = ColorF.FromRgba(0x9D, 0x5D, 0x00),
        SystemFillSuccess  = ColorF.FromRgba(0x0C, 0x6B, 0x0C),
        SystemFillCriticalBackground  = ColorF.FromRgba(0xFD, 0xE7, 0xE9),
        SystemFillCautionBackground   = ColorF.FromRgba(0xFF, 0xF4, 0xCE),
        SystemFillSuccessBackground   = ColorF.FromRgba(0xDF, 0xF6, 0xDD),
        SystemFillAttentionBackground = ColorF.FromRgba(0xF6, 0xF6, 0xF6, 0x80),
        SystemFillSolidNeutral = ColorF.FromRgba(0x8A, 0x8A, 0x8A),
        TextInverse = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        WindowBackground = ColorF.FromRgba(0xE9, 0xE7, 0xE2),
    };

    /// <summary>Channel tolerance check for Warm calibration tests.</summary>
    public static bool NearColor(in ColorF a, in ColorF b, int tolerance = 3)
    {
        static int Ch(float f) => (int)MathF.Round(f * 255f);
        return Math.Abs(Ch(a.R) - Ch(b.R)) <= tolerance
            && Math.Abs(Ch(a.G) - Ch(b.G)) <= tolerance
            && Math.Abs(Ch(a.B) - Ch(b.B)) <= tolerance;
    }
}
