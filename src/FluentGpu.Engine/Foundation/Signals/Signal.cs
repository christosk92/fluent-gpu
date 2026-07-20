namespace FluentGpu.Signals;

/// <summary>Read-only view of a reactive value (a <see cref="Signal{T}"/> or a <see cref="Memo{T}"/>).</summary>
public interface IReadSignal<out T>
{
    /// <summary>Read the value AND subscribe the current computation (if any).</summary>
    T Value { get; }
    /// <summary>Read without subscribing.</summary>
    T Peek();
}

/// <summary>Factory helpers for <see cref="Signal{T}"/>.</summary>
public static class Signal
{
    /// <summary>An always-notify signal: every set notifies subscribers even when the value is equal to the current one
    /// (the third equality mode — see <see cref="Signal{T}"/>). For animation-tick / semantic-retrigger signals and for
    /// per-field prop diffing of mutable references where an equal-by-reference write must still re-run consumers.</summary>
    public static Signal<T> AlwaysNotify<T>(T initial) => new(initial, AlwaysNotifyComparer<T>.Instance);
}

/// <summary>An equality comparer that never reports equal — the always-notify mode (<see cref="Signal.AlwaysNotify{T}"/>).</summary>
internal sealed class AlwaysNotifyComparer<T> : IEqualityComparer<T>
{
    public static readonly AlwaysNotifyComparer<T> Instance = new();
    public bool Equals(T? a, T? b) => false;   // never equal ⇒ every set notifies
    public int GetHashCode(T v) => 0;
}

/// <summary>
/// A reactive cell: reading <see cref="Value"/> inside a computation subscribes it; writing notifies subscribers
/// (deferred — they re-run on the next <see cref="ReactiveRuntime.Flush"/>). The unit of state in the engine; a
/// <c>UseState</c> is a Signal whose subscriber is the owning component's render-effect.
///
/// Three equality modes decide when a write notifies:
///   • DEFAULT — <see cref="EqualityComparer{T}.Default"/>; equal-by-value writes are coalesced (no notify).
///   • CUSTOM — pass an <see cref="IEqualityComparer{T}"/> to the constructor (e.g. a tolerance/identity comparer).
///   • ALWAYS-NOTIFY — <see cref="Signal.AlwaysNotify{T}"/>; every set notifies, even on an equal value.
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
        set => SetIfChanged(value);
    }

    /// <summary>Write <paramref name="value"/> and report whether it changed: under the signal's equality mode an equal
    /// value is coalesced (returns <c>false</c>, no notify), otherwise it writes + notifies and returns <c>true</c>. One
    /// compare (the setter routes through here — no doubled gate). An always-notify signal returns <c>true</c> every set.</summary>
    public bool SetIfChanged(T value)
    {
        if (BackwardsWriteGuard.CompiledIn && BackwardsWriteGuard.Enabled)
            BackwardsWriteGuard.CheckWrite(Tracking.Current, _subs, typeof(T));
        if (_cmp.Equals(_value, value)) return false;
        _value = value;
        NotifySubscribers();
        return true;
    }

    public T Peek() => _value;

    /// <summary>True when at least one computation is subscribed (lets the host skip publishing an ambient value nobody reads).</summary>
    public bool HasSubscribers => _subs.Count > 0;

    /// <summary>Number of subscribed computations. Used as a steady-state guardrail (finding #4): a perpetual
    /// <c>FrameClock.Tick</c> poller that fails to unmount keeps the frame loop awake forever — the soak/CI harness can
    /// assert <c>AppHost.FrameClockPollerCount</c> returns to 0 when playback/animation stops, catching that regression class.</summary>
    public int SubscriberCount => _subs.Count;

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
        set => SetIfChanged(value);   // exact-equality is intended for a scrub stream
    }

    /// <summary>Write <paramref name="value"/> and report whether it changed (exact float compare) — <c>false</c> +
    /// no notify on an equal write, <c>true</c> + notify on a change. Single compare (the setter routes through here).</summary>
    public bool SetIfChanged(float value)
    {
        if (BackwardsWriteGuard.CompiledIn && BackwardsWriteGuard.Enabled)
            BackwardsWriteGuard.CheckWriteFloat(Tracking.Current, _subs);
        if (_value == value) return false;
        _value = value;
        for (int i = _subs.Count - 1; i >= 0; i--) _subs[i].MarkStale();
        return true;
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
