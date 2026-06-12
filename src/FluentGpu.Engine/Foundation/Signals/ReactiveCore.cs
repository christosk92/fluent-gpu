using FluentGpu.Foundation;

namespace FluentGpu.Signals;

/// <summary>
/// The reactive core: a Solid/Preact-style fine-grained reactivity graph. Signals are observable cells; computations
/// (effects, memos, component render-effects) auto-subscribe to the signals they READ during a run, and re-run when a
/// read signal changes. This is the single update mechanism the whole engine is built on — a property binding is an
/// effect at node granularity; a component re-render is an effect at subtree granularity.
///
/// Threading: UI-thread-confined (the engine's single render/reconcile thread), so tracking state is <see cref="ThreadStaticAttribute"/>.
/// Scheduling is DEFERRED: a signal write marks dependent effects stale and asks the host for a frame; the host drains
/// them once per frame via <see cref="ReactiveRuntime.Flush"/> (phase 3), which keeps reconcile/layout on their phases
/// and the per-frame paint allocation-free.
/// </summary>
public static class Reactive
{
    /// <summary>Run <paramref name="fn"/> without subscribing the current computation to any signal it reads.</summary>
    public static T Untrack<T>(Func<T> fn)
    {
        var prev = Tracking.Current;
        Tracking.Current = null;
        try { return fn(); }
        finally { Tracking.Current = prev; }
    }

    /// <summary>Read a signal-backed value without subscribing (peek).</summary>
    public static void Untrack(Action fn)
    {
        var prev = Tracking.Current;
        Tracking.Current = null;
        try { fn(); }
        finally { Tracking.Current = prev; }
    }

    /// <summary>Register a cleanup to run when the enclosing computation re-runs or is disposed (the Solid <c>onCleanup</c>).</summary>
    public static void OnCleanup(Action cleanup)
    {
        Tracking.Current?.AddCleanup(cleanup);
    }
}

/// <summary>Per-thread reactive tracking state (the "currently running computation").</summary>
internal static class Tracking
{
    [ThreadStatic] internal static Computation? Current;
}

/// <summary>A source of reactive change (a <see cref="Signal{T}"/> or a <see cref="Memo{T}"/>): tracks its subscribers.</summary>
internal interface ISignalSource
{
    void Unsubscribe(Computation c);
}

/// <summary>
/// A reactive computation — the base for effects, memos and component render-effects. Holds the set of sources it read
/// last run (so it can unlink before re-tracking) and an owner tree of nested computations + cleanups (disposed on
/// re-run / dispose). Subclasses define what running means.
/// </summary>
public abstract class Computation : IDisposable
{
    internal const byte Clean = 0, Stale = 1;
    internal byte State = Stale;          // new computations start stale (need a first run)
    internal bool Disposed;
    internal bool Queued;                 // already in the runtime's pending list (dedup)

    internal readonly ReactiveRuntime Runtime;

    private readonly List<ISignalSource> _sources = new();   // what this read last run
    private List<Action>? _cleanups;                          // onCleanup callbacks
    private List<Computation>? _owned;                        // nested computations created during this run
    private readonly Computation? _owner;                    // who disposes us when they re-run/dispose

    protected Computation(ReactiveRuntime runtime, Computation? owner)
    {
        Runtime = runtime;
        // Ownership is EXPLICIT (no ambient capture): hook-created computations (UseComputed memos, bindings) persist
        // across a component's re-renders and are disposed by the reconciler on unmount, not auto-disposed by the
        // enclosing render-effect's next run. Pass an owner only when you want auto-cascade disposal.
        _owner = owner;
        _owner?.Own(this);
    }

    private void Own(Computation child) => (_owned ??= new()).Add(child);

    private void RemoveOwned(Computation child) => _owned?.Remove(child);

    internal void AddSource(ISignalSource s) => _sources.Add(s);

    internal void AddCleanup(Action c) => (_cleanups ??= new()).Add(c);

    /// <summary>Imperatively (re-)run this computation now, tracking dependencies (first mount, or a forced run).</summary>
    public void RunNow() => RunStale();

    /// <summary>Imperatively mark this computation stale + schedule it for the next flush (an imperative re-render request).</summary>
    public void Schedule() => MarkStale();

