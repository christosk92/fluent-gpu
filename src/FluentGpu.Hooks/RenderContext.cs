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

    public readonly List<Action> PendingEffects = new();

    internal void BeginRender() => _cursor = 0;
    internal void EndRender() => _mounted = true;

    /// <summary>Apply queued setState writes (phase 3). Returns true if any value actually changed.</summary>
    public bool FlushPending()
    {
        bool any = false;
        foreach (var cell in _cells) any |= cell.Flush();
        return any;
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

    public void UseEffect(Action effect, params object[] deps)
    {
        EffectCell cell;
        if (!_mounted) { cell = new EffectCell(); _cells.Add(cell); }
        else { cell = (EffectCell)_cells[_cursor]; }
        _cursor++;

        bool changed = !_mounted || !DepsEqual(cell.Deps, deps);
        if (changed)
        {
            cell.Deps = deps;
            PendingEffects.Add(() =>
            {
                cell.Cleanup?.Invoke();
                effect();
            });
        }
    }

    private static bool DepsEqual(object[]? a, object[]? b)
    {
        if (a is null || b is null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!Equals(a[i], b[i])) return false;
        return true;
    }
}
