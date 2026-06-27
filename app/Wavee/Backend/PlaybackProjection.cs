using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

// ── Stage D — the bidirectional playback-state projection (proto-free) ────────────────────────────────────────────────
// NowPlayingProjection folds three inputs into one slab and presents Wavee.Core.IPlaybackState (what PlaybackBridge/PlayerBar
// read, unchanged):
//   • ClusterDelta   — the remote truth (mapped from the Cluster proto by SpotifyLive's ClusterMapper) — VIEWER mode.
//   • PlaybackEvent  — the local reducer's events (Stage E controller) — when WE are the active device.
//   • AudioHostSignal— the local host clock + Ended (Stage H).
// Reconciliation (locked policy): when another device is active, the cluster wins (we are a viewer); when WE are active,
// local wins, and a *stale* cluster push inside the in-flight window does NOT revert a just-issued local command.

/// <summary>Proto-free snapshot of one cluster track (mapped from a ProvidedTrack by SpotifyLive).</summary>
public readonly record struct RemoteTrack(
    string Uri, string Title, string ArtistName, string ArtistUri,
    string AlbumName, string AlbumUri, string? ImageUrl, long DurationMs);

/// <summary>Proto-free row of the Connect device roster (volume in Spotify's 0..65535 range).</summary>
public readonly record struct ConnectDeviceRow(string Id, string Name, DeviceKind Kind, bool IsActive, int Volume0_65535);

/// <summary>Proto-free snapshot of a Spotify Cluster (the remote playback truth) + the device roster.</summary>
public sealed record ClusterDelta(
    string ActiveDeviceId,
    bool HasTrack, RemoteTrack Track,
    string? ContextUri,
    bool IsPlaying, bool IsPaused, bool IsBuffering,
    long PositionAsOfMs, long TimestampMs, long ServerTimestampMs, long DurationMs,
    bool Shuffle, RepeatMode Repeat,
    IReadOnlyList<ConnectDeviceRow> Devices,
    IReadOnlyList<RemoteTrack> NextTracks);

public sealed class NowPlayingProjection : IPlaybackProjection, IPlaybackState, IDisposable
{
    // After a local command, a contradicting cluster push within this window is merged (play-state not reverted).
    const long LocalCmdWindowMs = 2500;

    readonly string _ourDeviceId;
    readonly Func<long> _now;
    readonly SimpleSubject<IPlaybackState> _changes = new();
    readonly SimpleSubject<long> _positionTicks = new();
    readonly object _gate = new();
    Timer? _ticker;
    bool _disposed;

    // ── the slab (mutated in place under _gate; coarse Changes fired outside) ─────────────────────────────────────────
    Track? _track;
    string? _contextUri;
    string _activeDeviceId = "";
    bool _isPlaying, _isBuffering, _shuffle;
    RepeatMode _repeat;
    double _volume = 0.7;
    long _posMs, _posAnchorWall, _durMs;
    Palette? _palette;
    IReadOnlyList<QueueEntry> _queue = Array.Empty<QueueEntry>();
    // reconciliation
    long _lastLocalCmdWall = long.MinValue;
    int _inFlightSeq;

    public NowPlayingProjection(string ourDeviceId, Func<long>? clock = null)
    {
        _ourDeviceId = ourDeviceId;
        _now = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>True when the cluster's active device is us (the controller's local-vs-remote branch keys on this).</summary>
    public bool WeAreActive { get { lock (_gate) return _activeDeviceId == _ourDeviceId; } }
    public string ActiveDeviceId { get { lock (_gate) return _activeDeviceId; } }

    /// <summary>The controller calls this the instant it issues a local optimistic command, so a stale cluster echo
    /// arriving just after does not revert the optimistic play-state.</summary>
    public void NoteLocalCommand() { lock (_gate) { _lastLocalCmdWall = _now(); _inFlightSeq++; } }

