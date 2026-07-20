using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

public enum ThemeKind : byte { Light = 0, Dark = 1 }   // HighContrast reserved for the full FluentGpu.Theme subsystem

/// <summary>
/// The seven OS-authored accent shades WinUI keys every accent brush off (<c>SystemAccentColor</c> + <c>Light1..3</c> +
/// <c>Dark1..3</c>). The live system accent supplies these EXACTLY (<c>IUISettings3.GetColorValue</c>, host-read); a
/// custom / album-extracted accent has only a <see cref="Base"/>, so <see cref="Derive"/> synthesizes the ramp with
/// WinUI's own commented "alpha-blend over black / white" approximation:
/// <list type="bullet">
/// <item><c>Dark{1,2,3}</c> = <see cref="Base"/> multiplied per channel by <c>{0.75, 0.55, 0.315}</c> (blend toward BLACK).</item>
/// <item><c>Light{1,2,3}</c> = <see cref="Base"/> blended toward WHITE by <c>{0.26, 0.48, 0.68}</c>.</item>
/// </list>
/// Calibration against the WinUI default accent <c>#0078D4</c> (its <c>SystemThemingInterop.cpp</c> test fallback):
/// derived <c>Dark1 #005A9F · Dark2 #004275 · Dark3 #002643 · Light1 #429BDF · Light2 #7AB9E9 · Light3 #ADD4F1</c>
/// vs WinUI <c>Dark1 #005A9E · Dark2 #004275 · Dark3 #002642 · Light1 #429CE3 · Light2 #76B9ED · Light3 #A6D8FF</c> —
/// the DARK ramp reproduces WinUI's within a rounding LSB; the LIGHT ramp is the documented approximation (a few LSBs
/// off, and irrelevant for custom colors, which have no OS-authored ramp at all). Every shade is opaque (A = 1); the
/// alpha-reduced fills (<c>AccentSecondary/Tertiary/Subtle</c>) apply their alpha at read time in <see cref="Tok"/>.
/// </summary>
public readonly record struct AccentRamp(
    ColorF Base, ColorF Light1, ColorF Light2, ColorF Light3, ColorF Dark1, ColorF Dark2, ColorF Dark3)
{
    /// <summary>Synthesize the full ramp from a single base color (see the type remarks for the WinUI approximation).</summary>
    public static AccentRamp Derive(ColorF @base)
    {
        var b = @base with { A = 1f };
        static ColorF Darker(ColorF c, float f) => new(c.R * f, c.G * f, c.B * f, 1f);                 // blend toward black
        static ColorF Lighter(ColorF c, float f) => new(c.R + (1f - c.R) * f, c.G + (1f - c.G) * f, c.B + (1f - c.B) * f, 1f); // toward white
        return new AccentRamp(b,
            Light1: Lighter(b, 0.26f), Light2: Lighter(b, 0.48f), Light3: Lighter(b, 0.68f),
            Dark1:  Darker(b, 0.75f),  Dark2:  Darker(b, 0.55f),  Dark3:  Darker(b, 0.315f));
    }
}

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
    // WinUI ControlOnImageFillColorDefault — near-solid chrome floated over media (the ItemContainer multi-select
    // checkbox unchecked plate). Common_themeresources_any.xaml:34 dark #B31C1C1C / :238 light #C9FFFFFF.
    public required ColorF FillControlOnImage { get; init; }
    public required ColorF FillSubtleTransparent { get; init; }
    public required ColorF FillSubtleSecondary { get; init; }    // subtle hover
    public required ColorF FillSubtleTertiary { get; init; }     // subtle pressed
    public required ColorF FillCardDefault { get; init; }
    public required ColorF FillCardSecondary { get; init; }
    public required ColorF FillLayerDefault { get; init; }       // flyout / expander body
    public required ColorF FillLayerAlt { get; init; }           // WinUI LayerFillColorAltBrush (ContentDialog top overlay)
    // WinUI LayerOnMicaBaseAltFillColorDefault — the translucent "layer over Mica" chrome material (nav pane / address
    // bar / selected tab). THEME-AWARE: dark #733A3A3A (a translucent dark layer), light #B3FFFFFF (translucent white).
    public required ColorF LayerOnMicaBaseAlt { get; init; }
    public required ColorF FillSolidBase { get; init; }          // page background
    public required ColorF FillSolidBaseAlt { get; init; }       // pane / lower surface
    public required ColorF FillSolidSecondary { get; init; }
    public required ColorF FillSolidTertiary { get; init; }
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
    public required ColorF StrokeControlOnAccentTertiary { get; init; }    // checked SplitButton divider (#37000000)

    // Text hierarchy
    public required ColorF TextPrimary { get; init; }
    public required ColorF TextSecondary { get; init; }
    public required ColorF TextTertiary { get; init; }
    public required ColorF TextDisabled { get; init; }
    public required ColorF TextOnAccentPrimary { get; init; }
    public required ColorF TextOnAccentSecondary { get; init; }
    public required ColorF TextOnAccentDisabled { get; init; }
    // WinUI TextOnAccentFillColorSelectedText — the recolored glyphs inside a text selection. #FFFFFF in BOTH themes
    // (NOT TextOnAccentPrimary, which is black in dark): the selection plate is the accent BASE (#0078D4-class, dark
    // blue) in both themes, so the selected text stays white in both.
    public required ColorF TextOnAccentSelectedText { get; init; }

    // Custom-titlebar caption buttons (engine-drawn min/max/close). The close hover/press red is the Win11 SHELL
    // caption red — no WinUI XAML resource exists (system caption buttons are shell-drawn); same value in BOTH themes.
    public required ColorF CaptionCloseHover { get; init; }      // #C42B1C opaque, white glyph on top
    public required ColorF CaptionClosePressed { get; init; }    // #C42B1C @ 0.9

    // Accent (base; live OS accent folds in via Tok overrides)
    public required ColorF AccentDefault { get; init; }
    public required ColorF AccentSecondary { get; init; }
    public required ColorF AccentTertiary { get; init; }
    public required ColorF AccentDisabled { get; init; }
    public required ColorF AccentSubtle { get; init; }           // accent @ ~.16 (nav selection, info card)
    // Accent TEXT (distinct from the accent FILL above): WinUI AccentTextFillColor{Primary,Secondary,Tertiary} =
    // SystemAccentColorLight3/Light3/Light2 (dark) and Dark2/Dark3/Dark1 (light). Used for link text (HyperlinkButton),
    // accent labels — readability-adjusted and SOLID (never the alpha-reduced fill). Disabled = TextDisabled.
    public required ColorF AccentTextPrimary { get; init; }
    public required ColorF AccentTextSecondary { get; init; }
    public required ColorF AccentTextTertiary { get; init; }
    // WinUI AccentFillColorSelectedTextBackground (→ TextControlSelectionHighlightColor): the text-selection plate.
    // Bound to the system accent BASE (SystemAccentColor, #FF0078D4 default) in BOTH themes — not the theme-shifted
    // Light2/Dark1 fill the other accent tokens use.
    public required ColorF AccentSelectedTextBackground { get; init; }

    // Focus
    public required ColorF FocusOuter { get; init; }
    public required ColorF FocusInner { get; init; }

    // Scroll / acrylic
    public required ColorF ScrollThumb { get; init; }
    public required ColorF AcrylicTint { get; init; }
    public required ColorF AcrylicBase { get; init; }
    /// <summary>The ONE WinUI default transient-surface acrylic (AcrylicInAppFillColorDefaultBrush ==
    /// AcrylicBackgroundFillColorDefaultBrush, AcrylicBrush_themeresources.xaml): every flyout-family surface carries
    /// it — FlyoutPresenter (FlyoutPresenter_themeresources.xaml:5/15), MenuFlyoutPresenter (via the
    /// AcrylicBackgroundFillColorDefaultBackdrop system backdrop, MenuFlyout_themeresources.xaml:264+271), the
    /// ComboBox PopupBorder (ComboBox_themeresources.xaml:63/273), the AutoSuggestBox SuggestionsContainer
    /// (AutoSuggestBox_themeresources.xaml:5/17) and ToolTip (ToolTipBackgroundBrush, ToolTip_themeresources.xaml:14/40).
    /// Dark: tint #2C2C2C @ 0.15, luminosity 0.96, FALLBACK #2C2C2C; Light: tint #FCFCFC @ 0.0, luminosity 0.85,
    /// FALLBACK #F9F9F9. The Fallback is the solid paint when blur is unavailable (AcrylicBrush FallbackColor).</summary>
    public required AcrylicSpec AcrylicFlyout { get; init; }

    // Hero gradient stops
    public required ColorF HeroGradientTop { get; init; }
    public required ColorF HeroGradientBottom { get; init; }

    // Control-alt fill hierarchy (WinUI ControlAltFillColor* — hollow/secondary control surfaces: checkbox/radio/
    // toggle OFF box+ellipse fill, combo placeholder, person-picture ellipse). Distinct from the FillControl* ramp.
    public required ColorF FillControlAltSecondary { get; init; }
    public required ColorF FillControlAltTertiary { get; init; }    // alt hover
    public required ColorF FillControlAltQuaternary { get; init; }  // alt pressed
    public required ColorF FillControlAltDisabled { get; init; }

    // Strong STROKE (WinUI ControlStrongStrokeColor* — the control outer-ring stroke: checkbox/radio/toggle/combo
    // border. The fill counterpart is FillControlStrong; this is the stroke variant we were missing.)
    public required ColorF StrokeControlStrongDefault { get; init; }
    public required ColorF StrokeControlStrongDisabled { get; init; }

    // Input-active fill (WinUI ControlFillColorInputActive — focused text-control body: TextBox/AutoSuggest/NumberBox)
    public required ColorF FillControlInputActive { get; init; }

    // Text-control chrome (TextBox/PasswordBox/NumberBox/AutoSuggestBox header + description + disabled text).
    // WinUI TextControlHeaderForeground = SystemControlForegroundBaseHighBrush = SystemBaseHighColor
    // (generic.xaml:886; color generic.xaml:207 dark / :4132 light).
    public required ColorF TextControlHeaderForeground { get; init; }
    // WinUI TextControlHeaderForegroundDisabled = SystemControlDisabledBaseMediumLowBrush = SystemBaseMediumLowColor
    // (generic.xaml:887; color generic.xaml:211 dark / :4136 light).
    public required ColorF TextControlHeaderForegroundDisabled { get; init; }
    // WinUI SystemControlDescriptionTextForegroundBrush = SystemControlPageTextBaseMediumBrush = SystemBaseMediumColor
    // (generic.xaml:327; color generic.xaml:209 dark / :4134 light) — the DescriptionPresenter row of every text control.
    public required ColorF TextControlDescriptionForeground { get; init; }
    // WinUI TextControlForegroundDisabled = TemporaryTextFillColorDisabled (TextBox_themeresources.xaml:22 dark /
    // :129 light) — the disabled field TEXT; note it is NOT TextFillColorDisabled (the disabled PLACEHOLDER token).
    public required ColorF TextControlForegroundDisabled { get; init; }

    // Severity palette (WinUI SystemFillColor* — InfoBar/InfoBadge/TeachingTip). Saturated icon color + tinted
    // background per severity. Attention (informational) follows the system accent, exposed as Tok.SystemFillAttention.
    public required ColorF SystemFillCritical { get; init; }
    public required ColorF SystemFillCaution { get; init; }
    public required ColorF SystemFillSuccess { get; init; }
    public required ColorF SystemFillCriticalBackground { get; init; }
    public required ColorF SystemFillCautionBackground { get; init; }
    public required ColorF SystemFillSuccessBackground { get; init; }
    public required ColorF SystemFillAttentionBackground { get; init; }
    public required ColorF SystemFillSolidNeutral { get; init; }   // WinUI SystemFillColorSolidNeutral (opaque gray) — InfoBadge Informational dot
    public required ColorF TextInverse { get; init; }    // WinUI TextFillColorInverse — text on a severity/inverse fill

    // Window
    public required ColorF WindowBackground { get; init; }
}

