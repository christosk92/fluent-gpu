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
public sealed class AggregatingLyricsProvider : IUpgradingLyricsProvider
{
    readonly IReadOnlyList<ILyricCandidateSource> _sources;
    readonly Func<string, CancellationToken, Task<LyricsRequest?>> _resolve;
    readonly LyricsOptions _opt;
    readonly string _referenceSourceId;
    readonly Action<string>? _log;
    readonly Dictionary<string, LyricsDocument> _cache = new();
    readonly SimpleEvent<LyricsDocument> _upgrades = new();
    readonly object _gate = new();
    // Bound the winner cache: a long session touches thousands of distinct tracks and each LyricsDocument is tens of KB
    // (word-synced). A miss re-fetches (self-healing), so an LRU cap is safe. Touched/evicted under _gate; MRU at the end.
    const int CacheCap = 64;
    readonly List<string> _lru = new();
    void TouchLru(string id) { _lru.Remove(id); _lru.Add(id); }
    void EvictLru() { while (_lru.Count > CacheCap) { var oldest = _lru[0]; _lru.RemoveAt(0); _cache.Remove(oldest); } }
    public IObservable<LyricsDocument> LyricsUpgraded => _upgrades;

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

    readonly record struct Probed(LyricsCandidate? Cand, LyricsOutcome Outcome, long Ms, string Detail);

    // An exact-recording word-synced lyric (matched by Spotify identity or ISRC) is the best result possible — nothing a
    // slower source could return beats it, so the moment one arrives we stop waiting.
    static bool IsGold(LyricsCandidate? c)
        => c is { Sync: LyricsSyncKind.Syllable, Basis: MatchBasis.Identity or MatchBasis.Isrc };

