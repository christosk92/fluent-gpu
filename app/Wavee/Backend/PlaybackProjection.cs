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
    string AlbumName, string AlbumUri, string? ImageUrl, long DurationMs,
    // Context uid + provider ("queue" / "context") — carried so a forwarded set_queue can re-emit the active device's
    // own queue rows faithfully. Trailing defaults so display-only constructions are unaffected.
    string Uid = "", string Provider = "",
    IReadOnlyDictionary<string, string>? Metadata = null);

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
    IReadOnlyList<RemoteTrack> NextTracks,
    // Restrictions on the active track (ads / first-last) + our device's volume (0..65535, -1 = unknown). Trailing defaults
    // so existing constructions are unaffected.
    bool DisallowSkipPrev = false, bool DisallowSkipNext = false, bool DisallowSeeking = false, int OurVolume0_65535 = -1,
    // Content playback rate (spoken-word media); 1.0 = normal. Trailing default so existing constructions are unaffected.
    double PlaybackSpeed = 1.0,
    // The ACTIVE device's volume (0..65535, -1 = unknown) — the slider follows the active device, not just us.
    int ActiveVolume0_65535 = -1,
    // The Connect queue revision (PlayerState.queue_revision, a STRING — can exceed Int64). Echoed back on an outbound
    // set_queue. Trailing default so existing constructions are unaffected.
    string QueueRevision = "",
    // The active device's history (prev_tracks) as the cluster reports it — kept with uid+provider so a forwarded
    // set_queue can rewrite the REMOTE device's REAL queue (its NextTracks above are the up-next), not our local one.
    // Non-const default → nullable; coalesced at the fold.
    IReadOnlyList<RemoteTrack>? PrevTracks = null);

public sealed class NowPlayingProjection : IPlaybackProjection, IPlaybackState, IDisposable
{
    // After a local command, a contradicting cluster push within this window is merged (play-state not reverted).
    const long LocalCmdWindowMs = 2500;

    readonly string _ourDeviceId;
    readonly Func<long> _now;
    readonly Func<long> _serverNow;   // estimated server-clock Unix ms (<=0 ⇒ unsynced); read only at cluster fold
    readonly SimpleSubject<IPlaybackState> _changes = new();
    readonly SimpleSubject<long> _positionTicks = new();
    readonly object _gate = new();
    Timer? _ticker;
    bool _disposed;

    // ── the slab (mutated in place under _gate; coarse Changes fired outside) ─────────────────────────────────────────
    Track? _track;
    // Live enrichment: the cluster's player_state metadata is often THIN (title + album only, no artist name, no album
    // art). When set by the live bootstrap, this resolves the full track by uri (transport metadata) and folds artist +
    // album + art into the now-playing track. Null offline -> the cluster snapshot is shown as-is.
    Func<string, CancellationToken, Task<Track?>>? _trackResolver;
    public Func<string, CancellationToken, Task<Track?>>? TrackResolver
    {
        get => _trackResolver;
        set { _trackResolver = value; if (value is not null) MaybeEnrichCurrent(); }
    }
    string? _resolvingUri;   // de-dupe: at most one in-flight resolve per uri (guarded by _gate)
    string? _contextUri;
    long _localRevision;     // the session's monotonic revision (from the last ApplyLocalSnapshot) — for diagnostics / UI keying
    // Viewer-row ids live in a DISJOINT high range (ViewerIdBase+seq) so they can NEVER collide with the local session's
    // small monotonic ids (F5 — the "unified" guarantee is non-collision): a stale viewer id resolved against a live local
    // session after a device-role flip finds no match (safe no-op) instead of hitting an unrelated track.
    const ulong ViewerIdBase = 1UL << 62;
    int _viewerIdSeq;        // mints per-row ids for the viewer queue so a viewer row-click can be targeted (F5)
    readonly Dictionary<ulong, QueueEntry> _viewerRows = new();
    bool _hasLocalContext;
    IReadOnlyDictionary<string, string> _contextMetadata = new Dictionary<string, string>();
    string _activeDeviceId = "";
    ClusterDelta? _lastCluster;   // the last folded cluster (raw next/prev with uid+provider+metadata) — the source the controller replays through PlaybackSession.ReplaceFromCluster on ghost-resume (§8)
    string _queueRevision = "";
    bool _isPlaying, _isBuffering, _isPrebuffering, _shuffle;
    public bool IsPrivateSession { get; set; }
    RepeatMode _repeat;
    double _volume = 0.7;
    long _posMs, _posAnchorWall, _durMs;
    double _speed = 1.0;   // playback rate folded from the cluster (remote) / 1.0 (local); applied in Pos()
    Palette? _palette;
    IReadOnlyList<QueueEntry> _queue = Array.Empty<QueueEntry>();
    string? _lastLocalQueueDiagSig, _lastViewerQueueDiagSig, _lastRemoteClusterDiagSig;
    // The active device's queue, verbatim from the last cluster (with uid+provider) — the source for a forwarded set_queue.
    IReadOnlyList<RemoteTrack> _clusterPrev = Array.Empty<RemoteTrack>(), _clusterNext = Array.Empty<RemoteTrack>();
    bool _canSkipNext = true, _canSkipPrev = true, _canSeek = true;   // from cluster restrictions (viewer); true when local
    // reconciliation
    long _lastLocalCmdWall = long.MinValue;
    int _inFlightSeq;

