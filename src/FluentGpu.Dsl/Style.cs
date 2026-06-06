using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>
/// A control's visual style. Controls are barebone (behavior + structure); this is the look layered on top — the
/// framework supplies a Fluent default (<see cref="Theme.AccentButton"/>/<see cref="Theme.StandardButton"/>), and any
/// developer can override it per-instance (pass a style) or globally (replace the Theme style). Per-state backgrounds
/// default to Transparent, which the renderer reads as "auto" (lighten on hover / darken on press).
/// </summary>
public sealed record ButtonStyle
{
    public ColorF Background { get; init; }
    public ColorF Foreground { get; init; }
    public ColorF Border { get; init; }
    public ColorF HoverBackground { get; init; }     // Transparent ⇒ auto-lighten
    public ColorF PressedBackground { get; init; }   // Transparent ⇒ auto-darken
    public float BorderWidth { get; init; } = 1f;
    public float CornerRadius { get; init; } = 4f;
    public Edges4 Padding { get; init; } = new(12, 6, 12, 7);
    public float FontSize { get; init; } = 14f;
    public bool Bold { get; init; }
}