/// <summary>
/// The active design-token table + the theme switch. Author UI reads tokens via the static getters (e.g.
/// <c>Tok.FillCardDefault</c>); they resolve against <see cref="T"/> at zero per-read cost. <see cref="Use"/> re-themes
/// the whole app with one pointer write. The OS accent + Mica window background are mutable overrides on top of the set,
/// so they survive a theme swap and let the host inject the live system accent at startup.
/// </summary>
public static partial class Tok
{
    public static readonly ThemePalette WarmPalette = PaletteBuilder.Build(PaletteBuilder.Warm);
    public static readonly ThemePalette SlatePalette = PaletteBuilder.Build(PaletteBuilder.Slate);
    public static readonly ThemePalette NeutralPalette = PaletteBuilder.Build(PaletteBuilder.Neutral);
    public static ThemePalette AccentTintedPalette { get; private set; } = PaletteBuilder.Build(PaletteBuilder.AccentTinted(210f));

    /// <summary>All built-in presets (Accent-tinted excluded — rebuilt on accent injection).</summary>
    public static readonly ThemePalette[] Presets = [WarmPalette, SlatePalette, NeutralPalette];

    // Back-compat aliases — the Warm preset is the default calibrated ramp.
    public static TokenSet Dark => WarmPalette.Dark;
    public static TokenSet Light => WarmPalette.Light;

