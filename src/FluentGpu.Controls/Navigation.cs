using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>A serializable navigation destination — a route name + an optional argument (e.g. a playlist id).</summary>
public sealed record Route(string Name, string? Arg = null);

/// <summary>
/// Navigation as **serializable state** (the SwiftUI/Flutter lesson): the whole back stack is a typed value the view
/// tree is derived from — never stored in view objects. That gives deep-linking, cold-launch restore, and undo for
/// free, and composes with the reconciler (a route change is a new immutable description diffed against the retained
/// tree). The host wires <see cref="OnChange"/> to a re-render request.
/// </summary>
public sealed class Navigator
{
    private readonly List<Route> _stack = new();

    /// <summary>Set by <see cref="PageHost"/> to request a re-render when the stack changes.</summary>
    public Action OnChange = static () => { };

    public Navigator(Route initial) => _stack.Add(initial);

    public Route Current => _stack[^1];
    public int Depth => _stack.Count;
    public bool CanGoBack => _stack.Count > 1;

    public void Push(Route route) { _stack.Add(route); OnChange(); }
    public void Push(string name, string? arg = null) => Push(new Route(name, arg));
    public void Replace(Route route) { _stack[^1] = route; OnChange(); }
    public bool Pop()
    {
        if (_stack.Count <= 1) return false;
        _stack.RemoveAt(_stack.Count - 1);
        OnChange();
        return true;
    }

    /// <summary>Serialize the back stack for cold-launch restore / deep links: <c>home|playlist=p1|...</c>.</summary>
    public string Serialize()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _stack.Count; i++)
        {
            if (i > 0) sb.Append('|');
            var r = _stack[i];
            sb.Append(r.Name);
            if (r.Arg is not null) sb.Append('=').Append(r.Arg);
        }
        return sb.ToString();
    }

    public static Navigator Deserialize(string s)
    {
        var parts = s.Split('|', System.StringSplitOptions.RemoveEmptyEntries);
        Navigator nav = new(Parse(parts.Length > 0 ? parts[0] : "home"));
        for (int i = 1; i < parts.Length; i++) nav._stack.Add(Parse(parts[i]));
        return nav;

        static Route Parse(string p)
        {
            int eq = p.IndexOf('=');
            return eq < 0 ? new Route(p) : new Route(p[..eq], p[(eq + 1)..]);
        }
    }
}

/// <summary>
/// Renders the top route of a <see cref="Navigator"/> via a route→view factory, re-rendering on every navigation.
/// Wraps the view in the <see cref="Nav.Context"/> provider, so any descendant can <c>UseContext(Nav.Context)</c> to
/// push/pop. Pages keep their state by reconciler identity (same route name at the top ⇒ same node subtree).
/// </summary>
public sealed class PageHost : Component
{
    private readonly Navigator _nav;
    private readonly Func<Route, Element> _view;

    public PageHost(Navigator nav, Func<Route, Element> view) { _nav = nav; _view = view; }

    public override Element Render()
    {
        _nav.OnChange = Context.RequestRerender;          // navigation → re-render this host
        return Ctx.Provide(Nav.Context, _nav, _view(_nav.Current));
    }
}

/// <summary>The ambient navigator. Read it in any component with <c>UseContext(Nav.Context)</c> to push/pop.</summary>
public static class Nav
{
    public static readonly Context<Navigator?> Context = new(null);
}
