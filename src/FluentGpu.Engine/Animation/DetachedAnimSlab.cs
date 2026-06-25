using System.Collections.Generic;
using FluentGpu.Foundation;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION REWORK — Presence + DetachedAnimSlab (Phase 5 substrate; additive).
//
//  The seam-safe way for an EXITING node to outlive its topology slot: the reconciler runs hook cleanups synchronously
//  on unmount and recycles the slab slot, so a live orphaned node can be overwritten by the next mount. Instead, on a
//  presence/exit, the node's renderable PAINT COLUMNS are value-copied into a stable DetachedNode row (and any image is
//  pinned), the exit tracks run against that snapshot, and the row retires when its detach-GROUP's pending count hits 0
//  (the AnimatePresence completion gate). modes wait/sync/popLayout are z/layout-ordering policy on the detached set.
//
//  Implemented here: the snapshot row (mirrors the real NodePaint renderable fields — Fill/Border are ColorF, image is
//  an int ImageId, per Scene/Columns.cs), the gen-versioned slab + free-list, and the DetachGroup completion gate.
//  WIRED: the phase-8 RecordDetached() render-walk (SceneRecorder) emits opcodes from a snapshot — the flag-gated
//  ConnectedAnimation Hero-fly rebuild (FG_DETACHED_FLY) drives it (gate CF.a). REMAINING for full AnimatePresence:
//  the reconciler detach-on-unmount + logical-identity enter/exit keying (exits currently use the orphan/reclaim path).
//  Design: docs/plans/animation-engine-rework-design.md §3.6 / §4.7.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>How a detach group orders against live siblings while its members animate out (Framer AnimatePresence mode).</summary>
public enum PresenceMode : byte
{
    Sync,       // exiting + entering animate together (default)
    Wait,       // entering waits until exiting completes
    PopLayout,  // exiting is pulled from layout flow immediately (siblings reflow now); it animates in the freed space
}

/// <summary>A value-copied renderable snapshot of an exiting node — a self-contained row that CANNOT be overwritten by
/// topology churn (unlike the recycled slot). Mirrors the renderable subset of <c>NodePaint</c> + the composed world
/// transform + bounds + z. Driven by its own <see cref="TrackHead"/> AnimValue rows.</summary>
public struct DetachedNode
{
    // composed at detach (the orphan has no live parent to recompose against)
    public Affine2D WorldTransform;
    public Affine2D LocalTransform;
    public RectF Bounds;
    public ulong SortKey;            // the z/record order the node had at detach time

    // renderable paint columns (mirror Scene/Columns.cs NodePaint)
    public float Opacity;
    public float BlurSigma;
    public float OriginX, OriginY;
    public float PresentedW, PresentedH;
    public RectF ClipRect;
    public float StrokeTrimStart, StrokeTrimEnd;
    public ColorF Fill;
    public ColorF BorderColor;
    public float BorderWidth;
    public ColorF TextColor;
    public int ImageId;              // VisualKind.Image: the draw image. The CALLER owns the pin lifetime — ConnectedAnimation pins at Begin and un-pins at every retire; the slab never touches the ImageCache, so a retired row can't leak a pin.
    public byte Kind;                // VisualKind discriminant (Box/Image/Text) — exact enum mapping wired at the walk
    public CornerRadius4 Corners;    // rounded-image corners (the connected fly morphs these source→dest across the fly)
    public byte Fit;                 // ImageFit (object-fit) for the image content map

    public int Group;                // index into the slab's DetachGroup table (-1 = standalone)
    public int TrackHead;            // first AnimValue slab row driving this snapshot (-1 = none)
    public uint Gen;                 // ABA guard — a stale Handle into a recycled row fails validation
    public bool InUse;
}

/// <summary>A set of nodes exiting together (a Presence boundary's leavers). The boundary's structural removal is
/// deferred until <see cref="PendingCount"/> reaches 0 (every member's exit settled) — the AnimatePresence gate.</summary>
public struct DetachGroup
{
    public int PendingCount;         // members still animating; reclaim the group at 0
    public PresenceMode Mode;
    public NodeHandle Anchor;        // the boundary whose structural removal this gate guards
    public bool InUse;
}

/// <summary>The gen-versioned detached-snapshot slab + the detach-group completion gates. Reconciler-owned; rows recycle
/// through a free-list. A tiny set (count of currently-exiting nodes — typically 0-3).</summary>
public sealed class DetachedAnimSlab
{
    private DetachedNode[] _nodes = System.Array.Empty<DetachedNode>();
    private int _nodeCount;
    private readonly Stack<int> _freeNodes = new();

    private DetachGroup[] _groups = System.Array.Empty<DetachGroup>();
    private int _groupCount;
    private readonly Stack<int> _freeGroups = new();

    /// <summary>Live detached snapshots (census).</summary>
    public int Count => _nodeCount - _freeNodes.Count;
    public bool HasActive => Count > 0;
    /// <summary>High-water slot count — iterate [0, NodeCount) checking <see cref="DetachedNode.InUse"/> to walk the
    /// live snapshots (the recorder's RecordDetached pass).</summary>
    public int NodeCount => _nodeCount;

    public ref DetachedNode At(int slot) => ref _nodes[slot];

    /// <summary>Open a presence detach group (the boundary defers its removal until the group's pending count hits 0).</summary>
    public int OpenGroup(NodeHandle anchor, PresenceMode mode)
    {
        int g;
        if (_freeGroups.Count > 0) g = _freeGroups.Pop();
        else { if (_groupCount == _groups.Length) System.Array.Resize(ref _groups, _groups.Length == 0 ? 4 : _groups.Length * 2); g = _groupCount++; }
        _groups[g] = new DetachGroup { PendingCount = 0, Mode = mode, Anchor = anchor, InUse = true };
        return g;
    }

    public ref DetachGroup Group(int g) => ref _groups[g];

    /// <summary>Snapshot an exiting node into a stable row (the caller fills the paint fields via <see cref="At"/>).
    /// Increments the group's pending count if grouped.</summary>
    public int Detach(int group)
    {
        int s;
        if (_freeNodes.Count > 0) s = _freeNodes.Pop();
        else { if (_nodeCount == _nodes.Length) System.Array.Resize(ref _nodes, _nodes.Length == 0 ? 8 : _nodes.Length * 2); s = _nodeCount++; }
        uint gen = _nodes[s].Gen + 1;   // bump on (re)alloc — ABA-safe handle
        _nodes[s] = new DetachedNode { Group = group, TrackHead = -1, Gen = gen, InUse = true };
        if (group >= 0) _groups[group].PendingCount++;
        return s;
    }

    /// <summary>Retire a settled snapshot: free the row (gen bumped on next alloc), decrement its group's gate, and
    /// return the group index if it just reached 0 (so the host can commit the boundary's deferred removal). -1 otherwise.
    /// The CALLER owns the image pin + any AnimValue chain (ConnectedAnimation un-pins + frees its overlay node's tracks
    /// at retire); this only recycles the snapshot row + decrements the gate.</summary>
    public int Retire(int slot)
    {
        ref DetachedNode n = ref _nodes[slot];
        int group = n.Group;
        n.InUse = false;
        _freeNodes.Push(slot);
        if (group >= 0 && --_groups[group].PendingCount <= 0)
        {
            int ready = group;
            _groups[group].InUse = false;
            _freeGroups.Push(group);
            return ready;   // group complete → host commits the boundary removal
        }
        return -1;
    }
}
