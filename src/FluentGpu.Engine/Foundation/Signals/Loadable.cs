namespace FluentGpu.Signals;

/// <summary>The load state of a <see cref="Loadable{T}"/> — the THIRD state (a per-field Pending) is what lets
/// skeleton-loading express "this node is real while that sibling still shimmers" and incremental field arrival.</summary>
public enum LoadState : byte { Pending, Ready, Failed }

/// <summary>
/// The per-field async-value spine for native skeleton-loading (the one model that survives "fields arrive
/// incrementally"). A tiny bundle of two reactive cells — a <see cref="State"/> signal (Pending|Ready|Failed) and a
/// <see cref="Value"/> signal — so a region or a single leaf can shimmer on Pending and reveal on Ready, with full
/// signals-first granularity (a flip re-fires only the boundaries that read THIS loadable's State). A reference type
/// (NOT a struct): <see cref="SetFailed"/> mutates <see cref="Error"/>, and an incremental field is stored ON a model
/// record (<c>Track.Duration : Loadable&lt;string&gt;</c>) then mutated later — both need reference semantics so every
/// holder sees the same cells. Two <see cref="Signal{T}"/>s allocated once (in <c>UseLoadable</c>/<c>UseAsyncResource</c>).
/// </summary>
public sealed class Loadable<T>
{
    /// <summary>The load state as a byte (reads subscribe). Cast to <see cref="LoadState"/>; byte keeps the signal cheap.</summary>
    public readonly Signal<byte> State;
    /// <summary>The value (meaningful once <see cref="State"/> is Ready; the seed/placeholder while Pending).</summary>
    public readonly Signal<T> Value;
    /// <summary>The failure (set by <see cref="SetFailed"/>); null unless State is Failed.</summary>
    public Exception? Error { get; private set; }

    private Loadable(LoadState state, T value)
    {
        State = new Signal<byte>((byte)state);
        Value = new Signal<T>(value);
    }

    /// <summary>A still-loading loadable; <paramref name="seed"/> is the placeholder value (e.g. an empty array).</summary>
    public static Loadable<T> Pending(T seed = default!) => new(LoadState.Pending, seed);
    /// <summary>An already-resolved loadable (the partial-KNOWN case — a field you had on click).</summary>
    public static Loadable<T> Ready(T value) => new(LoadState.Ready, value);
    /// <summary>A pre-failed loadable.</summary>
    public static Loadable<T> Failed(Exception e) { var l = new Loadable<T>(LoadState.Pending, default!); l.SetFailed(e); return l; }

    /// <summary>Peek the state without subscribing (engine-edge readers — the reconciler boundary checks).</summary>
    public bool IsReady => State.Peek() == (byte)LoadState.Ready;
    public bool IsLoading => State.Peek() == (byte)LoadState.Pending;
    public bool IsFailed => State.Peek() == (byte)LoadState.Failed;

    /// <summary>The "data loaded" trigger: write the value then flip to Ready (two signal writes, batched into one
    /// flush). The async producer calls this on the UI thread (via <c>UsePost</c>).</summary>
    public void SetReady(T value) { Value.Value = value; State.Value = (byte)LoadState.Ready; Error = null; }
    /// <summary>Re-arm the loading state (a refresh).</summary>
    public void SetPending() { Error = null; State.Value = (byte)LoadState.Pending; }
    /// <summary>Re-arm loading AND reset the value to <paramref name="seed"/> — a RE-KEYED reload (the new key shows its
    /// own placeholder/preview instead of the prior key's stale value while the fresh load runs). Value-then-State so the
    /// two writes batch into one flush, matching <see cref="SetReady"/>.</summary>
    public void SetPending(T seed) { Value.Value = seed; Error = null; State.Value = (byte)LoadState.Pending; }
    /// <summary>Route a failure through the same State signal (drives the region's onFailed branch).</summary>
    public void SetFailed(Exception e) { Error = e; State.Value = (byte)LoadState.Failed; }

    /// <summary>A <see cref="Prop{T}"/> thunk for a bound leaf (<c>Text = field.Bind()</c>): reads <see cref="State"/>
    /// THEN <see cref="Value"/>, so the bound node re-resolves when the field flips Ready (and tracks value changes).</summary>
    public Prop<T> Bind() => Prop.Of(() => { _ = State.Value; return Value.Value; });
}
