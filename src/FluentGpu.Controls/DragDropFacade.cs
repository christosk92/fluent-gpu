using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>
/// The declarative DRAG-SOURCE facade (SwiftUI <c>draggable</c> / dnd-kit <c>useDraggable</c>): one line makes anything
/// draggable with the premiere defaults, so app code declares INTENT and never coordinates geometry.
///
/// <code>
/// row with { Draggable = Drag.Source(MyKinds.Resource, () =&gt; payload) }
/// </code>
///
/// The defaults are the researched chip model, NOT the legacy ghost: <see cref="DragLift.Stationary"/> lift (the source
/// row stays in its slot) at <see cref="SourceDimOpacity"/> (Atlassian's "it's in the chip" 0.4 dim), with the moving
/// visual drawn by a mounted <see cref="DragPreviewLayer"/>. Opt back into the lifted ghost — translate + shadow +
/// unclipped hoist — with <c>lift: DragLift.Ghost</c> or by passing an explicit <see cref="DragVisualStyle"/>.
/// </summary>
public static class Drag
{
    /// <summary>Source-row dim while its payload is "in the chip" (Atlassian/Pragmatic: sources stay VISIBLE at 0.4 so
    /// the user keeps their place, rather than disappearing).</summary>
    public const float SourceDimOpacity = 0.4f;

    /// <summary>Make a node draggable. <paramref name="kind"/> is the string discriminator target accept-tests match on;
    /// <paramref name="payload"/> resolves the typed payload ONCE at promotion (never per move).
    /// <paramref name="opacity"/> dims the source; <paramref name="lift"/> selects stationary (default) vs the lifted
    /// ghost; <paramref name="backplate"/>/<paramref name="shadow"/>/<paramref name="scale"/> tune the GHOST lift only.</summary>
    public static DragSource Source(string kind, Func<object?> payload,
                                    float opacity = SourceDimOpacity,
                                    DragLift lift = DragLift.Stationary,
                                    ColorF? backplate = null,
                                    ShadowSpec? shadow = null,
                                    float scale = 1f)
        => new(kind, payload)
        {
            Style = new DragVisualStyle
            {
                Lift = lift,
                Opacity = opacity,
                Backplate = backplate,
                Shadow = shadow,
                Scale = scale,
            },
        };

    /// <summary>Escape hatch: a source carrying a fully hand-authored <see cref="DragVisualStyle"/>.</summary>
    public static DragSource Source(string kind, Func<object?> payload, DragVisualStyle style)
        => new(kind, payload) { Style = style };

    /// <summary>A source that HIDES its row entirely for the gesture (opacity 0) — the sidebar/reorder case where the
    /// vacated slot itself is the insertion gap, so a dimmed ghost row would read as a duplicate.</summary>
    public static DragSource SourceHidden(string kind, Func<object?> payload)
        => Source(kind, payload, opacity: 0f);
}

/// <summary>
/// The declarative DROP-TARGET facade (SwiftUI <c>dropDestination&lt;T&gt;</c> / dnd-kit <c>useDroppable</c>): a typed
/// wrapper over <see cref="DropTargetSpec"/> that unwraps the payload for you, so a target's handlers are written
/// against the app's own type instead of <c>object?</c> casts and null checks.
///
/// <code>
/// DropTarget = Drop.Target&lt;TrackPayload&gt;(MyKinds.Resource,
///     accepts: p =&gt; p.CanCopy,
///     onDrop:  (p, s) =&gt; Deposit(p),
///     caption: p =&gt; $"Add {p.Count} tracks to {name}")
/// </code>
///
/// Unwrapping accepts the payload EITHER as the target's own type directly OR wrapped in a
/// <see cref="ReorderPayload"/> (a sortable list's own gesture), so one target serves both a foreign drop and a
/// same-list reorder. A payload that doesn't unwrap makes the target transparent — discovery continues to a compatible
/// ancestor, exactly as a failing <see cref="DropTargetSpec.CanAccept"/> does.
///
/// A transparent target is invisible to the user, so any <c>accepts</c> test that can turn away a payload the surface
/// LOOKS like it should take ought to pass <c>refusalCaption</c> too — see <see cref="DropTargetSpec.RefusalCaption"/>.
/// </summary>
public static class Drop
{
    /// <summary>Typed target over ONE kind. See the class remarks for the unwrap rule.</summary>
    public static DropTargetSpec Target<T>(string kind,
                                           Func<T, bool>? accepts = null,
                                           Action<T, DragSession>? onDrop = null,
                                           Func<T, string>? caption = null,
                                           Action<T, DragSession>? onEnter = null,
                                           Action<T, DragSession>? onOver = null,
                                           Action<DragSession>? onLeave = null,
                                           bool settleOnDrop = false,
                                           DropTargetVisualPolicy visualPolicy = DropTargetVisualPolicy.None,
                                           Func<DragSession, bool>? spotlightWhen = null,
                                           Func<T, string?>? refusalCaption = null)
        => Target(new[] { kind }, accepts, onDrop, caption, onEnter, onOver, onLeave, settleOnDrop, visualPolicy,
                  spotlightWhen, refusalCaption);

