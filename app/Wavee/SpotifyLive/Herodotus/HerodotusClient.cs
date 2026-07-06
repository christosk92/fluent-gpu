using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee.Backend;
using Wavee.Protocol.Resumption;

namespace Wavee.SpotifyLive;

/// <summary>Herodotus resume-point + play-history head client (bare protobuf, not gRPC-framed).</summary>
public sealed class HerodotusClient
{
    const string PlayHistoryUri = "spotify:list:play-history:v1";
    const string CreateRoute = "/herodotus/spotify.resumption.v1.ResumePointRevisionService/CreateResumePointRevision";
    const string BatchRoute = "/herodotus/spotify.resumption.v1.ResumePointRevisionService/BatchCreateResumePointRevisions";

    readonly ITransport _transport;
    readonly Action<string>? _log;
    readonly IWaveeLog _structuredLog;

    public HerodotusClient(ITransport transport, Action<string>? log = null, IWaveeLog? structuredLog = null)
    {
        _transport = transport;
        _log = log;
        _structuredLog = structuredLog ?? WaveeLog.Instance;
    }

    /// <summary>Writes one or more revisions. A single play-history head uses <c>CreateResumePointRevision</c> (capture
    /// parity); two or more coalesced writes use <c>BatchCreateResumePointRevisions</c> (minimum 2 items).</summary>
    public async Task<bool> WriteAsync(IReadOnlyList<CreateResumePointRevisionRequest> requests, CancellationToken ct = default)
    {
        if (requests.Count == 0) return true;
        if (requests.Count == 1)
        {
            var resp = await PostAsync(CreateRoute, requests[0].ToByteArray(), ct).ConfigureAwait(false);
            return resp.Ok;
        }

        var batch = new BatchCreateResumePointRevisionsRequest();
        batch.Requests.AddRange(requests);
        var batchResp = await PostAsync(BatchRoute, batch.ToByteArray(), ct).ConfigureAwait(false);
        return batchResp.Ok;
    }

    public Task<bool> BatchWriteAsync(IReadOnlyList<CreateResumePointRevisionRequest> requests, CancellationToken ct = default)
        => WriteAsync(requests, ct);

    public async Task<IReadOnlyList<CurrentStateRevision>> ListResumePointsAsync(string entityUri, int limit = 500,
        CancellationToken ct = default)
    {
        var req = new ListResumePointRevisionsRequest { EntityUri = entityUri, Limit = limit };
        var resp = await PostAsync(
            "/herodotus/spotify.resumption.v1.ResumePointRevisionService/ListResumePointRevisions",
            req.ToByteArray(), ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Body.Length == 0) return Array.Empty<CurrentStateRevision>();
        var parsed = ListResumePointRevisionsResponse.Parser.ParseFrom(resp.Body);
        return parsed.Revisions;
    }

    public async Task<long> TryGetEpisodeResumeMicrosAsync(string episodeUri, CancellationToken ct = default)
    {
        var revisions = await ListResumePointsAsync(episodeUri, 1, ct).ConfigureAwait(false);
        if (revisions.Count == 0) return 0;
        return revisions[0].Value?.ResumePoint?.Position ?? 0;
    }

    async Task<Resp> PostAsync(string route, byte[] body, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-protobuf",
        };
        var resp = await _transport.Request(Channel.Spclient, route, body, ct, method: "POST", headers: headers)
            .ConfigureAwait(false);
        if (!resp.Ok)
        {
            _log?.Invoke($"herodotus {route} failed status={resp.Status}");
            _structuredLog.Warn("connect", "herodotus.request.failed", "herodotus request rejected",
                WaveeLogField.Of("route", ShortRoute(route)),
                WaveeLogField.Of("status", resp.Status));
        }
        return resp;
    }

    static string ShortRoute(string route)
    {
        int i = route.LastIndexOf('/');
        return i >= 0 ? route[(i + 1)..] : route;
    }

    static Timestamp CreateTimeFromUnixMs(long createTimeMs) =>
        Timestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(createTimeMs));

    public static CreateResumePointRevisionRequest PlayHistoryHead(string itemUri, long createTimeMs) =>
        new()
        {
            EntityUri = PlayHistoryUri,
            Revision = new CurrentStateRevision
            {
                Value = new CurrentStateValue { ItemUri = itemUri },
                CreateTime = CreateTimeFromUnixMs(createTimeMs),
            },
        };

    public static CreateResumePointRevisionRequest EpisodeResumePoint(string episodeUri, long positionMicros, long createTimeMs) =>
        new()
        {
            EntityUri = episodeUri,
            Revision = new CurrentStateRevision
            {
                Value = new CurrentStateValue
                {
                    EntityUri = episodeUri,
                    ResumePoint = new ResumePoint { Position = positionMicros },
                },
                CreateTime = CreateTimeFromUnixMs(createTimeMs),
            },
        };
}

/// <summary>Writes play-history heads on becoming-current and episode resume-points on leave. Batches on a 2 s timer;
/// a failed flush is requeued (bounded) rather than silently dropped, and the outcome is logged honestly.</summary>
public sealed class ResumePointProjection : IPlaybackProjection
{
    const int MaxFlushAttempts = 5;   // ~10 s of 2 s-tick retries before dropping a persistently-failing batch

