using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

public enum PutStateReasonKind { NewConnection, PlayerStateChanged, VolumeChanged, BecameInactive }

public readonly record struct SnapshotTrack(
    string Uri, string Uid, string Provider, string Title, string AlbumTitle,
    string ArtistUri, string ArtistName, string AlbumUri, string ImageUrl,
    bool HasVideo, int ViewIndex, IReadOnlyDictionary<string, string> Metadata);

public readonly record struct LocalPlaybackSnapshot(
    SnapshotTrack Track, string? ContextUri, long PositionMs, long DurationMs,
    bool IsPlaying, bool IsPaused, bool Shuffle, RepeatMode Repeat,
    IReadOnlyList<SnapshotTrack> PrevTracks, IReadOnlyList<SnapshotTrack> NextTracks,
    IReadOnlyDictionary<string, string> ContextMetadata, int ContextIndex,
    string InteractionId, string PageInstanceId, string QueueRevision,
    string SessionId, string PlaybackId, long HasBeenPlayingForMs, long StartedPlayingAtMs, double Volume01 = 0.0);

public sealed class DeviceStatePublisher : IPlaybackProjection, IDisposable
{
    const int MaxWirePrevTracks = 50;
    const int MaxWireNextTracks = 50;

    readonly ITransport _transport;
    readonly string _deviceId;
    readonly IPlaybackState _state;
    readonly Func<string?> _connectionId;
    readonly Func<PutStateReasonKind, LocalPlaybackSnapshot?, uint, bool, byte[]> _build;
    readonly Action<byte[]>? _onCluster;
    readonly Action<string>? _log;
    readonly Func<long> _now;
    readonly IDisposable _connSub;
    readonly object _gate = new();
    uint _messageId;
    string _sessionId = "";
    string _playbackId = "";
    string _interactionId = "";
    string _pageInstanceId = "";
    string _queueRevision = "";
    ulong _queueRevisionCounter = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
    string? _sessionContextUri;
    long _startedPlayingAtMs;
    bool _transportPaused;
    string _lastPublishKey = "";
    readonly TrailingCoalescer _volumeTx;

    public DeviceStatePublisher(
        ITransport transport, string deviceId, IPlaybackState state,
        IObservable<string?> connectionId, Func<string?> currentConnectionId,
        Func<PutStateReasonKind, LocalPlaybackSnapshot?, uint, bool, byte[]> build,
        Action<byte[]>? onCluster = null, Action<string>? log = null, Func<long>? clock = null,
        int volumePublishWindowMs = 400, Func<int, CancellationToken, Task>? delay = null)
    {
        _transport = transport;
        _deviceId = deviceId;
        _state = state;
        _connectionId = currentConnectionId;
        _build = build;
        _onCluster = onCluster;
        _log = log;
        _now = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _volumeTx = new TrailingCoalescer(volumePublishWindowMs, _now, delay);
        _connSub = connectionId.Subscribe(Observers.From<string?>(OnConnectionId));
    }

