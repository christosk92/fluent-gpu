using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>
/// Fluent elevation presets — soft drop shadows matching the depth WinUI's ThemeShadow gives cards, flyouts and dialogs.
/// Use via <c>.Elevate(Elevation.Card)</c> (Modifiers) or <c>Ui.Card(...)</c> which carries <see cref="Card"/> by default.
/// </summary>
public static class Elevation
{
    public static readonly ShadowSpec None = default;
    public static readonly ShadowSpec Card = new(Blur: 8f, OffsetY: 2f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x33));
    public static readonly ShadowSpec CardHover = new(Blur: 16f, OffsetY: 4f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x40));
    public static readonly ShadowSpec Flyout = new(Blur: 32f, OffsetY: 8f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x46));
    public static readonly ShadowSpec Dialog = new(Blur: 64f, OffsetY: 16f, OffsetX: 0f, Color: ColorF.FromRgba(0, 0, 0, 0x66));
}
