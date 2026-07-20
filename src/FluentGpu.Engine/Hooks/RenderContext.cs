using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Hooks;

internal abstract class HookCell { }

/// <summary>A hook cell that owns a disposable reactive primitive (a memo), disposed when the component unmounts.</summary>
internal interface IDisposableCell { void DisposeCell(); }

/// <summary>Backs <see cref="RenderContext.UseState{T}"/> / <see cref="RenderContext.UseSignal{T}"/> — a persistent reactive cell.</summary>
internal sealed class SignalCell<T> : HookCell
{
    public readonly Signal<T> Signal;
    public SignalCell(Signal<T> s) => Signal = s;
}

internal sealed class FloatSignalCell : HookCell
{
    public readonly FloatSignal Signal;
    public FloatSignalCell(FloatSignal s) => Signal = s;
}

internal sealed class MemoHookCell<T> : HookCell, IDisposableCell
{
    public readonly Memo<T> Memo;
    public MemoHookCell(Memo<T> m) => Memo = m;
    public void DisposeCell() => Memo.Dispose();
}

/// <summary>Backs the DEPS-GATED <see cref="RenderContext.UseEffect(Action, DepKey)"/> family: re-runs when the
/// <see cref="DepKey"/> changes (<c>default</c>/<see cref="DepKey.Empty"/> = mount-once). <see cref="Cleanup"/> is the
/// return of a <c>Func&lt;Action?&gt;</c> effect — invoked before each re-run and once at unmount.</summary>
internal sealed class EffectCell : HookCell
{
    public DepKey Key;
    public bool HasKey;     // false until the first run set Key (so mount always runs, even for DepKey.Empty)
    public Action? Cleanup;
}

internal sealed class MemoCell<T> : HookCell
{
    public T Value = default!;
    public DepKey Key;
    public bool HasKey;
}

/// <summary>Backs an AUTO-TRACKED effect (<see cref="RenderContext.UseEffect(Action)"/> / <c>Func&lt;Action?&gt;</c>
/// with no deps): owns the <see cref="AutoEffect"/> computation, disposed on unmount (unlinks sources + runs cleanup).</summary>
internal sealed class AutoEffectCell : HookCell, IDisposableCell
{
    public AutoEffect Effect = null!;
    public void DisposeCell() => Effect.Dispose();
}

/// <summary>
/// An AUTO-TRACKED effect: a reactive computation whose body runs with signal-read tracking, but whose RUN is DEFERRED
/// into the owning context's pending-effect list (drained after paint) — preserving <c>UseEffect</c>'s passive timing
/// (never inline during <see cref="ReactiveRuntime.Flush"/>). Tracking is RUNTIME and re-armed EVERY run: a body that
/// reads <c>sigA</c> this run and (after a branch flip in a later render) <c>sigB</c> the next follows <c>sigB</c> and
/// drops <c>sigA</c> — <see cref="Computation.RunComputation"/> unlinks the old sources then re-links the new. A body
/// that reads no signal runs exactly once. The cleanup (a <c>Func&lt;Action?&gt;</c> return) is registered via
/// <see cref="Reactive.OnCleanup"/>, so it fires before each re-run and once on <see cref="Computation.Dispose"/> (unmount).
/// </summary>
internal sealed class AutoEffect : Computation
{
    private readonly RenderContext _ctx;
    private readonly bool _layout;
    private readonly Action _run;                 // stable delegate enqueued into the pending list (no per-render alloc)
    public Action? ActionBody;                    // set by the void overload
    public Func<Action?>? FuncBody;               // set by the cleanup-returning overload (mutually exclusive)

    public AutoEffect(ReactiveRuntime runtime, RenderContext ctx, bool layout) : base(runtime, owner: null)
    {
        _ctx = ctx; _layout = layout; _run = RunBody;
        // Do NOT run at construction — the hook Primes the first run into the pending list so it lands in the passive
        // drain (after paint), like every other UseEffect body. New Computations start Stale; that is fine (nothing
        // schedules or runs us until Prime), and RunComputation flips us to Clean once the first run executes.
    }

    // A read signal changed off-frame ⇒ ask the runtime to schedule (it wakes the host + dedups). The Flush picks us up
    // and calls RunStale, which does NOT run the body inline — it enqueues it into the passive/layout list instead.
    private protected override void OnStale() => Runtime.Schedule(this);

    internal override void RunStale() => _ctx.EnqueueEffectRun(_run, _layout);

    /// <summary>Enqueue the first run at mount (a frame is already in flight — no wake needed).</summary>
    internal void Prime() => _ctx.EnqueueEffectRun(_run, _layout);

    private void RunBody() => RunComputation(() =>
    {
        if (FuncBody is { } f) { var cleanup = f(); if (cleanup is not null) Reactive.OnCleanup(cleanup); }
        else ActionBody?.Invoke();
    });
}

internal sealed class RefHolderCell : HookCell
{
    public object? Ref;
}

internal sealed class ContextSignalCell<T> : HookCell
{
    public readonly IReadSignal<T> Signal;
    public ContextSignalCell(RenderContext ctx, Context<T> context) => Signal = new RenderContext.ContextReadSignal<T>(ctx, context);
}

internal sealed class SignalEffectCell : HookCell, IDisposableCell
{
    public readonly Effect Effect;
    public SignalEffectCell(Effect effect) => Effect = effect;
    public void DisposeCell() => Effect.Dispose();
}

internal sealed class AnimValueCell : HookCell
{
    public float Current, From, Target, Elapsed;
}

/// <summary>Backs <see cref="RenderContext.UseLoadable{T}"/> — a persistent per-field async value (the skeleton-loading spine).</summary>
internal sealed class LoadableCell<T> : HookCell
{
    public Loadable<T> Loadable = null!;
}

/// <summary>Backs <see cref="RenderContext.UseAsyncResource{T}"/>: the loadable + the fetch's CancellationTokenSource,
/// cancelled on component unmount (IDisposableCell ⇒ RunAllCleanups disposes it) so a back-nav mid-load aborts cleanly.</summary>
internal sealed class AsyncResourceCell<T> : HookCell, IDisposableCell
{
    public Loadable<T> Loadable = null!;
    public CancellationTokenSource Cts = null!;
    public bool Started;
    public DepKey Deps;      // the dep-keyed overload's reload gate (unused by the once-at-mount overload)
    public bool HasDeps;     // false for the once-at-mount overload; true when the dep-keyed overload seeded Deps
    public void DisposeCell() { try { Cts.Cancel(); } catch { /* already disposed */ } Cts.Dispose(); }
}

