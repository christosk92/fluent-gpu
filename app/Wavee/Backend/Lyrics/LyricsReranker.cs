using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using static Wavee.Backend.Lyrics.LyricsText;

namespace Wavee.Backend.Lyrics;

/// <summary>Per-candidate decision record (docs plan §9) — kept so a wrong pick is explainable (which provider, why it
/// won/lost, what offset was applied).</summary>
public sealed record LyricsDecision(
    string ProviderId, LyricsSyncKind Sync, double Score,
    double TextAgreement, double Coverage, double TimingScore, long AppliedOffsetMs, string Reason);

public sealed record RankedLyrics(LyricsDocument? Winner, LyricsDecision? Best, IReadOnlyList<LyricsDecision> All);

/// <summary>The reranker (docs/lyrics-aggregator-reranker-plan.md §9): score every candidate together against the
/// Spotify-native reference (text agreement + timing coherence + sync tier + coverage + provider prior), correct a
/// constant timing offset on the winner, and gate word-synced candidates so a WRONG word-synced lyric can't beat a
/// correct line-synced one. Pure + deterministic → unit-testable.</summary>
public static class LyricsReranker
{
    const double WText = 0.40, WSync = 0.25, WTiming = 0.20, WCoverage = 0.10, WPrior = 0.05;
    const double LineMatchThreshold = 0.5;   // token overlap to call two lines "the same line"
    const double DriftToleranceMs = 600;     // MAD above this ⇒ timing is locally wrong
    const double SyncGateText = 0.80, SyncGateTiming = 0.5, SyncGateCoverage = 0.6;

    public static RankedLyrics Rank(IReadOnlyList<LyricsCandidate> candidates, LyricsDocument? reference)
    {
        if (candidates.Count == 0) return new RankedLyrics(null, null, Array.Empty<LyricsDecision>());

        List<string[]>? refTokens = reference is null ? null
            : reference.Lines.Select(l => Tokens(l.Text)).ToList();

        var decisions = new List<LyricsDecision>(candidates.Count);
        int bestIdx = -1; double bestScore = double.NegativeInfinity; long bestOffset = 0;

        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var candTokens = c.Document.Lines.Select(l => Tokens(l.Text)).ToList();

            double text, coverage, timing; long applied = 0; string reason;
            if (refTokens is { Count: > 0 } && candTokens.Count > 0)
            {
                var (lcs, pairs) = LcsAlign(candTokens, refTokens);
                int overlap = Math.Min(candTokens.Count, refTokens.Count);
                text = lcs / (double)Math.Max(1, overlap);
                coverage = overlap / (double)Math.Max(1, Math.Max(candTokens.Count, refTokens.Count));
                (timing, applied) = TimingScoreVsRef(c.Document, reference!, pairs);
                reason = $"ref-align lcs={lcs}/{overlap} off={applied}ms";
            }
            else
            {
                // No reference: cannot verify content. Credit coverage + intrinsic timing sanity; stay text-neutral so a
                // word-synced, internally-coherent candidate can still win on sync/timing.
                text = candTokens.Count > 0 ? 0.6 : 0.0;
                coverage = Math.Min(1.0, candTokens.Count / 10.0);
                timing = IntrinsicTimingSanity(c.Document);
                reason = "no-reference";
            }

            double sync = c.Sync switch { LyricsSyncKind.Syllable => 1.0, LyricsSyncKind.Line => 0.6, LyricsSyncKind.Unsynced => 0.2, _ => 0.2 };
            // Sync gate: a word-synced candidate keeps its sync advantage ONLY if it's verified correct. Otherwise it is
            // demoted below a clean line-synced candidate (prevents a bad richsync beating Spotify's correct line lyric).
            if (c.Sync == LyricsSyncKind.Syllable && refTokens is { Count: > 0 }
                && !(text >= SyncGateText && timing >= SyncGateTiming && coverage >= SyncGateCoverage))
            {
                sync = 0.45;
                reason += " [sync-gate:demoted]";
            }

            double score = WText * text + WSync * sync + WTiming * timing + WCoverage * coverage + WPrior * Math.Clamp(c.Prior, 0, 1);
            decisions.Add(new LyricsDecision(c.ProviderId, c.Sync, score, text, coverage, timing, applied, reason));
            if (score > bestScore) { bestScore = score; bestIdx = i; bestOffset = applied; }
        }

