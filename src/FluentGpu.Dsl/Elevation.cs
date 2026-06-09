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
    public static readonly ShadowSpec Card = new(Blur: 8f, OffsetY: 2f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x33));
    public static readonly ShadowSpec CardHover = new(Blur: 16f, OffsetY: 4f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x40));
    /// <summary>ToolTip band (WinUI depth-16 class) — lighter than <see cref="Flyout"/>; tooltips sit on the lowest popup band.</summary>
    public static readonly ShadowSpec Tooltip = new(Blur: 16f, OffsetY: 4f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x40));
    /// <summary>Flyout/menu band (WinUI depth-32 class — ElevationHelper.cpp:19 base elevation depth 32).</summary>
    public static readonly ShadowSpec Flyout = new(Blur: 32f, OffsetY: 8f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x46));
    /// <summary>Dialog band (WinUI depth-128 class) — ContentDialog and full-window modal surfaces.</summary>
    public static readonly ShadowSpec Dialog = new(Blur: 64f, OffsetY: 16f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x66));
}
