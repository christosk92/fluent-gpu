using System.Linq;

namespace Wavee.Core;

/// <summary>The ordered set of connected sources (docs/architecture.md §4.3). Order matters: the first source that
/// <see cref="ISource.Owns"/> a URI wins single-item routing, so put richer/real sources before the fallback.</summary>
public sealed class SourceRegistry
{
    readonly IReadOnlyList<ISource> _sources;
    public SourceRegistry(IReadOnlyList<ISource> sources) => _sources = sources;

    public IReadOnlyList<ISource> All => _sources;

    /// <summary>Catalog-capable sources, in registry order.</summary>
    public IEnumerable<ICatalogSource> CatalogSources =>
        _sources.OfType<ICatalogSource>().Where(s => (s.Capabilities & SourceCapabilities.Catalog) != 0);

    /// <summary>The first catalog source that owns <paramref name="uri"/> (null if none — the aggregate then falls back).</summary>
    public ICatalogSource? OwnerOf(string uri) => CatalogSources.FirstOrDefault(s => s.Owns(uri));

    /// <summary>Sources that declare a capability, in registry order — the hook the future per-facet federation
    /// (FederatedPlayback / FederatedRemote) routes through.</summary>
    public IEnumerable<ISource> OfCapability(SourceCapabilities cap) => _sources.Where(s => (s.Capabilities & cap) != 0);
}
