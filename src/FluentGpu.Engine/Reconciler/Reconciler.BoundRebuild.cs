using FluentGpu.Dsl;
using FluentGpu.Scene;
using FluentGpu.Signals;

using FluentGpu.Foundation;
namespace FluentGpu.Reconciler;

/// <summary>
/// EP6 - stamp-driven rebuild of a bound list's slots. A bound (<see cref="VirtualListEl.RowBind"/>) list builds each slot's
/// subtree ONCE and recycles it by writing the slot's index signal, so a new template could never reach a slot that already
/// exists. <see cref="VirtualListEl.TemplateStamp"/> is the explicit "the template changed" edge: when a pass sees a stamp
/// different from the one the slots were built under, every slot is rebuilt from the CURRENT <c>RowBind</c> and the measured
/// extents are reset, in that one reconcile.
/// </summary>
public sealed partial class TreeReconciler
{
    /// <summary>Drop the extent source (a <see cref="MeasuredStackVirtualLayout"/>'s measured table too) so the pass that
    /// follows re-measures every row against the new template. The anchored reseed keeps the row under the viewport top where
    /// the user sees it; a fixed-stride stack or grid recreates its (exact) extent source.</summary>
    private void ResetBoundMeasure(NodeHandle node, VirtualListEl ve, int count)
    {
        _scene.ScrollRef(node).Extent = null;
        if (ve.ItemLayout is MeasuredStackVirtualLayout measured) measured.Reseed(count);
    }

    /// <summary>Rebuild every bound slot of <paramref name="entry"/>: the realized slots, the persistent prefix, the Spare pool
    /// and the keep-alive bucket. Each slot keeps its index signal (so the new subtree reads the same logical item), is
    /// mounted from the current <see cref="VirtualListEl.RowBind"/>, and replaces the old one in the same position / pool. The
    /// old subtrees are freed at once (no exit orphan). Runs inside the realize pass, so the mounts already snap bound
    /// transitions, and under a <c>SnapScope</c> the reconciler's <c>SnapMotion</c> checks keep every seed at its end value.</summary>
    private void RebuildBoundSlots(NodeHandle node, NodeHandle content, VirtualEntry entry, VirtualListEl ve)
    {
        var rowBind = ve.RowBind!;
        entry.BuiltStamp = ve.TemplateStamp;
        int rebuilt = 0;

        // Prefix first, then the ordinary window: appending each replacement keeps the content chain in the
        // [prefix..., window...] order every realize path expects (the old roots are freed as we go).
        if (entry.PrefixSlots is { Count: > 0 } prefix)
            for (int i = 0; i < prefix.Count; i++)
            {
                var s = prefix[i];
                if (s.Index is null) continue;
                var fresh = MountReplacement(content, rowBind, s.Index, s.ContentType);
                DiscardBoundRoot(s.Root, wasAttached: true);
                // Prefix roots live detached between realize passes only while a pass walks the chain; leave them attached
                // like every other root here - the prefix pass detaches whatever it finds.
                prefix[i] = fresh;
                rebuilt++;
            }

        if (entry.Slots is { Count: > 0 } slots)
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s.Index is null) continue;
                slots[i] = MountReplacement(content, rowBind, s.Index, s.ContentType);
                DiscardBoundRoot(s.Root, wasAttached: true);
                rebuilt++;
            }

        // Spare pool: rebuilt as parked spares (same index signals), so the pool the next grow takes from carries the new
        // template and the recycle hit rate is what it was before the rebuild.
        if (entry.Spare is { Count: > 0 } oldSpare)
        {
            entry.Spare = null;
            for (int i = 0; i < oldSpare.Count; i++)
            {
                var s = oldSpare[i];
                if (s.Index is null) continue;
                if (!_scene.IsLive(s.Root)) continue;
                var fresh = MountReplacement(content, rowBind, s.Index, s.ContentType);
                DiscardBoundRoot(s.Root, wasAttached: false);
                ParkSpareSlot(entry, fresh);
                rebuilt++;
            }
        }

        // Keep-alive bucket: the same parked mechanics as the phase-1 park of the extended recycler.
        if (entry.Kept is { Count: > 0 } kept)
        {
            var items = new List<int>(kept.Keys);
            for (int i = 0; i < items.Count; i++)
            {
                var ks = kept[items[i]];
                if (!_scene.IsLive(ks.Root)) continue;
                var fresh = MountReplacement(content, rowBind, ks.Index, ks.ContentType);
                DiscardBoundRoot(ks.Root, wasAttached: false);
                OnSubtreeDeactivated?.Invoke(fresh.Root);
                _scene.Detach(fresh.Root);
                SetSubtreeParked(fresh.Root, parked: true);
                ks.El = fresh.El;
                ks.Root = fresh.Root;
                rebuilt++;
            }
        }

        if (rebuilt == 0) return;
        MarkLayoutShape(content);
        _reconciled = true;
        _realizeProgress = true;

        // The roving tab stop and any window-keyed owner re-sync against the new roots.
        if (ve.OnItemPrepared is { } prepared && entry.Slots is { Count: > 0 } active)
            for (int i = 0; i < active.Count; i++) prepared(active[i].Index.Peek());
    }

    /// <summary>Mount a new subtree for <paramref name="index"/>'s logical item under <paramref name="content"/> (appended;
    /// the caller re-orders or parks it). Mirrors the cold-mount block of the bound recyclers.</summary>
    private BoundSlot MountReplacement(NodeHandle content, Func<IReadSignal<int>, Element> rowBind, Signal<int> index, int contentType)
    {
        Element el = rowBind(index);
        var child = _scene.CreateNode(el.ElementTypeId);
        _scene.AppendChild(content, child);
        int outerIndex = _realizeSlotIndex; var outerSignal = _realizeSlotSignal;
        _realizeSlotIndex = index.Peek(); _realizeSlotSignal = index;   // the cold mount's image requests classify by this slot
        Mount(child, el);
        _realizeSlotIndex = outerIndex; _realizeSlotSignal = outerSignal;
        return new BoundSlot(index, el, child, contentType);
    }

    /// <summary>Hard-free a replaced slot root: hover/press/focus hygiene, unmount (hook cleanups, binds), free. No exit orphan.</summary>
    private void DiscardBoundRoot(NodeHandle root, bool wasAttached)
    {
        if (!_scene.IsLive(root)) return;
        if (wasAttached) OnSubtreeDeactivated?.Invoke(root);   // before the free: the dispatcher's clear walk needs the live parent chain
        _scene.Unmark(root, NodeFlags.Hovered | NodeFlags.Pressed | NodeFlags.Focused | NodeFlags.FocusVisual);
        OnSubtreeRemoved?.Invoke(root);
        UnmountSubtree(root);
        _scene.FreeSubtree(root);
        _reconciled = true;
    }
}