    static ThemePalette _palette = WarmPalette;
    public static ThemePalette Palette => _palette;

    public static TokenSet T = WarmPalette.Dark;
    public static ThemeKind Theme { get; private set; } = ThemeKind.Dark;

    /// <summary>Monotonic version bumped by every theme mutation (<see cref="Use"/>/<see cref="SetAccent"/>/
    /// <see cref="SetWindowBackground"/>). The host watches this counter and, on a change, re-renders every mounted
    /// component IN PLACE and cross-fades the resulting color diffs — a live, animated theme switch with no remount.
    /// A plain counter, NOT a <c>Signal</c>: <see cref="Tok"/> is a process-global static, so a signal would bind to a
    /// single host's reactive runtime and cross-contaminate multi-window / headless. The mutators are no-op-guarded so a
    /// redundant set (e.g. toggling to the already-active theme, re-injecting the same OS accent) doesn't churn a frame.</summary>
    public static int Epoch { get; private set; }

    // Live OS/developer accent override — the FULL ramp, so accent FILL/TEXT resolve THEME-AWARE (WinUI Dark1 in light,
    // Light2 in dark), not one flat color reused in both themes. Null = revert to the baked per-theme TokenSet accents.
    private static AccentRamp? _accentRamp;
    private static ColorF? _windowBg;     // Mica → Transparent override

