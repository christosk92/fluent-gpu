using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace Wavee;

// THE SELECTION CUE, AND WHY IT MOVES AGAIN.
//
// WHAT THIS FILE USED TO BE. A MEASURED single overlay per mode (`WaveeSelPill`): it looked up the selected row's
// NodeHandle in a per-mode dictionary, read its laid-out AbsoluteRect and glided between rows on the WinUI
// NavigationView stretch animation. That cannot survive the unified pane — rows RECYCLE inside one virtualized list, so a
// key→handle map is stale by construction — and the class sat here unmounted with 0 call sites.
//
// WHAT WENT WRONG WHEN IT WAS REPLACED. The replacement was a per-row 3×16 accent bar revealed by
// `Opacity = selected ? 1 : 0` with `Transition = MotionTok.ControlFaster` on the same element. `Element.Transition` does
// NOT animate a static value change: the reconciler only consults it for Enter/Exit/Layout (`Reconciler.SynthesizeDeclarative`)
// and for the While* gesture targets (`SetInteractTargets`), while a static opacity is re-asserted verbatim every render
// (`Reconciler.cs`, `if (!b.Opacity.IsBound) paint.Opacity = b.Opacity.Value`). So the cue HARD-CUT between rows — the
// user's "previous one had animations for the selector, now it just pops up".
//
// WHAT IT IS NOW. Still ONE cue per row (the only model a recycling list can honour, and what WinUI's own
// per-NavigationViewItem SelectionIndicator does), but it MOVES: on a selection change the arriving row's pill springs in
// FROM the side the selection travelled from while the departing row's pill springs out TOWARD where it went, so the two
// halves read as one pill travelling between the rows. The animation is SEEDED (`AnimEngine.SeedValue` under a named
// token), never declared — that is the only mechanism that actually animates, and it is also where reduced motion is
// enforced as a VALUE rather than as a branch in authoring code.
//
// It is a CHILD COMPONENT, not inline markup, for two reasons: it needs hooks (a layout effect) that a recycling row slot
// cannot grow per row kind, and its own render must re-read its state so a recycle re-skins it. Props freeze at mount, so
// the state arrives as a `Func` the component invokes inside ITS render — the reads inside that Func (the slot's index
// signal, the live route) ARE its subscription.

/// <summary>Everything the row indicator draws from, re-read on every one of its renders.
///
/// <para>The GEOMETRY half (<paramref name="Route"/>/<paramref name="Indent"/>/<paramref name="Top"/>) is published by the
/// owning row slot as a plain field during its own render (the <c>SidebarPane.Plan</c> precedent), so the route and the
/// row's metrics are resolved exactly ONCE by the row builder that already knows them. The SELECTION half is re-derived
/// from that route on every read, so it cannot lag by a frame if the pill's own reactive computation happens to flush
/// before its parent slot's.</para></summary>
/// <param name="Route">The row's navigation target, or null/empty when it has none (a folder, a track, a missing entity).</param>
/// <param name="Selected">This row is the live selection.</param>
/// <param name="Departing">This row was the selection immediately BEFORE the current change (the travel's origin).</param>
/// <param name="Direction">+1 = the selection travelled DOWN the plan, -1 = up, 0 = unknown (off-plan / first paint).</param>
/// <param name="Epoch">Bumped once per real selection change. Equal epochs mean "nothing moved" — a recycle, not a change.</param>
/// <param name="Indent">Left inset: the row's nesting indent, so the pill sits over the row's own 3-DIP gutter.</param>
/// <param name="Top">Vertical inset that centres the pill in the row.</param>
readonly record struct SidebarPillState(
    string? Route, bool Selected, bool Departing, int Direction, int Epoch, float Indent, float Top);

sealed class SidebarSelectionPill : Component
{
    /// <summary>SelectionIndicator height (WinUI 3×16). Row metrics read this to centre the pill vertically.</summary>
    public const float PillH = 16f;
    /// <summary>SelectionIndicator width (WinUI 3×16).</summary>
    public const float PillW = 3f;

    /// <summary>How far the pill travels in/out along the selection's direction. Deliberately larger than the 3-DIP
    /// gutter it lives in and smaller than a row, so the motion reads as "the pill came from the row above/below" without
    /// ever overlapping the neighbour's own cue.</summary>
    const float Slide = 10f;
    /// <summary>The parked (invisible) pill is SHORT as well as offset, so arriving reads as a stretch into place rather
    /// than a slab sliding by.</summary>
    const float Squash = 0.35f;

    /// <summary>The pill's travel: the WinUI NavigationView selection-pill spring, named so a retune is central. SnapEnd
    /// under reduced motion — the transform lands instantly and only the fade survives.</summary>
    static readonly MotionTokenDef PillTravel =
        MotionTokenDef.SpringOf(MotionSprings.NavPill, ReducedMotionPolicy.SnapEnd);
    /// <summary>The reveal. KeepFade, so reduced motion still cross-fades (a fade aids orientation; it is not motion).</summary>
    static MotionTokenDef PillFade => MotionTok.ControlFast;

