using FluentGpu.Dsl;

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
