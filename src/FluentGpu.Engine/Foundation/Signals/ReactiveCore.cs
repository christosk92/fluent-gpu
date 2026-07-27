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

    /// <summary>Bring this source up to date so a subscriber resolving a <see cref="Computation.Check"/> can decide
    /// whether it REALLY has to re-run (the pull half of the push-pull cut-off). A <see cref="Signal{T}"/> is always
    /// current by construction (a write is the push) ⇒ no-op; a <see cref="Memo{T}"/> recomputes if its own upstream
    /// moved, and pushes <see cref="Computation.Dirty"/> to its subscribers only when the recomputed value differs.</summary>
    void EnsureFresh();
}

/// <summary>
/// A reactive computation — the base for effects, memos and component render-effects. Holds the set of sources it read
/// last run (so it can unlink before re-tracking) and an owner tree of nested computations + cleanups (disposed on
/// re-run / dispose). Subclasses define what running means.
/// </summary>
public abstract class Computation : IDisposable
{
    // ── Three-state staleness (the push-pull equality cut-off; Reactively/Solid-2.0 shape) ───────────────────────────
    // CLEAN — up to date; nothing to do.
    // CHECK — an ancestor MIGHT have changed: some upstream memo went stale, but nobody knows yet whether it will
    //         actually recompute to a DIFFERENT value. Cheap flag, pushed eagerly through the whole subtree at write
    //         time (that eager reach is what keeps the graph glitch-free — see MarkCheck).
    // DIRTY  — definitely out of date: a signal this computation reads was written, or an upstream memo recomputed to a
    //         value its comparer reports as different.
    // A CHECK is RESOLVED by pulling: poll the recorded sources in read order (ISignalSource.EnsureFresh) and see
    // whether any of them upgrades us to DIRTY. If none does, we go straight back to CLEAN without running the body —
    // that skipped run IS the optimization (before this, an equal memo recompute still re-ran every subscriber).
    // Ordering matters: CLEAN < CHECK < DIRTY, so a state can only ever be RAISED between resolutions.
    internal const byte Clean = 0, Check = 1, Dirty = 2;
    internal byte State = Dirty;          // new computations start dirty (need a first run)
    internal bool Disposed;
    internal bool Queued;                 // already in the runtime's pending list (dedup)

    /// <summary>
    /// STRUCTURAL priority: this computation is a reconciler-owned TREE BOUNDARY (today: the <c>Flow.KeepAlive</c>
    /// boundary effect), not a component render / user effect. Structural effects flush BEFORE normal ones — see
    /// <see cref="ReactiveRuntime.Flush"/> — because a boundary decides whether the components below it should render at
    /// all. Without the split, flush order is subscription order, so one signal write that both re-routes a KeepAlive
    /// boundary AND is read by a page inside it could render that page against the INCOMING value and only then park it.
    /// Immutable for the computation's lifetime (fixed at construction), so the queue choice in
    /// <see cref="ReactiveRuntime.Schedule"/> is a branch, never a re-classification.
    /// </summary>
    internal readonly bool Structural;

    internal readonly ReactiveRuntime Runtime;

    private readonly List<ISignalSource> _sources = new();   // what this read last run
    private List<Action>? _cleanups;                          // onCleanup callbacks
    private List<Computation>? _owned;                        // nested computations created during this run
    private readonly Computation? _owner;                    // who disposes us when they re-run/dispose

