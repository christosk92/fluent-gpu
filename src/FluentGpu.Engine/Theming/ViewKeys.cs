using FluentGpu.Dsl;

namespace FluentGpu.Theming;

/// <summary>A typed handle to one replaceable view ("slot"): <c>new ViewKey&lt;PlayerBarProps&gt;("wavee.player.bar@1")</c>.
/// The id is <c>"name@version"</c>; the part before the <c>@</c> is the slot's identity, the version only tells a
/// registry which props contract the template was written against. <typeparamref name="TProps"/> is the immutable props
/// record the slot is rendered with (see <c>component-props-contract.md</c>: equal records skip the template).</summary>
public readonly record struct ViewKey<TProps>(string Id) where TProps : class;

/// <summary>A typed handle to one style object a theme registers next to its views (a table of metrics, a role set, ...),
/// read by a template through <see cref="ViewContext.StyleOf{TStyle}"/>.</summary>
public readonly record struct StyleKey<TStyle>(string Id) where TStyle : class;

/// <summary>A view template: pure props to element. It runs inside the slot host's component, so hooks called through
/// <see cref="ViewContext.Hooks"/> (<c>UseState</c>, <c>UseEffect</c>, <c>UseInterval</c>, ...) live and die with the
/// template: a template change remounts the slot and disposes them.</summary>
public delegate Element ViewTemplate<TProps>(TProps props, in ViewContext ctx) where TProps : class;

/// <summary>The re-pushed props of a slot host (the props channel of <c>Embed.Comp(props, factory)</c>). A record class so
/// the reconciler's props gate compares it by value: equal args skip the template.</summary>
public sealed record ViewSlotArgs<TProps>(ViewKey<TProps> Key, TProps Props) where TProps : class;

internal static class ViewIds
{
    /// <summary>Length of <paramref name="id"/> without its <c>@version</c> suffix.</summary>
    public static int BaseLength(string id)
    {
        int at = id.LastIndexOf('@');
        return at < 0 ? id.Length : at;
    }

    public static bool SameBase(string a, string b)
        => a.AsSpan(0, BaseLength(a)).SequenceEqual(b.AsSpan(0, BaseLength(b)));

    /// <summary>The sibling key <c>"{id without @version}|{callerKey}"</c> in a single allocation.</summary>
    public static string SlotKey(string id, string callerKey)
        => string.Concat(id.AsSpan(0, BaseLength(id)), "|", callerKey);
}