    readonly Func<SidebarPillState> _state;
    NodeHandle _self;
    /// <summary>The last selection epoch this pill reacted to; -1 until the first render. A pill whose epoch is unchanged
    /// was RECYCLED rather than re-selected, and must snap to the declared statics instead of replaying a travel.</summary>
    int _epoch = -1;

    public SidebarSelectionPill(Func<SidebarPillState> state) => _state = state;

    public override Element Render()
    {
        var st = _state();          // the reads inside this Func subscribe THIS component (recycle + navigation + re-plan)
        bool selected = st.Selected;
        bool departing = !selected && st.Departing;
        int dir = st.Direction;

        // The resting transform. The selected row's pill sits at identity; every other row's pill parks off-position on
        // the side that makes the travel read correctly: an ARRIVING pill starts on the side the selection came from
        // (-dir), a DEPARTING pill ends on the side it went to (+dir).
        float park = dir == 0 ? 0f : (departing ? dir : -dir) * Slide;
        float restY = selected ? 0f : park;
        float restScale = selected ? 1f : Squash;
        // Grow (or shrink) from the edge facing the travel, so the pill reaches toward the destination instead of
        // inflating in place. Unknown direction ⇒ centre, which degrades to a plain cross-fade.
        float originY = dir == 0 ? 0.5f
            : selected ? (dir > 0 ? 0f : 1f)
            : (dir > 0 ? 1f : 0f);

        int epoch = st.Epoch;
        bool involved = selected || departing;
        UseLayoutEffect(() =>
        {
            int previous = _epoch;
            _epoch = epoch;
            var anim = Context.Anim;
            if (anim is null || _self.IsNull) return;
            // First render (nothing to travel from), a recycle (the epoch did not move — this row was not re-selected),
            // or a row the change did not touch. The transform channels are NOT declared on the element (they are the
            // tracks' alone, so the seed can never double a static offset), and this node OUTLIVES the row it drew a
            // moment ago — so a recycle must place them at rest explicitly or it inherits the previous row's travel.
            if (previous < 0 || previous == epoch || !involved)
            {
                Place(anim, AnimChannel.TranslateY, restY);
                Place(anim, AnimChannel.ScaleY, restScale);
                return;
            }

            // The 5th argument is SeedValue's `from` (the fresh-seed start value) — passed POSITIONALLY on purpose: a
            // named `from:` argument is the one spelling that reads as the head of a query expression to a human skimming
            // the line, and this file already carries enough parser folklore.
            anim.SeedValue(_self, AnimChannel.Opacity, selected ? 1f : 0f, PillFade, selected ? 0f : 1f);
            anim.SeedValue(_self, AnimChannel.TranslateY, restY, PillTravel, selected ? park : 0f);
            anim.SeedValue(_self, AnimChannel.ScaleY, restScale, PillTravel, selected ? Squash : 1f);
            // Deps as (int,int): DepKey has no (int,bool,bool) conversion — pack the two flags into one int.
        }, (epoch, (selected ? 1 : 0) | (departing ? 2 : 0)));

        // SHAPE-STABLE: always present, never added/removed, so a recycle never changes the row's element shape.
        // Opacity is declared because it is the one channel the reconciler must re-assert for a row that never animated
        // (a cold-realized selected row); the transform channels are owned exclusively by the tracks above.
        return new BoxEl
        {
            Width = PillW, Height = PillH,
            Margin = new Edges4(st.Indent, st.Top, 0f, 0f),
            Corners = CornerRadius4.All(PillW * 0.5f),
            Fill = Tok.AccentDefault,
            Opacity = selected ? 1f : 0f,
            TransformOriginY = originY,
            HitTestVisible = false,
            OnRealized = h => _self = h,
        };
    }

    /// <summary>Place a channel AT a value with no motion — a settled 1 ms linear track, which is exactly how the engine
    /// snaps a channel under reduced motion (<c>AnimScheduler.SnapTo</c>). 0-alloc, so it is safe on the recycle edge.</summary>
    void Place(AnimEngine anim, AnimChannel channel, float value)
    {
        // No track ⇒ nothing has moved this node, so it sits at identity. That IS the rest state for the selected pill,
        // and for an unselected one the difference is invisible (it is at Opacity 0) — so leave the slab untouched rather
        // than minting a row per realized row on every cold scroll.
        if (!anim.TryGetTrackValue(_self, channel, out float live)) return;
        if (MathF.Abs(live - value) < 0.01f) return;
        anim.SeedEased(_self, channel, value, value, 1f, Easing.Linear);
    }
}
