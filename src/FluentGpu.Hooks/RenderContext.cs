using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Hooks;

internal abstract class HookCell
{
    public abstract bool Flush();   // apply pending → value; returns true if a change occurred
}

internal sealed class StateCell<T> : HookCell
{
    public T Value;
    public T Pending = default!;
    public bool HasPending;
    public StateCell(T initial) { Value = initial; }

    public override bool Flush()
    {
        if (!HasPending) return false;
        bool changed = !EqualityComparer<T>.Default.Equals(Value, Pending);
        Value = Pending;
        HasPending = false;
        return changed;
    }
}

internal sealed class EffectCell : HookCell
{
    public object[]? Deps;
    public Action? Cleanup;
    public Action? Pending;       // effect to run after commit
    public override bool Flush() => false;
}

internal sealed class MemoCell<T> : HookCell
{
    public T Value = default!;
    public object[]? Deps;
    public override bool Flush() => false;
}

internal sealed class RefHolderCell : HookCell
{
    public object? Ref;
    public override bool Flush() => false;
}

internal sealed class AnimValueCell : HookCell
{
    public float Current, From, Target, Elapsed;
    public override bool Flush() => false;
}

/// <summary>A stable, mutable per-component box that persists across renders (the React <c>useRef</c> container).</summary>
public sealed class Ref<T>
{
    public T Value;
    public Ref(T value) => Value = value;
}

/// <summary>
/// Per-component hook storage (ordered; rules-of-hooks = stable call order). Holds <see cref="UseState"/> cells and
/// schedules a re-render through the host. The slice uses managed cells (edge allocation only, at mount).
/// </summary>
public sealed class RenderContext
{
    private readonly List<HookCell> _cells = new();
    private int _cursor;
    private bool _mounted;
    public Action RequestRerender = static () => { };

    public readonly List<Action> PendingEffects = new();        // UseEffect — after present (phase 12)
    public readonly List<Action> PendingLayoutEffects = new();  // UseLayoutEffect — after layout, before paint (phase 6.5)

    // Set by the reconciler at mount: the engine + this component's host scene node, so hooks can animate "itself".
    public AnimEngine? Anim;
    public ImageCache? Images;   // host-injected; backs UseImage / PrefetchImage
    public NodeHandle HostNode;

    internal void BeginRender() => _cursor = 0;
    internal void EndRender() => _mounted = true;

    /// <summary>Apply queued setState writes (phase 3). Returns true if any value actually changed.</summary>
    public bool FlushPending()
    {
        bool any = false;
        foreach (var cell in _cells) any |= cell.Flush();
        return any;
    }

    /// <summary>Run every pending effect cleanup (component unmount).</summary>
    public void RunAllCleanups()
    {
        foreach (var cell in _cells) if (cell is EffectCell e) e.Cleanup?.Invoke();
    }

    public (T Value, Action<T> Set) UseState<T>(T initial)
    {
        StateCell<T> cell;
        if (!_mounted) { cell = new StateCell<T>(initial); _cells.Add(cell); }
        else { cell = (StateCell<T>)_cells[_cursor]; }
        _cursor++;

        T value = cell.Value;
        var captured = cell;
        var req = RequestRerender;
        Action<T> set = v => { captured.Pending = v; captured.HasPending = true; req(); };
        return (value, set);
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

    /// <summary>State driven by a reducer (the React <c>useReducer</c>); dispatches fold over the latest pending state.</summary>
    public (TState State, Action<TAction> Dispatch) UseReducer<TState, TAction>(Func<TState, TAction, TState> reducer, TState initial)
    {
        StateCell<TState> cell;
        if (!_mounted) { cell = new StateCell<TState>(initial); _cells.Add(cell); }
        else { cell = (StateCell<TState>)_cells[_cursor]; }
        _cursor++;

        var captured = cell; var req = RequestRerender; var r = reducer;
        Action<TAction> dispatch = action =>
        {
            TState cur = captured.HasPending ? captured.Pending : captured.Value;
            captured.Pending = r(cur, action);
            captured.HasPending = true;
            req();
        };
        return (cell.Value, dispatch);
    }

    /// <summary>Memoize an expensive value; recomputes only when <paramref name="deps"/> change.</summary>
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

    /// <summary>Read the nearest provided value of <paramref name="context"/> (or its default). Re-render propagates it.</summary>
    public T UseContext<T>(Context<T> context)
    {
        if (ContextStack.TryGet(context, out var v) && v is T tv) return tv;
        return context.Default;
    }

    /// <summary>
    /// A value that smoothly eases toward <paramref name="target"/> whenever the target changes (React/framer-style).
    /// While animating it requests the next frame; once settled it stops (no more renders). Bind it to a color/scale.
    /// </summary>
    public float UseAnimatedValue(float target, float durationMs = 180f)
    {
        AnimValueCell cell;
        if (!_mounted) { cell = new AnimValueCell { Current = target, From = target, Target = target }; _cells.Add(cell); }
        else cell = (AnimValueCell)_cells[_cursor];
        _cursor++;

        if (cell.Target != target) { cell.From = cell.Current; cell.Target = target; cell.Elapsed = 0f; }
        if (cell.Current != cell.Target)
        {
            cell.Elapsed += 16f;   // ~one frame; the host renders ~60 Hz while a value is animating
            float t = Math.Clamp(cell.Elapsed / MathF.Max(durationMs, 1f), 0f, 1f);
            float e = t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t);   // ease-in-out
            cell.Current = cell.From + (cell.Target - cell.From) * e;
            if (t >= 1f) cell.Current = cell.Target;
            else RequestRerender();
        }
        return cell.Current;
    }

