using System.Collections.Generic;

namespace FluentGpu.Scroll.Runtime;

/// <summary>Pure scroll-position memory keyed by an app-supplied content identity (<c>ScrollEl.ScrollKey</c> /
/// <c>VirtualListEl.ScrollKey</c>): the host <see cref="Save"/>s a viewport's shown offset when its handle is
/// destroyed and hands it to <see cref="ScrollHandle.Restore"/> on the next mount under the same key (design §9 —
/// replaces the reconciler's <c>ScrollMemory</c>). Bounded by <see cref="Capacity"/> entries: the least recently
/// SAVED key goes first, so a page the user keeps coming back to survives the per-visit keys churning past it.</summary>
public sealed class ScrollPositionMemory
{
    public const int Capacity = 256;

    // Key → its node in _order (the node carries the offset). _order runs least → most recently saved: a re-Save moves its
    // node to the back and eviction takes the front. A FIFO queue kept a key at its FIRST insertion, so a page saved
    // seconds ago was dropped first, and Forget left a stale copy behind that later deleted the key's fresh entry.
    private readonly Dictionary<string, LinkedListNode<(string Key, double Offset)>> _entries = new();
    private readonly LinkedList<(string Key, double Offset)> _order = new();

    public int Count => _entries.Count;

    public void Save(string key, double offset)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            node.Value = (key, offset);
            _order.Remove(node);
            _order.AddLast(node);
            return;
        }
        if (_entries.Count >= Capacity)
        {
            node = _order.First!;                 // least recently saved; its node is recycled for the new key
            _order.RemoveFirst();
            _entries.Remove(node.Value.Key);
            node.Value = (key, offset);
        }
        else node = new LinkedListNode<(string Key, double Offset)>((key, offset));
        _order.AddLast(node);
        _entries[key] = node;
    }

    public bool TryGet(string key, out double offset)
    {
        if (_entries.TryGetValue(key, out var node)) { offset = node.Value.Offset; return true; }
        offset = 0.0;
        return false;
    }

    public void Forget(string key)
    {
        if (_entries.Remove(key, out var node)) _order.Remove(node);
    }
}
