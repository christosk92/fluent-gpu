using System.Collections.Generic;

namespace FluentGpu.Scroll.Runtime;

/// <summary>Pure scroll-position memory keyed by an app-supplied content identity (<c>ScrollEl.ScrollKey</c> /
/// <c>VirtualListEl.ScrollKey</c>): the host <see cref="Save"/>s a viewport's shown offset when its handle is
/// destroyed and hands it to <see cref="ScrollHandle.Restore"/> on the next mount under the same key (design §9 —
/// replaces the reconciler's <c>ScrollMemory</c>). Bounded by <see cref="Capacity"/> entries (LRU by insertion order).</summary>
public sealed class ScrollPositionMemory
{
    public const int Capacity = 256;

    private readonly Dictionary<string, double> _offsets = new();
    private readonly Queue<string> _order = new();

    public int Count => _offsets.Count;

    public void Save(string key, double offset)
    {
        if (!_offsets.ContainsKey(key))
        {
            _order.Enqueue(key);
            while (_order.Count > Capacity)
            {
                string victim = _order.Dequeue();
                if (!ReferenceEquals(victim, key)) _offsets.Remove(victim);
            }
        }
        _offsets[key] = offset;
    }

    public bool TryGet(string key, out double offset) => _offsets.TryGetValue(key, out offset);

    public void Forget(string key) => _offsets.Remove(key);
}
