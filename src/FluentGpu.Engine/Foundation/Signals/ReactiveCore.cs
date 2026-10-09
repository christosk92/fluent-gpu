using System.Diagnostics;
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

    /// <summary>DIAGNOSTIC name (<c>DebugName</c> on <see cref="Signal{T}"/>/<see cref="FloatSignal"/>/<see cref="Memo{T}"/>);
    /// null when the author gave none.</summary>
    string? DebugName { get; }

    /// <summary>DIAGNOSTIC kind label, e.g. <c>Signal&lt;Boolean&gt;</c>. Allocates — report time only.</summary>
    string DiagKind { get; }
}

/// <summary>Names a <see cref="Computation.StaleCause"/> for a report line (the render census's <c>by=</c>). Report-time
/// only: every call allocates the label it returns.</summary>
internal static class SignalDiag
{
    /// <summary><c>Kind[:DebugName]</c>; a memo is <c>Memo(OwnerType)[:DebugName]</c> and, when <paramref name="hop"/>,
    /// follows ONE hop to what made the memo stale (<c>Memo(ChartRow)&lt;-Signal&lt;UInt32&gt;:tracks.changed</c>);
    /// null is <c>sched</c> (an imperative schedule or a forced run).</summary>
    public static string Describe(ISignalSource? source, bool hop = true)
    {
        if (source is null) return "sched";
        string label = source.DebugName is { } name ? source.DiagKind + ":" + name : source.DiagKind;
        if (hop && source is Computation c && c.StaleCause is { } upstream && !ReferenceEquals(upstream, source))
            label += "<-" + Describe(upstream, hop: false);
        return label;
    }
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

    private RefList<ISignalSource> _sources;   // what this read last run
    private List<Action>? _cleanups;                          // onCleanup callbacks
    private List<Computation>? _owned;                        // nested computations created during this run
    private readonly Computation? _owner;                    // who disposes us when they re-run/dispose

    /// <summary>DIAGNOSTIC identity: the object this computation exists on behalf of — the <c>Component</c> for a
    /// render-effect, or for an effect/memo a component's hooks created. <c>null</c> for anything built outside a
    /// component. Typed <see cref="object"/> so the reactive core stays ignorant of the hooks layer.
    /// <para>It exists because a leaked per-frame poller — a <c>FrameClock.Tick</c> subscriber that never unmounts and
    /// so pins the host at panel rate forever — was only ever visible as a COUNT
    /// (<c>AppHost.FrameClockPollerCount</c>), and nothing on a subscribed computation said WHO it was:
    /// <see cref="_owner"/> is the disposal scope (a payload-less <c>ReactiveScope</c>) and hook computations are
    /// deliberately ownerless (see the constructor note below), so no walk from the subscriber list reaches a type
    /// name. Read only at diagnostic-report cadence (the 30 s <c>[wake]</c> census), never per frame: one reference
    /// per computation, written once at construction, and no string is materialised until a report names it.</para></summary>
    internal object? DiagOwner;

    /// <summary>DIAGNOSTIC: the source whose change last took this computation out of CLEAN — the signal written (a direct
    /// subscriber), the memo that went stale or recomputed differently (a downstream one), or null for an imperative
    /// <see cref="Schedule"/>. One reference write on the CLEAN→stale edge, no allocation; read (and cleared) by the
    /// reconciler's render census so a census line names WHAT scheduled each re-render (<see cref="SignalDiag"/>).</summary>
    internal ISignalSource? StaleCause;

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
    public void Schedule() => MarkDirty(null);

    /// <summary>
    /// Mark this computation DEFINITELY out of date and propagate: effects schedule, memos cascade downstream. Called by
    /// a <see cref="Signal{T}"/>/<see cref="FloatSignal"/> write (the value we read demonstrably moved) and by a
    /// <see cref="Memo{T}"/> whose recompute produced a value its comparer reports as DIFFERENT.
    /// CLEAN→DIRTY runs <see cref="OnStale"/> (schedule/cascade). CHECK→DIRTY is an UPGRADE ONLY: OnStale already ran
    /// when we were flagged CHECK, so we are already queued/cascaded — re-running it would double-schedule.
    /// </summary>
    internal void MarkDirty(ISignalSource? cause)
    {
        if (Disposed || State == Dirty) return;
        bool wasClean = State == Clean;
        State = Dirty;
        if (wasClean) { StaleCause = cause; OnStale(); }
    }

