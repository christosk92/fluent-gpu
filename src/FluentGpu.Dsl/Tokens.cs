using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

public enum ThemeKind : byte { Light = 0, Dark = 1 }   // HighContrast reserved for the full FluentGpu.Theme subsystem

/// <summary>
/// An immutable, baked palette of every semantic Fluent brush for one theme (mirrors WinUI's *_themeresources). Built
/// once per theme; never mutated. Read through <see cref="Tok"/>, which swaps the active set with a single pointer write.
/// </summary>
public sealed record TokenSet
{
    // Fill hierarchy (control backgrounds, layered translucency over the window base)
    public required ColorF FillControlDefault { get; init; }
    public required ColorF FillControlSecondary { get; init; }   // hover
    public required ColorF FillControlTertiary { get; init; }    // pressed
    public required ColorF FillControlDisabled { get; init; }
    public required ColorF FillControlStrong { get; init; }          // ControlStrongFillColorDefault — slider rail, scrollbar thumb
    public required ColorF FillControlStrongDisabled { get; init; }  // ControlStrongFillColorDisabled
    public required ColorF FillControlSolid { get; init; }           // ControlSolidFillColorDefault — slider thumb ring
    public required ColorF FillSubtleTransparent { get; init; }
    public required ColorF FillSubtleSecondary { get; init; }    // subtle hover
    public required ColorF FillSubtleTertiary { get; init; }     // subtle pressed
    public required ColorF FillCardDefault { get; init; }
    public required ColorF FillCardSecondary { get; init; }
    public required ColorF FillLayerDefault { get; init; }       // flyout / expander body
    public required ColorF FillSolidBase { get; init; }          // page background
    public required ColorF FillSolidBaseAlt { get; init; }       // pane / lower surface
    public required ColorF FillSolidSecondary { get; init; }
    public required ColorF FillSmoke { get; init; }              // dialog / overlay scrim

    // Stroke hierarchy
    public required ColorF StrokeControlDefault { get; init; }
    public required ColorF StrokeControlSecondary { get; init; }
    public required ColorF StrokeCardDefault { get; init; }
    public required ColorF StrokeDividerDefault { get; init; }
    public required ColorF StrokeSurfaceDefault { get; init; }
    public required ColorF StrokeFlyoutDefault { get; init; }    // SurfaceStrokeColorFlyout — flyout/menu border
    public required ColorF StrokeControlOnAccentDefault { get; init; }
    public required ColorF StrokeControlOnAccentSecondary { get; init; }   // top stop of the accent elevation border gradient

    // Text hierarchy
    public required ColorF TextPrimary { get; init; }
    public required ColorF TextSecondary { get; init; }
    public required ColorF TextTertiary { get; init; }
    public required ColorF TextDisabled { get; init; }
    public required ColorF TextOnAccentPrimary { get; init; }
    public required ColorF TextOnAccentSecondary { get; init; }
    public required ColorF TextOnAccentDisabled { get; init; }

    // Accent (base; live OS accent folds in via Tok overrides)
    public required ColorF AccentDefault { get; init; }
    public required ColorF AccentSecondary { get; init; }
    public required ColorF AccentTertiary { get; init; }
    public required ColorF AccentDisabled { get; init; }
    public required ColorF AccentSubtle { get; init; }           // accent @ ~.16 (nav selection, info card)

    // Focus
    public required ColorF FocusOuter { get; init; }
    public required ColorF FocusInner { get; init; }

    // Scroll / acrylic
    public required ColorF ScrollThumb { get; init; }
    public required ColorF AcrylicTint { get; init; }
    public required ColorF AcrylicBase { get; init; }

    // Hero gradient stops
    public required ColorF HeroGradientTop { get; init; }
    public required ColorF HeroGradientBottom { get; init; }

    // Window
    public required ColorF WindowBackground { get; init; }
}

/// <summary>
/// The active design-token table + the theme switch. Author UI reads tokens via the static getters (e.g.
/// <c>Tok.FillCardDefault</c>); they resolve against <see cref="T"/> at zero per-read cost. <see cref="Use"/> re-themes
/// the whole app with one pointer write. The OS accent + Mica window background are mutable overrides on top of the set,
/// so they survive a theme swap and let the host inject the live system accent at startup.
/// </summary>
public static class Tok
{
    public static readonly TokenSet Dark = BuildDark();
    public static readonly TokenSet Light = BuildLight();

