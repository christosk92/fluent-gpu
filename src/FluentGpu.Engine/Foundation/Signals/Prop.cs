using System;
using System.Collections.Generic;

namespace FluentGpu.Signals;

/// <summary>
/// A unified reactive channel property: ONE field that is EITHER a static value (re-asserted by the reconciler on
/// every reconcile of the element, iff not bound) OR a reactive bind — a <see cref="Func{T}"/> thunk or a concrete
/// signal the reconciler wires into a mount-time effect that writes exactly one scene column and marks its dirty
/// axis. Constructed implicitly: <c>Opacity = 0.5f</c> (static), <c>Opacity = () =&gt; f(sig.Value)</c> (derived
/// thunk), <c>Opacity = sig</c> (signal-direct — no user closure). <c>default(Prop&lt;T&gt;)</c> is the static
/// <c>default(T)</c> — never use it to mean "unset"; element property initializers (<c>= 1f</c>, <c>= NaN</c>) run
/// the T conversion and survive <c>with</c> clones (a bound clone stays bound).
/// Kind discrimination is a runtime type test on the single payload ref — no tag byte; the static reconcile path
/// reads only <see cref="IsBound"/> (one null test). Conversions from the <see cref="IReadSignal{T}"/> INTERFACE are
/// illegal in C# (CS0552) — through an interface-typed variable use the thunk form <c>chan = () =&gt; s.Value</c>.
/// </summary>
/// <summary>Thunk sugar for inline lambdas: C# cannot chain a lambda conversion into a user-defined conversion, so
/// <c>Opacity = () =&gt; x</c> does not compile bare — write <c>Opacity = Prop.Of(() =&gt; x)</c> (or assign a typed
/// <c>Func&lt;T&gt;</c> local, the usual control idiom). Pure passthroughs should assign the signal itself instead.</summary>
public static class Prop
{
    public static Prop<T> Of<T>(Func<T> thunk) => thunk;

    /// <summary>Bind a channel from an interface-typed reactive source (a <c>Memo&lt;T&gt;</c> or <c>Signal&lt;T&gt;</c>
    /// held as <see cref="IReadSignal{T}"/>). The named form of the signal-direct bind for the interface case, where the
    /// implicit <c>Signal&lt;T&gt;</c>/<c>Memo&lt;T&gt;</c> conversions don't apply (a user conversion from an interface
    /// is illegal, CS0552). Equivalent to assigning a concrete signal directly: <c>Fill = Prop.Bind(store.Color)</c>.</summary>
    public static Prop<T> Bind<T>(IReadSignal<T> signal) => Prop<T>.FromSignal(signal);
}

public readonly struct Prop<T> : IEquatable<Prop<T>>
{
    private readonly T _value;        // static value, stored inline (never boxed)
    private readonly object? _ref;    // null = static | Func<T> = thunk | IReadSignal<T> = signal-direct

    private Prop(T value, object? @ref) { _value = value; _ref = @ref; }

    /// <summary>True ⇒ a bind owns this channel; the reconciler's static write must skip it.</summary>
    public bool IsBound => _ref is not null;
    /// <summary>The static value — meaningful only when <see cref="IsBound"/> is false (a bound Prop's value is the
    /// inert conversion seed).</summary>
    public T Value => _value;
    /// <summary>The static value, or <paramref name="fallback"/> when bound — for engine edge readers that need a
    /// scalar regardless (e.g. exit-animation seeds).</summary>
    public T ValueOr(T fallback) => _ref is null ? _value : fallback;

    /// <summary>Resolve the CURRENT value regardless of kind: a static returns its value, a thunk is invoked, a
    /// signal is read (<see cref="IReadSignal{T}.Value"/> — so reading this inside a bind thunk / reactive
    /// computation subscribes to it). Use when a control reads a <see cref="Prop{T}"/> from INSIDE its own bind
    /// thunk (e.g. a culture-reactive placeholder via <c>Loc.Bind(key)</c>), rather than letting the reconciler
    /// wire the channel into a scene-column mount effect.</summary>
    public T Current() => _ref switch
    {
        Func<T> f => f(),
        IReadSignal<T> s => s.Value,
        _ => _value,
    };

    /// <summary>The thunk payload (bind wiring only — one type test per channel per mount).</summary>
    public Func<T>? Thunk => _ref as Func<T>;
    /// <summary>The signal payload (bind wiring only).</summary>
    public IReadSignal<T>? Signal => _ref as IReadSignal<T>;

    public static implicit operator Prop<T>(T value) => new(value, null);
    public static implicit operator Prop<T>(Func<T> thunk) => new(default!, thunk);
    public static implicit operator Prop<T>(Signal<T> signal) => new(default!, signal);
    public static implicit operator Prop<T>(Memo<T> memo) => new(default!, memo);

    /// <summary>Bind from any concrete <see cref="IReadSignal{T}"/> implementation (used by sibling-declared
    /// operators like <c>FloatSignal → Prop&lt;float&gt;</c>, and by callers holding the interface).</summary>
    public static Prop<T> FromSignal(IReadSignal<T> signal) => new(default!, signal);

    // Value equality WITHOUT boxing or reflection. The generated {T}Diff.AnyChanged compares EVERY channel via
    // EqualityComparer<Prop<T>>.Default; absent IEquatable<Prop<T>> that resolves to ObjectEqualityComparer, which boxes
    // BOTH operands and runs reflection-based ValueType.Equals on every node's every channel on every reconcile — the
    // dominant cost of a large-tree diff (a route swap) AND a major reconcile-phase allocation source (→ GC pauses).
    // Implementing IEquatable flips the comparer to the no-box GenericEqualityComparer. Semantics MATCH the prior
    // ValueType.Equals field-wise compare exactly: the inline value (via T's own default comparer) AND the bind payload
    // (reference/delegate equality, null-safe) — so a static prop diffs by value, a bound prop by payload identity.
    public bool Equals(Prop<T> other)
        => EqualityComparer<T>.Default.Equals(_value, other._value) && object.Equals(_ref, other._ref);

    public override bool Equals(object? obj) => obj is Prop<T> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_value, _ref);

    public static bool operator ==(Prop<T> a, Prop<T> b) => a.Equals(b);
    public static bool operator !=(Prop<T> a, Prop<T> b) => !a.Equals(b);
}
