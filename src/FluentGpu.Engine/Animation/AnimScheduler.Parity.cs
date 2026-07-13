using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION REWORK — switch-over Step A: AnimEngine API parity (so AppHost/Reconciler/controls can swap the field).
//
//  This increment covers the parity surface that needs NO new infra: the per-node LayoutTransition side-table, the
//  O(1) census, HasTracks/CancelToRest, and the dt-driven Tick(dt) compat entry. The heavier parity (Keyframes +
//  the keyframe store, Drive + the index-based SignalSource clocks, the SizeMode.Reflow machinery + the host
//  worklists ReflowRoots/PendingEnterReflow/…) lands in the next Step-A increments. Build-verified each step.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AnimEngine
{
    // Per-node layout-transition spec, keyed by node INDEX (slot reuse self-cleans; bounded by the slab size). The
    // reconciler Set/Clears it from BoxEl.Animate/Layout each reconcile; FLIP capture/apply read it.
    private readonly Dictionary<int, LayoutTransition> _transitions = new();

    // ── census (read by the MemCensus sampler / wake diagnostics) ─────────────────────────────────
    /// <summary>Active rows, all channels — O(1).</summary>
    public int TrackCount => _slab.Count;
    // Census memo (perf plan W6/E12): LoopTrackCount/DisplayRateActive feed AnimIsAmbient, which the host evaluates
    // inside ComputeWakeReasons — SEVERAL times per frame (RecommendedWaitMs, Paint's keep-alive gate, the Resync
    // step-up guard, HasActiveWork). Each read re-walked every row. Both are pure functions of the slab's rows, so one
    // walk per slab-mutation Version (Add/Free/ClearNode + BumpVersion at the Loop/DisplayRate-rewriting seeds)
    // serves every read until the slab actually changes. -1 sentinel forces the first compute (Version starts at 0).
    private int _censusVersion = -1;
    private int _censusLoopCount;
    private bool _censusDisplayRate;

    private void RefreshCensus()
    {
        if (_censusVersion == _slab.Version) return;
        _censusVersion = _slab.Version;
        int loops = 0; bool displayRate = false;
        for (int nodeIndex = _slab.FirstActiveNode; nodeIndex >= 0; nodeIndex = _slab.NextActiveNode(nodeIndex))
            for (int s = _slab.HeadOnNode(nodeIndex); s >= 0; s = _slab.At(s).NextOnNode)
            {
                AnimFlags f = _slab.At(s).Flags;
                if ((f & AnimFlags.Loop) != 0) loops++;
                if ((f & AnimFlags.DisplayRate) != 0) displayRate = true;
            }
        _censusLoopCount = loops;
        _censusDisplayRate = displayRate;
    }

    /// <summary>Looping rows (the Loop flag) — memoized per slab-mutation version (see <see cref="RefreshCensus"/>);
    /// read by the census / wake sampler / the host's ambient-cap decision.</summary>
    public int LoopTrackCount { get { RefreshCensus(); return _censusLoopCount; } }
    /// <summary>True if any active row is a DISPLAY-RATE loop — a transient loop (e.g. an indeterminate progress bar)
    /// that opts out of the ambient frame-rate cap so it runs at the panel refresh. Read by the host's cap decision;
    /// memoized per slab-mutation version alongside <see cref="LoopTrackCount"/>.</summary>
    public bool DisplayRateActive { get { RefreshCensus(); return _censusDisplayRate; } }
    /// <summary>Live per-node layout-transition specs — O(1).</summary>
    public int TransitionCount => _transitions.Count;

    // ── layout-transition side-table (node index → spec) ──────────────────────────────────────────
    public void SetTransition(NodeHandle node, in LayoutTransition t) => _transitions[(int)node.Raw.Index] = t;
    public bool TryGetTransition(NodeHandle node, out LayoutTransition t) => _transitions.TryGetValue((int)node.Raw.Index, out t);
    public void ClearTransition(NodeHandle node) => _transitions.Remove((int)node.Raw.Index);
    /// <summary>Symmetric teardown when a scene slot is FREED (wired to SceneStore.OnFreeIndex): drop the index-keyed
    /// spec so a freed node leaves no dormant spec the next node reusing the slot inherits. In-flight rows are
    /// gen-checked and self-prune at the next tick's IsLive guard.</summary>
    public void ClearForIndex(int index) { _transitions.Remove(index); ClearInteractTargets(index); }   // #12: also drop the index's interact-target row (was leaked — ClearInteractTargets had no caller)

    /// <summary>True while any row targets this node (the host detects a settled exit orphan when this goes false).</summary>
    public bool HasTracks(NodeHandle node) => _slab.HeadOnNode((int)node.Raw.Index) >= 0;

    /// <summary>Cancel + reset the channel's paint to its settle-time resting sentinel (StrokeTrim/PresentedW/H → NaN,
    /// Clip → Infinite) — symmetric with the settle path. Channels without a rest sentinel behave exactly like
    /// <see cref="Cancel"/>. Ported from AnimEngine.CancelToRest.</summary>
    public void CancelToRest(NodeHandle node, AnimChannel channel)
    {
        Cancel(node, channel);
        if (!_scene.IsLive(node)) return;
        ref NodePaint p = ref _scene.Paint(node);
        switch (channel)
        {
            case AnimChannel.SizeW: p.PresentedW = float.NaN; _scene.Unmark(node, NodeFlags.Relayouting); break;
            case AnimChannel.SizeH: p.PresentedH = float.NaN; break;
            case AnimChannel.StrokeTrimStart: p.StrokeTrimStart = float.NaN; break;
            case AnimChannel.StrokeTrimEnd: p.StrokeTrimEnd = float.NaN; break;
            case AnimChannel.ClipL or AnimChannel.ClipT or AnimChannel.ClipR or AnimChannel.ClipB: p.ClipRect = RectF.Infinite; break;
            default: return;   // no rest sentinel — identical to Cancel
        }
        _scene.Mark(node, NodeFlags.PaintDirty);
    }

    /// <summary>dt-driven tick entry (tests + the AppHost compat path during the switch): advance the owned clock by the
    /// clamped dt and run one tick. RunFrame is the wall-time path; this is the dt-injected one (also the determinism-gate
    /// entry — inject dt ∈ {8.33,16.67,33.3}).</summary>
    public void Tick(float dtMs)
    {
        // Explicit dt-injection (the AppHost compat path + tests/fast-forward + the determinism gate): use the RAW
        // step — NO 1..40ms clamp. The clamp is the WALL-TIME GC-spike defense and lives in AnimClock.Advance/RunFrame;
        // the analytical spring is stable at any dt (no sub-stepping), so the raw step here matches the old Tick(dt).
        _clock.DeltaMs = dtMs;
        _clock.NowMs += dtMs;
        _clock.FrameId++;
        Tick(in _clock);
    }
}
