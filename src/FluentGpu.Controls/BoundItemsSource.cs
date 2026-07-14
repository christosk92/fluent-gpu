using System;
using System.Collections.Generic;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>Creates atomic, signal-backed item sources for <see cref="ItemsView.CreateBound{T}"/>. The snapshot is
/// read exactly once per count/item resolution; selectors must derive solely from that snapshot so every cell in a
/// persistent slot observes one coherent collection version.</summary>
public static class BoundItems
{
    /// <summary>Adapts an ordinary reactive list. <paramref name="fallback"/> is returned while a recycled slot is
    /// transiently outside the current range (for example during a shrink).</summary>
    public static BoundItemsSource<T> From<T>(IReadSignal<IReadOnlyList<T>> items, T fallback)
        => Project(items, static x => x.Count, static (x, i) => x[i], fallback);

    /// <summary>Projects a larger reactive snapshot into a bound item source. Both selectors receive the same snapshot
    /// value; keep them pure and do not read independent mutable collection fields from inside them.</summary>
    public static BoundItemsSource<TItem> Project<TSnapshot, TItem>(
        IReadSignal<TSnapshot> snapshot,
        Func<TSnapshot, int> count,
        Func<TSnapshot, int, TItem> itemAt,
        TItem fallback)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(count);
        ArgumentNullException.ThrowIfNull(itemAt);

        int ReadCount(bool tracked)
        {
            var current = tracked ? snapshot.Value : snapshot.Peek();
            return Math.Max(0, count(current));
        }

        bool TryRead(int index, bool tracked, out TItem item)
        {
            var current = tracked ? snapshot.Value : snapshot.Peek();
            int n = Math.Max(0, count(current));
            if ((uint)index < (uint)n)
            {
                item = itemAt(current, index);
                return true;
            }
            item = fallback;
            return false;
        }

        return new BoundItemsSource<TItem>(ReadCount, TryRead, fallback);
    }
}

internal delegate int BoundItemCountReader(bool tracked);
internal delegate bool BoundItemReader<T>(int index, bool tracked, out T item);

/// <summary>A stable reactive item source for persistent/recycled rows. Allocate it once (normally through
/// <see cref="BoundItems"/>); <see cref="BindItem"/> allocates one tiny derived signal per mounted slot and performs
/// no allocation when the slot index or source snapshot later changes.</summary>
public sealed class BoundItemsSource<T>
{
    readonly BoundItemCountReader _readCount;
    readonly BoundItemReader<T> _readItem;
    readonly T _fallback;
    readonly IReadSignal<int> _count;

    internal BoundItemsSource(BoundItemCountReader readCount, BoundItemReader<T> readItem, T fallback)
    {
        _readCount = readCount;
        _readItem = readItem;
        _fallback = fallback;
        _count = new CountSignal(this);
    }

    /// <summary>Reactive projected count. Reading <c>Value</c> subscribes to the underlying snapshot.</summary>
    public IReadSignal<int> Count => _count;

    /// <summary>Returns a derived signal for one persistent slot. <paramref name="itemStartIndex"/> is the number of
    /// non-item prefix slots (for example a virtualized hero + header); the logical item index is slot minus start.</summary>
    public IReadSignal<T> BindItem(IReadSignal<int> slotIndex, int itemStartIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(slotIndex);
        return new ItemSignal(this, slotIndex, itemStartIndex);
    }

    /// <summary>Resolve the current item without subscribing. Event handlers should use this instead of capturing the
    /// item returned by an earlier render.</summary>
    public bool TryPeek(int slotIndex, out T item, int itemStartIndex = 0)
        => _readItem(slotIndex - itemStartIndex, tracked: false, out item);

    sealed class CountSignal(BoundItemsSource<T> owner) : IReadSignal<int>
    {
        public int Value => owner._readCount(tracked: true);
        public int Peek() => owner._readCount(tracked: false);
    }

    sealed class ItemSignal(BoundItemsSource<T> owner, IReadSignal<int> slotIndex, int itemStartIndex) : IReadSignal<T>
    {
        public T Value
        {
            get
            {
                int i = slotIndex.Value - itemStartIndex;
                return owner._readItem(i, tracked: true, out var item) ? item : owner._fallback;
            }
        }

        public T Peek()
        {
            int i = slotIndex.Peek() - itemStartIndex;
            return owner._readItem(i, tracked: false, out var item) ? item : owner._fallback;
        }
    }
}

/// <summary>The typed scope for <see cref="ItemsView.CreateBound{T}"/>: selector/interaction state stays on
/// <see cref="Row"/>, while <see cref="Item"/> resolves the current source snapshot and recycled slot index together.</summary>
public readonly record struct BoundItemScope<T>(RowScope Row, IReadSignal<T> Item)
{
    public IReadSignal<int> Index => Row.Index;
    public Func<bool> IsSelected => Row.IsSelected;
    public Func<bool> IsCurrent => Row.IsCurrent;
    public Func<bool> IsEnabled => Row.IsEnabled;
}
