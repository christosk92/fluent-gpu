using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Hooks;

internal abstract class HookCell { }

/// <summary>The call-site identity of a hook cell (the WS1 keyed-substrate key): the hashed <c>[CallerFilePath]</c>,
/// the <c>[CallerLineNumber]</c>, and a per-render ORDINAL (the Nth time this exact source line is hit in one render —
/// a hook in a loop gets ordinals 0..N-1, reset each render). Value-typed (record struct ⇒ non-boxing
/// <c>IEquatable</c>) so the per-render dictionary lookup that resolves a hook to its cell never allocates.</summary>
internal readonly record struct HookKey(ulong FileHash, int Line, int Ordinal);

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

/// <summary>Tuning for <see cref="RenderContext.UseResource{T}"/>. <see cref="StaleTimeMs"/> = how long a freshly-loaded
/// value is considered fresh (0 = stale immediately on Ready); <see cref="KeepPreviousData"/> = on a deps change, keep
/// showing the previous <c>Ready</c> value while the new identity loads (instead of resetting to <c>Pending(seed)</c>).</summary>
public sealed record ResourceOptions
{
    public float StaleTimeMs { get; init; }
    public bool KeepPreviousData { get; init; }
    public static ResourceOptions Default { get; } = new();
}

internal interface IResourceControl<T>
{
    Loadable<T> Loadable { get; }
    IReadSignal<bool> IsFetching { get; }
    IReadSignal<bool> IsStale { get; }
    Exception? LastError { get; }
    void Refresh();
    void Mutate(T optimistic, bool refresh);
}

/// <summary>
/// A stale-while-revalidate async resource (returned by <see cref="RenderContext.UseResource{T}"/>). The
/// <see cref="Loadable"/> is the Pending/Ready/Failed spine (unchanged — <c>Skel.Region</c> consumers bind it directly);
/// <see cref="IsFetching"/> is any load in flight; <see cref="IsStale"/> flips once the data is older than the configured
/// stale time; <see cref="Refresh"/> revalidates keeping the current data visible; <see cref="Mutate"/> writes an
/// optimistic value immediately and (by default) revalidates. Every load is epoch-stamped, so an older/slower fetch can
/// never overwrite a newer one.
/// </summary>
public readonly struct Resource<T>
{
    private readonly IResourceControl<T>? _c;
    internal Resource(IResourceControl<T> c) => _c = c;

    /// <summary>The Pending/Ready/Failed value spine — bind straight into <c>Skel.Region</c> / a leaf's <c>.Bind()</c>.</summary>
    public Loadable<T> Loadable => _c!.Loadable;
    /// <summary>True while any load (initial / deps-change / refresh / mutate-revalidate) is in flight.</summary>
    public IReadSignal<bool> IsFetching => _c!.IsFetching;
    /// <summary>True once the current data is older than <see cref="ResourceOptions.StaleTimeMs"/> (immediately when 0).</summary>
    public IReadSignal<bool> IsStale => _c!.IsStale;
    /// <summary>The last load failure (kept even when a refresh failure leaves the prior <c>Ready</c> value visible); null on success.</summary>
    public Exception? LastError => _c?.LastError;
    /// <summary>Re-run the loader keeping the current <c>Ready</c> value visible while it loads (stale-while-revalidate);
    /// a refresh failure keeps the old value + sets <see cref="LastError"/>.</summary>
    public void Refresh() => _c?.Refresh();
    /// <summary>Write <paramref name="optimistic"/> as <c>Ready</c> immediately, then (when <paramref name="refresh"/>)
    /// revalidate. Cancels any in-flight load first so a stale completion can't clobber the optimistic value.</summary>
    public void Mutate(T optimistic, bool refresh = true) => _c?.Mutate(optimistic, refresh);
}

