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

    /// <summary>Live nested components — the host flushes their state and drains their effects each frame.</summary>
    public List<Component> LiveComponents => _live;
    /// <summary>Set by the host; a nested component's setState calls this to request the next frame.</summary>
    public Action RequestRerender { get; set; } = static () => { };

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

        WriteColumns(node, el, isMount: true);
        if (el is BoxEl box)
        {
            foreach (var childEl in box.Children)
            {
                var child = _scene.CreateNode(childEl.ElementTypeId);
                _scene.AppendChild(node, child);
                Mount(child, childEl);
            }
        }
    }

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
                entry.Rendered = newRendered;
            }
            else
            {
                ReplaceComponent(node, nce);   // different component type at this position
            }
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
            ContextStack.Pop();
            return;
        }

        WriteColumns(node, newEl, isMount: false);
        ReconcileChildren(node, (newEl as BoxEl)?.Children ?? [], (oldEl as BoxEl)?.Children ?? []);
    }

    private void MountProvider(NodeHandle node, ContextProviderEl cp)
    {
        ContextStack.Push(cp.Channel, cp.Value);
        var child = _scene.CreateNode(cp.Child.ElementTypeId);
        _scene.AppendChild(node, child);
        Mount(child, cp.Child);
        ContextStack.Pop();
    }

    private void MountComponent(NodeHandle node, ComponentEl ce)
    {
        var comp = ce.Factory();
        comp.Context.RequestRerender = RequestRerender;
        var rendered = RenderComponent(comp);
        _comps[node] = new CompEntry { Comp = comp, Rendered = rendered, Type = ce.ComponentType };
        _live.Add(comp);

        var child = _scene.CreateNode(rendered.ElementTypeId);
        _scene.AppendChild(node, child);
        Mount(child, rendered);
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
        if (_comps.Remove(node, out var e)) { e.Comp.Unmount(); _live.Remove(e.Comp); }
    }

    /// <summary>
    /// Keyed child reconcile: match old↔new by Key (else by position+type), update matches in place (state preserved),
    /// mount new, free removed, then reorder the sibling chain to the new order via O(1) detach+append. (LIS move-
    /// minimization is a perf follow-up; correctness + identity preservation are here.)
    /// </summary>
    private void ReconcileChildren(NodeHandle node, Element[] newKids, Element[] oldKids)
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
                bool hasSurface = b.Fill.A > 0f || b.BorderWidth > 0f || b.OnClick is not null;
                paint.VisualKind = hasSurface ? VisualKind.Box : VisualKind.None;
                paint.Fill = b.Fill;
                paint.HoverFill = b.HoverFill;
                paint.PressedFill = b.PressedFill;
                paint.BorderColor = b.BorderColor;
                paint.BorderWidth = b.BorderWidth;
                paint.Corners = b.Corners;

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

                ref InteractionInfo ii = ref _scene.Interaction(node);
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

                // Clickable or explicitly-focusable nodes participate in focus/Tab navigation.
                ii.Focusable = b.Focusable || b.OnClick is not null;
                ii.TabIndex = b.TabIndex;
                if (ii.Focusable) _scene.Mark(node, NodeFlags.Focusable);
                break;
            }
            case TextEl t:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                paint.VisualKind = VisualKind.Text;
                paint.TextColor = t.Color;
                var newText = _strings.Intern(t.Text);
                if (paint.Text != newText) { paint.Text = newText; _scene.Mark(node, NodeFlags.LayoutDirty); }

                ref LayoutInput li = ref _scene.Layout(node);
                li.TextStyle = new TextStyle(StringId.Empty, t.Size, t.Bold);
                break;
            }
        }

        if (!isMount) _scene.Mark(node, NodeFlags.PaintDirty);
    }
}
