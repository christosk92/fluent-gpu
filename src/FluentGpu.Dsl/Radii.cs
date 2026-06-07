using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>The WinUI Fluent corner-radius ramp (ControlCornerRadius / OverlayCornerRadius + the SelectorBar pill).</summary>
public static class Radii
{
    public const float Control = 4f;     // ControlCornerRadius — buttons, inputs, small controls
    public const float Overlay = 8f;     // OverlayCornerRadius — cards, flyouts, dialogs, expanders
    public const float Pill = 16f;       // SelectorBar / pill / badge

    public static readonly CornerRadius4 ControlAll = CornerRadius4.All(Control);
    public static readonly CornerRadius4 OverlayAll = CornerRadius4.All(Overlay);
    public static readonly CornerRadius4 PillAll = CornerRadius4.All(Pill);

    public static CornerRadius4 Circle(float diameter) => CornerRadius4.All(diameter / 2f);

    /// <summary>Top-only overlay corners (a card whose bottom meets an expander/footer flush).</summary>
    public static CornerRadius4 OverlayTop => new(Overlay, Overlay, 0f, 0f);
    /// <summary>Bottom-only overlay corners (the closed-expander header that caps a card).</summary>
    public static CornerRadius4 OverlayBottom => new(0f, 0f, Overlay, Overlay);
}
