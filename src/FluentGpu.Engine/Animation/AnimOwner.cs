using System.Collections.Generic;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  AnimOwner — the per-(node, channel-group) owner partition (Fork-1 Phase 1; design §4.5, §6.1).
//
//  The most systemic band-aid (dossier Rank 1): AnimEngine, the interaction/brush ticker, the scroll integrator, and
//  connected-anim ALL write the same NodePaint fields, colliding only by accident of disjoint node-sets + phase-7 call
//  order. This generalizes the ScrollBind R3 rule ("a node is EITHER AnimEngine-driven OR scroll-bound on a channel,
//  asserted at reconcile") engine-wide: each writer Claims its (node, channel-group) at reconcile, and a DEBUG-only
//  assert turns a silent last-writer-wins clobber into a stack trace at the offending element. This is the safe,
//  additive Phase-1 floor; the Phase-7 single fold-and-write-once compose pass (FG_COMPOSE_PASS) consumes the same
//  table to write each node×channel exactly once. [Conditional("DEBUG")] ⇒ erased from the shipping AOT binary.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Who owns a node×channel-group this frame. None = unclaimed (free to write).</summary>
public enum AnimOwner : byte { None, Anim, Scroll, Interaction, Connected }

/// <summary>The compositor channel groups a single owner partitions over (coarser than AnimChannel — a transform
/// owner owns translate+scale+rotation together, since they fold into one LocalTransform).</summary>
public enum OwnChannelGroup : byte { Transform, Opacity, Blur, Presented, Clip, Color, InteractScale }

/// <summary>Sparse cold table: (node index, channel-group) → owner. Set at reconcile, asserted on write. Cheap and
/// reconciler-owned; a non-animated node costs nothing.</summary>
public sealed class AnimOwnerTable
{
    private readonly Dictionary<long, AnimOwner> _owner = new();

    private static long Key(int nodeIndex, OwnChannelGroup g) => ((long)(uint)nodeIndex << 4) | (byte)g;

    /// <summary>Claim a node×channel-group for a writer (at reconcile). Idempotent for the same owner.</summary>
    public void Claim(int nodeIndex, OwnChannelGroup g, AnimOwner owner) => _owner[Key(nodeIndex, g)] = owner;

    /// <summary>Release a claim (on unmount / when the writer stops owning the channel).</summary>
    public void Release(int nodeIndex, OwnChannelGroup g) => _owner.Remove(Key(nodeIndex, g));

    /// <summary>Drop every claim for a node (slot freed / unmounted).</summary>
    public void ReleaseNode(int nodeIndex)
    {
        for (byte g = 0; g <= (byte)OwnChannelGroup.InteractScale; g++) _owner.Remove(Key(nodeIndex, (OwnChannelGroup)g));
    }

    public AnimOwner OwnerOf(int nodeIndex, OwnChannelGroup g) => _owner.TryGetValue(Key(nodeIndex, g), out var o) ? o : AnimOwner.None;

    /// <summary>DEBUG-only: throw if <paramref name="writer"/> writes a node×channel-group owned by someone else.
    /// Erased from the shipping AOT binary; in production, safety == the CI gate that exercises this.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public void AssertOwner(int nodeIndex, OwnChannelGroup g, AnimOwner writer)
    {
        AnimOwner cur = OwnerOf(nodeIndex, g);
        if (cur != AnimOwner.None && cur != writer)
            throw new System.InvalidOperationException(
                $"Anim owner conflict on node {nodeIndex} {g}: owned by {cur}, written by {writer}. " +
                "Partition at reconcile — exactly one writer per node×channel-group (ScrollBind R3, generalized).");
    }
}
