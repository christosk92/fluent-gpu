using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// How a route's page animates in when it becomes the top of the stack. Maps onto the engine's declarative
/// <see cref="Element.Enter"/>/<see cref="Element.Transition"/> motion tokens on the resolved page root
/// (see <see cref="PageHost.WithTransition"/>). <see cref="Default"/> is a gentle standard entrance; <see cref="None"/>
/// snaps in (the page author owns all motion); <see cref="Entrance"/> is an emphasized enter.
/// </summary>
public enum NavTransition : byte { Default, None, Entrance }

/// <summary>
/// One entry in the <see cref="RouteRegistry"/>: a stable <paramref name="Key"/> + the <paramref name="Factory"/> that
/// builds its page from the active <see cref="Route"/> (so argful pages read <c>route.Arg</c>). The remaining fields are
/// showcase/nav metadata the shell derives (title, icon, grouping, order, whether it shows in the nav tree, whether it
/// keeps state alive when parked, its entrance transition, and extra search terms). Populated by hand
/// (<see cref="RouteRegistry.Add(RouteDef)"/>) or emitted from a <see cref="RouteAttribute"/> by the RouteTableGenerator.
/// </summary>
public sealed record RouteDef(string Key, Func<Route, Element> Factory)
{
    public string Title { get; init; } = "";
    public string Icon { get; init; } = "";
    public string? Category { get; init; }
    public int Order { get; init; } = 1000;
    public bool ShowInNav { get; init; } = true;
    public bool KeepAlive { get; init; }
    public NavTransition Transition { get; init; } = NavTransition.Default;
    public string[] SearchTerms { get; init; } = [];
}

/// <summary>
/// The one source of truth for a router: route key → page factory (+ nav/search metadata). It replaces the
/// hand-synced trio of a page switch, a nav-item tree, and a search index — <see cref="BuildNavTree"/> and
/// <see cref="BuildSearchIndex"/> derive the latter two from the registered routes. Registration is compile-time
/// (the RouteTableGenerator emits <c>Routes.RegisterAll(registry)</c> from <see cref="RouteAttribute"/>) with a
/// runtime <see cref="Add(RouteDef)"/> escape hatch for dynamic/argful app routes. A duplicate key throws — the same
/// uniqueness contract the generator enforces at compile time (FGRT001).
/// </summary>
public sealed class RouteRegistry
{
    private readonly Dictionary<string, RouteDef> _byKey = new(StringComparer.Ordinal);
    private readonly List<RouteDef> _order = new();

    /// <summary>Register a route. Throws <see cref="InvalidOperationException"/> if <see cref="RouteDef.Key"/> is
    /// already registered (keys are the router's identities — a collision would silently shadow a page).</summary>
    public void Add(RouteDef def)
    {
        if (_byKey.ContainsKey(def.Key))
            throw new InvalidOperationException(
                $"RouteRegistry: duplicate route key '{def.Key}'. Route keys must be unique — a collision would shadow a page.");
        _byKey.Add(def.Key, def);
        _order.Add(def);
    }

    /// <summary>Convenience overload for a propless page: <c>Add("home", "Home", Icons.Home, () =&gt; Embed.Comp(...))</c>.</summary>
    public void Add(string key, string title, string icon, Func<Element> factory)
        => Add(new RouteDef(key, _ => factory()) { Title = title, Icon = icon });

    /// <summary>Resolve a route key to its definition, or <c>null</c> if unregistered (callers fall back to
    /// <see cref="Fallback"/>).</summary>
    public RouteDef? Resolve(string key) => _byKey.TryGetValue(key, out var d) ? d : null;

    /// <summary>Every registered route in registration order.</summary>
    public IReadOnlyList<RouteDef> All => _order;

    /// <summary>Builds the page for an unresolved key (deep links to routes not in the table, 404 pages). Null ⇒ the
    /// host renders an empty placeholder for unknown keys.</summary>
    public Func<Route, Element>? Fallback { get; set; }