    // ── IPlaybackState ────────────────────────────────────────────────────────────────────────────────────────────────
    public Track? CurrentTrack { get { lock (_gate) return _track; } }
    public string? ContextUri { get { lock (_gate) return _contextUri; } }
    public bool IsPlaying { get { lock (_gate) return _isPlaying; } }
    public bool IsBuffering { get { lock (_gate) return _isBuffering; } }
    public long PositionMs { get { lock (_gate) return Pos(); } }
    public long DurationMs { get { lock (_gate) return _durMs; } }
    public double Volume { get { lock (_gate) return _volume; } }
    public bool IsShuffle { get { lock (_gate) return _shuffle; } }
    public RepeatMode Repeat { get { lock (_gate) return _repeat; } }
    public Palette? Palette { get { lock (_gate) return _palette; } }
    public IReadOnlyList<QueueEntry> Queue { get { lock (_gate) return _queue; } }
    public IObservable<IPlaybackState> Changes => _changes;
    public IObservable<long> PositionTicks => _positionTicks;

    // IPlaybackState : INotifyPropertyChanged — consumers use Changes/PositionTicks; the INPC event is raised coarsely
    // (null name = "everything may have changed") for any INPC-based binder.
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    static readonly System.ComponentModel.PropertyChangedEventArgs AllChanged = new(null);

    long Pos() => _isPlaying ? Math.Min(_durMs <= 0 ? long.MaxValue : _durMs, _posMs + (_now() - _posAnchorWall)) : _posMs;

    /// <summary>Allow the app to set a palette derived from the current art (off the slab path).</summary>
    public void SetPalette(Palette? p) { lock (_gate) _palette = p; FireChanges(); }

    // ── Remote (cluster) fold — viewer mode + reconciliation ─────────────────────────────────────────────────────────
    public void OnCluster(in ClusterDelta c)
    {
        lock (_gate)
        {
            _activeDeviceId = c.ActiveDeviceId;
            bool weActive = c.ActiveDeviceId == _ourDeviceId;
            // Stale-cluster suppression: only when WE are active and a local command is still in flight do we refuse to let
            // a contradicting cluster revert our optimistic play-state. As a viewer, the cluster is always the truth.
            bool suppressPlayState = weActive && _lastLocalCmdWall != long.MinValue && (_now() - _lastLocalCmdWall) < LocalCmdWindowMs;

            if (c.HasTrack) _track = MapTrack(c.Track);
            _contextUri = c.ContextUri;
            _durMs = c.DurationMs > 0 ? c.DurationMs : (c.HasTrack ? c.Track.DurationMs : _durMs);
            if (!suppressPlayState)
            {
                // no-active-device Playing→Paused clamp (ported correctness): if nobody is active, we are not playing.
                bool active = !string.IsNullOrEmpty(c.ActiveDeviceId);
                _isPlaying = active && c.IsPlaying && !c.IsPaused;
                _isBuffering = c.IsBuffering;
            }
            _shuffle = c.Shuffle;
            _repeat = c.Repeat;
            _posMs = c.PositionAsOfMs;
            _posAnchorWall = _now();
            _queue = MapQueue(c.NextTracks);
        }
        FireChanges();
        RestartTicker();
    }

    // ── Local fold — when WE are the active device (Stage E controller + Stage H host) ───────────────────────────────
    public void OnEvent(in PlaybackEvent e)
    {
        lock (_gate)
        {
            if (e.Track is not null) _track = e.Track;
            switch (e.Kind)
            {
                case EvKind.Started:
                case EvKind.Resumed:
                case EvKind.TrackChanged: _isPlaying = true; _isBuffering = false; break;
                case EvKind.Paused:
                case EvKind.Ended: _isPlaying = false; break;
            }
            _posMs = e.AtMs; _posAnchorWall = _now();
        }
        FireChanges();
        RestartTicker();
    }