    public static TokenSet T = Dark;
    public static ThemeKind Theme { get; private set; } = ThemeKind.Dark;

    private static ColorF? _accent;       // live OS accent override (folds into AccentDefault/Secondary/Tertiary/Subtle)
    private static ColorF? _windowBg;     // Mica → Transparent override

    public static void Use(ThemeKind kind) { Theme = kind; T = kind == ThemeKind.Light ? Light : Dark; }

    /// <summary>Inject the OS accent (host startup). Derived shades recompute from it.</summary>
    public static void SetAccent(ColorF c) => _accent = c;
    public static void SetWindowBackground(ColorF c) => _windowBg = c;

    // Fill
    public static ColorF FillControlDefault => T.FillControlDefault;
    public static ColorF FillControlSecondary => T.FillControlSecondary;
    public static ColorF FillControlTertiary => T.FillControlTertiary;
    public static ColorF FillControlDisabled => T.FillControlDisabled;
    public static ColorF FillControlStrong => T.FillControlStrong;
    public static ColorF FillControlStrongDisabled => T.FillControlStrongDisabled;
    public static ColorF FillControlSolid => T.FillControlSolid;
    public static ColorF FillSubtleTransparent => T.FillSubtleTransparent;
    public static ColorF FillSubtleSecondary => T.FillSubtleSecondary;
    public static ColorF FillSubtleTertiary => T.FillSubtleTertiary;
    public static ColorF FillCardDefault => T.FillCardDefault;
    public static ColorF FillCardSecondary => T.FillCardSecondary;
    public static ColorF FillLayerDefault => T.FillLayerDefault;
    public static ColorF FillSolidBase => T.FillSolidBase;
    public static ColorF FillSolidBaseAlt => T.FillSolidBaseAlt;
    public static ColorF FillSolidSecondary => T.FillSolidSecondary;
    public static ColorF FillSmoke => T.FillSmoke;

    // Stroke
    public static ColorF StrokeControlDefault => T.StrokeControlDefault;
    public static ColorF StrokeControlSecondary => T.StrokeControlSecondary;
    public static ColorF StrokeCardDefault => T.StrokeCardDefault;
    public static ColorF StrokeDividerDefault => T.StrokeDividerDefault;
    public static ColorF StrokeSurfaceDefault => T.StrokeSurfaceDefault;
    public static ColorF StrokeFlyoutDefault => T.StrokeFlyoutDefault;
    public static ColorF StrokeControlOnAccentDefault => T.StrokeControlOnAccentDefault;
    public static ColorF StrokeControlOnAccentSecondary => T.StrokeControlOnAccentSecondary;

    /// <summary>WinUI ControlElevationBorderBrush: a 2-stop vertical gradient (secondary edge → default edge) — the
    /// signature Fluent control border. Theme-aware; pass to BoxEl.BorderBrush. (Allocated at reconcile time, not per frame.)</summary>
    public static GradientSpec ControlElevationBorder =>
        new(GradientShape.Linear, 90f, [new GradientStop(0.33f, StrokeControlSecondary), new GradientStop(1f, StrokeControlDefault)]);

    /// <summary>WinUI AccentControlElevationBorderBrush: the on-accent variant (for accent buttons / checked toggles).</summary>
    public static GradientSpec AccentControlElevationBorder =>
        new(GradientShape.Linear, 90f, [new GradientStop(0.33f, StrokeControlOnAccentSecondary), new GradientStop(1f, StrokeControlOnAccentDefault)]);

    // Text
    public static ColorF TextPrimary => T.TextPrimary;
    public static ColorF TextSecondary => T.TextSecondary;
    public static ColorF TextTertiary => T.TextTertiary;
    public static ColorF TextDisabled => T.TextDisabled;
    public static ColorF TextOnAccentPrimary => T.TextOnAccentPrimary;
    public static ColorF TextOnAccentSecondary => T.TextOnAccentSecondary;
    public static ColorF TextOnAccentDisabled => T.TextOnAccentDisabled;