    /// <summary>
    /// Derive a <see cref="NavItem"/> tree from the registered routes (those with <see cref="RouteDef.ShowInNav"/>).
    /// Routes are grouped by <see cref="RouteDef.Category"/>: each entry in <paramref name="categoryOrder"/> becomes a
    /// group (in the given order, using the supplied icon) holding its category's routes sorted by
    /// <see cref="RouteDef.Order"/> then title; any category present in the routes but absent from
    /// <paramref name="categoryOrder"/> is appended (first-seen order, no icon); routes with a null category become
    /// top-level items. Every group/item's <see cref="NavItem.Key"/> is the route (or category) key; its label is the
    /// title (falling back to the key).
    /// </summary>
    public NavItem[] BuildNavTree(params (string Category, string Icon)[] categoryOrder)
    {
        var visible = _order.Where(static r => r.ShowInNav).ToList();
        var result = new List<NavItem>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        NavItem Leaf(RouteDef r) => new(r.Key, r.Icon, r.Title.Length > 0 ? r.Title : r.Key);

        void EmitCategory(string cat, string icon)
        {
            if (!emitted.Add(cat)) return;
            var kids = visible.Where(r => r.Category == cat)
                              .OrderBy(static r => r.Order).ThenBy(static r => r.Title, StringComparer.Ordinal)
                              .Select(Leaf).ToArray();
            if (kids.Length > 0)
                result.Add(new NavItem(cat, icon, cat) { Children = kids });
        }

        foreach (var (cat, icon) in categoryOrder) EmitCategory(cat, icon);
        // Categories present in routes but not named in categoryOrder — append (first-seen), so nothing is dropped.
        foreach (var r in visible)
            if (r.Category is { } c) EmitCategory(c, "");
        // Uncategorized routes → top-level items, in Order.
        foreach (var r in visible.Where(static r => r.Category is null)
                                 .OrderBy(static r => r.Order).ThenBy(static r => r.Title, StringComparer.Ordinal))
            result.Add(Leaf(r));

        return result.ToArray();
    }

    /// <summary>
    /// Derive a TWO-level nav tree for a sectioned information architecture (the WS7 gallery shape): each
    /// <paramref name="sections"/> entry is a section group whose children are its listed categories. A category whose
    /// name equals its section name is <b>flattened</b> — its pages become direct leaf children of the section (e.g.
    /// "Fundamentals" section holding "Fundamentals"-category pages directly); every other category becomes a subgroup
    /// of its pages (e.g. the "Controls" section holding "Basic input"/"Media"/… subgroups). Pages within a category are
    /// sorted by <see cref="RouteDef.Order"/> then title; empty categories and empty sections are dropped. Only routes
    /// with <see cref="RouteDef.ShowInNav"/> participate. This is the derivation primitive a registry-driven gallery
    /// shell builds its nav from (paired with a hand-authored section→categories table).
    /// </summary>
    public NavItem[] BuildSectionedNavTree(params (string Section, string Icon, string[] Categories)[] sections)
    {
        var visible = _order.Where(static r => r.ShowInNav).ToList();
        NavItem Leaf(RouteDef r) => new(r.Key, r.Icon, r.Title.Length > 0 ? r.Title : r.Key);
        NavItem[] PagesIn(string cat) => visible.Where(r => r.Category == cat)
            .OrderBy(static r => r.Order).ThenBy(static r => r.Title, StringComparer.Ordinal)
            .Select(Leaf).ToArray();

        var result = new List<NavItem>();
        foreach (var (section, icon, cats) in sections)
        {
            var children = new List<NavItem>();
            foreach (var cat in cats)
            {
                var pages = PagesIn(cat);
                if (pages.Length == 0) continue;
                if (string.Equals(cat, section, StringComparison.Ordinal)) children.AddRange(pages);   // flat section
                else children.Add(new NavItem(cat, "", cat) { Children = pages });                      // category subgroup
            }
            if (children.Count > 0) result.Add(new NavItem(section, icon, section) { Children = children.ToArray() });
        }
        return result.ToArray();
    }

    /// <summary>
    /// Derive the search corpus: one <c>(Label, Key)</c> per visible route (label = title, else key), plus one entry
    /// per <see cref="RouteDef.SearchTerms"/> alias pointing at the same key. Powers an AutoSuggestBox / command palette.
    /// </summary>
    public (string Label, string Key)[] BuildSearchIndex()
    {
        var list = new List<(string, string)>(_order.Count);
        foreach (var r in _order)
        {
            if (!r.ShowInNav) continue;
            list.Add((r.Title.Length > 0 ? r.Title : r.Key, r.Key));
            foreach (var term in r.SearchTerms) list.Add((term, r.Key));
        }
        return list.ToArray();
    }
}

/// <summary>
/// Tags a <see cref="FluentGpu.Hooks.Component"/> subclass as a router page: the RouteTableGenerator collects every
/// <c>[Route]</c>-tagged component in the assembly and emits <c>Routes.RegisterAll(RouteRegistry)</c> (compile-time,
/// zero reflection — AOT-clean). A page with a parameterless ctor is registered as <c>route =&gt; Embed.Comp(() =&gt; new
/// Page())</c>; a page with a <c>(Route)</c> or <c>(string)</c> ctor gets the route (or <c>route.Arg</c>) threaded in.
/// Duplicate keys across the compilation are a compile error (FGRT001).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RouteAttribute(string key) : Attribute
{
    /// <summary>The stable route key (the switch string it replaces).</summary>
    public string Key { get; } = key;
    public string? Title { get; set; }
    public string? Icon { get; set; }
    public string? Category { get; set; }
    public int Order { get; set; }
    public bool KeepAlive { get; set; }
    public bool ShowInNav { get; set; } = true;
}
