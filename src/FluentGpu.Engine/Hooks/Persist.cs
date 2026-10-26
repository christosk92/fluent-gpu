using System;
using FluentGpu.Dsl;

namespace FluentGpu.Hooks;

/// <summary>The re-pushed props of a <see cref="Flow.Persist"/> slot: the persistence key (read at mount; a different key on a
/// reused slot remounts it) and the builder of the content.</summary>
internal sealed record PersistProps(string Key, Func<Element> Build);

/// <summary>
/// E7 - the anchor of a persistent subtree (<see cref="Flow.Persist"/>). A plain component: it renders <c>Build()</c> as its one
/// child. Everything that makes the child persistent is a reconciler table keyed by <see cref="PersistProps.Key"/>
/// (<c>TreeReconciler</c> "Persistent subtrees"): when this slot is unmounted its child subtree is detached and PARKED instead
/// of unmounted, and the next slot with the same key that mounts ADOPTS it (re-parents it under itself) and renders it once
/// against its new providers. No new element type: the slot is identified by its component type.
/// </summary>
internal sealed class PersistSlot : Component
{
    public override Element Render() => UseProps<PersistProps>().Build();
}