    public static void Use(ThemeKind kind) => Use(_palette, kind);

    public static void Use(ThemePalette palette, ThemeKind kind)
    {
        if (palette == _palette && kind == Theme) return;
        _palette = palette;
        Theme = kind;
        T = kind == ThemeKind.Light ? palette.Light : palette.Dark;
        Epoch++;
    }

    public static ThemePalette? PaletteById(string id) => id switch
    {
        "warm" => WarmPalette,
        "slate" => SlatePalette,
        "neutral" => NeutralPalette,
        "accent" => AccentTintedPalette,
        _ => null,
    };

    /// <summary>Inject the OS accent (host startup) or a developer/album global override from a single base color; all
    /// accent tokens (fill + text + subtle + hero + attention) recompute from the <see cref="AccentRamp.Derive"/>d ramp,
    /// THEME-AWARE. Pass <c>null</c> to clear the override and revert to the baked per-theme accents.</summary>
    public static void SetAccent(ColorF? c)
    {
        if (c is { } cc) { var ramp = AccentRamp.Derive(cc); SetAccent(in ramp); }
        else if (_accentRamp is not null) { _accentRamp = null; Epoch++; }
    }

    /// <summary>Inject the EXACT OS-authored accent ramp (host startup via <c>IUISettings3.GetColorValue</c>) — the
    /// seven system shades, no approximation. Same effect as <see cref="SetAccent(ColorF?)"/> otherwise.</summary>
    public static void SetAccent(in AccentRamp ramp)
    {
        if (_accentRamp is { } cur && cur == ramp) return;
        _accentRamp = ramp;
        AccentTintedPalette = PaletteBuilder.BuildAccentTinted(ramp.Base);
        if (_palette.Id == "accent")
        {
            // Re-point the ACTIVE palette too, not just T — Tok.Palette feeds the app-shell chrome (WaveeColors
            // reads Palette.LightShell/DarkShell), which otherwise keeps serving the stale pre-injection accent
            // palette until the next Tok.Use happens to re-resolve it.
            _palette = AccentTintedPalette;
            T = Theme == ThemeKind.Light ? AccentTintedPalette.Light : AccentTintedPalette.Dark;
        }
        Epoch++;
    }
    public static void SetWindowBackground(ColorF c) { if (_windowBg == c) return; _windowBg = c; Epoch++; }

