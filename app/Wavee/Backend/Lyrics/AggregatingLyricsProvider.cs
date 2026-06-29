using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

/// <summary>The app-facing lyrics provider that fans out to every enabled candidate source in parallel, normalizes each to
/// a <see cref="LyricsCandidate"/>, picks the Spotify-native candidate as the reranker reference, and returns the single
/// best <see cref="LyricsDocument"/> (docs/lyrics-aggregator-reranker-plan.md §7). NOT first-hit: a later word-synced
/// candidate can still beat an earlier line-synced one. A per-source miss/timeout/throw degrades to null for that source
/// and never fails the aggregate. Winners are cached by track id; the decision is logged for explainability.</summary>
public sealed class AggregatingLyricsProvider : ILyricsProvider
{
    readonly IReadOnlyList<ILyricCandidateSource> _sources;
    readonly Func<string, CancellationToken, Task<LyricsRequest?>> _resolve;
    readonly LyricsOptions _opt;
    readonly string _referenceSourceId;
    readonly Action<string>? _log;
    readonly Dictionary<string, LyricsDocument> _cache = new();
    readonly object _gate = new();

    public AggregatingLyricsProvider(
        IEnumerable<ILyricCandidateSource> sources,
        Func<string, CancellationToken, Task<LyricsRequest?>> resolveRequest,
        LyricsOptions? options = null,
        string referenceSourceId = "spotify",
        Action<string>? log = null)
    {
        _sources = sources.Where(s => s.Enabled).ToList();
        _resolve = resolveRequest;
        _opt = options ?? LyricsOptions.Default;
        _referenceSourceId = referenceSourceId;
        _log = log;
    }

    public async Task<LyricsDocument?> GetLyricsAsync(string trackId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackId)) return null;
        lock (_gate) if (_cache.TryGetValue(trackId, out var cached)) return cached;

        LyricsRequest? req;
        try { req = await _resolve(trackId, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { req = null; }
        if (req is null) return null;

        // Fan out — each source bounded by its own timeout; a throw/timeout becomes a null candidate (never fails the set).
        var tasks = _sources.Select(s => FetchOne(s, req, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var candidates = results.Where(c => c is not null).Select(c => c!).ToList();
        if (candidates.Count == 0) return null;

        var reference = candidates.FirstOrDefault(c => c.ProviderId == _referenceSourceId)?.Document;
        var ranked = LyricsReranker.Rank(candidates, reference);

        if (ranked.Best is { } b)
            _log?.Invoke($"track={trackId} winner={b.ProviderId} sync={b.Sync} score={b.Score:F3} text={b.TextAgreement:F2} " +
                $"timing={b.TimingScore:F2} offset={b.AppliedOffsetMs}ms candidates=[{string.Join(",", candidates.Select(c => c.ProviderId))}] ({b.Reason})");

        var winner = ranked.Winner;
        if (winner is not null) lock (_gate) _cache[trackId] = winner;
        return winner;
    }

    async Task<LyricsCandidate?> FetchOne(ILyricCandidateSource source, LyricsRequest req, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_opt.PerSourceTimeoutMs);
            return await source.FetchAsync(req, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the CALLER cancelled — propagate
        }
        catch (Exception e)
        {
            _log?.Invoke($"source {source.Id} failed for {req.TrackId}: {e.GetType().Name}");
            return null;   // this source's timeout/error — drop it, keep the rest
        }
    }

    /// <summary>Clear the winner cache (e.g. on logout / provider-config change).</summary>
    public void ClearCache() { lock (_gate) _cache.Clear(); }
}
