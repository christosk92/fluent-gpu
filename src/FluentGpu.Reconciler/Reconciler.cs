using System.Buffers;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Text;

namespace FluentGpu.Reconciler;

/// <summary>
/// Patches the retained SceneStore from an immutable Element tree. Slice algorithm: positional + type-keyed diff
/// (same type at a position ⇒ update-in-place + recurse; any structural change at a level ⇒ rebuild that level).
/// The full engine is the re-authored keyed-LIS over arena scratch. State-loss-on-type-change is intentional.
/// </summary>
public sealed class TreeReconciler
{
    private readonly SceneStore _scene;
    private readonly StringTable _strings;

    // Mounted child components, keyed by their host node (the ComponentEl anchor).
    private sealed class CompEntry { public Component Comp = null!; public Element Rendered = null!; public Type Type = null!; }
    private readonly Dictionary<NodeHandle, CompEntry> _comps = new();
    private readonly List<Component> _live = new();

    // The previously-realized window per virtual-list viewport (the keyed-diff's oldKids). Rented from ArrayPool.
    private sealed class VirtualEntry { public Element[]? Prev; public int PrevLen; }
    private readonly Dictionary<NodeHandle, VirtualEntry> _virtuals = new();

    /// <summary>Live nested components — the host flushes their state and drains their effects each frame.</summary>
    public List<Component> LiveComponents => _live;
    /// <summary>Set by the host; a nested component's setState calls this to request the next frame.</summary>
    public Action RequestRerender { get; set; } = static () => { };
    /// <summary>Set by the host; injected into each component so animation hooks can seed tracks on their node.</summary>
    public AnimEngine? Anim { get; set; }

    /// <summary>Set by the host; image nodes request decodes through it and pin/unpin for residency (liveness).</summary>
    public ImageCache? Images { get; set; }

    public TreeReconciler(SceneStore scene, StringTable strings)
    {
        _scene = scene;
        _strings = strings;
    }

    /// <summary>Reconcile the whole tree from a freshly-rendered root Element against the previous one.</summary>
    public void ReconcileRoot(Element newRoot, Element? oldRoot)
    {
        if (_scene.Root.IsNull || oldRoot is null || oldRoot.ElementTypeId != newRoot.ElementTypeId)
        {
            if (!_scene.Root.IsNull) Remove(_scene.Root);
            var node = _scene.CreateNode(newRoot.ElementTypeId);
            _scene.Root = node;
            Mount(node, newRoot);
        }
        else
        {
            Update(_scene.Root, newRoot, oldRoot);
        }
    }

