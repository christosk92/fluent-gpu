namespace Wavee.Core;

/// <summary>The artist "Popular" chart's two-step pipeline. Step one is the Pathfinder <c>queryArtistOverview</c> seed
/// (~10 tracks, the ONLY source of play counts — see <see cref="IArtistStatsService"/>); step two is this: the SpClient
/// <c>artist-top-tracks-extensions</c> list (up to <see cref="ArtistPopularTracks.ExtendedCap"/> uris, play-count-free)
/// enriched over the shared extended-metadata transport and merged onto the seed.
///
/// Deliberately NOT folded into <see cref="IArtistStatsService"/>: that one is Pathfinder-only with its own TTL, this one
/// is REST + a metadata batch with its own in-flight coalescing, and keeping them apart lets tests swap either half.
/// Like stats, it is standalone-<c>ArtistPage</c>-scoped — the Library artist pane must never fire it.</summary>
public interface IArtistPopularTracksService
{
    /// <summary>Ensure <see cref="Artist.TopTracks"/> is the EXTENDED popular list (overview seed ∪ extensions) and return
    /// it. Idempotent + SWR: a store artist that already carries an extended list within the stats TTL returns without a
    /// request. Best-effort by contract — offline, a non-2xx, or a parse failure returns <paramref name="seed"/> unchanged
    /// rather than blanking an already-painted chart. Cancellation propagates as <see cref="OperationCanceledException"/>.</summary>
    /// <param name="seed">The caller's current list (the overview top-10) — the merge base and the failure fallback.</param>
    Task<IReadOnlyList<Track>> EnsureExtendedAsync(string artistUri, IReadOnlyList<Track> seed, CancellationToken ct = default);
}

/// <summary>The pure merge + the shared caps. Lives here (not in the live service) so the ordering/play-count contract is
/// unit-testable without an HTTP seam, and so the UI can size its pager off the same constant the service writes.</summary>
public static class ArtistPopularTracks
{
    /// <summary>What <c>queryArtistOverview</c> returns. A stored <c>TopTracks</c> longer than this IS an extended list —
    /// that count, not a second timestamp column, is the "already extended" gate.</summary>
    public const int OverviewSeedCap = 10;

    /// <summary>The extended ceiling (Spotify serves ~50). Bounds the metadata batch, the merged list, and therefore the
    /// uris the artist_overview document persists.</summary>
    public const int ExtendedCap = 50;

    /// <summary>Fold the extension list onto the seed. The SEED KEEPS ITS ORDER AND ITS PLAY COUNTS at the head (it is the
    /// only play-count-bearing source; the extension carries uris only), then extension-only tracks append in extension
    /// order. Duplicates collapse by uri, the seed instance always winning. An empty extension returns the seed unchanged
    /// — a failed or empty step two must never reorder a painted chart.</summary>
    public static IReadOnlyList<Track> Merge(IReadOnlyList<Track>? seed, IReadOnlyList<Track>? extension)
    {
        var head = seed ?? Array.Empty<Track>();
        if (extension is not { Count: > 0 })
            return head.Count > ExtendedCap ? Slice(head) : head;

        var merged = new List<Track>(Math.Min(ExtendedCap, head.Count + extension.Count));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < head.Count && merged.Count < ExtendedCap; i++)
        {
            var t = head[i];
            if (t.Uri.Length == 0 || !seen.Add(t.Uri)) continue;
            merged.Add(t);
        }
        for (int i = 0; i < extension.Count && merged.Count < ExtendedCap; i++)
        {
            var t = extension[i];
            if (t.Uri.Length == 0 || !seen.Add(t.Uri)) continue;
            merged.Add(t);
        }
        return merged;
    }

    /// <summary>How many of <paramref name="merged"/> came from beyond the seed (the structured-log "appended" field).</summary>
    public static int AppendedCount(IReadOnlyList<Track>? seed, IReadOnlyList<Track> merged)
        => Math.Max(0, merged.Count - (seed?.Count ?? 0));

    static IReadOnlyList<Track> Slice(IReadOnlyList<Track> tracks)
    {
        var list = new List<Track>(ExtendedCap);
        for (int i = 0; i < ExtendedCap; i++) list.Add(tracks[i]);
        return list;
    }
}

/// <summary>A stable service identity whose live provider can be installed after login without rebuilding the UI tree.</summary>
public sealed class SwitchableArtistPopularTracksService : IArtistPopularTracksService
{
    IArtistPopularTracksService _inner;
    public SwitchableArtistPopularTracksService(IArtistPopularTracksService inner) => _inner = inner;
    public void SetInner(IArtistPopularTracksService inner)
        => System.Threading.Volatile.Write(ref _inner, inner ?? throw new ArgumentNullException(nameof(inner)));

    IArtistPopularTracksService Current => System.Threading.Volatile.Read(ref _inner);
    public Task<IReadOnlyList<Track>> EnsureExtendedAsync(string artistUri, IReadOnlyList<Track> seed, CancellationToken ct = default)
        => Current.EnsureExtendedAsync(artistUri, seed, ct);
}

/// <summary>Offline/fake fallback: the chart keeps whatever the overview (or the cold store) already gave it.</summary>
public sealed class NullArtistPopularTracksService : IArtistPopularTracksService
{
    public Task<IReadOnlyList<Track>> EnsureExtendedAsync(string artistUri, IReadOnlyList<Track> seed, CancellationToken ct = default)
        => Task.FromResult(seed ?? (IReadOnlyList<Track>)Array.Empty<Track>());
}