    public void OnHostSignal(in AudioHostSignal s)
    {
        bool structural = s.Kind != AudioHostSignalKind.PositionTick;
        lock (_gate)
        {
            switch (s.Kind)
            {
                case AudioHostSignalKind.Playing: _isPlaying = true; _isBuffering = false; break;
                case AudioHostSignalKind.Paused: _isPlaying = false; break;
                case AudioHostSignalKind.Buffering: _isBuffering = true; break;
                case AudioHostSignalKind.Ended: _isPlaying = false; break;
            }
            _posMs = s.PositionMs; _posAnchorWall = _now();
        }
        if (structural) { FireChanges(); RestartTicker(); }
        else _positionTicks.OnNext(s.PositionMs);
    }

    void FireChanges() { if (_disposed) return; _changes.OnNext(this); PropertyChanged?.Invoke(this, AllChanged); }

    // A 1 Hz tick re-anchors the UI position WHILE PLAYING only (zero ticks when paused — the guardrail).
    void RestartTicker()
    {
        bool playing; lock (_gate) playing = _isPlaying;
        if (playing) { _ticker ??= new Timer(_ => Tick(), null, 1000, 1000); }
        else { _ticker?.Dispose(); _ticker = null; }
    }

    void Tick()
    {
        long pos; bool playing; lock (_gate) { pos = Pos(); playing = _isPlaying; }
        if (playing) _positionTicks.OnNext(pos);
    }

    static Track MapTrack(in RemoteTrack r)
    {
        var artists = new ArtistRef[] { new(IdFromUri(r.ArtistUri), r.ArtistUri, r.ArtistName) };
        var album = new AlbumRef(IdFromUri(r.AlbumUri), r.AlbumUri, r.AlbumName);
        Image? img = string.IsNullOrEmpty(r.ImageUrl) ? null : new Image(r.ImageUrl!);
        return new Track(IdFromUri(r.Uri), r.Uri, r.Title, artists, album, r.DurationMs, false, img);
    }

    static IReadOnlyList<QueueEntry> MapQueue(IReadOnlyList<RemoteTrack> next)
    {
        if (next.Count == 0) return Array.Empty<QueueEntry>();
        var list = new List<QueueEntry>(next.Count);
        for (int i = 0; i < next.Count; i++) list.Add(new QueueEntry("n" + i, MapTrack(next[i]), QueueBucket.NextUp, false));
        return list;
    }

    static string IdFromUri(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return "";
        int i = uri.LastIndexOf(':');
        return i >= 0 && i + 1 < uri.Length ? uri[(i + 1)..] : uri;
    }

    public void Dispose() { _disposed = true; _ticker?.Dispose(); _ticker = null; }
}

// IConnectDevices backed by the cluster device roster. TransferAsync is wired to the controller in Stage E.
public sealed class LiveConnectDevices : IConnectDevices
{
    readonly SimpleSubject<IReadOnlyList<PlaybackDevice>> _changed = new(Array.Empty<PlaybackDevice>());
    IReadOnlyList<PlaybackDevice> _devices = Array.Empty<PlaybackDevice>();

    /// <summary>Wired in Stage E (issues the outbound transfer command). Null → transfer is a no-op for now.</summary>
    public Func<string, CancellationToken, Task>? TransferHandler { get; set; }

    public IReadOnlyList<PlaybackDevice> Devices => _devices;
    public IObservable<IReadOnlyList<PlaybackDevice>> DevicesChanged => _changed;
    public Task TransferAsync(string deviceId, CancellationToken ct = default) => TransferHandler?.Invoke(deviceId, ct) ?? Task.CompletedTask;

    public void Update(IReadOnlyList<ConnectDeviceRow> rows)
    {
        var list = new PlaybackDevice[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            list[i] = new PlaybackDevice(r.Id, r.Name, r.Kind, r.IsActive, (int)Math.Round(r.Volume0_65535 / 655.35));
        }
        _devices = list;
        _changed.OnNext(list);
    }
}
