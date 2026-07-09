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
/// </summary>
public static class Elevation
{
    public static readonly ShadowSpec None = default;
    public static ShadowSpec Card => Tok.Theme == ThemeKind.Dark ? CardDark : CardLight;
    private static readonly ShadowSpec CardDark = new(Blur: 8f, OffsetY: 2f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x33));
    // WinUI card Z=8 -> recipe elevation 4: blur 4, y 2, light directional opacity about 10%.
    private static readonly ShadowSpec CardLight = new(Blur: 4f, OffsetY: 2f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x1A));
    public static ShadowSpec CardHover => Tok.Theme == ThemeKind.Dark ? CardHoverDark : CardHoverLight;
    private static readonly ShadowSpec CardHoverDark = new(Blur: 16f, OffsetY: 4f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x40));
    private static readonly ShadowSpec CardHoverLight = new(Blur: 12f, OffsetY: 4f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x24));
    /// <summary>ToolTip band (WinUI depth-16 class) — lighter than <see cref="Flyout"/>; tooltips sit on the lowest popup band.</summary>
    public static ShadowSpec Tooltip => Tok.Theme == ThemeKind.Dark ? TooltipDark : TooltipLight;
    private static readonly ShadowSpec TooltipDark = new(Blur: 16f, OffsetY: 4f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x40));
    private static readonly ShadowSpec TooltipLight = new(Blur: 8f, OffsetY: 4f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x24));
    /// <summary>Flyout/menu band (WinUI depth-32 class). The WinUI popup DropShadowRecipe at elevation 16 is directional-
    /// only (ambient off): blur = elevation = 16, OffsetY = elevation·0.5 = 8, opacity 0.26 dark / 0.14 light
    /// (DropShadowRecipe.h:118-132,151-152). Theme-split here; only the IN-WINDOW flyout fallback uses this — OS-backed
    /// windowed menus get DWM's own system shadow.</summary>
    public static ShadowSpec Flyout => Tok.Theme == ThemeKind.Dark ? FlyoutDark : FlyoutLight;
    private static readonly ShadowSpec FlyoutDark = new(Blur: 16f, OffsetY: 8f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x42));   // ~0.26
    private static readonly ShadowSpec FlyoutLight = new(Blur: 16f, OffsetY: 8f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x24));  // ~0.14
    /// <summary>Player dock band — upward cast shadow so the transport reads as floating above the page.</summary>
    public static ShadowSpec DockTop => Tok.Theme == ThemeKind.Dark ? DockTopDark : DockTopLight;
    private static readonly ShadowSpec DockTopDark = new(Blur: 12f, OffsetY: -2f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x28));
    private static readonly ShadowSpec DockTopLight = new(Blur: 6f, OffsetY: -1f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x14));
    /// <summary>Dialog band (WinUI depth-128 class) — ContentDialog and full-window modal surfaces.</summary>
    public static ShadowSpec Dialog => Tok.Theme == ThemeKind.Dark ? DialogDark : DialogLight;
    private static readonly ShadowSpec DialogDark = new(Blur: 64f, OffsetY: 16f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x66));
    private static readonly ShadowSpec DialogLight = new(Blur: 48f, OffsetY: 12f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x30));
}
