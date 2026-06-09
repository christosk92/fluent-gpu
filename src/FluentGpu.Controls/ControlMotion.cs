using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>
/// Reusable, WinUI-accurate discrete-state motion presets for control sub-elements — the checkmarks, dots and glyphs
/// that appear/disappear on a logical state flip (checked, selected, expanded…). These ride the engine's existing
/// enter/exit lifecycle: attach one as <see cref="FluentGpu.Dsl.BoxEl.Animate"/> on a node the reconciler INSERTS on
/// enter and REMOVES on exit, and it scales+fades in (on insert) / out (orphaned, then reclaimed when the spring
/// settles, on removal). The motion is property-level and signals-native — the state IS the element's presence and the
/// animation falls out of the reconcile diff — so there is no per-control animation code and no VisualStateManager:
/// the opposite of WinUI's stringly-typed GoToState storyboards, and a strict superset in composability.
/// Springs (velocity-carrying, interruptible) are the default; durations map to the Fluent motion tokens.
/// </summary>
/// <summary>
/// A per-property interaction RAMP — the antidote to WinUI's enumerated visual-state matrix. WinUI restates every
/// property in all {logical}×{Normal,PointerOver,Pressed,Disabled} combinations (e.g. a CheckBox = 12 hand-written
/// states); we declare the value as a function of the orthogonal interaction axis ONCE: <see cref="Rest"/> →
/// <see cref="Hover"/> → <see cref="Pressed"/> → <see cref="Disabled"/>. A control picks the ramp by its logical state
/// (checked/unchecked/…), wires Rest/Hover/Pressed into the box's Fill/HoverFill/PressedFill (or BorderColor/Hover/
/// Pressed), and the engine's InteractionAnimator eases the displayed value between them using the element's
/// hover/press duration + easing specs — the SwiftUI/Compose "animate the property toward its target" model.
/// No GoToState, no storyboards, combinations free.
/// </summary>
public readonly record struct StateBrush(ColorF Rest, ColorF Hover, ColorF Pressed, ColorF Disabled)
{
    /// <summary>A flat ramp (all four the same) — for a property that doesn't react to interaction.</summary>
    public static StateBrush Flat(ColorF c) => new(c, c, c, c);
    /// <summary>The resting value for the current enablement (the hover/pressed legs are eased by the engine).</summary>
    public ColorF Resting(bool enabled) => enabled ? Rest : Disabled;
}

/// <summary>
/// The typed computed-"TemplateSettings" convention — the antidote to WinUI's stringly <c>TemplateSettings</c> bound into
/// storyboards. A control declares a local <c>readonly record struct &lt;Control&gt;TemplateSettings(...)</c> of typed values
/// (rotations, heights, clip rects, open/close animation positions), computes it ONCE per state change (a pure factory
/// called in render, or a <c>UseMemo</c>/<c>OnRealized</c> effect that reads the control's own signals), and feeds the
/// fields into the engine's existing channels: static props, <c>TransformBind</c>/<c>OpacityBind</c>/<c>WidthBind</c>/
/// <c>HeightBind</c>, <c>AnimEngine</c> keyframe seeds, or the clip-rect channel (<c>AnimChannel.ClipL/T/R/B</c>). Never
/// rebuild the record inside a hot bind/effect body — compute it in the memo/factory. <see cref="ExpanderTemplateSettings"/>
/// is the reference; the same shape serves CommandBarFlyout, NavigationView, Progress*, TeachingTip, TabView, TreeView, …
/// These small helpers are the shared interpolation math those records use.
/// </summary>
public static class Tween
{
    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static float SmoothStep(float t) { t = Clamp01(t); return t * t * (3f - 2f * t); }
}

public static class ControlMotion
{
    /// <summary>Icon "pop": scale 0.5→1 + fade 0→1 on a snappy spring (~180ms, faint overshoot). For a checkmark or the
    /// indeterminate dash appearing/disappearing on a CheckBox. WinUI keeps the box fill instant and animates the glyph.</summary>
    public static readonly LayoutTransition IconPop = new(
        TransitionChannels.Opacity,
        // A pronounced, clearly-perceptible spring: ~280ms settle with a little overshoot (bounce) — WinUI's checkmark pop
        // reads as motion, not an instant swap. Scales from 0 so the glyph visibly grows in. (Faster/critically-damped felt
        // like an instant appear on a 12px glyph.)
        TransitionDynamics.Spring(response: 0.28f, dampingRatio: 0.55f),
        Enter: new EnterExit(Sx: 0f, Sy: 0f, Opacity: 0f, Active: true),
        Exit:  new EnterExit(Sx: 0.3f, Sy: 0.3f, Opacity: 0f, Active: true));

    /// <summary>Legacy dot "scale-in": an inserted dot grows 0→full + fades. WinUI RadioButton itself does not use this;
    /// its checked glyph appears immediately and changes size through PointerOver/Pressed visual states.</summary>
    public static readonly LayoutTransition DotScaleIn = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Spring(response: 0.20f, dampingRatio: 0.68f),
        Enter: new EnterExit(Sx: 0.1f, Sy: 0.1f, Opacity: 0f, Active: true),
        Exit:  new EnterExit(Sx: 0.1f, Sy: 0.1f, Opacity: 0f, Active: true));
}
