using FluentGpu.Hooks;

namespace FluentGpu.Theming;

/// <summary>
/// An immutable table <c>view key id -> (template, template id)</c> plus the style objects a theme registers. Built once
/// with a <see cref="ViewRegistryBuilder"/>, provided to a subtree through <see cref="Context"/>
/// (<c>Ctx.Provide(ViewRegistry.Context, registry, child)</c>) and resolved by every slot host
/// (<see cref="Slots.View{TProps}"/>). Swapping the provided registry re-renders exactly the slot hosts; a host whose
/// template id changed remounts its slot, a host whose template id is unchanged re-runs the template (the registry
/// <see cref="Epoch"/> changed).
/// </summary>
public sealed class ViewRegistry
{
    /// <summary>The ambient registry. Null (the default) means no registry: every slot renders nothing and logs once.</summary>
    public static readonly Context<ViewRegistry?> Context = new(null);

    /// <summary>A registry with no views and epoch 0.</summary>
    public static ViewRegistry Empty { get; } = new(new Dictionary<string, ViewEntry>(), new Dictionary<string, object>(), 0);

    private static long s_epoch;

    internal readonly record struct ViewEntry(Delegate Template, string TemplateId);

    private readonly Dictionary<string, ViewEntry> _views;
    private readonly Dictionary<string, object> _styles;
    private readonly object _gate = new();
    private HashSet<string>? _missingReported;

    internal ViewRegistry(Dictionary<string, ViewEntry> views, Dictionary<string, object> styles, long epoch)
    {
        _views = views;
        _styles = styles;
        Epoch = epoch;
    }

    internal static long NextEpoch() => Interlocked.Increment(ref s_epoch);

    /// <summary>Monotonic build stamp: a newer registry has a larger epoch. A slot host re-runs its template when the
    /// registry it resolves changes (reference or epoch).</summary>
    public long Epoch { get; }

    /// <summary>Number of registered views.</summary>
    public int ViewCount => _views.Count;

    /// <summary>Every registered view id (with its <c>@version</c>).</summary>
    public IReadOnlyCollection<string> ViewKeyIds => _views.Keys;

    public bool Contains(string viewId) => _views.ContainsKey(viewId);

    /// <summary>The template id registered for <paramref name="viewId"/>, or null.</summary>
    public string? TemplateIdOf(string viewId) => _views.TryGetValue(viewId, out var e) ? e.TemplateId : null;

    public bool TryGetStyle<TStyle>(StyleKey<TStyle> key, out TStyle style) where TStyle : class
    {
        if (_styles.TryGetValue(key.Id, out var o) && o is TStyle s) { style = s; return true; }
        style = null!;
        return false;
    }

    internal bool TryGetView<TProps>(string id, out ViewTemplate<TProps> template, out string templateId) where TProps : class
    {
        if (_views.TryGetValue(id, out var e) && e.Template is ViewTemplate<TProps> t)
        {
            template = t;
            templateId = e.TemplateId;
            return true;
        }
        template = null!;
        templateId = "";
        return false;
    }

    /// <summary>True the first time <paramref name="id"/> is reported against this registry (UI thread, bounded by the
    /// number of distinct missing ids).</summary>
    internal bool FirstMissing(string id)
    {
        lock (_gate) return (_missingReported ??= new HashSet<string>(StringComparer.Ordinal)).Add(id);
    }
}

/// <summary>Collects views and styles, then freezes them into a <see cref="ViewRegistry"/>.</summary>
public sealed class ViewRegistryBuilder
{
    private readonly Dictionary<string, ViewRegistry.ViewEntry> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _styles = new(StringComparer.Ordinal);

    /// <summary>Register <paramref name="template"/> for <paramref name="key"/>. <paramref name="templateId"/> names the
    /// template (e.g. <c>"classic.player.bar"</c>): it is the slot's identity, so a different id under the same key
    /// remounts the slot while the same id with a new epoch only re-runs it. Duplicate ids throw.</summary>
    public ViewRegistryBuilder Add<TProps>(ViewKey<TProps> key, ViewTemplate<TProps> template, string templateId) where TProps : class
    {
        ArgumentException.ThrowIfNullOrEmpty(key.Id);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        if (!_views.TryAdd(key.Id, new ViewRegistry.ViewEntry(template, templateId)))
            throw new InvalidOperationException($"ViewRegistryBuilder.Add: view key '{key.Id}' is already registered (template '{_views[key.Id].TemplateId}').");
        return this;
    }

    /// <summary>Register a style object for <paramref name="key"/>. Duplicate ids throw.</summary>
    public ViewRegistryBuilder AddStyle<TStyle>(StyleKey<TStyle> key, TStyle style) where TStyle : class
    {
        ArgumentException.ThrowIfNullOrEmpty(key.Id);
        ArgumentNullException.ThrowIfNull(style);
        if (!_styles.TryAdd(key.Id, style))
            throw new InvalidOperationException($"ViewRegistryBuilder.AddStyle: style key '{key.Id}' is already registered.");
        return this;
    }

    /// <summary>Freeze the table. <paramref name="epoch"/> defaults to the next process-wide value, so every built
    /// registry is distinguishable from every earlier one.</summary>
    public ViewRegistry Build(long? epoch = null)
        => new(new Dictionary<string, ViewRegistry.ViewEntry>(_views, StringComparer.Ordinal),
               new Dictionary<string, object>(_styles, StringComparer.Ordinal),
               epoch ?? ViewRegistry.NextEpoch());
}
