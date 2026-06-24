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
    private sealed class CompEntry { public Component Comp = null!; public Element? Rendered; public Type Type = null!; public Effect? Effect; public bool Parked; public bool DeferredRender; public Signal<bool>? ActiveSig; }
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

    // Context provider value signals, keyed by provider node index (a consumer resolves by walking ancestors).
    private readonly Dictionary<int, (object Channel, Signal<object?> Sig)> _providerSig = new();
    // Host-published ambient contexts (Viewport.Size, FrameDiagnostics.Current), keyed by channel.
    private readonly Dictionary<object, Signal<object?>> _ambient = new();

    // Per-node reactive bindings + control-flow effects, disposed when the node is unmounted.
    private readonly Dictionary<int, List<Computation>> _nodeBindings = new();
    private readonly Dictionary<int, Element?> _showState = new();             // last-mounted branch per ShowEl node
    private readonly Dictionary<int, (Element[] Prev, int Len)> _forState = new();   // last realized children per ForEl node
    // Skeleton-loading: per SkelRegionEl node, the last branch (0 none / 1 shimmer / 2 real / 3 failed), the last-mounted
    // child element (for ReconcileSingleChild's type-compare), and the reveal-group token (for the group coordinator).
    private readonly Dictionary<int, (byte Branch, Element? El, object? Group)> _skelState = new();
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
        public long Clock;
        public int TransientSeq;
    }
    private readonly Dictionary<int, KeepAliveState> _keepAliveState = new();
    private readonly HashSet<int> _imagePinnedNodes = new();
    private readonly List<NodeHandle> _dirtyVirtualScratch = new();

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
    /// <summary>Set by the host (→ ScrollAnimator.Arm); injected into each component so a control can arm a viewport for a
    /// smooth programmatic scroll (set Target, then phase 7 eases the offset toward it).</summary>
    public Action<FluentGpu.Foundation.NodeHandle>? ArmScroll { get; set; }
    /// <summary>Set by the host; image nodes request decodes through it and pin/unpin for residency (liveness).</summary>
    public ImageCache? Images { get; set; }
    /// <summary>Set by the host; bumped on any image status change so <c>UseImage</c> consumers re-render granularly.</summary>
    public IReadSignal<int>? ImageEpoch { get; set; }
    /// <summary>Set by the host; clears input/focus state when a retained subtree is parked off the live scene chain.</summary>
    public Action<NodeHandle>? OnSubtreeDeactivated { get; set; }
    /// <summary>Set by the host; called for each node as a subtree is parked/un-parked by KeepAlive so the animation +
    /// scroll tickers can quiesce that node's tracks (a parked, invisible tab must not keep the app awake / defeat the
    /// idle wake-stop). Wired to <c>AnimEngine.SetNodeParked</c> + <c>ScrollAnimator.SetNodeParked</c>.</summary>
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
        if (_virtuals.Count == 0) return false;
        _dirtyVirtualScratch.Clear();
        foreach (var kv in _virtuals)
            if (_scene.IsLive(kv.Key) && (_scene.Flags(kv.Key) & NodeFlags.VirtualRangeDirty) != 0 && kv.Value.El is not null)
                _dirtyVirtualScratch.Add(kv.Key);
        for (int i = 0; i < _dirtyVirtualScratch.Count; i++)
        {
            var node = _dirtyVirtualScratch[i];
            if (_virtuals.TryGetValue(node, out var e) && e.El is { } el) RealizeWindow(node, el, reuseOverlap: true);
        }
        return _dirtyVirtualScratch.Count > 0;
    }

    private void InjectContext(RenderContext ctx, NodeHandle anchor)
    {
        ctx.Runtime = Runtime;
        ctx.Anim = Anim;
        ctx.Images = Images;
        ctx.Scene = _scene;
        ctx.ArmScroll = ArmScroll;
        ctx.AnchorNode = anchor;
        ctx.ResolveContextSignal = ResolveContext;
        ctx.ImageEpoch = ImageEpoch;
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
        if (el is ForEl fe) { MountFor(node, fe); return; }
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
            // change → replace.
            if (oldEl is ComponentEl oce && oce.ComponentType == nce.ComponentType && _comps.ContainsKey(node))
                return;
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
            // The boundary effect manages its own child reactively; nothing to do on a parent re-render.
            return;
        }

        if (newEl is ForEl nfe)
        {
            return;   // autonomous reactive list boundary
        }

        if (newEl is KeepAliveEl)
        {
            return;   // autonomous retained-page boundary
        }

        if (newEl is SkelRegionEl)
        {
            return;   // autonomous skeleton boundary — its effect manages shimmer↔real on the loadable's state
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

        WriteColumns(node, newEl, isMount: false, oldEl);
        ReconcileChildren(node, ChildrenOf(newEl), ChildrenOf(oldEl));
    }

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
        var entry = new CompEntry { Comp = comp, Type = ce.ComponentType, Parked = parked };
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
        _showState[(int)node.Raw.Index] = null;
        var eff = new Effect(Runtime, () =>
        {
            if (!_scene.IsLive(node)) return;
            Element? desired = se.When() ? se.Then : se.Else;
            int idx = (int)node.Raw.Index;
            _showState.TryGetValue(idx, out var last);
            ReconcileSingleChild(node, desired, last);
            _showState[idx] = desired;
            MirrorParticipation(node, _scene.FirstChild(node));
        }, owner: null, runNow: true);
        AddBinding(node, eff);
    }

    // Native skeleton-loading boundary (modelled on MountShow): a reconcile effect reads the loadable's STATE via
    // se.Pending()/se.Failed() (subscribes State; se.Content() reads Value via Peek, so a value change never re-fires
    // here) and mounts one of three branches — a DERIVED shimmer, the real content, or the onFailed UI. On the
    // shimmer→real edge it blur-reveals the freshly-mounted real subtree (the existing recipes); the looping pulse is
    // cancelled on the orphan-exit path (Remove) so HasTracks drops and the idle wake-stop is not defeated.
    private void MountSkeletonRegion(NodeHandle node, SkelRegionEl se)
    {
        int mountIdx = (int)node.Raw.Index;
        _skelState[mountIdx] = (0, null, se.Group);
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
            if (!_scene.IsLive(node)) return;
            int idx = (int)node.Raw.Index;
            var (lastBranch, lastEl, _) = _skelState.TryGetValue(idx, out var st) ? st : ((byte)0, (Element?)null, se.Group);

            byte branch = se.Failed() ? (byte)3 : se.Pending() ? (byte)1 : (byte)2;   // 1 shimmer / 2 real / 3 failed
            if (branch == lastBranch) return;   // state didn't flip the branch (a value-only change won't reach here)

            Element? desired = branch switch
            {
                // Reduced motion ⇒ no exit-fade stamp (the swap snaps); the pulse + reveal already no-op under it.
                // Content-owned reveal (SkelReveal.None): the shimmer must LINGER across the content's own per-row
                // entrance (it draws behind the live tree, so a too-fast exit leaves an empty gap before the rows fade
                // up). Floor the exit at the standard content-reveal duration so apps get the cross-dissolve for free —
                // no hand-tuned ExitMs to match the list's row-add timing.
                1 => Motion.ReducedMotion
                        ? SkeletonDeriver.Derive(se.ShimmerSource(), se.Style)
                        : StampShimmerExit(SkeletonDeriver.Derive(se.ShimmerSource(), se.Style),
                            se.Reveal == SkelReveal.None ? Math.Max(se.Style.ExitMs, Expressive.Slow) : se.Style.ExitMs),
                3 => se.OnFailed?.Invoke(),
                _ => se.Content(),
            };
            ReconcileSingleChild(node, desired, lastEl);
            // Inherit the active branch's layout participation (Grow/size) onto this transparent boundary — exactly like a
            // component (ReconcileComponent) or KeepAlive (ReconcileKeepAlive) does. Without it the SkelRegion node keeps its
            // default Grow=0, so a Grow=1 content subtree (e.g. a single-column virtualized list whose only intrinsic height
            // is its chrome) can't fill its parent: the region collapses to the content's intrinsic size and a viewport-driven
            // list realizes 0 rows (the empty-Liked bug). Large-intrinsic content (home shelves, a detail rail) masked it.
            MirrorParticipation(node, _scene.FirstChild(node));
            _scene.Mark(node, NodeFlags.LayoutDirty);
            _skelState[idx] = (branch, desired, se.Group);

            if (branch == 1 && Anim is { } a1)
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
        }, owner: null, runNow: true);
        AddBinding(node, eff);
    }

    // Stamp the derived shimmer ROOT with a fast Opacity+Blur EXIT terminal so the orphan-exit (Remove) cross-blurs it
    // out while the real content blur-reveals in (the two-layer cross-blur, same slot). Only a BoxEl root carries Animate.
    private static Element StampShimmerExit(Element shimmerRoot, float exitMs)
        => shimmerRoot is BoxEl b
            ? b with
            {
                Animate = new LayoutTransition(
                    TransitionChannels.Opacity,
                    TransitionDynamics.Tween(exitMs, Easing.SmoothOut),
                    Exit: new EnterExit(Opacity: 0f, Blur: Expressive.BlurMedium, Active: true)),
            }
            : shimmerRoot;

    private void MountFor(NodeHandle node, ForEl fe)
    {
        var eff = new Effect(Runtime, () =>
        {
            if (!_scene.IsLive(node)) return;
            int n = fe.Count();
            var cur = n == 0 ? Array.Empty<Element>() : new Element[n];
            for (int i = 0; i < n; i++)
            {
                var el = fe.ItemAt(i);
                string key = fe.KeyOf?.Invoke(i) ?? ("#" + i);
                cur[i] = el with { Key = key };
            }
            int idx = (int)node.Raw.Index;
            var prev = _forState.TryGetValue(idx, out var p) ? p : (Array.Empty<Element>(), 0);
            ReconcileChildren(node, cur.AsSpan(0, n), prev.Item1.AsSpan(0, prev.Item2));
            _forState[idx] = (cur, n);
        }, owner: null, runNow: true);
        AddBinding(node, eff);
    }

    // ── Fine-grained bindings (signal → scene node, no re-render) ────────────────────────────────

    private void MountKeepAlive(NodeHandle node, KeepAliveEl ka)
    {
        int idx = (int)node.Raw.Index;
        var state = new KeepAliveState();
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

        if (state.ActiveKey is { } oldKey && oldKey != key && state.Entries.TryGetValue(oldKey, out var oldActive))
        {
            DeactivateKeepAliveEntry(oldActive, options);
            if (!oldActive.Cacheable) state.Entries.Remove(oldKey);
        }

        if (!state.Entries.TryGetValue(key, out var entry))
        {
            var root = _scene.CreateNode(desired.ElementTypeId);
            _scene.AppendChild(node, root);
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
        EvictInactiveKeepAliveEntries(state, options);
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
            UnmountSubtree(entry.Root);
            _scene.FreeSubtree(entry.Root);
        }
        var root = _scene.CreateNode(desired.ElementTypeId);
        _scene.AppendChild(parent, root);
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
        UnmountSubtree(entry.Root);
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
        if (_imagePinnedNodes.Add((int)node.Raw.Index))
            Images.Pin(new ImageHandle(imageId));
    }

    private void UnpinImageNode(NodeHandle node, int imageId)
    {
        if (Images is null || imageId == 0 || !_scene.IsLive(node)) return;
        if (_imagePinnedNodes.Remove((int)node.Raw.Index))
            Images.Unpin(new ImageHandle(imageId));
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
                    if (newId == paint.ImageId) return;
                    UnpinImageNode(node, paint.ImageId);
                    paint.ImageId = newId;
                    PinImageNode(node, newId);
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }, owner: null, runNow: true));
            }
            if (ime.Placeholder.IsBound)
            {
                var pbind = ime.Placeholder.Thunk; var psig = ime.Placeholder.Signal;
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).Fill = pbind is not null ? pbind() : psig!.Value; _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
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

    // ── Scroll / Virtualization (unchanged behavior) ────────────────────────────────────────────

    private void MountScroll(NodeHandle node, ScrollEl se)
    {
        WriteColumns(node, se, isMount: true);
        var content = _scene.CreateNode(se.Content.ElementTypeId);
        _scene.AppendChild(node, content);
        Mount(content, se.Content);
        _scene.ScrollRef(node).ContentNode = content;
    }

    private void MountVirtual(NodeHandle node, VirtualListEl ve)
    {
        WriteColumns(node, ve, isMount: true);
        var content = _scene.CreateNode(1);
        _scene.AppendChild(node, content);
        _scene.ScrollRef(node).ContentNode = content;
        _virtuals[node] = new VirtualEntry { El = ve };
        RealizeWindow(node, ve);
        ve.OnRealized?.Invoke(node);   // E11: viewport-handle escape hatch (ItemsView StartBringItemIntoView / sticky pinning)
    }

    private void RealizeWindow(NodeHandle node, VirtualListEl ve, bool reuseOverlap = false)
    {
        if (!_virtuals.TryGetValue(node, out var entry)) { entry = new VirtualEntry(); _virtuals[node] = entry; }
        entry.El = ve;
        _scene.TryGetScroll(node, out var sc);
        var content = sc.ContentNode;
        if (content.IsNull) return;

        bool horizontal = ve.Horizontal;
        float offset = horizontal ? sc.OffsetX : sc.OffsetY;
        float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        if (viewport <= 0f) viewport = horizontal ? Hint(ve.Width) : Hint(ve.Height);

        int count = ve.ItemCount;
        int first, last;
        if (ve.Layout is not null)
        {
            float cross = horizontal ? (sc.ViewportH > 0f ? sc.ViewportH : Hint(ve.Height))
                                     : (sc.ViewportW > 0f ? sc.ViewportW : Hint(ve.Width));
            ve.Layout.Window(count, cross, viewport, offset, ve.Overscan, out first, out last);
        }
        else
        {
            var table = _scene.ExtentTableFor(node, count, ve.EstimatedExtent);
            first = Math.Max(0, table.IndexAt(offset) - ve.Overscan);
            last = Math.Min(count, table.IndexAt(offset + viewport) + 1 + ve.Overscan);
        }
        if (last < first) last = first;
        int w = last - first;

        if (ve.RowBind is not null)
        {
            RealizeBoundWindow(node, content, entry, ve, first, last, w);
            return;
        }

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
        _scene.Unmark(node, NodeFlags.VirtualRangeDirty);

        FireWindowLifecycle(ve, oldFirst, oldLast, first, last);   // E11: Prepared/Clearing/VisibleRange (cold realize edge)
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
                                    VirtualListEl ve, int first, int last, int w)
    {
        var rowBind = ve.RowBind!;
        var slots = entry.Slots ??= new List<(Signal<int>, Element)>(Math.Max(4, w));
        bool structural = false;
        int oldFirst = entry.PrevFirst, oldLast = entry.PrevFirst + entry.PrevLen;   // E11 lifecycle window delta

        // Cold-mount stagger: if filling this window needs MANY new slots (a fresh mount / hint→real grow, never a
        // recycle — a scroll keeps slots.Count == w), realize at most ColdRealizeRowsPerFrame rows per FRAME so the
        // initial window doesn't reconcile in one frame (the nav cold-mount spike). The scroll extent is computed from
        // the FULL ItemCount (ContentExtent), independent of the realized window, so the scrollbar stays correct as rows
        // fill in. A STEADY scroll realize (slots.Count already == w) is never capped — its leading edge must not lag.
        if (!entry.Warming && ve.StaggerColdRealize && slots.Count + ColdRealizeRowsPerFrame < w) { entry.Warming = true; _warmingCount++; }
        int target = w;
        if (entry.Warming)
        {
            if (entry.LastGrowEpoch == FrameEpoch) target = slots.Count;   // already grew this frame → roll to next frame
            else { target = Math.Min(w, slots.Count + ColdRealizeRowsPerFrame); entry.LastGrowEpoch = FrameEpoch; }
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
        if (entry.Warming && mat < w)
            _scene.Mark(node, NodeFlags.VirtualRangeDirty);   // stay dirty → next frame realizes the next batch
        else
        {
            if (entry.Warming) { entry.Warming = false; _warmingCount--; }
            _scene.Unmark(node, NodeFlags.VirtualRangeDirty);
        }

        FireWindowLifecycle(ve, oldFirst, oldLast, first, first + mat);
    }

    /// <summary>
    /// Window-diff for virtualization (virtualization.md: recycle, don't churn). Overlapping rows reuse their element
    /// OBJECT (identity-matched to their existing node — a no-op). Every other new row RECYCLES a scrolled-out node of
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

        // Pass 1: overlap — a reused element object keeps its node untouched.
        for (int i = 0; i < newN; i++)
        {
            int os = i + shift;
            if ((uint)os < (uint)oldN && ReferenceEquals(newKids[i], oldKids[os]))
            {
                newNodes[i] = oldNodes[os];
                used[os] = true;
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
    /// identity at mount — they must mount fresh.</summary>
    private static bool IsRecyclable(Element el)
    {
        switch (el)
        {
            case TextEl t:
                return !t.Text.IsBound && !t.Color.IsBound;
            case SpanTextEl:
                return true;   // plain leaf — WriteColumns rewrites every column incl. the span run/handlers
            case ImageEl im:
                return !im.Source.IsBound && !im.Placeholder.IsBound;
            case PolylineStrokeEl:
                return true;
            case BoxEl b:
                if (b.Transform.IsBound || b.Opacity.IsBound || b.Fill.IsBound
                    || b.Width.IsBound || b.Height.IsBound || b.OnRealized is not null || b.OnBoundsChanged is not null) return false;
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
        if (Anim is { } anim && anim.TryGetTransition(node, out var spec) && spec.Exit.Active)
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

        Connected?.CaptureOnLeave(node, removeTag: true);   // shared-element: a tagged node leaving captures its reverse snapshot
        int idx = (int)node.Raw.Index;
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
        }
        if (_nodeBindings.Remove(idx, out var binds)) for (int i = 0; i < binds.Count; i++) binds[i].Dispose();
        _providerSig.Remove(idx);
        _showState.Remove(idx);
        _forState.Remove(idx);
        if (_skelState.Remove(idx, out var sk) && sk.Group is { } skg) SkelGroupCoordinator.Unregister(skg, idx);
        if (_comps.Remove(node, out var e)) { e.Effect?.Dispose(); e.Comp.Unmount(); _live.Remove(e.Comp); _anchorOf.Remove(e.Comp); }
        if (_virtuals.Remove(node, out var v))
        {
            if (v.Warming) _warmingCount--;   // a bound list unmounted mid-warm → keep the warming census exact
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
        table.ClearNode(nodeIdx);                                 // wholesale re-bake (slot reuse self-cleans)
        if (dsls is null || dsls.Length == 0) return;

        // Resolve the enclosing scroll viewport once (the per-frame eval is then pure index arithmetic).
        NodeHandle scroller = NodeHandle.Null;
        for (var p = _scene.Parent(node); !p.IsNull; p = _scene.Parent(p))
            if ((_scene.Flags(p) & NodeFlags.Scrollable) != 0) { scroller = p; break; }

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

    private static bool IsGeometryAnchor(FluentGpu.Animation.ScrollBindAnchor a)
        => a != FluentGpu.Animation.ScrollBindAnchor.OffsetPx;

    private void WriteColumns(NodeHandle node, Element el, bool isMount, Element? old = null)
    {
        // Shared-element (connected-animation) tag: a node carrying MorphId is a Hero participant — its laid-out rect +
        // art are tracked so they fly between routes. Runs for every element type (cover Image, skeleton/cover Box).
        if (el.MorphId is { Length: > 0 } morphKey) Connected?.NoteTagged(node, morphKey);

        // Generic scroll-driven bindings (sticky / overscroll-stretch / parallax / fade / collapse / shy / pull-to-refresh):
        // compiled to POD ScrollBind rows for every element type, replacing the old per-feature StickyTop/ScrollStretchHeader passes.
        BakeScrollBinds(node, el);

        switch (el)
        {
            case BoxEl b:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                bool hasSurface = b.TabShape || b.Fill.IsBound || b.Fill.Value.A > 0f || b.HoverFill.A > 0f || b.PressedFill.A > 0f
                                  || b.BorderWidth > 0f || b.OnClick is not null || b.Gradient is not null || b.BorderBrush is not null;
                paint.VisualKind = b.TabShape ? VisualKind.TabShape : hasSurface ? VisualKind.Box : VisualKind.None;

                // Implicit BrushTransition (WinUI, 83ms): a LIVE node re-rendered with a different fill/border cross-fades
                // from the previously-DISPLAYED color (mid-flight retargets stay continuous) instead of snapping.
                // A BOUND fill is excluded per-channel: its effect owns paint.Fill, so diffing against the static would
                // arm a phantom fade toward a color the channel doesn't own (the border sub-block is unaffected).
                bool fillOwned = !b.Fill.IsBound;
                float fillMs = ThemeTransitionOr(b.BrushTransitionMs);   // live re-theme overrides the element's NaN/own duration
                if (!isMount && !float.IsNaN(fillMs) && fillMs > 0f
                    && ((fillOwned && paint.Fill != b.Fill.Value) || paint.BorderColor != b.BorderColor))
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
                    if (paint.BorderColor != b.BorderColor)
                    {
                        ba.BorderFrom = midFlight && (prev.Channels & BrushAnim.BorderBit) != 0
                            ? ColorF.LerpLinear(prev.BorderFrom, paint.BorderColor, prev.T)
                            : paint.BorderColor;
                        ba.Channels |= BrushAnim.BorderBit;
                    }
                    _scene.SetBrushAnim(node, ba);
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }

                // Guarded like Opacity/Width/Height/Text: a bound fill is owned by its effect — the static must never
                // clobber it on an update between signal fires (mount was safe only because the bind fires after this).
                if (fillOwned) paint.Fill = b.Fill.Value;
                paint.HoverFill = b.HoverFill;
                paint.PressedFill = b.PressedFill;
                paint.BorderColor = b.BorderColor;
                paint.HoverBorderColor = b.HoverBorderColor;
                paint.PressedBorderColor = b.PressedBorderColor;
                // Static (unbound) Validation: a bound channel owns paint.ValidationBorder via its effect, so only the
                // static form asserts here (guarded like Fill above) — the common unbound case resets it to none.
                if (!b.Validation.IsBound) paint.ValidationBorder = b.Validation.Value == ValidationState.Error ? Tok.SystemFillCritical : default;
                paint.BorderWidth = b.BorderWidth;
                paint.BorderDashOn = b.BorderDashOn;
                paint.BorderDashOff = b.BorderDashOff;
                paint.TabFlareRadius = b.TabFlareRadius <= 0f ? 4f : b.TabFlareRadius;
                paint.Corners = b.Corners;

                if (b.Shadow is { } sh) _scene.SetShadow(node, sh); else _scene.ClearShadow(node);
                if (b.Arc is { } arcSpec) _scene.SetArc(node, arcSpec); else _scene.ClearArc(node);
                if (b.Gradient is { } gr) _scene.SetGradient(node, gr); else _scene.ClearGradient(node);
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
                if (b.ZStack) _scene.Mark(node, NodeFlags.ZStack); else _scene.Unmark(node, NodeFlags.ZStack);
                if (b.ClipToBounds) _scene.Mark(node, NodeFlags.ClipsToBounds); else _scene.Unmark(node, NodeFlags.ClipsToBounds);
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
                if (b.HitTestVisible) _scene.Mark(node, NodeFlags.HitTestVisible); else _scene.Unmark(node, NodeFlags.HitTestVisible);
                // Disabled gate (set unconditionally each reconcile — toggling IsEnabled must both set AND clear the bit).
                if (b.IsEnabled) _scene.Unmark(node, NodeFlags.Disabled); else _scene.Mark(node, NodeFlags.Disabled);

                ref InteractionInfo ii = ref _scene.Interaction(node);
                ii.Role = b.Role;
                if (b.OnClick is not null)
                {
                    ii.HandlerMask |= InteractionInfo.ClickBit;
                    _scene.SetClickHandler(node, b.OnClick);
                    _scene.Mark(node, NodeFlags.WantsPointer);
                }
                else
                {
                    ii.HandlerMask &= unchecked((ushort)~InteractionInfo.ClickBit);
                    _scene.SetClickHandler(node, null);
                }

                if (b.OnKeyDown is not null) { ii.HandlerMask |= InteractionInfo.KeyBit; _scene.SetKeyHandler(node, b.OnKeyDown); }
                else { ii.HandlerMask &= unchecked((ushort)~InteractionInfo.KeyBit); _scene.SetKeyHandler(node, null); }

                if (b.OnCharInput is not null) { ii.HandlerMask |= InteractionInfo.CharBit; _scene.SetCharHandler(node, b.OnCharInput); }
                else { ii.HandlerMask &= unchecked((ushort)~InteractionInfo.CharBit); _scene.SetCharHandler(node, null); }

                if (b.Repeats) ii.HandlerMask |= InteractionInfo.RepeatBit;
                else ii.HandlerMask &= unchecked((ushort)~InteractionInfo.RepeatBit);
                ii.RepeatDelayMs = b.RepeatDelayMs;       // NaN = WinUI DP defaults (500/33) — the ticker resolves
                ii.RepeatIntervalMs = b.RepeatIntervalMs;

                // WinUI KeyPress::Button bAcceptsReturn=false (CheckBox/RadioButton/ToggleSwitch): Space-only activation.
                if (!b.ActivateOnEnter) ii.HandlerMask |= InteractionInfo.NoEnterActivateBit;
                else ii.HandlerMask &= unchecked((ushort)~InteractionInfo.NoEnterActivateBit);
                // WinUI AllowFocusOnInteraction=False: a press never moves focus to (or past) this node.
                if (!b.AllowFocusOnInteraction) ii.HandlerMask |= InteractionInfo.NoPointerFocusBit;
                else ii.HandlerMask &= unchecked((ushort)~InteractionInfo.NoPointerFocusBit);

                if (b.OnPointerWheel is not null)
                {
                    ii.HandlerMask |= InteractionInfo.WheelBit;
                    _scene.SetPointerWheel(node, b.OnPointerWheel);
                }
                else
                {
                    ii.HandlerMask &= unchecked((ushort)~InteractionInfo.WheelBit);
                    _scene.SetPointerWheel(node, null);
                }

                if (b.OnPointerDown is not null || b.OnDrag is not null || b.OnHoverMove is not null || b.OnPointerExit is not null)
                {
                    ii.HandlerMask |= InteractionInfo.PointerBit;   // hit-testable so it receives press/drag AND bare-hover/exit
                    _scene.SetPointerDown(node, b.OnPointerDown);
                    _scene.SetDrag(node, b.OnDrag);
                    _scene.SetHoverMove(node, b.OnHoverMove);
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
                    ii.HandlerMask &= unchecked((ushort)~InteractionInfo.PointerBit);
                    _scene.SetPointerDown(node, null);
                    _scene.SetDrag(node, null);
                    _scene.SetHoverMove(node, null);
                    _scene.SetPointerExit(node, null);
                    _scene.Unmark(node, NodeFlags.DragYieldsToPan);
                }

                if (b.OnPointerPressed is not null)
                {
                    ii.HandlerMask |= InteractionInfo.PressedBit;
                    _scene.SetPointerPressed(node, b.OnPointerPressed);
                    _scene.Mark(node, NodeFlags.WantsPointer);
                }
                else
                {
                    ii.HandlerMask &= unchecked((ushort)~InteractionInfo.PressedBit);
                    _scene.SetPointerPressed(node, null);
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
                    ii.HandlerMask &= unchecked((ushort)~InteractionInfo.DragBit);
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
                    ii.HandlerMask &= unchecked((ushort)~InteractionInfo.ContextBit);
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
                    ii.HandlerMask &= unchecked((ushort)~InteractionInfo.FocusBit);
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
                else { ii.Cursor = CursorId.Arrow; ii.HandlerMask &= unchecked((ushort)~InteractionInfo.CursorBit); }

                // WinUI Control.IsTabStop: an explicit TabStop beats the clickable⇒focusable auto-derive (the overlay
                // light-dismiss catcher is clickable but must never enter the tab order — WinUI's dismiss layer is
                // not a tab stop, so Tab from a flyout's invoker reaches the flyout content, not the catcher).
                ii.Focusable = b.TabStop ?? (b.Focusable || b.OnClick is not null);
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
                ss.AutoEdgeFade = s.AutoEdgeFade; ss.AutoEdgeFadeBand = s.AutoEdgeFade ? 24f : 0f;
                ss.AlwaysShowBar = s.AlwaysShowScrollbar;
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
                sc.ItemCount = v.ItemCount;
                sc.Layout = v.Layout;
                sc.Overscan = v.Overscan;
                sc.EdgeCueConfig = ResolveEdgeCues(v.EdgeCues);
                if (v.EdgeFade is { } vef) _scene.SetEdgeFade(node, vef); else _scene.ClearEdgeFade(node);
                sc.AutoEdgeFade = v.AutoEdgeFade; sc.AutoEdgeFadeBand = v.AutoEdgeFade ? 24f : 0f;
                sc.SuppressBar = v.SuppressScrollBar;
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
                        PinImageNode(node, newId);
                    }
                }

                ref LayoutInput li = ref _scene.Layout(node);
                li.Width = im.Width; li.Height = im.Height; li.AspectRatio = im.AspectRatio;
                li.Margin = im.Margin; li.AlignSelf = im.AlignSelf;
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
                }

                // Guarded like Text below: a bound color is owned by its effect (EditableText's doc-vs-placeholder
                // ColorBind was clobbered back to the static by any re-render between signal fires).
                if (colorOwned) paint.TextColor = t.Color.Value;
                paint.TextHoverColor = t.HoverColor;
                paint.TextPressedColor = t.PressedColor;
                paint.TextDisabledColor = t.DisabledColor;
                paint.TextFocusedColor = t.FocusedColor;
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
                else ii.HandlerMask &= unchecked((ushort)~InteractionInfo.SpanLinksBit);

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
            ii.HandlerMask &= unchecked((ushort)~(InteractionInfo.SelectableTextBit | InteractionInfo.CursorBit));
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