    /// <summary>Mark this computation stale and propagate: effects schedule, memos cascade to their own subscribers.</summary>
    internal void MarkStale()
    {
        if (Disposed || State == Stale) return;
        State = Stale;
        OnStale();
    }

    /// <summary>What "becoming stale" does — effects enqueue for the next flush; memos propagate downstream.</summary>
    private protected abstract void OnStale();

    /// <summary>Re-run because the scheduler picked this stale computation off the pending queue (effects only; memos are lazy).</summary>
    internal abstract void RunStale();

    /// <summary>Re-run the body (clearing nested owned computations + cleanups + old source links first).</summary>
    internal void RunComputation(Action body)
    {
        if (Disposed) return;
        DisposeChildrenAndCleanups();
        UnlinkSources();

        var prevC = Tracking.Current;
        Tracking.Current = this;
        try { body(); }
        finally { Tracking.Current = prevC; State = Clean; }
    }

    private void UnlinkSources()
    {
        for (int i = 0; i < _sources.Count; i++) _sources[i].Unsubscribe(this);
        _sources.Clear();
    }

    private void DisposeChildrenAndCleanups()
    {
        if (_owned is { Count: > 0 })
        {
            for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
            _owned.Clear();
        }
        if (_cleanups is { Count: > 0 })
        {
            for (int i = _cleanups.Count - 1; i >= 0; i--) _cleanups[i]();
            _cleanups.Clear();
        }
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        DisposeChildrenAndCleanups();
        UnlinkSources();
        _owner?.RemoveOwned(this);
    }
}

/// <summary>
/// A stable owner that never re-runs — a container for child computations + cleanups. Used as a component's lifetime
/// scope (its render-effect + bindings live under it) and as the app root. Disposing it cascades to everything it owns.
/// </summary>
public sealed class ReactiveScope : Computation
{
    public ReactiveScope(ReactiveRuntime runtime, Computation? owner = null) : base(runtime, owner) => State = Clean;
    private protected override void OnStale() { }
    internal override void RunStale() { }
}

/// <summary>
/// The scheduler: owns the pending-effect queue, the batch depth, and the "a frame is needed" callback the host wires
/// to wake its loop. One per <c>AppHost</c> (so headless tests don't cross-contaminate). Single-threaded.
/// </summary>
public sealed class ReactiveRuntime
{
    private List<Computation> _pending = new(64);
    private List<Computation> _draining = new(64);
    private int _batchDepth;
    private bool _flushing;

    /// <summary>Set by the host: called (once-ish) when work becomes pending, so the host schedules a frame.</summary>
    public Action FrameRequested = static () => { };

    /// <summary>True when effects are queued and waiting for the next <see cref="Flush"/>.</summary>
    public bool HasPending => _pending.Count > 0;

    internal void Schedule(Computation c)
    {
        if (c.Queued || c.Disposed) return;
        c.Queued = true;
        _pending.Add(c);
        if (_batchDepth == 0 && !_flushing) FrameRequested();
    }

    /// <summary>Coalesce many signal writes (e.g. a pointer-drag burst) into one flush.</summary>
    public void Batch(Action action)
    {
        _batchDepth++;
        try { action(); }
        finally { if (--_batchDepth == 0 && _pending.Count > 0) FrameRequested(); }
    }

    /// <summary>Drain all pending effects (and any they transitively schedule) — called by the host once per frame.</summary>
    public void Flush()
    {
        if (_flushing) return;
        _flushing = true;
        try
        {
            int guard = 0;
            while (_pending.Count > 0)
            {
                (_draining, _pending) = (_pending, _draining);   // swap; new work lands in the now-empty _pending
                var batch = _draining;
                for (int i = 0; i < batch.Count; i++)
                {
                    var c = batch[i];
                    c.Queued = false;
                    if (!c.Disposed && c.State == Computation.Stale) c.RunStale();
                }
                batch.Clear();
                if (++guard > 1_000)
                {
                    Diag.Event("signals", "Flush exceeded 1000 iterations — likely a self-retriggering effect; bailing.");
                    // Drop anything still queued this frame to avoid a hang; it will re-schedule if still stale.
                    for (int i = 0; i < _pending.Count; i++) _pending[i].Queued = false;
                    _pending.Clear();
                    break;
                }
            }
        }
        finally { _flushing = false; }
    }
}