/// <summary>Backs <see cref="RenderContext.UseResource{T}"/> — the SWR resource controller: the loadable spine + fetch
/// state signals + the epoch-stamped load pipeline. Cancelled + epoch-bumped on unmount (IDisposableCell ⇒ RunAllCleanups)
/// so a back-nav mid-load aborts and any late completion is dropped.</summary>
internal sealed class ResourceCell<T> : HookCell, IDisposableCell, IResourceControl<T>
{
    public Loadable<T> Loadable = null!;
    public readonly Signal<bool> IsFetchingSig = new(true);   // the initial mount load is in flight from construction
    public readonly Signal<bool> IsStaleSig = new(false);
    public CancellationTokenSource Cts = null!;
    public long Epoch;          // monotonic; a completion whose epoch != Epoch is DROPPED (older slower fetch can't win)
    public long StaleGen;       // generation guard for the stale timer (a re-arm / cancel bumps it)
    public DepKey Deps;
    public bool HasDeps;
    public ResourceOptions Options = ResourceOptions.Default;
    public T Seed = default!;
    public Func<CancellationToken, Task<T>> Loader = null!;   // latest closure, re-pointed each render
    public Action<Action> Post = null!;                       // UI-thread marshal for completions
    public FluentGpu.Hosting.HostTimerQueue? Queue;           // drives the stale-time flip (UI cadence; NOT the media clock)
    public Exception? LastError { get; private set; }
    private readonly Action<long> _staleFire;

    public ResourceCell() => _staleFire = OnStaleFire;

    Loadable<T> IResourceControl<T>.Loadable => Loadable;
    IReadSignal<bool> IResourceControl<T>.IsFetching => IsFetchingSig;
    IReadSignal<bool> IResourceControl<T>.IsStale => IsStaleSig;

    private void OnStaleFire(long g) { if (g == StaleGen) IsStaleSig.Value = true; }

    // Arm the stale-time countdown from a fresh Ready. staleTime<=0 ⇒ stale immediately; else a one-shot on the queue.
    private void ArmStale()
    {
        StaleGen++;
        IsStaleSig.Value = false;
        if (Options.StaleTimeMs <= 0f) { IsStaleSig.Value = true; return; }
        Queue?.Schedule(Queue.NowMs + Options.StaleTimeMs, StaleGen, _staleFire);
    }

    // Bump the epoch + swap the CTS (cancels any in-flight load). Returns the new epoch the completion must match.
    private long Advance()
    {
        try { Cts?.Cancel(); } catch { /* already disposed */ }
        Cts?.Dispose();
        Cts = new CancellationTokenSource();
        return ++Epoch;
    }

