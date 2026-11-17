using FluentGpu.Signals;

namespace FluentGpu.Hooks;

/// <summary>"Something under me is still loading": a counter that the pending <c>Skel.Region</c>s (and
/// <see cref="RenderContext.UsePendingClaim"/>) under a provider hold claims on. Provide one with
/// <c>Ctx.Provide(LoadingScope.Current, scope, child)</c>; feed <see cref="IsPending"/> to
/// <see cref="RenderContext.UseDelayedPending"/> to draw a "this is taking a while" indicator.</summary>
/// <remarks>The nearest scope wins: a claim never bubbles to an outer scope. A region on its pending branch holds one
/// claim on the scope that resolves from its node; a region inside a subtree parked by <c>Flow.KeepAlive</c> (or a
/// parked virtual row) holds none and claims again on the un-park. The engine never creates a scope: popups and panels
/// mount outside a page's provider and report nowhere unless their host provides one of its own.</remarks>
public sealed class LoadingScope
{
    /// <summary>The nearest scope. Null (the default) = none: a region outside every scope reports nowhere.</summary>
    public static readonly Context<LoadingScope?> Current = new(null);

    int _count;
    readonly Signal<bool> _pending = new(false);

    /// <summary>True while at least one claim is held. Equality-gated: 1 → 2 → 1 claims notify nobody.</summary>
    public IReadSignal<bool> IsPending => _pending;

    /// <summary>The claims held now (diagnostics only).</summary>
    public int Count => _count;

    /// <summary>Take one claim. The first claim raises <see cref="IsPending"/>.</summary>
    public void Enter() { if (_count++ == 0) _pending.Value = true; }

    /// <summary>Release one claim. The last release lowers <see cref="IsPending"/>; a release with none held is ignored.</summary>
    public void Leave() { if (_count > 0 && --_count == 0) _pending.Value = false; }
}