/// <summary>Backs <see cref="RenderContext.UseVideoSurface"/>: the acquired video-surface token, released (tearing down
/// the presenter child visual) when the owning component unmounts.</summary>
internal sealed class VideoSurfaceCell : HookCell, IDisposableCell
{
    public FluentGpu.Media.VideoSurfaceRegistry? Registry;
    public int Token;
    public void DisposeCell() { if (Token > 0) Registry?.Release(Token); }
}

/// <summary>Backs <see cref="RenderContext.UseDisposable"/>: a component-lifetime-owned disposable created once at mount
/// and disposed on unmount.</summary>
internal sealed class DisposableCell<T> : HookCell, IDisposableCell where T : class, IDisposable
{
    public T? Value;
    public void DisposeCell() { try { Value?.Dispose(); } catch { /* swallow teardown faults */ } }
}

/// <summary>Backs <see cref="RenderContext.UseAsyncCommand"/>: the persistent command + whether to cancel its in-flight
/// run on unmount (default false — a started command should complete; the spinner is gone with the component).</summary>
internal sealed class AsyncCommandCell : HookCell, IDisposableCell
{
    public AsyncCommand Command = null!;
    public bool CancelOnUnmount;
    public void DisposeCell() { if (CancelOnUnmount) Command.Cancel(); }
}

/// <summary>Backs <see cref="RenderContext.UseAsyncCommands{TKey}"/>: the persistent keyed command set.</summary>
internal sealed class AsyncCommandSetCell<TKey> : HookCell, IDisposableCell where TKey : notnull
{
    public AsyncCommandSet<TKey> Commands = null!;
    public bool CancelOnUnmount;
    public void DisposeCell() { if (CancelOnUnmount) Commands.CancelAll(); }
}

/// <summary>A stable, mutable per-component box that persists across renders (the React <c>useRef</c> container).</summary>
public sealed class Ref<T>
{
    public T Value;
    public Ref(T value) => Value = value;
}

/// <summary>Persistent backing for one <see cref="RenderContext.UseGesture"/> declaration (input-a11y.md §13). Created
/// once (held in a <see cref="Ref{T}"/> cell), so its <see cref="Register"/> effect delegate + the installed forwarder
/// are stable instances — the layout-effect registers only at mount / kind-change and the steady render allocates
/// nothing. The forwarder dispatches to the LATEST <see cref="Handler"/> (overwritten each render), so the call site
/// may pass a fresh lambda without re-registering.</summary>
internal sealed class GestureHookState
{
    private readonly RenderContext _ctx;
    private readonly GestureType _kind;
    public Action<GestureEventArgs>? Handler;
    public readonly DepKey KindDep;              // the layout-effect dep key (stable; re-registers only on kind change)
    public readonly Action Register;             // stable effect delegate (installs the forwarder on the current node)
    private readonly Action<GestureEventArgs> _forward;   // stable forwarder written into the scene column
    private NodeHandle _registeredNode;           // the node the forwarder is currently installed on (for re-target/cleanup)

    public GestureHookState(RenderContext ctx, GestureType kind)
    {
        _ctx = ctx;
        _kind = kind;
        KindDep = DepKey.From((int)kind);
        _forward = e => Handler?.Invoke(e);
        Register = DoRegister;
    }

    private void DoRegister()
    {
        var node = _ctx.HostNode;
        var scene = _ctx.Scene;
        if (scene is null || node.IsNull || !scene.IsLive(node)) return;
        // Re-target: a kind-change effect re-run (or a node swap) clears the prior install before the new one.
        if (!_registeredNode.IsNull && _registeredNode != node && scene.IsLive(_registeredNode))
            scene.SetGestureHandler(_registeredNode, _kind, null);
        scene.SetGestureHandler(node, _kind, _forward);
        _registeredNode = node;
    }
}

/// <summary>
/// Per-component hook storage (ordered; rules-of-hooks = stable call order). In the signals-first model a component
/// is a reactive computation (its render-effect); <see cref="UseState"/> is a <see cref="Signal{T}"/> whose only
/// subscriber is that render-effect, so a setState re-renders ONLY this component's subtree (granular), and
/// <see cref="UseContext"/> subscribes the render-effect to the provider's signal so a context change propagates to
/// exactly the consumers that read it. High-frequency scalars get <see cref="UseSignal"/> bound straight to a node
/// channel — no re-render at all.
/// </summary>
public sealed partial class RenderContext
{
    private readonly List<HookCell> _cells = new();
    private int _cursor;
    private int _cleanupCellCount;
    private bool _mounted;

    public readonly List<Action> PendingEffects = new();        // UseEffect — after present (phase 12)
    public readonly List<Action> PendingLayoutEffects = new();  // UseLayoutEffect — after layout, before paint (phase 6.5)
    internal Action<RenderContext, bool>? RegisterPendingEffectContext;

    // Injected by the reconciler/host at mount.
    public ReactiveRuntime? Runtime;            // creates signals/memos; schedules this component's render-effect
    /// <summary>Imperatively re-render THIS component (granular — schedules only this component's render-effect). Wired
    /// by the reconciler at mount. For escape-hatch callers (e.g. a Navigator's OnChange); state/context use signals.</summary>
    public Action RequestRerender = static () => { };
    public AnimEngine? Anim;
    public ImageCache? Images;                  // host-injected; backs UseImage / PrefetchImage
    public SceneStore? Scene;                   // reconciler-injected; for measuring nodes (AbsoluteRect) + overlay positioning
    public Action<NodeHandle>? ArmScroll;       // host-injected (→ ScrollIntegrator.Arm): arm a viewport so phase 7 eases Offset→Target (smooth programmatic scroll)
    /// <summary>Host-injected peek: true while ANY viewport is in user scroll (wheel/fling/drag) or inside the post-scroll
    /// hold window. Apps use this to defer heavy per-frame work (e.g. lyrics glow/wipe) so main-content scroll stays smooth.</summary>
    public Func<bool>? PeekMainScrollBusy;
    public NodeHandle HostNode;                 // this component's rendered child (animation hooks target it)
    public NodeHandle AnchorNode;               // this component's anchor in the scene (context resolution walks up from here)
    public Func<NodeHandle, object, Signal<object?>?>? ResolveContextSignal;   // (anchor, channel) → nearest provider signal
    public IReadSignal<int>? ImageEpoch;        // bumped by the host on any image status change → re-renders UseImage consumers
    public Func<Signal<bool>>? GetActiveSig;    // reconciler-injected: get-or-create THIS component's KeepAlive-parked signal (UseIsActive)

