namespace Wavee.Core;

/// <summary>The in-process Podcasts source (docs/architecture.md §2, §9): synthesizes shows + episodes (the export has
/// none) and owns the <c>wavee:show:*</c> / <c>wavee:episode:*</c> namespace. Declares only the Podcasts capability, so
/// the aggregate routes podcast reads here via <c>OfCapability(Podcasts)</c> — capability-segregated, like every facet.</summary>
public sealed class FakePodcastSource : IPodcastSource
{
    public string Id => "podcasts";
    public bool Owns(string uri) =>
        uri.StartsWith("wavee:show:", System.StringComparison.Ordinal) || uri.StartsWith("wavee:episode:", System.StringComparison.Ordinal);
    public SourceCapabilities Capabilities => SourceCapabilities.Podcasts;

    public Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct = default) => Task.FromResult(FakeData.Shows());
    public Task<Show?> GetShowAsync(string uri, CancellationToken ct = default) => Task.FromResult(FakeData.Show(uri));
}
