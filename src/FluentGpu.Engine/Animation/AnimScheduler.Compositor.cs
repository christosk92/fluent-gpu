using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

public sealed partial class AnimEngine
{
    private readonly Dictionary<int, CompositorSeed> _compositorSeeds = new(64);
    private readonly Dictionary<NodeHandle, Accum> _compositorFeedbackAccumulators = new(64);
    private readonly List<int> _compositorCompletedSlots = new(64);
    private ulong _nextCompositorInstance;
    /// <summary><paramref name="Base"/>: NaN, or the UI's view of the row that this re-seed's start departed from (taken
    /// against <paramref name="BaseRevision"/>, the revision the row carried before it), and <paramref name="DriftScale"/>
    /// the factor that start re-based it by - see <see cref="MarkSeedRelative"/>.</summary>
    private readonly record struct CompositorSeed(ulong Instance, ulong Revision, bool ExplicitFrom, float Base = float.NaN,
        ulong BaseRevision = 0, float DriftScale = 1f);

    /// <summary>Enabled by a host whose render thread owns scene recording and compositor pose evaluation.</summary>
    public bool RenderOwnsCompositor
    {
        get => _renderOwnsCompositor;
        set
        {
            if (_renderOwnsCompositor == value) return;
            _renderOwnsCompositor = value;
            _slab.BumpVersion();   // ownership of every compositor row just flipped: re-derive the wake census
        }
    }
    private bool _renderOwnsCompositor;

    // ── perf plan item 4: compositor-candidate cache ────────────────────────────────────────────────────────────────
    // IsCompositorRowStatic below is a pure function of a row's OWN static fields (Flags/Kind/Channel) plus its
    // siblings-on-the-same-node's static fields (the mixed additive/replace check) — every bit it reads is seed-time
    // data that only changes via _slab.Add/Free/ClearNode or an explicit _slab.BumpVersion() (the established
    // convention: AnimValue.cs's Version doc, AnimScheduler.Parity.cs's RefreshCensus). So the candidate SLOT LIST is
    // valid until the next such mutation and is rebuilt only then — not every frame — while Parked/Done/live-scene
    // (Relayouting) state, which DOES change every frame without a Version bump, is always re-read fresh from the slab
    // at consumption time (a slot index is never a stale copy of row data). This is exactly why HasUiWork/
    // CaptureCompositorAnimations used to cost O(active nodes × rows-per-node) every frame: 10,000 parked/off-screen
    // rows on off-screen nodes were walked (and IsCompositorRowStatic's own O(rows-on-node) sibling scan re-run) on
    // every call, even though nothing about them changed frame to frame.
    private readonly List<int> _compositorCandidateSlots = new(64);
    private int _compositorCandidateVersion = -1;

    private void RefreshCompositorCandidates()
    {
        if (_compositorCandidateVersion == _slab.Version) return;
        _compositorCandidateVersion = _slab.Version;
        _compositorCandidateSlots.Clear();
        for (int node = _slab.FirstActiveNode; node >= 0; node = _slab.NextActiveNode(node))
            for (int slot = _slab.HeadOnNode(node); slot >= 0; slot = _slab.At(slot).NextOnNode)
                if (IsCompositorRowStatic(in _slab.At(slot))) _compositorCandidateSlots.Add(slot);
    }

