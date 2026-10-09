using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Reconciler;

// P1 (Operation ultra-fast GPU engine, layout.md §4.7): Element.Visible : Prop<bool> — the presence channel. Two
// writers feed the SAME scene-level state (SceneStore._aux.Collapsed, mirrored onto NodeFlags.Visible; HitTestVisible stays the element's own):
//   • WriteColumns' generic (every-element-type) section calls ApplyPresenceStatic for an UNBOUND Visible — equality-
//     gated via SceneStore.SetCollapsedIfChanged so an identical re-render marks nothing (gate.hooks.layout-dirty-
//     identical-tree stays green).
//   • BindNode calls BindPresence for a BOUND Visible — one BindEffect wired at mount (re-wired in place when a re-render
//     binds a new thunk/signal — Reconciler.Rewire.cs), equality-gated the same way, counted by
//     NodeBindingFireCount/WriteCount (the P0 counters).
// Both funnel through SetSubtreeHidden, which is the ONLY place that touches CompEntry.Hidden — the "renders not
// suppressed" contract: Hidden feeds solely into the ActiveSig formula (UseIsActive/UseActivation/UseInterval), never
// entry.Parked/DeferredRender/Effect, so a collapsed component keeps rendering (its bindings settle even though the
// node paints nothing) while ONLY its timers pause. See docs/design/subsystems/layout.md §4.7.
public sealed partial class TreeReconciler
{
    /// <summary>Static (unbound) Visible write — called from WriteColumns' generic section for EVERY element type.
    /// Equality-gated: a re-render with the same resolved visibility marks nothing.</summary>
    private void ApplyPresenceStatic(NodeHandle node, bool visible)
    {
        bool collapsed = !visible;
        if (_scene.SetCollapsedIfChanged(node, collapsed)) SetSubtreeHidden(node, collapsed);
    }

    /// <summary>Bound Visible wiring — called once at mount from <c>BindNode</c> for every element type (the channel
    /// lives on the base <see cref="Element"/>, not a concrete subtype, so this runs unconditionally, unlike the
    /// BoxEl-only channels above it); a re-render that binds a new thunk/signal re-wires the same effect
    /// (<c>RewireBinds</c>). DEBUG-asserts (BindContract) that a MorphId (shared-element) node never binds
    /// Visible — collapsing a hero participant mid-flight would break ConnectedAnimation capture.</summary>
    private void BindPresence(NodeHandle node, Element el)
    {
        if (!el.Visible.IsBound) return;
        if (BindContract.CompiledIn && BindContract.Enabled && el.MorphId is not null)
            BindContract.MorphVisibleBind(el.GetType().Name);

        var fx = new BindEffect<bool>(Runtime, el, static e => e.Visible);
        AddBinding(node, fx.Start(() =>
        {
            NodeBindingFireCount++;
            if (!_scene.IsLive(node)) return;
            bool next = fx.Read();
            bool wasCollapsed = _scene.IsCollapsed(node);
            bool nowCollapsed = !next;
            if (wasCollapsed == nowCollapsed) return;
            NodeBindingWriteCount++;
            _scene.SetCollapsed(node, nowCollapsed);
            SetSubtreeHidden(node, nowCollapsed);
            RemirrorAncestors(node);   // a component's root (or a bound anchor): the boundary above leaves/rejoins flow too
            // false→true edge: treat like a mount — seed the node's declared Enter (the true→false edge just snaps,
            // matching a static collapse; there is no exit-animation hook here because a collapsed node is already
            // out of layout/paint the instant this effect runs, so there is nothing left to animate OUT of). The
            // declared Enter is read off fx.El — the element this node was LAST reconciled against, not the mount one.
            if (wasCollapsed && !nowCollapsed && SuppressBoundTransitions == 0 && Anim is { } anim && !Motion.ReducedMotion
                && SynthesizeDeclarative(node, fx.El) is { } dt && dt.Enter.Active)
            {
                anim.SeedEnterOver(node, dt.Enter, dt, EnterRestOf(fx.El));
                if (dt.Size == SizeMode.Reflow) anim.PendingEnterReflow.Add(node);
                else if (dt.Size == SizeMode.FlowReveal) anim.PendingEnterReveal.Add(node);
            }
        }));
    }

    /// <summary>Walk NODE and every live descendant's mounted <c>CompEntry</c>, setting <c>Hidden</c> and refreshing
    /// its ActiveSig (folds <c>!Parked &amp;&amp; !Hidden</c> — the same formula <c>SetSubtreeParked</c> writes on a
    /// Park edge). Does NOT touch <c>NodeFlags.Parked</c>, <c>DeferredRender</c>, or the render <c>Effect</c> — a
    /// presence collapse pauses timers (<c>UseInterval</c>, which already gates on <c>UseIsActive()</c>) WITHOUT
    /// suspending the component's own re-renders, unlike KeepAlive parking. <c>UseTimeout</c>/<c>UseKeyframes</c>
    /// have no active-gating at all today (neither for Parked nor Hidden) — see the P1 progress notes for that
    /// follow-up. A reveal only clears Hidden where no collapsed node remains on the chain: it is a no-op under a
    /// still-collapsed ancestor and skips nested collapsed subtrees.</summary>
    private void SetSubtreeHidden(NodeHandle node, bool hidden)
    {
        // Hidden means "some node on my ancestor chain is collapsed", not "the node that just flipped is". A reveal
        // under a still-collapsed ancestor changes nothing below it (the mount seed walks the same chain).
        if (!hidden)
            for (var a = _scene.Parent(node); !a.IsNull; a = _scene.Parent(a))
                if (_scene.IsCollapsed(a)) return;
        WriteSubtreeHidden(node, hidden);
    }

    private void WriteSubtreeHidden(NodeHandle node, bool hidden)
    {
        if (!_scene.IsLive(node)) return;
        if (_comps.TryGetValue(node, out var entry))
        {
            entry.Hidden = hidden;
            if (entry.ActiveSig is { } sig) sig.Value = !entry.Parked && !entry.Hidden;
        }
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            // A reveal stops at a nested collapsed node: its subtree stays hidden under its own Visible=false.
            if (!hidden && _scene.IsCollapsed(c)) continue;
            WriteSubtreeHidden(c, hidden);
        }
    }
}
