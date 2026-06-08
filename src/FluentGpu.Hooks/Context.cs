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

/// <summary>
/// A per-frame tick the host bumps (only while something subscribes, so it's free when idle). A tree-level concern that
/// must poll each frame — e.g. an overlay driving a timed close animation to completion — reads
/// <c>UseContext(FrameClock.Tick)</c> to re-render every frame for as long as it's mounted.
/// </summary>
public static class FrameClock
{
    public static readonly Context<long> Tick = new(0L);
}

/// <summary>
/// A host-owned registration surface a tree-level concern can hook into without the host depending on it. The host
/// bridges <see cref="KeyPreview"/> into the input dispatcher's pre-focus key hook, so an open overlay/flyout (which
/// lives in the tree) can intercept Escape regardless of focus. Read the host instance via <c>UseContext(InputHooks.Current)</c>.
/// </summary>
public sealed class InputHooks
{
    /// <summary>Return true to consume the key. Set by an open overlay; cleared when it closes.</summary>
    public Func<int, bool>? KeyPreview;
    public bool Preview(int key) => KeyPreview?.Invoke(key) ?? false;
    public static readonly Context<InputHooks> Current = new(new InputHooks());
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
