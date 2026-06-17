using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>
/// The ONE friendly-UX surface every async region uses. Over the engine's <see cref="Skel.Region{T}"/> +
/// <see cref="Loadable{T}"/>: Pending → a shimmer derived from the REAL row template (matched layout), Ready → content
/// (or <see cref="EmptyState"/> when the collection is empty), Failed → <see cref="ErrorState"/> + Retry. The reveal is
/// the engine's blur/stagger. Pair with <c>Context.UseAsyncResource(loader, seed)</c> (which cancels on unmount).
/// </summary>
public static class StatefulRegion
{
    /// <summary>A list/collection region.</summary>
    public static Element List<T>(
        Loadable<T[]> loadable, Func<T?, Element> rowTemplate, int skeletonCount,
        Func<T[], Element> content, Element? empty = null, Action? onRetry = null,
        SkelReveal reveal = SkelReveal.StaggerRows)
        => Skel.Region(
            loadable, rowTemplate, skeletonCount,
            content: items => items.Length == 0 ? (empty ?? EmptyState.Default()) : content(items),
            reveal: reveal,
            onFailed: () => ErrorState.Build(loadable.Error, onRetry));

    /// <summary>A single-subtree region (one card / hero / detail).</summary>
    public static Element Single<T>(
        Loadable<T> loadable, Func<Element> shimmer, Func<T, Element> content,
        Action? onRetry = null, SkelReveal reveal = SkelReveal.Soft)
        => Skel.Region(
            loadable, shimmer, content,
            reveal: reveal,
            onFailed: () => ErrorState.Build(loadable.Error, onRetry));
}