    /// <summary>
    /// Flag this computation as MAYBE out of date (an upstream memo went stale but has not recomputed yet). Only
    /// CLEAN→CHECK does anything: it runs <see cref="OnStale"/>, so effects still get QUEUED and downstream memos still
    /// get flagged — the eager cascade that keeps the graph glitch-free — while the decision to actually run is deferred
    /// to the resolution poll (<see cref="ResolveCheck"/>). CHECK is never allowed to demote a DIRTY.
    /// </summary>
    internal void MarkCheck(ISignalSource? cause)
    {
        if (Disposed || State != Clean) return;
        State = Check;
        StaleCause = cause;
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

    /// <summary>Drop every upstream link and go DIRTY, so the next pull recomputes (and re-links) from scratch: the
    /// release half of <see cref="Memo{T}"/>'s <c>releaseWhenUnobserved</c>. Never called from inside a notify loop (only
    /// from a read, or from a reader's unlink at its re-run or dispose), so no <c>_subs</c> walk sees it.</summary>
    private protected void ReleaseSources()
    {
        UnlinkSources();
        State = Dirty;
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
/// A completed flush (always run to quiescence). <paramref name="UnitsRun"/> computations ran; the slowest took
/// <paramref name="LongestUnitTicks"/> Stopwatch ticks — synchronous memo pulls and any reconciliation in that callback
/// included — and was <paramref name="LongestUnit"/> (its <c>DiagOwner</c> names the component; the host surfaces a unit
/// longer than a frame in its always-on diagnostics, so a slow render is fixed where it is, never time-sliced away).
/// <paramref name="HasPending"/> is true only when a unit threw (its siblings stay queued) or the runaway guard fired.
/// </summary>
public readonly record struct ReactiveFlushResult(bool HasPending, int UnitsRun, long LongestUnitTicks, Computation? LongestUnit = null);

/// <summary>
/// The UI-thread scheduler: owns pending-effect queues, batch depth, and the host frame-request callback.
/// One per <c>AppHost</c>. Every <see cref="Flush"/> runs to QUIESCENCE: a committed write is applied whole in the flush
/// that drains it — there is no wall-clock slice, so a write is never presented half-applied across frames and a flush
/// always makes progress. The one bound is STRUCTURAL: <see cref="MaxFlushIterations"/> batches in one flush means a
/// self-retriggering cycle, which is broken and reported (always-on) with the owners it ran.
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
    private int _structuralCursor;
    private int _normalCursor;
    private int _batchDepth;
    private bool _flushing;
    // Runaway detector, scoped to ONE Flush call: batches swapped since THIS call started. A flush runs to quiescence, so
    // the only way to swap MaxFlushIterations batches is an effect cycle that re-schedules itself forever.
    private int _flushGuard;
    private readonly Func<long>? _timestamp;

    /// <summary>The structural hang guard: this many queue batches in ONE flush is a self-retriggering cycle (a finite
    /// cascade of dependent effects — each batch one level of it — never comes near it). Reported always-on, then broken.</summary>
    public const int MaxFlushIterations = 1_000;

    /// <summary>Runaway reports so far (always-on counter; each also wrote one <c>[signals.runaway]</c> line).</summary>
    public int RunawayCount { get; private set; }

    public ReactiveRuntime() { }

    // Deterministic unit-timing tests exercise the real scheduler without sleeps or wall-clock races.
    internal ReactiveRuntime(Func<long> timestamp) => _timestamp = timestamp;

    private long Timestamp() => _timestamp is null ? Stopwatch.GetTimestamp() : _timestamp();

    /// <summary>Set by the host: called (once-ish) when work becomes pending, so the host schedules a frame.</summary>
    public Action FrameRequested = static () => { };

    /// <summary>True when effects are queued and waiting for the next <see cref="Flush"/>.</summary>
    public bool HasPending => _pending.Count > 0 || _pendingStructural.Count > 0
        || _normalCursor < _draining.Count || _structuralCursor < _drainingStructural.Count;

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
    /// Run every pending computation until the queues are EMPTY — including everything those runs schedule — and return
    /// what ran (<see cref="ReactiveFlushResult"/>). Hosted frames, the post-realize rebind flush and deterministic harness
    /// operations all use this one entry: a committed write is applied whole in the flush that drains it.
    ///
    /// ORDERING GUARANTEE (park-before-render): within every quiescence iteration, STRUCTURAL effects (the
    /// reconciler-owned tree boundaries — see <see cref="Computation.Structural"/>) drain to quiescence BEFORE any
    /// normal effect runs, and a structural effect scheduled from inside a normal batch pre-empts the remainder of that
    /// batch. Consequence: one signal write that both re-routes a <c>Flow.KeepAlive</c> boundary and is read by
    /// components inside it PARKS those components before their render-effects are given the chance to run, so a page
    /// on its way out can never render once against the incoming route (deriving nonsense from another page class's
    /// route) before it is detached. Ordering among effects of the SAME priority is unchanged (schedule order).
    /// <para>A throwing unit is consumed before it runs: the exception propagates, its unread siblings stay queued (and
    /// keep their dedup flags and order) for the next flush, and the scheduler is released in <c>finally</c>. A re-entrant
    /// call (from inside a running unit) runs nothing. ONE <see cref="Stopwatch"/> read per executed unit: the timestamp
    /// taken after a unit is both that unit's end and the next unit's start (the longest-unit diagnostic).</para>
    /// </summary>
    public ReactiveFlushResult Flush()
    {
        if (_flushing) return new(HasPending, 0, 0);
        _flushing = true;
        _flushGuard = 0;
        int units = 0;
        long longest = 0;
        Computation? longestUnit = null;
        long now = Timestamp();
        try
        {
            while (HasPending)
            {
                bool structural = _structuralCursor < _drainingStructural.Count || _pendingStructural.Count > 0;
                ref var draining = ref (structural ? ref _drainingStructural : ref _draining);
                ref var pending = ref (structural ? ref _pendingStructural : ref _pending);
                ref int cursor = ref (structural ? ref _structuralCursor : ref _normalCursor);
                if (cursor == draining.Count)
                {
                    draining.Clear();
                    cursor = 0;
                    if (++_flushGuard > MaxFlushIterations) { BailOut(pending, 0); break; }   // pending = the batch the cycle queued next
                    (draining, pending) = (pending, draining);
                }

                // Consume before invoking user code: a throw must not replay the failing unit or discard its siblings.
                var computation = draining[cursor++];
                computation.Queued = false;
                if (!computation.Disposed)
                {
                    units++;
                    try { computation.RunIfNecessary(); }
                    finally
                    {
                        long after = Timestamp();
                        if (after - now > longest) { longest = after - now; longestUnit = computation; }
                        now = after;   // this unit's end is the next unit's start — one Stopwatch read per unit
                    }
                }
            }
        }
        finally
        {
            _flushing = false;
            if (HasPending) FrameRequested();   // a unit threw: its siblings wait for the next flush
            else
            {
                _draining.Clear();
                _drainingStructural.Clear();
                _normalCursor = _structuralCursor = 0;
            }
        }
        return new(HasPending, units, longest, longestUnit);
    }

    /// <summary>The runaway guard fired: report the cycle ALWAYS-ON — the owners (component types) of the batch that
    /// tripped it, which is the cycle itself — then break it. Rare by construction, so the one line may allocate.</summary>
    private void BailOut(List<Computation> cycle, int from)
    {
        RunawayCount++;
        var names = new System.Text.StringBuilder(128);
        int named = 0;
        for (int i = from; i < cycle.Count && named < 6; i++)
        {
            object? owner = cycle[i].DiagOwner;
            string n = owner is null ? cycle[i].GetType().Name : owner.GetType().Name;
            if (names.ToString().Contains(n, StringComparison.Ordinal)) continue;
            if (named++ > 0) names.Append(", ");
            names.Append(n);
        }
        Diag.Line($"[signals.runaway] a flush swapped {MaxFlushIterations} batches: a self-retriggering effect cycle, broken " +
            $"(its queued work dropped). The cycle's batch ran: {(named == 0 ? "?" : names.ToString())}.");
        // Break the cycle. Return stale entries to Clean so a subsequent independent signal write can schedule them.
        Drop(_pendingStructural);
        Drop(_pending);
        Drop(_drainingStructural, _structuralCursor);
        Drop(_draining, _normalCursor);
        _structuralCursor = _normalCursor = 0;

        static void Drop(List<Computation> q, int start = 0)
        {
            for (int i = start; i < q.Count; i++)
            {
                q[i].Queued = false;
                q[i].State = Computation.Clean;
            }
            q.Clear();
        }
    }
}
