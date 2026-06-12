namespace FluentGpu.Signals;

/// <summary>
/// A derived reactive value (the Solid <c>createMemo</c> / Vue <c>computed</c>): recomputes lazily from the signals it
/// reads, caches the result, and only notifies its own subscribers when the cached value actually changes. It is both a
/// computation (it subscribes upstream) and a source (downstream subscribes to it).
/// </summary>
public sealed class Memo<T> : Computation, ISignalSource, IReadSignal<T>
{
    private readonly Func<T> _fn;
    private readonly IEqualityComparer<T> _cmp;
    private readonly List<Computation> _subs = new();
    private T _value = default!;

    public Memo(ReactiveRuntime runtime, Func<T> fn, IEqualityComparer<T>? comparer = null, Computation? owner = null)
        : base(runtime, owner)
    {
        _fn = fn;
        _cmp = comparer ?? EqualityComparer<T>.Default;
        Recompute();   // prime the cached value + dependency links
    }

    public T Value
    {
        get
        {
            if (State == Stale) Recompute();
            SubscribeReader();
            return _value;
        }
    }

    public T Peek()
    {
        if (State == Stale) Recompute();
        return _value;
    }

    private void Recompute()
    {
        T next = default!;
        RunComputation(() => next = _fn());
        if (!_cmp.Equals(_value, next)) _value = next;   // staleness already propagated on MarkStale; cache the result
    }

    // A memo becoming stale propagates downstream (its subscribers) so dependent effects re-run; the memo itself stays
    // lazy and recomputes only when next read.
    private protected override void OnStale()
    {
        for (int i = _subs.Count - 1; i >= 0; i--) _subs[i].MarkStale();
    }

    internal override void RunStale() { /* memos are pull-based: no scheduled run */ }

    private void SubscribeReader()
    {
        var c = Tracking.Current;
        if (c is null || c == this) return;
        if (!_subs.Contains(c)) { _subs.Add(c); c.AddSource(this); }
    }

    void ISignalSource.Unsubscribe(Computation c) => _subs.Remove(c);
}
