using System;

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
public readonly struct Prop<T>
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
}