    public NowPlayingProjection(string ourDeviceId, Func<long>? clock = null, Func<long>? serverNowUnixMs = null,
        double initialVolume01 = 0.7)
    {
        _ourDeviceId = ourDeviceId;
        _volume = Math.Clamp(initialVolume01, 0, 1);   // the announce + local host reconcile follow this (remember-volume seed)
        _now = clock ?? (() => Environment.TickCount64);
        // Estimated server-clock "now" in Unix ms, used only to age remote snapshots at fold. Default returns 0 (the
        // "unsynced" sentinel) so the offset-dependent network term stays off until a server clock is wired in.
        _serverNow = serverNowUnixMs ?? (() => 0L);
        PlaybackBucketDiagnostics.Startup("projection", "created",
            WaveeLogField.Of("device", ourDeviceId),
            WaveeLogField.Of("initialVolume", initialVolume01));
    }

    /// <summary>True when the cluster's active device is us (the controller's local-vs-remote branch keys on this).</summary>
    public bool WeAreActive { get { lock (_gate) return _activeDeviceId == _ourDeviceId; } }
    public string ActiveDeviceId { get { lock (_gate) return _activeDeviceId; } }
    /// <summary>The last-seen Connect queue revision (echoed on an outbound set_queue). "" until the first cluster.</summary>
    public string QueueRevision { get { lock (_gate) return _queueRevision; } }
    /// <summary>The active device's queue from the last cluster (uid+provider preserved) — what a forwarded set_queue
    /// rewrites. Empty until the first cluster. ClusterNextTracks = up-next (user queue then context continuation);
    /// ClusterPrevTracks = history.</summary>
    public IReadOnlyList<RemoteTrack> ClusterNextTracks { get { lock (_gate) return _clusterNext; } }
    public IReadOnlyList<RemoteTrack> ClusterPrevTracks { get { lock (_gate) return _clusterPrev; } }
    /// <summary>The most-recent folded cluster (full raw next/prev rows with uid+provider+metadata) — the controller replays
    /// it through <see cref="PlaybackSession.ReplaceFromCluster"/> for full session recovery on ghost-resume (§8). Null until
    /// the first cluster fold.</summary>
    public ClusterDelta? LastCluster { get { lock (_gate) return _lastCluster; } }
    public IReadOnlyDictionary<string, string> ContextMetadata { get { lock (_gate) return _contextMetadata; } }
    /// <summary>The session revision published by the last <see cref="ApplyLocalSnapshot"/> (0 until the first). Local only.</summary>
    public long LocalRevision { get { lock (_gate) return _localRevision; } }

