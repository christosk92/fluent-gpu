using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  InteractionRecipe — the ONE app-authoring surface for interactive box styling (WS3 P1; the 254-site killer).
//
//  A HYBRID over the engine's two interaction mechanisms, because `MotionTarget` carries no brush lane:
//    • BRUSH half → the BoxEl field ramp (Fill/HoverFill/PressedFill + the BorderColor ramp + BrushTransitionMs):
//      engine-serviced by the HoverFade/PressFade/BrushFade tracks on the input hover/press edge. This is the
//      exact ramp the framework controls (CheckBox, Button, …) use — the recipe just packages it.
//    • MOTION half → the declarative While* surface (WhileHover/WhilePressed MotionTargets + the Transition motion
//      token): the press > focus > hover > rest priority resolver springs scale/opacity on the same input edge.
//    • CURSOR default → the hand on a CLICKABLE box (OnClick / ClickRequestsContext) that declares no cursor of its own.
//      The ONE place clickability implies the hand: the element itself keeps WinUI's arrow (Element.Cursor's doc), so
//      the framework controls — which never call this helper — are untouched.
//
//  `.Interactive(recipe)` is a pure `with`-expansion at element construction (cold path, no closures, no per-frame
//  alloc). It is an APP-AUTHORING surface: the framework controls keep their WinUI-exact hand ramps — do NOT restyle
//  a control with a preset. Presets read `Tok.*` fresh on every access, so a live theme/palette swap re-resolves them.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A packaged interactive-styling declaration for a <see cref="BoxEl"/> — the app-authoring alternative to
/// hand-wiring Fill/HoverFill/PressedFill + the border ramp + While* motion on every clickable surface. Apply it with
/// <see cref="Interaction.Interactive"/>. A value type: constructing one and expanding it allocates nothing beyond the
/// single result <see cref="BoxEl"/> record.
/// <para>NOTE (struct-default caveat): the field defaults below apply only through the parameterless constructor
/// (<c>new InteractionRecipe { … }</c>, which every preset uses); <c>default(InteractionRecipe)</c> is the degenerate
/// all-zero value (HoverScale 0, BrushMs 0) — always construct with <c>new()</c>.</para></summary>
public readonly record struct InteractionRecipe
{
    /// <summary>The four-leg fill ramp (Rest / Hover / Pressed / Disabled) → Fill/HoverFill/PressedFill + the disabled
    /// resting swap. The engine eases between the legs on the hover/press edge (HoverFade/PressFade + BrushFade).</summary>
    public StateBrush Fill { get; init; }
    /// <summary>Optional four-leg border ramp → BorderColor/HoverBorderColor/PressedBorderColor. Null = no border (the
    /// recipe leaves the box's existing border untouched).</summary>
    public StateBrush? Stroke { get; init; }
    /// <summary>Border width in DIP when <see cref="Stroke"/> is set (ignored otherwise). Default 1.</summary>
    public float StrokeWidth { get; init; }
    /// <summary>Hover scale (1 = none) → WhileHover.Scale (motion half). Composited only — never relayout/hit-test.</summary>
    public float HoverScale { get; init; }
    /// <summary>Pressed scale (1 = none) → WhilePressed.Scale.</summary>
    public float PressScale { get; init; }
    /// <summary>Hover opacity (NaN = none) → WhileHover.Opacity.</summary>
    public float HoverOpacity { get; init; }
    /// <summary>Pressed opacity (NaN = none) → WhilePressed.Opacity.</summary>
    public float PressedOpacity { get; init; }
    /// <summary>The brush cross-fade duration (ms) → BoxEl.BrushTransitionMs. Default 83 (the WinUI ControlFaster /
    /// BrushTransition timing).</summary>
    public float BrushMs { get; init; }
    /// <summary>The motion token driving the While* scale/opacity dynamics → BoxEl.Transition. Default
    /// <see cref="MotionTokenId.ControlFaster"/> (the 83ms eased curve, matching <see cref="BrushMs"/>); presets that
    /// want a springy press (Card) use <see cref="MotionTokenId.StandardSpring"/>.</summary>
    public MotionTokenId Motion { get; init; }

    public InteractionRecipe()
    {
        // Fill/Stroke default to their zero values (transparent ramp / no stroke) — presets override.
        StrokeWidth = 1f;
        HoverScale = 1f;
        PressScale = 1f;
        HoverOpacity = float.NaN;
        PressedOpacity = float.NaN;
        BrushMs = 83f;
        Motion = MotionTokenId.ControlFaster;
    }
}