    // Fill + Stroke forwards (pure `=> T.X`) are GENERATED by TokAccessorGenerator (SourceGen/Engine) from the
    // TokenSet fields — every settable TokenSet property without a hand-written Tok getter below gets a forward,
    // so adding a TokenSet field can no longer silently do nothing. Only the getters WITH LOGIC stay hand-written
    // here (accent-aware shades, memoized GradientSpecs, theme-keyed IconBase/IconAlt, override-aware WindowBackground).

    // GEN-05 (docs/plans/source-generators-opportunity-investigation.md): these three elevation-border gradients
    // depend ONLY on the active theme, yet each `=>` getter allocated a fresh GradientSpec + a 2-element GradientStop[]
    // on EVERY read — at reconcile time, for every control that sets an elevation border. Memoize per Tok.Epoch (it
    // bumps on theme/accent change) so a steady theme returns the cached instance with zero Gen0. This is the report's
    // recommended in-place fix — the target is three getters in one file, NOT worth a Roslyn generator. The token
    // structs are immutable and reads are UI-thread, consistent with the existing Tok static model.
    private static int _ctrlElevEpoch = -1, _accentElevEpoch = -1, _circleElevEpoch = -1;
    private static GradientSpec _ctrlElev, _accentElev, _circleElev;

    /// <summary>WinUI ControlElevationBorderBrush: the signature Fluent control border — MappingMode="Absolute"
    /// StartPoint 0,0 EndPoint 0,3: the secondary→default blend lives in a 3-PHYSICAL-px band at one edge, the
    /// default stroke everywhere else (Common_themeresources_any.xaml:186-191 dark). LIGHT carries a ScaleTransform
    /// ScaleY=-1 (:382-390) anchoring the band (darker secondary edge) at the BOTTOM — encoded as AnchorEnd.
    /// Theme-aware; pass to BoxEl.BorderBrush. (Memoized per theme epoch — zero Gen0 on a steady theme.)</summary>
    public static GradientSpec ControlElevationBorder
    {
        get
        {
            if (_ctrlElevEpoch != Epoch)
            {
                _ctrlElev = new(GradientShape.Linear, 90f, [new GradientStop(0.33f, StrokeControlSecondary), new GradientStop(1f, StrokeControlDefault)])
                    { AxisLengthPx = 3f, AnchorEnd = Theme == ThemeKind.Light };
                _ctrlElevEpoch = Epoch;
            }
            return _ctrlElev;
        }
    }

