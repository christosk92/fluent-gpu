namespace FluentGpu.Dsl;

/// <summary>
/// The transitions.dev "expressive" motion vocabulary — semantic durations, translate distances, pre-open scales, and
/// blur radii adopted as an opt-in app-author palette for the Expressive Motion Kit recipes (see <c>Motions</c> /
/// <c>MotionHooks</c> and the gallery's Motion-recipes page). Deliberately DISTINCT from the WinUI/Fluent-2
/// <see cref="Motion"/> + <c>MotionSprings</c> constants (which framework controls keep for 1:1 parity): these are the
/// playful, emphasis-leaning values from the transitions.dev motion-token table. Match a value to a recipe by its
/// USAGE, not the raw number. Durations are ms; distances DIP; blur is the <c>AnimChannel.Blur</c> sigma (px).
/// </summary>
public static class Expressive
{
    // Durations (transitions.dev "Motion tokens").
    public const float Stagger = 40f;    // per-item stagger offset
    public const float Micro = 80f;      // tooltip delay, shake segment, large stagger
    public const float Quick = 150f;     // modal/dropdown close, text swap, tooltip appear
    public const float Fast = 250f;      // icon swap, dropdown/modal open, tabs sliding, page slide
    public const float Medium = 350f;    // panel close, toast close
    public const float Slow = 400f;      // panel open, skeleton content reveal, input clear
    public const float VerySlow = 500f;  // emphasis moments — badge appear, text reveal, success check, number pop-in

    // Translate distances (DIP).
    public const float DistMicro = 4f;   // text swap
    public const float DistSmall = 6f;   // error shake (small segment)
    public const float DistBase = 8f;    // badge diagonal reveal, page slide, error shake (large segment), number pop-in
    public const float DistMedium = 12f; // text reveal
    public const float DistLarge = 30f;  // check badge appear

    // Pre-open scales (the surface starts slightly scaled, then scales to 1).
    public const float ScaleLarge = 0.96f;  // modal open / close
    public const float ScaleMedium = 0.97f; // dropdown open
    public const float ScaleSmall = 0.98f;  // tooltip open
    public const float ScaleTiny = 0.99f;   // dropdown close

    // Blur radii (the AnimChannel.Blur sigma, px) — the perceptual softener that makes a short travel read as full.
    public const float BlurSmall = 2f;   // panel reveal, icon swap, text swap, skeleton reveal, number pop-in
    public const float BlurMedium = 3f;  // page slide, text reveal
    public const float BlurLarge = 8f;   // success check open
}