    /// <summary>UI tick debt only. Render-owned rows remain in the desired set until matching completion feedback.
    /// O(compositor-candidate rows), not O(active rows): <see cref="_parked"/> (AnimScheduler.cs) is an already-
    /// maintained O(1) counter (the same one <see cref="HasActive"/> uses), and only the small compositor-candidate
    /// list (see <see cref="RefreshCompositorCandidates"/>) is walked to find/exclude the live (Relayouting) and
    /// Parked/Done exceptions — never the whole active-node chain. Matches the previous exact semantics: true iff some
    /// non-Parked/Done row is NOT a compositor row.</summary>
    public bool HasUiWork
    {
        get
        {
            if (!RenderOwnsCompositor) return HasActive;
            int nonParkedActive = _slab.Count - _parked;
            if (nonParkedActive <= 0) return false;
            RefreshCompositorCandidates();
            int compositorRows = 0;
            foreach (int slot in _compositorCandidateSlots)
            {
                ref var row = ref _slab.At(slot);
                if (row.Has(AnimFlags.Parked | AnimFlags.Done)) continue;
                if (IsCompositorRow(in row)) compositorRows++;
                else return true;   // a non-parked/done candidate that fails the LIVE check (mid-relayout) is UI work
            }
            // Every remaining non-parked/done row is either a non-candidate (never compositor-eligible) or one of the
            // compositorRows counted above; any left over after subtracting those is UI work.
            return nonParkedActive - compositorRows > 0;
        }
    }

    private void StampCompositorSeed(int slot, bool newInstance, bool explicitFrom)
    {
        ref var seed = ref CollectionsMarshal.GetValueRefOrAddDefault(_compositorSeeds, slot, out bool exists);
        seed = !exists || newInstance
            ? new(++_nextCompositorInstance, 1, explicitFrom)
            : new(seed.Instance, seed.Revision + 1, explicitFrom);
    }