    /// <summary>Resolve a viewer-queue row by the id minted in <see cref="MapQueue"/> — the viewer path of a queue-row click
    /// (the controller forwards next_track for the row). Best-effort: the id is valid against the most-recent cluster push.</summary>
    public bool TryGetViewerRow(QueueItemId id, out QueueEntry row)
    { lock (_gate) return _viewerRows.TryGetValue(id.Value, out row!); }

    /// <summary>The controller calls this the instant it issues a local optimistic command, so a stale cluster echo
    /// arriving just after does not revert the optimistic play-state.</summary>
    public void NoteLocalCommand() { lock (_gate) { _lastLocalCmdWall = _now(); _inFlightSeq++; } }

    // ── IPlaybackState ────────────────────────────────────────────────────────────────────────────────────────────────
    public Track? CurrentTrack { get { lock (_gate) return _track; } }
    public string? ContextUri { get { lock (_gate) return _contextUri; } }
    public bool IsPlaying { get { lock (_gate) return _isPlaying; } }
    // Prebuffering (playing the clear head while key+body resolve) reads as "buffering" to the UI so the player-bar's
    // indeterminate edge shows during the instant-start window without a new interface member.
    public bool IsBuffering { get { lock (_gate) return _isBuffering || _isPrebuffering; } }
    public bool IsPrebuffering { get { lock (_gate) return _isPrebuffering; } }
    public long PositionMs { get { lock (_gate) return Pos(); } }
    public long DurationMs { get { lock (_gate) return _durMs; } }
    public double Volume { get { lock (_gate) return _volume; } }
    public bool IsShuffle { get { lock (_gate) return _shuffle; } }
    public RepeatMode Repeat { get { lock (_gate) return _repeat; } }
    public Palette? Palette { get { lock (_gate) return _palette; } }
    public IReadOnlyList<QueueEntry> Queue { get { lock (_gate) return _queue; } }
    public bool CanSkipNext { get { lock (_gate) return _canSkipNext; } }
    public bool CanSkipPrev { get { lock (_gate) return _canSkipPrev; } }
    public bool CanSeek { get { lock (_gate) return _canSeek; } }
    public IObservable<IPlaybackState> Changes => _changes;
    public IObservable<long> PositionTicks => _positionTicks;

    // IPlaybackState : INotifyPropertyChanged — consumers use Changes/PositionTicks; the INPC event is raised coarsely
    // (null name = "everything may have changed") for any INPC-based binder.
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    static readonly System.ComponentModel.PropertyChangedEventArgs AllChanged = new(null);

    long Pos() => _isPlaying ? Math.Clamp(_posMs + (long)((_now() - _posAnchorWall) * _speed), 0, _durMs <= 0 ? long.MaxValue : _durMs) : _posMs;

    // Clamp a content playback rate to Spotify's spoken-word range; invalid/zero ⇒ normal speed.
    static double NormalizeSpeed(double v) => v <= 0 || double.IsNaN(v) || double.IsInfinity(v) ? 1.0 : Math.Clamp(v, 0.5, 3.5);

    /// <summary>Allow the app to set a palette derived from the current art (off the slab path).</summary>
    public void SetPalette(Palette? p) { lock (_gate) _palette = p; FireChanges(); }

    /// <summary>The Connect controller pushes QueueCore's snapshot here after a local queue change, so IPlaybackState.Queue
    /// (and the PutState next-up) reflect OUR local queue while we're the active device. OnCluster won't overwrite it while
    /// we're active (local wins); a viewer's queue still comes from the cluster.</summary>
    public void SetLocalQueue(IReadOnlyList<QueueEntry> queue)
    {
        string? ctx, current;
        lock (_gate)
        {
            _queue = queue;
            ctx = _contextUri;
            current = _track?.Uri;
        }
        PlaybackBucketDiagnostics.QueueIfChanged(ref _lastLocalQueueDiagSig, "projection.local.set", queue, ctx, current);
        FireChanges();
    }

