using System;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>Repeat behaviour for the queue (spec §8.1).</summary>
public enum RepeatMode : byte { Off, One, All }

/// <summary>The transition applied at a queue join (spec §8.1). ONE mechanism, overlap ∈ {0, N}: gapless = butt-join
/// with overlap 0; crossfade = overlap N frames of two live voices.</summary>
public enum TransitionKind : byte { Gapless, Crossfade, HardCut }

/// <summary>The default transition for the whole queue (spec §8.1).</summary>
public enum TransitionMode : byte { Gapless, Crossfade, HardCut }

/// <summary>A scheduled per-join transition (spec §8.1). Trim is encoder-INDEPENDENT (a frame count, not a timer).</summary>
public readonly record struct ScheduledTransition(
    TransitionKind Kind, TimeSpan Overlap, TimeSpan? TrimStart, TimeSpan? TrimEnd, Easing Curve)
{
    /// <summary>The default gapless (overlap 0) transition.</summary>
    public static ScheduledTransition Gapless => new(TransitionKind.Gapless, TimeSpan.Zero, null, null, Easing.Linear);
    /// <summary>An equal-power crossfade of the given overlap.</summary>
    public static ScheduledTransition Crossfade(TimeSpan overlap)
        => new(TransitionKind.Crossfade, overlap, null, null, Easing.EaseInOut);
}

/// <summary>The preroll look-ahead policy (spec §8.1).</summary>
public readonly record struct PrefetchPolicy(int LookaheadItems, TimeSpan MaxPrefetchTime)
{
    /// <summary>The default policy: 2 items ahead, up to 30 s of preroll.</summary>
    public static PrefetchPolicy Default => new(2, TimeSpan.FromSeconds(30));
}

/// <summary>One queue entry (spec §8.1). Immutable identity (<see cref="Id"/>) so a reorder/removal is unambiguous.</summary>
public sealed record QueueItem(long Id, MediaSource Source, MediaMetadata? Metadata);

/// <summary>
/// The first-class play queue (spec §8.1) — the OBSERVABLE MODEL + the transport verbs. It holds queue state and raises
/// intents (current index / next-requested); it does NOT preroll yet.
/// <para><b>M0 BOUNDARY:</b> the <c>VoiceScheduler</c>/<c>PreparedSlot</c> preroll machinery (spec §8.4) is M3 — so in M0
/// the queue is a pure state model: <see cref="GoToAsync"/>/<see cref="NextAsync"/>/<see cref="PreviousAsync"/> move
/// <see cref="CurrentIndex"/> and raise <see cref="TransitionRequested"/> for the owner to fulfil, but nothing is
/// pre-rolled. The public surface is complete and stable; only the internals are deferred (no stubbed public members).</para>
/// </summary>
public sealed class PlayQueue
{
    private long _nextId = 1;
    private readonly Signal<int> _currentIndex = new(-1);
    private readonly Signal<QueueItem?> _current = new(null);
    private readonly Random _rng;
    private bool _shuffle;
    private int[]? _order;    // a permutation of item indices (non-null iff shuffle is on)
    private int _orderPos;    // the current position within _order

    /// <summary>Create a queue (optionally with a seeded RNG for a deterministic shuffle in tests).</summary>
    public PlayQueue(int? shuffleSeed = null) => _rng = shuffleSeed is int s ? new Random(s) : new Random();

    /// <summary>The queue items (observable).</summary>
    public ObservableList<QueueItem> Items { get; } = new();
    /// <summary>The current item's index (-1 = none).</summary>
    public IReadSignal<int> CurrentIndex => _currentIndex;
    /// <summary>The current item (null = none).</summary>
    public IReadSignal<QueueItem?> Current => _current;

    /// <summary>The look-ahead preroll policy (consumed by the M3 scheduler).</summary>
    public PrefetchPolicy Prefetch { get; set; } = PrefetchPolicy.Default;
    /// <summary>The default transition applied at joins.</summary>
    public TransitionMode DefaultTransition { get; set; } = TransitionMode.Gapless;
    /// <summary>The repeat mode.</summary>
    public RepeatMode Repeat { get; set; }
    /// <summary>Whether shuffle is enabled. Toggling it (re)builds a fresh play order (current item first).</summary>
    public bool Shuffle
    {
        get => _shuffle;
        set { if (_shuffle == value) return; _shuffle = value; if (value) BuildOrder(); else _order = null; }
    }

    /// <summary>Raised when the owner should move to a new current item (the intent the M3 scheduler/backends fulfil).
    /// The int is the target index; the transition is the join to apply. Never fires with the queue mid-mutation.</summary>
    public event Action<int, ScheduledTransition>? TransitionRequested;

    private readonly System.Collections.Generic.Dictionary<long, ScheduledTransition> _perItemTransitions = new();

    /// <summary>Append a source to the queue; returns the created <see cref="QueueItem"/>.</summary>
    public QueueItem Add(MediaSource source, MediaMetadata? meta = null)
    {
        var item = new QueueItem(_nextId++, source, meta ?? source.Metadata);
        Items.Add(item);
        if (_currentIndex.Peek() < 0) SetCurrent(0);
        if (_shuffle) BuildOrder();
        return item;
    }

