using System.Buffers;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
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
public sealed class TreeReconciler
{
    private readonly SceneStore _scene;
    private readonly StringTable _strings;

    // Mounted child components, keyed by their host node (the ComponentEl anchor).
    private sealed class CompEntry { public Component Comp = null!; public Element? Rendered; public Type Type = null!; public Effect? Effect; public bool Parked; public bool DeferredRender; public Signal<bool>? ActiveSig; public SkeletonStyle? DerivedSkeletonStyle; }
    private readonly Dictionary<NodeHandle, CompEntry> _comps = new();
    private readonly Dictionary<Component, NodeHandle> _anchorOf = new();
    private readonly List<Component> _live = new();

    // The previously-realized window per virtual-list viewport (the keyed-diff's oldKids). Rented from ArrayPool.
    // Bound (RowBind) viewports keep persistent SLOTS instead: one (index signal, mounted element) per visible row.
    private sealed class VirtualEntry
    {
        public Element[]? Prev; public int PrevLen; public int PrevFirst; public VirtualListEl? El;
        public List<(Signal<int> Index, Element El)>? Slots;
        // Cold-mount stagger (bound lists): while a freshly-mounted list's large initial window is being realized a few
        // rows per frame (not all at once → the nav cold-mount spike), Warming is true. LastGrowEpoch caps the grow to
        // ONE batch per frame (the host calls realize up to ~5x/frame: pre-layout + the 2-pass post-layout loop + the
        // 2-pass scroll catch-up) so the spread is per-FRAME, not per-call.
        public bool Warming; public int LastGrowEpoch = -1;
        // E4 steady-scroll realize budget: true while this viewport's overscan is only PARTIALLY realized because the
        // per-frame row budget was exhausted (or a nested-rail mount deferred its overscan). The window's VISIBLE band is
        // always fully realized; only the overscan halo is spread across frames. Tracked so the host stays awake until caught up.
        public bool RealizeDeferred;
    }
    private readonly Dictionary<NodeHandle, VirtualEntry> _virtuals = new();

    // Cold-realize stagger plumbing. FrameEpoch is bumped once per host Paint; ColdRealizeRowsPerFrame is the per-frame
    // grow budget. _warmingCount is an O(1) census so the host can keep the loop awake until every list finishes warming.
    private const int ColdRealizeRowsPerFrame = 4;
    public int FrameEpoch;
    private int _warmingCount;
    /// <summary>True while any bound virtual list is still spreading its initial window across frames — the host ORs this
    /// into its wake mask so the loop keeps running until the cold realize completes (it isn't a re-render or anim).</summary>
    public bool HasWarmingVirtuals => _warmingCount > 0;