        var winnerCand = candidates[bestIdx];
        var winner = ApplyOffset(winnerCand.Document, bestOffset);
        return new RankedLyrics(winner, decisions[bestIdx], decisions);
    }

    // ── text agreement: fuzzy line-sequence LCS ──────────────────────────────────────────────────────────────────────

    static string[] Tokens(string text)
    {
        var n = Normalize(text);
        return n.Length == 0 ? Array.Empty<string>() : n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    static bool LineMatch(string[] a, string[] b)
    {
        if (a.Length == 0 && b.Length == 0) return true;
        if (a.Length == 0 || b.Length == 0) return false;
        var set = new HashSet<string>(a);
        int inter = 0;
        foreach (var t in b) if (set.Contains(t)) inter++;
        double overlap = inter / (double)Math.Min(a.Length, b.Length);   // overlap coefficient (length-robust)
        return overlap >= LineMatchThreshold;
    }

    /// <summary>LCS over the two line sequences with fuzzy line equality; returns the LCS length and the matched index
    /// pairs (candIndex, refIndex) in order — the pairs drive the timing-offset estimate.</summary>
    static (int Lcs, List<(int C, int R)> Pairs) LcsAlign(List<string[]> cand, List<string[]> reff)
    {
        int n = cand.Count, m = reff.Count;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = LineMatch(cand[i], reff[j]) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var pairs = new List<(int, int)>();
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (LineMatch(cand[i], reff[j])) { pairs.Add((i, j)); i++; j++; }
            else if (dp[i + 1, j] >= dp[i, j + 1]) i++;
            else j++;
        }
        return (dp[0, 0], pairs);
    }

    // ── timing ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static (double Score, long AppliedOffsetMs) TimingScoreVsRef(LyricsDocument cand, LyricsDocument reff, List<(int C, int R)> pairs)
    {
        if (pairs.Count < 3) return (0.6, 0);   // too few matches → neutral, don't auto-correct
        var deltas = new long[pairs.Count];
        for (int k = 0; k < pairs.Count; k++)
            deltas[k] = cand.Lines[pairs[k].C].StartMs - reff.Lines[pairs[k].R].StartMs;
        long median = Median(deltas);
        var absDev = deltas.Select(d => Math.Abs(d - median)).ToArray();
        long mad = Median(absDev);
        double score = Math.Clamp(1.0 - mad / DriftToleranceMs, 0.0, 1.0);
        // Correct the constant offset only when internally coherent (low drift); otherwise the times are locally wrong and
        // shifting wouldn't help — leave it and let the low score demote it.
        long applied = mad <= DriftToleranceMs ? -median : 0;
        return (score, applied);
    }

    static double IntrinsicTimingSanity(LyricsDocument doc)
    {
        if (doc.Lines.Count < 2) return 0.4;
        int monotonic = 0;
        for (int i = 1; i < doc.Lines.Count; i++)
            if (doc.Lines[i].StartMs >= doc.Lines[i - 1].StartMs) monotonic++;
        return 0.3 + 0.6 * (monotonic / (double)(doc.Lines.Count - 1));
    }

    static long Median(long[] v)
    {
        if (v.Length == 0) return 0;
        var s = (long[])v.Clone();
        Array.Sort(s);
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    /// <summary>Shift every line + syllable timestamp by <paramref name="offsetMs"/> and record it on the document.</summary>
    public static LyricsDocument ApplyOffset(LyricsDocument doc, long offsetMs)
    {
        if (offsetMs == 0) return doc;
        long Shift(long t) => Math.Max(0, t + offsetMs);
        var lines = doc.Lines.Select(l => l with
        {
            StartMs = Shift(l.StartMs),
            EndMs = l.EndMs is { } e ? Shift(e) : (long?)null,
            Syllables = l.Syllables.Count == 0 ? l.Syllables
                : l.Syllables.Select(s => new LyricSyllable(Shift(s.StartMs), Shift(s.EndMs), s.Text)).ToList(),
        }).ToList();
        return doc with { Lines = lines, OffsetMsApplied = offsetMs };
    }
}