    void OnConnectionId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _ = PublishAsync(PutStateReasonKind.NewConnection, IsLocallyPlaying());
    }

    public void OnEvent(in PlaybackEvent e)
    {
        if (e.Kind is EvKind.Paused)
            lock (_gate) _transportPaused = true;
        else if (e.Kind is EvKind.Started or EvKind.Resumed or EvKind.TrackChanged or EvKind.Ended or EvKind.BecameInactive)
            lock (_gate) _transportPaused = false;

        if (e.Kind is EvKind.Started or EvKind.TrackChanged)
        {
            lock (_gate)
            {
                var ctx = _state.ContextUri;
                if (e.Kind == EvKind.Started || ctx != _sessionContextUri)
                {
                    _sessionId = e.Ids?.SessionId ?? NewId();
                    _sessionContextUri = ctx;
                    _interactionId = e.Ids?.InteractionId ?? NewDashedUuid();
                    _pageInstanceId = e.Ids?.PageInstanceId ?? NewDashedUuid();
                }
                _playbackId = e.Ids?.PlaybackIdHex ?? NewId();
                if (_startedPlayingAtMs == 0) _startedPlayingAtMs = _now();
                BumpQueueRevision();
            }
        }
        else if (e.Kind is EvKind.QueueChanged or EvKind.OptionsChanged)
        {
            lock (_gate) BumpQueueRevision();
        }
        else if (e.Kind is EvKind.Ended or EvKind.BecameInactive)
        {
            lock (_gate) _startedPlayingAtMs = 0;
        }

        bool isActive = _state.CurrentTrack is not null && e.Kind is not (EvKind.Ended or EvKind.BecameInactive);
        var reason = e.Kind switch
        {
            EvKind.VolumeChanged => PutStateReasonKind.VolumeChanged,
            EvKind.BecameInactive => PutStateReasonKind.BecameInactive,
            _ => PutStateReasonKind.PlayerStateChanged,
        };
        if (reason == PutStateReasonKind.VolumeChanged)
            _volumeTx.Post(() => _ = PublishAsync(PutStateReasonKind.VolumeChanged, _state.CurrentTrack is not null));
        else
            _ = PublishAsync(reason, isActive);
    }

    public void PublishInactive() => _ = PublishAsync(PutStateReasonKind.BecameInactive, false);

    bool IsLocallyPlaying() => _state.CurrentTrack is not null && _state.IsPlaying;

    async Task PublishAsync(PutStateReasonKind reason, bool isActive)
    {
        var connId = _connectionId();
        if (string.IsNullOrEmpty(connId)) return;

        var snap = BuildSnapshot();
        uint mid;
        lock (_gate)
        {
            string key = reason + "|" + isActive + "|" + (snap?.Track.Uri ?? "") + "|" + (snap?.Track.Uid ?? "")
                + "|" + (snap?.IsPlaying ?? false) + "|" + (snap?.IsPaused ?? false) + "|" + (snap?.Shuffle ?? false) + "|" + (snap?.Repeat ?? RepeatMode.Off)
                + "|" + ((snap?.PositionMs ?? 0) / 1000) + "|" + (int)Math.Round((snap?.Volume01 ?? 0) * 100) + "|" + NextSig(snap);
            if (reason is PutStateReasonKind.PlayerStateChanged or PutStateReasonKind.VolumeChanged
                && key == _lastPublishKey) return;
            _lastPublishKey = key;
            mid = ++_messageId;
        }

        try
        {
            var bytes = _build(reason, snap, mid, isActive);
            var resp = await _transport.Publish(_deviceId, connId!, bytes).ConfigureAwait(false);
            if (resp.Ok)
            {
                _log?.Invoke($"put-state ({reason}, active={isActive}, track={snap?.Track.Uri ?? "-"})");
                if (resp.Body.Length > 0) _onCluster?.Invoke(resp.Body);
            }
            else
            {
                _log?.Invoke($"put-state failed ({resp.Status})");
                WaveeLog.Instance.Warn("connect", "put-state.rejected", "connect-state PUT rejected by server",
                    WaveeLogField.Of("status", resp.Status),
                    WaveeLogField.Of("reason", reason.ToString()),
                    WaveeLogField.Of("track", snap?.Track.Uri ?? "-"));
            }
        }
        catch (Exception ex)
        {
            // Structured + full exception (type + stack) so a future null/serialization fault in the builder is
            // diagnosable at a glance — the bare ex.Message alone made the Restrictions NRE cryptic.
            _log?.Invoke("put-state error: " + ex.Message);
            WaveeLog.Instance.Error("connect", "put-state.error", "connect-state PUT threw while building/publishing", ex,
                WaveeLogField.Of("reason", reason.ToString()),
                WaveeLogField.Of("active", isActive),
                WaveeLogField.Of("track", snap?.Track.Uri ?? "-"));
        }
    }

    LocalPlaybackSnapshot? BuildSnapshot()
    {
        var t = _state.CurrentTrack;
        if (t is null) return null;

        var prev = new List<SnapshotTrack>();
        var next = new List<SnapshotTrack>();
        string trackUid = "";
        bool nowAutoplay = false;
        QueueEntry? nowEntry = null;

        foreach (var qe in _state.Queue)
        {
            if (qe.Bucket == QueueBucket.NowPlaying)
            {
                trackUid = qe.Uid;
                nowAutoplay = qe.IsAutoplay;
                nowEntry = qe;
            }
        }

        int currentIndex = 0;
        int nextContextIndex = currentIndex + 1;
        foreach (var qe in _state.Queue)
        {
            if (qe.Bucket is QueueBucket.NowPlaying or QueueBucket.History) continue;
            string provider = ProviderOf(qe);
            int viewIndex = IsContextProvider(provider) ? nextContextIndex++ : -1;
            next.Add(ToSnapshotTrack(qe, provider, viewIndex));
        }

        var currentSource = nowEntry ?? new QueueEntry(QueueItemId.None, "now", t, QueueBucket.NowPlaying,
            nowAutoplay ? QueueProvider.Autoplay : QueueProvider.Context, nowAutoplay, trackUid);
        var current = ToSnapshotTrack(currentSource, ProviderOf(currentSource), currentIndex);
        IReadOnlyDictionary<string, string> metadata = _state is NowPlayingProjection p
            ? p.ContextMetadata
            : new Dictionary<string, string>();

        long started, hasBeen; string sid, pid, iid, page, rev; bool transportPaused;
        lock (_gate)
        {
            started = _startedPlayingAtMs; sid = _sessionId; pid = _playbackId;
            iid = _interactionId; page = _pageInstanceId; rev = _queueRevision;
            transportPaused = _transportPaused;
            hasBeen = started > 0 && _state.IsPlaying ? Math.Max(0, _now() - started) : 0;
        }

        // Connect wire: paused is a sub-state of playing (transport engaged, audio stopped). Ended/stopped ⇒ both false.
        bool wirePaused = transportPaused;
        bool wirePlaying = _state.IsPlaying || wirePaused;

        var wirePrev = CapPrev(prev);
        var wireNext = CapNext(next);

        return new LocalPlaybackSnapshot(current, _state.ContextUri, _state.PositionMs, _state.DurationMs,
            wirePlaying, wirePaused, _state.IsShuffle, _state.Repeat,
            wirePrev, wireNext, metadata, currentIndex, iid, page, rev, sid, pid, hasBeen, started, _state.Volume);
    }

    static IReadOnlyList<SnapshotTrack> CapPrev(List<SnapshotTrack> tracks)
    {
        if (tracks.Count <= MaxWirePrevTracks) return tracks;
        return tracks.GetRange(tracks.Count - MaxWirePrevTracks, MaxWirePrevTracks);
    }

    static IReadOnlyList<SnapshotTrack> CapNext(List<SnapshotTrack> tracks)
    {
        if (tracks.Count <= MaxWireNextTracks) return tracks;
        return tracks.GetRange(0, MaxWireNextTracks);
    }

    static SnapshotTrack ToSnapshotTrack(QueueEntry entry, string provider, int viewIndex)
    {
        var t = entry.Track;
        var artist = t.Artists.Count > 0 ? t.Artists[0] : new ArtistRef("", "", "");
        return new SnapshotTrack(t.Uri, entry.Uid, provider, t.Title ?? "", t.Album.Name ?? "",
            artist.Uri ?? "", artist.Name ?? "", t.Album.Uri ?? "", t.Image?.Url ?? "",
            t.HasVideo, viewIndex, entry.Metadata ?? new Dictionary<string, string>());
    }

    static string ProviderOf(QueueEntry entry)
    {
        if (entry.Provider != QueueProvider.Context) return entry.Provider.ToWire();
        if (entry.IsAutoplay) return "autoplay";
        return entry.Bucket == QueueBucket.UserQueue ? "queue" : "context";
    }

    static bool IsContextProvider(string provider) => provider is "context" or "autoplay";

    static string NextSig(LocalPlaybackSnapshot? snap) =>
        snap is { } s && s.NextTracks.Count > 0 ? s.NextTracks.Count + ":" + s.NextTracks[0].Uri : "0";

    static string NewId() => Guid.NewGuid().ToString("N");
    static string NewDashedUuid() => Guid.NewGuid().ToString();

    void BumpQueueRevision()
    {
        unchecked { _queueRevisionCounter++; }
        _queueRevision = _queueRevisionCounter.ToString(CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _volumeTx.Dispose();
        _connSub.Dispose();
    }
}
