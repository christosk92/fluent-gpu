using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Hooks;

/// <summary>A context channel with a default. Consumers read the nearest provider's value via <c>UseContext</c>.</summary>
public sealed class Context<T>
{
    public T Default;
    public Context(T defaultValue) => Default = defaultValue;
}

/// <summary>
/// The ambient client size (DIP), pushed by the host each frame so components can adapt to available width
/// (responsive layout / NavigationView display modes). Read with <c>UseContext(Viewport.Size)</c>.
/// </summary>
public static class Viewport
{
    public static readonly Context<Size2> Size = new(default);
}

/// <summary>Provides a context value to its subtree (the React <c>Context.Provider</c>). One child.</summary>
public sealed record ContextProviderEl(object Channel, object? Value, Element Child) : Element
{
    public override ushort ElementTypeId => 4;
}

/// <summary>Fluent: <c>Ctx.Provide(MyContext, value, child)</c>.</summary>
public static class Ctx
{
    public static ContextProviderEl Provide<T>(Context<T> context, T value, Element child) => new(context, value, child);
}

/// <summary>
/// Reconcile-time shadow stack of provided context values. The reconciler pushes a provider's value before
/// reconciling its subtree (incl. nested component renders) and pops after; <c>UseContext</c> reads the top.
/// (Push/pop happen on the dirty reconcile path, never the zero-alloc paint half.)
/// </summary>
public static class ContextStack
{
    [ThreadStatic] private static List<(object ctx, object? val)>? _s;

    public static void Push(object ctx, object? val) => (_s ??= new()).Add((ctx, val));
    public static void Pop() { var s = _s!; s.RemoveAt(s.Count - 1); }

    public static bool TryGet(object ctx, out object? val)
    {
        var s = _s;
        if (s is not null)
            for (int i = s.Count - 1; i >= 0; i--)
                if (ReferenceEquals(s[i].ctx, ctx)) { val = s[i].val; return true; }
        val = null;
        return false;
    }
}
