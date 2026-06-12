namespace FluentGpu.Signals;

/// <summary>Read-only view of a reactive value (a <see cref="Signal{T}"/> or a <see cref="Memo{T}"/>).</summary>
public interface IReadSignal<out T>
{
    /// <summary>Read the value AND subscribe the current computation (if any).</summary>
    T Value { get; }
    /// <summary>Read without subscribing.</summary>
    T Peek();
}

/// <summary>
/// A reactive cell: reading <see cref="Value"/> inside a computation subscribes it; writing notifies subscribers
/// (deferred — they re-run on the next <see cref="ReactiveRuntime.Flush"/>). The unit of state in the engine; a
/// <c>UseState</c> is a Signal whose subscriber is the owning component's render-effect.
/// </summary>
public sealed class Signal<T> : ISignalSource, IReadSignal<T>
{
    private T _value;
    private readonly IEqualityComparer<T> _cmp;
    private readonly List<Computation> _subs = new();

    public Signal(T initial, IEqualityComparer<T>? comparer = null)
    {
        _value = initial;
        _cmp = comparer ?? EqualityComparer<T>.Default;
    }

    public T Value
    {
        get { Subscribe(); return _value; }
        set
        {
            if (_cmp.Equals(_value, value)) return;
            _value = value;
            NotifySubscribers();
        }
    }

    public T Peek() => _value;

    /// <summary>True when at least one computation is subscribed (lets the host skip publishing an ambient value nobody reads).</summary>
    public bool HasSubscribers => _subs.Count > 0;

    /// <summary>Functional update (read-modify-write off the latest committed value), e.g. <c>s.Update(x =&gt; x + 1)</c>.</summary>
    public void Update(Func<T, T> f) => Value = f(_value);

    private void Subscribe()
    {
        var c = Tracking.Current;
        if (c is null) return;
        if (!_subs.Contains(c)) { _subs.Add(c); c.AddSource(this); }
    }

    private void NotifySubscribers()
    {
        // Iterate a snapshot count downward — MarkStale won't mutate _subs (effects only schedule), so a forward
        // loop is safe; using the live list avoids an allocation on the hot write path.
        for (int i = _subs.Count - 1; i >= 0; i--) _subs[i].MarkStale();
    }

    void ISignalSource.Unsubscribe(Computation c) => _subs.Remove(c);
}

/// <summary>
/// A specialized scalar signal for the hot high-frequency path (slider scrub, scroll offset, progress) — avoids the
/// generic boxing/comparer indirection so a per-pointer-move write is allocation-free. Bind it to a node channel
/// (transform/paint) for a value→pixels update that skips render/reconcile/layout entirely.
/// </summary>
public sealed class FloatSignal : ISignalSource, IReadSignal<float>
{
    private float _value;
    private readonly List<Computation> _subs = new();

    public FloatSignal(float initial = 0f) => _value = initial;

    // Declared here, not on Prop<float>: a user conversion on Prop<T> may only involve types spelled in terms of T
    // (FloatSignal is concrete, and conversions from the IReadSignal<T> interface are illegal — CS0552).
    public static implicit operator Prop<float>(FloatSignal s) => Prop<float>.FromSignal(s);

    public float Value
    {
        get { Subscribe(); return _value; }
        set
        {
            if (_value == value) return;   // exact-equality is intended for a scrub stream
            _value = value;
            for (int i = _subs.Count - 1; i >= 0; i--) _subs[i].MarkStale();
        }
    }

    public float Peek() => _value;

    private void Subscribe()
    {
        var c = Tracking.Current;
        if (c is null) return;
        if (!_subs.Contains(c)) { _subs.Add(c); c.AddSource(this); }
    }

    void ISignalSource.Unsubscribe(Computation c) => _subs.Remove(c);
}
