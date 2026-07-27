namespace FluentGpu.Signals;

/// <summary>
/// A derived reactive value (the Solid <c>createMemo</c> / Vue <c>computed</c>): recomputes lazily from the signals it
/// reads, caches the result, and only notifies its own subscribers when the cached value actually changes. It is both a
/// computation (it subscribes upstream) and a source (downstream subscribes to it).
///
/// PUSH-PULL EQUALITY CUT-OFF. Notification is split in two halves so "only when the value changes" is real without
/// giving up glitch-freedom:
///   • PUSH (eager, at write time, cheap): an upstream write marks this memo <see cref="Computation.Dirty"/>, and this
///     memo cascades a <see cref="Computation.Check"/> — a MAYBE flag — to its own subscribers. The cascade still reaches
///     every transitive subscriber in the same write, so every affected effect is QUEUED and no lazy reader can observe
///     a stale-but-Clean node.
///   • PULL (deferred, at resolution time): a CHECK'd subscriber polls its sources in read order
///     (<see cref="ISignalSource.EnsureFresh"/> → <see cref="UpdateIfNecessary"/>), which recomputes this memo. ONLY IF
///     the new value differs per the comparer does the memo push <see cref="Computation.MarkDirty"/> to its subscribers.
///     An EQUAL recompute is silent, and the subscriber falls back to CLEAN without running its body.
/// So a <c>UseComputed</c> whose value did not move now really does stop the re-render behind it — before this split,
/// staleness propagated unconditionally and the comparer only gated the cached assignment.
///
/// Memos stay PULL-ONLY: they are never enqueued in the runtime's pending list (<see cref="RunStale"/> is a no-op), so
/// the flush loop's structure is unchanged; the work happens inside whichever effect (or lazy reader) needs the value.
/// </summary>
public sealed class Memo<T> : Computation, ISignalSource, IReadSignal<T>
{
    private readonly Func<T> _fn;
    private readonly IEqualityComparer<T> _cmp;
    private readonly List<Computation> _subs = new();
    private readonly Action _compute;   // stable delegate (`_next = _fn()`) — no closure allocated per recompute
    private T _value = default!;
    private T _next = default!;

    public Memo(ReactiveRuntime runtime, Func<T> fn, IEqualityComparer<T>? comparer = null, Computation? owner = null)
        : base(runtime, owner)
    {
        _fn = fn;
        _cmp = comparer ?? EqualityComparer<T>.Default;
        _compute = () => _next = _fn();
        Recompute();   // prime the cached value + dependency links
    }

    public T Value
    {
        get
        {
            UpdateIfNecessary();   // a lazy read outside a flush still pulls (it may be CHECK or DIRTY)
            SubscribeReader();
            return _value;
        }
    }

    public T Peek()
    {
        UpdateIfNecessary();
        return _value;
    }

    /// <summary>
    /// The pull half. CLEAN ⇒ nothing. CHECK ⇒ poll the recorded sources in read order (early-out the moment one of them
    /// upgrades us to DIRTY); if the walk leaves us still CHECK, nothing we read actually moved, so we go CLEAN WITHOUT
    /// recomputing — the cheap cut, which is also what makes a deep memo chain O(changed) instead of O(subscribers).
    /// DIRTY ⇒ <see cref="Recompute"/>.
    /// </summary>
    internal void UpdateIfNecessary()
    {
        if (NeedsRecompute()) Recompute();
    }

    private void Recompute()
    {
        // RunComputation re-tracks dependencies and leaves us CLEAN.
        RunComputation(_compute);
        T next = _next;
        _next = default!;                       // don't pin a reference between recomputes
        if (_cmp.Equals(_value, next)) return;  // EQUAL ⇒ silence: subscribers stay CHECK and resolve to CLEAN unrun
        _value = next;
        // CHANGED ⇒ the deferred, equality-gated push. Subscribers were already flagged CHECK (and therefore already
        // queued/cascaded) by OnStale at write time, so MarkDirty here is an in-place upgrade for them — it does not
        // re-schedule, and a subscriber currently polling us sees the upgrade on its next loop-condition check.
        for (int i = _subs.Count - 1; i >= 0; i--) _subs[i].MarkDirty();
    }

    // A memo becoming stale cascades a MAYBE (Check) downstream — eagerly, so every transitive subscriber is flagged and
    // scheduled in the same write — while the memo itself stays lazy and recomputes only when pulled. Whether that
    // recompute turns the MAYBE into a real re-run is decided later, by value (see Recompute).
    private protected override void OnStale()
    {
        for (int i = _subs.Count - 1; i >= 0; i--) _subs[i].MarkCheck();
    }

    internal override void RunStale() { /* memos are pull-based: no scheduled run */ }

    private void SubscribeReader()
    {
        var c = Tracking.Current;
        if (c is null || c == this) return;
        if (!_subs.Contains(c)) { _subs.Add(c); c.AddSource(this); }
    }

    void ISignalSource.Unsubscribe(Computation c) => _subs.Remove(c);

    // The pull entry point for a downstream computation resolving its Check (see Computation.ResolveCheck).
    void ISignalSource.EnsureFresh() => UpdateIfNecessary();
}
