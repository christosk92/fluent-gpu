using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>The corner-radius ramp of the ACTIVE theme definition (<c>Tok.Definition.Radii</c>): ControlCornerRadius /
/// OverlayCornerRadius + the SelectorBar pill, plus the none/card/full rungs. Every member is a static PROPERTY that
/// resolves against <see cref="Tok.Definition"/> when read, so a definition swap changes every corner built from it; they
/// are not constants (see <see cref="Spacing"/> for the rules: no <c>const</c>, <c>static readonly</c>, attribute
/// argument, <c>case</c> label or parameter default). The Fluent default values live in <see cref="FluentGpu.Theming.RadiiScale"/>.</summary>
public static class Radii
{
    public static float None => Tok.Definition.Radii.None;          // square corners (no rounding)
    public static float Control => Tok.Definition.Radii.Control;    // ControlCornerRadius — buttons, inputs, small controls
    public static float Card => Tok.Definition.Radii.Card;          // surface / media card (outer-8 → inner-4 is the native Fluent tell)
    public static float Overlay => Tok.Definition.Radii.Overlay;    // OverlayCornerRadius — cards, flyouts, dialogs, expanders
    public static float Pill => Tok.Definition.Radii.Pill;          // SelectorBar / pill / badge
    public static float Full => Tok.Definition.Radii.Full;          // fully-rounded (circle / capsule) — clamped to half the box at record time

    public static CornerRadius4 ControlAll => CornerRadius4.All(Tok.Definition.Radii.Control);
    public static CornerRadius4 CardAll => CornerRadius4.All(Tok.Definition.Radii.Card);
    public static CornerRadius4 OverlayAll => CornerRadius4.All(Tok.Definition.Radii.Overlay);
    public static CornerRadius4 PillAll => CornerRadius4.All(Tok.Definition.Radii.Pill);
    public static CornerRadius4 FullAll => CornerRadius4.All(Tok.Definition.Radii.Full);

    public static CornerRadius4 Circle(float diameter) => CornerRadius4.All(diameter / 2f);

    /// <summary>Top-only overlay corners (a card whose bottom meets an expander/footer flush).</summary>
    public static CornerRadius4 OverlayTop { get { float o = Tok.Definition.Radii.Overlay; return new CornerRadius4(o, o, 0f, 0f); } }
    /// <summary>Bottom-only overlay corners (the closed-expander header that caps a card).</summary>
    public static CornerRadius4 OverlayBottom { get { float o = Tok.Definition.Radii.Overlay; return new CornerRadius4(0f, 0f, o, o); } }
}
