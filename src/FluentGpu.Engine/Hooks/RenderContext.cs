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

internal sealed class EffectCell : HookCell
{
    public object[]? Deps;
    public Action? Cleanup;
}

internal sealed class MemoCell<T> : HookCell
{
    public T Value = default!;
    public object[]? Deps;
}

internal sealed class RefHolderCell : HookCell
{
    public object? Ref;
}

internal sealed class AnimValueCell : HookCell
{
    public float Current, From, Target, Elapsed;
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
    public readonly object[] KindDep;            // the layout-effect dep array (stable; identity-equal each render)
    public readonly Action Register;             // stable effect delegate (installs the forwarder on the current node)
    private readonly Action<GestureEventArgs> _forward;   // stable forwarder written into the scene column
    private NodeHandle _registeredNode;           // the node the forwarder is currently installed on (for re-target/cleanup)

    public GestureHookState(RenderContext ctx, GestureType kind)
    {
        _ctx = ctx;
        _kind = kind;
        KindDep = new object[] { kind };
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
public sealed class RenderContext
{
    private readonly List<HookCell> _cells = new();
    private int _cursor;
    private bool _mounted;

    public readonly List<Action> PendingEffects = new();        // UseEffect — after present (phase 12)
    public readonly List<Action> PendingLayoutEffects = new();  // UseLayoutEffect — after layout, before paint (phase 6.5)

    // Injected by the reconciler/host at mount.
    public ReactiveRuntime? Runtime;            // creates signals/memos; schedules this component's render-effect
    /// <summary>Imperatively re-render THIS component (granular — schedules only this component's render-effect). Wired
    /// by the reconciler at mount. For escape-hatch callers (e.g. a Navigator's OnChange); state/context use signals.</summary>
    public Action RequestRerender = static () => { };
    public AnimEngine? Anim;
    public ImageCache? Images;                  // host-injected; backs UseImage / PrefetchImage
    public SceneStore? Scene;                   // reconciler-injected; for measuring nodes (AbsoluteRect) + overlay positioning
    public NodeHandle HostNode;                 // this component's rendered child (animation hooks target it)
    public NodeHandle AnchorNode;               // this component's anchor in the scene (context resolution walks up from here)
    public Func<NodeHandle, object, Signal<object?>?>? ResolveContextSignal;   // (anchor, channel) → nearest provider signal
    public IReadSignal<int>? ImageEpoch;        // bumped by the host on any image status change → re-renders UseImage consumers

    internal void BeginRender() => _cursor = 0;
    internal void EndRender() => _mounted = true;

    /// <summary>Run every pending effect cleanup + dispose owned reactive primitives (component unmount).</summary>
    public void RunAllCleanups()
    {
        foreach (var cell in _cells)
        {
            if (cell is EffectCell e) e.Cleanup?.Invoke();
            else if (cell is IDisposableCell d) d.DisposeCell();
        }
    }

    private ReactiveRuntime Rt => Runtime ?? throw new InvalidOperationException("RenderContext.Runtime not set (component not mounted by the host).");

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
        if (!_mounted) { cell = new MemoHookCell<T>(new Memo<T>(Rt, compute)); _cells.Add(cell); }
        else cell = (MemoHookCell<T>)_cells[_cursor];
        _cursor++;
        return cell.Memo;
    }

    public void UseEffect(Action effect, params object[] deps) => EffectImpl(effect, deps, PendingEffects);

    /// <summary>Like <see cref="UseEffect"/> but runs after layout, before paint (Bounds are valid). Phase 6.5.</summary>
    public void UseLayoutEffect(Action effect, params object[] deps) => EffectImpl(effect, deps, PendingLayoutEffects);

    private void EffectImpl(Action effect, object[] deps, List<Action> target)
    {
        EffectCell cell;
        if (!_mounted) { cell = new EffectCell(); _cells.Add(cell); }
        else { cell = (EffectCell)_cells[_cursor]; }
        _cursor++;

        bool changed = !_mounted || !DepsEqual(cell.Deps, deps);
        if (changed)
        {
            cell.Deps = deps;
            target.Add(() => { cell.Cleanup?.Invoke(); effect(); });
        }
    }

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

    /// <summary>Memoize an expensive value; recomputes only when <paramref name="deps"/> change (dep-array memo).</summary>
    public T UseMemo<T>(Func<T> factory, params object[] deps)
    {
        MemoCell<T> cell;
        if (!_mounted)
        {
            cell = new MemoCell<T> { Value = factory(), Deps = deps };
            _cells.Add(cell);
        }
        else
        {
            cell = (MemoCell<T>)_cells[_cursor];
            if (!DepsEqual(cell.Deps, deps)) { cell.Value = factory(); cell.Deps = deps; }
        }
        _cursor++;
        return cell.Value;
    }

    /// <summary>Read the nearest provided value of <paramref name="context"/> (or its default), subscribing this
    /// component to that provider's signal so a change re-renders exactly here (no prop drilling, no full-tree walk).</summary>
    public T UseContext<T>(Context<T> context)
    {
        var sig = ResolveContextSignal?.Invoke(AnchorNode, context);
        if (sig is not null && sig.Value is T tv) return tv;   // sig.Value subscribes the render-effect
        return context.Default;
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

    public void UseSpring(AnimChannel channel, float to, SpringParams spring, params object[] deps)
        => UseLayoutEffect(() => { if (Anim is { } a && !HostNode.IsNull) a.Spring(HostNode, channel, to, spring); }, deps);

    public void UseTransition(AnimChannel channel, float from, float to, float durationMs, Easing easing = Easing.EaseInOut, params object[] deps)
        => UseLayoutEffect(() => { if (Anim is { } a && !HostNode.IsNull) a.Animate(HostNode, channel, from, to, durationMs, easing); }, deps);

    public void UseKeyframes(AnimChannel channel, Keyframe[] keys, float durationMs, bool loop = false, params object[] deps)
        => UseLayoutEffect(() => { if (Anim is { } a && !HostNode.IsNull) a.Keyframes(HostNode, channel, keys, durationMs, loop); }, deps);

    public void UseDrivenAnimation(AnimChannel channel, Keyframe[] keys, Func<float> source, float min, float max, params object[] deps)
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
    {
        _ = ImageEpoch?.Value;   // subscribe: any image status change re-renders this UseImage consumer
        if (Images is null || string.IsNullOrEmpty(src)) return default;
        var h = Images.Request(src, decodePx, decodePx, priority, blurHash);
        return new ImageBinding(h, Images.StateOf(h), Images.FailureOf(h), Images.AttemptsOf(h));
    }

    public void PrefetchImage(string src, int decodePx)
    {
        if (Images is not null && !string.IsNullOrEmpty(src)) Images.Prefetch(src, decodePx, decodePx);
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

    private static bool DepsEqual(object[]? a, object[]? b)
    {
        if (a is null || b is null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!Equals(a[i], b[i])) return false;
        return true;
    }
}
