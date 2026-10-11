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
    /// <summary>An OVERRIDE of the app role table for a subtree (a test, a preview): <c>Ctx.Provide(ViewContext.AppRolesContext,
    /// roles, child)</c>. When none is provided (the normal case) <see cref="App{T}"/> reads <c>Tok.Definition.AppRoles</c>.</summary>
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

    /// <summary>The app's role object as <typeparamref name="T"/>: the active <see cref="ThemeDefinition.AppRoles"/> (checked once
    /// per <c>Tok.Epoch</c>, so it is a field compare on the steady path). A theme switch bumps the epoch and the host re-renders
    /// every mounted template in place, so a template always reads the roles of the theme on screen. A subtree that provides
    /// <see cref="AppRolesContext"/> overrides it. Throws when no roles are set or they are another type.</summary>
    public T App<T>() where T : class
    {
        object? overridden = Hooks.UseContext(AppRolesContext);
        if (overridden is not null)
        {
            if (overridden is T typedOverride) return typedOverride;
            throw new InvalidOperationException($"ViewContext.App<{typeof(T).Name}>: the provided AppRolesContext object is a {overridden.GetType().Name}.");
        }
        T? roles = AppRolesCache<T>.Get();
        if (roles is not null) return roles;
        object? actual = Tok.Definition.AppRoles;
        throw new InvalidOperationException(actual is null
            ? $"ViewContext.App<{typeof(T).Name}>: the active ThemeDefinition has no AppRoles (set ThemeDefinition.AppRoles to the app's role table)."
            : $"ViewContext.App<{typeof(T).Name}>: ThemeDefinition.AppRoles is a {actual.GetType().Name}.");
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