    readonly HerodotusClient _client;
    readonly Func<bool> _suppressPlayHistory;
    readonly Action<string>? _log;
    readonly IWaveeLog _structuredLog;
    readonly object _gate = new();
    readonly List<CreateResumePointRevisionRequest> _pending = new();
    string? _currentUri;
    long _lastPositionMs;
    int _failStreak;
    int _flushing;
    Timer? _flushTimer;

    public ResumePointProjection(HerodotusClient client, Func<bool>? suppressPlayHistory = null, Action<string>? log = null,
        IWaveeLog? structuredLog = null)
    {
        _client = client;
        _suppressPlayHistory = suppressPlayHistory ?? (() => false);
        _log = log;
        _structuredLog = structuredLog ?? WaveeLog.Instance;
        _flushTimer = new Timer(_ => _ = FlushAsync(), null, 2000, 2000);
    }

    public void OnEvent(in PlaybackEvent e)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        switch (e.Kind)
        {
            case EvKind.Started:
            case EvKind.TrackChanged:
                if (e.Track is null) return;
                if (IsEpisode(_currentUri) && _currentUri != e.Track.Uri)
                    QueueEpisodeLeave(_currentUri!, _lastPositionMs, now);
                _currentUri = e.Track.Uri;
                _lastPositionMs = e.AtMs;
                if (!_suppressPlayHistory())
                    Enqueue(HerodotusClient.PlayHistoryHead(e.Track.Uri, now));
                break;
            case EvKind.Paused:
            case EvKind.Seeked:
                _lastPositionMs = e.AtMs;
                if (IsEpisode(_currentUri))
                    QueueEpisodeLeave(_currentUri!, _lastPositionMs, now);
                break;
            case EvKind.Ended:
                if (IsEpisode(_currentUri))
                    QueueEpisodeLeave(_currentUri!, _lastPositionMs, now);
                _currentUri = null;
                break;
        }
    }

    void QueueEpisodeLeave(string episodeUri, long positionMs, long createTimeMs)
    {
        long micros = Math.Max(0, positionMs) * 1000L;
        _structuredLog.Debug("connect", "herodotus.episode.leave", "queueing episode resume-point on leave",
            WaveeLogField.Of("episode", WaveeLogRedaction.HashLike(episodeUri)),
            WaveeLogField.Of("positionMs", positionMs));
        Enqueue(HerodotusClient.EpisodeResumePoint(episodeUri, micros, createTimeMs));
        if (!_suppressPlayHistory())
            Enqueue(HerodotusClient.PlayHistoryHead(episodeUri, createTimeMs));
    }

    void Enqueue(CreateResumePointRevisionRequest r)
    {
        lock (_gate) _pending.Add(r);
    }

    async Task FlushAsync()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;   // no overlapping flushes (slow write > 2 s tick)
        try
        {
            CreateResumePointRevisionRequest[] batch;
            lock (_gate)
            {
                if (_pending.Count == 0) return;
                batch = _pending.ToArray();
                _pending.Clear();
            }

            bool ok;
            try
            {
                ok = await _client.WriteAsync(batch).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Requeue(batch, ex.GetType().Name, ex);
                return;
            }

            if (ok)
            {
                lock (_gate) _failStreak = 0;
                _structuredLog.Info("connect", "herodotus.write.ok", "resume-point revisions written",
                    WaveeLogField.Of("count", batch.Length));
                _log?.Invoke($"herodotus wrote {batch.Length} revision(s)");
                return;
            }

            Requeue(batch, "rejected", null);
        }
        finally { Interlocked.Exchange(ref _flushing, 0); }
    }

    /// <summary>Put a failed batch back at the FRONT for the next tick, bounded so a persistently-down herodotus can't
    /// accumulate/retry forever. Revisions are idempotent (server dedups by create_time), so retry is safe.</summary>
    void Requeue(CreateResumePointRevisionRequest[] batch, string reason, Exception? ex)
    {
        int attempt;
        bool giveUp;
        lock (_gate)
        {
            attempt = ++_failStreak;
            giveUp = attempt > MaxFlushAttempts;
            if (!giveUp) _pending.InsertRange(0, batch);
            else _failStreak = 0;
        }

        if (giveUp)
            _structuredLog.Error("connect", "herodotus.write.give_up",
                "resume-point write failed repeatedly; dropping batch",
                WaveeLogField.Of("count", batch.Length),
                WaveeLogField.Of("attempts", attempt),
                WaveeLogField.Of("reason", reason));
        else
            _structuredLog.Warn("connect", "herodotus.write.retry",
                "resume-point write failed; requeued for retry",
                WaveeLogField.Of("count", batch.Length),
                WaveeLogField.Of("attempt", attempt),
                WaveeLogField.Of("max", MaxFlushAttempts),
                WaveeLogField.Of("reason", reason));

        if (ex is not null) _log?.Invoke("herodotus flush error: " + ex.Message);
    }

    static bool IsEpisode(string? uri) =>
        uri is not null && uri.StartsWith("spotify:episode:", StringComparison.Ordinal);

    public void Dispose() => _flushTimer?.Dispose();
}