    internal void BeginRender() => _cursor = 0;
    internal void EndRender()
    {
        DebugCheckHooksConsumed();   // rules-of-hooks guard (DEBUG only; erased on Release) — see below
        _mounted = true;
        // The "form under construction" thread-local (set by UseForm so same-component UseField calls auto-join) lives
        // only for this render — clear it so it never leaks into the next component the reconciler renders.
        FluentGpu.Forms.FormScope.Building = null;
    }

    // ── Rules-of-hooks guard (engine follow-up to the DetailShell conditional-hook crash) ─────────────────────────────
    // Positional hooks (UseState/UseMemo/UseEffect/UseContextSignal/…) index a fixed _cells list by a per-render cursor,
    // so EVERY render of a mounted component must call the SAME hooks in the SAME order. A conditional hook (inside an
    // if/branch/loop, or after an early return) desyncs the cursor → an opaque IndexOutOfRange / InvalidCast deep in a
    // Use* call (the back-forward crash). These [Conditional("DEBUG")] checks turn that into a clear, named error in
    // DEBUG/CI and are entirely erased from the shipping Release/AOT binary (zero production cost).
    [System.Diagnostics.Conditional("DEBUG")]
    private void DebugCheckHooksConsumed()
    {
        if (_mounted && _cursor != _cells.Count)
            throw new InvalidOperationException(
                $"Rules of hooks violated: a component called {_cursor} positional hook(s) this render but {_cells.Count} on its first render — " +
                "a Use* hook was called conditionally (inside an if/branch/loop) or after an early return. Hoist every hook above any branch/return.");
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void DebugGuardCursor()
    {
        if (_cursor >= _cells.Count)
            throw new InvalidOperationException(
                "Rules of hooks violated: more positional hooks ran this render than on the first render — a Use* hook was called conditionally " +
                "(inside an if/branch/loop) or after an early return. Hoist every hook above any branch/return.");
    }

    /// <summary>Run every pending effect cleanup + dispose owned reactive primitives (component unmount).</summary>
    public void RunAllCleanups()
    {
        PendingEffects.Clear();
        PendingLayoutEffects.Clear();
        if (_cleanupCellCount == 0) return;
        foreach (var cell in _cells)
        {
            if (cell is EffectCell e) e.Cleanup?.Invoke();
            else if (cell is IDisposableCell d) d.DisposeCell();
        }
    }

    private void AddCell(HookCell cell, bool cleanupCapable = false)
    {
        _cells.Add(cell);
        if (cleanupCapable) _cleanupCellCount++;
    }

    private ReactiveRuntime Rt => Runtime ?? throw new InvalidOperationException("RenderContext.Runtime not set (component not mounted by the host).");

    internal sealed class ContextReadSignal<T> : IReadSignal<T>
    {
        private readonly RenderContext _ctx;
        private readonly Context<T> _context;

        public ContextReadSignal(RenderContext ctx, Context<T> context)
        {
            _ctx = ctx;
            _context = context;
        }

        public T Value
        {
            get
            {
                var sig = _ctx.ResolveContextSignal?.Invoke(_ctx.AnchorNode, _context);
                return sig is not null && sig.Value is T tv ? tv : _context.Default;
            }
        }

        public T Peek()
        {
            var sig = _ctx.ResolveContextSignal?.Invoke(_ctx.AnchorNode, _context);
            return sig is not null && sig.Peek() is T tv ? tv : _context.Default;
        }
    }

    public (T Value, Action<T> Set) UseState<T>(T initial)
    {
        SignalCell<T> cell;
        if (!_mounted) { cell = new SignalCell<T>(new Signal<T>(initial)); _cells.Add(cell); }
        else cell = (SignalCell<T>)_cells[_cursor];
        _cursor++;

        var sig = cell.Signal;
        // Reading .Value here (inside the render-effect) subscribes this component; the setter writes the signal,
        // which schedules ONLY this component's render-effect for the next flush (granular, batched, value-eq-gated).
        return (sig.Value, v => sig.Value = v);
    }

    /// <summary>A persistent reactive cell you own (the Solid/Preact <c>signal</c>): read <c>.Value</c> in render to
    /// subscribe, or bind it to a node channel for a render-free update. Survives re-renders.</summary>
    public Signal<T> UseSignal<T>(T initial)
    {
        SignalCell<T> cell;
        if (!_mounted) { cell = new SignalCell<T>(new Signal<T>(initial)); _cells.Add(cell); }
        else cell = (SignalCell<T>)_cells[_cursor];
        _cursor++;
        return cell.Signal;
    }

    /// <summary>A persistent scalar signal for the hot path (slider/scroll/progress). Bind it to a node transform/paint
    /// channel (<c>TransformBind</c>/<c>OpacityBind</c>) for a value→pixels update that skips render/reconcile/layout.</summary>
    public FloatSignal UseFloatSignal(float initial = 0f)
    {
        FloatSignalCell cell;
        if (!_mounted) { cell = new FloatSignalCell(new FloatSignal(initial)); _cells.Add(cell); }
        else cell = (FloatSignalCell)_cells[_cursor];
        _cursor++;
        return cell.Signal;
    }

    /// <summary>A derived reactive value (the Solid <c>createMemo</c>): recomputes from the signals it reads, cached.</summary>
    public Memo<T> UseComputed<T>(Func<T> compute)
    {
        MemoHookCell<T> cell;
        if (!_mounted) { cell = new MemoHookCell<T>(new Memo<T>(Rt, compute)); AddCell(cell, cleanupCapable: true); }
        else cell = (MemoHookCell<T>)_cells[_cursor];
        _cursor++;
        return cell.Memo;
    }

    // ── UseEffect / UseLayoutEffect ──────────────────────────────────────────────────────────────────────────────────
    // DEFAULT (no deps) = AUTO-TRACKED: the body runs with signal-read tracking; any signal it read re-runs it, re-armed
    // EVERY run (a branch that reads a different signal next run re-subscribes to that one). A body that reads no signal
    // runs exactly once. Runs in the passive-effect drain (after paint) — never inline during Flush.
    // EXPLICIT (DepKey deps) = DEPS-GATED (no tracking): runs when the key changes; DepKey.Empty/default = mount-once.
    // The Func<Action?> overloads return a CLEANUP run before each re-run and once at unmount; the Action overloads are
    // the no-cleanup convenience. NOTE (overload resolution): an expression-bodied lambda that RETURNS an Action binds to
    // the Func<Action?> overload and its return is treated as the cleanup — write a block body `() => { X(); }` for a
    // fire-only effect.

    public void UseEffect(Action effect) { var e = GetAutoEffect(layout: false, out bool mount); e.ActionBody = effect; e.FuncBody = null; if (mount) e.Prime(); }
    public void UseEffect(Func<Action?> effect) { var e = GetAutoEffect(layout: false, out bool mount); e.FuncBody = effect; e.ActionBody = null; if (mount) e.Prime(); }
    /// <summary>Like <see cref="UseEffect(Action)"/> but runs after layout, before paint (Bounds are valid). Phase 6.5.</summary>
    public void UseLayoutEffect(Action effect) { var e = GetAutoEffect(layout: true, out bool mount); e.ActionBody = effect; e.FuncBody = null; if (mount) e.Prime(); }
    /// <inheritdoc cref="UseLayoutEffect(Action)"/>
    public void UseLayoutEffect(Func<Action?> effect) { var e = GetAutoEffect(layout: true, out bool mount); e.FuncBody = effect; e.ActionBody = null; if (mount) e.Prime(); }

    /// <summary>Deps-gated effect (no tracking): runs at mount and whenever <paramref name="deps"/> changes. Pass
    /// <see cref="DepKey.Empty"/> (the default) for mount-once. Scalars/tuples convert implicitly (<c>UseEffect(fn, count)</c>).</summary>
    public void UseEffect(Action effect, DepKey deps) => EffectKey(effect, deps, PendingEffects);
    /// <inheritdoc cref="UseEffect(Action, DepKey)"/>
    public void UseEffect(Func<Action?> effect, DepKey deps) => EffectKeyC(effect, deps, PendingEffects);
    /// <inheritdoc cref="UseEffect(Action, DepKey)"/>
    public void UseLayoutEffect(Action effect, DepKey deps) => EffectKey(effect, deps, PendingLayoutEffects);
    /// <inheritdoc cref="UseEffect(Action, DepKey)"/>
    public void UseLayoutEffect(Func<Action?> effect, DepKey deps) => EffectKeyC(effect, deps, PendingLayoutEffects);

    /// <summary>Mount a signal-tracked effect owned by this component that runs EAGERLY (the body runs synchronously now,
    /// then re-runs inline during <see cref="ReactiveRuntime.Flush"/> when a read signal changes). Distinct from the
    /// passive auto-tracked <see cref="UseEffect(Action)"/> — use this adapter form ONLY to bridge a hot signal into a
    /// coarser retained signal where eager timing is required; prefer <see cref="UseEffect(Action)"/> otherwise.</summary>
    public void UseSignalEffect(Action effect)
    {
        SignalEffectCell cell;
        if (!_mounted) { cell = new SignalEffectCell(new Effect(Rt, effect)); AddCell(cell, cleanupCapable: true); }
        else cell = (SignalEffectCell)_cells[_cursor];
        _cursor++;
    }

    private AutoEffect GetAutoEffect(bool layout, out bool mount)
    {
        mount = !_mounted;
        AutoEffectCell cell;
        if (mount) { cell = new AutoEffectCell { Effect = new AutoEffect(Rt, this, layout) }; AddCell(cell, cleanupCapable: true); }
        else { DebugGuardCursor(); cell = (AutoEffectCell)_cells[_cursor]; }
        _cursor++;
        return cell.Effect;
    }

    private void EffectKey(Action effect, DepKey deps, List<Action> target)
    {
        EffectCell cell;
        if (!_mounted) { cell = new EffectCell(); AddCell(cell, cleanupCapable: true); }
        else { DebugGuardCursor(); cell = (EffectCell)_cells[_cursor]; }
        _cursor++;

        if (!cell.HasKey || cell.Key != deps)
        {
            cell.Key = deps; cell.HasKey = true;
            EnqueueEffect(target, () => { cell.Cleanup?.Invoke(); effect(); });
        }
    }

    private void EffectKeyC(Func<Action?> effect, DepKey deps, List<Action> target)
    {
        EffectCell cell;
        if (!_mounted) { cell = new EffectCell(); AddCell(cell, cleanupCapable: true); }
        else { DebugGuardCursor(); cell = (EffectCell)_cells[_cursor]; }
        _cursor++;

        if (!cell.HasKey || cell.Key != deps)
        {
            cell.Key = deps; cell.HasKey = true;
            EnqueueEffect(target, () => { cell.Cleanup?.Invoke(); cell.Cleanup = effect(); });
        }
    }

    private void EnqueueEffect(List<Action> target, Action action)
    {
        if (target.Count == 0)
            RegisterPendingEffectContext?.Invoke(this, ReferenceEquals(target, PendingLayoutEffects));
        target.Add(action);
    }

    // Enqueue an auto-tracked effect's tracked run into the passive (or layout) drain, registering this context with the
    // host on the 0→1 transition. Called at mount (Prime) and on each scheduled re-run (AutoEffect.RunStale).
    internal void EnqueueEffectRun(Action run, bool layout) => EnqueueEffect(layout ? PendingLayoutEffects : PendingEffects, run);

    /// <summary>State driven by a reducer (the React <c>useReducer</c>); dispatches fold over the latest committed state.</summary>
    public (TState State, Action<TAction> Dispatch) UseReducer<TState, TAction>(Func<TState, TAction, TState> reducer, TState initial)
    {
        SignalCell<TState> cell;
        if (!_mounted) { cell = new SignalCell<TState>(new Signal<TState>(initial)); _cells.Add(cell); }
        else cell = (SignalCell<TState>)_cells[_cursor];
        _cursor++;

        var sig = cell.Signal; var r = reducer;
        Action<TAction> dispatch = action => sig.Value = r(sig.Peek(), action);
        return (sig.Value, dispatch);
    }

    /// <summary>Memoize an expensive value; recomputes only when <paramref name="deps"/> changes (deps-gated, explicit —
    /// there is no auto-tracked memo). Scalars/tuples convert implicitly; <see cref="DepKey.Empty"/>/default = compute
    /// once at mount.</summary>
    public T UseMemo<T>(Func<T> factory, DepKey deps)
    {
        MemoCell<T> cell;
        if (!_mounted)
        {
            cell = new MemoCell<T> { Value = factory(), Key = deps, HasKey = true };
            _cells.Add(cell);
        }
        else
        {
            DebugGuardCursor(); cell = (MemoCell<T>)_cells[_cursor];
            if (!cell.HasKey || cell.Key != deps) { cell.Value = factory(); cell.Key = deps; cell.HasKey = true; }
        }
        _cursor++;
        return cell.Value;
    }

    // Last-resolved provider signal per context channel. Reused when a later resolve returns null because this
    // component's subtree is temporarily DETACHED — e.g. parked by Flow.KeepAlive (a tab/page cache). A parked subtree
    // re-attaches to the SAME provider chain, so the providers above it don't change while parked; without this, a
    // parked re-render (triggered by a signal it still subscribes to, like a store's Version) would resolve to the
    // context Default and overwrite its cached content with the empty fallback — the "History tab goes blank after
    // switching tabs" bug. The normal (attached) path is unchanged: resolution succeeds and refreshes the cache.
    Dictionary<object, Signal<object?>>? _ctxResolveCache;

    /// <summary>Read the nearest provided value of <paramref name="context"/> (or its default), subscribing this
    /// component to that provider's signal so a change re-renders exactly here (no prop drilling, no full-tree walk).</summary>
    public T UseContext<T>(Context<T> context)
    {
        var sig = ResolveContextSignal?.Invoke(AnchorNode, context);
        if (sig is not null) (_ctxResolveCache ??= new())[context] = sig;   // attached: refresh the last-resolved provider
        else _ctxResolveCache?.TryGetValue(context, out sig);              // detached/parked: reuse it (providers above are unchanged)
        if (sig is not null && sig.Value is T tv) return tv;   // sig.Value subscribes the render-effect
        return context.Default;
    }

    /// <summary>Return the nearest context as a read signal without subscribing this render. Reading the returned
    /// signal's <c>.Value</c> subscribes the current reactive computation at the call site.</summary>
    public IReadSignal<T> UseContextSignal<T>(Context<T> context)
    {
        ContextSignalCell<T> cell;
        if (!_mounted) { cell = new ContextSignalCell<T>(this, context); _cells.Add(cell); }
        else { DebugGuardCursor(); cell = (ContextSignalCell<T>)_cells[_cursor]; }
        _cursor++;
        return cell.Signal;
    }

    // ── Activation lifecycle (parked-by-KeepAlive OR window-minimized) ───────────────────────────────────────────────

    /// <summary>This component's activation state as a reactive signal: <c>false</c> when its page is parked by
    /// <c>Flow.KeepAlive</c> (a backgrounded tab) OR the window is minimized / app-suspended, <c>true</c> otherwise.
    /// Read <c>.Value</c> in render to subscribe (e.g. gate a live-only affordance); for transition callbacks use
    /// <see cref="UseActivation"/>. A value-gated <see cref="Memo{T}"/> built once at mount (zero steady-state
    /// allocation); a fresh component is active. Folds the per-component KeepAlive signal with the ambient
    /// <c>Activation.IsActive</c> window-visibility signal — either being false makes the component inactive.</summary>
    public IReadSignal<bool> UseIsActive()
    {
        MemoHookCell<bool> cell;
        if (!_mounted)
        {
            var componentActive = GetActiveSig;   // get-or-create this component's parked signal on first compute (lazy)
            var meta = ResolveContextSignal?.Invoke(AnchorNode, Activation.IsActive);
            var windowVisible = meta?.Peek() as IReadSignal<bool>;   // ambient visibility signal (the channel value never re-publishes)
            var memo = new Memo<bool>(Rt, () =>
                (componentActive is null || componentActive().Value) && (windowVisible is null || windowVisible.Value));
            cell = new MemoHookCell<bool>(memo);
            AddCell(cell, cleanupCapable: true);
        }
        else cell = (MemoHookCell<bool>)_cells[_cursor];
        _cursor++;
        return cell.Memo;
    }

    /// <summary>Run <paramref name="onDeactivated"/> when this component goes inactive (its page parked by
    /// <c>Flow.KeepAlive</c> OR the window minimized / app-suspended) and <paramref name="onActivated"/> when it comes
    /// back — the notify-only lifecycle for pausing/resuming the component's OWN background work (a poll, a periodic
    /// refresh, an OS subscription). TRANSITIONS ONLY: neither fires at mount (a fresh component is active — start work
    /// in <see cref="UseEffect"/>), and neither fires on unmount (use the <see cref="UseEffect"/> cleanup). Backed by a
    /// STANDALONE effect over <see cref="UseIsActive"/> — NOT the render-effect, which is suspended while parked, so the
    /// notification must live in an independent computation that keeps observing while parked. The latest callbacks are
    /// read from a stable cell (a fresh lambda each render does not re-subscribe) and invoked under
    /// <see cref="Reactive.Untrack"/> (a callback's signal reads do not subscribe the effect).</summary>
    public void UseActivation(Action? onActivated = null, Action? onDeactivated = null)
    {
        var cb = UseRef<(Action? On, Action? Off)>(default);
        cb.Value = (onActivated, onDeactivated);     // always route to the latest closures
        var active = UseIsActive();
        var prev = UseRef(true);
        var started = UseRef(false);

        SignalEffectCell cell;
        if (!_mounted)
        {
            var effect = new Effect(Rt, () =>
            {
                bool now = active.Value;   // subscribe this effect to the activation memo
                if (!started.Value) { started.Value = true; prev.Value = now; return; }   // capture initial state — no fire at mount
                if (now == prev.Value) return;   // value-gated memo already collapses churn; this guards re-fires
                prev.Value = now;
                var (on, off) = cb.Value;
                Reactive.Untrack(() => { if (now) on?.Invoke(); else off?.Invoke(); });
            });
            cell = new SignalEffectCell(effect);
            AddCell(cell, cleanupCapable: true);
        }
        else cell = (SignalEffectCell)_cells[_cursor];
        _cursor++;
    }

    /// <summary>The host's cross-thread UI-thread poster (<see cref="HostDispatch.Post"/>): <c>post(action)</c> runs
    /// <c>action</c> on the UI thread on the next frame, safe to call from ANY thread (OS callback / worker / agile COM).
    /// This is the engine-blessed marshal for off-thread data — use it instead of <c>UseContext(FrameClock.Tick)</c> +
    /// a per-frame drain, which forces a full re-render every frame just to poll a queue. Reading it subscribes this
    /// component to the (never-changing) host poster signal, so it costs no extra re-renders. When no host published a
    /// poster (headless / test), the fallback runs the action inline on the calling thread (the synchronous harness does
    /// not hop threads), so call sites need no null-check.</summary>
    public Action<Action> UsePost() => UseContext(HostDispatch.Post) ?? (static a => a());

    /// <summary>Reactive snapshot of the live drag — both an in-app <c>DragSource</c> drag and an OS file drag surface
    /// here. The component re-renders on drag begin/move/end (the host bumps a drag epoch while a drag is live), so a
    /// <c>DragPreviewLayer</c> can render a custom floating preview that follows the cursor at <see cref="DragState.Position"/>.
    /// Idle ⇒ <see cref="DragState.Active"/> false. Cheap: only re-renders the subscribing subtree while a drag is live.</summary>
    public DragState UseDragState()
    {
        var hooks = UseContext(InputHooks.Current);
        if (hooks?.DragEpoch is { } epoch) _ = epoch.Value;   // subscribe → re-render on each bump
        return hooks?.GetDragState?.Invoke() ?? default;
    }

    /// <summary>
    /// A value that eases toward <paramref name="target"/> one step per render whenever the target changes. It steps ONLY
    /// when this component re-renders for some other reason — for motion prefer the retained engine path
    /// (<see cref="UseSpring"/>/<see cref="UseTransition"/>), which rides the compositor frame with no re-render.
    /// </summary>
    public float UseAnimatedValue(float target, float durationMs = 180f, Easing easing = Easing.EaseInOut)
    {
        AnimValueCell cell;
        if (!_mounted) { cell = new AnimValueCell { Current = target, From = target, Target = target }; _cells.Add(cell); }
        else cell = (AnimValueCell)_cells[_cursor];
        _cursor++;

        if (cell.Target != target) { cell.From = cell.Current; cell.Target = target; cell.Elapsed = 0f; }
        if (cell.Current != cell.Target)
        {
            cell.Elapsed += 16f;
            float t = Math.Clamp(cell.Elapsed / MathF.Max(durationMs, 1f), 0f, 1f);
            float e = Easings.Ease(easing, t);
            cell.Current = cell.From + (cell.Target - cell.From) * e;
            if (t >= 1f) cell.Current = cell.Target;
        }
        return cell.Current;
    }

    // ── Declarative animation (seed/retarget engine tracks on this component's node; composited, no re-render/frame) ──
    // DepKey-gated only — the retained-anim hooks re-seed when their key changes (DepKey.Empty = seed once at mount).

    public void UseSpring(AnimChannel channel, float to, SpringParams spring, DepKey deps)
        => UseLayoutEffect(() => { if (Anim is { } a && !HostNode.IsNull) a.Spring(HostNode, channel, to, spring); }, deps);
    public void UseTransition(AnimChannel channel, float from, float to, float durationMs, Easing easing, DepKey deps)
        => UseLayoutEffect(() => { if (Anim is { } a && !HostNode.IsNull) a.Animate(HostNode, channel, from, to, durationMs, easing); }, deps);
    public void UseKeyframes(AnimChannel channel, Keyframe[] keys, float durationMs, bool loop, DepKey deps)
        => UseLayoutEffect(() => { if (Anim is { } a && !HostNode.IsNull) a.Keyframes(HostNode, channel, keys, durationMs, loop); }, deps);
    public void UseDrivenAnimation(AnimChannel channel, Keyframe[] keys, Func<float> source, float min, float max, DepKey deps)
        => UseLayoutEffect(() => { if (Anim is { } a && !HostNode.IsNull) a.Drive(HostNode, channel, keys, a.Clocks.Register(source), min, max); }, deps);

    /// <summary>Declare a gesture handler on this component's node (input-a11y.md §13 <c>UseGesture</c>). Config-only:
    /// enrolls a gesture-arena member on <see cref="HostNode"/> (via the <c>SceneStore</c> gesture column — the
    /// Hooks⇄Input seam) and routes the arena winner's event to <paramref name="handler"/>. No render output, no
    /// re-render. Zero per-render allocation after mount: the latest handler is stashed in a persistent cell (a field
    /// write each render) and a STABLE forwarder is installed ONCE via a phase-6.5 layout-effect keyed by the kind (so
    /// it only (re)registers at mount / kind-change, reading the valid mounted node). The forwarder dispatches to the
    /// current cell handler, so a fresh lambda each render needs no re-registration. On unmount the freed node drops the
    /// column (SceneStore); a kind-change re-target clears the prior install.</summary>
    public void UseGesture(GestureType kind, Action<GestureEventArgs> handler)
    {
        // Persistent per-call cell: holds the latest user handler + the once-allocated stable forwarder/effect/cleanup
        // (so nothing here allocates on a steady re-render — only the handler field is overwritten).
        var cell = UseRef<GestureHookState?>(null);
        var st = cell.Value ??= new GestureHookState(this, kind);
        st.Handler = handler;   // always route to the latest closure (no re-registration needed)
        // Mount-once registration (re-runs only if the kind changes): install the stable forwarder on the node. The
        // cached effect/cleanup delegates make this a no-alloc layout-effect on a steady render (the effect closure is
        // the SAME instance every render, so EffectImpl adds it only when deps change). HostNode is valid at 6.5.
        UseLayoutEffect(st.Register, st.KindDep);
    }

    /// <summary>Bind an async image and observe its load state (media-pipeline.md §5). Subscribes this component to the
    /// host's image-status epoch so a ready/failed transition re-renders just this component.</summary>
    public ImageBinding UseImage(string src, int decodePx, ImagePriority priority = ImagePriority.Visible, string? blurHash = null)
        => UseImage(src, decodePx, decodePx, priority, blurHash);

    /// <summary>As <see cref="UseImage(string,int,ImagePriority,string)"/> but with a non-square decode target, so an
    /// observer can share the EXACT cache handle of a non-square displayed image (key is <c>(src, decodeW, decodeH)</c>)
    /// instead of forking a second decode.</summary>
    public ImageBinding UseImage(string src, int decodeW, int decodeH, ImagePriority priority = ImagePriority.Visible, string? blurHash = null)
    {
        _ = ImageEpoch?.Value;   // subscribe: any image status change re-renders this UseImage consumer
        if (Images is null || string.IsNullOrEmpty(src)) return default;
        var h = Images.Request(src, decodeW, decodeH, priority, blurHash);
        return new ImageBinding(h, Images.StateOf(h), Images.FailureOf(h), Images.AttemptsOf(h));
    }

    public void PrefetchImage(string src, int decodePx)
    {
        if (Images is not null && !string.IsNullOrEmpty(src)) Images.Prefetch(src, decodePx, decodePx);
    }

    /// <summary>Acquire a composited video surface for this component (the video-compositing spine). Returns a
    /// <see cref="FluentGpu.Media.VideoBinding"/> a media player writes placement + a bound DirectComposition handle
    /// through; the host composites it z-below the UI (revealed through a transparent hole this component draws at the
    /// same rect) and tears it down automatically on unmount. Invalid (a safe no-op binding) when video compositing is
    /// unavailable (headless / non-composited window). Zero per-render work after mount.</summary>
    public FluentGpu.Media.VideoBinding UseVideoSurface()
    {
        var registry = UseContext(VideoCompositor.Current);
        VideoSurfaceCell cell;
        if (!_mounted)
        {
            cell = new VideoSurfaceCell { Registry = registry, Token = registry?.Acquire() ?? 0 };
            AddCell(cell, cleanupCapable: true);
        }
        else cell = (VideoSurfaceCell)_cells[_cursor];
        _cursor++;
        return new FluentGpu.Media.VideoBinding(cell.Registry, cell.Token);
    }

    /// <summary>Create a component-lifetime-owned disposable ONCE at mount and dispose it automatically on unmount.
    /// Returns the same instance every render (or null if the factory returned null). For a native/OS resource whose
    /// lifetime should track the component (e.g. a media player owning a background sidecar).</summary>
    public T? UseDisposable<T>(Func<T?> factory) where T : class, IDisposable
    {
        DisposableCell<T> cell;
        if (!_mounted) { cell = new DisposableCell<T> { Value = factory() }; AddCell(cell, cleanupCapable: true); }
        else cell = (DisposableCell<T>)_cells[_cursor];
        _cursor++;
        return cell.Value;
    }

    // ── Skeleton-loading: the async-value spine + the resource lifecycle ─────────────────────────────────────────────

    /// <summary>A persistent per-field async value (Pending|Ready|Failed + value) — the skeleton-loading spine. Survives
    /// re-renders; flip it with <c>SetReady</c>/<c>SetFailed</c> (typically from <see cref="UseAsyncResource"/> or an
    /// off-thread <see cref="UsePost"/>). Use this for a field whose load you drive yourself (or a partial-known seed).</summary>
    public Loadable<T> UseLoadable<T>(Loadable<T>? initial = null)
    {
        LoadableCell<T> cell;
        if (!_mounted) { cell = new LoadableCell<T> { Loadable = initial ?? Loadable<T>.Pending() }; _cells.Add(cell); }
        else cell = (LoadableCell<T>)_cells[_cursor];
        _cursor++;
        return cell.Loadable;
    }

    /// <summary>Kick an async <paramref name="loader"/> ONCE at mount and return its <see cref="Loadable{T}"/> (starts
    /// Pending). Completion is marshalled to the UI thread via <see cref="UsePost"/> so <c>SetReady</c>/<c>SetFailed</c>
    /// land in the batched flush; the <see cref="CancellationToken"/> is cancelled on unmount (the back-nav-mid-load
    /// case — the cell is <see cref="IDisposableCell"/>) and a late post is dropped by the cancelled-token guard.
    /// Modeled on the <see cref="UseImage"/> lifecycle. <paramref name="seed"/> is the placeholder value while pending
    /// (e.g. an empty array so the region's content lambda is never invoked against null).</summary>
    public Loadable<T> UseAsyncResource<T>(Func<CancellationToken, Task<T>> loader, T seed = default!)
    {
        var post = UsePost();   // UsePost consumes no hook cursor, so calling it here does not shift hook order
        AsyncResourceCell<T> cell;
        if (!_mounted)
        {
            cell = new AsyncResourceCell<T> { Loadable = Loadable<T>.Pending(seed), Cts = new CancellationTokenSource() };
            AddCell(cell, cleanupCapable: true);
        }
        else cell = (AsyncResourceCell<T>)_cells[_cursor];
        _cursor++;

        if (!cell.Started) { cell.Started = true; BeginAsyncLoad(cell, loader, post); }
        return cell.Loadable;
    }

    /// <summary>As <see cref="UseAsyncResource{T}(Func{CancellationToken, Task{T}}, T)"/> but RE-FIRES when
    /// <paramref name="deps"/> change (value-equality, like <see cref="UseMemo"/>): the in-flight run is cancelled, the
    /// loadable resets to <c>Pending(seed)</c>, and the loader restarts. Use for a resource keyed on a reactive input —
    /// e.g. a detail page whose album id changes while the component instance is REUSED across navigation, so the data
    /// follows the id with no remount. Re-evaluate <paramref name="seed"/> per render (so the reset shows the NEW key's
    /// placeholder/preview, not the prior one's) and pass a loader that closes over the current key.</summary>
    public Loadable<T> UseAsyncResource<T>(Func<CancellationToken, Task<T>> loader, T seed, DepKey deps)
    {
        var post = UsePost();
        AsyncResourceCell<T> cell;
        if (!_mounted)
        {
            cell = new AsyncResourceCell<T> { Loadable = Loadable<T>.Pending(seed), Cts = new CancellationTokenSource(), Deps = deps, HasDeps = true };
            AddCell(cell, cleanupCapable: true);
            _cursor++;
            BeginAsyncLoad(cell, loader, post);
            return cell.Loadable;
        }
        cell = (AsyncResourceCell<T>)_cells[_cursor];
        _cursor++;
        if (!cell.HasDeps || cell.Deps != deps)
        {
            cell.Deps = deps; cell.HasDeps = true;
            try { cell.Cts.Cancel(); } catch { /* already disposed */ }
            cell.Cts.Dispose();
            cell.Cts = new CancellationTokenSource();
            cell.Loadable.SetPending(seed);   // new key → its own preview/skeleton while the fresh load runs (no stale flash)
            BeginAsyncLoad(cell, loader, post);
        }
        return cell.Loadable;
    }

    // The loader kickoff shared by both UseAsyncResource overloads. Fires loader on cell.Cts's CURRENT token (pulled
    // here, AFTER any restart swapped cell.Cts, so it tracks THIS run) and marshals the result to the UI thread via post,
    // cancelled-token-guarded both before and inside the post so an unmount / re-key mid-load drops the late completion.
    private static void BeginAsyncLoad<T>(AsyncResourceCell<T> cell, Func<CancellationToken, Task<T>> loader, Action<Action> post)
    {
        var loadable = cell.Loadable;
        var token = cell.Cts.Token;
        _ = Task.Run(Run);

        async Task Run()
        {
            try
            {
                token.ThrowIfCancellationRequested();
                T v = await loader(token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                    post(() => { if (!token.IsCancellationRequested) loadable.SetReady(v); });
            }
            catch (OperationCanceledException) { /* unmounted / re-keyed mid-load — the CTS was cancelled */ }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    post(() => { if (!token.IsCancellationRequested) loadable.SetFailed(ex); });
            }
        }
    }

    /// <summary>A persistent fire-on-demand <see cref="AsyncCommand"/> — run an async action from an event (a button
    /// command), render a spinner / disable off <c>IsRunning</c>, guard re-entry, and optionally cancel. Completion is
    /// marshalled to the UI thread via <see cref="UsePost"/>. By default an in-flight run is NOT cancelled on unmount
    /// (a started command should complete); pass <paramref name="cancelOnUnmount"/> true to abort it.</summary>
    public AsyncCommand UseAsyncCommand(bool cancelOnUnmount = false)
    {
        var post = UsePost();   // UseContext consumes no hook cursor (calling it here does not shift hook order)
        AsyncCommandCell cell;
        if (!_mounted)
        {
            cell = new AsyncCommandCell { Command = new AsyncCommand(post), CancelOnUnmount = cancelOnUnmount };
            AddCell(cell, cleanupCapable: true);
        }
        else cell = (AsyncCommandCell)_cells[_cursor];
        _cursor++;
        return cell.Command;
    }

    /// <summary>A persistent KEYED <see cref="AsyncCommandSet{TKey}"/> — per-item commands (e.g. per-row play/like) with
    /// a reactive in-progress state per key. Same lifecycle as <see cref="UseAsyncCommand"/>.</summary>
    public AsyncCommandSet<TKey> UseAsyncCommands<TKey>(bool cancelOnUnmount = false) where TKey : notnull
    {
        var post = UsePost();
        AsyncCommandSetCell<TKey> cell;
        if (!_mounted)
        {
            cell = new AsyncCommandSetCell<TKey> { Commands = new AsyncCommandSet<TKey>(post), CancelOnUnmount = cancelOnUnmount };
            AddCell(cell, cleanupCapable: true);
        }
        else cell = (AsyncCommandSetCell<TKey>)_cells[_cursor];
        _cursor++;
        return cell.Commands;
    }

    // ── Localization (i18n) hooks ────────────────────────────────────────────────────────────────────────────────────
    // Bind a localized string into a text node WITHOUT re-rendering: L/Lf return a Prop<string> whose thunk reads the
    // localization culture-epoch (inside Localization.Get/Format), so the engine's Prop bind effect re-resolves ONLY
    // that text node when the culture changes (the binding-not-re-render path). UseLocale is the re-render-on-change
    // form for code that must branch on the active culture (e.g. a language picker's selected item).

    /// <summary>A localized string bound for a text node: <c>new TextEl("") { Text = ctx.L("app.title") }</c>. Returns a
    /// <see cref="Prop{T}"/> thunk that resolves <paramref name="key"/> through <see cref="FluentGpu.Localization.Localization.Get"/>;
    /// a culture switch re-resolves just this node (no component re-render). Missing key renders visibly as <c>[key]</c>.</summary>
    public Prop<string> L(string key) => Prop.Of(() => FluentGpu.Localization.Localization.Get(key));

    /// <summary>A localized + formatted string bound for a text node (named placeholders / ICU plural-select):
    /// <c>Text = ctx.Lf("player.added", ("name", track))</c>. Returns a <see cref="Prop{T}"/> thunk over
    /// <see cref="FluentGpu.Localization.Localization.Format(string, ValueTuple{string, object}[])"/>; re-resolves on
    /// culture change with no re-render. The <paramref name="args"/> are captured by the thunk.</summary>
    public Prop<string> Lf(string key, params (string Name, object Value)[] args)
        => Prop.Of(() => FluentGpu.Localization.Localization.Format(key, args));

    /// <summary>The re-rendering culture hook: returns the active culture name and a setter, subscribing THIS component
    /// to the culture epoch so it re-renders on a culture change (use when render output branches on the culture — a
    /// language picker's selected index, an RTL flip — rather than just displaying a localized string, which should use
    /// <see cref="L"/>/<see cref="Lf"/> to avoid the re-render).</summary>
    public (string Culture, Action<string> SetCulture) UseLocale()
    {
        _ = FluentGpu.Localization.Localization.CultureEpoch.Value;   // subscribe the render-effect to culture changes
        return (FluentGpu.Localization.Localization.CurrentCulture, FluentGpu.Localization.Localization.SetCulture);
    }

    /// <summary>A stable mutable box that survives re-renders without triggering them.</summary>
    public Ref<T> UseRef<T>(T initial)
    {
        RefHolderCell cell;
        if (!_mounted) { cell = new RefHolderCell { Ref = new Ref<T>(initial) }; _cells.Add(cell); }
        else { cell = (RefHolderCell)_cells[_cursor]; }
        _cursor++;
        return (Ref<T>)cell.Ref!;
    }
}