    public async Task<LyricsDocument?> GetLyricsAsync(string trackId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackId)) return null;
        lock (_gate) if (_cache.TryGetValue(trackId, out var cached)) { TouchLru(trackId); return cached; }

        LyricsRequest? req;
        try { req = await _resolve(trackId, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { req = null; }
        if (req is null)
        {
            LyricsDiagnostics.Publish(new LyricsSearchReport(trackId, "", "", "", 0L, null,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                "could not resolve track metadata — no title/artist to search with", Array.Empty<LyricsSourceTrace>()));
            return null;
        }

        // Ambient probe: flows (AsyncLocal) into each parallel source task so a source can record WHY it missed.
        var probe = new LyricsProbe();
        LyricsProbe.Current.Value = probe;

        // Fan out in parallel. The UI waits only for the short first-hit grace window; slower sources keep running in the
        // background and can publish a richer replacement without delaying the initial lyric.
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var srcCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var started = _sources.Select(s => (Source: s, Task: FetchOne(s, req, probe, srcCts.Token))).ToList();
        var collected = new Dictionary<string, Probed>(StringComparer.Ordinal);

        var pending = started.ToList();
        Task? grace = null;
        bool goldCollected = false;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();   // real caller cancel → propagate (sources return Skipped, not throw)
            var waiters = new List<Task>(pending.Count + 1);
            foreach (var p in pending) waiters.Add(p.Task);
            if (grace is not null) waiters.Add(grace);

            var done = await Task.WhenAny(waiters).ConfigureAwait(false);
            if (ReferenceEquals(done, grace)) break;   // grace window elapsed → stop waiting for slow stragglers

            int idx = pending.FindIndex(p => ReferenceEquals(p.Task, done));
            var entry = pending[idx];
            pending.RemoveAt(idx);
            var pr = await entry.Task.ConfigureAwait(false);   // FetchOne never throws
            collected[entry.Source.Id] = pr;
            if (grace is null && pr.Cand is not null)
                grace = Task.Delay(Math.Clamp(_opt.FirstHitGraceMs, 0, int.MaxValue));
            if (IsGold(pr.Cand)) goldCollected = true;
            if (goldCollected && (collected.ContainsKey(_referenceSourceId) || !pending.Any(p => p.Source.Id == _referenceSourceId)))
                break;   // gold is unbeatable, but keep the Spotify reference when it is already nearly here
        }
        bool continueInBackground = pending.Count > 0;

        var candidates = collected.Values.Where(p => p.Cand is not null).Select(p => p.Cand!).ToList();
        var reference = candidates.FirstOrDefault(c => c.ProviderId == _referenceSourceId)?.Document;
        RankedLyrics ranked = candidates.Count > 0
            ? LyricsReranker.Rank(candidates, reference)
            : new RankedLyrics(null, null, Array.Empty<LyricsDecision>());

        // Fold the reranker verdicts back into the per-source traces (a source we stopped waiting on is "skipped").
        string? winnerId = ranked.Best?.ProviderId;
        var traces = new List<LyricsSourceTrace>(_sources.Count);
        foreach (var s in _sources)
        {
            var dec = ranked.All.FirstOrDefault(d => d.ProviderId == s.Id);
            if (collected.TryGetValue(s.Id, out var pr))
                traces.Add(new LyricsSourceTrace(s.Id, pr.Outcome, pr.Ms, pr.Detail,
                    pr.Cand?.Sync ?? LyricsSyncKind.None, pr.Cand?.LineCount ?? 0,
                    dec?.Score ?? 0d, dec is not null && s.Id == winnerId, dec?.Reason ?? ""));
            else
                traces.Add(new LyricsSourceTrace(s.Id, LyricsOutcome.Skipped, 0L,
                    continueInBackground ? "background still checking richer sources" : "skipped — a faster match returned first",
                    LyricsSyncKind.None, 0, 0d, false, ""));
        }

        int hits = candidates.Count;
        int ran = collected.Count;
        string summary = hits == 0
            ? $"0/{ran} sources returned lyrics — no match anywhere"
            : ranked.Best is { } sb
                ? $"{hits}/{ran} returned; winner={sb.ProviderId} ({sb.Sync}, score {sb.Score:F2}, offset {sb.AppliedOffsetMs}ms)"
                : $"{hits}/{ran} returned";
        LyricsDiagnostics.Publish(new LyricsSearchReport(
            trackId, req.Title, req.ArtistsJoined, req.Album, req.DurationMs, req.Isrc,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), summary, traces));

        if (ranked.Best is { } b)
            _log?.Invoke($"track={trackId} winner={b.ProviderId} sync={b.Sync} score={b.Score:F3} text={b.TextAgreement:F2} " +
                $"timing={b.TimingScore:F2} offset={b.AppliedOffsetMs}ms candidates=[{string.Join(",", candidates.Select(c => c.ProviderId))}] ({b.Reason})");

        var winner = ranked.Winner;
        if (winner is not null) lock (_gate) { _cache[trackId] = winner; TouchLru(trackId); EvictLru(); }
        if (continueInBackground && winner is not null && Richness(winner) < 3)
            _ = ContinueForUpgradeAsync(trackId, req, srcCts, pending, collected, winner, startedAt);
        else
        {
            srcCts.Cancel();
            srcCts.Dispose();
        }
        return winner;
    }

    async Task ContinueForUpgradeAsync(
        string trackId,
        LyricsRequest req,
        CancellationTokenSource srcCts,
        List<(ILyricCandidateSource Source, Task<Probed> Task)> pending,
        Dictionary<string, Probed> collected,
        LyricsDocument initialWinner,
        long startedAt)
    {
        try
        {
            long elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            long remaining = _opt.TotalTimeoutMs - elapsed;
            if (remaining <= 0) return;

            Task budget = Task.Delay((int)Math.Min(int.MaxValue, remaining), srcCts.Token);
            while (pending.Count > 0)
            {
                var waiters = new List<Task>(pending.Count + 1);
                foreach (var p in pending) waiters.Add(p.Task);
                waiters.Add(budget);

                var done = await Task.WhenAny(waiters).ConfigureAwait(false);
                if (ReferenceEquals(done, budget)) break;

                int idx = pending.FindIndex(p => ReferenceEquals(p.Task, done));
                if (idx < 0) continue;
                var entry = pending[idx];
                pending.RemoveAt(idx);
                var pr = await entry.Task.ConfigureAwait(false);
                collected[entry.Source.Id] = pr;

                if (IsGold(pr.Cand) &&
                    (collected.ContainsKey(_referenceSourceId) || !pending.Any(p => p.Source.Id == _referenceSourceId)))
                    break;
            }

            var candidates = collected.Values.Where(p => p.Cand is not null).Select(p => p.Cand!).ToList();
            var reference = candidates.FirstOrDefault(c => c.ProviderId == _referenceSourceId)?.Document;
            RankedLyrics ranked = candidates.Count > 0
                ? LyricsReranker.Rank(candidates, reference)
                : new RankedLyrics(null, null, Array.Empty<LyricsDecision>());

            PublishReport(trackId, req, collected, ranked, candidates, "background complete");
            LogDecision(trackId, ranked, candidates);

            var winner = ranked.Winner;
            if (winner is null || !IsRicher(winner, initialWinner)) return;

            bool promoted = false;
            lock (_gate)
            {
                if (!_cache.TryGetValue(trackId, out var current) || IsRicher(winner, current))
                {
                    _cache[trackId] = winner;
                    TouchLru(trackId);
                    EvictLru();
                    promoted = true;
                }
            }

            if (promoted) _upgrades.OnNext(winner);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _log?.Invoke($"background lyrics upgrade failed for {trackId}: {e.GetType().Name}");
        }
        finally
        {
            srcCts.Cancel();
            srcCts.Dispose();
        }
    }

    void PublishReport(
        string trackId,
        LyricsRequest req,
        IReadOnlyDictionary<string, Probed> collected,
        RankedLyrics ranked,
        IReadOnlyList<LyricsCandidate> candidates,
        string suffix)
    {
        string? winnerId = ranked.Best?.ProviderId;
        var traces = new List<LyricsSourceTrace>(_sources.Count);
        foreach (var s in _sources)
        {
            var dec = ranked.All.FirstOrDefault(d => d.ProviderId == s.Id);
            if (collected.TryGetValue(s.Id, out var pr))
                traces.Add(new LyricsSourceTrace(s.Id, pr.Outcome, pr.Ms, pr.Detail,
                    pr.Cand?.Sync ?? LyricsSyncKind.None, pr.Cand?.LineCount ?? 0,
                    dec?.Score ?? 0d, dec is not null && s.Id == winnerId, dec?.Reason ?? ""));
            else
                traces.Add(new LyricsSourceTrace(s.Id, LyricsOutcome.Skipped, 0L,
                    suffix.Length > 0 ? suffix : "skipped — a faster match returned first",
                    LyricsSyncKind.None, 0, 0d, false, ""));
        }

        int hits = candidates.Count;
        int ran = collected.Count;
        string summary = hits == 0
            ? $"0/{ran} sources returned lyrics — no match anywhere"
            : ranked.Best is { } sb
                ? $"{hits}/{ran} returned; winner={sb.ProviderId} ({sb.Sync}, score {sb.Score:F2}, offset {sb.AppliedOffsetMs}ms)"
                : $"{hits}/{ran} returned";
        if (suffix.Length > 0) summary += $" — {suffix}";
        LyricsDiagnostics.Publish(new LyricsSearchReport(
            trackId, req.Title, req.ArtistsJoined, req.Album, req.DurationMs, req.Isrc,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), summary, traces));
    }

    void LogDecision(string trackId, RankedLyrics ranked, IReadOnlyList<LyricsCandidate> candidates)
    {
        if (ranked.Best is { } b)
            _log?.Invoke($"track={trackId} winner={b.ProviderId} sync={b.Sync} score={b.Score:F3} text={b.TextAgreement:F2} " +
                $"timing={b.TimingScore:F2} offset={b.AppliedOffsetMs}ms candidates=[{string.Join(",", candidates.Select(c => c.ProviderId))}] ({b.Reason})");
    }

    static bool IsRicher(LyricsDocument next, LyricsDocument current)
    {
        int nr = Richness(next), cr = Richness(current);
        if (nr != cr) return nr > cr;
        if (nr < 3) return false;
        return SyllableCount(next) > SyllableCount(current);
    }

    static int Richness(LyricsDocument doc)
    {
        if (doc.Lines.Any(l => l.IsWordByWord && l.Syllables.Count > 0)) return 3;
        return doc.Sync switch
        {
            LyricsSyncKind.Syllable => 3,
            LyricsSyncKind.Line => 2,
            LyricsSyncKind.Unsynced => 1,
            _ => 0,
        };
    }

    static int SyllableCount(LyricsDocument doc)
    {
        int n = 0;
        foreach (var l in doc.Lines) n += l.Syllables.Count;
        return n;
    }

    async Task<Probed> FetchOne(ILyricCandidateSource source, LyricsRequest req, LyricsProbe probe, CancellationToken ct)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        long Ms() => (long)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        string With(string head) { var n = probe.NotesFor(source.Id); return n.Length > 0 ? head + " — " + n : head; }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_opt.PerSourceTimeoutMs);
        try
        {
            var c = await source.FetchAsync(req, cts.Token).ConfigureAwait(false);
            if (c is null) return new Probed(null, LyricsOutcome.Miss, Ms(), With("no match"));
            return new Probed(c, LyricsOutcome.Hit, Ms(), With($"{c.Sync}, {c.LineCount} lines, basis={c.Basis}"));
        }
        catch (OperationCanceledException)
        {
            // Our per-source CancelAfter fired = a real timeout; otherwise the aggregate cancelled us (early-exit / caller).
            bool timedOut = cts.IsCancellationRequested && !ct.IsCancellationRequested;
            return new Probed(null, timedOut ? LyricsOutcome.Timeout : LyricsOutcome.Skipped, Ms(),
                timedOut ? With($"timed out (> {_opt.PerSourceTimeoutMs}ms)") : "cancelled");
        }
        catch (Exception e)
        {
            _log?.Invoke($"source {source.Id} failed for {req.TrackId}: {e.GetType().Name}");
            return new Probed(null, LyricsOutcome.Error, Ms(), With($"{e.GetType().Name}: {e.Message}"));
        }
    }

    /// <summary>Clear the winner cache (e.g. on logout / provider-config change).</summary>
    public void ClearCache() { lock (_gate) { _cache.Clear(); _lru.Clear(); } }
}