/// <summary>The <see cref="InteractionRecipe"/> apply modifier + the theme-live app-authoring presets.</summary>
public static class Interaction
{
    /// <summary>Expand a recipe onto a box (a pure `with`-expansion, cold path). The BRUSH half always applies
    /// (Fill/HoverFill/PressedFill + optional border ramp + BrushTransitionMs + the disabled resting swap). The MOTION
    /// half (WhileHover/WhilePressed + Transition) applies only when the recipe declares non-identity scale/opacity AND
    /// the node does not already own a transform — see the composition rules below.
    /// <para>Composition / one-transform-owner: the recipe's While* targets are SKIPPED when (a) <paramref name="isEnabled"/>
    /// is false, (b) the recipe declares no motion (HoverScale/PressScale == 1 and both opacities NaN), or (c) the box
    /// already carries a bound <see cref="BoxEl.Transform"/> — the bound transform is the single transform owner, and a
    /// While* scale would fight it. A While* leg the caller already set (WhileHover/WhilePressed) is preserved (caller
    /// wins), as is a caller-set <see cref="Element.Transition"/>. Channels the recipe does not name are left untouched.</para>
    /// <para><paramref name="isEnabled"/> = false applies the Disabled fill/stroke legs and sets
    /// <see cref="Element.IsEnabled"/> = false, so the engine routes no hover/press progress (the Hover/Pressed legs are
    /// never reached) — exactly how CheckBox/Button disable their ramps.</para>
    /// <para>Cursor (E3): a box that is CLICKABLE when it reaches this call — <see cref="BoxEl.OnClick"/> set or
    /// <see cref="BoxEl.ClickRequestsContext"/> — and declares no <see cref="BoxEl.Cursor"/> gets
    /// <see cref="CursorId.Hand"/>; a caller-set cursor (Arrow included) wins, and a non-clickable box stays cursor-less
    /// (it inherits, else the arrow). The element-level stance is unchanged — clickability alone never implies the hand
    /// (<see cref="BoxEl.Cursor"/>'s doc; the reconciler writes no default) — this app-authoring helper is the one place
    /// that opts in. A disabled box keeps the stamp but shows nothing: the dispatcher's cursor walk skips a Disabled
    /// node. Set <c>OnClick</c> BEFORE <c>.Interactive(…)</c>: a click added afterwards through <c>with</c> is not seen
    /// here. Reference engines: Zed's <c>ListItem</c> applies <c>cursor_pointer()</c> exactly when an enabled
    /// <c>on_click</c> exists (<c>crates/ui/src/components/list/list_item.rs:388-389</c>); egui's <c>Button</c> sets
    /// <c>Visuals::interact_cursor</c> on hover only for an interactive widget (<c>crates/egui/src/widgets/button.rs:376-379</c>);
    /// Flutter's <c>InkResponse</c> resolves its <c>mouseCursor</c> to <c>SystemMouseCursors.click</c> when <c>onTap</c>
    /// is non-null (material package, not cloned locally).</para></summary>
    public static BoxEl Interactive(this BoxEl el, in InteractionRecipe r, bool isEnabled = true)
    {
        // ── BRUSH half (always) + the clickable-hand cursor default (caller-set cursor wins) ────────────────────
        var box = el with
        {
            Fill = r.Fill.Resting(isEnabled),
            HoverFill = r.Fill.Hover,
            PressedFill = r.Fill.Pressed,
            BrushTransitionMs = r.BrushMs,
            IsEnabled = isEnabled,
            Cursor = el.Cursor ?? ClickableCursor(el),
        };
        if (r.Stroke is { } stroke)
            box = box with
            {
                BorderColor = stroke.Resting(isEnabled),
                HoverBorderColor = stroke.Hover,
                PressedBorderColor = stroke.Pressed,
                BorderWidth = r.StrokeWidth,
            };

        // ── MOTION half (declarative While*) ───────────────────────────────────────────────────────────────────
        bool wantsHover = r.HoverScale != 1f || float.IsFinite(r.HoverOpacity);
        bool wantsPress = r.PressScale != 1f || float.IsFinite(r.PressedOpacity);
        // One transform owner per node: a DECLARED Transform — a bound thunk or a plain static matrix, both of which the
        // reconciler now writes — owns the matrix outright, so never fight it with a While* scale.
        bool transformOwned = el.Transform.IsBound || el.Transform.Value != default;
        if (isEnabled && (wantsHover || wantsPress) && !transformOwned)
        {
            MotionTarget? hover = el.WhileHover is { } callerHover ? callerHover
                : wantsHover ? new MotionTarget { Scale = r.HoverScale, Opacity = float.IsFinite(r.HoverOpacity) ? r.HoverOpacity : 1f }
                : null;
            MotionTarget? press = el.WhilePressed is { } callerPress ? callerPress
                : wantsPress ? new MotionTarget { Scale = r.PressScale, Opacity = float.IsFinite(r.PressedOpacity) ? r.PressedOpacity : 1f }
                : null;
            box = box with
            {
                WhileHover = hover,
                WhilePressed = press,
                Transition = el.Transition ?? MotionTok.Get(r.Motion),   // caller-set Transition wins
            };
        }
        return box;
    }