    /// <summary>WinUI AccentControlElevationBorderBrush (accent buttons / checked toggles): the same absolute 3px
    /// band, mirrored (ScaleY=-1) in BOTH themes — the darker on-accent secondary edge sits at the BOTTOM
    /// (Common_themeresources_any.xaml:198-205 dark / :397-404 light). (Memoized per theme epoch.)</summary>
    public static GradientSpec AccentControlElevationBorder
    {
        get
        {
            if (_accentElevEpoch != Epoch)
            {
                _accentElev = new(GradientShape.Linear, 90f, [new GradientStop(0.33f, StrokeControlOnAccentSecondary), new GradientStop(1f, StrokeControlOnAccentDefault)])
                    { AxisLengthPx = 3f, AnchorEnd = true };
                _accentElevEpoch = Epoch;
            }
            return _accentElev;
        }
    }

    /// <summary>WinUI CircleElevationBorderBrush (Common_themeresources_any.xaml:192-198 dark / :391-397 light): a
    /// RelativeToBoundingBox vertical 2-stop gradient — ControlStrokeColorDefault @0.50 → ControlStrokeColorSecondary
    /// @0.70 — the subtle rim on circular knobs/glyphs (ToggleSwitch SwitchKnobOn stroke, RadioButton glyph stroke).
    /// (Memoized per theme epoch.)</summary>
    public static GradientSpec CircleElevationBorder
    {
        get
        {
            if (_circleElevEpoch != Epoch)
            {
                _circleElev = new(GradientShape.Linear, 90f, [new GradientStop(0.50f, StrokeControlDefault), new GradientStop(0.70f, StrokeControlSecondary)]);
                _circleElevEpoch = Epoch;
            }
            return _circleElev;
        }
    }

    // Text forwards (pure `=> T.X`) are GENERATED by TokAccessorGenerator.

    // ── On-media ink + scrim (theme-INVARIANT statics — theming.md's leaf-value rule). Ink/text sits over media
    // (album art / video / dark scrims), so it does NOT follow the light/dark theme. These are the canonical form of
    // Wavee's MediaCard literals (white-alpha ink 255 / 224 / 153; black-alpha chip/pill scrims ~0.55; the footer
    // gradient taken exactly from the editorial card). `static readonly` ⇒ one instance, zero per-read allocation.
    /// <summary>Primary on-media ink (titles over art/video): opaque white.</summary>
    public static readonly ColorF OnMediaPrimary = new(1f, 1f, 1f, 1f);
    /// <summary>Secondary on-media ink (subtitles / eyebrows / chip labels over art): white @ 0.80.</summary>
    public static readonly ColorF OnMediaSecondary = new(1f, 1f, 1f, 0.80f);
    /// <summary>Tertiary on-media ink (captions / meta over art): white @ 0.60.</summary>
    public static readonly ColorF OnMediaTertiary = new(1f, 1f, 1f, 0.60f);
    /// <summary>The chip/pill/FAB scrim plate floated over media (the MediaCard "…" / kind-chip / eq-pill fills): black @ 0.55.</summary>
    public static readonly ColorF MediaScrim = new(0f, 0f, 0f, 0.55f);