    /// <summary>Insert a source right after the current item (play-next).</summary>
    public void InsertNext(MediaSource source)
    {
        var item = new QueueItem(_nextId++, source, source.Metadata);
        int at = Math.Max(0, _currentIndex.Peek() + 1);
        if (at >= Items.Count) Items.Add(item); else Items.Insert(at, item);
        int cur = _currentIndex.Peek();
        if (cur < 0) SetCurrent(0);
        else if (at <= cur) SetCurrent(cur + 1);   // an insert at/before current shifts the current index
        if (_shuffle) BuildOrder();
    }

    /// <summary>Remove an item (fixes up the current index).</summary>
    public void Remove(QueueItem item)
    {
        int idx = Items.IndexOf(item);
        if (idx < 0) return;
        Items.RemoveAt(idx);
        _perItemTransitions.Remove(item.Id);
        int cur = _currentIndex.Peek();
        if (idx < cur) SetCurrent(cur - 1);
        else if (idx == cur) SetCurrent(Math.Min(cur, Items.Count - 1));
        if (_shuffle) BuildOrder();
    }

    /// <summary>Clear the queue.</summary>
    public void Clear()
    {
        Items.Clear();
        _perItemTransitions.Clear();
        SetCurrent(-1);
    }

    /// <summary>Move to <paramref name="index"/> and raise the transition intent (M0: no preroll — the owner fulfils it).</summary>
    public ValueTask GoToAsync(int index)
    {
        if (index < 0 || index >= Items.Count) return ValueTask.CompletedTask;
        var transition = TransitionFor(_currentIndex.Peek());
        SetCurrent(index);
        TransitionRequested?.Invoke(index, transition);
        return ValueTask.CompletedTask;
    }

    /// <summary>Advance to the next item (honouring <see cref="Repeat"/> and <see cref="Shuffle"/>).</summary>
    public ValueTask NextAsync()
    {
        if (Items.Count == 0) return ValueTask.CompletedTask;
        int next = NextTargetIndex();
        return next >= 0 ? GoToAsync(next) : ValueTask.CompletedTask;
    }

    /// <summary>Go to the previous item (honouring <see cref="Shuffle"/> order).</summary>
    public ValueTask PreviousAsync()
    {
        int cur = _currentIndex.Peek();
        if (Items.Count == 0) return ValueTask.CompletedTask;
        if (Repeat == RepeatMode.One) return GoToAsync(cur);

        if (_shuffle && _order is not null)
        {
            int pos = _orderPos - 1;
            if (pos < 0) pos = Repeat == RepeatMode.All ? _order.Length - 1 : 0;
            return GoToAsync(_order[pos]);
        }

        int prev = cur - 1;
        if (prev < 0) prev = Repeat == RepeatMode.All ? Items.Count - 1 : 0;
        return GoToAsync(prev);
    }

    /// <summary>The index <see cref="NextAsync"/> would advance to (honouring Repeat/Shuffle), or −1 when there is no next.
    /// Pure — does not mutate the queue. The scheduler uses it to know WHICH item to preroll.</summary>
    public int NextTargetIndex()
    {
        int cur = _currentIndex.Peek();
        if (Items.Count == 0) return -1;
        if (Repeat == RepeatMode.One) return cur;

        if (_shuffle && _order is not null)
        {
            int pos = _orderPos + 1;
            if (pos >= _order.Length) return Repeat == RepeatMode.All ? _order[0] : -1;
            return _order[pos];
        }

        int next = cur + 1;
        if (next >= Items.Count) return Repeat == RepeatMode.All ? 0 : -1;
        return next;
    }

    /// <summary>The <see cref="QueueItem"/> <see cref="NextAsync"/> would advance to, or null when there is no next — the
    /// item the scheduler pre-rolls at <c>ending-soon</c>.</summary>
    public QueueItem? PeekNext()
    {
        int i = NextTargetIndex();
        return i >= 0 && i < Items.Count ? Items[i] : null;
    }

    /// <summary>The transition applied when leaving item at <paramref name="fromIndex"/> (per-item override or the default).</summary>
    public ScheduledTransition TransitionAfter(int fromIndex) => TransitionFor(fromIndex);

    /// <summary>Override the transition applied when leaving <paramref name="from"/>.</summary>
    public void SetTransition(QueueItem from, ScheduledTransition transition) => _perItemTransitions[from.Id] = transition;

    private void BuildOrder()
    {
        int n = Items.Count;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        for (int i = n - 1; i > 0; i--) { int j = _rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
        int cur = _currentIndex.Peek();
        if (cur >= 0)
        {
            int p = Array.IndexOf(order, cur);
            if (p > 0) (order[0], order[p]) = (order[p], order[0]);
        }
        _order = order;
        _orderPos = 0;
    }

    private ScheduledTransition TransitionFor(int index)
    {
        if (index >= 0 && index < Items.Count && _perItemTransitions.TryGetValue(Items[index].Id, out var t)) return t;
        return DefaultTransition switch
        {
            TransitionMode.Crossfade => ScheduledTransition.Crossfade(TimeSpan.FromSeconds(4)),
            TransitionMode.HardCut => new ScheduledTransition(TransitionKind.HardCut, TimeSpan.Zero, null, null, Easing.Linear),
            _ => ScheduledTransition.Gapless
        };
    }

    private void SetCurrent(int index)
    {
        _currentIndex.Value = index;
        _current.Value = index >= 0 && index < Items.Count ? Items[index] : null;
        if (_shuffle && _order is not null && index >= 0)
        {
            int p = Array.IndexOf(_order, index);
            if (p >= 0) _orderPos = p;
        }
    }
}
