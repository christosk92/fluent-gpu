using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Scene;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Extent;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text;

namespace FluentGpu.Reconciler;

/// <summary>
/// Patches the retained SceneStore from an immutable Element tree. Signals-first: every component is a reactive
/// render-effect (re-renders + reconciles ONLY its own subtree when its state/context changes — granular, never the
/// whole app); fine-grained bindings (TransformBind/OpacityBind/…) and reactive control-flow (<see cref="ShowEl"/>/
/// <see cref="ForEl"/>) are effects too. The keyed positional+type diff is retained as the STRUCTURAL engine (used on
/// re-render and behind For/Show); a reused component on a parent re-render is a no-op (it is autonomous).
/// </summary>
public sealed partial class TreeReconciler
{
    private readonly SceneStore _scene;
    private readonly StringTable _strings;

    // Mounted child components, keyed by their host node (the ComponentEl anchor).
    // Scope: the component's ONE lifetime owner (ReactiveCore's ReactiveScope, a stable owner that never re-runs). It
    // owns the render-effect (auto-disposed when the scope disposes) and carries the hook-cleanup teardown as a scope
    // cleanup, so component unmount == Scope.Dispose() (cascades: dispose render-effect → run RunAllCleanups). Node
    // bindings deliberately stay NODE-owned (_nodeBindings, disposed at node unmount) — a bind created during a render
    // outlives that render and can die with its node WITHOUT the component unmounting (Show/For swaps, KeepAlive
    // eviction), so it must not hang off the component scope. G4c's per-component props signal will hang off Scope too.
    private sealed class CompEntry { public Component Comp = null!; public Element? Rendered; public Type Type = null!; public Effect? Effect; public ReactiveScope? Scope; public bool Parked; public bool ExitFrozen; public bool DeferredRender; public bool QueuedReplay; public Signal<bool>? ActiveSig; public Signal<object?>? PropsSig; public SkeletonStyle? DerivedSkeletonStyle; public bool Hidden; }
    private readonly Dictionary<NodeHandle, CompEntry> _comps = new();
    private readonly Dictionary<Component, NodeHandle> _anchorOf = new();
    private readonly List<Component> _live = new();

    private struct BoundSlot
    {
        public Signal<int> Index;
        public Element El;
        public NodeHandle Root;
        public int ContentType;

        public BoundSlot(Signal<int> index, Element el, NodeHandle root, int contentType = 0)
        {
            Index = index;
            El = el;
            Root = root;
            ContentType = contentType;
        }
    }

    // The previously-realized window per virtual-list viewport (the keyed-diff's oldKids). Rented from ArrayPool.
    // Bound (RowBind) viewports keep persistent SLOTS instead: one signal/element/root per realized logical item.
    private sealed class VirtualEntry
    {
        public Element[]? Prev; public int PrevLen; public int PrevFirst; public VirtualListEl? El;
        // W2-E3: the viewport's VISIBLE item band [VisibleFirst, VisibleLast) as of the latest RealizeWindow — the
        // reference every image request from a realized slot classifies against (inside → Visible lane; realized in the
        // overscan halo, incl. the +1 guard rows → Overscan lane). Written before the realize dispatch so the cold-mount
        // requests of the same pass already see it; read again at the host's post-realize Flush by the bound ImageEl
        // effect's later fires (a RebindBoundSlot recycle drains there, outside the pass).
        public int VisibleFirst, VisibleLast;
        public List<BoundSlot>? Slots;
        // Retained slow-path scratch. Equal-size contiguous scrolls use the in-place rotation fast path; this is touched
        // only for cold grow/shrink or defensive invariant repair and grows only with the viewport high-water mark.
        public List<BoundSlot>? SlotScratch;
        public bool[]? SlotUsed;
        // Opt-in retained prefix: fixed-index slots stay attached ahead of Slots while the latter keep their ordinary
        // overlap/extended recycler semantics. Roots are stored because prefix nodes are temporarily detached while
        // the existing recycler operates on the normal child band, then restored before layout.
        public List<BoundSlot>? PrefixSlots;
        // Slot pool at the high-water mark (virtualization.md §6.1a, "slot pool"): bound slots the window no longer
        // needs are PARKED here instead of removed — detached from the content node (no layout/paint/hit-test),
        // NodeFlags.Parked (render-effects/animations quiesced), images unpinned — and taken back before rowBind on
        // the next grow. A parked slot KEEPS its index signal: writing a sentinel would run every channel of every
        // parked row through the rebind flush (fires, writes, the template's own formatting allocations) for rows
        // nobody can see, and the same rows are the first to come back on the next flutter/reversal, where an exact
        // index match makes the take a zero-write re-attach. Slots.Count + Spare.Count never exceeds the widest window
        // this list has realized.
        public List<BoundSlot>? Spare;
        // ── extended bound-realize state (research adjustments #5 keep-alive + #16 content-type) — allocated ONLY when
        //    ve.KeepAlive or ve.ContentType is set (the default RealizeBoundWindow leaves both null; byte-identical path).
        // Keep-alive bucket: item index → its parked slot (detached, hidden, quiesced). Bounded + LRU-evicted.
        public Dictionary<int, KeptSlot>? Kept;
    }

    /// <summary>Park a bound slot the window no longer needs: hover/press/focus hygiene, detach, quiesce, unpin its
    /// images, push on the entry's spare list. The index signal is left as is (see <see cref="VirtualEntry.Spare"/>);
    /// the subtree stays mounted so the next grow can take it back with at most one signal write instead of a
    /// <c>rowBind</c> + <c>Mount</c>. Same mechanics as a keep-alive park with ReleaseInactiveResources.</summary>
    private void ParkSpareSlot(VirtualEntry entry, in BoundSlot slot)
    {
        if (slot.Index is null || !_scene.IsLive(slot.Root)) return;
        OnSubtreeDeactivated?.Invoke(slot.Root);   // before Detach: the dispatcher's clear walk needs the live parent chain
        _scene.Unmark(slot.Root, NodeFlags.Hovered | NodeFlags.Pressed | NodeFlags.Focused | NodeFlags.FocusVisual);
        SetSubtreeResourcesActive(slot.Root, active: false);   // a parked row must not keep a cover resident
        SetSubtreeParked(slot.Root, parked: true);
        _scene.Detach(slot.Root);
        (entry.Spare ??= new List<BoundSlot>(4)).Add(slot);
    }

    /// <summary>Re-attach spare <paramref name="i"/> under <paramref name="content"/> for item <paramref name="index"/>:
    /// un-park, re-pin, and write the index signal only when it differs (a slot parked from this very row comes back
    /// with zero writes — the flutter/reversal case).</summary>
    private BoundSlot TakeSpareSlotAt(VirtualEntry entry, NodeHandle content, int i, int index)
    {
        var spare = entry.Spare!;
        var slot = spare[i];
        spare.RemoveAt(i);
        _scene.AppendChild(content, slot.Root);
        SetSubtreeResourcesActive(slot.Root, active: true);
        SetSubtreeParked(slot.Root, parked: false);
        if (slot.Index.Peek() != index) slot.Index.Value = index;
        _realizeProgress = true;   // the host's post-realize flush must run either way (re-attached subtree settles)
        return slot;
    }

    /// <summary>Take the parked slot still bound to exactly <paramref name="index"/>, if there is one (no signal write).</summary>
    private bool TryTakeSpareSlotExact(VirtualEntry entry, NodeHandle content, int index, out BoundSlot slot)
    {
        var spare = entry.Spare;
        if (spare is { Count: > 0 })
            for (int i = spare.Count - 1; i >= 0; i--)
            {
                if (spare[i].Index.Peek() != index) continue;
                if (!_scene.IsLive(spare[i].Root)) { spare.RemoveAt(i); continue; }   // defensive: freed behind our back
                slot = TakeSpareSlotAt(entry, content, i, index);
                return true;
            }
        slot = default;
        return false;
    }

    /// <summary>Take any parked slot for item <paramref name="index"/>. False when the pool is empty (the caller then
    /// mounts a fresh slot).</summary>
    private bool TryTakeSpareSlot(VirtualEntry entry, NodeHandle content, int index, out BoundSlot slot)
    {
        var spare = entry.Spare;
        while (spare is { Count: > 0 })
        {
            int i = spare.Count - 1;
            if (!_scene.IsLive(spare[i].Root)) { spare.RemoveAt(i); continue; }
            slot = TakeSpareSlotAt(entry, content, i, index);
            return true;
        }
        slot = default;
        return false;
    }

    /// <summary>Free parked slots beyond <paramref name="keep"/>: the pool trim (ItemCount fell below the pool and the
    /// kernel is idle) and the unmount release both land here.</summary>
    private void FreeSpareSlots(VirtualEntry entry, int keep)
    {
        var spare = entry.Spare;
        if (spare is null) return;
        keep = Math.Max(0, keep);
        while (spare.Count > keep)
        {
            int i = spare.Count - 1;
            var s = spare[i];
            spare.RemoveAt(i);
            if (_scene.IsLive(s.Root) && _scene.Parent(s.Root).IsNull)
            {
                UnmountSubtree(s.Root);
                _scene.FreeSubtree(s.Root);
            }
            _reconciled = true;
        }
    }

    /// <summary>Unmount and free every keep-alive-parked row of a list that is itself unmounting: like the spares they
    /// are detached, so the list's FreeSubtree cannot reach them.</summary>
    private void FreeKeptSlots(VirtualEntry entry)
    {
        if (entry.Kept is not { Count: > 0 } kept) return;
        foreach (var ks in kept.Values)
            if (_scene.IsLive(ks.Root) && _scene.Parent(ks.Root).IsNull)
            {
                UnmountSubtree(ks.Root);
                _scene.FreeSubtree(ks.Root);
            }
        kept.Clear();
        _reconciled = true;
    }

    /// <summary>Probe seam (VerticalSlice gate.virt.slotPool*): parked spare slots of the bound list at <paramref name="viewport"/>.</summary>
    internal int SpareSlotCount(NodeHandle viewport)
        => _virtuals.TryGetValue(viewport, out var e) && e.Spare is { } sp ? sp.Count : 0;

    /// <summary>Probe seam: the root node of the <paramref name="i"/>-th parked spare slot.</summary>
    internal bool TryGetSpareSlotRoot(NodeHandle viewport, int i, out NodeHandle root)
    {
        if (_virtuals.TryGetValue(viewport, out var e) && e.Spare is { } sp && (uint)i < (uint)sp.Count)
        {
            root = sp[i].Root;
            return true;
        }
        root = NodeHandle.Null;
        return false;
    }

    // A keep-alive-parked bound slot (research adjustment #5): its subtree stays mounted but is detached from the content
    // node (no layout/paint), parked (render-effects/animations quiesced via SetSubtreeParked), and kept bound to its item
    // so its live state (a mid-edit TextBox, an in-flight UseResource) survives until the item re-enters the window.
    private sealed class KeptSlot
    {
        public Signal<int> Index = null!;
        public Element El = null!;
        public NodeHandle Root;
        public int ContentType;
        public long LastUsed;   // FrameEpoch of the last park/touch — the LRU key
    }
    // Most keep-alive-parked rows one bound list retains; the least recently parked is evicted (unmounted) beyond it.
    private const int KeptSlotCap = 8;
    private readonly Dictionary<NodeHandle, VirtualEntry> _virtuals = new();

    /// <summary>Bumped once per host Paint — the realize walk runs several times per paint and some per-frame
    /// bookkeeping (the dirty-queue scan census) keys off it.</summary>
    public int FrameEpoch;
    /// <summary>True while a just-un-parked keep-alive subtree is still dripping its deferred renders — the host ORs
    /// this into its wake mask so the loop keeps running until it finishes.</summary>
    public bool HasWarmingVirtuals => _replayQueue.Count > 0;

    // ── Un-park replay budget (KeepAlive page return) ──────────────────────────────────────────────────────────────
    // While a KeepAlive page is parked its components skip their render-effects and record the debt (RunComponent sets
    // DeferredRender). Un-parking released the WHOLE debt into ONE flush — measured 143 component renders in a single
    // 13.6 ms paint on an artist-page return (a per-frame overlay ×83, cover shimmers ×44, chart rows, shelves) — which
    // janks the page-enter animation. So the replay is BUDGETED exactly like the cold-realize stagger above: the first
    // UnparkReplaysPerFrame debtors of the un-park walk (pre-order ⇒ ancestors and the top of the page lead) replay in
    // the un-park flush itself, the rest queue and drip at the same rate per frame (drained from BeginRenderCensus).
    // K is sized to cover a screenful because this layer has no viewport info: the deferred remainder is typically
    // off-screen cards/sections, and a queued debtor still shows the valid content it rendered before it was parked.
    // Never deferred: the activation signal (UseIsActive) flips immediately in the walk, and a real signal write that
    // reaches a queued component schedules it normally (RunComponent cancels the queue slot) — the drip only carries
    // entries whose ONLY reason to run is the park debt.
    // DISABLED (int.MaxValue = no drip): a returning page replays its whole park debt in the un-park flush. The
    // measurement above (143 component renders in one 13.6 ms paint) is the cost this budget was spreading out; it is
    // being re-judged against how the drip actually feels on a fast machine, where paying it once beats a page that
    // keeps filling in behind its own enter animation. Restore the 24 if the enter jank returns.
    private const int UnparkReplaysPerFrame = 1_000_000;
    private readonly Queue<CompEntry> _replayQueue = new();
    private int _replayBudgetUsed;
    private int _replayBudgetEpoch = -1;
    /// <summary>True while a just-un-parked keep-alive subtree still owes queued renders (the drip is mid-flight).</summary>
    public bool HasDeferredReplays => _replayQueue.Count > 0;

    private bool _realizeProgress;   // set by RealizeWindow when the realized window actually changed (drives the 2-pass loops)
    // ── W2-E3: the realize-pass image-priority context ─────────────────────────────────────────────────────────────
    // Valid only while RealizeWindow is mounting / updating / rebinding the slots of ONE viewport (saved and restored
    // around the nested case — a rail realized inside a page row). An image request made under it decides its
    // DecodeScheduler lane from the slot being realized (ImageRequestPriority): inside the viewport's visible band →
    // Visible; realized in the overscan halo → Overscan. Outside any realize pass (a page-level cover, a Show/For swap
    // inside an already-realized row) the context is empty and a request keeps Visible — never a demotion by accident.
    // Before this every request was Visible, so a fling that realized 30 rows started 30 Visible decodes at once and the
    // scheduler's Overscan/Prefetch lanes and its backpressure drop arm were dead.
    private VirtualEntry? _realizeEntry;       // the viewport whose window is being realized
    private int _realizeSlotIndex = -1;         // the logical item index of the slot being mounted/updated (-1 = unknown)
    private Signal<int>? _realizeSlotSignal;    // bound path: that slot's index signal (captured by the ImageEl effect)
    private bool _realizeOuterOverscan;         // an ENCLOSING pass classified our viewport's own row as overscan
    // Probe seams (VerticalSlice gate.virt.*): dirty-queue entries examined / realized, SUMMED across every ReRealizeVirtuals
    // call in a Paint (there are up to three) and reset on the FrameEpoch tick — proves the steady path iterates the
    // scene-owned queue (== the dirty count), never scans the _virtuals dictionary.
    internal int LastReRealizeScan;
    internal int LastReRealizeRealized;
    private int _scanFrameEpoch = -1;

    // Context provider value signals, keyed by provider node index (a consumer resolves by walking ancestors).
    private readonly Dictionary<int, (object Channel, Signal<object?> Sig)> _providerSig = new();
    // Host-published ambient contexts (Viewport.Size, FrameDiagnostics.Current), keyed by channel.
    private readonly Dictionary<object, Signal<object?>> _ambient = new();


    // Per-node reactive bindings + control-flow effects, disposed when the node is unmounted.
    private readonly Dictionary<int, List<Computation>> _nodeBindings = new();
    private readonly Dictionary<int, Element?> _showState = new();             // last-mounted branch per ShowEl node
    private readonly Dictionary<int, ShowEl> _showEl = new();                  // latest ShowEl per boundary node (parent re-renders replace it — see UpdateShow)
    private readonly Dictionary<int, Effect> _showEffect = new();              // the Show boundary effect, rescheduled by UpdateShow
    private readonly Dictionary<int, (Element[] Prev, int Len)> _forState = new();   // last realized children per ForEl node
    private readonly Dictionary<int, ForElBase> _forEl = new();                       // latest ForElBase per boundary node (parent re-renders replace it — see UpdateFor)
    private readonly Dictionary<int, Effect> _forEffect = new();                      // the For boundary effect, rescheduled by UpdateFor
    private readonly Dictionary<int, float> _childStagger = new();                   // node → per-child Enter stagger (ms): a parent's Element.Stagger, read by SynthesizeDeclarative
    // Element.Stagger's index base for the child diff in flight: a diff mounts every NEW child after ALL of the parent's
    // existing children (the unmatched old ones are removed only afterwards), so an entering child's live sibling index
    // counts surviving AND departing siblings. StaggerDelayMs subtracts the base for that parent → the ordinal among the
    // children entering in this pass. Saved/restored around nested diffs.
    private NodeHandle _staggerDiffParent;
    private int _staggerDiffBase;
    private readonly Dictionary<string, NodeHandle> _keyNode = new();                 // MorphId → node: the shared-layout anchor a RelativeTo follower FLIPs against
    private readonly Dictionary<int, string> _morphKeyByNode = new();                 // node → MorphId; gates shared-element teardown to actual participants
    private readonly Dictionary<int, string> _relativeKey = new();                    // follower node → the MorphId key it FLIPs relative to (Element.RelativeTo)
    private readonly Dictionary<int, NodeHandle> _mirroredChild = new();             // transparent anchor → the child MirrorParticipation last mirrored (RemirrorAncestors)
    // Skeleton-loading: per SkelRegionEl node, the last branch (0 none / 1 shimmer / 2 real / 3 failed), the last-mounted
    // child element (for ReconcileSingleChild's type-compare), and the reveal-group token (for the group coordinator).
    private readonly Dictionary<int, (byte Branch, Element? El, object? Group)> _skelState = new();
    private readonly Dictionary<int, SkelRegionEl> _skelEl = new();
    private readonly Dictionary<int, Effect> _skelEffect = new();
    private readonly HashSet<int> _skelForce = new();
    // Loading-scrollbar suppression is node-owned. Remember the exact viewport each pending region incremented so an
    // unmount, branch replacement, or independent sibling completion releases precisely its own claim.
    private readonly Dictionary<int, NodeHandle> _skelScrollSuppression = new();
    // KeepAlive boundaries retain inactive page subtrees detached from the live child chain. Entries are node-owned so
    // unmounting the boundary releases every parked component/effect/resource deterministically.
    private sealed class KeepAliveEntry
    {
        public string Key = "";
        public object Token = null!;
        public Element El = null!;
        public NodeHandle Root;
        public long LastUsed;
        public bool Attached;
        public bool ResourcesActive = true;
        public bool Cacheable = true;
    }
    private sealed class KeepAliveState
    {
        public readonly Dictionary<string, KeepAliveEntry> Entries = new();
        public string? ActiveKey;
        public string? ExitingKey;
        // Backstop deadlines for the CURRENT ExitingKey (mirrors the orphan path's own-deadline + host wall-clock
        // guard — see ExitMaxAgeMs / AppHost.OrphanSettleTimeoutMs): a parked/wedged exit row never advances
        // (HasTracks stays true forever), so FinalizeKeepAliveTransitions needs a deadline independent of tracks
        // settling. Zeroed whenever ExitingKey is nulled — not load-bearing, just honest state.
        public double ExitDeadlineAnimMs;
        public long ExitStartTicks;
        public KeepAliveOptions? Options;
        public NodeHandle Boundary;
        public long Clock;
        public int TransientSeq;
    }
    private readonly Dictionary<int, KeepAliveState> _keepAliveState = new();
    // Reverse map: a KeepAlive entry's ROOT node index → its slot key.
    private readonly Dictionary<int, string> _keepAliveRootKey = new();


    /// <summary>Host hook (scroll rework §9): a viewport's <c>ScrollKey</c> was set at mount or changed on a content
    /// swap — <c>(node, oldKey, newKey)</c>. The host saves the outgoing offset under the old key and restores (or
    /// resets to the top) for the new key through the viewport's <c>ScrollHandle</c>.</summary>
    public Action<NodeHandle, string?, string?>? ScrollKeyChanged { get; set; }
    /// <summary>Host hook: persist a departing viewport's offset for its <c>ScrollKey</c> BEFORE anything new mounts.</summary>
    public Action<NodeHandle>? SaveScrollPosition { get; set; }
    private readonly HashSet<long> _imagePinnedNodes = new();
    private readonly Dictionary<int, List<NodeHandle>> _imageNodes = new();   // imageId → nodes that pinned it (for status→dirty)
    // ImageEl.KeepWhileHidden: nodes (scene index) that asked their image to stay resident while the window is hidden, and the
    // pins (node, id) that took a keep reference for it, so an unpin gives back exactly what its pin took.
    private readonly HashSet<int> _keepWhileHiddenNodes = new();
    private readonly HashSet<long> _keepWhileHiddenPins = new();
    // Hold-last-good (media-pipeline.md §hold-last-good): node index → the NEW (still-decoding) image id a re-keyed
    // Image node is holding while its OLD Ready texture keeps drawing. Absent ⇒ no hold in progress for that node.
    // Lives here, not on ImageVisualEffects, because that struct is rewritten wholesale from the element every reconcile.
    private readonly Dictionary<int, int> _pendingImageId = new();

    private Component? _root;
    private Element? _oldRoot;
    private Effect? _rootEffect;
    private bool _reconciled;   // set when any structural/column change happened → the host runs (scoped) layout
    // Set during Update/WriteColumns/structural reconcile when layout shape actually changed. RunComponent/RunRoot
    // mark their rendered root LayoutDirty only when this is true — so paint-only re-renders skip layout, while a
    // deep structural/size change still dirties ABOVE ContentSized scroll firewalls (TabView strip / add button).
    // Scope-local by construction: the only READS are inside RunRoot/RunComponent, each after clearing it, so a value
    // left set by a writer running OUTSIDE a render scope (the host's ReRealizeVirtuals) is inert, never a false mark.
    private bool _layoutShapeMutated;
    private int _renderCount;   // component render-effects that ran since the last frame (granularity metric)
    private int _keepAliveLayoutSuppressionFrames;   // activation commit + first bounds-measure correction
    private float _themeTransitionMs = float.NaN;        // live-re-theme cross-fade duration; armed only during a RethemeAll flush
    private readonly List<CompEntry> _rethemeScratch = new();   // snapshot of _comps.Values for RethemeAll (defensive vs reentrancy)

    /// <summary>Per-frame component census (type → renders, render/reconcile ticks, UI-thread bytes), the answer to
    /// "WHICH components made this frame slow and WHO allocated". Host-set (an app that wants every slow frame
    /// attributed in its own log turns it on for the whole session — there is no env switch). Cost while on: one
    /// dictionary op per component render on the cached <c>GetType().Name</c>; off = a null dict and one branch.</summary>
    public bool RenderCensusEnabled { get; set; }
    /// <summary>Dump the census for any frame that rendered at least this many components (default 25) even when the
    /// flush itself stayed under budget, so a probe can see WHICH components a single recycled row re-renders.</summary>
    public int RenderCensusMinComps { get; set; } = 25;
    private long _lastChurnDumpTicks;
    // Per component TYPE this frame: renders, ticks inside Render (element construction + hooks), ticks inside the
    // synchronous child reconcile that follows, and UI-thread bytes across both — so a census line says not only WHO
    // re-rendered but whether the cost is building the element tree or diffing it into the scene.
    // Cause: what scheduled the most recent render of the type this frame (Computation.StaleCause, read + cleared at the
    // render) — a reference, resolved to text only when a line is built; CauseTag 1 = first render (mount), 2 = the
    // component's re-pushed props signal, 0 = the Cause source (null = an imperative schedule / forced run).
    private struct CensusEntry { public int Count; public long RenderTicks, ReconcileTicks, Bytes; public ISignalSource? Cause; public byte CauseTag; }
    // Element-level counters for the same frame (census on only): Update calls, column rewrites (RecordChanged true),
    // child plans begun, and node mounts — "how many scene nodes did this frame's renders actually touch".
    private int _censusUpdates, _censusWrites, _censusPlans, _censusMounts;
    /// <summary>ALWAYS-ON monotonic count of scene nodes this reconciler has mounted (one int increment in
    /// <see cref="Mount"/>, no allocation, no gate — unlike <c>_censusMounts</c>, which only counts while the render
    /// census is on). The cold-realize ramp charges its node budget against the DELTA across one grow, so it cannot
    /// depend on a diagnostic being enabled. Wraps harmlessly: the ramp only ever reads a difference.</summary>
    internal int MountedNodes;
    private Dictionary<string, CensusEntry>? _renderCensus;
    private readonly List<KeyValuePair<string, CensusEntry>> _censusScratch = new(32);
    private readonly StringBuilder _censusSb = new(256);
    /// <summary>Last spike dump line (empty when none this process). Probes / stderr consumers can peek it.</summary>
    public string LastRenderCensusDump { get; private set; } = "";

    /// <summary>True (and reset) if any mount/update/remove happened since the last call — the host's "layout needed" gate.</summary>
    public bool ConsumeReconciled() { var r = _reconciled; _reconciled = false; return r; }

    /// <summary>Number of component render-effects that ran since the last call (proves granular re-render in tests).</summary>
    public int ConsumeRenderCount() { var c = _renderCount; _renderCount = 0; return c; }
    /// <summary>Current frame's render-effect count without resetting (census dump before <see cref="ConsumeRenderCount"/>).</summary>
    public int PeekRenderCount() => _renderCount;

    /// <summary>Consume one frame of a KeepAlive activation's opt-in layout-transition suppression. The activation
    /// frame lands the route swap itself; the second frame lands self-measuring controls that publish their first real
    /// bounds after layout. Page-root enter/opacity tracks remain authored by <c>TransitionFor</c>.</summary>
    public bool ConsumeKeepAliveLayoutSuppressionFrame()
    {
        if (_keepAliveLayoutSuppressionFrames <= 0) return false;
        _keepAliveLayoutSuppressionFrames--;
        return true;
    }

    /// <summary>The per-paint reconciler tick. Drips the budgeted un-park replay queue (BEFORE the frame's reactive
    /// flush, so the drained batch renders in this same frame), releases image-swap crossfades whose window has landed,
    /// and clears the per-frame render-type histogram (the histogram half is a no-op unless
    /// <see cref="RenderCensusEnabled"/>). Call at Paint start.</summary>
    public void BeginRenderCensus()
    {
        DrainDeferredReplays();
        if (_imageSwaps.Count > 0) SweepImageSwaps();   // release image-swap crossfades whose window has landed
        NodeBindingFireCount = 0;
        NodeBindingWriteCount = 0;
        if (!RenderCensusEnabled) { _renderCensus = null; return; }
        _renderCensus ??= new Dictionary<string, CensusEntry>(64, StringComparer.Ordinal);
        _renderCensus.Clear();
        _censusUpdates = _censusWrites = _censusPlans = _censusMounts = 0;
    }

    /// <summary>P0 always-on counter: every bound-channel effect PROLOGUE this frame (mount runNow + every re-fire from
    /// the reactive flush + every bound→bound re-wire re-run, <see cref="RewireBinds"/>), incremented as the first
    /// statement of each binding body in <see cref="BindNode"/> — fires even when the effect's own equality gate refuses
    /// to write. Reset at <see cref="BeginRenderCensus"/> (Paint start).
    /// Surfaced into <c>FrameStats.BindingFires</c>.</summary>
    public int NodeBindingFireCount { get; private set; }

    /// <summary>P0 always-on counter: binding effects this frame that actually wrote a scene column (past every
    /// equality gate) — a subset of <see cref="NodeBindingFireCount"/>. An UNGATED channel (HoverFill/PressedFill/
    /// BorderColor/Corners/TextEl.Color/ImageEl.Placeholder/IconLayerEl.Tint today — see BindNode) writes on every
    /// fire, so fire==write there until a later phase gates it. Reset at <see cref="BeginRenderCensus"/>. Surfaced
    /// into <c>FrameStats.BindingWrites</c>.</summary>
    public int NodeBindingWriteCount { get; private set; }

    private void NoteRenderCensus(Component comp, long renderTicks, long reconcileTicks, long bytes,
                                  ISignalSource? cause = null, byte causeTag = 0)
    {
        if (_renderCensus is null) return;   // census off (the steady/shipping case) — no GetType().Name work below
        string typeName = comp.GetType().Name;
        _renderCensus.TryGetValue(typeName, out var e);
        e.Count++; e.RenderTicks += renderTicks; e.ReconcileTicks += reconcileTicks; e.Bytes += bytes;
        e.Cause = cause; e.CauseTag = causeTag;
        _renderCensus[typeName] = e;
    }

    /// <summary>The census's <c>by=</c> text for one entry (report time only; allocates).</summary>
    private static string CauseText(in CensusEntry e) => e.CauseTag switch
    {
        1 => "mount",
        2 => "props",
        _ => SignalDiag.Describe(e.Cause),
    };

    /// <summary>If this frame's flush is over <paramref name="budgetMs"/> (the host passes the panel's refresh interval)
    /// or rendered ≥ <see cref="RenderCensusMinComps"/> components, build one census line — the top-12 types by
    /// render+reconcile time, then the top-4 by UI-thread bytes — stash it in <see cref="LastRenderCensusDump"/> and
    /// forward it to <see cref="Diag.Sink"/>. Returns the line (null when not a spike / census off). The host carries it
    /// in <c>FrameStats.Census</c>; the app's own frame log is where it lands.</summary>
    public string? MaybeDumpRenderCensus(double budgetMs, double flushMs, double reactiveFlushMs, double virtualRealizeMs, int comps, bool scrollActive)
    {
        if (_renderCensus is null) return null;
        // Spike-gated as before, PLUS a once-a-second sample of any frame that rendered anything at all. A settled page
        // is supposed to render nothing: the frames that quietly re-render a handful of components at 120 Hz never trip
        // the spike gate, and they are where a session's managed allocation actually goes (tens of KB per frame, every
        // frame, is what turns into a gen1 pause twice a second). Without this line that churn is invisible — the only
        // evidence is an allocation total with nothing to attribute it to.
        bool spike = flushMs >= budgetMs || comps >= RenderCensusMinComps;
        if (!spike)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            if (comps <= 0 || nowTicks - _lastChurnDumpTicks < Stopwatch.Frequency) { LastRenderCensusDump = ""; return null; }
            _lastChurnDumpTicks = nowTicks;
        }
        _censusScratch.Clear();
        foreach (var kv in _renderCensus) _censusScratch.Add(kv);
        _censusScratch.Sort(static (a, b) => (b.Value.RenderTicks + b.Value.ReconcileTicks).CompareTo(a.Value.RenderTicks + a.Value.ReconcileTicks));
        _censusSb.Clear();
        _censusSb.Append("[render-census] flush=").Append(flushMs.ToString("0.0", CultureInfo.InvariantCulture))
            .Append("ms nodes(upd=").Append(_censusUpdates.ToString(CultureInfo.InvariantCulture))
            .Append(" wr=").Append(_censusWrites.ToString(CultureInfo.InvariantCulture))
            .Append(" plans=").Append(_censusPlans.ToString(CultureInfo.InvariantCulture))
            .Append(" mounts=").Append(_censusMounts.ToString(CultureInfo.InvariantCulture)).Append(')')
            .Append("ms rx=").Append(reactiveFlushMs.ToString("0.0", CultureInfo.InvariantCulture))
            .Append(" vr=").Append(virtualRealizeMs.ToString("0.0", CultureInfo.InvariantCulture))
            .Append(" comps=").Append(comps.ToString(CultureInfo.InvariantCulture))
            .Append(scrollActive ? " scroll=1" : " scroll=0")
            .Append(" top=");
        int n = Math.Min(12, _censusScratch.Count);
        if (n == 0) _censusSb.Append("(none)");
        for (int i = 0; i < n; i++)
        {
            if (i > 0) _censusSb.Append(',');
            var e = _censusScratch[i].Value;
            double toMs = 1000.0 / Stopwatch.Frequency;
            _censusSb.Append(_censusScratch[i].Key).Append('×').Append(e.Count.ToString(CultureInfo.InvariantCulture))
                .Append("(r=").Append((e.RenderTicks * toMs).ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" c=").Append((e.ReconcileTicks * toMs).ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" a=").Append((e.Bytes / 1024).ToString(CultureInfo.InvariantCulture)).Append('K')
                .Append(" by=").Append(CauseText(in e)).Append(')');
        }
        // The same frame's allocators, ordered by bytes: a cheap-to-render component that allocates 6 MB is the memory
        // story even when it is not the time story.
        _censusScratch.Sort(static (a, b) => b.Value.Bytes.CompareTo(a.Value.Bytes));
        int m = Math.Min(4, _censusScratch.Count);
        _censusSb.Append(" bytes=");
        if (m == 0 || _censusScratch[0].Value.Bytes <= 0) _censusSb.Append("(none)");
        for (int i = 0; i < m && _censusScratch[i].Value.Bytes > 0; i++)
        {
            if (i > 0) _censusSb.Append(',');
            var e = _censusScratch[i].Value;
            _censusSb.Append(_censusScratch[i].Key).Append('×').Append(e.Count.ToString(CultureInfo.InvariantCulture))
                .Append('=').Append((e.Bytes / 1024).ToString(CultureInfo.InvariantCulture)).Append('K');
        }
        string line = _censusSb.ToString();
        LastRenderCensusDump = line;
        if (Diag.Sink is { } sink) sink(line);
        return line;
    }

    /// <summary>The reactive scheduler — one per host; signals schedule render-effects/bindings here, the host flushes it.</summary>
    public ReactiveRuntime Runtime { get; }

    /// <summary>Live nested components — the host drains their effects each frame.</summary>
    public List<Component> LiveComponents => _live;

    // ── O(1) census accessors (read by the MemCensus sampler; trivial .Count reads) ───────────────
    /// <summary>Mounted component entries (the <c>_comps</c> anchor map) — O(1) census.</summary>
    public int ComponentCount => _comps.Count;
    /// <summary>Nodes carrying reactive bindings / control-flow effects (the <c>_nodeBindings</c> map) — O(1) census.</summary>
    public int NodeBindingCount => _nodeBindings.Count;
    /// <summary>Virtual-list viewport boundaries (the <c>_virtuals</c> map) — O(1) census.</summary>
    public int VirtualBoundaryCount => _virtuals.Count;
    /// <summary>Context-provider value signals (the <c>_providerSig</c> map) — O(1) census.</summary>
    public int ProviderCount => _providerSig.Count;

    /// <summary>Set by the host; injected into each component so animation hooks can seed tracks on their node.</summary>
    public AnimEngine? Anim { get; set; }

    /// <summary>P3 (Operation ultra-fast GPU engine, virtualization.md §5.5 "Recycle snaps transitions"): a depth
    /// counter (re-entrancy-safe — always paired via <see cref="PushSuppressBoundTransitions"/> in a <c>using</c>) the
    /// host sets around <c>FlushRebindsToQuiescence</c> and around slot-creation mounts inside <c>RealizeBoundWindow</c>
    /// (and its Extended/PersistentPrefix siblings). While &gt; 0, a bound-channel transition seed (BrushFade/spring on
    /// a bound write) and the P1 <see cref="Element.Visible"/> false→true Enter seed both SNAP instead of animating —
    /// a recycled slot's freshly-rebound row must not replay a cross-fade/pop for what is, from the app's point of
    /// view, the SAME persistent row simply pointing at new data. No app code decides "this is a recycle"; the engine
    /// derives it from where the write physically happens.</summary>
    public int SuppressBoundTransitions { get; private set; }

    /// <summary>RAII helper for <see cref="SuppressBoundTransitions"/> — <c>using (reconciler.PushSuppressBoundTransitions())</c>.</summary>
    public SuppressBoundTransitionsScope PushSuppressBoundTransitions() { SuppressBoundTransitions++; return new SuppressBoundTransitionsScope(this); }

    public readonly struct SuppressBoundTransitionsScope : IDisposable
    {
        private readonly TreeReconciler _r;
        internal SuppressBoundTransitionsScope(TreeReconciler r) => _r = r;
        public void Dispose() => _r.SuppressBoundTransitions--;
    }
    /// <summary>Set by the host; shared-element (connected-animation) registry. A node carrying <c>Element.MorphId</c> is
    /// registered as a participant here so its art flies between routes (backdrop-effects-animation.md §5.4/§5.6).</summary>
    public ConnectedAnimation? Connected { get; set; }
    /// <summary>Set by the host (→ AppHost.WakeFrame); injected into each component so an escape hatch that has already
    /// mutated retained scene state can wake the frame loop WITHOUT scheduling its own render-effect.</summary>
    public Action? RequestFrame { get; set; }
    /// <summary>Set by the host; image nodes request decodes through it and pin/unpin for residency (liveness).</summary>
    public ImageCache? Images { get; set; }
    /// <summary>Set by the host; bumped on any image status change so <c>UseImage</c> consumers re-render granularly.</summary>
    /// <summary>Set by the host; clears input/focus state when a retained subtree is parked off the live scene chain.</summary>
    public Action<NodeHandle>? OnSubtreeDeactivated { get; set; }
    /// <summary>Set by the host; called at the top of <see cref="Remove"/>, while the subtree's parent chain is still
    /// walkable, so the dispatcher can run the hover exit for a hovered row that is about to orphan or free. NOT the
    /// deactivation hook: a removal ends no captured gesture and must not clear focus/press/drag.</summary>
    public Action<NodeHandle>? OnSubtreeRemoved { get; set; }
    /// <summary>Set by the host; called when a bound-list recycle rebinds a LIVE slot root to a different item. The
    /// handle survives the rebind, so state keyed on it (an in-flight drag's source) must let go here — the IsLive
    /// prunes never fire for it. Hot scroll path: the host handler is O(1) unless a drag is armed or active.</summary>
    public Action<NodeHandle>? OnSlotRebound { get; set; }
    /// <summary>Set by the host; called when a component context's passive/layout effect queue transitions 0→1.</summary>
    public Action<RenderContext, bool>? RegisterPendingEffectContext { get; set; }
    /// <summary>Set by the host; called for each node as a subtree is parked/un-parked by KeepAlive so the animation +
    /// scroll tickers can quiesce that node's tracks (a parked, invisible tab must not keep the app awake / defeat the
    /// idle wake-stop). Wired to <c>AnimEngine.SetNodeParked</c> + <c>ScrollBarChrome.SetNodeParked</c> (a parked viewport
    /// is also skipped by the scroll coverage, so the render poser stops posing it).</summary>
    public Action<NodeHandle, bool>? OnNodeParkedChanged { get; set; }

    public TreeReconciler(SceneStore scene, StringTable strings, ReactiveRuntime? runtime = null)
    {
        _scene = scene;
        _strings = strings;
        _scene.Strings = strings;   // text-id lifetime accounting: FreeSubtree releases paint.Text / TextStyle.Family
        Runtime = runtime ?? new ReactiveRuntime();
    }

    /// <summary>Swap a node's text id with ownership accounting (the scene's text column holds a ref per live node, so
    /// streamed virtual-list strings are reclaimed by the StringTable once no node shows them).</summary>
    private void SetPaintText(ref NodePaint paint, StringId next)
    {
        _strings.AddRef(next);
        _strings.Release(paint.Text);
        paint.Text = next;
    }

    /// <summary>Publish an ambient context (e.g. Viewport.Size) as a host-owned signal consumers can read.</summary>
    public void SetAmbient(object channel, Signal<object?> sig) => _ambient[channel] = sig;

    // ── Live re-theme (host-driven) ────────────────────────────────────────────────────────────────
    // Re-render every mounted component IN PLACE so it re-reads the active token set after a Tok.Use/SetAccent, with the
    // resulting fill/border/text color diffs cross-faded. No remount — node identity + component state survive.

    /// <summary>Arm/disarm the live-re-theme cross-fade window. While &gt; 0, <c>WriteColumns</c> seeds a
    /// <see cref="FluentGpu.Scene.BrushAnim"/> for EVERY fill/border/text color diff at this duration — overriding the
    /// element's own <c>BrushTransitionMs</c> default (<c>NaN</c> = snap) — so a whole-app token swap animates uniformly.
    /// The host sets it around exactly the flush that runs the <see cref="RethemeAll"/>-scheduled re-renders, then clears
    /// it (<c>NaN</c>) so ordinary logical-state flips keep their own per-element timing afterward.</summary>
    public void SetThemeTransition(float ms) => _themeTransitionMs = ms;

    /// <summary>The live-re-theme cross-fade duration when armed, else the element's own value — the one chokepoint the
    /// BrushAnim seeding blocks below consult so the override is applied identically to fill, border, and text.</summary>
    private float ThemeTransitionOr(float elementMs)
        => (!float.IsNaN(_themeTransitionMs) && _themeTransitionMs > 0f) ? _themeTransitionMs : elementMs;

    /// <summary>Schedule a live re-theme after a <c>Tok.Use</c>/<c>SetAccent</c>: re-run EVERY reactive computation that
    /// reads the token set so each picks up the new theme, IN PLACE (diff, not remount — state + node identity survive):
    /// <list type="bullet">
    /// <item>every mounted component's render-effect (and the root) — scheduling re-runs each render body against its
    /// SAME <see cref="RenderContext"/> (keyed hook cells preserved), so a direct <c>Tok.*</c> read in a render body
    /// (the former <c>Setup</c> idiom) picks up the new theme in place, with hook state + node identity intact;</item>
    /// <item>every node binding + control-flow boundary in <c>_nodeBindings</c> — <c>Flow.For</c>/<c>Flow.Show</c>/skeleton
    /// boundary effects (which build their rows/branches reading tokens, and are NOT component re-renders) and bound
    /// color channels (<c>Fill</c>/<c>Color</c> = <c>Prop.Of(() =&gt; Tok.X)</c>, owned by their effect, never reached by a
    /// re-render). Without this, Flow.For lists and bound/frozen surfaces keep the old theme.</item>
    /// </list>
    /// <c>Schedule()</c> only enqueues — the re-runs happen in the host's next flush; wrap that flush in
    /// <see cref="SetThemeTransition"/> so the resulting color diffs cross-fade (re-rendered nodes cross-fade; bound
    /// channels snap, as they bypass the BrushAnim path by design).</summary>
    public void RethemeAll()
    {
        _rootEffect?.Schedule();
        _rethemeScratch.Clear();
        foreach (var e in _comps.Values) _rethemeScratch.Add(e);   // snapshot: Schedule won't mutate _comps, but be defensive
        for (int i = 0; i < _rethemeScratch.Count; i++)
            _rethemeScratch[i].Effect?.Schedule();
        _rethemeScratch.Clear();
        // Re-run bindings + control-flow boundaries (Flow.For rows, bound colors, Show/skeleton branches). They read
        // tokens but are not component renders, so a component re-render alone leaves them on the old theme. Scheduling
        // is enqueue-only (no _nodeBindings mutation here); harmless for token-independent binds (they re-fire to the
        // same value, equality-gated). The host's transition window cross-fades the re-rendered diffs these produce.
        foreach (var list in _nodeBindings.Values)
            for (int i = 0; i < list.Count; i++)
                list[i].Schedule();
    }

    // ── Root ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Mount a root COMPONENT as a reactive render-effect (the host path): it renders into <c>Scene.Root</c> and
    /// re-renders itself (only) when its own state/context changes.</summary>
    public void MountRoot(Component root)
    {
        _root = root;
        InjectContext(root.Context, NodeHandle.Null);   // root resolves ambient contexts only
        root.Context.Owner = root;   // diagnostic identity for the hook computations this context will create
        var effect = new Effect(Runtime, () => RunRoot(root), owner: null, runNow: false) { DiagOwner = root };
        _rootEffect = effect;
        root.Context.RequestRerender = effect.Schedule;
        effect.RunNow();
    }

    /// <summary>Unmount the whole component tree (host Dispose): dispose the root render-effect, run the root component's
    /// hook cleanups, then unmount every node (component scopes, bindings, image pins, keep-alive pages). The scene itself
    /// is left to the host's teardown. Idempotent; without it nothing ever disposed the root effect, so a closed host
    /// stayed reachable from every process-lifetime signal its components subscribed to.</summary>
    public void UnmountRoot()
    {
        var effect = _rootEffect;
        _rootEffect = null;
        if (effect is null && _root is null) return;
        effect?.Dispose();
        if (_root is { } root) { _root = null; root.Unmount(); }
        var sceneRoot = _scene.Root;
        if (!sceneRoot.IsNull && _scene.IsLive(sceneRoot)) UnmountSubtree(sceneRoot);
        _oldRoot = null;
    }

    private void RunRoot(Component root)
    {
        _renderCount++;
        bool census = _renderCensus is not null;
        long t0 = 0, b0 = 0;
        if (census) { t0 = Stopwatch.GetTimestamp(); b0 = GC.GetAllocatedBytesForCurrentThread(); }
        bool prevShape = _layoutShapeMutated;
        _layoutShapeMutated = false;
        Element newRoot = root.RenderWithHooks();
        long t1 = census ? Stopwatch.GetTimestamp() : 0;
        RenderRootDiff(newRoot);
        if (census) NoteRenderCensus(root, t1 - t0, Stopwatch.GetTimestamp() - t1, GC.GetAllocatedBytesForCurrentThread() - b0);
        // Same gate as RunComponent: mark the root only when reconcile mutated layout shape.
        if (_layoutShapeMutated && !_scene.Root.IsNull)
            _scene.Mark(_scene.Root, NodeFlags.LayoutDirty);
        _layoutShapeMutated = prevShape;   // nested Mount sets the bit on the outer via ReconcileSingleChild after Mount
    }

    private void RenderRootDiff(Element newRoot)
    {
        if (_scene.Root.IsNull || _oldRoot is null || _oldRoot.ElementTypeId != newRoot.ElementTypeId)
        {
            if (!_scene.Root.IsNull) Remove(_scene.Root);
            var node = _scene.CreateNode(newRoot.ElementTypeId);
            _scene.Root = node;
            Mount(node, newRoot);
            // A remount is the one structural path with nothing below it to mark: CreateNode does not set LayoutDirty
            // and Mount's WriteColumns takes the isMount arm. Without this a post-first-layout root swap left the
            // worklist empty and the host's scoped RunDirty skipped the new tree entirely.
            MarkLayoutShape(node);
        }
        else
        {
            Update(_scene.Root, newRoot, _oldRoot);
            // LayoutDirty is marked by WriteColumns (layout-input / grid / clip-flag change) and by structural
            // reconcile paths — an identical-shape root re-render must not force a full solve.
        }
        _oldRoot = newRoot;
        _root?.Context.SetHostNode(_scene.Root);
    }

    /// <summary>Imperative full reconcile of an explicit element tree (tests / non-host callers).</summary>
    public void ReconcileRoot(Element newRoot, Element? oldRoot)
    {
        _oldRoot = oldRoot;
        RenderRootDiff(newRoot);
    }

    /// <summary>Re-realize any virtual-list windows flagged <see cref="NodeFlags.VirtualRangeDirty"/> (the host's scroll
    /// step found the plan's present-time window outside the realized one) — granular, no component re-render. Every
    /// covered row realizes in this call (scroll rework §6: no ramp, no budget, no deadline).</summary>
    public bool ReRealizeVirtuals()
    {
        var dirty = _scene.VirtualRangeDirtyNodes;   // E6: the scene-owned queue — NO _virtuals dictionary scan
        if (_scanFrameEpoch != FrameEpoch) { _scanFrameEpoch = FrameEpoch; LastReRealizeScan = 0; LastReRealizeRealized = 0; }
        LastReRealizeScan += dirty.Count;
        _realizeProgress = false;
        // Reverse-iterate so a swap-remove (moving the tail entry into the freed slot) never skips an unprocessed entry.
        for (int i = dirty.Count - 1; i >= 0; i--)
        {
            var node = dirty[i];
            bool alive = _scene.IsLive(node)
                         && (_scene.Flags(node) & NodeFlags.VirtualRangeDirty) != 0
                         && _virtuals.TryGetValue(node, out var e) && e.El is not null;
            if (!alive) { SwapRemoveDirty(dirty, i); continue; }
            _virtuals.TryGetValue(node, out var entry);
            RealizeWindow(node, entry!.El!, reuseOverlap: true);
            LastReRealizeRealized++;
            // Fully realized (flag cleared) ⇒ drop from the queue; still flagged (budget/warm deficit) ⇒ leave it queued.
            if ((_scene.Flags(node) & NodeFlags.VirtualRangeDirty) == 0) SwapRemoveDirty(dirty, i);
        }
        // "Made realize progress": the realized window of at least one viewport changed OR a bound slot was rebound.
        // The latter can happen while ScrollState already publishes the target range; it still has to return true so the
        // host performs the same-frame reactive flush for the rewritten index signals. A queue left dirty PURELY by
        // budget exhaustion (visible already covered, window unchanged, no rebind) returns false, so the AppHost 2-pass
        // loops don't burn a pass re-checking it.
        return _realizeProgress;

        static void SwapRemoveDirty(List<NodeHandle> list, int i)
        {
            int last = list.Count - 1;
            list[i] = list[last];
            list.RemoveAt(last);
        }
    }

    // The seams every component's context is handed. A method-group conversion allocates a delegate each time it is evaluated,
    // so converting them per mount cost five delegates (~320 bytes) per component; they are built once per reconciler.
    private Action<NodeHandle, IReadOnlyList<int>, EnterExit, MotionTokenId, float, Action>? _beginVirtualRemovalSeam;
    private Func<NodeHandle, int, int, bool, bool>? _beginVirtualDisclosureSeam;
    private Action<NodeHandle, bool>? _completeVirtualDisclosureSeam;
    private Action<NodeHandle>? _clearVirtualDisclosureSeam;
    private Func<NodeHandle, object, Signal<object?>?>? _resolveContextSeam;

    private void InjectContext(RenderContext ctx, NodeHandle anchor)
    {
        ctx.Runtime = Runtime;
        ctx.Anim = Anim;
        ctx.Images = Images;
        ctx.Scene = _scene;
        ctx.RequestFrame = RequestFrame;
        ctx.BeginVirtualRemoval = _beginVirtualRemovalSeam ??= BeginVirtualRemoval;
        ctx.BeginVirtualDisclosure = _beginVirtualDisclosureSeam ??= BeginVirtualDisclosure;
        ctx.CompleteVirtualDisclosure = _completeVirtualDisclosureSeam ??= CompleteVirtualDisclosure;
        ctx.ClearVirtualDisclosure = _clearVirtualDisclosureSeam ??= ClearVirtualDisclosure;
        ctx.AnchorNode = anchor;
        ctx.ResolveContextSignal = _resolveContextSeam ??= ResolveContext;
        ctx.RegisterPendingEffectContext = RegisterPendingEffectContext;
    }

    /// <summary>DEBUG diagnostic helper (the relayout-escape message): a best-effort human key for a node — the
    /// KeepAlive slot key or the MorphId a follower FLIPs against, if this node happens to be one. Null otherwise. This is
    /// NOT a general per-node key store (the reconciler keys transiently during the keyed diff); it just surfaces the
    /// boundary-worthy anchors (page/keepalive hosts) that a "relayout escaped to root" message most wants to name.</summary>
    internal string? DebugKeyOf(NodeHandle n)
    {
        int idx = (int)n.Raw.Index;
        if (_keepAliveRootKey.TryGetValue(idx, out var k)) return k;
        if (_relativeKey.TryGetValue(idx, out var rk)) return rk;
        return null;
    }

    private Signal<object?>? ResolveContext(NodeHandle anchor, object channel)
    {
        for (var n = anchor.IsNull ? NodeHandle.Null : _scene.Parent(anchor); !n.IsNull; n = _scene.Parent(n))
            if (_providerSig.TryGetValue((int)n.Raw.Index, out var e) && ReferenceEquals(e.Channel, channel))
                return e.Sig;
        return _ambient.TryGetValue(channel, out var asig) ? asig : null;
    }

    // ── Mount ─────────────────────────────────────────────────────────────────────────────────────

    private void Mount(NodeHandle node, Element el)
    {
        if (_renderCensus is not null) _censusMounts++;
        MountedNodes++;   // always-on: the cold-realize ramp's node budget is a delta across one grow (see MountedNodes)
        _reconciled = true;
        // Evidence names (evidence-diagnostics §A.5): a keyed node's key, for logs and exports (UI thread; mounts allocate).
        if (el.Key is { Length: > 0 } debugKey) _scene.NoteDebugKey(node, debugKey);
        // Mount-under-parked-ancestor, for EVERY node kind (not only components). A reactive boundary inside a parked
        // KeepAlive page still settles while the page is detached — a SkelRegion swapping shimmer→real, a Show/For
        // flipping, a virtual row realizing — and mounts a fresh subtree. SceneStore.CreateNode zeroes a new node's
        // flags, so without this the plain wrapper node mounted here would lose the Parked marker and a ComponentEl one
        // level below would read parked=false from its parent, skip RunComponent's defer, and render its first frame
        // inside a subtree with no path to the scene root: ResolveContext walks up to the detached page root, finds no
        // provider, and UseRequiredContext throws (plain UseContext silently returns the default and never subscribes —
        // the same bug, silent). Marking here is what makes MountComponent's read — and AnimScheduler's per-row parked
        // seed (it reads this same flag when a track is created) — see the truth. Every mount path parents the node
        // BEFORE calling Mount (the single-child slots, the keyed diff, the bound/virtual realizers, MountProvider/
        // MountScroll); only the scene root has no parent, and the null guard covers it. Un-parking clears it: the
        // SetSubtreeParked(parked:false) walk descends the whole retained subtree, so nodes marked here are unmarked
        // and their components' deferred renders replayed with the rest of the page.
        var mountParent = _scene.Parent(node);
        if (!mountParent.IsNull && (_scene.Flags(mountParent) & NodeFlags.Parked) != 0)
            _scene.Mark(node, NodeFlags.Parked);
        if (el is ComponentEl ce) { MountComponent(node, ce); return; }
        if (el is ContextProviderEl cp) { MountProvider(node, cp); return; }
        if (el is ScrollEl se) { MountScroll(node, se); return; }
        if (el is VirtualListEl ve) { MountVirtual(node, ve); return; }
        if (el is ShowEl sh) { MountShow(node, sh); return; }
        if (el is ForElBase fe) { MountFor(node, fe); return; }
        if (el is KeepAliveEl ka) { MountKeepAlive(node, ka); return; }
        if (el is SkelRegionEl skr) { MountSkeletonRegion(node, skr); return; }

        WriteColumns(node, el, isMount: true);
        BindNode(node, el);
        foreach (var childEl in ChildrenOf(el))
        {
            var child = _scene.CreateNode(childEl.ElementTypeId);
            _scene.AppendChild(node, child);
            Mount(child, childEl);
        }
    }

    /// <summary>The positional children of a container element (box or grid); empty for leaves.</summary>
    private static Element[] ChildrenOf(Element? el) => el switch
    {
        BoxEl b => b.Children,
        GridEl g => g.Children,
        _ => [],
    };

    /// <summary>Mark a node <see cref="NodeFlags.LayoutDirty"/> AND raise <c>_layoutShapeMutated</c>. Every
    /// reconcile-time layout mark goes through here: the local mark alone can be firewalled below a ContentSized
    /// scroll viewport, so the enclosing RunComponent/RunRoot must ALSO start a dirty walk at its rendered root.
    /// <para>Deliberately NOT used by the bound Width/Height/Text effects: those fire outside a render scope and must not
    /// raise <c>_layoutShapeMutated</c>; they mark their own node plus its parent (<see cref="MarkParentLayoutDirty"/>),
    /// never a render-scope walk. (A bound→bound RE-WIRE re-runs them INSIDE a render scope;
    /// <see cref="RewireBinds"/> raises <c>_layoutShapeMutated</c> for such a re-run that wrote, matching this.)</para></summary>
    private void MarkLayoutShape(NodeHandle node)
    {
        _scene.Mark(node, NodeFlags.LayoutDirty);
        _layoutShapeMutated = true;
    }

    /// <summary>The node's OWN parent-facing inputs changed (explicit Width/Height, Margin, Min/Max, flex, AlignSelf):
    /// its outer box is decided by its PARENT's solve, so the parent must re-solve too. The node's own mark is not enough
    /// when the node is itself a layout boundary (a fixed-size clipped box, <c>.Boundary()</c>, a filling viewport):
    /// LayoutInvalidator stops the walk AT it and RunSubtree re-solves it at the new size in its OLD slot, so siblings
    /// never reflow. Same rule <see cref="SceneStore.SetCollapsed"/> follows for a presence flip.</summary>
    private void MarkParentLayoutDirty(NodeHandle node)
    {
        var parent = _scene.Parent(node);
        if (!parent.IsNull && _scene.IsLive(parent)) _scene.Mark(parent, NodeFlags.LayoutDirty);
    }

    // ── Update ────────────────────────────────────────────────────────────────────────────────────

    private void Update(NodeHandle node, Element newEl, Element oldEl)
    {
        if (ReferenceEquals(newEl, oldEl)) return;
        if (_renderCensus is not null) _censusUpdates++;

        if (newEl is ComponentEl nce)
        {
            // Reuse → the component is AUTONOMOUS: it re-renders via its own effect on its own state/context. A parent
            // re-render does NOT re-render it (props are carried by signals/context, not the factory closure). Type
            // change → replace. EXCEPTION: a flip of DeriveRenderedOutput is the skeleton shimmer↔real edge — the SAME
            // component type sits on both sides (a DeriveRenderedOutput proxy during Pending, the real component on
            // Ready; e.g. Responsive's ResponsiveBox). Reusing across it strands the instance in shimmer-deriving mode
            // AND keeps its stale Pending build closure (which closed over the seed), so the section never resolves —
            // the "page only half-resolves" bug. Force a fresh mount so the real build + cleared derive flag take hold.
            if (oldEl is ComponentEl oce && oce.ComponentType == nce.ComponentType && _comps.ContainsKey(node)
                && oce.DeriveRenderedOutput == nce.DeriveRenderedOutput)
            {
                var entry = _comps[node];
                // E14: the reuse path never reached WriteColumns for this anchor either — re-apply its base-Element
                // props (a re-render can change .Sticky's scope name, flip Visible, retarget Enter/Exit, …) exactly as
                // every other element type does on its own Update path.
                WriteAnchorColumns(node, nce, oce);
                if (nce.Props is { } p)
                {
                    // Re-pushed live props — THE core delivery. This runs during the parent's render-effect (inside the
                    // flush) or a ReconcileRoot: the write marks the child's render-effect stale and it drains in the
                    // SAME flush (the flush while-loop coalesces — no next-frame defer, no torn intermediate paint).
                    if (entry.Comp is IPropsHost host)
                    {
                        // The [Props]-generator seam (a later phase): a single sink that writes many field signals — wrap
                        // in Batch so dependent effects never observe a torn mid-diff state (only the outermost exit flushes).
                        Runtime.Batch(() => host.ApplyProps(p));
                    }
                    else if (entry.PropsSig is { } ps)
                    {
                        // Reference short-circuit FIRST (adjustment #4): a parent that re-rendered without rebuilding the
                        // props (a memoized/cached record) hands back the SAME reference — skip the record-Equals walk and
                        // the write entirely (O(1), no re-render). Only a genuinely new object reaches the equality-gated
                        // write, which itself coalesces a fresh-but-equal record via the Signal comparer (record value
                        // equality). Batch keeps the delivery on the same wrapped path the IPropsHost multi-write uses.
                        if (!ReferenceEquals(ps.Peek(), p))
                            Runtime.Batch(() => ps.Value = p);
                    }
                    else if (ReuseGuard.CompiledIn && ReuseGuard.Enabled)
                    {
                        // DEBUG diagnostic: props were re-pushed to a component mounted WITHOUT a props channel (no
                        // PropsSig, not an IPropsHost) — the value is silently dropped. Mount it via Embed.Comp(props, …).
                        ReuseGuard.Violation(entry.Comp, "Props",
                            "this component was mounted without props (Embed.Comp(factory)); re-pushed Props are dropped — mount it via Embed.Comp(props, factory)");
                    }
                }
                // Frozen-props tripwire (DEBUG): only for a PROPLESS reuse — a component receiving data through the props
                // channel above is no longer frozen, so it is exempt from this probe. The CompiledIn const folds the whole
                // block away in release — zero cost, no probe allocation on the hot path.
                else if (ReuseGuard.CompiledIn && ReuseGuard.Enabled)
                {
                    var liveComp = entry.Comp;
                    if (liveComp.ChecksReuse) liveComp.DebugCheckReuse(nce.Factory());
                }
                return;
            }
            ReplaceComponent(node, nce);
            return;
        }

        if (newEl is ScrollEl nse)
        {
            WriteColumns(node, nse, isMount: false);
            RewireBinds(node, nse);   // a re-render that binds Visible with a new thunk/signal re-points the mount bind
            var oldContent = (oldEl as ScrollEl)?.Content;
            var content = _scene.FirstChild(node);
            if (content.IsNull)
            {
                content = _scene.CreateNode(nse.Content.ElementTypeId);
                _scene.AppendChild(node, content);
                Mount(content, nse.Content);
                MarkLayoutShape(node);
            }
            else if (oldContent is not null && oldContent.ElementTypeId == nse.Content.ElementTypeId)
            {
                Update(content, nse.Content, oldContent);
            }
            else
            {
                Remove(content);
                content = _scene.CreateNode(nse.Content.ElementTypeId);
                _scene.AppendChild(node, content);
                Mount(content, nse.Content);
                MarkLayoutShape(node);
            }
            _scene.ScrollRef(node).ContentNode = content;
            return;
        }

        if (newEl is VirtualListEl nve)
        {
            WriteColumns(node, nve, isMount: false);
            RewireBinds(node, nve);
            RealizeWindow(node, nve);
            return;
        }

        if (newEl is ShowEl nsh)
        {
            // Parent re-renders replace the stored ShowEl and reschedule the boundary effect (mirrors
            // UpdateSkeletonRegion), so the new Then/Else children — and the new When thunk's deps — take hold.
            WriteAnchorColumns(node, nsh, oldEl);
            UpdateShow(node, nsh);
            return;
        }

        if (newEl is ForElBase nfe)
        {
            // Parent re-renders replace the stored ForElBase and reschedule the boundary effect (mirrors UpdateShow),
            // so the fresh Items/KeyOf/Row closures take hold instead of freezing at first mount (the Show-parity fix —
            // ForEl.Update used to be a no-op, which froze rows built from parent render state).
            WriteAnchorColumns(node, nfe, oldEl);
            UpdateFor(node, nfe);
            return;
        }

        if (newEl is KeepAliveEl nka)
        {
            WriteAnchorColumns(node, nka, oldEl);   // the boundary's own base-Element props; its pages stay autonomous
            return;
        }

        if (newEl is SkelRegionEl nskr)
        {
            WriteAnchorColumns(node, nskr, oldEl);
            ApplySkeletonSmoothResize(node, nskr);
            UpdateSkeletonRegion(node, nskr);
            return;
        }

        if (newEl is ContextProviderEl np)
        {
            WriteAnchorColumns(node, np, oldEl);
            int idx = (int)node.Raw.Index;
            if (_providerSig.TryGetValue(idx, out var e) && ReferenceEquals(e.Channel, np.Channel))
                e.Sig.Value = np.Value;                                  // notify consumers iff changed
            else
                _providerSig[idx] = (np.Channel, new Signal<object?>(np.Value));

            var oldChild = (oldEl as ContextProviderEl)?.Child;
            ReconcileSingleChild(node, np.Child, oldChild);
            MirrorParticipation(node, _scene.FirstChild(node));
            return;
        }

        // GEN-01 DiffProps fast-path (WIRED): skip the redundant column rewrite when every diffable prop — incl. the
        // inherited Element animation/declarative fields — is identical to last render. The generated AnyChanged covers
        // the WHOLE prop set, so any change to Fill/Layout/Animate/Transition/WhileHover/… forces the full WriteColumns
        // (keeping the BoundsAnimated/FLIP/reflow side-effects correct). Children are EXCLUDED from the diff, so they
        // are ALWAYS reconciled. The FLIP "First" capture runs in the host commit loop over the BoundsAnimated flag
        // (AppHost), independent of this call, so a truly-unchanged node still rides a sibling reflow.
        // DEBUG-only bind-contract tripwire: a bindable channel that flipped between static and bound on this reused
        // node silently loses (a bind is wired at mount and only ever RE-WIRED bound→bound — RewireBinds below never
        // wires a newly-bound channel nor unwires a newly-static one). The CompiledIn const folds this away in release.
        if (BindContract.CompiledIn && BindContract.Enabled && BindFlip(newEl, oldEl) is { } flipped)
            BindContract.Flip(newEl.GetType().Name, flipped);

        if (RecordChanged(newEl, oldEl))
        {
            if (_renderCensus is not null) _censusWrites++;
            WriteColumns(node, newEl, isMount: false, oldEl);
            // Bound→bound re-wire (Reconciler.Rewire.cs): a bound channel whose thunk/signal payload changed (a fresh
            // lambda capturing new render-time values) now evaluates the NEW source. Inside the RecordChanged gate on
            // purpose — AnyChanged compares every Prop<T> payload with the same equality, so an unchanged element
            // can never need a re-wire. After WriteColumns, mirroring Mount's WriteColumns-then-BindNode order.
            RewireBinds(node, newEl);
        }
        ReconcileChildren(node, ChildrenOf(newEl), ChildrenOf(oldEl));
    }

    /// <summary>DEBUG-only (<see cref="BindContract"/>): the name of the first bindable channel whose bound/static shape
    /// flipped between the same-type <paramref name="a"/>/<paramref name="b"/> element versions (the generated
    /// <c>{T}Diff.FirstBoundFlip</c>), or null. A type mismatch is a replace (handled elsewhere), never a flip.</summary>
    private static string? BindFlip(Element a, Element b) => a switch
    {
        BoxEl x => b is BoxEl y ? BoxElDiff.FirstBoundFlip(x, y) : null,
        TextEl x => b is TextEl y ? TextElDiff.FirstBoundFlip(x, y) : null,
        GridEl x => b is GridEl y ? GridElDiff.FirstBoundFlip(x, y) : null,
        ImageEl x => b is ImageEl y ? ImageElDiff.FirstBoundFlip(x, y) : null,
        IconLayerEl x => b is IconLayerEl y ? IconLayerElDiff.FirstBoundFlip(x, y) : null,
        SpanTextEl x => b is SpanTextEl y ? SpanTextElDiff.FirstBoundFlip(x, y) : null,
        ListRowEl x => b is ListRowEl y ? ListRowElDiff.FirstBoundFlip(x, y) : null,
        PolylineStrokeEl x => b is PolylineStrokeEl y ? PolylineStrokeElDiff.FirstBoundFlip(x, y) : null,
        PathEl x => b is PathEl y ? PathElDiff.FirstBoundFlip(x, y) : null,
        SeriesEl x => b is SeriesEl y ? SeriesElDiff.FirstBoundFlip(x, y) : null,
        SpriteFieldEl x => b is SpriteFieldEl y ? SpriteFieldElDiff.FirstBoundFlip(x, y) : null,
        _ => null,
    };

    /// <summary>GEN-01 (wired): true unless <paramref name="a"/> and <paramref name="b"/> are the same leaf element
    /// type with EVERY diffable prop unchanged (the generated <c>{T}Diff.AnyChanged</c> — inherited fields included,
    /// Children excluded). A different type / unlisted kind conservatively returns true (always re-write).</summary>
    private static bool RecordChanged(Element a, Element b) => a switch
    {
        BoxEl x => b is not BoxEl y || BoxElDiff.AnyChanged(x, y),
        TextEl x => b is not TextEl y || TextElDiff.AnyChanged(x, y),
        GridEl x => b is not GridEl y || GridElDiff.AnyChanged(x, y),
        ImageEl x => b is not ImageEl y || ImageElDiff.AnyChanged(x, y),
        IconLayerEl x => b is not IconLayerEl y || IconLayerElDiff.AnyChanged(x, y),
        SpanTextEl x => b is not SpanTextEl y || SpanTextElDiff.AnyChanged(x, y),
        ListRowEl x => b is not ListRowEl y || ListRowElDiff.AnyChanged(x, y),
        PolylineStrokeEl x => b is not PolylineStrokeEl y || PolylineStrokeElDiff.AnyChanged(x, y),
        PathEl x => b is not PathEl y || PathElDiff.AnyChanged(x, y),
        SeriesEl x => b is not SeriesEl y || SeriesElDiff.AnyChanged(x, y),
        SpriteFieldEl x => b is not SpriteFieldEl y || SpriteFieldElDiff.AnyChanged(x, y),
        _ => true,
    };

    /// <summary>Mount/update/replace a single optional child under <paramref name="parent"/> (component output, provider, Show).
    /// <para>CONTRACT: this slot pairs old↔new by <see cref="Element.ElementTypeId"/> ONLY — <see cref="Element.Key"/> is
    /// honored exclusively by <see cref="ReconcileChildren"/>. A key on a component's ROOT element, a provider body, or a
    /// Show body is therefore INERT: same type ⇒ update in place, whatever the key says. Honoring it here would turn a
    /// key change into a remount for every such site in the tree (an audited-unsafe blast radius: keyed roots whose key
    /// varies with a measured width or an expansion target exist today and rely on being updated), so the semantic stays
    /// as-is and the report below (UNCONDITIONAL — see <see cref="ReuseGuard.KeyIgnoredInSingleChildSlot"/>) makes the
    /// dropped request loud instead of silent, in every build a user actually runs.</para></summary>
    private void ReconcileSingleChild(NodeHandle parent, Element? newChild, Element? oldChild)
    {
        var child = _scene.FirstChild(parent);
        if (newChild is null)
        {
            if (!child.IsNull) { Remove(child); MarkLayoutShape(parent); }
            return;
        }
        if (child.IsNull)
        {
            var c = _scene.CreateNode(newChild.ElementTypeId);
            _scene.AppendChild(parent, c);
            Mount(c, newChild);
            MarkLayoutShape(parent);
        }
        else if (oldChild is not null && oldChild.ElementTypeId == newChild.ElementTypeId)
        {
            // ALWAYS ON (unlike every other ReuseGuard report — this one is not DEBUG/FLUENTGPU_DIAG-gated): the author
            // asked for a REMOUNT via a changed key and this slot cannot deliver one. Same-type + both keys present +
            // different ⇒ the intent is unambiguous, so this must never be silent again — it is how a chart froze at its
            // seed count, and a Release build (where ReuseGuard.CompiledIn is false) had NO way to see it happen. Cost on
            // the hot path: two reference reads (Element.Key), and only when both are non-empty, one ordinal compare;
            // ReuseGuard.KeyIgnoredInSingleChildSlot owns the per-site dedupe that keeps a legitimately-varying-every-frame
            // key (a measured width baked into a root key) a single log line instead of one per frame.
            if (newChild.Key is { Length: > 0 } nk && oldChild.Key is { Length: > 0 } ok && nk != ok)
                ReportKeyIgnoredInSingleChildSlot(newChild, oldChild, ok, nk);
            Update(child, newChild, oldChild);
        }
        else
        {
            Remove(child);
            var c = _scene.CreateNode(newChild.ElementTypeId);
            _scene.AppendChild(parent, c);
            Mount(c, newChild);
            MarkLayoutShape(parent);
        }
    }

    /// <summary>The <see cref="ReconcileSingleChild"/> tripwire's report step, split out so the hot slot keeps only the
    /// const-folded guard. Two things the raw call site got wrong: every <c>ComponentEl</c> shares
    /// <c>ElementTypeId</c> 3, so a ComponentEl pair whose <c>ComponentType</c> actually DIFFERS is replaced by
    /// <see cref="Update"/> anyway — the key request IS honored there, and reporting it would be a false positive; and
    /// "ComponentEl" names nothing an author can act on, so the report carries the component type instead.</summary>
    private static void ReportKeyIgnoredInSingleChildSlot(Element newChild, Element oldChild, string oldKey, string newKey)
    {
        if (newChild is ComponentEl nce)
        {
            // The same two conditions Update reuses on (type identity + the skeleton↔real DeriveRenderedOutput edge).
            if (oldChild is not ComponentEl oce || oce.ComponentType != nce.ComponentType
                || oce.DeriveRenderedOutput != nce.DeriveRenderedOutput) return;   // replaced ⇒ the key WAS honored
            ReuseGuard.KeyIgnoredInSingleChildSlot(nce.ComponentType.Name, oldKey, newKey);
            return;
        }
        ReuseGuard.KeyIgnoredInSingleChildSlot(newChild.GetType().Name, oldKey, newKey);
    }

    private void ReplaceSingleChild(NodeHandle parent, Element? newChild)
    {
        var child = _scene.FirstChild(parent);
        if (!child.IsNull) Remove(child);
        // A removal alone changes the parent's shape, and the mount path below is skipped — mark before the early-out.
        if (newChild is null) { if (!child.IsNull) MarkLayoutShape(parent); return; }

        var c = _scene.CreateNode(newChild.ElementTypeId);
        _scene.AppendChild(parent, c);
        Mount(c, newChild);
        MarkLayoutShape(parent);
    }

    // ── Components (render-effects) ──────────────────────────────────────────────────────────────

    private void MountComponent(NodeHandle node, ComponentEl ce)
    {
        var comp = ce.Factory();
        InjectContext(comp.Context, node);
        comp.Context.Owner = comp;   // diagnostic identity for the hook computations this context will create (Computation.DiagOwner)
        // Mount-under-parked-ancestor: a component can be mounted into an already-parked subtree (a reactive Show/For
        // boundary inside a parked page still fires its effect). It must initialize INACTIVE — seed Parked from the
        // marker and mark this node too, so the deferred-render gate holds and descendants inherit it. Read THIS node's
        // flag first (Mount marked it on the way in — the every-kind inheritance above) and fall back to the parent's,
        // which is what the ReplaceComponent path (a shimmer↔real remount, which calls straight in here without going
        // through Mount) has to rely on.
        var parent = _scene.Parent(node);
        bool parked = (_scene.Flags(node) & NodeFlags.Parked) != 0
                      || (!parent.IsNull && (_scene.Flags(parent) & NodeFlags.Parked) != 0);
        // P1 presence: unlike Parked, a collapsed ancestor's NodeFlags.Visible is NOT propagated down to fresh
        // children at mount (the recorder/hit-test walks already early-return the instant THEY hit the collapsed
        // node — see SceneRecorder.Walk / InputDispatcher.ConsiderContainingScrollers — so descendant bits are
        // never consulted for paint/hit-test). But a component's render-effect is reconciled independently of that
        // walk, so a fresh mount under an already-collapsed ancestor (a realized row inside a collapsed section)
        // must still seed Hidden — walk up (mount-only cost, O(depth)) rather than trust one parent's bit.
        bool hidden = false;
        for (var a = node; !a.IsNull; a = _scene.Parent(a))
            if ((_scene.Flags(a) & NodeFlags.Visible) == 0) { hidden = true; break; }
        var entry = new CompEntry
        {
            Comp = comp,
            Type = ce.ComponentType,
            Parked = parked,
            Hidden = hidden,
            DerivedSkeletonStyle = ce.DerivedSkeletonStyle,
        };
        if (parked) _scene.Mark(node, NodeFlags.Parked);
        _comps[node] = entry;
        _anchorOf[comp] = node;
        _live.Add(comp);

        // The component's per-instance activation signal (UseIsActive), created lazily on first read so a component that
        // never uses the lifecycle allocates nothing. Initial value = its current attached state (inactive if
        // parked OR presence-hidden — SetSubtreeHidden/Reconciler.Presence.cs folds the same formula on a live flip).
        comp.Context.GetActiveSig = () => entry.ActiveSig ??= new Signal<bool>(!entry.Parked && !entry.Hidden);

        // Re-pushed props channel — seeded BEFORE the first render-effect run so the very first Render sees the props.
        // An IPropsHost ([Props]-generated) component receives them through its typed sink; otherwise we back them with a
        // per-instance PropsSig (default comparer ⇒ record value equality) that UseProps<T> reads. The signal needs no
        // explicit disposal: its only subscriber is the scope-owned render-effect, which unlinks from it when the scope
        // disposes at unmount (ReactiveCore.UnlinkSources); the signal itself is then unreferenced and collected.
        if (ce.Props is { } props)
        {
            if (comp is IPropsHost host) host.ApplyProps(props);
            else { entry.PropsSig = new Signal<object?>(props); comp.Context.PropsSig = entry.PropsSig; }
        }

        // One lifetime scope per component (the never-re-running owner ReactiveCore was designed for). It OWNS the
        // render-effect (so Scope.Dispose cascades to it — no manual Effect.Dispose) and carries the hook-cleanup
        // teardown as a scope cleanup, so unmount collapses to Scope.Dispose(): dispose the render-effect, then run
        // RunAllCleanups — the same order the split (Effect.Dispose(); Comp.Unmount();) ran. Re-running the render-effect
        // is safe under this owner: it owns nothing itself (its nested binds/For/Show effects are created owner:null and
        // registered node-owned via AddBinding), so RunComputation's dispose-children pass on re-render is a no-op.
        var scope = new ReactiveScope(Runtime);
        entry.Scope = scope;
        scope.AddCleanup(comp.Unmount);   // RunAllCleanups runs exactly once, when the scope disposes
        // DiagOwner: a UseContext(FrameClock.Tick) poller IS this effect (the read happens while it is Tracking.Current),
        // so stamping it here is what lets the [wake] census name the component instead of counting an anonymous one.
        var effect = new Effect(Runtime, () => RunComponent(node, entry), owner: scope, runNow: false) { DiagOwner = comp };
        entry.Effect = effect;
        comp.Context.RequestRerender = effect.Schedule;   // imperative re-render (granular) for escape-hatch callers
        // E14: apply the anchor's own base-Element props (Sticky/Visible/Enter/…) BEFORE the first render — mirrors
        // Mount's WriteColumns-then-mount-children order for every other element kind.
        WriteAnchorColumns(node, ce, old: null);
        effect.RunNow();                                  // first render + child mount (deferred if mounted parked)
    }

    private void RunComponent(NodeHandle node, CompEntry entry)
    {
        if (!_scene.IsLive(node)) return;
        // Parked by Flow.KeepAlive (inactive tab/page), OR exit-frozen while a KeepAlive page is still attached and
        // animating out: skip the render — an exiting page must not rebuild against the incoming route (or any other
        // write) mid-fade. Parked pages are detached; exit-frozen pages stay attached so the exit track can paint.
        // Remember that a render was owed; un-park / un-freeze replays it once when the subtree comes back.
        if (entry.Parked || entry.ExitFrozen) { entry.DeferredRender = true; return; }
        // A real invalidation (a signal write reaching this component) beat the un-park drip to it — running now settles
        // the debt, so cancel the queue slot (the drain skips a cancelled entry without spending its budget).
        entry.QueuedReplay = false;
        _renderCount++;
        bool census = _renderCensus is not null;
        long t0 = 0, b0 = 0;
        if (census) { t0 = Stopwatch.GetTimestamp(); b0 = GC.GetAllocatedBytesForCurrentThread(); }
        if (Diag.Enabled) Diag.Event("render", entry.Comp.GetType().Name);   // who re-rendered (granularity diagnosis)
        var comp = entry.Comp;
        // What scheduled this render (the census's `by=`): read and CLEARED here so a later forced run (an un-park
        // replay, RunNow) reads null ("sched") instead of a stale cause. One reference read/write per render.
        ISignalSource? cause = null;
        byte causeTag = 0;
        if (entry.Effect is { } fx)
        {
            cause = fx.StaleCause;
            fx.StaleCause = null;
            causeTag = entry.Rendered is null ? (byte)1 : cause is not null && ReferenceEquals(cause, entry.PropsSig) ? (byte)2 : (byte)0;
        }
        Element newRendered = comp.RenderWithHooks();
        if (entry.DerivedSkeletonStyle is { } skeletonStyle)
            newRendered = SkeletonDeriver.Derive(newRendered, skeletonStyle);
        long t1 = census ? Stopwatch.GetTimestamp() : 0;
        bool prevShape = _layoutShapeMutated;
        _layoutShapeMutated = false;
        ReconcileSingleChild(node, newRendered, entry.Rendered);
        // Nested component renders (children mounted/re-rendered inside this reconcile) report their own rows; this
        // row's reconcile time therefore INCLUDES them — read the per-type numbers as inclusive of the subtree.
        if (census) NoteRenderCensus(comp, t1 - t0, Stopwatch.GetTimestamp() - t1, GC.GetAllocatedBytesForCurrentThread() - b0, cause, causeTag);
        MirrorParticipation(node, _scene.FirstChild(node));
        RemirrorAncestors(node);   // an enclosing boundary that did not re-render still mirrors this anchor
        comp.Context.SetHostNode(_scene.FirstChild(node));
        entry.Rendered = newRendered;
        // Scoped relayout: mark the rendered root only when reconcile mutated structure or a layout-affecting
        // column. Starting the dirty walk HERE (not only at the deep mutation site) clears ContentSized scroll
        // firewalls so a TabView equal-width / add-button strip still reflows. Paint-only re-renders leave the bit
        // clear → no LayoutDirty → no page-scale flex solve.
        if (_layoutShapeMutated)
        {
            var child = _scene.FirstChild(node);
            if (!child.IsNull) _scene.Mark(child, NodeFlags.LayoutDirty);
        }
        _layoutShapeMutated = prevShape;   // nested Mount sets the bit on the outer via ReconcileSingleChild after Mount
    }

    private void MountProvider(NodeHandle node, ContextProviderEl cp)
    {
        _providerSig[(int)node.Raw.Index] = (cp.Channel, new Signal<object?>(cp.Value));
        WriteAnchorColumns(node, cp, old: null);   // base-Element props on the boundary itself, before its child mounts
        var child = _scene.CreateNode(cp.Child.ElementTypeId);
        _scene.AppendChild(node, child);
        Mount(child, cp.Child);
        MirrorParticipation(node, child);   // a provider is layout-transparent
    }

    /// <summary>
    /// A component anchor is layout-transparent: it must participate in its parent's flex/grid exactly as its rendered
    /// child would. We mirror the child's sizing/participation onto the anchor each (re)render. (layout.md §2.2.)
    /// </summary>
    private void MirrorParticipation(NodeHandle anchor, NodeHandle child)
    {
        // Transparent boundaries are NEVER input targets of their own. Keep the anchor traversable and make it yield
        // when none of its rendered descendants hit: a child with HitTestVisible=false is then skipped naturally and
        // input reaches the sibling behind it (closed retained rail), while a later live child is immediately reachable
        // without requiring visibility to propagate synchronously back through every nested component/Skel/KeepAlive
        // boundary. Copying the child's bit here was racy: DetailPage → SkelRegion → DetailShell could temporarily copy
        // false during a branch swap and leave an outer component anchor permanently blocking descent even after the
        // inner branch became hit-testable. Child hits still win because pass-through is consulted only after descent.
        //
        // Set BEFORE the empty-boundary return, because an EMPTY boundary is the case that bites hardest. A `Show`
        // whose branch is false (and has no `else`) keeps a live anchor with NO child and therefore nothing to mirror —
        // and a bare anchor is not inert: CreateNode gives every node HitTestVisible, and an auto-sized child of a
        // ZStack is STRETCHED to the whole slot (ArrangeZStack: NaN width/height ⇒ fill), so the anchor becomes a
        // full-bleed hittable node above everything below it in the stack. `Hit` (the interaction-gated walk) still
        // falls through it because it carries no handler, but `HitTestAny` — the handler-less walk that resolves wheel
        // targets, drop targets and middle-click — returns it for EVERY point, and the wheel then finds no Scrollable
        // ancestor on its chain. Symptom: clicks keep working while scrolling silently dies everywhere under the stack.
        _scene.Mark(anchor, NodeFlags.HitTestVisible);
        _scene.SetHitTestPassThrough(anchor, anchor);
        // Presence is participation too: a rendered root that is out of flow (Visible=false, or itself a transparent
        // boundary over one) takes the anchor out of flow with it — no gap slot, no mirrored Width/Height/Grow left
        // reserving space. Layout-only (MirrorCollapsed), so the anchor's own Visible keeps owning its Hidden/timers.
        _mirroredChild[(int)anchor.Raw.Index] = child;
        _scene.SetMirrorCollapsedIfChanged(anchor, !child.IsNull && _scene.IsLayoutCollapsed(child));
        if (child.IsNull) return;
        ref LayoutInput a = ref _scene.Layout(anchor);
        ref LayoutInput c = ref _scene.Layout(child);
        a.FlexGrow = c.FlexGrow; a.FlexShrink = c.FlexShrink; a.FlexBasis = c.FlexBasis; a.AlignSelf = c.AlignSelf; a.JustifySelf = c.JustifySelf;
        // NEVER snapshot an ACTIVELY ANIMATED size. A SizeMode.Reflow track writes the eased extent straight into the
        // child's LayoutInput.Width/Height each tick; mirroring THAT onto the transparent anchor freezes a mid-flight
        // number as a hard declared size, and nothing ever undoes it — SettleRestore restores only the animated node,
        // and MountComponent never writes an anchor's LayoutInput. Mirror the DECLARED value the track will restore
        // (normally NaN/auto) so the anchor stays genuinely transparent and measures the eased child naturally.
        if (Anim is { } mpAnim)
        {
            float mirroredW = mpAnim.TryGetReflowDeclared(child, AnimChannel.LayoutW, out float mpW) ? mpW : c.Width;
            float mirroredH = mpAnim.TryGetReflowDeclared(child, AnimChannel.LayoutH, out float mpH) ? mpH : c.Height;
            // The SAME hazard one level up, with the roles swapped: the ANCHOR itself may be the reflow node. A
            // SkelRegionEl with SmoothResize puts the SizeMode.Reflow track on the BOUNDARY (MountSkeletonRegion marks
            // the region BoundsAnimated), and ReconcileSkeletonRegion then mirrors its branch onto it on every flush —
            // so writing a.Width/a.Height raw stomps the extent the anchor's own live row publishes each tick, and
            // leaves the row's RestoreTo holding a value the author never declared. Hand the mirrored declared value to
            // the track instead, exactly as WriteColumns does for an authored Width/Height: RecordDeclaredSize files it
            // as the row's RestoreTo (so the settle restores the CURRENT declared value, not a stale one) and re-aims
            // the row when a genuinely changed non-NaN value arrives mid-flight. Write LayoutInput only when no row
            // owns the channel; the probe is O(1) for the overwhelmingly common unanimated anchor.
            if (!mpAnim.RecordDeclaredSize(anchor, AnimChannel.LayoutW, mirroredW)) a.Width = mirroredW;
            if (!mpAnim.RecordDeclaredSize(anchor, AnimChannel.LayoutH, mirroredH)) a.Height = mirroredH;
        }
        else { a.Width = c.Width; a.Height = c.Height; }
        a.MinW = c.MinW; a.MinH = c.MinH; a.MaxW = c.MaxW; a.MaxH = c.MaxH;
        a.MeasureUnboundedWidth = c.MeasureUnboundedWidth;
    }

    /// <summary>Re-mirror every transparent anchor above <paramref name="node"/> whose mirrored child it is, bottom-up. A
    /// bound Visible/Width/Height fire, or a nested component's own re-render, changes the rendered root without
    /// re-rendering the boundary that mirrors it. Stops at the first parent that is not such an anchor (one probe).</summary>
    private void RemirrorAncestors(NodeHandle node)
    {
        while (true)
        {
            var parent = _scene.Parent(node);
            if (parent.IsNull || !_mirroredChild.TryGetValue((int)parent.Raw.Index, out var mirrored) || mirrored != node) return;
            MirrorParticipation(parent, node);
            node = parent;
        }
    }

    private void ReplaceComponent(NodeHandle node, ComponentEl ce)
    {
        var kids = new List<NodeHandle>();
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c)) kids.Add(c);
        foreach (var k in kids) Remove(k);
        if (_comps.Remove(node, out var old)) { old.QueuedReplay = false; old.Scope?.Dispose(); _live.Remove(old.Comp); _anchorOf.Remove(old.Comp); }
        // The anchor survives but its bound-Visible effect (node-owned, so the scope dispose above never reaches it) was
        // wired for the OLD embed: drop it as UnmountSubtree does. MountComponent's BindNode wires the new embed afresh;
        // a surviving old effect would keep driving the new component's presence and stack one more per swap.
        if (_nodeBindings.Remove((int)node.Raw.Index, out var binds)) for (int i = 0; i < binds.Count; i++) binds[i].Dispose();
        MountComponent(node, ce);
        // Remount (the shimmer↔real swap) — a whole child tree was torn down and rebuilt. Promote AFTER MountComponent:
        // the nested RunComponent it triggers restores the enclosing scope's saved value on exit, which would otherwise
        // erase a bit raised before the call.
        _layoutShapeMutated = true;
    }

    // ── Reactive control-flow (Show / For) ──────────────────────────────────────────────────────

    private void MountShow(NodeHandle node, ShowEl se)
    {
        // The boundary node is a layout-transparent container; an effect mounts/updates the active branch reactively.
        // The effect reads the LATEST stored ShowEl (not its mount-time capture): parent re-renders replace the stored
        // element and reschedule this effect (UpdateShow), like the skeleton boundary.
        int mountIdx = (int)node.Raw.Index;
        _showState[mountIdx] = null;
        _showEl[mountIdx] = se;
        WriteAnchorColumns(node, se, old: null);   // before the first branch mounts (mirrors MountComponent)
        var eff = new Effect(Runtime, () =>
        {
            if (!_scene.IsLive(node)) return;
            int idx = (int)node.Raw.Index;
            if (!_showEl.TryGetValue(idx, out var cur)) return;
            Element? desired = cur.When() ? cur.Then : cur.Else;
            _showState.TryGetValue(idx, out var last);
            ReconcileSingleChild(node, desired, last);
            _showState[idx] = desired;
            MirrorParticipation(node, _scene.FirstChild(node));
        }, owner: null, runNow: false);
        _showEffect[mountIdx] = eff;
        AddBinding(node, eff);
        eff.RunNow();
    }

    private void UpdateShow(NodeHandle node, ShowEl next)
    {
        int idx = (int)node.Raw.Index;
        _showEl[idx] = next;
        if (_showEffect.TryGetValue(idx, out var eff)) eff.Schedule();
    }

    // Native skeleton-loading boundary (modelled on MountShow): a reconcile effect reads the current loadable's state,
    // and the real branch reads its value so Ready-to-Ready refreshes reconcile in place. Parent re-renders replace the
    // stored SkelRegionEl and schedule this same effect, so dependency tracking follows the latest loadable.
    private void MountSkeletonRegion(NodeHandle node, SkelRegionEl se)
    {
        int mountIdx = (int)node.Raw.Index;
        _skelState[mountIdx] = (0, null, se.Group);
        _skelEl[mountIdx] = se;
        if (se.Group is { } grp) SkelGroupCoordinator.Register(grp, mountIdx);

        WriteAnchorColumns(node, se, old: null);
        ApplySkeletonSmoothResize(node, se);

        var eff = new Effect(Runtime, () =>
        {
            int idx = (int)node.Raw.Index;
            bool force = _skelForce.Remove(idx);
            ReconcileSkeletonRegion(node, force);
        }, owner: null, runNow: false);
        _skelEffect[mountIdx] = eff;
        AddBinding(node, eff);
        eff.RunNow();
    }

    /// <summary>Smooth-resize (see the block comment inside): an authored Enter/Exit/Layout on the region
    /// (<see cref="WriteAnchorColumns"/>) owns the node's transition and wins over it.</summary>
    private void ApplySkeletonSmoothResize(NodeHandle node, SkelRegionEl se)
    {
        // Smooth-resize: mark the region BoundsAnimated with a SizeMode.Reflow transition so a branch swap whose new
        // content has a DIFFERENT height eases the region's layout size — the host re-solves the parent boundary each
        // tick, so SURROUNDING content (the sibling below a failed/shorter section) reflows smoothly instead of snapping.
        // Skipped under reduced motion (the swap snaps). The FLIP deadband makes a same-height swap a no-op.
        if (se.SmoothResize && !Motion.ReducedMotion && Anim is { } sa && !sa.TryGetTransition(node, out _))
        {
            // HEIGHT ONLY. `LayoutTransition.Axes` defaults to SizeAxes.Both, but the region's smooth resize exists to
            // ease the BRANCH HEIGHT difference; its width is parent-owned (a page section fills its column) and easing
            // it turns every re-measure into a horizontal rubber-band. Axes gates the seed itself (AnimateBounds →
            // ReflowSize per axis), so the width channel is never created rather than created and ignored.
            sa.SetTransition(node, new LayoutTransition(
                TransitionChannels.Size,
                TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
                Size: SizeMode.Reflow,
                Axes: SizeAxes.Height));
            _scene.Mark(node, NodeFlags.BoundsAnimated);
        }
    }

    private void UpdateSkeletonRegion(NodeHandle node, SkelRegionEl next)
    {
        int idx = (int)node.Raw.Index;
        object? oldGroup = _skelState.TryGetValue(idx, out var st) ? st.Group : null;
        if (!Equals(oldGroup, next.Group))
        {
            if (oldGroup is not null) SkelGroupCoordinator.Unregister(oldGroup, idx);
            if (next.Group is not null) SkelGroupCoordinator.Register(next.Group, idx);
            if (_skelState.TryGetValue(idx, out st)) _skelState[idx] = (st.Branch, st.El, next.Group);
        }

        _skelEl[idx] = next;
        _skelForce.Add(idx);
        if (_skelEffect.TryGetValue(idx, out var eff)) eff.Schedule();
    }

    private void ReconcileSkeletonRegion(NodeHandle node, bool force)
    {
        if (!_scene.IsLive(node)) return;
        int idx = (int)node.Raw.Index;
        if (!_skelEl.TryGetValue(idx, out var se)) return;
        var (lastBranch, lastEl, _) = _skelState.TryGetValue(idx, out var st) ? st : ((byte)0, (Element?)null, se.Group);

        byte branch = se.Failed() ? (byte)3 : se.Pending() ? (byte)1 : (byte)2;   // 1 shimmer / 2 real / 3 failed
        bool sameBranch = branch == lastBranch;
        if (sameBranch && !force && branch != 2 && !(branch == 1 && se.ShimmerSource is null)) return;

        Element? desired = branch switch
        {
            // CONTRACT (shimmer→real is a CROSS-DISSOLVE, never a dip to empty): the shimmer branch carries its own
            // Opacity EXIT terminal, so Remove() below orphans it and fades it to 0 while the freshly mounted real tree
            // reveals UP over it. Orphans draw UNDER live children (SceneRecorder), so the two layers dissolve in the
            // same slot instead of the shimmer vanishing on the swap frame and leaving a blank page.
            // Opacity ONLY — no EnterExit.Blur: a page-sized shimmer tree is exactly the blur-group perf cliff called
            // out above MotionRecipes.PageSlideForward, and the fade alone reads as the dissolve.
            // Bounded, not trusted: Remove() gives the orphan its OWN hard deadline (duration + delay + slack), so the
            // "half resolved page" a wedged/never-settling exit track once produced is now reclaimed in ~one exit
            // duration instead of waiting out the host's global 2s backstop.
            // Reduced motion ⇒ no exit stamp at all (the swap snaps); the pulse + reveal already no-op under it.
            // Content-owned reveal (SkelReveal.None): the shimmer must LINGER across the content's own per-row
            // entrance (it draws behind the live tree, so a too-fast exit leaves an empty gap before the rows fade
            // up). Floor the exit at the standard content-reveal duration so apps get the cross-dissolve for free —
            // no hand-tuned ExitMs to match the list's row-add timing.
            1 => StampShimmerExit(
                    SkeletonDeriver.Derive((se.ShimmerSource ?? se.Content)(), se.Style),
                    se.Reveal == SkelReveal.None ? MathF.Max(se.Style.ExitMs, Expressive.Slow) : se.Style.ExitMs),
            3 => se.OnFailed?.Invoke(),
            _ => se.Content(),
        };
        bool branchChanged = lastBranch != 0 && branch != lastBranch;
        if (branchChanged)
        {
            // Pending/ready/failed edges are semantic tree replacements even when both roots have the
            // same ElementTypeId. Diffing the shimmer root in place keeps its animation state attached
            // to the real branch.
            ReplaceSingleChild(node, desired);
        }
        else
        {
            ReconcileSingleChild(node, desired, lastEl);
        }
        // Inherit the active branch's layout participation (Grow/size) onto this transparent boundary — exactly like a
        // component (ReconcileComponent) or KeepAlive (ReconcileKeepAlive) does. Without it the SkelRegion node keeps its
        // default Grow=0, so a Grow=1 content subtree (e.g. a single-column virtualized list whose only intrinsic height
        // is its chrome) can't fill its parent: the region collapses to the content's intrinsic size and a viewport-driven
        // list realizes 0 rows (the empty-Liked bug). Large-intrinsic content (home shelves, a detail rail) masked it.
        MirrorParticipation(node, _scene.FirstChild(node));
        // LayoutDirty: ReplaceSingleChild / ReconcileSingleChild (structural) and WriteColumns (layout-input change)
        // already mark. A force re-run on an unchanged Ready branch (parent re-render) must not page-dirty the region.
        _skelState[idx] = (branch, desired, se.Group);

        // Hide the enclosing scrollbar while this region is loading (branch 1): the short skeleton → tall real swap
        // would otherwise pop the rail from a tiny thumb to its real size. Restored on Ready/Failed.
        SetSkeletonScrollbarSuppression(node, branch == 1);

        if (branch == 1 && lastBranch != 1 && Anim is { } a1)
        {
            // Pulse the whole derived skeleton (one looping track on the root; CancelAll on the orphan path kills it).
            var shimmerRoot = _scene.FirstChild(node);
            if (!shimmerRoot.IsNull) a1.SkeletonPulse(shimmerRoot, se.Style.PulseMin, se.Style.PulseMs);
        }
        else if (branch == 2 && lastBranch == 0 && se.Group is { } initialGroup)
        {
            // A grouped region may mount directly on its real branch when its data was already warm. It still
            // registered above, so it MUST report Done or it leaves the group waiting forever for a transition that
            // will never happen. Keep the initial-ready reveal grouped; ungrouped regions retain their no-animation
            // mount behavior.
            Action? reveal = null;
            if (Anim is { } a0)
            {
                var realRoot = _scene.FirstChild(node);
                reveal = () =>
                {
                    if (!realRoot.IsNull && _scene.IsLive(realRoot))
                        SkeletonReveal.Play(a0, _scene, se.Reveal, realRoot, se.Style);
                };
            }
            SkelGroupCoordinator.Done(initialGroup, idx, reveal);
        }
        else if (branch == 2 && lastBranch == 1 && Anim is { } a2)
        {
            // Shimmer→real: blur-reveal the freshly-mounted real subtree (grouped regions reveal together).
            var realRoot = _scene.FirstChild(node);
            void Reveal() { if (!realRoot.IsNull && _scene.IsLive(realRoot)) SkeletonReveal.Play(a2, _scene, se.Reveal, realRoot, se.Style); }
            if (se.Group is { } g) SkelGroupCoordinator.Done(g, idx, Reveal);
            else Reveal();
        }
        else if (branch == 3 && lastBranch != 3 && se.Group is { } gf)
        {
            SkelGroupCoordinator.Done(gf, idx, null);   // a failed member still completes its group's round
        }

        MirrorParticipation(node, _scene.FirstChild(node));
    }

    // Stamp the derived shimmer ROOT with an Opacity EXIT terminal so Remove() cross-dissolves it out (as an orphan
    // drawn UNDER the live tree) while the real content reveals in — the same slot, no empty frame. Opacity only: a
    // page-sized blur group is a known perf cliff. Under reduced motion the stamp is skipped and the swap snaps.
    // Only a BoxEl root can carry Animate; SkeletonDeriver returns a BoxEl for every container/leaf shape it derives,
    // and the residual non-BoxEl case (a bespoke SkeletonOverride of another element type) simply snaps as before.
    private static Element StampShimmerExit(Element shimmerRoot, float exitMs)
        => Motion.ReducedMotion || shimmerRoot is not BoxEl b
            ? shimmerRoot
            : b with
            {
                Animate = new LayoutTransition(
                    TransitionChannels.Opacity,
                    TransitionDynamics.Tween(exitMs, Easing.SmoothOut),
                    Exit: new EnterExit(Opacity: 0f, Active: true)),
            };

    private void MountFor(NodeHandle node, ForElBase fe)
    {
        // The boundary node is a layout-transparent container; the effect reads the LATEST stored ForElBase (not its
        // mount-time capture), so a parent re-render can re-point the closures (UpdateFor) — exactly like MountShow.
        int mountIdx = (int)node.Raw.Index;
        _forEl[mountIdx] = fe;
        WriteAnchorColumns(node, fe, old: null);   // before the first rows mount: a Stagger must be on record for them
        var eff = new Effect(Runtime, () =>
        {
            if (!_scene.IsLive(node)) return;
            int idx = (int)node.Raw.Index;
            if (!_forEl.TryGetValue(idx, out var cur)) return;
            // Fill reads the items source ONCE (tracked ⇒ subscribes this effect) and fills a pooled buffer it rents via
            // Grow (mirrors RealizeBoundWindow) — no fresh new Element[n] each run, so the nav-churn Gen0 stays flat.
            Element[] buf = Array.Empty<Element>();
            int n = cur.Fill(ref buf);
            var prev = _forState.TryGetValue(idx, out var p) ? p : (Array.Empty<Element>(), 0);
            ReconcileChildren(node, buf.AsSpan(0, n), prev.Item1.AsSpan(0, prev.Item2));
            if (prev.Item1.Length > 0) { Array.Clear(prev.Item1, 0, prev.Item2); ArrayPool<Element>.Shared.Return(prev.Item1); }
            _forState[idx] = (buf, n);
        }, owner: null, runNow: false);
        _forEffect[mountIdx] = eff;
        AddBinding(node, eff);
        eff.RunNow();
    }

    private void UpdateFor(NodeHandle node, ForElBase next)
    {
        int idx = (int)node.Raw.Index;
        _forEl[idx] = next;
        if (_forEffect.TryGetValue(idx, out var eff)) eff.Schedule();
    }

    // ── Fine-grained bindings (signal → scene node, no re-render) ────────────────────────────────

    private void MountKeepAlive(NodeHandle node, KeepAliveEl ka)
    {
        int idx = (int)node.Raw.Index;
        var state = new KeepAliveState { Boundary = node };
        _keepAliveState[idx] = state;
        WriteAnchorColumns(node, ka, old: null);

        var eff = new Effect(Runtime, () =>
        {
            if (!_scene.IsLive(node)) return;

            object token = ka.Active();
            bool cacheable = ka.Options.ShouldCache?.Invoke(token) ?? true;
            // A transient (non-cached) page gets a fresh key per ACTIVATION, not per run: a re-run that did not move the
            // token (RethemeAll schedules every boundary; a signal read inside View) keeps the active transient entry and
            // updates it in place. Minting a new key here unmounted the live page and mounted a copy (state lost, Enter
            // replayed). Leaving and coming back still remounts: ActiveKey is then the other route's key.
            string key = !cacheable
                && state.ActiveKey is { } activeKey
                && state.Entries.TryGetValue(activeKey, out var activeEntry)
                && !activeEntry.Cacheable && Equals(activeEntry.Token, token)
                ? activeKey
                : cacheable ? ka.KeyOf(token) : "__transient:" + (++state.TransientSeq).ToString();
            Element desired = ka.View(token) with { Key = key };

            ReconcileKeepAlive(node, state, ka.Options, key, token, desired, cacheable);
            // structural: this effect PARKS the outgoing subtree (ReconcileKeepAlive → DeactivateKeepAliveEntry →
            // SetSubtreeParked), and a parked component's render is skipped + deferred (RunComponent). It must therefore
            // beat the render effects of the pages inside it whenever ONE signal write feeds both — otherwise the
            // outgoing page renders once against the incoming route before being parked. ReactiveRuntime.Flush drains
            // the structural queue first, so the park always lands before those renders. (Only KeepAlive qualifies:
            // Show/Skel/For boundaries REMOVE their outgoing branch rather than park it, and a removed computation is
            // already skipped by the drain's Disposed check.)
        }, owner: null, runNow: true, structural: true);
        AddBinding(node, eff);
    }

    private void ReconcileKeepAlive(NodeHandle node, KeepAliveState state, KeepAliveOptions options, string key, object token, Element desired, bool cacheable)
    {
        state.Clock++;
        state.Options = options;

        LayoutTransition? transition = null;
        bool activationChanged = state.ActiveKey != key;

        if (state.ActiveKey is { } activeKey && state.Entries.TryGetValue(activeKey, out var activeEntry)
            && activeKey == key && !Equals(activeEntry.Token, token))
        {
            // A cache key identifies retained STATE, not necessarily one immutable route. Wavee deliberately collapses
            // album→album and artist→artist onto one warm slot; the token still changed and TransitionFor must get the
            // edge so the in-place update receives its directional entrance. There is no outgoing root in this case:
            // Update below preserves the mounted component/state, then the normal enter seed animates that same root.
            transition = options.TransitionFor?.Invoke(activeEntry.Token, token);
            activationChanged = true;
        }

        // A retained page commonly contains self-measuring responsive controls: activation mounts/attaches their proxy
        // geometry now, then OnBoundsChanged publishes the real width for the following frame. Suppress both projection
        // windows so navigation never turns that implementation detail into card/shelf/content-card size motion.
        if (activationChanged && options.SuppressLayoutTransitionsOnActivation)
            _keepAliveLayoutSuppressionFrames = Math.Max(_keepAliveLayoutSuppressionFrames, 2);

        if (state.ActiveKey is { } oldKey && oldKey != key && state.Entries.TryGetValue(oldKey, out var oldActive))
        {
            transition = options.TransitionFor?.Invoke(oldActive.Token, token);
            if (transition is { } spec && spec.Exit.Active && Anim is not null && !Motion.ReducedMotion)
                BeginKeepAliveExit(state, oldActive, options, spec);
            else
            {
                FinishKeepAliveExit(state, options);
                DeactivateKeepAliveEntry(oldActive, options);
                if (!oldActive.Cacheable) state.Entries.Remove(oldKey);
            }
        }
        else if (state.ActiveKey is null && FrameEpoch > 1)
        {
            // FIRST activation of a boundary that appears into an already-presented UI (a freshly opened tab whose first
            // route IS the destination — the player-bar artist link). There is no outgoing entry, so the switch branch
            // above never ran and no Enter was seeded: a one-frame hard cut. TransitionFor owns this edge too, with
            // KeepAliveOptions.FirstActivation as the old token (the recipe is the app's; null ⇒ no entrance). The
            // gate is the engine's first-frame notion: the host bumps FrameEpoch at the top of every paint, so ≤ 1
            // means this reconcile runs before or inside the very first paint — the launch page, which nothing was on
            // screen to transition from, mounts without one (Flutter's Navigator likewise never animates its initial
            // route; host-less reconcilers never tick FrameEpoch and keep the plain mount).
            transition = options.TransitionFor?.Invoke(KeepAliveOptions.FirstActivation, token);
        }

        if (!state.Entries.TryGetValue(key, out var entry))
        {
            var root = _scene.CreateNode(desired.ElementTypeId);
            _scene.AppendChild(node, root);
            _keepAliveRootKey[(int)root.Raw.Index] = key;   // before Mount: descendant scroll nodes resolve scope at mount
            Mount(root, desired);
            entry = new KeepAliveEntry
            {
                Key = key,
                Token = token,
                El = desired,
                Root = root,
                LastUsed = state.Clock,
                Attached = true,
                ResourcesActive = true,
                Cacheable = cacheable,
            };
            state.Entries[key] = entry;
            MarkLayoutShape(node);
        }
        else
        {
            entry.Token = token;
            entry.Cacheable = cacheable;
            entry.LastUsed = state.Clock;
            if (!entry.Attached)
                ReactivateKeepAliveEntry(node, entry, options);
            else if (state.ExitingKey == key)
            {
                Anim?.CancelAll(entry.Root);
                state.ExitingKey = null;
                state.ExitDeadlineAnimMs = 0; state.ExitStartTicks = 0;
                _scene.Mark(entry.Root, NodeFlags.HitTestVisible);
                // Mid-exit reclaim: the page was render-frozen (not parked) so the exit track could paint. Cancel the
                // freeze with the same budgeted replay un-park uses — the deferred render (if any) lands once, now
                // against the restored route, instead of dumping a whole page into this flush.
                BeginUnparkReplayWindow();
                SetSubtreeExitFrozen(entry.Root, frozen: false, budgetReplays: true);
            }

            if (entry.El.ElementTypeId == desired.ElementTypeId)
            {
                Update(entry.Root, desired, entry.El);
                entry.El = desired;
            }
            else
            {
                ReplaceKeepAliveRoot(node, entry, desired);
            }
        }

        state.ActiveKey = key;
        MirrorParticipation(node, entry.Root);
        if (transition is { } enter && enter.Enter.Active && Anim is { } anim && !Motion.ReducedMotion)
        {
            anim.CancelAll(entry.Root);
            anim.SeedEnterOver(entry.Root, enter.Enter, enter, EnterRestOf(entry.El));
        }
        EvictInactiveKeepAliveEntries(state, options);
    }

    private void BeginKeepAliveExit(KeepAliveState state, KeepAliveEntry entry, KeepAliveOptions options, in LayoutTransition spec)
    {
        // Bound every boundary to one outgoing root. A rapid second navigation parks the older outgoing page now, then
        // reversals can reclaim the newly outgoing page without accumulating visible/active retained trees.
        FinishKeepAliveExit(state, options);
        if (!_scene.IsLive(entry.Root)) return;
        // Overlay only for the brief two-root interval. Returning to the normal single-child transparent boundary after
        // settle preserves the page's original flex/scroll measurement semantics.
        if (_scene.IsLive(state.Boundary)) _scene.Mark(state.Boundary, NodeFlags.ZStack);
        OnSubtreeDeactivated?.Invoke(entry.Root);
        _scene.Unmark(entry.Root, NodeFlags.HitTestVisible);
        Anim!.CancelAll(entry.Root);
        Anim.SeedExit(entry.Root, spec.Exit, spec);
        // Freeze component renders for the outgoing snapshot: the page stays attached (exit tracks keep ticking;
        // UseActivation does not fire — park still owns that) but must not rebuild against the incoming route.
        SetSubtreeExitFrozen(entry.Root, frozen: true);
        state.ExitingKey = entry.Key;
        // Belt-and-braces after exit-freeze: the freeze is what keeps the outgoing page a snapshot. These deadlines
        // still catch a wedged exit track that never settles (HasTracks is index-only and matches ANY row on the
        // root, including a Parked one the scheduler never advances) so ExitingKey — and the ZStack overlay — cannot
        // pin forever. Mirror of the orphan path's ExitMaxAgeMs + AppHost.OrphanSettleTimeoutMs.
        state.ExitDeadlineAnimMs = _scene.AnimClockMs + ExitMaxAgeMs(spec);
        state.ExitStartTicks = Stopwatch.GetTimestamp();
    }

    private void FinishKeepAliveExit(KeepAliveState state, KeepAliveOptions options)
    {
        if (state.ExitingKey is not { } key) return;
        state.ExitingKey = null;
        state.ExitDeadlineAnimMs = 0; state.ExitStartTicks = 0;
        if (!state.Entries.TryGetValue(key, out var entry))
        {
            if (_scene.IsLive(state.Boundary)) _scene.Unmark(state.Boundary, NodeFlags.ZStack);
            return;
        }
        Anim?.CancelAll(entry.Root);
        DeactivateKeepAliveEntry(entry, options);
        if (!entry.Cacheable) state.Entries.Remove(key);
        if (_scene.IsLive(state.Boundary)) _scene.Unmark(state.Boundary, NodeFlags.ZStack);
    }

    /// <summary>Park retained outgoing pages once their finite exit track settles — OR force-finish at the exit's own
    /// backstop deadline even while HasTracks still reports true. HasTracks is index-only (matches ANY row on the
    /// root, PASS1/PASS2-skipped Parked rows included), so a wedged/parked exit that never advances would otherwise
    /// pin ExitingKey (and the boundary's ZStack overlay) forever — the same failure mode the orphan path's own
    /// deadline (ExitMaxAgeMs) + the host's 2s wall-clock backstop (AppHost.OrphanSettleTimeoutMs) exist to catch.
    /// Called by the host after animation ticking; no allocations and no scan unless a KeepAlive boundary exists.</summary>
    public void FinalizeKeepAliveTransitions()
    {
        foreach (var state in _keepAliveState.Values)
        {
            if (state.ExitingKey is not { } key || !state.Entries.TryGetValue(key, out var entry)) continue;
            bool settled = Anim is null || !Anim.HasTracks(entry.Root);
            if (!settled)
            {
                bool animDeadlinePassed = _scene.AnimClockMs >= state.ExitDeadlineAnimMs;
                double wallAgeMs = (Stopwatch.GetTimestamp() - state.ExitStartTicks) * 1000.0 / Stopwatch.Frequency;
                if (!animDeadlinePassed && wallAgeMs < KeepAliveExitWallTimeoutMs) continue;   // still within its exit window
            }
            FinishKeepAliveExit(state, state.Options ?? KeepAliveOptions.Default);
        }
    }

    // Wall-clock outer guard, same value/rationale as AppHost.OrphanSettleTimeoutMs: the anim-clock deadline
    // (ExitMaxAgeMs) can never trip on a healthy exit however badly the wall clock hitches, so this only fires on a
    // genuinely wedged track.
    private const long KeepAliveExitWallTimeoutMs = 2000;

    private void ReactivateKeepAliveEntry(NodeHandle parent, KeepAliveEntry entry, KeepAliveOptions options)
    {
        if (!_scene.IsLive(entry.Root)) return;
        _scene.Detach(entry.Root);
        _scene.AppendChild(parent, entry.Root);
        entry.Attached = true;
        if (options.ReleaseInactiveResources && !entry.ResourcesActive)
        {
            SetSubtreeResourcesActive(entry.Root, active: true);
            entry.ResourcesActive = true;
        }
        // budgetReplays: a whole PAGE comes back here, so its accumulated render debt is spread across frames rather than
        // dumped into this one flush (the page-enter animation has to stay smooth). The row-level keep-alive un-park in
        // RealizeWindow stays UNBUDGETED on purpose — that subtree is re-entering the viewport, so it must be current now.
        BeginUnparkReplayWindow();
        SetSubtreeParked(entry.Root, parked: false,
            snapStructural: options.SuppressLayoutTransitionsOnActivation, budgetReplays: true);
        _scene.Mark(entry.Root, NodeFlags.HitTestVisible);
        MarkLayoutShape(parent);
    }

    private void DeactivateKeepAliveEntry(KeepAliveEntry entry, KeepAliveOptions options)
    {
        if (!_scene.IsLive(entry.Root)) return;
        OnSubtreeDeactivated?.Invoke(entry.Root);
        if (options.ReleaseInactiveResources && entry.ResourcesActive)
        {
            SetSubtreeResourcesActive(entry.Root, active: false);
            entry.ResourcesActive = false;
        }
        SetSubtreeParked(entry.Root, parked: true);   // inactive → suspend its render-effects (no re-render while invisible)
        _scene.Detach(entry.Root);
        entry.Attached = false;
        if (!entry.Cacheable) FreeKeepAliveEntry(entry);
    }

    private void ReplaceKeepAliveRoot(NodeHandle parent, KeepAliveEntry entry, Element desired)
    {
        if (_scene.IsLive(entry.Root))
        {
            OnSubtreeDeactivated?.Invoke(entry.Root);
            UnmountSubtree(entry.Root);                             // saves scroll (scope still mapped)
            _keepAliveRootKey.Remove((int)entry.Root.Raw.Index);
            _scene.FreeSubtree(entry.Root);
        }
        var root = _scene.CreateNode(desired.ElementTypeId);
        _scene.AppendChild(parent, root);
        _keepAliveRootKey[(int)root.Raw.Index] = entry.Key;
        Mount(root, desired);
        entry.Root = root;
        entry.El = desired;
        entry.Attached = true;
        entry.ResourcesActive = true;
        MarkLayoutShape(parent);
    }

    private void EvictInactiveKeepAliveEntries(KeepAliveState state, KeepAliveOptions options)
    {
        int max = Math.Max(1, options.MaxEntries);
        while (state.Entries.Count > max)
        {
            KeepAliveEntry? victim = null;
            foreach (var e in state.Entries.Values)
            {
                if (e.Attached) continue;
                if (victim is null || e.LastUsed < victim.LastUsed) victim = e;
            }
            if (victim is null) break;
            state.Entries.Remove(victim.Key);
            FreeKeepAliveEntry(victim);
        }
    }

    private void FreeKeepAliveEntry(KeepAliveEntry entry)
    {
        if (!_scene.IsLive(entry.Root)) return;
        OnSubtreeDeactivated?.Invoke(entry.Root);
        UnmountSubtree(entry.Root);                             // saves each scroll node's offset (scope still mapped)
        _keepAliveRootKey.Remove((int)entry.Root.Raw.Index);
        _scene.FreeSubtree(entry.Root);
    }

    private void SetSubtreeResourcesActive(NodeHandle node, bool active)
    {
        if (!_scene.IsLive(node)) return;
        ref NodePaint paint = ref _scene.Paint(node);
        if (paint.VisualKind == VisualKind.Image && paint.ImageId != 0)
        {
            if (active) PinImageNode(node, paint.ImageId);
            else UnpinImageNode(node, paint.ImageId);
            if (_scene.TryGetImageEffects(node, out var effects) && effects.DerivedImageId != 0)
            {
                if (active) PinImageNode(node, effects.DerivedImageId);
                else UnpinImageNode(node, effects.DerivedImageId);
            }
            // A hold-last-good in progress (media-pipeline.md §hold-last-good): the pending (new, still-decoding) id
            // is pinned exactly like paint.ImageId/DerivedImageId, so a park doesn't leave it live-but-unpinned
            // (evictable mid-hold) and an un-park re-pins it (idempotent via _imagePinnedNodes).
            if (_pendingImageId.TryGetValue((int)node.Raw.Index, out int pendingId) && pendingId != 0)
            {
                if (active) PinImageNode(node, pendingId);
                else UnpinImageNode(node, pendingId);
            }
            // A parked page has nothing on screen to dissolve: finish any swap crossfade instead of parking its pin.
            if (!active) FinishImageSwap(node);
        }
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
            SetSubtreeResourcesActive(c, active);
    }

    // Park / un-park a kept-alive subtree so an INACTIVE page doesn't keep working while invisible. The single chokepoint
    // for three effects, all driven off one walk on the tab-switch edge:
    //  (1) component render-effects: while parked RunComponent defers (sets DeferredRender); on un-park we replay exactly
    //      the components that owed a render — once, now attached, so context resolves and content is current. A page
    //      un-park (budgetReplays) spreads that replay across frames — see UnparkReplaysPerFrame.
    //  (2) the per-component activation signal (UseIsActive): flipped here so UseActivation fires onDeactivated/onActivated.
    //  (3) a scene-level Parked marker + a per-node ticker notification so the animation/scroll engines quiesce this
    //      subtree's tracks (a backgrounded looping animation / mid-fling scroll must not defeat the idle wake-stop), and
    //      so a component mounted under a parked ancestor seeds inactive (MountComponent reads the marker).
    private void SetSubtreeParked(NodeHandle node, bool parked, bool snapStructural = false, bool budgetReplays = false)
    {
        if (!_scene.IsLive(node)) return;
        // A cached page can be parked halfway through a card-refit/reveal. Navigation-owned activation must land those
        // finite geometry rows before un-parking; looping/opacity/brush tracks remain paused/resumable as authored.
        if (!parked && snapStructural) Anim?.SnapStructuralToLayout(node);
        // WhileHover Offset/Rotation shares TranslateX with FLIP. A Fold cover parked mid-fan (or snapped to
        // identity on activation) stays at the origin until the next hover retargets it — the "hover puts the
        // covers back" Browse→Charts→Back bug. Land the authored rest on both edges; pointer state is gone.
        Anim?.SnapAuthoredPose(node);
        if (parked) _scene.Mark(node, NodeFlags.Parked); else _scene.Unmark(node, NodeFlags.Parked);
        OnNodeParkedChanged?.Invoke(node, parked);
        if (_comps.TryGetValue(node, out var entry))
        {
            entry.Parked = parked;
            if (parked) entry.ExitFrozen = false;   // park subsumes freeze; DeferredRender debt carries
            // P1 presence folds in too: Active is Parked-AND-Hidden-free (SetSubtreeHidden, Reconciler.Presence.cs)
            // so UseIsActive()/UseInterval auto-pause across EITHER edge without duplicating this formula.
            if (entry.ActiveSig is { } sig) sig.Value = !parked && !entry.Hidden;   // value-gated; flips the UseIsActive memo → UseActivation
            // Re-parked before its queued replay ran: cancel the queue slot and hand the debt back to DeferredRender, so
            // the NEXT un-park owns it (a parked component must never be scheduled by the drip).
            if (parked && entry.QueuedReplay) { entry.QueuedReplay = false; entry.DeferredRender = true; }
            if (!parked && entry.DeferredRender)
            {
                entry.DeferredRender = false;
                if (!budgetReplays || TakeReplayBudget()) entry.Effect?.Schedule();
                else { entry.QueuedReplay = true; _replayQueue.Enqueue(entry); }   // drips from BeginRenderCensus
            }
        }
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
            SetSubtreeParked(c, parked, snapStructural, budgetReplays);
    }

    // Render-freeze an attached KeepAlive page for the duration of its exit. Orthogonal to Parked: no NodeFlags.Parked
    // (the AnimScheduler must keep ticking the exit), no ActiveSig flip (UseActivation stays a park/un-park event),
    // no Detach (the outgoing root stays in the ZStack overlay). Scope is component render-effects only — node-level
    // property binds still fire so the exit can paint. Un-freeze replays DeferredRender the same way un-park does.
    private void SetSubtreeExitFrozen(NodeHandle node, bool frozen, bool budgetReplays = false)
    {
        if (!_scene.IsLive(node)) return;
        if (_comps.TryGetValue(node, out var entry))
        {
            if (frozen)
            {
                entry.ExitFrozen = true;
                if (entry.QueuedReplay) { entry.QueuedReplay = false; entry.DeferredRender = true; }
            }
            else if (entry.ExitFrozen)
            {
                entry.ExitFrozen = false;
                if (entry.DeferredRender && !entry.Parked)
                {
                    entry.DeferredRender = false;
                    if (!budgetReplays || TakeReplayBudget()) entry.Effect?.Schedule();
                    else { entry.QueuedReplay = true; _replayQueue.Enqueue(entry); }
                }
            }
        }
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
            SetSubtreeExitFrozen(c, frozen, budgetReplays);
    }

    // The per-frame replay allowance, epoch-keyed exactly like the steady realize budget (the host bumps FrameEpoch once
    // per paint), and SHARED between the un-park walk itself and that frame's drip so a frame can never exceed K.
    private bool TakeReplayBudget()
    {
        if (_replayBudgetEpoch != FrameEpoch) { _replayBudgetEpoch = FrameEpoch; _replayBudgetUsed = 0; }
        if (_replayBudgetUsed >= UnparkReplaysPerFrame) return false;
        _replayBudgetUsed++;
        return true;
    }

    /// <summary>Open a fresh replay window for an un-park that starts with nothing queued. Belt-and-braces for a host-less
    /// reconciler (the VerticalSlice suites) which never ticks <see cref="FrameEpoch"/> and never drains: with the queue
    /// empty there is no drip in flight to protect, so no navigation can be starved by an earlier one's spend.</summary>
    private void BeginUnparkReplayWindow()
    {
        if (_replayQueue.Count == 0) { _replayBudgetEpoch = FrameEpoch; _replayBudgetUsed = 0; }
    }

    /// <summary>Drip the queued un-park replays in tree order: schedule up to <see cref="UnparkReplaysPerFrame"/> per
    /// frame (minus whatever an un-park in the same frame already spent). Cancelled entries — re-parked, unmounted, or
    /// already re-rendered by a real signal write — are dropped without spending budget.</summary>
    private void DrainDeferredReplays()
    {
        while (_replayQueue.Count > 0)
        {
            var entry = _replayQueue.Peek();
            if (!entry.QueuedReplay) { _replayQueue.Dequeue(); continue; }
            if (!TakeReplayBudget()) return;
            _replayQueue.Dequeue();
            entry.QueuedReplay = false;
            entry.Effect?.Schedule();
        }
    }

    private void AddBinding(NodeHandle node, Computation c)
    {
        int idx = (int)node.Raw.Index;
        if (!_nodeBindings.TryGetValue(idx, out var list)) { list = new List<Computation>(2); _nodeBindings[idx] = list; }
        list.Add(c);
    }

    // A bound Prop<T> is either a thunk or a signal-direct payload — the BindEffect body reads whichever the channel
    // carries (one null test per fire; signal-direct means the CALLER allocated no closure). Wired at mount; a NEW
    // thunk/signal supplied on a re-render RE-WIRES the same effect (bound→bound — RewireBinds, Reconciler.Rewire.cs;
    // locked by gate.bind.rewire-*), while a static↔bound flip still loses (BindContract flags it).
    /// <summary>Pin <paramref name="imageId"/> for <paramref name="node"/>. <paramref name="priority"/> is the lane the
    /// request was made at: <c>ImageCache.Pin</c> re-prioritizes a still-Pending entry to it, so an Overscan request
    /// stays in the Overscan lane through its pin instead of being force-promoted to Visible by the pin itself.</summary>
    private void PinImageNode(NodeHandle node, int imageId, ImagePriority priority = ImagePriority.Visible)
    {
        if (Images is null || imageId == 0 || !_scene.IsLive(node) || !IsReachableFromRoot(node)) return;
        long pinKey = ((long)(int)node.Raw.Index << 32) | (uint)imageId;
        if (_imagePinnedNodes.Add(pinKey))
        {
            // Before the pin: a keep-while-hidden image must already be protected when the pin restarts a parked entry.
            if (_keepWhileHiddenNodes.Contains((int)node.Raw.Index) && _keepWhileHiddenPins.Add(pinKey))
                Images.AddKeepWhileHidden(new ImageHandle(imageId), +1);
            Images.Pin(new ImageHandle(imageId), priority);
            TrackImageNode(imageId, node);
        }
    }

    /// <summary>W2-E3: the DecodeScheduler lane for an image request from <paramref name="node"/>. Inside a realize pass
    /// (the <c>_realizeEntry</c> block) it is the slot being realized: scalar compares only. Outside one, the row is
    /// located by walking parents until the parent is a viewport's content node — the shape a realized row takes when it
    /// RE-RENDERS after its data lands (the cover source going "" → url), which is the common form of the fling defect and
    /// so cannot be left at Visible. A node under no viewport is Visible.</summary>
    private ImagePriority ImageRequestPriority(NodeHandle node)
    {
        if (_realizeOuterOverscan) return ImagePriority.Overscan;
        if (_realizeEntry is not null) return ImagePriorityFor(_realizeEntry, _realizeSlotIndex);
        if (_virtuals.Count == 0) return ImagePriority.Visible;
        NodeHandle cur = node, parent = _scene.Parent(node);
        while (!parent.IsNull)
        {
            NodeHandle viewport = _scene.Parent(parent);
            if (!viewport.IsNull && _virtuals.TryGetValue(viewport, out var entry)
                && _scene.TryGetScroll(viewport, out var sc) && sc.ContentNode == parent)
                return ImagePriorityFor(entry, SlotIndexOf(entry, parent, cur, sc.FirstRealized));
            cur = parent; parent = viewport;
        }
        return ImagePriority.Visible;
    }

    private static ImagePriority ImagePriorityFor(VirtualEntry? entry, int index)
    {
        if (entry is null || index < 0) return ImagePriority.Visible;
        return index >= entry.VisibleFirst && index < entry.VisibleLast ? ImagePriority.Visible : ImagePriority.Overscan;
    }

    /// <summary>The logical item index of slot root <paramref name="root"/> under <paramref name="content"/>: the bound
    /// slot's index signal, or FirstRealized + child ordinal on the keyed path (logical order is the layout/hit-test
    /// contract). -1 (⇒ Visible) for a persistent-prefix slot or an unknown root.</summary>
    private int SlotIndexOf(VirtualEntry entry, NodeHandle content, NodeHandle root, int firstRealized)
    {
        if (entry.Slots is { } slots)
        {
            var span = CollectionsMarshal.AsSpan(slots);
            for (int i = 0; i < span.Length; i++)
                if (span[i].Root == root) return span[i].Index is null ? -1 : span[i].Index.Peek();
            return -1;
        }
        int ordinal = 0;
        for (var c = _scene.FirstChild(content); !c.IsNull; c = _scene.NextSibling(c), ordinal++)
            if (c == root) return firstRealized + ordinal;
        return -1;
    }

    /// <summary>W2-E3 promotion: rows that were realized in the overscan halo under the PREVIOUS visible band and sit inside
    /// the NEW one queued their covers in the Overscan lane — move every still-Pending request in their subtrees to the
    /// Visible lane. <c>DecodeScheduler.Prioritize</c> is raise-only and claim-deduped, and <c>ImageCache.Promote</c> skips
    /// settled entries, so a repeat is a no-op. Rows mounted or rebound BY this pass already requested at their final lane
    /// and are excluded by the "was realized before this pass" test. A scalar walk over the slot list (bound) or the content
    /// child chain (keyed; in logical order once ReconcileWindow returns) — no allocation, phases 6–13 safe.</summary>
    private void PromoteNewlyVisibleRows(VirtualEntry entry, NodeHandle content, int prevFirst, int prevLast, int oldVisFirst, int oldVisLast)
    {
        if (Images is null || prevLast <= prevFirst) return;
        int newVisFirst = entry.VisibleFirst, newVisLast = entry.VisibleLast;
        if (newVisLast <= newVisFirst) return;
        if (entry.Slots is { } slots)
        {
            var span = CollectionsMarshal.AsSpan(slots);
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i].Index is null) continue;
                int idx = span[i].Index.Peek();
                if (NewlyVisible(idx, prevFirst, prevLast, oldVisFirst, oldVisLast, newVisFirst, newVisLast))
                    PromoteSlotImages(span[i].Root);
            }
        }
        else
        {
            int idx = entry.PrevFirst;
            for (var c = _scene.FirstChild(content); !c.IsNull; c = _scene.NextSibling(c), idx++)
                if (NewlyVisible(idx, prevFirst, prevLast, oldVisFirst, oldVisLast, newVisFirst, newVisLast))
                    PromoteSlotImages(c);
        }

        static bool NewlyVisible(int idx, int prevFirst, int prevLast, int oldVisFirst, int oldVisLast, int newVisFirst, int newVisLast)
            => idx >= newVisFirst && idx < newVisLast          // inside the new visible band
            && idx >= prevFirst && idx < prevLast              // was realized BEFORE this pass (so it requested at Overscan)
            && !(idx >= oldVisFirst && idx < oldVisLast);      // and was not already visible (already Visible-lane)
    }

    private void PromoteSlotImages(NodeHandle n)
    {
        if (n.IsNull || !_scene.IsLive(n)) return;
        ref NodePaint paint = ref _scene.Paint(n);
        if (paint.VisualKind == VisualKind.Image)
        {
            if (paint.ImageId != 0) Images!.Promote(new ImageHandle(paint.ImageId), ImagePriority.Visible);
            if (_pendingImageId.TryGetValue((int)n.Raw.Index, out int held) && held != 0)   // a hold-last-good target still decoding
                Images!.Promote(new ImageHandle(held), ImagePriority.Visible);
        }
        for (var c = _scene.FirstChild(n); !c.IsNull; c = _scene.NextSibling(c)) PromoteSlotImages(c);
    }

    private void UnpinImageNode(NodeHandle node, int imageId)
    {
        if (Images is null || imageId == 0 || !_scene.IsLive(node)) return;
        long pinKey = ((long)(int)node.Raw.Index << 32) | (uint)imageId;
        if (_imagePinnedNodes.Remove(pinKey))
        {
            var h = new ImageHandle(imageId);
            if (_keepWhileHiddenPins.Remove(pinKey)) Images.AddKeepWhileHidden(h, -1);
            Images.Unpin(h);
            UntrackImageNode(imageId, node);
            if (Images.RefsOf(h) == 0 && Images.StateOf(h) == ImageState.Pending)
                Images.Cancel(h);
        }
    }

    // Sole write path for paint.ImageId (media-pipeline.md §hold-last-good). A bare re-key would swap straight onto
    // the new (Pending) entry, dropping the OLD Ready texture the same frame — the placeholder flashes back in.
    // Instead, when the old texture is Ready and the new one is still decoding, HOLD: keep drawing the old id, pin
    // the new one (so its completion is tracked) under _pendingImageId, and let MarkImageDirty commit once the new
    // entry settles (Ready or Failed) — no fade restart (see ImageCache.SettleReveal): a hard cut for the SAME picture
    // at another decode size, a short dissolve (BeginImageSwap) for a DIFFERENT one. With nothing drawable on the node
    // (a mount), a resident rendition of the same source stands in the same way. A synchronous cache hit and an instant
    // failure commit at once. Never a placeholder frame over decoded content: Flutter's Image.gaplessPlayback, plus the
    // dissolve.
    // `priority` is the lane `newId` was requested at (W2-E3); it rides along to the pins of `newId` only — a stand-in's
    // pin is a Ready entry, where the lane is moot.
    private void SwapImageId(NodeHandle node, ref NodePaint paint, int newId, ImagePriority priority = ImagePriority.Visible)
    {
        int idx = (int)node.Raw.Index;
        int oldId = paint.ImageId;
        if (newId == oldId)
        {
            // Re-keyed back to what's already on screen while a DIFFERENT id was mid-hold: that pending decode is
            // now orphaned. Unpin it — UnpinImageNode's refs==0 check cancels it — and drop the bookkeeping.
            if (_pendingImageId.Remove(idx, out int stale) && stale != newId)
                UnpinImageNode(node, stale);
            return;
        }

        _pendingImageId.TryGetValue(idx, out int pending);
        if (pending != 0 && pending == newId) return;   // already holding on exactly this id — nothing changed

        ImageState newState = Images is not null && newId != 0 ? Images.StateOf(new ImageHandle(newId)) : ImageState.None;
        bool oldDrawable = Images is not null && oldId != 0 && Images.StateOf(new ImageHandle(oldId)) == ImageState.Ready;
        // A virtual slot is a presentation shell, not an item identity. Different sources
        // must not borrow its previous item's ready cover during a pending decode.
        bool wrongItem = oldDrawable && newId != 0
            && !Images!.SameSource(new ImageHandle(oldId), new ImageHandle(newId))
            && IsVirtualImage(node);
        if (wrongItem)
        {
            FinishImageSwap(node);
            UnpinImageNode(node, oldId);
            paint.ImageId = 0;
            oldId = 0;
            oldDrawable = false;
        }
        bool holdable = oldDrawable && newState == ImageState.Pending;

        if (holdable)
        {
            if (pending != 0) UnpinImageNode(node, pending);   // superseded hold target
            _pendingImageId[idx] = newId;
            PinImageNode(node, newId, priority);   // pins + tracks — MarkImageDirty reaches this node when `newId` settles
            _scene.Mark(node, NodeFlags.PaintDirty);
            if (Diag.CompiledIn && Diag.Enabled && Images is not null && ImageCache.DiagTraced(Images.SourceOf(new ImageHandle(newId))))
                Diag.Event("img", $"hold node={node.Raw.Index} old={oldId} new={newId} " +
                    $"src={ImageCache.DiagSourceTail(Images.SourceOf(new ImageHandle(newId)))}");
            return;
        }

        // Resident-rendition stand-in: nothing drawable on this node (a MOUNT, or a re-key off a still-decoding id)
        // but the SAME source is already resident at another decode size — hold THAT exactly like hold-last-good
        // instead of painting a placeholder for the whole decode. This is the case hold-last-good cannot reach: a cover
        // remounted by a structural page change (preview → loaded page, a layout-tier switch) asking for a new decode
        // bucket. The settle is a hard cut (same picture, sharper). See ImageCache.ResidentRenditionOf.
        if (newState == ImageState.Pending && !oldDrawable
            && Images!.ResidentRenditionOf(new ImageHandle(newId)) is { IsNull: false } standIn)
        {
            if (pending != 0) UnpinImageNode(node, pending);
            FinishImageSwap(node);
            if (oldId != standIn.Id) UnpinImageNode(node, oldId);
            paint.ImageId = standIn.Id;
            PinImageNode(node, standIn.Id);
            _pendingImageId[idx] = newId;
            PinImageNode(node, newId, priority);
            _scene.Mark(node, NodeFlags.PaintDirty);
            if (Diag.CompiledIn && Diag.Enabled && Images is not null && ImageCache.DiagTraced(Images.SourceOf(new ImageHandle(newId))))
                Diag.Event("img", $"standin node={node.Raw.Index} old={oldId} drawn={standIn.Id} new={newId} " +
                    $"src={ImageCache.DiagSourceTail(Images.SourceOf(new ImageHandle(newId)))}");
            return;
        }

        if (pending != 0)
        {
            _pendingImageId.Remove(idx);
            if (pending != newId) UnpinImageNode(node, pending);
        }
        // A synchronous hit (the new picture already resident) still cuts at once. Deliberately NOT a dissolve: this is
        // also the path every recycled virtual row rebinds through while scrolling, and dissolving a slot from the
        // PREVIOUS item's cover would ghost the wrong art across the list. The dissolve belongs to the hold settle
        // (MarkImageDirty), where the old texture was on screen for the whole decode anyway.
        FinishImageSwap(node);
        UnpinImageNode(node, oldId);
        paint.ImageId = newId;
        if (newId != 0) PinImageNode(node, newId, priority);
        _scene.Mark(node, NodeFlags.PaintDirty);
        if (Diag.CompiledIn && Diag.Enabled && Images is not null && newId != 0
            && ImageCache.DiagTraced(Images.SourceOf(new ImageHandle(newId))))
            Diag.Event("img", $"commit node={node.Raw.Index} old={oldId} new={newId} " +
                $"src={ImageCache.DiagSourceTail(Images.SourceOf(new ImageHandle(newId)))}");
    }

    // ── image-swap crossfade (a node's drawn picture replaced by a DIFFERENT one) ───────────────────────────────────
    // node index → the swap in flight: the OUTGOING texture stays pinned and is drawn opaque under the incoming image
    // for ImageCache.SwapCrossfadeMs (SceneRecorder, ImageVisualEffects.SwapOutgoingId). Lives here, not only on
    // ImageVisualEffects, because that struct is rewritten wholesale from the element every reconcile.
    // Image-key changes only: includes nested components rendered outside the realization pass.
    private bool IsVirtualImage(NodeHandle node)
    {
        for (var parent = _scene.Parent(node); !parent.IsNull && _scene.IsLive(parent); parent = _scene.Parent(parent))
            if (_virtuals.ContainsKey(parent)) return true;
        return false;
    }

    private readonly record struct ImageSwap(NodeHandle Node, int OutgoingId, int IncomingId, float StartMs, float DurationMs, bool Cut);
    private readonly Dictionary<int, ImageSwap> _imageSwaps = new();

    /// <summary>Adds every image id the reconciler itself still holds outside the scene's columns — hold-last-good
    /// targets still decoding and swap-crossfade outgoing/incoming ids — to <paramref name="held"/> (the image cache's
    /// tombstone-reclaim proof, <c>ImageCache.SetHeldImageSource</c>). UI thread; reclaim cadence, not per frame.</summary>
    internal void CollectHeldImageIds(HashSet<int> held)
    {
        foreach (var kv in _pendingImageId) if (kv.Value != 0) held.Add(kv.Value);
        foreach (var kv in _imageSwaps)
        {
            if (kv.Value.OutgoingId != 0) held.Add(kv.Value.OutgoingId);
            if (kv.Value.IncomingId != 0) held.Add(kv.Value.IncomingId);
        }
    }
    private readonly List<int> _imageSwapSweep = new(4);
    // The outgoing pin is released a little AFTER the window: the render thread replays against its own image clock,
    // which may trail the UI's by a frame; the outgoing draw already resolves to nothing past the window, so the slack
    // only delays an unpin, never a pixel. The fade clock is held open past the release point (see BeginImageSwap) so
    // the UI is guaranteed a frame that performs it.
    private const float ImageSwapReleaseSlackMs = 34f;
    private const float ImageSwapWakeSlackMs = 100f;

    /// <summary>Start (or supersede) this node's swap crossfade from <paramref name="outgoingId"/> — the texture on
    /// screen — to <paramref name="incomingId"/>. The outgoing is already pinned as the node's drawn id; it simply stays
    /// pinned until <see cref="SweepImageSwaps"/> releases it. Callers commit <c>paint.ImageId = incomingId</c>.
    /// <paramref name="cut"/>: the SAME picture at another decode size. The incoming draws at once (no fade-in) and the
    /// outgoing only backs it while the new pixels may still be on their way to the GPU (a discrete GPU's copy queue).</summary>
    private void BeginImageSwap(NodeHandle node, int outgoingId, int incomingId, bool cut)
    {
        var images = Images!;
        int idx = (int)node.Raw.Index;
        // A newer swap supersedes an older one: its outgoing is the older swap's INCOMING, so only the older outgoing
        // is released — unless it is the NEW incoming (re-keyed back mid-dissolve), whose pin the caller keeps.
        if (_imageSwaps.Remove(idx, out var prev) && prev.OutgoingId != outgoingId && prev.OutgoingId != incomingId)
            UnpinImageNode(node, prev.OutgoingId);
        // Keep the fade clock (UI wake + the render thread's clock-driven presents) open until the release can run: the
        // swap window, or the incoming entry's own still-running reveal if that ends later, plus the release slack.
        float holdMs = ImageCache.SwapCrossfadeMs;
        if (images.FadeParamsOf(new ImageHandle(incomingId), out float revealStart, out float revealMs, out _))
            holdMs = MathF.Max(holdMs, revealStart + revealMs - images.ClockMs);
        float start = images.BeginSwap(new ImageHandle(outgoingId), holdMs + ImageSwapWakeSlackMs);
        _imageSwaps[idx] = new ImageSwap(node, outgoingId, incomingId, start, ImageCache.SwapCrossfadeMs, cut);
        ApplyImageSwapEffects(node, outgoingId, start, ImageCache.SwapCrossfadeMs, cut);
        _scene.Mark(node, NodeFlags.PaintDirty);
        if (Diag.CompiledIn && Diag.Enabled && ImageCache.DiagTraced(images.SourceOf(new ImageHandle(incomingId))))
            Diag.Event("img", $"swap node={node.Raw.Index} out={outgoingId} in={incomingId} " +
                $"src={ImageCache.DiagSourceTail(images.SourceOf(new ImageHandle(incomingId)))}");
    }

    /// <summary>End this node's swap crossfade (if any): release the outgoing pin and re-record without it. Safe to
    /// call on any node — the no-swap case is one dictionary probe.</summary>
    private void FinishImageSwap(NodeHandle node)
    {
        int idx = (int)node.Raw.Index;
        if (!_imageSwaps.Remove(idx, out var swap)) return;
        if (!_scene.IsLive(node)) return;
        // Role guard: the pin set is keyed (node, id), so never drop the pin of an id this node still draws or holds.
        bool stillUsed = _scene.Paint(node).ImageId == swap.OutgoingId
                         || (_pendingImageId.TryGetValue(idx, out int held) && held == swap.OutgoingId);
        if (!stillUsed) UnpinImageNode(node, swap.OutgoingId);
        ApplyImageSwapEffects(node, 0, float.NaN, 0f, cut: false);
        _scene.Mark(node, NodeFlags.PaintDirty);
    }

    /// <summary>Write the swap fields onto the node's sparse <see cref="ImageVisualEffects"/> row, keeping the rest of
    /// the row; drops the row when nothing is left on it (the plain-image case).</summary>
    private void ApplyImageSwapEffects(NodeHandle node, int outgoingId, float startMs, float durationMs, bool cut)
    {
        ImageVisualEffects fx = _scene.TryGetImageEffects(node, out var cur) ? cur : new ImageVisualEffects(0, default, default);
        fx = fx with { SwapOutgoingId = outgoingId, SwapStartMs = startMs, SwapMs = durationMs, SwapCut = cut };
        if (fx.SwapOutgoingId == 0 && fx.DerivedImageId == 0 && fx.Overlay.A <= 0f && fx.Mask.IsNone && fx.Saturation == 1f)
            _scene.ClearImageEffects(node);
        else _scene.SetImageEffects(node, fx);
    }

    /// <summary>Release every swap whose window (and the incoming image's own reveal) has landed. Run once per paint
    /// from <see cref="BeginRenderCensus"/>; the common no-swap case costs one count read.</summary>
    private void SweepImageSwaps()
    {
        _imageSwapSweep.Clear();
        float now = Images?.ClockMs ?? float.PositiveInfinity;
        foreach (var (idx, swap) in _imageSwaps)
        {
            bool landed = now >= swap.StartMs + swap.DurationMs + ImageSwapReleaseSlackMs
                          && (Images is null || Images.CrossFadeOf(new ImageHandle(swap.IncomingId)) >= 1f);
            if (landed || !_scene.IsLive(swap.Node)) _imageSwapSweep.Add(idx);
        }
        foreach (int idx in _imageSwapSweep)
        {
            if (!_imageSwaps.TryGetValue(idx, out var swap)) continue;
            if (_scene.IsLive(swap.Node)) FinishImageSwap(swap.Node);
            else _imageSwaps.Remove(idx);
        }
    }

    void TrackImageNode(int imageId, NodeHandle node)
    {
        if (!_imageNodes.TryGetValue(imageId, out var list))
        {
            list = new List<NodeHandle>(2);
            _imageNodes[imageId] = list;
        }
        for (int i = 0; i < list.Count; i++) if (list[i] == node) return;
        list.Add(node);
    }

    void UntrackImageNode(int imageId, NodeHandle node)
    {
        if (!_imageNodes.TryGetValue(imageId, out var list)) return;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] == node) list.RemoveAt(i);
        }
        if (list.Count == 0) _imageNodes.Remove(imageId);
    }

    /// <summary>Mark every on-screen Image node holding <paramref name="imageId"/> paint-dirty (image status landed).
    /// Returns true iff at least one LIVE node actually owned this id and was marked — an id with no owning node
    /// (prefetch-only, or a node that already unmounted) returns false. E1 (design-engine-images.md): the host's UI-
    /// thread sweep over <c>ImageCache.ContentChangedIds</c> ORs this return across every changed id to decide whether
    /// a content change can matter to the SUBMITTED draw-list bytes at all — a change with no owning node cannot
    /// (there is no draw op referencing it), so it must not defeat the skip-submit hash shortcut for a frame that is
    /// otherwise byte-identical.</summary>
    public bool MarkImageDirty(int imageId)
    {
        if (imageId == 0 || !_imageNodes.TryGetValue(imageId, out var list)) return false;
        bool dirtied = false;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var node = list[i];
            if (!_scene.IsLive(node))
            {
                list.RemoveAt(i);
                continue;
            }
            int idx = (int)node.Raw.Index;
            bool isPending = _pendingImageId.TryGetValue(idx, out int pending) && pending == imageId;
            ref NodePaint paint = ref _scene.Paint(node);
            bool owns = paint.ImageId == imageId
                || (_scene.TryGetImageEffects(node, out var effects) && effects.DerivedImageId == imageId)
                || isPending;
            if (!owns) { list.RemoveAt(i); continue; }

            // Hold-last-good settle (media-pipeline.md §hold-last-good): the held (new) texture just reached a
            // terminal state — commit. It's already pinned + tracked (SwapImageId's hold path did that), so this is a
            // plain field write, never a re-pin. The SAME picture at a new decode size (or a resident stand-in's
            // exact size) hard-cuts: only sharpness changes. A DIFFERENT picture (a new track's cover, another CDN
            // rendition of other art) dissolves from the held texture over ImageCache.SwapCrossfadeMs instead of the
            // old one-frame cut. Either way the node owns the transition, so the entry's own placeholder reveal is
            // settled — nothing ever fades in over a placeholder here. The cut keeps the held texture under the new one
            // for the same window: Ready is a UI-thread state, and on a discrete GPU the new pixels are still on the copy
            // queue in the turn that records this commit, so that frame draws the new id's placeholder — see-through
            // over the held picture, never the node's flat fill over the cover.
            if (isPending && Images is not null)
            {
                var state = Images.StateOf(new ImageHandle(imageId));
                if (state is ImageState.Ready or ImageState.Failed)
                {
                    int heldId = paint.ImageId;   // read BEFORE overwrite
                    bool fromHeld = state == ImageState.Ready && heldId != 0
                        && Images.StateOf(new ImageHandle(heldId)) == ImageState.Ready;
                    bool dissolve = fromHeld && !Images.SameSource(new ImageHandle(heldId), new ImageHandle(imageId));
                    if (state == ImageState.Ready) Images.SettleReveal(new ImageHandle(imageId));   // no fade restart
                    if (fromHeld) BeginImageSwap(node, heldId, imageId, cut: !dissolve);   // the held texture stays pinned for the window
                    else
                    {
                        FinishImageSwap(node);
                        UnpinImageNode(node, heldId);   // release the OLD (held) id
                    }
                    paint.ImageId = imageId;
                    _pendingImageId.Remove(idx);
                    if (Diag.CompiledIn && Diag.Enabled && ImageCache.DiagTraced(Images.SourceOf(new ImageHandle(imageId))))
                        Diag.Event("img", $"commit node={node.Raw.Index} new={imageId} state={state} via=settle{(dissolve ? " dissolve" : "")}");
                }
            }

            _scene.Mark(node, NodeFlags.PaintDirty);
            dirtied = true;
        }
        if (list.Count == 0) _imageNodes.Remove(imageId);
        return dirtied;
    }

    /// <summary>Describe every on-screen node holding <paramref name="imageId"/> as a repaint rect (damage-scoped-
    /// repaint-design.md "Step 3": an image landing or crossfade tick damages ITS nodes, not the whole window). Returns
    /// the count of rects added, or <c>-1</c> the moment a node cannot be described as a plain translated box (a scaled/
    /// rotated ancestor) — the caller falls back to a named <c>ForceFull(DetachedContent)</c> for THIS id rather than
    /// emit an under-covering rect. <c>0</c> with no nodes tracked means the id is off screen (prefetch-only, or a node
    /// that already unmounted) — nothing to repaint, not a failure. Zero-alloc: walks the existing per-id node list.</summary>
    public int AddImageNodeRepaint(int imageId, ref RepaintDamageRegion region)
    {
        if (imageId == 0 || !_imageNodes.TryGetValue(imageId, out var list)) return 0;
        int added = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var node = list[i];
            if (!_scene.IsLive(node)) { list.RemoveAt(i); continue; }
            if (!_scene.TryAbsoluteRectTranslationOnly(node, out var r)) return -1;
            region.Add(new RectF(r.X - SceneRecordingContext.RepaintAaPadDip, r.Y - SceneRecordingContext.RepaintAaPadDip,
                                  r.W + 2 * SceneRecordingContext.RepaintAaPadDip, r.H + 2 * SceneRecordingContext.RepaintAaPadDip));
            added++;
        }
        if (list.Count == 0) _imageNodes.Remove(imageId);
        return added;
    }

    private bool IsReachableFromRoot(NodeHandle node)
    {
        for (var n = node; !n.IsNull && _scene.IsLive(n); n = _scene.Parent(n))
            if (n == _scene.Root) return true;
        return false;
    }

    /// <summary>Wire every BOUND channel of a freshly mounted node: one <see cref="BindEffect{T}"/> per bound
    /// <c>Prop&lt;T&gt;</c>, run once now (the mount write). Each body reads its source via <c>fx.Read()</c> and its static
    /// companions via <c>fx.El</c> — never mount-captured locals — so <see cref="RewireBinds"/> can re-point the SAME
    /// effect when a re-render binds the channel with a new thunk/signal (bound→bound re-wire; Reconciler.Rewire.cs). The
    /// selector passed to each effect is a static lambda (cached, allocation-free) that reads the same channel off a
    /// re-rendered element. Mount-only here: <c>OnRealized</c> (fires once per node).</summary>
    private void BindNode(NodeHandle node, Element el)
    {
        BindPresence(node, el);   // P1: Element.Visible lives on the base type — wire it for EVERY element kind
        if (el is BoxEl b)
        {
            if (b.Transform.IsBound)
            {
                var fx = new BindEffect<Affine2D>(Runtime, b, static e => e is BoxEl x ? x.Transform : default);
                // Value-gate: an unchanged matrix must NOT set TransformDirty — that bit alone defeats skip-submit
                // (AppHost maybeUnchanged requires !transformWrote). Quantized EQ/seek land here often with equal values.
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    Affine2D next = fx.Read();                // read FIRST: the read is what keeps the effect subscribed
                    if (_scene.IsFollowing(node)) return;   // F169: the host's follow pass owns the translation this frame
                    ref NodePaint paint = ref _scene.Paint(node);
                    if (paint.LocalTransform == next) return;
                    paint.LocalTransform = next;
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                }));
            }
            if (b.Opacity.IsBound)
            {
                var fx = new BindEffect<float>(Runtime, b, static e => e is BoxEl x ? x.Opacity : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    float next = fx.Read();
                    ref NodePaint paint = ref _scene.Paint(node);
                    if (paint.Opacity == next) return;
                    paint.Opacity = next;
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }));
            }
            if (b.HitTestVisible.IsBound)
            {
                // E15: a resolved false clears NodeFlags.HitTestVisible without a re-render — hit-testing reads the
                // flag straight off the live scene, so the flip needs no PaintDirty/re-record/re-publish at all
                // (gate.hit.bindable: FrameStats.Rendered stays false across the flip).
                var fx = new BindEffect<bool>(Runtime, b, static e => e is BoxEl x ? x.HitTestVisible : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    bool next = fx.Read();
                    bool cur = (_scene.Flags(node) & NodeFlags.HitTestVisible) != 0;
                    if (cur == next) return;
                    if (next) _scene.Mark(node, NodeFlags.HitTestVisible); else _scene.Unmark(node, NodeFlags.HitTestVisible);
                    NodeBindingWriteCount++;
                }));
            }
            if (b.Fill.IsBound)
            {
                var fx = new BindEffect<ColorF>(Runtime, b, static e => e is BoxEl x ? x.Fill : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    ColorF next = fx.Read();
                    ref NodePaint paint = ref _scene.Paint(node);
                    if (paint.Fill == next) return;
                    paint.Fill = next;
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }));
            }
            if (b.HoverFill.IsBound)
            {
                // Equality-gated (P3, same shape as Fill above): a re-fire whose resolved color did not move must not
                // mark PaintDirty, or NodeBindingWriteCount over-counts and an equal republish repaints for nothing.
                var fx = new BindEffect<ColorF>(Runtime, b, static e => e is BoxEl x ? x.HoverFill : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    ColorF next = fx.Read();
                    ref NodePaint paint = ref _scene.Paint(node);
                    if (paint.HoverFill == next) return;
                    paint.HoverFill = next;
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }));
            }
            if (b.PressedFill.IsBound)
            {
                var fx = new BindEffect<ColorF>(Runtime, b, static e => e is BoxEl x ? x.PressedFill : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    ColorF next = fx.Read();
                    ref NodePaint paint = ref _scene.Paint(node);
                    if (paint.PressedFill == next) return;
                    paint.PressedFill = next;
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }));
            }
            if (b.BorderColor.IsBound)
            {
                var fx = new BindEffect<ColorF>(Runtime, b, static e => e is BoxEl x ? x.BorderColor : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    ColorF next = fx.Read();
                    ref NodePaint paint = ref _scene.Paint(node);
                    if (paint.BorderColor == next) return;
                    paint.BorderColor = next;
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }));
            }
            if (b.Corners.IsBound)
            {
                var fx = new BindEffect<CornerRadius4>(Runtime, b, static e => e is BoxEl x ? x.Corners : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    var next = fx.Read();
                    ref NodePaint paint = ref _scene.Paint(node);
                    if (paint.Corners.Equals(next)) return;
                    paint.Corners = next;
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }));
            }
            if (b.RadialGradientCenter.IsBound)
            {
                var fx = new BindEffect<Point2>(Runtime, b, static e => e is BoxEl x ? x.RadialGradientCenter : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    Point2 center = fx.Read();
                    bool hadCenter = _scene.TryGetRadialGradientCenter(node, out Point2 prevCenter);
                    bool nextValid = float.IsFinite(center.X) && float.IsFinite(center.Y);
                    if (nextValid && hadCenter && prevCenter.X == center.X && prevCenter.Y == center.Y) return;
                    if (!nextValid && !hadCenter) return;
                    NodeBindingWriteCount++;
                    if (nextValid) _scene.SetRadialGradientCenter(node, center);
                    else _scene.ClearRadialGradientCenter(node);
                }));
            }
            if (b.FeedbackTransform.IsBound)
            {
                var fx = new BindEffect<Affine2D>(Runtime, b, static e => e is BoxEl x ? x.FeedbackTransform : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node) || !_scene.TryGetFeedback(node, out var st)) return;
                    Affine2D warp = fx.Read();
                    if (st.Warp.Equals(warp)) return;
                    NodeBindingWriteCount++;
                    _scene.SetFeedback(node, st with { Warp = warp });
                }));
            }
            if (b.FeedbackDecay.IsBound)
            {
                var fx = new BindEffect<float>(Runtime, b, static e => e is BoxEl x ? x.FeedbackDecay : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node) || !_scene.TryGetFeedback(node, out var st)) return;
                    float decay = fx.Read();
                    if (st.Decay.Equals(decay)) return;
                    NodeBindingWriteCount++;
                    _scene.SetFeedback(node, st with { Decay = decay });
                }));
            }
            if (b.GradientMix.IsBound)
            {
                var fx = new BindEffect<float>(Runtime, b, static e => e is BoxEl x ? x.GradientMix : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    float mix = fx.Read();
                    float prev = _scene.TryGetGradientMix(node, out float m) ? m : 0f;
                    float next = float.IsFinite(mix) ? Math.Clamp(mix, 0f, 1f) : 0f;
                    if (prev == next) return;
                    NodeBindingWriteCount++;
                    _scene.SetGradientMix(node, next);
                }));
            }
            if (b.Validation.IsBound)
            {
                // form-validation.md: resolve the semantic state → theme critical color on the UI thread (the recorder
                // stays theme-agnostic), and write the resolved border equality-gated so an unchanged validity marks NO
                // PaintDirty (Memo.OnStale re-runs this effect each keystroke, but a no-op validity dirties nothing).
                var fx = new BindEffect<ValidationState>(Runtime, b, static e => e is BoxEl x ? x.Validation : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    ValidationState st = fx.Read();
                    ColorF col = st == ValidationState.Error ? Tok.SystemFillCritical : default;
                    ref var paint = ref _scene.Paint(node);
                    if (paint.ValidationBorder == col) return;
                    paint.ValidationBorder = col;
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }));
            }
            // Width/Height write the LAYOUT column, so an ungated re-fire is the most expensive no-op in the engine: a
            // signal that ticks every frame (a clock, a scroll offset a size is derived from) marked LayoutDirty even when
            // it re-wrote the SAME number, and with no boundary above that mark it escalates into a whole-window solve.
            // Equality-gate the mark exactly like the Text/Validation bindings above. float.Equals — not == — so NaN
            // (auto) compares equal to NaN and an auto→auto re-fire is a true no-op. The FIRST run always writes+marks
            // (`primed`), keeping mount behaviour byte-identical to the ungated version.
            if (b.Width.IsBound)
            {
                var fx = new BindEffect<float>(Runtime, b, static e => e is BoxEl x ? x.Width : default) { WritesLayout = true };
                bool wPrimed = false;
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    float next = fx.Read();                   // read FIRST: the read is what keeps the effect subscribed
                    if (_scene.IsFollowing(node)) return;   // F169: the host's follow pass owns the size this frame
                    ref var li = ref _scene.Layout(node);
                    if (wPrimed && li.Width.Equals(next)) return;
                    wPrimed = true;
                    li.Width = next;
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.LayoutDirty);
                    RemirrorAncestors(node);   // a component root's bound size: its anchor reserves the mirrored copy
                    MarkParentLayoutDirty(node);   // a resized boundary must not relayout only itself
                }));
            }
            if (b.Height.IsBound)
            {
                var fx = new BindEffect<float>(Runtime, b, static e => e is BoxEl x ? x.Height : default) { WritesLayout = true };
                bool hPrimed = false;
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    float next = fx.Read();                   // read FIRST: the read is what keeps the effect subscribed
                    if (_scene.IsFollowing(node)) return;   // F169: the host's follow pass owns the size this frame
                    ref var li = ref _scene.Layout(node);
                    if (hPrimed && li.Height.Equals(next)) return;
                    hPrimed = true;
                    li.Height = next;
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.LayoutDirty);
                    RemirrorAncestors(node);
                    MarkParentLayoutDirty(node);   // a resized boundary must not relayout only itself
                }));
            }
            b.OnRealized?.Invoke(node);
        }
        else if (el is TextEl t)
        {
            if (t.Text.IsBound)
            {
                var fx = new BindEffect<string>(Runtime, t, static e => e is TextEl x ? x.Text : default) { WritesLayout = true };
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    var next = _strings.Intern(fx.Read());
                    ref var paint = ref _scene.Paint(node);
                    if (paint.Text == next) return;
                    SetPaintText(ref paint, next);
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.LayoutDirty);
                }));
            }
            if (t.Color.IsBound)
            {
                var fx = new BindEffect<ColorF>(Runtime, t, static e => e is TextEl x ? x.Color : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    ColorF next = fx.Read();
                    ref var paint = ref _scene.Paint(node);
                    if (paint.TextColor == next) return;
                    paint.TextColor = next;
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }));
            }
            t.OnRealized?.Invoke(node);
        }
        else if (el is SpanTextEl st)
        {
            BindSpanText(node, st);   // Reconciler.Spans.cs — bound Spans : Prop<TextSpans> only (P2)
        }
        else if (el is ImageEl ime)
        {
            if (ime.Source.IsBound)
            {
                var fx = new BindEffect<string>(Runtime, ime, static e => e is ImageEl x ? x.Source : default);
                // W2-E3: the slot this ImageEl belongs to, captured at bind time (both null outside a bound realize
                // pass). The first fire (runNow, inside the cold mount) sees the live realize context; every later fire
                // — a RebindBoundSlot recycle draining at the host's post-realize Flush — classifies the slot's CURRENT
                // index against the viewport's latest visible band. Two extra captured locals on a closure that already
                // exists once per bound node; nothing further allocates on a rebind.
                var slotEntry = _realizeEntry; var slotSig = _realizeSlotSignal;
                // ImageLatencyCensus.SourceWait: when this node's source went empty (a row mounted or rebound before its
                // data landed) and the source it last showed. Two captured locals on the closure that exists anyway.
                long emptySince = 0; string? lastSrc = null;
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    string src = fx.Read();
                    // Static companions come from the element this node was LAST reconciled against (fx.El), not the
                    // mount element: a re-render that changed the extent / BlurHash / reveal / mask / overlay is honoured
                    // by a re-wire and every later fire alike, and a re-wire's re-run never re-applies stale mount values
                    // over what WriteColumns just wrote. The decode target is recomputed per fire (pure, allocation-free).
                    var im = (ImageEl)fx.El;
                    (int dW, int dH) = ImageDecodeTarget(in im, _scene.DeviceScale);
                    if (Images is not null && !ReferenceEquals(src, lastSrc))
                    {
                        if (src.Length == 0) { if (emptySince == 0) emptySince = System.Diagnostics.Stopwatch.GetTimestamp(); }
                        else if (emptySince != 0) { Images.Latency.NoteSourceWait(emptySince); emptySince = 0; }
                        else if (src != lastSrc) Images.Latency.NoteSourceImmediate();
                        lastSrc = src;
                    }
                    ImagePriority prio = slotSig is not null && !ReferenceEquals(_realizeEntry, slotEntry)
                        ? ImagePriorityFor(slotEntry, slotSig.Peek())
                        : ImageRequestPriority(node);
                    int newId = Images is not null && src.Length > 0
                        ? Images.Request(src, dW, dH, prio, im.BlurHash, im.RevealTransition).Id : 0;
                    NodeBindingWriteCount++;
                    ref var paint = ref _scene.Paint(node);
                    int oldDerived = _scene.TryGetImageEffects(node, out var oldEffects) ? oldEffects.DerivedImageId : 0;
                    int newDerived = RequestBakedImage(in im, newId, dW, dH);
                    SwapImageId(node, ref paint, newId, prio);
                    if (newDerived != oldDerived)
                    {
                        UnpinImageNode(node, oldDerived);
                        if (newDerived != 0) PinImageNode(node, newDerived);
                    }
                    WriteImageEffects(node, in im, newDerived);
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }));
            }
            if (ime.Placeholder.IsBound)
            {
                var fx = new BindEffect<ColorF>(Runtime, ime, static e => e is ImageEl x ? x.Placeholder : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    ColorF next = fx.Read();
                    ref var paint = ref _scene.Paint(node);
                    if (paint.Fill == next) return;
                    paint.Fill = next;
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }));
            }
        }
        else if (el is IconLayerEl ile)
        {
            // The layer TINT rides NodePaint.Fill (VisualKind.IconLayer). A bound Tint (the ThemedIcon role thunk reads
            // Tok) re-fires on RethemeAll → repaints the node with the new ColorF, NO re-raster (the mask is colorless).
            if (ile.Tint.IsBound)
            {
                var fx = new BindEffect<ColorF>(Runtime, ile, static e => e is IconLayerEl x ? x.Tint : default);
                AddBinding(node, fx.Start(() =>
                {
                    NodeBindingFireCount++;
                    if (!_scene.IsLive(node)) return;
                    ColorF next = fx.Read();
                    ref var paint = ref _scene.Paint(node);
                    if (paint.Fill == next) return;
                    paint.Fill = next;
                    NodeBindingWriteCount++;
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }));
            }
        }
        else if (el is PathEl pe)
        {
            // Mirrors BoxEl.OnRealized above (:1972) — BindNode runs at mount only (Mount(), :623), so this fires
            // exactly once, handing the caller the live NodeHandle a draw-on stroke-trim/transform Keyframes loop needs
            // to target (PathEl carries no bindable channels of its own today, so this is BindNode's only PathEl work).
            pe.OnRealized?.Invoke(node);
        }
        else if (el is SeriesEl se)
        {
            BindSeriesSamples(node, se);   // Reconciler.Series.cs — the bound sample-source channel
        }
        else if (el is SpriteFieldEl sf)
        {
            BindSprites(node, sf);   // Reconciler.Sprites.cs — the bound instance-buffer channel
        }
        else if (el is ListRowEl lr)
        {
            BindListRowCells(node, lr);   // Reconciler.ListRow.cs — Cells/Fill/HoverFill/SelectedFill/Placeholder
        }
    }

    // Decode-target PHYSICAL px for an image: explicit Width/Height (DIPs) × the device scale — decoding at the DIP extent
    // under-decodes by 1/scale at 125-200 % and the cover is then upscaled on the GPU (soft), so the target is exactly the
    // pixels the box paints (ceil, never smaller); otherwise the DecodePx hint (already physical px — a fluid/aspect image's
    // real box size isn't known until layout, so the caller scales it), deriving the missing cross extent from AspectRatio.
    // 0 ⇒ source resolution.
    internal static (int W, int H) ImageDecodeTarget(in ImageEl im, float scale = 1f)
    {
        if (!(scale > 0f) || !float.IsFinite(scale)) scale = 1f;
        int hint = !float.IsNaN(im.DecodePx) ? (int)im.DecodePx : 0;
        int w = !float.IsNaN(im.Width) ? (int)MathF.Ceiling(im.Width * scale - 0.001f) : hint;
        int h;
        if (!float.IsNaN(im.Height)) h = (int)MathF.Ceiling(im.Height * scale - 0.001f);
        else if (!float.IsNaN(im.AspectRatio) && im.AspectRatio > 0f && w > 0) h = (int)MathF.Round(w / im.AspectRatio);
        else h = hint;
        return (w, h);
    }

    /// <summary>Pure re-target of an explicit-extent image for a new device scale: Width (DIPs) x scale, Height likewise or
    /// derived from the aspect ratio, else the height it already had (a hint-driven height cannot be re-derived here).
    /// A fluid image (<paramref name="width"/> NaN) is the caller's physical-px hint: never re-targeted (returns false).</summary>
    internal static bool TryRetargetDecode(float width, float height, float aspect, int oldH, float scale, out int w, out int h)
    {
        w = h = 0;
        if (float.IsNaN(width) || !(scale > 0f) || !float.IsFinite(scale)) return false;
        w = (int)MathF.Ceiling(width * scale - 0.001f);
        if (!float.IsNaN(height)) h = (int)MathF.Ceiling(height * scale - 0.001f);
        else if (aspect > 0f && w > 0) h = (int)MathF.Round(w / aspect);
        else h = oldH;
        return true;
    }

    /// <summary>The device scale changed (monitor move, OS scale change): re-request every live explicit-extent image at its new
    /// physical decode size. A reused node whose props did not change never re-runs its column write (the RecordChanged gate), so
    /// nothing else would: moving to a denser display kept the 1x handles (soft covers), moving back kept 4x bytes. A node with a
    /// baked-blur derivative keeps its decode (the derivative is keyed on it). UI thread; rare event, O(nodes).</summary>
    internal void RetargetImagesForScale(float scale)
    {
        if (Images is null) return;
        int count = _scene.RecordingNodeCount;
        for (int i = 0; i < count; i++)
        {
            var node = _scene.HandleAt(i);
            if (node.IsNull || !_scene.IsLive(node)) continue;
            ref NodePaint paint = ref _scene.Paint(node);
            if (paint.VisualKind != VisualKind.Image || paint.ImageId == 0) continue;
            if (_scene.TryGetImageEffects(node, out var fx) && fx.DerivedImageId != 0) continue;
            if (!Images.TryGetTarget(new ImageHandle(paint.ImageId), out string src, out _, out int oldH)) continue;
            ref LayoutInput li = ref _scene.Layout(node);
            if (!TryRetargetDecode(li.Width, li.Height, li.AspectRatio, oldH, scale, out int w, out int h)) continue;
            ImagePriority prio = ImageRequestPriority(node);
            int newId = Images.Request(src, w, h, prio).Id;
            SwapImageId(node, ref paint, newId, prio);
        }
    }

    private int RequestBakedImage(in ImageEl im, int sourceId, int decodeW, int decodeH)
    {
        if (Images is null || sourceId == 0 || im.BakedBlur is not { } baked || baked.IsNone) return 0;
        return Images.RequestBakedBlur(new ImageHandle(sourceId), decodeW, decodeH, in baked, im.RevealTransition).Id;
    }

    private void WriteImageEffects(NodeHandle node, in ImageEl im, int derivedId)
    {
        ImageMaskSpec mask = im.Mask is { } m && !m.IsNone ? m : default;
        // The row is rewritten wholesale from the element, so a swap crossfade in flight is re-applied from _imageSwaps.
        bool swapping = _imageSwaps.TryGetValue((int)node.Raw.Index, out var swap);
        if (derivedId != 0 || im.ColorOverlay.A > 0f || !mask.IsNone || im.Saturation != 1f || swapping)
        {
            var fx = new ImageVisualEffects(derivedId, im.ColorOverlay, mask, im.Saturation);
            if (swapping) fx = fx with { SwapOutgoingId = swap.OutgoingId, SwapStartMs = swap.StartMs, SwapMs = swap.DurationMs, SwapCut = swap.Cut };
            _scene.SetImageEffects(node, fx);
        }
        else
            _scene.ClearImageEffects(node);
    }

    // ── Scroll / Virtualization (unchanged behavior) ────────────────────────────────────────────

    private void MountScroll(NodeHandle node, ScrollEl se)
    {
        WriteColumns(node, se, isMount: true);
        // WriteColumns skips a BOUND Visible (BindPresence owns it) — wire it here, Mount's WriteColumns-then-BindNode pair.
        BindNode(node, se);
        var content = _scene.CreateNode(se.Content.ElementTypeId);
        _scene.AppendChild(node, content);
        Mount(content, se.Content);
        _scene.ScrollRef(node).ContentNode = content;
        se.OnRealized?.Invoke(node);
    }

    /// <summary>Provide this viewport's bound <see cref="FluentGpu.Scroll.Runtime.ScrollHandle"/> on
    /// <see cref="FluentGpu.Hooks.ScrollCtx.Nearest"/> to its content (a descendant's <c>UseScroll()</c> resolves the
    /// nearest scroller through it). Re-asserted on every patch so an authored handle taking over re-publishes; the
    /// provider signal is REUSED (a consumer's parked-cache fallback resolves it by reference) and written only when the
    /// handle identity actually changed. Removed with the node (<c>_providerSig</c> teardown in unmount).</summary>
    private void ProvideScrollCtx(NodeHandle node)
    {
        var handle = _scene.ScrollHandleFor(node);
        int idx = (int)node.Raw.Index;
        if (_providerSig.TryGetValue(idx, out var existing) && ReferenceEquals(existing.Channel, FluentGpu.Hooks.ScrollCtx.Nearest))
        {
            if (!ReferenceEquals(existing.Sig.Peek(), handle)) existing.Sig.Value = handle;
        }
        else _providerSig[idx] = (FluentGpu.Hooks.ScrollCtx.Nearest, new Signal<object?>(handle));
    }

    /// <summary>Stamp the viewport's <see cref="ScrollState.ScrollKey"/> and tell the host about the identity edge
    /// (mount: <c>old = null</c>; a content swap on a reused node: <c>old → new</c>) so it can save/restore through the
    /// viewport's <c>ScrollHandle</c> (scroll rework §9).</summary>
    private void ApplyScrollKey(NodeHandle node, ref ScrollState sc, string? newKey, bool isMount)
    {
        if (isMount)
        {
            sc.ScrollKey = newKey;
            if (newKey is not null) ScrollKeyChanged?.Invoke(node, null, newKey);
            return;
        }
        if (newKey == sc.ScrollKey) return;   // not a content-identity change → leave the live offset untouched
        string? old = sc.ScrollKey;
        sc.ScrollKey = newKey;
        ScrollKeyChanged?.Invoke(node, old, newKey);
    }

    /// <summary>Persist the offsets of every viewport in a subtree that is ABOUT to be removed, before anything new
    /// mounts (<see cref="ReconcileChildren"/> mounts new keyed children before removing the old ones — a same-key
    /// swap must read the position the user was looking at). Allocation-free recursion.</summary>
    private void PreSaveScroll(NodeHandle node)
    {
        if (_scene.HasScroll(node)) SaveScrollPosition?.Invoke(node);
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c)) PreSaveScroll(c);
    }

    /// <summary>While a <see cref="SkelRegionEl"/> is loading, hide its enclosing viewport's scrollbar (the nearest scroll
    /// ancestor): the short skeleton → tall real-content swap would otherwise pop the rail. Claims are counted and
    /// node-owned because a pending region can unmount without ever reconciling a Ready/Failed branch.</summary>
    private void SetSkeletonScrollbarSuppression(NodeHandle node, bool loading)
    {
        int owner = (int)node.Raw.Index;
        if (!loading)
        {
            ReleaseSkeletonScrollbarSuppression(owner);
            return;
        }

        NodeHandle viewport = NodeHandle.Null;
        for (var n = _scene.Parent(node); !n.IsNull; n = _scene.Parent(n))
            if (_scene.HasScroll(n)) { viewport = n; break; }
        if (viewport.IsNull) { ReleaseSkeletonScrollbarSuppression(owner); return; }

        // A VIRTUALIZED viewport is never suppressed. `ScrollState.ItemCount > 0` is the engine-wide marker for one
        // (Columns.cs: "ItemCount == 0 ⇒ a plain ScrollView, non-virtual"; FlexLayout's measure/arrange virtual split and
        // SkeletonRegion.FindVirtualRows read exactly the same field). Two reasons. (1) It buys nothing: a virtual
        // viewport's scroll extent comes from its measured-extent table, which already absorbs a short→tall swap inside
        // one row, so the rail does not pop. (2) It actively flashes: a Skel.Region used PER ITEM puts N regions under
        // the SAME viewport — the list's own — and they all claim and release it, so the suppressor count toggles 0↔N
        // every flush and the scroll chrome blinks with the rows. Release first rather than plain-return: a region whose
        // ancestor chain changed (rare reparent), or whose enclosing list only became virtual after the claim, must drop
        // the claim it already holds. A region that never claimed has no entry, so the Remove is a no-op and a
        // never-claimed region can never decrement — claims stay balanced (the unmount sink calls the same release).
        // Read through TryGetScroll, not ScrollRef: ScrollRef is get-or-create and notes a capture change on every call,
        // which a pure predicate has no business doing.
        if (_scene.TryGetScroll(viewport, out var vpScroll) && vpScroll.ItemCount > 0)
        {
            ReleaseSkeletonScrollbarSuppression(owner);
            return;
        }

        if (_skelScrollSuppression.TryGetValue(owner, out var prior))
        {
            if (prior == viewport && _scene.IsLive(prior)) return;   // this region already owns one claim
            ReleaseSkeletonScrollbarSuppression(owner);             // rare reparent: move the claim atomically
        }

        _skelScrollSuppression[owner] = viewport;
        ref ScrollState sc = ref _scene.ScrollRef(viewport);
        sc.LoadingBarSuppressors++;
        _scene.Mark(viewport, NodeFlags.PaintDirty);
    }

    private void ReleaseSkeletonScrollbarSuppression(int owner)
    {
        if (!_skelScrollSuppression.Remove(owner, out var viewport) || !_scene.IsLive(viewport) || !_scene.HasScroll(viewport)) return;
        ref ScrollState sc = ref _scene.ScrollRef(viewport);
        if (sc.LoadingBarSuppressors > 0) sc.LoadingBarSuppressors--;
        _scene.Mark(viewport, NodeFlags.PaintDirty);
    }

    private void MountVirtual(NodeHandle node, VirtualListEl ve)
    {
        WriteColumns(node, ve, isMount: true);
        BindNode(node, ve);   // bound Visible (see MountScroll)
        var content = _scene.CreateNode(1);
        _scene.AppendChild(node, content);
        _scene.ScrollRef(node).ContentNode = content;
        _virtuals[node] = new VirtualEntry { El = ve };
        RealizeWindow(node, ve, mount: true);   // E4: mount realizes the VISIBLE band only; overscan trickles via the budget
        ve.OnRealized?.Invoke(node);   // E11: viewport-handle escape hatch (ItemsView StartBringItemIntoView / sticky pinning)
    }

    /// <summary>Builds (or refreshes) the viewport's main-axis <see cref="IExtentSource"/> (scroll rework §6): a
    /// <see cref="FixedExtent"/> for a uniform stack (exact at any depth), a <see cref="MeasuredExtent"/> for the
    /// estimate-then-correct list without a pluggable layout, and a <see cref="VirtualLayoutExtent"/> adapter over any
    /// other <see cref="IVirtualLayout"/> (grids, fill-row shelves, grouped lists).</summary>
    internal static IExtentSource EnsureExtentSource(ref ScrollState sc, VirtualListEl ve, int count, float cross)
    {
        bool horizontal = ve.Horizontal;
        switch (ve.ItemLayout)
        {
            case null:
                if (sc.Extent is MeasuredExtent me)
                {
                    if (me.Count != count) me.Resize(count);
                    return me;
                }
                return sc.Extent = new MeasuredExtent(count, ve.EstimatedExtent > 0f ? ve.EstimatedExtent : 48.0);
            case StackVirtualLayout stack:
                if (sc.Extent is FixedExtent fe && fe.Stride == stack.Extent)
                {
                    if (fe.Count != count) fe.Resize(count);
                    return fe;
                }
                return sc.Extent = new FixedExtent(count, stack.Extent);
            default:
                if (sc.Extent is VirtualLayoutExtent vle && ReferenceEquals(vle.Layout, ve.ItemLayout) && vle.Horizontal == horizontal)
                {
                    if (vle.Count != count) vle.Resize(count);
                    if (cross > 0f) vle.Cross = cross;
                    return vle;
                }
                return sc.Extent = new VirtualLayoutExtent(ve.ItemLayout, count, cross > 0f ? cross : 0f, horizontal);
        }
    }

    /// <summary>Realize EVERY row the virtualizer's present-time window covers (scroll rework §6): the window is
    /// <see cref="Virtualizer.Plan"/> over the viewport's extent source at the plan's displayed offset/velocity
    /// (velocity-sized overscan ahead, a fixed floor behind); rows are widened to whole grid rows; there is no ramp,
    /// budget, cap or deferral — a frame that runs this leaves the window fully covered.</summary>
    private void RealizeWindow(NodeHandle node, VirtualListEl ve, bool reuseOverlap = false, bool mount = false)
    {
        _ = mount;
        if (!_virtuals.TryGetValue(node, out var entry)) { entry = new VirtualEntry(); _virtuals[node] = entry; }
        entry.El = ve;
        _scene.TryGetScroll(node, out var sc);
        var content = sc.ContentNode;
        if (content.IsNull) return;
        int prevFirstR = sc.FirstRealized, prevLastR = sc.LastRealized;   // window-change (progress) detection

        bool horizontal = ve.Horizontal;
        double offset = sc.Offset;
        double velocity = sc.Velocity;
        float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        if (viewport <= 0f) viewport = horizontal ? Hint(ve.Width) : Hint(ve.Height);
        int count = Math.Max(0, ve.ItemCount);
        // Content cross first: the arrange paths window/measure the layout at the padding-subtracted inner cross
        // (published as ContentW/H on the cross axis).
        float cross = horizontal ? (sc.ContentH > 0f ? sc.ContentH : sc.ViewportH > 0f ? sc.ViewportH : Hint(ve.Height))
                                 : (sc.ContentW > 0f ? sc.ContentW : sc.ViewportW > 0f ? sc.ViewportW : Hint(ve.Width));
        if (ve.ItemLayout is IViewportVirtualLayout vvl) vvl.SetViewport(viewport, cross);

        ref ScrollState scEnsure = ref _scene.ScrollRef(node);
        IExtentSource ext = EnsureExtentSource(ref scEnsure, ve, count, cross);
        // A restore (ScrollKey memory, a ScrollTo posted before mount) still pending resolves HERE against the extent
        // source — before this pass picks its window — so the first realized window is the restored one.
        if (_scene.ScrollHandleFor(node) is { RestorePending: true } pending)
        {
            pending.SetExtent(ext.Total, viewport);
            if (!pending.RestorePending)
            {
                offset = pending.OffsetNow;
                velocity = 0.0;
                scEnsure.Offset = offset;
                scEnsure.Velocity = 0.0;
            }
        }
        // An out-of-band extent rewrite since the last pass (a wholesale reseed — IAnchoredReseedLayout) moved every row
        // above the anchor with no plan shift: anchor it HERE, before this pass picks its window, so the window is planned
        // in the rewritten coordinates at the offset that keeps the anchor row where the user sees it (FlexLayout's pass 0
        // takes a reseed that lands after the realize).
        if (ve.ItemLayout is IAnchoredReseedLayout reseeded
            && reseeded.TakeReseedShift(sc.AnchorIndex, out double reseedDelta) && reseedDelta != 0.0)
        {
            offset += reseedDelta;
            scEnsure.Offset = offset;
            _scene.ScrollHandleFor(node)?.ShiftFrame(reseedDelta);
        }
        var feel = ScrollTunables.Current;
        var rw = Virtualizer.Plan(ext, offset, velocity, viewport, in feel, sc.AnchorIndex);

        int first, last;   // [first, last) exclusive
        if (rw.IsEmpty) { first = 0; last = 0; }
        else
        {
            first = rw.First;
            last = rw.Last + 1;
            if (ext is VirtualLayoutExtent grid)
            {
                first = grid.RowStart(first);
                last = grid.RowEnd(Math.Min(last, count) - 1);
            }
        }
        // MeasureAll: the realized window is the whole item range — every row is laid out, so every extent is measured
        // (the visible band below stays the real one: image priority and the lifecycle's visible range are unchanged).
        if (ve.MeasureAll && count > 0) { first = 0; last = count; }
        int visibleFirst = count == 0 ? 0 : ext.IndexAt(offset);
        int visibleLast = count == 0 ? 0 : Math.Min(count, ext.IndexAt(offset + viewport) + 1);
        visibleFirst = Math.Clamp(visibleFirst, 0, count);
        visibleLast = Math.Clamp(visibleLast, visibleFirst, count);
        first = Math.Clamp(first, 0, count);
        last = Math.Clamp(last, first, count);

        // A persistent prefix is covered by retained children, not by the recyclable interval. Trim every normal range
        // to begin after it; layout/window coverage treats the two bands as [0,prefix) U [FirstRealized,LastRealized).
        int prefix = ve.RowBind is null ? 0 : Math.Clamp(ve.PersistentPrefixCount, 0, count);
        if (prefix > 0)
        {
            visibleFirst = Math.Max(visibleFirst, prefix); visibleLast = Math.Max(visibleLast, prefix);
            first = Math.Max(first, prefix); last = Math.Max(last, prefix);
        }

        bool boundList = ve.RowBind is not null;
        int poolCapacity = boundList ? (entry.Slots?.Count ?? 0) + (entry.Spare?.Count ?? 0) : 0;
        bool poolOversize = boundList && count < poolCapacity;
        int w = last - first;
        int visibleSlots = Math.Clamp(visibleLast - first, 0, w);

        // ── W2-E3: publish this pass's visible band and push the image-priority context ──────────────────────────────
        int oldVisFirst = entry.VisibleFirst, oldVisLast = entry.VisibleLast;
        entry.VisibleFirst = visibleFirst; entry.VisibleLast = visibleLast;
        var outerEntry = _realizeEntry; int outerSlot = _realizeSlotIndex; var outerSig = _realizeSlotSignal; bool outerOverscan = _realizeOuterOverscan;
        _realizeOuterOverscan = outerOverscan || (outerEntry is not null && ImagePriorityFor(outerEntry, outerSlot) == ImagePriority.Overscan);
        _realizeEntry = entry; _realizeSlotIndex = -1; _realizeSlotSignal = null;
        try
        {
        if (ve.RowBind is not null)
        {
            // P3 (virtualization.md §5.5): every bound realize pass runs under SuppressBoundTransitions so a bound-channel
            // transition seed or the P1 Visible Enter seed snaps instead of animating.
            using var _suppressBoundTx = PushSuppressBoundTransitions();
            if (prefix > 0 || entry.PrefixSlots is { Count: > 0 })
                RealizeBoundWindowWithPersistentPrefix(node, content, entry, ve, prefix, first, last, w, visibleSlots);
            else if (ve.KeepAlive is not null || ve.ContentType is not null)
                RealizeBoundWindowExtended(node, content, entry, ve, first, last, w, visibleSlots);
            else
                RealizeBoundWindow(node, content, entry, ve, first, last, w, visibleSlots);
            // Pool trim: ItemCount can no longer use the whole pool — the pool ends at exactly ItemCount slots.
            if (poolOversize)
                FreeSpareSlots(entry, keep: count - (entry.Slots?.Count ?? 0));
        }
        else
        {
            int oldFirst = entry.PrevFirst, oldLast = entry.PrevFirst + entry.PrevLen;   // E11 lifecycle window delta

            var prev = reuseOverlap ? entry.Prev : null;
            int prevFirst = entry.PrevFirst;
            var cur = ArrayPool<Element>.Shared.Rent(Math.Max(1, w));
            for (int i = 0; i < w; i++)
            {
                int idx = first + i;
                int oldSlot = idx - prevFirst;
                Element? el = prev is not null && (uint)oldSlot < (uint)entry.PrevLen ? prev[oldSlot] : null;
                cur[i] = el ?? ve.RenderItem(idx);   // overlap reuses the element OBJECT — no keys, no `with` clone
            }

            ReconcileWindow(content, cur.AsSpan(0, w),
                entry.Prev is null ? default : entry.Prev.AsSpan(0, entry.PrevLen), first - prevFirst, first);

            if (entry.Prev is not null) { Array.Clear(entry.Prev, 0, entry.PrevLen); ArrayPool<Element>.Shared.Return(entry.Prev); }
            entry.Prev = cur; entry.PrevLen = w; entry.PrevFirst = first;

            ref ScrollState scw = ref _scene.ScrollRef(node);
            scw.FirstRealized = first; scw.LastRealized = last;
            _scene.Unmark(node, NodeFlags.VirtualRangeDirty);

            FireWindowLifecycle(ve, oldFirst, oldLast, first, last);   // E11: Prepared/Clearing/VisibleRange (cold realize edge)
        }

        // W2-E3: rows that were realized in the halo under the OLD visible band and now sit inside the NEW one carry
        // Overscan-lane decodes — move those to the Visible lane (a no-op for anything already settled or Visible).
        PromoteNewlyVisibleRows(entry, content, prevFirstR, prevLastR, oldVisFirst, oldVisLast);
        }
        finally
        {
            _realizeEntry = outerEntry; _realizeSlotIndex = outerSlot; _realizeSlotSignal = outerSig; _realizeOuterOverscan = outerOverscan;
        }

        // Publish the window's coverage and its arrange origin (layout refreshes the offsets after measured corrections).
        ref ScrollState scc = ref _scene.ScrollRef(node);
        scc.AnchorIndex = rw.IsEmpty ? 0 : rw.AnchorIndex;
        scc.WindowOriginIndex = Virtualizer.ArrangeOriginIndex(ext, scc.WindowOriginIndex, scc.FirstRealized, scc.LastRealized);
        scc.WindowOrigin = ext.OffsetOf(scc.WindowOriginIndex);
        ScrollContentPose.CoverageOf(ext, scc.PersistentPrefixCount, scc.FirstRealized, scc.LastRealized, out scc.CoverStart, out scc.CoverEnd);

        // Progress = the realized window of this viewport actually changed (drives the AppHost 2-pass loops).
        if (scc.FirstRealized != prevFirstR || scc.LastRealized != prevLastR) _realizeProgress = true;
    }

    /// <summary>E11 lifecycle: Clearing for indices that left [oldFirst,oldLast), Prepared for indices that entered
    /// [newFirst,newLast) (a recycled row = Clearing(old) + Prepared(new) — the WinUI ItemsRepeater recycle order),
    /// plus the visible-range prefetch hook. Fires only when the window actually moved (steady transform-only scroll
    /// frames never reach here), so null callbacks cost nothing.</summary>
    private static void FireWindowLifecycle(VirtualListEl ve, int oldFirst, int oldLast, int newFirst, int newLast)
    {
        if (oldFirst == newFirst && oldLast == newLast) return;
        if (ve.OnItemClearing is { } clearing)
            for (int i = oldFirst; i < oldLast; i++)
                if (i < newFirst || i >= newLast) clearing(i);
        if (ve.OnItemPrepared is { } prepared)
            for (int i = newFirst; i < newLast; i++)
                if (i < oldFirst || i >= oldLast) prepared(i);
        ve.OnVisibleRange?.Invoke(newFirst, newLast);
    }

    private static float Hint(float explicitSize) => float.IsNaN(explicitSize) ? 1024f : explicitSize;

    /// <summary>Retain <paramref name="prefixCount"/> fixed-index bound slots ahead of the ordinary recyclable window.
    /// Prefix nodes are detached only for the duration of realization so the existing positional/extended recyclers can
    /// remain unchanged; they are restored as the leading direct content children before layout/paint.</summary>
    private void RealizeBoundWindowWithPersistentPrefix(NodeHandle node, NodeHandle content, VirtualEntry entry,
        VirtualListEl ve, int prefixCount, int first, int last, int w, int visibleSlots)
    {
        var prefix = entry.PrefixSlots ??= new List<BoundSlot>(prefixCount);
        bool prefixChanged = prefix.Count != prefixCount;

        // Remove the retained band from the child chain while the normal recycler walks children by ordinal.
        for (int i = 0; i < prefix.Count; i++)
            if (_scene.IsLive(prefix[i].Root) && !_scene.Parent(prefix[i].Root).IsNull)
                _scene.Detach(prefix[i].Root);

        while (prefix.Count > prefixCount)
        {
            int i = prefix.Count - 1;
            var slot = prefix[i];
            ve.OnItemClearing?.Invoke(i);
            if (_scene.IsLive(slot.Root)) Remove(slot.Root);
            prefix.RemoveAt(i);
        }
        while (prefix.Count < prefixCount)
        {
            int index = prefix.Count;
            var sig = new Signal<int>(index);
            Element el = ve.RowBind!(sig);
            var root = _scene.CreateNode(el.ElementTypeId);
            _scene.AppendChild(content, root);   // mount under the viewport so scroll bindings resolve their container
            Mount(root, el);
            _scene.Detach(root);
            prefix.Add(new BoundSlot(sig, el, root));
            ve.OnItemPrepared?.Invoke(index);
        }

        // With the prefix detached, the existing paths see exactly their original child/slot invariant.
        if (ve.KeepAlive is not null || ve.ContentType is not null)
            RealizeBoundWindowExtended(node, content, entry, ve, first, last, w, visibleSlots);
        else
            RealizeBoundWindow(node, content, entry, ve, first, last, w, visibleSlots);

        // Existing normal roots currently occupy the content chain. Append the prefix, then rotate the normal band
        // behind it using only linked-list operations (no per-realize buffer/allocation).
        int normalCount = 0;
        for (var c = _scene.FirstChild(content); !c.IsNull; c = _scene.NextSibling(c)) normalCount++;
        for (int i = 0; i < prefix.Count; i++) _scene.AppendChild(content, prefix[i].Root);
        var normal = _scene.FirstChild(content);
        for (int i = 0; i < normalCount && !normal.IsNull; i++)
        {
            var next = _scene.NextSibling(normal);
            _scene.Detach(normal);
            _scene.AppendChild(content, normal);
            normal = next;
        }

        ref ScrollState sc = ref _scene.ScrollRef(node);
        sc.PersistentPrefixCount = prefixCount;
        if (prefixChanged)
        {
            MarkLayoutShape(content);
            _reconciled = true;
            _realizeProgress = true;
        }
    }

    /// <summary>
    /// Bound (signals-first) realize: a slot follows its LOGICAL ITEM while that item overlaps the next window. A
    /// one-row shift therefore rotates one leaving root to the entering edge and writes ONE index signal rather than
    /// positionally rebinding the whole realized window. Frozen element trees never rebuild on the homogeneous path;
    /// the host flushes recycled-slot bindings in the same frame.
    /// </summary>
    private void RealizeBoundWindow(NodeHandle node, NodeHandle content, VirtualEntry entry,
                                    VirtualListEl ve, int first, int last, int w, int visibleSlots)
    {
        _ = visibleSlots;
        var rowBind = ve.RowBind!;
        var slots = entry.Slots ??= new List<BoundSlot>(Math.Max(4, w));
        bool structural = false;
        bool orderChanged = false;
        int oldFirst = entry.PrevFirst, oldLast = entry.PrevFirst + entry.PrevLen;   // E11 lifecycle window delta

        int desiredCount = w;   // every covered row, this frame (scroll rework §6 — no ramp, no budget)
        bool fastContiguous = desiredCount == slots.Count
            && slots.Count == entry.PrevLen
            && BoundSlotsAreContiguous(content, slots, entry.PrevFirst);

        if (fastContiguous)
        {
            int count = slots.Count;
            int shift = first - entry.PrevFirst;
            if (count > 0 && shift > 0 && shift < count)
            {
                // Downward scroll: leading roots leave; append them in the same order, then rotate the slot metadata.
                for (int i = 0; i < shift; i++)
                {
                    var root = slots[i].Root;
                    _scene.Detach(root);
                    _scene.AppendChild(content, root);
                }
                RotateSlotsLeft(slots, count, shift);
                var span = CollectionsMarshal.AsSpan(slots);
                for (int i = count - shift; i < count; i++)
                    RebindBoundSlot(ref span[i], first + i, ve);
                orderChanged = true;
            }
            else if (count > 0 && shift < 0 && -shift < count)
            {
                // Reverse scroll: trailing roots enter at the front. Prepend in reverse so their logical order survives.
                int entering = -shift;
                for (int i = count - 1; i >= count - entering; i--)
                {
                    var root = slots[i].Root;
                    _scene.Detach(root);
                    _scene.PrependChild(content, root);
                }
                RotateSlotsRight(slots, count, entering);
                var span = CollectionsMarshal.AsSpan(slots);
                for (int i = 0; i < entering; i++)
                    RebindBoundSlot(ref span[i], first + i, ve);
                orderChanged = true;
            }
            else if (shift != 0)
            {
                // No overlap: keep the retained roots in place and bind the complete window to its new logical range.
                var span = CollectionsMarshal.AsSpan(slots);
                for (int i = 0; i < count; i++)
                    RebindBoundSlot(ref span[i], first + i, ve);
            }
        }
        else
        {
            // Cold grow/shrink and defensive invariant recovery: preserve every exact logical-index overlap first,
            // recycle only the unmatched roots, then reorder the child chain. Scratch storage is retained on the entry,
            // so recurring resize/recovery at the same high-water mark does not allocate.
            var scratch = entry.SlotScratch ??= new List<BoundSlot>(Math.Max(4, desiredCount));
            scratch.Clear();
            for (int i = 0; i < desiredCount; i++) scratch.Add(default);

            bool[] used = entry.SlotUsed ?? Array.Empty<bool>();
            if (used.Length < slots.Count)
                entry.SlotUsed = used = new bool[Math.Max(4, slots.Count)];
            else
                Array.Clear(used, 0, slots.Count);

            // Reserve exact overlaps before consuming any leaving slot, otherwise a leading gap could steal a later
            // logical survivor and turn a one-row repair into a full-window fan-out.
            for (int ord = 0; ord < desiredCount; ord++)
            {
                int idx = first + ord;
                for (int j = 0; j < slots.Count; j++)
                {
                    if (used[j] || slots[j].Index.Peek() != idx) continue;
                    scratch[ord] = slots[j];
                    used[j] = true;
                    break;
                }
                // A slot parked from this very row (the window flutter / reversal case) returns with zero writes.
                if (scratch[ord].Index is null && TryTakeSpareSlotExact(entry, content, idx, out var back))
                {
                    scratch[ord] = back;
                    structural = true;
                }
            }

            for (int ord = 0; ord < desiredCount; ord++)
            {
                if (scratch[ord].Index is not null) continue;
                int idx = first + ord;
                int reuse = -1;
                for (int j = 0; j < slots.Count; j++)
                    if (!used[j]) { reuse = j; break; }

                if (reuse >= 0)
                {
                    var recycled = slots[reuse];
                    used[reuse] = true;
                    RebindBoundSlot(ref recycled, idx, ve);
                    scratch[ord] = recycled;
                }
                else if (TryTakeSpareSlot(entry, content, idx, out var taken))
                {
                    // A parked slot from an earlier shrink: re-attached and rebound, no rowBind, no Mount.
                    scratch[ord] = taken;
                    structural = true;
                }
                else
                {
                    var sig = new Signal<int>(idx);
                    Element el = rowBind(sig);
                    var child = _scene.CreateNode(el.ElementTypeId);
                    _scene.AppendChild(content, child);
                    _realizeSlotIndex = idx; _realizeSlotSignal = sig;   // W2-E3: the cold mount's image requests classify by this slot
                    Mount(child, el);
                    _realizeSlotIndex = -1; _realizeSlotSignal = null;
                    scratch[ord] = new BoundSlot(sig, el, child);
                    structural = true;
                }
            }

            // Surplus slots are parked, not removed: the pool stays at its high-water mark so the next grow (a direction
            // reversal, the budget catching up, a viewport growing back) is a signal write instead of a cold mount.
            for (int j = 0; j < slots.Count; j++)
                if (!used[j] && _scene.IsLive(slots[j].Root))
                {
                    ParkSpareSlot(entry, slots[j]);
                    structural = true;
                }

            // Exact logical order is part of the layout/hit-test contract: FirstRealized + child ordinal maps to item.
            for (int i = 0; i < desiredCount; i++)
            {
                var root = scratch[i].Root;
                _scene.Detach(root);
                _scene.AppendChild(content, root);
            }

            slots.Clear();
            slots.AddRange(scratch);
            orderChanged = true;
            _realizeProgress = true;
        }

        // `mat` = rows actually materialized (== w every frame now that the whole window realizes at once); every
        // downstream field keys off the MATERIALIZED count so the published range and the child chain always agree.
        int mat = Math.Min(slots.Count, w);

        bool moved = structural || orderChanged || first != entry.PrevFirst || mat != entry.PrevLen;
        entry.PrevFirst = first;
        entry.PrevLen = mat;
        if (moved) MarkLayoutShape(content);   // children are positioned by FirstRealized + order
        if (structural) _reconciled = true;

        ref ScrollState scw = ref _scene.ScrollRef(node);
        scw.FirstRealized = first; scw.LastRealized = first + mat;
        _scene.Unmark(node, NodeFlags.VirtualRangeDirty);

        FireWindowLifecycle(ve, oldFirst, oldLast, first, first + mat);
    }

    private bool BoundSlotsAreContiguous(NodeHandle content, List<BoundSlot> slots, int first)
    {
        var root = _scene.FirstChild(content);
        for (int i = 0; i < slots.Count; i++)
        {
            if (root.IsNull || slots[i].Root != root || slots[i].Index.Peek() != first + i) return false;
            root = _scene.NextSibling(root);
        }
        return root.IsNull;
    }

    private void RebindBoundSlot(ref BoundSlot slot, int index, VirtualListEl ve)
    {
        int previous = slot.Index.Peek();
        if (previous == index) return;
        if (_scene.IsLive(slot.Root))
        {
            _scene.Unmark(slot.Root, NodeFlags.Hovered | NodeFlags.Pressed | NodeFlags.Focused | NodeFlags.FocusVisual);
            OnSlotRebound?.Invoke(slot.Root);
        }
        slot.Index.Value = index;
        // A signal repair can be progress even when ScrollState's published range did not move. The host must perform
        // its post-realize reactive flush or component-snapshot cells can remain one generation behind bound leaves.
        _realizeProgress = true;
        ve.OnItemIndexChanged?.Invoke(previous, index);
    }

    private static void RotateSlotsLeft(List<BoundSlot> slots, int count, int shift)
    {
        if (shift <= 0 || shift >= count) return;
        var span = CollectionsMarshal.AsSpan(slots)[..count];
        span[..shift].Reverse();
        span[shift..].Reverse();
        span.Reverse();
    }

    private static void RotateSlotsRight(List<BoundSlot> slots, int count, int shift)
        => RotateSlotsLeft(slots, count, count - shift);

    /// <summary>
    /// The EXTENDED bound realize (research adjustments #5 keep-alive + #16 content-type pools). Runs ONLY when
    /// <see cref="VirtualListEl.KeepAlive"/> or <see cref="VirtualListEl.ContentType"/> is set. Like the default path,
    /// exact logical-index overlaps keep their subtree; this path additionally trades allocation for two behaviours:
    /// <list type="bullet">
    /// <item><b>Keep-alive (#5):</b> a slot bound to an item for which <c>KeepAlive(item)</c> is true is NOT recycled when
    /// it leaves the window — it PARKS (detached from the content node ⇒ no layout/paint, and <see cref="SetSubtreeParked"/>
    /// quiesces its render-effects/animations — the same mechanics as <c>Flow.KeepAlive</c>), keeping its live state until
    /// the item re-enters the window (reactivate) or the bucket evicts the least recently parked row beyond its cap.</item>
    /// <item><b>Content-type pools (#16):</b> a slot only cheap-rebinds to an index whose <c>ContentType(index)</c> matches
    /// the type its frozen subtree was built for; a cross-type reuse REBUILDS the slot (fresh subtree) instead. Homogeneous
    /// lists (all one type) rebind exactly as the default path does.</item>
    /// </list>
    /// Scratch storage is retained at the viewport high-water mark. Equal-size contiguous content-type windows rotate
    /// roots in place; cold grow/shrink and keep-alive repair use the retained slow-path scratch.
    /// </summary>
    private void RealizeBoundWindowExtended(NodeHandle node, NodeHandle content, VirtualEntry entry,
                                            VirtualListEl ve, int first, int last, int w, int visibleSlots)
    {
        _ = visibleSlots;
        var rowBind = ve.RowBind!;
        var keepAlive = ve.KeepAlive;
        var contentTypeOf = ve.ContentType;
        var slots = entry.Slots ??= new List<BoundSlot>(Math.Max(4, w));
        int epoch = FrameEpoch;
        bool structural = false;
        int oldFirst = entry.PrevFirst, oldLast = entry.PrevFirst + entry.PrevLen;

        int TypeOf(int idx) => contentTypeOf?.Invoke(idx) ?? 0;

        // Content-type-only lists have the same hot scroll shape as the default recycler. Preserve overlapping roots,
        // rotate only the leaving edge, and rebind only entering slots when every mapped root has the required shape.
        if (keepAlive is null && contentTypeOf is not null && slots.Count == w && entry.PrevLen == w
            && TryRealizeBoundWindowExtendedFast(content, slots, ve, entry.PrevFirst, first))
        {
            if (first != entry.PrevFirst) MarkLayoutShape(content);
            ref ScrollState fastScroll = ref _scene.ScrollRef(node);
            fastScroll.FirstRealized = first; fastScroll.LastRealized = first + w;
            _scene.Unmark(node, NodeFlags.VirtualRangeDirty);
            entry.PrevFirst = first; entry.PrevLen = w;
            FireWindowLifecycle(ve, oldFirst, oldLast, first, first + w);
            return;
        }

        var kept = entry.Kept ??= new Dictionary<int, KeptSlot>();

        int n0 = slots.Count;
        int wCap = w;   // every covered row, this frame (scroll rework §6)

        bool[] consumed = entry.SlotUsed ?? Array.Empty<bool>();
        if (consumed.Length < n0)
            entry.SlotUsed = consumed = new bool[Math.Max(4, n0)];
        else
            Array.Clear(consumed, 0, n0);

        // PHASE 1 — park keep-alive slots leaving the window. They remain bound to their logical item and are excluded
        // from the leaving-slot recycle pool.
        if (keepAlive is not null)
        {
            for (int i = 0; i < n0; i++)
            {
                int item = slots[i].Index.Peek();
                if (item >= first && item < last) continue;          // stays in the window — normal handling
                if (!keepAlive(item)) continue;                       // plain row — recycle/shrink handles it
                if (kept.ContainsKey(item)) continue;                 // defensive: already parked
                // Same hover/press/focus hygiene as the boundary-level Flow.KeepAlive path (BeginKeepAliveExit/
                // DeactivateKeepAliveEntry) — invoke BEFORE Detach so the ancestor-HoverWithin clear walk still sees
                // the live parent chain. Row-level virtualized KeepAlive was missing this call entirely.
                OnSubtreeDeactivated?.Invoke(slots[i].Root);
                _scene.Detach(slots[i].Root);
                SetSubtreeParked(slots[i].Root, parked: true);         // quiesce render-effects/animations (Flow.KeepAlive mechanics)
                kept[item] = new KeptSlot
                {
                    Index = slots[i].Index, El = slots[i].El, Root = slots[i].Root,
                    ContentType = slots[i].ContentType, LastUsed = epoch,
                };
                consumed[i] = true;
                structural = true;
            }
        }

        // PHASE 2 — reserve exact active overlaps first. A one-row shift must not let the entering gap consume a slot
        // that already represents a later survivor.
        // Everything below builds and publishes the REALIZED prefix [first, first+wCap), never the desired window `w`.
        // Same rule the default recycler's `mat` follows: layout, hit-testing, SlotRootForIndex and the arrange
        // contract all key off what was actually built, so a short window stays internally consistent and the untouched
        // tail keeps the extent the measured layout already reserved.
        var newSlots = entry.SlotScratch ??= new List<BoundSlot>(Math.Max(4, w));
        newSlots.Clear();
        for (int ord = 0; ord < wCap; ord++) newSlots.Add(default);
        for (int ord = 0; ord < wCap; ord++)
        {
            int item = first + ord;
            int dtype = TypeOf(item);
            if (kept.TryGetValue(item, out var ks))
            {
                kept.Remove(item);
                if (ks.ContentType == dtype)
                {
                    _scene.AppendChild(content, ks.Root);
                    SetSubtreeParked(ks.Root, parked: false);
                    var restored = new BoundSlot(ks.Index, ks.El, ks.Root, ks.ContentType);
                    RebindBoundSlot(ref restored, item, ve);
                    newSlots[ord] = restored;
                    structural = true;
                    _realizeProgress = true;
                }
                else
                {
                    if (_scene.IsLive(ks.Root)) Remove(ks.Root);
                    structural = true;
                }
                continue;
            }

            for (int i = 0; i < n0; i++)
            {
                if (consumed[i] || slots[i].Index.Peek() != item || slots[i].ContentType != dtype) continue;
                consumed[i] = true;
                newSlots[ord] = slots[i];
                break;
            }
        }

        // PHASE 3 — fill entering gaps from leaving slots of the same content type. Cross-type leftovers rebuild exactly
        // that entering row; overlapping logical items above never rebuild merely because their screen ordinal changed.
        for (int ord = 0; ord < wCap; ord++)
        {
            if (newSlots[ord].Index is not null) continue;
            int item = first + ord;
            int dtype = TypeOf(item);
            int reuse = -1;
            for (int i = 0; i < n0; i++)
                if (!consumed[i] && slots[i].ContentType == dtype) { reuse = i; break; }

            if (reuse >= 0)
            {
                consumed[reuse] = true;
                var recycled = slots[reuse];
                RebindBoundSlot(ref recycled, item, ve);
                newSlots[ord] = recycled;
                continue;
            }

            // No compatible leaving root. Consume one incompatible root so it cannot leak, then build the correct shape.
            for (int i = 0; i < n0; i++)
                if (!consumed[i])
                {
                    consumed[i] = true;
                    if (_scene.IsLive(slots[i].Root)) Remove(slots[i].Root);
                    structural = true;
                    break;
                }

            var nsig = new Signal<int>(item);
            Element nel = rowBind(nsig);
            var child = _scene.CreateNode(nel.ElementTypeId);
            _scene.AppendChild(content, child);
            _realizeSlotIndex = item; _realizeSlotSignal = nsig;   // W2-E3: the cold mount's image requests classify by this slot
            Mount(child, nel);
            _realizeSlotIndex = -1; _realizeSlotSignal = null;
            newSlots[ord] = new BoundSlot(nsig, nel, child, dtype);
            structural = true;
            _realizeProgress = true;
        }

        // Shrink any unused plain leaving roots.
        for (int i = 0; i < n0; i++)
            if (!consumed[i] && _scene.IsLive(slots[i].Root))
            {
                Remove(slots[i].Root);
                structural = true;
            }

        entry.Slots = newSlots;
        entry.SlotScratch = slots;

        // Re-order active children to window order (ord-th child ⇒ item first+ord — the arrange contract).
        for (int ord = 0; ord < newSlots.Count; ord++)
        {
            var h = newSlots[ord].Root;
            if (h.IsNull || !_scene.IsLive(h)) continue;
            _scene.Detach(h);
            _scene.AppendChild(content, h);
        }

        // Bounded keep-alive bucket: evict the least recently parked row beyond the cap, so a long scroll over keep-alive
        // rows cannot retain one mounted subtree per row it ever passed.
        while (kept.Count > KeptSlotCap)
        {
            int victim = 0; long victimUsed = long.MaxValue; bool found = false;
            foreach (var kv in kept)
                if (kv.Value.LastUsed < victimUsed) { victimUsed = kv.Value.LastUsed; victim = kv.Key; found = true; }
            if (!found || !kept.Remove(victim, out var evicted)) break;
            if (_scene.IsLive(evicted.Root)) Remove(evicted.Root);   // parked ⇒ hard unmount, no exit ghost
            structural = true;
        }

        if (structural) _reconciled = true;
        MarkLayoutShape(content);   // window/order changed → re-arrange the realized band

        ref ScrollState scw = ref _scene.ScrollRef(node);
        scw.FirstRealized = first; scw.LastRealized = first + wCap;
        _scene.Unmark(node, NodeFlags.VirtualRangeDirty);
        entry.PrevFirst = first; entry.PrevLen = wCap;

        FireWindowLifecycle(ve, oldFirst, oldLast, first, first + wCap);
    }

    private bool TryRealizeBoundWindowExtendedFast(NodeHandle content, List<BoundSlot> slots, VirtualListEl ve,
                                                   int oldFirst, int first)
    {
        int count = slots.Count;
        if (!BoundSlotsAreContiguous(content, slots, oldFirst)) return false;
        int shift = first - oldFirst;
        var contentTypeOf = ve.ContentType!;

        if (shift == 0)
        {
            for (int i = 0; i < count; i++)
                if (slots[i].ContentType != contentTypeOf(first + i)) return false;
            return true;
        }

        if (Math.Abs(shift) >= count)
        {
            for (int i = 0; i < count; i++)
                if (slots[i].ContentType != contentTypeOf(first + i)) return false;
            var span = CollectionsMarshal.AsSpan(slots);
            for (int i = 0; i < count; i++) RebindBoundSlot(ref span[i], first + i, ve);
            return true;
        }

        int normalized = shift > 0 ? shift : count + shift;
        for (int ord = 0; ord < count; ord++)
        {
            int oldOrd = ord + normalized;
            if (oldOrd >= count) oldOrd -= count;
            if (slots[oldOrd].ContentType != contentTypeOf(first + ord)) return false;
        }

        if (shift > 0)
        {
            for (int i = 0; i < shift; i++)
            {
                var root = slots[i].Root;
                _scene.Detach(root);
                _scene.AppendChild(content, root);
            }
            RotateSlotsLeft(slots, count, shift);
            var span = CollectionsMarshal.AsSpan(slots);
            for (int i = count - shift; i < count; i++) RebindBoundSlot(ref span[i], first + i, ve);
        }
        else
        {
            int entering = -shift;
            for (int i = count - 1; i >= count - entering; i--)
            {
                var root = slots[i].Root;
                _scene.Detach(root);
                _scene.PrependChild(content, root);
            }
            RotateSlotsRight(slots, count, entering);
            var span = CollectionsMarshal.AsSpan(slots);
            for (int i = 0; i < entering; i++) RebindBoundSlot(ref span[i], first + i, ve);
        }
        return true;
    }

    /// <summary>
    /// Window-diff for virtualization (virtualization.md: recycle, don't churn). Overlapping rows reuse their element
    /// OBJECT (identity-matched to their existing node — a no-op). A row REBUILT at the SAME slot (a parent re-render
    /// re-ran RenderItem over an unchanged window) with the same type + key is the same item re-described: it is
    /// diffed IN PLACE via the general Update (keyed child reconcile), so component subtrees keep their instance and
    /// state — no mount+remove, no first-frame self-measure flash. Every other new row RECYCLES a scrolled-out node of
    /// the same shape: its columns are rewritten in place (text/fill/image rebind) with NO scene mount/unmount, no key
    /// strings, and no per-realize dictionary — the thumb-drag storm becomes a column rewrite instead of a tree rebuild.
    /// Non-recyclable subtrees (components, Show/For, providers, scrollers, reactive binds — identity fixed at mount)
    /// fall back to mount+remove. <paramref name="shift"/> = newFirst − prevFirst (the overlap slot mapping).
    /// </summary>
    private void ReconcileWindow(NodeHandle node, ReadOnlySpan<Element> newKids, ReadOnlySpan<Element> oldKids, int shift, int firstIndex)
    {
        int oldN = oldKids.Length, newN = newKids.Length;
        if (oldN == 0 && newN == 0) return;

        if (oldN <= StackScratchMax && newN <= StackScratchMax)
        {
            ReconcileWindowCore(node, newKids, oldKids, shift, firstIndex,
                stackalloc NodeHandle[oldN], stackalloc bool[oldN], stackalloc NodeHandle[newN]);
            return;
        }

        var scratch = ChildScratch.Rent(oldN, newN);
        try { ReconcileWindowCore(node, newKids, oldKids, shift, firstIndex, scratch.Old, scratch.Used, scratch.New); }
        finally { scratch.Return(); }
    }

    // `firstIndex` = the logical item index of newKids[0] (W2-E3): every Update/Mount below runs with _realizeSlotIndex
    // = firstIndex + i so an ImageEl written for slot i requests at the lane its position in the viewport warrants.
    private void ReconcileWindowCore(NodeHandle node, ReadOnlySpan<Element> newKids, ReadOnlySpan<Element> oldKids,
                                     int shift, int firstIndex, Span<NodeHandle> oldNodes, Span<bool> used, Span<NodeHandle> newNodes)
    {
        int oldN = oldKids.Length, newN = newKids.Length;
        {
            int i = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull && i < oldN; c = _scene.NextSibling(c)) oldNodes[i++] = c;
        }

        // Pass 1: overlap — a reused element object keeps its node untouched. A REBUILT element at the SAME slot
        // (a parent re-render re-ran RenderItem over an unchanged window — the reuseOverlap:false realize) with the
        // same type + key is the SAME ITEM re-described (the slot mapping aligns item indices): diff it IN PLACE via
        // the general Update, so the keyed child reconcile absorbs content deltas and component subtrees keep their
        // instance/state — no mount+remove, no first-frame self-measure flash. Slots with no same-slot counterpart
        // (scrolled in) or a type/key mismatch fall through to the recycle/mount pass below, exactly as before.
        for (int i = 0; i < newN; i++)
        {
            int os = i + shift;
            if ((uint)os >= (uint)oldN) { newNodes[i] = NodeHandle.Null; continue; }
            Element nk = newKids[i], ok = oldKids[os];
            if (ReferenceEquals(nk, ok))
            {
                newNodes[i] = oldNodes[os];
                used[os] = true;
            }
            else if (nk.ElementTypeId == ok.ElementTypeId
                     && string.Equals(nk.Key, ok.Key, System.StringComparison.Ordinal))
            {
                // NOT the recycle path: the node still shows the same item, so transient interaction state
                // (Hovered/Pressed/Focused) stays — normal Update semantics. AssertRecycleShapeStable does not
                // apply (the keyed child diff CAN realign structure); `structural` stays false (node identity and
                // document order are unchanged).
                newNodes[i] = oldNodes[os];
                used[os] = true;
                _realizeSlotIndex = firstIndex + i;
                Update(oldNodes[os], nk, ok);
                _realizeSlotIndex = -1;
            }
            else newNodes[i] = NodeHandle.Null;
        }

        // Pass 2: fresh rows recycle scrolled-out nodes (column rewrite via Update); mount only when none is left.
        bool structural = false;
        int cursor = 0;
        for (int i = 0; i < newN; i++)
        {
            if (!newNodes[i].IsNull) continue;
            Element nk = newKids[i];

            int match = -1;
            if (IsRecyclable(nk))
            {
                while (cursor < oldN && used[cursor]) cursor++;
                if (cursor < oldN && oldKids[cursor].ElementTypeId == nk.ElementTypeId) match = cursor;
            }

            if (match >= 0)
            {
                used[match] = true;
                newNodes[i] = oldNodes[match];
                AssertRecycleShapeStable(oldKids[match], nk);   // [Conditional("DEBUG")] — catches a PartDelta/factory that varied SHAPE per item
                _realizeSlotIndex = firstIndex + i;
                Update(oldNodes[match], nk, oldKids[match]);
                _realizeSlotIndex = -1;
                // The node now shows a DIFFERENT item: transient interaction state must not travel with it (the old
                // code freed the node, which dropped this state implicitly).
                _scene.Unmark(oldNodes[match], NodeFlags.Hovered | NodeFlags.Pressed | NodeFlags.Focused | NodeFlags.FocusVisual);
            }
            else
            {
                var child = _scene.CreateNode(nk.ElementTypeId);
                // Parent BEFORE Mount (like every other mount path): a component mounting inside this realize pass
                // renders immediately, and its UseContext resolves providers by walking UP from its anchor — an
                // unparented anchor would silently miss every provider (and never subscribe). The ordering pass
                // below detaches/re-appends all children anyway.
                _scene.AppendChild(node, child);
                _realizeSlotIndex = firstIndex + i;
                Mount(child, nk);
                _realizeSlotIndex = -1;
                newNodes[i] = child;
                structural = true;
            }
        }

        for (int j = 0; j < oldN; j++)
            if (!used[j]) { Remove(oldNodes[j]); structural = true; }

        for (int i = 0; i < newN; i++)
        {
            _scene.Detach(newNodes[i]);
            _scene.AppendChild(node, newNodes[i]);
        }

        // The window moved (children are positioned by FirstRealized + document order) or changed size/shape → re-arrange.
        if (structural || newN != oldN || shift != 0) MarkLayoutShape(node);
    }

    /// <summary>True if the subtree is a PLAIN visual tree (box/grid/text/image/polyline, no reactive binds, no
    /// OnRealized): safe to rebind onto a recycled node. Components/flow/providers/scrollers and bound elements capture
    /// identity at mount — they must mount fresh. EXCEPTION: the shared theme-text brushes (<see cref="Ui.IsThemeTextBrush"/>,
    /// e.g. every default-colored TextEl) are recyclable — their persisted binding re-fires on RethemeAll, and a recycle
    /// onto a different tier's singleton thunk is re-wired in place by the recycle's Update (bound→bound,
    /// <see cref="RewireBinds"/>); the pairing's bind CLASS stability is DEBUG-asserted in <see cref="ShapeCompatible"/>.</summary>
    private static bool IsRecyclable(Element el)
    {
        switch (el)
        {
            case TextEl t:
                return !t.Text.IsBound && (!t.Color.IsBound || Ui.IsThemeTextBrush(t.Color)) && t.OnRealized is null;
            case SpanTextEl:
                return true;   // plain leaf — WriteColumns rewrites every column incl. the span run/handlers
            case ListRowEl:
                return true;   // plain leaf — WriteColumns/WriteRowCells rewrites every column incl. the cell array
                              // and the click handler; a recycle is exactly the ItemsView.CreateBound story (signal
                              // writes only, never a remount).
            case ImageEl im:
                return !im.Source.IsBound && !im.Placeholder.IsBound;
            case IconLayerEl il:
                return !il.Tint.IsBound;   // ThemedIcon always binds Tint (theme-live), so an icon layer mounts fresh (like a bound image)
            case PolylineStrokeEl:
                return true;
            case SeriesEl se:
                return !se.Samples.IsBound && !se.GradientMix.IsBound;   // a bound sample source / mix is a mount-time BindEffect (Reconciler.Series.cs): mount fresh
            case SpriteFieldEl sf:
                return !sf.Instances.IsBound; // likewise the bound instance buffer (Reconciler.Sprites.cs)
            case PathEl pe:
                // Mirrors the BoxEl rule just below: a path with an OnRealized capture (the hero-art draw-on timelines)
                // must mount fresh every time so the callback fires and the caller's ref stays pointed at a live node.
                return pe.OnRealized is null;
            case BoxEl b:
                if (b.Transform.IsBound || b.Opacity.IsBound || b.Fill.IsBound || b.BorderColor.IsBound
                    || b.RadialGradientCenter.IsBound || b.GradientMix.IsBound || b.FeedbackTransform.IsBound || b.FeedbackDecay.IsBound
                    || b.Width.IsBound || b.Height.IsBound
                    || b.OnRealized is not null || b.OnBoundsChanged is not null || b.FollowRect is not null) return false;
                foreach (var c in b.Children) if (!IsRecyclable(c)) return false;
                return true;
            case GridEl g:
                foreach (var c in g.Children) if (!IsRecyclable(c)) return false;
                return true;
            default:
                return false;   // ComponentEl / ShowEl / ForEl / ContextProviderEl / ScrollEl / VirtualListEl / unknown
        }
    }

    // The recycle-shape contract guard (production safety == CI coverage): per-item VALUE variation (a PartDelta) or
    // invisible-part flips are legal in a recycled scroll path, but per-item STRUCTURE variation that the keyed child
    // reconcile (below) CANNOT absorb is not — it rebinds onto a recycled node the diff can't realign. This catches
    // that in DEBUG/CI. The comparison mirrors ReconcileChildren's model (keyed children match by Key — so a keyed
    // child legally appears/disappears, e.g. ItemContainer's selection-state ring/common/checkbox; UNKEYED children
    // match by ORDINAL AMONG UNKEYED SIBLINGS — the pairing ReconcileChildren applies whenever the unkeyed population
    // is unchanged — so their type sequence must be stable, and each key present in BOTH must stay shape-compat).
    [System.Diagnostics.Conditional("DEBUG")]
    private static void AssertRecycleShapeStable(Element prev, Element next)
    {
        if (!ShapeCompatible(prev, next))
            System.Diagnostics.Debug.Fail(
                $"recycle shape mismatch: a factory/PartDelta varied keyed-reconcile-incompatible SHAPE (not values) " +
                $"per item — prev typeId={prev.ElementTypeId} next typeId={next.ElementTypeId}. Per-item variation must " +
                $"be VALUES (PartDelta) or invisible-part flips; structural variation must use STABLE KEYS so the keyed " +
                $"window diff can absorb it, never an unkeyed positional add/remove (docs/guide/control-fidelity.md §6).");
    }

    // Shape-compat = same element type, and child lists reconcilable by the SAME rules ReconcileChildren applies — so
    // legal STATE-driven chrome (the selection ring/inner-stroke/checkbox coming & going, the checkmark glyph appearing
    // when checked) passes, while a genuinely corrupting recycle (a different-typed UNKEYED child landing at an aligned
    // positional slot, or a keyed child whose own subtree shape changes) is flagged:
    //   • UNKEYED children match by ORDINAL AMONG UNKEYED SIBLINGS (keyed siblings skipped — the cursor
    //     ReconcileChildrenCore / ChildReconcilePlan.Step pair by when the unkeyed count is unchanged; an unkeyed
    //     add/remove there falls back to same-index pairing, UnkeyedPairing) — overlapping ordinals must agree on type +
    //     recurse-compat; a surplus on either side is a legal TAIL insert/remove (exactly the unchecked↔checked glyph child).
    //   • KEYED children match by Key — a key in only one side is a free insert/remove (the selected↔unselected ring);
    //     a key in BOTH must recurse-compat.
    // Values (Fill/Color/Opacity/…) are ignored — only structure is checked. Leaves (Text/Image/Polyline) compare by type.
    private static bool ShapeCompatible(Element a, Element b)
    {
        if (a.ElementTypeId != b.ElementTypeId) return false;
        // Text color-BIND CLASS is structure-adjacent under recycle: Update never CREATES or REMOVES a binding, so an
        // unbound↔bound flip would strand (or never wire) the node's color binding. A different theme-brush TIER is fine
        // — the recycle's Update re-wires the bound Color to the new singleton thunk (bound→bound, RewireBinds). Unbound
        // explicit colors are free to differ (WriteColumns rewrites them every recycle) so their VALUE is ignored — only
        // the bind class is checked.
        if (a is TextEl ta && b is TextEl tb && ta.Color.IsBound != tb.Color.IsBound) return false;
        Element[]? ac = a switch { BoxEl x => x.Children, GridEl x => x.Children, _ => null };
        Element[]? bc = b switch { BoxEl x => x.Children, GridEl x => x.Children, _ => null };
        if (ac is null || bc is null) return true;   // leaf type matched (no child structure to compare)

        // Ordinal pass over UNKEYED children (Key == null): walk both in order skipping keyed siblings, comparing
        // overlapping ordinals only. A trailing surplus on either side is a legal tail insert/remove (ReconcileChildren
        // removes/mounts it).
        int ai = 0, bi = 0;
        while (true)
        {
            while (ai < ac.Length && ac[ai].Key is not null) ai++;
            while (bi < bc.Length && bc[bi].Key is not null) bi++;
            if (ai >= ac.Length || bi >= bc.Length) break;   // one side ran out → remaining unkeyed are tail churn
            if (!ShapeCompatible(ac[ai], bc[bi])) return false;
            ai++; bi++;
        }

        // Keyed children: every key present in BOTH must stay shape-compatible (a key in one side only is a legal
        // keyed insert/remove — exactly how the selection ring/checkbox come and go across a selected↔unselected recycle).
        foreach (var ce in ac)
            if (ce.Key is string k)
                foreach (var de in bc)
                    if (de.Key == k) { if (!ShapeCompatible(ce, de)) return false; break; }
        return true;
    }

    // ── Keyed child reconcile (the structural engine, retained) ──────────────────────────────────

    /// <summary>Small containers retain stack scratch; larger ones lease reusable isolated child-identity plans.
    /// Commit remains one indivisible UI operation until mounts/updates gain their own staging representation.</summary>
    private const int StackScratchMax = 128;

    /// <summary>Array-pool scratch for the shifted virtual-window recycler, whose identity law differs from keyed
    /// child planning. Each recursive invocation rents independently; all slices start cleared.</summary>
    private ref struct ChildScratch
    {
        public Span<NodeHandle> Old, New;
        public Span<bool> Used;
        private NodeHandle[] _oldRent, _newRent;
        private bool[] _usedRent;

        public static ChildScratch Rent(int oldN, int newN)
        {
            ChildScratch s = default;
            s._oldRent = ArrayPool<NodeHandle>.Shared.Rent(Math.Max(1, oldN));
            s._usedRent = ArrayPool<bool>.Shared.Rent(Math.Max(1, oldN));
            s._newRent = ArrayPool<NodeHandle>.Shared.Rent(Math.Max(1, newN));
            s.Old = s._oldRent.AsSpan(0, oldN);
            s.Used = s._usedRent.AsSpan(0, oldN);
            s.New = s._newRent.AsSpan(0, newN);
            s.Old.Clear(); s.Used.Clear(); s.New.Clear();
            return s;
        }
        public void Return()
        {
            ArrayPool<NodeHandle>.Shared.Return(_oldRent);
            ArrayPool<bool>.Shared.Return(_usedRent);
            ArrayPool<NodeHandle>.Shared.Return(_newRent);
        }
    }

    internal void ReconcileChildren(NodeHandle node, ReadOnlySpan<Element> newKids, ReadOnlySpan<Element> oldKids)
    {
        _childReconcileRevision++;
        int oldN = oldKids.Length, newN = newKids.Length;
        if (oldN == 0 && newN == 0) return;

        if (oldN <= StackScratchMax && newN <= StackScratchMax)
        {
            ReconcileChildrenCore(node, newKids, oldKids,
                stackalloc NodeHandle[oldN], stackalloc bool[oldN], stackalloc NodeHandle[newN]);
            return;
        }

        if (_renderCensus is not null) _censusPlans++;
        ReconcilePlannedChildren(node, newKids, oldKids);
    }

    private void ReconcileChildrenCore(NodeHandle node, ReadOnlySpan<Element> newKids, ReadOnlySpan<Element> oldKids,
                                       Span<NodeHandle> oldNodes, Span<bool> used, Span<NodeHandle> newNodes)
    {
        int oldN = oldKids.Length, newN = newKids.Length;
        if (oldN > 0)
        {
            int i = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull && i < oldN; c = _scene.NextSibling(c)) oldNodes[i++] = c;
        }

        Dictionary<string, int>? keyMap = null;
        if (oldN > 32)
            for (int j = 0; j < oldN; j++)
                if (oldKids[j].Key is string k) (keyMap ??= new()).TryAdd(k, j);

        bool structural = false;
        // A PURE reorder (same key set, different order — e.g. a list reverse) creates/removes nothing, but the
        // re-appended child order still needs a relayout to move the rows. Non-monotonic match order detects it.
        bool moved = false;
        int lastMatch = -1;
        // UNKEYED identity (keyed children match by Key exactly as before, and only ever consume KEYED old slots, so the
        // two rules never compete for a slot). The mode is decided once, at the first unkeyed new child (a pure-keyed
        // list — every Flow.For — never pays for it), by UnkeyedPairing.Ordinal:
        //   • ORDINAL — the unkeyed population is unchanged (same count in old and new): the k-th unkeyed new child pairs
        //     with the k-th unkeyed old child; keyed siblings are skipped by the cursor, never counted (the model
        //     ShapeCompatible / the recycle-shape guard encode). A keyed insert / remove / move therefore never shifts
        //     unkeyed identity — the bug class this fixes: [title, artists] → [keyedLine, title, artists] used to hand
        //     the new title the old ARTISTS node + component instance (whose factory froze at mount) and remount artists.
        //   • POSITIONAL — an unkeyed child was added or removed: the former same-index rule (same slot, both unkeyed).
        //     Pure ordinal pairing would be WRONG here: a keyed⇄unkeyed flip at one slot (`cond ? keyedX : unkeyedY`
        //     ahead of [title, artists]; NavigationView's top bar, whose unkeyed "More" button takes an overflowed keyed
        //     item's slot) would shift every unkeyed sibling after it — the very swap above — while same-index pairing
        //     keeps them put.
        // Either way reuse needs the element type to match; a mismatch consumes that ordinal/slot (no reuse, no slide:
        // the new child mounts, the old one is removed). Pure-unkeyed lists are EXACTLY the former rule in both modes
        // (ordinal == index when nothing was added/removed), and so is every render that adds/removes an unkeyed child;
        // only a render that shifts keyed children around an unchanged unkeyed population pairs differently. Zero-alloc:
        // one forward cursor (amortized O(oldN)) + one counting pass. The ordinal matches are monotone among themselves,
        // so `moved` below still flags exactly a real reorder of surviving nodes.
        int unkeyedMode = 0;        // 0 = undecided, 1 = ordinal, 2 = positional
        int unkeyedCursor = 0;

        for (int i = 0; i < newN; i++)
        {
            Element nk = newKids[i];
            int match = -1;
            if (nk.Key is string key)
            {
                if (keyMap is not null && keyMap.TryGetValue(key, out int mapped)
                    && !used[mapped] && oldKids[mapped].ElementTypeId == nk.ElementTypeId)
                    match = mapped;
                else if (keyMap is null)
                {
                    for (int oldIndex = 0; oldIndex < oldN; oldIndex++)
                    {
                        if (used[oldIndex] || oldKids[oldIndex].ElementTypeId != nk.ElementTypeId || oldKids[oldIndex].Key != key) continue;
                        match = oldIndex;
                        break;
                    }
                }
            }
            else
            {
                if (unkeyedMode == 0) unkeyedMode = UnkeyedPairing.Ordinal(oldKids, newKids) ? 1 : 2;
                if (unkeyedMode == 1)
                {
                    while (unkeyedCursor < oldN && oldKids[unkeyedCursor].Key is not null) unkeyedCursor++;
                    if (unkeyedCursor < oldN)
                    {
                        int ordinal = unkeyedCursor++;   // consumed whether or not the type matches
                        if (!used[ordinal] && oldKids[ordinal].ElementTypeId == nk.ElementTypeId) match = ordinal;
                    }
                }
                else if (i < oldN && !used[i] && oldKids[i].Key is null && oldKids[i].ElementTypeId == nk.ElementTypeId)
                    match = i;
            }

            if (match >= 0)
            {
                used[match] = true;
                if (match < lastMatch) moved = true; else lastMatch = match;
                newNodes[i] = oldNodes[match];
                Update(oldNodes[match], nk, oldKids[match]);
            }
        }

        // Any viewport in a departing subtree persists its offset HERE (SaveScrollPosition → the host's
        // ScrollPositionMemory), before a single new child mounts: mounting is what reads the memory back (a ScrollKey
        // restore), and a save on the far side of it (Remove → unmount) would let a same-key swap restore a stale position. Splitting
        // the match loop above from the create loop below is what makes the unmatched-OLD set knowable this early.
        for (int j = 0; j < oldN; j++)
            if (!used[j]) PreSaveScroll(oldNodes[j]);

        // Creation still happens BEFORE removal, deliberately. Hoisting the removal instead looks tempting — it would
        // align this with every other replace path (ReplaceSingleChild, ReconcileSingleChild and ReplaceKeepAliveRoot
        // all remove first) and it makes the pre-save above unnecessary — but it breaks e4popup.3 (menus/flyouts
        // windowing) reproducibly: some popup/overlay state is order-sensitive across the removal of the outgoing
        // presenter and the mount of the incoming one. Pre-saving is the change that fixes the scroll bug WITHOUT
        // touching node lifecycle ordering at all. (An earlier note here blamed gate.arena.alloc-zero for the same
        // conclusion — that was a stale-incremental-build false positive, the one ops/diag/README.md warns about for
        // exactly that gate. On a clean build the reordering passes it 3/3. e4popup.3 is the real constraint.)
        NodeHandle outerStaggerParent = _staggerDiffParent;
        int outerStaggerBase = _staggerDiffBase;
        _staggerDiffParent = node;
        _staggerDiffBase = oldN;   // every old child is still attached here: new ones land after them
        for (int i = 0; i < newN; i++)
        {
            if (!newNodes[i].IsNull) continue;
            Element nk = newKids[i];
            var child = _scene.CreateNode(nk.ElementTypeId);
            // Parent BEFORE Mount (like the single-child Diff path): a ComponentEl mounted here runs its first
            // render synchronously, and UseContext resolves providers by walking UP from the component's anchor —
            // mounting detached would silently resolve to the context DEFAULT (and never subscribe, so it stays
            // wrong forever). The ordering pass below detaches/re-appends every child anyway.
            _scene.AppendChild(node, child);
            Mount(child, nk);
            newNodes[i] = child;
            structural = true;
        }
        _staggerDiffParent = outerStaggerParent;
        _staggerDiffBase = outerStaggerBase;

        for (int j = 0; j < oldN; j++)
            if (!used[j]) { Remove(oldNodes[j]); structural = true; }

        for (int i = 0; i < newN; i++)
        {
            _scene.Detach(newNodes[i]);
            _scene.AppendChild(node, newNodes[i]);
        }

        // Structural change to the child set (including a pure keyed reorder) → relayout this container's subtree
        // (scoped to its boundary).
        if (structural || moved || newN != oldN) MarkLayoutShape(node);
    }

    // ── Removal / unmount (dispose reactive effects) ────────────────────────────────────────────

    private void Remove(NodeHandle node)
    {
        _reconciled = true;
        OnSubtreeRemoved?.Invoke(node);
        // Parked KeepAlive content is already invisible. A reactive boundary may settle after the park edge, but its
        // animated child must be hard-removed instead of escaping the detached page as a globally drawn exit orphan.
        bool parked = (_scene.Flags(node) & NodeFlags.Parked) != 0;
        if (!parked && Anim is { } anim && anim.TryGetTransition(node, out var spec) && spec.Exit.Active)
        {
            // Smooth exit (mirror of the enter-reflow): orphaning DETACHES this node, so its sibling would SNAP into the
            // freed space. For a SizeMode.Reflow exit, snapshot the surviving PARENT's with-child size + queue it — after
            // layout the host eases the parent → its without-child size, so the neighbour (the seek bar) reflows instead
            // of snapping while the orphan fades in the closing space. Not resize-gated (RunReflowLayout drives it).
            if (spec.Size == SizeMode.Reflow)
            {
                var par = _scene.Parent(node);
                if (!par.IsNull) { var pb = _scene.Bounds(par); anim.PendingExitReflow.Add((par, pb.W, pb.H, spec)); }
            }
            UnmountSubtree(node);
            // Kill any looping track (the SkeletonPulse) BEFORE orphaning + SeedExit, so only the FINITE exit tracks
            // remain: an orphan is reclaimed when HasTracks(node)→false, and a forever-looping pulse would pin it and
            // defeat the engine's idle wake-stop (a battery/never-quiesce regression). Then seed the finite exit.
            anim.CancelAll(node);
            // Hand the orphan its OWN hard deadline (see ExitMaxAgeMs) on top of the host's global 2s backstop: a wedged
            // exit track on a page-sized subtree (the skeleton shimmer) must not keep painting over the live content for
            // two seconds.
            _scene.Orphan(node, ExitMaxAgeMs(spec));
            anim.SeedExit(node, spec.Exit, spec);
            return;
        }
        UnmountSubtree(node);
        _scene.FreeSubtree(node);
    }

    // A spring has no nominal duration — cap the deadline instead of leaving it unbounded.
    private const float SpringExitCapMs = 1200f;
    // Slack over the nominal duration. The deadline is measured on SceneStore.AnimClockMs — the same clamped timebase
    // the exit track integrates on — so this only has to cover the seed-to-first-advance gap plus one clock quantum
    // (AnimClock clamps a frame to <=40ms). It does NOT have to absorb wall-clock jank: a wall-measured deadline would
    // force-reclaim HEALTHY exits mid-fade on any slow frame, which is why the timebase is the animation clock.
    private const float ExitReclaimSlackMs = 100f;

    /// <summary>The exit orphan's own reclaim deadline (animation-clock ms): the animated leg's nominal duration + its
    /// start delay + slack. Passed to <see cref="SceneStore.Orphan"/>; <c>AppHost.ReclaimSettledOrphans</c> force-reclaims
    /// past it even while tracks remain, so a never-settling exit can't pin a half-faded layer over live content.</summary>
    private static float ExitMaxAgeMs(in LayoutTransition spec)
    {
        var dyn = spec.ExitDynamics ?? spec.Dynamics;
        float dur = dyn.Kind == DynamicsKind.Tween && dyn.DurationMs > 0f ? dyn.DurationMs : SpringExitCapMs;
        float delay = MathF.Max(0f, spec.ExitDelayMs ?? spec.DelayMs);
        return dur + delay + ExitReclaimSlackMs;
    }

    private void UnmountSubtree(NodeHandle node)
    {
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c)) UnmountSubtree(c);

        int idx = (int)node.Raw.Index;
        Anim?.CancelAll(node);
        if (_scene.HasScroll(node)) SaveScrollPosition?.Invoke(node);   // persist this viewport's offset for its ScrollKey
        if (_morphKeyByNode.Remove(idx, out string? morphKey))
        {
            RemoveMorphKey(node, morphKey);
            Connected?.CaptureOnLeave(node, removeTag: true);   // shared-element: only tagged nodes participate
        }
        if (_keepAliveState.Remove(idx, out var kas))
        {
            foreach (var ex in kas.Entries.Values)
            {
                if (!_scene.IsLive(ex.Root) || !_scene.Parent(ex.Root).IsNull) continue;
                UnmountSubtree(ex.Root);
                _scene.FreeSubtree(ex.Root);
            }
        }
        if (Images is not null)
        {
            ref NodePaint paint = ref _scene.Paint(node);
            // Diagnostic only: the third way a cover reloads is the node going away and coming back (H3). Paired with
            // the ImageCache `pin`/`unpin` events, an `unmount` followed by a `pin ... state=None` is that exact story.
            if (Diag.CompiledIn && Diag.Enabled && paint.VisualKind == VisualKind.Image && paint.ImageId != 0
                && ImageCache.DiagTraced(Images.SourceOf(new ImageHandle(paint.ImageId))))
                Diag.Event("img", $"unmount node={node.Raw.Index} id={paint.ImageId} " +
                    $"src={ImageCache.DiagSourceTail(Images.SourceOf(new ImageHandle(paint.ImageId)))}");
            if (paint.VisualKind == VisualKind.Image && paint.ImageId != 0) UnpinImageNode(node, paint.ImageId);
            if (paint.VisualKind == VisualKind.Image && _scene.TryGetImageEffects(node, out var effects)
                && effects.DerivedImageId != 0) UnpinImageNode(node, effects.DerivedImageId);
            // A hold-last-good in progress (media-pipeline.md §hold-last-good): unpin the pending (new, still-decoding)
            // id too — otherwise an unmount mid-hold would leak its pin (UnpinImageNode's refs==0 check cancels it).
            if (_pendingImageId.Remove(idx, out int pendingId)) UnpinImageNode(node, pendingId);
            // A swap crossfade in flight holds its OUTGOING texture pinned — release it with the node.
            if (_imageSwaps.Remove(idx, out var swap)) UnpinImageNode(node, swap.OutgoingId);
            _keepWhileHiddenNodes.Remove(idx);   // the slot may be reused by a node that never asked to be kept
        }
        if (_nodeBindings.Remove(idx, out var binds)) for (int i = 0; i < binds.Count; i++) binds[i].Dispose();
        _providerSig.Remove(idx);
        _mirroredChild.Remove(idx);
        _showState.Remove(idx);
        _showEl.Remove(idx);
        _showEffect.Remove(idx);
        _forEl.Remove(idx);
        _forEffect.Remove(idx);
        if (_forState.Remove(idx, out var fs) && fs.Prev.Length > 0)   // return the pooled For buffer (finding #6)
        {
            Array.Clear(fs.Prev, 0, fs.Len);
            ArrayPool<Element>.Shared.Return(fs.Prev);
        }
        _skelEl.Remove(idx);
        _skelEffect.Remove(idx);
        _skelForce.Remove(idx);
        ReleaseSkeletonScrollbarSuppression(idx);
        if (_skelState.Remove(idx, out var sk) && sk.Group is { } skg) SkelGroupCoordinator.Unregister(skg, idx);
        if (_comps.Remove(node, out var e)) { e.QueuedReplay = false; e.Scope?.Dispose(); _live.Remove(e.Comp); _anchorOf.Remove(e.Comp); }   // Scope.Dispose cascades: dispose render-effect → RunAllCleanups
        if (_virtuals.Remove(node, out var v))
        {
            FreeSpareSlots(v, keep: 0);   // parked spares are detached, so the list's FreeSubtree cannot reach them
            FreeKeptSlots(v);             // so are keep-alive-parked rows
            if (v.Prev is not null)
            {
                Array.Clear(v.Prev, 0, v.PrevLen);
                ArrayPool<Element>.Shared.Return(v.Prev);
            }
        }
    }

    // ── Column writes (POD → scene) ─────────────────────────────────────────────────────────────

    /// <summary>[Conditional("DEBUG")] one-transform-owner tripwire — the invariant stated on
    /// <see cref="Element.Transform"/>, now that an unbound static matrix is honored rather than dropped. A node may
    /// declare EITHER an explicit matrix OR the decomposed Offset/Scale/Rotation floats, and neither may be combined with
    /// a transform-owning scroll effect (Sticky / Parallax / Scale — rewrites LocalTransform every frame and would silently win).
    /// Erased from the shipping AOT binary — in production, safety == the CI gate that exercises this.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void AssertSingleTransformOwner(Element el, bool staticMatrix, bool staticDecomposed)
    {
        if (!staticMatrix) return;
        if (staticDecomposed)
            throw new System.InvalidOperationException(
                "Transform owner conflict: this element declares BOTH a static Transform matrix and decomposed " +
                "OffsetX/OffsetY/Scale/Rotation. The matrix wins and the floats are dropped — declare one or the other.");
        var effects = el.ScrollEffects;
        if (effects is null) return;
        foreach (var e in effects)
        {
            bool ownsTransform = e.Effect.Channel is FluentGpu.Scroll.Effects.EffectChannel.TransX
                or FluentGpu.Scroll.Effects.EffectChannel.TransY or FluentGpu.Scroll.Effects.EffectChannel.ScaleXY;
            if (ownsTransform)
                throw new System.InvalidOperationException(
                    "Transform owner conflict: a static Transform matrix cannot be combined with a transform-owning " +
                    "ScrollEffect (Sticky / Parallax / Scale) — the effect rewrites LocalTransform every frame and " +
                    "would clobber the static matrix.");
        }
    }

    /// <summary>E14 (home-redesign-remediation.md §2, Appendix W0): a <see cref="ComponentEl"/> anchor never runs
    /// <see cref="WriteColumns"/> — <see cref="Mount"/> returns at <see cref="MountComponent"/> BEFORE reaching it, and
    /// the <see cref="Update"/> reuse branch (a live component is autonomous — its parent's re-render never touches it)
    /// returns early too — so every base-<see cref="Element"/> prop authored on an <c>Embed.Comp(...)</c> record
    /// (<c>.Sticky</c>, <c>Visible</c>, <c>Enter</c>/<c>Exit</c>, …) was silently dropped on the floor. This applies
    /// EXACTLY the base-<see cref="Element"/> subset WriteColumns already bakes for every other element type — never
    /// the BoxEl-only layout-shape props (Fill/Margin/Shrink/Grow/Animate/…; those have no meaning on a transparent
    /// anchor and stay behind the control's <c>Parts[PartRoot]</c> door, component-props-contract.md "Base props on the
    /// embed"). Called from both <see cref="MountComponent"/> (<paramref name="old"/> null) and the <see cref="Update"/>
    /// reuse branch (<paramref name="old"/> = the previous <see cref="ComponentEl"/>, used only to derive
    /// <c>isMount</c> — a reused anchor never changes identity, so there is no BoxEl-style "declared→identity" hand-off
    /// to detect here). Zero-alloc on the steady (no-op) path: every write below is either a no-op TryGetValue/flag
    /// check or a scalar column write, exactly like the analogous WriteColumns lines it mirrors.
    /// Also applied to the other layout-transparent boundary kinds Mount routes past WriteColumns (Show / For / KeepAlive /
    /// SkelRegion / Ctx.Provide), which dropped the same props for the same reason.</summary>
    private void WriteAnchorColumns(NodeHandle node, Element ce, Element? old)
    {
        bool isMount = old is null;
        _scene.NoteCaptureChanged((int)node.Raw.Index);
        int nodeIdx = (int)node.Raw.Index;

        // Shared-element tag (mirrors WriteColumns' MorphId block verbatim — a component anchor can be a Hero participant).
        if (ce.MorphId is { Length: > 0 } morphKey)
        {
            if (_morphKeyByNode.TryGetValue(nodeIdx, out string? oldMorphKey) && oldMorphKey != morphKey)
                RemoveMorphKey(node, oldMorphKey);
            _morphKeyByNode[nodeIdx] = morphKey;
            Connected?.NoteTagged(node, morphKey);
            _keyNode[morphKey] = node;
        }
        else if (_morphKeyByNode.Remove(nodeIdx, out string? oldMorphKey))
        {
            RemoveMorphKey(node, oldMorphKey);
            Connected?.CaptureOnLeave(node, removeTag: true);
        }
        // FLIP relativeTarget.
        if (ce.RelativeTo is { Length: > 0 } relKey) _relativeKey[nodeIdx] = relKey; else _relativeKey.Remove(nodeIdx);

        // Scroll-linked effects (sticky / parallax / fade) + the node's own ScrollScope name.
        BakeScrollEffects(node, ce);

        // Wheel routing.
        _scene.SetWheelTarget(node, ce.WheelTarget);

        // Per-child entrance stagger (this anchor delaying its OWN Enter — the child-side stagger read is
        // StaggerDelayMs walking the PARENT's row, unaffected by this node's own entry here).
        if (ce.Stagger > 0f) _childStagger[nodeIdx] = ce.Stagger; else _childStagger.Remove(nodeIdx);

        // P1 presence: static (unbound) Visible write — a bound channel is instead owned by its BindNode effect (wired
        // at mount, re-wired bound→bound on reuse below — the same contract every other element type gets).
        if (!ce.Visible.IsBound) ApplyPresenceStatic(node, ce.Visible.Value);

        // Declarative Enter/Exit/Layout: a ComponentEl carries no legacy BoxEl.Animate, so — unlike the BoxEl case in
        // WriteColumns, which only takes this branch when Animate is unset — the declarative path is unconditional here.
        Anim?.ClearTransition(node);
        _scene.Unmark(node, NodeFlags.BoundsAnimated);
        if (Anim is { } danim && SynthesizeDeclarative(node, ce) is { } dt)
        {
            danim.SetTransition(node, dt);
            if ((dt.Channels & TransitionChannels.Bounds) != 0) _scene.Mark(node, NodeFlags.BoundsAnimated);
            if (isMount && dt.Enter.Active)
            {
                danim.SeedEnter(node, dt.Enter, dt);
                if (dt.Size == SizeMode.Reflow) danim.PendingEnterReflow.Add(node);
            }
        }

        // Gesture-state targets (WhileHover/WhilePressed/WhileFocus): the anchor has no authored OffsetX/ScaleX/Rotation/
        // Opacity/Blur of its own (those are BoxEl-only), so its rest pose is the identity delta — `new MotionTarget()`,
        // NEVER `default` (MotionTarget's parameterless ctor sets Scale/Opacity to 1; default(MotionTarget) zeroes them
        // — MotionTok.cs's documented gotcha). All-null clears the row, so this is inert for the common case.
        Anim?.SetInteractTargets(nodeIdx, ce.WhileHover, ce.WhilePressed, ce.WhileFocus,
            new MotionTarget(), ce.Transition ?? MotionTok.ControlFaster);

        // Bound Visible wiring (BindPresence, inside BindNode) is created at mount — mirrors Mount's separate
        // `WriteColumns(...); BindNode(...);` pair. The reuse branch never re-creates it; it RE-WIRES the existing effect
        // when the re-rendered embed binds Visible with a new thunk/signal (bound→bound; an equal payload is a no-op, so
        // this stays allocation-free across repeated parent re-renders — gate.reconcile.componentel.alloc).
        if (isMount) BindNode(node, ce);
        else RewireBinds(node, ce);
    }

    /// <summary>Bake an element's declarative <see cref="FluentGpu.Scroll.Effects.ScrollEffectSpec"/>s onto the scene's
    /// scroll-effect table (scroll rework §8): resolve each named sticky scope to the nearest ancestor carrying that
    /// <see cref="Element.ScrollScope"/> (0 = the scroller's whole content), and record the node's own scope name. The
    /// host evaluates the rows after layout — UI-side paint for hit-test/publish and coverage rows for the poser.
    /// Re-bake is wholesale so a prop change self-cleans.</summary>
    private void BakeScrollEffects(NodeHandle node, Element el)
    {
        _scene.SetScrollScope(node, el.ScrollScope);
        var specs = el.ScrollEffects;
        int nodeIdx = (int)node.Raw.Index;
        _scene.TryGetScrollEffects(nodeIdx, out var oldRows);
        if (specs is null || specs.Length == 0)
        {
            if (oldRows is not null)
            {
                _scene.SetScrollEffects(node, null);
                _scene.SetScrollEffectEngaged(node, null);
                ResetScrollEffectPaint(node, oldRows, null);
            }
            return;
        }
        var rows = new FluentGpu.Scroll.Effects.ScrollEffect[specs.Length];
        FluentGpu.Signals.Signal<bool>?[]? engaged = null;
        for (int i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            int scope = 0;
            if (spec.Scope is { } name)
            {
                var scopeNode = _scene.FindScrollScope(node, name);
                if (!scopeNode.IsNull) scope = (int)scopeNode.Raw.Index;
            }
            rows[i] = spec.Effect with { ScopeNode = scope };
            if (spec.Engaged is { } edge) (engaged ??= new FluentGpu.Signals.Signal<bool>?[specs.Length])[i] = edge;
        }
        if (oldRows is not null) ResetScrollEffectPaint(node, oldRows, rows);
        _scene.SetScrollEffects(node, rows);
        _scene.SetScrollEffectEngaged(node, engaged);
    }

    /// <summary>A node whose scroll effects no longer write a paint channel (all effects dropped, or a re-bake that lost
    /// that channel) lands back on its authored paint for it: identity transform, no clip, the laid-out height, no child
    /// shift. Channels the new rows still own are left alone — the host re-poses them this frame.</summary>
    private void ResetScrollEffectPaint(NodeHandle node, FluentGpu.Scroll.Effects.ScrollEffect[] oldRows, FluentGpu.Scroll.Effects.ScrollEffect[]? newRows)
    {
        ref NodePaint p = ref _scene.Paint(node);
        bool any = false;
        for (int i = 0; i < oldRows.Length; i++)
        {
            var ch = oldRows[i].Channel;
            if (newRows is not null && OwnsChannel(newRows, ch)) continue;
            switch (ch)
            {
                case FluentGpu.Scroll.Effects.EffectChannel.TransX:
                case FluentGpu.Scroll.Effects.EffectChannel.TransY:
                case FluentGpu.Scroll.Effects.EffectChannel.ScaleXY:
                    if (newRows is null || !OwnsTransform(newRows)) { p.LocalTransform = Foundation.Affine2D.Identity; any = true; }
                    break;
                case FluentGpu.Scroll.Effects.EffectChannel.ClipTop: p.ClipRect = RectF.Infinite; any = true; break;
                case FluentGpu.Scroll.Effects.EffectChannel.PresentedH: p.PresentedH = float.NaN; any = true; break;
                case FluentGpu.Scroll.Effects.EffectChannel.ChildShiftY: p.ChildShiftY = 0f; any = true; break;
                case FluentGpu.Scroll.Effects.EffectChannel.ClipBottom: p.ClipRect = RectF.Infinite; any = true; break;
            }
        }
        // A node that no longer carries a pinning (Sticky) row is no longer pinned (the flag lifts it above its siblings
        // in paint; a StickyClip never sets it).
        if ((_scene.Flags(node) & NodeFlags.StickyPinned) != 0 && (newRows is null || !HasSticky(newRows)))
        {
            _scene.Unmark(node, NodeFlags.StickyPinned);
            any = true;
        }
        if (any) _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);

        static bool HasSticky(FluentGpu.Scroll.Effects.ScrollEffect[] rows)
        {
            for (int i = 0; i < rows.Length; i++)
                if (FluentGpu.Scroll.Effects.ScrollEffectEval.PinsAboveSiblings(rows[i].Kind)) return true;
            return false;
        }
        static bool OwnsChannel(FluentGpu.Scroll.Effects.ScrollEffect[] rows, FluentGpu.Scroll.Effects.EffectChannel ch)
        {
            for (int i = 0; i < rows.Length; i++) if (rows[i].Channel == ch) return true;
            return false;
        }
        static bool OwnsTransform(FluentGpu.Scroll.Effects.ScrollEffect[] rows)
        {
            for (int i = 0; i < rows.Length; i++) if (FluentGpu.Scroll.Effects.ScrollEffectEval.IsTransformChannel(rows[i].Channel)) return true;
            return false;
        }
    }

    /// <summary>Detach realized bound slots into the exit-orphan layer before their backing items disappear, then remap
    /// every retained survivor to its post-removal logical index. The mutation callback is invoked exactly once and is
    /// deliberately scheduled before survivor signal writes, so the owning plan/count computation queues ahead of the
    /// recycled row computations.</summary>
    private void BeginVirtualRemoval(NodeHandle viewport, IReadOnlyList<int> removed, EnterExit exit,
                                     MotionTokenId motion, float staggerMs, Action commit)
    {
        if (removed.Count == 0 || viewport.IsNull || !_scene.IsLive(viewport)
            || !_virtuals.TryGetValue(viewport, out var entry) || entry.El?.RowBind is null)
        {
            commit();
            return;
        }

        var ve = entry.El!;
        var anim = Anim;
        var motionDef = MotionTok.Get(motion);
        bool animate = exit.Active && anim is not null;
        // The stagger deals the rows that actually exit, in index order. Ranked against the whole removed set, a row
        // scrolled deep into the list waited behind every unseen removed item above it: seconds at full opacity over the
        // survivors sliding up, then the 2 s orphan backstop cut it with no exit at all. Collected before any detach.
        int[]? dealt = animate && staggerMs > 0f ? RealizedRemoved(entry, removed) : null;

        void Retire(in BoundSlot slot)
        {
            int index = slot.Index.Peek();
            ve.OnItemClearing?.Invoke(index);
            var root = slot.Root;
            if (root.IsNull || !_scene.IsLive(root)) return;
            UnmountSubtree(root);
            if (animate)
            {
                anim!.CancelAll(root);
                _scene.Orphan(root);
                anim!.SeedExit(root, exit, in motionDef, dealt is null ? 0f : staggerMs * RemovedRank(index, dealt));
            }
            else _scene.FreeSubtree(root);
            _reconciled = true;
        }

        static void DetachRemoved(List<BoundSlot>? slots, IReadOnlyList<int> indices, ActionRef<BoundSlot> retire)
        {
            if (slots is null) return;
            for (int i = slots.Count - 1; i >= 0; i--)
            {
                var slot = slots[i];
                if (!ContainsRemoved(slot.Index.Peek(), indices)) continue;
                retire(in slot);
                slots.RemoveAt(i);
            }
        }

        DetachRemoved(entry.Slots, removed, Retire);
        DetachRemoved(entry.PrefixSlots, removed, Retire);

        commit();

        // Parked keep-alive rows are invisible, so they hard-retire instead of materializing a global exit ghost.
        if (entry.Kept is { Count: > 0 } kept)
        {
            var snapshot = new KeyValuePair<int, KeptSlot>[kept.Count];
            int n = 0;
            foreach (var pair in kept) snapshot[n++] = pair;
            kept.Clear();
            for (int i = 0; i < n; i++)
            {
                var pair = snapshot[i];
                if (ContainsRemoved(pair.Key, removed))
                {
                    ve.OnItemClearing?.Invoke(pair.Key);
                    if (_scene.IsLive(pair.Value.Root))
                    {
                        UnmountSubtree(pair.Value.Root);
                        _scene.FreeSubtree(pair.Value.Root);
                    }
                    continue;
                }
                int mapped = pair.Key - CountBefore(pair.Key, removed);
                pair.Value.Index.Value = mapped;
                if (mapped != pair.Key) ve.OnItemIndexChanged?.Invoke(pair.Key, mapped);
                kept[mapped] = pair.Value;
            }
        }

        RemapSurvivors(entry.PrefixSlots, removed, ve);
        RemapSurvivors(entry.Slots, removed, ve);
        entry.PrevFirst = Math.Max(0, entry.PrevFirst - CountBefore(entry.PrevFirst, removed));
        entry.PrevLen = entry.Slots?.Count ?? 0;

        if (_scene.TryGetScroll(viewport, out var current))
        {
            ref ScrollState scroll = ref _scene.ScrollRef(viewport);
            scroll.FirstRealized = entry.PrevFirst;
            scroll.LastRealized = entry.PrevFirst + entry.PrevLen;
            if (!current.ContentNode.IsNull && _scene.IsLive(current.ContentNode))
                MarkLayoutShape(current.ContentNode);
        }
        _scene.Mark(viewport, NodeFlags.VirtualRangeDirty);
        _realizeProgress = true;
    }

    /// <summary>Seed or retarget one contiguous disclosure range. The backing list stays in its EXPANDED shape while
    /// progress moves; the composing control owns insert-before-expand and collapse-commit-after-settle ordering.</summary>
    private bool BeginVirtualDisclosure(NodeHandle viewport, int first, int count, bool expanding)
    {
        if (viewport.IsNull || !_scene.IsLive(viewport) || Anim is null
            || !_virtuals.TryGetValue(viewport, out var entry) || entry.El?.RowBind is null
            || !_scene.TryGetScroll(viewport, out var snapshot) || snapshot.Orientation != 0
            || snapshot.Layout is null || first < 0 || count <= 0 || first + count > snapshot.ItemCount)
            return false;

        float cross = MathF.Max(1f, _scene.Bounds(viewport).W);
        RectF firstRect = snapshot.Layout.ItemRect(first, cross);
        RectF lastRect = snapshot.Layout.ItemRect(first + count - 1, cross);
        float top = firstRect.Y;
        float extent = lastRect.Bottom - top;
        if (!float.IsFinite(top) || !float.IsFinite(extent) || extent <= 0f) return false;

        float from = float.IsFinite(snapshot.DisclosureT) ? Math.Clamp(snapshot.DisclosureT, 0f, 1f)
                                                          : expanding ? 0f : 1f;
        if (!_scene.BeginVirtualDisclosure(viewport, first, count, top, extent, from)) return false;
        Anim.SeedValue(viewport, AnimChannel.DisclosureProgress, expanding ? 1f : 0f,
            expanding ? MotionTokenId.DisclosureExpand : MotionTokenId.DisclosureCollapse, from: from);
        return true;
    }

    /// <summary>Force the active disclosure to its requested endpoint. Used before a different logical band starts.</summary>
    private void CompleteVirtualDisclosure(NodeHandle viewport, bool expanded)
    {
        if (viewport.IsNull || !_scene.IsLive(viewport) || !_scene.TryGetScroll(viewport, out var sc)
            || !float.IsFinite(sc.DisclosureT)) return;
        Anim?.Cancel(viewport, AnimChannel.DisclosureProgress);
        _scene.SetVirtualDisclosureProgress(viewport, expanded ? 1f : 0f);
    }

    /// <summary>Release the presentation after the expanded model has reached the same resting geometry.</summary>
    private void ClearVirtualDisclosure(NodeHandle viewport)
    {
        if (viewport.IsNull || !_scene.IsLive(viewport) || !_scene.TryGetScroll(viewport, out _)) return;
        Anim?.Cancel(viewport, AnimChannel.DisclosureProgress);
        _scene.ClearVirtualDisclosure(viewport);
    }

    private delegate void ActionRef<T>(in T value);

    private static void RemapSurvivors(List<BoundSlot>? slots, IReadOnlyList<int> removed, VirtualListEl ve)
    {
        if (slots is null) return;
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            int old = slot.Index.Peek();
            int mapped = old - CountBefore(old, removed);
            if (mapped == old) continue;
            slot.Index.Value = mapped;
            ve.OnItemIndexChanged?.Invoke(old, mapped);
        }
    }

    private static bool ContainsRemoved(int index, IReadOnlyList<int> removed)
    {
        int lo = 0, hi = removed.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1), value = removed[mid];
            if (value == index) return true;
            if (value < index) lo = mid + 1; else hi = mid - 1;
        }
        return false;
    }

    private static int CountBefore(int index, IReadOnlyList<int> removed)
    {
        int lo = 0, hi = removed.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (removed[mid] < index) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static int RemovedRank(int index, IReadOnlyList<int> removed)
    {
        int lo = 0, hi = removed.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (removed[mid] < index) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>The removed indices that hold a realized slot (window + retained prefix), sorted: the rows a removal
    /// stagger deals over. Unseen removed items take no turn.</summary>
    private static int[] RealizedRemoved(VirtualEntry entry, IReadOnlyList<int> removed)
    {
        int n = Collect(entry.Slots, removed, null, 0) + Collect(entry.PrefixSlots, removed, null, 0);
        if (n == 0) return Array.Empty<int>();
        var dealt = new int[n];
        Collect(entry.PrefixSlots, removed, dealt, Collect(entry.Slots, removed, dealt, 0));
        Array.Sort(dealt);
        return dealt;

        static int Collect(List<BoundSlot>? slots, IReadOnlyList<int> removed, int[]? into, int at)
        {
            if (slots is null) return at;
            for (int i = 0; i < slots.Count; i++)
            {
                int index = slots[i].Index.Peek();
                if (!ContainsRemoved(index, removed)) continue;
                if (into is not null) into[at] = index;
                at++;
            }
            return at;
        }
    }

    /// <summary>Build a LayoutTransition from the new declarative Element fields (Enter/Exit/Transition/Layout/Stagger)
    /// so the rework's authoring surface routes through the existing FLIP/enter/exit seed lifecycle. Null when the node
    /// declares none. (While* gesture states are wired separately; this covers Enter/Exit/Layout + the parent Stagger.)</summary>
    private LayoutTransition? SynthesizeDeclarative(NodeHandle node, Element el)
    {
        bool hasEnter = el.Enter is not null, hasExit = el.Exit is not null;
        // A parent's Stagger delays this child's ENTER only, so it rides EnterExit.DelayMs (enter-only). Baked into
        // LayoutTransition.DelayMs it also held every later Exit (SeedExit) and FLIP move (AnimateBounds) by index × stagger.
        EnterExit enter = default;
        if (el.Enter is { } e) enter = e with { Active = true, DelayMs = e.DelayMs + StaggerDelayMs(node) };
        if (el.Layout is { } lt)
            return (!hasEnter && !hasExit) ? lt
                 : lt with
                   {
                       Enter = hasEnter ? enter : lt.Enter,
                       Exit = hasExit ? (el.Exit!.Value with { Active = true }) : lt.Exit,
                   };
        if (!hasEnter && !hasExit) return null;
        TransitionDynamics dyn = el.Transition is { } m ? m.ToDynamics() : TransitionDynamics.Default;
        return new LayoutTransition(
            TransitionChannels.Opacity, dyn, SizeMode.Auto,
            Enter: enter,
            Exit: hasExit ? (el.Exit!.Value with { Active = true }) : default);
    }

    /// <summary>The authored static pose an Enter settles on (<see cref="EnterRest"/>): a BoxEl's unbound Opacity,
    /// OffsetX/Y, ScaleX/Y and Blur, or its unbound Transform matrix. The matrix wins over the floats, exactly as
    /// WriteColumns applies them. A bound channel belongs to its bind effect, so it rests at identity, as
    /// SetInteractTargets' rest pose does. Only a box carries a static pose.</summary>
    private static EnterRest EnterRestOf(Element el)
    {
        if (el is not BoxEl b) return EnterRest.Identity;
        float op = b.Opacity.IsBound ? 1f : b.Opacity.Value;
        if (b.Transform.IsBound) return EnterRest.Identity with { Opacity = op, Blur = b.Blur };
        Affine2D m = b.Transform.Value;
        if (m != default)
            return new EnterRest(m.Dx, m.Dy, MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12),
                                 MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22), op, b.Blur);
        return new EnterRest(b.OffsetX, b.OffsetY, b.ScaleX, b.ScaleY, op, b.Blur);
    }

    /// <summary>A parent's <see cref="FluentGpu.Dsl.Element.Stagger"/> delays each ENTERING child's Enter by (its ordinal
    /// among the children entering in that diff × stagger ms): a list/shelf whose new items reveal in sequence. A diff
    /// appends new children after every old one (removal comes later), so inside the diff in flight the walk is rebased
    /// past the old children; a first mount appends in order, where the live index already is that ordinal. O(siblings)
    /// at mount (not the hot path); returns 0 when no parent staggers.</summary>
    private float StaggerDelayMs(NodeHandle node)
    {
        NodeHandle parent = _scene.Parent(node);
        if (parent.IsNull || !_childStagger.TryGetValue((int)parent.Raw.Index, out float per) || per <= 0f) return 0f;
        int i = 0;
        for (var c = _scene.FirstChild(parent); !c.IsNull && c.Raw.Index != node.Raw.Index; c = _scene.NextSibling(c)) i++;
        if (parent == _staggerDiffParent && i >= _staggerDiffBase) i -= _staggerDiffBase;
        return i * per;
    }

    /// <summary>Resolve a node's <see cref="FluentGpu.Dsl.Element.RelativeTo"/> to the live anchor node (the one carrying
    /// that MorphId) it should FLIP relative to — the host calls this at projection capture. Null when the node has no
    /// relativeTarget or its anchor isn't currently live (→ the default parent-relative FLIP).</summary>
    internal NodeHandle ResolveRelativeTarget(NodeHandle node)
    {
        if (!_relativeKey.TryGetValue((int)node.Raw.Index, out string? key)) return default;
        if (!_keyNode.TryGetValue(key, out NodeHandle target) || target.IsNull || !_scene.IsLive(target)) return default;
        return target;
    }

    private void RemoveMorphKey(NodeHandle node, string key)
    {
        if (_keyNode.TryGetValue(key, out NodeHandle current) && current.Equals(node)) _keyNode.Remove(key);
    }

    /// <summary>True while this node's implicit BrushTransition is still fading its FILL away from a visible colour —
    /// the displayed colour is <c>LerpLinear(FillFrom, Fill, T)</c>, which is on screen even when the target is
    /// transparent (see the BoxEl VisualKind hold in <see cref="WriteColumns"/>). SparsePaint gates the sparse lookup:
    /// <c>SetBrushAnim</c> sets it, so a node that never faded pays one flag read.</summary>
    private bool FillFadeInFlight(NodeHandle node)
        => (_scene.Flags(node) & NodeFlags.SparsePaint) != 0
           && _scene.TryGetBrushAnim(node, out var ba)
           && (ba.Channels & BrushAnim.FillBit) != 0 && ba.FillFrom.A > 0f && ba.T < 1f;

    private void WriteColumns(NodeHandle node, Element el, bool isMount, Element? old = null)
    {
        // P8 (Operation ultra-fast GPU engine, scroll-root-cause-2026-09-23 §5): a reconciler column write rewrites
        // LayoutInput / NodePaint / InteractionInfo / NodeFlags / the sparse visual tables for THIS node only — every
        // write below (and every Mark/Unmark/Set*/Clear* it calls) targets `node`'s own row, never a sibling's or an
        // ancestor's. That is exactly the capture ledger's granularity (NoteCaptureChanged is per-node, not per-
        // column: CaptureNode re-copies a marked node's ENTIRE captured column set, so marking the node once covers
        // every field this method can touch for it). So a reconcile ledgers precisely — this node's row is stale as
        // of the next publication — instead of forcing every publisher slot's NEXT capture to recopy the whole scene.
        // A virtualization recycle rebinding one row no longer costs O(scene) here; only a genuinely unenumerable
        // change (a column reallocation, a store compact) still takes NoteBulkMutation, elsewhere.
        _scene.NoteCaptureChanged((int)node.Raw.Index);
        // Snapshot layout shape before column writes so an update that only touches paint can skip LayoutDirty
        // (RunComponent no longer force-marks). float.Equals so NaN auto-sizes compare equal.
        LayoutInput layoutBefore = default;
        NodeFlags layoutFlagsBefore = 0;
        bool hadGrid = false;
        GridSpec gridBefore = default;
        if (!isMount)
        {
            layoutBefore = _scene.Layout(node);
            layoutFlagsBefore = _scene.Flags(node) & (NodeFlags.ClipsToBounds | NodeFlags.LayoutBoundary | NodeFlags.ZStack);
            hadGrid = _scene.TryGetGrid(node, out gridBefore);
        }

        // Shared-element (connected-animation) tag: a node carrying MorphId is a Hero participant — its laid-out rect +
        // art are tracked so they fly between routes. Runs for every element type (cover Image, skeleton/cover Box).
        int nodeIdx = (int)node.Raw.Index;
        if (el.MorphId is { Length: > 0 } morphKey)
        {
            if (_morphKeyByNode.TryGetValue(nodeIdx, out string? oldMorphKey) && oldMorphKey != morphKey)
                RemoveMorphKey(node, oldMorphKey);
            _morphKeyByNode[nodeIdx] = morphKey;
            Connected?.NoteTagged(node, morphKey);
            _keyNode[morphKey] = node;
        }
        else if (_morphKeyByNode.Remove(nodeIdx, out string? oldMorphKey))
        {
            RemoveMorphKey(node, oldMorphKey);
            Connected?.CaptureOnLeave(node, removeTag: true);
        }
        // FLIP relativeTarget: record the follower → anchor-key link (resolved live by ResolveRelativeTarget at capture).
        if (el.RelativeTo is { Length: > 0 } relKey) _relativeKey[nodeIdx] = relKey; else _relativeKey.Remove(nodeIdx);

        // Scroll-linked effects (sticky / parallax / fade / scale): baked onto the scene's effect table for every element type.
        BakeScrollEffects(node, el);

        // Wheel routing (Element.WheelTarget): a header names the scroller its wheel input glides — every element type,
        // the node-keyed sparse row InputDispatcher.RouteWheelTarget reads off the hit chain (null clears it).
        _scene.SetWheelTarget(node, el.WheelTarget);

        // Stagger (declarative): a parent records its per-child entrance delay; each child's SynthesizeDeclarative reads
        // it + the child's sibling index to delay that child's Enter (a staggered list/shelf reveal). Reconciler-local;
        // cleared when Stagger drops to 0. Set for every element type (any container can stagger its children).
        if (el.Stagger > 0f) _childStagger[(int)node.Raw.Index] = el.Stagger; else _childStagger.Remove((int)node.Raw.Index);

        // P1 presence (layout.md §4.7): static (unbound) Visible write, every element type. A bound channel is owned
        // by its BindPresence effect (Reconciler.Presence.cs) — the static write must not clobber it, same guard
        // shape as Fill/Opacity/Width/Height/Text above.
        if (!el.Visible.IsBound) ApplyPresenceStatic(node, el.Visible.Value);

        switch (el)
        {
            case BoxEl b:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                bool hasSurface = b.TabShape || b.Fill.IsBound || b.Fill.Value.A > 0f
                                  || b.HoverFill.IsBound || b.HoverFill.Value.A > 0f
                                  || b.PressedFill.IsBound || b.PressedFill.Value.A > 0f
                                  || b.BorderWidth > 0f || b.OnClick is not null || b.Gradient is not null || b.BorderBrush is not null;
                // A video hole outranks every surface kind: the node ERASES rather than paints, so any fill it declares is
                // irrelevant (see BoxEl.VideoHole). The slot token rides ImageId — the IconLayer PathId pun.
                paint.VisualKind = b.VideoHole ? VisualKind.Video : b.TabShape ? VisualKind.TabShape : hasSurface ? VisualKind.Box : VisualKind.None;
                if (b.VideoHole) paint.ImageId = b.VideoSurfaceId;

                // Implicit BrushTransition (WinUI, 83ms): a LIVE node re-rendered with a different fill/border cross-fades
                // from the previously-DISPLAYED color (mid-flight retargets stay continuous) instead of snapping.
                // A BOUND fill is excluded per-channel: its effect owns paint.Fill, so diffing against the static would
                // arm a phantom fade toward a color the channel doesn't own (the border sub-block is unaffected).
                bool fillOwned = !b.Fill.IsBound;
                bool borderOwned = !b.BorderColor.IsBound;
                float fillMs = ThemeTransitionOr(b.BrushTransitionMs);   // live re-theme overrides the element's NaN/own duration
                if (!isMount && !float.IsNaN(fillMs) && fillMs > 0f
                    && ((fillOwned && paint.Fill != b.Fill.Value)
                        || (borderOwned && paint.BorderColor != b.BorderColor.Value)))
                {
                    bool midFlight = _scene.TryGetBrushAnim(node, out var prev);
                    var ba = new BrushAnim { DurationMs = fillMs };
                    if (fillOwned && paint.Fill != b.Fill.Value)
                    {
                        ba.FillFrom = midFlight && (prev.Channels & BrushAnim.FillBit) != 0
                            ? ColorF.LerpLinear(prev.FillFrom, paint.Fill, prev.T)   // continue from the displayed color
                            : paint.Fill;
                        ba.Channels |= BrushAnim.FillBit;
                    }
                    if (borderOwned && paint.BorderColor != b.BorderColor.Value)
                    {
                        ba.BorderFrom = midFlight && (prev.Channels & BrushAnim.BorderBit) != 0
                            ? ColorF.LerpLinear(prev.BorderFrom, paint.BorderColor, prev.T)
                            : paint.BorderColor;
                        ba.Channels |= BrushAnim.BorderBit;
                    }
                    _scene.SetBrushAnim(node, ba);
                    _scene.Mark(node, NodeFlags.PaintDirty);
                    Anim?.SeedBrushFade(node, ba.DurationMs);   // drive the cross-fade T via the unified engine (no separate ticker)
                }

                // Guarded like Opacity/Width/Height/Text: a bound fill is owned by its effect — the static must never
                // clobber it on an update between signal fires (mount was safe only because the bind fires after this).
                if (fillOwned) paint.Fill = b.Fill.Value;
                // A fade TO transparent must be DRAWN until it lands. With no other surface a transparent fill made the
                // node VisualKind.None above, so the recorder never emitted it: colour→colour eased over the whole
                // BrushTransition while colour→"no tint" snapped off in one frame, and the NEXT fade then started from a
                // colour that was never on screen (a same-colour hop A→none→A blinked). Flutter's AnimatedContainer /
                // ColorTween and a CSS background-color transition both keep painting the interpolated colour all the way
                // down to alpha 0. The row retires at T≥1 (SceneStore.SetBrushAnimT); the first reconcile after that drops
                // the node back to None, and until then the recorder draws nothing for the settled transparent box.
                if (paint.VisualKind == VisualKind.None && FillFadeInFlight(node)) paint.VisualKind = VisualKind.Box;
                if (!b.HoverFill.IsBound) paint.HoverFill = b.HoverFill.Value;
                if (!b.PressedFill.IsBound) paint.PressedFill = b.PressedFill.Value;
                if (borderOwned) paint.BorderColor = b.BorderColor.Value;
                paint.HoverBorderColor = b.HoverBorderColor;
                paint.PressedBorderColor = b.PressedBorderColor;
                // Static (unbound) Validation: a bound channel owns paint.ValidationBorder via its effect, so only the
                // static form asserts here (guarded like Fill above) — the common unbound case resets it to none.
                if (!b.Validation.IsBound) paint.ValidationBorder = b.Validation.Value == ValidationState.Error ? Tok.SystemFillCritical : default;
                paint.BorderWidth = b.BorderWidth;
                paint.BorderDashOn = b.BorderDashOn;
                paint.BorderDashOff = b.BorderDashOff;
                paint.TabFlareRadius = b.TabFlareRadius <= 0f ? 4f : b.TabFlareRadius;
                // Like Fill/Opacity: a bound corner set is owned by its bind effect — the static write must not clobber it.
                if (!b.Corners.IsBound) paint.Corners = b.Corners.Value;

                if (b.Shadow is { } sh) _scene.SetShadow(node, sh); else _scene.ClearShadow(node);
                if (b.Arc is { } arcSpec) _scene.SetArc(node, arcSpec); else _scene.ClearArc(node);
                if (b.Gradient is { } gr) _scene.SetGradient(node, gr); else _scene.ClearGradient(node);
                if (!b.RadialGradientCenter.IsBound)
                {
                    Point2 center = b.RadialGradientCenter.Value;
                    if (float.IsFinite(center.X) && float.IsFinite(center.Y)) _scene.SetRadialGradientCenter(node, center);
                    else _scene.ClearRadialGradientCenter(node);
                }
                if (b.GradientTo is { } gto) _scene.SetGradientTo(node, gto); else _scene.ClearGradientTo(node);
                if (!b.GradientMix.IsBound) _scene.SetGradientMix(node, b.GradientMix.Value);
                if (b.BorderBrush is { } bb) _scene.SetBorderBrush(node, bb); else _scene.ClearBorderBrush(node);
                if (b.HoverGradient is { } hg) _scene.SetHoverGradient(node, hg); else _scene.ClearHoverGradient(node);
                if (b.PressedGradient is { } pg) _scene.SetPressedGradient(node, pg); else _scene.ClearPressedGradient(node);
                if (b.HoverBorderBrush is { } hbb) _scene.SetHoverBorderBrush(node, hbb); else _scene.ClearHoverBorderBrush(node);
                if (b.PressedBorderBrush is { } pbb) _scene.SetPressedBorderBrush(node, pbb); else _scene.ClearPressedBorderBrush(node);
                if (b.Acrylic is { } ac) _scene.SetAcrylic(node, ac); else _scene.ClearAcrylic(node);
                // A feedback box IS a repaint boundary, at the feedback surface scale.
                _scene.SetRepaintBoundary(node, b.RepaintBoundary || b.Feedback is not null || b.CompositePose,
                    SceneStore.RasterDown(b.Feedback is { } fspec ? fspec.RasterScale : b.RasterScale), b.CompositePose && b.Feedback is null);
                _scene.SetBlend(node, b.Blend, b.LayerBlend);
                if (b.Feedback is { } fb)
                {
                    // the bound channels own their halves (BindNode): seed from the previous state when bound
                    Affine2D warp = b.FeedbackTransform.IsBound && _scene.TryGetFeedback(node, out var prevFb) ? prevFb.Warp : b.FeedbackTransform.IsBound ? Affine2D.Identity : b.FeedbackTransform.Value;
                    float decay = b.FeedbackDecay.IsBound && _scene.TryGetFeedback(node, out var prevFd) ? prevFd.Decay : b.FeedbackDecay.IsBound ? float.NaN : b.FeedbackDecay.Value;
                    _scene.SetFeedback(node, new FeedbackState(fb, warp, decay));
                }
                else _scene.SetFeedback(node, null);
                if (b.EdgeFade is { } bef) _scene.SetEdgeFade(node, bef); else _scene.ClearEdgeFade(node);
                _scene.SetHitTestPassThrough(node, b.HitTestPassThrough ? node : NodeHandle.Null);   // self = yield to behind, except own children
                _scene.SetBlocksBackgroundScroll(node, b.BlocksBackgroundScroll);

                // Transform origin (used by static + animated scale/rotate; default centre). Set unconditionally so an
                // AnimEngine ScaleX/Y track or a TransformBind pivots about the requested origin (e.g. a menu's top edge).
                paint.OriginX = b.TransformOriginX;
                paint.OriginY = b.TransformOriginY;

                // Static transform/opacity ONLY when the element declares one AND there's no transform binding/animation
                // owning the channel (else a re-render would reset the bound/animated value to identity each frame).
                // A static transform has TWO spellings, in precedence order: an explicit unbound MATRIX
                // (Transform = Affine2D.Translation(...)) WINS over the decomposed OffsetX/Y + Scale + Rotation floats —
                // the same "one transform owner per node" rule the BOUND path already states. Until this existed an
                // unbound Transform was silently DROPPED (its Value was never read), so an authored
                // `Transform = Affine2D.Translation(8, -8)` compiled, ran, and moved nothing.
                bool tfUnbound = !b.Transform.IsBound;
                bool staticMatrix = tfUnbound && b.Transform.Value != default;
                bool staticDecomposed = tfUnbound
                    && (b.OffsetX != 0f || b.OffsetY != 0f || b.ScaleX != 1f || b.ScaleY != 1f || b.Rotation != 0f);
                AssertSingleTransformOwner(b, staticMatrix, staticDecomposed);
                Affine2D restTf = Affine2D.Identity;   // the authored pose a position FLIP settles on (AnimEngine.SetRestTransform)
                if (staticMatrix)
                {
                    restTf = b.Transform.Value;
                    paint.LocalTransform = restTf;
                }
                else if (staticDecomposed)
                {
                    var tf = Affine2D.Translation(b.OffsetX, b.OffsetY);
                    if (b.Rotation != 0f) tf = tf.Multiply(Affine2D.Rotation(b.Rotation * (MathF.PI / 180f)));
                    if (b.ScaleX != 1f || b.ScaleY != 1f) tf = tf.Multiply(Affine2D.Scale(b.ScaleX, b.ScaleY));
                    paint.LocalTransform = tf;
                    restTf = tf;
                }
                // Static→identity hand-off: when the PREVIOUS element declared a static transform and this one
                // declares none, clear the stale static — the in-place differ can morph e.g. a rail (OffsetY=14)
                // into a plain track box, which otherwise keeps painting/hit-testing 14px off forever (the ranged-
                // slider tooltip hover flap). Identity-declared elements still leave ANIM-owned matrices alone:
                // this writes only on the declared-static → declared-identity transition, never per re-render.
                // Covers BOTH static spellings, so dropping an authored matrix clears it exactly like dropping OffsetY.
                else if (tfUnbound && old is BoxEl ob && !ob.Transform.IsBound
                         && (ob.Transform.Value != default
                             || ob.OffsetX != 0f || ob.OffsetY != 0f || ob.ScaleX != 1f || ob.ScaleY != 1f || ob.Rotation != 0f))
                {
                    paint.LocalTransform = Affine2D.Identity;
                }
                // Re-assert unconditionally (like Width/Fill): gating on != 1f made an Opacity 0→1 update a no-op,
                // so a node hidden by a prior render could never be shown again (the ProgressRing IsActive flip).
                if (!b.Opacity.IsBound) paint.Opacity = b.Opacity.Value;
                paint.HoverOpacity = b.HoverOpacity;
                paint.PressedOpacity = b.PressedOpacity;
                paint.OpacityGroup = b.OpacityGroup;
                paint.BlurSigma = b.Blur;   // self-blur (Expressive Motion Kit); phase-7 AnimChannel.Blur overrides for animated nodes



                bool seedHoverOnMount = false;
                if (b.HoverScale != 1f || b.PressScale != 1f || !float.IsNaN(b.HoverOpacity) || !float.IsNaN(b.PressedOpacity)
                    || !float.IsNaN(b.HoverDurationMs) || !float.IsNaN(b.PressDurationMs))
                {
                    ref InteractionAnim ia = ref _scene.InteractRef(node);
                    ia.HoverScale = b.HoverScale;
                    ia.PressScale = b.PressScale;
                    ia.HoverDurationMs = float.IsNaN(b.HoverDurationMs) ? InteractionAnim.ControlFasterMs : b.HoverDurationMs;
                    ia.PressDurationMs = float.IsNaN(b.PressDurationMs) ? InteractionAnim.ControlFasterMs : b.PressDurationMs;
                    ia.HoverEasing = b.HoverEasing;
                    ia.PressEasing = b.PressEasing;
                    // DEFERRED to the end of this case (see the seed block): the decision needs this node's OWN
                    // HandlerMask — "is it its own interaction scope?" — and the handler-mask writes have not run yet.
                    seedHoverOnMount = isMount;
                }

                ref LayoutInput li = ref _scene.Layout(node);
                li.Direction = b.Direction;
                li.Gap = b.Gap;
                li.Padding = b.Padding;
                li.Margin = b.Margin;
                // Like the TransformBind/OpacityBind guards above: a bound dimension is owned by its bind effect — a
                // re-render must not clobber it back to the static prop (the bind re-fires only when its signal changes).
                // A live SizeMode.Reflow track owns LayoutInput.Width/Height the same way, one layer down: it writes the
                // eased extent into that very field every tick. So hand the declared value to the TRACK instead of
                // stomping the interp — RecordDeclaredSize files it as the row's RestoreTo (the declared shadow that
                // SettleRestore writes back when the row settles), and retargets the track when a genuinely changed
                // non-NaN declared value arrives mid-flight. Leaving `li` alone is also what keeps the LayoutDirty gate
                // at the tail of WriteColumns honest: the interp→declared excursion this used to write read as a
                // layout-shape change on EVERY re-render during an animation. RecordDeclaredSize's first check is an
                // O(1) no-rows probe, so an unanimated node pays ~nothing for the detour.
                if (!b.Width.IsBound)
                {
                    if (Anim is null || !Anim.RecordDeclaredSize(node, AnimChannel.LayoutW, b.Width.Value)) li.Width = b.Width.Value;
                }
                if (!b.Height.IsBound)
                {
                    if (Anim is null || !Anim.RecordDeclaredSize(node, AnimChannel.LayoutH, b.Height.Value)) li.Height = b.Height.Value;
                }
                li.MinW = b.MinWidth; li.MinH = b.MinHeight; li.MaxW = b.MaxWidth; li.MaxH = b.MaxHeight;
                li.FlexGrow = b.Grow;
                li.FlexShrink = b.Shrink;
                li.FlexBasis = b.Basis;
                li.AlignSelf = b.AlignSelf; li.JustifySelf = b.JustifySelf;
                li.Justify = b.Justify;
                li.AlignItems = b.AlignItems;
                li.Wrap = b.Wrap;
                li.MeasureUnboundedWidth = b.MeasureUnboundedWidth;
                li.AspectRatio = b.AspectRatio;   // CSS aspect-ratio: FlexLayout.Measure derives the missing extent (Ui.AspectRatio)
                if (b.ZStack) _scene.Mark(node, NodeFlags.ZStack); else _scene.Unmark(node, NodeFlags.ZStack);
                // The clip may not be the AUTHOR's: a SizeMode.Reflow track adds ClipsToBounds for its own lifetime
                // (AnimFlags.ClipAdded) because it drives the node's LAYOUT size while the CONTENT is still arranged at
                // its natural size — without it, eased content paints straight over the sibling below. A mid-flight
                // re-render must not strip that; the row-teardown sink (FreeSlot → ReleaseReflowClip) is the ONE place
                // that releases it, at settle. The flags read short-circuits, so an already-unclipped node — every node
                // that never animates — pays no row walk.
                if (b.ClipToBounds)
                {
                    // Author takes (or keeps) the clip. If a reflow row added it first, transfer ownership
                    // (drop ClipAdded) so the row's teardown doesn't strip a clip the author now declares.
                    _scene.Mark(node, NodeFlags.ClipsToBounds);
                    Anim?.AdoptEngineClip(node);
                }
                else if ((_scene.Flags(node) & NodeFlags.ClipsToBounds) == 0 || Anim is null || !Anim.HasEngineOwnedClip(node))
                    _scene.Unmark(node, NodeFlags.ClipsToBounds);
                // A ClipPath IMPLIES ClipsToBounds (all 32 NodeFlags bits are taken, so the tier is discriminated by
                // the cold column, not a flag): Mark AFTER the line above so it wins over a ClipToBounds=false author.
                if (b.ClipPath is not null)
                {
                    _scene.SetClipPath(node, new ClipPathSpec(b.ClipPath, b.ClipPathRule, b.ClipPathViewBoxW, b.ClipPathViewBoxH));
                    _scene.Mark(node, NodeFlags.ClipsToBounds);
                }
                else if (old is BoxEl oldBoxClipPath && oldBoxClipPath.ClipPath is not null)
                    _scene.ClearClipPath(node);
                if (b.IsolateLayout) _scene.Mark(node, NodeFlags.LayoutBoundary); else _scene.Unmark(node, NodeFlags.LayoutBoundary);
                if (b.CounterScale) _scene.Mark(node, NodeFlags.CounterScaled); else _scene.Unmark(node, NodeFlags.CounterScaled);
                _scene.SetBoundsChangedHandler(node, b.OnBoundsChanged);
                _scene.SetFollowRect(node, b.FollowRect);   // F169: the engine's post-layout follow pass (SceneStore.Follow.cs)
                if (b.Animate is { } at && Anim is { } anim)
                {
                    anim.SetTransition(node, at);
                    anim.SetRestTransform(node, restTf);
                    _scene.Mark(node, NodeFlags.BoundsAnimated);
                    if (isMount && at.Enter.Active)
                    {
                        anim.SeedEnterOver(node, at.Enter, at, EnterRestOf(b));
                        // SizeMode.Reflow enter: ease the layout size 0→natural AFTER layout so neighbours reflow as it
                        // reveals (host-driven; the natural size isn't known here, pre-layout).
                        if (at.Size == SizeMode.Reflow) anim.PendingEnterReflow.Add(node);
                    }
                }
                else { Anim?.ClearTransition(node); _scene.Unmark(node, NodeFlags.BoundsAnimated); }
                // NEW declarative Enter/Exit/Layout (the base-Element authoring fields): when the author used the new
                // surface instead of `Animate`, synthesize a LayoutTransition and route through the PROVEN seed/orphan/
                // reclaim lifecycle — the mount-Enter seed here + the unmount-Exit path read the stashed spec. Guarded by
                // `b.Animate is null` + a declarative field set, so it cannot affect the existing (b.Animate) gates.
                if (b.Animate is null && Anim is { } danim && SynthesizeDeclarative(node, el) is { } dt)
                {
                    danim.SetTransition(node, dt);
                    danim.SetRestTransform(node, restTf);
                    if ((dt.Channels & TransitionChannels.Bounds) != 0) _scene.Mark(node, NodeFlags.BoundsAnimated);
                    if (isMount && dt.Enter.Active)
                    {
                        danim.SeedEnterOver(node, dt.Enter, dt, EnterRestOf(b));
                        if (dt.Size == SizeMode.Reflow) danim.PendingEnterReflow.Add(node);
                    }
                }
                // NEW declarative gesture-state targets (WhileHover/WhilePressed/WhileFocus): stashed for the
                // InteractionState priority resolver, which springs them on the input edge (AppHost wires
                // ApplyInteractionEdge). All-null clears the row, so it's inert for nodes that don't use While*.
                // The rest pose is ALSO stashed (every While* target is a DELTA on it — MotionTarget's rest-pose-
                // relative contract) so releasing every gesture state animates back to the node's authored static
                // transform instead of identity. MotionTarget.Scale is uniform, so a non-uniform static
                // ScaleX != ScaleY combined with While* is unrepresentable here — ScaleX wins (ScaleY is dropped).
                Anim?.SetInteractTargets((int)node.Raw.Index, b.WhileHover, b.WhilePressed, b.WhileFocus,
                    new MotionTarget
                    {
                        Scale = b.ScaleX, OffsetX = b.OffsetX, OffsetY = b.OffsetY, Rotation = b.Rotation,
                        Opacity = b.Opacity.IsBound ? 1f : b.Opacity.Value, Blur = b.Blur,
                    },
                    b.Transition ?? MotionTok.ControlFaster);
                // E15: bindable like Fill/Opacity — guarded the same way, a bound channel is owned by its bind effect
                // (wired below in BindNode) and the static write here must not clobber it back between signal fires.
                if (!b.HitTestVisible.IsBound)
                {
                    if (b.HitTestVisible.Value) _scene.Mark(node, NodeFlags.HitTestVisible); else _scene.Unmark(node, NodeFlags.HitTestVisible);
                }
                // Disabled gate (set unconditionally each reconcile — toggling IsEnabled must both set AND clear the bit).
                if (b.IsEnabled) _scene.Unmark(node, NodeFlags.Disabled); else _scene.Mark(node, NodeFlags.Disabled);

                ref InteractionInfo ii = ref _scene.Interaction(node);
                ii.Role = b.Role;
                // ClickRequestsContext (input-a11y §6.5.1) IMPLIES ClickBit — the node hit-tests / presses / hovers /
                // focuses exactly like an OnClick target — but the click-handler column stays NULL: the bit-16
                // discriminator redirects an activation into the context-request funnel (RequestContextFrom) instead of
                // firing a click. The two are mutually exclusive (a node is either a click target OR a context-invoker);
                // the prop wins if both are somehow set.
                System.Diagnostics.Debug.Assert(!(b.OnClick is not null && b.ClickRequestsContext),
                    "BoxEl: OnClick and ClickRequestsContext are mutually exclusive (ClickRequestsContext wins).");
                if (b.OnClick is not null || b.ClickRequestsContext)
                {
                    ii.HandlerMask |= InteractionInfo.ClickBit;
                    _scene.SetClickHandler(node, b.ClickRequestsContext ? null : b.OnClick);
                    _scene.Mark(node, NodeFlags.WantsPointer);
                }
                else
                {
                    ii.HandlerMask &= ~(uint)InteractionInfo.ClickBit;
                    _scene.SetClickHandler(node, null);
                }
                if (b.ClickRequestsContext) ii.HandlerMask |= InteractionInfo.ClickRequestsContextBit;
                else ii.HandlerMask &= ~InteractionInfo.ClickRequestsContextBit;

                // Element.HoverElevatePaint / HoverElevateClipRoot: paint-order-only discriminators (never hit-test
                // bits) the recorder reads — defer a hover-active child above its siblings / hoist it out of this
                // clip scope. Set unconditionally each reconcile (toggle both ways).
                if (b.HoverElevatePaint) ii.HandlerMask |= InteractionInfo.HoverElevatePaintBit;
                else ii.HandlerMask &= ~InteractionInfo.HoverElevatePaintBit;
                if (b.HoverElevateClipRoot) ii.HandlerMask |= InteractionInfo.HoverElevateClipRootBit;
                else ii.HandlerMask &= ~InteractionInfo.HoverElevateClipRootBit;

                // Element.BlocksDragArm: the drag-ARM barrier DragController.TryArm's upward walk stops at (a card's
                // play FAB must not be a handle for dragging the card). Discriminator only — same toggle-both-ways rule.
                if (b.BlocksDragArm) ii.HandlerMask |= InteractionInfo.BlocksDragArmBit;
                else ii.HandlerMask &= ~InteractionInfo.BlocksDragArmBit;

                // BoxEl.HoverScopeTransparent: a pointer listener the hover cascade / mount seed look through (the
                // ToolTip wrapper). Discriminator only — same toggle-both-ways rule.
                if (b.HoverScopeTransparent) ii.HandlerMask |= InteractionInfo.HoverScopeTransparentBit;
                else ii.HandlerMask &= ~InteractionInfo.HoverScopeTransparentBit;

                if (b.OnKeyDown is not null) { ii.HandlerMask |= InteractionInfo.KeyBit; _scene.SetKeyHandler(node, b.OnKeyDown); }
                else { ii.HandlerMask &= ~(uint)InteractionInfo.KeyBit; _scene.SetKeyHandler(node, null); }

                if (b.OnCharInput is not null) { ii.HandlerMask |= InteractionInfo.CharBit; _scene.SetCharHandler(node, b.OnCharInput); }
                else { ii.HandlerMask &= ~(uint)InteractionInfo.CharBit; _scene.SetCharHandler(node, null); }

                if (b.Repeats) ii.HandlerMask |= InteractionInfo.RepeatBit;
                else ii.HandlerMask &= ~(uint)InteractionInfo.RepeatBit;
                ii.RepeatDelayMs = b.RepeatDelayMs;       // NaN = WinUI DP defaults (500/33) — the ticker resolves
                ii.RepeatIntervalMs = b.RepeatIntervalMs;

                // WinUI KeyPress::Button bAcceptsReturn=false (CheckBox/RadioButton/ToggleSwitch): Space-only activation.
                if (!b.ActivateOnEnter) ii.HandlerMask |= InteractionInfo.NoEnterActivateBit;
                else ii.HandlerMask &= ~(uint)InteractionInfo.NoEnterActivateBit;
                // WinUI AllowFocusOnInteraction=False: a press never moves focus to (or past) this node.
                if (!b.AllowFocusOnInteraction) ii.HandlerMask |= InteractionInfo.NoPointerFocusBit;
                else ii.HandlerMask &= ~(uint)InteractionInfo.NoPointerFocusBit;

                if (b.OnPointerWheel is not null)
                {
                    ii.HandlerMask |= InteractionInfo.WheelBit;
                    _scene.SetPointerWheel(node, b.OnPointerWheel);
                }
                else
                {
                    ii.HandlerMask &= ~(uint)InteractionInfo.WheelBit;
                    _scene.SetPointerWheel(node, null);
                }

                if (b.OnPointerDown is not null || b.OnDrag is not null || b.OnHoverMove is not null
                    || b.OnPointerMoveWithin is not null || b.OnPointerExit is not null)
                {
                    ii.HandlerMask |= InteractionInfo.PointerBit;   // hit-testable so it receives press/drag AND bare-hover/exit
                    _scene.SetPointerDown(node, b.OnPointerDown);
                    _scene.SetDrag(node, b.OnDrag);
                    _scene.SetHoverMove(node, b.OnHoverMove);
                    _scene.SetPointerMoveWithin(node, b.OnPointerMoveWithin);
                    _scene.SetPointerExit(node, b.OnPointerExit);
                    _scene.Mark(node, NodeFlags.WantsPointer);
                    // Cross-axis content-pan opt-in (SwipeControl/FlipView): the touch path enrolls an axis-locked Drag
                    // arena member that competes with an enclosing scroller's Pan instead of eager-capturing (§7A). Only
                    // meaningful with an OnDrag — a bare DragYieldsToPan with no drag handler is a no-op flag.
                    if (b.OnDrag is not null && b.DragYieldsToPan) _scene.Mark(node, NodeFlags.DragYieldsToPan);
                    else _scene.Unmark(node, NodeFlags.DragYieldsToPan);
                }
                else
                {
                    ii.HandlerMask &= ~(uint)InteractionInfo.PointerBit;
                    _scene.SetPointerDown(node, null);
                    _scene.SetDrag(node, null);
                    _scene.SetHoverMove(node, null);
                    _scene.SetPointerMoveWithin(node, null);
                    _scene.SetPointerExit(node, null);
                    _scene.Unmark(node, NodeFlags.DragYieldsToPan);
                }

                if (b.OnPointerPressed is not null || b.OnPointerReleased is not null)
                {
                    ii.HandlerMask |= InteractionInfo.PressedBit;
                    _scene.SetPointerPressed(node, b.OnPointerPressed);
                    _scene.SetPointerReleased(node, b.OnPointerReleased);
                    _scene.Mark(node, NodeFlags.WantsPointer);
                }
                else
                {
                    ii.HandlerMask &= ~(uint)InteractionInfo.PressedBit;
                    _scene.SetPointerPressed(node, null);
                    _scene.SetPointerReleased(node, null);
                }

                // Drag-reorder promotion (WinUI CanDragItems/CanReorderItems): the DragBit makes the node hit-testable
                // and arms Input.DragController on press; the lifecycle handler columns fire past the drag threshold.
                // An L2 typed source (BoxEl.Draggable) IMPLIES the L1 gesture — its spec lands in the sparse
                // drag-source column the DragDropContext resolves at promotion (payload factory runs ONCE there).
                if (b.CanDrag || b.Draggable is not null)
                {
                    ii.HandlerMask |= InteractionInfo.DragBit;
                    _scene.SetDragStarted(node, b.OnDragStarted);
                    _scene.SetDragDelta(node, b.OnDragDelta);
                    _scene.SetDragCompleted(node, b.OnDragCompleted);
                    _scene.SetDragCanceled(node, b.OnDragCanceled);
                    _scene.SetDragSource(node, b.Draggable);
                    _scene.Mark(node, NodeFlags.WantsPointer);
                }
                else
                {
                    ii.HandlerMask &= ~(uint)InteractionInfo.DragBit;
                    _scene.SetDragStarted(node, null);
                    _scene.SetDragDelta(node, null);
                    _scene.SetDragCompleted(node, null);
                    _scene.SetDragCanceled(node, null);
                    _scene.SetDragSource(node, null);
                }

                // L2 drop target (BoxEl.DropTarget → sparse spec column). Discovery is hit-test-CHAIN based (the
                // context walks parents for the nearest accepting spec), so no handler-mask bit is needed — any
                // surface can receive any drag without becoming click/pointer hit-testable itself.
                _scene.SetDropTarget(node, b.DropTarget);

                if (b.OnContextRequested is not null)
                {
                    ii.HandlerMask |= InteractionInfo.ContextBit;
                    _scene.SetContextRequested(node, b.OnContextRequested);
                }
                else
                {
                    ii.HandlerMask &= ~(uint)InteractionInfo.ContextBit;
                    _scene.SetContextRequested(node, null);
                }

                // Focus-change notification (WinUI GotFocus/LostFocus): no hit-test participation — the dispatcher
                // delivers it on SetFocus; the bit only lets it skip the handler-column lookup.
                if (b.OnFocusChanged is not null)
                {
                    ii.HandlerMask |= InteractionInfo.FocusBit;
                    _scene.SetFocusChanged(node, b.OnFocusChanged);
                }
                else
                {
                    ii.HandlerMask &= ~(uint)InteractionInfo.FocusBit;
                    _scene.SetFocusChanged(node, null);
                }

                if (b.Accelerator is { } accel) { ii.AccelKey = accel.Key; ii.AccelMods = accel.Mods; }
                else { ii.AccelKey = 0; ii.AccelMods = KeyModifiers.None; }
                ii.AccessKey = b.AccessKey;
                // Cursor follows WinUI: NO clickable default (arrow everywhere; only HyperlinkButton/inline links set
                // the hand, editable text sets the I-beam). An EXPLICIT cursor — including Arrow — terminates the
                // dispatcher's hover walk via CursorBit, so a TextBox delete button (Arrow) masks the field's I-beam
                // exactly like WinUI's forced SetCursor(MouseCursorArrow) (TextBox_Partial.cpp:884).
                if (b.Cursor is { } cursor) { ii.Cursor = cursor; ii.HandlerMask |= InteractionInfo.CursorBit; }
                else { ii.Cursor = CursorId.Arrow; ii.HandlerMask &= ~(uint)InteractionInfo.CursorBit; }

                // WinUI Control.IsTabStop: an explicit TabStop beats the clickable⇒focusable auto-derive (the overlay
                // light-dismiss catcher is clickable but must never enter the tab order — WinUI's dismiss layer is
                // not a tab stop, so Tab from a flyout's invoker reaches the flyout content, not the catcher).
                ii.Focusable = b.TabStop ?? (b.Focusable || b.OnClick is not null || b.ClickRequestsContext);
                ii.TabIndex = b.TabIndex;
                ii.FocusVisualMargin = b.FocusVisualMargin ?? Edges4.All(-3f);   // the WinUI template default
                // Keep the NodeFlags mirror in sync on REUSE too: a roving tab stop (RadioButtons, RadioButtons.xaml:5-6)
                // moves IsTabStop between reused items frame-to-frame — a set-only mark would leave stale flags behind.
                if (ii.Focusable) _scene.Mark(node, NodeFlags.Focusable);
                else _scene.ClearFlagBits(node, NodeFlags.Focusable);

                // A lazy hover affordance can mount AFTER its card/row received the pointer-enter edge (media-card play
                // FABs are the canonical case). Seed it from the NEAREST interactive ancestor's live scope; stopping at
                // that ancestor is load-bearing, otherwise a hovered list/pane would light newly mounted reveals in every
                // sibling row. A HoverScopeTransparent ancestor (the ToolTip wrapper) is not a scope: the walk skips it
                // and reads the card behind it. Existing nodes keep their own eased progress untouched.
                //
                // Runs HERE, not next to the InteractionAnim writes above, because the rule is the CASCADE's rule
                // (AnimScheduler.Hover.cs): a REVEAL follows the container it mounted into, a nested interactive control
                // is its own scope and must not — a button mounting (or re-keying) inside a hovered card would otherwise
                // light up with no pointer edge at all. That question is `ii.HandlerMask`, which is only final now.
                if (seedHoverOnMount && MountsInsideHoveredScope(node))
                {
                    if (Anim is { } hoverAnim) hoverAnim.TrySeedHoverFromContainer(node);
                    else
                    {
                        const uint ownsScope = InteractionInfo.PointerBit | InteractionInfo.ClickBit | InteractionInfo.PressedBit;
                        bool reveal = !float.IsNaN(b.HoverOpacity) || !float.IsNaN(b.PressedOpacity);
                        bool scaleFollows = (ii.HandlerMask & ownsScope) == 0
                                            && (b.HoverScale != 1f || b.PressScale != 1f);
                        if (reveal || scaleFollows)
                        {
                            ref InteractionAnim seed = ref _scene.InteractRef(node);
                            seed.HoverT = seed.HoverTarget = 1f;
                        }
                    }
                }
                break;
            }
            case ScrollEl s:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = s.Fill.A > 0f ? VisualKind.Box : VisualKind.None;
                paint.Fill = s.Fill;
                paint.Corners = s.Corners;

                ref LayoutInput li = ref _scene.Layout(node);
                li.Direction = s.Horizontal ? (byte)0 : (byte)1;
                li.Padding = s.Padding;
                li.Margin = s.Margin;
                li.Width = s.Width; li.Height = s.Height;
                li.MinW = s.MinWidth; li.MinH = s.MinHeight; li.MaxW = s.MaxWidth; li.MaxH = s.MaxHeight;
                li.FlexGrow = s.Grow; li.FlexShrink = s.Shrink; li.FlexBasis = s.Basis;
                li.AlignSelf = s.AlignSelf; li.JustifySelf = s.JustifySelf;

                _scene.Mark(node, NodeFlags.ClipsToBounds);
                ref ScrollState ss = ref _scene.ScrollRef(node);
                ss.Orientation = s.Horizontal ? (byte)1 : (byte)0;
                ss.ContentSized = s.ContentSized;
                ss.LineDip = s.ScrollLineDip > 0f ? s.ScrollLineDip : 0f;   // wheel line height hint (S6); 0 = viewport rule
                // Pinch-zoom opt-in (Input owns the live ZoomFactor — re-reconciling the element must NOT reset a
                // mid-gesture / committed zoom, so only the declared opt-in + clamp bounds are written here).
                ss.Zoomable = s.Zoomable;
                ss.MinZoom = s.MinZoom; ss.MaxZoom = s.MaxZoom;
                ss.ItemClipTopInset = float.NaN;
                ss.ItemClipTopFadeBand = 0f;
                if (s.EdgeFade is { } sef) _scene.SetEdgeFade(node, sef); else _scene.ClearEdgeFade(node);
                var sEdge = ScrollEdgeCueResolver.Resolve(s.EdgeCues, s.EdgeFade is not null, s.AutoEdgeFade, s.AutoEdgeFadeBand);
                ss.AutoEdgeFade = sEdge.AutoEdgeFade;
                ss.AutoEdgeFadeBand = sEdge.AutoEdgeFadeBand;
                ss.EdgeCueConfig = sEdge.Chevron ? ScrollState.EdgeCueChevronBit : (byte)0;
                ss.AlwaysShowBar = s.AlwaysShowScrollbar;
                ss.SuppressBar = s.SuppressScrollBar;
                // DECLARATION-GATED (the ScrollState snap-field writer contract): only a declaring element writes the snap
                // columns, and then on EVERY patch (so a per-render interval stays current). A null Snap leaves them
                // exactly as they are, which is what keeps a control's/probe's post-mount SnapInterval write alive.
                if (s.Snap is { } snapSpec) snapSpec.ApplyTo(ref ss);
                _scene.SetAuthoredScrollHandle(node, s.Handle);
                ApplyScrollKey(node, ref ss, s.ScrollKey, isMount);   // seed (mount) / save+seed (content change) the offset
                ProvideScrollCtx(node);                               // BEFORE the content mounts: UseScroll resolves THIS viewport
                break;
            }
            case VirtualListEl v:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = v.Fill.A > 0f ? VisualKind.Box : VisualKind.None;
                paint.Fill = v.Fill;

                ref LayoutInput li = ref _scene.Layout(node);
                li.Direction = v.Horizontal ? (byte)0 : (byte)1;
                li.Margin = v.Margin;
                li.Width = v.Width; li.Height = v.Height;
                li.MinW = v.MinWidth; li.MinH = v.MinHeight; li.MaxW = v.MaxWidth; li.MaxH = v.MaxHeight;
                li.FlexGrow = v.Grow; li.FlexShrink = v.Shrink; li.FlexBasis = v.Basis;
                li.AlignSelf = v.AlignSelf; li.JustifySelf = v.JustifySelf;

                _scene.Mark(node, NodeFlags.ClipsToBounds);
                ref ScrollState sc = ref _scene.ScrollRef(node);
                sc.Orientation = v.Horizontal ? (byte)1 : (byte)0;
                sc.ItemCount = Math.Max(0, v.ItemCount);
                sc.Layout = v.ItemLayout;
                sc.LineDip = v.ScrollLineDip > 0f ? v.ScrollLineDip : 0f;   // wheel line height hint (S6); 0 = viewport rule
                sc.PersistentPrefixCount = v.RowBind is null ? 0 : Math.Clamp(v.PersistentPrefixCount, 0, sc.ItemCount);
                sc.MeasureAll = v.MeasureAll;
                sc.ItemClipTopInset = v.RowBind is null || !float.IsFinite(v.ItemClipTopInset)
                    ? float.NaN
                    : MathF.Max(0f, v.ItemClipTopInset);
                sc.ItemClipTopFadeBand = float.IsFinite(sc.ItemClipTopInset)
                    ? MathF.Max(0f, v.ItemClipTopFadeBand)
                    : 0f;
                if (v.EdgeFade is { } vef) _scene.SetEdgeFade(node, vef); else _scene.ClearEdgeFade(node);
                var vEdge = ScrollEdgeCueResolver.Resolve(v.EdgeCues, v.EdgeFade is not null, v.AutoEdgeFade, v.AutoEdgeFadeBand);
                sc.AutoEdgeFade = vEdge.AutoEdgeFade;
                sc.AutoEdgeFadeBand = vEdge.AutoEdgeFadeBand;
                sc.EdgeCueConfig = vEdge.Chevron ? ScrollState.EdgeCueChevronBit : (byte)0;
                sc.SuppressBar = v.SuppressScrollBar;
                // Declaration-gated, exactly as for ScrollEl above (see the ScrollState snap-field writer contract).
                if (v.Snap is { } vSnapSpec) vSnapSpec.ApplyTo(ref sc);
                _scene.SetAuthoredScrollHandle(node, v.Handle);
                ApplyScrollKey(node, ref sc, v.ScrollKey, isMount);   // seed BEFORE RealizeWindow → first window at saved row
                ProvideScrollCtx(node);                               // BEFORE the rows realize: UseScroll resolves THIS viewport
                break;
            }
            case GridEl g:
            {
                ref LayoutInput li = ref _scene.Layout(node);
                li.Width = g.Width; li.Height = g.Height;
                li.FlexGrow = g.Grow; li.FlexShrink = g.Shrink; li.FlexBasis = g.Basis;
                li.AlignSelf = g.AlignSelf; li.JustifySelf = g.JustifySelf; li.Margin = g.Margin; li.Padding = g.Padding;
                _scene.SetGrid(node, new GridSpec { Columns = g.Columns, ColGap = g.ColGap, RowGap = g.RowGap, RowHeight = g.RowHeight, MinColWidth = g.MinColWidth, MaxColumns = g.MaxColumns });
                break;
            }
            case PolylineStrokeEl pl:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.PolylineStroke;
                paint.OriginX = pl.TransformOriginX;
                paint.OriginY = pl.TransformOriginY;
                paint.Opacity = pl.Opacity;

                // Identity value-gate, matching the BoxEl rule (:1003): an identity-declared polyline has no opinion
                // about its matrix — leave it to AnimEngine owners (a settled T/R/S track's terminal otherwise got
                // reset to identity by every re-render; no polyline in the tree declares transform statics today).
                if (pl.OffsetX != 0f || pl.OffsetY != 0f || pl.Rotation != 0f || pl.ScaleX != 1f || pl.ScaleY != 1f)
                {
                    var tf = Affine2D.Translation(pl.OffsetX, pl.OffsetY);
                    if (pl.Rotation != 0f) tf = tf.Multiply(Affine2D.Rotation(pl.Rotation * (MathF.PI / 180f)));
                    if (pl.ScaleX != 1f || pl.ScaleY != 1f) tf = tf.Multiply(Affine2D.Scale(pl.ScaleX, pl.ScaleY));
                    paint.LocalTransform = tf;
                }

                if (pl.HoverScale != 1f || pl.PressScale != 1f)
                {
                    ref InteractionAnim ia = ref _scene.InteractRef(node);
                    ia.HoverScale = pl.HoverScale;
                    ia.PressScale = pl.PressScale;
                }

                _scene.SetPolylineStroke(node, new PolylineStrokeSpec(
                    pl.P0, pl.P1, pl.P2, pl.P3, pl.PointCount,
                    pl.Color, pl.Thickness, pl.TrimStart, pl.TrimEnd, pl.RoundCaps));

                ref LayoutInput li = ref _scene.Layout(node);
                li.Margin = pl.Margin;
                li.Width = pl.Width; li.Height = pl.Height;
                li.MinW = pl.MinWidth; li.MinH = pl.MinHeight; li.MaxW = pl.MaxWidth; li.MaxH = pl.MaxHeight;
                li.FlexGrow = pl.Grow; li.FlexShrink = pl.Shrink; li.FlexBasis = pl.Basis;
                li.AlignSelf = pl.AlignSelf; li.JustifySelf = pl.JustifySelf;
                break;
            }
            case PathEl pe:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.Path;
                paint.OriginX = pe.TransformOriginX;
                paint.OriginY = pe.TransformOriginY;
                paint.Opacity = pe.Opacity;

                // Identity value-gate, matching PolylineStrokeEl/BoxEl (:1003): an identity-declared path has no
                // opinion about its matrix — leave it to AnimEngine owners.
                if (pe.OffsetX != 0f || pe.OffsetY != 0f || pe.Rotation != 0f || pe.ScaleX != 1f || pe.ScaleY != 1f)
                {
                    var tf = Affine2D.Translation(pe.OffsetX, pe.OffsetY);
                    if (pe.Rotation != 0f) tf = tf.Multiply(Affine2D.Rotation(pe.Rotation * (MathF.PI / 180f)));
                    if (pe.ScaleX != 1f || pe.ScaleY != 1f) tf = tf.Multiply(Affine2D.Scale(pe.ScaleX, pe.ScaleY));
                    paint.LocalTransform = tf;
                }

                if (pe.HoverScale != 1f || pe.PressScale != 1f)
                {
                    ref InteractionAnim ia = ref _scene.InteractRef(node);
                    ia.HoverScale = pe.HoverScale;
                    ia.PressScale = pe.PressScale;
                }

                _scene.SetPath(node, new PathSpec(pe.Geometry, pe.Fill, pe.Rule, pe.StrokeColor, pe.Stroke,
                    pe.TrimStart, pe.TrimEnd, pe.TrimMode, pe.ViewBoxW, pe.ViewBoxH, pe.HitTestGeometry));

                ref LayoutInput lip = ref _scene.Layout(node);
                lip.Margin = pe.Margin;
                lip.Width = pe.Width; lip.Height = pe.Height;
                lip.MinW = pe.MinWidth; lip.MinH = pe.MinHeight; lip.MaxW = pe.MaxWidth; lip.MaxH = pe.MaxHeight;
                lip.FlexGrow = pe.Grow; lip.FlexShrink = pe.Shrink; lip.FlexBasis = pe.Basis;
                lip.AlignSelf = pe.AlignSelf; lip.JustifySelf = pe.JustifySelf;
                break;
            }
            case SeriesEl se:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.Series;
                paint.Opacity = se.Opacity;
                _scene.SetSeries(node, new SeriesSpec(se.Shape, se.Color, se.Gradient, se.Thickness, se.Baseline, se.Amplitude, se.Opacity,
                                                      se.GradientTo, se.GradientAxis, se.AntiAlias, se.Blend));
                if (!se.GradientMix.IsBound) _scene.SetGradientMix(node, se.GradientMix.Value);
                if (!se.Samples.IsBound) _scene.SetSeriesSamples(node, se.Samples.Value.AsSpan());
                // else: the bound path defers to BindSeriesSamples' mount-time effect (the ListRowEl.Cells deferral).
                ref LayoutInput li = ref _scene.Layout(node);
                li.Margin = se.Margin;
                li.Width = se.Width; li.Height = se.Height;
                li.MinW = se.MinWidth; li.MinH = se.MinHeight; li.MaxW = se.MaxWidth; li.MaxH = se.MaxHeight;
                li.FlexGrow = se.Grow; li.FlexShrink = se.Shrink; li.FlexBasis = se.Basis;
                li.AlignSelf = se.AlignSelf; li.JustifySelf = se.JustifySelf;
                break;
            }
            case SpriteFieldEl sf:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.Sprites;
                paint.Opacity = sf.Opacity;
                _scene.SetSpriteSpec(node, new SpriteSpec(sf.Kernel, sf.Blend, sf.Opacity));
                if (!sf.Instances.IsBound) _scene.SetSprites(node, sf.Instances.Value.AsSpan());
                // else: the bound path defers to BindSprites' mount-time effect.
                ref LayoutInput li = ref _scene.Layout(node);
                li.Margin = sf.Margin;
                li.Width = sf.Width; li.Height = sf.Height;
                li.MinW = sf.MinWidth; li.MinH = sf.MinHeight; li.MaxW = sf.MaxWidth; li.MaxH = sf.MaxHeight;
                li.FlexGrow = sf.Grow; li.FlexShrink = sf.Shrink; li.FlexBasis = sf.Basis;
                li.AlignSelf = sf.AlignSelf; li.JustifySelf = sf.JustifySelf;
                break;
            }
            case ListRowEl lr:
            {
                // Scroll-rework Wave 0.E: ONE node, RowPaint payload ≤8 cells (scroll-rework-design.md §B.4).
                // Fill/HoverFill/(Selected→Pressed)Fill reuse the generic paint channels every element carries —
                // ResolveSurface (Render/SceneRecorder.cs) hover/press-fades them for free.
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.ListRow;
                paint.Corners = lr.Corners;
                if (!lr.Fill.IsBound) paint.Fill = lr.Fill.Value;
                if (!lr.HoverFill.IsBound) paint.HoverFill = lr.HoverFill.Value;
                if (!lr.SelectedFill.IsBound) paint.PressedFill = lr.SelectedFill.Value;

                bool hasClick = lr.OnCellClick is not null;
                _scene.SetRowCellClickHandler(node, lr.OnCellClick);   // mount-static; rewritten unconditionally, cheap
                if (!lr.Cells.IsBound)
                    WriteRowCells(node, lr.Cells.Value.AsSpan(), lr.Placeholder.ValueOr(false), lr.PlaceholderColor, hasClick);
                // else: the bound path defers to BindListRowCells's mount-time Effect (runNow: true — same deferral
                // SpanTextEl's bound Spans uses).

                ref LayoutInput li = ref _scene.Layout(node);
                li.Width = lr.Width; li.Height = lr.Height;
                li.MinW = lr.MinWidth; li.MinH = lr.MinHeight; li.MaxW = lr.MaxWidth; li.MaxH = lr.MaxHeight;
                li.FlexGrow = lr.Grow; li.FlexShrink = lr.Shrink; li.FlexBasis = lr.Basis;
                li.AlignSelf = lr.AlignSelf; li.JustifySelf = lr.JustifySelf;
                li.Margin = lr.Margin;
                break;
            }
            case ImageEl im:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.Image;
                // Before any request / pin below: the flag is read when this node pins an id (PinImageNode).
                if (im.KeepWhileHidden) _keepWhileHiddenNodes.Add((int)node.Raw.Index);
                else _keepWhileHiddenNodes.Remove((int)node.Raw.Index);
                if (!im.Placeholder.IsBound) paint.Fill = im.Placeholder.Value;   // bound rows tint via the binding
                paint.Corners = im.Corners;
                paint.ImageFit = (byte)im.Fit;
                paint.ImageFocusX = Math.Clamp(im.FocusX, 0f, 1f);
                paint.ImageFocusY = Math.Clamp(im.FocusY, 0f, 1f);

                // Decode-target size: explicit Width/Height when set; otherwise the DecodePx hint (a fluid/aspect image's
                // real box size isn't known until layout), deriving the cross dimension from AspectRatio when possible.
                (int decodeW, int decodeH) = ImageDecodeTarget(in im, _scene.DeviceScale);

                // ── image-pipeline trace (DIAGNOSTIC ONLY, --fg diag + optional --fg img=FILTER=<substring>) ──────────
                // Distinguishes the three ways a cover can visibly re-load: it MOUNTED fresh, its SOURCE url changed
                // (same art at a different CDN size hash ⇒ a new cache key ⇒ Pending ⇒ placeholder), or only its
                // requested DECODE size changed (the width measure landing after the first render — also a new key).
                if (Diag.CompiledIn && Diag.Enabled && !im.Source.IsBound
                    && ImageCache.DiagTraced(im.Source.Value))
                {
                    if (isMount)
                        Diag.Event("img", $"mount node={node.Raw.Index} src={ImageCache.DiagSourceTail(im.Source.Value)} " +
                            $"decode={decodeW}x{decodeH}");
                    else if (old is ImageEl oldIm && !oldIm.Source.IsBound)
                    {
                        (int oldW, int oldH) = ImageDecodeTarget(in oldIm, _scene.DeviceScale);
                        bool srcChanged = !string.Equals(oldIm.Source.Value, im.Source.Value, StringComparison.Ordinal);
                        if (srcChanged)
                            Diag.Event("img", $"src-change node={node.Raw.Index} " +
                                $"from={ImageCache.DiagSourceTail(oldIm.Source.Value)} " +
                                $"to={ImageCache.DiagSourceTail(im.Source.Value)} " +
                                $"decode={decodeW}x{decodeH} wasDecode={oldW}x{oldH}");
                        else if (oldW != decodeW || oldH != decodeH)
                            Diag.Event("img", $"decode-change node={node.Raw.Index} " +
                                $"src={ImageCache.DiagSourceTail(im.Source.Value)} " +
                                $"from={oldW}x{oldH} to={decodeW}x{decodeH}");
                    }
                }

                if (!im.Source.IsBound)   // bound rows request via the binding (the effect owns pin/unpin)
                {
                    // W2-E3: the lane comes from the row's position in its viewport — Visible inside the visible band,
                    // Overscan in the halo; Visible for a node under no viewport (see ImageRequestPriority).
                    ImagePriority prio = ImageRequestPriority(node);
                    int newId = (Images is not null && im.Source.Value.Length > 0)
                        ? Images.Request(im.Source.Value, decodeW, decodeH, prio, im.BlurHash, im.RevealTransition).Id : 0;
                    SwapImageId(node, ref paint, newId, prio);
                }

                int oldDerived = _scene.TryGetImageEffects(node, out var oldEffects) ? oldEffects.DerivedImageId : 0;
                int newDerived = RequestBakedImage(in im, paint.ImageId, decodeW, decodeH);
                if (newDerived != oldDerived)
                {
                    UnpinImageNode(node, oldDerived);
                    if (newDerived != 0) PinImageNode(node, newDerived);
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }
                WriteImageEffects(node, in im, newDerived);

                ref LayoutInput li = ref _scene.Layout(node);
                li.Width = im.Width; li.Height = im.Height; li.AspectRatio = im.AspectRatio;
                li.Margin = im.Margin; li.AlignSelf = im.AlignSelf; li.JustifySelf = im.JustifySelf;
                break;
            }
            case IconLayerEl il:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.IconLayer;
                paint.ImageId = il.PathId;                       // ImageId column DOUBLES as the geometry PathId
                if (!il.Tint.IsBound) paint.Fill = il.Tint.Value;   // bound tint recolors via the effect (theme-live)

                ref LayoutInput li = ref _scene.Layout(node);
                li.Width = il.Size; li.Height = il.Size;
                li.Margin = il.Margin; li.AlignSelf = il.AlignSelf; li.JustifySelf = il.JustifySelf;
                break;
            }
            case TextEl t:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.Text;

                // Implicit BrushTransition on the resting foreground (WinUI BrushTransition on a logical state flip).
                // Skipped when ColorBind owns the channel (same per-channel rule as the BoxEl fill block above).
                bool colorOwned = !t.Color.IsBound;
                float textMs = ThemeTransitionOr(t.BrushTransitionMs);   // live re-theme overrides the element's NaN/own duration
                if (!isMount && colorOwned && !float.IsNaN(textMs) && textMs > 0f && paint.TextColor != t.Color.Value)
                {
                    bool midFlight = _scene.TryGetBrushAnim(node, out var prev);
                    var ba = new BrushAnim
                    {
                        DurationMs = textMs,
                        Channels = BrushAnim.TextBit,
                        TextFrom = midFlight && (prev.Channels & BrushAnim.TextBit) != 0
                            ? ColorF.LerpLinear(prev.TextFrom, paint.TextColor, prev.T)
                            : paint.TextColor,
                    };
                    _scene.SetBrushAnim(node, ba);
                    _scene.Mark(node, NodeFlags.PaintDirty);
                    Anim?.SeedBrushFade(node, ba.DurationMs);   // drive the cross-fade T via the unified engine (no separate ticker)
                }

                // Guarded like Text below: a bound color is owned by its effect (EditableText's doc-vs-placeholder
                // ColorBind was clobbered back to the static by any re-render between signal fires).
                if (colorOwned) paint.TextColor = t.Color.Value;
                paint.TextHoverColor = t.HoverColor;
                paint.TextPressedColor = t.PressedColor;
                paint.TextDisabledColor = t.DisabledColor;
                paint.TextFocusedColor = t.FocusedColor;
                // Glyph wipe (general text-reveal; the lyrics karaoke uses it): a SPARSE side-table, not the hot paint
                // struct. Mark dirty when it changes so the wiped line re-records as the split advances (reshape-free).
                if (!Nullable.Equals(_scene.TryGetGlyphWipe(node, out var prevWipe) ? prevWipe : (GlyphWipe?)null, t.Wipe))
                    _scene.Mark(node, NodeFlags.PaintDirty);
                _scene.SetGlyphWipe(node, t.Wipe);
                paint.TextDecorations = (byte)((t.Underline ? NodePaint.UnderlineBit : 0)
                                             | (t.Strikethrough ? NodePaint.StrikethroughBit : 0));
                _scene.SetDynamicText(node, t.DynamicText);
                if (!t.Text.IsBound)
                {
                    var newText = _strings.Intern(t.Text.Value);
                    if (paint.Text != newText) { SetPaintText(ref paint, newText); MarkLayoutShape(node); }
                }

                ref LayoutInput li = ref _scene.Layout(node);
                var famId = _strings.Intern(t.FontFamily);
                if (li.TextStyle.FontFamily != famId) { _strings.AddRef(famId); _strings.Release(li.TextStyle.FontFamily); }
                li.TextStyle = new TextStyle(famId, t.Size, t.ResolvedWeight, t.Wrap, t.Trim, t.MaxLines,
                    t.CharSpacing, t.LineHeight, t.LineStacking, t.LineBounds,
                    MinSizeDip: float.IsNaN(t.MinSize) ? 0f : t.MinSize);
                li.Margin = t.Margin;
                li.Width = t.Width; li.Height = t.Height;
                li.MinW = t.MinWidth; li.MinH = t.MinHeight; li.MaxW = t.MaxWidth; li.MaxH = t.MaxHeight;
                li.FlexGrow = t.Grow; li.FlexShrink = t.Shrink; li.FlexBasis = t.Basis;
                li.AlignSelf = t.AlignSelf; li.JustifySelf = t.JustifySelf;

                WriteTextSelection(node, t.IsTextSelectionEnabled, t.SelectionHighlightColor);
                break;
            }
            case SpanTextEl st:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.Text;
                paint.TextColor = st.Color;
                paint.TextDecorations = 0;   // span decorations ride the span-run artifact path, not the single-run bits

                _scene.SetSpanClickHandler(node, st.OnSpanClick);   // mount-static; rewritten unconditionally, cheap

                int runId;
                if (!st.Spans.IsBound)
                    runId = WriteSpanText(node, st.Spans.Value.AsSpan(), st.OverflowSuffix, inRenderScope: true);
                else
                {
                    // Bound path: Reconciler.Spans.cs's mount-time effect owns the first (and every rebind) fire; keep
                    // whatever run id the scene already carries (0 on a fresh mount before the effect runs, matching
                    // TextEl.Text's bound convention — the static write here never races the bind, effects run in the
                    // same BindNode pass immediately after WriteColumns at mount, :691-692).
                    runId = _scene.Layout(node).TextStyle.SpanRunId;
                }

                ref LayoutInput li = ref _scene.Layout(node);
                var famId = _strings.Intern(st.FontFamily);
                if (li.TextStyle.FontFamily != famId) { _strings.AddRef(famId); _strings.Release(li.TextStyle.FontFamily); }
                li.TextStyle = new TextStyle(famId, st.Size, st.Weight != 0 ? st.Weight : (ushort)400,
                    st.Wrap, st.Trim, st.MaxLines, st.CharSpacing, st.LineHeight, st.LineStacking, st.LineBounds, runId);
                li.Margin = st.Margin;
                li.Width = st.Width; li.Height = st.Height;
                li.MinW = st.MinWidth; li.MinH = st.MinHeight; li.MaxW = st.MaxWidth; li.MaxH = st.MaxHeight;
                li.FlexGrow = st.Grow; li.FlexShrink = st.Shrink; li.FlexBasis = st.Basis;
                li.AlignSelf = st.AlignSelf; li.JustifySelf = st.JustifySelf;

                WriteTextSelection(node, st.IsTextSelectionEnabled, st.SelectionHighlightColor);
                break;
            }
        }

        if (!isMount)
        {
            _scene.Mark(node, NodeFlags.PaintDirty);
            _reconciled = true;
            // Equality-gate LayoutDirty: structural mounts/removals mark elsewhere; Text/SpanText already mark on
            // content/shaping change. This catches Width/Height/flex/gap/padding/TextStyle/grid/clip-flag flips that
            // WriteColumns used to silently rewrite without dirtying (RunComponent's blanket mark papered over it).
            ref LayoutInput layoutAfter = ref _scene.Layout(node);
            NodeFlags layoutFlagsAfter = _scene.Flags(node) & (NodeFlags.ClipsToBounds | NodeFlags.LayoutBoundary | NodeFlags.ZStack);
            bool gridChanged = false;
            if (el is GridEl)
            {
                bool hasGrid = _scene.TryGetGrid(node, out var gridAfter);
                gridChanged = hadGrid != hasGrid || (hasGrid && !SameGridSpec(in gridBefore, in gridAfter));
            }
            if (gridChanged || layoutFlagsBefore != layoutFlagsAfter || !SameLayoutInput(in layoutBefore, in layoutAfter))
            {
                MarkLayoutShape(node);
                if (!SameParentFacing(in layoutBefore, in layoutAfter)) MarkParentLayoutDirty(node);
            }
        }
    }

    /// <summary>Layout-column equality for the WriteColumns LayoutDirty gate. Uses <see cref="float.Equals"/> so NaN
    /// (auto) compares equal to NaN — same contract as the bound Width/Height effects.</summary>
    private static bool SameLayoutInput(in LayoutInput a, in LayoutInput b)
        => a.Direction == b.Direction
           && a.Gap.Equals(b.Gap)
           && a.Padding == b.Padding
           && a.Margin == b.Margin
           && a.Width.Equals(b.Width) && a.Height.Equals(b.Height)
           && a.AspectRatio.Equals(b.AspectRatio)
           && a.MinW.Equals(b.MinW) && a.MinH.Equals(b.MinH)
           && a.MaxW.Equals(b.MaxW) && a.MaxH.Equals(b.MaxH)
           && a.FlexGrow.Equals(b.FlexGrow) && a.FlexShrink.Equals(b.FlexShrink) && a.FlexBasis.Equals(b.FlexBasis)
           && a.AlignSelf == b.AlignSelf && a.JustifySelf == b.JustifySelf
           && a.Justify == b.Justify && a.AlignItems == b.AlignItems
           && a.Wrap == b.Wrap
           && a.MeasureUnboundedWidth == b.MeasureUnboundedWidth
           && a.TextStyle == b.TextStyle;

    /// <summary>The <see cref="LayoutInput"/> fields a PARENT's solve reads off this node (its outer box and slot).
    /// A change here moves siblings, so <see cref="MarkParentLayoutDirty"/> must run, not only the node's own mark.</summary>
    private static bool SameParentFacing(in LayoutInput a, in LayoutInput b)
        => a.Margin == b.Margin
           && a.Width.Equals(b.Width) && a.Height.Equals(b.Height)
           && a.AspectRatio.Equals(b.AspectRatio)
           && a.MinW.Equals(b.MinW) && a.MinH.Equals(b.MinH)
           && a.MaxW.Equals(b.MaxW) && a.MaxH.Equals(b.MaxH)
           && a.FlexGrow.Equals(b.FlexGrow) && a.FlexShrink.Equals(b.FlexShrink) && a.FlexBasis.Equals(b.FlexBasis)
           && a.AlignSelf == b.AlignSelf && a.JustifySelf == b.JustifySelf
           && a.MeasureUnboundedWidth == b.MeasureUnboundedWidth;

    private static bool SameGridSpec(in GridSpec a, in GridSpec b)
    {
        if (!a.ColGap.Equals(b.ColGap) || !a.RowGap.Equals(b.RowGap)
            || !a.RowHeight.Equals(b.RowHeight) || !a.MinColWidth.Equals(b.MinColWidth) || a.MaxColumns != b.MaxColumns)
            return false;
        TrackSize[]? ac = a.Columns, bc = b.Columns;
        if (ReferenceEquals(ac, bc)) return true;
        if (ac is null || bc is null || ac.Length != bc.Length) return false;
        for (int i = 0; i < ac.Length; i++)
            if (ac[i] != bc[i]) return false;
        return true;
    }

    private bool MountsInsideHoveredScope(NodeHandle node)
    {
        const uint interactive = InteractionInfo.PointerBit | InteractionInfo.ClickBit | InteractionInfo.PressedBit;
        for (var parent = _scene.Parent(node); !parent.IsNull && _scene.IsLive(parent); parent = _scene.Parent(parent))
        {
            uint mask = _scene.Interaction(parent).HandlerMask;
            // Non-interactive ancestors and transparent listeners (the ToolTip wrapper) are not scopes: keep walking.
            if ((mask & interactive) == 0 || (mask & InteractionInfo.HoverScopeTransparentBit) != 0) continue;
            return (_scene.Flags(parent) & (NodeFlags.Hovered | NodeFlags.HoverWithin)) != 0;
        }
        return false;
    }

    /// <summary>Wire (or clear) read-only text selection on a text leaf (rtb-02): the SelectableTextBit makes it
    /// hit-testable for the dispatcher's drag-select gestures, focusable so Ctrl+C routes to it, and I-beam-cursored —
    /// WinUI's selection-enabled text behavior (RichTextBlock.cpp:1730 creates the TextSelectionManager;
    /// TextBlock.cpp:583 does so on the opt-in flip). Also publishes the api-04 per-control highlight override.</summary>
    private void WriteTextSelection(NodeHandle node, bool selectable, ColorF highlight)
    {
        ref InteractionInfo ii = ref _scene.Interaction(node);
        if (selectable)
        {
            ii.HandlerMask |= InteractionInfo.SelectableTextBit;
            ii.Focusable = true;
            // Text leaves declare no element Cursor of their own, so the CursorBit here is selection's (an I-beam
            // while selectable — the WinUI selectable-text cursor).
            ii.Cursor = CursorId.IBeam;
            ii.HandlerMask |= InteractionInfo.CursorBit;
            _scene.Mark(node, NodeFlags.WantsPointer | NodeFlags.Focusable);
        }
        else if ((ii.HandlerMask & InteractionInfo.SelectableTextBit) != 0)
        {
            // A recycled/re-rendered leaf that LOST selection: clear exactly what selection set (text leaves carry no
            // other focus/cursor source), including any live selection state.
            ii.HandlerMask &= ~(uint)(InteractionInfo.SelectableTextBit | InteractionInfo.CursorBit);
            ii.Cursor = CursorId.Arrow;
            ii.Focusable = false;
            _scene.ClearFlagBits(node, NodeFlags.Focusable);
            _scene.ClearTextSelection(node);
        }
        _scene.SetSelectionHighlight(node, highlight);
    }

    /// <summary>True when two span arrays are SHAPING-identical (text, weight, size, family, color, decorations,
    /// link-ness) — everything the span-run id keys downstream caches on. OnClick identity is deliberately excluded:
    /// re-rendered lambdas must not churn run ids (the scene's TextSpan[] side-table carries the fresh actions).</summary>
    private static bool SameSpanShaping(ReadOnlySpan<TextSpan> a, ReadOnlySpan<TextSpan> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            ref readonly var x = ref a[i];
            ref readonly var y = ref b[i];
            if (!string.Equals(x.Text, y.Text, StringComparison.Ordinal) || x.Weight != y.Weight
                || x.Color != y.Color || x.Underline != y.Underline || x.Strikethrough != y.Strikethrough
                || !Nullable.Equals(x.Size, y.Size) || !string.Equals(x.FontFamily, y.FontFamily, StringComparison.Ordinal)
                || x.IsHyperlink != y.IsHyperlink) return false;
        }
        return true;
    }

    // Scene-owned, grow-only-capacity scratch for the span-paragraph concat build (P2): reused across every
    // WriteSpanText call on the UI thread (single writer, never reentrant) so a shaping MISS never allocates a throwaway
    // char[] — only StringTable.Intern's OWN `new string(...)` pays when the concatenated text is genuinely new.
    private char[] _spanConcatScratch = Array.Empty<char>();

    /// <summary>Mint/reuse the POD shaping overlay (<see cref="SpanRunTable"/> run) for one span paragraph and copy the
    /// resolved spans into the scene's alias-safe per-node table (P2, "bound spans with index-resolved clicks").
    /// Shared by the static WriteColumns path and <c>Reconciler.Spans.cs</c>'s bound effect — same shaping-gate
    /// discipline either way: an identical span content/style set never re-shapes and never re-mints a run id
    /// (<c>gate.spans.shaping-gate-keeps-run</c>). Returns the resolved <c>TextStyle.SpanRunId</c> for the caller to
    /// fold into its own <see cref="LayoutInput.TextStyle"/> rebuild (every other TextStyle axis — size/weight/wrap/…
    /// — rides <c>SpanTextEl</c>'s STATIC properties and is unaffected by a <c>Spans</c> rebind, so this method never
    /// touches <see cref="LayoutInput"/> itself).
    /// <para><paramref name="inRenderScope"/>: true from the static WriteColumns path (a re-shape may safely set
    /// <c>_layoutShapeMutated</c> via <see cref="MarkLayoutShape"/>, like every other WriteColumns shape change);
    /// false from <c>Reconciler.Spans.cs</c>'s bound effect, which — like the bound Text/Width/Height effects above
    /// it — fires OUTSIDE a render scope and must mark only its OWN node (<see cref="NodeFlags.LayoutDirty"/>), per
    /// <see cref="MarkLayoutShape"/>'s own doc comment ("Deliberately NOT used by the bound … effects").</para></summary>
    private int WriteSpanText(NodeHandle node, ReadOnlySpan<TextSpan> bodySpans, TextSpan[]? suffixSpans, bool inRenderScope)
    {
        int bodyCount = bodySpans.Length;
        int suffixCount = suffixSpans?.Length ?? 0;
        TextSpan[]? merged = null;
        ReadOnlySpan<TextSpan> spans;
        int overflowSuffixStart = -1;
        if (suffixCount == 0) spans = bodySpans;
        else
        {
            merged = new TextSpan[bodyCount + suffixCount];
            bodySpans.CopyTo(merged);
            Array.Copy(suffixSpans!, 0, merged, bodyCount, suffixCount);
            spans = merged;
            overflowSuffixStart = 0;
            for (int i = 0; i < bodyCount; i++) overflowSuffixStart += bodySpans[i].Text?.Length ?? 0;
        }

        ref NodePaint paint = ref _scene.Paint(node);
        ref LayoutInput li = ref _scene.Layout(node);

        // Re-register the POD shaping overlay ONLY when a shaping input changed: the run id is the key of the
        // measure cache AND the renderer's shaped-run cache, so minting a fresh id IS the invalidation; an
        // identical re-render (or an identical rebind — the whole point of the bound-spans gate) keeps the id.
        int runId = li.TextStyle.SpanRunId;
        bool same = runId != 0
            && SpanRunTable.Shared.Resolve(runId)?.OverflowSuffixStart == overflowSuffixStart
            && _scene.TryGetSpanText(node, out var oldSpans)
            && SameSpanShaping(oldSpans, spans);
        if (!same)
        {
            int total = 0;
            for (int i = 0; i < spans.Length; i++) total += spans[i].Text?.Length ?? 0;
            if (_spanConcatScratch.Length < total)
                _spanConcatScratch = new char[Math.Max(total, Math.Max(64, _spanConcatScratch.Length * 2))];
            var scratch = _spanConcatScratch.AsSpan(0, total);
            int at = 0;
            for (int i = 0; i < spans.Length; i++)
            {
                var s = spans[i].Text;
                if (string.IsNullOrEmpty(s)) continue;
                s.AsSpan().CopyTo(scratch[at..]);
                at += s.Length;
            }

            var styles = new SpanStyle[spans.Length];
            int pos = 0;
            for (int i = 0; i < spans.Length; i++)
            {
                ref readonly var sp = ref spans[i];
                int len = sp.Text?.Length ?? 0;
                byte flags = (byte)((sp.Underline ? SpanStyle.UnderlineBit : 0)
                                  | (sp.Strikethrough ? SpanStyle.StrikethroughBit : 0)
                                  | (sp.IsHyperlink ? SpanStyle.LinkBit : 0));
                var spanFam = _strings.Intern(sp.FontFamily);
                _strings.AddRef(spanFam);   // released by SceneStore.ReleaseSpanRun with the run
                styles[i] = new SpanStyle(pos, pos + len, sp.Weight, sp.Size ?? 0f, spanFam, sp.Color, flags);
                pos += len;
            }
            int newRunId = SpanRunTable.Shared.Create(styles, overflowSuffixStart);
            SpanRunTable.Shared.AddRef(newRunId);
            _scene.ReleaseSpanRun(runId);
            runId = newRunId;

            var newText = _strings.Intern(scratch);   // probes by content first — allocates only a genuinely new string
            if (paint.Text != newText) SetPaintText(ref paint, newText);
            if (inRenderScope) MarkLayoutShape(node); else _scene.Mark(node, NodeFlags.LayoutDirty);
        }
        _scene.SetSpanText(node, spans);   // always — hyperlink actions may change without a shaping change

        // Hyperlink spans: hit-testable so the dispatcher can resolve Hand over the span rects and fire the
        // span's action (own OnClick, else the node's OnSpanClick by index — WinUI inline Hyperlink,
        // RichTextBlock.cpp:2995 SetCursor(MouseCursorHand)).
        ref InteractionInfo ii = ref _scene.Interaction(node);
        bool hasLinks = false;
        for (int i = 0; i < spans.Length && !hasLinks; i++) hasLinks = spans[i].IsHyperlink;
        if (hasLinks)
        {
            ii.HandlerMask |= InteractionInfo.SpanLinksBit;
            _scene.Mark(node, NodeFlags.WantsPointer);
        }
        else ii.HandlerMask &= ~(uint)InteractionInfo.SpanLinksBit;

        return runId;
    }
}