    /// <summary>The re-seed just stamped on <paramref name="slot"/> starts from the UI's view of the row's CURRENT value
    /// (<paramref name="uiBase"/>, plus any frame shift the caller added), not from an authored one. A render-owned row's UI
    /// view is only the last imported feedback pose (or, before a pose of that revision came back, the start the UI gave it),
    /// while the render thread kept advancing it - so the renderer moves this start by however far its own pose ran from
    /// <paramref name="uiBase"/> (RenderCompositorAnimations.Adopt), and an interrupted fade or move continues from the pixel
    /// on screen instead of stepping back to the older pose. A fresh instance has nothing in flight there: it keeps its start.
    /// <paramref name="driftScale"/>: a start the caller re-based into a new basis (uiBase times the scale, plus a shift - a
    /// connected fly's re-based model box) moves by the render drift times the same scale, and so does its velocity.</summary>
    private void MarkSeedRelative(int slot, float uiBase, float driftScale = 1f)
    {
        if (!RenderOwnsCompositor || slot < 0) return;
        ref var seed = ref CollectionsMarshal.GetValueRefOrNullRef(_compositorSeeds, slot);
        if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref seed) || seed.Revision <= 1) return;
        seed = seed with { Base = uiBase, BaseRevision = seed.Revision - 1, DriftScale = driftScale };
    }

    /// <summary>The structural part of compositor eligibility — everything except the live scene-state (Relayouting)
    /// check, which can flip without a slab mutation. Pure function of the slab's seed-time data; see the perf plan
    /// item 4 remarks above <see cref="_compositorCandidateSlots"/> for why that makes it cacheable per-Version.</summary>
    private bool IsCompositorRowStatic(in AnimValue row)
    {
        if (row.Has(AnimFlags.Driven | AnimFlags.Additive | AnimFlags.RestoreLayout | AnimFlags.TrailingAnchor)) return false;
        if (row.Kind is not (GenKind.Spring or GenKind.Eased or GenKind.Keyframes)) return false;
        if (row.Channel is AnimChannel.LayoutW or AnimChannel.LayoutH or AnimChannel.RevealExtent or (>= AnimChannel.RevealBand0 and <= AnimChannel.RevealBand3)) return false;
        // A mixed additive/replace axis remains one UI-owned composition so no captured additive value is applied twice.
        for (int slot = _slab.HeadOnNode((int)row.Node.Raw.Index); slot >= 0; slot = _slab.At(slot).NextOnNode)
            if (_slab.At(slot).Channel == row.Channel && _slab.At(slot).Has(AnimFlags.Additive)) return false;
        return true;
    }

    private bool IsCompositorRow(in AnimValue row)
    {
        if (!IsCompositorRowStatic(in row)) return false;
        if (row.Channel is AnimChannel.SizeW or AnimChannel.SizeH
            && (_scene.Flags(row.Node) & NodeFlags.Relayouting) != 0) return false;
        // F169: a translate/scale/rotate row on a node a follow-rect pass reads (the followed target, the follower, and
        // their ancestor chains) stays UI-owned: phase 7.15 reads those nodes' painted pose THIS frame, and a render-owned
        // row never advances the UI-side transform between feedbacks, so the follower would trail the slide. Live check
        // like Relayouting above, so it needs no candidate-cache invalidation of its own.
        if (_followAnchors.Count != 0 && row.Channel <= AnimChannel.Rotation && _followAnchors.Contains(row.Node)) return false;
        return true;
    }

    // F169: the nodes whose painted pose the host's follow-rect pass (SceneStore.PlaceFollowRects) reads, republished by the
    // host after every pass. Empty whenever nothing follows, so the compositor offload is only lost for the chain under a
    // live follower and only while it follows.
    private readonly HashSet<NodeHandle> _followAnchors = new();

    /// <summary>True while a follow-rect pass holds any node's transform rows UI-owned.</summary>
    public bool HasFollowAnchors => _followAnchors.Count != 0;

    /// <summary>Replace the follow anchor set (see <see cref="SceneStore.CollectFollowAnchors"/>). A row whose node enters
    /// the set hands off to the UI tick from the last compositor feedback pose (<see cref="ApplyCompositorFeedback"/> keeps
    /// the UI row's Position/Elapsed there), so the slide continues from where it was last reported; a row whose node
    /// leaves it is captured again from the UI row's advanced state, and the render thread re-seeds from that. Rewrites
    /// nothing when the set is unchanged (the steady frame is one SetEquals).</summary>
    public void SetFollowAnchors(HashSet<NodeHandle> anchors)
    {
        if (_followAnchors.SetEquals(anchors)) return;
        _followAnchors.Clear();
        _followAnchors.UnionWith(anchors);
        _slab.BumpVersion();   // ownership of live rows just flipped: re-derive the candidate list and the wake census
    }

    /// <summary>UI publication: copy the complete desired supported set, including parked rows, into an owned slot.
    /// O(compositor-candidate rows) — walks <see cref="_compositorCandidateSlots"/> (refreshed only on a slab-mutation
    /// Version change) instead of every active row on every active node.</summary>
    public void CaptureCompositorAnimations(CompositorAnimationSnapshot target, double wallNowMs)
    {
        target.BeginCapture(wallNowMs);
        RefreshCompositorCandidates();
        foreach (int slot in _compositorCandidateSlots)
        {
            ref var row = ref _slab.At(slot);
            if (!_scene.IsLive(row.Node) || !IsCompositorRow(in row)) continue;
            var identity = _compositorSeeds[slot];
            _keysBySlot.TryGetValue(slot, out var keys);
            // Cadence travels WITH the row: the render thread owns these rows' advance, so it must apply the same
            // due-check the UI-thread PASS1 does or an explicit Cadence.At(hz) row would silently run at panel rate once
            // the compositor adopts it.
            target.Add(in row, identity.Instance, identity.Revision, identity.ExplicitFrom, keys, (ushort)PeriodMsOf(slot),
                identity.Base, identity.BaseRevision, identity.DriftScale);
        }
        target.EndCapture();
    }

    /// <summary>A fingerprint of exactly what <see cref="CaptureCompositorAnimations"/> would hand the renderer, as far as the
    /// renderer's adoption can tell rows apart (<c>RenderCompositorAnimations.Adopt</c>): which rows are captured (live,
    /// compositor-owned), and for each its identity (instance), its seed revision (every retarget re-stamps it), its node,
    /// its cadence and its Parked/Done/Hold/Paused flags (a hold or a pause rewrites nothing else, and the renderer learns of
    /// it only by adopting). Two equal fingerprints mean a re-capture would adopt to the identical render
    /// state — the renderer advances these rows itself, so their positions are not an input. Same walk as the capture
    /// (O(compositor-candidate rows)), no allocation. The host compares it across frames for its no-op publication skip.</summary>
    internal ulong CompositorCaptureFingerprint()
    {
        RefreshCompositorCandidates();
        ulong h = 14695981039346656037UL;
        int n = 0;
        foreach (int slot in _compositorCandidateSlots)
        {
            ref var row = ref _slab.At(slot);
            if (!_scene.IsLive(row.Node) || !IsCompositorRow(in row)) continue;
            var identity = _compositorSeeds[slot];
            h = Mix(h, identity.Instance);
            h = Mix(h, identity.Revision);
            h = Mix(h, ((ulong)row.Node.Raw.Index << 32) | row.Node.Raw.Gen);
            h = Mix(h, ((ulong)(uint)PeriodMsOf(slot) << 16)
                | (ulong)(row.Flags & (AnimFlags.Parked | AnimFlags.Done | AnimFlags.Hold | AnimFlags.Paused)));
            n++;
        }
        return Mix(h, (ulong)n);

        static ulong Mix(ulong h, ulong v) => (h ^ v) * 1099511628211UL;
    }

    /// <summary>
    /// UI-only import before input/FLIP. Generation + instance + revision reject old completion and slot reuse.
    /// Completion applies the terminal pose before normal UI-owned settle/cleanup releases the desired row.
    /// </summary>
    public void ApplyCompositorFeedback(ReadOnlySpan<CompositorAnimationPose> poses)
    {
        if (!RenderOwnsCompositor) return;
        _compositorFeedbackAccumulators.Clear();
        _compositorCompletedSlots.Clear();
        foreach (ref readonly var pose in poses)
        {
            if (!_scene.IsLive(pose.Node)) continue;
            for (int slot = _slab.HeadOnNode((int)pose.Node.Raw.Index); slot >= 0; slot = _slab.At(slot).NextOnNode)
            {
                ref var row = ref _slab.At(slot);
                if (row.Has(AnimFlags.Parked) || row.Channel != pose.Channel || !_compositorSeeds.TryGetValue(slot, out var seed)
                    || seed.Instance != pose.InstanceId || seed.Revision != pose.Revision || !IsCompositorRow(in row)) continue;
                if (pose.Hidden && !pose.Done)
                {
                    // Nothing of it is on screen: keep the timing, leave the scene (and the row's shown value) untouched.
                    row.ElapsedMs = pose.ElapsedMs;
                    row.DelayRemainingMs = pose.DelayRemainingMs;
                    row.Flags &= ~(AnimFlags.JustSeeded | AnimFlags.StartPending);
                    break;
                }
                row.Position = pose.Value;
                row.Velocity = pose.Velocity;
                row.ElapsedMs = pose.ElapsedMs;
                row.DelayRemainingMs = pose.DelayRemainingMs;
                // The render thread resolved the pending start (RenderCompositorAnimations) — never re-pend on re-capture.
                row.Flags &= ~(AnimFlags.JustSeeded | AnimFlags.StartPending);
                if (IsSideTableChannel(row.Channel)) WriteSideTable(row.Channel, row.Node, row.Position);
                else
                {
                    ref var accumulation = ref CollectionsMarshal.GetValueRefOrAddDefault(_compositorFeedbackAccumulators, row.Node, out bool exists);
                    if (!exists) accumulation = Accum.FromPaint(in _scene.Paint(row.Node));
                    accumulation.Fold(row.Channel, Posed(in row, row.Position, in _scene.Bounds(row.Node), _scene.DeviceScale), replace: true);
                }
                if (pose.Done)
                {
                    row.Flags |= AnimFlags.Done;
                    _compositorCompletedSlots.Add(slot);
                }
                break;
            }
        }
        // Match the ordinary scheduler: fold all axes from one authored base, then compose once. Repeated
        // decomposition per channel introduces rotation/non-uniform-scale drift and can lose clip-edge channels.
        foreach (var entry in _compositorFeedbackAccumulators) Compose(entry.Key, entry.Value);
        foreach (int slot in _compositorCompletedSlots) { SettleRestore(slot); FreeSlot(slot); }
    }
}

