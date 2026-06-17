namespace Wavee;

// Wavee's named motion vocabulary. The actual animation is the engine's Expressive Motion Kit (MotionRecipes.*) +
// the component hooks (MotionHooks.UseEntrance/UseHoverScale, MotionRecipes.UseSoftReveal) + Context.UseSpring/
// UseTransition — all reduced-motion-aware and compositor-only. This file fixes the TIMING vocabulary (the Fluent
// duration tiers) so every surface animates on the same clock. Recipes are called directly at the call site.
public static class WaveeMotion
{
    // Fluent duration tiers (ms) — Common_themeresources_any.xaml.
    public const float Faster = 83f;     // press / quick acknowledgement
    public const float Fast = 167f;      // small state changes
    public const float Standard = 250f;  // page / icon swap / recolor cross-fade (the workhorse)
    public const float Slow = 400f;      // panel / now-playing expand

    // Page-nav asymmetry (incoming decelerates in, outgoing accelerates out).
    public const float PageInMs = 300f;
    public const float PageOutMs = 150f;

    // List entrance stagger per row.
    public const float StaggerMs = 40f;

    // Hover/press scale targets.
    public const float HoverScale = 1.02f;
    public const float PressScale = 0.97f;
}
