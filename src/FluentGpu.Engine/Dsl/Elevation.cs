using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>
/// Fluent elevation presets — the engine's ANALYTIC drop shadows standing in for WinUI's ThemeShadow Z-depth model
/// (we own every pixel; documented visual-equivalent to the DComp shadow). WinUI elevates popups by Z translation —
/// base depth 32 + 8 per nested tier (ElevationHelper.cpp:19-21 s_elevationBaseDepth/s_elevationIterativeDepth) — and
/// the design maps the popup bands to three depth CLASSES: ToolTip (lightest, depth-16 class) &lt; Flyout/Menu
/// (depth-32 class) &lt; Dialog (depth-128 class). The preset blur/offset/alpha values keep the engine's existing
/// visual scale; the CLASS a control uses is the contract: ToolTip → <see cref="Tooltip"/>;
/// Flyout/MenuFlyout/CommandBarFlyout/TeachingTip → <see cref="Flyout"/>; ContentDialog → <see cref="Dialog"/>.
/// Use via <c>.Elevate(Elevation.Card)</c> (Modifiers) or <c>Ui.Card(...)</c> which carries <see cref="Card"/> by default.
/// <para>The values live in the active theme definition's <see cref="FluentGpu.Theming.ElevationSet"/> (one ramp per theme
/// kind); every member here resolves against <see cref="Tok.Definition"/> and <see cref="Tok.Theme"/> when read.</para>
/// </summary>
public static class Elevation
{
    public static readonly ShadowSpec None = default;
    public static ShadowSpec Card => Ramp.Card;
    public static ShadowSpec CardHover => Ramp.CardHover;
    /// <summary>ToolTip band (WinUI depth-16 class) — lighter than <see cref="Flyout"/>; tooltips sit on the lowest popup band.</summary>
    public static ShadowSpec Tooltip => Ramp.Tooltip;
    /// <summary>Flyout/menu band (WinUI depth-32 class). The WinUI popup DropShadowRecipe at elevation 16 is directional-
    /// only (ambient off): blur = elevation = 16, OffsetY = elevation·0.5 = 8, opacity 0.26 dark / 0.14 light
    /// (DropShadowRecipe.h:118-132,151-152). Theme-split here; only the IN-WINDOW flyout fallback uses this — OS-backed
    /// windowed menus get DWM's own system shadow.</summary>
    public static ShadowSpec Flyout => Ramp.Flyout;
    /// <summary>Player dock band — upward cast shadow so the transport reads as floating above the page.</summary>
    public static ShadowSpec DockTop => Ramp.DockTop;
    /// <summary>Dialog band (WinUI depth-128 class) — ContentDialog and full-window modal surfaces.</summary>
    public static ShadowSpec Dialog => Ramp.Dialog;

    private static FluentGpu.Theming.ElevationRamp Ramp
    {
        get
        {
            var set = Tok.Definition.Elevation;
            return Tok.Theme == ThemeKind.Dark ? set.Dark : set.Light;
        }
    }
}
