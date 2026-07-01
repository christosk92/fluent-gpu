using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

// Explainability for the lyrics aggregator (the "why did this song find no/that lyric" debug surface). A per-search
// LyricsProbe flows (AsyncLocal) into every parallel source task so each source can leave breadcrumbs (query, hit count,
// where it bailed); the aggregator times each source, classifies the outcome, folds in the reranker's per-candidate
// decision, and Publishes one LyricsSearchReport to the process-wide LyricsDiagnostics store the debug panel reads.

public enum LyricsOutcome { Pending, Hit, Miss, Timeout, Error, Skipped }

/// <summary>One source's result within a single track search — outcome + timing + a human "why" (its probe breadcrumbs),
/// plus the reranker's verdict once ranked (score/winner/reason).</summary>
public sealed record LyricsSourceTrace(
    string SourceId,
    LyricsOutcome Outcome,
    long ElapsedMs,
    string Detail,
    LyricsSyncKind Sync,
    int LineCount,
    double Score,
    bool Winner,
    string RerankReason);

/// <summary>The full explainable record of ONE GetLyricsAsync call: the request metadata the sources searched with, the
/// per-source traces, and a one-line summary.</summary>
public sealed record LyricsSearchReport(
    string TrackId,
    string Title,
    string Artist,
    string Album,
    long DurationMs,
    string? Isrc,
    long WhenUnixMs,
    string Summary,
    IReadOnlyList<LyricsSourceTrace> Sources);

/// <summary>Ambient (AsyncLocal) per-search breadcrumb collector. The aggregator sets <see cref="Current"/> before the
/// fan-out; each source calls the static <see cref="Note"/> at its decision points (query, result count, miss reason).
/// Because the fan-out tasks are started while Current is set, the value flows into each task; the probe object itself is
/// shared, so notes from all sources land in one place (thread-safe).</summary>
public sealed class LyricsProbe
{
    public static readonly AsyncLocal<LyricsProbe?> Current = new();

    readonly object _gate = new();
    readonly Dictionary<string, List<string>> _notes = new(StringComparer.Ordinal);

    /// <summary>Record a breadcrumb for <paramref name="sourceId"/> (no-op if no probe is active, e.g. a unit test).</summary>
    public static void Note(string sourceId, string message)
    {
        var p = Current.Value;
        if (p is null) return;
        lock (p._gate)
        {
            if (!p._notes.TryGetValue(sourceId, out var list)) p._notes[sourceId] = list = new List<string>();
            list.Add(message);
        }
    }

    public string NotesFor(string sourceId)
    {
        lock (_gate) return _notes.TryGetValue(sourceId, out var list) ? string.Join("; ", list) : "";
    }
}

/// <summary>Process-wide store of the most recent lyric searches, read by the debug panel. Keyed by track id (latest per
/// track) plus a small recency ring. <see cref="Version"/> bumps on every publish so an open panel can refresh live.</summary>
public static class LyricsDiagnostics
{
    const int Cap = 24;
    static readonly object _gate = new();
    static readonly LinkedList<LyricsSearchReport> _recent = new();
    static readonly Dictionary<string, LyricsSearchReport> _byTrack = new(StringComparer.Ordinal);
    static long _version;

    /// <summary>Monotonic publish counter — read it to detect a fresh report without holding the lock.</summary>
    public static long Version => Interlocked.Read(ref _version);

    public static void Publish(LyricsSearchReport report)
    {
        lock (_gate)
        {
            _byTrack[report.TrackId] = report;
            _recent.AddFirst(report);
            while (_recent.Count > Cap) _recent.RemoveLast();
        }
        Interlocked.Increment(ref _version);
    }

    public static LyricsSearchReport? ForTrack(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return null;
        lock (_gate) return _byTrack.TryGetValue(trackId, out var r) ? r : null;
    }

    public static IReadOnlyList<LyricsSearchReport> Recent()
    {
        lock (_gate) return _recent.ToArray();
    }
}
