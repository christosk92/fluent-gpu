using FluentGpu.Dsl;

namespace FluentGpu.Hooks;

/// <summary>Base for stateful components. Override <see cref="Render"/>; use hooks in stable order each render.</summary>
public abstract class Component
{
    public RenderContext Context { get; internal set; } = new();

    public abstract Element Render();

    protected (T Value, Action<T> Set) UseState<T>(T initial) => Context.UseState(initial);
    protected void UseEffect(Action effect, params object[] deps) => Context.UseEffect(effect, deps);
    protected void UseLayoutEffect(Action effect, params object[] deps) => Context.UseLayoutEffect(effect, deps);
    protected (TState State, Action<TAction> Dispatch) UseReducer<TState, TAction>(Func<TState, TAction, TState> reducer, TState initial) => Context.UseReducer(reducer, initial);
    protected T UseMemo<T>(Func<T> factory, params object[] deps) => Context.UseMemo(factory, deps);
    protected Ref<T> UseRef<T>(T initial) => Context.UseRef(initial);

    /// <summary>Run one render pass with hook bookkeeping.</summary>
    public Element RenderWithHooks()
    {
        Context.BeginRender();
        var el = Render();
        Context.EndRender();
        return el;
    }

    /// <summary>Called by the reconciler when this component leaves the tree — run effect cleanups.</summary>
    public void Unmount() => Context.RunAllCleanups();
}