    // ── E4 steady-scroll realize budget ────────────────────────────────────────────────────────────────────────────
    // The per-FRAME row pool shared across every realize call in a Paint (pre-layout ReRealizeVirtuals + the D1 loop + the
    // phase-7.6 scroll catch-up). The VISIBLE range (+1 guard row/side, the "mandatory band") is ALWAYS realized, exempt
    // from the budget — so a recorded frame can never blank a visible row (the invariant phase 7.6 exists to guarantee).
    // The budget clips ONLY the overscan refill: it extends the realized window toward the desired directional-overscan
    // window on the velocity side first, stops when exhausted, and leaves the viewport VirtualRangeDirty so the remainder
    // trickles in over subsequent frames. Reset when FrameEpoch changes (host bumps it once per Paint).
    private const int SteadyRealizeRowsPerFrame = 12;
    internal int? SteadyRealizeBudgetForTest;   // VerticalSlice-only override (InternalsVisibleTo); null ⇒ the const
    private int SteadyRealizeBudget => SteadyRealizeBudgetForTest ?? SteadyRealizeRowsPerFrame;
    private int _frameRealizeBudgetUsed;
    private int _budgetFrameEpoch = -1;
    private int _budgetDeferredCount;   // O(1) census of viewports whose overscan is mid-spread (mirrors _warmingCount)
    /// <summary>True while any viewport's overscan is only partially realized (budget-deferred or nested-rail mount
    /// deferral) — the host ORs this into its wake mask so frames keep coming until every window catches up.</summary>
    public bool HasBudgetDeferredVirtuals => _budgetDeferredCount > 0;
    private bool _realizeProgress;   // set by RealizeWindow when the realized window actually changed (drives the 2-pass loops)
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
    private readonly Dictionary<string, NodeHandle> _keyNode = new();                 // MorphId → node: the shared-layout anchor a RelativeTo follower FLIPs against
    private readonly Dictionary<int, string> _morphKeyByNode = new();                 // node → MorphId; gates shared-element teardown to actual participants
    private readonly Dictionary<int, string> _relativeKey = new();                    // follower node → the MorphId key it FLIPs relative to (Element.RelativeTo)
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
        public KeepAliveOptions? Options;
        public NodeHandle Boundary;
        public long Clock;
        public int TransientSeq;
    }
    private readonly Dictionary<int, KeepAliveState> _keepAliveState = new();
    // Reverse map: a KeepAlive entry's ROOT node index → its slot key. Lets a scroll node deep in a page resolve its
    // enclosing navigation-slot scope (ScopeFor) so ScrollMemory keys are namespaced per (tab × page-slot) — see ScrollMemory.
    private readonly Dictionary<int, string> _keepAliveRootKey = new();
    // Scroll-position memory: saved offsets keyed by (KeepAlive-slot scope ∥ content ScrollKey). Lives OFF the scene so it
    // survives the freed subtree when a page is evicted from KeepAlive — a cold revisit then seeds the offset before the
    // first layout/realize (no scroll-to-top flash). Bounded LRU; nav-rate writes only (never a frame-hot path).
    private sealed class ScrollMemory
    {
        private readonly Dictionary<string, (float X, float Y, long Used)> _map = new();
        private long _clock;
        private const int Cap = 64;
        public bool TryGet(string key, out float x, out float y)
        {
            if (_map.TryGetValue(key, out var v)) { x = v.X; y = v.Y; _map[key] = (v.X, v.Y, ++_clock); return true; }
            x = 0f; y = 0f; return false;
        }
        public void Put(string key, float x, float y)
        {
            _map[key] = (x, y, ++_clock);
            if (_map.Count <= Cap) return;
            string? lru = null; long best = long.MaxValue;
            foreach (var kv in _map) if (kv.Value.Used < best) { best = kv.Value.Used; lru = kv.Key; }
            if (lru is not null) _map.Remove(lru);
        }
    }
    private readonly ScrollMemory _scrollMem = new();
    private readonly HashSet<long> _imagePinnedNodes = new();
    private readonly Dictionary<int, List<NodeHandle>> _imageNodes = new();   // imageId → nodes that pinned it (for status→dirty)

    private Component? _root;
    private Element? _oldRoot;
    private Effect? _rootEffect;
    private bool _reconciled;   // set when any structural/column change happened → the host runs (scoped) layout
    private int _renderCount;   // component render-effects that ran since the last frame (granularity metric)
    private float _themeTransitionMs = float.NaN;        // live-re-theme cross-fade duration; armed only during a RethemeAll flush
    private readonly List<CompEntry> _rethemeScratch = new();   // snapshot of _comps.Values for RethemeAll (defensive vs reentrancy)

    /// <summary>True (and reset) if any mount/update/remove happened since the last call — the host's "layout needed" gate.</summary>
    public bool ConsumeReconciled() { var r = _reconciled; _reconciled = false; return r; }

    /// <summary>Number of component render-effects that ran since the last call (proves granular re-render in tests).</summary>
    public int ConsumeRenderCount() { var c = _renderCount; _renderCount = 0; return c; }

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
    /// <summary>Set by the host; shared-element (connected-animation) registry. A node carrying <c>Element.MorphId</c> is
    /// registered as a participant here so its art flies between routes (backdrop-effects-animation.md §5.4/§5.6).</summary>
    public ConnectedAnimation? Connected { get; set; }
    /// <summary>Set by the host (→ ScrollIntegrator.Arm); injected into each component so a control can arm a viewport for a
    /// smooth programmatic scroll (set Target, then phase 7 eases the offset toward it).</summary>
    public Action<FluentGpu.Foundation.NodeHandle>? ArmScroll { get; set; }
    /// <summary>Peek: any user scroll active or inside the host's post-scroll hold (see AppHost).</summary>
    public Func<bool>? PeekMainScrollBusy { get; set; }
    /// <summary>Set by the host; image nodes request decodes through it and pin/unpin for residency (liveness).</summary>
    public ImageCache? Images { get; set; }
    /// <summary>Set by the host; bumped on any image status change so <c>UseImage</c> consumers re-render granularly.</summary>
    public IReadSignal<int>? ImageEpoch { get; set; }
    /// <summary>Set by the host; clears input/focus state when a retained subtree is parked off the live scene chain.</summary>
    public Action<NodeHandle>? OnSubtreeDeactivated { get; set; }
    /// <summary>Set by the host; called when a component context's passive/layout effect queue transitions 0→1.</summary>
    public Action<RenderContext, bool>? RegisterPendingEffectContext { get; set; }
    /// <summary>Set by the host; called for each node as a subtree is parked/un-parked by KeepAlive so the animation +
    /// scroll tickers can quiesce that node's tracks (a parked, invisible tab must not keep the app awake / defeat the
    /// idle wake-stop). Wired to <c>AnimEngine.SetNodeParked</c> + <c>ScrollIntegrator.SetNodeParked</c>.</summary>
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
    /// <item>every mounted component's render-effect (and the root) — <see cref="ReactiveComponent"/>s are invalidated
    /// first so their cached <c>Setup</c> re-runs with preserved positional hook cells;</item>
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
        if (_root is ReactiveComponent rr) rr.InvalidateTree();
        _rootEffect?.Schedule();
        _rethemeScratch.Clear();
        foreach (var e in _comps.Values) _rethemeScratch.Add(e);   // snapshot: Schedule won't mutate _comps, but be defensive
        for (int i = 0; i < _rethemeScratch.Count; i++)
        {
            var e = _rethemeScratch[i];
            if (e.Comp is ReactiveComponent rc) rc.InvalidateTree();
            e.Effect?.Schedule();
        }
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
        var effect = new Effect(Runtime, () => RunRoot(root), owner: null, runNow: false);
        _rootEffect = effect;
        root.Context.RequestRerender = effect.Schedule;
        effect.RunNow();
    }

    private void RunRoot(Component root)
    {
        _renderCount++;
        Element newRoot = root.RunsOnce ? Reactive.Untrack(root.RenderWithHooks) : root.RenderWithHooks();
        RenderRootDiff(newRoot);
    }

    private void RenderRootDiff(Element newRoot)
    {
        if (_scene.Root.IsNull || _oldRoot is null || _oldRoot.ElementTypeId != newRoot.ElementTypeId)
        {
            if (!_scene.Root.IsNull) Remove(_scene.Root);
            var node = _scene.CreateNode(newRoot.ElementTypeId);
            _scene.Root = node;
            Mount(node, newRoot);
        }
        else
        {
            Update(_scene.Root, newRoot, _oldRoot);
            // Scoped relayout (the RunComponent idiom, :344): the root render-effect may have changed the root's own
            // layout inputs or subtree structure — a column write alone never marks LayoutDirty, which left a
            // root-level Width/Height change reconciled but never re-laid-out (RunDirty had nothing to solve).
            if (!_scene.Root.IsNull) _scene.Mark(_scene.Root, NodeFlags.LayoutDirty);
        }
        _oldRoot = newRoot;
        if (_root is not null) _root.Context.HostNode = _scene.Root;
    }

    /// <summary>Imperative full reconcile of an explicit element tree (tests / non-host callers).</summary>
    public void ReconcileRoot(Element newRoot, Element? oldRoot)
    {
        _oldRoot = oldRoot;
        RenderRootDiff(newRoot);
    }

    /// <summary>Re-realize any virtual-list windows flagged <see cref="NodeFlags.VirtualRangeDirty"/> (scroll boundary
    /// crossing) — granular, no component re-render. Called by the host each frame.</summary>
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
        // loops don't burn a pass re-checking it — the budget catch-up rides subsequent frames (HasBudgetDeferredVirtuals).
        return _realizeProgress;

        static void SwapRemoveDirty(List<NodeHandle> list, int i)
        {
            int last = list.Count - 1;
            list[i] = list[last];
            list.RemoveAt(last);
        }
    }

    private void InjectContext(RenderContext ctx, NodeHandle anchor)
    {
        ctx.Runtime = Runtime;
        ctx.Anim = Anim;
        ctx.Images = Images;
        ctx.Scene = _scene;
        ctx.ArmScroll = ArmScroll;
        ctx.PeekMainScrollBusy = PeekMainScrollBusy;
        ctx.AnchorNode = anchor;
        ctx.ResolveContextSignal = ResolveContext;
        ctx.ImageEpoch = ImageEpoch;
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
        _reconciled = true;
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

    // ── Update ────────────────────────────────────────────────────────────────────────────────────

    private void Update(NodeHandle node, Element newEl, Element oldEl)
    {
        if (ReferenceEquals(newEl, oldEl)) return;

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
                // DEBUG-only frozen-props tripwire: the factory we're about to discard may carry NEW caller data into
                // a field that froze at mount. Let the live component compare (only if it opted in). The CompiledIn
                // const folds this whole block away in release — zero cost, no probe allocation on the hot path.
                if (ReuseGuard.CompiledIn && ReuseGuard.Enabled)
                {
                    var liveComp = _comps[node].Comp;
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
            var oldContent = (oldEl as ScrollEl)?.Content;
            var content = _scene.FirstChild(node);
            if (content.IsNull)
            {
                content = _scene.CreateNode(nse.Content.ElementTypeId);
                _scene.AppendChild(node, content);
                Mount(content, nse.Content);
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
            }
            _scene.ScrollRef(node).ContentNode = content;
            return;
        }

        if (newEl is VirtualListEl nve)
        {
            WriteColumns(node, nve, isMount: false);
            RealizeWindow(node, nve);
            return;
        }

        if (newEl is ShowEl nsh)
        {
            // Parent re-renders replace the stored ShowEl and reschedule the boundary effect (mirrors
            // UpdateSkeletonRegion), so the new Then/Else children — and the new When thunk's deps — take hold.
            UpdateShow(node, nsh);
            return;
        }

        if (newEl is ForElBase nfe)
        {
            // Parent re-renders replace the stored ForElBase and reschedule the boundary effect (mirrors UpdateShow),
            // so the fresh Items/KeyOf/Row closures take hold instead of freezing at first mount (the Show-parity fix —
            // ForEl.Update used to be a no-op, which froze rows built from parent render state).
            UpdateFor(node, nfe);
            return;
        }

        if (newEl is KeepAliveEl)
        {
            return;   // autonomous retained-page boundary
        }

        if (newEl is SkelRegionEl nskr)
        {
            UpdateSkeletonRegion(node, nskr);
            return;
        }

        if (newEl is ContextProviderEl np)
        {
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
        // node silently loses (bind wiring is mount-only). The CompiledIn const folds this away in release.
        if (BindContract.CompiledIn && BindContract.Enabled && BindFlip(newEl, oldEl) is { } flipped)
            BindContract.Flip(newEl.GetType().Name, flipped);

        if (RecordChanged(newEl, oldEl)) WriteColumns(node, newEl, isMount: false, oldEl);
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
        PolylineStrokeEl x => b is PolylineStrokeEl y ? PolylineStrokeElDiff.FirstBoundFlip(x, y) : null,
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
        PolylineStrokeEl x => b is not PolylineStrokeEl y || PolylineStrokeElDiff.AnyChanged(x, y),
        _ => true,
    };

    /// <summary>Mount/update/replace a single optional child under <paramref name="parent"/> (component output, provider, Show).</summary>
    private void ReconcileSingleChild(NodeHandle parent, Element? newChild, Element? oldChild)
    {
        var child = _scene.FirstChild(parent);
        if (newChild is null)
        {
            if (!child.IsNull) Remove(child);
            return;
        }
        if (child.IsNull)
        {
            var c = _scene.CreateNode(newChild.ElementTypeId);
            _scene.AppendChild(parent, c);
            Mount(c, newChild);
            _scene.Mark(parent, NodeFlags.LayoutDirty);
        }
        else if (oldChild is not null && oldChild.ElementTypeId == newChild.ElementTypeId)
        {
            Update(child, newChild, oldChild);
        }
        else
        {
            Remove(child);
            var c = _scene.CreateNode(newChild.ElementTypeId);
            _scene.AppendChild(parent, c);
            Mount(c, newChild);
            _scene.Mark(parent, NodeFlags.LayoutDirty);
        }
    }

    private void ReplaceSingleChild(NodeHandle parent, Element? newChild)
    {
        var child = _scene.FirstChild(parent);
        if (!child.IsNull) Remove(child);

        if (newChild is null) return;

        var c = _scene.CreateNode(newChild.ElementTypeId);
        _scene.AppendChild(parent, c);
        Mount(c, newChild);
        _scene.Mark(parent, NodeFlags.LayoutDirty);
    }

    // ── Components (render-effects) ──────────────────────────────────────────────────────────────

    private void MountComponent(NodeHandle node, ComponentEl ce)
    {
        var comp = ce.Factory();
        InjectContext(comp.Context, node);
        // Mount-under-parked-ancestor: a component can be mounted into an already-parked subtree (a reactive Show/For
        // boundary inside a parked page still fires its effect). It must initialize INACTIVE — seed Parked from the
        // parent's marker and mark this node too, so the deferred-render gate holds and descendants inherit it.
        var parent = _scene.Parent(node);
        bool parked = !parent.IsNull && (_scene.Flags(parent) & NodeFlags.Parked) != 0;
        var entry = new CompEntry
        {
            Comp = comp,
            Type = ce.ComponentType,
            Parked = parked,
            DerivedSkeletonStyle = ce.DerivedSkeletonStyle,
        };
        if (parked) _scene.Mark(node, NodeFlags.Parked);
        _comps[node] = entry;
        _anchorOf[comp] = node;
        _live.Add(comp);

        // The component's per-instance activation signal (UseIsActive), created lazily on first read so a component that
        // never uses the lifecycle allocates nothing. Initial value = its current attached state (inactive if parked).
        comp.Context.GetActiveSig = () => entry.ActiveSig ??= new Signal<bool>(!entry.Parked);

        var effect = new Effect(Runtime, () => RunComponent(node, entry), owner: null, runNow: false);
        entry.Effect = effect;
        comp.Context.RequestRerender = effect.Schedule;   // imperative re-render (granular) for escape-hatch callers
        effect.RunNow();                                  // first render + child mount (deferred if mounted parked)
    }

    private void RunComponent(NodeHandle node, CompEntry entry)
    {
        if (!_scene.IsLive(node)) return;
        // Parked by Flow.KeepAlive (inactive tab/page): skip the render entirely — it is invisible and detached, so
        // rebuilding it is pure waste (and a parked page subscribed to a per-frame signal would re-render every frame).
        // Remember that a render was owed; ReactivateKeepAliveEntry replays it once when the subtree comes back.
        if (entry.Parked) { entry.DeferredRender = true; return; }
        _renderCount++;
        if (Diag.Enabled) Diag.Event("render", entry.Comp.GetType().Name);   // who re-rendered (granularity diagnosis)
        var comp = entry.Comp;
        Element newRendered = comp.RunsOnce ? Reactive.Untrack(comp.RenderWithHooks) : comp.RenderWithHooks();
        if (entry.DerivedSkeletonStyle is { } skeletonStyle)
            newRendered = SkeletonDeriver.Derive(newRendered, skeletonStyle);
        ReconcileSingleChild(node, newRendered, entry.Rendered);
        MirrorParticipation(node, _scene.FirstChild(node));
        comp.Context.HostNode = _scene.FirstChild(node);
        entry.Rendered = newRendered;
        // Scoped relayout: a re-render may have changed this component's subtree size/structure → mark its rendered
        // subtree dirty; the LayoutInvalidator walks up to the nearest layout boundary and re-solves just that subtree.
        var child = _scene.FirstChild(node);
        if (!child.IsNull) _scene.Mark(child, NodeFlags.LayoutDirty);
    }

    private void MountProvider(NodeHandle node, ContextProviderEl cp)
    {
        _providerSig[(int)node.Raw.Index] = (cp.Channel, new Signal<object?>(cp.Value));
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
        if (child.IsNull) return;
        ref LayoutInput a = ref _scene.Layout(anchor);
        ref LayoutInput c = ref _scene.Layout(child);
        a.FlexGrow = c.FlexGrow; a.FlexShrink = c.FlexShrink; a.FlexBasis = c.FlexBasis; a.AlignSelf = c.AlignSelf;
        a.Width = c.Width; a.Height = c.Height;
        a.MinW = c.MinW; a.MinH = c.MinH; a.MaxW = c.MaxW; a.MaxH = c.MaxH;
        // Transparent boundaries are NEVER input targets of their own. Keep the anchor traversable and make it yield
        // when none of its rendered descendants hit: a child with HitTestVisible=false is then skipped naturally and
        // input reaches the sibling behind it (closed retained rail), while a later live child is immediately reachable
        // without requiring visibility to propagate synchronously back through every nested component/Skel/KeepAlive
        // boundary. Copying the child's bit here was racy: DetailPage → SkelRegion → DetailShell could temporarily copy
        // false during a branch swap and leave an outer component anchor permanently blocking descent even after the
        // inner branch became hit-testable. Child hits still win because pass-through is consulted only after descent.
        _scene.Mark(anchor, NodeFlags.HitTestVisible);
        _scene.SetHitTestPassThrough(anchor, anchor);
    }

    private void ReplaceComponent(NodeHandle node, ComponentEl ce)
    {
        var kids = new List<NodeHandle>();
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c)) kids.Add(c);
        foreach (var k in kids) Remove(k);
        if (_comps.Remove(node, out var old)) { old.Effect?.Dispose(); old.Comp.Unmount(); _live.Remove(old.Comp); _anchorOf.Remove(old.Comp); }
        MountComponent(node, ce);
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

        // Smooth-resize: mark the region BoundsAnimated with a SizeMode.Reflow transition so a branch swap whose new
        // content has a DIFFERENT height eases the region's layout size — the host re-solves the parent boundary each
        // tick, so SURROUNDING content (the sibling below a failed/shorter section) reflows smoothly instead of snapping.
        // Skipped under reduced motion (the swap snaps). The FLIP deadband makes a same-height swap a no-op.
        if (se.SmoothResize && !Motion.ReducedMotion && Anim is { } sa)
        {
            sa.SetTransition(node, new LayoutTransition(
                TransitionChannels.Size,
                TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
                Size: SizeMode.Reflow));
            _scene.Mark(node, NodeFlags.BoundsAnimated);
        }

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
            // Reduced motion ⇒ no exit-fade stamp (the swap snaps); the pulse + reveal already no-op under it.
            // Content-owned reveal (SkelReveal.None): the shimmer must LINGER across the content's own per-row
            // entrance (it draws behind the live tree, so a too-fast exit leaves an empty gap before the rows fade
            // up). Floor the exit at the standard content-reveal duration so apps get the cross-dissolve for free —
            // no hand-tuned ExitMs to match the list's row-add timing.
            // The pending tree is removed immediately on Ready. Keeping it as an exiting orphan over the newly
            // mounted real tree produced a visibly "half resolved" page when an exit track was delayed/wedged.
            // The real branch still owns the configured reveal below, so the swap remains animated without two
            // complete page trees painting simultaneously.
            1 => SkeletonDeriver.Derive((se.ShimmerSource ?? se.Content)(), se.Style),
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
        _scene.Mark(node, NodeFlags.LayoutDirty);
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

    private void MountFor(NodeHandle node, ForElBase fe)
    {
        // The boundary node is a layout-transparent container; the effect reads the LATEST stored ForElBase (not its
        // mount-time capture), so a parent re-render can re-point the closures (UpdateFor) — exactly like MountShow.
        int mountIdx = (int)node.Raw.Index;
        _forEl[mountIdx] = fe;
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

        var eff = new Effect(Runtime, () =>
        {
            if (!_scene.IsLive(node)) return;

            object token = ka.Active();
            bool cacheable = ka.Options.ShouldCache?.Invoke(token) ?? true;
            string key = cacheable ? ka.KeyOf(token) : "__transient:" + (++state.TransientSeq).ToString();
            Element desired = ka.View(token) with { Key = key };

            ReconcileKeepAlive(node, state, ka.Options, key, token, desired, cacheable);
        }, owner: null, runNow: true);
        AddBinding(node, eff);
    }

    private void ReconcileKeepAlive(NodeHandle node, KeepAliveState state, KeepAliveOptions options, string key, object token, Element desired, bool cacheable)
    {
        state.Clock++;
        state.Options = options;

        LayoutTransition? transition = null;

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
            _scene.Mark(node, NodeFlags.LayoutDirty);
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
                _scene.Mark(entry.Root, NodeFlags.HitTestVisible);
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
            anim.SeedEnter(entry.Root, enter.Enter, enter);
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
        state.ExitingKey = entry.Key;
    }

    private void FinishKeepAliveExit(KeepAliveState state, KeepAliveOptions options)
    {
        if (state.ExitingKey is not { } key) return;
        state.ExitingKey = null;
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

    /// <summary>Park retained outgoing pages once their finite exit track settles. Called by the host after animation
    /// ticking; no allocations and no scan unless a KeepAlive boundary exists.</summary>
    public void FinalizeKeepAliveTransitions()
    {
        foreach (var state in _keepAliveState.Values)
        {
            if (state.ExitingKey is not { } key || !state.Entries.TryGetValue(key, out var entry)) continue;
            if (Anim is not null && Anim.HasTracks(entry.Root)) continue;
            FinishKeepAliveExit(state, state.Options ?? KeepAliveOptions.Default);
        }
    }

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
        SetSubtreeParked(entry.Root, parked: false);   // re-attached → un-park + replay any render owed while parked
        _scene.Mark(entry.Root, NodeFlags.HitTestVisible);
        _scene.Mark(parent, NodeFlags.LayoutDirty);
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
        _scene.Mark(parent, NodeFlags.LayoutDirty);
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
        }
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
            SetSubtreeResourcesActive(c, active);
    }

    // Park / un-park a kept-alive subtree so an INACTIVE page doesn't keep working while invisible. The single chokepoint
    // for three effects, all driven off one walk on the tab-switch edge:
    //  (1) component render-effects: while parked RunComponent defers (sets DeferredRender); on un-park we replay exactly
    //      the components that owed a render — once, now attached, so context resolves and content is current.
    //  (2) the per-component activation signal (UseIsActive): flipped here so UseActivation fires onDeactivated/onActivated.
    //  (3) a scene-level Parked marker + a per-node ticker notification so the animation/scroll engines quiesce this
    //      subtree's tracks (a backgrounded looping animation / mid-fling scroll must not defeat the idle wake-stop), and
    //      so a component mounted under a parked ancestor seeds inactive (MountComponent reads the marker).
    private void SetSubtreeParked(NodeHandle node, bool parked)
    {
        if (!_scene.IsLive(node)) return;
        if (parked) _scene.Mark(node, NodeFlags.Parked); else _scene.Unmark(node, NodeFlags.Parked);
        OnNodeParkedChanged?.Invoke(node, parked);
        if (_comps.TryGetValue(node, out var entry))
        {
            entry.Parked = parked;
            if (entry.ActiveSig is { } sig) sig.Value = !parked;   // value-gated; flips the UseIsActive memo → UseActivation
            if (!parked && entry.DeferredRender) { entry.DeferredRender = false; entry.Effect?.Schedule(); }
        }
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
            SetSubtreeParked(c, parked);
    }

    private void AddBinding(NodeHandle node, Computation c)
    {
        int idx = (int)node.Raw.Index;
        if (!_nodeBindings.TryGetValue(idx, out var list)) { list = new List<Computation>(2); _nodeBindings[idx] = list; }
        list.Add(c);
    }

    // A bound Prop<T> is either a thunk or a signal-direct payload — the effect body reads whichever the channel
    // carries (one null test per fire; signal-direct means the CALLER allocated no closure). Wiring stays MOUNT-ONLY:
    // a new thunk/signal supplied on a re-render is ignored (the signals-first contract — change the signal's value,
    // not the bind; locked by bind.mount-only.stale).
    private void PinImageNode(NodeHandle node, int imageId)
    {
        if (Images is null || imageId == 0 || !_scene.IsLive(node) || !IsReachableFromRoot(node)) return;
        long pinKey = ((long)(int)node.Raw.Index << 32) | (uint)imageId;
        if (_imagePinnedNodes.Add(pinKey))
        {
            Images.Pin(new ImageHandle(imageId));
            TrackImageNode(imageId, node);
        }
    }

    private void UnpinImageNode(NodeHandle node, int imageId)
    {
        if (Images is null || imageId == 0 || !_scene.IsLive(node)) return;
        long pinKey = ((long)(int)node.Raw.Index << 32) | (uint)imageId;
        if (_imagePinnedNodes.Remove(pinKey))
        {
            var h = new ImageHandle(imageId);
            Images.Unpin(h);
            UntrackImageNode(imageId, node);
            if (Images.RefsOf(h) == 0 && Images.StateOf(h) == ImageState.Pending)
                Images.Cancel(h);
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

    /// <summary>Mark every on-screen Image node holding <paramref name="imageId"/> paint-dirty (image status landed).</summary>
    public void MarkImageDirty(int imageId)
    {
        if (imageId == 0 || !_imageNodes.TryGetValue(imageId, out var list)) return;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var node = list[i];
            if (!_scene.IsLive(node))
            {
                list.RemoveAt(i);
                continue;
            }
            ref readonly NodePaint paint = ref _scene.Paint(node);
            bool owns = paint.ImageId == imageId
                || (_scene.TryGetImageEffects(node, out var effects) && effects.DerivedImageId == imageId);
            if (!owns) { list.RemoveAt(i); continue; }
            _scene.Mark(node, NodeFlags.PaintDirty);
        }
        if (list.Count == 0) _imageNodes.Remove(imageId);
    }

    private bool IsReachableFromRoot(NodeHandle node)
    {
        for (var n = node; !n.IsNull && _scene.IsLive(n); n = _scene.Parent(n))
            if (n == _scene.Root) return true;
        return false;
    }

    private void BindNode(NodeHandle node, Element el)
    {
        if (el is BoxEl b)
        {
            if (b.Transform.IsBound)
            {
                var tb = b.Transform.Thunk; var ts = b.Transform.Signal;
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).LocalTransform = tb is not null ? tb() : ts!.Value; _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty); } }, owner: null, runNow: true));
            }
            if (b.Opacity.IsBound)
            {
                var ob = b.Opacity.Thunk; var os = b.Opacity.Signal;
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).Opacity = ob is not null ? ob() : os!.Value; _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
            }
            if (b.Fill.IsBound)
            {
                var fb = b.Fill.Thunk; var fs = b.Fill.Signal;
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).Fill = fb is not null ? fb() : fs!.Value; _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
            }
            if (b.HoverFill.IsBound)
            {
                var hfb = b.HoverFill.Thunk; var hfs = b.HoverFill.Signal;
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).HoverFill = hfb is not null ? hfb() : hfs!.Value; _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
            }
            if (b.PressedFill.IsBound)
            {
                var pfb = b.PressedFill.Thunk; var pfs = b.PressedFill.Signal;
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).PressedFill = pfb is not null ? pfb() : pfs!.Value; _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
            }
            if (b.BorderColor.IsBound)
            {
                var bcb = b.BorderColor.Thunk; var bcs = b.BorderColor.Signal;
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).BorderColor = bcb is not null ? bcb() : bcs!.Value; _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
            }
            if (b.Corners.IsBound)
            {
                var crb = b.Corners.Thunk; var crs = b.Corners.Signal;
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).Corners = crb is not null ? crb() : crs!.Value; _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
            }
            if (b.RadialGradientCenter.IsBound)
            {
                var rcb = b.RadialGradientCenter.Thunk; var rcs = b.RadialGradientCenter.Signal;
                AddBinding(node, new Effect(Runtime, () =>
                {
                    if (!_scene.IsLive(node)) return;
                    Point2 center = rcb is not null ? rcb() : rcs!.Value;
                    if (float.IsFinite(center.X) && float.IsFinite(center.Y)) _scene.SetRadialGradientCenter(node, center);
                    else _scene.ClearRadialGradientCenter(node);
                }, owner: null, runNow: true));
            }
            if (b.Validation.IsBound)
            {
                // form-validation.md: resolve the semantic state → theme critical color on the UI thread (the recorder
                // stays theme-agnostic), and write the resolved border equality-gated so an unchanged validity marks NO
                // PaintDirty (Memo.OnStale re-runs this effect each keystroke, but a no-op validity dirties nothing).
                var vb = b.Validation.Thunk; var vs = b.Validation.Signal;
                AddBinding(node, new Effect(Runtime, () =>
                {
                    if (!_scene.IsLive(node)) return;
                    ValidationState st = vb is not null ? vb() : vs!.Value;
                    ColorF col = st == ValidationState.Error ? Tok.SystemFillCritical : default;
                    ref var paint = ref _scene.Paint(node);
                    if (paint.ValidationBorder == col) return;
                    paint.ValidationBorder = col;
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }, owner: null, runNow: true));
            }
            if (b.Width.IsBound)
            {
                var wb = b.Width.Thunk; var ws = b.Width.Signal;
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Layout(node).Width = wb is not null ? wb() : ws!.Value; _scene.Mark(node, NodeFlags.LayoutDirty); } }, owner: null, runNow: true));
            }
            if (b.Height.IsBound)
            {
                var hb = b.Height.Thunk; var hs = b.Height.Signal;
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Layout(node).Height = hb is not null ? hb() : hs!.Value; _scene.Mark(node, NodeFlags.LayoutDirty); } }, owner: null, runNow: true));
            }
            b.OnRealized?.Invoke(node);
        }
        else if (el is TextEl t)
        {
            if (t.Text.IsBound)
            {
                var txb = t.Text.Thunk; var txs = t.Text.Signal;
                AddBinding(node, new Effect(Runtime, () =>
                {
                    if (!_scene.IsLive(node)) return;
                    var next = _strings.Intern(txb is not null ? txb() : txs!.Value);
                    ref var paint = ref _scene.Paint(node);
                    if (paint.Text == next) return;
                    SetPaintText(ref paint, next);
                    _scene.Mark(node, NodeFlags.LayoutDirty);
                }, owner: null, runNow: true));
            }
            if (t.Color.IsBound)
            {
                var cb = t.Color.Thunk; var cs = t.Color.Signal;
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).TextColor = cb is not null ? cb() : cs!.Value; _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
            }
            t.OnRealized?.Invoke(node);
        }
        else if (el is ImageEl ime)
        {
            if (ime.Source.IsBound)
            {
                var sbind = ime.Source.Thunk; var ssig = ime.Source.Signal;
                (int dW, int dH) = ImageDecodeTarget(in ime);   // extent props don't bind, so the target is stable
                AddBinding(node, new Effect(Runtime, () =>
                {
                    if (!_scene.IsLive(node)) return;
                    string src = sbind is not null ? sbind() : ssig!.Value;
                    int newId = Images is not null && src.Length > 0
                        ? Images.Request(src, dW, dH, ImagePriority.Visible, ime.BlurHash, ime.Transition).Id : 0;
                    ref var paint = ref _scene.Paint(node);
                    int oldId = paint.ImageId;
                    int oldDerived = _scene.TryGetImageEffects(node, out var oldEffects) ? oldEffects.DerivedImageId : 0;
                    int newDerived = RequestBakedImage(in ime, newId, dW, dH);
                    if (newId != oldId)
                    {
                        UnpinImageNode(node, oldId);
                        paint.ImageId = newId;
                        if (newId != 0) PinImageNode(node, newId);
                    }
                    if (newDerived != oldDerived)
                    {
                        UnpinImageNode(node, oldDerived);
                        if (newDerived != 0) PinImageNode(node, newDerived);
                    }
                    WriteImageEffects(node, in ime, newDerived);
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }, owner: null, runNow: true));
            }
            if (ime.Placeholder.IsBound)
            {
                var pbind = ime.Placeholder.Thunk; var psig = ime.Placeholder.Signal;
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).Fill = pbind is not null ? pbind() : psig!.Value; _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
            }
        }
        else if (el is IconLayerEl ile)
        {
            // The layer TINT rides NodePaint.Fill (VisualKind.IconLayer). A bound Tint (the ThemedIcon role thunk reads
            // Tok) re-fires on RethemeAll → repaints the node with the new ColorF, NO re-raster (the mask is colorless).
            if (ile.Tint.IsBound)
            {
                var tbind = ile.Tint.Thunk; var tsig = ile.Tint.Signal;
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).Fill = tbind is not null ? tbind() : tsig!.Value; _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
            }
        }
    }

    // Decode-target px for an image: explicit Width/Height drive it; otherwise the DecodePx hint (a fluid/aspect image's
    // real box size isn't known until layout), deriving the missing cross extent from AspectRatio. 0 ⇒ source resolution.
    private static (int W, int H) ImageDecodeTarget(in ImageEl im)
    {
        int hint = !float.IsNaN(im.DecodePx) ? (int)im.DecodePx : 0;
        int w = !float.IsNaN(im.Width) ? (int)im.Width : hint;
        int h;
        if (!float.IsNaN(im.Height)) h = (int)im.Height;
        else if (!float.IsNaN(im.AspectRatio) && im.AspectRatio > 0f && w > 0) h = (int)MathF.Round(w / im.AspectRatio);
        else h = hint;
        return (w, h);
    }

    private int RequestBakedImage(in ImageEl im, int sourceId, int decodeW, int decodeH)
    {
        if (Images is null || sourceId == 0 || im.BakedBlur is not { } baked || baked.IsNone) return 0;
        return Images.RequestBakedBlur(new ImageHandle(sourceId), decodeW, decodeH, in baked, im.Transition).Id;
    }

    private void WriteImageEffects(NodeHandle node, in ImageEl im, int derivedId)
    {
        ImageMaskSpec mask = im.Mask is { } m && !m.IsNone ? m : default;
        if (derivedId != 0 || im.ColorOverlay.A > 0f || !mask.IsNone)
            _scene.SetImageEffects(node, new ImageVisualEffects(derivedId, im.ColorOverlay, mask));
        else
            _scene.ClearImageEffects(node);
    }

    // ── Scroll / Virtualization (unchanged behavior) ────────────────────────────────────────────

    private void MountScroll(NodeHandle node, ScrollEl se)
    {
        WriteColumns(node, se, isMount: true);
        var content = _scene.CreateNode(se.Content.ElementTypeId);
        _scene.AppendChild(node, content);
        Mount(content, se.Content);
        _scene.ScrollRef(node).ContentNode = content;
        se.OnRealized?.Invoke(node);
    }

    // ── Scroll-position restoration (ScrollKey → ScrollMemory). Wired through WriteColumns (mount + content-identity
    // change), UnmountSubtree (save on teardown), and ArrangeViewport (the RestorePending latch). See ScrollState. ──

    /// <summary>The enclosing KeepAlive-slot key for a node (walks parents to the nearest tracked entry root), or "" if
    /// the node is not under a KeepAlive boundary. Namespaces the scroll cache per navigation slot — which already carries
    /// the tab id — so the same content open in two tabs keeps independent saved positions. Computed once, at mount.</summary>
    private string ScopeFor(NodeHandle node)
    {
        for (var n = _scene.Parent(node); !n.IsNull; n = _scene.Parent(n))
            if (_keepAliveRootKey.TryGetValue((int)n.Raw.Index, out var k)) return k;
        return "";
    }

    private static string ScrollCacheKey(string? scope, string key)
        => scope is { Length: > 0 } ? scope + "" + key : key;

    private static void SeedRestore(ref ScrollState sc, float x, float y)
    {
        sc.OffsetX = sc.TargetX = x; sc.OffsetY = sc.TargetY = y;
        sc.RestoreX = x; sc.RestoreY = y; sc.RestorePending = true;
    }

    /// <summary>Seed/save the viewport offset for its <see cref="ScrollState.ScrollKey"/>. At mount: stamp the slot scope +
    /// key and, if a saved offset exists, seed it + arm the restore latch (so the FIRST realize/layout lands at the saved
    /// position). On a content-identity change (same reused node, new key): save the outgoing offset, then seed the
    /// incoming — or reset to the top for never-seen content (the "page B must not inherit page A's scroll" fix).</summary>
    private void ApplyScrollKey(NodeHandle node, ref ScrollState sc, string? newKey, bool isMount)
    {
        if (isMount)
        {
            sc.ScrollKey = newKey;
            if (newKey is null) return;                  // the common no-restoration viewport: skip the scope walk
            sc.ScrollScope = ScopeFor(node);
            if (_scrollMem.TryGet(ScrollCacheKey(sc.ScrollScope, newKey), out float x, out float y))
                SeedRestore(ref sc, x, y);
            return;
        }
        if (newKey == sc.ScrollKey) return;   // not a content-identity change → leave the live offset untouched
        if (sc.ScrollKey is not null)
            _scrollMem.Put(ScrollCacheKey(sc.ScrollScope, sc.ScrollKey), sc.OffsetX, sc.OffsetY);
        sc.ScrollKey = newKey;
        if (newKey is not null)
        {
            sc.ScrollScope ??= ScopeFor(node);   // scope is fixed for the node's lifetime; compute once if a null-key mount skipped it
            if (_scrollMem.TryGet(ScrollCacheKey(sc.ScrollScope, newKey), out float nx, out float ny)) { SeedRestore(ref sc, nx, ny); return; }
        }
        sc.OffsetX = sc.TargetX = 0f; sc.OffsetY = sc.TargetY = 0f; sc.RestorePending = false;   // fresh/keyless content → top
    }

    /// <summary>Persist a scroll node's offset to <see cref="_scrollMem"/> on teardown (content-swap removal or KeepAlive
    /// eviction), so a later cold revisit can restore it. The scope was stamped at mount, so no parent walk is needed.</summary>
    private void SaveScroll(NodeHandle node)
    {
        if (!_scene.HasScroll(node)) return;
        ref ScrollState sc = ref _scene.ScrollRef(node);
        if (sc.ScrollKey is not null)
            _scrollMem.Put(ScrollCacheKey(sc.ScrollScope, sc.ScrollKey), sc.OffsetX, sc.OffsetY);
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
        var content = _scene.CreateNode(1);
        _scene.AppendChild(node, content);
        _scene.ScrollRef(node).ContentNode = content;
        _virtuals[node] = new VirtualEntry { El = ve };
        RealizeWindow(node, ve, mount: true);   // E4: mount realizes the VISIBLE band only; overscan trickles via the budget
        ve.OnRealized?.Invoke(node);   // E11: viewport-handle escape hatch (ItemsView StartBringItemIntoView / sticky pinning)
    }

    private void RealizeWindow(NodeHandle node, VirtualListEl ve, bool reuseOverlap = false, bool mount = false)
    {
        if (_budgetFrameEpoch != FrameEpoch) { _budgetFrameEpoch = FrameEpoch; _frameRealizeBudgetUsed = 0; }
        if (!_virtuals.TryGetValue(node, out var entry)) { entry = new VirtualEntry(); _virtuals[node] = entry; }
        entry.El = ve;
        _scene.TryGetScroll(node, out var sc);
        var content = sc.ContentNode;
        if (content.IsNull) return;
        int prevFirstR = sc.FirstRealized, prevLastR = sc.LastRealized;   // window-change (progress) detection

        bool horizontal = ve.Horizontal;
        float offset = horizontal ? sc.OffsetX : sc.OffsetY;
        float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        if (viewport <= 0f) viewport = horizontal ? Hint(ve.Width) : Hint(ve.Height);

        int count = Math.Max(0, ve.ItemCount);
        // E5 directional overscan: buffer ahead ∝ fling speed, trim the receding edge. A nested-rail MOUNT defers overscan
        // entirely (overscan 0) so only the visible cards land this frame; the halo trickles in via the budget on later frames.
        float contentExt = horizontal ? sc.ContentW : sc.ContentH;
        float avgExtent = count > 0 && contentExt > 0f ? contentExt / count : (ve.EstimatedExtent > 0f ? ve.EstimatedExtent : 1f);
        int effOverscan = mount ? 0 : ve.Overscan;
        VirtualWindowing.DirectionalOverscan(effOverscan, sc.FlingVelocity, avgExtent, out int lowOv, out int highOv);

        int first, last, visibleFirst, visibleLast, mandFirst, mandLast;
        if (ve.Layout is not null)
        {
            // Content cross first: the arrange paths window/measure the layout at the padding-subtracted inner cross
            // (published as ContentW/H on the cross axis) — passing the raw viewport here instead made a width-keyed
            // measured layout reseed its extent table every frame (alternating cross values), flapping the anchor re-pin.
            float cross = horizontal ? (sc.ContentH > 0f ? sc.ContentH : sc.ViewportH > 0f ? sc.ViewportH : Hint(ve.Height))
                                     : (sc.ContentW > 0f ? sc.ContentW : sc.ViewportW > 0f ? sc.ViewportW : Hint(ve.Width));
            ve.Layout.Window(count, cross, viewport, offset, 0, out visibleFirst, out visibleLast);
            ve.Layout.Window(count, cross, viewport, offset, 1, out mandFirst, out mandLast);   // +1 GUARD ROW each side — row-aligned mandatory band
            ve.Layout.Window(count, cross, viewport, offset, lowOv, out first, out _);           // splice: low edge from the behind/low overscan
            ve.Layout.Window(count, cross, viewport, offset, highOv, out _, out last);           //        high edge from the ahead/high overscan
        }
        else
        {
            var table = _scene.ExtentTableFor(node, count, ve.EstimatedExtent);
            visibleFirst = table.IndexAt(offset);
            visibleLast = Math.Min(count, table.IndexAt(offset + viewport) + 1);
            mandFirst = Math.Max(0, visibleFirst - 1);
            mandLast = Math.Min(count, visibleLast + 1);
            first = Math.Max(0, table.IndexAt(offset) - lowOv);
            last = Math.Min(count, table.IndexAt(offset + viewport) + 1 + highOv);
        }
        // Layouts may be stateful. A collection shrink can therefore race a layout's cached geometry and return a
        // window from the old item count (for example [0,60) after the count became 43). Normalize every range at the
        // engine seam BEFORE using one range as another Math.Clamp bound; otherwise stale visibleLast/mandLast values
        // turn into an invalid min > max pair and escape the app loop as Argument_MinMaxValue.
        visibleFirst = Math.Clamp(visibleFirst, 0, count);
        visibleLast = Math.Clamp(visibleLast, visibleFirst, count);

        // The mandatory band (visible +1 guard row/side) must bound the desired window. Clamp it to the normalized
        // visible band/current count, then normalize and expand the directional-overscan window around it.
        mandFirst = Math.Clamp(mandFirst, 0, visibleFirst);
        mandLast = Math.Clamp(mandLast, visibleLast, count);
        first = Math.Clamp(first, 0, count);
        last = Math.Clamp(last, first, count);
        if (first > mandFirst) first = mandFirst;
        if (last < mandLast) last = mandLast;

        // E4 budget: the mandatory band is realized unconditionally; the overscan halo is clipped to the per-frame row pool.
        bool budgetDeficit = ClipRealizeBudget(in sc, mandFirst, mandLast, ref first, ref last);
        bool stayDirty = budgetDeficit || mount;
        if (last < first) last = first;
        int w = last - first;
        int visibleSlots = Math.Clamp(visibleLast - first, 0, w);

        if (ve.RowBind is not null)
        {
            RealizeBoundWindow(node, content, entry, ve, first, last, w, visibleSlots, stayDirty);
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
                entry.Prev is null ? default : entry.Prev.AsSpan(0, entry.PrevLen), first - prevFirst);

            if (entry.Prev is not null) { Array.Clear(entry.Prev, 0, entry.PrevLen); ArrayPool<Element>.Shared.Return(entry.Prev); }
            entry.Prev = cur; entry.PrevLen = w; entry.PrevFirst = first;

            ref ScrollState scw = ref _scene.ScrollRef(node);
            scw.FirstRealized = first; scw.LastRealized = last;
            if (stayDirty) _scene.Mark(node, NodeFlags.VirtualRangeDirty);   // overscan owed → re-realize next frame (budget/mount)
            else _scene.Unmark(node, NodeFlags.VirtualRangeDirty);

            FireWindowLifecycle(ve, oldFirst, oldLast, first, last);   // E11: Prepared/Clearing/VisibleRange (cold realize edge)
        }

        // Progress = the realized window of this viewport actually changed (drives the AppHost 2-pass loops; a purely
        // budget-deferred re-check that moves nothing returns false so a pass isn't burned). Census = overscan still owed.
        _scene.TryGetScroll(node, out var scPost);
        if (scPost.FirstRealized != prevFirstR || scPost.LastRealized != prevLastR) _realizeProgress = true;
        bool deferred = (scPost.LastRealized - scPost.FirstRealized) > 0 && (_scene.Flags(node) & NodeFlags.VirtualRangeDirty) != 0;
        if (deferred && !entry.RealizeDeferred) { entry.RealizeDeferred = true; _budgetDeferredCount++; }
        else if (!deferred && entry.RealizeDeferred) { entry.RealizeDeferred = false; _budgetDeferredCount--; }
    }

    /// <summary>E4 realize budget clip: the mandatory band <c>[mandFirst,mandLast)</c> (visible +1 guard/side) is exempt
    /// and already covered by <paramref name="first"/>/<paramref name="last"/>; extend the overscan halo toward the desired
    /// window on the velocity side first, charging the shared per-frame row pool. Already-realized rows still inside the
    /// desired window are kept for free (never contracted by budget), so the charge is only the NEW extension and the pass
    /// is idempotent. Returns true when budget ran out before the desired window was reached (overscan still owed).</summary>
    private bool ClipRealizeBudget(in ScrollState sc, int mandFirst, int mandLast, ref int first, ref int last)
    {
        int df = first, dl = last;   // desired directional-overscan window (already ⊇ mandatory)
        // "Have" = rows already realized this scroll episode, clamped into [desired, mandatory] — so the mandatory band
        // reads as already-covered and stale rows outside the desired window are dropped.
        int haveFirst = mandFirst, haveLast = mandLast;
        if (sc.LastRealized > sc.FirstRealized)
        {
            haveFirst = Math.Clamp(sc.FirstRealized, df, mandFirst);
            haveLast = Math.Clamp(sc.LastRealized, mandLast, dl);
        }
        int budget = Math.Max(0, SteadyRealizeBudget - _frameRealizeBudgetUsed);
        int lowWant = haveFirst - df;    // overscan rows still owed below
        int highWant = dl - haveLast;    // overscan rows still owed above
        bool forward = sc.FlingVelocity >= 0f;   // velocity side = ahead: fill it first
        int newFirst = haveFirst, newLast = haveLast;
        if (forward)
        {
            int t = Math.Min(highWant, budget); newLast = haveLast + t; budget -= t;
            int t2 = Math.Min(lowWant, budget); newFirst = haveFirst - t2; budget -= t2;
        }
        else
        {
            int t = Math.Min(lowWant, budget); newFirst = haveFirst - t; budget -= t;
            int t2 = Math.Min(highWant, budget); newLast = haveLast + t2; budget -= t2;
        }
        _frameRealizeBudgetUsed += (haveFirst - newFirst) + (newLast - haveLast);
        first = newFirst; last = newLast;
        return newFirst > df || newLast < dl;   // desired not fully reached ⇒ overscan owed
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

    /// <summary>
    /// Bound (signals-first) realize: slots are PERSISTENT — recycling a slot = writing its index signal, which
    /// re-runs only that row's reactive binds (TextBind/FillBind/SourceBind). No element rebuild, no reconcile, no
    /// node churn: the thumb-drag storm degenerates to signal writes + granular column updates. The host flushes the
    /// runtime again after re-realize so the rebinds land in the SAME frame.
    /// </summary>
    private void RealizeBoundWindow(NodeHandle node, NodeHandle content, VirtualEntry entry,
                                    VirtualListEl ve, int first, int last, int w, int visibleSlots, bool stayDirty)
    {
        var rowBind = ve.RowBind!;
        var slots = entry.Slots ??= new List<(Signal<int>, Element)>(Math.Max(4, w));
        bool structural = false;
        int oldFirst = entry.PrevFirst, oldLast = entry.PrevFirst + entry.PrevLen;   // E11 lifecycle window delta

        // Cold-mount stagger can spread overscan slot creation, but it must never present a partial visible window.
        // A shimmer-to-ready swap has no cover once the real branch mounts, so all visible rows must exist this frame.
        int minVisibleTarget = Math.Clamp(visibleSlots, 0, w);
        if (!entry.Warming && ve.StaggerColdRealize && Math.Max(slots.Count + ColdRealizeRowsPerFrame, minVisibleTarget) < w) { entry.Warming = true; _warmingCount++; }
        int target = w;
        if (entry.Warming)
        {
            if (entry.LastGrowEpoch == FrameEpoch && slots.Count >= minVisibleTarget) target = slots.Count;   // already grew this frame → roll to next frame
            else
            {
                target = Math.Min(w, Math.Max(minVisibleTarget, slots.Count + ColdRealizeRowsPerFrame));
                if (target > slots.Count) entry.LastGrowEpoch = FrameEpoch;
            }
        }

        while (slots.Count < target)   // grow: the template runs ONCE per slot; its element tree is never rebuilt
        {
            var sig = new Signal<int>(first + slots.Count);
            Element el = rowBind(sig);
            var child = _scene.CreateNode(el.ElementTypeId);
            _scene.AppendChild(content, child);
            Mount(child, el);
            slots.Add((sig, el));
            structural = true;
        }
        while (slots.Count > w)   // shrink (viewport got smaller): drop the trailing slot
        {
            var c = _scene.FirstChild(content);
            for (int ord = 1; !c.IsNull && ord < slots.Count; ord++) c = _scene.NextSibling(c);
            if (!c.IsNull) Remove(c);
            slots.RemoveAt(slots.Count - 1);
            structural = true;
        }

        // Walk the slot ROOT nodes (direct children of content, in slot order) alongside the rebind loop so a recycled
        // slot can drop transient interaction state — the same reset the recycling window-diff does (ReconcileWindow,
        // the Unmark at the recycle match). Without it a slot rebound under a STATIONARY pointer keeps the previous
        // item's hover/press tint until the next pointer move.
        // `mat` = rows actually materialized so far (== w once warmed). All downstream state keys off the MATERIALIZED
        // count, never the uncapped target `w`, so a partially-realized window is internally consistent (rebind walk,
        // PrevLen, LastRealized, focus/tab-stop). LastRealized = first + mat means NeedsRealize re-flags the still-short
        // window next layout, and SlotRootForIndex correctly reports "not realized yet" for the not-yet-mounted tail.
        int mat = Math.Min(slots.Count, w);
        var slotRoot = _scene.FirstChild(content);
        for (int i = 0; i < mat; i++)
        {
            var sig = slots[i].Index;
            int idx = first + i;
            int prevIdx = sig.Peek();
            if (prevIdx != idx)
            {
                if (!slotRoot.IsNull)
                    _scene.Unmark(slotRoot, NodeFlags.Hovered | NodeFlags.Pressed | NodeFlags.Focused | NodeFlags.FocusVisual);
                sig.Value = idx;   // granular rebind — only this slot's bind effects re-run
                // ScrollState's range can already equal [first,last) while a slot signal is one generation behind.
                // Range-only progress detection would then return false and AppHost would skip the post-realize Flush,
                // presenting mixed rows (index-bound title from the new item, component snapshot cells from the old).
                _realizeProgress = true;
                ve.OnItemIndexChanged?.Invoke(prevIdx, idx);   // E11: a persistent bound slot moved indices
            }
            if (!slotRoot.IsNull) slotRoot = _scene.NextSibling(slotRoot);
        }

        bool moved = structural || first != entry.PrevFirst || mat != entry.PrevLen;
        entry.PrevFirst = first;
        entry.PrevLen = mat;
        if (moved) _scene.Mark(content, NodeFlags.LayoutDirty);   // children are positioned by FirstRealized + order
        if (structural) _reconciled = true;

        ref ScrollState scw = ref _scene.ScrollRef(node);
        scw.FirstRealized = first; scw.LastRealized = first + mat;
        bool warmingIncomplete = entry.Warming && mat < w;
        if (warmingIncomplete || stayDirty)
            _scene.Mark(node, NodeFlags.VirtualRangeDirty);   // stay dirty → next frame realizes the next batch (warm) / owed overscan (budget/mount)
        else
        {
            if (entry.Warming) { entry.Warming = false; _warmingCount--; }
            _scene.Unmark(node, NodeFlags.VirtualRangeDirty);
        }

        FireWindowLifecycle(ve, oldFirst, oldLast, first, first + mat);
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
    private void ReconcileWindow(NodeHandle node, ReadOnlySpan<Element> newKids, ReadOnlySpan<Element> oldKids, int shift)
    {
        int oldN = oldKids.Length, newN = newKids.Length;
        if (oldN == 0 && newN == 0) return;

        Span<NodeHandle> oldNodes = oldN <= 128 ? stackalloc NodeHandle[oldN] : new NodeHandle[oldN];
        {
            int i = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull && i < oldN; c = _scene.NextSibling(c)) oldNodes[i++] = c;
        }

        Span<bool> used = oldN <= 128 ? stackalloc bool[oldN] : new bool[oldN];
        Span<NodeHandle> newNodes = newN <= 128 ? stackalloc NodeHandle[newN] : new NodeHandle[newN];

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
                Update(oldNodes[os], nk, ok);
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
                Update(oldNodes[match], nk, oldKids[match]);
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
                Mount(child, nk);
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
        if (structural || newN != oldN || shift != 0) _scene.Mark(node, NodeFlags.LayoutDirty);
    }

    /// <summary>True if the subtree is a PLAIN visual tree (box/grid/text/image/polyline, no reactive binds, no
    /// OnRealized): safe to rebind onto a recycled node. Components/flow/providers/scrollers and bound elements capture
    /// identity at mount — they must mount fresh. EXCEPTION: the shared theme-text brushes (<see cref="Ui.IsThemeTextBrush"/>,
    /// e.g. every default-colored TextEl) are recyclable — their persisted mount-time binding re-fires on RethemeAll for
    /// the SAME singleton thunk, so the recycled node needs no rewrite (the pairing's thunk stability is DEBUG-asserted in
    /// <see cref="ShapeCompatible"/>).</summary>
    private static bool IsRecyclable(Element el)
    {
        switch (el)
        {
            case TextEl t:
                return !t.Text.IsBound && (!t.Color.IsBound || Ui.IsThemeTextBrush(t.Color)) && t.OnRealized is null;
            case SpanTextEl:
                return true;   // plain leaf — WriteColumns rewrites every column incl. the span run/handlers
            case ImageEl im:
                return !im.Source.IsBound && !im.Placeholder.IsBound;
            case IconLayerEl il:
                return !il.Tint.IsBound;   // ThemedIcon always binds Tint (theme-live), so an icon layer mounts fresh (like a bound image)
            case PolylineStrokeEl:
                return true;
            case BoxEl b:
                if (b.Transform.IsBound || b.Opacity.IsBound || b.Fill.IsBound || b.BorderColor.IsBound
                    || b.RadialGradientCenter.IsBound || b.Width.IsBound || b.Height.IsBound
                    || b.OnRealized is not null || b.OnBoundsChanged is not null) return false;
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
    // that in DEBUG/CI. The comparison mirrors ReconcileChildren EXACTLY (keyed children match by Key — so a keyed
    // child legally appears/disappears, e.g. ItemContainer's selection-state ring/common/checkbox; UNKEYED children
    // match POSITIONALLY — so their type sequence must be stable, and each key present in BOTH must stay shape-compat).
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
    //   • UNKEYED children match POSITIONALLY by index — overlapping positions must agree on type + recurse-compat;
    //     a surplus on either side is a legal TAIL insert/remove (exactly the unchecked↔checked glyph child).
    //   • KEYED children match by Key — a key in only one side is a free insert/remove (the selected↔unselected ring);
    //     a key in BOTH must recurse-compat.
    // Values (Fill/Color/Opacity/…) are ignored — only structure is checked. Leaves (Text/Image/Polyline) compare by type.
    private static bool ShapeCompatible(Element a, Element b)
    {
        if (a.ElementTypeId != b.ElementTypeId) return false;
        // Text color-BIND CLASS is structure-adjacent under recycle: Update never re-registers bindings, so a recycled
        // node keeps its MOUNT-TIME color binding. A default theme-brush node is only correct when the paired element
        // binds the SAME singleton thunk (same tier) — an unbound↔bound flip or a different tier would strand the
        // persisted binding on the wrong color after RethemeAll. Unbound explicit colors are free to differ (WriteColumns
        // rewrites them every recycle) so their VALUE is ignored — only the bind class + thunk identity is checked.
        if (a is TextEl ta && b is TextEl tb
            && (ta.Color.IsBound != tb.Color.IsBound
                || (ta.Color.IsBound && !ReferenceEquals(ta.Color.Thunk, tb.Color.Thunk)))) return false;
        Element[]? ac = a switch { BoxEl x => x.Children, GridEl x => x.Children, _ => null };
        Element[]? bc = b switch { BoxEl x => x.Children, GridEl x => x.Children, _ => null };
        if (ac is null || bc is null) return true;   // leaf type matched (no child structure to compare)

        // Positional pass over UNKEYED children (Key == null): walk both in order, comparing overlapping slots only.
        // A trailing surplus on either side is a legal tail insert/remove (ReconcileChildren removes/mounts it).
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

    internal void ReconcileChildren(NodeHandle node, ReadOnlySpan<Element> newKids, ReadOnlySpan<Element> oldKids)
    {
        int oldN = oldKids.Length, newN = newKids.Length;
        if (oldN == 0 && newN == 0) return;

        Span<NodeHandle> oldNodes = oldN <= 128 ? stackalloc NodeHandle[oldN] : new NodeHandle[oldN];
        if (oldN > 0)
        {
            int i = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull && i < oldN; c = _scene.NextSibling(c)) oldNodes[i++] = c;
        }

        Dictionary<string, int>? keyMap = null;
        if (oldN > 32)
            for (int j = 0; j < oldN; j++)
                if (oldKids[j].Key is string k) (keyMap ??= new()).TryAdd(k, j);

        Span<bool> used = oldN <= 128 ? stackalloc bool[oldN] : new bool[oldN];
        Span<NodeHandle> newNodes = newN <= 128 ? stackalloc NodeHandle[newN] : new NodeHandle[newN];
        bool structural = false;
        // A PURE reorder (same key set, different order — e.g. a list reverse) creates/removes nothing, but the
        // re-appended child order still needs a relayout to move the rows. Non-monotonic match order detects it.
        bool moved = false;
        int lastMatch = -1;

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
            else if (nk.Key is null && i < oldN && !used[i] && oldKids[i].Key is null
                && oldKids[i].ElementTypeId == nk.ElementTypeId)
                match = i;

            if (match >= 0)
            {
                used[match] = true;
                if (match < lastMatch) moved = true; else lastMatch = match;
                newNodes[i] = oldNodes[match];
                Update(oldNodes[match], nk, oldKids[match]);
            }
            else
            {
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
        }

        for (int j = 0; j < oldN; j++)
            if (!used[j]) { Remove(oldNodes[j]); structural = true; }

        for (int i = 0; i < newN; i++)
        {
            _scene.Detach(newNodes[i]);
            _scene.AppendChild(node, newNodes[i]);
        }

        // Structural change to the child set (including a pure keyed reorder) → relayout this container's subtree
        // (scoped to its boundary).
        if (structural || moved || newN != oldN) _scene.Mark(node, NodeFlags.LayoutDirty);
    }

    // ── Removal / unmount (dispose reactive effects) ────────────────────────────────────────────

    private void Remove(NodeHandle node)
    {
        _reconciled = true;
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
            _scene.Orphan(node);
            anim.SeedExit(node, spec.Exit, spec);
            return;
        }
        UnmountSubtree(node);
        _scene.FreeSubtree(node);
    }

    private void UnmountSubtree(NodeHandle node)
    {
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c)) UnmountSubtree(c);

        int idx = (int)node.Raw.Index;
        Anim?.CancelAll(node);
        SaveScroll(node);   // persist this viewport's offset for its ScrollKey so a cold revisit can restore it
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
            if (paint.VisualKind == VisualKind.Image && paint.ImageId != 0) UnpinImageNode(node, paint.ImageId);
            if (paint.VisualKind == VisualKind.Image && _scene.TryGetImageEffects(node, out var effects)
                && effects.DerivedImageId != 0) UnpinImageNode(node, effects.DerivedImageId);
        }
        if (_nodeBindings.Remove(idx, out var binds)) for (int i = 0; i < binds.Count; i++) binds[i].Dispose();
        _providerSig.Remove(idx);
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
        if (_comps.Remove(node, out var e)) { e.Effect?.Dispose(); e.Comp.Unmount(); _live.Remove(e.Comp); _anchorOf.Remove(e.Comp); }
        if (_virtuals.Remove(node, out var v))
        {
            if (v.Warming) _warmingCount--;   // a bound list unmounted mid-warm → keep the warming census exact
            if (v.RealizeDeferred) _budgetDeferredCount--;   // …and the budget-deferred census exact (E4)
            if (v.Prev is not null)
            {
                Array.Clear(v.Prev, 0, v.PrevLen);
                ArrayPool<Element>.Shared.Return(v.Prev);
            }
        }
    }

    // ── Column writes (POD → scene) ─────────────────────────────────────────────────────────────

    /// <summary>Resolve a <see cref="ScrollEdgeCues"/> prop to the packed <c>ScrollState.EdgeCueConfig</c> bits, collapsing
    /// <see cref="ScrollEdgeCues.Auto"/> against the app default ONCE here so the recorder never branches on the default.</summary>
    private static byte ResolveEdgeCues(ScrollEdgeCues c)
    {
        var eff = c == ScrollEdgeCues.Auto ? ScrollEdgeCuesDefaults.Default : c;
        return eff switch
        {
            ScrollEdgeCues.None => 0,
            ScrollEdgeCues.FadeAndChevron => (byte)(ScrollState.EdgeCueFadeBit | ScrollState.EdgeCueChevronBit),
            _ => ScrollState.EdgeCueFadeBit,   // Fade (and the defensive Auto-already-resolved fallthrough)
        };
    }

    /// <summary>Compile an element's declarative <see cref="ScrollBindDsl"/> entries into POD
    /// <see cref="FluentGpu.Animation.ScrollBind"/> rows on the scene's scroll-binding slab: resolve the enclosing scroller
    /// once, bake literal-px anchors now (geometry anchors re-bake at ArrangeViewport), and link each into the scroller's
    /// eval chain + the node's teardown chain. Re-bake is wholesale (free the node's old rows first) so a prop change
    /// self-cleans. Subsumes the old StickyTop / OnPinned / ScrollStretchHeader wiring.</summary>
    private void BakeScrollBinds(NodeHandle node, Element el)
    {
        var dsls = el.ScrollBinds;
        int nodeIdx = (int)node.Raw.Index;
        var table = _scene.ScrollBinds;
        bool had = table.NodeHasBinds(nodeIdx);
        if ((dsls is null || dsls.Length == 0) && !had) return;   // nothing now, nothing before → skip
        bool hadTrailing = table.NodeOwnsSink(nodeIdx, FluentGpu.Animation.BindSink.PresentedHTrailing);
        bool willOwnTrailing = OwnsScrollSink(dsls, FluentGpu.Animation.BindSink.PresentedHTrailing);

        NodeHandle scroller = NodeHandle.Null;
        for (var p = _scene.Parent(node); !p.IsNull; p = _scene.Parent(p))
            if ((_scene.Flags(p) & NodeFlags.Scrollable) != 0) { scroller = p; break; }

        if (hadTrailing && (!willOwnTrailing || scroller.IsNull))
        {
            ref NodePaint p = ref _scene.Paint(node);
            p.PresentedH = float.NaN;
            p.ChildShiftY = 0f;
            _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
        table.ClearNode(nodeIdx);                                 // wholesale re-bake (slot reuse self-cleans)
        if (dsls is null || dsls.Length == 0) return;

        foreach (var d in dsls)
        {
            var row = new FluentGpu.Animation.ScrollBind { Target = node, OnFlag = d.OnFlag, FlagBit = d.FlagBit };
            if (d.PinTop is { } inset)
            {
                row.PinKind = 1;
                row.Inset = inset;
                row.Source = FluentGpu.Animation.ScrollChannel.Offset;
                row.Sink = FluentGpu.Animation.BindSink.TransY;
                row.Flags |= FluentGpu.Animation.ScrollBind.FlagPaintAbove;
            }
            else if (d.ClipTopAtViewport is { } clipInset)
            {
                row.PinKind = 3;                                   // sticky clip-top — evaluated in the phase-7 pin pass
                row.Inset = clipInset;
                row.Source = FluentGpu.Animation.ScrollChannel.Offset;
                row.Sink = FluentGpu.Animation.BindSink.ClipTop;
            }
            else if (d.StretchFromTop)
            {
                row.Source = FluentGpu.Animation.ScrollChannel.OverscrollBand;
                row.Sink = FluentGpu.Animation.BindSink.ScaleUniform;
                row.Flags |= FluentGpu.Animation.ScrollBind.FlagStretchClosedForm;
            }
            else
            {
                row.Source = d.From;
                row.Sink = d.To;
                row.OutLo = d.OutStart;
                row.OutHi = d.OutEnd;
                row.Ease = d.Ease;
                if (d.Clamp) row.Flags |= FluentGpu.Animation.ScrollBind.FlagClampOut;
                var r = d.Range;
                if (!r.HasValue)
                {
                    row.AnchorA = FluentGpu.Animation.ScrollBindAnchor.OffsetFrac; row.AnchorAv = 0f;
                    row.AnchorB = FluentGpu.Animation.ScrollBindAnchor.OffsetFrac; row.AnchorBv = 1f;
                }
                else { row.AnchorA = r.A; row.AnchorAv = r.Av; row.AnchorB = r.B; row.AnchorBv = r.Bv; }
                if (IsGeometryAnchor(row.AnchorA) || IsGeometryAnchor(row.AnchorB))
                    row.Flags |= FluentGpu.Animation.ScrollBind.FlagGeometryAnchor;   // (re)bake at ArrangeViewport
                else { row.RangeA = row.AnchorAv; row.RangeB = row.AnchorBv; }          // literal-px ⇒ bake now
            }
            table.Add(nodeIdx, scroller, row);
        }
    }

    private static bool OwnsScrollSink(ScrollBindDsl[]? dsls, FluentGpu.Animation.BindSink sink)
    {
        if (dsls is null) return false;
        for (int i = 0; i < dsls.Length; i++)
        {
            var d = dsls[i];
            if (d.PinTop is null && d.ClipTopAtViewport is null && !d.StretchFromTop && d.To == sink) return true;
        }
        return false;
    }

    private static bool IsGeometryAnchor(FluentGpu.Animation.ScrollBindAnchor a)
        => a != FluentGpu.Animation.ScrollBindAnchor.OffsetPx;

    /// <summary>Build a LayoutTransition from the new declarative Element fields (Enter/Exit/Transition/Layout/Stagger)
    /// so the rework's authoring surface routes through the existing FLIP/enter/exit seed lifecycle. Null when the node
    /// declares none. (While* gesture states are wired separately; this covers Enter/Exit/Layout + the parent Stagger.)</summary>
    private LayoutTransition? SynthesizeDeclarative(NodeHandle node, Element el)
    {
        bool hasEnter = el.Enter is not null, hasExit = el.Exit is not null;
        float stagger = hasEnter ? StaggerDelayMs(node) : 0f;   // a parent's Stagger delays this child's Enter
        if (el.Layout is { } lt)
            return (!hasEnter && !hasExit) ? lt
                 : lt with
                   {
                       Enter = hasEnter ? (el.Enter!.Value with { Active = true }) : lt.Enter,
                       Exit = hasExit ? (el.Exit!.Value with { Active = true }) : lt.Exit,
                       DelayMs = lt.DelayMs + stagger,
                   };
        if (!hasEnter && !hasExit) return null;
        TransitionDynamics dyn = el.Transition is { } m ? m.ToDynamics() : TransitionDynamics.Default;
        return new LayoutTransition(
            TransitionChannels.Opacity, dyn, SizeMode.Auto,
            Enter: hasEnter ? (el.Enter!.Value with { Active = true }) : default,
            Exit: hasExit ? (el.Exit!.Value with { Active = true }) : default,
            DelayMs: stagger);
    }

    /// <summary>A parent's <see cref="FluentGpu.Dsl.Element.Stagger"/> delays each child's ENTER by (sibling index ×
    /// stagger ms) — a list/shelf whose items reveal in sequence. O(siblings) at mount (not the hot path); returns 0
    /// when no parent staggers.</summary>
    private float StaggerDelayMs(NodeHandle node)
    {
        NodeHandle parent = _scene.Parent(node);
        if (parent.IsNull || !_childStagger.TryGetValue((int)parent.Raw.Index, out float per) || per <= 0f) return 0f;
        int i = 0;
        for (var c = _scene.FirstChild(parent); !c.IsNull && c.Raw.Index != node.Raw.Index; c = _scene.NextSibling(c)) i++;
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

    private void WriteColumns(NodeHandle node, Element el, bool isMount, Element? old = null)
    {
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

        // Generic scroll-driven bindings (sticky / overscroll-stretch / parallax / fade / collapse / shy / pull-to-refresh):
        // compiled to POD ScrollBind rows for every element type, replacing the old per-feature StickyTop/ScrollStretchHeader passes.
        BakeScrollBinds(node, el);

        // Stagger (declarative): a parent records its per-child entrance delay; each child's SynthesizeDeclarative reads
        // it + the child's sibling index to delay that child's Enter (a staggered list/shelf reveal). Reconciler-local;
        // cleared when Stagger drops to 0. Set for every element type (any container can stagger its children).
        if (el.Stagger > 0f) _childStagger[(int)node.Raw.Index] = el.Stagger; else _childStagger.Remove((int)node.Raw.Index);

        switch (el)
        {
            case BoxEl b:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                bool hasSurface = b.TabShape || b.Fill.IsBound || b.Fill.Value.A > 0f
                                  || b.HoverFill.IsBound || b.HoverFill.Value.A > 0f
                                  || b.PressedFill.IsBound || b.PressedFill.Value.A > 0f
                                  || b.BorderWidth > 0f || b.OnClick is not null || b.Gradient is not null || b.BorderBrush is not null;
                paint.VisualKind = b.TabShape ? VisualKind.TabShape : hasSurface ? VisualKind.Box : VisualKind.None;

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
                if (b.BorderBrush is { } bb) _scene.SetBorderBrush(node, bb); else _scene.ClearBorderBrush(node);
                if (b.HoverGradient is { } hg) _scene.SetHoverGradient(node, hg); else _scene.ClearHoverGradient(node);
                if (b.PressedGradient is { } pg) _scene.SetPressedGradient(node, pg); else _scene.ClearPressedGradient(node);
                if (b.HoverBorderBrush is { } hbb) _scene.SetHoverBorderBrush(node, hbb); else _scene.ClearHoverBorderBrush(node);
                if (b.PressedBorderBrush is { } pbb) _scene.SetPressedBorderBrush(node, pbb); else _scene.ClearPressedBorderBrush(node);
                if (b.Acrylic is { } ac) _scene.SetAcrylic(node, ac); else _scene.ClearAcrylic(node);
                if (b.EdgeFade is { } bef) _scene.SetEdgeFade(node, bef); else _scene.ClearEdgeFade(node);
                _scene.SetHitTestPassThrough(node, b.HitTestPassThrough ? node : NodeHandle.Null);   // self = yield to behind, except own children

                // Transform origin (used by static + animated scale/rotate; default centre). Set unconditionally so an
                // AnimEngine ScaleX/Y track or a TransformBind pivots about the requested origin (e.g. a menu's top edge).
                paint.OriginX = b.TransformOriginX;
                paint.OriginY = b.TransformOriginY;

                // Static transform/opacity ONLY when the element declares one AND there's no transform binding/animation
                // owning the channel (else a re-render would reset the bound/animated value to identity each frame).
                if (!b.Transform.IsBound && (b.OffsetX != 0f || b.OffsetY != 0f || b.ScaleX != 1f || b.ScaleY != 1f || b.Rotation != 0f))
                {
                    var tf = Affine2D.Translation(b.OffsetX, b.OffsetY);
                    if (b.Rotation != 0f) tf = tf.Multiply(Affine2D.Rotation(b.Rotation * (MathF.PI / 180f)));
                    if (b.ScaleX != 1f || b.ScaleY != 1f) tf = tf.Multiply(Affine2D.Scale(b.ScaleX, b.ScaleY));
                    paint.LocalTransform = tf;
                }
                // Static→identity hand-off: when the PREVIOUS element declared a static transform and this one
                // declares none, clear the stale static — the in-place differ can morph e.g. a rail (OffsetY=14)
                // into a plain track box, which otherwise keeps painting/hit-testing 14px off forever (the ranged-
                // slider tooltip hover flap). Identity-declared elements still leave ANIM-owned matrices alone:
                // this writes only on the declared-static → declared-identity transition, never per re-render.
                else if (!b.Transform.IsBound && old is BoxEl ob && !ob.Transform.IsBound
                         && (ob.OffsetX != 0f || ob.OffsetY != 0f || ob.ScaleX != 1f || ob.ScaleY != 1f || ob.Rotation != 0f))
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
                paint.BlurCachePolicy = b.BlurCachePolicy;



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
                }

                ref LayoutInput li = ref _scene.Layout(node);
                li.Direction = b.Direction;
                li.Gap = b.Gap;
                li.Padding = b.Padding;
                li.Margin = b.Margin;
                // Like the TransformBind/OpacityBind guards above: a bound dimension is owned by its bind effect — a
                // re-render must not clobber it back to the static prop (the bind re-fires only when its signal changes).
                if (!b.Width.IsBound) li.Width = b.Width.Value;
                if (!b.Height.IsBound) li.Height = b.Height.Value;
                li.MinW = b.MinWidth; li.MinH = b.MinHeight; li.MaxW = b.MaxWidth; li.MaxH = b.MaxHeight;
                li.FlexGrow = b.Grow;
                li.FlexShrink = b.Shrink;
                li.FlexBasis = b.Basis;
                li.AlignSelf = b.AlignSelf;
                li.Justify = b.Justify;
                li.AlignItems = b.AlignItems;
                li.Wrap = b.Wrap;
                li.AspectRatio = b.AspectRatio;   // CSS aspect-ratio: FlexLayout.Measure derives the missing extent (Ui.AspectRatio)
                if (b.ZStack) _scene.Mark(node, NodeFlags.ZStack); else _scene.Unmark(node, NodeFlags.ZStack);
                if (b.ClipToBounds) _scene.Mark(node, NodeFlags.ClipsToBounds); else _scene.Unmark(node, NodeFlags.ClipsToBounds);
                if (b.IsolateLayout) _scene.Mark(node, NodeFlags.LayoutBoundary); else _scene.Unmark(node, NodeFlags.LayoutBoundary);
                if (b.CounterScale) _scene.Mark(node, NodeFlags.CounterScaled); else _scene.Unmark(node, NodeFlags.CounterScaled);
                // sticky + overscroll-stretch are now generic ScrollBinds (baked in WriteColumns' common section above).
                _scene.SetBoundsChangedHandler(node, b.OnBoundsChanged);
                if (b.Animate is { } at && Anim is { } anim)
                {
                    anim.SetTransition(node, at);
                    _scene.Mark(node, NodeFlags.BoundsAnimated);
                    if (isMount && at.Enter.Active)
                    {
                        anim.SeedEnter(node, at.Enter, at);
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
                    if ((dt.Channels & TransitionChannels.Bounds) != 0) _scene.Mark(node, NodeFlags.BoundsAnimated);
                    if (isMount && dt.Enter.Active)
                    {
                        danim.SeedEnter(node, dt.Enter, dt);
                        if (dt.Size == SizeMode.Reflow) danim.PendingEnterReflow.Add(node);
                    }
                }
                // NEW declarative gesture-state targets (WhileHover/WhilePressed/WhileFocus): stashed for the
                // InteractionState priority resolver, which springs them on the input edge (AppHost wires
                // ApplyInteractionEdge). All-null clears the row, so it's inert for nodes that don't use While*.
                Anim?.SetInteractTargets((int)node.Raw.Index, b.WhileHover, b.WhilePressed, b.WhileFocus, b.Transition ?? MotionTok.ControlFaster);
                if (b.HitTestVisible) _scene.Mark(node, NodeFlags.HitTestVisible); else _scene.Unmark(node, NodeFlags.HitTestVisible);
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
                else _scene.Flags(node) &= ~NodeFlags.Focusable;
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
                li.AlignSelf = s.AlignSelf;

                _scene.Mark(node, NodeFlags.ClipsToBounds);
                ref ScrollState ss = ref _scene.ScrollRef(node);
                ss.Orientation = s.Horizontal ? (byte)1 : (byte)0;
                ss.ContentSized = s.ContentSized;
                // Pinch-zoom opt-in (Input owns the live ZoomFactor — re-reconciling the element must NOT reset a
                // mid-gesture / committed zoom, so only the declared opt-in + clamp bounds are written here).
                ss.Zoomable = s.Zoomable;
                ss.MinZoom = s.MinZoom; ss.MaxZoom = s.MaxZoom;
                ss.EdgeCueConfig = ResolveEdgeCues(s.EdgeCues);
                ss.Chaining = (byte)s.Chaining;                  // nested-scroll hand-off policy (touch pan)
                // Change-only scroll-geometry observer (the escape hatch; pull-to-refresh / analytics).
                if (s.OnScrollGeometryChanged is { } obs) _scene.SetScrollObserver(node, obs.Project, obs.Action);
                else _scene.ClearScrollObserver(node);
                if (s.EdgeFade is { } sef) _scene.SetEdgeFade(node, sef); else _scene.ClearEdgeFade(node);
                ss.AutoEdgeFade = s.AutoEdgeFade; ss.AutoEdgeFadeBand = s.AutoEdgeFade ? 40f : 0f;
                // ScrollEl owns the real clip + edge-fade scope, so it can be the native escape root for a hovered
                // descendant (PagedShelf's measured realize-all strip). Paint-order only; never a hit-test bit.
                ref InteractionInfo ii = ref _scene.Interaction(node);
                if (s.HoverElevateClipRoot) ii.HandlerMask |= InteractionInfo.HoverElevateClipRootBit;
                else ii.HandlerMask &= ~InteractionInfo.HoverElevateClipRootBit;
                ss.AlwaysShowBar = s.AlwaysShowScrollbar;
                ss.SuppressBar = s.SuppressScrollBar;
                ApplyScrollKey(node, ref ss, s.ScrollKey, isMount);   // seed (mount) / save+seed (content change) the offset
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
                li.AlignSelf = v.AlignSelf;

                _scene.Mark(node, NodeFlags.ClipsToBounds);
                ref ScrollState sc = ref _scene.ScrollRef(node);
                sc.Orientation = v.Horizontal ? (byte)1 : (byte)0;
                sc.ItemCount = Math.Max(0, v.ItemCount);
                sc.Layout = v.Layout;
                sc.Overscan = v.Overscan;
                sc.EdgeCueConfig = ResolveEdgeCues(v.EdgeCues);
                if (v.OnScrollGeometryChanged is { } obs) _scene.SetScrollObserver(node, obs.Project, obs.Action);
                else _scene.ClearScrollObserver(node);
                if (v.EdgeFade is { } vef) _scene.SetEdgeFade(node, vef); else _scene.ClearEdgeFade(node);
                sc.AutoEdgeFade = v.AutoEdgeFade; sc.AutoEdgeFadeBand = v.AutoEdgeFade ? 40f : 0f;
                sc.SuppressBar = v.SuppressScrollBar;
                ApplyScrollKey(node, ref sc, v.ScrollKey, isMount);   // seed BEFORE RealizeWindow → first window at saved row
                break;
            }
            case GridEl g:
            {
                ref LayoutInput li = ref _scene.Layout(node);
                li.Width = g.Width; li.Height = g.Height;
                li.FlexGrow = g.Grow; li.FlexShrink = g.Shrink; li.FlexBasis = g.Basis;
                li.AlignSelf = g.AlignSelf; li.Margin = g.Margin; li.Padding = g.Padding;
                _scene.SetGrid(node, new GridSpec { Columns = g.Columns, ColGap = g.ColGap, RowGap = g.RowGap, RowHeight = g.RowHeight, MinColWidth = g.MinColWidth });
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
                li.AlignSelf = pl.AlignSelf;
                break;
            }
            case ImageEl im:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.Image;
                if (!im.Placeholder.IsBound) paint.Fill = im.Placeholder.Value;   // bound rows tint via the binding
                paint.Corners = im.Corners;
                paint.ImageFit = (byte)im.Fit;
                paint.ImageFocusX = Math.Clamp(im.FocusX, 0f, 1f);
                paint.ImageFocusY = Math.Clamp(im.FocusY, 0f, 1f);

                // Decode-target size: explicit Width/Height when set; otherwise the DecodePx hint (a fluid/aspect image's
                // real box size isn't known until layout), deriving the cross dimension from AspectRatio when possible.
                (int decodeW, int decodeH) = ImageDecodeTarget(in im);

                int oldId = paint.ImageId;
                if (!im.Source.IsBound)   // bound rows request via the binding (the effect owns pin/unpin)
                {
                    int newId = (Images is not null && im.Source.Value.Length > 0)
                        ? Images.Request(im.Source.Value, decodeW, decodeH, ImagePriority.Visible, im.BlurHash, im.Transition).Id : 0;
                    if (newId != oldId)
                    {
                        UnpinImageNode(node, oldId);
                        paint.ImageId = newId;
                        if (newId != 0) PinImageNode(node, newId);
                        _scene.Mark(node, NodeFlags.PaintDirty);
                    }
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
                li.Margin = im.Margin; li.AlignSelf = im.AlignSelf;
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
                li.Margin = il.Margin; li.AlignSelf = il.AlignSelf;
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
                    if (paint.Text != newText) { SetPaintText(ref paint, newText); _scene.Mark(node, NodeFlags.LayoutDirty); }
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
                li.AlignSelf = t.AlignSelf;

                WriteTextSelection(node, t.IsTextSelectionEnabled, t.SelectionHighlightColor);
                break;
            }
            case SpanTextEl st:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.Text;
                paint.TextColor = st.Color;
                paint.TextDecorations = 0;   // span decorations ride the span-run artifact path, not the single-run bits

                ref LayoutInput li = ref _scene.Layout(node);
                var spans = st.Spans;
                // Re-register the POD shaping overlay ONLY when a shaping input changed: the run id is the key of the
                // measure cache AND the renderer's shaped-run cache, so minting a fresh id IS the invalidation; an
                // identical re-render keeps the id (steady reconciles never churn the caches).
                int runId = li.TextStyle.SpanRunId;
                bool same = runId != 0 && _scene.TryGetSpanText(node, out var oldSpans) && SameSpanShaping(oldSpans, spans);
                if (!same)
                {
                    int total = 0;
                    for (int i = 0; i < spans.Length; i++) total += spans[i].Text?.Length ?? 0;
                    var concat = string.Create(total, spans, static (dst, src) =>
                    {
                        int at = 0;
                        for (int i = 0; i < src.Length; i++)
                        {
                            var s = src[i].Text;
                            if (string.IsNullOrEmpty(s)) continue;
                            s.AsSpan().CopyTo(dst[at..]);
                            at += s.Length;
                        }
                    });
                    var styles = new SpanStyle[spans.Length];
                    int pos = 0;
                    for (int i = 0; i < spans.Length; i++)
                    {
                        ref readonly var sp = ref spans[i];
                        int len = sp.Text?.Length ?? 0;
                        byte flags = (byte)((sp.Underline ? SpanStyle.UnderlineBit : 0)
                                          | (sp.Strikethrough ? SpanStyle.StrikethroughBit : 0)
                                          | (sp.OnClick is not null ? SpanStyle.LinkBit : 0));
                        var spanFam = _strings.Intern(sp.FontFamily);
                        _strings.AddRef(spanFam);   // released by SceneStore.ReleaseSpanRun with the run
                        styles[i] = new SpanStyle(pos, pos + len, sp.Weight, sp.Size ?? 0f, spanFam, sp.Color, flags);
                        pos += len;
                    }
                    int newRunId = SpanRunTable.Shared.Create(styles);
                    SpanRunTable.Shared.AddRef(newRunId);
                    _scene.ReleaseSpanRun(runId);
                    runId = newRunId;

                    var newText = _strings.Intern(concat);
                    if (paint.Text != newText) SetPaintText(ref paint, newText);
                    _scene.Mark(node, NodeFlags.LayoutDirty);
                }
                _scene.SetSpanText(node, spans);   // always — hyperlink actions may change without a shaping change

                var famId = _strings.Intern(st.FontFamily);
                if (li.TextStyle.FontFamily != famId) { _strings.AddRef(famId); _strings.Release(li.TextStyle.FontFamily); }
                li.TextStyle = new TextStyle(famId, st.Size, st.Weight != 0 ? st.Weight : (ushort)400,
                    st.Wrap, st.Trim, st.MaxLines, st.CharSpacing, st.LineHeight, st.LineStacking, st.LineBounds, runId);
                li.Margin = st.Margin;
                li.Width = st.Width; li.Height = st.Height;
                li.MinW = st.MinWidth; li.MinH = st.MinHeight; li.MaxW = st.MaxWidth; li.MaxH = st.MaxHeight;
                li.FlexGrow = st.Grow; li.FlexShrink = st.Shrink; li.FlexBasis = st.Basis;
                li.AlignSelf = st.AlignSelf;

                // Hyperlink spans: hit-testable so the dispatcher can resolve Hand over the span rects and fire the
                // span's OnClick (WinUI inline Hyperlink — RichTextBlock.cpp:2995 SetCursor(MouseCursorHand)).
                ref InteractionInfo ii = ref _scene.Interaction(node);
                bool hasLinks = false;
                for (int i = 0; i < spans.Length && !hasLinks; i++) hasLinks = spans[i].OnClick is not null;
                if (hasLinks)
                {
                    ii.HandlerMask |= InteractionInfo.SpanLinksBit;
                    _scene.Mark(node, NodeFlags.WantsPointer);
                }
                else ii.HandlerMask &= ~(uint)InteractionInfo.SpanLinksBit;

                WriteTextSelection(node, st.IsTextSelectionEnabled, st.SelectionHighlightColor);
                break;
            }
        }

        if (!isMount) { _scene.Mark(node, NodeFlags.PaintDirty); _reconciled = true; }
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
            _scene.Flags(node) &= ~NodeFlags.Focusable;
            _scene.ClearTextSelection(node);
        }
        _scene.SetSelectionHighlight(node, highlight);
    }

    /// <summary>True when two span arrays are SHAPING-identical (text, weight, size, family, color, decorations,
    /// link-ness) — everything the span-run id keys downstream caches on. OnClick identity is deliberately excluded:
    /// re-rendered lambdas must not churn run ids (the scene's TextSpan[] side-table carries the fresh actions).</summary>
    private static bool SameSpanShaping(TextSpan[] a, TextSpan[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            ref readonly var x = ref a[i];
            ref readonly var y = ref b[i];
            if (!string.Equals(x.Text, y.Text, StringComparison.Ordinal) || x.Weight != y.Weight
                || x.Color != y.Color || x.Underline != y.Underline || x.Strikethrough != y.Strikethrough
                || !Nullable.Equals(x.Size, y.Size) || !string.Equals(x.FontFamily, y.FontFamily, StringComparison.Ordinal)
                || (x.OnClick is null) != (y.OnClick is null)) return false;
        }
        return true;
    }
}