    /// <summary>Footer scrim (transparent at the top → black at the bottom) so ink pinned to the bottom of a media tile
    /// stays legible. The exact ramp from MediaCard's editorial footer gradient. Apply with <c>.Gradient(...)</c>.</summary>
    public static readonly GradientSpec ScrimBottom = new(GradientShape.Linear, 90f,
    [
        new GradientStop(0f, ColorF.Transparent),
        new GradientStop(0.36f, ColorF.Transparent),
        new GradientStop(0.66f, ColorF.FromRgba(0, 0, 0, 76)),
        new GradientStop(1f, ColorF.FromRgba(0, 0, 0, 224)),
    ]);
    /// <summary>Hero/top scrim (black at the top → transparent below) so top-pinned chrome over media stays legible.
    /// The gentler mirror of <see cref="ScrimBottom"/>. Apply with <c>.Gradient(...)</c>.</summary>
    public static readonly GradientSpec ScrimTop = new(GradientShape.Linear, 90f,
    [
        new GradientStop(0f, ColorF.FromRgba(0, 0, 0, 153)),
        new GradientStop(0.35f, ColorF.FromRgba(0, 0, 0, 51)),
        new GradientStop(1f, ColorF.Transparent),
    ]);

    // ── On-accent contrast (research adjustment #7 — computed at accent-SET time, NEVER at paint). SetAccent/Use bump
    // Epoch; this bakes the legible ink for the CURRENT accent fill ONCE per epoch (the ControlElevationBorder
    // memoize-per-Epoch pattern) so paint reads a field, never ratio math. Picker = ColorContrast.PickContrast
    // (near-black #161616 vs white) — the engine-blessed form of Wavee's WCAG on-accent picker.
    private static int _onAccentEpoch = -1;
    private static ColorF _onAccentInk;
    /// <summary>Recompute counter (gate.tok.onaccent-contrast asserts memoization): bumped once per epoch, never on a
    /// same-epoch read. Public so the cross-assembly VerticalSlice gate can assert no per-read recompute.</summary>
    public static int OnAccentComputeCount;
    /// <summary>The legible ink (near-black or white) for text/icons on the CURRENT accent fill
    /// (<see cref="AccentDefault"/>), chosen by the fill's WCAG luminance. Baked at accent-set time (memoized per
    /// <see cref="Epoch"/>) — the read is a field access, with ZERO paint-time contrast math.</summary>
    public static ColorF OnAccent
    {
        get
        {
            if (_onAccentEpoch != Epoch)
            {
                _onAccentInk = ColorContrast.PickContrast(AccentDefault);
                _onAccentEpoch = Epoch;
                OnAccentComputeCount++;
            }
            return _onAccentInk;
        }
    }

    // ThemedIcon layer roles (Files-app ThemedIcon theme brushes, MIT): the neutral Base fill and the translucent Alt
    // fill for layered vector icons. Theme-keyed constants (Files' exact values), resolved against the active Theme —
    // a bound icon Tint thunk reads these and re-fires on RethemeAll (Tok.Epoch bump), so an icon live-recolors with a
    // theme swap exactly like every token, with NO mask re-raster (the masks are colorless). NOT TokenSet fields: they
    // are fixed cross-palette values, and adding `required` props would ripple through every PaletteBuilder construction.
    /// <summary>ThemedIcon Base layer: dark #DBF0F0F0 (near-white @ .86) / light #DB161616 (near-black @ .86).</summary>
    public static ColorF IconBase => Theme == ThemeKind.Light
        ? new ColorF(0x16 / 255f, 0x16 / 255f, 0x16 / 255f, 0xDB / 255f)
        : new ColorF(0xF0 / 255f, 0xF0 / 255f, 0xF0 / 255f, 0xDB / 255f);
    /// <summary>ThemedIcon Alt layer: dark #66161616 (near-black @ .40) / light #66F0F0F0 (near-white @ .40).</summary>
    public static ColorF IconAlt => Theme == ThemeKind.Light
        ? new ColorF(0xF0 / 255f, 0xF0 / 255f, 0xF0 / 255f, 0x66 / 255f)
        : new ColorF(0x16 / 255f, 0x16 / 255f, 0x16 / 255f, 0x66 / 255f);

    // Custom-titlebar caption buttons (Win11 shell close-red; theme-invariant) — CaptionCloseHover/Pressed are GENERATED.