    // ── Declarative animation (seed/retarget engine tracks on this component's node; composited, no re-render/frame) ──

    /// <summary>Spring a transform/opacity channel of this node toward <paramref name="to"/>. When deps change it
    /// retargets, keeping velocity (smooth interruption) — e.g. UseSpring(ScaleX, hovered?1.05f:1f, spring, hovered).</summary>
    public void UseSpring(AnimChannel channel, float to, SpringParams spring, params object[] deps)
        => UseLayoutEffect(() => { if (Anim is { } a && !HostNode.IsNull) a.Spring(HostNode, channel, to, spring); }, deps);

    /// <summary>Eased transition of a channel from→to when deps change (the CSS <c>transition</c> model).</summary>
    public void UseTransition(AnimChannel channel, float from, float to, float durationMs, Easing easing = Easing.EaseInOut, params object[] deps)
        => UseLayoutEffect(() => { if (Anim is { } a && !HostNode.IsNull) a.Animate(HostNode, channel, from, to, durationMs, easing); }, deps);

    /// <summary>Multi-keyframe animation on a channel (the CSS <c>@keyframes</c> model).</summary>
    public void UseKeyframes(AnimChannel channel, Keyframe[] keys, float durationMs, bool loop = false, params object[] deps)
        => UseLayoutEffect(() => { if (Anim is { } a && !HostNode.IsNull) a.Keyframes(HostNode, channel, keys, durationMs, loop); }, deps);

    /// <summary>Scroll/value-driven animation: a channel tracks a value source through [min,max] (animation-timeline: scroll()).</summary>
    public void UseDrivenAnimation(AnimChannel channel, Keyframe[] keys, Func<float> source, float min, float max, params object[] deps)
        => UseLayoutEffect(() => { if (Anim is { } a && !HostNode.IsNull) a.Drive(HostNode, channel, keys, a.Clocks.Register(source), min, max); }, deps);

    /// <summary>Bind an async image and observe its load state (media-pipeline.md §5): requests (or dedups) a decode at
    /// <paramref name="decodePx"/> and returns the handle + current <see cref="ImageState"/> / failure, so a component
    /// can render a spinner, a broken-art fallback, etc. Pair with <c>Ui.Image(src,…)</c> to actually paint it.</summary>
    public ImageBinding UseImage(string src, int decodePx, ImagePriority priority = ImagePriority.Visible, string? blurHash = null)
    {
        if (Images is null || string.IsNullOrEmpty(src)) return default;
        var h = Images.Request(src, decodePx, decodePx, priority, blurHash);
        return new ImageBinding(h, Images.StateOf(h), Images.FailureOf(h), Images.AttemptsOf(h));
    }

    /// <summary>Warm an image the UI is about to need (the next scroll page) at <see cref="ImagePriority.Prefetch"/> so
    /// it's resident before it scrolls in. Decoded ahead, never pinned (evictable until a node shows it).</summary>
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