    /// <summary>Set the context display metadata (name/images the PutState publisher reads). Does NOT touch play-state /
    /// the queue / _contextUri — those arrive atomically via <see cref="ApplyLocalSnapshot"/> (F3: the split setter that
    /// let context and track publish at different times is gone). No FireChanges: the following ApplyLocalSnapshot fires.</summary>
    public void SetContextMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        lock (_gate)
            _contextMetadata = metadata is { Count: > 0 }
                ? new Dictionary<string, string>(metadata, StringComparer.Ordinal)
                : new Dictionary<string, string>();
    }

    /// <summary>The ONE atomic local publish (F3/F4/F6, §5): while WE are the active device, the session's snapshot AND the
    /// playback event fold under a single lock with a single FireChanges — the track, the display-windowed queue, the
    /// context, the options and the revision can never self-contradict for a frame. <paramref name="ev"/> null = a pure
    /// queue/options change (no play-state fold). Display windowing (history tail 16, next 50) lives here, never in the
    /// session core. A DEBUG assert fires if the published NowPlaying row's uri diverges from the current track.</summary>
    public void ApplyLocalSnapshot(QueueSnapshot snap, PlaybackEvent? ev = null)
    {
        IReadOnlyList<QueueEntry> windowed;
        lock (_gate)
        {
            _track = snap.Current?.Track ?? ev?.Track;   // the single source of "current" while we're active
            if (_track is { DurationMs: > 0 } t) _durMs = t.DurationMs;
            if (ev is { } e)
            {
                switch (e.Kind)
                {
                    case EvKind.Started:
                    case EvKind.Resumed:
                    case EvKind.TrackChanged:
                        _isPlaying = true; _isBuffering = false;
                        _canSkipNext = _canSkipPrev = _canSeek = true;
                        _speed = 1.0; _posMs = e.AtMs; _posAnchorWall = _now();
                        break;
                    case EvKind.Paused:
                    case EvKind.Ended:
                    case EvKind.BecameInactive:
                        _isPlaying = false; _speed = 1.0; _posMs = e.AtMs; _posAnchorWall = _now();
                        break;
                    case EvKind.Seeked:
                        _posMs = e.AtMs; _posAnchorWall = _now();
                        break;
                    // OptionsChanged / VolumeChanged / QueueChanged: no play-state fold — options ride in the snapshot below.
                }
            }
            _contextUri = snap.ContextUri;
            _hasLocalContext = !string.IsNullOrEmpty(snap.ContextUri);
            _shuffle = snap.Shuffle;
            _repeat = snap.Repeat;
            _localRevision = snap.Revision;
            windowed = _queue = WindowQueue(snap);
            AssertCurrentMatchesNowPlaying();
        }
        FireChanges();
        RestartTicker();
        MaybeEnrichCurrent();
    }

    // Display windowing (§5): history tail (≤16), current, user queue (uncapped), upcoming (≤50). History is local-only
    // and listed first so any consumer walking the flat queue sees buckets in panel order.
    static IReadOnlyList<QueueEntry> WindowQueue(in QueueSnapshot s)
    {
        const int NextCap = 50, HistoryTail = 16;
        int nUp = Math.Min(s.Upcoming.Length, NextCap);
        int firstH = Math.Max(0, s.History.Length - HistoryTail);
        var list = new List<QueueEntry>((s.History.Length - firstH) + 1 + s.UserQueue.Length + nUp);
        for (int h = firstH; h < s.History.Length; h++) list.Add(s.History[h]);
        if (s.Current is { } cur) list.Add(cur);
        for (int i = 0; i < s.UserQueue.Length; i++) list.Add(s.UserQueue[i]);
        for (int i = 0; i < nUp; i++) list.Add(s.Upcoming[i]);
        return list;
    }

    // DEBUG tripwire (§5): the log contradiction (Queue[NowPlaying].uri ≠ CurrentTrack.uri) that motivated the rework is
    // now structurally impossible — this proves it. [Conditional] → erased from the shipping AOT binary.
    [System.Diagnostics.Conditional("DEBUG")]
    void AssertCurrentMatchesNowPlaying()
    {
        for (int i = 0; i < _queue.Count; i++)
        {
            if (_queue[i].Bucket != QueueBucket.NowPlaying) continue;
            if (!string.Equals(_queue[i].Track.Uri, _track?.Uri, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"published state contradiction: NowPlaying row uri '{_queue[i].Track.Uri}' != CurrentTrack uri '{_track?.Uri}'");
        }
    }

    /// <summary>Controller pushes the local shuffle/repeat after a change so IPlaybackState + PutState reflect them while
    /// we're active (OnCluster won't overwrite them while active — local wins).</summary>
    public void SetLocalOptions(bool shuffle, RepeatMode repeat) { lock (_gate) { _shuffle = shuffle; _repeat = repeat; } FireChanges(); }

    /// <summary>Controller pushes the local volume after a change (so PutState carries it). 0..1.</summary>
    public void SetLocalVolume(double volume01) { lock (_gate) _volume = Math.Clamp(volume01, 0, 1); FireChanges(); }

    // ── Remote (cluster) fold — viewer mode + reconciliation ─────────────────────────────────────────────────────────
    public void OnCluster(in ClusterDelta c)
    {
        PlaybackBucketDiagnostics.RemoteClusterIfChanged(ref _lastRemoteClusterDiagSig, "projection.cluster.raw", c);
        IReadOnlyList<QueueEntry>? viewerQueue = null;
        string? ctxForLog = null, currentForLog = null;
        lock (_gate)
        {
            _lastCluster = c;
            _activeDeviceId = c.ActiveDeviceId;
            _queueRevision = c.QueueRevision ?? "";
            _clusterPrev = c.PrevTracks ?? Array.Empty<RemoteTrack>();
            _clusterNext = c.NextTracks;   // the active device's up-next, kept verbatim (uid+provider) for a forwarded set_queue
            bool weActive = c.ActiveDeviceId == _ourDeviceId;
            // Stale-cluster suppression: only when WE are active and a local command is still in flight do we refuse to let
            // a contradicting cluster revert our optimistic play-state. As a viewer, the cluster is always the truth.
            bool suppressPlayState = weActive && _lastLocalCmdWall != long.MinValue && (_now() - _lastLocalCmdWall) < LocalCmdWindowMs;

            // Detect track change BEFORE merging — a fresh track's Timestamp can lag the prior track, so we must not age it.
            bool isNewTrack = c.HasTrack && (_track is null || !string.Equals(_track.Uri, c.Track.Uri, StringComparison.Ordinal));
            // F4: while WE are active WITH a live local session the local snapshot durably owns _track (the same gate the
            // context uses just below) — a stale cluster echo must NOT overwrite the just-issued current, not merely inside
            // the 2.5 s suppression window. Recovery (weActive but no local context yet) still takes the cluster's track.
            bool localOwnsTrack = weActive && _hasLocalContext;
            if (c.HasTrack && !suppressPlayState && !localOwnsTrack) _track = MergeClusterTrack(_track, MapTrack(c.Track));
            if (!weActive || !_hasLocalContext)
            {
                _contextUri = c.ContextUri;
                _hasLocalContext = false;
                _contextMetadata = new Dictionary<string, string>();
            }
            _durMs = c.DurationMs > 0 ? c.DurationMs : (c.HasTrack ? c.Track.DurationMs : _durMs);
            if (!suppressPlayState)
            {
                // no-active-device Playing→Paused clamp (ported correctness): if nobody is active, we are not playing.
                bool active = !string.IsNullOrEmpty(c.ActiveDeviceId);
                _isPlaying = active && c.IsPlaying && !c.IsPaused;
                _isBuffering = c.IsBuffering;
            }
            if (!weActive) { _shuffle = c.Shuffle; _repeat = c.Repeat; }   // active: local owns shuffle/repeat (SetLocalOptions)
            _canSkipNext = !c.DisallowSkipNext;
            _canSkipPrev = !c.DisallowSkipPrev;
            _canSeek = !c.DisallowSeeking;
            // The slider follows the ACTIVE device's volume; suppress only within our own local-command window (so our
            // optimistic set isn't snapped back by a stale echo) — a genuine remote change, from any device, flows through.
            bool inLocalWindow = _lastLocalCmdWall != long.MinValue && (_now() - _lastLocalCmdWall) < LocalCmdWindowMs;
            if (!inLocalWindow && c.ActiveVolume0_65535 >= 0) _volume = c.ActiveVolume0_65535 / 65535.0;
            // The remote position is a snapshot AS OF c.TimestampMs; by the time we fold it, it is already stale. Re-project
            // it to "now" as two isolated terms, then anchor in the monotonic domain so Pos() interpolates forward smoothly.
            //   serverSideAge — pure server-domain Δ (sample→emit); correct with NO clock sync, even fully offline.
            //   networkAge    — transit since the server emitted the cluster; needs a synced server clock (<=0 ⇒ skipped).
            // No aging while paused (position is frozen) or on a fresh near-zero track (its Timestamp may lag).
            _speed = NormalizeSpeed(c.PlaybackSpeed);
            long serverSideAge = c.ServerTimestampMs > 0 && c.TimestampMs > 0 ? Math.Max(0, c.ServerTimestampMs - c.TimestampMs) : 0;
            long serverNow = _serverNow();
            long networkAge = serverNow > 0 && c.ServerTimestampMs > 0 ? Math.Max(0, serverNow - c.ServerTimestampMs) : 0;
            long age = !_isPlaying || (isNewTrack && c.PositionAsOfMs <= 1000) ? 0 : serverSideAge + networkAge;
            _posMs = c.PositionAsOfMs + (long)Math.Round(age * _speed);
            _posAnchorWall = _now();
            if (!weActive)
            {
                viewerQueue = MapQueue(c.NextTracks, c.PrevTracks, c.HasTrack ? c.Track : null);
                _queue = viewerQueue;   // viewer: cluster queue. Active: keep the local queue (ApplyLocalSnapshot).
            }
            ctxForLog = _contextUri;
            currentForLog = _track?.Uri;
            if (weActive) AssertCurrentMatchesNowPlaying();   // the tripwire runs on the active cluster path too (F4) — local owns _track, so it can't diverge
        }
        if (viewerQueue is not null)
            PlaybackBucketDiagnostics.QueueIfChanged(ref _lastViewerQueueDiagSig, "projection.viewer.mapped",
                viewerQueue, ctxForLog, currentForLog);
        FireChanges();
        RestartTicker();
        MaybeEnrichCurrent();   // the cluster track may be thin (no artist/art) → resolve + fold in the full metadata
    }

    // Resolve full metadata for a cluster track whose player_state was thin (no artist name / no album art). At most one
    // resolve per uri is in flight; the result is applied only if that uri is STILL current (the user didn't skip on).
    void MaybeEnrichCurrent()
    {
        if (TrackResolver is not { } resolve) return;
        string uri;
        lock (_gate)
        {
            if (_track is not { } t) return;
            // Album identity is UI-critical too: Connect can provide usable art + artist while omitting Album.Uri.
            // Without this term the player-bar title looks complete but can never become an album hyperlink.
            bool thin = !ImageSource.IsUsable(t.Image)
                || t.Artists.Count == 0 || string.IsNullOrEmpty(t.Artists[0].Name)
                || string.IsNullOrEmpty(t.Album.Uri);
            if (!thin || t.Uri.Length == 0 || _resolvingUri == t.Uri) return;
            _resolvingUri = uri = t.Uri;
        }
        _ = ResolveAsync(resolve, uri);
    }

    async Task ResolveAsync(Func<string, CancellationToken, Task<Track?>> resolve, string uri)
    {
        Track? enriched = null;
        try { enriched = await resolve(uri, default).ConfigureAwait(false); } catch { /* best-effort */ }
        bool changed = false;
        lock (_gate)
        {
            if (_resolvingUri == uri) _resolvingUri = null;
            if (enriched is { } e && _track is { } cur && cur.Uri == uri)
            {
                // Keep the cluster's title (+ duration/position state); fill artist + album + art from the resolved track.
                _track = cur with
                {
                    Title = TitleMissing(cur.Title, cur.Uri) ? e.Title : cur.Title,
                    Artists = e.Artists.Count > 0 ? e.Artists : cur.Artists,
                    Album = e.Album,
                    Image = ImageSource.ChooseBetter(e.Image, cur.Image),
                    Isrc = e.Isrc ?? cur.Isrc,   // carry the resolved ISRC onto the now-playing track (cluster track has none)
                };
                changed = true;
            }
        }
        if (changed) FireChanges();
    }

    // ── Local fold — when WE are the active device (Stage E controller + Stage H host) ───────────────────────────────
    public void OnEvent(in PlaybackEvent e)
    {
        lock (_gate)
        {
            if (e.Track is not null)
            {
                _track = e.Track;
                // Local events are authoritative while we're the active device — fold the duration too. Without this,
                // _durMs keeps the PREVIOUS track's length until a cluster echo arrives (never, when playing offline):
                // the player-bar label shows the old duration AND the seek bar scales scrub fractions by the wrong
                // length, so every committed seek targets the wrong millisecond.
                if (e.Track.DurationMs > 0) _durMs = e.Track.DurationMs;
            }
            switch (e.Kind)
            {
                case EvKind.Started:
                case EvKind.Resumed:
                case EvKind.TrackChanged:
                    _isPlaying = true; _isBuffering = false;
                    _canSkipNext = _canSkipPrev = _canSeek = true;   // local playback → full local control
                    _speed = 1.0; _posMs = e.AtMs; _posAnchorWall = _now();
                    break;
                case EvKind.Paused:
                case EvKind.Ended:
                case EvKind.BecameInactive:
                    _isPlaying = false; _speed = 1.0; _posMs = e.AtMs; _posAnchorWall = _now();
                    break;
                case EvKind.Seeked:
                    _posMs = e.AtMs; _posAnchorWall = _now();
                    break;
                // OptionsChanged / VolumeChanged / QueueChanged: shuffle/repeat/volume/queue arrive via SetLocal* — just notify.
            }
        }
        FireChanges();
        RestartTicker();
        MaybeEnrichCurrent();
    }

    public void OnHostSignal(in AudioHostSignal s)
    {
        bool structural = s.Kind != AudioHostSignalKind.PositionTick;
        lock (_gate)
        {
            switch (s.Kind)
            {
                case AudioHostSignalKind.Playing: _isPlaying = true; _isBuffering = false; _isPrebuffering = false; break;
                case AudioHostSignalKind.Paused: _isPlaying = false; break;
                case AudioHostSignalKind.Buffering: _isBuffering = true; break;
                case AudioHostSignalKind.Prebuffering: _isPrebuffering = true; _isBuffering = false; break;
                case AudioHostSignalKind.Ended: _isPlaying = false; break;
                case AudioHostSignalKind.Error: _isPlaying = false; _isBuffering = false; _isPrebuffering = false; break;
            }
            _speed = 1.0; _posMs = s.PositionMs; _posAnchorWall = _now();
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
        return new Track(IdFromUri(r.Uri), r.Uri, r.Title, artists, album, r.DurationMs, HasVideoMetadata(r), img);
    }

    // Cluster player_state often repeats a THIN copy of the same current track. Preserve the enriched TrackV4 fields
    // already folded into the slab (artist credits + album cover) so each cluster heartbeat cannot blank the player bar.
    static Track MergeClusterTrack(Track? current, Track incoming)
    {
        if (current is null || current.Uri != incoming.Uri) return incoming;
        bool incomingHasArtist = incoming.Artists.Count > 0 && !string.IsNullOrEmpty(incoming.Artists[0].Name);
        return incoming with
        {
            Title = TitleMissing(incoming.Title, incoming.Uri) ? current.Title : incoming.Title,
            Artists = incomingHasArtist ? incoming.Artists : current.Artists,
            Album = MergeAlbumRef(current.Album, incoming.Album),
            DurationMs = incoming.DurationMs > 0 ? incoming.DurationMs : current.DurationMs,
            Image = ImageSource.ChooseBetter(incoming.Image, current.Image),
            Isrc = incoming.Isrc ?? current.Isrc,   // a thin cluster heartbeat must not blank the resolved ISRC
        };
    }

    // A title is "missing" if it's blank OR is just the track URI echoed back — the synthetic/context placeholders
    // seed Title=Uri before real metadata resolves, and that placeholder must never win over a resolved name.
    static bool TitleMissing(string? title, string uri) => string.IsNullOrEmpty(title) || title == uri;

    static AlbumRef MergeAlbumRef(AlbumRef current, AlbumRef incoming) => new(
        string.IsNullOrEmpty(incoming.Id) ? current.Id : incoming.Id,
        string.IsNullOrEmpty(incoming.Uri) ? current.Uri : incoming.Uri,
        string.IsNullOrEmpty(incoming.Name) ? current.Name : incoming.Name);

    // Viewer-mode queue: the active device's next_tracks split by provider. History is local-only on the active device;
    // viewer mode does not surface cluster prev_tracks (server-driven history is a follow-up).
    IReadOnlyList<QueueEntry> MapQueue(IReadOnlyList<RemoteTrack> next, IReadOnlyList<RemoteTrack>? prev, RemoteTrack? current = null)
    {
        _ = prev;
        _viewerRows.Clear();
        if (next.Count == 0 && current is null) return Array.Empty<QueueEntry>();
        var list = new List<QueueEntry>(1 + next.Count);
        if (current is { Uri: { Length: > 0 } uri } cur && uri != "spotify:delimiter")
        {
            string provider = string.IsNullOrEmpty(cur.Provider) ? "context" : cur.Provider;
            list.Add(ViewerEntry(cur, QueueBucket.NowPlaying, provider));
        }
        for (int i = 0; i < next.Count; i++)
        {
            if (next[i].Uri == "spotify:delimiter") continue;   // queue/context boundary marker
            string provider = string.IsNullOrEmpty(next[i].Provider) ? "context" : next[i].Provider;
            list.Add(ViewerEntry(next[i], provider == "queue" ? QueueBucket.UserQueue : QueueBucket.NextUp, provider));
        }
        return list;
    }

    QueueEntry ViewerEntry(in RemoteTrack r, QueueBucket bucket, string provider)
    {
        var id = new QueueItemId(ViewerIdBase + (ulong)(++_viewerIdSeq));
        var entry = new QueueEntry(id, "i" + id.Value, MapTrack(r), bucket,
            QueueProviderExtensions.FromWire(provider), provider == "autoplay", r.Uid, r.Metadata);
        _viewerRows[id.Value] = entry;
        return entry;
    }

    static bool HasVideoMetadata(in RemoteTrack r)
    {
        var metadata = r.Metadata;
        if (metadata is null) return false;
        if (metadata.TryGetValue("track_player", out var player) && player == "video") return true;
        if (metadata.TryGetValue("media.type", out var media) && (media == "video" || media == "mixed")) return true;
        return metadata.ContainsKey("media.manifest_id") || metadata.ContainsKey("save_track.uri");
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