    private void Mount(NodeHandle node, Element el)
    {
        if (el is ComponentEl ce) { MountComponent(node, ce); return; }
        if (el is ContextProviderEl cp) { MountProvider(node, cp); return; }
        if (el is ScrollEl se) { MountScroll(node, se); return; }
        if (el is VirtualListEl ve) { MountVirtual(node, ve); return; }

        WriteColumns(node, el, isMount: true);
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

    private void Update(NodeHandle node, Element newEl, Element oldEl)
    {
        if (newEl is ComponentEl nce)
        {
            if (oldEl is ComponentEl oce && oce.ComponentType == nce.ComponentType && _comps.TryGetValue(node, out var entry))
            {
                // Reuse the instance (state preserved): re-render and reconcile its single output child.
                var newRendered = RenderComponent(entry.Comp);
                var childNode = _scene.FirstChild(node);
                if (childNode.IsNull)
                {
                    childNode = _scene.CreateNode(newRendered.ElementTypeId);
                    _scene.AppendChild(node, childNode);
                    Mount(childNode, newRendered);
                }
                else if (entry.Rendered.ElementTypeId == newRendered.ElementTypeId)
                {
                    Update(childNode, newRendered, entry.Rendered);
                }
                else
                {
                    Remove(childNode);
                    var nc = _scene.CreateNode(newRendered.ElementTypeId);
                    _scene.AppendChild(node, nc);
                    Mount(nc, newRendered);
                }
                MirrorParticipation(node, _scene.FirstChild(node));
                entry.Rendered = newRendered;
            }
            else
            {
                ReplaceComponent(node, nce);   // different component type at this position
            }
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
            _scene.ScrollRef(node).ContentNode = content;   // preserves OffsetX/Y (scroll position survives re-render)
            return;
        }

        if (newEl is VirtualListEl nve)
        {
            WriteColumns(node, nve, isMount: false);
            RealizeWindow(node, nve);   // re-realize the window against the committed scroll offset
            return;
        }

        if (newEl is ContextProviderEl np)
        {
            ContextStack.Push(np.Channel, np.Value);
            var oldChild = (oldEl as ContextProviderEl)?.Child;
            var childNode = _scene.FirstChild(node);
            if (childNode.IsNull)
            {
                var nc = _scene.CreateNode(np.Child.ElementTypeId);
                _scene.AppendChild(node, nc);
                Mount(nc, np.Child);
            }
            else if (oldChild is not null && oldChild.ElementTypeId == np.Child.ElementTypeId)
            {
                Update(childNode, np.Child, oldChild);
            }
            else
            {
                Remove(childNode);
                var nc = _scene.CreateNode(np.Child.ElementTypeId);
                _scene.AppendChild(node, nc);
                Mount(nc, np.Child);
            }
            MirrorParticipation(node, _scene.FirstChild(node));
            ContextStack.Pop();
            return;
        }

        WriteColumns(node, newEl, isMount: false);
        ReconcileChildren(node, ChildrenOf(newEl), ChildrenOf(oldEl));
    }

    private void MountScroll(NodeHandle node, ScrollEl se)
    {
        WriteColumns(node, se, isMount: true);          // viewport columns + ClipsToBounds + ScrollState orientation
        var content = _scene.CreateNode(se.Content.ElementTypeId);
        _scene.AppendChild(node, content);
        Mount(content, se.Content);
        _scene.ScrollRef(node).ContentNode = content;   // the child that carries the -ScrollOffset LocalTransform
    }

    private void MountVirtual(NodeHandle node, VirtualListEl ve)
    {
        WriteColumns(node, ve, isMount: true);          // viewport columns + ClipsToBounds + ScrollState item params
        var content = _scene.CreateNode(1);             // synthetic content container (holds the realized rows)
        _scene.AppendChild(node, content);
        _scene.ScrollRef(node).ContentNode = content;
        _virtuals[node] = new VirtualEntry();
        RealizeWindow(node, ve);
    }

    /// <summary>
    /// Realize the visible window [first,last)+overscan as keyed children of the content node, handing it to the
    /// existing keyed-LIS diff (so recycling IS CreateNode/FreeNode over the slab free-list). Uniform fast path:
    /// the window is O(1) arithmetic over the committed scroll offset (virtualization.md §3.1/§6.1).
    /// </summary>
    private void RealizeWindow(NodeHandle node, VirtualListEl ve)
    {
        if (!_virtuals.TryGetValue(node, out var entry)) { entry = new VirtualEntry(); _virtuals[node] = entry; }
        _scene.TryGetScroll(node, out var sc);
        var content = sc.ContentNode;
        if (content.IsNull) return;

        bool horizontal = ve.Horizontal;
        float offset = horizontal ? sc.OffsetX : sc.OffsetY;
        // Viewport extent: last frame's published size if known, else the explicit hint, else a generous default
        // (over-realizing on frame 1 is harmless — extra rows are clip-culled — and self-corrects next layout).
        float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        if (viewport <= 0f) viewport = horizontal ? Hint(ve.Width) : Hint(ve.Height);

        int count = ve.ItemCount;
        int first, last;
        if (ve.Layout is not null)   // pluggable fixed-geometry layout (stack/grid/custom) — pure arithmetic
        {
            float cross = horizontal ? (sc.ViewportH > 0f ? sc.ViewportH : Hint(ve.Height))
                                     : (sc.ViewportW > 0f ? sc.ViewportW : Hint(ve.Width));
            ve.Layout.Window(count, cross, viewport, offset, ve.Overscan, out first, out last);
        }
        else                         // variable path — Fenwick offset↔index (O(log n)), estimate-then-correct
        {
            var table = _scene.ExtentTableFor(node, count, ve.EstimatedExtent);
            first = Math.Max(0, table.IndexAt(offset) - ve.Overscan);
            last = Math.Min(count, table.IndexAt(offset + viewport) + 1 + ve.Overscan);
        }
        if (last < first) last = first;
        int w = last - first;

        var cur = ArrayPool<Element>.Shared.Rent(Math.Max(1, w));
        for (int i = 0; i < w; i++)
        {
            int idx = first + i;
            var el = ve.RenderItem(idx);
            string key = ve.KeyOf?.Invoke(idx) ?? ("#" + idx);     // positional key when no stable identity supplied
            cur[i] = el with { Key = key };
        }

        ReconcileChildren(content, cur.AsSpan(0, w), entry.Prev is null ? default : entry.Prev.AsSpan(0, entry.PrevLen));

        if (entry.Prev is not null) { Array.Clear(entry.Prev, 0, entry.PrevLen); ArrayPool<Element>.Shared.Return(entry.Prev); }
        entry.Prev = cur; entry.PrevLen = w;

        ref ScrollState scw = ref _scene.ScrollRef(node);
        scw.FirstRealized = first; scw.LastRealized = last;
        _scene.Unmark(node, NodeFlags.VirtualRangeDirty);
    }

    private static float Hint(float explicitSize) => float.IsNaN(explicitSize) ? 1024f : explicitSize;

    private void MountProvider(NodeHandle node, ContextProviderEl cp)
    {
        ContextStack.Push(cp.Channel, cp.Value);
        var child = _scene.CreateNode(cp.Child.ElementTypeId);
        _scene.AppendChild(node, child);
        Mount(child, cp.Child);
        MirrorParticipation(node, child);   // a provider is layout-transparent too
        ContextStack.Pop();
    }

    private void MountComponent(NodeHandle node, ComponentEl ce)
    {
        var comp = ce.Factory();
        comp.Context.RequestRerender = RequestRerender;
        comp.Context.Anim = Anim;
        comp.Context.Images = Images;   // UseImage / PrefetchImage in nested components
        var rendered = RenderComponent(comp);
        _comps[node] = new CompEntry { Comp = comp, Rendered = rendered, Type = ce.ComponentType };
        _live.Add(comp);

        var child = _scene.CreateNode(rendered.ElementTypeId);
        comp.Context.HostNode = child;   // animation hooks (queued during render) read this when they run in phase 6.5
        _scene.AppendChild(node, child);
        Mount(child, rendered);
        MirrorParticipation(node, child);
    }

    /// <summary>
    /// A component anchor is layout-transparent: it must participate in its parent's flex/grid exactly as its rendered
    /// child would (so a child with <c>Grow=1</c> makes the anchor grow, and the anchor's own arrange re-grows the child
    /// to fill). We mirror the child's sizing/participation onto the anchor each (re)render. (layout.md §2.2 passthrough.)
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
        if (_comps.Remove(node, out var old)) { old.Comp.Unmount(); _live.Remove(old.Comp); }
        var kids = new List<NodeHandle>();
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c)) kids.Add(c);
        foreach (var k in kids) Remove(k);
        MountComponent(node, ce);
    }

    private Element RenderComponent(Component comp)
    {
        try { return comp.RenderWithHooks(); }
        catch (Exception ex) { Diag.Event("reconciler", "component render threw: " + ex.Message); return new BoxEl(); }
    }

    /// <summary>Remove a subtree: run component effect-cleanups within it, then free the nodes.</summary>
    private void Remove(NodeHandle node)
    {
        UnmountSubtree(node);
        _scene.FreeSubtree(node);
    }

    private void UnmountSubtree(NodeHandle node)
    {
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c)) UnmountSubtree(c);
        if (Images is not null)   // release image residency so scrolled-away album art becomes evictable
        {
            ref NodePaint paint = ref _scene.Paint(node);
            if (paint.VisualKind == VisualKind.Image && paint.ImageId != 0) Images.Unpin(new ImageHandle(paint.ImageId));
        }
        if (_comps.Remove(node, out var e)) { e.Comp.Unmount(); _live.Remove(e.Comp); }
        if (_virtuals.Remove(node, out var v) && v.Prev is not null)
        {
            Array.Clear(v.Prev, 0, v.PrevLen);
            ArrayPool<Element>.Shared.Return(v.Prev);
        }
    }

    /// <summary>
    /// Keyed child reconcile: match old↔new by Key (else by position+type), update matches in place (state preserved),
    /// mount new, free removed, then reorder the sibling chain to the new order via O(1) detach+append. (LIS move-
    /// minimization is a perf follow-up; correctness + identity preservation are here.)
    /// </summary>
    internal void ReconcileChildren(NodeHandle node, ReadOnlySpan<Element> newKids, ReadOnlySpan<Element> oldKids)
    {
        int oldN = oldKids.Length, newN = newKids.Length;
        if (oldN == 0 && newN == 0) return;

        // Snapshot old child handles in order.
        var oldNodes = oldN == 0 ? Array.Empty<NodeHandle>() : new NodeHandle[oldN];
        if (oldN > 0)
        {
            int i = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull && i < oldN; c = _scene.NextSibling(c)) oldNodes[i++] = c;
        }

        // Key → old index map.
        Dictionary<string, int>? keyMap = null;
        for (int j = 0; j < oldN; j++)
            if (oldKids[j].Key is string k) (keyMap ??= new()).TryAdd(k, j);

        var used = oldN == 0 ? Array.Empty<bool>() : new bool[oldN];
        var newNodes = newN == 0 ? Array.Empty<NodeHandle>() : new NodeHandle[newN];

        for (int i = 0; i < newN; i++)
        {
            Element nk = newKids[i];
            int match = -1;
            if (nk.Key is string key && keyMap is not null && keyMap.TryGetValue(key, out int j)
                && !used[j] && oldKids[j].ElementTypeId == nk.ElementTypeId)
                match = j;
            else if (nk.Key is null && i < oldN && !used[i] && oldKids[i].Key is null
                && oldKids[i].ElementTypeId == nk.ElementTypeId)
                match = i;

            if (match >= 0)
            {
                used[match] = true;
                newNodes[i] = oldNodes[match];
                Update(oldNodes[match], nk, oldKids[match]);   // reuse → state preserved
            }
            else
            {
                var child = _scene.CreateNode(nk.ElementTypeId);
                Mount(child, nk);
                newNodes[i] = child;
            }
        }

        for (int j = 0; j < oldN; j++)
            if (!used[j]) Remove(oldNodes[j]);   // removed (runs nested component cleanups first)

        for (int i = 0; i < newN; i++)                       // reorder to new order
        {
            _scene.Detach(newNodes[i]);
            _scene.AppendChild(node, newNodes[i]);
        }
    }

    private void WriteColumns(NodeHandle node, Element el, bool isMount)
    {
        switch (el)
        {
            case BoxEl b:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                bool hasSurface = b.Fill.A > 0f || b.HoverFill.A > 0f || b.PressedFill.A > 0f
                                  || b.BorderWidth > 0f || b.OnClick is not null || b.Gradient is not null || b.BorderBrush is not null;
                paint.VisualKind = hasSurface ? VisualKind.Box : VisualKind.None;
                paint.Fill = b.Fill;
                paint.HoverFill = b.HoverFill;
                paint.PressedFill = b.PressedFill;
                paint.BorderColor = b.BorderColor;
                paint.BorderWidth = b.BorderWidth;
                paint.Corners = b.Corners;

                // Optional rich paint → sparse side-tables (O(decorated nodes)). Clear on update if the prop went away.
                if (b.Shadow is { } sh) _scene.SetShadow(node, sh); else _scene.ClearShadow(node);
                if (b.Gradient is { } gr) _scene.SetGradient(node, gr); else _scene.ClearGradient(node);
                if (b.BorderBrush is { } bb) _scene.SetBorderBrush(node, bb); else _scene.ClearBorderBrush(node);
                if (b.Acrylic is { } ac) _scene.SetAcrylic(node, ac); else _scene.ClearAcrylic(node);

                // Composited transform (CSS order: scale → rotate → translate) + opacity. Only write when the element
                // declares a STATIC transform/opacity — otherwise leave these for the animation engine to own (else a
                // re-render every frame would reset the animated value to identity and the animation would never show).
                if (b.OffsetX != 0f || b.OffsetY != 0f || b.ScaleX != 1f || b.ScaleY != 1f || b.Rotation != 0f)
                {
                    var tf = Affine2D.Translation(b.OffsetX, b.OffsetY);
                    if (b.Rotation != 0f) tf = tf.Multiply(Affine2D.Rotation(b.Rotation * (MathF.PI / 180f)));
                    if (b.ScaleX != 1f || b.ScaleY != 1f) tf = tf.Multiply(Affine2D.Scale(b.ScaleX, b.ScaleY));
                    paint.LocalTransform = tf;
                }
                if (b.Opacity != 1f) paint.Opacity = b.Opacity;

                // Interaction-driven scale targets → the eased side-table (recorder composites them by HoverT/PressT).
                if (b.HoverScale != 1f || b.PressScale != 1f)
                {
                    ref InteractionAnim ia = ref _scene.InteractRef(node);
                    ia.HoverScale = b.HoverScale;
                    ia.PressScale = b.PressScale;
                }

                ref LayoutInput li = ref _scene.Layout(node);
                li.Direction = b.Direction;
                li.Gap = b.Gap;
                li.Padding = b.Padding;
                li.Margin = b.Margin;
                li.Width = b.Width;
                li.Height = b.Height;
                li.MinW = b.MinWidth; li.MinH = b.MinHeight; li.MaxW = b.MaxWidth; li.MaxH = b.MaxHeight;
                li.FlexGrow = b.Grow;
                li.FlexShrink = b.Shrink;
                li.FlexBasis = b.Basis;
                li.AlignSelf = b.AlignSelf;
                li.Justify = b.Justify;
                li.AlignItems = b.AlignItems;
                li.Wrap = b.Wrap;
                if (b.ZStack) _scene.Mark(node, NodeFlags.ZStack); else _scene.Unmark(node, NodeFlags.ZStack);
                if (b.HitTestVisible) _scene.Mark(node, NodeFlags.HitTestVisible); else _scene.Unmark(node, NodeFlags.HitTestVisible);

                ref InteractionInfo ii = ref _scene.Interaction(node);
                ii.Role = b.Role;
                if (b.OnClick is not null)
                {
                    ii.HandlerMask |= InteractionInfo.ClickBit;
                    ii.Cursor = CursorId.Hand;
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

                if (b.OnPointerDown is not null || b.OnDrag is not null)
                {
                    ii.HandlerMask |= InteractionInfo.PointerBit;
                    _scene.SetPointerDown(node, b.OnPointerDown);
                    _scene.SetDrag(node, b.OnDrag);
                    _scene.Mark(node, NodeFlags.WantsPointer);
                }
                else
                {
                    ii.HandlerMask &= unchecked((ushort)~InteractionInfo.PointerBit);
                    _scene.SetPointerDown(node, null);
                    _scene.SetDrag(node, null);
                }

                // Clickable or explicitly-focusable nodes participate in focus/Tab navigation.
                ii.Focusable = b.Focusable || b.OnClick is not null;
                ii.TabIndex = b.TabIndex;
                if (ii.Focusable) _scene.Mark(node, NodeFlags.Focusable);
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

                _scene.Mark(node, NodeFlags.ClipsToBounds);     // the viewport clips its overflowing content
                _scene.ScrollRef(node).Orientation = s.Horizontal ? (byte)1 : (byte)0;
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
            case ImageEl im:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.Image;
                paint.Fill = im.Placeholder;     // shown until the decode lands
                paint.Corners = im.Corners;

                int oldId = paint.ImageId;
                int newId = (Images is not null && im.Source.Length > 0)
                    ? Images.Request(im.Source, (int)im.Width, (int)im.Height, ImagePriority.Visible, im.BlurHash, im.Transition).Id : 0;
                if (newId != oldId)
                {
                    if (Images is not null)   // residency: pin the new source, release the old (recycle-safe)
                    {
                        if (oldId != 0) Images.Unpin(new ImageHandle(oldId));
                        if (newId != 0) Images.Pin(new ImageHandle(newId));
                    }
                    paint.ImageId = newId;
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
                paint.TextColor = t.Color;
                _scene.SetDynamicText(node, t.DynamicText);
                var newText = _strings.Intern(t.Text);
                if (paint.Text != newText) { paint.Text = newText; _scene.Mark(node, NodeFlags.LayoutDirty); }

                ref LayoutInput li = ref _scene.Layout(node);
                li.TextStyle = new TextStyle(_strings.Intern(t.FontFamily), t.Size, t.Bold, t.Wrap, t.Trim, t.MaxLines);
                break;
            }
        }

        if (!isMount) _scene.Mark(node, NodeFlags.PaintDirty);
    }
}
