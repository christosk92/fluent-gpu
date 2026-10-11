using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace FluentGpu.Theming;

/// <summary>
/// What a <see cref="ViewTemplate{TProps}"/> is handed next to its props. A ref struct: it lives for one template call
/// and cannot be captured by a closure, so an event handler must read what it needs (an <see cref="IOptionRequests"/>, a
/// value) into a local first.
/// </summary>
public readonly ref struct ViewContext
{
    /// <summary>The app's role table, provided by the app as <c>Ctx.Provide(ViewContext.AppRolesContext, roles, child)</c>
    /// and read by <see cref="App{T}"/>. (It moves onto the theme definition with the app role table work.)</summary>
    public static readonly Context<object?> AppRolesContext = new(null);

    private readonly ViewRegistry? _registry;

    public ViewContext(RenderContext hooks, IOptionReader options, IOptionRequests optionRequests, ViewRegistry? registry, bool reduced)
    {
        Hooks = hooks;
        Options = options;
        OptionRequests = optionRequests;
        _registry = registry;
        Reduced = reduced;
    }

    /// <summary>The slot host's hook context: <c>ctx.Hooks.UseState(...)</c>, <c>UseEffect</c>, <c>UseInterval</c>, ... The
    /// cells belong to the slot, so they are disposed when the slot remounts (template change) or unmounts.</summary>
    public RenderContext Hooks { get; }

    /// <summary>Tracked option reads: the template re-renders when an option it read here changes.</summary>
    public IOptionReader Options { get; }

    /// <summary>Option writes, routed to the app (<see cref="OptionStore.Requests"/>).</summary>
    public IOptionRequests OptionRequests { get; }

    /// <summary>Reduced motion as a value (never branch hook order on it).</summary>
    public bool Reduced { get; }

    /// <summary>The app's role object as <typeparamref name="T"/>, read through <see cref="AppRolesContext"/> (so a
    /// change of the provided object re-renders the template). Throws when none is provided or it is another type.</summary>
    public T App<T>() where T : class
    {
        object? roles = Hooks.UseContext(AppRolesContext);
        if (roles is T typed) return typed;
        throw new InvalidOperationException(roles is null
            ? $"ViewContext.App<{typeof(T).Name}>: no app roles object is provided. Wrap an ancestor in Ctx.Provide(ViewContext.AppRolesContext, roles, ...)."
            : $"ViewContext.App<{typeof(T).Name}>: the provided app roles object is a {roles.GetType().Name}.");
    }

    /// <summary>Nest another slot (same as <see cref="Slots.View{TProps}"/>). Inside a loop, <c>Flow.For</c> or any
    /// collection builder pass <paramref name="callerKey"/>.</summary>
    public Element View<TProps>(ViewKey<TProps> key, TProps props, string? callerKey = null) where TProps : class
        => Slots.View(key, props, callerKey);

    /// <summary>A style object the current registry holds for <paramref name="key"/>. Throws when it is missing.</summary>
    public TStyle StyleOf<TStyle>(StyleKey<TStyle> key) where TStyle : class
    {
        if (_registry is not null && _registry.TryGetStyle(key, out var style)) return style;
        throw new InvalidOperationException(_registry is null
            ? $"ViewContext.StyleOf: style key '{key.Id}' requested but no ViewRegistry is in scope."
            : $"ViewContext.StyleOf: style key '{key.Id}' is not registered as a {typeof(TStyle).Name} in the current ViewRegistry.");
    }
}
