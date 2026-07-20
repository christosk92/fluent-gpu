using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>The Fluent corner-radius ramp (ControlCornerRadius / OverlayCornerRadius + the SelectorBar pill), plus the
/// surface/none/full rungs an app UI needs. A superset of any app's radius layer (e.g. Wavee's WaveeRadius) so an app
/// can drop its duplicate and read these const-for-const (its <c>Straight</c>→<see cref="None"/>, its full pill→<see cref="Full"/>).</summary>
public static class Radii
{
    public const float None = 0f;        // square corners (no rounding)
    public const float Control = 4f;     // ControlCornerRadius — buttons, inputs, small controls
    public const float Card = 8f;        // surface / media card (outer-8 → inner-4 is the native Fluent tell)
    public const float Overlay = 8f;     // OverlayCornerRadius — cards, flyouts, dialogs, expanders
    public const float Pill = 16f;       // SelectorBar / pill / badge
    public const float Full = 999f;      // fully-rounded (circle / capsule) — clamped to half the box at record time

    public static readonly CornerRadius4 ControlAll = CornerRadius4.All(Control);
    public static readonly CornerRadius4 CardAll = CornerRadius4.All(Card);
    public static readonly CornerRadius4 OverlayAll = CornerRadius4.All(Overlay);
    public static readonly CornerRadius4 PillAll = CornerRadius4.All(Pill);
    public static readonly CornerRadius4 FullAll = CornerRadius4.All(Full);

    public static CornerRadius4 Circle(float diameter) => CornerRadius4.All(diameter / 2f);

    /// <summary>Top-only overlay corners (a card whose bottom meets an expander/footer flush).</summary>
    public static CornerRadius4 OverlayTop => new(Overlay, Overlay, 0f, 0f);
    /// <summary>Bottom-only overlay corners (the closed-expander header that caps a card).</summary>
    public static CornerRadius4 OverlayBottom => new(0f, 0f, Overlay, Overlay);
}