    // Accent (override-aware)
    public static ColorF AccentDefault => _accent ?? T.AccentDefault;
    public static ColorF AccentSecondary => _accent is { } a ? a with { A = 0.90f } : T.AccentSecondary;
    public static ColorF AccentTertiary => _accent is { } a ? a with { A = 0.80f } : T.AccentTertiary;
    public static ColorF AccentDisabled => T.AccentDisabled;
    public static ColorF AccentSubtle => _accent is { } a ? a with { A = 0.16f } : T.AccentSubtle;

    // Focus
    public static ColorF FocusOuter => T.FocusOuter;
    public static ColorF FocusInner => T.FocusInner;
    public const float FocusThickness = 2f;

    // Scroll / acrylic / hero
    public static ColorF ScrollThumb => T.ScrollThumb;
    public static ColorF AcrylicTint => T.AcrylicTint;
    public static ColorF AcrylicBase => T.AcrylicBase;
    public static ColorF HeroGradientTop => _accent is { } a ? a with { A = 0.55f } : T.HeroGradientTop;
    public static ColorF HeroGradientBottom => T.HeroGradientBottom;

    public static ColorF WindowBackground => _windowBg ?? T.WindowBackground;

    private static TokenSet BuildDark() => new()
    {
        FillControlDefault   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0F),
        FillControlSecondary = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x15),
        FillControlTertiary  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x08),
        FillControlDisabled  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0B),
        FillControlStrong         = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x8B),
        FillControlStrongDisabled = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x3F),
        FillControlSolid          = ColorF.FromRgba(0x45, 0x45, 0x45),
        FillSubtleTransparent= ColorF.Transparent,
        FillSubtleSecondary  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0F),
        FillSubtleTertiary   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0A),
        FillCardDefault      = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x0D),
        FillCardSecondary    = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x08),
        FillLayerDefault     = ColorF.FromRgba(0x3A, 0x3A, 0x3A, 0x4C),
        FillSolidBase        = ColorF.FromRgba(0x20, 0x20, 0x20),
        FillSolidBaseAlt     = ColorF.FromRgba(0x1C, 0x1C, 0x1C),
        FillSolidSecondary   = ColorF.FromRgba(0x28, 0x28, 0x28),
        FillSmoke            = ColorF.FromRgba(0x00, 0x00, 0x00, 0x4D),
        StrokeControlDefault = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x12),
        StrokeControlSecondary = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x18),
        StrokeCardDefault    = ColorF.FromRgba(0x00, 0x00, 0x00, 0x19),
        StrokeDividerDefault = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x15),
        StrokeSurfaceDefault = ColorF.FromRgba(0x75, 0x75, 0x75, 0x66),
        StrokeFlyoutDefault = ColorF.FromRgba(0x00, 0x00, 0x00, 0x33),   // SurfaceStrokeColorFlyout (dark): 20% black
        StrokeControlOnAccentDefault = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x14),
        StrokeControlOnAccentSecondary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x23),
        TextPrimary   = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        TextSecondary = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xC5),
        TextTertiary  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x87),
        TextDisabled  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x5D),
        TextOnAccentPrimary   = ColorF.FromRgba(0x00, 0x00, 0x00),
        TextOnAccentSecondary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x80),
        TextOnAccentDisabled  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x87),
        AccentDefault = ColorF.FromRgba(0x60, 0xCD, 0xFF),
        AccentSecondary = ColorF.FromRgba(0x60, 0xCD, 0xFF, 0xE6),
        AccentTertiary  = ColorF.FromRgba(0x60, 0xCD, 0xFF, 0xCC),
        AccentDisabled  = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x29),
        AccentSubtle    = ColorF.FromRgba(0x60, 0xCD, 0xFF, 0x29),
        FocusOuter = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        FocusInner = ColorF.FromRgba(0x00, 0x00, 0x00, 0xB3),
        ScrollThumb = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x8B),   // == FillControlStrong (WinUI ControlStrongFillColorDefault)
        AcrylicTint = ColorF.FromRgba(0x2C, 0x2C, 0x2C, 0xCC),
        AcrylicBase = ColorF.FromRgba(0x20, 0x20, 0x20, 0xD9),
        HeroGradientTop = ColorF.FromRgba(0x2A, 0x4A, 0x66, 0xCC),
        HeroGradientBottom = ColorF.FromRgba(0x20, 0x20, 0x20, 0x00),
        WindowBackground = ColorF.FromRgba(0x20, 0x20, 0x20),
    };

    private static TokenSet BuildLight() => new()
    {
        FillControlDefault   = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3),
        FillControlSecondary = ColorF.FromRgba(0xF9, 0xF9, 0xF9, 0x80),
        FillControlTertiary  = ColorF.FromRgba(0xF9, 0xF9, 0xF9, 0x4D),
        FillControlDisabled  = ColorF.FromRgba(0xF9, 0xF9, 0xF9, 0x4D),
        FillControlStrong         = ColorF.FromRgba(0x00, 0x00, 0x00, 0x72),
        FillControlStrongDisabled = ColorF.FromRgba(0x00, 0x00, 0x00, 0x51),
        FillControlSolid          = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        FillSubtleTransparent= ColorF.Transparent,
        FillSubtleSecondary  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x09),
        FillSubtleTertiary   = ColorF.FromRgba(0x00, 0x00, 0x00, 0x06),
        FillCardDefault      = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3),
        FillCardSecondary    = ColorF.FromRgba(0xF6, 0xF6, 0xF6, 0x80),
        FillLayerDefault     = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x80),
        FillSolidBase        = ColorF.FromRgba(0xF3, 0xF3, 0xF3),
        FillSolidBaseAlt     = ColorF.FromRgba(0xEB, 0xEB, 0xEB),
        FillSolidSecondary   = ColorF.FromRgba(0xEE, 0xEE, 0xEE),
        FillSmoke            = ColorF.FromRgba(0x00, 0x00, 0x00, 0x4D),
        StrokeControlDefault = ColorF.FromRgba(0x00, 0x00, 0x00, 0x0F),
        StrokeControlSecondary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x29),
        StrokeCardDefault    = ColorF.FromRgba(0x00, 0x00, 0x00, 0x0F),
        StrokeDividerDefault = ColorF.FromRgba(0x00, 0x00, 0x00, 0x0F),
        StrokeSurfaceDefault = ColorF.FromRgba(0x75, 0x75, 0x75, 0x66),
        StrokeFlyoutDefault = ColorF.FromRgba(0x00, 0x00, 0x00, 0x0F),   // SurfaceStrokeColorFlyout (light): 6% black
        StrokeControlOnAccentDefault = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x14),
        StrokeControlOnAccentSecondary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x66),
        TextPrimary   = ColorF.FromRgba(0x00, 0x00, 0x00, 0xE4),
        TextSecondary = ColorF.FromRgba(0x00, 0x00, 0x00, 0x9E),
        TextTertiary  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x72),
        TextDisabled  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x5C),
        TextOnAccentPrimary   = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        TextOnAccentSecondary = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3),
        TextOnAccentDisabled  = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        AccentDefault = ColorF.FromRgba(0x00, 0x5F, 0xB8),
        AccentSecondary = ColorF.FromRgba(0x00, 0x5F, 0xB8, 0xE6),
        AccentTertiary  = ColorF.FromRgba(0x00, 0x5F, 0xB8, 0xCC),
        AccentDisabled  = ColorF.FromRgba(0x00, 0x00, 0x00, 0x37),
        AccentSubtle    = ColorF.FromRgba(0x00, 0x5F, 0xB8, 0x24),
        FocusOuter = ColorF.FromRgba(0x00, 0x00, 0x00, 0xE4),
        FocusInner = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        ScrollThumb = ColorF.FromRgba(0x00, 0x00, 0x00, 0x72),
        AcrylicTint = ColorF.FromRgba(0xFC, 0xFC, 0xFC, 0xD9),
        AcrylicBase = ColorF.FromRgba(0xF3, 0xF3, 0xF3, 0xE6),
        HeroGradientTop = ColorF.FromRgba(0x7A, 0xB6, 0xE6, 0xB3),
        HeroGradientBottom = ColorF.FromRgba(0xF3, 0xF3, 0xF3, 0x00),
        WindowBackground = ColorF.FromRgba(0xF3, 0xF3, 0xF3),
    };
}