    /// <summary>The E3 default: the hand for a box that is clickable (a click target or a context-invoker), else none.</summary>
    static CursorId? ClickableCursor(BoxEl el)
        => el.OnClick is not null || el.ClickRequestsContext ? CursorId.Hand : (CursorId?)null;

    // ── Presets (APP-AUTHORING; theme-live — get-only, re-read Tok.* on every access, matching the Tok gradient
    //    precedent). Framework controls keep their own WinUI-exact ramps; never restyle a control with a preset. ──

    /// <summary>Transparent → subtle-hover → subtle-pressed, no border. The generic "this surface is clickable"
    /// treatment (nav items, ghost buttons, icon affordances).</summary>
    public static InteractionRecipe Subtle => new()
    {
        Fill = new StateBrush(Tok.FillSubtleTransparent, Tok.FillSubtleSecondary, Tok.FillSubtleTertiary, Tok.FillSubtleTransparent),
    };

    /// <summary>The same subtle ramp as <see cref="Subtle"/>, as a distinct preset so list-row tuning can diverge
    /// (a heavier hover, a selection-aware fill) without moving the shared Subtle surface.</summary>
    public static InteractionRecipe ListRow => new()
    {
        Fill = new StateBrush(Tok.FillSubtleTransparent, Tok.FillSubtleSecondary, Tok.FillSubtleTertiary, Tok.FillSubtleTransparent),
    };

    /// <summary>A card surface: opaque card fills + a flat card stroke, pressing in slightly (0.985) on a spring —
    /// the geometric press feedback (not a fill flash) that reads as a physical card.</summary>
    public static InteractionRecipe Card => new()
    {
        Fill = new StateBrush(Tok.FillCardDefault, Tok.FillCardSecondary, Tok.FillCardSecondary, Tok.FillCardDefault),
        Stroke = StateBrush.Flat(Tok.StrokeCardDefault),
        StrokeWidth = 1f,
        PressScale = 0.985f,
        Motion = MotionTokenId.StandardSpring,
    };

    /// <summary>The STANDARD control-surface chrome: the opaque control fill ramp (default→secondary→tertiary→disabled,
    /// the same ramp <see cref="Button"/>'s Standard appearance uses) under a 1px control border — the "this is a
    /// standard button / input face" treatment that sits between the transparent <see cref="Subtle"/> ghost and the
    /// elevated <see cref="Card"/>. Fill + border only (no press geometry), so it reads as a solid control face;
    /// theme-live like the other presets.</summary>
    public static InteractionRecipe Control => new()
    {
        Fill = new StateBrush(Tok.FillControlDefault, Tok.FillControlSecondary, Tok.FillControlTertiary, Tok.FillControlDisabled),
        Stroke = StateBrush.Flat(Tok.StrokeControlDefault),
        StrokeWidth = 1f,
    };

    /// <summary>A tile surface: opaque card fills ramping into the control secondary/tertiary hover/press legs (a
    /// heavier ramp than <see cref="Card"/>'s card-to-card step) under a flat card stroke — the "clickable tile"
    /// treatment for browse/genre/wide tiles. Fill + border only, no press geometry (WhileHover/WhilePressed);
    /// theme-live like the other presets.</summary>
    public static InteractionRecipe Tile => new()
    {
        Fill = new StateBrush(Tok.FillCardDefault, Tok.FillControlSecondary, Tok.FillControlTertiary, Tok.FillCardDefault),
        Stroke = StateBrush.Flat(Tok.StrokeCardDefault),
        StrokeWidth = 1f,
    };

    /// <summary>Transparent at rest, an accent-subtle wash on hover, a dimmer accent on press — an accent-tinted ghost
    /// affordance (a toolbar toggle, an accent list action) that shows intent without a solid accent plate.</summary>
    public static InteractionRecipe AccentGhost => new()
    {
        Fill = new StateBrush(
            ColorF.Transparent,
            Tok.AccentSubtle,
            Tok.AccentSubtle with { A = Tok.AccentSubtle.A * 0.6f },
            ColorF.Transparent),
    };
}