    protected Computation(ReactiveRuntime runtime, Computation? owner, bool structural = false)
    {
        Runtime = runtime;
        Structural = structural;
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

    /// <summary>Imperatively mark this computation dirty + schedule it for the next flush (an imperative re-render
    /// request). DIRTY, not CHECK: the caller is asserting the body must run, so it must not be cut off by a poll.</summary>
    public void Schedule() => MarkDirty();

    /// <summary>
    /// Mark this computation DEFINITELY out of date and propagate: effects schedule, memos cascade downstream. Called by
    /// a <see cref="Signal{T}"/>/<see cref="FloatSignal"/> write (the value we read demonstrably moved) and by a
    /// <see cref="Memo{T}"/> whose recompute produced a value its comparer reports as DIFFERENT.
    /// CLEAN→DIRTY runs <see cref="OnStale"/> (schedule/cascade). CHECK→DIRTY is an UPGRADE ONLY: OnStale already ran
    /// when we were flagged CHECK, so we are already queued/cascaded — re-running it would double-schedule.
    /// </summary>
    internal void MarkDirty()
    {
        if (Disposed || State == Dirty) return;
        bool wasClean = State == Clean;
        State = Dirty;
        if (wasClean) OnStale();
    }

    /// <summary>
    /// Flag this computation as MAYBE out of date (an upstream memo went stale but has not recomputed yet). Only
    /// CLEAN→CHECK does anything: it runs <see cref="OnStale"/>, so effects still get QUEUED and downstream memos still
    /// get flagged — the eager cascade that keeps the graph glitch-free — while the decision to actually run is deferred
    /// to the resolution poll (<see cref="ResolveCheck"/>). CHECK is never allowed to demote a DIRTY.
    /// </summary>
    internal void MarkCheck()
    {
        if (Disposed || State != Clean) return;
        State = Check;
        OnStale();
    }

    /// <summary>What "becoming stale" does — effects enqueue for the next flush; memos propagate downstream.</summary>
    private protected abstract void OnStale();

    /// <summary>Re-run because the scheduler picked this dirty computation off the pending queue (effects only; memos are lazy).</summary>
    internal abstract void RunStale();

    /// <summary>
    /// Resolve a <see cref="Check"/> by PULLING: walk the sources recorded on the last run, IN READ ORDER, asking each to
    /// bring itself up to date. Read order is what preserves glitch-freedom for the body that follows — an upstream memo
    /// is refreshed before anything downstream of it observes a value. A source that recomputes to a different value
    /// calls <see cref="MarkDirty"/> on us, which the loop condition sees, so we stop polling the moment the answer is
    /// known (the rest of the sources are irrelevant: we are running regardless, and the run re-reads them anyway).
    /// Still CHECK after the whole walk ⇒ nothing we read actually moved ⇒ back to CLEAN with NO run. Allocation-free:
    /// a byte compare plus the existing source list.
    /// </summary>
    private void ResolveCheck()
    {
        for (int i = 0; i < _sources.Count && State == Check; i++) _sources[i].EnsureFresh();
        if (State == Check) State = Clean;   // the cut: nothing downstream-visible changed
    }

    /// <summary>
    /// The ONE resolution site for every effect-like computation (Effect / ManagedEffect / the reconciler's render and
    /// control-flow effects / AutoEffect): run the body only if this computation is really out of date. CLEAN ⇒ nothing.
    /// CHECK ⇒ poll first, and run only if the poll upgraded us to DIRTY. DIRTY ⇒ run, exactly as before.
    /// Note for AutoEffect: the resolution happens HERE, before <see cref="RunStale"/> hands the body to the passive
    /// drain — so a cut-off effect never enters the drain at all and the passive timing of a surviving one is unchanged.
    /// </summary>
    internal void RunIfNecessary()
    {
        if (State == Clean) return;
        if (State == Check)
        {
            ResolveCheck();
            if (State != Dirty) return;   // resolved CLEAN — the equality cut-off; body skipped
        }
        RunStale();
    }

    /// <summary>Resolve a <see cref="Check"/> in place (used by <see cref="Memo{T}"/>'s pull, which recomputes instead of
    /// running a body). Returns true when the poll left us DIRTY and the caller must recompute.</summary>
    private protected bool NeedsRecompute()
    {
        if (State == Clean) return false;
        if (State == Check) { ResolveCheck(); return State == Dirty; }
        return true;   // Dirty
    }

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
    // TWO queues, one priority order. Everything scheduled lands in exactly one of them (Computation.Structural picks),
    // and a flush iteration always empties the structural one first — see Flush. Both are allocated once and reused
    // (swap + Clear, never re-new), so the second queue costs no per-frame managed allocation.
    private List<Computation> _pendingStructural = new(16);
    private List<Computation> _drainingStructural = new(16);
    private List<Computation> _pending = new(64);
    private List<Computation> _draining = new(64);
    private int _batchDepth;
    private bool _flushing;
    private int _flushGuard;               // batches drained in the CURRENT Flush (the runaway-effect tripwire)

    private const int MaxFlushIterations = 1_000;

    /// <summary>Set by the host: called (once-ish) when work becomes pending, so the host schedules a frame.</summary>
    public Action FrameRequested = static () => { };

    /// <summary>True when effects are queued and waiting for the next <see cref="Flush"/>.</summary>
    public bool HasPending => _pending.Count > 0 || _pendingStructural.Count > 0;

    internal void Schedule(Computation c)
    {
        if (c.Queued || c.Disposed) return;
        c.Queued = true;
        (c.Structural ? _pendingStructural : _pending).Add(c);
        if (_batchDepth == 0 && !_flushing) FrameRequested();
    }

    /// <summary>Coalesce many signal writes (e.g. a pointer-drag burst) into one flush.</summary>
    public void Batch(Action action)
    {
        _batchDepth++;
        try { action(); }
        finally { if (--_batchDepth == 0 && HasPending) FrameRequested(); }
    }

    /// <summary>
    /// Drain all pending effects (and any they transitively schedule) — called by the host once per frame.
    ///
    /// ORDERING GUARANTEE (park-before-render): within every quiescence iteration, STRUCTURAL effects (the
    /// reconciler-owned tree boundaries — see <see cref="Computation.Structural"/>) drain to quiescence BEFORE any
    /// normal effect runs, and a structural effect scheduled from inside a normal batch pre-empts the remainder of that
    /// batch. Consequence: one signal write that both re-routes a <c>Flow.KeepAlive</c> boundary and is read by
    /// components inside it PARKS those components before their render-effects are given the chance to run, so a page
    /// on its way out can never render once against the incoming route (deriving nonsense from another page class's
    /// route) before it is detached. Ordering among effects of the SAME priority is unchanged (schedule order).
    /// </summary>
    public void Flush()
    {
        if (_flushing) return;
        _flushing = true;
        _flushGuard = 0;
        try
        {
            while (HasPending)
            {
                if (!DrainStructural()) return;      // priority pass — boundaries settle first
                if (_pending.Count == 0) continue;   // structural work may have queued only more structural work
                (_draining, _pending) = (_pending, _draining);   // swap; new work lands in the now-empty _pending
                if (!DrainBatch(_draining)) return;
            }
        }
        finally { _flushing = false; }
    }

    /// <summary>Run the structural queue until it is empty (a boundary may re-route another boundary). Returns false
    /// when the runaway guard fired and the flush must abandon this frame.</summary>
    private bool DrainStructural()
    {
        while (_pendingStructural.Count > 0)
        {
            (_drainingStructural, _pendingStructural) = (_pendingStructural, _drainingStructural);
            if (!DrainBatch(_drainingStructural)) return false;
        }
        return true;
    }

    /// <summary>Run one swapped-out batch. Returns false when the runaway guard fired.</summary>
    private bool DrainBatch(List<Computation> batch)
    {
        bool normal = !ReferenceEquals(batch, _drainingStructural);
        for (int i = 0; i < batch.Count; i++)
        {
            // A NORMAL batch yields to structural work the moment any appears — an effect earlier in this very batch may
            // have written the signal that re-routes a boundary, and the rest of this batch can contain the render
            // effects of the components that boundary is about to park. (A structural batch never re-enters here, so
            // there is no recursion: it is already the highest priority.)
            if (normal && _pendingStructural.Count > 0 && !DrainStructural())
            {
                for (int j = i; j < batch.Count; j++) batch[j].Queued = false;   // never strand a queued computation
                batch.Clear();
                return false;
            }
            var c = batch[i];
            c.Queued = false;
            // RunIfNecessary, not RunStale: a computation queued because an upstream MEMO went stale is only
            // flagged CHECK. It polls its sources here — if every memo it reads recomputes to an EQUAL value the
            // body is skipped and it drops back to CLEAN. This strictly REDUCES the work a flush does (a CHECK
            // can only resolve to "run" or "don't", never to more scheduling), so the max-iteration guard below
            // is untouched and the loop cannot be made to spin by the cut-off.
            // Disposed: a structural effect that ran earlier this flush may have removed this computation's subtree.
            if (!c.Disposed) c.RunIfNecessary();
        }
        batch.Clear();
        if (++_flushGuard > MaxFlushIterations) { BailOut(); return false; }
        return true;
    }

    private void BailOut()
    {
        Diag.Event("signals", "Flush exceeded 1000 iterations — likely a self-retriggering effect; bailing.");
        // Drop anything still queued this frame to avoid a hang; it will re-schedule if still stale.
        Drop(_pendingStructural);
        Drop(_pending);

        static void Drop(List<Computation> q)
        {
            for (int i = 0; i < q.Count; i++) q[i].Queued = false;
            q.Clear();
        }
    }
}
