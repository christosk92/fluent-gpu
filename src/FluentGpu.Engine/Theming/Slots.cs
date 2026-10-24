using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Theming;

/// <summary>
/// Entry point for view slots: a place in the UI whose content is a template the active <see cref="ViewRegistry"/>
/// supplies. See <c>docs/design/subsystems/theming.md</c> "View slots".
/// </summary>
public static class Slots
{
    /// <summary>
    /// Embed the slot <paramref name="key"/> rendered with <paramref name="props"/>. It is an <c>Embed.Comp(props, factory)</c>
    /// of an internal host component, so the props ride the re-pushed-props channel: an equal props record (record equality
    /// of <see cref="ViewSlotArgs{TProps}"/>) re-renders nothing and runs no template. Props stability is the caller's
    /// contract (<c>component-props-contract.md</c>): a props record holding a delegate or an Element member never compares
    /// equal.
    /// <para>Identity: the slot's sibling <c>Key</c> is <c>"{key id without @version}|{callerKey}"</c> when a
    /// <paramref name="callerKey"/> is given, and unkeyed otherwise (a caller may set its own <c>Key</c> with <c>with</c>;
    /// it is never overwritten here). Pass <paramref name="callerKey"/> whenever the call sits in a loop, <c>Flow.For</c>
    /// or a collection builder. The template id is part of the slot's identity too, but it is resolved by the host from the
    /// registry, not at this call: when the registry resolves a different template id for the key, the host remounts the
    /// template body, disposing the old template's hook cells, effects and timers.</para>
    /// </summary>
    public static Element View<TProps>(ViewKey<TProps> key, TProps props, string? callerKey = null) where TProps : class
    {
        ArgumentNullException.ThrowIfNull(props);
        var el = Embed.Comp<ViewSlotHost<TProps>, ViewSlotArgs<TProps>>(
            new ViewSlotArgs<TProps>(key, props), static () => new ViewSlotHost<TProps>());
        return callerKey is null ? el : el with { Key = ViewIds.SlotKey(key.Id, callerKey) };
    }
}

/// <summary>Reports a slot that could not be resolved, once per key (per registry), through the always-on
/// <see cref="Diag.Line"/> writer (a dropped slot must be visible in a Release build).</summary>
internal static class ViewSlotDiagnostics
{
    private static readonly object s_gate = new();
    private static readonly HashSet<string> s_noRegistryReported = new(StringComparer.Ordinal);

    public static void ReportMissing(ViewRegistry? registry, string id, string reason)
    {
        bool first;
        if (registry is not null) first = registry.FirstMissing(id);
        else lock (s_gate) first = s_noRegistryReported.Add(id);
        if (first) Diag.Line($"[view-slot] view key '{id}' renders nothing: {reason}");
    }
}

/// <summary>What a slot host resolved last, for inspection (<see cref="FluentGpu.Hosting.AppHost.CollectViewSlots"/>): both null
/// while it renders nothing (no registry, an unregistered key).</summary>
internal interface IViewSlotHost
{
    string? ViewId { get; }
    string? TemplateId { get; }
}

/// <summary>One live view slot, for inspection: the host component's anchor node, the view key id it resolved (with its
/// <c>@version</c>) and the template id the registry gave it.</summary>
public readonly record struct ViewSlotInfo(NodeHandle Anchor, string ViewId, string TemplateId);

/// <summary>
/// The component behind <see cref="Slots.View{TProps}"/>. It owns no template state: it resolves the key through the
/// nearest <see cref="ViewRegistry"/> provider and renders a body component with the resolved template. Re-renders when its
/// props record changes or the provided registry changes; never because of an option (the body reads those).
/// <para>Remount rule: the body is one of two component types (A and B). Whenever the resolved template id (or the key's
/// identity) changes, the host renders the other type. The reconciler replaces a component whose type differs (a plain
/// <c>Key</c> on a component's root is inert), which disposes the old body's whole scope: hook cells, effects, intervals,
/// measure hooks.</para>
/// </summary>
internal sealed class ViewSlotHost<TProps> : Component, IViewSlotHost where TProps : class
{
    private static readonly BoxEl s_empty = new();

    private string? _lastId;
    private string? _lastTemplateId;
    private bool _flip;

    string? IViewSlotHost.ViewId => _lastId;
    string? IViewSlotHost.TemplateId => _lastTemplateId;

    public override Element Render()
    {
        var args = UseProps<ViewSlotArgs<TProps>>();
        var registry = UseContext(ViewRegistry.Context);
        string id = args.Key.Id;

        if (registry is null)
        {
            ViewSlotDiagnostics.ReportMissing(null, id, "no ViewRegistry is provided");
            _lastId = null;
            return s_empty;
        }
        if (!registry.TryGetView<TProps>(id, out var template, out var templateId))
        {
            ViewSlotDiagnostics.ReportMissing(registry, id, registry.Contains(id)
                ? "it is registered with a different props type"
                : "it is not registered in the current ViewRegistry");
            _lastId = null;
            return s_empty;
        }

        if (_lastId is not null
            && !(ViewIds.SameBase(_lastId, id) && string.Equals(_lastTemplateId, templateId, StringComparison.Ordinal)))
            _flip = !_flip;
        _lastId = id;
        _lastTemplateId = templateId;

        var body = new ViewSlotBodyArgs<TProps>(args, template, registry);
        return _flip
            ? Embed.Comp<ViewSlotBodyB<TProps>, ViewSlotBodyArgs<TProps>>(body, static () => new ViewSlotBodyB<TProps>())
            : Embed.Comp<ViewSlotBodyA<TProps>, ViewSlotBodyArgs<TProps>>(body, static () => new ViewSlotBodyA<TProps>());
    }
}

/// <summary>What the host hands its body. A record class: the props gate re-renders the body only when the slot args, the
/// template (delegate equality) or the registry (reference, which carries its epoch) changed.</summary>
internal sealed record ViewSlotBodyArgs<TProps>(ViewSlotArgs<TProps> Args, ViewTemplate<TProps> Template, ViewRegistry Registry)
    where TProps : class;

/// <summary>Runs the template. Everything it reads while rendering (props, registry, an option cell, the app roles
/// context) subscribes THIS component, so only it re-runs the template.</summary>
internal abstract class ViewSlotBody<TProps> : Component where TProps : class
{
    private static readonly BoxEl s_empty = new();

    public sealed override Element Render()
    {
        var a = UseProps<ViewSlotBodyArgs<TProps>>();
        var store = UseContext(OptionStore.Context);
        IOptionReader reader = DefaultOptionReader.Instance;
        IOptionRequests requests = UnhandledOptionRequests.Instance;
        if (store is not null) { reader = store; requests = store.RequestForwarder; }
        var ctx = new ViewContext(Context, reader, requests, a.Registry, FluentGpu.Dsl.Motion.ReducedMotion);
        return a.Template(a.Args.Props, in ctx) ?? s_empty;
    }
}

/// <summary>Body type A of the A/B pair (see <see cref="ViewSlotHost{TProps}"/>).</summary>
internal sealed class ViewSlotBodyA<TProps> : ViewSlotBody<TProps> where TProps : class;

/// <summary>Body type B of the A/B pair.</summary>
internal sealed class ViewSlotBodyB<TProps> : ViewSlotBody<TProps> where TProps : class;