    /// <summary>Typed target over several kinds (e.g. an in-app resource AND <see cref="DropKinds.Files"/>).</summary>
    public static DropTargetSpec Target<T>(string[] kinds,
                                           Func<T, bool>? accepts = null,
                                           Action<T, DragSession>? onDrop = null,
                                           Func<T, string>? caption = null,
                                           Action<T, DragSession>? onEnter = null,
                                           Action<T, DragSession>? onOver = null,
                                           Action<DragSession>? onLeave = null,
                                           bool settleOnDrop = false,
                                           DropTargetVisualPolicy visualPolicy = DropTargetVisualPolicy.None,
                                           Func<DragSession, bool>? spotlightWhen = null,
                                           Func<T, string?>? refusalCaption = null)
    {
        // The caption is applied on BOTH Enter and Over: the engine clears session.Caption on every target change, and
        // an Over-only refresh keeps it correct when a target's caption depends on the pointer (an insertion slot).
        return new DropTargetSpec(
            kinds,
            OnEnter: onEnter is not null || caption is not null
                ? s => { if (TryUnwrap<T>(s.Payload, out var v)) { if (caption is not null) s.Caption = caption(v); onEnter?.Invoke(v, s); } }
                : null,
            OnOver: onOver is not null || caption is not null
                ? s => { if (TryUnwrap<T>(s.Payload, out var v)) { if (caption is not null) s.Caption = caption(v); onOver?.Invoke(v, s); } }
                : null,
            OnLeave: onLeave,
            OnDrop: onDrop is not null
                ? s => { if (TryUnwrap<T>(s.Payload, out var v)) onDrop(v, s); }
                : null)
        {
            // A payload that cannot unwrap is not for this target — make it transparent rather than accepting and
            // silently no-op'ing on the drop (the "cannot drop in this mode" class of silent refusals).
            CanAccept = accepts is not null
                ? s => TryUnwrap<T>(s.Payload, out var v) && accepts(v)
                : s => TryUnwrap<T>(s.Payload, out _),
            SettleOnDrop = settleOnDrop,
            VisualPolicy = visualPolicy,
            SpotlightWhen = spotlightWhen,
            // Only a payload of this target's own type can be REFUSED by it — one that doesn't unwrap was never for
            // this surface, so it stays a pass-through with nothing to explain.
            RefusalCaption = refusalCaption is not null
                ? s => TryUnwrap<T>(s.Payload, out var v) ? refusalCaption(v) : null
                : null,
        };
    }

    /// <summary>THE payload unwrap rule, exposed so hand-written targets can share it: the payload is
    /// <typeparamref name="T"/> itself, or the <see cref="ReorderPayload.Item"/> of a sortable list's own gesture.</summary>
    public static bool TryUnwrap<T>(object? payload, out T value)
    {
        switch (payload)
        {
            case T direct:
                value = direct;
                return true;
            case ReorderPayload { Item: T wrapped }:
                value = wrapped;
                return true;
            default:
                value = default!;
                return false;
        }
    }
}