    // Accent (override-aware, THEME-AWARE). WinUI AccentFillColorDefault = SystemAccentColorDark1 (LIGHT theme) /
    // SystemAccentColorLight2 (DARK theme), opaque — the shade that fixes the light-theme accent bug (one flat color was
    // returned raw in both themes). Secondary/Tertiary/Subtle are the SAME shade at 0.90/0.80/0.16 alpha.
    private static ColorF AccentFillShade(in AccentRamp r) => Theme == ThemeKind.Light ? r.Dark1 : r.Light2;
    public static ColorF AccentDefault => _accentRamp is { } r ? AccentFillShade(r) : T.AccentDefault;
    public static ColorF AccentSecondary => _accentRamp is { } r ? AccentFillShade(r) with { A = 0.90f } : T.AccentSecondary;
    public static ColorF AccentTertiary => _accentRamp is { } r ? AccentFillShade(r) with { A = 0.80f } : T.AccentTertiary;
    // AccentDisabled (pure `=> T.AccentDisabled`, fixed #37000000 light / #28FFFFFF dark) is GENERATED.
    // Accent SUBTLE (nav selection / info card): the theme fill shade at low alpha — matching the baked TokenSet, which
    // keys subtle off the same shade as AccentDefault (light #005FB8·, dark #60CDFF·), NOT the raw base.
    public static ColorF AccentSubtle => _accentRamp is { } r ? AccentFillShade(r) with { A = 0.16f } : T.AccentSubtle;
    // Accent TEXT shades (link text / accent labels): WinUI AccentTextFillColor{Primary,Secondary,Tertiary} =
    // Dark2/Dark3/Dark1 (LIGHT) and Light3/Light3/Light2 (DARK). Opaque (never alpha-reduced — that would make link text
    // translucent). With no override, the exact WinUI default values baked in the TokenSet are used.
    public static ColorF AccentTextPrimary   => _accentRamp is { } r ? (Theme == ThemeKind.Light ? r.Dark2 : r.Light3) : T.AccentTextPrimary;
    public static ColorF AccentTextSecondary => _accentRamp is { } r ? (Theme == ThemeKind.Light ? r.Dark3 : r.Light3) : T.AccentTextSecondary;
    public static ColorF AccentTextTertiary  => _accentRamp is { } r ? (Theme == ThemeKind.Light ? r.Dark1 : r.Light2) : T.AccentTextTertiary;
    // Selection plate = the accent BASE itself (WinUI binds it to SystemAccentColor) in BOTH themes, so a live override
    // substitutes the ramp base directly.
    public static ColorF AccentSelectedTextBackground => _accentRamp is { } r ? r.Base : T.AccentSelectedTextBackground;

    // Focus: FocusOuter/FocusInner are GENERATED (pure forwards); FocusThickness is a const (kept).
    public const float FocusThickness = 2f;

    // Scroll / acrylic / hero: ScrollThumb / AcrylicTint / AcrylicBase / AcrylicFlyout / HeroGradientBottom are
    // GENERATED. HeroGradientTop stays hand-written (accent-aware: the OS/album accent base @ 0.55 when overridden).
    public static ColorF HeroGradientTop => _accentRamp is { } r ? r.Base with { A = 0.55f } : T.HeroGradientTop;

    // Control-alt fill, strong stroke + input-active, text-control chrome: all pure forwards — GENERATED.

    // Severity palette: Critical/Caution/Success + the *Background/SolidNeutral/TextInverse forwards are GENERATED.
    // SystemFillAttention follows the live OS accent (WinUI SystemFillColorAttention = SystemAccentColor*), so it stays
    // hand-written (there is no TokenSet.SystemFillAttention field — it maps to AccentDefault).
    public static ColorF SystemFillAttention => AccentDefault;

    // WindowBackground stays hand-written: the Mica→Transparent override (_windowBg) wins over the TokenSet field.
    public static ColorF WindowBackground => _windowBg ?? T.WindowBackground;
}
