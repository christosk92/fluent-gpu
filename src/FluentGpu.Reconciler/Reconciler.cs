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

        var newKids = (newEl as BoxEl)?.Children ?? [];
        var oldKids = (oldEl as BoxEl)?.Children ?? [];

        bool structuralSame = newKids.Length == oldKids.Length;
        for (int i = 0; structuralSame && i < newKids.Length; i++)
            if (newKids[i].ElementTypeId != oldKids[i].ElementTypeId) structuralSame = false;

        if (structuralSame)
        {
            int i = 0;
            for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c), i++)
                Update(c, newKids[i], oldKids[i]);
        }
        else
        {
            // rebuild this level (correct; the slice never hits it)
            var toFree = new List<NodeHandle>();
            for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c)) toFree.Add(c);
            foreach (var c in toFree) _scene.FreeSubtree(c);
            foreach (var childEl in newKids)
            {
                var child = _scene.CreateNode(childEl.ElementTypeId);
                _scene.AppendChild(node, child);
                Mount(child, childEl);
            }
        }
    }

    private void WriteColumns(NodeHandle node, Element el, bool isMount)
    {
        switch (el)
        {
            case BoxEl b:
            {
                ref NodePaint paint = ref _scene.Paint(node);
                bool hasSurface = b.Fill.A > 0f || b.OnClick is not null;
                paint.VisualKind = hasSurface ? VisualKind.Box : VisualKind.None;
                paint.Fill = b.Fill;
                paint.Corners = b.Corners;

                ref LayoutInput li = ref _scene.Layout(node);
                li.Direction = b.Direction;
                li.Gap = b.Gap;
                li.Padding = b.Padding;
                li.Width = float.NaN;
                li.Height = float.NaN;

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