/// <summary>Copied desired animation data. Keyframes contain only POD easing descriptions, never UI delegates.</summary>
public sealed class CompositorAnimationSnapshot
{
    internal struct Entry
    {
        public AnimValue Row;
        public ulong Instance, Revision;
        public bool ExplicitFrom;
        public Keyframe[] Keys;
        /// <summary>The row's resolved cadence period in ms (0 = display rate) — see the note at the Add call site.</summary>
        public ushort PeriodMs;
        /// <summary>NaN, or the UI's view of the row (taken against <see cref="BaseRevision"/>) that this re-seed's start
        /// departed from - see <c>AnimEngine.MarkSeedRelative</c>.</summary>
        public float Base;
        public ulong BaseRevision;
        /// <summary>The factor the re-seed's start re-based the UI's view by: the renderer scales its drift and velocity by it.</summary>
        public float DriftScale;
    }
    private Entry[] _entries = [];
    private int _count, _oldCount, _distinctNodes;
    private NodeHandle _lastAddedNode;
    public int Count => _count;

    /// <summary>How many DISTINCT nodes this desired set can pose — the compositor-overlay row demand
    /// <c>SceneRecordingSnapshot.ReserveCompositorRows</c> reserves for the same publication. Rows arrive grouped by
    /// node (the candidate list is built by walking each active node's slot chain), so counting node changes is exact
    /// in practice; a non-contiguous repeat would over-count, which is the safe direction for a reserve.</summary>
    public int DistinctNodeCount => _distinctNodes;
    public double CapturedAtMs { get; private set; }
    internal ref readonly Entry At(int index) => ref _entries[index];
    internal void BeginCapture(double now)
    {
        CapturedAtMs = now; _oldCount = _count; _count = 0;
        _distinctNodes = 0; _lastAddedNode = NodeHandle.Null;
    }
    internal void Add(in AnimValue row, ulong instance, ulong revision, bool explicitFrom, Keyframe[]? keys, ushort periodMs,
        float fromBase = float.NaN, ulong baseRevision = 0, float driftScale = 1f)
    {
        SceneRecordingSnapshot.Grow(ref _entries, _count + 1);
        if (row.Node != _lastAddedNode) { _distinctNodes++; _lastAddedNode = row.Node; }
        ref var target = ref _entries[_count++];
        int count = keys?.Length ?? 0;
        if (target.Keys is null || target.Keys.Length != count) target.Keys = new Keyframe[count];
        keys.AsSpan().CopyTo(target.Keys);
        target.Row = row;
        target.Row.NextOnNode = target.Row.NextActive = -1;
        target.Row.DrivenSrc = AnimValue.WallClock;
        target.Instance = instance; target.Revision = revision; target.ExplicitFrom = explicitFrom;
        target.PeriodMs = periodMs;
        target.Base = fromBase; target.BaseRevision = baseRevision; target.DriftScale = driftScale;
    }
    internal void EndCapture()
    {
        if (_oldCount > _count) Array.Clear(_entries, _count, _oldCount - _count);
    }
}

/// <summary>Latest render pose; settled entries persist until a later desired set omits their instance. One render-owned
/// row's pose fed back to the UI. <paramref name="Hidden"/>: its node could not reach a pixel on
/// the tick that produced it — the UI imports the timing only and composes nothing into its scene.</summary>
public readonly record struct CompositorAnimationPose(NodeHandle Node, AnimChannel Channel, ulong InstanceId, ulong Revision,
    float Value, float Velocity, float ElapsedMs, float DelayRemainingMs, bool Done, bool Hidden = false);