    // Fire the loader on a captured epoch/token. The result is ALWAYS posted; Settle's epoch guard is the sole authority
    // for whether it lands (so a completion enqueued before a supersession is dropped when it drains late).
    private void Launch(long epoch)
    {
        IsFetchingSig.Value = true;
        var token = Cts.Token;
        var loader = Loader;
        var post = Post;
        _ = Task.Run(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                T v = await loader(token).ConfigureAwait(false);
                post(() => Settle(epoch, v, null));
            }
            catch (OperationCanceledException) { /* cancelled — a fresher load owns the epoch */ }
            catch (Exception ex) { post(() => Settle(epoch, default!, ex)); }
        });
    }

    // A completion lands on the UI thread. EPOCH ORDERING (the non-negotiable): a completion whose epoch is not current
    // is dropped, so an older/slower fetch never overwrites a newer one.
    private void Settle(long epoch, T value, Exception? error)
    {
        if (epoch != Epoch) return;
        IsFetchingSig.Value = false;
        if (error is null)
        {
            LastError = null;
            Loadable.SetReady(value);
            ArmStale();
        }
        else
        {
            LastError = error;
            // A refresh/revalidation failure keeps the prior Ready value (stale-while-revalidate); only surface Failed
            // when there is no prior data to keep.
            if (!Loadable.IsReady) Loadable.SetFailed(error);
        }
    }

    /// <summary>Initial / deps-change load. <paramref name="keepPrevious"/> keeps a prior <c>Ready</c> value visible
    /// (KeepPreviousData); otherwise resets to <c>Pending(seed)</c>.</summary>
    public void Reload(bool keepPrevious)
    {
        long epoch = Advance();
        StaleGen++;
        IsStaleSig.Value = false;
        if (!keepPrevious || !Loadable.IsReady) Loadable.SetPending(Seed);
        Launch(epoch);
    }

    public void Refresh()
    {
        long epoch = Advance();
        StaleGen++;
        IsStaleSig.Value = false;
        // Keep Ready(old) visible while revalidating — do NOT reset to Pending.
        Launch(epoch);
    }

    public void Mutate(T optimistic, bool refresh)
    {
        long epoch = Advance();          // (1) cancel in-flight so a stale completion can't clobber the optimistic value
        StaleGen++;
        IsStaleSig.Value = false;
        LastError = null;
        Loadable.SetReady(optimistic);   // (2) optimistic write, visible immediately
        if (refresh) Launch(epoch);      // (3) revalidate on this epoch (keeps Ready(optimistic) visible until it lands)
        else { IsFetchingSig.Value = false; ArmStale(); }
    }

    public void DisposeCell()
    {
        Epoch++;        // any in-flight completion posted after here is epoch-dropped by Settle
        StaleGen++;     // drop any pending stale timer
        try { Cts?.Cancel(); } catch { /* already disposed */ }
        Cts?.Dispose();
    }
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
    // ── Call-site-keyed hook cells (WS1 substrate) ──────────────────────────────────────────────────────────────────
    // Each hook cell is keyed by its CALL SITE — (hashed [CallerFilePath], [CallerLineNumber], per-render ordinal) —
    // NOT by a positional cursor. A stable key means a hook keeps the SAME cell across renders even when it is called
    // CONDITIONALLY: a hook inside an `if` gains/loses its slot without shifting its neighbours' state (the thing the
    // old positional cursor could not do), and a hook in a loop is disambiguated by the per-line ordinal. Cells persist
    // in _cells for the component's lifetime; a conditionally-skipped hook keeps its state for when the branch is
    // re-entered, and every cell's cleanup still runs at unmount (RunAllCleanups walks all of _cells). Conditional /
    // looped hooks are therefore LEGAL — see docs/guide/reactivity.md.
    private readonly List<HookCell> _cells = new();
    private readonly Dictionary<HookKey, int> _keyed = new();                     // call-site key → index into _cells
    private readonly Dictionary<(ulong File, int Line), int> _ordinals = new();   // per-render loop-ordinal counter (reset each render)
    private int _cleanupCellCount;

    // [CallerFilePath] yields the SAME interned string reference for every call site in a file, so the file path is
    // hashed ONCE per distinct file (XxHash64, via DepKey) and cached by reference identity — the steady render re-hashes
    // nothing. Static + UI-thread-confined (the engine's single render/reconcile thread), so no lock is needed.
    private static readonly Dictionary<string, ulong> _fileHashes = new(ReferenceEqualityComparer.Instance);

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
    /// <summary>Reconciler-injected at mount when this component was embedded with re-pushed props
    /// (<c>Embed.Comp(props, factory)</c>): the per-instance signal the reconciler's reuse seam writes on each parent
    /// re-render (equality-gated) and that <see cref="UseProps{T}"/> reads (subscribing this component's render-effect).
    /// Null for a propless mount or an <see cref="IPropsHost"/> component (which owns its own field signals).</summary>
    public Signal<object?>? PropsSig;

    internal void BeginRender() => _ordinals.Clear();   // reset the per-render loop ordinals; cells persist by key
    internal void EndRender()
    {
        // The "form under construction" thread-local (set by UseForm so same-component UseField calls auto-join) lives
        // only for this render — clear it so it never leaks into the next component the reconciler renders.
        FluentGpu.Forms.FormScope.Building = null;
    }

    // ── Keyed cell resolution ────────────────────────────────────────────────────────────────────────────────────────
    // Every positional hook resolves its cell through these two helpers. LookupCell advances the per-(file,line) loop
    // ordinal, computes the call-site key, and returns the existing cell index (or -1 for a fresh call site/ordinal —
    // the hook then creates its cell and calls RegisterCell with the SAME key). No closures ⇒ zero allocation on the
    // steady (reuse) path; the ordinal/keyed dictionary probes are value-typed (no boxing).

    /// <summary>Resolve the current call site to its existing cell index (or -1 if fresh), yielding the key to register with.</summary>
    private int LookupCell(string? file, int line, out HookKey key)
    {
        ulong fh = FileHash(file);
        var fl = (fh, line);
        _ordinals.TryGetValue(fl, out int ord);
        _ordinals[fl] = ord + 1;
        key = new HookKey(fh, line, ord);
        return _keyed.TryGetValue(key, out int idx) ? idx : -1;
    }

    /// <summary>Append a freshly-created cell and bind it to its call-site <paramref name="key"/>.</summary>
    private void RegisterCell(in HookKey key, HookCell cell, bool cleanupCapable = false)
    {
        _keyed[key] = _cells.Count;
        _cells.Add(cell);
        if (cleanupCapable) _cleanupCellCount++;
    }

    private static ulong FileHash(string? file)
    {
        if (file is null) return 0;
        if (_fileHashes.TryGetValue(file, out ulong h)) return h;
        h = (ulong)DepKey.HashString(file);   // the existing zero-alloc XxHash64; computed once per distinct file
        _fileHashes[file] = h;
        return h;
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

    public (T Value, Action<T> Set) UseState<T>(T initial, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        SignalCell<T> cell;
        if (idx < 0) { cell = new SignalCell<T>(new Signal<T>(initial)); RegisterCell(__k, cell); }
        else cell = (SignalCell<T>)_cells[idx];

        var sig = cell.Signal;
        // Reading .Value here (inside the render-effect) subscribes this component; the setter writes the signal,
        // which schedules ONLY this component's render-effect for the next flush (granular, batched, value-eq-gated).
        return (sig.Value, v => sig.Value = v);
    }

    /// <summary>A persistent reactive cell you own (the Solid/Preact <c>signal</c>): read <c>.Value</c> in render to
    /// subscribe, or bind it to a node channel for a render-free update. Survives re-renders.</summary>
    public Signal<T> UseSignal<T>(T initial, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        SignalCell<T> cell;
        if (idx < 0) { cell = new SignalCell<T>(new Signal<T>(initial)); RegisterCell(__k, cell); }
        else cell = (SignalCell<T>)_cells[idx];
        return cell.Signal;
    }

    /// <summary>A persistent scalar signal for the hot path (slider/scroll/progress). Bind it to a node transform/paint
    /// channel (<c>TransformBind</c>/<c>OpacityBind</c>) for a value→pixels update that skips render/reconcile/layout.</summary>
    public FloatSignal UseFloatSignal(float initial = 0f, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        FloatSignalCell cell;
        if (idx < 0) { cell = new FloatSignalCell(new FloatSignal(initial)); RegisterCell(__k, cell); }
        else cell = (FloatSignalCell)_cells[idx];
        return cell.Signal;
    }

    /// <summary>A derived reactive value (the Solid <c>createMemo</c>): recomputes from the signals it reads, cached.</summary>
    public Memo<T> UseComputed<T>(Func<T> compute, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        MemoHookCell<T> cell;
        if (idx < 0) { cell = new MemoHookCell<T>(new Memo<T>(Rt, compute)); RegisterCell(__k, cell, cleanupCapable: true); }
        else cell = (MemoHookCell<T>)_cells[idx];
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

    // AUTO-TRACKED (no deps) and DEPS-GATED (a DepKey) stay separate overloads (non-nullable DepKey → 0-alloc; a
    // DepKey? nullable boxes on every call). The deps overloads carry [OverloadResolutionPriority(1)] so a bare-string
    // deps — `UseEffect(fn, someString)`, where `string → DepKey` competes with the CallerFilePath `string? __hf` of the
    // auto overload — still binds to the DEPS overload (the higher priority wins over the identity string conversion).
    // Without it the string would be captured as the file path and the effect would silently become auto-tracked.
    public void UseEffect(Action effect, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) { var e = GetAutoEffect(layout: false, out bool mount, __hf, __hl); e.ActionBody = effect; e.FuncBody = null; if (mount) e.Prime(); }
    public void UseEffect(Func<Action?> effect, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) { var e = GetAutoEffect(layout: false, out bool mount, __hf, __hl); e.FuncBody = effect; e.ActionBody = null; if (mount) e.Prime(); }
    /// <summary>Like <see cref="UseEffect(Action)"/> but runs after layout, before paint (Bounds are valid). Phase 6.5.</summary>
    public void UseLayoutEffect(Action effect, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) { var e = GetAutoEffect(layout: true, out bool mount, __hf, __hl); e.ActionBody = effect; e.FuncBody = null; if (mount) e.Prime(); }
    /// <inheritdoc cref="UseLayoutEffect(Action)"/>
    public void UseLayoutEffect(Func<Action?> effect, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) { var e = GetAutoEffect(layout: true, out bool mount, __hf, __hl); e.FuncBody = effect; e.ActionBody = null; if (mount) e.Prime(); }

    /// <summary>Deps-gated effect (no tracking): runs at mount and whenever <paramref name="deps"/> changes. Pass
    /// <see cref="DepKey.Empty"/> for mount-once. Scalars/tuples convert implicitly (<c>UseEffect(fn, count)</c>).</summary>
    [OverloadResolutionPriority(1)]
    public void UseEffect(Action effect, DepKey deps, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => EffectKey(effect, deps, PendingEffects, __hf, __hl);
    /// <inheritdoc cref="UseEffect(Action, DepKey)"/>
    [OverloadResolutionPriority(1)]
    public void UseEffect(Func<Action?> effect, DepKey deps, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => EffectKeyC(effect, deps, PendingEffects, __hf, __hl);
    /// <inheritdoc cref="UseEffect(Action, DepKey)"/>
    [OverloadResolutionPriority(1)]
    public void UseLayoutEffect(Action effect, DepKey deps, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => EffectKey(effect, deps, PendingLayoutEffects, __hf, __hl);
    /// <inheritdoc cref="UseEffect(Action, DepKey)"/>
    [OverloadResolutionPriority(1)]
    public void UseLayoutEffect(Func<Action?> effect, DepKey deps, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) => EffectKeyC(effect, deps, PendingLayoutEffects, __hf, __hl);

    /// <summary>Mount a signal-tracked effect owned by this component that runs EAGERLY (the body runs synchronously now,
    /// then re-runs inline during <see cref="ReactiveRuntime.Flush"/> when a read signal changes). Distinct from the
    /// passive auto-tracked <see cref="UseEffect(Action)"/> — use this adapter form ONLY to bridge a hot signal into a
    /// coarser retained signal where eager timing is required; prefer <see cref="UseEffect(Action)"/> otherwise.</summary>
    public void UseSignalEffect(Action effect, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        if (idx < 0) { var cell = new SignalEffectCell(new Effect(Rt, effect)); RegisterCell(__k, cell, cleanupCapable: true); }
    }

    private AutoEffect GetAutoEffect(bool layout, out bool mount, string? file, int line)
    {
        int idx = LookupCell(file, line, out var __k);
        mount = idx < 0;
        AutoEffectCell cell;
        if (mount) { cell = new AutoEffectCell { Effect = new AutoEffect(Rt, this, layout) }; RegisterCell(__k, cell, cleanupCapable: true); }
        else cell = (AutoEffectCell)_cells[idx];
        return cell.Effect;
    }

    private void EffectKey(Action effect, DepKey deps, List<Action> target, string? file, int line)
    {
        int idx = LookupCell(file, line, out var __k);
        EffectCell cell;
        if (idx < 0) { cell = new EffectCell(); RegisterCell(__k, cell, cleanupCapable: true); }
        else cell = (EffectCell)_cells[idx];

        if (!cell.HasKey || cell.Key != deps)
        {
            cell.Key = deps; cell.HasKey = true;
            EnqueueEffect(target, () => { cell.Cleanup?.Invoke(); effect(); });
        }
    }

    private void EffectKeyC(Func<Action?> effect, DepKey deps, List<Action> target, string? file, int line)
    {
        int idx = LookupCell(file, line, out var __k);
        EffectCell cell;
        if (idx < 0) { cell = new EffectCell(); RegisterCell(__k, cell, cleanupCapable: true); }
        else cell = (EffectCell)_cells[idx];

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
    public (TState State, Action<TAction> Dispatch) UseReducer<TState, TAction>(Func<TState, TAction, TState> reducer, TState initial, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        SignalCell<TState> cell;
        if (idx < 0) { cell = new SignalCell<TState>(new Signal<TState>(initial)); RegisterCell(__k, cell); }
        else cell = (SignalCell<TState>)_cells[idx];

        var sig = cell.Signal; var r = reducer;
        Action<TAction> dispatch = action => sig.Value = r(sig.Peek(), action);
        return (sig.Value, dispatch);
    }

    /// <summary>Memoize an expensive value; recomputes only when <paramref name="deps"/> changes (deps-gated, explicit —
    /// there is no auto-tracked memo). Scalars/tuples convert implicitly; <see cref="DepKey.Empty"/>/default = compute
    /// once at mount.</summary>
    public T UseMemo<T>(Func<T> factory, DepKey deps, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        MemoCell<T> cell;
        if (idx < 0)
        {
            cell = new MemoCell<T> { Value = factory(), Key = deps, HasKey = true };
            RegisterCell(__k, cell);
        }
        else
        {
            cell = (MemoCell<T>)_cells[idx];
            if (!cell.HasKey || cell.Key != deps) { cell.Value = factory(); cell.Key = deps; cell.HasKey = true; }
        }
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

    /// <summary>Like <see cref="UseContext{T}"/> but MANDATORY: resolves the nearest provided value of
    /// <paramref name="context"/> (same resolve path, including the parked <see cref="_ctxResolveCache"/> fallback so a
    /// KeepAlive-parked re-render still finds it), subscribing this component to that provider's signal — but THROWS
    /// <see cref="InvalidOperationException"/> naming the context type when no provider is in scope. A provider carrying
    /// <c>null</c> (for a <c>Context&lt;T?&gt;</c>) also throws — a required dependency must be present AND non-null.
    /// Use for a dependency the component cannot render without (a service/store), so a missing provider is a loud
    /// programming error at the consumer instead of a silent default.</summary>
    public T UseRequiredContext<T>(Context<T> context)
    {
        var sig = ResolveContextSignal?.Invoke(AnchorNode, context);
        if (sig is not null) (_ctxResolveCache ??= new())[context] = sig;   // attached: refresh the last-resolved provider
        else _ctxResolveCache?.TryGetValue(context, out sig);              // detached/parked: reuse it (providers above are unchanged)
        if (sig is not null && sig.Value is T tv) return tv;   // sig.Value subscribes the render-effect
        throw new InvalidOperationException(
            $"UseRequiredContext<{typeof(T).Name}>: no provider for this context is in scope (or it carries a null/incompatible " +
            $"value). Wrap an ancestor in Ctx.Provide({typeof(T).Name} …) so the required dependency resolves.");
    }

    /// <summary>Read the RE-PUSHED props of this component (<c>Embed.Comp(props, factory)</c>) as
    /// <typeparamref name="T"/>, subscribing this component's render-effect so a changed re-push re-renders exactly here.
    /// NON-positional (no hook cell): it just reads the injected props signal, so it may be called conditionally / after
    /// an early return. THROWS <see cref="InvalidOperationException"/> (naming this component's type) when the component
    /// was mounted WITHOUT props — use <see cref="UsePropsOrDefault{T}"/> for a component usable both ways.</summary>
    public T UseProps<T>(Type? owner = null) where T : class
    {
        if (PropsSig is null)
        {
            string who = owner?.Name ?? "This component";
            throw new InvalidOperationException(
                $"UseProps<{typeof(T).Name}>: {who} was mounted without props. Embed it with " +
                $"Embed.Comp(new {typeof(T).Name}(…), () => new {who}()) to supply them, or call " +
                $"UsePropsOrDefault<{typeof(T).Name}>() for a component usable with and without props.");
        }
        return (T)PropsSig.Value!;   // .Value subscribes the render-effect; delivered props are non-null by construction
    }

    /// <summary>Like <see cref="UseProps{T}"/> but returns <c>null</c> (without subscribing) when the component was
    /// mounted propless — for a component usable both with re-pushed props and standalone. When props ARE present,
    /// reading them subscribes this component's render-effect.</summary>
    public T? UsePropsOrDefault<T>() where T : class
        => PropsSig is null ? null : PropsSig.Value as T;

    /// <summary>Return the nearest context as a read signal without subscribing this render. Reading the returned
    /// signal's <c>.Value</c> subscribes the current reactive computation at the call site.</summary>
    public IReadSignal<T> UseContextSignal<T>(Context<T> context, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        ContextSignalCell<T> cell;
        if (idx < 0) { cell = new ContextSignalCell<T>(this, context); RegisterCell(__k, cell); }
        else cell = (ContextSignalCell<T>)_cells[idx];
        return cell.Signal;
    }

    // ── Activation lifecycle (parked-by-KeepAlive OR window-minimized) ───────────────────────────────────────────────

    /// <summary>This component's activation state as a reactive signal: <c>false</c> when its page is parked by
    /// <c>Flow.KeepAlive</c> (a backgrounded tab) OR the window is minimized / app-suspended, <c>true</c> otherwise.
    /// Read <c>.Value</c> in render to subscribe (e.g. gate a live-only affordance); for transition callbacks use
    /// <see cref="UseActivation"/>. A value-gated <see cref="Memo{T}"/> built once at mount (zero steady-state
    /// allocation); a fresh component is active. Folds the per-component KeepAlive signal with the ambient
    /// <c>Activation.IsActive</c> window-visibility signal — either being false makes the component inactive.</summary>
    public IReadSignal<bool> UseIsActive([CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        MemoHookCell<bool> cell;
        if (idx < 0)
        {
            var componentActive = GetActiveSig;   // get-or-create this component's parked signal on first compute (lazy)
            var meta = ResolveContextSignal?.Invoke(AnchorNode, Activation.IsActive);
            var windowVisible = meta?.Peek() as IReadSignal<bool>;   // ambient visibility signal (the channel value never re-publishes)
            var memo = new Memo<bool>(Rt, () =>
                (componentActive is null || componentActive().Value) && (windowVisible is null || windowVisible.Value));
            cell = new MemoHookCell<bool>(memo);
            RegisterCell(__k, cell, cleanupCapable: true);
        }
        else cell = (MemoHookCell<bool>)_cells[idx];
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
    public void UseActivation(Action? onActivated = null, Action? onDeactivated = null, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        var cb = UseRef<(Action? On, Action? Off)>(default);
        cb.Value = (onActivated, onDeactivated);     // always route to the latest closures
        var active = UseIsActive();
        var prev = UseRef(true);
        var started = UseRef(false);

        int idx = LookupCell(__hf, __hl, out var __k);
        if (idx < 0)
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
            RegisterCell(__k, new SignalEffectCell(effect), cleanupCapable: true);
        }
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
    public float UseAnimatedValue(float target, float durationMs = 180f, Easing easing = Easing.EaseInOut, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        AnimValueCell cell;
        if (idx < 0) { cell = new AnimValueCell { Current = target, From = target, Target = target }; RegisterCell(__k, cell); }
        else cell = (AnimValueCell)_cells[idx];

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
    public FluentGpu.Media.VideoBinding UseVideoSurface([CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        var registry = UseContext(VideoCompositor.Current);
        int idx = LookupCell(__hf, __hl, out var __k);
        VideoSurfaceCell cell;
        if (idx < 0)
        {
            cell = new VideoSurfaceCell { Registry = registry, Token = registry?.Acquire() ?? 0 };
            RegisterCell(__k, cell, cleanupCapable: true);
        }
        else cell = (VideoSurfaceCell)_cells[idx];
        return new FluentGpu.Media.VideoBinding(cell.Registry, cell.Token);
    }

    /// <summary>Create a component-lifetime-owned disposable ONCE at mount and dispose it automatically on unmount.
    /// Returns the same instance every render (or null if the factory returned null). For a native/OS resource whose
    /// lifetime should track the component (e.g. a media player owning a background sidecar).</summary>
    public T? UseDisposable<T>(Func<T?> factory, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) where T : class, IDisposable
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        DisposableCell<T> cell;
        if (idx < 0) { cell = new DisposableCell<T> { Value = factory() }; RegisterCell(__k, cell, cleanupCapable: true); }
        else cell = (DisposableCell<T>)_cells[idx];
        return cell.Value;
    }

    // ── Skeleton-loading: the async-value spine + the resource lifecycle ─────────────────────────────────────────────

    /// <summary>A persistent per-field async value (Pending|Ready|Failed + value) — the skeleton-loading spine. Survives
    /// re-renders; flip it with <c>SetReady</c>/<c>SetFailed</c> (typically from <see cref="UseResource"/> or an
    /// off-thread <see cref="UsePost"/>). Use this for a field whose load you drive yourself (or a partial-known seed).</summary>
    public Loadable<T> UseLoadable<T>(Loadable<T>? initial = null, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        LoadableCell<T> cell;
        if (idx < 0) { cell = new LoadableCell<T> { Loadable = initial ?? Loadable<T>.Pending() }; RegisterCell(__k, cell); }
        else cell = (LoadableCell<T>)_cells[idx];
        return cell.Loadable;
    }

    /// <summary>A stale-while-revalidate async resource. Kicks <paramref name="loader"/> at mount (Pending→Ready/Failed
    /// via <see cref="UsePost"/>), RE-FIRES when <paramref name="deps"/> change (value-equality — a detail page whose id
    /// changes while its component instance is REUSED across navigation), and hands back a <see cref="Resource{T}"/> whose
    /// <see cref="Resource{T}.Refresh"/>/<see cref="Resource{T}.Mutate"/> drive revalidation. Every load is EPOCH-stamped,
    /// so an older/slower fetch can never overwrite a newer one; the in-flight load + any stale timer are cancelled on
    /// unmount. <paramref name="seed"/> is the placeholder while Pending; <paramref name="options"/> tunes stale time and
    /// keep-previous-data (default: <c>StaleTimeMs=0</c> ⇒ stale on Ready; deps change ⇒ reset to <c>Pending(seed)</c>).</summary>
    public Resource<T> UseResource<T>(Func<CancellationToken, Task<T>> loader, T seed = default!, DepKey deps = default, ResourceOptions? options = null, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        var post = UsePost();   // UsePost consumes no hook cell, so calling it here does not shift the ordinal
        int idx = LookupCell(__hf, __hl, out var __k);
        ResourceCell<T> cell;
        if (idx < 0)
        {
            cell = new ResourceCell<T>
            {
                Loadable = Loadable<T>.Pending(seed),
                Cts = new CancellationTokenSource(),
                Deps = deps, HasDeps = true,
                Options = options ?? ResourceOptions.Default,
                Seed = seed, Loader = loader, Post = post, Queue = ResolveTimers(),
            };
            RegisterCell(__k, cell, cleanupCapable: true);
            cell.Reload(keepPrevious: false);   // initial load (Loadable already Pending(seed) ⇒ no signal write during render)
            return new Resource<T>(cell);
        }
        cell = (ResourceCell<T>)_cells[idx];
        cell.Loader = loader;                    // re-point the latest closure (fresh lambdas don't restart the load)
        cell.Seed = seed;
        if (options is not null) cell.Options = options;
        if (cell.Deps != deps)
        {
            cell.Deps = deps;
            cell.Reload(keepPrevious: cell.Options.KeepPreviousData);   // deps change → reload (keep-prev shows old data meanwhile)
        }
        return new Resource<T>(cell);
    }

    /// <summary>A persistent fire-on-demand <see cref="AsyncCommand"/> — run an async action from an event (a button
    /// command), render a spinner / disable off <c>IsRunning</c>, guard re-entry, and optionally cancel. Completion is
    /// marshalled to the UI thread via <see cref="UsePost"/>. By default an in-flight run is NOT cancelled on unmount
    /// (a started command should complete); pass <paramref name="cancelOnUnmount"/> true to abort it.</summary>
    public AsyncCommand UseAsyncCommand(bool cancelOnUnmount = false, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        var post = UsePost();   // UseContext consumes no hook cell (calling it here does not shift the ordinal)
        int idx = LookupCell(__hf, __hl, out var __k);
        AsyncCommandCell cell;
        if (idx < 0)
        {
            cell = new AsyncCommandCell { Command = new AsyncCommand(post), CancelOnUnmount = cancelOnUnmount };
            RegisterCell(__k, cell, cleanupCapable: true);
        }
        else cell = (AsyncCommandCell)_cells[idx];
        return cell.Command;
    }

    /// <summary>A persistent KEYED <see cref="AsyncCommandSet{TKey}"/> — per-item commands (e.g. per-row play/like) with
    /// a reactive in-progress state per key. Same lifecycle as <see cref="UseAsyncCommand"/>.</summary>
    public AsyncCommandSet<TKey> UseAsyncCommands<TKey>(bool cancelOnUnmount = false, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0) where TKey : notnull
    {
        var post = UsePost();
        int idx = LookupCell(__hf, __hl, out var __k);
        AsyncCommandSetCell<TKey> cell;
        if (idx < 0)
        {
            cell = new AsyncCommandSetCell<TKey> { Commands = new AsyncCommandSet<TKey>(post), CancelOnUnmount = cancelOnUnmount };
            RegisterCell(__k, cell, cleanupCapable: true);
        }
        else cell = (AsyncCommandSetCell<TKey>)_cells[idx];
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
    public Ref<T> UseRef<T>(T initial, [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
    {
        int idx = LookupCell(__hf, __hl, out var __k);
        RefHolderCell cell;
        if (idx < 0) { cell = new RefHolderCell { Ref = new Ref<T>(initial) }; RegisterCell(__k, cell); }
        else cell = (RefHolderCell)_cells[idx];
        return (Ref<T>)cell.Ref!;
    }
}
