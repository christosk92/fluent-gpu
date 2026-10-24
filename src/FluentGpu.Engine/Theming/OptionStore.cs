using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Theming;

/// <summary>A typed option cell id with its default: <c>new OptionKey&lt;bool&gt;("classic.dense", false)</c>.</summary>
public readonly record struct OptionKey<T>(string Id, T Default);

/// <summary>Tracked option reads. A read made while a component renders subscribes that component to the option's cell
/// only, so a template re-renders when an option it read changes and not when any other option does.</summary>
public interface IOptionReader
{
    T Get<T>(OptionKey<T> key);
}

/// <summary>Option writes. A request is a REQUEST: it is routed to the app (<see cref="OptionStore.Requests"/>), which
/// decides how to apply it (live or as a transaction) and then publishes the result with
/// <see cref="OptionStore.Publish{T}"/>. Nothing in a template ever writes an option cell directly.</summary>
public interface IOptionRequests
{
    void Set<T>(OptionKey<T> key, T value);
}

/// <summary>Where option values survive a restart. Values cross this seam as raw boxed objects; the implementation owns
/// any conversion (the app types them from the theme's option schema).</summary>
public interface IOptionPersistence
{
    /// <summary>The stored value for <paramref name="optionId"/>, if any.</summary>
    bool TryLoad(string optionId, out object? raw);

    /// <summary>Store <paramref name="raw"/> for <paramref name="optionId"/>.</summary>
    void Save(string optionId, object? raw);
}

/// <summary>Adapts a plain delegate to <see cref="IOptionRequests"/> (the value is boxed).</summary>
public sealed class DelegateOptionRequests(Action<string, object?> handler) : IOptionRequests
{
    public void Set<T>(OptionKey<T> key, T value) => handler(key.Id, value);
}

/// <summary>
/// One signal per option id, a change epoch, and the request sink. Provide it with
/// <c>Ctx.Provide(OptionStore.Context, store, child)</c>; templates read it through <see cref="ViewContext.Options"/>.
/// UI-thread only (like every signal write in the engine).
/// <para>Reads: <see cref="Get{T}"/> is tracked, <see cref="Peek{T}"/> is not. The cell is created on first touch and
/// seeded from <see cref="IOptionPersistence"/> (when it holds a value of the key's type) or the key's default.</para>
/// <para>Writes: a template calls <c>ctx.OptionRequests.Set</c>, which forwards to <see cref="Requests"/> (installed by the
/// app). The app applies the change and calls <see cref="Publish{T}"/> to commit it: that writes the cell (equality-gated:
/// re-publishing the same value notifies nobody), bumps <see cref="Epoch"/> and persists it.</para>
/// </summary>
public sealed class OptionStore : IOptionReader
{
    /// <summary>The ambient option store. Null (the default) means no store: reads return the key's default.</summary>
    public static readonly Context<OptionStore?> Context = new(null);

    private readonly Dictionary<string, object> _cells = new(StringComparer.Ordinal);
    private readonly Signal<long> _epoch = new(0L);
    private readonly IOptionPersistence? _persistence;
    private IOptionRequests _requests = UnhandledOptionRequests.Instance;

    public OptionStore(IOptionPersistence? persistence = null)
    {
        _persistence = persistence;
        RequestForwarder = new Forwarder(this);
    }

    /// <summary>The sink the app installs; every <c>ctx.OptionRequests.Set</c> ends here. Defaults to a sink that ignores
    /// the request and logs it. Assigning null restores the default.</summary>
    public IOptionRequests Requests
    {
        get => _requests;
        set => _requests = value ?? UnhandledOptionRequests.Instance;
    }

    /// <summary>The <see cref="IOptionRequests"/> handed to templates: forwards to the CURRENT <see cref="Requests"/> at
    /// call time, so a handler that captured it at render still reaches a sink the app installs later.</summary>
    public IOptionRequests RequestForwarder { get; }

    /// <summary>Bumped by every <see cref="Publish{T}"/> that changed a value.</summary>
    public long Epoch => _epoch.Peek();

    /// <summary>The epoch as a signal, for a consumer that must react to any option change.</summary>
    public IReadSignal<long> EpochSignal => _epoch;

    /// <summary>Number of option cells created so far.</summary>
    public int Count => _cells.Count;

    /// <summary>Tracked read: subscribes the computation that is running (a template's render) to this option's cell.</summary>
    public T Get<T>(OptionKey<T> key) => Cell(key).Value;

    /// <summary>Untracked read.</summary>
    public T Peek<T>(OptionKey<T> key) => Cell(key).Peek();

    /// <summary>Commit a value the app has applied. Returns true when it changed the cell. Only the app's apply path
    /// calls this; templates request through <see cref="Requests"/>.</summary>
    public bool Publish<T>(OptionKey<T> key, T value, bool persist = true)
    {
        var cell = Cell(key);
        if (!cell.SetIfChanged(value)) return false;
        _epoch.SetIfChanged(_epoch.Peek() + 1);
        if (persist) _persistence?.Save(key.Id, value);
        return true;
    }

    private Signal<T> Cell<T>(OptionKey<T> key)
    {
        if (_cells.TryGetValue(key.Id, out var existing))
        {
            if (existing is Signal<T> typed) return typed;
            throw new InvalidOperationException(
                $"OptionStore: option '{key.Id}' is already registered with a different value type than {typeof(T).Name}.");
        }
        T initial = key.Default;
        if (_persistence is { } p && p.TryLoad(key.Id, out var raw) && raw is T loaded) initial = loaded;
        var cell = new Signal<T>(initial);
        _cells.Add(key.Id, cell);
        return cell;
    }

    private sealed class Forwarder(OptionStore store) : IOptionRequests
    {
        public void Set<T>(OptionKey<T> key, T value) => store._requests.Set(key, value);
    }
}

/// <summary>The reader of a tree with no <see cref="OptionStore"/>: every option reads as its default.</summary>
internal sealed class DefaultOptionReader : IOptionReader
{
    public static readonly DefaultOptionReader Instance = new();
    public T Get<T>(OptionKey<T> key) => key.Default;
}

/// <summary>The sink used until the app installs one: the request is dropped and reported.</summary>
internal sealed class UnhandledOptionRequests : IOptionRequests
{
    public static readonly UnhandledOptionRequests Instance = new();
    public void Set<T>(OptionKey<T> key, T value)
        => Diag.Line($"[options] request to set option '{key.Id}' ignored: no IOptionRequests sink is installed (OptionStore.Requests)");
}
