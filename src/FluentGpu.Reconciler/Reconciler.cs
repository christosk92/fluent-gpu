using FluentGpu.Dsl;
using FluentGpu.Foundation;
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
            if (!_scene.Root.IsNull) _scene.FreeSubtree(_scene.Root);
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
        WriteColumns(node, newEl, isMount: false);
        ReconcileChildren(node, (newEl as BoxEl)?.Children ?? [], (oldEl as BoxEl)?.Children ?? []);
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
            if (!used[j]) _scene.FreeSubtree(oldNodes[j]);   // removed

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
