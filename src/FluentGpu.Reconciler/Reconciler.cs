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
    private sealed class CompEntry { public Component Comp = null!; public Element? Rendered; public Type Type = null!; public Effect? Effect; }
    private readonly Dictionary<NodeHandle, CompEntry> _comps = new();
    private readonly Dictionary<Component, NodeHandle> _anchorOf = new();
    private readonly List<Component> _live = new();

    // The previously-realized window per virtual-list viewport (the keyed-diff's oldKids). Rented from ArrayPool.
    // Bound (RowBind) viewports keep persistent SLOTS instead: one (index signal, mounted element) per visible row.
    private sealed class VirtualEntry
    {
        public Element[]? Prev; public int PrevLen; public int PrevFirst; public VirtualListEl? El;
        public List<(Signal<int> Index, Element El)>? Slots;
    }
    private readonly Dictionary<NodeHandle, VirtualEntry> _virtuals = new();

    // Context provider value signals, keyed by provider node index (a consumer resolves by walking ancestors).
    private readonly Dictionary<int, (object Channel, Signal<object?> Sig)> _providerSig = new();
    // Host-published ambient contexts (Viewport.Size, FrameDiagnostics.Current), keyed by channel.
    private readonly Dictionary<object, Signal<object?>> _ambient = new();

    // Per-node reactive bindings + control-flow effects, disposed when the node is unmounted.
    private readonly Dictionary<int, List<Computation>> _nodeBindings = new();
    private readonly Dictionary<int, Element?> _showState = new();             // last-mounted branch per ShowEl node
    private readonly Dictionary<int, (Element[] Prev, int Len)> _forState = new();   // last realized children per ForEl node
    private readonly List<NodeHandle> _dirtyVirtualScratch = new();

    private Component? _root;
    private Element? _oldRoot;
    private Effect? _rootEffect;
    private bool _reconciled;   // set when any structural/column change happened → the host runs (scoped) layout
    private int _renderCount;   // component render-effects that ran since the last frame (granularity metric)

    /// <summary>True (and reset) if any mount/update/remove happened since the last call — the host's "layout needed" gate.</summary>
    public bool ConsumeReconciled() { var r = _reconciled; _reconciled = false; return r; }

    /// <summary>Number of component render-effects that ran since the last call (proves granular re-render in tests).</summary>
    public int ConsumeRenderCount() { var c = _renderCount; _renderCount = 0; return c; }

    /// <summary>The reactive scheduler — one per host; signals schedule render-effects/bindings here, the host flushes it.</summary>
    public ReactiveRuntime Runtime { get; }

    /// <summary>Live nested components — the host drains their effects each frame.</summary>
    public List<Component> LiveComponents => _live;
    /// <summary>Set by the host; injected into each component so animation hooks can seed tracks on their node.</summary>
    public AnimEngine? Anim { get; set; }
    /// <summary>Set by the host; image nodes request decodes through it and pin/unpin for residency (liveness).</summary>
    public ImageCache? Images { get; set; }
    /// <summary>Set by the host; bumped on any image status change so <c>UseImage</c> consumers re-render granularly.</summary>
    public IReadSignal<int>? ImageEpoch { get; set; }

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

        WriteColumns(node, newEl, isMount: false);
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
        var entry = new CompEntry { Comp = comp, Type = ce.ComponentType };
        _comps[node] = entry;
        _anchorOf[comp] = node;
        _live.Add(comp);

        var effect = new Effect(Runtime, () => RunComponent(node, entry), owner: null, runNow: false);
        entry.Effect = effect;
        comp.Context.RequestRerender = effect.Schedule;   // imperative re-render (granular) for escape-hatch callers
        effect.RunNow();                                  // first render + child mount
    }

    private void RunComponent(NodeHandle node, CompEntry entry)
    {
        if (!_scene.IsLive(node)) return;
        _renderCount++;
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

    private void AddBinding(NodeHandle node, Computation c)
    {
        int idx = (int)node.Raw.Index;
        if (!_nodeBindings.TryGetValue(idx, out var list)) { list = new List<Computation>(2); _nodeBindings[idx] = list; }
        list.Add(c);
    }

    private void BindNode(NodeHandle node, Element el)
    {
        if (el is BoxEl b)
        {
            if (b.TransformBind is { } tb)
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).LocalTransform = tb(); _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty); } }, owner: null, runNow: true));
            if (b.OpacityBind is { } ob)
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).Opacity = ob(); _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
            if (b.FillBind is { } fb)
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).Fill = fb(); _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
            if (b.WidthBind is { } wb)
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Layout(node).Width = wb(); _scene.Mark(node, NodeFlags.LayoutDirty); } }, owner: null, runNow: true));
            if (b.HeightBind is { } hb)
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Layout(node).Height = hb(); _scene.Mark(node, NodeFlags.LayoutDirty); } }, owner: null, runNow: true));
            b.OnRealized?.Invoke(node);
        }
        else if (el is TextEl t)
        {
            if (t.TextBind is { } txb)
                AddBinding(node, new Effect(Runtime, () =>
                {
                    if (!_scene.IsLive(node)) return;
                    var next = _strings.Intern(txb());
                    ref var paint = ref _scene.Paint(node);
                    if (paint.Text == next) return;
                    SetPaintText(ref paint, next);
                    _scene.Mark(node, NodeFlags.LayoutDirty);
                }, owner: null, runNow: true));
            if (t.ColorBind is { } cb)
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).TextColor = cb(); _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
        }
        else if (el is ImageEl ime)
        {
            if (ime.SourceBind is { } sbind)
                AddBinding(node, new Effect(Runtime, () =>
                {
                    if (!_scene.IsLive(node)) return;
                    string src = sbind();
                    int newId = Images is not null && src.Length > 0
                        ? Images.Request(src, (int)ime.Width, (int)ime.Height, ImagePriority.Visible, ime.BlurHash, ime.Transition).Id : 0;
                    ref var paint = ref _scene.Paint(node);
                    if (newId == paint.ImageId) return;
                    if (Images is not null)
                    {
                        if (paint.ImageId != 0) Images.Unpin(new ImageHandle(paint.ImageId));
                        if (newId != 0) Images.Pin(new ImageHandle(newId));
                    }
                    paint.ImageId = newId;
                    _scene.Mark(node, NodeFlags.PaintDirty);
                }, owner: null, runNow: true));
            if (ime.PlaceholderBind is { } pbind)
                AddBinding(node, new Effect(Runtime, () => { if (_scene.IsLive(node)) { _scene.Paint(node).Fill = pbind(); _scene.Mark(node, NodeFlags.PaintDirty); } }, owner: null, runNow: true));
        }
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

        while (slots.Count < w)   // grow: the template runs ONCE per slot; its element tree is never rebuilt
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

        for (int i = 0; i < w; i++)
        {
            var sig = slots[i].Index;
            int idx = first + i;
            int prevIdx = sig.Peek();
            if (prevIdx != idx)
            {
                sig.Value = idx;   // granular rebind — only this slot's bind effects re-run
                ve.OnItemIndexChanged?.Invoke(prevIdx, idx);   // E11: a persistent bound slot moved indices
            }
        }

        bool moved = structural || first != entry.PrevFirst || w != entry.PrevLen;
        entry.PrevFirst = first;
        entry.PrevLen = w;
        if (moved) _scene.Mark(content, NodeFlags.LayoutDirty);   // children are positioned by FirstRealized + order
        if (structural) _reconciled = true;

        ref ScrollState scw = ref _scene.ScrollRef(node);
        scw.FirstRealized = first; scw.LastRealized = last;
        _scene.Unmark(node, NodeFlags.VirtualRangeDirty);

        FireWindowLifecycle(ve, oldFirst, oldLast, first, last);
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
                return t.TextBind is null && t.ColorBind is null;
            case SpanTextEl:
                return true;   // plain leaf — WriteColumns rewrites every column incl. the span run/handlers
            case ImageEl im:
                return im.SourceBind is null && im.PlaceholderBind is null;
            case PolylineStrokeEl:
                return true;
            case BoxEl b:
                if (b.TransformBind is not null || b.OpacityBind is not null || b.FillBind is not null
                    || b.WidthBind is not null || b.HeightBind is not null || b.OnRealized is not null) return false;
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
            UnmountSubtree(node);
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
        if (Images is not null)
        {
            ref NodePaint paint = ref _scene.Paint(node);
            if (paint.VisualKind == VisualKind.Image && paint.ImageId != 0) Images.Unpin(new ImageHandle(paint.ImageId));
        }
        if (_nodeBindings.Remove(idx, out var binds)) for (int i = 0; i < binds.Count; i++) binds[i].Dispose();
        _providerSig.Remove(idx);
        _showState.Remove(idx);
        _forState.Remove(idx);
        if (_comps.Remove(node, out var e)) { e.Effect?.Dispose(); e.Comp.Unmount(); _live.Remove(e.Comp); _anchorOf.Remove(e.Comp); }
        if (_virtuals.Remove(node, out var v) && v.Prev is not null)
        {
            Array.Clear(v.Prev, 0, v.PrevLen);
            ArrayPool<Element>.Shared.Return(v.Prev);
        }
    }

    // ── Column writes (POD → scene) ─────────────────────────────────────────────────────────────

    private void WriteColumns(NodeHandle node, Element el, bool isMount)
    {
        switch (el)
        {
            case BoxEl b:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                bool hasSurface = b.Fill.A > 0f || b.HoverFill.A > 0f || b.PressedFill.A > 0f
                                  || b.BorderWidth > 0f || b.OnClick is not null || b.Gradient is not null || b.BorderBrush is not null
                                  || b.FillBind is not null;
                paint.VisualKind = hasSurface ? VisualKind.Box : VisualKind.None;

                // Implicit BrushTransition (WinUI, 83ms): a LIVE node re-rendered with a different fill/border cross-fades
                // from the previously-DISPLAYED color (mid-flight retargets stay continuous) instead of snapping.
                // A BOUND fill is excluded per-channel: its effect owns paint.Fill, so diffing against the static would
                // arm a phantom fade toward a color the channel doesn't own (the border sub-block is unaffected).
                bool fillOwned = b.FillBind is null;
                if (!isMount && !float.IsNaN(b.BrushTransitionMs) && b.BrushTransitionMs > 0f
                    && ((fillOwned && paint.Fill != b.Fill) || paint.BorderColor != b.BorderColor))
                {
                    bool midFlight = _scene.TryGetBrushAnim(node, out var prev);
                    var ba = new BrushAnim { DurationMs = b.BrushTransitionMs };
                    if (fillOwned && paint.Fill != b.Fill)
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
                if (fillOwned) paint.Fill = b.Fill;
                paint.HoverFill = b.HoverFill;
                paint.PressedFill = b.PressedFill;
                paint.BorderColor = b.BorderColor;
                paint.HoverBorderColor = b.HoverBorderColor;
                paint.PressedBorderColor = b.PressedBorderColor;
                paint.BorderWidth = b.BorderWidth;
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

                // Transform origin (used by static + animated scale/rotate; default centre). Set unconditionally so an
                // AnimEngine ScaleX/Y track or a TransformBind pivots about the requested origin (e.g. a menu's top edge).
                paint.OriginX = b.TransformOriginX;
                paint.OriginY = b.TransformOriginY;

                // Static transform/opacity ONLY when the element declares one AND there's no transform binding/animation
                // owning the channel (else a re-render would reset the bound/animated value to identity each frame).
                if (b.TransformBind is null && (b.OffsetX != 0f || b.OffsetY != 0f || b.ScaleX != 1f || b.ScaleY != 1f || b.Rotation != 0f))
                {
                    var tf = Affine2D.Translation(b.OffsetX, b.OffsetY);
                    if (b.Rotation != 0f) tf = tf.Multiply(Affine2D.Rotation(b.Rotation * (MathF.PI / 180f)));
                    if (b.ScaleX != 1f || b.ScaleY != 1f) tf = tf.Multiply(Affine2D.Scale(b.ScaleX, b.ScaleY));
                    paint.LocalTransform = tf;
                }
                // Re-assert unconditionally (like Width/Fill): gating on != 1f made an Opacity 0→1 update a no-op,
                // so a node hidden by a prior render could never be shown again (the ProgressRing IsActive flip).
                if (b.OpacityBind is null) paint.Opacity = b.Opacity;
                paint.HoverOpacity = b.HoverOpacity;
                paint.PressedOpacity = b.PressedOpacity;
                paint.OpacityGroup = b.OpacityGroup;

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
                if (b.WidthBind is null) li.Width = b.Width;
                if (b.HeightBind is null) li.Height = b.Height;
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
                if (b.StickyTop is { } st) _scene.SetSticky(node, st, b.OnPinned); else _scene.ClearSticky(node);
                if (b.Animate is { } at && Anim is { } anim)
                {
                    anim.SetTransition(node, at);
                    _scene.Mark(node, NodeFlags.BoundsAnimated);
                    if (isMount && at.Enter.Active) anim.SeedEnter(node, at.Enter, at);
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
                }
                else
                {
                    ii.HandlerMask &= unchecked((ushort)~InteractionInfo.PointerBit);
                    _scene.SetPointerDown(node, null);
                    _scene.SetDrag(node, null);
                    _scene.SetHoverMove(node, null);
                    _scene.SetPointerExit(node, null);
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

                var tf = Affine2D.Translation(pl.OffsetX, pl.OffsetY);
                if (pl.Rotation != 0f) tf = tf.Multiply(Affine2D.Rotation(pl.Rotation * (MathF.PI / 180f)));
                if (pl.ScaleX != 1f || pl.ScaleY != 1f) tf = tf.Multiply(Affine2D.Scale(pl.ScaleX, pl.ScaleY));
                paint.LocalTransform = tf;

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
                if (im.PlaceholderBind is null) paint.Fill = im.Placeholder;   // bound rows tint via the binding
                paint.Corners = im.Corners;

                int oldId = paint.ImageId;
                if (im.SourceBind is null)   // bound rows request via the binding (the effect owns pin/unpin)
                {
                    int newId = (Images is not null && im.Source.Length > 0)
                        ? Images.Request(im.Source, (int)im.Width, (int)im.Height, ImagePriority.Visible, im.BlurHash, im.Transition).Id : 0;
                    if (newId != oldId)
                    {
                        if (Images is not null)
                        {
                            if (oldId != 0) Images.Unpin(new ImageHandle(oldId));
                            if (newId != 0) Images.Pin(new ImageHandle(newId));
                        }
                        paint.ImageId = newId;
                    }
                }

                ref LayoutInput li = ref _scene.Layout(node);
                li.Width = im.Width; li.Height = im.Height;
                li.Margin = im.Margin; li.AlignSelf = im.AlignSelf;
                break;
            }
            case TextEl t:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.Text;

                // Implicit BrushTransition on the resting foreground (WinUI BrushTransition on a logical state flip).
                // Skipped when ColorBind owns the channel (same per-channel rule as the BoxEl fill block above).
                bool colorOwned = t.ColorBind is null;
                if (!isMount && colorOwned && !float.IsNaN(t.BrushTransitionMs) && t.BrushTransitionMs > 0f && paint.TextColor != t.Color)
                {
                    bool midFlight = _scene.TryGetBrushAnim(node, out var prev);
                    var ba = new BrushAnim
                    {
                        DurationMs = t.BrushTransitionMs,
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
                if (colorOwned) paint.TextColor = t.Color;
                paint.TextHoverColor = t.HoverColor;
                paint.TextPressedColor = t.PressedColor;
                paint.TextDisabledColor = t.DisabledColor;
                paint.TextFocusedColor = t.FocusedColor;
                paint.TextDecorations = (byte)((t.Underline ? NodePaint.UnderlineBit : 0)
                                             | (t.Strikethrough ? NodePaint.StrikethroughBit : 0));
                _scene.SetDynamicText(node, t.DynamicText);
                if (t.TextBind is null)
                {
                    var newText = _strings.Intern(t.Text);
                    if (paint.Text != newText) { SetPaintText(ref paint, newText); _scene.Mark(node, NodeFlags.LayoutDirty); }
                }

                ref LayoutInput li = ref _scene.Layout(node);
                var famId = _strings.Intern(t.FontFamily);
                if (li.TextStyle.FontFamily != famId) { _strings.AddRef(famId); _strings.Release(li.TextStyle.FontFamily); }
                li.TextStyle = new TextStyle(famId, t.Size, t.ResolvedWeight, t.Wrap, t.Trim, t.MaxLines,
                    t.CharSpacing, t.LineHeight, t.LineStacking, t.LineBounds);
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
