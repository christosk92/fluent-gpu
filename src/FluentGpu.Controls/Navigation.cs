using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
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
/// Renders the top route of a <see cref="Navigator"/> and re-renders the view tree on every navigation, wrapping it in
/// the <see cref="Nav.Context"/> provider so any descendant can <c>UseContext(Nav.Context)</c> to push/pop.
///
/// <para>Two shapes: the PRIMITIVE <c>PageHost(nav, Func&lt;Route,Element&gt;)</c> renders the top route through a
/// caller-supplied factory (pages keep state by reconciler identity - same route name at the top => same node subtree);
/// the REGISTRY-DRIVEN <see cref="Create(Navigator, RouteRegistry)"/> resolves the top route's page through a
/// <see cref="RouteRegistry"/> (with <see cref="RouteRegistry.Fallback"/> for unknown keys), parks
/// <see cref="RouteDef.KeepAlive"/> pages via <c>Flow.KeepAlive</c> (their state survives navigating away and back), and
/// applies the route's <see cref="RouteDef.Transition"/> to the page root (see <see cref="WithTransition"/>).</para>
/// </summary>
public sealed class PageHost : Component
{
    private readonly Navigator _nav;
    private readonly Func<Route, Element>? _view;
    private readonly RouteRegistry? _routes;

    public PageHost(Navigator nav, Func<Route, Element> view) { _nav = nav; _view = view; }
    public PageHost(Navigator nav, RouteRegistry routes) { _nav = nav; _routes = routes; }

    /// <summary>The registry-driven host: resolve <paramref name="nav"/>'s top route through <paramref name="routes"/>
    /// (fallback for unknown keys, KeepAlive parking, entrance transitions). The one-liner router surface.</summary>
    public static Element Create(Navigator nav, RouteRegistry routes) => Embed.Comp(() => new PageHost(nav, routes));

    public override Element Render()
    {
        if (_routes is { } routes)
        {
            // A route change updates a signal the active-route reader subscribes to - so the KeepAlive boundary re-runs
            // (its Update is a no-op by design), with no global dirty flag. Seeded once at mount.
            var routeSig = UseSignal(_nav.Current);
            _nav.OnChange = () => routeSig.Value = _nav.Current;
            var content = Flow.KeepAlive(
                () => routeSig.Value,                                        // reactive read here re-arms the boundary
                static r => r.Arg is null ? r.Name : r.Name + "|" + r.Arg,   // arg-aware park identity
                r => ResolveView(routes, r),
                // Only routes that opt into KeepAlive are cached/parked; the rest remount fresh on return.
                new KeepAliveOptions(ShouldCache: o => routes.Resolve(((Route)o).Name)?.KeepAlive == true));
            return Ctx.Provide(Nav.Context, _nav, content);
        }

        _nav.OnChange = Context.RequestRerender;          // primitive path: navigation re-renders this host
        return Ctx.Provide(Nav.Context, _nav, _view!(_nav.Current));
    }

    private static Element ResolveView(RouteRegistry routes, Route route)
    {
        RouteDef? def = routes.Resolve(route.Name);
        Element view = def is not null
            ? def.Factory(route)
            : routes.Fallback?.Invoke(route) ?? new BoxEl();
        return WithTransition(view, def?.Transition ?? NavTransition.Default);
    }

    // Emphasized page enter travels further than the standard one; both fade in. None snaps (author owns motion).
    private static readonly EnterExit StandardPageEnter = new(Dy: 8f, Opacity: 0f, Active: true);
    private static readonly EnterExit EmphasizedPageEnter = new(Dy: 24f, Opacity: 0f, Active: true);

    /// <summary>Apply a <see cref="NavTransition"/> to a page root: <see cref="NavTransition.None"/> leaves it untouched;
    /// <see cref="NavTransition.Default"/>/<see cref="NavTransition.Entrance"/> seed the engine's declarative
    /// <see cref="Element.Enter"/> + <see cref="Element.Transition"/> motion tokens (a fade + slide-up entrance).</summary>
    public static Element WithTransition(Element view, NavTransition transition) => transition switch
    {
        NavTransition.None => view,
        NavTransition.Entrance => view with { Enter = EmphasizedPageEnter, Transition = MotionTok.EmphasizedEnter },
        _ => view with { Enter = StandardPageEnter, Transition = MotionTok.StandardEnter },
    };
}

/// <summary>The ambient navigator. Read it in any component with <c>UseContext(Nav.Context)</c> to push/pop.</summary>
public static class Nav
{
    public static readonly Context<Navigator?> Context = new(null);
}
