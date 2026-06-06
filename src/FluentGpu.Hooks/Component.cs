using FluentGpu.Dsl;

namespace FluentGpu.Hooks;

/// <summary>Base for stateful components. Override <see cref="Render"/>; use hooks in stable order each render.</summary>
public abstract class Component
{
    public RenderContext Context { get; internal set; } = new();

    public abstract Element Render();

    protected (T Value, Action<T> Set) UseState<T>(T initial) => Context.UseState(initial);
    protected void UseEffect(Action effect, params object[] deps) => Context.UseEffect(effect, deps);

    /// <summary>Run one render pass with hook bookkeeping.</summary>
    public Element RenderWithHooks()
    {
        Context.BeginRender();
        var el = Render();
        Context.EndRender();
        return el;
    }
}
